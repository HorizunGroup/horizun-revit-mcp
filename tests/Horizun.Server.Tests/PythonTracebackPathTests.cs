// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// A traceback that names the file (5.27).
//
// execute_python hands the engine a NAMED script source when the code came from
// code_path. That is not decoration: an unnamed source reports every frame as
// "<string>", and a 535-line driver whose run fails somewhere in the middle then
// points nowhere at all.
//
// It is proved against a real IronPython engine - the one this project already
// carries for the recipe syntax check - because the claim is about the hosting
// API's behaviour, not about our arithmetic. A test of our own code would only
// prove that we call the overload we call.
// -----------------------------------------------------------------------------
using System;
using IronPython.Hosting;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Xunit;

namespace Horizun.Server.Tests
{
    public class PythonTracebackPathTests
    {
        private const string Fails = "def audit(model):\n    raise ValueError('boom')\naudit('X')\n";

        private static string FormattedFailure(ScriptEngine engine, ScriptSource source)
        {
            try
            {
                source.Execute(engine.CreateScope());
            }
            catch (Exception ex)
            {
                return engine.GetService<ExceptionOperations>().FormatException(ex);
            }
            Assert.Fail("the script was supposed to raise");
            return null;
        }

        [Fact]
        public void A_named_source_puts_the_real_file_in_the_traceback()
        {
            ScriptEngine engine = Python.CreateEngine();
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hz_audit_driver.py");

            string named = FormattedFailure(engine,
                engine.CreateScriptSourceFromString(Fails, path, SourceCodeKind.Statements));

            Assert.Contains("hz_audit_driver.py", named);
            Assert.Contains("ValueError", named);
        }

        [Fact]
        public void An_unnamed_source_reports_string_which_is_what_this_replaces()
        {
            ScriptEngine engine = Python.CreateEngine();

            string unnamed = FormattedFailure(engine,
                engine.CreateScriptSourceFromString(Fails, SourceCodeKind.Statements));

            // The state of the world before code_path: every frame, every script.
            Assert.Contains("<string>", unnamed);
        }
    }
}
