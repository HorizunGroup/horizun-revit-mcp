// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Review 2026-11-20, I3: after a typed write that failed AFTER writing started, the
// server refuses horizun_execute_python (not its preflight) until the model is re-read
// or the write is undone.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class PartialWriteGateTests : IDisposable
    {
        public PartialWriteGateTests() { PartialWriteGate.Reset(); }
        public void Dispose() { PartialWriteGate.Reset(); }

        private static JObject Failed(bool topLevel) => new JObject
        {
            ["isError"] = true,
            ["structuredContent"] = topLevel
                ? new JObject { ["error_code"] = "transaccion_revertida", ["write_started"] = true }
                : new JObject { ["error_code"] = "transaccion_revertida",
                                ["fallback"] = new JObject { ["allowed"] = false, ["write_started"] = true } }
        };

        private static readonly JObject Run = new JObject { ["code"] = "print(1)" };

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_partial_write_refuses_python_until_the_model_is_read_again(bool topLevel)
        {
            PartialWriteGate.Observe("horizun_create_elements", Failed(topLevel));
            string refusal = PartialWriteGate.Refusal("horizun_execute_python", Run);
            Assert.NotNull(refusal);
            Assert.Contains("horizun_create_elements", refusal);
            Assert.Contains("transaccion_revertida", refusal);
            Assert.Contains("Nothing was run", refusal);

            PartialWriteGate.Observe("horizun_query_model", new JObject { ["isError"] = false });
            Assert.Null(PartialWriteGate.Refusal("horizun_execute_python", Run));
        }

        [Fact]
        public void An_unknown_start_counts_as_started_and_a_clean_rollback_does_not()
        {
            PartialWriteGate.Observe("horizun_write_params_verified", new JObject
            {
                ["isError"] = true,
                ["structuredContent"] = new JObject { ["write_started"] = null, ["transaction_status"] = "unknown" }
            });
            Assert.NotNull(PartialWriteGate.Refusal("horizun_execute_python", Run));

            PartialWriteGate.Reset();
            PartialWriteGate.Observe("horizun_create_elements", new JObject
            {
                ["isError"] = true,
                ["structuredContent"] = new JObject { ["write_started"] = true, ["changes_applied"] = false,
                                                      ["rollback_status"] = "RolledBack" }
            });
            Assert.Null(PartialWriteGate.Refusal("horizun_execute_python", Run));

            PartialWriteGate.Observe("horizun_create_elements", new JObject { ["isError"] = true, ["structuredContent"] = new JObject() });
            Assert.Null(PartialWriteGate.Pending());

            // Every error is stamped with fallback.write_started=null by default; that alone is not a start (review r2).
            PartialWriteGate.Observe("horizun_query_model", new JObject
            {
                ["isError"] = true,
                ["structuredContent"] = new JObject { ["error_code"] = "esquema_invalido",
                    ["fallback"] = new JObject { ["allowed"] = false, ["reason"] = "esquema_invalido", ["write_started"] = null } }
            });
            Assert.Null(PartialWriteGate.Pending());
        }

        [Fact]
        public void A_read_that_fails_never_arms_it_and_the_same_write_applied_clears_it()
        {
            // review R1: a timeout is stamped write_started=null on any tool; a read cannot have written.
            var timedOut = new JObject { ["isError"] = true, ["structuredContent"] = new JObject { ["write_started"] = null } };
            PartialWriteGate.Observe("horizun_audit_model", timedOut);
            PartialWriteGate.Observe("horizun_model_scan", timedOut);
            PartialWriteGate.Observe("no_such_tool", timedOut);
            Assert.Null(PartialWriteGate.Pending());

            PartialWriteGate.Observe("horizun_create_elements", timedOut);
            Assert.NotNull(PartialWriteGate.Pending());
            PartialWriteGate.Observe("horizun_create_elements", new JObject
            {
                ["isError"] = false, ["structuredContent"] = new JObject { ["dry_run"] = false }
            });
            Assert.Null(PartialWriteGate.Pending());
        }

        [Fact]
        public void Preflight_and_other_tools_are_still_admitted()
        {
            PartialWriteGate.Observe("horizun_create_elements", Failed(true));
            Assert.Null(PartialWriteGate.Refusal("horizun_execute_python",
                new JObject { ["code"] = "print(1)", ["preflight"] = true }));
            Assert.Null(PartialWriteGate.Refusal("horizun_query_model", new JObject()));
        }

        [Fact]
        public void A_failed_reread_or_a_failure_before_writing_changes_nothing()
        {
            PartialWriteGate.Observe("horizun_create_elements", new JObject
            {
                ["isError"] = true,
                ["structuredContent"] = new JObject { ["fallback"] = new JObject { ["allowed"] = true, ["write_started"] = false } }
            });
            Assert.Null(PartialWriteGate.Pending());

            PartialWriteGate.Observe("horizun_create_elements", Failed(true));
            PartialWriteGate.Observe("horizun_query_model", new JObject { ["isError"] = true });
            Assert.NotNull(PartialWriteGate.Refusal("horizun_execute_python", Run));
            // Another tool's success, or the same tool's rehearsal, leaves it armed.
            PartialWriteGate.Observe("horizun_write_params_verified", new JObject { ["isError"] = false });
            Assert.NotNull(PartialWriteGate.Refusal("horizun_execute_python", Run));
            PartialWriteGate.Observe("horizun_create_elements", new JObject
            {
                ["isError"] = false, ["structuredContent"] = new JObject { ["dry_run"] = true }
            });
            Assert.NotNull(PartialWriteGate.Refusal("horizun_execute_python", Run));
            PartialWriteGate.Observe("horizun_undo", new JObject { ["isError"] = false });
            Assert.Null(PartialWriteGate.Refusal("horizun_execute_python", Run));
        }
    }
}
