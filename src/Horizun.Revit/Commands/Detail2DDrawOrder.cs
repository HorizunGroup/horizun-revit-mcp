// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_detail_2d operation=draw_order: Bring to Front / Send to Back / Bring Forward / Send
// Backward of EXISTING 2D detail (detail lines, regions, detail components, text), through the
// public DetailElementOrderUtils (Revit 2023-2027). Rules: Core/DrawOrderRules.cs.
//
// It is its own path inside the tool because it creates nothing: a batch of draw_order actions
// is rehearsed and applied alone (mixing it with a creation batch is refused, typed, since the
// order of elements that do not exist yet is not a thing to approve).
//
//   * dry run: the moves are applied in one transaction, the stack is read back after each and
//     judged by DrawOrderRules.Verify over the WHOLE detail set of the view, and the transaction is
//     ROLLED BACK; the rollback status is Revit's own and the stack must read as it did before;
//   * apply: the same moves, each judged in the still-reversible transaction, then committed and
//     the final stack of every view re-read and compared with what the transaction ended on;
//   * Revit supports draw order only in projects (and 3D families), and only for elements visible
//     in the view: anything else is refused by name in the rehearsal, with Revit's sentence.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class Detail2DCommand
    {
        private const string OpDrawOrder = "draw_order";
        private const int MaxDetailScan = 50000;

        private sealed class OrderPlan
        {
            public int Index; public View View; public string Order; public List<Element> Elements = new List<Element>();
            public List<long> Before; public string BeforeDigest;
        }

        private static bool IsDrawOrder(JToken action) => action is JObject o && string.Equals(o.Value<string>("operation"), OpDrawOrder, StringComparison.OrdinalIgnoreCase);

        private static List<ElementId> DetailElementsOf(Document doc, View view)
        {
            var ids = new List<ElementId>(); int seen = 0;
            foreach (Element el in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                if (++seen > MaxDetailScan)
                    throw new ArgumentException("view '" + view.Name + "' holds more than " + MaxDetailScan + " elements to scan for detail draw order.");
                if (DetailElementOrderUtils.IsDetailElement(doc, view, el.Id)) ids.Add(el.Id);
            }
            return ids;
        }

        private static List<long> StackOf(Document doc, View view)
        {
            List<ElementId> all = DetailElementsOf(doc, view);
            if (all.Count == 0) return new List<long>();
#if REVIT2023
            // GetDrawOrderForDetails arrived in the Revit 2024 API: without a readable stack the
            // order cannot be verified, so draw_order is refused at planning time on 2023.
            throw new NotSupportedException(DrawOrderUnreadable2023);
#else
            return DetailElementOrderUtils.GetDrawOrderForDetails(view, new HashSet<ElementId>(all)).Select(Rid.Value).ToList();
#endif
        }

        private static string Digest(IEnumerable<long> stack)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                    string.Join(",", stack.Select(x => x.ToString(CultureInfo.InvariantCulture)))))).Replace("-", "").Substring(0, 24);
        }

#if REVIT2023
        private static readonly bool DrawOrderReadable = false;
#else
        private static readonly bool DrawOrderReadable = true;
#endif
        private const string DrawOrderUnreadable2023 = "draw_order needs Revit 2024 or later: the Revit 2023 API cannot read the detail draw order back, so the change could not be verified. Nothing was changed.";

        private static OrderPlan PlanDrawOrder(Document doc, int index, JObject a)
        {
            if (!DrawOrderReadable) throw new ArgumentException(DrawOrderUnreadable2023);
            foreach (JProperty field in a.Properties())
                if (field.Name != "operation" && field.Name != "view_id" && field.Name != "element_ids" && field.Name != "order" && field.Name != "key")
                    throw new ArgumentException("draw_order does not take '" + field.Name + "' - accepting it would drop it silently. It takes view_id, element_ids and order.");
            long viewId = a.Value<long?>("view_id") ?? -1;
            if (!Rid.CanRepresent(viewId) || !(doc.GetElement(Rid.Make(viewId)) is View view))
                throw new ArgumentException("view_id must identify a view" + DocumentContext.Hint<View>(doc, viewId.ToString()));
            if (view is ViewSheet || view is ViewSchedule || view is View3D)
                throw new ArgumentException("view '" + view.Name + "' is a " + view.GetType().Name + ": detail draw order lives in plans, sections, elevations, drafting views and legends.");
            string order = DrawOrderRules.Parse(a.Value<string>("order"));
            if (order == null) throw new ArgumentException(DrawOrderRules.OrderError());
            var array = a["element_ids"] as JArray;
            if (array == null || array.Any(t => t.Type != JTokenType.Integer)) throw new ArgumentException("element_ids must be an array of detail element ids.");
            List<long> ids = array.Select(t => t.Value<long>()).ToList();
            string members = DrawOrderRules.MembersError(ids);
            if (members != null) throw new ArgumentException(members);
            var plan = new OrderPlan { Index = index, View = view, Order = order };
            foreach (long id in ids)
            {
                if (!Rid.CanRepresent(id) || doc.GetElement(Rid.Make(id)) == null) throw new ArgumentException("element " + id + " does not exist in this document.");
                Element el = doc.GetElement(Rid.Make(id));
                if (!DetailElementOrderUtils.IsDetailElement(doc, view, el.Id))
                    throw new ArgumentException("element " + id + " (" + (el.Category?.Name ?? el.GetType().Name) + ") does not take part in detail draw order in view '" + view.Name +
                        "': it must be a detail element (line, region, component, text) visible in that view.");
                ManageViewsCommand.RequireEditable(doc, el);
                plan.Elements.Add(el);
            }
            return plan;
        }

        private static void MoveElements(Document doc, OrderPlan p)
        {
            var ids = p.Elements.Select(e => e.Id).ToList();
            switch (p.Order)
            {
                case DrawOrderRules.Front: DetailElementOrderUtils.BringToFront(doc, p.View, ids); break;
                case DrawOrderRules.Back: DetailElementOrderUtils.SendToBack(doc, p.View, ids); break;
                case DrawOrderRules.Forward: DetailElementOrderUtils.BringForward(doc, p.View, ids); break;
                default: DetailElementOrderUtils.SendBackward(doc, p.View, ids); break;
            }
        }

        /// <summary>Apply every move in order inside the open transaction, judging the stack after each.</summary>
        private static JArray RunMoves(Document doc, List<OrderPlan> plans, Dictionary<long, List<long>> endStacks)
        {
            var rows = new JArray();
            foreach (OrderPlan p in plans)
            {
                List<long> before = StackOf(doc, p.View);
                MoveElements(doc, p);
                doc.Regenerate();
                List<long> after = StackOf(doc, p.View);
                string why = DrawOrderRules.Verify(p.Order, before, after, new HashSet<long>(p.Elements.Select(e => Rid.Value(e.Id))));
                rows.Add(new JObject
                {
                    ["index"] = p.Index, ["operation"] = OpDrawOrder, ["view_id"] = Rid.Value(p.View.Id), ["order"] = p.Order,
                    ["element_ids"] = new JArray(p.Elements.Select(e => Rid.Value(e.Id))),
                    ["stack_size"] = after.Count, ["changed"] = DrawOrderRules.Changed(before, after),
                    ["verified"] = why == null, ["reason"] = why
                });
                if (why != null) throw new InvalidOperationException("action " + p.Index + " (" + p.Order + " in view '" + p.View.Name + "'): " + why);
                endStacks[Rid.Value(p.View.Id)] = after;
            }
            return rows;
        }

        private CommandResult ExecuteDrawOrder(UIApplication app, GateResult gate, JObject request, JArray actions)
        {
            Document doc = gate.Document;
            if (actions.Any(x => !IsDrawOrder(x)))
                return CommandResult.Fail("draw_order cannot be mixed with creation operations in one batch: it reorders detail that already exists. " +
                    "Send the creation batch first, then a batch made only of draw_order actions. Nothing was changed.");
            var plans = new List<OrderPlan>(); var errors = new JArray(); var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < actions.Count; i++)
            {
                try { plans.Add(PlanDrawOrder(doc, i, (JObject)actions[i])); }
                catch (Exception ex)
                {
                    errors.Add(new JObject { ["index"] = i, ["error"] = ex.Message });
                    outcomes.Add(new ActionOutcome { Index = i, Error = ex.Message, UnsupportedReason = UnsupportedCapability.ReasonOf(ex) });
                }
            }
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string hash = DocumentGate.PlanHash(request, "units", "actions");
            foreach (OrderPlan p in plans)
                try { p.Before = StackOf(doc, p.View); p.BeforeDigest = Digest(p.Before); }
                catch (Exception ex) { errors.Add(new JObject { ["index"] = p.Index, ["error"] = "the draw order of view '" + p.View.Name + "' cannot be read: " + ex.Message }); }
            if (errors.Count > 0 && dry == false)
                return FallbackDecision.Refuse("Invalid actions; nothing ran: " + errors.ToString(Formatting.None), FallbackDecision.Decide(outcomes, writeStarted: false));

            var resolved = new ResolvedPlan { Command = Name, DocumentKey = gate.Fingerprint, RevitVersion = app?.Application?.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest() };
            foreach (OrderPlan p in plans)
            {
                var row = new PlannedElement { UniqueId = "action:" + p.Index, Category = OpDrawOrder, Action = PlannedAction.Modify, ElementId = Rid.Value(p.View.Id), BeforeValues = new Dictionary<string, string>() };
                row.BeforeValues["view"] = p.View.UniqueId + "|" + p.View.Name;
                row.BeforeValues["stack"] = p.BeforeDigest ?? "unreadable";
                foreach (Element e in p.Elements) row.BeforeValues["element:" + Rid.Value(e.Id)] = e.UniqueId;
                row.ProposedValues = new Dictionary<string, string> { { "order", p.Order }, { "elements", string.Join(",", p.Elements.Select(e => Rid.Value(e.Id))) } };
                resolved.Elements.Add(row);
            }

            // ---- THE REHEARSAL: the moves, judged after each, then a confirmed rollback.
            JArray rehearsalRows = null; string rollbackStatus = "not_started"; bool rehearsed = false;
            if (errors.Count == 0 && dry)
            {
                using (var tx = new Transaction(doc, "Horizun: rehearse draw order"))
                {
                    tx.Start();
                    string failure = null;
                    try { rehearsalRows = RunMoves(doc, plans, new Dictionary<long, List<long>>()); }
                    catch (Exception ex) { failure = ex.Message; }
                    TransactionStatus status = tx.GetStatus() == TransactionStatus.Started ? tx.RollBack() : tx.GetStatus();
                    rollbackStatus = status.ToString();
                    bool back = status == TransactionStatus.RolledBack;
                    if (back)
                        foreach (OrderPlan p in plans.GroupBy(x => Rid.Value(x.View.Id)).Select(g => g.First()))
                        {
                            try { back &= Digest(StackOf(doc, p.View)) == p.BeforeDigest; } catch { back = false; }
                        }
                    if (!back)
                        return CommandResult.FailWithDetail("The draw-order rehearsal could not be rolled back to the original stack (Revit reported '" + rollbackStatus +
                            "'): the state of the views is UNCERTAIN. Re-read it before anything else; no token is issued.",
                            new JObject { ["state"] = DimensionPlanRules.StateUncertain, ["rehearsal_rollback_status"] = rollbackStatus, ["write_started"] = true });
                    if (failure != null)
                        errors.Add(new JObject { ["index"] = -1, ["error"] = "Revit rejected the rehearsal: " + failure });
                    rehearsed = failure == null;
                }
            }

            if (dry)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["valid"] = plans.Count, ["invalid"] = errors.Count, ["errors"] = errors,
                    ["transaction_status"] = rollbackStatus, ["changes_applied"] = false,
                    ["plan"] = new JArray(plans.Select(p => new JObject
                    {
                        ["index"] = p.Index, ["operation"] = OpDrawOrder, ["view_id"] = Rid.Value(p.View.Id), ["view"] = p.View.Name, ["order"] = p.Order,
                        ["element_ids"] = new JArray(p.Elements.Select(e => Rid.Value(e.Id))), ["stack_size"] = p.Before?.Count
                    })),
                    ["rehearsal"] = rehearsalRows == null ? (JToken)JValue.CreateNull() : rehearsalRows
                };
                bool ok = errors.Count == 0 && rehearsed;
                if (ok) DocumentGate.RecordResolvedPlan(resolved);
                ApplicationOutcome.StampRehearsal(result, plans.Count + errors.Count, errors.Count, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, hash, ok,
                    ok ? "the token binds each view and element by UniqueId and the draw order read before the moves; a view whose stack changed before you spend it refuses as a stale plan."
                       : "no usable token: an action is invalid or Revit rejected the rehearsal");
                return FallbackDecision.Attach(CommandResult.Ok(result), FallbackDecision.Decide(outcomes, writeStarted: false));
            }

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash, resolved, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name); if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: detail draw order";
            var endStacks = new Dictionary<long, List<long>>(); JArray rows;
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try { rows = RunMoves(doc, plans, endStacks); Guard.Commit(tx, txName); }
                catch (Exception ex)
                {
                    bool attempted = false; string rb = PlanFailure.NotAttempted;
                    if (tx.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(tx).StatusName; }
                    return CommandResult.Fail("Detail draw order failed: " + ex.Message + ". " + PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing was reordered"));
                }
            }
            // Post-commit: every view must still read the stack the transaction ended on.
            int verified = 0;
            foreach (JObject row in rows.Cast<JObject>())
            {
                long viewId = row.Value<long>("view_id");
                bool held;
                try { held = endStacks.TryGetValue(viewId, out var expected) && StackOf(doc, (View)doc.GetElement(Rid.Make(viewId))).SequenceEqual(expected); }
                catch { held = false; }
                row["post_commit_verified"] = held; row["verified"] = (bool)row["verified"] && held;
                if ((bool)row["verified"]) verified++;
            }
            if (verified != rows.Count)
                return CommandResult.Fail("The transaction committed, but only " + verified + " of " + rows.Count + " draw-order action(s) passed the post-commit re-read. Inspect the views: " + rows.ToString(Formatting.None));
            var applied = new JObject { ["transaction_status"] = "Committed", ["state"] = DimensionPlanRules.StateCommittedVerified, ["actions_verified"] = verified, ["rows"] = rows };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, plans.Count, verified, verified, 0, 0, 0);
            applied["applied"] = true;
            return CommandResult.Ok(applied);
        }
    }
}
