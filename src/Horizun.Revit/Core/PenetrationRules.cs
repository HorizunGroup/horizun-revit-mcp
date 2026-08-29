// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Penetration arithmetic without Revit. A clash pair becomes a penetration
// PLAN only when the roles are unambiguous (one penetrant, one host the write
// can reach) and the geometry supports the cut; everything else is a named
// refusal on the row, never a silent drop. The Revit half hands this file the
// crossing point, the penetrant direction and the cross-section; it takes
// back either the opening rectangle or the code that says why not.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class PenetrationRules
    {
        public const string CodeNotAPenetrationPair = "not_a_penetration_pair";
        public const string CodeHostIsLinked = "host_is_linked";
        public const string CodeStructuralHostRequiresOptIn = "structural_host_requires_opt_in";
        public const string CodeOpeningWallsOnly = "opening_supported_for_walls_only";
        public const string CodeNoCrossSection = "penetrant_cross_section_unreadable";
        public const string CodeOpeningSizeInvalid = "opening_size_invalid";

        /// <summary>
        /// A penetrant steeper than this against the horizontal cannot take a wall
        /// opening rectangle spanned by (up x direction): the span degenerates.
        /// </summary>
        public const double MaxVerticalComponentForWallOpening = 0.85;

        /// <summary>
        /// Which side of a clash pair is the penetrant. True only when exactly one
        /// side is an MEP curve - two pipes clashing is coordination, not a
        /// penetration; a wall clashing a wall is neither.
        /// </summary>
        public static bool ClassifyPair(bool aIsMepCurve, bool bIsMepCurve,
                                        out bool penetrantIsA, out string code, out string reason)
        {
            penetrantIsA = false; code = null; reason = null;
            if (aIsMepCurve == bIsMepCurve)
            {
                code = CodeNotAPenetrationPair;
                reason = aIsMepCurve
                    ? "both sides are MEP curves; that is a routing clash, not a penetration."
                    : "neither side is an MEP curve; a penetration is a curve crossing a host.";
                return false;
            }
            penetrantIsA = aIsMepCurve;
            return true;
        }

        /// <summary>
        /// Whether the write can act on this host at all, and whether it may. The
        /// structural gate is an OPT-IN, not a prohibition: cutting a bearing wall
        /// is somebody's engineering decision, so the default refuses and the
        /// argument records that a person made it.
        /// </summary>
        public static bool HostPermitted(bool hostInHostDocument, bool hostIsStructural, bool allowStructural,
                                         out string code, out string reason)
        {
            code = null; reason = null;
            if (!hostInHostDocument)
            {
                code = CodeHostIsLinked;
                reason = "the host lives in a LINKED document; this bridge writes only the active host model. " +
                         "Open the linked model and plan the penetration there.";
                return false;
            }
            if (hostIsStructural && !allowStructural)
            {
                code = CodeStructuralHostRequiresOptIn;
                reason = "the host is STRUCTURAL. Cutting it is an engineering decision: pass " +
                         "allow_structural_hosts=true (planner) or allow_structural=true (opening row) to state " +
                         "that a person approved it. Nothing was planned for this host.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Cluster crossing points that share a host: two crossings within
        /// `radiusFeet` of each other belong to one opening. Greedy transitive
        /// grouping in deterministic input order - the caller passes crossings in
        /// a stable order, and the first member names the cluster. radius <= 0
        /// means no clustering: every crossing is its own group.
        /// </summary>
        public static List<List<int>> Cluster(IList<double[]> pointsFeet, double radiusFeet)
        {
            var groups = new List<List<int>>();
            if (pointsFeet == null) return groups;
            if (radiusFeet <= 0)
            {
                for (int i = 0; i < pointsFeet.Count; i++) groups.Add(new List<int> { i });
                return groups;
            }
            var assigned = new int[pointsFeet.Count];
            for (int i = 0; i < assigned.Length; i++) assigned[i] = -1;
            for (int i = 0; i < pointsFeet.Count; i++)
            {
                if (assigned[i] >= 0) continue;
                var group = new List<int> { i };
                assigned[i] = groups.Count;
                // Transitive: anything within radius of ANY member joins.
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    for (int j = 0; j < pointsFeet.Count; j++)
                    {
                        if (assigned[j] >= 0) continue;
                        foreach (int member in group)
                        {
                            double dx = pointsFeet[j][0] - pointsFeet[member][0];
                            double dy = pointsFeet[j][1] - pointsFeet[member][1];
                            double dz = pointsFeet[j][2] - pointsFeet[member][2];
                            if (Math.Sqrt(dx * dx + dy * dy + dz * dz) <= radiusFeet)
                            {
                                group.Add(j); assigned[j] = groups.Count; grew = true; break;
                            }
                        }
                    }
                }
                groups.Add(group);
            }
            return groups;
        }

        /// <summary>
        /// One opening covering a CLUSTER of wall crossings: the rectangle that
        /// spans every member's own rectangle. Direction and sizes come from the
        /// members; the cluster refuses if any member would refuse alone.
        /// </summary>
        public static bool ClusterCorners(IList<double[]> corner1s, IList<double[]> corner2s,
                                          out double[] corner1, out double[] corner2)
        {
            corner1 = null; corner2 = null;
            if (corner1s == null || corner1s.Count == 0 || corner2s == null || corner2s.Count != corner1s.Count)
                return false;
            corner1 = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            corner2 = new[] { double.MinValue, double.MinValue, double.MinValue };
            for (int i = 0; i < corner1s.Count; i++)
                for (int axis = 0; axis < 3; axis++)
                {
                    corner1[axis] = Math.Min(corner1[axis], Math.Min(corner1s[i][axis], corner2s[i][axis]));
                    corner2[axis] = Math.Max(corner2[axis], Math.Max(corner1s[i][axis], corner2s[i][axis]));
                }
            return true;
        }

        /// <summary>Opening size validation shared by the slab kinds: positive, bounded.</summary>
        public static bool ValidateOpeningSize(double widthFeet, double heightFeet, out string reason)
        {
            reason = null;
            const double maxFeet = 20000 / 304.8;
            if (widthFeet <= 0 || heightFeet <= 0)
            { reason = CodeOpeningSizeInvalid + ": width and height (or diameter) must be positive."; return false; }
            if (widthFeet > maxFeet || heightFeet > maxFeet)
            { reason = CodeOpeningSizeInvalid + ": an opening over 20 m is a modelling accident, not a penetration."; return false; }
            return true;
        }

        /// <summary>
        /// The opening rectangle for a wall host: two diagonal corners in host feet,
        /// spanned horizontally along the wall (up x penetrant direction) and
        /// vertically by world Z, centred on the crossing point, sized by the
        /// penetrant cross-section plus clearance ALL AROUND. A near-vertical
        /// penetrant refuses - that is a floor penetration wearing a wall's name.
        /// </summary>
        public static bool OpeningCorners(double px, double py, double pz,
                                          double dx, double dy, double dz,
                                          double widthFeet, double heightFeet, double clearanceFeet,
                                          out double[] corner1, out double[] corner2,
                                          out string code, out string reason)
        {
            corner1 = null; corner2 = null; code = null; reason = null;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-9 || widthFeet <= 0 || heightFeet <= 0)
            {
                code = CodeNoCrossSection;
                reason = "the penetrant's direction or cross-section could not be measured; an opening cannot " +
                         "be sized from nothing.";
                return false;
            }
            dx /= len; dy /= len; dz /= len;
            if (Math.Abs(dz) > MaxVerticalComponentForWallOpening)
            {
                code = CodeOpeningWallsOnly;
                reason = "the penetrant runs near-vertically (|z| component " +
                         Math.Abs(dz).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                         "); a rectangular wall opening is spanned horizontally and this crossing is a " +
                         "floor/ceiling penetration. Use a sleeve family for it.";
                return false;
            }
            // Horizontal span: up x direction, normalized. Nonzero because |dz| <= 0.85.
            double tx = -dy, ty = dx;
            double tLen = Math.Sqrt(tx * tx + ty * ty);
            tx /= tLen; ty /= tLen;
            double halfW = widthFeet / 2 + clearanceFeet;
            double halfH = heightFeet / 2 + clearanceFeet;
            corner1 = new[] { px - tx * halfW, py - ty * halfW, pz - halfH };
            corner2 = new[] { px + tx * halfW, py + ty * halfW, pz + halfH };
            return true;
        }
    }
}
