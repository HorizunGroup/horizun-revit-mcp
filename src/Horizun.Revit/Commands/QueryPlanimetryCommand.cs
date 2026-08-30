// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_query_planimetry: read the documentation surface
// of a model, from the model.
//
// The question this answers is the one a modeller asks before a delivery and
// currently answers by exporting a PDF and looking at it: what is on my sheets,
// where is it, what is annotated, and what points at what. A PDF cannot answer
// it - it has no ids, no crop states, no template assignments, and no way to
// tell "this viewport is empty" from "this viewport's view has no content".
// The database can, and this reads the database.
//
// SIX MODES, because one answer would be enormous and useless. `inventory` is
// the census. `sheets`, `views`, `placements`, `annotations` and `references`
// each return ONE population, paginated, with the exact total whether or not the
// page was truncated.
//
// READ-ONLY BY CONSTRUCTION: it opens no Transaction, and PlanimetryInventory -
// the single collector it and the auditor share - does not either.
//
// Every row carries entity_kind, so no list ever mixes two kinds of thing
// without a discriminator, and every coordinate carries the frame it is in.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class QueryPlanimetryCommand : ICommand
    {
        public string Name => "horizun_query_planimetry";

        public string Description =>
            "Read the documentation surface of the active model: the census, sheets with their title blocks and " +
            "placements, views with template/scale/crop/phase, viewport and schedule placements in sheet " +
            "coordinates, annotations in view-plane coordinates, and references between views. Read-only, " +
            "deterministic, paginated, with exact totals and explicit coverage.";

        public const int MaxRows = 500;
        public const int DefaultRows = 100;

        private static readonly string[] Modes =
        { "inventory", "sheets", "views", "placements", "annotations", "references" };

        /// <summary>The annotation populations `categories` may name. Published in the schema
        /// and refused by name here, so a typo is an error rather than an empty answer.</summary>
        public static readonly string[] AnnotationCategories =
        {
            "dimensions", "tags", "text_notes", "detail_curves", "filled_regions",
            "detail_components", "generic_annotations", "revision_clouds"
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string mode = (request.Value<string>("mode") ?? "inventory").ToLowerInvariant();
            if (!Modes.Contains(mode, StringComparer.Ordinal))
                return CommandResult.Fail("mode must be one of: " + string.Join(", ", Modes) + ".");

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double scale;
            if (!PlanimetryGeometry.TryScaleFromFeet(units, out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            int maxRows = Math.Max(1, Math.Min(MaxRows, request.Value<int?>("max_rows") ?? DefaultRows));

            var scope = new PlanimetryScope();
            string error;
            if (!Ids(request, "sheet_ids", out scope.SheetIds, out error)) return CommandResult.Fail(error);
            if (!Ids(request, "view_ids", out scope.ViewIds, out error)) return CommandResult.Fail(error);
            if (!Ids(request, "element_ids", out scope.ElementIds, out error)) return CommandResult.Fail(error);

            JArray categories = request["categories"] as JArray;
            if (categories != null && categories.Count > 0)
            {
                if (mode != "annotations")
                    return CommandResult.Fail(
                        "categories narrows the ANNOTATION population and is meaningful only in mode=annotations. " +
                        "It is refused here rather than accepted and ignored.");
                scope.Categories = new List<string>();
                foreach (JToken c in categories)
                {
                    string name = c.Type == JTokenType.String ? (string)c : null;
                    if (name == null || !AnnotationCategories.Contains(name, StringComparer.OrdinalIgnoreCase))
                        return CommandResult.Fail(
                            "categories must name annotation populations: " +
                            string.Join(", ", AnnotationCategories) + ". Got '" + (name ?? "(not a string)") + "'.");
                    scope.Categories.Add(name);
                }
            }

            scope.IncludeParameters = request.Value<bool?>("include_parameters") ?? false;
            JArray parameterNames = request["parameter_names"] as JArray;
            if (parameterNames != null)
                foreach (JToken n in parameterNames)
                {
                    if (n.Type != JTokenType.String)
                        return CommandResult.Fail("parameter_names must be a list of strings.");
                    scope.ParameterNames.Add((string)n);
                }
            if (scope.IncludeParameters && scope.ParameterNames.Count == 0)
                return CommandResult.Fail(
                    "include_parameters=true needs parameter_names: projecting EVERY parameter of every sheet and " +
                    "view is an answer nobody can page through, and choosing a subset silently would be this " +
                    "command deciding what matters. Name the parameters you want.");
            if (!scope.IncludeParameters && scope.ParameterNames.Count > 0)
                return CommandResult.Fail(
                    "parameter_names was given without include_parameters=true, so it would be silently ignored.");

            // inventory is a census. An unscoped census can answer exactly from the
            // collector totals without materialising viewport or annotation geometry.
            // Besides being much cheaper, this avoids a measured Revit 2027 native crash
            // where GetBoxOutline forced GRep regeneration across unrelated MEP views.
            scope.CensusOnly = mode == "inventory" && !scope.Narrowed && !scope.IncludeParameters;

            // What this mode actually needs. Collecting only that keeps a `sheets` answer
            // from walking every dimension in the model.
            scope.NeedSheets = !scope.CensusOnly && mode != "views" && mode != "references";
            scope.NeedViews = !scope.CensusOnly;          // placements and annotations name views
            scope.NeedPlacements = !scope.CensusOnly &&
                                   (mode == "inventory" || mode == "sheets" || mode == "placements" ||
                                    mode == "views" || mode == "annotations");
            scope.NeedAnnotations = !scope.CensusOnly && (mode == "annotations" || mode == "inventory");
            scope.NeedReferences = mode == "references" || mode == "inventory";

            PlanimetrySnapshot snap;
            try { snap = PlanimetryInventory.Collect(doc, scope, RevitYear()); }
            catch (Exception ex) { return CommandResult.Fail("The planimetry inventory could not be read: " + ex.Message); }

            var result = new JObject
            {
                ["document"] = snap.DocumentTitle,
                ["mode"] = mode,
                ["units"] = new JObject { ["internal"] = "feet", ["display"] = units },
                ["coordinate_conventions"] = new JObject
                {
                    ["sheet"] = "paper coordinates in the requested units: X and Y of the sheet's own plane, " +
                                "which is what Viewport.GetBoxOutline and a sheet bounding box report.",
                    ["view_plane"] = "X along View.RightDirection, Y along View.UpDirection, from View.Origin. " +
                                     "A model bounding box is projected by all eight corners, so a rotated view " +
                                     "reports the element's real extent."
                },
                ["totals"] = snap.TotalsJson(),
                ["max_rows"] = maxRows,
                ["scoped"] = scope.Narrowed,
                ["unmatched_ids"] = new JArray(scope.UnmatchedIds.OrderBy(x => x).Select(x => (JToken)x))
            };

            if (mode == "inventory") return Inventory(snap, scope, result);

            List<JObject> rows;
            string population;
            switch (mode)
            {
                case "sheets":
                    population = "sheets";
                    rows = snap.Sheets
                        .OrderBy(s => s.SheetNumber ?? "￿", StringComparer.Ordinal)
                        .ThenBy(s => s.Id)
                        .Select(s => s.ToJson(scale, scope.IncludeParameters)).ToList();
                    break;
                case "views":
                    population = "views";
                    rows = snap.Views
                        .OrderBy(v => v.ViewType ?? "￿", StringComparer.Ordinal)
                        .ThenBy(v => v.Name ?? "￿", StringComparer.Ordinal)
                        .ThenBy(v => v.Id)
                        .Select(v => v.ToJson(scale)).ToList();
                    break;
                case "placements":
                    population = "placements";
                    rows = snap.Placements
                        .OrderBy(p => p.SheetNumber ?? "￿", StringComparer.Ordinal)
                        .ThenBy(p => p.SheetId)
                        .ThenBy(p => p.Class, StringComparer.Ordinal)
                        .ThenBy(p => p.Id)
                        .Select(p => p.ToJson(scale)).ToList();
                    break;
                case "annotations":
                    population = "annotations";
                    rows = snap.Annotations
                        .OrderBy(a => a.OwnerViewId ?? long.MaxValue)
                        .ThenBy(a => a.Kind, StringComparer.Ordinal)
                        .ThenBy(a => a.Id)
                        .Select(a => a.ToJson(scale)).ToList();
                    break;
                default:
                    population = "references";
                    rows = snap.References
                        .OrderBy(f => f.OwnerViewId ?? long.MaxValue)
                        .ThenBy(f => f.Kind ?? "￿", StringComparer.Ordinal)
                        .ThenBy(f => f.Id)
                        .ThenBy(f => f.TargetViewId ?? long.MaxValue)
                        .Select(f => f.ToJson()).ToList();
                    break;
            }

            string queryHash = QueryHash(request);
            string setHash = SetHash(rows);
            int offset = 0;
            string cursor = request.Value<string>("cursor");
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                string cursorError;
                if (!TryCursor(cursor, queryHash, setHash, out offset, out cursorError))
                    return CommandResult.Fail(cursorError);
            }
            if (offset > rows.Count)
                return CommandResult.Fail("The cursor starts beyond the current result set. Re-run without cursor.");

            List<JObject> page = rows.Skip(offset).Take(maxRows).ToList();
            int nextOffset = offset + page.Count;
            string nextCursor = nextOffset < rows.Count ? MakeCursor(nextOffset, queryHash, setHash) : null;

            result["population"] = population;
            result["matched_total"] = rows.Count;
            result["returned"] = page.Count;
            result["offset"] = offset;
            result["truncated"] = nextCursor != null;
            result["next_cursor"] = nextCursor == null ? (JToken)JValue.CreateNull() : nextCursor;
            result["result_set_fingerprint"] = setHash.Substring(0, 16);
            result["rows"] = new JArray(page.Select(r => (JToken)r));
            Coverage(snap, result);
            return CommandResult.Ok(result);
        }

        private static CommandResult Inventory(PlanimetrySnapshot snap, PlanimetryScope scope, JObject result)
        {
            if (scope.CensusOnly)
                return CensusInventory(snap, result);

            result["population"] = "inventory";
            result["collected"] = new JObject
            {
                ["sheets"] = snap.Sheets.Count,
                ["views"] = snap.Views.Count,
                ["placements"] = snap.Placements.Count,
                ["annotations"] = snap.Annotations.Count,
                ["references"] = snap.References.Count
            };
            result["annotations_by_kind"] = JsonObjectKey.SummaryCounts(snap.Annotations.Select(a => a.Kind));
            result["placements_by_class"] = JsonObjectKey.SummaryCounts(snap.Placements.Select(p => p.Class));
            result["references_by_target_state"] =
                JsonObjectKey.SummaryCounts(snap.References.Select(f => f.TargetState));
            result["totals_unreadable"] = new JArray(
                snap.ChecksFailed.Select(c => (JToken)c.Check).Distinct());
            result["note"] =
                "totals describe the WHOLE document; `collected` describes what this call gathered. A total " +
                "listed in totals_unreadable is ABSENT from totals rather than reported as zero.";
            Coverage(snap, result);
            return CommandResult.Ok(result);
        }

        /// <summary>
        /// Render the unscoped census from exact collector totals. No row geometry was
        /// requested, so "collected" is derived from the named totals rather than from
        /// deliberately empty row lists. A missing constituent stays null and is also
        /// named in totals_unreadable; it is never fabricated as zero.
        /// </summary>
        private static CommandResult CensusInventory(PlanimetrySnapshot snap, JObject result)
        {
            result["population"] = "inventory";
            result["collected"] = new JObject
            {
                ["sheets"] = SumTotals(snap, "sheets_total"),
                ["views"] = SumTotals(snap, "views_total", "templates_total"),
                ["placements"] = SumTotals(snap, "viewports_total", "schedule_placements_total"),
                ["annotations"] = SumTotals(snap,
                    "dimensions_total", "tags_total", "text_notes_total", "detail_curves_total",
                    "filled_regions_total", "detail_components_total", "generic_annotations_total",
                    "revision_clouds_total"),
                ["references"] = snap.References.Count
            };
            result["annotations_by_kind"] = TotalsByName(snap, new Dictionary<string, string>
            {
                ["dimension"] = "dimensions_total",
                ["tag"] = "tags_total",
                ["text_note"] = "text_notes_total",
                ["detail_curve"] = "detail_curves_total",
                ["filled_region"] = "filled_regions_total",
                ["detail_component"] = "detail_components_total",
                ["generic_annotation"] = "generic_annotations_total",
                ["revision_cloud"] = "revision_clouds_total"
            });
            result["placements_by_class"] = TotalsByName(snap, new Dictionary<string, string>
            {
                ["viewport"] = "viewports_total",
                ["schedule_placement"] = "schedule_placements_total"
            });
            result["references_by_target_state"] =
                JsonObjectKey.SummaryCounts(snap.References.Select(f => f.TargetState));
            result["totals_unreadable"] = new JArray(
                snap.ChecksFailed.Select(c => (JToken)c.Check).Distinct());
            result["note"] =
                "totals and collected are an exact lightweight census of the WHOLE document. The census does " +
                "not request viewport or annotation geometry; use placements or annotations mode for those " +
                "rows. A total listed in totals_unreadable is null in collected rather than reported as zero.";
            Coverage(snap, result);
            return CommandResult.Ok(result);
        }

        private static JToken SumTotals(PlanimetrySnapshot snap, params string[] keys)
        {
            int sum = 0;
            foreach (string key in keys)
            {
                int value;
                if (!snap.Totals.TryGetValue(key, out value)) return JValue.CreateNull();
                sum += value;
            }
            return sum;
        }

        private static JObject TotalsByName(PlanimetrySnapshot snap, Dictionary<string, string> keys)
        {
            var result = new JObject();
            foreach (KeyValuePair<string, string> pair in keys.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                int value;
                result[pair.Key] = snap.Totals.TryGetValue(pair.Value, out value)
                    ? (JToken)value : JValue.CreateNull();
            }
            return result;
        }

        /// <summary>
        /// The coverage block every mode carries. It answers the question a reader must ask
        /// before believing an empty list: was the whole model available to be read?
        /// </summary>
        private static void Coverage(PlanimetrySnapshot snap, JObject result)
        {
            var unreadable = new JArray();
            const int shown = 50;
            foreach (PlanimetryUnreadable u in snap.Unreadable.Take(shown))
                unreadable.Add(u.ToJson());
            foreach (PlanimetryRow row in snap.Sheets.Cast<PlanimetryRow>()
                         .Concat(snap.Views).Concat(snap.Placements)
                         .Concat(snap.Annotations).Concat(snap.References)
                         .Where(r => r.HasUnreadableField).Take(Math.Max(0, shown - unreadable.Count)))
                unreadable.Add(new JObject
                {
                    ["element_id"] = row.Id,
                    ["fields"] = new JArray(row.Notes.Where(n => n.State == Read.Unreadable)
                                                     .Select(n => (JToken)n.ToJson()))
                });

            result["coverage_complete"] = snap.CoverageComplete;
            result["checks_failed"] = new JArray(snap.ChecksFailed.Select(c => (JToken)c.ToJson()));
            result["unreadable_total"] = snap.UnreadableTotal;
            result["unreadable_shown"] = unreadable.Count;
            result["unreadable_truncated"] = snap.UnreadableTotal > unreadable.Count;
            result["unreadable"] = unreadable;
            result["visibility_coverage"] = snap.VisibilityCoverage;
            result["link_coverage"] = snap.LinkCoverage;
            result["coverage_note"] = snap.CoverageNote();
        }

        // ---------------------------------------------------------------------
        // Cursor: offset bound to BOTH the query arguments and the result set. A
        // cursor from other arguments, or from a model that has moved since, is
        // refused rather than silently paging through a different list.
        // ---------------------------------------------------------------------
        private static string QueryHash(JObject request)
        {
            var copy = (JObject)request.DeepClone();
            copy.Remove("cursor");
            copy.Remove("max_rows");
            return RequestFingerprint.Sha256Hex(RequestFingerprint.Canonical(copy));
        }

        private static string SetHash(IEnumerable<JObject> rows)
        {
            return RequestFingerprint.Sha256Hex(
                string.Join("\n", rows.Select(r => RequestFingerprint.Canonical(r))));
        }

        private static string MakeCursor(int offset, string queryHash, string setHash)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(
                offset.ToString(CultureInfo.InvariantCulture) + "\n" + queryHash + "\n" + setHash));
        }

        private static bool TryCursor(string cursor, string queryHash, string setHash, out int offset, out string error)
        {
            offset = 0; error = null;
            try
            {
                string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n');
                if (parts.Length != 3 ||
                    !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
                    throw new FormatException("cursor payload has the wrong shape");
                if (parts[1] != queryHash)
                { error = "The cursor belongs to different query arguments. Re-run without cursor."; return false; }
                if (parts[2] != setHash)
                {
                    error = "The planimetry result set changed since the previous page. The cursor is stale; " +
                            "re-run from the first page.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "cursor is invalid: " + ex.Message + ". Re-run without cursor.";
                return false;
            }
        }

        internal static bool Ids(JObject request, string field, out HashSet<long> ids, out string error)
        {
            ids = null; error = null;
            JArray array = request[field] as JArray;
            if (request[field] != null && array == null)
            { error = field + " must be an array of element ids."; return false; }
            if (array == null || array.Count == 0) return true;
            if (array.Count > 2000)
            { error = field + " carries " + array.Count + " ids; the limit is 2000."; return false; }
            ids = new HashSet<long>();
            foreach (JToken t in array)
            {
                if (t.Type != JTokenType.Integer)
                { error = field + " must contain integers only."; return false; }
                long id = (long)t;
                if (!Rid.CanRepresent(id)) { error = Rid.RangeError(id); return false; }
                ids.Add(id);
            }
            return true;
        }

        internal static int RevitYear()
        {
#if REVIT2023
            return 2023;
#elif REVIT2024
            return 2024;
#elif REVIT2025
            return 2025;
#elif REVIT2026
            return 2026;
#elif REVIT2027
            return 2027;
#else
            return 0;
#endif
        }
    }
}
