// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_link_schedule, operation=status_view. Original code.
//
// Duplicates the given view (the original is never touched), detaches the copy
// from any view template so element overrides show, and colours every matched
// element by its status at as_of: done green, in_progress amber, future grey,
// late red. After the commit the copy and EVERY override are re-read.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class LinkScheduleCommand
    {
        private static readonly Dictionary<string, Color> StatusColors = new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            { "done", new Color(0, 153, 0) }, { "in_progress", new Color(255, 176, 0) },
            { "future", new Color(170, 170, 170) }, { "late", new Color(220, 0, 0) }
        };

        private CommandResult StatusView(UIApplication app, JObject request, ScheduleImportResult schedule, JObject spec,
                                         string fileSha, JObject head, int maxRows)
        {
            if (!DateTime.TryParseExact(request.Value<string>("as_of") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime asOf))
                return CommandResult.Fail("as_of is required as yyyy-MM-dd.");
            long viewId = request.Value<long?>("view_id") ?? -1;

            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");

            View source = viewId > 0 && Rid.CanRepresent(viewId) ? doc.GetElement(Rid.Make(viewId)) as View : null;
            if (source == null || source.IsTemplate || source is ViewSheet || source is ViewSchedule)
                return CommandResult.Fail("view_id must name a model view (plan, section or 3D), not a template, sheet or schedule.");
            if (!source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
                return CommandResult.Fail("View " + viewId + " cannot be duplicated.");

            ScheduleMatch m = ScheduleLinkRules.Match(spec, schedule.Activities, Facts(doc, spec, null));
            var status = new Dictionary<long, string>();
            int fromProgress = 0;
            foreach (var kv in m.Links)
            {
                status[kv.Key] = ScheduleLinkRules.Status(kv.Value, asOf, out bool p);
                if (p) fromProgress++;
            }
            string name = UniqueViewName(doc, "HZ 4D " + asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " - " + source.Name);

            head["document"] = doc.Title;
            MatchJson(head, m, maxRows);
            head["as_of"] = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            head["status_counts"] = new JObject(ScheduleLinkRules.Statuses.Select(s => new JProperty(s, status.Values.Count(v => v == s))));
            head["status_basis"] = new JObject
            {
                ["from_progress"] = fromProgress, ["planned_only"] = status.Count - fromProgress,
                ["means"] = "planned_only elements are coloured by planned dates; 'late' needs percent complete or actual dates and is never assigned to them."
            };
            head["colors"] = new JObject(StatusColors.Select(c => new JProperty(c.Key, "#" + c.Value.Red.ToString("X2") + c.Value.Green.ToString("X2") + c.Value.Blue.ToString("X2"))));
            head["view_name"] = name;

            var plan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            plan.Elements.Add(new PlannedElement { UniqueId = "view:" + name, Action = PlannedAction.Create, ElementId = viewId });
            foreach (var kv in status.OrderBy(k => k.Key))
                plan.Elements.Add(new PlannedElement
                {
                    UniqueId = doc.GetElement(Rid.Make(kv.Key))?.UniqueId, ElementId = kv.Key, Action = PlannedAction.Modify,
                    ProposedValues = new Dictionary<string, string> { { "status", kv.Value } }
                });
            string planHash = PlanHash(request, fileSha, "status_view");

            if (dryRun)
            {
                head["dry_run"] = true;
                DocumentGate.RecordResolvedPlan(plan);
                DocumentGate.StampConfirmation(head, gate, Name, planHash, true,
                    "the token binds the schedule file's SHA-256, the match spec, the view and as_of.");
                ApplicationOutcome.StampRehearsal(head, status.Count + 1, 0, 0, 0);
                return CommandResult.Ok(head);
            }
            if (status.Count == 0) return CommandResult.Fail("No element is linked to an activity; there is nothing to colour. Nothing was changed.");

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, plan, null);
            if (refusal != null) return refusal;

            ElementId solid = SolidFill(doc);
            if (solid == ElementId.InvalidElementId)
                return CommandResult.Fail("The document has no solid fill pattern to colour with. Nothing was changed.");

            TransactionStatus commit = TransactionStatus.Uninitialized;
            ElementId dupId = ElementId.InvalidElementId;
            var refused = new List<long>();
            using (var tx = new Transaction(doc, "Horizun: 4D status view"))
            {
                tx.Start();
                try
                {
                    dupId = source.Duplicate(ViewDuplicateOption.Duplicate);
                    View dup = (View)doc.GetElement(dupId);
                    dup.Name = name;
                    if (dup.ViewTemplateId != ElementId.InvalidElementId) dup.ViewTemplateId = ElementId.InvalidElementId;
                    foreach (var kv in status)
                    {
                        try { dup.SetElementOverrides(Rid.Make(kv.Key), Overrides(StatusColors[kv.Value], solid)); }
                        catch { refused.Add(kv.Key); }
                    }
                    try { commit = Guard.Commit(tx, "4D status view"); }
                    catch (SilentRollbackException ex) { commit = ex.Status; }
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx);
                    throw;
                }
            }
            return VerifyStatusView(doc, gate, head, planHash, commit, dupId, name, status, refused, maxRows);
        }

        private CommandResult VerifyStatusView(Document doc, GateResult gate, JObject head, string planHash, TransactionStatus commit,
                                               ElementId dupId, string name, Dictionary<long, string> status, List<long> refused, int maxRows)
        {
            View view = commit == TransactionStatus.Committed ? doc.GetElement(dupId) as View : null;
            var post = new PostconditionCheck("view_exists", "view_name");
            post.Compare("view_exists", true, view != null);
            if (view != null) post.Compare("view_name", name, view.Name);
            else post.Unreadable("view_name", name, "the duplicated view was not found after the commit.");

            int verified = 0, unverified = 0;
            var mismatches = new JArray();
            foreach (var kv in status)
            {
                if (refused.Contains(kv.Key)) continue;
                bool ok = false;
                try
                {
                    Color c = view?.GetElementOverrides(Rid.Make(kv.Key)).SurfaceForegroundPatternColor;
                    Color want = StatusColors[kv.Value];
                    ok = c != null && c.IsValid && c.Red == want.Red && c.Green == want.Green && c.Blue == want.Blue;
                }
                catch { }
                if (ok) verified++;
                else { unverified++; if (mismatches.Count < maxRows) mismatches.Add(kv.Key); }
            }
            head["dry_run"] = false;
            head["view_id"] = view == null ? (JToken)JValue.CreateNull() : Rid.Value(view.Id);
            head["postcondition"] = post.ToJson();
            head["overrides_verified"] = verified;
            head["overrides_refused_by_revit"] = new JArray(refused.Take(maxRows));
            head["overrides_not_verified"] = mismatches;
            DocumentGate.StampConfirmation(head, gate, Name, planHash, false);
            ApplicationOutcome.Stamp(head, WriteTally.PerTarget(commit.ToString(), status.Count, 0, verified, unverified + refused.Count));
            bool all = post.AllVerified && unverified == 0 && refused.Count == 0;
            return all ? CommandResult.Ok(head)
                : CommandResult.FailWithDetail("The status view did not fully verify: commit " + commit + ", " + refused.Count +
                    " override(s) refused, " + unverified + " not re-read as requested. The view, if created, remains.", head);
        }

        private static OverrideGraphicSettings Overrides(Color c, ElementId solid)
        {
            var o = new OverrideGraphicSettings();
            o.SetSurfaceForegroundPatternId(solid);
            o.SetSurfaceForegroundPatternColor(c);
            o.SetCutForegroundPatternId(solid);
            o.SetCutForegroundPatternColor(c);
            o.SetProjectionLineColor(c);
            return o;
        }

        private static ElementId SolidFill(Document doc)
        {
            foreach (FillPatternElement f in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                try { if (f.GetFillPattern().IsSolidFill) return f.Id; } catch { }
            }
            return ElementId.InvalidElementId;
        }

        private static string UniqueViewName(Document doc, string wanted)
        {
            // Revit refuses these in a view name ({3D} is the classic source).
            wanted = new string(wanted.Where(ch => "\\:{}[]|;<>?`~".IndexOf(ch) < 0).ToArray()).Trim();
            var taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Select(v => { try { return v.Name; } catch { return null; } }).Where(n => n != null), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(wanted)) return wanted;
            for (int i = 2; ; i++) if (!taken.Contains(wanted + " (" + i + ")")) return wanted + " (" + i + ")";
        }
    }
}
