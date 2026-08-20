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
        public void The_prelude_and_the_epilogue_are_valid_python()
        {
            ScriptEngine engine = Python.CreateEngine();

            // Compile-only: if either has a syntax error this throws, which is the whole
            // point of the test existing.
            engine.CreateScriptSourceFromString(ScriptPrelude.Prologue, SourceCodeKind.Statements).Compile();
            engine.CreateScriptSourceFromString(ScriptPrelude.Epilogue, SourceCodeKind.Statements).Compile();
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
