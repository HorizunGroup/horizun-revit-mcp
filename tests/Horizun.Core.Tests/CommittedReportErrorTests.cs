// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// FIELD FEEDBACK E1: horizun_execute_plan answered
// "ArgumentException ... KeyValuePair" AND the changes were in the model. Two exits
// could do that after the group committed, and both are closed:
//
//   1. building the plan's own reply (the KeyValuePair defect: new JObject(dict),
//      fixed earlier in SuccessPayloadFromResults - pinned again here), now guarded
//      by PlanLedger.CommittedWithReportError;
//   2. the dispatcher's report stages after ANY command returned, whose exception
//      used to replace the result with a Fail - now PostCommitReport.Record.
//
// The rule both pin: if the commit happened the answer is success, with the report
// failure named inside it; a failure stays a failure.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class CommittedReportErrorTests
    {
        [Fact]
        public void The_original_defect_new_JObject_over_a_dictionary_throws_KeyValuePair()
        {
            var results = new Dictionary<string, JToken> { ["a"] = new JObject { ["x"] = 1 } };
            var ex = Assert.ThrowsAny<ArgumentException>(() => new JObject(results));
            Assert.Contains("KeyValuePair", ex.Message);
            // The ledger's own path does not.
            JObject reply = new PlanLedger().SuccessPayloadFromResults("g", results);
            Assert.Equal(1, (int)reply["results"]["a"]["x"]);
        }

        [Fact]
        public void A_committed_plan_whose_reply_failed_is_still_ok_and_says_committed()
        {
            var ledger = new PlanLedger();
            var data = new JObject();
            ApplicationOutcome.StampApplied(data, ApplicationOutcome.Committed, 2, 2, 2, 0, 0, 0);
            Assert.True(ledger.RecordExecuted(0, "params", "horizun_write_params_verified", true, data, null, out _));

            var results = new JObject { ["params"] = data.DeepClone() };
            JObject reply = ledger.CommittedWithReportError("Horizun: atomic plan", results,
                new ArgumentException("An item with the same key has already been added. Key: params"));

            Assert.Equal("ok", (string)reply["status"]);
            Assert.Equal("committed_with_report_error", (string)reply["outcome"]);
            Assert.Equal(ApplicationOutcome.Committed, (string)reply["transaction_status"]);
            Assert.Equal(1, (int)reply["actions_verified"]);
            Assert.Equal(1, (int)reply["actions_executed"]);
            Assert.Equal("System.ArgumentException", (string)reply["report_error"]["type"]);
            Assert.Contains("same key", (string)reply["report_error"]["message"]);
            Assert.Contains("Do NOT retry", (string)reply["report_error"]["means"]);
            Assert.Single((JArray)reply["actions"]);
            Assert.NotNull(reply["results"]["params"]);
        }

        [Fact]
        public void A_report_stage_that_throws_after_success_keeps_the_success()
        {
            CommandResult result = CommandResult.Ok(new JObject { ["transaction_status"] = "Committed", ["n"] = 3 });
            PostCommitReport.Record(result, "post_command_report",
                new ArgumentException("Could not determine JSON object type for type KeyValuePair`2."));
            Assert.True(result.Success);
            var data = (JObject)result.Data;
            Assert.Equal(3, (int)data["n"]);
            Assert.Equal("post_command_report", (string)data["report_error"]["phase"]);
            Assert.Contains("KeyValuePair", (string)data["report_error"]["message"]);
        }

        [Fact]
        public void An_anonymous_success_payload_is_kept_and_a_failure_is_left_alone()
        {
            CommandResult anon = CommandResult.Ok(new { saved = true });
            PostCommitReport.Record(anon, "post_command_report", new InvalidOperationException("boom"));
            Assert.True(anon.Success);
            Assert.True((bool)((JObject)anon.Data)["saved"]);

            CommandResult failed = CommandResult.Fail("refused");
            PostCommitReport.Record(failed, "post_command_report", new InvalidOperationException("boom"));
            Assert.False(failed.Success);
            Assert.Equal("refused", failed.Error);
        }
    }
}
