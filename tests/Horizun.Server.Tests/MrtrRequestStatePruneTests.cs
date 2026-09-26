// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The 2026-09-26 review fix: Prune() used to evict the oldest-expiring LIVE
// (unexpired) nonce once the Consumed table hit MaxConsumed, to make room for
// a new one. That breaks the single-use guarantee this whole file exists for -
// an evicted nonce is forgotten, so its original (still unexpired) requestState
// could be replayed and re-applied. Prune must remove ONLY expired entries;
// TryConsume must REFUSE a new consume with a clear "capacity" reason when the
// table is genuinely full of live entries, never silently forget one.
//
// This whole assembly runs single-threaded (Parallelism.cs), and MrtrRequestState
// is source-linked into THIS project (see the .csproj), so its internals -
// Consumed, MaxConsumed, ResetForTests, Now - are ordinary members here.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class MrtrRequestStatePruneTests : IDisposable
    {
        public MrtrRequestStatePruneTests() => MrtrRequestState.ResetForTests();

        public void Dispose()
        {
            MrtrRequestState.Now = () => DateTimeOffset.UtcNow;
            MrtrRequestState.ResetForTests();
        }

        private static JObject Payload(string nonce, long expUnixMs) => new JObject
        {
            ["v"] = MrtrRequestState.Version, ["nonce"] = nonce, ["exp"] = expUnixMs
        };

        [Fact]
        public void A_fresh_nonce_consumes_once_and_a_replay_is_refused_as_replayed()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            MrtrRequestState.Now = () => now;
            JObject payload = Payload("n1", now.AddMinutes(5).ToUnixTimeMilliseconds());

            string reason;
            Assert.True(MrtrRequestState.TryConsume(payload, out reason));
            Assert.Null(reason);

            Assert.False(MrtrRequestState.TryConsume(payload, out reason));
            Assert.Equal("replayed", reason);
        }

        [Fact]
        public void A_full_table_of_LIVE_nonces_refuses_the_new_consume_as_capacity_not_eviction()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            MrtrRequestState.Now = () => now;
            long farExpiry = now.AddHours(1).ToUnixTimeMilliseconds();

            // Fill the table to MaxConsumed with nonces that will NOT expire during this test.
            for (int i = 0; i < MrtrRequestState.MaxConsumed; i++)
            {
                string reason;
                bool ok = MrtrRequestState.TryConsume(Payload("live-" + i, farExpiry), out reason);
                Assert.True(ok, "setup consume " + i + " unexpectedly failed: " + reason);
            }

            // One more, brand new nonce: the table is genuinely full of live entries, so this
            // MUST be refused with "capacity" - never silently evict one of the 4096 above.
            string refusalReason;
            bool consumed = MrtrRequestState.TryConsume(Payload("newcomer", farExpiry), out refusalReason);
            Assert.False(consumed);
            Assert.Equal("capacity", refusalReason);

            // THE PROOF that nothing was forgotten: every one of the original live nonces still
            // reports "replayed" (i.e. still present in Consumed), not a fresh consume succeeding
            // where an eviction would have made room for it.
            for (int i = 0; i < MrtrRequestState.MaxConsumed; i += 512)   // sample, not all 4096, to keep this fast
            {
                string reason;
                bool ok = MrtrRequestState.TryConsume(Payload("live-" + i, farExpiry), out reason);
                Assert.False(ok);
                Assert.Equal("replayed", reason);
            }
        }

        [Fact]
        public void An_expired_nonce_frees_its_slot_so_a_full_table_of_expired_ones_accepts_a_new_consume()
        {
            DateTimeOffset issuedAt = DateTimeOffset.UtcNow;
            MrtrRequestState.Now = () => issuedAt;
            long alreadyPastExpiry = issuedAt.AddSeconds(-1).ToUnixTimeMilliseconds();

            for (int i = 0; i < MrtrRequestState.MaxConsumed; i++)
            {
                string reason;
                Assert.True(MrtrRequestState.TryConsume(Payload("expired-" + i, alreadyPastExpiry), out reason));
            }

            // Move the clock forward: every one of the entries above is now expired. Prune runs
            // inside the next TryConsume (only once the table is at capacity, by design) and must
            // remove them - freeing room for the new nonce without evicting anything live, because
            // there is nothing live left to evict.
            MrtrRequestState.Now = () => issuedAt.AddMinutes(10);
            string finalReason;
            bool accepted = MrtrRequestState.TryConsume(Payload("fresh-after-expiry", issuedAt.AddMinutes(20).ToUnixTimeMilliseconds()), out finalReason);
            Assert.True(accepted, "expected room after expired entries were pruned; refusal reason: " + finalReason);
            Assert.Null(finalReason);
        }
    }
}
