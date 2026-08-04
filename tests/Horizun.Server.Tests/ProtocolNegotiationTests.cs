// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Golden tests for the negotiation rule. A bad answer here breaks every client at
// the first message, and the failure is silent from the server's side - the
// client simply leaves. Each case is written out as the spec words it.
// -----------------------------------------------------------------------------
using Xunit;

namespace Horizun.Server.Tests
{
    public class ProtocolNegotiationTests
    {
        [Theory]
        [InlineData("2025-11-25")]
        [InlineData("2025-06-18")]
        [InlineData("2025-03-26")]
        [InlineData("2024-11-05")]
        public void A_supported_request_is_answered_with_itself(string v)
        {
            Assert.Equal(v, ProtocolNegotiation.Answer(v));
        }

        [Fact]
        public void An_unknown_version_gets_the_latest_and_the_client_decides()
        {
            Assert.Equal(ProtocolNegotiation.Latest, ProtocolNegotiation.Answer("2019-01-01"));
        }

        /// <summary>
        /// The revision that is still RC upstream. Adopting it is a deliberate act behind
        /// the full adapter, never a string added here in passing - this test turns that
        /// sentence from a comment into a failure.
        /// </summary>
        [Fact]
        public void The_rc_revision_is_not_supported_yet()
        {
            Assert.DoesNotContain("2026-07-28", ProtocolNegotiation.Supported);
            Assert.Equal(ProtocolNegotiation.Latest, ProtocolNegotiation.Answer("2026-07-28"));
        }

        [Fact]
        public void A_missing_version_gets_the_latest()
        {
            Assert.Equal(ProtocolNegotiation.Latest, ProtocolNegotiation.Answer(null));
        }

        [Fact]
        public void The_latest_is_itself_supported()
        {
            Assert.Contains(ProtocolNegotiation.Latest, ProtocolNegotiation.Supported);
        }
    }
}
