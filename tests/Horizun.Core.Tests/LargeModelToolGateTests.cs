using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LargeModelToolGateTests
    {
        [Theory]
        [InlineData("horizun_query_model")]
        [InlineData("horizun_list_elements")]
        [InlineData("horizun_model_scan")]
        [InlineData("horizun_audit_model")]
        [InlineData("horizun_quantities")]
        public void Each_tool_stops_before_running_and_explicit_confirmation_allows_it(string tool)
        {
            int calls = 0;
            var counts = new Dictionary<string, long> { ["host"] = 120000, ["link"] = 10000 };
            Func<int> execute = () => ++calls;
            var refusal = LargeModelToolGate.Run(tool, 120000, counts, false, execute);
            Assert.False(refusal.Executed);
            Assert.Equal(0, calls);
            Assert.Equal(130000, refusal.Estimate.ScopedElements);
            Assert.False(string.IsNullOrWhiteSpace(refusal.ScopeProposal));
            var accepted = LargeModelToolGate.Run(tool, 120000, counts, true, execute);
            Assert.True(accepted.Executed);
            Assert.Equal(1, calls);
        }
    }
}
