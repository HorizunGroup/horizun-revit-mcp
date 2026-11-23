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
            bool? requestedCutoff = request.Value<bool?>("include_as_of_day");
            bool includeAsOfDay = requestedCutoff ?? true;
            var assumptions = new[] { ScheduleLinkRules.StatusCutoffAssumption(requestedCutoff) };
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

            ScheduleMatch m = ScheduleLinkRules.Match(spec, schedule, Facts(doc, spec, null));
            JObject statusBasis = ScheduleLinkRules.StatusBasis(m, asOf, requestedCutoff, maxRows, out var status);
            string name = UniqueViewName(doc, "HZ 4D " + asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " - " + source.Name);

            head["document"] = doc.Title;
            MatchJson(head, m, maxRows);
            head["as_of"] = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            head["include_as_of_day"] = includeAsOfDay;
            head["last_included_day"] = ScheduleImport.Day(ScheduleLinkRules.LastIncludedDay(asOf, requestedCutoff));
            head["status_counts"] = new JObject(ScheduleLinkRules.Statuses.Select(s => new JProperty(s, status.Values.Count(v => v == s))));
            head["status_basis"] = statusBasis;
            head["colors"] = new JObject(StatusColors.Select(c => new JProperty(c.Key, "#" + c.Value.Red.ToString("X2") + c.Value.Green.ToString("X2") + c.Value.Blue.ToString("X2"))));
            head["source_view_id"] = viewId;
            head["source_view_unique_id"] = source.UniqueId;
            head["view_name"] = name;

            var plan = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber, DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            ScheduleLinkRules.BindStatusViewPlan(plan, m, status, viewId, source.UniqueId, name);
            string planHash = ScheduleLinkRules.PlanHash(request, fileSha, "status_view");

            if (dryRun)
                return ScheduleLinkRules.RehearseTargets(head, m.Ambiguous.Count, DocumentGate.Confirmations, gate.Identity,
                    planHash, plan, "the token binds the schedule SHA-256, match spec, source view identity, date cutoff and resolved overrides.", assumptions);
            if (status.Count == 0)
                return ScheduleLinkRules.NoStatusViewTargets(head, m, assumptions);

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, plan, null);
            if (refusal != null) return refusal;

            ElementId solid = SolidFill(doc);
            if (solid == ElementId.InvalidElementId)
                return AssumptionNotes.MarkFailure(CommandResult.FailWithDetail(
                    "The document has no solid fill pattern to colour with. Nothing was changed.", head), "", assumptions);

            TransactionStatus commit = TransactionStatus.Uninitialized;
            string cleanupError = null;
            ElementId dupId = ElementId.InvalidElementId;
            var refused = new List<long>();
            try
            {
                using var tx = new Transaction(doc, "Horizun: 4D status view");
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
                catch (Exception ex)
                {
                    var failure = ScheduleLinkRules.FailAfterRollback("Revit could not create the status view: " + ex.Message,
                        () => Guard.RollBack(tx).StatusName, head, status.Count + 1, m.Ambiguous.Count, assumptions,
                        () => tx.GetStatus().ToString());
                    failure.Detail["postcondition"] = CheckStatusView(null, name,
                        StatusViewReadSkipped((string)failure.Detail["transaction_status"])).ToJson();
                    return failure;
                }
            }
            catch (Exception ex) when (commit == TransactionStatus.Committed)
            {
                cleanupError = ex.Message;
            }

            var result = VerifyStatusView(doc, gate, head, planHash, commit, dupId, name, solid, m, status, refused, maxRows, assumptions);
            return ScheduleLinkRules.ReportCommittedCleanupFailure(result, cleanupError);
        }

        private CommandResult VerifyStatusView(Document doc, GateResult gate, JObject head, string planHash, TransactionStatus commit,
            ElementId dupId, string name, ElementId solid, ScheduleMatch match, Dictionary<long, string> status,
            List<long> refused, int maxRows, IEnumerable<JObject> assumptions)
        {
            View view = null;
            string viewReadError = null;
            if (commit == TransactionStatus.Committed)
            {
                try { view = doc.GetElement(dupId) as View; }
                catch (Exception ex) { viewReadError = ex.Message; }
            }
            else
                viewReadError = StatusViewReadSkipped(commit.ToString());
            var post = CheckStatusView(view, name, viewReadError);

            int verified = 0, mismatched = 0, unverified = 0;
            var mismatches = new JArray();
            var readErrors = new JArray();
            foreach (var kv in status)
            {
                if (refused.Contains(kv.Key)) continue;
                // A measured absence of the view also proves its overrides are absent.
                bool? matches = viewReadError == null ? false : (bool?)null;
                if (view != null)
                {
                    string uniqueId = match.LinkedUniqueIds[kv.Key];
                    try
                    {
                        Element target = doc.GetElement(uniqueId);
                        OverrideGraphicSettings actual = target == null ? null : view.GetElementOverrides(target.Id);
                        Color want = StatusColors[kv.Value];
                        matches = actual != null && actual.SurfaceForegroundPatternId == solid && actual.CutForegroundPatternId == solid &&
                            ColorMatches(actual.SurfaceForegroundPatternColor, want) && ColorMatches(actual.CutForegroundPatternColor, want) &&
                            ColorMatches(actual.ProjectionLineColor, want);
                    }
                    catch (Exception ex)
                    {
                        matches = null;
                        if (readErrors.Count < maxRows)
                            readErrors.Add(new JObject { ["element_id"] = kv.Key, ["unique_id"] = uniqueId, ["error"] = ex.Message });
                    }
                }
                if (matches == true) verified++;
                else
                {
                    if (matches == false) mismatched++;
                    unverified++;
                    if (mismatches.Count < maxRows) mismatches.Add(kv.Key);
                }
            }
            head["view_id"] = view == null ? (JToken)JValue.CreateNull() : Rid.Value(view.Id);
            head["postcondition"] = post.ToJson();
            head["overrides_verified"] = verified;
            head["overrides_refused_by_revit"] = new JArray(refused.Take(maxRows));
            head["overrides_not_verified"] = mismatches;
            if (readErrors.Count > 0) head["override_read_errors"] = readErrors;
            DocumentGate.StampConfirmation(head, gate, Name, planHash, false);
            bool viewMeasured = viewReadError == null && (view == null || post.AllMeasured);
            return ScheduleLinkRules.CompleteStatusView(head, match, commit.ToString(), post.AllVerified,
                viewMeasured, verified, refused.Count, mismatched, unverified, assumptions);
        }

        private static string StatusViewReadSkipped(string status) =>
            "The duplicated view was not re-read (transaction status: " + status + ").";

        private static PostconditionCheck CheckStatusView(View view, string name, string viewReadError)
        {
            var post = new PostconditionCheck("view_exists", "view_name", "template_detached");
            if (viewReadError == null) post.Compare("view_exists", true, view != null);
            else post.Unreadable("view_exists", true, viewReadError);
            if (view == null)
            {
                string reason = viewReadError ?? "the duplicated view was not found after the commit.";
                post.Unreadable("view_name", name, reason);
                post.Unreadable("template_detached", true, reason);
                return post;
            }

            try { post.Compare("view_name", name, view.Name); }
            catch (Exception ex) { post.Unreadable("view_name", name, ex.Message); }
            try { post.Compare("template_detached", true, view.ViewTemplateId == ElementId.InvalidElementId); }
            catch (Exception ex) { post.Unreadable("template_detached", true, ex.Message); }
            return post;
        }

        private static bool ColorMatches(Color actual, Color expected) =>
            actual != null && actual.IsValid && actual.Red == expected.Red && actual.Green == expected.Green && actual.Blue == expected.Blue;

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
