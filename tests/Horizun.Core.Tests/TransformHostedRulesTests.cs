using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TransformHostedRulesTests
    {
        [Theory]
        [InlineData("rotate")]
        [InlineData("mirror")]
        public void Rotating_or_mirroring_a_wall_hosted_instance_is_refused_with_the_flip_route(string op)
        {
            string refusal = TransformHostedRules.Refusal(op, 1362400, isFamilyInstance: true, hostIsWall: true);
            Assert.NotNull(refusal);
            Assert.Contains("flipFacing", refusal);
            Assert.Contains("Nothing was written", refusal);
            Assert.Contains(TransformHostedRules.Code, refusal);
        }

        [Theory]
        [InlineData("move", true, true)]
        [InlineData("copy", true, true)]
        [InlineData("rotate", true, false)]
        [InlineData("mirror", false, false)]
        public void Other_operations_and_unhosted_elements_are_not_blocked(string op, bool familyInstance, bool hostIsWall) =>
            Assert.Null(TransformHostedRules.Refusal(op, 1, familyInstance, hostIsWall));
    }
}
