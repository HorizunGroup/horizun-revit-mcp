// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_audit_support (read-only, no transaction): elements in the air, beam ends
// on nothing, rooms without a floor under them, and a model shifted against its
// link. The judgements are Core/SupportAuditRules; this file reads boxes, faces and
// grids, from the host and (include_links) from every loaded link through its
// transform. What could not be read is listed in unreadable, never counted clean.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class AuditSupportCommand : ICommand
    {
        public string Name => "horizun_audit_support";
        public string Description => "Read-only: floating elements, beam ends on nothing, rooms without floor under them and a model shifted against its link.";

        private const double Mm = 1.0 / 304.8;

        private static readonly BuiltInCategory[] SupportCategories =
        {
            BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns, BuiltInCategory.OST_Ceilings
        };

        private static readonly BuiltInCategory[] BeamSupports =
        {
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralFoundation
        };

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject r;
            try { r = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, "Parameters must be a JSON object: " + ex.Message); }
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.FailCoded(ErrorCodes.RevitSinDocumento, "No active Revit document.");
            CommandResult guard = DocumentGate.ReadGuard(doc, r, Name);
            if (guard != null) return guard;
            string invalid = SupportAuditRules.Validate(r);
            if (invalid != null) return CommandResult.FailCoded(ErrorCodes.EsquemaInvalido, invalid);
            try
            {
                switch (r.Value<string>("mode"))
                {
                    case "floating": return CommandResult.Ok(Floating(doc, r));
                    case "slab_supports": return CommandResult.Ok(Floating(doc, r));
                    case "loose_beam_ends": return CommandResult.Ok(Beams(doc, r));
                    case "coverage_gaps": return CommandResult.Ok(Coverage(doc, r));
                    default: return CommandResult.Ok(Offset(doc, r));
                }
            }
            catch (ArgumentException ex) { return CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida, ex.Message + " Nothing was read."); }
        }

        // ---- shared readers ---------------------------------------------------------------

        private sealed class Boxed { public long Id; public string Source; public string Category; public double[] Box; }

        private static double[] BoxOf(Element e, Transform tf)
        {
            BoundingBoxXYZ b = null;
            try { b = e.get_BoundingBox(null); } catch { }
            if (b == null) return null;
            if (tf == null || tf.IsIdentity) return new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z };
            var pts = new List<XYZ>();
            foreach (double x in new[] { b.Min.X, b.Max.X }) foreach (double y in new[] { b.Min.Y, b.Max.Y }) foreach (double z in new[] { b.Min.Z, b.Max.Z }) pts.Add(tf.OfPoint(new XYZ(x, y, z)));
            return new[] { pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z), pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z) };
        }

        private static void SolidBoxes(GeometryElement geometry, List<double[]> boxes)
        {
            if (geometry == null) return;
            foreach (GeometryObject obj in geometry)
            {
                if (obj is GeometryInstance instance) { SolidBoxes(instance.GetInstanceGeometry(), boxes); continue; }
                if (!(obj is Solid solid) || solid.Volume <= 0) continue;
                BoundingBoxXYZ box = solid.GetBoundingBox();
                if (box == null) continue;
                var points = new List<XYZ>();
                foreach (double x in new[] { box.Min.X, box.Max.X }) foreach (double y in new[] { box.Min.Y, box.Max.Y }) foreach (double z in new[] { box.Min.Z, box.Max.Z })
                    points.Add(box.Transform.OfPoint(new XYZ(x, y, z)));
                boxes.Add(new[] { points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z), points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z) });
            }
        }

        private static bool HasSupportHost(Element e, Document doc)
        {
            if (!(e is FamilyInstance instance)) return false;
            Element host = instance.Host;
            return host != null && host.IsValidObject && doc.GetElement(host.Id) != null &&
                (host is Wall || host is Floor || host is RoofBase || host is Ceiling);
        }

        private static IEnumerable<KeyValuePair<Document, Transform>> Sources(Document doc, bool links)
        {
            yield return new KeyValuePair<Document, Transform>(doc, Transform.Identity);
            if (!links) yield break;
            foreach (RevitLinkInstance li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document ld = null;
                try { ld = li.GetLinkDocument(); } catch { }
                if (ld != null) yield return new KeyValuePair<Document, Transform>(ld, li.GetTotalTransform());
            }
        }

        private static List<Boxed> Collect(Document doc, bool links, IEnumerable<BuiltInCategory> cats, JArray unreadable)
        {
            var list = new List<Boxed>();
            foreach (var s in Sources(doc, links))
                foreach (BuiltInCategory c in cats)
                    foreach (Element e in new FilteredElementCollector(s.Key).OfCategory(c).WhereElementIsNotElementType())
                    {
                        double[] b = BoxOf(e, s.Value);
                        if (b == null) { if (unreadable.Count < SupportAuditRules.MaxRows) unreadable.Add(new JObject { ["element_id"] = Rid.Value(e.Id), ["source"] = s.Key.Title, ["reason"] = "no bounding box" }); continue; }
                        list.Add(new Boxed { Id = Rid.Value(e.Id), Source = s.Key == doc ? "host" : s.Key.Title, Category = e.Category?.Name, Box = b });
                    }
            return list;
        }

        private static bool OnLevel(Element e, JObject r) => r["level_id"] == null || Rid.Value(e.LevelId) == r.Value<long>("level_id");

        private static JObject Report(string mode, JArray findings, JArray unreadable, int examined, bool truncated, string method)
        {
            return new JObject
            {
                ["read_only"] = true, ["mode"] = mode, ["examined"] = examined, ["findings_total"] = findings.Count,
                ["findings"] = new JArray(findings.Take(SupportAuditRules.MaxRows)), ["findings_truncated"] = findings.Count > SupportAuditRules.MaxRows || truncated,
                ["unreadable"] = unreadable, ["coverage_complete"] = unreadable.Count == 0 && !truncated, ["method"] = method
            };
        }

        // ---- floating ---------------------------------------------------------------------

        private static JObject Floating(Document doc, JObject r)
        {
            bool structural = r.Value<string>("mode") == "slab_supports";
            double tol = (r.Value<double?>("tolerance_mm") ?? 50) * Mm, above = (r.Value<double?>("above_tolerance_mm") ?? 0) * Mm;
            bool links = r.Value<bool?>("include_links") ?? true;
            var unreadable = new JArray();
            if (structural && links)
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document linked = null;
                    try { linked = link.GetLinkDocument(); } catch { }
                    if (linked == null) unreadable.Add(new JObject { ["element_id"] = Rid.Value(link.Id), ["unique_id"] = link.UniqueId, ["reason"] = "link unavailable; supports in it could not be measured" });
                }
            var cats = new List<BuiltInCategory>();
            JArray requested = (JArray)r["categories"] ?? new JArray("OST_Floors", "OST_StructuralFraming", "OST_StructuralColumns", "OST_StructuralFoundation");
            foreach (JToken t in requested)
            {
                if (!Enum.TryParse((string)t, out BuiltInCategory bic) || Category.GetCategory(doc, bic) == null) throw new ArgumentException("'" + t + "' is not a category of this document.");
                cats.Add(bic);
            }
            List<Boxed> supports = Collect(doc, links, structural ? new[] { BuiltInCategory.OST_Floors, BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns } : SupportCategories, unreadable);
            var findings = new JArray();
            var supported = new JArray(); int supportedTotal = 0;
            void AddSupported(Element element, string via, string measuredBy, long? supportId = null)
            {
                supportedTotal++;
                if (supported.Count >= SupportAuditRules.MaxRows) return;
                var row = new JObject { ["element_id"] = Rid.Value(element.Id), ["unique_id"] = element.UniqueId,
                    ["category"] = element.Category?.Name, ["built_in_category"] = CategoryKey.Of(element), ["via"] = via };
                if (via == "hosted") row["host_id"] = Rid.Value(((FamilyInstance)element).Host.Id);
                if (via == "support") row["support_id"] = supportId;
                if (measuredBy != null) row["measured_by"] = measuredBy;
                supported.Add(row);
            }
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.ProjectElevation).ToList();
            double? lowestLevel = levels.Count == 0 ? (double?)null : levels.Min();
            int examined = 0, hosted = 0, onGround = 0, boxFallback = 0; bool truncated = false;
            foreach (BuiltInCategory c in cats)
                foreach (Element e in new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType())
                {
                    if (!OnLevel(e, r)) continue;
                    if (++examined > SupportAuditRules.MaxElements) { truncated = true; break; }
                    long id = Rid.Value(e.Id);
                    if (HasSupportHost(e, doc)) { hosted++; AddSupported(e, "hosted", null); continue; }
                    var solidBoxes = new List<double[]>();
                    try { SolidBoxes(e.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine }), solidBoxes); }
                    catch (Exception ex) { unreadable.Add(new JObject { ["element_id"] = id, ["unique_id"] = e.UniqueId, ["reason"] = "solid geometry could not be read: " + ex.Message }); continue; }
                    bool usedFallback;
                    double[] b = SupportAuditRules.ElementBox(solidBoxes, solidBoxes.Count == 0 ? BoxOf(e, null) : null, out usedFallback);
                    if (usedFallback) boxFallback++;
                    if (b == null) { unreadable.Add(new JObject { ["element_id"] = id, ["unique_id"] = e.UniqueId, ["measurement"] = null, ["reason"] = "no bounding box" }); continue; }
                    string measuredBy = usedFallback ? "bounding_box_fallback" : "solid_bounds";
                    if (SupportAuditRules.OnGround(e is Floor || c == BuiltInCategory.OST_StructuralFoundation, b[2], lowestLevel)) { onGround++; AddSupported(e, "on_ground", measuredBy); continue; }
                    IEnumerable<Boxed> others = supports.Where(s => !(s.Source == "host" && s.Id == id));
                    Boxed support = others.FirstOrDefault(s => SupportAuditRules.Supports(s.Box, b, tol, above));
                    if (support != null) { AddSupported(e, "support", measuredBy, support.Id); continue; }
                    double? gap = SupportAuditRules.GapBelow(others.Select(s => s.Box), b);
                    findings.Add(new JObject
                    {
                        ["element_id"] = id, ["category"] = e.Category?.Name, ["built_in_category"] = CategoryKey.Of(e), ["bottom_z_mm"] = Math.Round(b[2] / Mm, 1),
                        ["unique_id"] = e.UniqueId, ["level_id"] = Rid.Value(e.LevelId), ["level"] = (doc.GetElement(e.LevelId) as Level)?.Name,
                        ["reason"] = "no qualifying support within tolerance below the element",
                        ["method"] = usedFallback ? "bounding_box_fallback" : "solid_bounds",
                        ["gap_below_mm"] = gap.HasValue ? (JToken)Math.Round(gap.Value / Mm, 1) : null,
                        ["note"] = gap.HasValue ? null : "nothing under it in plan"
                    });
                }
            JObject report = Report(structural ? "slab_supports" : "floating", findings, unreadable, Math.Min(examined, SupportAuditRules.MaxElements), truncated,
                "hosted: existing wall, floor, roof or ceiling host; on_ground: floor or structural foundation bottom at or below the lowest model level, assumed ground, not measured terrain; otherwise positive-volume solid bounds, or bounding_box_fallback when none exist, against support boxes; box overlap can hide a real gap");
            report["supported"] = supported;
            report["supported_truncated"] = supportedTotal > SupportAuditRules.MaxRows;
            report["method_counts"] = new JObject { ["hosted"] = hosted, ["on_ground"] = onGround, ["bounding_box_fallback"] = boxFallback };
            return report;
        }

        // ---- loose_beam_ends -------------------------------------------------------------

        private static JObject Beams(Document doc, JObject r)
        {
            double tol = (r.Value<double?>("tolerance_mm") ?? 50) * Mm;
            var unreadable = new JArray();
            List<Boxed> supports = Collect(doc, r.Value<bool?>("include_links") ?? true, BeamSupports, unreadable);
            var findings = new JArray();
            int examined = 0;
            foreach (FamilyInstance b in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                if (!OnLevel(b, r) && !(r["level_id"] != null && b.Host != null && Rid.Value(b.Host.Id) == r.Value<long>("level_id"))) continue;
                examined++;
                if (!(b.Location is LocationCurve lc)) { unreadable.Add(new JObject { ["element_id"] = Rid.Value(b.Id), ["reason"] = "no location curve" }); continue; }
                long id = Rid.Value(b.Id);
                for (int end = 0; end < 2; end++)
                {
                    XYZ p = lc.Curve.GetEndPoint(end);
                    double[] q = { p.X, p.Y, p.Z };
                    if (supports.Any(s => !(s.Source == "host" && s.Id == id) && SupportAuditRules.Contains(s.Box, q, tol))) continue;
                    findings.Add(new JObject { ["element_id"] = id, ["end"] = end, ["point_mm"] = new JArray(Math.Round(p.X / Mm, 1), Math.Round(p.Y / Mm, 1), Math.Round(p.Z / Mm, 1)) });
                }
            }
            return Report("loose_beam_ends", findings, unreadable, examined, false,
                "box-level: an end inside a column, wall, beam or foundation box (expanded by tolerance) counts as supported");
        }

        // ---- coverage_gaps ------------------------------------------------------------------

        private static JObject Coverage(Document doc, JObject r)
        {
            double grid = (r.Value<double?>("grid_mm") ?? 500) * Mm;
            bool links = r.Value<bool?>("include_links") ?? true;
            var unreadable = new JArray();
            List<Room> rooms = r["room_ids"] is JArray ids
                ? ids.Select(t => doc.GetElement(Rid.Make(t.Value<long>())) as Room ?? throw new ArgumentException("room_ids: " + t + " is not a room.")).ToList()
                : new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
                    .Where(x => Rid.Value(x.LevelId) == r.Value<long>("level_id") && x.Area > 0).ToList();
            // floor top faces near each room's level, with the transform into their document
            var floors = new List<Tuple<double[], Face, Transform>>();
            foreach (var s in Sources(doc, links))
                foreach (Floor f in new FilteredElementCollector(s.Key).OfClass(typeof(Floor)).Cast<Floor>())
                {
                    double[] b = BoxOf(f, s.Value);
                    if (b == null) continue;
                    IList<Reference> tops;
                    try { tops = HostObjectUtils.GetTopFaces(f); } catch { continue; }
                    foreach (Reference rf in tops)
                    {
                        Face face = null;
                        try { face = f.GetGeometryObjectFromReference(rf) as Face; } catch { }
                        if (face != null) floors.Add(Tuple.Create(b, face, s.Value.Inverse));
                    }
                }
            var findings = new JArray();
            foreach (Room room in rooms)
            {
                BoundingBoxXYZ rb = room.get_BoundingBox(null);
                double z = (doc.GetElement(room.LevelId) as Level)?.Elevation ?? 0;
                if (rb == null) { unreadable.Add(new JObject { ["room_id"] = Rid.Value(room.Id), ["reason"] = "no bounding box" }); continue; }
                int samples = 0, uncovered = 0;
                for (double x = rb.Min.X + grid / 2; x < rb.Max.X; x += grid)
                    for (double y = rb.Min.Y + grid / 2; y < rb.Max.Y; y += grid)
                    {
                        var p = new XYZ(x, y, z + 300 * Mm);
                        if (!room.IsPointInRoom(p)) continue;
                        samples++;
                        bool covered = false;
                        foreach (var fl in floors)
                        {
                            double[] b = fl.Item1;
                            if (x < b[0] || x > b[3] || y < b[1] || y > b[4] || b[5] < z - 500 * Mm || b[5] > z + 500 * Mm) continue;
                            XYZ q = fl.Item3.OfPoint(new XYZ(x, y, z));
                            IntersectionResult pr = null;
                            try { pr = fl.Item2.Project(q); } catch { }
                            if (pr != null && Math.Abs(pr.XYZPoint.X - q.X) < Mm && Math.Abs(pr.XYZPoint.Y - q.Y) < Mm) { covered = true; break; }
                        }
                        if (!covered) uncovered++;
                    }
                if (samples == 0) { unreadable.Add(new JObject { ["room_id"] = Rid.Value(room.Id), ["reason"] = "no sample point fell inside the room at grid_mm; use a finer grid" }); continue; }
                if (uncovered == 0) continue;
                double fraction = (double)uncovered / samples;
                findings.Add(new JObject
                {
                    ["room_id"] = Rid.Value(room.Id), ["room"] = room.Name, ["samples"] = samples, ["uncovered"] = uncovered,
                    ["uncovered_fraction"] = Math.Round(fraction, 3), ["uncovered_area_m2"] = Math.Round(fraction * room.Area * 0.09290304, 2)
                });
            }
            return Report("coverage_gaps", findings, unreadable, rooms.Count, false,
                "sampled: grid points inside the room tested against floor top faces (host and links) within 500 mm of the room's level");
        }

        // ---- model_offset -------------------------------------------------------------------

        private static JObject Offset(Document doc, JObject r)
        {
            double tol = (r.Value<double?>("tolerance_mm") ?? 10) * Mm;
            var link = doc.GetElement(Rid.Make(r.Value<long>("link_instance_id"))) as RevitLinkInstance ?? throw new ArgumentException("link_instance_id is not a Revit link instance.");
            Document ld = link.GetLinkDocument() ?? throw new ArgumentException("the link is not loaded.");
            Transform tf = link.GetTotalTransform();
            var host = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve is Line).ToDictionary(g => g.Name, g => (Line)g.Curve);
            var rows = new JArray();
            var unreadable = new JArray();
            var normals = new List<double[]>(); var offsets = new List<double>(); var angles = new List<double>(); var abs = new List<double>();
            foreach (Grid g in new FilteredElementCollector(ld).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (!(g.Curve is Line ll)) continue;
                if (!host.TryGetValue(g.Name, out Line hl)) { unreadable.Add(new JObject { ["grid"] = g.Name, ["reason"] = "no host grid of that name" }); continue; }
                XYZ a = tf.OfPoint(ll.GetEndPoint(0)), b = tf.OfPoint(ll.GetEndPoint(1));
                XYZ hd = new XYZ(hl.Direction.X, hl.Direction.Y, 0).Normalize(), ld2 = new XYZ(b.X - a.X, b.Y - a.Y, 0).Normalize();
                XYZ n = new XYZ(-hd.Y, hd.X, 0);
                double angle = Math.Asin(Math.Max(-1, Math.Min(1, hd.X * ld2.Y - hd.Y * ld2.X))) * 180 / Math.PI;
                XYZ mid = (a + b) / 2;
                double off = (mid - hl.GetEndPoint(0)).DotProduct(n);
                normals.Add(new[] { n.X, n.Y }); offsets.Add(off); angles.Add(angle); abs.Add(Math.Abs(off));
                rows.Add(new JObject { ["grid"] = g.Name, ["offset_mm"] = Math.Round(off / Mm, 1), ["rotation_deg"] = Math.Round(angle, 3) });
            }
            double[] t = normals.Count > 0 ? SupportAuditRules.Translation(normals, offsets) : null;
            double median = abs.Count > 0 ? SupportAuditRules.Percentile(abs, 0.5) : double.NaN, p95 = abs.Count > 0 ? SupportAuditRules.Percentile(abs, 0.95) : double.NaN;
            return new JObject
            {
                ["read_only"] = true, ["mode"] = "model_offset", ["link"] = ld.Title, ["grids_compared"] = rows.Count, ["grids"] = rows,
                ["median_offset_mm"] = double.IsNaN(median) ? null : (JToken)Math.Round(median / Mm, 1),
                ["p95_offset_mm"] = double.IsNaN(p95) ? null : (JToken)Math.Round(p95 / Mm, 1),
                ["rotation_median_deg"] = angles.Count > 0 ? (JToken)Math.Round(SupportAuditRules.Percentile(angles, 0.5), 3) : null,
                ["translation_mm"] = t == null ? null : (JToken)new JArray(Math.Round(t[0] / Mm, 1), Math.Round(t[1] / Mm, 1)),
                ["translation_note"] = t == null ? "the grids compared are all parallel: only the offset across them is measurable" : "move the LINK by minus this vector to align it (least squares over the grids)",
                ["aligned"] = rows.Count > 0 ? (JToken)(p95 <= tol) : null,
                ["unreadable"] = unreadable, ["coverage_complete"] = unreadable.Count == 0 && rows.Count > 0,
                ["method"] = "same-named straight grids of host and link (through the link transform), offset of the link grid's midpoint from the host grid line"
            };
        }
    }
}
