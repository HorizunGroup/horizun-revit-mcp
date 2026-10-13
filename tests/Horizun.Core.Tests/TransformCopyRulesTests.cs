// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A copied insert is a dependent of the copy, not an unexplained element
// (audit 2026-10-08, 6.8).
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TransformCopyRulesTests
    {
        [Fact]
        public void The_door_copied_with_its_wall_is_a_dependent()
        {
            var hosts = new Dictionary<long, long?> { [11] = 10, [12] = 11 };   // door 11 in wall 10, a nested 12 in 11
            TransformCopyRules.Classify(new long[] { 11, 12 }, new long[] { 10 }, id => hosts.TryGetValue(id, out var h) ? h : null,
                                        out List<long> dependents, out List<long> unexplained);
            Assert.Equal(new long[] { 11, 12 }, dependents);
            Assert.Empty(unexplained);
        }

        [Fact]
        public void An_element_hosted_elsewhere_or_unhosted_is_unexplained()
        {
            var hosts = new Dictionary<long, long?> { [21] = 99, [22] = null };
            TransformCopyRules.Classify(new long[] { 21, 22 }, new long[] { 10 }, id => hosts.TryGetValue(id, out var h) ? h : null,
                                        out List<long> dependents, out List<long> unexplained);
            Assert.Empty(dependents);
            Assert.Equal(new long[] { 21, 22 }, unexplained);
        }
    }
}
