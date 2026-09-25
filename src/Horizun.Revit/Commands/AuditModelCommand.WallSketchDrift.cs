// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_audit_model - wall_sketch_drift: walls whose edited elevation
// profile (Wall.SketchId) was left behind by a move. The geometry read is
// Core/WallSketchGeometry.cs, shared with horizun_transform_elements
// realign_wall_sketch so a correction never disagrees with the finding that
// proposed it; the decision itself is Core/WallSketchDriftRules.cs.
//
// Read-only, like the rest of this command: no transaction, no SketchEditScope
// is opened here.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public partial class AuditModelCommand
    {
        private static JObject WallSketchDrift(Document doc, int top, Dictionary<long, WallSketchDriftResult> stranded)
        {
            List<KeyValuePair<long, WallSketchDriftResult>> hits =
                stranded.Where(kv => kv.Value.Stranded)
                        .OrderByDescending(kv => kv.Value.PerpendicularOffsetMm + kv.Value.InPlaneOffsetMm)
                        .ToList();

            var items = new JArray(hits.Take(top).Select(kv => (JToken)new JObject
            {
                ["element_id"] = kv.Key,
                ["perpendicular_offset_mm"] = System.Math.Round(kv.Value.PerpendicularOffsetMm, 1),
                ["in_plane_offset_mm"] = System.Math.Round(kv.Value.InPlaneOffsetMm, 1),
                ["correctable"] = kv.Value.Correctable,
                ["reason"] = kv.Value.Reason
            }));

            string summary = hits.Count == 0
                ? "No wall carries an edited profile whose sketch has drifted from its current location line."
                : hits.Count + " wall(s) with an edited profile (Wall.SketchId) whose sketch no longer matches " +
                  "the wall's current location line. " + WallSketchDriftRules.StrandedMeans + " " +
                  hits.Count(kv => !kv.Value.Correctable) + " of them moved OFF their sketch's own plane and " +
                  "cannot be corrected by translation; " + hits.Count(kv => kv.Value.Correctable) +
                  " can be, via horizun_transform_elements realign_wall_sketch.";

            return Finding(AuditCheckNames.WallSketchDrift, hits.Count > 0, hits.Count, summary, items, hits.Count);
        }
    }
}
