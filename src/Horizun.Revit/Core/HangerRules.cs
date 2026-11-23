// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF the "hangers" operation of horizun_mep_routing (Pro edition): where
// along a run a support station goes, given the run's own length, the end
// clearance the caller asked for, the maximum spacing, and the interior
// fittings (taps) a support must also stay clear of.
//
// The rule, stated once so a unit test can hold it exactly:
//  * The stations live on [end_offset, L - end_offset] (the end clearance).
//  * Each interior fitting f owns an EXCLUSION ZONE, the open interval
//    (f - end_offset, f + end_offset): no station inside it, one exactly on its
//    edge is fine. The zones cut the span into free SEGMENTS.
//  * Every free segment gets a station at each of its two edges, and as many
//    intermediate stations as needed so no gap inside it exceeds the spacing -
//    split EVENLY rather than packed from one side, so a segment that is not an
//    exact multiple of the spacing does not leave one short tail.
//  * So the end stations are subject to the fitting clearance too: an end
//    station that would fall inside a zone moves to the zone's far edge
//    (StartShift/EndShift say by how much).
//  * The gap ACROSS a zone is the zone's width, 2 x end_offset (more where zones
//    merge). When that is above the spacing it cannot be closed without breaking
//    the clearance, so it is REPORTED in WideGaps rather than hidden.
//
// A run shorter than 2 * end_offset has no station that can keep the clearance
// from BOTH ends at once, so it gets none (TooShort); a span entirely inside
// fitting clearances gets none either (NoFreeSpan). MepRoutingHangers.cs reports
// both rather than placing a support closer than asked.
//
// THE COUNT IS BOUNDED BEFORE ANYTHING IS ALLOCATED. The number of intervals is
// computed as a double and compared with the caller's budget before any cast or
// list growth: a spacing typed in the wrong unit (1.5 meaning metres) refuses
// arithmetically instead of casting millions of rays or wrapping an int.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class HangerStationPlan
    {
        /// <summary>Distances from the run's start, ascending.</summary>
        public List<double> Stations { get; } = new List<double>();

        /// <summary>[from, to] pairs of consecutive stations further apart than the spacing because a fitting's clearance zone lies between them.</summary>
        public List<double[]> WideGaps { get; } = new List<double[]>();

        /// <summary>The run is shorter than 2 x end_offset.</summary>
        public bool TooShort { get; set; }

        /// <summary>Every point that keeps the end clearance is inside a fitting's clearance.</summary>
        public bool NoFreeSpan { get; set; }

        /// <summary>More stations than the budget would be needed; Stations is left empty.</summary>
        public bool OverBudget { get; set; }

        /// <summary>How far the first / last station moved inward from end_offset to clear a fitting (0 when it did not).</summary>
        public double StartShift { get; set; }
        public double EndShift { get; set; }
    }

    public static class HangerRules
    {
        private const double Tol = 1e-6;

        /// <summary>
        /// Stations along a run, in the same length unit as the inputs (feet, when
        /// called from the command), measured from the run's start (0) to its end
        /// (runLength). At most maxStations are generated; beyond that the plan is
        /// OverBudget and empty.
        /// </summary>
        public static HangerStationPlan Plan(double runLength, double endOffset, double spacing,
            IReadOnlyList<double> interiorFittingPositions, int maxStations)
        {
            var plan = new HangerStationPlan();
            if (!(runLength > 0) || !(spacing > 0) || !(endOffset >= 0) || double.IsInfinity(runLength) || double.IsInfinity(spacing) || double.IsInfinity(endOffset))
                return plan;

            double start = endOffset, end = runLength - endOffset;
            if (end < start - Tol) { plan.TooShort = true; return plan; }
            if (end < start) end = start;

            // Exclusion zones that reach into [start, end], merged where they overlap.
            // A zone of zero width (end_offset 0) excludes nothing.
            var zones = new List<double[]>();
            if (interiorFittingPositions != null && endOffset > Tol)
            {
                foreach (double f in interiorFittingPositions.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).OrderBy(x => x))
                {
                    double lo = f - endOffset, hi = f + endOffset;
                    if (hi <= start + Tol || lo >= end - Tol) continue; // touches the span at most on its edge
                    if (zones.Count > 0 && lo < zones[zones.Count - 1][1] - Tol)
                        zones[zones.Count - 1][1] = Math.Max(zones[zones.Count - 1][1], hi);
                    else zones.Add(new[] { lo, hi });
                }
            }

            // The free segments between the zones (closed: a zone edge is allowed).
            var segments = new List<double[]>();
            double cursor = start;
            foreach (double[] z in zones)
            {
                if (z[0] >= cursor - Tol) segments.Add(new[] { cursor, Math.Min(Math.Max(z[0], cursor), end) });
                cursor = Math.Max(cursor, z[1]);
                if (cursor > end + Tol) break;
            }
            if (cursor <= end + Tol) segments.Add(new[] { Math.Min(cursor, end), end });
            if (segments.Count == 0) { plan.NoFreeSpan = true; return plan; }

            // Count first, as doubles, so a runaway ratio refuses before any allocation.
            double total = 0;
            foreach (double[] s in segments)
            {
                double len = s[1] - s[0];
                total += len <= Tol ? 1 : Math.Max(1, Math.Ceiling(len / spacing - Tol)) + 1;
                if (total > maxStations) { plan.OverBudget = true; return plan; }
            }

            for (int k = 0; k < segments.Count; k++)
            {
                double s0 = segments[k][0], s1 = segments[k][1], len = s1 - s0;
                if (k > 0)
                {
                    double prev = plan.Stations[plan.Stations.Count - 1];
                    if (s0 - prev > spacing + Tol) plan.WideGaps.Add(new[] { prev, s0 });
                }
                if (len <= Tol) { plan.Stations.Add(s0); continue; }
                int intervals = (int)Math.Max(1, Math.Ceiling(len / spacing - Tol)); // bounded by maxStations above
                double step = len / intervals;
                for (int i = 0; i <= intervals; i++) plan.Stations.Add(i == intervals ? s1 : s0 + i * step);
            }
            plan.StartShift = Math.Max(0, plan.Stations[0] - start);
            plan.EndShift = Math.Max(0, end - plan.Stations[plan.Stations.Count - 1]);
            return plan;
        }

        /// <summary>Consecutive stations further apart than the spacing (+ tolerance), as [from, to] pairs.</summary>
        public static List<double[]> GapsAbove(IReadOnlyList<double> stations, double spacing)
        {
            var list = new List<double[]>();
            if (stations == null) return list;
            for (int i = 1; i < stations.Count; i++)
                if (stations[i] - stations[i - 1] > spacing + Tol) list.Add(new[] { stations[i - 1], stations[i] });
            return list;
        }
    }
}
