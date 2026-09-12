using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class PythonSourceSnapshotTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "hrz-source-" + Guid.NewGuid().ToString("N"));
        public PythonSourceSnapshotTests() { Directory.CreateDirectory(dir); }
        public void Dispose() { Directory.Delete(dir, true); }

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
        public void Missing_file_and_escaping_root_are_refused()
        {
            Assert.NotNull(PythonSourceSnapshot.Resolve(new JObject { ["code_path"] = Path.Combine(dir, "missing.py") }).Error);
            Assert.NotNull(PythonSourceSnapshot.Resolve(new JObject { ["scripts_root"] = dir, ["code_path"] = "../outside.py" }).Error);
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
