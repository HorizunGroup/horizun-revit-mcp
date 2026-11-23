// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A GEOMETRY READ IS SCOPED BY A VIEW OR BY A DETAIL LEVEL, NEVER BOTH.
//
// Revit throws InvalidOperationException ("DetailLevel is already set") when
// Options gets the second of the two. The 2026-09-30 dry run hit it on every
// horizun_cad_extract call with view_id: the CAD harvest built its Options with
// DetailLevel = Fine and then assigned the view. The rule is pinned here, and the
// whole add-in's source is scanned so the pattern cannot come back in another
// command - none of these reads can run without a Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GeometryOptionsRulesTests
    {


        /// <summary>
        /// Every `new Options` in the add-in: an initializer that sets DetailLevel
        /// must not also set View, and must not be followed - before the geometry is
        /// read - by an assignment of `.View`. That is the exact shape that threw.
        /// </summary>
        [Fact]
        public void No_geometry_read_in_the_add_in_sets_both_a_view_and_a_detail_level()
        {
            string root = Path.Combine(RepoRoot(), "src", "Horizun.Revit");
            var offenders = new List<string>();
            var viewAssign = new Regex(@"(^|[\s{,.])View\s*=(?!=)", RegexOptions.Multiline);
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                                             .Where(f => f.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0 &&
                                                         f.IndexOf(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0))
            {
                string text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"new\s+Options\s*(\(\s*\))?\s*\{"))
                {
                    int open = m.Index + m.Length - 1;
                    int close = MatchingBrace(text, open);
                    if (close < 0) continue;
                    string initializer = text.Substring(open, close - open + 1);
                    bool setsDetail = Regex.IsMatch(initializer, @"\bDetailLevel\s*=(?!=)");
                    if (!setsDetail) continue;

                    int read = text.IndexOf("get_Geometry(", close, StringComparison.Ordinal);
                    string until = read < 0 ? "" : text.Substring(close, read - close);
                    if (viewAssign.IsMatch(initializer) || viewAssign.IsMatch(until))
                        offenders.Add(Path.GetFileName(file) + " @" + m.Index);
                }
            }
            Assert.True(offenders.Count == 0,
                "Options built with DetailLevel and then given a View (Revit throws 'DetailLevel is already set'): " +
                string.Join(", ", offenders));
        }

        private static int MatchingBrace(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }
    }
}
