// -----------------------------------------------------------------------------
// Horizun tests - original Horizun code.
//
// THE RECIPES PARSE, AND THEY EXPOSE WHAT THE HOST CALLS.
//
// The recipe-backed tools keep their geometry in Python that ships beside the
// add-in (see Recipe.cs). That buys the algorithms their existing bug history
// instead of restarting it in C#, and it costs one thing: the C# compiler cannot
// see them. A stray colon in a recipe would build clean, deploy clean, and fail
// the first time somebody called the tool - inside Revit, mid-model, having
// already been told the plan looked fine.
//
// So the same IronPython engine that runs them PARSES them here. Parsing needs no
// Revit, which is the whole reason this test can exist in CI at all: the imports
// inside a recipe resolve at run time, so `from Autodesk.Revit.DB import Floor`
// costs nothing until executed.
//
// WHAT THIS DOES NOT PROVE, stated so nobody reads more into a green run: that a
// recipe is CORRECT. Parsing catches syntax, not geometry. The tools these came
// from are verified against real models, and that is where they stay verified.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Xunit;

namespace Horizun.Server.Tests
{
    public class RecipeSyntaxTests
    {
        /// <summary>Modules that support recipes rather than being one. No plan/apply expected.</summary>
        private static readonly HashSet<string> Helpers =
            new HashSet<string>(StringComparer.Ordinal) { "hz" };

        [Fact]
        public void The_recipe_folder_is_where_the_add_in_expects_it()
        {
            // A rename that moved the folder would make every recipe tool fail at run time
            // with "not installed". Better to fail here.
            Assert.True(Directory.Exists(RecipeFolder()),
                        "No recipe folder at " + RecipeFolder());
            Assert.NotEmpty(RecipeFiles());
        }

        [Fact]
        public void Every_recipe_parses()
        {
            ScriptEngine engine = Python.CreateEngine();
            var broken = new List<string>();

            foreach (string path in RecipeFiles())
            {
                try
                {
                    engine.CreateScriptSourceFromString(
                        File.ReadAllText(path), Microsoft.Scripting.SourceCodeKind.Statements).Compile();
                }
                catch (Exception ex)
                {
                    broken.Add(Path.GetFileName(path) + ": " + ex.Message);
                }
            }

            Assert.True(broken.Count == 0, "These recipes do not parse:\n  " + string.Join("\n  ", broken));
        }

        [Fact]
        public void Every_recipe_defines_the_functions_the_host_calls()
        {
            // Recipe.Run invokes plan(doc, args) and apply(doc, args, plan) by name. A
            // recipe missing one is a tool that throws on its first real call.
            var missing = new List<string>();

            foreach (string path in RecipeFiles())
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (Helpers.Contains(name)) continue;

                string source = File.ReadAllText(path);
                foreach (string fn in new[] { "plan", "apply" })
                    if (!Regex.IsMatch(source, @"^def\s+" + fn + @"\s*\(", RegexOptions.Multiline))
                        missing.Add(name + " has no " + fn + "()");
            }

            Assert.True(missing.Count == 0, string.Join("; ", missing));
        }

        [Fact]
        public void No_recipe_opens_its_own_transaction()
        {
            // The host owns the commit so that Guard can prove it landed. A recipe that
            // opened its own would put the commit outside Guard's reach - the exact defect
            // this whole design exists to prevent - and Recipe.Run's runtime check would
            // catch it only after the work had already been done. Catch it in CI instead.
            var offenders = new List<string>();

            foreach (string path in RecipeFiles())
            {
                string source = File.ReadAllText(path);
                foreach (Match m in Regex.Matches(source, @"^[^#\n]*\bTransaction\s*\(", RegexOptions.Multiline))
                    offenders.Add(Path.GetFileName(path) + ": " + m.Value.Trim());
            }

            Assert.True(offenders.Count == 0,
                        "A recipe must not open a transaction; the host owns the commit:\n  " +
                        string.Join("\n  ", offenders));
        }

        // ---- locating the recipes ------------------------------------------------

        private static IEnumerable<string> RecipeFiles()
            => Directory.Exists(RecipeFolder())
                ? Directory.GetFiles(RecipeFolder(), "*.py").OrderBy(p => p, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        /// <summary>
        /// The source folder, found by walking up from the test assembly to the repository
        /// root. The recipes under test are the ones in source control, not a copy in some
        /// bin directory that a stale build could leave behind.
        /// </summary>
        private static string RecipeFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "Horizun.Revit", "Recipes");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return Path.Combine(AppContext.BaseDirectory, "Recipes");  // fails the first test, loudly
        }
    }
}
