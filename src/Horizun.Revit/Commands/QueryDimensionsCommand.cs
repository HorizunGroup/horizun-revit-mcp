// -----------------------------------------------------------------------------
// Horizun Revit MCP - read every fact a dimension carries, with explicit coverage.
//
// A dimension is the densest small element in a model: identity, shape, an owner
// view, a type, a curve, an ordered set of references into OTHER elements,
// per-segment values and overrides, an EQ toggle, a lock, and - for spots - an
// origin. Any of those reads can fail on a real model (a reference whose element
// was deleted, a curve Revit hands back unbound, a segment mid-regeneration), and
// the house rule applies to reads as much as writes: a field that could not be
// read is a warning WITH A CODE on that row, never a silent null, and an element
// that could not be read at all is a coverage entry, never a missing row.
//
// Host-only by design. A reference INTO a link is reported (linked:true, with the
// raw ids) but never resolved further - the link's document is another model with
// its own lifecycle, and half-resolving it would produce rows that read complete.
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
    public sealed class QueryDimensionsCommand : ICommand
    {
        public string Name => "horizun_query_dimensions";
        public string Description =>
            "Read dimensions from the active document - shape, owner view, type, curve, references, segments, " +
            "overrides, EQ and lock - paginated deterministically, with per-row warnings for every field that " +
            "could not be read and explicit coverage for every element that could not.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document host = app.ActiveUIDocument?.Document;
            if (host == null) return CommandResult.Fail("No active Revit document.");

            string units = (request.Value<string>("units") ?? "mm").ToLowerInvariant();
            double scale;
            if (!TryScaleFromFeet(units, out scale))
                return CommandResult.Fail("units must be mm, m or feet.");

            // ---- Filters. Every one optional; every one validated before the walk. ----
            ElementId viewId = null;
            if (request["view_id"] != null)
            {
                long raw = request.Value<long?>("view_id") ?? -1;
                if (!Rid.CanRepresent(raw)) return CommandResult.Fail("view_id must be a valid ElementId.");
                View view = host.GetElement(Rid.Make(raw)) as View;
                if (view == null) return CommandResult.Fail("view_id " + raw + " does not identify a view in the active document.");
                viewId = view.Id;
            }

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

            long? typeFilter = null;
            if (request["dimension_type_id"] != null)
            {
                long raw = request.Value<long?>("dimension_type_id") ?? -1;
                if (!Rid.CanRepresent(raw)) return CommandResult.Fail("dimension_type_id must be a valid ElementId.");
                typeFilter = raw;
            }

            HashSet<string> shapeFilter = null;
            if (request["shapes"] is JArray shapesArray)
            {
                var names = shapesArray.Select(t => t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None)).ToList();
                if (!DimensionEditRules.TryParseShapes(names, out shapeFilter, out string shapeError))
                    return CommandResult.Fail(shapeError);
                if (shapeFilter.Count == 0) shapeFilter = null;
            }

            int maxRows = Math.Max(1, Math.Min(500, request.Value<int?>("max_rows") ?? 100));
            int offset = Math.Max(0, request.Value<int?>("offset") ?? 0);

            // ---- The walk. One guarded pass; a row that cannot be read is coverage. ----
            var matched = new List<KeyValuePair<long, JObject>>();
            var unreadable = new JArray();
            int unreadableTotal = 0;
            int inspected = 0;

            foreach (Dimension d in new FilteredElementCollector(host)
                         .OfClass(typeof(Dimension)).OfType<Dimension>())
            {
                inspected++;
                long id = Rid.Value(d.Id);
                try
                {
                    if (wantedIds != null && !wantedIds.Contains(id)) continue;
                    if (viewId != null && d.OwnerViewId != viewId) continue;
                    if (typeFilter != null && Rid.Value(d.GetTypeId()) != typeFilter.Value) continue;

                    var warnings = new JArray();
                    string shape = Shape(d, warnings);
                    if (shapeFilter != null && (shape == null || !shapeFilter.Contains(shape))) continue;

                    matched.Add(new KeyValuePair<long, JObject>(id, Row(host, d, id, shape, scale, warnings)));
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

            var result = new JObject
            {
                ["document"] = Safe(() => host.Title),
                ["units"] = new JObject { ["internal"] = "feet", ["display"] = units },
                ["filters"] = new JObject
                {
                    ["view_id"] = viewId == null ? JValue.CreateNull() : new JValue(Rid.Value(viewId)),
                    ["element_ids"] = wantedIds == null ? JValue.CreateNull()
                                                        : new JArray(wantedIds.OrderBy(x => x).Select(x => (JToken)x)),
                    ["dimension_type_id"] = typeFilter == null ? JValue.CreateNull() : new JValue(typeFilter.Value),
                    ["shapes"] = shapeFilter == null ? JValue.CreateNull()
                                                     : new JArray(shapeFilter.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JToken)x))
                },
                ["total_matched"] = matched.Count,
                ["returned"] = page.Count,
                ["offset"] = offset,
                ["max_rows"] = maxRows,
                ["truncated"] = offset + page.Count < matched.Count,
                ["ordering"] = "rows are ordered by element_id ascending; offset/max_rows page over that order",
                ["coverage"] = new JObject
                {
                    ["inspected"] = inspected,
                    ["unreadable_total"] = unreadableTotal,
                    ["unreadable_shown"] = unreadable.Count,
                    ["unreadable"] = unreadable
                },
                ["rows"] = new JArray(page)
            };

            // Ids the caller ASKED about that produced no row are named, not implied: a
            // typo'd id and a dimension filtered out by shape both read as silence
            // otherwise, and silence is the one answer this repository does not give.
            if (wantedIds != null)
            {
                var matchedIds = new HashSet<long>(matched.Select(kv => kv.Key));
                result["element_ids_not_matched"] = new JArray(
                    wantedIds.Where(x => !matchedIds.Contains(x)).OrderBy(x => x).Select(x => (JToken)x));
            }

            return CommandResult.Ok(result);
        }

        // ---------------------------------------------------------------------
        // One row. Every field individually guarded: a warning with a code, never
        // a silent absence.
        // ---------------------------------------------------------------------

        private static JObject Row(Document host, Dimension d, long id, string shape, double scale, JArray warnings)
        {
            var row = new JObject
            {
                ["element_id"] = id,
                ["unique_id"] = Guarded(() => d.UniqueId, warnings, "unique_id_unreadable"),
                ["shape"] = shape ?? "unknown"
            };

            // Constraints wear the Dimension class too: a locked alignment or a sketch EQ
            // is a Dimension in the Constraints category with no usable owner view. A
            // caller "reading the model's dimensions" must be able to tell annotation
            // from constraint WITHOUT decoding a null view, so the category and
            // view-specificity travel on every row.
            row["category"] = Guarded(() => d.Category == null ? null : d.Category.Name, warnings, "category_unreadable");
            bool? viewSpecific = GuardedBool(() => d.ViewSpecific, warnings, "view_specific_unreadable");
            row["is_view_specific"] = viewSpecific == null ? JValue.CreateNull() : new JValue(viewSpecific.Value);

            row["owner_view"] = OwnerView(host, d, warnings);
            row["type"] = TypeBlock(host, d, warnings);
            row["curve"] = CurveJson(d, scale, warnings);

            bool? refsAvailable = GuardedBool(() => d.AreReferencesAvailable, warnings, "references_available_unreadable");
            row["references_available"] = refsAvailable == null ? JValue.CreateNull() : new JValue(refsAvailable.Value);

            int broken;
            row["references"] = References(host, d, warnings, out broken);
            row["broken_references"] = broken;

            int segmentCount = GuardedInt(() => d.NumberOfSegments, warnings, "segment_count_unreadable") ?? 0;
            row["number_of_segments"] = segmentCount;
            row["segments"] = Segments(d, warnings);

            if (segmentCount <= 1)
            {
                double? value = GuardedNullableDouble(() => d.Value, warnings, "value_unreadable");
                row["value_internal_feet"] = value == null ? JValue.CreateNull() : new JValue(value.Value);
                row["value_presented"] = Guarded(() => d.ValueString, warnings, "value_presented_unreadable");
                row["eq"] = JValue.CreateNull();   // nothing to equalise; reading it would throw
            }
            else
            {
                row["value_internal_feet"] = JValue.CreateNull();   // per segment on a multi-segment dimension
                row["value_presented"] = JValue.CreateNull();
                bool? eq = GuardedBool(() => d.AreSegmentsEqual, warnings, "eq_unreadable");
                row["eq"] = eq == null ? JValue.CreateNull() : new JValue(eq.Value);
            }

            bool? locked = GuardedBool(() => d.IsLocked, warnings, "lock_unreadable");
            row["lock"] = locked == null ? JValue.CreateNull() : new JValue(locked.Value);

            if (d is SpotDimension)
            {
                XYZ origin = GuardedXyz(() => d.Origin, warnings, "spot_origin_unreadable");
                row["spot"] = origin == null
                    ? (JToken)JValue.CreateNull()
                    : new JObject { ["origin"] = PointJson(origin, scale) };
            }
            else row["spot"] = JValue.CreateNull();

            row["warnings"] = warnings;
            return row;
        }

        private static string Shape(Dimension d, JArray warnings)
        {
            string shapeName = Guarded(() => d.DimensionShape.ToString(), warnings, "shape_unreadable");
            string styleName = Guarded(() =>
            {
                DimensionType t = d.DimensionType;
                return t == null ? null : t.StyleType.ToString();
            }, warnings, "style_type_unreadable");
            string shape = DimensionEditRules.ClassifyShape(shapeName, styleName);
            if (shape == null)
                warnings.Add(Warn("shape_unclassified",
                    "DimensionShape '" + (shapeName ?? "(unreadable)") + "' with StyleType '" +
                    (styleName ?? "(unreadable)") + "' is not a combination this bridge classifies."));
            return shape;
        }

        private static JToken OwnerView(Document host, Dimension d, JArray warnings)
        {
            try
            {
                ElementId viewId = d.OwnerViewId;
                if (viewId == null || viewId == ElementId.InvalidElementId) return JValue.CreateNull();
                View view = host.GetElement(viewId) as View;
                if (view == null)
                {
                    warnings.Add(Warn("owner_view_missing",
                        "OwnerViewId " + Rid.Value(viewId) + " does not resolve to a view."));
                    return new JObject
                    {
                        ["id"] = Rid.Value(viewId),
                        ["unique_id"] = JValue.CreateNull(),
                        ["name"] = JValue.CreateNull()
                    };
                }
                return new JObject
                {
                    ["id"] = Rid.Value(view.Id),
                    ["unique_id"] = Guarded(() => view.UniqueId, warnings, "owner_view_unreadable"),
                    ["name"] = Guarded(() => view.Name, warnings, "owner_view_unreadable")
                };
            }
            catch (Exception ex)
            {
                warnings.Add(Warn("owner_view_unreadable", ex.Message));
                return JValue.CreateNull();
            }
        }

        private static JToken TypeBlock(Document host, Dimension d, JArray warnings)
        {
            try
            {
                DimensionType t = d.DimensionType;
                if (t == null)
                {
                    warnings.Add(Warn("type_missing", "the dimension reports no DimensionType"));
                    return JValue.CreateNull();
                }
                return new JObject
                {
                    ["id"] = Rid.Value(t.Id),
                    ["unique_id"] = Guarded(() => t.UniqueId, warnings, "type_unreadable"),
                    ["name"] = Guarded(() => t.Name, warnings, "type_unreadable"),
                    ["style_type"] = Guarded(() => t.StyleType.ToString(), warnings, "type_unreadable")
                };
            }
            catch (Exception ex)
            {
                warnings.Add(Warn("type_unreadable", ex.Message));
                return JValue.CreateNull();
            }
        }

        /// <summary>
        /// The curve, in display units. A LINE Revit hands back is often UNBOUND - an
        /// infinite line through the dimension line - and asking an unbound line for
        /// endpoints throws. So a bound line reports start/end, an unbound one reports
        /// origin/direction and says bound:false rather than inventing endpoints.
        /// </summary>
        private static JToken CurveJson(Dimension d, double scale, JArray warnings)
        {
            try
            {
                Curve c = d.Curve;
                if (c == null) return JValue.CreateNull();

                if (c is Line line)
                {
                    if (line.IsBound)
                        return new JObject
                        {
                            ["kind"] = "line", ["bound"] = true,
                            ["start"] = PointJson(line.GetEndPoint(0), scale),
                            ["end"] = PointJson(line.GetEndPoint(1), scale)
                        };
                    return new JObject
                    {
                        ["kind"] = "line", ["bound"] = false,
                        ["origin"] = PointJson(line.Origin, scale),
                        // A direction is unitless; scaling it would manufacture a length.
                        ["direction"] = PointJson(line.Direction, 1.0)
                    };
                }
                if (c is Arc arc)
                    return new JObject
                    {
                        ["kind"] = "arc",
                        ["center"] = PointJson(arc.Center, scale),
                        ["radius"] = arc.Radius * scale
                    };

                warnings.Add(Warn("curve_kind_unmapped",
                    "the dimension's curve is a " + c.GetType().Name + ", which this row does not decompose"));
                return new JObject { ["kind"] = c.GetType().Name.ToLowerInvariant() };
            }
            catch (Exception ex)
            {
                warnings.Add(Warn("curve_unreadable", ex.Message));
                return JValue.CreateNull();
            }
        }

        /// <summary>
        /// The references, in the order the dimension holds them. A reference into a
        /// LINK is marked linked:true with its raw ids and resolved no further; it is
        /// also never counted broken, because "we did not look inside the link" is not
        /// "the element is gone".
        /// </summary>
        private static JArray References(Document host, Dimension d, JArray warnings, out int broken)
        {
            broken = 0;
            var result = new JArray();
            ReferenceArray refs;
            try { refs = d.References; }
            catch (Exception ex)
            {
                warnings.Add(Warn("references_unreadable", ex.Message));
                return result;
            }
            if (refs == null) return result;

            int index = 0;
            foreach (Reference r in refs)
            {
                var entry = new JObject { ["index"] = index };
                try
                {
                    string stable = null;
                    try { stable = r.ConvertToStableRepresentation(host); }
                    catch (Exception ex)
                    {
                        warnings.Add(Warn("reference_stable_representation_unreadable",
                            "reference " + index + ": " + ex.Message));
                    }
                    entry["stable_representation"] = stable;

                    bool linked = false;
                    try { linked = r.LinkedElementId != null && r.LinkedElementId != ElementId.InvalidElementId; }
                    catch (Exception ex)
                    {
                        warnings.Add(Warn("reference_link_state_unreadable", "reference " + index + ": " + ex.Message));
                    }
                    entry["linked"] = linked;
                    entry["element_id"] = Rid.Value(r.ElementId);

                    if (linked)
                    {
                        // The host-side id above is the RevitLinkInstance; the raw linked id
                        // is reported and NOT resolved - the link is another document.
                        entry["linked_element_id"] = Rid.Value(r.LinkedElementId);
                        entry["unique_id"] = JValue.CreateNull();
                        entry["category"] = JValue.CreateNull();
                        entry["element_exists"] = JValue.CreateNull();
                        entry["reference_resolvable"] = JValue.CreateNull();
                    }
                    else
                    {
                        Element e = r.ElementId == ElementId.InvalidElementId ? null : host.GetElement(r.ElementId);
                        entry["element_exists"] = e != null;
                        if (e == null) broken++;
                        entry["unique_id"] = e == null ? null : Guarded(() => e.UniqueId, warnings, "reference_element_unreadable");
                        entry["category"] = e == null ? null : Guarded(() => e.Category?.Name, warnings, "reference_element_unreadable");
                        if (stable == null) entry["reference_resolvable"] = JValue.CreateNull();
                        else
                        {
                            bool? resolvable = GuardedBool(
                                () => Reference.ParseFromStableRepresentation(host, stable) != null,
                                warnings, "reference_unresolvable");
                            entry["reference_resolvable"] = resolvable == null
                                ? (JToken)new JValue(false) : new JValue(resolvable.Value);
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add(Warn("reference_unreadable", "reference " + index + ": " + ex.Message));
                }
                result.Add(entry);
                index++;
            }
            return result;
        }

        private static JArray Segments(Dimension d, JArray warnings)
        {
            var result = new JArray();
            try
            {
                int index = 0;
                foreach (DimensionSegment s in d.Segments)
                {
                    DimensionSegment segment = s;   // foreach variable, captured per lambda below
                    var entry = new JObject { ["index"] = index };
                    double? value = GuardedNullableDouble(() => segment.Value, warnings, "segment_value_unreadable");
                    entry["value_internal_feet"] = value == null ? JValue.CreateNull() : new JValue(value.Value);
                    entry["value_presented"] = Guarded(() => segment.ValueString, warnings, "segment_value_presented_unreadable");
                    entry["prefix"] = Guarded(() => segment.Prefix, warnings, "segment_prefix_unreadable");
                    entry["suffix"] = Guarded(() => segment.Suffix, warnings, "segment_suffix_unreadable");
                    entry["above"] = Guarded(() => segment.Above, warnings, "segment_above_unreadable");
                    entry["below"] = Guarded(() => segment.Below, warnings, "segment_below_unreadable");
                    entry["value_override"] = Guarded(() => segment.ValueOverride, warnings, "segment_value_override_unreadable");
                    bool? locked = GuardedBool(() => segment.IsLocked, warnings, "segment_lock_unreadable");
                    entry["is_locked"] = locked == null ? JValue.CreateNull() : new JValue(locked.Value);
                    result.Add(entry);
                    index++;
                }
            }
            catch (Exception ex)
            {
                warnings.Add(Warn("segments_unreadable", ex.Message));
            }
            return result;
        }

        // ---------------------------------------------------------------------
        // Guarded reads: the failure becomes a coded warning, never a silence.
        // ---------------------------------------------------------------------

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

        private static double? GuardedNullableDouble(Func<double?> f, JArray warnings, string code)
        {
            try { return f(); }
            catch (Exception ex) { warnings.Add(Warn(code, ex.Message)); return null; }
        }

        private static XYZ GuardedXyz(Func<XYZ> f, JArray warnings, string code)
        {
            try { return f(); }
            catch (Exception ex) { warnings.Add(Warn(code, ex.Message)); return null; }
        }

        private static JArray PointJson(XYZ p, double scale)
            => new JArray(p.X * scale, p.Y * scale, p.Z * scale);

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
