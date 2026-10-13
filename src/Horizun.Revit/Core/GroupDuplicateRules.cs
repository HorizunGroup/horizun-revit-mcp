using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class GroupDuplicateFact
    {
        public long Id, TypeId;
        public string Kind;
        public double X, Y, Z;
    }

    public sealed class GroupDuplicatePair
    {
        public long FirstId, SecondId, TypeId;
        public string Kind;
        public double DistanceMm;
    }

    public sealed class GroupLevelFact
    {
        public long Id;
        public double ElevationMm;
    }

    public sealed class GroupLevelInference
    {
        public long? LevelId;
        public double GeometryBottomMm, DistanceMm;
        public string Evidence;
    }

    public static class GroupDuplicateRules
    {
        // A geometric inference, not a claim that the instance's LevelId is truthful.
        // If no level is close to the bottom of its geometry the answer is unknown.
        public static GroupLevelInference InferLevel(double geometryBottomMm,
            IEnumerable<GroupLevelFact> levels, double toleranceMm)
        {
            var nearest = levels.OrderBy(x => Math.Abs(x.ElevationMm - geometryBottomMm))
                .ThenBy(x => x.Id).FirstOrDefault();
            double distance = nearest == null ? double.PositiveInfinity :
                Math.Abs(nearest.ElevationMm - geometryBottomMm);
            return new GroupLevelInference { GeometryBottomMm = geometryBottomMm,
                DistanceMm = distance,
                LevelId = nearest != null && distance <= toleranceMm ? nearest.Id : (long?)null,
                Evidence = nearest == null ? "no_levels" :
                    distance <= toleranceMm ? "geometry_bottom_nearest_level" : "no_level_within_tolerance" };
        }

        public static List<GroupDuplicatePair> Find(IEnumerable<GroupDuplicateFact> facts, double toleranceMm)
        {
            if (toleranceMm <= 0 || double.IsNaN(toleranceMm) || double.IsInfinity(toleranceMm))
                throw new ArgumentOutOfRangeException(nameof(toleranceMm));
            var result = new List<GroupDuplicatePair>();
            foreach (var bucket in facts.GroupBy(f => (f.Kind, f.TypeId)))
            {
                var sorted = bucket.OrderBy(f => f.X).ThenBy(f => f.Id).ToList();
                for (int i = 0; i < sorted.Count; i++)
                    for (int j = i + 1; j < sorted.Count && sorted[j].X - sorted[i].X <= toleranceMm; j++)
                    {
                        var a = sorted[i]; var b = sorted[j];
                        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                        double distance = Math.Sqrt(dx*dx + dy*dy + dz*dz);
                        if (distance <= toleranceMm)
                            result.Add(new GroupDuplicatePair { FirstId = Math.Min(a.Id,b.Id),
                                SecondId = Math.Max(a.Id,b.Id), Kind = a.Kind,
                                TypeId = a.TypeId, DistanceMm = distance });
                    }
            }
            return result.OrderBy(p => p.Kind, StringComparer.Ordinal).ThenBy(p => p.TypeId)
                .ThenBy(p => p.FirstId).ThenBy(p => p.SecondId).ToList();
        }
    }
}
