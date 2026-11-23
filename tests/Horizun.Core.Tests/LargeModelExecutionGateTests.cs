using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LargeModelExecutionGateTests
    {
        [Fact]
        public void Large_scope_refuses_before_operation_and_explicit_confirmation_runs_it()
        {
            int calls = 0;
            Func<string> operation = () => { calls++; return "ran"; };
            var counts = new Dictionary<string, long> { ["Walls"] = 110000 };
            var refusal = LargeModelExecutionGate.Run(120000, counts, 50000, false, operation);
            Assert.False(refusal.Executed);
            Assert.Equal(0, calls);
            Assert.Equal(110000, refusal.Estimate.ScopedElements);
            Assert.False(string.IsNullOrWhiteSpace(refusal.ScopeProposal));
            var accepted = LargeModelExecutionGate.Run(120000, counts, 50000, true, operation);
            Assert.True(accepted.Executed);
            Assert.Equal("ran", accepted.Value);
            Assert.Equal(1, calls);
        }
    }
}
