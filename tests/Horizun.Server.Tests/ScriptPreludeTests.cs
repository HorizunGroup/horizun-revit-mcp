// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The prelude is PYTHON, and nothing else in the build can parse it (5.25).
//
// Every execute_python call runs it first. A missing colon in it does not fail a
// compile, does not fail a deploy, and does not fail until a person inside Revit
// runs a script - at which point EVERY script on that machine is broken and the
// error points at code the caller did not write. So the same engine that will run
// it in Revit runs it here, against stand-ins for the three objects the command
// injects.
//
// The behaviour pinned below is the part a script depends on:
//   * revit_raised(since) hands back real dicts, windowed from the index given;
//   * a run nobody watched RAISES rather than answering "nothing happened";
//   * dialog_answer() releases its scope when the block ends - including when the
//     block ends by throwing, which is exactly when a leaked "dismiss" would go
//     on answering OK to every dialog for the rest of a 250-model batch.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using IronPython.Hosting;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ScriptPreludeTests
    {
        /// <summary>Stands in for Job: the command injects the real one as __hz_job.</summary>
        public sealed class FakeJob
        {
            public readonly List<string> Labels = new List<string>();
            public void Write(string label, object done, object total) { Labels.Add(label); }
        }

        /// <summary>Stands in for the scope Interference.WithDialogAnswer hands back.</summary>
        public sealed class FakeScope : IDisposable
        {
            public bool Disposed;
            public void Dispose() { Disposed = true; }
        }

        private static ScriptScope Prepared(ScriptEngine engine, Func<int, string> raised,
                                            Func<string, IDisposable> dialogScope, FakeJob job)
        {
            ScriptScope scope = engine.CreateScope();
            scope.SetVariable("__hz_job", job);
            scope.SetVariable("__hz_raised", raised);
            scope.SetVariable("__hz_dialog_scope", dialogScope);
            engine.Execute(ScriptPrelude.Prologue, scope);
            return scope;
        }

        private static readonly string TwoDialogs =
            "[{\"kind\":\"dialog\",\"description\":\"Dialog_Revit_DocWarnDialog\",\"while\":\"MOD-001\"}," +
            "{\"kind\":\"dialog\",\"description\":\"Dialog_Revit_LinksNotFound\",\"while\":\"MOD-002\"}]";

        [Fact]
        public void Revit_imports_and_portable_helpers_are_available_to_scripts()
        {
            var engine = Python.CreateEngine();
            var scope = engine.CreateScope();
            engine.Execute(@"
import sys, types
a = types.ModuleType('Autodesk'); a.__path__ = []
r = types.ModuleType('Autodesk.Revit'); r.__path__ = []
db = types.ModuleType('Autodesk.Revit.DB')
class Wall: pass
db.Wall = Wall
a.Revit = r; r.DB = db
sys.modules['Autodesk'] = a; sys.modules['Autodesk.Revit'] = r; sys.modules['Autodesk.Revit.DB'] = db
", scope);
            scope.SetVariable("__hz_import_revit", true);
            engine.Execute(ScriptPrelude.Prologue, scope);
            engine.Execute(@"
assert Autodesk.Revit.DB.Wall is Wall
import Autodesk
from Autodesk.Revit.DB import Wall
assert clr is not None
class Old: ParameterType = 'legacy'
class New:
    def GetDataType(self): return 'forge'
assert horizun.param_data_type(Old()) == 'legacy'
assert horizun.param_data_type(New()) == 'forge'
class Param:
    def __init__(self, kind): self.StorageType = kind
class FamilyType:
    def AsDouble(self, p): return 1.5
    def AsInteger(self, p): return 2
    def AsString(self, p): return 'x'
    def AsElementId(self, p): return 3
for kind, expected in [('Double', 1.5), ('Integer', 2), ('String', 'x'), ('ElementId', 3)]:
    assert horizun.family_type_value(FamilyType(), Param(kind)) == expected
w = Wall()
assert horizun.of_class([w, object()], Wall) == [w]
assert list(horizun.iter_safe(None)) == []
assert list(horizun.iter_safe([1, 2])) == [1, 2]
import json
path = 'C:\\example\\file.rvt'
assert json.loads(horizun.safe_json_path(path)) == path
", scope);
            engine.Execute(ScriptPrelude.Epilogue, scope);
        }

        [Fact]
        public void The_prelude_and_the_epilogue_are_valid_python()
        {
            ScriptEngine engine = Python.CreateEngine();

            // Compile-only: if either has a syntax error this throws, which is the whole
            // point of the test existing.
            engine.CreateScriptSourceFromString(ScriptPrelude.Prologue, SourceCodeKind.Statements).Compile();
            engine.CreateScriptSourceFromString(ScriptPrelude.Epilogue, SourceCodeKind.Statements).Compile();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Transaction_helper_commits_or_rolls_back_and_always_disposes(bool nested, bool fail)
        {
            var engine = Python.CreateEngine();
            var scope = Prepared(engine, _ => "[]", _ => new FakeScope(), new FakeJob());
            scope.SetVariable("nested", nested);
            scope.SetVariable("fail_body", fail);
            engine.Execute(@"
import sys, types
fake_db = types.ModuleType('Autodesk.Revit.DB')
class Status:
    Started = 'Started'
    Committed = 'Committed'
    RolledBack = 'RolledBack'
class Doc:
    IsModifiable = nested
doc = Doc()
events = []
class Transaction:
    def __init__(self, doc, name=None):
        events.append(self.__class__.__name__)
        self.state = None
    def Start(self):
        self.state = Status.Started
        return self.state
    def Commit(self):
        events.append('commit')
        self.state = Status.Committed
        return self.state
    def RollBack(self):
        events.append('rollback')
        self.state = Status.RolledBack
        return self.state
    def GetStatus(self):
        return self.state
    def Dispose(self):
        events.append('dispose')
class SubTransaction(Transaction):
    pass
fake_db.Transaction = Transaction
fake_db.SubTransaction = SubTransaction
fake_db.TransactionStatus = Status
sys.modules['Autodesk'] = types.ModuleType('Autodesk')
sys.modules['Autodesk.Revit'] = types.ModuleType('Autodesk.Revit')
sys.modules['Autodesk.Revit.DB'] = fake_db
caught = False
try:
    with horizun.transaction('fixture'):
        if fail_body:
            raise ValueError('body failure')
except ValueError:
    caught = True
trace = ','.join(events)
", scope);
            Assert.Equal(fail, (bool)scope.GetVariable("caught"));
            Assert.Equal((nested ? "SubTransaction" : "Transaction") + (fail ? ",rollback,dispose" : ",commit,dispose"), (string)scope.GetVariable("trace"));
        }

        [Fact]
        public void Report_helper_preserves_evidence_without_inventing_verification()
        {
            var engine = Python.CreateEngine();
            var scope = Prepared(engine, _ => "[]", _ => new FakeScope(), new FakeJob());
            engine.Execute("horizun.report(created_ids=[42], summary='placed')\nunchecked = __output__['verification']['checked']\nhorizun.report('self_reported_verified', created_ids=[42], evidence=[{'z': 10.32}])\nchecked = __output__['verification']['checked']\n", scope);
            Assert.False((bool)scope.GetVariable("unchecked"));
            Assert.True((bool)scope.GetVariable("checked"));
        }

        [Fact]
        public void Id_value_reads_an_element_id_on_every_supported_revit()
        {
            // Course rehearsal 2026-10-03: an agent-written script used ElementId.IntegerValue
            // and failed on Revit 2026, where only .Value exists. 2023 has only IntegerValue.
            var engine = Python.CreateEngine();
            var scope = Prepared(engine, _ => "[]", _ => new FakeScope(), new FakeJob());
            engine.Execute(
                "class Id2026(object):\n" +
                "    Value = 9007199254740993\n" +
                "class Id2023(object):\n" +
                "    IntegerValue = 4242\n" +
                "modern = horizun.id_value(Id2026())\n" +
                "legacy = horizun.id_value(Id2023())\n" +
                "nothing = horizun.id_value(None)\n", scope);
            Assert.Equal("9007199254740993", scope.GetVariable("modern").ToString());
            Assert.Equal("4242", scope.GetVariable("legacy").ToString());
            Assert.Null(scope.GetVariable("nothing"));
        }

        [Fact]
        public void Id_value_hands_back_a_python_int_that_json_can_serialise()
        {
            // .Value is a System.Int64 in 2024+; json.dumps refuses Int64. The helper
            // recommended for portable ids must not reintroduce the TypeError.
            var engine = Python.CreateEngine();
            var scope = Prepared(engine, _ => "[]", _ => new FakeScope(), new FakeJob());
            engine.Execute(
                "import System, json\n" +
                "class Id2026(object):\n" +
                "    Value = System.Int64(9007199254740993)\n" +
                "v = horizun.id_value(Id2026())\n" +
                "is_int = type(v) is int\n" +
                "text = json.dumps({'id': v})\n", scope);
            Assert.True((bool)scope.GetVariable("is_int"));
            Assert.Equal("{\"id\": 9007199254740993}", (string)scope.GetVariable("text"));
        }

        [Fact]
        public void Json_serialises_the_net_values_the_revit_api_returns()
        {
            // Field signals 2026-10: TypeError '... is not JSON serializable' on
            // ElementId.Value (Int64) and Color.Red (Byte) at the last line of read-only
            // scripts that had finished their work.
            var engine = Python.CreateEngine();
            var scope = Prepared(engine, _ => "[]", _ => new FakeScope(), new FakeJob());
            engine.Execute(
                "import System, json, io\n" +
                "vals = {'int64': System.Int64(5), 'byte': System.Byte(200), 'u32': System.UInt32(7),\n" +
                "        'single': System.Single(1.5), 'guid': System.Guid.Empty,\n" +
                "        'enum': System.StringSplitOptions.RemoveEmptyEntries, 'nested': [System.Int16(3)]}\n" +
                "text = json.dumps(vals, sort_keys=True)\n" +
                "buf = io.StringIO()\n" +
                "json.dump({'a': System.Int64(9)}, buf)\n" +
                "dumped = buf.getvalue()\n", scope);
            Assert.Equal(
                "{\"byte\": 200, \"enum\": \"RemoveEmptyEntries\", \"guid\": \"00000000-0000-0000-0000-000000000000\", " +
                "\"int64\": 5, \"nested\": [3], \"single\": 1.5, \"u32\": 7}",
                (string)scope.GetVariable("text"));
            Assert.Equal("{\"a\": 9}", (string)scope.GetVariable("dumped"));
        }

        [Fact]
        public void Json_still_refuses_what_it_cannot_convert_and_a_script_encoder_still_wins()
        {
            // Only lossless conversions are added: an arbitrary object is still a
            // TypeError (no silent str()), and a script's own JSONEncoder overrides ours.
            var engine = Python.CreateEngine();
            var scope = Prepared(engine, _ => "[]", _ => new FakeScope(), new FakeJob());
            engine.Execute(
                "import json\n" +
                "try:\n" +
                "    json.dumps({'x': object()})\n" +
                "    refused = False\n" +
                "except TypeError:\n" +
                "    refused = True\n" +
                "class Mine(json.JSONEncoder):\n" +
                "    def default(self, o):\n" +
                "        return 'mine'\n" +
                "custom = json.dumps({'x': object()}, cls=Mine)\n", scope);
            Assert.True((bool)scope.GetVariable("refused"));
            Assert.Equal("{\"x\": \"mine\"}", (string)scope.GetVariable("custom"));
        }

        [Fact]
        public void Json_hook_is_installed_once_when_the_prelude_runs_again_on_the_same_engine()
        {
            // The command keeps ONE engine per Revit session, so the prelude runs many
            // times against the same json module; it must not wrap itself in layers.
            var engine = Python.CreateEngine();
            Prepared(engine, _ => "[]", _ => new FakeScope(), new FakeJob());
            var second = Prepared(engine, _ => "[]", _ => new FakeScope(), new FakeJob());
            engine.Execute(
                "import json, System\n" +
                "first = json.JSONEncoder.default\n" +
                "_hz_install_json_net_values()\n" +
                "same = json.JSONEncoder.default is first\n" +
                "text = json.dumps(System.Int64(1))\n", second);
            Assert.True((bool)second.GetVariable("same"));
            Assert.Equal("1", (string)second.GetVariable("text"));
        }

        [Fact]
        public void Checkpoint_reaches_the_record_and_stdout_is_captured_not_lost()
        {
            ScriptEngine engine = Python.CreateEngine();
            var job = new FakeJob();
            ScriptScope scope = Prepared(engine, _ => "[]", a => new FakeScope(), job);

            engine.Execute("checkpoint('model 2 of 3', 2, 3)\nprint('hola')\n", scope);
            engine.Execute(ScriptPrelude.Epilogue, scope);

            Assert.Equal(new[] { "model 2 of 3" }, job.Labels);
            Assert.Equal("hola\n", (string)scope.GetVariable("__hz_printed"));
        }

        [Theory]
        [InlineData("__hz_buf = None")]
        [InlineData("__hz_buf.close()")]
        public void Stdout_and_stderr_are_restored_even_when_the_script_damages_the_capture_buffer(
            string damage)
        {
            ScriptEngine engine = Python.CreateEngine();
            ScriptScope sys = engine.GetSysModule();
            object stdoutBefore = sys.GetVariable("stdout");
            object stderrBefore = sys.GetVariable("stderr");
            PythonStreamRestorer restorer = PythonStreamRestorer.Capture(engine);
            ScriptScope scope = Prepared(engine, _ => "[]", a => new FakeScope(), new FakeJob());

            // Adversarial script code is allowed to see globals and can rebind or close
            // the capture buffer. getvalue() then raises; restoration must live in a
            // finally outside that read, or the whole Python process keeps the broken
            // buffer as sys.stdout/sys.stderr after this command returns.
            engine.Execute(damage, scope);
            engine.Execute(ScriptPrelude.Epilogue, scope);
            Assert.True(restorer.TryRestore(out string restorationError), restorationError);

            Assert.Same(stdoutBefore, sys.GetVariable("stdout"));
            Assert.Same(stderrBefore, sys.GetVariable("stderr"));
            Assert.Null(scope.GetVariable("__hz_printed"));
            Assert.False(string.IsNullOrWhiteSpace((string)scope.GetVariable("__hz_capture_error_text")));
        }

        [Fact]
        public void Csharp_restores_streams_even_if_the_script_rebinds_and_deletes_every_python_alias()
        {
            ScriptEngine engine = Python.CreateEngine();
            ScriptScope sys = engine.GetSysModule();
            object stdoutBefore = sys.GetVariable("stdout");
            object stderrBefore = sys.GetVariable("stderr");
            PythonStreamRestorer restorer = PythonStreamRestorer.Capture(engine);
            ScriptScope scope = Prepared(engine, _ => "[]", a => new FakeScope(), new FakeJob());

            engine.Execute(
                "import sys\n" +
                "del __hz_sys\n" +
                "del __hz_stdout\n" +
                "del __hz_stderr\n" +
                "sys.stdout = None\n" +
                "sys.stderr = None\n", scope);

            // Epilogue no longer owns process restoration; it may succeed or fail based
            // only on capture-buffer state. The C# references remain authoritative.
            try { engine.Execute(ScriptPrelude.Epilogue, scope); } catch { }
            Assert.True(restorer.TryRestore(out string restorationError), restorationError);

            Assert.Same(stdoutBefore, sys.GetVariable("stdout"));
            Assert.Same(stderrBefore, sys.GetVariable("stderr"));
        }

        [Fact]
        public void Revit_raised_returns_dicts_a_script_can_read()
        {
            ScriptEngine engine = Python.CreateEngine();
            ScriptScope scope = Prepared(engine, _ => TwoDialogs, a => new FakeScope(), new FakeJob());

            engine.Execute(
                "__r = revit_raised()\n" +
                "__n = len(__r)\n" +
                "__first = __r[0]['description']\n" +
                "__where = __r[1]['while']\n", scope);

            Assert.Equal(2, (int)scope.GetVariable("__n"));
            Assert.Equal("Dialog_Revit_DocWarnDialog", (string)scope.GetVariable("__first"));
            Assert.Equal("MOD-002", (string)scope.GetVariable("__where"));
        }

        [Fact]
        public void The_since_argument_is_passed_through_as_the_caller_gave_it()
        {
            // The attribution the batch case needs: len() before the open, the same
            // number after it. If this argument were dropped or coerced wrongly, every
            // dialog would be attributed to every model that followed.
            ScriptEngine engine = Python.CreateEngine();
            var asked = new List<int>();
            ScriptScope scope = Prepared(engine, since => { asked.Add(since); return "[]"; },
                                         a => new FakeScope(), new FakeJob());

            engine.Execute("revit_raised()\nrevit_raised(7)\n", scope);

            Assert.Equal(new[] { 0, 7 }, asked);
        }

        [Fact]
        public void A_run_that_was_never_watched_raises_instead_of_reporting_a_quiet_run()
        {
            ScriptEngine engine = Python.CreateEngine();
            ScriptScope scope = Prepared(engine, _ => null, a => new FakeScope(), new FakeJob());

            var ex = Assert.ThrowsAny<Exception>(() => engine.Execute("revit_raised()\n", scope));

            string message = engine.GetService<ExceptionOperations>().FormatException(ex);
            Assert.Contains("NOT the same as Revit raising nothing", message);
        }

        [Fact]
        public void Dialog_answer_releases_its_scope_at_the_end_of_the_block()
        {
            ScriptEngine engine = Python.CreateEngine();
            var opened = new List<FakeScope>();
            ScriptScope scope = Prepared(engine, _ => "[]",
                                         a => { var s = new FakeScope(); opened.Add(s); return s; },
                                         new FakeJob());

            engine.Execute("with dialog_answer('dismiss'):\n    pass\n", scope);

            Assert.Single(opened);
            Assert.True(opened[0].Disposed);
        }

        [Fact]
        public void It_releases_the_scope_even_when_the_block_throws()
        {
            // The one that matters: a leaked 'dismiss' answers OK to every later dialog,
            // and Revit reads OK on close-with-changes as SAVE.
            ScriptEngine engine = Python.CreateEngine();
            var opened = new List<FakeScope>();
            ScriptScope scope = Prepared(engine, _ => "[]",
                                         a => { var s = new FakeScope(); opened.Add(s); return s; },
                                         new FakeJob());

            Assert.ThrowsAny<Exception>(() => engine.Execute(
                "with dialog_answer('dismiss'):\n    raise ValueError('boom')\n", scope));

            Assert.Single(opened);
            Assert.True(opened[0].Disposed);
        }

        [Fact]
        public void The_block_does_not_swallow_the_exception_it_was_wrapped_around()
        {
            // __exit__ returning False is load-bearing: returning True - or returning
            // from a finally - would make `with dialog_answer(...)` silently eat a
            // failed open, which is the opposite of what this whole story is for.
            ScriptEngine engine = Python.CreateEngine();
            ScriptScope scope = Prepared(engine, _ => "[]", a => new FakeScope(), new FakeJob());

            var ex = Assert.ThrowsAny<Exception>(() => engine.Execute(
                "with dialog_answer('dismiss'):\n    raise ValueError('boom')\n", scope));

            Assert.Contains("boom", engine.GetService<ExceptionOperations>().FormatException(ex));
        }

        [Fact]
        public void A_bad_answer_word_reaches_the_host_which_is_the_one_that_refuses_it()
        {
            ScriptEngine engine = Python.CreateEngine();
            ScriptScope scope = Prepared(engine, _ => "[]",
                                         a => throw new ArgumentException(
                                             "dialog_answer() takes 'cancel' or 'dismiss' (got '" + a + "')."),
                                         new FakeJob());

            var ex = Assert.ThrowsAny<Exception>(() => engine.Execute(
                "with dialog_answer('acknowledge'):\n    pass\n", scope));

            Assert.Contains("takes 'cancel' or 'dismiss'",
                            engine.GetService<ExceptionOperations>().FormatException(ex));
        }
    }
}
