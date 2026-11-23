using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OpenRecoveryProcessorTests
    {
        private sealed class Failure : IOpenRecoveryFailure
        {
            public string Message { get; set; }
            public IReadOnlyList<long> ElementIds { get; set; }
            public string DefaultResolution { get; set; }
            public bool Permitted { get; set; }
            public bool ThrowOnResolve { get; set; }
            public int Calls;
            public void Resolve() { Calls++; if (ThrowOnResolve) throw new InvalidOperationException("resolution failed"); }
        }

        [Fact]
        public void Resolves_permitted_error_and_reports_exact_evidence()
        {
            var error = new Failure { Message = "Cannot form radial dimension", ElementIds = new long[] { 12, 13 }, DefaultResolution = "DeleteElements", Permitted = true };
            var processor = new OpenRecoveryProcessor();
            Assert.True(processor.TryResolve(error));
            Assert.Equal(1, error.Calls);
            Assert.Equal("Cannot form radial dimension", processor.Resolved[0].Message);
            Assert.Equal(new long[] { 12, 13 }, processor.Resolved[0].ElementIds);
            Assert.Equal("DeleteElements", processor.Resolved[0].Resolution);
        }

        [Fact]
        public void Unavailable_resolution_is_not_claimed_or_called()
        {
            var error = new Failure { Message = "error", ElementIds = new long[0], DefaultResolution = "DeleteElements", Permitted = false };
            var processor = new OpenRecoveryProcessor();
            Assert.False(processor.TryResolve(error));
            Assert.Equal(0, error.Calls);
            Assert.Empty(processor.Resolved);
        }

        [Fact]
        public void Reports_each_successful_attempt_and_snapshots_its_element_ids()
        {
            var ids = new List<long> { 12, 13 };
            var first = new Failure { Message = "first", ElementIds = ids, DefaultResolution = "DeleteElements", Permitted = true };
            var second = new Failure { Message = "second", ElementIds = new long[] { 42 }, DefaultResolution = "DetachElements", Permitted = true };
            var processor = new OpenRecoveryProcessor();

            Assert.True(processor.TryResolve(first));
            ids.Clear();
            Assert.True(processor.TryResolve(second));

            Assert.Equal(2, processor.Resolved.Count);
            Assert.Equal("first", processor.Resolved[0].Message);
            Assert.Equal(new long[] { 12, 13 }, processor.Resolved[0].ElementIds);
            Assert.Equal("DeleteElements", processor.Resolved[0].Resolution);
            Assert.Equal("second", processor.Resolved[1].Message);
            Assert.Equal(new long[] { 42 }, processor.Resolved[1].ElementIds);
            Assert.Equal("DetachElements", processor.Resolved[1].Resolution);
        }

        [Fact]
        public void Throwing_resolution_is_not_recorded_as_successful()
        {
            var failure = new Failure { Message = "error", ElementIds = new long[] { 5 }, DefaultResolution = "DeleteElements", Permitted = true, ThrowOnResolve = true };
            var processor = new OpenRecoveryProcessor();

            Assert.Throws<InvalidOperationException>(() => processor.TryResolve(failure));
            Assert.Equal(1, failure.Calls);
            Assert.Empty(processor.Resolved);
        }

        [Fact]
        public void Resolution_call_alone_is_not_a_verified_effect()
        {
            var error = new Failure { Message = "error", ElementIds = new long[] { 5 }, DefaultResolution = "DeleteElements", Permitted = true };
            var processor = new OpenRecoveryProcessor();
            processor.TryResolve(error);
            Assert.Equal("unverified", processor.Resolved[0].EffectStatus);
        }
    }
}
