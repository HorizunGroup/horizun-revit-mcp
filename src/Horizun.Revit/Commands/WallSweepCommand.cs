// -----------------------------------------------------------------------------
// Horizun Revit MCP - wall sweeps and reveals: a type from a profile, placed on walls.
//
// INFORME_P6 6.7: nothing typed created a wall sweep, so a baseboard had no
// instance anyone could check. Three operations, all through ModelEditRunner
// (rehearse, single-use token, re-read after commit, roll back on disagreement):
//
//   read        the wall sweep and reveal types (profile, material) and, for a
//               wall, the sweeps on it. Read-only.
//   create_type duplicates an existing sweep/reveal TYPE and sets its profile and
//               material. WallSweep.Create takes profile and material from the
//               type and IGNORES those in WallSweepInfo (RevitAPI.xml 2023), so the
//               type is where a baseboard is defined.
//   place       one standalone sweep per wall (WallSweep.Create, 2023-2027
//               unchanged) at a distance from the wall's base or top, on the side
//               named. Re-read: host, type, distance, side, orientation, and the
//               body's vertical extent around the height asked.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class WallSweepCommand : ICommand
    {
        public string Name => "horizun_wall_sweep";
        public string Description => "Wall sweeps and reveals: read their types, create a type from a profile, and place one on walls at a height. Slab edges, fascias and gutters: read a floor's or roof's edges, place_edge on named edges. Re-read after commit.";

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: wall sweep", WallSweepRules.Validate, Plan);

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            string op = (r.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (op == "read") return r["host_id"] != null ? ReadEdges(doc, r, scale) : Read(doc, r, scale);
            if (op == "place_edge") return PlaceEdge(doc, r, scale);
            if (op == "create_type") return CreateType(doc, r);
            return Place(doc, r, scale);
        }

        /// <summary>Sweep or reveal, by the type's category; null for anything else.</summary>
        private static WallSweepType? KindOf(Element type)
        {
            ElementId cat = type?.Category?.Id;
            if (cat == null) return null;
            if (cat == new ElementId(BuiltInCategory.OST_Cornices)) return WallSweepType.Sweep;
            if (cat == new ElementId(BuiltInCategory.OST_Reveals)) return WallSweepType.Reveal;
            return null;
        }

        private static ElementType NeedSweepType(Document doc, JObject r, string field)
        {
            ElementType t = ModelEditRunner.Need<ElementType>(doc, r, field);
            if (KindOf(t) == null)
                throw new ArgumentException(field + " " + Rid.Value(t.Id) + " is a " + (t.Category?.Name ?? t.GetType().Name) +
                    " type, not a wall sweep or reveal type. operation=read lists them.");
            return t;
        }

        private static long? IdParam(Element e, BuiltInParameter bip)
        {
            Parameter p = e?.get_Parameter(bip);
            if (p == null || p.StorageType != StorageType.ElementId) return null;
            return Rid.Value(p.AsElementId());
        }

        private static JObject TypeRow(Document doc, ElementType t)
        {
            long? profile = IdParam(t, BuiltInParameter.WALL_SWEEP_PROFILE_PARAM);
            long? material = IdParam(t, BuiltInParameter.MATERIAL_ID_PARAM);
            Element profileElement = profile.HasValue && Rid.CanRepresent(profile.Value) ? doc.GetElement(Rid.Make(profile.Value)) : null;
            return new JObject
            {
                ["type_id"] = Rid.Value(t.Id), ["name"] = t.Name, ["kind"] = KindOf(t) == WallSweepType.Reveal ? "reveal" : "sweep",
                ["profile_id"] = profile, ["profile"] = profileElement is FamilySymbol fs ? fs.FamilyName + " : " + fs.Name : profileElement?.Name,
                ["material_id"] = material
            };
        }

        // ---- read -------------------------------------------------------------------------

        private static ArchModelEdit Read(Document doc, JObject r, double scale)
        {
            var types = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<Element>()
                .Where(e => KindOf(e) != null).Cast<ElementType>().OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            var profiles = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ProfileFamilies).WhereElementIsElementType()
                .OfType<FamilySymbol>().OrderBy(s => s.FamilyName + " : " + s.Name, StringComparer.Ordinal)
                .Select(s => new JObject { ["profile_id"] = Rid.Value(s.Id), ["name"] = s.FamilyName + " : " + s.Name }).ToList();
            var result = new JObject
            {
                ["read_only"] = true,
                ["types"] = new JArray(types.Select(t => TypeRow(doc, t))),
                ["profiles"] = new JArray(profiles),
                ["note"] = types.Count == 0
                    ? "This model has no wall sweep or reveal type to duplicate. Load or transfer one (Wall Sweeps / Reveals category) first; create_type needs a source."
                    : null
            };
            if (r["element_id"] != null)
            {
                Wall wall = ModelEditRunner.Need<Wall>(doc, r, "element_id");
                result["wall_id"] = Rid.Value(wall.Id);
                result["sweeps"] = new JArray(SweepsOn(doc, wall).Select(s => SweepRow(doc, s, scale)));
            }
            return new ArchModelEdit { ReadResult = result };
        }

        private static IEnumerable<WallSweep> SweepsOn(Document doc, Wall wall) =>
            new FilteredElementCollector(doc).OfClass(typeof(WallSweep)).Cast<WallSweep>()
                .Where(s => { try { return s.GetHostIds().Contains(wall.Id); } catch { return false; } });

        private static JObject SweepRow(Document doc, WallSweep s, double scale)
        {
            var row = new JObject { ["element_id"] = Rid.Value(s.Id), ["type_id"] = Rid.Value(s.GetTypeId()) };
            try
            {
                WallSweepInfo info = s.GetWallSweepInfo();
                row["kind"] = info.WallSweepType == WallSweepType.Reveal ? "reveal" : "sweep";
                row["distance"] = Math.Round(info.Distance / scale, 4);
                row["measured_from"] = info.DistanceMeasuredFrom == DistanceMeasuredFrom.Top ? "top" : "base";
                row["side"] = info.WallSide == WallSide.Interior ? "interior" : "exterior";
                row["vertical"] = info.IsVertical;
                row["fixed_by_wall_type"] = info.IsFixed;
                row["wall_offset"] = Math.Round(info.WallOffset / scale, 4);
            }
            catch (Exception ex) { row["info_error"] = ex.Message; }
            try { row["host_ids"] = new JArray(s.GetHostIds().Select(id => (JToken)Rid.Value(id))); } catch { }
            return row;
        }

        // ---- create_type ---------------------------------------------------------------------

        private static ArchModelEdit CreateType(Document doc, JObject r)
        {
            ElementType source = NeedSweepType(doc, r, "source_type_id");
            string name = r.Value<string>("new_name").Trim();
            FamilySymbol profile = ModelEditRunner.Need<FamilySymbol>(doc, r, "profile_id");
            if (profile.Category == null || profile.Category.Id != new ElementId(BuiltInCategory.OST_ProfileFamilies))
                throw new ArgumentException("profile_id " + Rid.Value(profile.Id) + " is a " + (profile.Category?.Name ?? "?") + " type, not a Profile family type. operation=read lists the loaded profiles.");
            Material material = r["material_id"] == null ? null : ModelEditRunner.Need<Material>(doc, r, "material_id");
            WallSweepType kind = KindOf(source).Value;
            bool taken = new FilteredElementCollector(doc).WhereElementIsElementType().Cast<Element>()
                .Any(e => KindOf(e) == kind && string.Equals(e.Name, name, StringComparison.Ordinal));
            if (taken) throw new ArgumentException("a " + (kind == WallSweepType.Reveal ? "reveal" : "wall sweep") + " type named '" + name + "' already exists: use it, or choose another new_name.");
            if (source.get_Parameter(BuiltInParameter.WALL_SWEEP_PROFILE_PARAM) == null)
                throw new ArgumentException("source_type_id " + Rid.Value(source.Id) + " exposes no Profile parameter (WALL_SWEEP_PROFILE_PARAM) to set.");
            if (material != null && source.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM) == null)
                throw new ArgumentException("source_type_id " + Rid.Value(source.Id) + " exposes no Material parameter (MATERIAL_ID_PARAM) to set.");

            var edit = new ArchModelEdit();
            edit.Planned.Add(ModelEditRunner.Planned(source, PlannedAction.Modify, r));
            edit.Summary["operation"] = "create_type";
            edit.Summary["source_type_id"] = Rid.Value(source.Id);
            edit.Summary["kind"] = kind == WallSweepType.Reveal ? "reveal" : "sweep";
            edit.Summary["new_name"] = name;
            edit.Summary["profile"] = profile.FamilyName + " : " + profile.Name;
            if (material != null) edit.Summary["material"] = material.Name;
            long created = -1;
            edit.Apply = d =>
            {
                ElementType copy = (ElementType)((ElementType)d.GetElement(source.Id)).Duplicate(name);
                created = Rid.Value(copy.Id);
                Parameter p = copy.get_Parameter(BuiltInParameter.WALL_SWEEP_PROFILE_PARAM);
                if (p == null || p.IsReadOnly) throw new InvalidOperationException("the new type's Profile parameter is read-only.");
                p.Set(profile.Id);
                if (material != null)
                {
                    Parameter m = copy.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                    if (m == null || m.IsReadOnly) throw new InvalidOperationException("the new type's Material parameter is read-only.");
                    m.Set(material.Id);
                }
            };
            edit.Verify = d =>
            {
                var req = new List<string> { "created", "name", "kind", "profile_id" };
                if (material != null) req.Add("material_id");
                var check = new PostconditionCheck(req.ToArray());
                ElementType t = created > 0 ? d.GetElement(Rid.Make(created)) as ElementType : null;
                check.Compare("created", 1, t == null ? 0 : 1);
                check.Record("name", name, t?.Name, t != null && string.Equals(t.Name, name, StringComparison.Ordinal));
                check.Record("kind", edit.Summary["kind"].ToString(), t == null ? null : (KindOf(t) == WallSweepType.Reveal ? "reveal" : KindOf(t) == WallSweepType.Sweep ? "sweep" : "other"), t != null && KindOf(t) == kind);
                long? readProfile = IdParam(t, BuiltInParameter.WALL_SWEEP_PROFILE_PARAM);
                check.Record("profile_id", Rid.Value(profile.Id), readProfile, readProfile == Rid.Value(profile.Id));
                if (material != null)
                {
                    long? readMaterial = IdParam(t, BuiltInParameter.MATERIAL_ID_PARAM);
                    check.Record("material_id", Rid.Value(material.Id), readMaterial, readMaterial == Rid.Value(material.Id));
                }
                if (t != null) edit.Evidence["type"] = TypeRow(d, t);
                return check;
            };
            return edit;
        }

        // ---- place -----------------------------------------------------------------------------

        private static ArchModelEdit Place(Document doc, JObject r, double scale)
        {
            ElementType type = NeedSweepType(doc, r, "type_id");
            WallSweepType kind = KindOf(type).Value;
            double distance = r.Value<double>("height") * scale;
            bool fromTop = r.Value<string>("measured_from") == "top";
            WallSide side = r.Value<string>("side") == "interior" ? WallSide.Interior : WallSide.Exterior;
            double? wallOffset = r["wall_offset"] == null ? (double?)null : r.Value<double>("wall_offset") * scale;
            var walls = new List<Wall>();
            foreach (JToken id in (JArray)r["wall_ids"])
            {
                long raw = id.Value<long>();
                Wall w = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) as Wall : null;
                if (w == null) throw new ArgumentException("wall_ids: " + raw + " is not a wall of this document.");
                if (!WallSweep.WallAllowsWallSweep(w))
                    throw new ArgumentException("wall " + raw + " cannot host a sweep or reveal (a curtain wall, or the main wall of a stacked wall).");
                walls.Add(w);
            }
            var edit = new ArchModelEdit();
            foreach (Wall w in walls) edit.Planned.Add(ModelEditRunner.Planned(w, PlannedAction.Modify, r));
            edit.Summary["operation"] = "place";
            edit.Summary["type"] = TypeRow(doc, type);
            edit.Summary["walls"] = walls.Count;
            edit.Summary["height"] = r["height"];
            edit.Summary["measured_from"] = fromTop ? "top" : "base";
            edit.Summary["side"] = side == WallSide.Interior ? "interior" : "exterior";
            var created = new Dictionary<long, long>();   // wall -> sweep
            var routes = new Dictionary<long, string>();   // wall -> how the placement was written
            edit.Apply = d =>
            {
                created.Clear();
                foreach (Wall w in walls)
                {
                    var info = new WallSweepInfo(kind, false)
                    {
                        Distance = distance,
                        DistanceMeasuredFrom = fromTop ? DistanceMeasuredFrom.Top : DistanceMeasuredFrom.Base,
                        WallSide = side
                    };
                    if (wallOffset.HasValue) info.WallOffset = wallOffset.Value;
                    WallSweep s = WallSweep.Create(w, type.Id, info);
                    if (s == null) throw new InvalidOperationException("Revit created no sweep on wall " + Rid.Value(w.Id) + " and gave no reason.");
                    created[Rid.Value(w.Id)] = Rid.Value(s.Id);
                    // RevitAPI.xml says Create takes the type's properties and "ignores" WallSweepInfo. Where the
                    // distance did not stick, the instance's own placement parameters are written; the re-read after
                    // commit judges either route, and the evidence names the one used.
                    d.Regenerate();
                    WallSweepInfo got = s.GetWallSweepInfo();
                    bool distanceHeld = Math.Abs(got.Distance - distance) <= 0.5 / 304.8;
                    bool offsetHeld = !wallOffset.HasValue || Math.Abs(got.WallOffset - wallOffset.Value) <= 0.5 / 304.8;
                    string route = "wall_sweep_info";
                    if (!distanceHeld)
                    {
                        // "Offset From Level" is measured from the sweep's own Level: the absolute height asked, minus that level.
                        double? target = TargetElevation(d, w, distance, fromTop);
                        Level sweepLevel = d.GetElement(s.get_Parameter(BuiltInParameter.WALL_SWEEP_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                        Parameter fromLevel = s.get_Parameter(BuiltInParameter.WALL_SWEEP_OFFSET_PARAM);
                        if (!target.HasValue || sweepLevel == null || fromLevel == null || fromLevel.IsReadOnly)
                            throw new InvalidOperationException("the sweep on wall " + Rid.Value(w.Id) + " did not take the distance and its Offset From Level cannot be written.");
                        fromLevel.Set(target.Value - sweepLevel.Elevation);
                        route = "offset_from_level";
                    }
                    if (!offsetHeld)
                    {
                        Parameter fromWall = s.get_Parameter(BuiltInParameter.WALL_SWEEP_WALL_OFFSET_PARAM);
                        if (fromWall == null || fromWall.IsReadOnly)
                            throw new InvalidOperationException("the sweep on wall " + Rid.Value(w.Id) + " did not take wall_offset and its Offset From Wall cannot be written.");
                        fromWall.Set(wallOffset.Value);
                        route += "+offset_from_wall";
                    }
                    routes[Rid.Value(w.Id)] = route;
                }
            };
            edit.Verify = d =>
            {
                var req = new List<string> { "created", "host", "type_id", "distance", "measured_from", "side", "horizontal" };
                if (wallOffset.HasValue) req.Add("wall_offset");
                if (kind == WallSweepType.Sweep) req.Add("body_at_height");
                var check = new PostconditionCheck(req.ToArray());
                var rows = new JArray();
                int present = 0, hosted = 0, typed = 0, atDistance = 0, fromOk = 0, sideOk = 0, horizontal = 0, offsetOk = 0, body = 0, bodyUnread = 0;
                double worstDistanceMm = 0;
                foreach (Wall w in walls)
                {
                    long wid = Rid.Value(w.Id);
                    WallSweep s = created.TryGetValue(wid, out long sid) ? d.GetElement(Rid.Make(sid)) as WallSweep : null;
                    var row = new JObject { ["wall_id"] = wid, ["sweep_id"] = s == null ? null : (JToken)Rid.Value(s.Id),
                        ["placement_written_by"] = routes.TryGetValue(wid, out string how) ? how : null };
                    rows.Add(row);
                    if (s == null) continue;
                    present++;
                    try { if (s.GetHostIds().Contains(w.Id)) hosted++; } catch { }
                    if (s.GetTypeId() == type.Id) typed++;
                    WallSweepInfo info = null;
                    try { info = s.GetWallSweepInfo(); } catch (Exception ex) { row["info_error"] = ex.Message; }
                    if (info != null)
                    {
                        double dmm = Math.Abs(info.Distance - distance) * 304.8;
                        worstDistanceMm = Math.Max(worstDistanceMm, dmm);
                        if (dmm <= 0.5) atDistance++;
                        if ((info.DistanceMeasuredFrom == DistanceMeasuredFrom.Top) == fromTop) fromOk++;
                        if (info.WallSide == side) sideOk++;
                        if (!info.IsVertical) horizontal++;
                        if (wallOffset.HasValue && Math.Abs(info.WallOffset - wallOffset.Value) * 304.8 <= 0.5) offsetOk++;
                        row["distance"] = Math.Round(info.Distance / scale, 4);
                    }
                    if (kind == WallSweepType.Sweep)
                    {
                        BoundingBoxXYZ box = s.get_BoundingBox(null);
                        double? target = TargetElevation(d, w, distance, fromTop);
                        if (box == null || !target.HasValue) { bodyUnread++; row["body"] = "unreadable"; }
                        else
                        {
                            row["body_z"] = new JArray(Math.Round(box.Min.Z / scale, 4), Math.Round(box.Max.Z / scale, 4));
                            row["target_z"] = Math.Round(target.Value / scale, 4);
                            if (WallSweepRules.BodyAtHeight(box.Min.Z, box.Max.Z, target.Value, 1.0 / 304.8)) body++;
                        }
                    }
                }
                int n = walls.Count;
                check.Compare("created", n, present);
                check.Compare("host", n, hosted);
                check.Compare("type_id", n, typed);
                check.Record("distance", n, atDistance, atDistance == n);
                check.Compare("measured_from", n, fromOk);
                check.Compare("side", n, sideOk);
                check.Compare("horizontal", n, horizontal);
                if (wallOffset.HasValue) check.Compare("wall_offset", n, offsetOk);
                if (kind == WallSweepType.Sweep)
                {
                    if (bodyUnread > 0) check.Unreadable("body_at_height", n, bodyUnread + " sweep(s) have no readable box or wall base elevation");
                    else check.Compare("body_at_height", n, body);
                }
                edit.Evidence["sweeps"] = rows;
                edit.Evidence["worst_distance_error_mm"] = Math.Round(worstDistanceMm, 3);
                return check;
            };
            return edit;
        }

        /// <summary>The absolute elevation the sweep's insertion point should be at: the wall's base (level + base offset) plus the distance, or its top minus it.</summary>
        private static double? TargetElevation(Document doc, Wall w, double distance, bool fromTop)
        {
            try
            {
                Level level = doc.GetElement(w.LevelId) as Level;
                double? baseOffset = w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble();
                if (level == null || !baseOffset.HasValue) return null;
                double bottom = level.Elevation + baseOffset.Value;
                if (!fromTop) return bottom + distance;
                double? height = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble();
                return height.HasValue ? bottom + height.Value - distance : (double?)null;
            }
            catch { return null; }
        }
    }
}
