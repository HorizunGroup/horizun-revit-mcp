// E2: a plan creation refuses the section-box arguments it would otherwise ignore.
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ViewArgumentRulesTests
    {
        [Fact]
        public void A_plan_with_start_and_end_is_refused_naming_the_argument()
            => Assert.Equal("start", ViewArgumentRules.PlanIgnoredArgument("create_floor_plan", new[] { "operation", "level_id", "start", "end" }));

        [Fact]
        public void A_plain_plan_and_a_section_pass()
        {
            Assert.Null(ViewArgumentRules.PlanIgnoredArgument("create_floor_plan", new[] { "operation", "level_id", "name" }));
            Assert.Null(ViewArgumentRules.PlanIgnoredArgument("create_section", new[] { "operation", "start", "end" }));
        }
    }
}
