using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests;

public sealed class GroupPatternRulesTests
{
    [Fact]
    public void ExpectedMemberBoxesTranslateWithNewInstanceWithoutChangingType()
    {
        var before = new List<MemberSignature> { new MemberSignature(1, 2,
            new[] { 0.0, 1.0, 2.0 }, new[] { 3.0, 4.0, 5.0 }) };
        var expected = GroupPatternRules.Translated(before, 10, -2, 0);
        Assert.Equal(10, expected[0].Min[0]);
        Assert.Equal(2, expected[0].Max[1]);
        Assert.Equal(2, expected[0].Type);
    }

    [Fact]
    public void RotationCandidatesComeFromMemberGeometryWhenGroupRotationIsUnreadable()
    {
        var pattern = new[] { new MemberSignature(1,2,new[]{-1.1,0.9,0.0},new[]{-0.9,1.1,1.0}) };
        var baseline = new[] { new MemberSignature(1,2,new[]{0.9,0.9,0.0},new[]{1.1,1.1,1.0}) };
        var angles = GroupPatternRules.CandidateAngles(pattern, baseline, 0, 0);
        Assert.Contains(angles, x => System.Math.Abs(x - System.Math.PI/2) < 1e-6);
    }

    [Fact]
    public void MirroredPatternAddsSumAngleCandidates()
    {
        static MemberSignature At(double degrees)
        {
            double x = System.Math.Cos(degrees * System.Math.PI / 180);
            double y = System.Math.Sin(degrees * System.Math.PI / 180);
            return new MemberSignature(1, 2,
                new[] { x - .01, y - .01, 0.0 }, new[] { x + .01, y + .01, 1.0 });
        }
        var angles = GroupPatternRules.CandidateAngles(new[] { At(120) }, new[] { At(20) }, 0, 0);
        Assert.Contains(angles, x => System.Math.Abs(x - (-40 * System.Math.PI / 180)) < 1e-6);
    }
}
