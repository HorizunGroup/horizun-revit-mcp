// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// horizun_model_scan — one native pass that replaces two IronPython audit engines
// (preentrega_audit.py, 412 lines; revit_audit.py, 510 lines).
//
// Those two engines are the most silently-broken code in the estate, and every
// defect below was measured on real models. This handler exists to make each one
// unrepresentable rather than merely fixed:
//
//   1. `views_default_named_count = len(default_named)` where default_named was
//      capped at CAP=60 DURING the loop. 300 off-standard views got reported as
//      60 — a number the reader has no way to distrust. Here the full list is
//      built first and only the RETURN is capped, so `total` is the model's count
//      and `truncated` says out loud that you are not seeing all of it.
//   2. `section()` wrote {"_error": ...} and the host indexed d["warnings"]
//      without .get(). A section that threw either exploded in the host or, worse,
//      rendered as "CAD importado: 0 → OK". Here a failed section carries NO
//      buckets at all: there is no empty list to mistake for a clean result, and
//      `complete` is false at the top level.
//   3. `except: area = 0` then `elif area == 0: unbounded += 1` — a room whose
//      Area could not be read was reported as an unbounded room. "I could not
//      look" was silently spelled "there is something wrong there". Both engines
//      do it. Here unreadable is its own bucket, always.
//   4. `ename()` returned "?" on failure and "?" then failed every naming check.
//      A read error was reported as a naming violation. Here names come back raw
//      with `name_unreadable` beside them, and NOTHING here judges a name: the
//      standard is regex, the host has stdlib, and this tool has no opinion.
//   5. s_views computed no_template and threw the list away, so the correction
//      step had no ids to act on. It is returned.
//   6. 'unused' was derived from FamilyInstance only — see TypesWithNoInstances.
//
// Read-only by construction: no transaction is opened, so a scan cannot damage
// the model it is judging.
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
    public class HorizunModelScanHandler : IRevitCommand
    {
        public string Name => "horizun_model_scan";

        public string Description =>
            "One deep native pass over the active model: cleanliness (CAD imported vs linked, IMPORT-* patterns, " +
            "unused templates/filters/group types/types, stray lines, in-place families), naming inputs (RAW view/" +
            "sheet/level/grid names — never judged here; validate them host-side with a regex), documentation " +
            "(views without template WITH ids, views not on a sheet, sheets missing a titleblock), project info " +
            "(raw values, placeholders are the caller's call), health (warnings grouped with failing element ids, " +
            "rooms/areas), links, worksets, categories, design options and the element-type universe. Every section " +
            "reports status ok|failed(reason) — a section that threw returns no buckets, so it can never read as " +
            "clean. Every bucket reports total (exact) vs returned vs truncated. Unreadable elements get their own " +
            "bucket: 'I could not look' is never spelled 'there is nothing there'.";

        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""target_document_title""],
  ""properties"": {
    ""target_document_title"": { ""type"": ""string"",
      ""description"": ""Title of the document you believe is active. The scan ABORTS if the active document differs. Required because two Revit hosts run side by side (2025 on one port, 2026 on another) and a scan of the wrong model is a clean bill of health for a file nobody looked at. '.rvt' is optional."" },
    ""top"": { ""type"": ""integer"", ""default"": 50, ""minimum"": 1,
      ""description"": ""Max items returned per bucket. Totals are always exact and independent of this; a shortened list always says truncated=true."" },
    ""sections"": { ""type"": ""array"", ""items"": { ""type"": ""string"",
        ""enum"": [""document"",""categories"",""cleanliness"",""naming"",""documentation"",""project_info"",""health"",""links"",""worksets"",""design_options"",""lines"",""types""] },
      ""description"": ""Which sections to run. Default: all of them. A section you did not ask for is reported as 'not_requested', never as empty."" },
    ""target_parameter"": { ""type"": ""string"",
      ""description"": ""Optional parameter name read off every element type in the 'types' section (e.g. 'Keynote', 'PRD_Nota_Clave'). Reported as absent / empty / value, which are three different things."" }
  }
}";

        private static readonly string[] AllSections =
        {
            "document", "categories", "cleanliness", "naming", "documentation",
            "project_info", "health", "links", "worksets", "design_options", "lines", "types"
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            _census = null;   // the model may have changed since the last request
            _censusUnreadable = 0;
            _censusUnreadableError = null;

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var wanted = request.Value<string>("target_document_title");
            if (string.IsNullOrWhiteSpace(wanted))
                return CommandResult.Fail(
                    "target_document_title is required. Two Revit hosts run side by side here, and a scan of the " +
                    "wrong document is worse than no scan: it is a clean report about a file nobody looked at. " +
                    "The active document is '" + (SafeTitle(doc) ?? "(title unreadable)") + "'.");

            var actual = SafeTitle(doc);
            if (!TitlesMatch(wanted, actual))
                return CommandResult.Fail(
                    "Refusing to scan: you asked for '" + wanted + "' but the active document is '" +
                    (actual ?? "(title unreadable)") + "'. Nothing was read. Activate the intended document, " +
                    "or check you are talking to the right Revit host.");

            int top = 50;
            if (request["top"] != null) top = Math.Max(1, request.Value<int>("top"));
            var targetParam = request.Value<string>("target_parameter");

            HashSet<string> sections;
            var secToken = request["sections"] as JArray;
            if (secToken == null || secToken.Count == 0)
            {
                sections = new HashSet<string>(AllSections, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in secToken)
                {
                    var s = t.ToString();
                    // An unknown section name silently doing nothing is how a caller
                    // thinks it checked something it never checked.
                    if (!AllSections.Contains(s, StringComparer.OrdinalIgnoreCase))
                        return CommandResult.Fail("Unknown section '" + s + "'. Known sections: " + string.Join(", ", AllSections));
                    sections.Add(s);
                }
            }

            var result = new JObject();
            var failed = new JArray();
            var skipped = new JArray();

            Section(result, failed, skipped, sections, "document", () => DocumentSection(doc, app));
            Section(result, failed, skipped, sections, "categories", () => CategoriesSection(doc, top));
            Section(result, failed, skipped, sections, "cleanliness", () => CleanlinessSection(doc, top));
            Section(result, failed, skipped, sections, "naming", () => NamingSection(doc, top));
            Section(result, failed, skipped, sections, "documentation", () => DocumentationSection(doc, top));
            Section(result, failed, skipped, sections, "project_info", () => ProjectInfoSection(doc));
            Section(result, failed, skipped, sections, "health", () => HealthSection(doc, top));
            Section(result, failed, skipped, sections, "links", () => LinksSection(doc, top));
            Section(result, failed, skipped, sections, "worksets", () => WorksetsSection(doc, top));
            Section(result, failed, skipped, sections, "design_options", () => DesignOptionsSection(doc, top));
            Section(result, failed, skipped, sections, "lines", () => LinesSection(doc, top));
            Section(result, failed, skipped, sections, "types", () => TypesSection(doc, top, targetParam));

            return CommandResult.Ok(new JObject
            {
                ["document_title"] = actual,
                ["document_verified"] = true,
                ["top"] = top,
                // `sections` is what was REQUESTED and `skipped` is what was not, so
                // subtracting one from the other mixes two disjoint sets: asking for 3
                // sections gave 3 - 9 = -6. Failures are the only thing that takes a
                // requested section away from you, so they are the only thing to subtract.
                ["sections_ok"] = sections.Count - failed.Count,
                ["sections_requested"] = sections.Count,
                ["sections_failed"] = failed,
                ["sections_not_requested"] = skipped,
                // The host must be able to refuse to render a verdict. It cannot do
                // that off a per-section flag it might forget to read, so the whole
                // scan carries one: complete=false means no bucket in here supports
                // the sentence "the model is clean".
                ["complete"] = failed.Count == 0,
                ["note"] = failed.Count == 0
                    ? null
                    : failed.Count + " section(s) could not run — see sections_failed. Those sections returned NO " +
                      "buckets on purpose: an empty list would be indistinguishable from a clean result. This scan " +
                      "is INCOMPLETE. Do not read a missing finding as a pass.",
                ["sections"] = result
            });
        }

        // ---- Section plumbing: failure is structural, not a flag in a footnote. ----
        private static void Section(JObject into, JArray failed, JArray skipped,
                                    HashSet<string> wanted, string name, Func<JObject> build)
        {
            if (!wanted.Contains(name))
            {
                // Distinct from both "ok, empty" and "failed". A section nobody asked
                // for must not be mistakable for one that came back clean.
                skipped.Add(name);
                into[name] = new JObject { ["status"] = "not_requested" };
                return;
            }

            JObject body;
            try { body = build(); }
            catch (Exception ex)
            {
                // NO buckets on a failed section. The IronPython original wrote
                // {"_error": ...} next to an empty list and the checklist rendered
                // "CAD importado: 0 → OK 🟢" over a scan that threw.
                into[name] = new JObject
                {
                    ["status"] = "failed",
                    ["reason"] = ex.Message,
                    ["consequence"] = "'" + name + "' was NOT scanned. Its findings are unknown, not clean. " +
                                      "No count from this section exists, because a zero here would be a lie."
                };
                failed.Add(name);
                return;
            }

            var wrap = new JObject { ["status"] = "ok" };
            foreach (var p in body.Properties()) wrap[p.Name] = p.Value;
            into[name] = wrap;
        }

        /// <summary>
        /// total is the model's count, ALWAYS. `items` is the complete list; only
        /// the RETURN is shortened. This is the shape that makes the CAP=60 lie
        /// (count taken from a list capped mid-loop) impossible to write.
        /// </summary>
        private static JObject Bucket(List<JToken> items, int top)
        {
            int total = items.Count;
            var shown = items.Take(top).ToList();
            return new JObject
            {
                ["total"] = total,
                ["returned"] = shown.Count,
                ["truncated"] = shown.Count < total,
                ["items"] = new JArray(shown)
            };
        }

        // ================================ document ================================
        private static JObject DocumentSection(Document doc, UIApplication app)
        {
            return new JObject
            {
                ["title"] = SafeTitle(doc),
                ["path"] = SafePath(doc),
                ["is_workshared"] = TryBool(() => doc.IsWorkshared),
                ["is_family_document"] = TryBool(() => doc.IsFamilyDocument),
                ["revit_version"] = TryStr(() => app.Application.VersionNumber),
                ["revit_build"] = TryStr(() => app.Application.VersionBuild),
                ["user"] = TryStr(() => app.Application.Username),
                ["file_size_mb"] = FileSizeMb(doc),
                ["element_count"] = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(),
                ["element_type_count"] = new FilteredElementCollector(doc).WhereElementIsElementType().GetElementCount(),
                ["scanned_utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + "Z"
            };
        }

        // ============================== categories ================================
        private static JObject CategoriesSection(Document doc, int top)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int total = 0, unreadable = 0;
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                total++;
                string nm;
                try
                {
                    var c = e.Category;
                    nm = c == null ? "(no category)" : c.Name;
                }
                catch { unreadable++; continue; }
                int n;
                counts.TryGetValue(nm, out n);
                counts[nm] = n + 1;
            }

            var rows = counts.OrderByDescending(kv => kv.Value)
                .Select(kv => (JToken)new JObject { ["category"] = kv.Key, ["instances"] = kv.Value })
                .ToList();

            return new JObject
            {
                ["total_instances"] = total,
                ["distinct_categories"] = counts.Count,
                // Not folded into any category: an element whose Category threw is
                // not an element "with no category".
                ["category_unreadable"] = unreadable,
                ["by_category"] = Bucket(rows, top)
            };
        }

        // ============================== cleanliness ===============================
        private static JObject CleanlinessSection(Document doc, int top)
        {
            var o = new JObject();

            // ---- CAD. An import is permanent weight; a link is not. Never one bucket. ----
            var hardImports = new List<JToken>();
            var cadLinks = new List<JToken>();
            var cadUnreadable = new List<JToken>();
            foreach (var imp in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                bool isLink;
                try { isLink = imp.IsLinked; }
                catch (Exception ex)
                {
                    // IsLinked is the ONLY thing separating "blocks delivery" from
                    // "fine". Guessing false here (as the python does) would file an
                    // unreadable link under hard imports and vice versa.
                    cadUnreadable.Add(new JObject
                    {
                        ["id"] = imp.Id.ToString(),
                        ["error"] = "IsLinked unreadable: " + ex.Message,
                        ["consequence"] = "Cannot tell import from link for this one. It is in neither bucket."
                    });
                    continue;
                }

                var rec = new JObject
                {
                    ["id"] = imp.Id.ToString(),
                    ["type_name"] = TypeNameOf(doc, imp),
                    ["category"] = CategoryOf(imp),
                    ["view_specific"] = TryBool(() => imp.ViewSpecific),
                    ["owner_view_id"] = TryStr(() => imp.ViewSpecific ? imp.OwnerViewId.ToString() : null),
                    ["pinned"] = TryBool(() => imp.Pinned)
                };
                if (isLink) cadLinks.Add(rec); else hardImports.Add(rec);
            }

            o["cad_imported"] = Bucket(hardImports, top);
            o["cad_linked"] = Bucket(cadLinks, top);
            o["cad_unreadable"] = Bucket(cadUnreadable, top);
            o["cad_link_types"] = new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).GetElementCount();
            o["cad_note"] = hardImports.Count == 0
                ? null
                : hardImports.Count + " CAD file(s) are IMPORTED, not linked. An import writes its layers, line " +
                  "patterns and text styles into this model's namespace permanently — deleting the instance does " +
                  "not take them back out. That is why the IMPORT-* pattern buckets below are usually non-zero too.";

            // ---- IMPORT-* patterns: the residue an import leaves behind. ----
            var impLine = new List<JToken>();
            int lineTotal = 0;
            foreach (var lp in new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)))
            {
                lineTotal++;
                var n = SafeName(lp);
                if (n != null && n.ToUpperInvariant().Contains("IMPORT"))
                    impLine.Add(new JObject { ["id"] = lp.Id.ToString(), ["name"] = n });
            }
            var impFill = new List<JToken>();
            int fillTotal = 0;
            foreach (var fp in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)))
            {
                fillTotal++;
                var n = SafeName(fp);
                if (n != null && n.ToUpperInvariant().Contains("IMPORT"))
                    impFill.Add(new JObject { ["id"] = fp.Id.ToString(), ["name"] = n });
            }
            o["line_patterns_total"] = lineTotal;
            o["line_patterns_import"] = Bucket(impLine, top);
            o["fill_patterns_total"] = fillTotal;
            o["fill_patterns_import"] = Bucket(impFill, top);

            // ---- Unused view templates. A template nobody applies is dead weight
            //      that still has to be reviewed by whoever inherits the model. ----
            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var usedTemplates = new HashSet<string>(StringComparer.Ordinal);
            var usedFilters = new HashSet<string>(StringComparer.Ordinal);
            var templates = new List<View>();
            // Every read that throws below silently demotes a LIVE template or filter to
            // "unused": its reference never reaches usedTemplates/usedFilters, and the
            // correction step deletes off those lists. So each failure is recorded here
            // and both lists degrade to an UPPER BOUND while any of them exist.
            //
            // Keyed BY VIEW ID, not one row per failure. Two reads are attempted per
            // view and the ViewTemplateId catch deliberately does not `continue` — the
            // filters are still worth collecting, since giving up on them would demote
            // MORE live filters to "unused". So one view can fail twice. A list that
            // appended per failure would make this bucket's `total` an event count
            // while the file's contract says `total` is always the model's count, and
            // would make UnusedNote say "2 view(s)" about one view. Both numbers are
            // the reader's only handle on how far off the unused lists are, so an
            // inflated one is not a safe over-warning — it is a wrong number.
            var unreadableById = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var viewsUnreadable = new List<JToken>();   // insertion order, one entry per view
            foreach (var v in allViews)
            {
                bool isTpl;
                try { isTpl = v.IsTemplate; }
                catch (Exception ex)
                {
                    NoteViewUnreadable(unreadableById, viewsUnreadable, v,
                        "IsTemplate unreadable: " + ex.Message,
                        "Neither the template it applies nor the filters it uses could be collected, " +
                        "so anything this view references may be listed as unused below.");
                    continue;
                }
                if (isTpl) { templates.Add(v); continue; }
                try
                {
                    var t = v.ViewTemplateId;
                    if (t != null && t != ElementId.InvalidElementId) usedTemplates.Add(t.ToString());
                }
                catch (Exception ex)
                {
                    NoteViewUnreadable(unreadableById, viewsUnreadable, v,
                        "ViewTemplateId unreadable: " + ex.Message,
                        "The template this view applies is unknown. If it applies one, that template " +
                        "is in view_templates_unused and is NOT unused.");
                }
                try { foreach (var fid in v.GetFilters()) usedFilters.Add(fid.ToString()); }
                catch (Exception ex)
                {
                    NoteViewUnreadable(unreadableById, viewsUnreadable, v,
                        "GetFilters unreadable: " + ex.Message,
                        "The filters this view applies are unknown. Any of them may be sitting in " +
                        "filters_unused while this view uses it.");
                }
            }
            // A template can also be applied BY another template's owner, and filters
            // are referenced from templates too — templates are Views, so the loop
            // above already walked them for GetFilters(). Deliberate.
            foreach (var t in templates)
            {
                try { foreach (var fid in t.GetFilters()) usedFilters.Add(fid.ToString()); }
                catch (Exception ex)
                {
                    NoteViewUnreadable(unreadableById, viewsUnreadable, t,
                        "GetFilters unreadable on a view template: " + ex.Message,
                        "The filters this template applies are unknown. Any of them may be sitting " +
                        "in filters_unused while this template uses it.");
                }
            }

            o["views_unreadable"] = Bucket(viewsUnreadable, top);
            o["views_unreadable_contract"] = "One entry per VIEW, never per failed read: `total` is the number of " +
                                             "views we could not fully interrogate, and a view whose template AND " +
                                             "filters both threw carries both messages in its `errors` array rather " +
                                             "than appearing twice.";

            var unusedTpl = templates
                .Where(t => !usedTemplates.Contains(t.Id.ToString()))
                .Select(t => (JToken)new JObject { ["id"] = t.Id.ToString(), ["name"] = SafeName(t) })
                .ToList();
            o["view_templates_total"] = templates.Count;
            o["view_templates_unused"] = Bucket(unusedTpl, top);
            o["view_templates_unused_note"] = UnusedNote(viewsUnreadable.Count, "view template");

            // ---- Unused view filters. ----
            var allFilters = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).ToList();
            var unusedFilters = allFilters
                .Where(f => !usedFilters.Contains(f.Id.ToString()))
                .Select(f => (JToken)new JObject { ["id"] = f.Id.ToString(), ["name"] = SafeName(f) })
                .ToList();
            o["filters_total"] = allFilters.Count;
            o["filters_unused"] = Bucket(unusedFilters, top);
            o["filters_unused_note"] = UnusedNote(viewsUnreadable.Count, "filter");

            // ---- Orphan group types: full geometry in the file, in no view. ----
            var placedGroupTypes = new HashSet<string>(StringComparer.Ordinal);
            // A placed group whose GetTypeId() throws leaves its GroupType out of this
            // set, and group_types_orphan is a list people delete from. The group IS in
            // the building; only our read of it failed.
            var groupsUnreadable = new List<JToken>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Group))
                         .WhereElementIsNotElementType().Cast<Group>())
            {
                try { placedGroupTypes.Add(g.GetTypeId().ToString()); }
                catch (Exception ex)
                {
                    groupsUnreadable.Add(new JObject
                    {
                        ["id"] = g.Id.ToString(),
                        ["error"] = "GetTypeId unreadable: " + ex.Message,
                        ["consequence"] = "This group instance is PLACED but we cannot say which group type it uses. " +
                                          "That type may be listed in group_types_orphan; deleting it would delete " +
                                          "geometry that is in the building."
                    });
                }
            }
            var groupTypes = new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>().ToList();
            var orphanGroups = groupTypes
                .Where(gt => !placedGroupTypes.Contains(gt.Id.ToString()))
                .Select(gt => (JToken)new JObject
                {
                    ["id"] = gt.Id.ToString(),
                    ["name"] = SafeName(gt),
                    ["category"] = CategoryOf(gt)
                })
                .ToList();
            o["group_types_total"] = groupTypes.Count;
            o["group_types_orphan"] = Bucket(orphanGroups, top);
            o["group_instances_type_unreadable"] = Bucket(groupsUnreadable, top);
            o["group_types_orphan_note"] = orphanGroups.Count == 0
                ? null
                : groupsUnreadable.Count > 0
                    ? orphanGroups.Count + " group type(s) are listed here because no placed instance pointed at " +
                      "them — but " + groupsUnreadable.Count + " placed group instance(s) would not report their " +
                      "type (see group_instances_type_unreadable), so any of those types could be one of these. " +
                      "This is an UPPER BOUND, not a delete list: deleting off it can delete geometry that is " +
                      "placed in the building. Resolve the unreadable instances first."
                    : orphanGroups.Count + " group type(s) have ZERO placed instances. They carry their full geometry " +
                      "in the file and appear in no view. Listing group INSTANCES never finds them, which is why a " +
                      "model can be inexplicably large with nothing visibly wrong.";

            // ---- Stray model lines. ----
            o["stray_lines_ost_lines"] = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Lines).WhereElementIsNotElementType().GetElementCount();
            o["scope_boxes"] = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().GetElementCount();
            o["materials"] = new FilteredElementCollector(doc).OfClass(typeof(Material)).GetElementCount();

            // ---- In-place families. ----
            var inPlace = new List<JToken>();
            int loadable = 0, famUnreadable = 0;
            foreach (var fam in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
            {
                bool ip;
                try { ip = fam.IsInPlace; } catch { famUnreadable++; continue; }
                if (!ip) { loadable++; continue; }
                inPlace.Add(new JObject
                {
                    ["id"] = fam.Id.ToString(),
                    ["name"] = SafeName(fam),
                    ["category"] = TryStr(() => fam.FamilyCategory?.Name)
                });
            }
            o["families_loadable"] = loadable;
            o["families_unreadable"] = famUnreadable;
            o["families_in_place"] = Bucket(inPlace, top);

            return o;
        }

        /// <summary>
        /// Record a failed read against the VIEW it happened on, folding a second
        /// failure on the same view into the existing entry. Two reads are attempted
        /// per view and neither aborts the other, so appending per failure would count
        /// one view twice — in this bucket's `total` and in UnusedNote's "N view(s)".
        /// `order` keeps insertion order so that a truncated return is stable and
        /// Dictionary enumeration order never leaks into the response.
        /// </summary>
        private static void NoteViewUnreadable(Dictionary<string, JObject> byId, List<JToken> order,
                                               Element v, string error, string consequence)
        {
            var id = v.Id.ToString();
            JObject rec;
            if (!byId.TryGetValue(id, out rec))
            {
                rec = new JObject
                {
                    ["id"] = id,
                    ["errors"] = new JArray(),
                    ["consequences"] = new JArray()
                };
                byId[id] = rec;
                order.Add(rec);
            }
            ((JArray)rec["errors"]).Add(error);
            ((JArray)rec["consequences"]).Add(consequence);
        }

        /// <summary>
        /// "Unused" here means "no view told us it uses this". One view we could not
        /// read turns that into "no view we could ask told us", which is a different
        /// sentence and the difference is somebody's deleted template. types_no_instances
        /// already says out loud that it is an upper bound; these two lists never did,
        /// so they read as verified-dead and got purged.
        /// </summary>
        private static string UnusedNote(int viewsUnreadable, string what)
        {
            if (viewsUnreadable == 0)
                return "Derived from what every view reports it applies. Every view was read, so this list is as " +
                       "good as the model's own answer — still confirm before deleting.";
            return viewsUnreadable + " view(s) would not report the templates/filters they apply (see " +
                   "views_unreadable). Anything they use is listed here as unused. This " + what + " list is an " +
                   "UPPER BOUND, NOT a delete list: it contains an unknown number of live " + what + "s.";
        }

        // ================================ naming ==================================
        // RAW NAMES ONLY. This section decides nothing.
        //
        // The python it replaces hand-rolled ^[A-Z]{1,4}-\d{2,3}[A-Za-z]?$ out of
        // split()/isalpha()/isdigit() ONLY because IronPython inside Revit has no
        // stdlib and therefore no `re`. That constraint is gone: the host has
        // stdlib. Re-implementing the standard here would fork it into a second
        // place that drifts, and — worse — would make a name we could not READ
        // (ename() returning "?") come back as a name that FAILED, which is a
        // different fact entirely. So: names out, verdicts nowhere.
        private static JObject NamingSection(Document doc, int top)
        {
            var views = new List<JToken>();
            int viewUnreadable = 0;
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                bool isTpl;
                string vt;
                try { isTpl = v.IsTemplate; vt = v.ViewType.ToString(); }
                catch { viewUnreadable++; continue; }
                bool bad;
                var nm = SafeName(v, out bad);
                views.Add(new JObject
                {
                    ["id"] = v.Id.ToString(),
                    ["name"] = nm,
                    ["name_unreadable"] = bad,
                    ["view_type"] = vt,
                    ["is_template"] = isTpl
                });
            }

            var sheets = new List<JToken>();
            foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                bool bad;
                var nm = SafeName(s, out bad);
                bool numBad = false;
                string num = null;
                try { num = s.SheetNumber; } catch { numBad = true; }
                sheets.Add(new JObject
                {
                    ["id"] = s.Id.ToString(),
                    ["sheet_number"] = num,
                    ["sheet_number_unreadable"] = numBad,
                    ["name"] = nm,
                    ["name_unreadable"] = bad
                });
            }

            var levels = new List<JToken>();
            foreach (var l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                bool bad;
                var nm = SafeName(l, out bad);
                levels.Add(new JObject
                {
                    ["id"] = l.Id.ToString(),
                    ["name"] = nm,
                    ["name_unreadable"] = bad,
                    ["elevation_m"] = TryDouble(() => HorizunGuard.ToM(l.Elevation))
                });
            }

            var grids = new List<JToken>();
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                bool bad;
                var nm = SafeName(g, out bad);
                grids.Add(new JObject { ["id"] = g.Id.ToString(), ["name"] = nm, ["name_unreadable"] = bad });
            }

            return new JObject
            {
                ["contract"] = "Raw names, never judged. Validate them host-side against ONE standard with a real " +
                               "regex. A name with name_unreadable=true was NOT read — it is not a name that failed.",
                ["views_name_unreadable_skipped"] = viewUnreadable,
                ["views"] = Bucket(views, top),
                ["sheets"] = Bucket(sheets, top),
                ["levels"] = Bucket(levels, top),
                ["grids"] = Bucket(grids, top)
            };
        }

        // ============================= documentation ==============================
        private static JObject DocumentationSection(Document doc, int top)
        {
            // GetAllPlacedViews() and not the Viewport collector: schedules and
            // revision schedules are placed on sheets WITHOUT a Viewport, so a
            // Viewport-only scan reports every schedule on every sheet as "not
            // placed" and the list is noise the reader learns to ignore.
            var unreadable = new List<JToken>();
            var placed = new HashSet<string>(StringComparer.Ordinal);
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            int sheetsPlacementUnreadable = 0;
            foreach (var s in sheets)
            {
                try { foreach (var vid in s.GetAllPlacedViews()) placed.Add(vid.ToString()); }
                catch (Exception ex)
                {
                    // `placed` is the ONLY evidence a view is on a sheet. A sheet we
                    // could not open up donates none of its views to it, so every view
                    // it carries lands in views_not_on_sheet as if it were off-sheet.
                    sheetsPlacementUnreadable++;
                    unreadable.Add(new JObject
                    {
                        ["id"] = s.Id.ToString(),
                        ["sheet_number"] = TryStr(() => s.SheetNumber),
                        ["error"] = "GetAllPlacedViews unreadable: " + ex.Message,
                        ["consequence"] = "The views placed on this sheet are unknown. Each of them is now listed in " +
                                          "views_not_on_sheet even though it IS placed."
                    });
                }
            }

            var noTemplate = new List<JToken>();
            var notOnSheet = new List<JToken>();
            // Both counters gate the notes below. Without them views_no_template is a
            // silent LOWER bound: a view we could not classify at all, and a view whose
            // ViewTemplateId threw, are both absent from it — and absent reads exactly
            // like "this view has a template". The cleanliness section already refuses
            // to let an unreadable view pass unremarked (UnusedNote); this section runs
            // the same two reads and had a caveat only for views_not_on_sheet.
            int viewsClassifyUnreadable = 0;   // in NEITHER list: we never got past IsTemplate/ViewType
            int viewTemplateUnreadable = 0;    // in the model, template state unknown

            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                bool isTpl;
                ViewType vt;
                try { isTpl = v.IsTemplate; vt = v.ViewType; }
                catch (Exception ex)
                {
                    viewsClassifyUnreadable++;
                    unreadable.Add(new JObject
                    {
                        ["id"] = v.Id.ToString(),
                        ["error"] = ex.Message,
                        ["consequence"] = "This view could not be classified, so it is in NEITHER views_no_template " +
                                          "NOR views_not_on_sheet. Its absence from both is not a pass."
                    });
                    continue;
                }
                if (isTpl) continue;
                if (vt == ViewType.ProjectBrowser || vt == ViewType.SystemBrowser ||
                    vt == ViewType.Internal || vt == ViewType.Undefined || vt == ViewType.DrawingSheet)
                    continue;

                var rec = new JObject
                {
                    ["id"] = v.Id.ToString(),
                    ["name"] = SafeName(v),
                    ["view_type"] = vt.ToString()
                };

                try
                {
                    var t = v.ViewTemplateId;
                    // Returned, not discarded. s_views computed exactly this list and
                    // dropped it on the floor, leaving the correction step with no ids.
                    if (t == null || t == ElementId.InvalidElementId) noTemplate.Add(rec.DeepClone());
                }
                catch (Exception ex)
                {
                    // Not counted as 'no template': we do not know whether it has one.
                    // Counted HERE though, because "we could not look" that nothing
                    // reports is indistinguishable from "we looked and it was fine".
                    viewTemplateUnreadable++;
                    unreadable.Add(new JObject
                    {
                        ["id"] = v.Id.ToString(),
                        ["error"] = "ViewTemplateId unreadable: " + ex.Message,
                        ["consequence"] = "Not counted as 'no template'. We could not look."
                    });
                }

                if (!placed.Contains(v.Id.ToString())) notOnSheet.Add(rec.DeepClone());
            }

            var missingTb = new List<JToken>();
            // The failure had no counter at all: it landed only in `unreadable`, which is
            // emitted through Bucket() and drops everything past `top` (default 50). The
            // GetAllPlacedViews failures share that list, so titleblock failures could be
            // truncated out of the response entirely — a sheet nobody scanned, absent from
            // sheets_missing_titleblock, in a section still reporting status "ok". A count
            // survives truncation; an error record does not.
            int sheetsTitleblockUnreadable = 0;
            foreach (var s in sheets)
            {
                try
                {
                    int n = new FilteredElementCollector(doc, s.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsNotElementType()
                        .GetElementCount();
                    if (n == 0)
                        missingTb.Add(new JObject
                        {
                            ["id"] = s.Id.ToString(),
                            ["sheet_number"] = TryStr(() => s.SheetNumber),
                            ["name"] = SafeName(s)
                        });
                }
                catch (Exception ex)
                {
                    sheetsTitleblockUnreadable++;
                    unreadable.Add(new JObject
                    {
                        ["id"] = s.Id.ToString(),
                        ["error"] = "Titleblock scan failed: " + ex.Message,
                        ["consequence"] = "This sheet is NOT in sheets_missing_titleblock. We never counted its " +
                                          "titleblocks, so its absence from that list is not a pass."
                    });
                }
            }

            return new JObject
            {
                ["sheets_total"] = sheets.Count,
                ["sheets_placement_unreadable"] = sheetsPlacementUnreadable,
                ["views_classify_unreadable"] = viewsClassifyUnreadable,
                ["view_template_unreadable"] = viewTemplateUnreadable,
                ["sheets_titleblock_unreadable"] = sheetsTitleblockUnreadable,
                ["views_no_template"] = Bucket(noTemplate, top),
                ["views_no_template_note"] = NoTemplateNote(viewTemplateUnreadable, viewsClassifyUnreadable),
                ["views_not_on_sheet"] = Bucket(notOnSheet, top),
                ["views_not_on_sheet_note"] = NotOnSheetNote(sheetsPlacementUnreadable, viewsClassifyUnreadable),
                ["sheets_missing_titleblock"] = Bucket(missingTb, top),
                ["sheets_missing_titleblock_note"] = MissingTitleblockNote(sheetsTitleblockUnreadable),
                // Error records, one per failed READ, over a mix of views and sheets —
                // a sheet that failed both GetAllPlacedViews and the titleblock scan is
                // in here twice. Nothing derives a count of things from it; the counters
                // above are what the notes are gated on, and each is per element.
                ["unreadable_contract"] = "A list of failed reads, not of elements: one element can appear more " +
                                          "than once and views and sheets are mixed. It is capped by `top`, so it " +
                                          "is not a census of the failures — use views_classify_unreadable, " +
                                          "view_template_unreadable, sheets_placement_unreadable and " +
                                          "sheets_titleblock_unreadable for counts.",
                ["unreadable"] = Bucket(unreadable, top),
                ["note"] = "views_not_on_sheet is a review list, not a defect list — working views legitimately " +
                           "live off-sheet. Placement is read from ViewSheet.GetAllPlacedViews(), so schedules " +
                           "count as placed."
            };
        }

        /// <summary>
        /// sheets_missing_titleblock had neither a note nor a counter while both its
        /// neighbours had one, and it fails the same way they do: a sheet whose
        /// titleblock scan threw is absent from the list, and absent reads as "this
        /// sheet has a titleblock". The correction step works off this list, so an
        /// unscanned sheet would otherwise be left untouched AND unmentioned.
        /// </summary>
        private static string MissingTitleblockNote(int titleblockUnreadable)
        {
            if (titleblockUnreadable == 0)
                return "Every sheet was scanned for titleblocks, so this list is the model's own answer.";
            return titleblockUnreadable + " sheet(s) could not be scanned for titleblocks (see unreadable, which " +
                   "is itself capped by `top` — this count is not). None of them are in this list, so it is a " +
                   "LOWER BOUND: there may be further sheets with no titleblock. Do not read this count as the total.";
        }

        /// <summary>
        /// views_no_template is built by ASKING each view. Every view we could not ask
        /// is missing from it, and a view missing from a defect list reads as a view
        /// that passed. So the list is a LOWER bound whenever either read failed, and
        /// this says so — the correction step assigns templates off it and would
        /// otherwise leave the unreadable ones untouched and unmentioned.
        /// </summary>
        private static string NoTemplateNote(int templateUnreadable, int classifyUnreadable)
        {
            int blind = templateUnreadable + classifyUnreadable;
            if (blind == 0)
                return "Every view reported whether it applies a template, so this list is the model's own answer.";
            return blind + " view(s) never reported their template state (" + templateUnreadable +
                   " whose ViewTemplateId threw, " + classifyUnreadable + " that could not be classified at all — " +
                   "see unreadable). None of them are in this list, so it is a LOWER BOUND: there are an unknown " +
                   "number of further views with no template. Do not read this count as the total.";
        }

        /// <summary>
        /// views_not_on_sheet is wrong in BOTH directions at once, from two different
        /// failures, so one sentence about sheets was never the whole story: an
        /// unreadable sheet INFLATES the list (its placed views look off-sheet), and an
        /// unclassifiable view DEFLATES it (it is in no list at all). Reporting only the
        /// first would let a reader treat the list as a superset it is not.
        /// </summary>
        private static string NotOnSheetNote(int sheetsPlacementUnreadable, int classifyUnreadable)
        {
            if (sheetsPlacementUnreadable == 0 && classifyUnreadable == 0) return null;
            var parts = new List<string>();
            if (sheetsPlacementUnreadable > 0)
                parts.Add(sheetsPlacementUnreadable + " sheet(s) would not report their placed views, so this list " +
                          "is INFLATED by everything placed on them: a view in it is not known to be off-sheet");
            if (classifyUnreadable > 0)
                parts.Add(classifyUnreadable + " view(s) could not be classified, so they are absent from this list " +
                          "whether or not they are on a sheet: it is also INCOMPLETE");
            return string.Join("; ", parts.ToArray()) + " (see unreadable). This count is not the model's number.";
        }

        // ============================= project_info ===============================
        // RAW VALUES. Whether "Enter address here" is a placeholder is the caller's
        // comparison to make against the caller's list; baking that list in here
        // would put the client's standard in a compiled DLL. Three states are kept
        // apart on purpose: absent (no such parameter), empty (present, ""), and
        // unreadable (the read threw) — the python collapsed all three into "".
        private static JObject ProjectInfoSection(Document doc)
        {
            var pi = doc.ProjectInformation;
            if (pi == null) throw new InvalidOperationException("Document has no ProjectInformation element.");

            var fields = new JObject();
            AddInfo(fields, pi, "name", BuiltInParameter.PROJECT_NAME);
            AddInfo(fields, pi, "number", BuiltInParameter.PROJECT_NUMBER);
            AddInfo(fields, pi, "status", BuiltInParameter.PROJECT_STATUS);
            AddInfo(fields, pi, "address", BuiltInParameter.PROJECT_ADDRESS);
            AddInfo(fields, pi, "client_name", BuiltInParameter.CLIENT_NAME);
            AddInfo(fields, pi, "building_name", BuiltInParameter.PROJECT_BUILDING_NAME);
            AddInfo(fields, pi, "author", BuiltInParameter.PROJECT_AUTHOR);
            AddInfo(fields, pi, "issue_date", BuiltInParameter.PROJECT_ISSUE_DATE);
            AddInfo(fields, pi, "organization_name", BuiltInParameter.PROJECT_ORGANIZATION_NAME);
            AddInfo(fields, pi, "organization_description", BuiltInParameter.PROJECT_ORGANIZATION_DESCRIPTION);

            return new JObject
            {
                ["element_id"] = pi.Id.ToString(),
                ["contract"] = "Raw values. 'present' false means the parameter does not exist; 'readable' false " +
                               "means the read threw and the value is unknown — neither is an empty string. " +
                               "Compare against your own placeholder list host-side.",
                ["fields"] = fields
            };
        }

        private static void AddInfo(JObject into, Element pi, string key, BuiltInParameter bip)
        {
            var o = new JObject();
            Parameter p = null;
            try { p = pi.get_Parameter(bip); }
            catch (Exception ex)
            {
                o["present"] = null;
                o["readable"] = false;
                o["value"] = null;
                o["error"] = ex.Message;
                into[key] = o;
                return;
            }

            if (p == null)
            {
                o["present"] = false;
                o["readable"] = true;
                o["value"] = null;
                into[key] = o;
                return;
            }

            o["present"] = true;
            try
            {
                var v = p.AsString();
                if (v == null) v = p.AsValueString();
                o["readable"] = true;
                o["value"] = v;
                o["is_empty"] = string.IsNullOrEmpty(v);
            }
            catch (Exception ex)
            {
                o["readable"] = false;
                o["value"] = null;
                o["error"] = ex.Message;
            }
            into[key] = o;
        }

        // ================================ health ==================================
        private static JObject HealthSection(Document doc, int top)
        {
            var o = new JObject();

            // ---- Warnings grouped by description, with the failing ids. ----
            var all = doc.GetWarnings();
            var groups = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var w in all)
            {
                string desc;
                try { desc = w.GetDescriptionText(); }
                catch { desc = "(description unreadable)"; }
                if (string.IsNullOrEmpty(desc)) desc = "(description empty)";

                JObject g;
                if (!groups.TryGetValue(desc, out g))
                {
                    g = new JObject
                    {
                        ["description"] = desc,
                        ["severity"] = TryStr(() => w.GetSeverity().ToString()),
                        ["occurrences"] = 0,
                        ["failing_element_ids"] = new JArray(),
                        ["ids_unreadable"] = false
                    };
                    groups[desc] = g;
                    order.Add(desc);
                }
                g["occurrences"] = (int)g["occurrences"] + 1;
                try
                {
                    var arr = (JArray)g["failing_element_ids"];
                    foreach (var id in w.GetFailingElements()) arr.Add(id.ToString());
                }
                catch (Exception ex)
                {
                    // Unlike every other bucket in this file, this one's `total` is what
                    // the loop managed to collect — not the model's count. Swallowing
                    // here emits total:0 next to a non-zero `occurrences`: a warning with
                    // failing elements we never read, dressed as one with none.
                    g["ids_unreadable"] = true;
                    if (g["ids_error"] == null) g["ids_error"] = "GetFailingElements failed: " + ex.Message;
                }
            }

            var rows = order.Select(d => groups[d])
                .OrderByDescending(g => (int)g["occurrences"])
                .Select(g =>
                {
                    // The id list gets its own total/returned/truncated: a warning
                    // type with 4,000 failing elements must not hand back 50 ids and
                    // look like it hands back all of them.
                    var arr = (JArray)g["failing_element_ids"];
                    var ids = arr.ToList();
                    if ((bool)g["ids_unreadable"])
                    {
                        // No total at all rather than a total we know is short. A number
                        // here would be read as the model's count, which is the one
                        // thing it is not.
                        var shown = ids.Take(top).ToList();
                        g["failing_elements"] = new JObject
                        {
                            ["total"] = JValue.CreateNull(),
                            ["total_unknown_reason"] = "At least one occurrence of this warning would not enumerate " +
                                                       "its failing elements (see ids_error). How many elements this " +
                                                       "warning touches is UNKNOWN; the ids below are only the ones " +
                                                       "that could be read and are a lower bound, never a complete set.",
                            ["returned"] = shown.Count,
                            ["truncated"] = JValue.CreateNull(),
                            ["items"] = new JArray(shown)
                        };
                    }
                    else
                    {
                        g["failing_elements"] = Bucket(ids, top);
                    }
                    g.Remove("failing_element_ids");
                    return (JToken)g;
                })
                .ToList();

            o["warnings_total"] = all.Count;
            o["warnings_distinct"] = groups.Count;
            o["warnings_by_type"] = Bucket(rows, top);

            // ---- Rooms. Unreadable is NOT unbounded. ----
            o["rooms"] = SpatialBucket(doc, BuiltInCategory.OST_Rooms, top);
            o["areas"] = SpatialBucket(doc, BuiltInCategory.OST_Areas, top);
            return o;
        }

        /// <summary>
        /// Rooms/areas split four ways. Both python engines do `except: area = 0`
        /// and then classify area==0 as unbounded — so a room whose Area could not
        /// be read is reported to the client as a defective room. Unreadable gets
        /// its own bucket here and is never merged into a finding.
        /// </summary>
        private static JObject SpatialBucket(Document doc, BuiltInCategory bic, int top)
        {
            var unplaced = new List<JToken>();
            var unbounded = new List<JToken>();
            var unreadable = new List<JToken>();
            int total = 0;

            foreach (var e in new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType())
            {
                total++;
                var se = e as SpatialElement;
                if (se == null)
                {
                    unreadable.Add(new JObject
                    {
                        ["id"] = e.Id.ToString(),
                        ["error"] = "Not a SpatialElement; cannot read placement or area."
                    });
                    continue;
                }

                bool placed;
                try { placed = se.Location != null; }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject { ["id"] = e.Id.ToString(), ["error"] = "Location unreadable: " + ex.Message });
                    continue;
                }

                if (!placed)
                {
                    unplaced.Add(new JObject
                    {
                        ["id"] = e.Id.ToString(),
                        ["name"] = SafeName(e),
                        ["problem"] = "Unplaced: it has no location. It still appears in schedules, with no area."
                    });
                    continue;
                }

                double area;
                try { area = se.Area; }
                catch (Exception ex)
                {
                    unreadable.Add(new JObject { ["id"] = e.Id.ToString(), ["error"] = "Area unreadable: " + ex.Message });
                    continue;
                }

                if (area <= 0.0)
                    unbounded.Add(new JObject
                    {
                        ["id"] = e.Id.ToString(),
                        ["name"] = SafeName(e),
                        ["problem"] = "Not enclosed: area is 0. Its boundary is open, so it measures nothing."
                    });
            }

            return new JObject
            {
                ["total"] = total,
                ["unplaced"] = Bucket(unplaced, top),
                ["unbounded"] = Bucket(unbounded, top),
                ["unreadable"] = Bucket(unreadable, top),
                ["note"] = "unreadable is separate from unbounded on purpose: a read that threw is not a defect " +
                           "we found, it is a defect we cannot rule out. Unplaced and unbounded both understate " +
                           "any area takeoff taken from this model."
            };
        }

        // ================================= links ==================================
        private static JObject LinksSection(Document doc, int top)
        {
            var rows = new List<JToken>();
            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var rec = new JObject
                {
                    ["instance_id"] = li.Id.ToString(),
                    ["name"] = SafeName(li),
                    ["pinned"] = TryBool(() => li.Pinned)
                };

                string status = null, pathType = null, path = null;
                string statusError = null;
                try
                {
                    var lt = doc.GetElement(li.GetTypeId()) as RevitLinkType;
                    if (lt == null)
                    {
                        statusError = "Link type could not be resolved.";
                    }
                    else
                    {
                        rec["type_id"] = lt.Id.ToString();
                        var efr = lt.GetExternalFileReference();
                        if (efr == null)
                        {
                            statusError = "Link has no external file reference.";
                        }
                        else
                        {
                            status = efr.GetLinkedFileStatus().ToString();
                            pathType = efr.PathType.ToString();
                            try { path = ModelPathUtils.ConvertModelPathToUserVisiblePath(efr.GetAbsolutePath()); }
                            catch { path = null; }
                        }
                    }
                }
                catch (Exception ex) { statusError = ex.Message; }

                rec["load_status"] = status;
                rec["path_type"] = pathType;
                rec["path"] = path;
                // null status + explicit error, never a defaulted "not loaded". A link
                // we could not interrogate is not a link we know is broken.
                rec["status_unreadable"] = statusError != null;
                rec["status_error"] = statusError;
                rows.Add(rec);
            }

            int unpinned = rows.Count(r => r["pinned"] != null && r["pinned"].Type == JTokenType.Boolean && !(bool)r["pinned"]);
            int notLoaded = rows.Count(r => r["load_status"] != null && (string)r["load_status"] != "Loaded");
            int unknown = rows.Count(r => (bool)r["status_unreadable"]);

            var cadUnpinned = new List<JToken>();
            // This runs the SAME imp.IsLinked read that CleanlinessSection hardens into
            // cad_unreadable, and it used to swallow it with a bare catch{}. That made
            // total:0 mean either "every CAD link is pinned" or "the read threw on every
            // single one of them", with nothing in the response able to tell them apart:
            // an empty defect list reads as a pass. The failures get their own bucket and
            // gate the note below, exactly as the sibling site does.
            var cadPinUnreadable = new List<JToken>();
            foreach (var i in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                try
                {
                    if (i.IsLinked && !i.Pinned)
                        cadUnpinned.Add(new JObject { ["id"] = i.Id.ToString(), ["type_name"] = TypeNameOf(doc, i) });
                }
                catch (Exception ex)
                {
                    cadPinUnreadable.Add(new JObject
                    {
                        ["id"] = i.Id.ToString(),
                        ["type_name"] = TryStr(() => TypeNameOf(doc, i)),
                        ["error"] = "IsLinked/Pinned unreadable: " + ex.Message,
                        ["consequence"] = "Neither read succeeded, so this one is NOT in cad_links_unpinned. " +
                                          "Its absence from that list is not a pass."
                    });
                }
            }

            return new JObject
            {
                ["rvt_link_instances"] = rows.Count,
                ["rvt_link_types"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).GetElementCount(),
                ["rvt_links_unpinned"] = unpinned,
                ["rvt_links_not_loaded"] = notLoaded,
                ["rvt_links_status_unknown"] = unknown,
                ["rvt_links"] = Bucket(rows, top),
                ["cad_links_unpinned"] = Bucket(cadUnpinned, top),
                ["cad_pin_unreadable"] = Bucket(cadPinUnreadable, top),
                ["cad_links_unpinned_note"] = CadUnpinnedNote(cadPinUnreadable.Count),
                ["note"] = notLoaded == 0 && unknown == 0
                    ? null
                    : "A link that is not loaded means anything coordinated against it — clashes, dimensions, " +
                      "copy/monitor — is currently checking against nothing. rvt_links_status_unknown counts links " +
                      "whose status could not be read at all; they are in neither the loaded nor the unloaded count."
            };
        }

        /// <summary>
        /// cad_links_unpinned is built by ASKING each ImportInstance whether it is a
        /// link and whether it is pinned. Every one that refused to answer is missing
        /// from the list, and a missing element reads as a pinned one. So the list is a
        /// LOWER BOUND whenever the read failed, and this says so rather than letting
        /// total:0 be read as "all CAD links are pinned".
        /// </summary>
        private static string CadUnpinnedNote(int pinUnreadable)
        {
            if (pinUnreadable == 0)
                return "Every CAD instance reported its link and pin state, so this list is the model's own answer.";
            return pinUnreadable + " CAD instance(s) never reported their link/pin state (see cad_pin_unreadable). " +
                   "None of them are in this list, so it is a LOWER BOUND: there may be further unpinned CAD links " +
                   "we could not see. Do not read this count as the total.";
        }

        // =============================== worksets =================================
        private static JObject WorksetsSection(Document doc, int top)
        {
            bool ws;
            try { ws = doc.IsWorkshared; }
            catch (Exception ex) { throw new InvalidOperationException("IsWorkshared unreadable: " + ex.Message); }

            if (!ws)
                return new JObject
                {
                    ["is_workshared"] = false,
                    ["note"] = "Model is not workshared, so there are no user worksets. This is a fact about the " +
                               "model, not a failed check."
                };

            var names = new Dictionary<int, string>();
            var counts = new Dictionary<int, int>();
            foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                names[w.Id.IntegerValue] = w.Name;
                counts[w.Id.IntegerValue] = 0;
            }

            // Two increments that used to share one counter, meaning two different
            // things: "the model told us this element sits outside the user worksets"
            // and "the WorksetId read threw, so we know nothing about it". Merging them
            // reports "I could not look" as "this element is outside the user worksets"
            // — defect #3 from this file's header, reproduced. They stay apart.
            int outsideUserWorksets = 0;   // read SUCCEEDED, the id is not a user workset
            int worksetUnreadable = 0;     // read THREW: assignment unknown, in no count below
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                int wid;
                try { wid = e.WorksetId.IntegerValue; }
                catch { worksetUnreadable++; continue; }
                int n;
                if (counts.TryGetValue(wid, out n)) counts[wid] = n + 1;
                else outsideUserWorksets++;   // family/view worksets etc. — not a user workset
            }

            var rows = names.Keys
                .Select(k => (JToken)new JObject
                {
                    ["workset_id"] = k,
                    ["name"] = names[k],
                    ["elements"] = counts[k]
                })
                .OrderByDescending(r => (int)r["elements"])
                .ToList();

            return new JObject
            {
                ["is_workshared"] = true,
                ["user_worksets"] = names.Count,
                ["elements_outside_user_worksets"] = outsideUserWorksets,
                ["workset_unreadable"] = worksetUnreadable,
                ["worksets"] = Bucket(rows, top),
                ["worksets_note"] = WorksetsNote(worksetUnreadable)
            };
        }

        /// <summary>
        /// Every per-workset `elements` count is built by asking each element which
        /// workset it is on. An element whose WorksetId threw is on no workset in this
        /// report — so the per-workset counts are LOWER bounds, and a reader dividing
        /// the model across worksets is dividing fewer elements than the model holds.
        /// Nothing else in the section would tell them that.
        /// </summary>
        private static string WorksetsNote(int worksetUnreadable)
        {
            if (worksetUnreadable == 0)
                return "Every element reported its workset, so these counts account for the whole model.";
            return worksetUnreadable + " element(s) never reported a workset (the WorksetId read threw). They are " +
                   "in NO count here — not in any workset's `elements`, and not in elements_outside_user_worksets, " +
                   "which counts only elements the model placed outside the user worksets. Every per-workset count " +
                   "is therefore a LOWER BOUND and they do not sum to the model's element total.";
        }

        // ============================ design_options ==============================
        private static JObject DesignOptionsSection(Document doc, int top)
        {
            var rows = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>()
                .Select(o => (JToken)new JObject
                {
                    ["id"] = o.Id.ToString(),
                    ["name"] = SafeName(o),
                    ["is_primary"] = TryBool(() => o.IsPrimary)
                })
                .ToList();

            return new JObject
            {
                ["design_options"] = Bucket(rows, top),
                ["note"] = rows.Count == 0
                    ? null
                    : "Elements in a non-primary design option are in the file and in nobody's takeoff. Confirm " +
                      "this is intended before delivering."
            };
        }

        // ================================= lines ==================================
        private static JObject LinesSection(Document doc, int top)
        {
            int model = 0, detail = 0, unreadable = 0;
            foreach (var ce in new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Cast<CurveElement>())
            {
                try { if (ce.ViewSpecific) detail++; else model++; }
                catch { unreadable++; }
            }

            return new JObject
            {
                ["model_lines"] = model,
                ["detail_lines"] = detail,
                // The python called this bucket "other", which reads like a third
                // kind of line. It is not: it is lines we failed to classify.
                ["view_specific_unreadable"] = unreadable,
                ["ost_lines_category"] = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Lines).WhereElementIsNotElementType().GetElementCount(),
                ["text_notes"] = new FilteredElementCollector(doc).OfClass(typeof(TextNote)).GetElementCount(),
                ["filled_regions"] = new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).GetElementCount()
            };
        }

        // ================================= types ==================================
        // Instance method, unlike its neighbours: the type census is per-request
        // state, cleared on entry to Execute, because the dispatcher keeps one
        // handler alive for the whole session and a cache that outlived a request
        // would answer the next scan from a model that has since changed.
        private JObject TypesSection(Document doc, int top, string targetParam)
        {
            var census = CensusOf(doc);

            var rows = new List<JToken>();
            var noInstances = new List<JToken>();
            int familySymbols = 0, familySymbolsNoInstances = 0;

            foreach (var t in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                int n;
                if (!census.TryGetValue(t.Id.ToString(), out n)) n = 0;

                var sym = t as FamilySymbol;
                if (sym != null)
                {
                    familySymbols++;
                    if (n == 0) familySymbolsNoInstances++;
                }

                var rec = new JObject
                {
                    ["id"] = t.Id.ToString(),
                    ["category"] = CategoryOf(t),
                    ["family"] = FamilyNameOf(t),
                    ["name"] = SafeName(t),
                    ["instances"] = n,
                    ["is_family_symbol"] = sym != null
                };
                if (!string.IsNullOrEmpty(targetParam)) rec["target_parameter"] = ReadNamed(t, targetParam);

                rows.Add(rec);
                if (n == 0) noInstances.Add(rec.DeepClone());
            }

            // Every count on this section that says "zero instances" is only as complete
            // as the census behind it, so the census's own failures travel with them.
            var censusCaveat = _censusUnreadable == 0
                ? ""
                : " ALSO: " + _censusUnreadable + " element(s) would not report GetTypeId() during the census " +
                  "(first error: " + (_censusUnreadableError ?? "unknown") + "), so their types got no vote. " +
                  "`instances` is a LOWER bound and this list — plus family_symbols_no_instances — is inflated by " +
                  "however many types those elements use. A type in here may be in the building.";

            return new JObject
            {
                ["target_parameter"] = targetParam,
                ["types_total"] = rows.Count,
                ["family_symbols_total"] = familySymbols,
                ["family_symbols_no_instances"] = familySymbolsNoInstances,
                // Not folded into any type's count: an element we could not read is not
                // an element that uses nothing.
                ["census_unreadable_elements"] = _censusUnreadable,
                ["census_unreadable_error"] = _censusUnreadableError,
                ["types_no_instances"] = Bucket(noInstances, top),
                ["types"] = Bucket(rows, top),
                // BOTH python engines derive "unused" from FilteredElementCollector
                // (doc).OfClass(FamilyInstance) alone. A wall/floor/roof/duct is not
                // a FamilyInstance, so every system-family type in the model came back
                // with zero users and got labelled purgable — the field in
                // preentrega_audit.py is literally named family_symbols_unused_aprox,
                // conceding the point. Deleting off that list deletes types that are
                // in the building. Here the census walks GetTypeId() over EVERY
                // non-elementtype element, so system families are counted like
                // anything else.
                ["instances_note"] = "instances comes from one census of GetTypeId() over ALL non-elementtype " +
                                     "elements, so system-family types (walls, floors, ducts) are counted — deriving " +
                                     "this from FamilyInstance alone reports every wall type as unused.",
                ["no_instances_note"] = "types_no_instances is NOT a purge list. A type with no instances can still " +
                                        "be referenced by another type (a compound wall's layers, a nested family, a " +
                                        "stacked wall), and those references are invisible to GetTypeId(). It is an " +
                                        "upper bound on what purge might remove. Purge with the real API and " +
                                        "re-verify inside the transaction." + censusCaveat
            };
        }

        private static JObject ReadNamed(Element e, string paramName)
        {
            var o = new JObject();
            Parameter p;
            try { p = e.LookupParameter(paramName); }
            catch (Exception ex)
            {
                o["present"] = null; o["readable"] = false; o["value"] = null; o["error"] = ex.Message;
                return o;
            }
            if (p == null)
            {
                // Absent is not empty. A caller counting "coded types" must not count
                // a type that has no such parameter as a type with a blank code.
                o["present"] = false; o["readable"] = true; o["value"] = null;
                return o;
            }
            o["present"] = true;
            try
            {
                var v = p.AsString();
                if (v == null) v = p.AsValueString();
                o["readable"] = true;
                o["value"] = v;
                o["is_empty"] = string.IsNullOrEmpty(v);
            }
            catch (Exception ex)
            {
                o["readable"] = false; o["value"] = null; o["error"] = ex.Message;
            }
            return o;
        }

        // ---- One census per request; the model may change between requests. ----
        private Dictionary<string, int> _census;
        private int _censusUnreadable;
        private string _censusUnreadableError;

        private Dictionary<string, int> CensusOf(Document doc)
        {
            if (_census != null) return _census;
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            int unreadable = 0;
            string firstError = null;
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                ElementId tid;
                try { tid = e.GetTypeId(); }
                catch (Exception ex)
                {
                    // This census is the sole evidence that a type is in use. An element
                    // dropped here takes its type's only vote with it: `instances` goes
                    // down and the type surfaces in types_no_instances as unused. "I
                    // could not read this element" must not become "this type has no users".
                    unreadable++;
                    if (firstError == null) firstError = ex.Message;
                    continue;
                }
                if (tid == null || tid == ElementId.InvalidElementId) continue;
                var key = tid.ToString();
                int n;
                map.TryGetValue(key, out n);
                map[key] = n + 1;
            }
            _censusUnreadable = unreadable;
            _censusUnreadableError = firstError;
            _census = map;
            return map;
        }

        // ---- Small, boring, and each one honest about failing. ----
        private static bool TitlesMatch(string wanted, string actual)
        {
            if (actual == null) return false;
            return string.Equals(Strip(wanted), Strip(actual), StringComparison.OrdinalIgnoreCase);
        }

        private static string Strip(string s)
        {
            s = (s ?? "").Trim();
            if (s.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s;
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafePath(Document d) { try { return d.PathName; } catch { return null; } }
        private static string SafeName(Element e) { try { return e?.Name; } catch { return null; } }

        private static string SafeName(Element e, out bool unreadable)
        {
            unreadable = false;
            try { return e.Name; }
            catch { unreadable = true; return null; }
        }

        private static string CategoryOf(Element e)
        {
            try { return e.Category?.Name; } catch { return null; }
        }

        private static string FamilyNameOf(Element e)
        {
            var et = e as ElementType;
            if (et == null) return null;
            try { return et.FamilyName; } catch { return null; }
        }

        private static string TypeNameOf(Document doc, Element e)
        {
            try
            {
                var tid = e.GetTypeId();
                if (tid == null || tid == ElementId.InvalidElementId) return null;
                return SafeName(doc.GetElement(tid));
            }
            catch { return null; }
        }

        private static JToken TryBool(Func<bool> f) { try { return f(); } catch { return null; } }
        private static JToken TryStr(Func<string> f) { try { return f(); } catch { return null; } }
        private static JToken TryDouble(Func<double> f) { try { return Math.Round(f(), 4); } catch { return null; } }

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
