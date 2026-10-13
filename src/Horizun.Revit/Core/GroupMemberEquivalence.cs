using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class GroupMemberFact
    {
        public long Id, Category;
        public double X,Y,Z, Width,Depth,Height;
        public GroupMemberFact(long id,long category,double x,double y,double z,double width,double depth,double height)
        { Id=id; Category=category; X=x; Y=y; Z=z; Width=width; Depth=depth; Height=height; }
    }

    public sealed class GroupMemberMap
    {
        public bool Complete;
        public string Reason;
        public Dictionary<long,long> ByOldId = new Dictionary<long,long>();
    }

    public static class GroupMemberEquivalence
    {
        public static GroupMemberMap Match(IEnumerable<GroupMemberFact> oldFacts,
            IEnumerable<GroupMemberFact> newFacts, double toleranceFeet)
        {
            var result = new GroupMemberMap();
            var remaining = newFacts.ToList();
            foreach (var old in oldFacts.OrderBy(x => x.Id))
            {
                var candidates = remaining.Where(x => x.Category == old.Category &&
                    Math.Abs(x.X-old.X) <= toleranceFeet &&
                    Math.Abs(x.Y-old.Y) <= toleranceFeet &&
                    Math.Abs(x.Z-old.Z) <= toleranceFeet &&
                    Math.Abs(x.Width-old.Width) <= toleranceFeet &&
                    Math.Abs(x.Depth-old.Depth) <= toleranceFeet &&
                    Math.Abs(x.Height-old.Height) <= toleranceFeet).ToList();
                if (candidates.Count != 1)
                {
                    result.Reason = "old member " + old.Id + " has " + candidates.Count +
                        " equivalent candidates; mapping must be unique";
                    return result;
                }
                result.ByOldId[old.Id] = candidates[0].Id;
                remaining.Remove(candidates[0]);
            }
            result.Complete = true;
            return result;
        }
    }
}
