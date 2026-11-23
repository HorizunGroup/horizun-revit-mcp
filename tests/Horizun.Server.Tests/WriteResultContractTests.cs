using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Horizun.Revit.Transport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class WriteResultContractTests
    {
        // Enumerate production effects: new write tools join this regression automatically.
        public static IEnumerable<object[]> WriteTools() => Contract.All
            .Where(t => t.Effect != ToolEffect.ReadOnly && t.Effect != ToolEffect.HostState)
            .Select(t => new object[] { t.Name });

        private static JObject FailedWrite(string tool, string status) => new JObject
        {
            ["tool"] = tool, ["transaction_status"] = status, ["requested"] = 8,
            ["writes_attempted"] = 8, ["writes_confirmed"] = 0,
            ["rows"] = new JArray(new JObject { ["confirmed"] = false }),
            ["note"] = "The requested constraints remain unchanged.",
            ["application"] = new JObject { ["state"] = status == "Committed" ? "failed" : "rolled_back",
                ["transaction_status"] = status, ["requested"] = 8, ["applied"] = 0, ["verified"] = 0 }
        };

        [Theory]
        [MemberData(nameof(WriteTools))]
        public void Every_write_tool_refuses_rollback_and_zero_of_N_at_both_transport_boundaries(string tool)
        {
            foreach (string status in new[] { "RolledBack", "Committed" })
            {
                JObject data = FailedWrite(tool, status);
                CommandResult command = CommandResult.Ok(data);
                Assert.False(command.Success);
                JObject reply = McpResult.FromPluginReply(PipeEnvelope.Of("write-contract", command), null);
                Assert.True((bool)reply["isError"]);
                Assert.Equal(status == "RolledBack" ? ErrorCodes.TransaccionRevertida : ErrorCodes.ApiRevitExcepcion,
                    (string)reply["structuredContent"]["error_code"]);
                Assert.Equal(status, (string)reply["structuredContent"]["transaction_status"]);
                Assert.Equal(8, (int)reply["structuredContent"]["requested"]);
                Assert.Equal(0, (int)reply["structuredContent"]["writes_confirmed"]);
                Assert.Equal(data["rows"].ToString(), reply["structuredContent"]["rows"].ToString());

                // Older add-in falsely marks success; server must still refuse it.
                JObject legacy = McpResult.FromPluginReply(new JObject { ["success"] = true, ["data"] = data }, null);
                Assert.True((bool)legacy["isError"]);
                Assert.NotNull(legacy["structuredContent"]["error_code"]);
            }
        }

        [Fact]
        public void Rehearsals_no_ops_and_verified_writes_remain_successful()
        {
            var rehearsal = FailedWrite("test", "not_started");
            rehearsal["dry_run"] = true;
            rehearsal["application"]["state"] = "rehearsed";
            Assert.True(CommandResult.Ok(rehearsal).Success);
            var noOp = new JObject { ["transaction_status"] = "not_started", ["requested"] = 0, ["writes_confirmed"] = 0 };
            Assert.True(CommandResult.Ok(noOp).Success);
            var verified = FailedWrite("test", "Committed");
            verified["writes_confirmed"] = 8;
            verified["application"]["applied"] = 8;
            verified["application"]["verified"] = 8;
            verified["application"]["state"] = "verified_applied";
            Assert.True(CommandResult.Ok(verified).Success);
        }

        [Theory]
        [InlineData("verified")]
        [InlineData("created_verified")]
        [InlineData("writes_verified")]
        public void Zero_verified_is_error_even_when_applied_is_positive(string counter)
        {
            var data = new JObject { ["requested"] = 1, ["applied"] = 1, [counter] = 0 };
            Assert.False(CommandResult.Ok(data).Success);
            Assert.True((bool)McpResult.Structured(data, data.ToString())["isError"]);
            data[counter] = 1;
            Assert.True(CommandResult.Ok(data).Success);
        }

        [Theory]
        [InlineData("failed")]
        [InlineData("rolled_back")]
        public void Failed_application_state_is_error_without_relying_on_counters(string state)
        {
            var data = new JObject { ["application"] = new JObject { ["state"] = state } };
            Assert.False(CommandResult.Ok(data).Success);
            Assert.True((bool)McpResult.Structured(data, data.ToString())["isError"]);
        }

        [Fact]
        public void Partial_zero_is_error_without_requested_but_some_verified_is_visible_success()
        {
            var data = new JObject { ["application"] = new JObject { ["state"] = "partial", ["verified"] = 0 } };
            Assert.False(CommandResult.Ok(data).Success);
            data["application"]["verified"] = 1;
            var command = CommandResult.Ok(data);
            Assert.True(command.Success);
            JObject reply = McpResult.FromPluginReply(PipeEnvelope.Of("partial-contract", command), null);
            Assert.False((bool)reply["isError"]);
            Assert.Equal("partial", (string)reply["structuredContent"]["application"]["state"]);
        }

        [Theory]
        [InlineData(null, "failed")]
        [InlineData(false, "failed")]
        [InlineData(null, "rolled_back")]
        [InlineData(false, "rolled_back")]
        [InlineData(null, "partial")]
        [InlineData(false, "partial")]
        public void A_provisional_absence_flag_cannot_hide_failed_applies(bool? dryRun, string state)
        {
            var payload = new JObject { ["provisional_elements_absent"] = true,
                ["transaction_status"] = "RolledBack", ["changes_applied"] = false,
                ["provisional_verification"] = new JArray(),
                ["application"] = new JObject { ["state"] = state, ["requested"] = 1, ["applied"] = 1, ["verified"] = 0 } };
            if (dryRun.HasValue) payload["dry_run"] = dryRun.Value;
            Assert.False(CommandResult.Ok(payload).Success);
            Assert.True((bool)McpResult.Structured(payload, payload.ToString())["isError"]);
        }

        [Fact]
        public void Real_provisional_construction_rehearsal_remains_successful()
        {
            var payload = new JObject { ["dry_run"] = true, ["transaction_status"] = "RolledBack", ["changes_applied"] = false,
                ["provisional_elements_absent"] = true, ["provisional_verification"] = new JArray() };
            Assert.True(CommandResult.Ok(payload).Success);
            ApplicationOutcome.StampRehearsal(payload, 1, 0, 0, 0);
            Assert.True(CommandResult.Ok(payload).Success);
            payload["dry_run"] = false;
            Assert.False(CommandResult.Ok(payload).Success);
            payload.Remove("dry_run");
            Assert.False(CommandResult.Ok(payload).Success);
        }

        [Fact]
        public void Root_subpart_counts_do_not_borrow_the_canonical_application_denominator()
        {
            var payload = new JObject { ["params_set_confirmed"] = 0, ["annotations_verified"] = 0,
                ["deleted"] = 0, ["application"] = ApplicationOutcome.DeclareApplied("Committed", 1, 1, 1, 0, 0, 0) };
            Assert.True(CommandResult.Ok(payload).Success);
            Assert.False((bool)McpResult.Structured(payload, payload.ToString())["isError"]);
        }

        [Fact]
        public void A_nested_rehearsal_rollback_does_not_poison_a_committed_result()
        {
            var payload = new JObject { ["transaction_status"] = "Committed", ["requested"] = 1,
                ["created_verified"] = 1, ["api_rehearsal"] = FailedWrite("test", "RolledBack") };
            Assert.True(CommandResult.Ok(payload).Success);
        }

        [Fact]
        public void A_corroborated_application_rehearsal_without_redundant_dry_run_is_successful()
        {
            var payload = new JObject { ["application"] = new JObject { ["state"] = "rehearsed",
                ["fully_applied"] = false, ["transaction_status"] = "not_started", ["requested"] = 1,
                ["applied"] = 0, ["verified"] = 0, ["unresolved"] = 0, ["failed"] = 0, ["unknown"] = 0 } };
            Assert.True(CommandResult.Ok(payload).Success);
            payload["transaction_status"] = "RolledBack";
            Assert.False(CommandResult.Ok(payload).Success);
            payload.Remove("transaction_status");
            payload["application"]["transaction_status"] = "Committed";
            Assert.False(CommandResult.Ok(payload).Success);
        }

        [Theory]
        [MemberData(nameof(WriteTools))]
        public void Every_write_tool_reports_invalid_or_partial_rehearsals_as_errors(string tool)
        {
            foreach (string invalidKey in new[] { "invalid", "invalid_operations", "application_partial" })
            {
                var payload = new JObject { ["tool"] = tool, ["dry_run"] = true, ["transaction_status"] = "not_started",
                    ["requested"] = 1, ["valid"] = 0, ["errors"] = new JArray(new JObject { ["index"] = 0, ["error"] = "invalid row" }) };
                if (invalidKey == "application_partial") payload["application"] = new JObject { ["state"] = "partial" };
                else payload[invalidKey] = 1;
                CommandResult command = CommandResult.Ok(payload);
                Assert.False(command.Success);
                JObject result = McpResult.FromPluginReply(PipeEnvelope.Of("invalid-rehearsal", command), null);
                Assert.True((bool)result["isError"]);
                Assert.NotNull(result["structuredContent"]["errors"]);
                // 5.0.4: invalid rows are entrada_invalida - the arguments met the schema, the rows' content did not.
                Assert.Equal(invalidKey == "application_partial" ? ErrorCodes.PrecondicionNoCumplida : ErrorCodes.EntradaInvalida,
                    (string)result["structuredContent"]["error_code"]);
                JObject legacy = McpResult.Structured(payload, payload.ToString());
                Assert.True((bool)legacy["isError"]);
            }
        }
    }
}
