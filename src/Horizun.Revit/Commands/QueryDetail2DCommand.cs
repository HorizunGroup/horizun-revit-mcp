// -----------------------------------------------------------------------------
// Horizun Revit MCP - read the 2D detail surface of ONE view, with explicit
// coverage.
//
// Two modes. `resources` answers "what could I draw WITH, in this document":
// line styles, filled-region types (masking or not - read from the type, never
// from its name), and the view-based family symbols that place as detail
// components or generic annotations. `elements` answers "what 2D detail is IN
// this view": detail curves, filled/masking regions and view-based family
// instances, each with normalized geometry, a deterministic geometry signature
// (Detail2DRules) and a view-plane bounding box.
//
// NAMES ARE NEVER RESOLVED HERE, BY DESIGN. Every resource and element travels
// with its ElementId and UniqueId, and the caller picks by id. Two line styles
// called "Wide Lines" is a real model; a command that silently took the first
// would be choosing on the caller's behalf, so this command does not take names
// at all - it lists, and the ambiguity never exists.
//
// COORDINATE CONVENTION (the one convention for every 2D view): a point [x, y]
// is view-plane coordinates - X along View.RightDirection, Y along
// View.UpDirection, origin at View.Origin. model_point = view.Origin
// + x * view.RightDirection + y * view.UpDirection. Points read back from the
// model are projected into that same frame; the third component of a reported
// point is the out-of-plane offset, 0 for geometry that lies on the view plane.
//
// The house rule applies to reads as to writes: a field that could not be read
// is a coded warning on its row, never a silent null; an element that could not
// be read at all is a coverage entry, never a missing row.
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
    public sealed class QueryDetail2DCommand : ICommand
    {
        public string Name => "horizun_query_detail_2d";
        public string Description =>
            "Read the 2D detail surface of one view: mode=resources lists line styles, filled-region types " +
            "(is_masking read from the type) and placeable view-based symbols; mode=elements lists detail curves, " +
            "regions and view-based instances with normalized view-plane geometry, deterministic signatures and " +
            "per-row warnings. Ids and UniqueIds always; this command never resolves resources by name.";

        private const string Convention =
            "view-plane coordinates: X along View.RightDirection, Y along View.UpDirection, origin at " +
            "View.Origin; model_point = Origin + x*RightDirection + y*UpDirection. Reported points carry a " +
            "third component: the out-of-plane offset, 0 for geometry on the view plane.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double display;   // internal feet -> display units
            if (!TryScaleFromFeet(units, out display))
                return CommandResult.Fail("units must be mm, m or feet.");

            string mode = (request.Value<string>("mode") ?? "resources").ToLowerInvariant();
            if (mode != "resources" && mode != "elements")
                return CommandResult.Fail("mode must be 'resources' (default) or 'elements'.");

            // ---- the view: REQUIRED, by id, in the active document, not a template. --
            if (request["view_id"] == null)
                return CommandResult.Fail("view_id is required: every answer this command gives is about ONE view. " +
                                          "Resolve views with horizun_manage_views or horizun_query_model first.");
            long rawViewId = request.Value<long?>("view_id") ?? -1;
            if (!Rid.CanRepresent(rawViewId)) return CommandResult.Fail("view_id must be a valid ElementId.");
            View view = doc.GetElement(Rid.Make(rawViewId)) as View;
            if (view == null)
                return CommandResult.Fail("view_id " + rawViewId + " does not identify a view in the active document.");
            bool isTemplate; try { isTemplate = view.IsTemplate; } catch { isTemplate = false; }
            if (isTemplate)
                return CommandResult.Fail("view_id " + rawViewId + " is a VIEW TEMPLATE. A template owns no detail " +
                                          "elements and offers nothing to draw in; pass a real view.");

            int maxRows = Math.Max(1, Math.Min(500, request.Value<int?>("max_rows") ?? 100));
            int offset = Math.Max(0, request.Value<int?>("offset") ?? 0);

            var viewWarnings = new JArray();
            string acceptReason;
            bool accepts = ViewAcceptsDetail2D(view, out acceptReason);
            var result = new JObject
            {
                ["document"] = Safe(() => doc.Title),
                ["mode"] = mode,
                ["units"] = new JObject { ["internal"] = "feet", ["display"] = units },
                ["coordinate_convention"] = Convention,
                ["name_resolution"] =
                    "This command never resolves resources or elements by NAME - every row carries its ElementId " +
                    "and UniqueId and the caller picks by id, so two resources sharing a name never become a " +
                    "silent first-match choice.",
                ["view"] = new JObject
                {
                    ["id"] = Rid.Value(view.Id),
                    ["unique_id"] = Guarded(() => view.UniqueId, viewWarnings, "view_unique_id_unreadable"),
                    ["name"] = Guarded(() => view.Name, viewWarnings, "view_name_unreadable"),
                    ["view_type"] = Guarded(() => view.ViewType.ToString(), viewWarnings, "view_type_unreadable"),
                    ["scale"] = GuardedInt(() => view.Scale, viewWarnings, "view_scale_unreadable") is int s
                        ? (JToken)s : JValue.CreateNull(),
                    ["is_template"] = false,
                    ["accepts_detail_2d"] = accepts,
                    ["accepts_detail_2d_reason"] = accepts ? null : acceptReason,
                    ["warnings"] = viewWarnings
                },
                ["max_rows"] = maxRows,
                ["offset"] = offset
            };

            return mode == "resources"
                ? Resources(doc, view, result, maxRows, offset)
                : Elements(doc, view, request, result, display, maxRows, offset);
        }

        // ---------------------------------------------------------------------
        // The view types horizun_detail_2d can create in. Kept in step with
        // Detail2DCommand: the write command REFUSES anything outside this set,
        // and this read command reports the same verdict so a caller learns it
        // before planning a write.
        // ---------------------------------------------------------------------
        private static bool ViewAcceptsDetail2D(View view, out string reason)
        {
            reason = null;
            if (view is ViewSchedule) { reason = "the view is a ViewSchedule; a schedule has no drawing plane."; return false; }
            if (view is ViewSheet) { reason = "the view is a ViewSheet; detail belongs in a view placed ON the sheet, not on the sheet itself."; return false; }
            if (view is View3D) { reason = "the view is a View3D; 2D detail needs a 2D view."; return false; }
            ViewType vt;
            try { vt = view.ViewType; }
            catch (Exception ex) { reason = "the view's ViewType could not be read: " + ex.Message; return false; }
            switch (vt)
            {
                case ViewType.DraftingView:
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                    return true;
                default:
                    reason = "ViewType '" + vt + "' is outside the set this bridge draws detail in " +
                             "(DraftingView, FloorPlan, CeilingPlan, EngineeringPlan, Section, Elevation, Detail).";
                    return false;
            }
        }

        // ---------------------------------------------------------------------
        // mode=resources.
        // ---------------------------------------------------------------------
        private static CommandResult Resources(Document doc, View view, JObject result, int maxRows, int offset)
        {
            var unreadable = new JArray(); int unreadableTotal = 0; int inspected = 0;

            // ---- line styles. The authoritative universe is what an existing
            // DetailCurve answers from GetLineStyleIds(); when the document has
            // none, the Lines category's subcategories (Projection style) are the
            // source. The response names which one it used.
            var styleRows = new List<KeyValuePair<long, JObject>>();
            string styleSource;
            DetailCurve sample = null;
            try
            {
                foreach (CurveElement ce in new FilteredElementCollector(doc)
                             .OfClass(typeof(CurveElement)).OfType<CurveElement>())
                {
                    var dc = ce as DetailCurve;
                    if (dc != null) { sample = dc; break; }
                }
            }
            catch { sample = null; }
            if (sample != null)
            {
                styleSource = "CurveElement.GetLineStyleIds of an existing detail curve (element " +
                              Rid.Value(sample.Id) + ")";
                ICollection<ElementId> ids = null;
                try { ids = sample.GetLineStyleIds(); }
                catch (Exception ex)
                {
                    unreadableTotal++;
                    unreadable.Add(new JObject { ["what"] = "line_styles", ["reason"] = "GetLineStyleIds: " + ex.Message });
                }
                if (ids != null)
                    foreach (ElementId id in ids)
                    {
                        inspected++;
                        try
                        {
                            Element e = doc.GetElement(id);
                            var w = new JArray();
                            styleRows.Add(new KeyValuePair<long, JObject>(Rid.Value(id), new JObject
                            {
                                ["id"] = Rid.Value(id),
                                ["unique_id"] = e == null ? null : Guarded(() => e.UniqueId, w, "unique_id_unreadable"),
                                ["name"] = e == null ? null : Guarded(() => e.Name, w, "name_unreadable"),
                                ["warnings"] = w
                            }));
                        }
                        catch (Exception ex)
                        {
                            unreadableTotal++;
                            if (unreadable.Count < 100)
                                unreadable.Add(new JObject { ["what"] = "line_style " + Rid.Value(id), ["reason"] = ex.Message });
                        }
                    }
            }
            else
            {
                styleSource = "OST_Lines subcategories, GraphicsStyleType.Projection (the document has no detail " +
                              "curve to ask GetLineStyleIds of)";
                try
                {
                    Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                    if (lines != null)
                        foreach (Category sub in lines.SubCategories)
                        {
                            inspected++;
                            try
                            {
                                GraphicsStyle gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                                if (gs == null) continue;
                                var w = new JArray();
                                styleRows.Add(new KeyValuePair<long, JObject>(Rid.Value(gs.Id), new JObject
                                {
                                    ["id"] = Rid.Value(gs.Id),
                                    ["unique_id"] = Guarded(() => gs.UniqueId, w, "unique_id_unreadable"),
                                    ["name"] = Guarded(() => gs.Name, w, "name_unreadable"),
                                    ["warnings"] = w
                                }));
                            }
                            catch (Exception ex)
                            {
                                unreadableTotal++;
                                if (unreadable.Count < 100)
                                    unreadable.Add(new JObject { ["what"] = "line subcategory", ["reason"] = ex.Message });
                            }
                        }
                }
                catch (Exception ex)
                {
                    unreadableTotal++;
                    unreadable.Add(new JObject { ["what"] = "line_styles", ["reason"] = ex.Message });
                }
            }

            // ---- filled region types, masking READ FROM THE TYPE. ----
            var regionRows = new List<KeyValuePair<long, JObject>>();
            try
            {
                foreach (FilledRegionType t in new FilteredElementCollector(doc)
                             .OfClass(typeof(FilledRegionType)).OfType<FilledRegionType>())
                {
                    inspected++;
                    long id = Rid.Value(t.Id);
                    try
                    {
                        var w = new JArray();
                        bool? masking = GuardedBool(() => t.IsMasking, w, "is_masking_unreadable");
                        long? fg = GuardedId(() => t.ForegroundPatternId, w, "foreground_pattern_unreadable");
                        regionRows.Add(new KeyValuePair<long, JObject>(id, new JObject
                        {
                            ["id"] = id,
                            ["unique_id"] = Guarded(() => t.UniqueId, w, "unique_id_unreadable"),
                            ["name"] = Guarded(() => t.Name, w, "name_unreadable"),
                            ["is_masking"] = masking.HasValue ? (JToken)masking.Value : JValue.CreateNull(),
                            ["foreground_pattern_id"] = fg.HasValue ? (JToken)fg.Value : JValue.CreateNull(),
                            ["warnings"] = w
                        }));
                    }
                    catch (Exception ex)
                    {
                        unreadableTotal++;
                        if (unreadable.Count < 100)
                            unreadable.Add(new JObject { ["what"] = "filled_region_type " + id, ["reason"] = ex.Message });
                    }
                }
            }
            catch (Exception ex)
            {
                unreadableTotal++;
                unreadable.Add(new JObject { ["what"] = "filled_region_types", ["reason"] = ex.Message });
            }

            // ---- placeable view-based symbols: detail components and generic
            // annotations, selected by Family.FamilyPlacementType == ViewBased -
            // the machine fact - never by name.
            var symbolRows = new List<KeyValuePair<long, JObject>>();
            try
            {
                foreach (FamilySymbol fs in new FilteredElementCollector(doc)
                             .OfClass(typeof(FamilySymbol)).OfType<FamilySymbol>())
                {
                    inspected++;
                    long id = Rid.Value(fs.Id);
                    try
                    {
                        Family fam = fs.Family;
                        if (fam == null || fam.FamilyPlacementType != FamilyPlacementType.ViewBased) continue;
                        long catId = fs.Category == null ? 0 : Rid.Value(fs.Category.Id);
                        string placement =
                            catId == (int)BuiltInCategory.OST_DetailComponents ? "detail_component"
                            : catId == (int)BuiltInCategory.OST_GenericAnnotation ? "generic_annotation"
                            : null;
                        if (placement == null) continue;
                        var w = new JArray();
                        symbolRows.Add(new KeyValuePair<long, JObject>(id, new JObject
                        {
                            ["id"] = id,
                            ["unique_id"] = Guarded(() => fs.UniqueId, w, "unique_id_unreadable"),
                            ["family_name"] = Guarded(() => fam.Name, w, "family_name_unreadable"),
                            ["type_name"] = Guarded(() => fs.Name, w, "type_name_unreadable"),
                            ["category"] = Guarded(() => fs.Category?.Name, w, "category_unreadable"),
                            ["placement"] = placement,
                            ["is_active"] = GuardedBool(() => fs.IsActive, w, "is_active_unreadable") is bool act
                                ? (JToken)act : JValue.CreateNull(),
                            ["warnings"] = w
                        }));
                    }
                    catch (Exception ex)
                    {
                        unreadableTotal++;
                        if (unreadable.Count < 100)
                            unreadable.Add(new JObject { ["what"] = "family_symbol " + id, ["reason"] = ex.Message });
                    }
                }
            }
            catch (Exception ex)
            {
                unreadableTotal++;
                unreadable.Add(new JObject { ["what"] = "family_symbols", ["reason"] = ex.Message });
            }

            result["line_styles"] = PageList(styleRows, maxRows, offset, "source", (JToken)styleSource);
            result["filled_region_types"] = PageList(regionRows, maxRows, offset, null, null);
            result["placeable_symbols"] = PageList(symbolRows, maxRows, offset, null, null);
            result["ordering"] = "each resource list is ordered by element_id ascending; offset/max_rows page over " +
                                 "each list independently";
            result["coverage"] = new JObject
            {
                ["inspected"] = inspected,
                ["unreadable_total"] = unreadableTotal,
                ["unreadable_shown"] = unreadable.Count,
                ["unreadable"] = unreadable
            };
            return CommandResult.Ok(result);
        }

        private static JObject PageList(List<KeyValuePair<long, JObject>> rows, int maxRows, int offset,
                                        string extraKey, JToken extraValue)
        {
            rows.Sort((a, b) => a.Key.CompareTo(b.Key));
            List<JObject> page = rows.Skip(offset).Take(maxRows).Select(kv => kv.Value).ToList();
            var block = new JObject
            {
                ["total"] = rows.Count,
                ["returned"] = page.Count,
                ["truncated"] = offset + page.Count < rows.Count,
                ["rows"] = new JArray(page)
            };
            if (extraKey != null) block[extraKey] = extraValue;
            return block;
        }

        // ---------------------------------------------------------------------
        // mode=elements.
        // ---------------------------------------------------------------------
        private static CommandResult Elements(Document doc, View view, JObject request, JObject result,
                                              double display, int maxRows, int offset)
        {
            HashSet<long> wantedIds = null;
            if (request["element_ids"] is JArray idsArray)
            {
                if (idsArray.Count == 0 || idsArray.Count > 2000)
                    return CommandResult.Fail("element_ids must contain 1..2000 ids when present.");
                wantedIds = new HashSet<long>();
                foreach (JToken token in idsArray)
                {
                    if (token.Type != JTokenType.Integer)
                        return CommandResult.Fail("element_ids entries must be integers.");
                    long id = token.Value<long>();
                    if (!Rid.CanRepresent(id)) return CommandResult.Fail(Rid.RangeError(id));
                    wantedIds.Add(id);
                }
            }

            HashSet<long> categoryFilter = null;
            if (request["categories"] is JArray catArray)
            {
                categoryFilter = new HashSet<long>();
                foreach (JToken token in catArray)
                {
                    string tokenText = token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None);
                    long catId;
                    if (!TryCategoryToken(tokenText, out catId))
                        return CommandResult.Fail("categories entry '" + tokenText + "' is not recognised. Accepted " +
                                                  "tokens: lines (OST_Lines), detail_components (OST_DetailComponents), " +
                                                  "generic_annotations (OST_GenericAnnotation), filled_regions " +
                                                  "(OST_FilledRegion); the OST_* spellings are accepted too.");
                    categoryFilter.Add(catId);
                }
                if (categoryFilter.Count == 0) categoryFilter = null;
            }

            HashSet<long> typeFilter = null;
            if (request["type_ids"] is JArray typeArray)
            {
                typeFilter = new HashSet<long>();
                foreach (JToken token in typeArray)
                {
                    if (token.Type != JTokenType.Integer)
                        return CommandResult.Fail("type_ids entries must be integers.");
                    typeFilter.Add(token.Value<long>());
                }
                if (typeFilter.Count == 0) typeFilter = null;
            }

            // bounding_box: {min:[x,y], max:[x,y]} in view-plane coordinates, in
            // the request's units. Elements whose view-plane box intersects it match.
            double[] boxMin = null, boxMax = null;
            if (request["bounding_box"] is JObject box)
            {
                boxMin = TwoNumbers(box["min"]); boxMax = TwoNumbers(box["max"]);
                if (boxMin == null || boxMax == null)
                    return CommandResult.Fail("bounding_box must be {min:[x,y], max:[x,y]} in view-plane " +
                                              "coordinates (" + Convention + "), in the request's units.");
                // to internal feet
                boxMin[0] /= display; boxMin[1] /= display; boxMax[0] /= display; boxMax[1] /= display;
                if (boxMin[0] > boxMax[0] || boxMin[1] > boxMax[1])
                    return CommandResult.Fail("bounding_box min must not exceed max on either axis.");
            }

            var matched = new List<KeyValuePair<long, JObject>>();
            var unreadable = new JArray(); int unreadableTotal = 0; int inspected = 0; int otherClasses = 0;

            IEnumerable<Element> owned;
            try { owned = new FilteredElementCollector(doc).OwnedByView(view.Id).ToElements(); }
            catch (Exception ex)
            {
                return CommandResult.Fail("Elements owned by view " + Rid.Value(view.Id) + " could not be " +
                                          "collected: " + ex.Message);
            }

            foreach (Element e in owned)
            {
                if (e == null) continue;
                inspected++;
                long id = Rid.Value(e.Id);
                try
                {
                    bool isCurve = e is DetailCurve;
                    bool isRegion = e is FilledRegion;
                    bool isInstance = e is FamilyInstance;
                    if (!isCurve && !isRegion && !isInstance) { otherClasses++; continue; }
                    if (wantedIds != null && !wantedIds.Contains(id)) continue;
                    long catId = 0; try { catId = e.Category == null ? 0 : Rid.Value(e.Category.Id); } catch { catId = 0; }
                    if (categoryFilter != null && !categoryFilter.Contains(catId)) continue;

                    var warnings = new JArray();
                    JObject row = isCurve
                        ? CurveRow(doc, view, (DetailCurve)e, id, display, warnings)
                        : isRegion
                            ? RegionRow(doc, view, (FilledRegion)e, id, display, warnings)
                            : InstanceRow(view, (FamilyInstance)e, id, display, warnings);
                    if (row == null) continue;

                    if (typeFilter != null)
                    {
                        JToken t = row["type_id"];
                        long tid = t != null && t.Type == JTokenType.Integer ? t.Value<long>() : -1;
                        JToken ls = row["line_style_id"];
                        long lsId = ls != null && ls.Type == JTokenType.Integer ? ls.Value<long>() : -1;
                        if (!typeFilter.Contains(tid) && !typeFilter.Contains(lsId)) continue;
                    }
                    if (boxMin != null)
                    {
                        double[] bb = row["__bbox_feet"] as JArray != null
                            ? ((JArray)row["__bbox_feet"]).Select(t => t.Value<double>()).ToArray() : null;
                        if (bb == null || !Intersects(bb, boxMin, boxMax)) continue;
                    }
                    row.Remove("__bbox_feet");
                    row["warnings"] = warnings;
                    matched.Add(new KeyValuePair<long, JObject>(id, row));
                }
                catch (Exception ex)
                {
                    unreadableTotal++;
                    if (unreadable.Count < 100)
                        unreadable.Add(new JObject { ["element_id"] = id, ["reason"] = ex.Message });
                }
            }

            matched.Sort((a, b) => a.Key.CompareTo(b.Key));
            List<JObject> page = matched.Skip(offset).Take(maxRows).Select(kv => kv.Value).ToList();

            result["filters"] = new JObject
            {
                ["view_id"] = Rid.Value(view.Id),
                ["element_ids"] = wantedIds == null ? JValue.CreateNull()
                                                    : new JArray(wantedIds.OrderBy(x => x).Select(x => (JToken)x)),
                ["categories"] = categoryFilter == null ? JValue.CreateNull()
                                                        : new JArray(categoryFilter.OrderBy(x => x).Select(x => (JToken)x)),
                ["type_ids"] = typeFilter == null ? JValue.CreateNull()
                                                  : new JArray(typeFilter.OrderBy(x => x).Select(x => (JToken)x)),
                ["bounding_box_applied"] = boxMin != null
            };
            result["total_matched"] = matched.Count;
            result["returned"] = page.Count;
            result["truncated"] = offset + page.Count < matched.Count;
            result["ordering"] = "rows are ordered by element_id ascending; offset/max_rows page over that order";
            result["coverage"] = new JObject
            {
                ["inspected"] = inspected,
                ["not_detail_2d_classes"] = otherClasses,
                ["unreadable_total"] = unreadableTotal,
                ["unreadable_shown"] = unreadable.Count,
                ["unreadable"] = unreadable
            };
            result["rows"] = new JArray(page);

            if (wantedIds != null)
            {
                var matchedIds = new HashSet<long>(matched.Select(kv => kv.Key));
                result["element_ids_not_matched"] = new JArray(
                    wantedIds.Where(x => !matchedIds.Contains(x)).OrderBy(x => x).Select(x => (JToken)x));
            }
            return CommandResult.Ok(result);
        }

        // ---------------------------------------------------------------------
        // One row per element kind.
        // ---------------------------------------------------------------------
        private static JObject CommonRow(View view, Element e, long id, string kind, JArray warnings)
        {
            var row = new JObject
            {
                ["element_id"] = id,
                ["unique_id"] = Guarded(() => e.UniqueId, warnings, "unique_id_unreadable"),
                ["kind"] = kind,
                ["class"] = e.GetType().Name,
                ["category"] = Guarded(() => e.Category?.Name, warnings, "category_unreadable"),
                ["owner_view_id"] = GuardedId(() => e.OwnerViewId, warnings, "owner_view_unreadable") is long ov
                    ? (JToken)ov : JValue.CreateNull(),
                ["owner_view_name"] = Guarded(() => view.Name, warnings, "owner_view_name_unreadable"),
                ["pinned"] = GuardedBool(() => e.Pinned, warnings, "pinned_unreadable") is bool p
                    ? (JToken)p : JValue.CreateNull()
            };
            long? groupId = GuardedId(() =>
            {
                ElementId g = e.GroupId;
                return g == null || g == ElementId.InvalidElementId ? null : g;
            }, warnings, "group_unreadable");
            row["group_id"] = groupId.HasValue ? (JToken)groupId.Value : JValue.CreateNull();
            return row;
        }

        private static JObject CurveRow(Document doc, View view, DetailCurve dc, long id, double display, JArray warnings)
        {
            JObject row = CommonRow(view, dc, id, "detail_curve", warnings);
            row["type_id"] = JValue.CreateNull();
            row["type_name"] = JValue.CreateNull();
            try
            {
                Element style = dc.LineStyle;
                row["line_style_id"] = style == null ? (JToken)JValue.CreateNull() : Rid.Value(style.Id);
                row["line_style_name"] = style == null ? null : Guarded(() => style.Name, warnings, "line_style_name_unreadable");
            }
            catch (Exception ex)
            {
                warnings.Add(Warn("line_style_unreadable", ex.Message));
                row["line_style_id"] = JValue.CreateNull(); row["line_style_name"] = JValue.CreateNull();
            }

            Curve c = null;
            try { c = dc.GeometryCurve; }
            catch (Exception ex) { warnings.Add(Warn("geometry_unreadable", ex.Message)); }
            var pts = new List<double[]>();
            if (c is Line line)
            {
                double[] s = ViewFrame(view, line.GetEndPoint(0)), t = ViewFrame(view, line.GetEndPoint(1));
                row["geometry_kind"] = "line";
                row["geometry"] = new JObject
                {
                    ["kind"] = "line",
                    ["start"] = PointJson(s, display),
                    ["end"] = PointJson(t, display)
                };
                row["geometry_signature"] = SafeSignature(() => Detail2DRules.CanonicalLineSignature(s, t), warnings);
                pts.Add(s); pts.Add(t);
            }
            else if (c is Arc arc)
            {
                double[] ctr = ViewFrame(view, arc.Center);
                double[] s = ViewFrame(view, arc.GetEndPoint(0)), t = ViewFrame(view, arc.GetEndPoint(1));
                row["geometry_kind"] = "arc";
                row["geometry"] = new JObject
                {
                    ["kind"] = "arc",
                    ["center"] = PointJson(ctr, display),
                    ["radius"] = arc.Radius * display,
                    ["start"] = PointJson(s, display),
                    ["end"] = PointJson(t, display)
                };
                row["geometry_signature"] = SafeSignature(
                    () => Detail2DRules.CanonicalArcSignature(ctr, arc.Radius, s, t), warnings);
                try { foreach (XYZ p in c.Tessellate()) pts.Add(ViewFrame(view, p)); }
                catch (Exception ex) { warnings.Add(Warn("tessellation_unreadable", ex.Message)); }
            }
            else if (c != null)
            {
                string kindName = c.GetType().Name.ToLowerInvariant();
                warnings.Add(Warn("curve_kind_unmapped",
                    "the curve is a " + c.GetType().Name + "; this row reports its class and bounding box but does " +
                    "not decompose or sign it"));
                row["geometry_kind"] = kindName;
                row["geometry"] = new JObject { ["kind"] = kindName };
                row["geometry_signature"] = JValue.CreateNull();
                try { foreach (XYZ p in c.Tessellate()) pts.Add(ViewFrame(view, p)); }
                catch (Exception ex) { warnings.Add(Warn("tessellation_unreadable", ex.Message)); }
            }
            else
            {
                row["geometry_kind"] = JValue.CreateNull();
                row["geometry"] = JValue.CreateNull();
                row["geometry_signature"] = JValue.CreateNull();
            }
            AttachViewBox(row, pts, display);
            return row;
        }

        private static JObject RegionRow(Document doc, View view, FilledRegion region, long id, double display,
                                         JArray warnings)
        {
            JObject row = CommonRow(view, region, id, "filled_region", warnings);
            row["line_style_id"] = JValue.CreateNull();
            row["line_style_name"] = JValue.CreateNull();

            long? typeId = GuardedId(() => region.GetTypeId(), warnings, "type_unreadable");
            row["type_id"] = typeId.HasValue ? (JToken)typeId.Value : JValue.CreateNull();
            FilledRegionType type = null;
            if (typeId.HasValue)
            {
                try { type = doc.GetElement(Rid.Make(typeId.Value)) as FilledRegionType; }
                catch (Exception ex) { warnings.Add(Warn("type_unreadable", ex.Message)); }
            }
            row["type_name"] = type == null ? null : Guarded(() => type.Name, warnings, "type_name_unreadable");
            bool? masking = type == null ? null : GuardedBool(() => type.IsMasking, warnings, "is_masking_unreadable");
            row["is_masking"] = masking.HasValue ? (JToken)masking.Value : JValue.CreateNull();

            IList<CurveLoop> loops = null;
            try { loops = region.GetBoundaries(); }
            catch (Exception ex) { warnings.Add(Warn("boundaries_unreadable", ex.Message)); }

            var curvesPerLoop = new JArray();
            var loopSignatures = new List<string>();
            var loopVertexLists = new List<List<double[]>>();
            var pts = new List<double[]>();
            bool signable = loops != null;
            if (loops != null)
            {
                foreach (CurveLoop loop in loops)
                {
                    int curves = 0;
                    var vertices = new List<double[]>();
                    var segSigs = new List<string>();
                    foreach (Curve c in loop)
                    {
                        curves++;
                        try
                        {
                            double[] a = ViewFrame(view, c.GetEndPoint(0)), b = ViewFrame(view, c.GetEndPoint(1));
                            vertices.Add(a);
                            pts.Add(a); pts.Add(b);
                            if (c is Line)
                            {
                                string sig = Detail2DRules.CanonicalLineSignature(a, b);
                                if (sig == null) signable = false; else segSigs.Add(sig);
                            }
                            else
                            {
                                var arc = c as Arc;
                                string sig = arc == null
                                    ? null
                                    : Detail2DRules.CanonicalArcSignature(ViewFrame(view, arc.Center), arc.Radius, a, b);
                                if (sig == null)
                                {
                                    signable = false;
                                    warnings.Add(Warn("boundary_curve_unsigned",
                                        "a boundary curve is a " + c.GetType().Name + "; the region signature is " +
                                        "withheld rather than computed over a curve this bridge cannot sign"));
                                }
                                else segSigs.Add(sig);
                            }
                        }
                        catch (Exception ex)
                        {
                            signable = false;
                            warnings.Add(Warn("boundary_curve_unreadable", ex.Message));
                        }
                    }
                    curvesPerLoop.Add(curves);
                    loopVertexLists.Add(vertices);
                    if (signable)
                    {
                        string ls = SafeSignature(() => Detail2DRules.LoopSignature(segSigs), warnings);
                        if (ls == null) signable = false; else loopSignatures.Add(ls);
                    }
                }
            }
            row["loops"] = loops == null ? (JToken)JValue.CreateNull() : loops.Count;
            row["curves_per_loop"] = curvesPerLoop;

            string regionSignature = null;
            if (signable && loopSignatures.Count > 0)
            {
                int outerIndex;
                string structural = null;
                try
                {
                    structural = Detail2DRules.ValidateRegionLoops(
                        loopVertexLists.Select(v => (IReadOnlyList<double[]>)v).ToList(), out outerIndex);
                }
                catch (Exception ex) { structural = ex.Message; outerIndex = -1; }
                if (structural == null && outerIndex >= 0 && outerIndex < loopSignatures.Count)
                {
                    var holes = new List<string>();
                    for (int i = 0; i < loopSignatures.Count; i++) if (i != outerIndex) holes.Add(loopSignatures[i]);
                    regionSignature = SafeSignature(
                        () => Detail2DRules.RegionSignature(loopSignatures[outerIndex], holes), warnings);
                }
                else if (structural != null)
                    warnings.Add(Warn("region_structure_unclassified",
                        "the read boundaries did not classify into one outer loop plus holes (" + structural +
                        "); region_signature is withheld"));
            }
            row["geometry_kind"] = "region";
            row["geometry"] = JValue.CreateNull();
            row["geometry_signature"] = JValue.CreateNull();
            row["region_signature"] = regionSignature;
            AttachViewBox(row, pts, display);
            return row;
        }

        private static JObject InstanceRow(View view, FamilyInstance fi, long id, double display, JArray warnings)
        {
            JObject row = CommonRow(view, fi, id, "family_instance", warnings);
            row["line_style_id"] = JValue.CreateNull();
            row["line_style_name"] = JValue.CreateNull();
            FamilySymbol symbol = null;
            try { symbol = fi.Symbol; } catch (Exception ex) { warnings.Add(Warn("symbol_unreadable", ex.Message)); }
            row["type_id"] = symbol == null ? (JToken)JValue.CreateNull() : Rid.Value(symbol.Id);
            row["type_name"] = symbol == null ? null : Guarded(() => symbol.Name, warnings, "type_name_unreadable");
            row["family_name"] = symbol == null ? null : Guarded(() => symbol.Family?.Name, warnings, "family_name_unreadable");

            double[] point = null; double? rotation = null;
            try
            {
                var lp = fi.Location as LocationPoint;
                if (lp != null)
                {
                    point = ViewFrame(view, lp.Point);
                    try { rotation = lp.Rotation; }
                    catch (Exception ex) { warnings.Add(Warn("rotation_unreadable", ex.Message)); }
                }
                else warnings.Add(Warn("location_not_point", "the instance's Location is not a LocationPoint"));
            }
            catch (Exception ex) { warnings.Add(Warn("location_unreadable", ex.Message)); }
            row["geometry_kind"] = "instance";
            row["geometry"] = point == null ? (JToken)JValue.CreateNull()
                                            : new JObject { ["kind"] = "point", ["point"] = PointJson(point, display) };
            row["geometry_signature"] = JValue.CreateNull();
            row["rotation_degrees"] = rotation.HasValue
                ? (JToken)(rotation.Value * 180.0 / Math.PI) : JValue.CreateNull();

            var pts = new List<double[]>();
            try
            {
                BoundingBoxXYZ bb = fi.get_BoundingBox(view);
                if (bb != null)
                {
                    // Project all eight corners so a rotated box still bounds correctly.
                    for (int i = 0; i < 8; i++)
                    {
                        var corner = new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X,
                                             (i & 2) == 0 ? bb.Min.Y : bb.Max.Y,
                                             (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                        Transform t = bb.Transform;
                        pts.Add(ViewFrame(view, t == null ? corner : t.OfPoint(corner)));
                    }
                }
            }
            catch (Exception ex) { warnings.Add(Warn("bounding_box_unreadable", ex.Message)); }
            if (pts.Count == 0 && point != null) pts.Add(point);
            AttachViewBox(row, pts, display);
            return row;
        }

        // ---------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------

        /// <summary>Model point into the view-plane frame, internal feet: [x, y, out-of-plane].</summary>
        private static double[] ViewFrame(View view, XYZ p)
        {
            XYZ d = p.Subtract(view.Origin);
            return new[]
            {
                d.DotProduct(view.RightDirection),
                d.DotProduct(view.UpDirection),
                d.DotProduct(view.ViewDirection)
            };
        }

        private static void AttachViewBox(JObject row, List<double[]> viewPointsFeet, double display)
        {
            if (viewPointsFeet == null || viewPointsFeet.Count == 0)
            {
                row["bounding_box_view"] = JValue.CreateNull();
                return;
            }
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (double[] p in viewPointsFeet)
            {
                if (p[0] < minX) minX = p[0];
                if (p[1] < minY) minY = p[1];
                if (p[0] > maxX) maxX = p[0];
                if (p[1] > maxY) maxY = p[1];
            }
            row["bounding_box_view"] = new JObject
            {
                ["min"] = new JArray(minX * display, minY * display),
                ["max"] = new JArray(maxX * display, maxY * display)
            };
            // Internal-feet copy for the bounding_box filter; stripped before the row ships.
            row["__bbox_feet"] = new JArray(minX, minY, maxX, maxY);
        }

        private static bool Intersects(double[] bboxFeet, double[] minFeet, double[] maxFeet)
            => bboxFeet.Length == 4 &&
               bboxFeet[0] <= maxFeet[0] && bboxFeet[2] >= minFeet[0] &&
               bboxFeet[1] <= maxFeet[1] && bboxFeet[3] >= minFeet[1];

        private static bool TryCategoryToken(string token, out long categoryId)
        {
            categoryId = 0;
            if (string.IsNullOrWhiteSpace(token)) return false;
            switch (token.Trim().ToLowerInvariant())
            {
                case "lines": case "detail_lines": case "ost_lines":
                    categoryId = (int)BuiltInCategory.OST_Lines; return true;
                case "detail_components": case "ost_detailcomponents":
                    categoryId = (int)BuiltInCategory.OST_DetailComponents; return true;
                case "generic_annotations": case "ost_genericannotation":
                    categoryId = (int)BuiltInCategory.OST_GenericAnnotation; return true;
                case "filled_regions": case "masking_regions": case "ost_filledregion":
                    categoryId = (int)BuiltInCategory.OST_FilledRegion; return true;
                default:
                    return false;
            }
        }

        private static double[] TwoNumbers(JToken t)
        {
            var a = t as JArray;
            if (a == null || a.Count != 2) return null;
            try { return new[] { a[0].Value<double>(), a[1].Value<double>() }; }
            catch { return null; }
        }

        private static JArray PointJson(double[] viewFeet, double display)
            => new JArray(viewFeet[0] * display, viewFeet[1] * display, viewFeet[2] * display);

        private static string SafeSignature(Func<string> f, JArray warnings)
        {
            try
            {
                string s = f();
                if (s == null) warnings.Add(Warn("signature_unavailable", "the signature rules answered null for this geometry"));
                return s;
            }
            catch (Exception ex) { warnings.Add(Warn("signature_unreadable", ex.Message)); return null; }
        }

        private static JObject Warn(string code, string message)
            => new JObject { ["code"] = code, ["message"] = message };

        private static string Guarded(Func<string> f, JArray warnings, string code)
        {
            try { return f(); }
            catch (Exception ex) { warnings.Add(Warn(code, ex.Message)); return null; }
        }

        private static bool? GuardedBool(Func<bool> f, JArray warnings, string code)
        {
            try { return f(); }
            catch (Exception ex) { warnings.Add(Warn(code, ex.Message)); return null; }
        }

        private static int? GuardedInt(Func<int> f, JArray warnings, string code)
        {
            try { return f(); }
            catch (Exception ex) { warnings.Add(Warn(code, ex.Message)); return null; }
        }

        private static long? GuardedId(Func<ElementId> f, JArray warnings, string code)
        {
            try
            {
                ElementId id = f();
                return id == null || id == ElementId.InvalidElementId ? (long?)null : Rid.Value(id);
            }
            catch (Exception ex) { warnings.Add(Warn(code, ex.Message)); return null; }
        }

        private static bool TryScaleFromFeet(string units, out double scale)
        {
            if (units == "feet") { scale = 1; return true; }
            if (units == "m") { scale = 0.3048; return true; }
            if (units == "mm") { scale = 304.8; return true; }
            scale = 0; return false;
        }

        private static string Safe(Func<string> f) { try { return f(); } catch { return null; } }
    }
}
