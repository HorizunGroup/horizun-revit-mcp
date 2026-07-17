// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// Direct Python access to the Revit API, on the UI thread.
//
// Why this exists when send_code_to_revit (C#) already does:
//   * Iteration speed. Exploring an unknown model is a conversation — read a
//     parameter, look, adjust, read again. Roslyn compiles a whole assembly per
//     round trip; the interpreter does not.
//   * It is what people already write. Dynamo and pyRevit made Python the lingua
//     franca of Revit automation. A team that has pyRevit scripts can paste them.
//   * 228 typed tools will never cover the whole API. This does.
//
// Two deliberate differences from the IronPython bridges we have used:
//   1. The standard library SHIPS. IronPython.StdLib is referenced, so `import
//      json`, `re`, `csv`, `datetime` work. Bridges that omit it force you to
//      hand-roll JSON with string joins — we spent a full day doing exactly that.
//   2. It cannot leave a transaction open. If a script throws inside a
//      Transaction, the document stays modifiable and every later command dies
//      with "Modification of the document is forbidden" — a failure that outlives
//      the request and looks like Revit broke. We roll it back in a finally.
//
// Not sandboxed, by design: it holds the same power as the C# path. The dialog
// guard (McpDialogGuard) is already active while this runs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    public class HorizunExecutePythonHandler : IRevitCommand
    {
        public string Name => "horizun_execute_python";

        public string Description =>
            "Run Python directly against the Revit API, on the UI thread. Pre-injected variables: " +
            "doc (Document), uidoc (UIDocument), uiapp (UIApplication), app (Application). " +
            "Return a value by printing it or by assigning __output__. The Python standard library " +
            "is available (json, re, csv, datetime, math...). Wrap edits in a Transaction; if your " +
            "script throws inside one it is rolled back for you. " +
            "Use this when no typed tool fits — it reaches the whole API. " +
            "Example: from Autodesk.Revit.DB import *; " +
            "__output__ = str(FilteredElementCollector(doc).OfClass(Level).GetElementCount())";

        public string ParametersSchema => @"{
  ""type"":""object"",
  ""required"":[""code""],
  ""properties"":{
    ""code"":{""type"":""string"",""description"":""Python source. doc/uidoc/uiapp/app are injected. Set __output__ or print() to return data.""},
    ""timeout_note"":{""type"":""string"",""description"":""Ignored; kept so callers can annotate long runs.""}
  }
}";

        // One engine per session: creating a ScriptEngine is expensive, and the
        // point of this handler is a fast round trip.
        private static ScriptEngine _engine;
        private static readonly object _engineLock = new object();

        private static ScriptEngine GetEngine()
        {
            if (_engine != null) return _engine;
            lock (_engineLock)
            {
                if (_engine != null) return _engine;
                var eng = Python.CreateEngine();

                // Make the Revit API importable from Python.
                foreach (var asm in new[]
                {
                    typeof(Document).Assembly,        // RevitAPI
                    typeof(UIApplication).Assembly,   // RevitAPIUI
                    typeof(System.Uri).Assembly,      // System
                })
                {
                    try { eng.Runtime.LoadAssembly(asm); } catch { }
                }

                // Point at the bundled stdlib so `import json` resolves. Without
                // this the packaged plugin has the DLLs but no Lib/ on the path.
                try
                {
                    var here = Path.GetDirectoryName(typeof(HorizunExecutePythonHandler).Assembly.Location);
                    var paths = new List<string>(eng.GetSearchPaths());
                    foreach (var candidate in new[] { Path.Combine(here, "Lib"), here })
                    {
                        if (Directory.Exists(candidate) && !paths.Contains(candidate))
                            paths.Add(candidate);
                    }
                    eng.SetSearchPaths(paths);
                }
                catch { }

                _engine = eng;
                return _engine;
            }
        }

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            string code;
            try
            {
                var request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                code = request.Value<string>("code");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }
            if (string.IsNullOrWhiteSpace(code))
                return CommandResult.Fail("'code' is required (the Python source to run).");

            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;

            var engine = GetEngine();
            var scope = engine.CreateScope();
            scope.SetVariable("uiapp", app);
            scope.SetVariable("app", app.Application);
            scope.SetVariable("uidoc", uidoc);
            scope.SetVariable("doc", doc);
            scope.SetVariable("__output__", null);

            string printed = null;
            object output = null;
            string error = null;

            try
            {
                // Capture print() inside Python, not through the runtime's byte
                // stream. Routing it through Runtime.IO with a UTF8 Encoding (or a
                // UTF8 StreamWriter) still came back UTF-16 — print('hola') arrived
                // as "h\0o\0l\0a\0". A StringIO never leaves managed string-land, so
                // there is no encoding to get wrong. It costs one extra Execute and
                // needs no indentation surgery on the caller's code.
                engine.Execute(
                    "import sys as __hz_sys, io as __hz_io\n" +
                    "__hz_buf = __hz_io.StringIO()\n" +
                    "__hz_stdout, __hz_stderr = __hz_sys.stdout, __hz_sys.stderr\n" +
                    "__hz_sys.stdout = __hz_buf\n" +
                    "__hz_sys.stderr = __hz_buf\n", scope);

                var source = engine.CreateScriptSourceFromString(code, Microsoft.Scripting.SourceCodeKind.Statements);
                source.Execute(scope);
                scope.TryGetVariable("__output__", out output);
            }
            catch (Exception ex)
            {
                // Surface the Python traceback, not just the .NET message — the
                // line number is what makes the next attempt cheap.
                try
                {
                    var eo = engine.GetService<ExceptionOperations>();
                    error = eo.FormatException(ex);
                }
                catch { error = ex.Message; }
            }
            finally
            {
                // Read what the script printed and put sys.stdout back, even if the
                // script threw — the output before the error is usually the clue.
                try
                {
                    engine.Execute(
                        "__hz_printed = __hz_buf.getvalue()\n" +
                        "__hz_sys.stdout, __hz_sys.stderr = __hz_stdout, __hz_stderr\n", scope);
                    if (scope.TryGetVariable("__hz_printed", out object p) && p != null)
                        printed = p.ToString();
                }
                catch { }

                // A throw inside an open Transaction leaves the document modifiable
                // and poisons every later command. Undo that here — the request is
                // allowed to fail, the session is not.
                try
                {
                    if (doc != null && doc.IsModifiable)
                    {
                        var t = new Transaction(doc);
                        try { t.Start("Horizun - rollback orphaned transaction"); } catch { }
                        try { t.RollBack(); } catch { }
                        App.DebugLog("horizun_execute_python: script left a transaction OPEN; rolled back. " +
                                     "Wrap edits in using(var t = Transaction(doc)) so a throw cleans up by itself.");
                    }
                }
                catch { }
            }

            if (error != null)
                return CommandResult.Fail(error + (string.IsNullOrEmpty(printed) ? "" : "\n--- stdout before the error ---\n" + printed));

            return CommandResult.Ok(new
            {
                executed = true,
                output = output?.ToString(),
                printed = string.IsNullOrEmpty(printed) ? null : printed
            });
        }
    }
}
