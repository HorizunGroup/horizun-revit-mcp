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
        /// 2026-07-28 IS supported by this server - statelessly, through Protocol/ - and
        /// must never be reachable from HERE.
        ///
        /// This file answers initialize, and that revision removed initialize. Handing it
        /// back to a client that opened with a handshake would name a dialect in which
        /// that client's own first message does not exist, and the client would then
        /// speak a protocol it had already proved it does not implement. The assertion
        /// did not change when the revision was adopted; only its reason did.
        /// </summary>
        [Fact]
        public void The_handshake_never_answers_a_revision_that_removed_the_handshake()
        {
            Assert.DoesNotContain("2026-07-28", ProtocolNegotiation.Supported);
            Assert.Equal(ProtocolNegotiation.Latest, ProtocolNegotiation.Answer("2026-07-28"));

            // ... and the modern table does carry it, so this is a deliberate exclusion
            // rather than a revision nobody implemented.
            Assert.Contains("2026-07-28", Horizun.Server.Protocol.McpRevision.All);
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
