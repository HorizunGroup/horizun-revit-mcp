using System.Collections.Generic;
using System;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class GroupPatternRules
    {
        public static List<double> CandidateAngles(IEnumerable<MemberSignature> pattern,
            IEnumerable<MemberSignature> baseline, double originXFeet, double originYFeet)
        {
            var candidates = new List<double> { 0 };
            foreach (var a in pattern.Where(x => x.HasBox))
            foreach (var b in baseline.Where(x => x.HasBox && x.Key == a.Key))
            {
                double ax = a.Center[0]-originXFeet, ay = a.Center[1]-originYFeet;
                double bx = b.Center[0]-originXFeet, by = b.Center[1]-originYFeet;
                if (Math.Sqrt(ax*ax+ay*ay) < 0.01 || Math.Sqrt(bx*bx+by*by) < 0.01) continue;
                double target = Math.Atan2(ay,ax), source = Math.Atan2(by,bx);
                // A reflection after rotation reverses the member's polar angle.
                // The two trial mirror planes in ManageGroups use normals at
                // angle and angle+90 degrees, so test both sum-angle solutions.
                foreach (double angle in new[] { target-source, target+source,
                    target+source-Math.PI })
                    if (!candidates.Any(x => Math.Abs(Math.Sin((x-angle)/2)) < 1e-6))
                        candidates.Add(angle);
                if (candidates.Count >= 100) return candidates;
            }
            return candidates;
        }

        public static List<MemberSignature> Translated(IEnumerable<MemberSignature> source,
            double dxFeet, double dyFeet, double dzFeet)
        {
            var result = new List<MemberSignature>();
            var shift = new[] { dxFeet, dyFeet, dzFeet };
            foreach (var row in source)
            {
                var clone = new MemberSignature { Category = row.Category, Type = row.Type,
                    HasBox = row.HasBox };
                if (row.HasBox)
                    for (int i = 0; i < 3; i++)
                    {
                        clone.Min[i] = row.Min[i] + shift[i];
                        clone.Max[i] = row.Max[i] + shift[i];
                    }
                result.Add(clone);
            }
            return result;
        }
    }
}
