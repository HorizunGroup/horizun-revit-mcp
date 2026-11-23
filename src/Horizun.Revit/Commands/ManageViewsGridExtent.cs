// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// horizun_manage_views operation=extend_grids_3d: makes EXISTING grids span every
// level of the model (Revit's "Maximize 3D Extents") - ONE action of the batch's
// single transaction. A grid created before kind=grid learned extent_3d, or drawn by
// someone else, is only visible in the plans its own vertical extent reaches; this
// is the typed way to fix it without deleting and recreating the grid.
//
//   * grid_ids (1..500) names the grids; omitted, every grid of the document. A grid
//     of a link is not this document's to change and is not a candidate.
//   * The rehearsal lists, per grid, the vertical extent it has now and the levels it
//     does not reach - the read half, bound into the plan so a model that changed
//     between rehearsal and apply refuses the token as stale.
//   * After the commit every grid's extent is re-read against the levels
//     (GridExtentRules.CoversLevels); a grid that still misses a level fails the action.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        private const int MaxGridExtentTargets = 500;
        private const double GridExtentToleranceFeet = 1e-3;

        internal static bool IsGridExtentOperation(string op) => string.Equals(op, "extend_grids_3d", StringComparison.OrdinalIgnoreCase);

        private static List<Grid> GridTargets(Document doc, JObject a)
        {
            if (a["grid_ids"] == null || a["grid_ids"].Type == JTokenType.Null)
                return new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().OrderBy(g => Rid.Value(g.Id)).ToList();
            if (!(a["grid_ids"] is JArray ids) || ids.Count == 0 || ids.Count > MaxGridExtentTargets)
                throw new ArgumentException("extend_grids_3d: grid_ids must hold 1.." + MaxGridExtentTargets + " grid ids (or be omitted for every grid).");
            var grids = new List<Grid>();
            foreach (JToken id in ids)
            {
                if (id.Type != JTokenType.Integer || !Rid.CanRepresent(id.Value<long>()))
                    throw new ArgumentException("extend_grids_3d: grid_ids must be integers.");
                if (!(doc.GetElement(Rid.Make(id.Value<long>())) is Grid g))
                    throw new ArgumentException("extend_grids_3d: element " + id + " is not a grid of this document.");
                if (grids.All(x => x.Id != g.Id)) grids.Add(g);
            }
            return grids;
        }

        private static List<double> LevelElevations(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Elevation).ToList();

        private static JObject GridExtentRow(Grid g, List<double> levels)
        {
            Outline o = g.GetExtents();
            bool covers = GridExtentRules.CoversLevels(o.MinimumPoint.Z, o.MaximumPoint.Z, levels, GridExtentToleranceFeet, out List<double> uncovered);
            JObject row = GridExtentRules.Report(GridExtentRules.Maximize, o.MinimumPoint.Z, o.MaximumPoint.Z, levels.Count, uncovered);
            row["grid_id"] = Rid.Value(g.Id);
            row["name"] = g.Name;
            row["covers_all_levels"] = covers;
            return row;
        }

        internal static void ValidateGridExtent(Document doc, JObject a, Dictionary<string, Type> known)
        {
            List<Grid> grids = GridTargets(doc, a);
            if (grids.Count == 0) throw new ArgumentException("extend_grids_3d: this document has no grids.");
            if (LevelElevations(doc).Count == 0) throw new ArgumentException("extend_grids_3d: this document has no levels to span.");
        }

        internal static JObject GridExtentPreview(Document doc, JObject a)
        {
            List<double> levels = LevelElevations(doc);
            List<JObject> rows = GridTargets(doc, a).Select(g => GridExtentRow(g, levels)).ToList();
            return new JObject
            {
                ["grids"] = rows.Count,
                ["already_covering"] = rows.Count(r => (bool)r["covers_all_levels"]),
                ["current"] = new JArray(rows)
            };
        }

        internal static void BindGridExtent(Document doc, JObject a, IDictionary<string, string> before)
        {
            List<double> levels = LevelElevations(doc);
            before["grid_extents.grids"] = string.Join(",", GridTargets(doc, a).Select(g => Rid.Value(g.Id).ToString(CultureInfo.InvariantCulture)));
            before["grid_extents.levels"] = levels.Count.ToString(CultureInfo.InvariantCulture);
        }

        internal static Element ApplyGridExtent(Document doc, JObject a)
        {
            List<Grid> grids = GridTargets(doc, a);
            if (grids.Count == 0) throw new InvalidOperationException("this document has no grids.");
            List<double> levels = LevelElevations(doc);
            var before = new JArray(grids.Select(g => (JToken)GridExtentRow(g, levels)));
            // SetVerticalExtents, not Maximize3DExtents: Maximize also stretches each grid along its
            // line, which moved the bubbles of every grid this operation touched (field cycle).
            if (!GridExtentRules.VerticalRange(levels, out double bottom, out double top))
                throw new InvalidOperationException("extend_grids_3d: this document has no levels to span.");
            foreach (Grid g in grids) g.SetVerticalExtents(bottom, top);
            a["__grid_extent"] = new JObject { ["grid_ids"] = new JArray(grids.Select(g => (JToken)Rid.Value(g.Id))), ["before"] = before };
            return grids[0];
        }

        internal static bool VerifyGridExtent(Document doc, JObject a, Element e)
        {
            if (!(a["__grid_extent"] is JObject detail) || !(e is Grid)) return false;
            List<double> levels = LevelElevations(doc);
            var reread = new JArray();
            bool ok = true;
            foreach (JToken id in (JArray)detail["grid_ids"])
            {
                Grid g = doc.GetElement(Rid.Make(id.Value<long>())) as Grid;
                if (g == null) { ok = false; reread.Add(new JObject { ["grid_id"] = id, ["error"] = "grid not found after the commit" }); continue; }
                JObject row = GridExtentRow(g, levels);
                if (!(bool)row["covers_all_levels"]) ok = false;
                reread.Add(row);
            }
            detail["reread"] = reread;
            detail["verified"] = ok;
            return ok;
        }

        internal static JObject GridExtentDetail(JObject a) => a["__grid_extent"] as JObject;
    }
}
