using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests;

public sealed class GroupMemberEquivalenceTests
{
    [Fact]
    public void UniqueRelativeGeometryMapsOldMembersToNewIds()
    {
        var old = new[] { new GroupMemberFact(1, 10, 0,0,0, 2,1,1),
                          new GroupMemberFact(2, 10, 5,0,0, 2,1,1) };
        var next = new[] { new GroupMemberFact(12,10,5,0,0,2,1,1),
                           new GroupMemberFact(11,10,0,0,0,2,1,1) };
        var map = GroupMemberEquivalence.Match(old, next, 0.01);
        Assert.True(map.Complete);
        Assert.Equal(11, map.ByOldId[1]);
        Assert.Equal(12, map.ByOldId[2]);
    }

    [Fact]
    public void AmbiguousEquivalentElementIsRefused()
    {
        var old = new[] { new GroupMemberFact(1,10,0,0,0,1,1,1) };
        var next = new[] { new GroupMemberFact(2,10,0,0,0,1,1,1),
                           new GroupMemberFact(3,10,0,0,0,1,1,1) };
        Assert.False(GroupMemberEquivalence.Match(old,next,0.01).Complete);
    }
}
