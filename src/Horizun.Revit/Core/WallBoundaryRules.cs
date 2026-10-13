// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// THE BOUNDARY ELEMENT OF A STRUCTURAL WALL IS A COLUMN INSIDE THE WALL.
//
// A schedule says "EB-1: 8#5, E#3 at 100, in the last 600 mm of the wall": a cage of
// vertical bars and closed stirrups at one end of the wall, up its whole height. That
// is a section rule whose section is the wall's end region, so this file does not lay
// a single bar. It turns the wall's measured frame - U along the wall, V across its
// thickness, W up, as SectionFrames.Measure reads any member with a horizontal
// location line - into the frame of that region:
//
//     U  up the wall          (UMin..UMax = the wall's bottom..top)
//     V  along the wall       (restricted to length_mm from the declared end)
//     W  across the thickness (the wall's two faces are W min and W max)
//
// which is right-handed, and hands it to SectionRules.ExpandAll with the rule's own
// cover, layers, stirrups and ties. Ids come out as <id>#h<wall id>#<layer>, exactly
// as a section rule's do (acero 3.0, L5).
//
// In that frame `top` and `bottom` are the wall's faces (+W, -W), `left` and `right`
// the region's ends along the wall (toward the wall's start, toward its end), and
// `end` cover is at the wall's bottom and top.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class StructuralWallBoundaryRule
    {
        public const string EndStart = "start", EndEnd = "end";
        public static readonly string[] Ends = { EndStart, EndEnd };

        public string Id;
        /// <summary>Which end of the wall's location line the region is at: start or end.</summary>
        public string End;
        /// <summary>How far the region runs along the wall from that end, in mm. Declared.</summary>
        public double LengthMm;
        /// <summary>The section the region is reinforced with: same id, host, cover, layers, stirrups, ties, mark.</summary>
        public StructuralSectionRule Section = new StructuralSectionRule();
        public StructuralHostSelector Host => Section.Host;
        public bool Required => Section.Required;
        public Newtonsoft.Json.Linq.JObject Raw;
    }

    public static class WallBoundaryRules
    {
        public const string CodeLongerThanWall = "wall_boundary_longer_than_the_wall";
        public const string CodeTooShort = "wall_boundary_too_short_for_its_bars";
        public const string CodeNotAWall = "wall_boundary_host_is_not_a_wall";
        public static readonly string[] AllCodes = { CodeLongerThanWall, CodeTooShort, CodeNotAWall };

        private static SectionResult Refuse(string code, string why) => new SectionResult { Code = code, Why = why };

        /// <summary>Null when a host of this built-in category may carry a boundary element; otherwise why not.</summary>
        public static string HostRefusal(string builtInCategory)
        {
            if (string.Equals(builtInCategory, "OST_Walls", StringComparison.Ordinal)) return null;
            return CodeNotAWall + ": the host is " + (string.IsNullOrWhiteSpace(builtInCategory) ? "of no readable category" : "a " + builtInCategory) +
                   ", not a wall. A boundary element is the end region of a wall; a column is reinforced with section_rules.";
        }

        /// <summary>
        /// The boundary region's frame, from the wall's measured frame (U along the wall, V
        /// across, W up). Refuses a region longer than the wall; the room for the bars is
        /// ExpandAll's question, because it needs the cover and the diameters.
        /// </summary>
        public static SectionResult Frame(StructuralWallBoundaryRule rule, SectionHostFrame wall, out SectionHostFrame boundary)
        {
            boundary = null;
            if (rule.LengthMm > wall.LengthMm + 1e-6)
                return Refuse(CodeLongerThanWall, "boundary rule '" + rule.Id + "' runs " + Mm(rule.LengthMm) + " mm along wall " +
                              wall.ElementId + ", which is " + Mm(wall.LengthMm) + " mm long.");
            double v0, v1;
            if (rule.End == StructuralWallBoundaryRule.EndStart) { v0 = wall.UMin; v1 = wall.UMin + rule.LengthMm; }
            else { v1 = wall.UMax; v0 = wall.UMax - rule.LengthMm; }
            boundary = new SectionHostFrame
            {
                ElementId = wall.ElementId,
                OriginMm = new[] { wall.OriginMm[0], wall.OriginMm[1], wall.OriginMm[2] },
                U = Copy(wall.W), V = Copy(wall.U), W = Copy(wall.V),
                UMin = wall.WMin, UMax = wall.WMax,
                VMin = v0, VMax = v1,
                WMin = wall.VMin, WMax = wall.VMax,
                // THE WALL'S SOLID is what was measured. A wall that is not a box along its
                // own axes - a door, a cut end, a mitred corner - is refused as a section is,
                // not reinforced as if its end region were whole.
                NotRectangular = wall.NotRectangular,
                // THE HOOKS' CORNER AGAINST THE WALL'S END (measured, Revit 2024 and 2026): there both
                // legs lie on the cover and Revit lays the second pass along the same leg; inside the
                // wall one leg is free and Revit moves the pass - and the corner bars - along it.
                HookCornerAtVMax = rule.End != StructuralWallBoundaryRule.EndStart
            };
            return new SectionResult();
        }

        /// <summary>
        /// The rule for ONE wall: its region's frame, the room the region leaves for the bars,
        /// then SectionRules.ExpandAll. `diameterOf` answers the MODEL diameter of a bar type
        /// id in mm, or zero; `hostCoverMm` is the wall's common cover when the rule reads it.
        /// </summary>
        public static SectionResult ExpandAll(StructuralWallBoundaryRule rule, SectionHostFrame wall, Func<string, double> diameterOf,
                                              Func<string, double> stirrupBendOf, double? hostCoverMm,
                                              out List<StructuralRebarRule> bars, out List<StructuralStirrupZoneRule> zoneRules)
        {
            bars = new List<StructuralRebarRule>();
            zoneRules = new List<StructuralStirrupZoneRule>();
            SectionHostFrame f;
            SectionResult framed = Frame(rule, wall, out f);
            if (!framed.Ok) return framed;

            StructuralSectionRule s = rule.Section;
            double left, right;
            if (s.Cover.FromHost)
            {
                if (!hostCoverMm.HasValue)
                    return Refuse(SectionRules.CodeNoCover, "boundary rule '" + rule.Id + "' reads the cover from the host and wall " +
                                  wall.ElementId + " has no readable common cover. Declare cover distance_mm or per-face values.");
                left = right = hostCoverMm.Value;
            }
            else if (s.Cover.CommonMm.HasValue) left = right = s.Cover.CommonMm.Value;
            else { left = s.Cover.LeftMm.Value; right = s.Cover.RightMm.Value; }

            double sd = 0, db = 0;
            if (s.Stirrups != null)
            {
                sd = diameterOf(s.Stirrups.BarTypeId);
                if (sd <= 0) return Refuse(SectionRules.CodeNoDiameter, "the stirrup bar type's diameter could not be read from the model.");
            }
            foreach (StructuralSectionLayer l in s.Layers)
            {
                double d = diameterOf(l.BarTypeId);
                if (d <= 0) return Refuse(SectionRules.CodeNoDiameter, "layer '" + (l.Name ?? l.Face) + "': its bar type's diameter could not be read.");
                db = Math.Max(db, d);
            }
            // THE LEAST REGION THAT HOLDS ONE BAR: a cover at each of its ends along the wall,
            // a stirrup leg inside each, and the largest bar between them. Shorter, and the
            // stirrup closes on nothing - refused here by name, with the arithmetic.
            double needed = left + right + 2 * sd + db;
            if (rule.LengthMm < needed - 1e-6)
                return Refuse(CodeTooShort, "boundary rule '" + rule.Id + "' runs " + Mm(rule.LengthMm) + " mm along wall " + wall.ElementId +
                              ", and its covers (" + Mm(left) + " + " + Mm(right) + "), " + (sd > 0 ? "two stirrup legs (2 x " + Mm(sd) + ") " : "") +
                              "and one bar (" + Mm(db) + ") need " + Mm(needed) + " mm.");

            SectionResult r = SectionRules.ExpandAll(s, f, diameterOf, stirrupBendOf, hostCoverMm, out bars, out zoneRules);
            // A REFUSED REGION LAYS NOTHING: no half of a cage for a caller that forgot to look.
            if (!r.Ok) { bars = new List<StructuralRebarRule>(); zoneRules = new List<StructuralStirrupZoneRule>(); }
            return r;
        }

        private static double[] Copy(double[] a) => new[] { a[0], a[1], a[2] };
        private static string Mm(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);
    }
}
