// -----------------------------------------------------------------------------
// Horizun Revit MCP - text, tags and dimensions with explicit references.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class AnnotateCommand : ICommand
    {
        public string Name => "horizun_annotate";
        public string Description => "Create text, tags and dimensions atomically and verify the committed annotations.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name); if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document; JArray actions = request["actions"] as JArray;
            if (actions == null || actions.Count == 0 || actions.Count > 1000) return CommandResult.Fail("actions must contain 1..1000 entries.");
            double scale; if (!Scale((request.Value<string>("units") ?? "mm").ToLowerInvariant(), out scale)) return CommandResult.Fail("units must be mm, m or feet.");

            var plans = new List<Plan>(); var errors = new JArray();
            // Every action's outcome, so the fallback is decided once over the whole
            // batch: one uncovered operation must not grant permission for a request that
            // also contains input the caller should fix. See FallbackDecision.
            var outcomes = new List<ActionOutcome>();
            for (int i = 0; i < actions.Count; i++)
            {
                string error = null, reason = null;
                Plan p = PlanAction(doc, i, actions[i] as JObject, scale, out error, out reason);
                if (p == null)
                {
                    string message = error ?? "entry is not an object";
                    errors.Add(new JObject { ["index"] = i, ["error"] = message });
                    outcomes.Add(new ActionOutcome { Index = i, Error = message, UnsupportedReason = reason });
                }
                else plans.Add(p);
            }
            bool dry = request["dry_run"] == null || request.Value<bool>("dry_run");
            string hash = DocumentGate.PlanHash(request, "units", "actions");

            // ---- The MATERIALISED plan: the VIEW and TARGET each annotation lands on. ---
            // hash binds the actions as written. An annotation is ABOUT something: a tag
            // points at a target element, a dimension hangs off references measured from
            // one, and everything lands on a view. A tag approved against "Bomba 5" that
            // gets applied after somebody swaps that element is a label telling a reader
            // the wrong thing in print - the quietest wrong answer a model can produce.
            // So each row records the view and the target as resolved now, by identity
            // and by name, and the dimension's reference count - the number the rehearsal
            // itself already showed the caller.
            var resolvedPlan = new ResolvedPlan
            {
                Command = Name,
                DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            foreach (Plan planned in plans)
            {
                resolvedPlan.Elements.Add(new PlannedElement
                {
                    UniqueId = "action:" + planned.Index,
                    Category = planned.Operation,
                    Action = PlannedAction.Create,
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "view", SafePlanIdName(planned.View) },
                        { "target", SafePlanIdName(planned.Target) },
                        { "type", SafePlanIdName(planned.Type) },
                        { "references", planned.References == null ? "" :
                              planned.References.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    }
                });
            }

            if (dry)
            {
                var result = new JObject { ["dry_run"] = true, ["valid"] = plans.Count, ["invalid"] = errors.Count,
                    ["errors"] = errors, ["plan"] = new JArray(plans.Select(p => new JObject { ["index"] = p.Index, ["operation"] = p.Operation, ["references"] = p.References?.Size })) };
                if (errors.Count == 0) DocumentGate.RecordResolvedPlan(resolvedPlan);
                DocumentGate.StampConfirmation(result, gate, Name, hash, errors.Count == 0,
                    errors.Count == 0
                        ? "the token binds views, geometry, text, AND the identity of every view and target as " +
                          "resolved right now - a swapped or renamed target refuses as a stale plan rather than " +
                          "tagging the wrong element under the approved words."
                        : "no usable token while invalid");
                // THE REHEARSAL CARRIES THE VERDICT TOO. dry_run defaults to true, so this
                // is the first call a caller makes; without the block here they got
                // success=true with invalid rows and no way to tell a capability gap
                // from a typo except by sending an apply they had no reason to send.
                return FallbackDecision.Attach(
                    CommandResult.Ok(result),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            if (errors.Count > 0)
            {
                // NOTHING RAN - no transaction was opened - so the decision is only about
                // what failed, and it is made centrally.
                return FallbackDecision.Refuse(
                    "Invalid annotations; nothing ran: " + errors.ToString(Formatting.None),
                    FallbackDecision.Decide(outcomes, writeStarted: false));
            }
            // Recomputed by THIS call's own PlanAction resolution.
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, hash,
                                                                     resolvedPlan, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name); if (refusal != null) return refusal;

            string txName = request.Value<string>("transaction_name"); if (string.IsNullOrWhiteSpace(txName)) txName = "Horizun: annotate";
            using (var tx = new Transaction(doc, txName))
            {
                tx.Start();
                try { foreach (Plan p in plans) p.Created = Create(doc, p)?.Id; Guard.Commit(tx, txName); }
                catch (Exception ex)
                {
                    // Report what the rollback ACTUALLY did, not the hoped-for prose. If the
                    // transaction is still open we roll it back and read Revit's status; a value
                    // other than RolledBack keeps its uncertainty rather than claiming a clean model.
                    string rolled = "was not attempted (the transaction was not open)";
                    if (tx.GetStatus() == TransactionStatus.Started)
                    {
                        Guard.RollbackResult rb = Guard.RollBack(tx);
                        rolled = rb.Confirmed
                            ? "rolled back (Revit reported RolledBack); nothing was retained"
                            : "is UNCERTAIN: RollBack() returned '" + rb.StatusName + "', not RolledBack - re-read the model before any retry";
                    }
                    return CommandResult.Fail("Atomic annotation failed: " + ex.Message + ". The transaction " + rolled + ".");
                }
            }
            var rows = new JArray(); int verified = 0;
            foreach (Plan p in plans)
            {
                Element e = p.Created == null ? null : doc.GetElement(p.Created); bool ok = Verify(p, e); if (ok) verified++;
                rows.Add(new JObject { ["index"] = p.Index, ["operation"] = p.Operation, ["element_id"] = p.Created == null ? JValue.CreateNull() : new JValue(Rid.Value(p.Created)), ["verified"] = ok });
            }
            if (verified != plans.Count) return CommandResult.Fail("Committed, but annotation verification failed: " + rows.ToString(Formatting.None));
            return CommandResult.Ok(new JObject { ["transaction_status"] = "Committed", ["annotations_verified"] = verified, ["rows"] = rows });
        }

        private static Plan PlanAction(Document doc, int index, JObject a, double scale, out string error,
                                       out string unsupportedReason)
        {
            error = null; unsupportedReason = null;
            if (a == null) { error = "entry is not an object"; return null; }
            try
            {
                string op = (a.Value<string>("operation") ?? "").ToLowerInvariant(); View view = Need<View>(doc, a, "view_id");
                if (view.IsTemplate) throw new ArgumentException("view_id is a template");
                var p = new Plan { Index = index, Operation = op, View = view, Input = a, Scale = scale };
                if (op == "text")
                {
                    p.Point = Point(a["point"], scale, false); p.Text = a.Value<string>("text");
                    if (string.IsNullOrEmpty(p.Text)) throw new ArgumentException("text is required");
                    p.Type = Need<TextNoteType>(doc, a, "text_type_id");
                }
                else if (op == "tag")
                {
                    p.Point = Point(a["point"], scale, false); p.Target = Need<Element>(doc, a, "element_id");
                }
                else if (op == "dimension")
                {
                    XYZ x = Point(a["line_start"], scale, true), y = Point(a["line_end"], scale, true);
                    if (x.DistanceTo(y) < 1e-9) throw new ArgumentException("dimension line endpoints must differ"); p.Line = Line.CreateBound(x, y);
                    JArray refs = a["references"] as JArray; if (refs == null || refs.Count < 2) throw new ArgumentException("references needs at least two stable reference strings");
                    p.References = new ReferenceArray();
                    foreach (JToken token in refs) p.References.Append(Reference.ParseFromStableRepresentation(doc, token.Value<string>()));
                    if (a["dimension_type_id"] != null) p.Type = Need<DimensionType>(doc, a, "dimension_type_id");
                }
                else throw new UnsupportedCapability(
                    "unsupported operation '" + op + "' - horizun_annotate creates text, tags and dimensions " +
                    "only. Nothing was written.", FallbackSignal.ReasonUnsupportedOperation);
                return p;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return null;
            }
        }
        private static Element Create(Document doc, Plan p)
        {
            if (p.Operation == "text") return TextNote.Create(doc, p.View.Id, p.Point, p.Text, p.Type.Id);
            if (p.Operation == "tag")
            {
                TagMode mode = TagMode.TM_ADDBY_CATEGORY; string m = (p.Input.Value<string>("tag_mode") ?? "by_category").ToLowerInvariant();
                if (m == "multi_category") mode = TagMode.TM_ADDBY_MULTICATEGORY; else if (m == "material") mode = TagMode.TM_ADDBY_MATERIAL;
                TagOrientation orientation = (p.Input.Value<string>("orientation") ?? "horizontal").ToLowerInvariant() == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;
                return IndependentTag.Create(doc, p.View.Id, new Reference(p.Target), p.Input.Value<bool?>("add_leader") == true, mode, orientation, p.Point);
            }
            DimensionType type = p.Type as DimensionType;
            return type == null ? doc.Create.NewDimension(p.View, p.Line, p.References) : doc.Create.NewDimension(p.View, p.Line, p.References, type);
        }
        private static bool Verify(Plan p, Element e)
        {
            if (p.Operation == "text") return e is TextNote note && note.Text == p.Text;
            if (p.Operation == "tag") return e is IndependentTag tag && tag.GetTaggedLocalElementIds().Any(id => id == p.Target.Id);
            return e is Dimension dimension && dimension.References != null && dimension.References.Size == p.References.Size;
        }
        private static T Need<T>(Document d, JObject a, string f) where T : Element { long id = a.Value<long?>(f) ?? -1; if (!Rid.CanRepresent(id) || !(d.GetElement(Rid.Make(id)) is T e)) throw new ArgumentException(f + " must identify " + typeof(T).Name); return e; }
        private static XYZ Point(JToken t, double s, bool z) { JArray a = t as JArray; if (a == null || a.Count < (z ? 3 : 2) || a.Count > 3) throw new ArgumentException("point/line coordinate has wrong length"); return new XYZ(a[0].Value<double>() * s, a[1].Value<double>() * s, (a.Count > 2 ? a[2].Value<double>() : 0) * s); }
        private static bool Scale(string u, out double s) { if (u == "feet") { s = 1; return true; } if (u == "m") { s = 1 / 0.3048; return true; } if (u == "mm") { s = 1 / 304.8; return true; } s = 0; return false; }
        /// <summary>
        /// Identity and name in one guarded read. The name is what the person read when
        /// they approved; the UniqueId is what makes a swap under the same name visible.
        /// A plan must never fail while MEASURING, so unreadable stays a value.
        /// </summary>
        private static string SafePlanIdName(Element e)
        {
            if (e == null) return "";
            string uid; try { uid = e.UniqueId ?? ""; } catch { uid = "<unreadable>"; }
            string name; try { name = e.Name ?? ""; } catch { name = "<unreadable>"; }
            return uid + "|" + name;
        }

        private sealed class Plan { public int Index; public string Operation, Text; public View View; public JObject Input; public double Scale; public XYZ Point; public Element Target, Type; public Line Line; public ReferenceArray References; public ElementId Created; }
    }
}
