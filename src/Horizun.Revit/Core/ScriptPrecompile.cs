// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// COMPILE FIRST. horizun_execute_python used to find a syntax error only when
// Execute() compiled the script lazily - after the read-only TransactionGroup had
// started, the prelude had run and a job record was open - and the caller got an
// exception with no cause. Compile() parses and compiles and runs NOTHING: no
// scope, no transaction, no document access. A script that does not compile is
// answered here, as python_error_sintaxis, before anything can touch the model.
//
// Revit-free on purpose (IronPython only), so the property "a syntax error never
// reaches a transaction" is proven by running the real engine in the test suite.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Microsoft.Scripting.Hosting;

namespace Horizun.Revit.Core
{
    public static class ScriptPrecompile
    {
        public sealed class Failure
        {
            /// <summary>The formatted error, with the script's own line number.</summary>
            public string Formatted;
            /// <summary>SyntaxError, or the bare type name of whatever else refused to compile.</summary>
            public string ErrorType;
            public string SourceCode;
            public string SourcePath;
        }

        /// <summary>Null when every source compiles; otherwise the first failure. Includes compile first, as they run first.</summary>
        public static Failure Check(ScriptEngine engine, string code, string path,
                                    IEnumerable<KeyValuePair<string, string>> includes)
        {
            string failingCode = code, failingPath = path;
            try
            {
                if (includes != null)
                    foreach (var include in includes)
                    {
                        failingCode = include.Key;
                        failingPath = include.Value;
                        SourceFor(engine, include.Key, include.Value).Compile();
                    }
                failingCode = code;
                failingPath = path;
                SourceFor(engine, code, path).Compile();
                return null;
            }
            catch (Exception ex)
            {
                string formatted;
                try { formatted = engine.GetService<ExceptionOperations>().FormatException(ex); }
                catch { formatted = ex.Message; }
                return new Failure
                {
                    Formatted = formatted, SourceCode = failingCode, SourcePath = failingPath,
                    ErrorType = ex is Microsoft.Scripting.SyntaxErrorException
                        ? "SyntaxError" : ErrorCodes.TypeNameOf(ex)
                };
            }
        }

        private static ScriptSource SourceFor(ScriptEngine engine, string code, string path)
        {
            var kind = Microsoft.Scripting.SourceCodeKind.Statements;
            if (string.IsNullOrEmpty(path)) return engine.CreateScriptSourceFromString(code ?? "", kind);
            return engine.CreateScriptSourceFromString(code ?? "", path, kind);
        }
    }
}
