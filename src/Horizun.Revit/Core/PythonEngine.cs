// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// ONE IronPython engine for the whole add-in.
//
// It used to be a private static inside ExecutePythonCommand, which was correct
// while that command was the only thing that ran Python. It is not any more: the
// recipe-backed tools (Recipe.cs) run SHIPPED Python — code that travels with the
// add-in and is hashed with the contract — and they must not pay for a second
// ScriptEngine, nor duplicate the two pieces of setup below that took a live
// Revit to discover.
//
// Sharing the engine is safe because nothing here is per-call: every caller gets
// its OWN ScriptScope, and a scope is the unit of isolation. What is shared is the
// expensive, immutable part — the loaded assemblies and the stdlib search path.
//
// The two non-obvious lines, both of which cost a debugging session:
//
//   * Codepage 1252. IronPython asks the runtime for the ANSI codepage while it
//     builds the engine. On .NET 5+ that codepage is not in the base library, so
//     CreateEngine throws "No data is available for encoding 1252" unless the
//     provider is registered first. It looked intermittent because another add-in
//     in the same Revit process sometimes registered it before us.
//
//   * The search path. Without it `import json` fails, because the bundled stdlib
//     sits beside the assembly and IronPython does not look there by itself.
//
// IMPORTANT: this class is NOT a capability gate. Being able to create an engine
// is not permission to run caller-supplied code — that decision belongs to
// Settings.ExecutePythonEnabled, and it stays in ExecutePythonCommand where the
// caller's code arrives. Shipped recipes are a different thing entirely and are
// deliberately not subject to it; see Recipe.cs for why that is not a loophole.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;

namespace Horizun.Revit.Core
{
    public static class PythonEngine
    {
        private static ScriptEngine _engine;
        private static readonly object _lock = new object();

        /// <summary>
        /// The process-wide engine, created on first use. Creating a ScriptEngine is
        /// expensive and the point of every command that uses one is a fast round trip.
        /// </summary>
        public static ScriptEngine Get()
        {
            if (_engine != null) return _engine;
            lock (_lock)
            {
                if (_engine != null) return _engine;

#if NET
                try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
                catch { }
#endif
                ScriptEngine eng = Python.CreateEngine();

                foreach (var asm in new[]
                {
                    typeof(Document).Assembly,       // RevitAPI
                    typeof(UIApplication).Assembly,  // RevitAPIUI
                    typeof(System.Uri).Assembly      // System
                })
                {
                    try { eng.Runtime.LoadAssembly(asm); } catch { }
                }

                try
                {
                    string here = AssemblyDirectory();
                    var paths = new List<string>(eng.GetSearchPaths());
                    foreach (string candidate in new[] { Path.Combine(here, "Lib"), here })
                        if (Directory.Exists(candidate) && !paths.Contains(candidate)) paths.Add(candidate);
                    eng.SetSearchPaths(paths);
                }
                catch { }

                _engine = eng;
                return _engine;
            }
        }

        /// <summary>Where this assembly sits on disk — the root for Lib\ and Recipes\.</summary>
        public static string AssemblyDirectory()
        {
            return Path.GetDirectoryName(typeof(PythonEngine).Assembly.Location);
        }

        /// <summary>
        /// Render an IronPython exception the way a person can act on it: the Python
        /// traceback, not the CLR wrapper that says nothing about which line failed.
        /// </summary>
        public static string FormatException(Exception ex)
        {
            try { return Get().GetService<ExceptionOperations>().FormatException(ex); }
            catch { return ex != null ? ex.Message : "unknown error"; }
        }
    }
}
