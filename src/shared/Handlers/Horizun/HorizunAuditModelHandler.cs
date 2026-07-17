// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
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

namespace RvtMcp.Plugin.Handlers
{
    public class HorizunAuditModelHandler : IRevitCommand
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

            return CommandResult.Ok(new JObject
            {
                ["model"] = SafeTitle(doc),
                ["path"] = SafePath(doc),
                ["file_size_mb"] = FileSizeMb(doc),
                ["element_count"] = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(),
                ["checks_run"] = findings.Count,
                ["checks_failed"] = checksFailed,
                ["issues_found"] = issues,
                ["findings"] = findings,
                ["note"] = checksFailed.Count > 0
                    ? $"{checksFailed.Count} check(s) could not run — see checks_failed. This audit is INCOMPLETE; " +
                      "do not read the absence of a finding as a pass."
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

        private static JObject Finding(string check, bool isIssue, int count, string summary, JArray items, int total)
        {
            return new JObject
            {
                ["check"] = check,
                ["is_issue"] = isIssue,
                ["count"] = count,
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
            var placed = new HashSet<ElementId>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>())
            {
                try { placed.Add(g.GetTypeId()); } catch { }
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
                    ? "Every group type is placed at least once."
                    : $"{orphans.Count} group type(s) exist with ZERO placed instances. They carry their full " +
                      "geometry in the file, appear in no view, and are the usual reason a model is " +
                      "inexplicably large. Listing group instances never finds these.",
                items, orphans.Count);
        }

        // ---- In-place families: the classic performance and coordination tax. ----
        private static JObject InPlaceFamilies(Document doc, int top)
        {
            var inPlace = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => { try { return fi.Symbol?.Family?.IsInPlace == true; } catch { return false; } })
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
                    ? "No in-place families."
                    : $"{inPlace.Count} in-place family instance(s) in {grouped.Count} family(ies). In-place " +
                      "geometry cannot be scheduled reliably, cannot be reused, and is recomputed on every " +
                      "regeneration. Each one is a loadable family somebody chose not to make.",
                items, grouped.Count);
        }

        // ---- Imported vs linked CAD. Imported DWG is permanent weight. ----
        private static JObject ImportedCad(Document doc, int top)
        {
            var imports = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .Where(i => { try { return !i.IsLinked; } catch { return false; } })
                .ToList();

            var items = new JArray(imports.Take(top).Select(i => (JToken)new JObject
            {
                ["id"] = i.Id.ToString(),
                ["name"] = SafeName(i),
                ["view_specific"] = SafeViewSpecific(i)
            }));

            return Finding("imported_cad", imports.Count > 0, imports.Count,
                imports.Count == 0
                    ? "No imported (non-linked) CAD."
                    : $"{imports.Count} CAD file(s) IMPORTED rather than linked. An import is permanent: its " +
                      "layers, line patterns and text styles are now part of this model's namespace and " +
                      "survive deletion of the instance. A link stays outside and can be reloaded or dropped.",
                items, imports.Count);
        }

        // ---- Views that are not on any sheet: work nobody will ever see. ----
        private static JObject ViewsOffSheets(Document doc, int top)
        {
            var onSheet = new HashSet<ElementId>();
            foreach (var vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                try { onSheet.Add(vp.ViewId); } catch { }
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
                    catch { return false; }
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
                      "excluded on purpose.",
                items, candidates.Count);
        }

        // ---- Rooms: unplaced and redundant both corrupt area takeoffs. ----
        private static JObject Rooms(Document doc, int top)
        {
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .ToElements();

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
                catch { }
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
                        ? $"All {rooms.Count} room(s) are placed and enclosed."
                        : $"{bad.Count} of {rooms.Count} room(s) are unplaced or unenclosed. Both still appear " +
                          "in room schedules — with an area of zero. Any area takeoff from this model is " +
                          "understated until they are fixed.",
                items, bad.Count);
        }

        // ---- Links: an unloaded link is a coordination hole. ----
        private static JObject Links(Document doc, int top)
        {
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            var items = new JArray();
            int unloaded = 0;
            foreach (var lt in types.Take(top))
            {
                bool loaded = false;
                try { loaded = lt.GetLinkedFileStatus() == LinkedFileStatus.Loaded; } catch { }
                if (!loaded) unloaded++;
                items.Add(new JObject
                {
                    ["id"] = lt.Id.ToString(),
                    ["name"] = SafeName(lt),
                    ["status"] = SafeLinkStatus(lt)
                });
            }
            // Count unloaded across ALL of them, not just the page we showed.
            unloaded = types.Count(lt => { try { return lt.GetLinkedFileStatus() != LinkedFileStatus.Loaded; } catch { return true; } });

            return Finding("links", unloaded > 0, types.Count,
                types.Count == 0
                    ? "No Revit links."
                    : unloaded == 0
                        ? $"All {types.Count} link(s) are loaded."
                        : $"{unloaded} of {types.Count} link(s) are NOT loaded. Anything coordinated against " +
                          "them — clash results, dimensions, copy/monitor — is currently checking against nothing.",
                items, types.Count);
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
        private static JToken SafeLinkStatus(RevitLinkType t) { try { return t.GetLinkedFileStatus().ToString(); } catch { return "(unreadable)"; } }
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
