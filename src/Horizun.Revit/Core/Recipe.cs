// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// SHIPPED PYTHON, UNDER THE HOST'S CONTRACT.
//
// Some Horizun tools are geometry: split a multi-loop floor, explode a compound
// wall into one wall per layer, level a toposolid against a slab edge. That logic
// is long, it is fiddly, and the versions that work are the ones that have been
// run against real models for months. Rewriting them into C# would not make them
// more correct; it would restart their bug history from zero.
//
// So the algorithm stays Python and the HONESTY stays C#. A recipe is a .py file
// that ships inside the add-in and exposes three functions:
//
//     plan(doc, args)                  -> what WOULD happen. Read-only.
//     apply(doc, args, plan)           -> do it. Runs inside OUR transaction.
//     verify(doc, args, plan, applied)  -> counts RE-READ from the model. Optional.
//
// and this class owns everything that decides whether the answer is true:
//
//   * the transaction, committed through Guard.Commit, so a silent rollback is an
//     error rather than a success with nothing written;
//   * dry_run, which runs plan() and stops — no transaction is ever opened;
//   * the verification pass, which runs AFTER the commit and therefore sees what
//     the model kept, not what apply() believed it did;
//   * Guard.Verify over intended-vs-actual, so the mismatch reaches the caller.
//
// A recipe that returns "created: 40" and a model that reports 37 produces a
// FAILED verification, not a cheerful 40. That is the entire point: the counts a
// caller sees come from the model, and the recipe cannot overrule them.
//
// WHY THIS IS NOT A BACK DOOR AROUND enable_execute_python
// -------------------------------------------------------
// That setting gates arbitrary code ARRIVING FROM A CALLER. Nothing here comes
// from a caller: the recipe name is resolved against a fixed folder beside the
// assembly, `..` and directory separators are refused, only .py resolves, and the
// file was installed by the same signed deploy that installed this DLL. A caller
// chooses WHICH shipped recipe runs and with what typed arguments — exactly the
// choice it already has between commands — and never what the code is. The sha256
// of the file that actually ran goes out in every reply, so "which version of the
// algorithm did this" is answerable after the fact rather than assumed.
//
// A recipe MUST NOT open a transaction. The host owns it; a recipe that opens its
// own would put the commit outside Guard's reach, which is the one thing this
// file exists to prevent. It is checked, and it fails the call.
// -----------------------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Microsoft.Scripting.Hosting;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What a recipe produced, before the host decides whether to believe it.</summary>
    public sealed class RecipeOutcome
    {
        public JToken Planned;
        public JToken Applied;
        public JToken Verified;
        public string RecipeSha256;
        public bool DryRun;

        /// <summary>
        /// Everything the recipe printed. These ports carry their originals' diagnostics —
        /// "could not re-place hosted element", "join failed" — one line per element that
        /// did not go perfectly. In the button they went to a pyRevit console somebody was
        /// looking at. Dropping them here would turn a partial result into a clean-looking
        /// one, so they ride back with the reply.
        /// </summary>
        public string Printed;
    }

    /// <summary>
    /// A recipe run that did not finish, carrying whatever it printed before it stopped.
    /// Those lines are per-element diagnostics — "could not re-place hosted element on
    /// wall 481203" — and they are usually the answer to "why". The reply object never
    /// gets built on this path, so they travel here instead of being dropped.
    /// </summary>
    public sealed class RecipeFailedException : Exception
    {
        public string Printed { get; }

        public RecipeFailedException(Exception inner, string printed)
            : base(inner != null ? inner.Message : "The recipe failed.", inner)
        {
            Printed = printed;
        }
    }

    public static class Recipe
    {
        /// <summary>Recipes live beside the assembly, installed by the deploy that installed it.</summary>
        public static string Folder()
        {
            return Path.Combine(PythonEngine.AssemblyDirectory(), "Recipes");
        }

        /// <summary>
        /// Resolve a recipe name to a file. The name comes from OUR code, never from the
        /// wire — but it is validated anyway, because the cost of being wrong about that
        /// once is arbitrary file execution, and the check is three lines.
        /// </summary>
        private static string PathFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("No recipe name.");
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains(".."))
                throw new InvalidOperationException("Recipe name '" + name + "' is not a bare name.");

            string path = Path.Combine(Folder(), name + ".py");
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    "Recipe '" + name + "' is not installed: " + path + " does not exist. The add-in and its " +
                    "recipes ship together, so this means a partial deploy — reinstall rather than retry.");
            return path;
        }

        /// <summary>
        /// Run a recipe end to end. `intendedKey` and `actualKey` name the count that
        /// apply() claims and the one verify() re-reads; when both are present the reply
        /// carries a Guard.Verify block and a mismatch is a FAILURE, not a footnote.
        /// </summary>
        public static RecipeOutcome Run(Document doc, string name, JObject args, bool dryRun,
                                        string transactionName)
        {
            if (doc == null) throw new InvalidOperationException("No document.");

            string path = PathFor(name);
            string source = File.ReadAllText(path);

            ScriptEngine engine = PythonEngine.Get();
            ScriptScope scope = engine.CreateScope();

            // The recipe folder on the path too, so one recipe may import a shared helper
            // module that ships beside it.
            try
            {
                var paths = new List<string>(engine.GetSearchPaths());
                string folder = Folder();
                if (!paths.Contains(folder)) { paths.Add(folder); engine.SetSearchPaths(paths); }
            }
            catch { }

            // Capture what the recipe prints. Set up BEFORE the module body runs, so a
            // diagnostic emitted at import time is not the one line that goes missing.
            engine.Execute(
                "import sys as __hz_sys, io as __hz_io\n" +
                "__hz_buf = __hz_io.StringIO()\n" +
                "__hz_stdout, __hz_stderr = __hz_sys.stdout, __hz_sys.stderr\n" +
                "__hz_sys.stdout = __hz_buf\n" +
                "__hz_sys.stderr = __hz_buf\n", scope);

            ScriptSource src = engine.CreateScriptSourceFromString(
                source, Microsoft.Scripting.SourceCodeKind.Statements);
            src.Execute(scope);

            object planFn, applyFn, verifyFn, preprocessorFn;
            if (!scope.TryGetVariable("plan", out planFn) || planFn == null)
                throw new InvalidOperationException("Recipe '" + name + "' defines no plan(doc, args).");
            scope.TryGetVariable("apply", out applyFn);
            scope.TryGetVariable("verify", out verifyFn);
            scope.TryGetVariable("failure_preprocessor", out preprocessorFn);

            ObjectOperations ops = engine.Operations;
            object argsPy = ToPython(args);

            var outcome = new RecipeOutcome { DryRun = dryRun, RecipeSha256 = Sha256(path) };

            try
            {
                // ---- 1. plan(): read-only, outside any transaction. ----
                object planned = ops.Invoke(planFn, doc, argsPy);
                outcome.Planned = ToJson(planned);

                if (dryRun)
                {
                    // Nothing was opened, so there is nothing to roll back and nothing to
                    // verify. A dry run reports the plan and says so.
                    return outcome;
                }

                if (applyFn == null)
                    throw new InvalidOperationException(
                        "Recipe '" + name + "' defines no apply(doc, args, plan); it can only be dry-run.");

                // ---- 2. apply(): inside OUR transaction, committed through Guard. ----
                object applied;
                using (var t = new Transaction(doc, transactionName))
                {
                    // A recipe may hand us a failure preprocessor: some of these operations
                    // legitimately raise a warning per element (splitting a wall into layers
                    // produces "Walls overlap" by construction), and a warning nobody
                    // dismisses becomes a modal that stops Revit's UI thread until the caller
                    // times out. Set BEFORE Start, and only ever supplied by the recipe — the
                    // caller cannot ask for warnings to be swallowed.
                    if (preprocessorFn != null)
                    {
                        try
                        {
                            var preprocessor = ops.Invoke(preprocessorFn) as IFailuresPreprocessor;
                            if (preprocessor != null)
                            {
                                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                                fho.SetFailuresPreprocessor(preprocessor);
                                fho.SetClearAfterRollback(true);
                                t.SetFailureHandlingOptions(fho);
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "Recipe '" + name + "' has a failure_preprocessor() that could not be installed: " +
                                ex.Message + ". Nothing was written — running without it would hang on the first " +
                                "warning this recipe expects to suppress.");
                        }
                    }

                    if (t.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException(
                            "Could not start the transaction '" + transactionName + "'. Nothing was written.");

                    try
                    {
                        applied = ops.Invoke(applyFn, doc, argsPy, planned);
                    }
                    catch
                    {
                        try { t.RollBack(); } catch { }
                        throw;
                    }

                    // A recipe that opened its own transaction and left it open would make
                    // the commit below meaningless. Catch it here rather than let the
                    // document go out poisoned for the next command.
                    if (doc.IsModifiable && !t.HasStarted())
                    {
                        try { t.RollBack(); } catch { }
                        throw new InvalidOperationException(
                            "Recipe '" + name + "' left the document modifiable — it opened a transaction of its " +
                            "own. Recipes must not; the host owns the commit so that Guard can prove it landed.");
                    }

                    Guard.Commit(t, transactionName);
                }
                outcome.Applied = ToJson(applied);

                // ---- 3. verify(): AFTER the commit, so it sees what the model kept. ----
                if (verifyFn != null)
                {
                    object verified = ops.Invoke(verifyFn, doc, argsPy, planned, applied);
                    outcome.Verified = ToJson(verified);
                }

                return outcome;
            }
            catch (Exception ex)
            {
                // A run that threw is exactly when the per-element diagnostics matter most,
                // and the outcome object is not coming back to carry them. So they travel
                // with the failure instead.
                throw new RecipeFailedException(ex, Drain(engine, scope));
            }
            finally
            {
                if (outcome.Printed == null) outcome.Printed = Drain(engine, scope);
            }
        }

        /// <summary>
        /// Take what the recipe printed and put stdout back. Safe to call twice: the second
        /// call finds an empty buffer, which is how the catch and the finally can both run.
        /// </summary>
        private static string Drain(ScriptEngine engine, ScriptScope scope)
        {
            try
            {
                engine.Execute(
                    "__hz_printed = __hz_buf.getvalue()\n" +
                    "__hz_buf = __hz_io.StringIO()\n" +
                    "__hz_sys.stdout, __hz_sys.stderr = __hz_stdout, __hz_stderr\n", scope);
                object printed;
                if (scope.TryGetVariable("__hz_printed", out printed) && printed != null)
                {
                    string text = printed.ToString();
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
            }
            catch { }
            return null;
        }

        // ---- Marshalling. Python values in, JSON out; nothing leaks a CLR type name. ----

        /// <summary>A JObject as a plain Python dict, so a recipe reads args the obvious way.</summary>
        private static object ToPython(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.Object:
                    var dict = new IronPython.Runtime.PythonDictionary();
                    foreach (var prop in (JObject)token) dict[prop.Key] = ToPython(prop.Value);
                    return dict;
                case JTokenType.Array:
                    var list = new IronPython.Runtime.PythonList();
                    foreach (JToken item in (JArray)token) list.Add(ToPython(item));
                    return list;
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return null;
                case JTokenType.Integer:
                    return token.Value<long>();
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.Boolean:
                    return token.Value<bool>();
                default:
                    return token.Value<string>();
            }
        }

        /// <summary>
        /// A Python result as JSON. Walked by hand rather than handed to a serializer:
        /// IronPython dictionaries key on object, ElementId is not a number until we make
        /// it one, and a stray CLR object rendered as its type name is not data.
        /// </summary>
        public static JToken ToJson(object value)
        {
            if (value == null) return JValue.CreateNull();

            if (value is string s) return new JValue(s);
            if (value is bool b) return new JValue(b);
            if (value is int || value is long || value is short || value is byte)
                return new JValue(Convert.ToInt64(value));
            if (value is double || value is float || value is decimal)
                return new JValue(Convert.ToDouble(value));

            if (value is ElementId id) return new JValue(Rid.GetId(id));

            if (value is IDictionary map)
            {
                var o = new JObject();
                foreach (DictionaryEntry entry in map)
                {
                    string key = entry.Key == null ? "null" : entry.Key.ToString();
                    o[key] = ToJson(entry.Value);
                }
                return o;
            }

            if (value is IEnumerable seq)
            {
                var a = new JArray();
                foreach (object item in seq) a.Add(ToJson(item));
                return a;
            }

            return new JValue(value.ToString());
        }

        /// <summary>Which version of the algorithm actually ran.</summary>
        private static string Sha256(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(path))
                {
                    var sb = new StringBuilder(64);
                    foreach (byte x in sha.ComputeHash(fs)) sb.Append(x.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return null; }
        }
    }
}
