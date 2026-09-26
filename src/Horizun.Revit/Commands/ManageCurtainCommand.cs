// -----------------------------------------------------------------------------
// Horizun Revit MCP - curtain grids: read, grid lines, mullions and panel types.
//
// A curtain wall's grid is three kinds of element that know about each other only
// through geometry: grid lines, the mullions that sit on their segments, and the
// panels in the cells between them. Every write here is verified the same way a
// person would check it - by re-reading the grid after the commit and measuring
// where the line landed, which segments carry a mullion of which type, and what
// type the panel now has.
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
    public sealed class ManageCurtainCommand : ICommand
    {
        public string Name => "horizun_manage_curtain";
        public string Description => "Read a curtain grid, add or remove grid lines, add or remove mullions and change panel types, verified from the committed grid.";

        private const int ListCap = 1000;

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: curtain grid", ArchitecturalEditRules.ValidateCurtain, Plan);

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            string op = r.Value<string>("operation").ToLowerInvariant();
            Element host = ModelEditRunner.Need<Element>(doc, r, "element_id");
            int gridIndex = r.Value<int?>("grid_index") ?? 0;
            CurtainGrid grid = GridOf(host, gridIndex);
            var edit = new ArchModelEdit();
            if (op == "read") { edit.ReadResult = Read(doc, host, grid, gridIndex, scale); return edit; }
            edit.Planned.Add(ModelEditRunner.Planned(host, PlannedAction.Modify, r));
            long hostId = Rid.Value(host.Id);
            switch (op)
            {
                case "add_grid_line": PlanAddLine(doc, r, scale, host, hostId, gridIndex, edit); break;
                case "remove_grid_line": PlanRemoveLine(doc, r, hostId, gridIndex, grid, edit); break;
                case "set_mullions": PlanMullions(doc, r, hostId, gridIndex, grid, edit); break;
                case "set_panel_type": PlanPanels(doc, r, scale, hostId, gridIndex, grid, edit); break;
            }
            edit.Summary["operation"] = op;
            edit.Summary["element_id"] = hostId;
            return edit;
        }

        private static CurtainGrid GridOf(Element host, int index)
        {
            if (host is Wall wall)
            {
                CurtainGrid g = wall.CurtainGrid;
                if (g == null) throw new ArgumentException("element_id " + Rid.Value(host.Id) + " is a wall without a curtain grid (not a curtain wall).");
                if (index != 0) throw new ArgumentException("a curtain wall has one grid; grid_index must be 0.");
                return g;
            }
            if (host is CurtainSystem system)
            {
                var grids = new List<CurtainGrid>();
                foreach (CurtainGrid g in system.CurtainGrids) grids.Add(g);
                if (index < 0 || index >= grids.Count) throw new ArgumentException("grid_index must be 0.." + (grids.Count - 1) + " for this curtain system.");
                return grids[index];
            }
            throw new UnsupportedCapability("element_id " + Rid.Value(host.Id) + " is a " + host.GetType().Name +
                "; horizun_manage_curtain edits curtain walls and curtain systems only.", FallbackSignal.ReasonUnsupportedKind);
        }

        /// <summary>The grid is re-found from the host on every read: a CurtainGrid object does not survive regeneration safely.</summary>
        private static CurtainGrid Fresh(Document doc, long hostId, int gridIndex) => GridOf(doc.GetElement(Rid.Make(hostId)), gridIndex);

        // ---- read ----------------------------------------------------------------------

        private static JObject Read(Document doc, Element host, CurtainGrid grid, int gridIndex, double scale)
        {
            var mullions = grid.GetMullionIds().Select(id => doc.GetElement(id) as Mullion).Where(m => m != null).ToList();
            JArray Lines(ICollection<ElementId> ids)
            {
                var a = new JArray();
                foreach (ElementId id in ids.Take(ListCap))
                {
                    var gl = doc.GetElement(id) as CurtainGridLine; if (gl == null) continue;
                    Curve full = gl.FullCurve;
                    var row = new JObject
                    {
                        ["id"] = Rid.Value(id), ["is_u"] = gl.IsUGridLine,
                        ["start"] = ModelEditRunner.Arr(full.GetEndPoint(0), scale), ["end"] = ModelEditRunner.Arr(full.GetEndPoint(1), scale),
                        ["segments_all"] = gl.AllSegmentCurves.Size, ["segments_existing"] = gl.ExistingSegmentCurves.Size,
                        ["mullions"] = mullions.Count(m => OnCurve(m, full)), ["locked"] = SafeBool(() => gl.Lock), ["pinned"] = SafeBool(() => gl.Pinned)
                    };
                    double? offset = WallOffset(host, gl);
                    if (offset.HasValue) row["offset"] = Math.Round(offset.Value / scale, 4);
                    a.Add(row);
                }
                return a;
            }
            var uIds = grid.GetUGridLineIds(); var vIds = grid.GetVGridLineIds(); var pIds = grid.GetPanelIds();
            var lineCurves = uIds.Concat(vIds).Select(id => doc.GetElement(id) as CurtainGridLine).Where(g => g != null).ToList();
            var mRows = new JArray();
            foreach (Mullion m in mullions.Take(ListCap))
            {
                CurtainGridLine on = lineCurves.FirstOrDefault(g => OnCurve(m, g.FullCurve));
                mRows.Add(new JObject { ["id"] = Rid.Value(m.Id), ["type_id"] = Rid.Value(m.GetTypeId()), ["type"] = TypeName(doc, m),
                    ["grid_line_id"] = on == null ? null : (JToken)Rid.Value(on.Id) });
            }
            var pRows = new JArray();
            foreach (ElementId id in pIds.Take(ListCap))
            {
                Element p = doc.GetElement(id); if (p == null) continue;
                var row = new JObject { ["id"] = Rid.Value(id), ["type_id"] = Rid.Value(p.GetTypeId()), ["type"] = TypeName(doc, p),
                    ["category"] = p.Category?.Name, ["is_door"] = IsCategory(p, BuiltInCategory.OST_Doors), ["pinned"] = SafeBool(() => p.Pinned) };
                BoundingBoxXYZ b = SafeBox(p);
                if (b != null) row["center"] = ModelEditRunner.Arr((b.Min + b.Max) / 2, scale);
                pRows.Add(row);
            }
            var result = new JObject
            {
                ["element_id"] = Rid.Value(host.Id), ["grid_index"] = gridIndex, ["host_kind"] = host.GetType().Name,
                ["u_lines"] = Lines(uIds), ["v_lines"] = Lines(vIds), ["mullions"] = mRows, ["panels"] = pRows,
                ["counts"] = new JObject { ["u_lines"] = uIds.Count, ["v_lines"] = vIds.Count, ["mullions"] = mullions.Count, ["panels"] = pIds.Count },
                ["truncated"] = uIds.Count > ListCap || vIds.Count > ListCap || mullions.Count > ListCap || pIds.Count > ListCap,
                ["offset_means"] = host is Wall ? "v line: distance along the wall's location line from its start; u line: height above the wall base" : "not computed for a curtain system"
            };
            if (host is Wall w && BaseZ(w).HasValue) result["base_z"] = Math.Round(BaseZ(w).Value / scale, 4);
            return result;
        }

        // ---- add a grid line -------------------------------------------------------------

        private static void PlanAddLine(Document doc, JObject r, double scale, Element host, long hostId, int gridIndex, ArchModelEdit edit)
        {
            bool isU = r.Value<string>("direction").ToLowerInvariant() == "u";
            XYZ position; double? offset = null;
            if (r["point"] != null) position = ModelEditRunner.Point(r["point"], scale, "point");
            else
            {
                var wall = host as Wall;
                if (wall == null) throw new ArgumentException("offset is measured on a wall; for a curtain system give point.");
                offset = r.Value<double>("offset") * scale;
                position = WallPosition(wall, isU, offset.Value);
            }
            var before = new HashSet<long>(LineIds(Fresh(doc, hostId, gridIndex), isU));
            long created = -1;
            edit.Summary["direction"] = isU ? "u" : "v";
            edit.Summary["position"] = ModelEditRunner.Arr(position, scale);
            edit.Apply = d =>
            {
                CurtainGridLine gl = Fresh(d, hostId, gridIndex).AddGridLine(isU, position, false);
                if (gl == null) throw new InvalidOperationException("Revit added no grid line at that position.");
                created = Rid.Value(gl.Id);
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("grid_line_created", "grid_line_position");
                var now = LineIds(Fresh(d, hostId, gridIndex), isU).Where(id => !before.Contains(id)).ToList();
                check.Compare("grid_line_created", 1, now.Count);
                var gl = now.Count == 1 ? d.GetElement(Rid.Make(now[0])) as CurtainGridLine : null;
                edit.Evidence["grid_line_id"] = now.Count == 1 ? (JToken)now[0] : JValue.CreateNull();
                if (gl == null) { check.Unreadable("grid_line_position", ModelEditRunner.Arr(position, scale), "no single new grid line to measure"); return check; }
                double? found = offset.HasValue ? WallOffset(d.GetElement(Rid.Make(hostId)), gl) : (double?)null;
                if (offset.HasValue && found.HasValue)
                    check.Measure("grid_line_position", offset.Value * 304.8, found.Value * 304.8, 0.5, "mm", "offset re-read from the committed line");
                else if (offset.HasValue)
                    check.Unreadable("grid_line_position", offset.Value * 304.8, "the offset could not be re-read on the wall");
                else
                    check.Measure("grid_line_position", 0, DistanceToLine(gl.FullCurve, position) * 304.8, 0.5, "mm", "distance from the requested point to the committed line");
                return check;
            };
        }

        // ---- remove a grid line ----------------------------------------------------------

        private static void PlanRemoveLine(Document doc, JObject r, long hostId, int gridIndex, CurtainGrid grid, ArchModelEdit edit)
        {
            long lineId = r.Value<long>("grid_line_id");
            CurtainGridLine gl = LineOf(doc, grid, lineId);
            bool isU = gl.IsUGridLine;
            int countBefore = LineIds(grid, isU).Count;
            List<Curve> segments = Segments(gl);
            var allSegments = Enumerable.Range(0, segments.Count).ToList();
            // Removing a grid line does not just delete it: Revit merges the cells on either side
            // of it into one, which can silently replace or discard whatever panel used to sit
            // there. The plan names both casualties explicitly - the mullions riding this line
            // (deleted along with it, since their segments disappear) and the panels the merge
            // touches - and refuses outright when a bordering panel is a DOOR, unless the caller
            // says accept_panel_merge=true.
            List<Mullion> mullionsOnLine = MullionsOn(doc, grid, segments, allSegments);
            List<long> adjacentPanelIds = AdjacentPanels(doc, grid, segments, allSegments);
            var doorPanels = adjacentPanelIds.Where(id => IsDoorPanel(doc.GetElement(Rid.Make(id)))).ToList();
            if (doorPanels.Count > 0 && r.Value<bool?>("accept_panel_merge") != true)
            {
                string named = string.Join(", ", doorPanels.Select(id =>
                    id + " (" + (TypeName(doc, doc.GetElement(Rid.Make(id))) ?? "unnamed type") + ")"));
                throw new ArgumentException("grid line " + lineId + " borders door panel(s) " + named +
                    "; removing this line merges its adjacent cells, which replaces or discards the door. Refused; " +
                    "pass accept_panel_merge=true to proceed anyway. Nothing was written.");
            }
            edit.Planned.Add(ModelEditRunner.Planned(gl, PlannedAction.Delete, r));
            edit.Summary["grid_line_id"] = lineId;
            edit.Summary["mullions_on_line"] = new JArray(mullionsOnLine.Select(m => Rid.Value(m.Id)));
            edit.Summary["adjacent_panels"] = new JArray(adjacentPanelIds);
            edit.Summary["adjacent_door_panels"] = new JArray(doorPanels);
            edit.Apply = d => d.Delete(Rid.Make(lineId));
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("grid_line_absent", "grid_line_count");
                check.Compare("grid_line_absent", true, d.GetElement(Rid.Make(lineId)) == null);
                check.Compare("grid_line_count", countBefore - 1, LineIds(Fresh(d, hostId, gridIndex), isU).Count);
                return check;
            };
        }

        /// <summary>The panels of this grid bordering any of the named segments - found the same
        /// way PlanPanels finds "the panel at this point": a tolerance-expanded bounding-box
        /// containment test, here against each segment's own midpoint (which lies exactly on the
        /// boundary between the cells the line separates).</summary>
        private static List<long> AdjacentPanels(Document doc, CurtainGrid grid, List<Curve> segments, List<int> targets)
        {
            var panelIds = grid.GetPanelIds().Select(Rid.Value).ToList();
            double tol = ArchitecturalEditRules.PositionToleranceFeet * 2;
            var found = new List<long>();
            foreach (int i in targets)
            {
                XYZ mid;
                try { mid = segments[i].Evaluate(0.5, true); } catch { continue; }
                foreach (long id in panelIds)
                {
                    if (found.Contains(id)) continue;
                    BoundingBoxXYZ b = SafeBox(doc.GetElement(Rid.Make(id)));
                    if (b == null) continue;
                    if (mid.X >= b.Min.X - tol && mid.Y >= b.Min.Y - tol && mid.Z >= b.Min.Z - tol &&
                        mid.X <= b.Max.X + tol && mid.Y <= b.Max.Y + tol && mid.Z <= b.Max.Z + tol)
                        found.Add(id);
                }
            }
            return found;
        }

        /// <summary>A panel that functions as a door: categorized OST_Doors, or a curtain panel
        /// whose FAMILY name marks it as a door style (e.g. a "Curtain Wall Door" family ships
        /// under OST_CurtainWallPanels, not OST_Doors, so the category alone under-reports these).</summary>
        private static bool IsDoorPanel(Element e)
        {
            if (e == null) return false;
            if (IsCategory(e, BuiltInCategory.OST_Doors)) return true;
            try
            {
                string familyName = (e as FamilyInstance)?.Symbol?.Family?.Name;
                return familyName != null && familyName.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // ---- mullions ----------------------------------------------------------------------

        private static void PlanMullions(Document doc, JObject r, long hostId, int gridIndex, CurtainGrid grid, ArchModelEdit edit)
        {
            long lineId = r.Value<long>("grid_line_id");
            CurtainGridLine gl = LineOf(doc, grid, lineId);
            bool add = r.Value<string>("mode").ToLowerInvariant() == "add";
            List<Curve> segments = Segments(gl);
            int? only = r.Value<int?>("segment_index");
            if (only.HasValue && (only < 0 || only >= segments.Count))
                throw new ArgumentException("segment_index must be 0.." + (segments.Count - 1) + " for grid line " + lineId + ".");
            var targets = only.HasValue ? new List<int> { only.Value } : Enumerable.Range(0, segments.Count).ToList();
            List<Mullion> existing = MullionsOn(doc, grid, segments, targets);
            MullionType type = null;
            if (add)
            {
                type = ModelEditRunner.Need<MullionType>(doc, r, "mullion_type_id");
                if (existing.Count > 0)
                    throw new ArgumentException("segment(s) of grid line " + lineId + " already carry mullion(s) " +
                        string.Join(", ", existing.Select(m => Rid.Value(m.Id))) + "; Revit leaves such a segment unchanged. " +
                        "Change their type with horizun_transform_elements change_type, or remove them first.");
            }
            else if (existing.Count == 0) throw new ArgumentException("no mullion sits on the targeted segment(s) of grid line " + lineId + "; nothing to remove.");
            else foreach (Mullion m in existing) edit.Planned.Add(ModelEditRunner.Planned(m, PlannedAction.Delete, r));
            var removed = existing.Select(m => Rid.Value(m.Id)).ToList();
            long typeId = type == null ? -1 : Rid.Value(type.Id);
            edit.Summary["grid_line_id"] = lineId; edit.Summary["mode"] = add ? "add" : "remove";
            edit.Summary["segments"] = new JArray(targets); edit.Summary["mullions_to_remove"] = new JArray(removed);
            edit.Apply = d =>
            {
                var line = (CurtainGridLine)d.GetElement(Rid.Make(lineId));
                if (add)
                {
                    var segs = Segments(line);
                    var mt = (MullionType)d.GetElement(Rid.Make(typeId));
                    if (only.HasValue) line.AddMullions(segs[only.Value], mt, true);
                    else foreach (Curve s in segs) line.AddMullions(s, mt, true);
                }
                else d.Delete(removed.Select(Rid.Make).ToList());
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck(add ? new[] { "segments_with_mullion", "mullion_type" } : new[] { "mullions_absent", "segments_without_mullion" });
                var line = d.GetElement(Rid.Make(lineId)) as CurtainGridLine;
                if (line == null) { foreach (string p in add ? new[] { "segments_with_mullion", "mullion_type" } : new[] { "mullions_absent", "segments_without_mullion" }) check.Unreadable(p, JValue.CreateNull(), "the grid line no longer exists"); return check; }
                var segs = Segments(line);
                var on = MullionsOn(d, Fresh(d, hostId, gridIndex), segs, targets);
                edit.Evidence["mullion_ids"] = new JArray(on.Select(m => Rid.Value(m.Id)));
                if (add)
                {
                    int covered = targets.Count(i => on.Any(m => OnCurve(m, segs[i])));
                    check.Compare("segments_with_mullion", targets.Count, covered);
                    check.Compare("mullion_type", typeId, on.Count > 0 && on.All(m => Rid.Value(m.GetTypeId()) == typeId) ? typeId : -1);
                }
                else
                {
                    check.Compare("mullions_absent", removed.Count, removed.Count(id => d.GetElement(Rid.Make(id)) == null));
                    check.Compare("segments_without_mullion", targets.Count, targets.Count(i => !on.Any(m => OnCurve(m, segs[i]))));
                }
                return check;
            };
        }

        // ---- panel type ----------------------------------------------------------------------

        private static void PlanPanels(Document doc, JObject r, double scale, long hostId, int gridIndex, CurtainGrid grid, ArchModelEdit edit)
        {
            var panelIds = new HashSet<long>(grid.GetPanelIds().Select(Rid.Value));
            var targets = new List<long>();
            if (r["panel_ids"] is JArray ids)
            {
                foreach (JToken t in ids)
                {
                    long id = t.Value<long>();
                    if (!panelIds.Contains(id)) throw new ArgumentException("panel " + id + " is not a panel of this curtain grid.");
                    if (!targets.Contains(id)) targets.Add(id);
                }
            }
            else
            {
                XYZ p = ModelEditRunner.Point(r["point"], scale, "point");
                double tol = ArchitecturalEditRules.PositionToleranceFeet * 2;
                var hits = panelIds.Where(id =>
                {
                    BoundingBoxXYZ b = SafeBox(doc.GetElement(Rid.Make(id)));
                    return b != null && p.X >= b.Min.X - tol && p.Y >= b.Min.Y - tol && p.Z >= b.Min.Z - tol &&
                           p.X <= b.Max.X + tol && p.Y <= b.Max.Y + tol && p.Z <= b.Max.Z + tol;
                }).ToList();
                if (hits.Count != 1)
                    throw new ArgumentException("point lies in " + hits.Count + " panel boxes (" + string.Join(", ", hits) + "); name the panel with panel_ids instead.");
                targets.Add(hits[0]);
            }
            if (targets.Count == 0) throw new ArgumentException("panel_ids names no panel.");
            ElementType type = ModelEditRunner.Need<ElementType>(doc, r, "type_id");
            long typeId = Rid.Value(type.Id);
            // Decided from what the type IS (ArchitecturalEditRules.CurtainPanelTypeRefusal),
            // not from Element.IsValidType: measured on Revit 2026, IsValidType refused a
            // type the Type Selector accepts for the same panel. Revit's own opinion is kept
            // as evidence, and ChangeTypeId plus the re-read still have the last word.
            string refusal = ArchitecturalEditRules.CurtainPanelTypeRefusal(TypeClass(type), WallKindOf(type), CategoryKey(type), IsCurtainPanelFamily(type));
            if (refusal != null)
                throw new ArgumentException("type " + typeId + " (" + DescribeType(type) + ") cannot replace a curtain panel: " + refusal);
            var revitSays = new JObject();
            foreach (long id in targets)
            {
                Element p = doc.GetElement(Rid.Make(id));
                bool? valid = null;
                try { valid = p.IsValidType(type.Id); } catch { }
                revitSays[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = valid.HasValue ? (JToken)valid.Value : JValue.CreateNull();
                // MEASURED 2026-09-24 on Revit 2026: where IsValidType said false, ChangeTypeId
                // then threw "The type typeId is not valid for this element". Revit's answer
                // for THIS panel decides; the refusal names the types it does accept.
                if (valid == false)
                    throw new ArgumentException("type " + typeId + " (" + DescribeType(type) + ") is not valid for panel " + id +
                        " in this Revit. Valid types for it: " + ValidTypesText(doc, p) + ".");
                edit.Planned.Add(ModelEditRunner.Planned(p, PlannedAction.Modify, r));
            }
            edit.Summary["type_kind"] = DescribeType(type);
            edit.Summary["revit_is_valid_type"] = revitSays;
            var now = new Dictionary<long, long>();
            edit.Summary["panel_ids"] = new JArray(targets); edit.Summary["type_id"] = typeId;
            edit.Apply = d =>
            {
                foreach (long id in targets)
                {
                    ElementId replaced = d.GetElement(Rid.Make(id)).ChangeTypeId(Rid.Make(typeId));
                    now[id] = replaced != null && replaced != ElementId.InvalidElementId ? Rid.Value(replaced) : id;
                }
            };
            edit.Verify = d =>
            {
                var check = new PostconditionCheck("panel_type", "in_grid");
                var inGrid = new HashSet<long>(Fresh(d, hostId, gridIndex).GetPanelIds().Select(Rid.Value));
                var rows = new JArray(); int typed = 0, present = 0;
                foreach (long id in targets)
                {
                    long cur = now.TryGetValue(id, out long x) ? x : id;
                    Element e = d.GetElement(Rid.Make(cur));
                    bool ok = e != null && Rid.Value(e.GetTypeId()) == typeId;
                    if (ok) typed++;
                    // MEASURED 2026-09-25 on Revit 2026: swapping a panel to a wall type gave a new
                    // element that GetPanelIds does not list; the grid can keep the original Panel,
                    // whose FindHostPanel names the wall displayed in its place. Either counts.
                    long hostPanel = -1;
                    if (cur != id && inGrid.Contains(id) && d.GetElement(Rid.Make(id)) is Panel orig)
                    {
                        try { hostPanel = Rid.Value(orig.FindHostPanel()); } catch { }
                    }
                    bool inPlace = inGrid.Contains(cur) || hostPanel == cur;
                    if (inPlace) present++;
                    rows.Add(new JObject { ["requested_id"] = id, ["element_id"] = cur, ["type_id"] = e == null ? null : (JToken)Rid.Value(e.GetTypeId()),
                        ["category"] = e?.Category?.Name, ["replaced"] = cur != id,
                        ["listed_by_grid"] = inGrid.Contains(cur), ["original_listed"] = inGrid.Contains(id), ["host_panel_of_original"] = hostPanel });
                }
                edit.Evidence["panels"] = rows;
                check.Compare("panel_type", targets.Count, typed);
                check.Compare("in_grid", targets.Count, present);
                return check;
            };
        }

        // ---- geometry ----------------------------------------------------------------------

        private static string ValidTypesText(Document doc, Element panel)
        {
            try
            {
                var ids = panel.GetValidTypes();
                var parts = new List<string>();
                foreach (ElementId vid in ids)
                {
                    if (parts.Count >= 15) { parts.Add("... " + (ids.Count - 15) + " more"); break; }
                    Element t = doc.GetElement(vid);
                    parts.Add(Rid.Value(vid) + " (" + (t == null ? "?" : t.Name) + ")");
                }
                return parts.Count == 0 ? "none reported" : string.Join(", ", parts);
            }
            catch (Exception ex) { return "could not be listed: " + ex.Message; }
        }

        private static List<long> LineIds(CurtainGrid g, bool isU) => (isU ? g.GetUGridLineIds() : g.GetVGridLineIds()).Select(Rid.Value).ToList();

        private static CurtainGridLine LineOf(Document doc, CurtainGrid grid, long id)
        {
            bool mine = grid.GetUGridLineIds().Concat(grid.GetVGridLineIds()).Any(x => Rid.Value(x) == id);
            var gl = doc.GetElement(Rid.Make(id)) as CurtainGridLine;
            if (!mine || gl == null) throw new ArgumentException("grid_line_id " + id + " is not a grid line of this curtain grid.");
            return gl;
        }

        private static List<Curve> Segments(CurtainGridLine gl)
        {
            var list = new List<Curve>();
            foreach (Curve c in gl.AllSegmentCurves) list.Add(c);
            return list;
        }

        private static List<Mullion> MullionsOn(Document doc, CurtainGrid grid, List<Curve> segments, List<int> targets) =>
            grid.GetMullionIds().Select(id => doc.GetElement(id) as Mullion)
                .Where(m => m != null && targets.Any(i => OnCurve(m, segments[i]))).ToList();

        /// <summary>A mullion lies on a curve when its location's midpoint and both ends are within 1 mm of it.</summary>
        private static bool OnCurve(Mullion m, Curve c)
        {
            try
            {
                Curve lc = m.LocationCurve;
                if (lc == null || c == null) return false;
                double tol = 1.0 / 304.8;
                return new[] { lc.Evaluate(0.5, true), lc.GetEndPoint(0), lc.GetEndPoint(1) }.All(p => c.Distance(p) <= tol);
            }
            catch { return false; }
        }

        private static double DistanceToLine(Curve c, XYZ p)
        {
            Curve unbound = c.Clone(); try { unbound.MakeUnbound(); } catch { unbound = c; }
            return unbound.Distance(p);
        }

        /// <summary>The wall's base elevation: its location line lies on the level, and the base offset moves it.</summary>
        private static double? BaseZ(Wall w)
        {
            try
            {
                var lc = (LocationCurve)w.Location;
                double offset = w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
                return lc.Curve.GetEndPoint(0).Z + offset;
            }
            catch { return null; }
        }

        private static XYZ WallPosition(Wall wall, bool isU, double offset)
        {
            Curve c = ((LocationCurve)wall.Location).Curve;
            double baseZ = BaseZ(wall) ?? c.GetEndPoint(0).Z;
            BoundingBoxXYZ b = wall.get_BoundingBox(null);
            double height = b == null ? 0 : b.Max.Z - baseZ;
            if (isU)
            {
                if (offset <= 0 || (height > 0 && offset >= height))
                    throw new ArgumentException("offset for a u line must lie inside the wall's height (0.." + Math.Round(height * 304.8) + " mm).");
                XYZ mid = c.Evaluate(0.5, true);
                return new XYZ(mid.X, mid.Y, baseZ + offset);
            }
            if (offset <= 0 || offset >= c.Length)
                throw new ArgumentException("offset for a v line must lie inside the wall's length (0.." + Math.Round(c.Length * 304.8) + " mm).");
            XYZ at = c.Evaluate(offset / c.Length, true);
            return new XYZ(at.X, at.Y, baseZ + height / 2);
        }

        /// <summary>Where a grid line sits on a wall, in feet: along the location line for v, above the base for u.</summary>
        private static double? WallOffset(Element host, CurtainGridLine gl)
        {
            var wall = host as Wall;
            if (wall == null) return null;
            try
            {
                Curve c = ((LocationCurve)wall.Location).Curve;
                XYZ mid = gl.FullCurve.Evaluate(0.5, true);
                if (gl.IsUGridLine) { double? b = BaseZ(wall); return b.HasValue ? mid.Z - b.Value : (double?)null; }
                XYZ flat = new XYZ(mid.X, mid.Y, c.GetEndPoint(0).Z);
                IntersectionResult ir = c.Project(flat);
                if (ir == null) return null;
                return c.ComputeNormalizedParameter(ir.Parameter) * c.Length;
            }
            catch { return null; }
        }

        // ---- what a candidate panel type is ------------------------------------------------

        private static string TypeClass(ElementType t)
        {
            if (t is WallType) return "WallType";
            if (t is PanelType) return "PanelType";
            if (t is FamilySymbol) return "FamilySymbol";
            return t?.GetType().Name;
        }

        private static string WallKindOf(ElementType t) { try { return (t as WallType)?.Kind.ToString(); } catch { return null; } }

        private static string CategoryKey(ElementType t)
        {
            try
            {
                if (t?.Category == null) return null;
                long raw = Rid.Value(t.Category.Id);
                return Enum.IsDefined(typeof(BuiltInCategory), (int)raw) ? ((BuiltInCategory)(int)raw).ToString() : null;
            }
            catch { return null; }
        }

        private static bool IsCurtainPanelFamily(ElementType t) { try { return (t as FamilySymbol)?.Family?.IsCurtainPanelFamily == true; } catch { return false; } }

        private static string DescribeType(ElementType t)
        {
            string family = null;
            try { family = t.FamilyName; } catch { }
            string kind = WallKindOf(t);
            return (string.IsNullOrEmpty(family) ? "" : family + ": ") + t.Name + " - " + TypeClass(t) +
                   (kind == null ? "" : " " + kind) + (CategoryKey(t) == null ? "" : ", " + CategoryKey(t));
        }

        private static string TypeName(Document doc, Element e) { try { return doc.GetElement(e.GetTypeId())?.Name; } catch { return null; } }
        private static bool IsCategory(Element e, BuiltInCategory c) { try { return e.Category != null && Rid.Value(e.Category.Id) == (long)c; } catch { return false; } }
        private static BoundingBoxXYZ SafeBox(Element e) { try { return e?.get_BoundingBox(null); } catch { return null; } }
        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
    }
}
