using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class PythonSourceSnapshotTests : IDisposable
    {
        [Fact]
        public void AsyncReadOnlyRefusalSuggestsSameRequestSynchronously()
        {
            var request = JObject.Parse("{ 'code':'print(1)', 'read_only':true, 'run_async':true, 'idempotency_key':'example' }");
            var method = typeof(PythonSourceSnapshot).GetMethod("AsyncReadOnlyRefusal");
            Assert.NotNull(method);
            var advice = (JObject)method.Invoke(null, new object[] { request });
            Assert.Contains("TransactionGroup", (string)advice["reason"]);
            Assert.Equal("horizun_execute_python", (string)advice["suggested_call"]["tool"]);
            Assert.True((bool)advice["suggested_call"]["args"]["read_only"]);
            Assert.False((bool)advice["suggested_call"]["args"]["run_async"]);
            Assert.True((bool)request["run_async"]);
        }
        private readonly string dir = Path.Combine(Path.GetTempPath(), "hrz-source-" + Guid.NewGuid().ToString("N"));
        public PythonSourceSnapshotTests() { Directory.CreateDirectory(dir); }
        public void Dispose() { Directory.Delete(dir, true); }

        [Fact]
        public void Timeout_advice_distinguishes_read_only_and_requires_outcome_before_retry()
        {
            string writable = PythonSourceSnapshot.PreflightTimeoutAdvice(false);
            Assert.Contains("cannot predict runtime", writable);
            Assert.Contains("before retrying", writable);
            Assert.Contains("run_async=true", writable);
            Assert.Contains("idempotency_key", writable);
            string readOnly = PythonSourceSnapshot.PreflightTimeoutAdvice(true);
            Assert.Contains("before retrying", readOnly);
            Assert.Contains("cannot be combined with read_only", readOnly);
            Assert.DoesNotContain("run_async=true", readOnly);
        }

        [Fact]
        public void Changed_file_conflicts_but_queued_snapshot_and_restart_replay_are_stable()
        {
            string file = Path.Combine(dir, "driver.py");
            File.WriteAllText(file, "x = 1\n");
            var request = new JObject { ["code_path"] = file, ["target_document"] = "fixture" };
            var first = PythonSourceSnapshot.Resolve(request);
            Assert.Null(first.Error);
            var queued = first.ExecutionRequest(request);
            var ledger = new DurableCommandLedger(() => Path.Combine(dir, "ledger"));
            string Fingerprint(JObject value) => RequestFingerprint.OfOperation("horizun_execute_python", "fixture", value);
            var claim = ledger.Claim("key", "horizun_execute_python", Fingerprint(queued), first.Sha256);
            Assert.True(claim.IsFresh);
            ledger.Complete(claim, CommandResult.Ok(new JObject { ["answer"] = 1 }));
            File.WriteAllText(file, "x = 2\n");
            var changed = PythonSourceSnapshot.Resolve(request);
            var conflict = ledger.Claim("key", "horizun_execute_python", Fingerprint(changed.ExecutionRequest(request)), changed.Sha256);
            Assert.Equal(DurableCommandOutcome.Conflict, conflict.Outcome);
            Assert.Equal(first.Sha256, conflict.PreviousSourceSha256);
            Assert.Contains(changed.Sha256, conflict.Message);
            Assert.Equal("x = 1\n", PythonSourceSnapshot.Resolve(queued).Code);
            var restarted = new DurableCommandLedger(() => Path.Combine(dir, "ledger"));
            Assert.Equal(DurableCommandOutcome.Replay, restarted.Claim("key", "horizun_execute_python", Fingerprint(queued), first.Sha256).Outcome);
            Assert.True(restarted.Claim("new-key", "horizun_execute_python", Fingerprint(changed.ExecutionRequest(request)), changed.Sha256).IsFresh);
        }
        [Fact]
        public void Frozen_file_retains_decoding_evidence_without_reading_the_file_again()
        {
            string file = Path.Combine(dir, "source.py");
            File.WriteAllText(file, "name = 'geometría'\r\n");
            var request = new JObject { ["code_path"] = file };
            var admitted = PythonSourceSnapshot.Resolve(request);
            var frozen = admitted.ExecutionRequest(request);
            File.Delete(file);
            var execution = PythonSourceSnapshot.Resolve(frozen);
            Assert.Null(execution.Error);
            Assert.Equal(admitted.Code, execution.Code);
            Assert.Equal(admitted.Encoding, execution.Encoding);
            Assert.True(execution.NewlinesNormalized);
            Assert.True(execution.ReadNow);
            Assert.Equal(admitted.ExecutionSha256, execution.ExecutionSha256);
        }
        [Fact]
        public void Missing_file_and_escaping_root_are_refused()
        {
            var missing = PythonSourceSnapshot.Resolve(new JObject { ["code_path"] = Path.Combine(dir, "missing.py") });
            Assert.Contains("existing file on the Revit machine", missing.Error);
            Assert.Contains("Nothing ran", missing.Error);
            var missingDirectory = PythonSourceSnapshot.Resolve(new JObject { ["code_path"] = Path.Combine(dir, "absent", "script.py") });
            Assert.Contains("directory exists on the Revit machine", missingDirectory.Error);
            Assert.NotNull(PythonSourceSnapshot.Resolve(new JObject { ["scripts_root"] = dir, ["code_path"] = "../outside.py" }).Error);
        }
        [Fact]
        public void Missing_or_conflicting_sources_explain_how_to_fix_the_request()
        {
            var missing = PythonSourceSnapshot.Resolve(new JObject());
            Assert.Contains("Provide non-empty code or code_path", missing.Error);
            Assert.Contains("Nothing ran", missing.Error);
            var both = PythonSourceSnapshot.Resolve(new JObject { ["code"] = "x=1", ["code_path"] = "script.py" });
            Assert.Contains("remove the other field", both.Error);
            Assert.Contains("Nothing ran", both.Error);
        }

        [Fact]
        public void Oversized_source_recommends_splitting_and_does_not_offer_path_as_a_bypass()
        {
            var source = PythonSourceSnapshot.Resolve(new JObject { ["code"] = new string('x', Horizun.Contracts.Contract.MaxScriptTextChars + 1) });
            Assert.Contains("split the work across separate calls", source.Error);
            Assert.Contains("code_path has the same limit", source.Error);
            Assert.Contains("Nothing ran", source.Error);
        }
        [Fact]
        public void Oversized_file_explains_byte_limit_and_safe_next_step()
        {
            string file = Path.Combine(dir, "large.py");
            int cap = checked(Horizun.Contracts.Contract.MaxScriptTextChars * 4 + 4);
            using (var stream = File.Create(file)) stream.SetLength((long)cap + 1);
            var source = PythonSourceSnapshot.Resolve(new JObject { ["code_path"] = file });
            Assert.Contains(cap.ToString(), source.Error);
            Assert.Contains("bytes", source.Error);
            Assert.Contains("split", source.Error);
            Assert.Contains("Nothing ran", source.Error);
        }
        [Fact]
        public void Includes_are_ordered_frozen_and_change_the_execution_hash()
        {
            string helper=Path.Combine(dir,"helper.py"); File.WriteAllText(helper,"value = 1\n");
            var request=new JObject { ["code"]="result = value",["includes"]=new JArray(helper) };
            var first=PythonSourceSnapshot.Resolve(request);
            Assert.Null(first.Error);
            var queued=first.ExecutionRequest(request);
            File.WriteAllText(helper,"value = 2\n");
            var second=PythonSourceSnapshot.Resolve(request);
            Assert.Equal(first.Sha256,second.Sha256);
            Assert.NotEqual(first.ExecutionSha256,second.ExecutionSha256);
            Assert.Equal(first.ExecutionSha256,PythonSourceSnapshot.Resolve(queued).ExecutionSha256);
            Assert.Equal("value = 1\n",(string)queued["source_includes"][0]["code"]);
        }
        [Fact]
        public void Long_path_and_utf8_are_read()
        {
            string nested = Path.Combine(dir, new string('a', 110), new string('b', 110));
            Directory.CreateDirectory(nested);
            string file = Path.Combine(nested, "source.py");
            Assert.True(file.Length > 260);
            File.WriteAllText(file, "name = 'geometría'\r\n");
            var source = PythonSourceSnapshot.Resolve(new JObject { ["code_path"] = file });
            Assert.Null(source.Error);
            Assert.Equal("name = 'geometría'\n", source.Code);
            var relative = Path.Combine(new string('a', 110), new string('b', 110), "source.py");
            var rooted = PythonSourceSnapshot.Resolve(new JObject { ["scripts_root"] = dir, ["code_path"] = relative });
            Assert.Null(rooted.Error);
            Assert.Equal(source.Sha256, rooted.Sha256);
            var escaped = PythonSourceSnapshot.Resolve(new JObject { ["scripts_root"] = dir,
                ["code_path"] = Path.Combine(new string('a', 110), new string('b', 110), "..", "..", "..", "outside.py") });
            Assert.Contains("escapes scripts_root", escaped.Error);
        }
    }
}
