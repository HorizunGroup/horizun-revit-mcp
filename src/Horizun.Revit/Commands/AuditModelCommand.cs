// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_audit_model — the pre-delivery health check.
//
// This is the tool you run before handing a model to a client. It answers one
// question: what in here will embarrass us? So its only job is to be true.
//
// Rules it follows that the handlers around it do not:
//
//   * NO SILENT CAPS. Every list says how many exist and how many are shown. A
//     truncated list that looks complete is how "the model is clean" gets said
//     about a model with 4,000 warnings.
//   * NO EMPTY CATCH. When a check cannot run, it says so and why, in the
//     response. A check that fails silently reads exactly like a check that
//     passed — that is worse than not running it, because it buys false calm.
//   * ORPHAN GROUP TYPES ARE COUNTED. Listing group *instances* misses group
//     types with zero instances: they carry their full geometry in the file,
//     never appear in any view, and survive Purge in older Revit. They are pure
//     invisible weight and the usual reason a model is inexplicably large.
//   * NOTHING IS SCORED AWAY. No 0-100 health index. A single number invites the
//     reader to stop reading; the findings are the deliverable.
//
// Read-only by construction: it opens no transaction, so it cannot damage the
// model it is auditing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public class AuditModelCommand : ICommand
    {
        public string Name => "horizun_audit_model";

        public string Description =>
            "Pre-delivery audit of the open model: warnings, orphan group types, in-place families, " +
            "imported (not linked) CAD, views off sheets, unplaced/redundant rooms, links, design options " +
            "and file weight. Read-only. Every count is the model's, every list states total vs. shown, " +
            "and any check that could not run is reported as failed rather than skipped silently.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""top"": { ""type"": ""integer"", ""default"": 20, ""minimum"": 1,
               ""description"": ""How many items to list per finding. Totals are always exact regardless of this."" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            int top = 20;
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                if (request["top"] != null) top = Math.Max(1, request.Value<int>("top"));
            }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var findings = new JArray();
            var checksFailed = new JArray();

            // Each check is wrapped so one failure cannot take the audit down, but
            // the failure is REPORTED, never swallowed.
            Run(checksFailed, "warnings", () => findings.Add(Warnings(doc, top)));
            Run(checksFailed, "group_types", () => findings.Add(GroupTypes(doc, top)));
            Run(checksFailed, "in_place_families", () => findings.Add(InPlaceFamilies(doc, top)));
            Run(checksFailed, "imported_cad", () => findings.Add(ImportedCad(doc, top)));
            Run(checksFailed, "views_off_sheets", () => findings.Add(ViewsOffSheets(doc, top)));
            Run(checksFailed, "rooms", () => findings.Add(Rooms(doc, top)));
            Run(checksFailed, "links", () => findings.Add(Links(doc, top)));
            Run(checksFailed, "design_options", () => findings.Add(DesignOptions(doc, top)));

            var issues = findings.Count(f => (bool)f["is_issue"]);

            // A check that RAN but could not read everything it examined. Distinct from
            // checks_failed, which is a check that died: this one produced a number, and
            // the number is a lower bound.
            var incompleteChecks = new JArray(
                findings.Where(f => f["coverage_complete"] != null && (bool)f["coverage_complete"] == false)
                        .Select(f => (JToken)new JObject
                        {
                            ["check"] = f["check"],
                            ["elements_unreadable"] = f["elements_unreadable"],
                            ["consequence"] = "'" + f["check"] + "' reports " + f["count"] +
                                              ", which is a LOWER BOUND. The elements it could not read are unknown, " +
                                              "not clean."
                        }));

            // THE THIRD WAY THIS AUDIT CAN FAIL TO SEE THE MODEL, and the only one that
            // leaves no trace in any check. A check that dies lands in checks_failed; a
            // check that cannot read an element lands in checks_with_incomplete_coverage.
            // A CLOSED WORKSET lands nowhere: its elements are not in the document, so
            // every check ran perfectly over a model with holes in it and reported clean.
            // See Core/DocumentVisibilityCoverage.cs.
            DocumentVisibilityCoverage visibility = DocumentVisibility.Measure(doc);

            return CommandResult.Ok(new JObject
            {
                ["model"] = SafeTitle(doc),
                ["path"] = SafePath(doc),
                ["file_size_mb"] = FileSizeMb(doc),
                ["element_count"] = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(),
                ["checks_run"] = findings.Count,
                ["checks_failed"] = checksFailed,
                ["checks_with_incomplete_coverage"] = incompleteChecks,
                ["visibility_coverage"] = visibility.ToJson(),
                ["coverage_complete"] = checksFailed.Count == 0 && incompleteChecks.Count == 0 &&
                                        visibility.CoverageComplete,
                ["issues_found"] = issues,
                ["findings"] = findings,
                ["note"] = (checksFailed.Count > 0 || incompleteChecks.Count > 0 || !visibility.CoverageComplete)
                    ? (checksFailed.Count > 0
                          ? $"{checksFailed.Count} check(s) could not run at all — see checks_failed. "
                          : "") +
                      (incompleteChecks.Count > 0
                          ? $"{incompleteChecks.Count} check(s) RAN but could not read every element they " +
                            "examined — see checks_with_incomplete_coverage. Their counts are lower bounds. "
                          : "") +
                      (visibility.CoverageComplete ? "" : visibility.Note() + " ") +
                      "This audit is INCOMPLETE; do not read the absence of a finding as a pass."
                    : null
            });
        }

        private static void Run(JArray failed, string name, Action check)
        {
            try { check(); }
            catch (Exception ex)
            {
                // A check that dies quietly is indistinguishable from a check that
                // passed. Say it out loud.
                failed.Add(new JObject
                {
                    ["check"] = name,
                    ["error"] = ex.Message,
                    ["consequence"] = $"'{name}' was NOT audited. Its findings are unknown, not clean."
                });
            }
        }

        /// <summary>
        /// One check's result, INCLUDING what it could not see.
        ///
        /// checks_failed already reports a check that died outright. It says nothing
        /// about a check that ran and silently skipped elements on the way - the
        /// `catch { return false; }` inside a filter, which turns "could not read this
        /// one" into "this one is fine". A count of 0 then reads as a pass when it
        /// means "none found among the ones I could read".
        ///
        /// elements_unreadable is that number, per check. coverage_complete is the
        /// single field to look at: false means the count below it is a LOWER BOUND.
        /// </summary>
        private static JObject Finding(string check, bool isIssue, int count, string summary, JArray items, int total,
                                       int elementsUnreadable = 0)
        {
            return new JObject
            {
                ["check"] = check,
                // An unreadable element cannot be ruled out as an issue, so a check that
                // could not read everything is not allowed to report a clean result.
                ["is_issue"] = isIssue || elementsUnreadable > 0,
                ["count"] = count,
                ["count_is_lower_bound"] = elementsUnreadable > 0,
                ["elements_unreadable"] = elementsUnreadable,
                ["coverage_complete"] = elementsUnreadable == 0,
                ["coverage_note"] = elementsUnreadable == 0
                    ? null
                    : elementsUnreadable + " element(s) could not be read by this check and are counted in neither " +
                      "column. They are NOT known to be clean - 'count' is a lower bound, and this check is " +
                      "reported as an issue for that reason alone.",
                ["summary"] = summary,
                ["shown"] = items?.Count ?? 0,
                ["total"] = total,
                ["truncated"] = items != null && items.Count < total,
                ["items"] = items
            };
        }

        // ---- Warnings: the model's own list of what it knows is wrong. ----
        private static JObject Warnings(Document doc, int top)
        {
            var all = doc.GetWarnings();
            var grouped = all
                .GroupBy(w => { try { return w.GetDescriptionText(); } catch { return "(description unavailable)"; } })
                .Select(g => new { desc = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .ToList();

            var items = new JArray(grouped.Take(top).Select(g => (JToken)new JObject
            {
                ["description"] = g.desc,
                ["occurrences"] = g.count
            }));

            return Finding("warnings", all.Count > 0, all.Count,
                all.Count == 0
                    ? "No warnings."
                    : $"{all.Count} warning(s) across {grouped.Count} distinct message(s). Warnings are Revit " +
                      "telling you the model already contradicts itself; they do not resolve themselves.",
                items, grouped.Count);
        }

        // ---- Group types with zero instances: invisible file weight. ----
        private static JObject GroupTypes(Document doc, int top)
        {
            // A Group whose GetTypeId cannot be read leaves its type looking UNPLACED,
            // because nothing added it to this set - so the check invents an orphan.
            // Counted instead of swallowed: the orphan count becomes a lower bound and
            // the check reports incomplete coverage.
            int unreadable = 0;
            var placed = new HashSet<ElementId>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>())
            {
                try { placed.Add(g.GetTypeId()); } catch { unreadable++; }
            }

            var orphans = new FilteredElementCollector(doc)
                .OfClass(typeof(GroupType))
                .Cast<GroupType>()
                .Where(gt => !placed.Contains(gt.Id))
                .ToList();

            var items = new JArray(orphans.Take(top).Select(gt => (JToken)new JObject
            {
                ["group_type_id"] = gt.Id.ToString(),
                ["name"] = SafeName(gt),
                ["members"] = SafeMemberCount(gt)
            }));

            return Finding("orphan_group_types", orphans.Count > 0, orphans.Count,
                orphans.Count == 0
                    ? (unreadable == 0
                        ? "Every group type is placed at least once."
                        : $"Every group type is placed at least once among the {unreadable} group(s) whose type " +
                          "could be read - and those that could NOT be read are not accounted for.")
                    : $"{orphans.Count} group type(s) exist with ZERO placed instances. They carry their full " +
                      "geometry in the file, appear in no view, and are the usual reason a model is " +
                      "inexplicably large. Listing group instances never finds these." +
                      (unreadable > 0
                          ? $" CAUTION: {unreadable} group(s) would not report their type, so a type they place " +
                            "may be listed here as an orphan that is not one."
                          : ""),
                items, orphans.Count, unreadable);
        }

        // ---- In-place families: the classic performance and coordination tax. ----
        private static JObject InPlaceFamilies(Document doc, int top)
        {
            // The catch here used to `return false`, which quietly EXCLUDED any instance
            // whose Symbol or Family could not be read - so an in-place family that
            // happened to be unreadable was reported as absent, and a count of 0 meant
            // "none found or none readable" while saying "none". Unreadable is now
            // counted and reported beside the count, not folded into it.
            int unreadable = 0;
            var inPlace = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi =>
                {
                    try { return fi.Symbol?.Family?.IsInPlace == true; }
                    catch { unreadable++; return false; }
                })
                .ToList();

            var grouped = inPlace
                .GroupBy(fi => { try { return fi.Symbol.Family.Name; } catch { return "(unnamed)"; } })
                .Select(g => new { name = g.Key, count = g.Count(), id = g.First().Id })
                .OrderByDescending(x => x.count)
                .ToList();

            var items = new JArray(grouped.Take(top).Select(g => (JToken)new JObject
            {
                ["family"] = g.name,
                ["instances"] = g.count,
                ["example_id"] = g.id.ToString()
            }));

            return Finding("in_place_families", inPlace.Count > 0, inPlace.Count,
                inPlace.Count == 0
                    ? (unreadable == 0
                        ? "No in-place families."
                        : $"No in-place families among the instances that could be read - but {unreadable} could " +
                          "NOT be read, so this is not a clean bill.")
                    : $"{inPlace.Count} in-place family instance(s) in {grouped.Count} family(ies). In-place " +
                      "geometry cannot be scheduled reliably, cannot be reused, and is recomputed on every " +
                      "regeneration. Each one is a loadable family somebody chose not to make.",
                items, grouped.Count, unreadable);
        }

        // ---- Imported vs linked CAD. Imported DWG is permanent weight. ----
        private static JObject ImportedCad(Document doc, int top)
        {
            // Same rule: an ImportInstance whose IsLinked cannot be read is UNKNOWN, and
            // unknown is not "linked, therefore fine".
            int unreadable = 0;
            var imports = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .Where(i => { try { return !i.IsLinked; } catch { unreadable++; return false; } })
                .ToList();

            var items = new JArray(imports.Take(top).Select(i => (JToken)new JObject
            {
                ["id"] = i.Id.ToString(),
                ["name"] = SafeName(i),
                ["view_specific"] = SafeViewSpecific(i)
            }));

            return Finding("imported_cad", imports.Count > 0, imports.Count,
                imports.Count == 0
                    ? (unreadable == 0
                        ? "No imported (non-linked) CAD."
                        : $"No imported CAD among the instances that could be read - but {unreadable} could NOT be " +
                          "read, so this is not a clean bill.")
                    : $"{imports.Count} CAD file(s) IMPORTED rather than linked. An import is permanent: its " +
                      "layers, line patterns and text styles are now part of this model's namespace and " +
                      "survive deletion of the instance. A link stays outside and can be reloaded or dropped.",
                items, imports.Count, unreadable);
        }

        // ---- Views that are not on any sheet: work nobody will ever see. ----
        private static JObject ViewsOffSheets(Document doc, int top)
        {
            // TWO silent catches lived here, and they failed in OPPOSITE directions.
            //
            // A Viewport whose ViewId could not be read left its view missing from
            // onSheet, so a view that IS on a sheet gets reported as off-sheet - a
            // fabricated finding. A View whose properties could not be read returned
            // false and vanished from the count - a hidden one. Both are now counted,
            // and either makes the check report incomplete coverage.
            int unreadable = 0;
            var onSheet = new HashSet<ElementId>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                try { onSheet.Add(vp.ViewId); } catch { unreadable++; }
            }

            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v =>
                {
                    try
                    {
                        if (v.IsTemplate) return false;
                        if (v is ViewSheet) return false;
                        // Schedules and legends can legitimately live off-sheet mid-project;
                        // 3D/plan/section views off-sheet are the ones that pile up.
                        if (v.ViewType == ViewType.Legend || v.ViewType == ViewType.Schedule) return false;
                        if (v.ViewType == ViewType.DrawingSheet || v.ViewType == ViewType.Internal) return false;
                        return !onSheet.Contains(v.Id);
                    }
                    catch { unreadable++; return false; }
                })
                .ToList();

            var items = new JArray(candidates.Take(top).Select(v => (JToken)new JObject
            {
                ["id"] = v.Id.ToString(),
                ["name"] = SafeName(v),
                ["type"] = v.ViewType.ToString()
            }));

            return Finding("views_off_sheets", candidates.Count > 0, candidates.Count,
                candidates.Count == 0
                    ? "Every non-legend, non-schedule view is placed on a sheet."
                    : $"{candidates.Count} view(s) are on no sheet. Some are working views and that is fine — " +
                      "this is a list to review before delivery, not a defect list. Legends and schedules are " +
                      "excluded on purpose." +
                      (unreadable > 0
                          ? $" CAUTION: {unreadable} viewport(s) or view(s) could not be read. A viewport that " +
                            "would not name its view makes the view it holds look off-sheet, so this list may " +
                            "contain views that are placed."
                          : ""),
                items, candidates.Count, unreadable);
        }

        // ---- Rooms: unplaced and redundant both corrupt area takeoffs. ----
        private static JObject Rooms(Document doc, int top)
        {
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .ToElements();

            // A room that could not be read is not a room that is fine. This catch used
            // to drop it, so a model whose rooms all failed to read reported "all rooms
            // are placed and enclosed" - a clean bill issued over nothing.
            int unreadable = 0;
            var bad = new List<(Element e, string why)>();
            foreach (var r in rooms)
            {
                try
                {
                    var area = r.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() ?? 0.0;
                    var loc = r.Location;
                    if (loc == null) bad.Add((r, "unplaced (no location — it exists in schedules but bounds nothing)"));
                    else if (area <= 0.0) bad.Add((r, "not enclosed (area 0 — its boundary is open, so it measures nothing)"));
                }
                catch { unreadable++; }
            }

            var items = new JArray(bad.Take(top).Select(b => (JToken)new JObject
            {
                ["id"] = b.e.Id.ToString(),
                ["name"] = SafeName(b.e),
                ["problem"] = b.why
            }));

            return Finding("rooms", bad.Count > 0, bad.Count,
                rooms.Count == 0
                    ? "No rooms in this model."
                    : bad.Count == 0
                        ? (unreadable == 0
                            ? $"All {rooms.Count} room(s) are placed and enclosed."
                            : $"Of {rooms.Count} room(s), the ones that could be read are placed and enclosed - but " +
                              $"{unreadable} could NOT be read, so this is not a clean bill.")
                        : $"{bad.Count} of {rooms.Count} room(s) are unplaced or unenclosed. Both still appear " +
                          "in room schedules — with an area of zero. Any area takeoff from this model is " +
                          "understated until they are fixed." +
                          (unreadable > 0 ? $" A further {unreadable} could not be read at all." : ""),
                items, bad.Count, unreadable);
        }

        // ---- Links: an unloaded link is a coordination hole. ----
        private static JObject Links(Document doc, int top)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            // Read each status ONCE, keeping null for "would not answer". This used to be
            // read twice per link, and the second read wrote `catch { return true; }` -
            // counting a failed read as an unloaded link. On a cloud-hosted model that
            // fabricates a defect: the link is loaded, we just could not ask.
            var statuses = new List<string>(types.Count);
            foreach (var lt in types)
            {
                string s = null;
                try { s = lt.GetLinkedFileStatus().ToString(); } catch { s = null; }
                statuses.Add(s);
            }

            var items = new JArray();
            for (int i = 0; i < types.Count && i < top; i++)
                items.Add(new JObject
                {
                    ["id"] = types[i].Id.ToString(),
                    ["name"] = SafeName(types[i]),
                    ["status"] = statuses[i] ?? "(unreadable)",
                    ["status_unreadable"] = statuses[i] == null
                });

            LinkTally tally = LinkStatusTally.Of(statuses);

            // An issue when a link is genuinely not loaded, AND when coverage is partial:
            // "I could not check" is a finding to review, not a pass.
            // This check tallies link TYPES; model_scan's links section tallies link
            // INSTANCES. On a model with the same link loaded several times the two
            // numbers differ ("1 of 8" vs "4 of 22", measured 2026-07-30) and both are
            // right - so each summary now names its unit instead of making the reader
            // guess which one is lying.
            return Finding("links", tally.NotLoaded > 0 || !tally.Complete, types.Count,
                tally.Summary("link type"), items, types.Count);
        }

        // ---- Design options: geometry that is in the file but not in the delivery. ----
        private static JObject DesignOptions(Document doc, int top)
        {
            var opts = new FilteredElementCollector(doc)
                .OfClass(typeof(DesignOption))
                .Cast<DesignOption>()
                .ToList();

            var items = new JArray(opts.Take(top).Select(o => (JToken)new JObject
            {
                ["id"] = o.Id.ToString(),
                ["name"] = SafeName(o),
                ["is_primary"] = SafePrimary(o)
            }));

            return Finding("design_options", opts.Count > 0, opts.Count,
                opts.Count == 0
                    ? "No design options."
                    : $"{opts.Count} design option(s) present. Elements in a non-primary option are in the file " +
                      "and in nobody's takeoff. Confirm this is intended before delivering.",
                items, opts.Count);
        }

        // ---- Small, boring, and each one honest about failing. ----
        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafePath(Document d) { try { return d.PathName; } catch { return null; } }
        private static string SafeName(Element e) { try { return e?.Name; } catch { return null; } }
        private static JToken SafePrimary(DesignOption o) { try { return o.IsPrimary; } catch { return null; } }
        private static JToken SafeViewSpecific(Element e) { try { return e.ViewSpecific; } catch { return null; } }
        private static JToken SafeMemberCount(GroupType gt)
        {
            try
            {
                var g = gt.Groups?.Cast<Group>().FirstOrDefault();
                return g?.GetMemberIds()?.Count;
            }
            catch { return null; }
        }

        private static JToken FileSizeMb(Document doc)
        {
            try
            {
                var p = doc.PathName;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
                return Math.Round(new FileInfo(p).Length / 1048576.0, 2);
            }
            catch { return null; }
        }
    }
}
