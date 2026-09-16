// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// IS THIS SYMBOL THE SAME SYMBOL IN A MIRROR?
//
// Nine of the twenty-one symbols in one apartment are inserted with a negative
// X scale. For a duplex receptacle that is a draughtsman's convenience: the mark
// is symmetric, and its reflection is the same mark. For a switch with a pilot
// light on one side, or a handed appliance, it is not - the device is reversed,
// and building it unreversed puts the handle on the wrong side of a door.
//
// Nothing about the FAMILY can settle this, and neither can the family's name:
// "Duplex Receptacle" sounds symmetric and a manufacturer's version of it need
// not be. What CAN settle it is the drawing, which contains the symbol's own
// geometry - so the question is answered by measuring it.
//
// THE MEASUREMENT. Reflect every point of the block definition about the
// definition's own Y axis - the axis a negative X scale reflects about - and ask
// whether the reflected set is the same set. Same within a tolerance means the
// mirror changes nothing that was drawn; a residual means it changes something,
// and the residual is reported in millimetres so a reviewer can see how much.
//
// WHAT IT REFUSES TO DO. It does not decide what to do about an asymmetric
// symbol: that is the requirement set's policy. It does not claim symmetry for
// a block whose definition this reading could not see - unknown is its own
// answer, and it is not "yes".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What the mirror does to one block's own geometry.</summary>
    public sealed class CadSymmetryReading
    {
        /// <summary>true, false, or null when the definition was not readable.</summary>
        public bool? Symmetric;

        /// <summary>The worst distance, in mm, between a reflected point and its nearest partner.</summary>
        public double ResidualMm;

        public int PointsCompared;
        public double ToleranceMm;
        public string Why;

        public JObject ToJson() => new JObject
        {
            ["symmetric"] = Symmetric.HasValue ? (JToken)Symmetric.Value : JValue.CreateNull(),
            ["residual_mm"] = Math.Round(ResidualMm, 2),
            ["points_compared"] = PointsCompared,
            ["tolerance_mm"] = ToleranceMm,
            ["why"] = Why
        };
    }

    public static class CadBlockSymmetry
    {
        /// <summary>
        /// Measure one block definition against its own reflection.
        ///
        /// <paramref name="definition"/> is every entity the reading found INSIDE
        /// this block, in the definition's own coordinates. The axis is the
        /// definition's Y axis through its origin, because that is what a
        /// negative X scale reflects about.
        /// </summary>
        public static CadSymmetryReading Measure(IEnumerable<CadIrEntity> definition, double toleranceMm)
        {
            var reading = new CadSymmetryReading { ToleranceMm = toleranceMm };
            var points = new List<CadPoint>();
            foreach (CadIrEntity e in definition ?? Enumerable.Empty<CadIrEntity>())
            {
                if (e == null) continue;
                foreach (CadPoint p in e.Points) points.Add(p);
                if (e.Arc != null) { points.Add(e.Arc.Start); points.Add(e.Arc.Middle); points.Add(e.Arc.End); }
            }

            reading.PointsCompared = points.Count;
            if (points.Count == 0)
            {
                reading.Symmetric = null;
                reading.Why = "this reading holds no geometry for the block definition, so whether its mirror " +
                              "is the same mark is UNKNOWN - which is not the same as symmetric.";
                return reading;
            }

            // A point cloud compared against its reflection. Every reflected point
            // must find a partner; the worst gap is the residual.
            double worst = 0;
            foreach (CadPoint p in points)
            {
                var mirrored = new CadPoint(-p.X, p.Y, p.Z);
                double nearest = double.MaxValue;
                foreach (CadPoint q in points)
                {
                    double d = mirrored.DistanceTo(q);
                    if (d < nearest) nearest = d;
                    if (nearest <= toleranceMm) break;
                }
                if (nearest > worst) worst = nearest;
                if (worst > 1e9) break;
            }

            reading.ResidualMm = worst;
            reading.Symmetric = worst <= toleranceMm;
            reading.Why = reading.Symmetric.Value
                ? "every point of this block's own geometry has a partner in its reflection, within " +
                  toleranceMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm. The mirror in the drawing " +
                  "changes nothing that was drawn."
                : "reflecting this block leaves a point " + worst.ToString("0.#", CultureInfo.InvariantCulture) +
                  " mm from anything drawn, which is more than the " +
                  toleranceMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm allowed. The mirror is part " +
                  "of what this symbol says.";
            return reading;
        }

        /// <summary>
        /// Measure every block definition a reading contains, by name.
        ///
        /// The entities of a definition are the ones whose nesting path ENDS at
        /// it: an entity two blocks deep belongs to the inner one.
        /// </summary>
        public static Dictionary<string, CadSymmetryReading> MeasureAll(IEnumerable<CadIrEntity> entities,
                                                                        double toleranceMm)
        {
            var byDefinition = new Dictionary<string, List<CadIrEntity>>(StringComparer.OrdinalIgnoreCase);
            foreach (CadIrEntity e in entities ?? Enumerable.Empty<CadIrEntity>())
            {
                if (e == null || e.BlockPath == null || e.BlockPath.Count == 0) continue;
                string owner = e.BlockPath[e.BlockPath.Count - 1];
                if (string.IsNullOrWhiteSpace(owner)) continue;
                List<CadIrEntity> bucket;
                if (!byDefinition.TryGetValue(owner, out bucket)) byDefinition[owner] = bucket = new List<CadIrEntity>();
                bucket.Add(e);
            }

            var result = new Dictionary<string, CadSymmetryReading>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in byDefinition) result[kv.Key] = Measure(kv.Value, toleranceMm);
            return result;
        }
    }
}
