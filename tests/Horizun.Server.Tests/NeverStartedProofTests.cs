// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// "Never started" is decided on structure, not on words (audit 2026-10-08).
// -----------------------------------------------------------------------------
using System;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class NeverStartedProofTests
    {
        private static JObject Failed(string text, JObject structured) => new JObject
        {
            ["isError"] = true,
            ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
            ["structuredContent"] = structured
        };

        [Fact]
        public void The_words_alone_prove_nothing()
        {
            Assert.Null(NeverStartedProof.FromResult(Failed("It NEVER STARTED: removed from the FIFO queue.", null)));
            Assert.Null(NeverStartedProof.FromResult(Failed("It NEVER STARTED.", new JObject { ["never_started"] = false })));
            Assert.Null(NeverStartedProof.FromResult(Failed("x", new JObject { ["never_started"] = "true" })));
        }

        [Fact]
        public void The_structured_flag_proves_it()
        {
            Assert.Equal("timed out; removed", NeverStartedProof.FromResult(
                Failed("timed out; removed", new JObject { ["never_started"] = true, ["transaction_status"] = "not_started" })));
            Assert.Null(NeverStartedProof.FromResult(new JObject { ["isError"] = false, ["structuredContent"] = new JObject { ["never_started"] = true } }));
        }

        // MEASURED live 2026-10-10 (Revit 2023, candidate 2.2.0, verify-live "a queued apply
        // cancelled on the wire"): CallTool catches PipeClient's cancellation and returns an
        // error RESULT whose structuredContent is the transport detail, spread flat. Its
        // proof is cancelled_before_start, set only when the add-in confirmed the removal.
        // Reading never_started alone delivered that cancellation as an ordinary failure
        // instead of the -32800 answer 2.1.7 gave.
        [Fact]
        public void The_transport_proof_on_a_returned_result_proves_it()
        {
            Assert.Equal("Error talking to Revit: removed", NeverStartedProof.FromResult(Failed("Error talking to Revit: removed",
                new JObject { ["code"] = "revit_transport_failed", ["cancelled_before_start"] = true, ["transaction_status"] = "not_started" })));
            Assert.Null(NeverStartedProof.FromResult(Failed("x",
                new JObject { ["code"] = "revit_transport_failed", ["cancelled_before_start"] = false })));
            Assert.Null(NeverStartedProof.FromResult(Failed("x",
                new JObject { ["code"] = "revit_transport_failed", ["cancelled_before_start"] = "true" })));
            Assert.True(NeverStartedProof.AnswerAsCancelled(true, Failed("removed",
                new JObject { ["code"] = "revit_transport_failed", ["cancelled_before_start"] = true })));
        }

        [Fact]
        public void A_cancellation_exception_needs_the_transport_detail()
        {
            var plain = new OperationCanceledException("Cancelled... It NEVER STARTED.");
            Assert.False(NeverStartedProof.FromException(plain));
            var proved = new OperationCanceledException("Cancelled.");
            proved.Data["horizun_transport_detail"] = new JObject { ["cancelled_before_start"] = true };
            Assert.True(NeverStartedProof.FromException(proved));
            var unproved = new OperationCanceledException("Cancelled.");
            unproved.Data["horizun_transport_detail"] = new JObject { ["cancelled_before_start"] = false };
            Assert.False(NeverStartedProof.FromException(unproved));
        }

        [Fact]
        public void A_result_that_came_back_is_delivered_even_if_a_cancel_arrived()
        {
            JObject done = new JObject { ["isError"] = false, ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "ok" }) };
            Assert.False(NeverStartedProof.AnswerAsCancelled(true, done));
            Assert.False(NeverStartedProof.AnswerAsCancelled(true, Failed("it failed on its own", new JObject { ["rollback_status"] = "rolled_back" })));
            Assert.True(NeverStartedProof.AnswerAsCancelled(true, Failed("removed", new JObject { ["never_started"] = true })));
            Assert.False(NeverStartedProof.AnswerAsCancelled(false, Failed("removed", new JObject { ["never_started"] = true })));
        }
    }
}
