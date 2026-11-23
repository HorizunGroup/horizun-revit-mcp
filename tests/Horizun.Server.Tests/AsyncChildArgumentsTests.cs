using System;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class AsyncChildArgumentsTests
    {
        [Fact]
        public void Scan_apply_receives_derived_key_and_submission_replay_does_not_enqueue_twice()
        {
            var root = Path.Combine(Path.GetTempPath(), "hz-submit-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var ledger = new DurableCommandLedger(() => root);
                var request = new JObject { ["tool"] = "horizun_apply_scan_plan", ["arguments"] = new JObject { ["dry_run"] = false, ["idempotency_key"] = "caller-child" } };
                string fingerprint = RequestFingerprint.OfOperation("horizun_submit_job", "doc", request, "idempotency_key", "confirmation_token");
                int executions = 0;
                var first = ledger.Claim("wrapper", "horizun_submit_job", fingerprint);
                Assert.True(first.IsFresh);
                var child = AsyncChildArguments.Prepare((JObject)request["arguments"], "wrapper");
                Assert.False((bool)child["dry_run"]);
                Assert.False(string.IsNullOrWhiteSpace((string)child["idempotency_key"]));
                Assert.NotEqual("caller-child", (string)child["idempotency_key"]);
                Assert.Equal((string)child["idempotency_key"], (string)AsyncChildArguments.Prepare((JObject)request["arguments"], "wrapper")["idempotency_key"]);
                executions++;
                ledger.Complete(first, CommandResult.Ok(new JObject { ["job_id"] = "job-1" }));
                var retry = ledger.Claim("wrapper", "horizun_submit_job", fingerprint);
                if (retry.IsFresh) executions++;
                Assert.Equal(DurableCommandOutcome.Replay, retry.Outcome);
                Assert.Equal(1, executions);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
