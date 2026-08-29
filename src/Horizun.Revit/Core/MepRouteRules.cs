// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Route arithmetic without Revit: a polyline becomes segments and corners.
// Every refusal names its vertex - a run someone's hydraulics will stand on
// is not a place for a silently dropped point - and a collinear vertex is
// MERGED with a note rather than becoming a zero-degree elbow Revit throws at.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    public sealed class RouteSegment
    {
        public double[] Start, End;   // feet
        public int FromVertex, ToVertex;
    }

    public static class MepRouteRules
    {
        public const double MinSegmentFeet = 50.0 / 304.8;          // 50 mm
        public const double CollinearAngleDegrees = 0.5;

        public const string CodeTooFewPoints = "route_needs_two_points";
        public const string CodeDegenerateSegment = "segment_too_short";

        /// <summary>
        /// Validate the polyline and fold collinear vertices. Returns null on
        /// success; segments come back ready to become pipes, and
        /// `mergedVertices` names each vertex folded away so the caller's reply
        /// can say the run has fewer corners than the request had points.
        /// </summary>
        public static string Segments(IList<double[]> pointsFeet, out List<RouteSegment> segments,
                                      out List<int> mergedVertices)
        {
            segments = new List<RouteSegment>();
            mergedVertices = new List<int>();
            if (pointsFeet == null || pointsFeet.Count < 2)
                return CodeTooFewPoints + ": a route is at least two points.";

            var kept = new List<KeyValuePair<int, double[]>> { new KeyValuePair<int, double[]>(0, pointsFeet[0]) };
            for (int i = 1; i < pointsFeet.Count; i++)
            {
                double[] previous = kept[kept.Count - 1].Value;
                double[] current = pointsFeet[i];
                double length = Distance(previous, current);
                if (length < MinSegmentFeet)
                    return CodeDegenerateSegment + ": the segment into vertex " + i + " is " + Mm(length) +
                           "; segments under " + Mm(MinSegmentFeet) + " are modelling accidents, not runs.";
                if (kept.Count >= 2)
                {
                    double[] before = kept[kept.Count - 2].Value;
                    if (TurnDegrees(before, previous, current) < CollinearAngleDegrees)
                    {
                        // The middle vertex adds no corner: fold it, and say so.
                        mergedVertices.Add(kept[kept.Count - 1].Key);
                        kept.RemoveAt(kept.Count - 1);
                    }
                }
                kept.Add(new KeyValuePair<int, double[]>(i, current));
            }
            for (int i = 0; i + 1 < kept.Count; i++)
                segments.Add(new RouteSegment
                {
                    Start = kept[i].Value, End = kept[i + 1].Value,
                    FromVertex = kept[i].Key, ToVertex = kept[i + 1].Key
                });
            return null;
        }

        /// <summary>The turn at `middle`, in degrees: 0 = straight through.</summary>
        public static double TurnDegrees(double[] before, double[] middle, double[] after)
        {
            double[] incoming = Unit(before, middle);
            double[] outgoing = Unit(middle, after);
            double dot = incoming[0] * outgoing[0] + incoming[1] * outgoing[1] + incoming[2] * outgoing[2];
            if (dot > 1.0) dot = 1.0; else if (dot < -1.0) dot = -1.0;
            return Math.Acos(dot) * 180.0 / Math.PI;
        }

        private static double[] Unit(double[] from, double[] to)
        {
            double dx = to[0] - from[0], dy = to[1] - from[1], dz = to[2] - from[2];
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length < 1e-12) return new[] { 0.0, 0.0, 0.0 };
            return new[] { dx / length, dy / length, dz / length };
        }

        private static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static string Mm(double feet) =>
            (feet * 304.8).ToString("0.0", CultureInfo.InvariantCulture) + " mm";
    }
}
