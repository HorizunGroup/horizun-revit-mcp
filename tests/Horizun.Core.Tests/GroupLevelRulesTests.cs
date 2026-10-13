using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests;

public sealed class GroupLevelRulesTests
{
    [Fact]
    public void KeepPositionOffsetsNewLevelFromExistingWorldElevation()
    {
        Assert.Equal(-2000.0, GroupLevelRules.OffsetMmForWorldZ(1000, 3000));
        Assert.True(GroupLevelRules.PositionHeld(1, 2, 3, 1.4, 2, 3, 0.5));
        Assert.False(GroupLevelRules.PositionHeld(1, 2, 3, 2, 2, 3, 0.5));
    }
}
