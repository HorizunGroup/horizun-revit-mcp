// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHAT THE PLAN RECORDS IS WHAT THE APPLY WILL READ.
//
// horizun_apply_cad_plan re-reads every id in apply_binding.resolved_names and
// compares Element.Name to the "name" the plan recorded. Anything else is
// drift, and drift means stale_plan with nothing written.
//
// MEASURED: the type entry recorded "HZ_DOOR: HZ_DOOR" - the family and the
// type joined together for a human to read - while the apply read "HZ_DOOR".
// Same id, same element, nothing changed in the document, and every plan that
// resolved a family type refused ITSELF as stale. Levels never showed it,
// because their entry happened to record the plain name; walls never showed it
// either, because a wall rule that names no family type resolves no type at all.
//
// These tests cannot open a Revit document, so they pin the invariant where it
// can be pinned: one function decides what a resolved entry says, and it is the
// only thing that builds one.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadResolvedNameTests
    {
        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit")))
                dir = dir.Parent;
            Assert.True(dir != null, "the repository root must be findable from the test binary");
            string path = Path.Combine(new[] { dir.FullName, "src", "Horizun.Revit" }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), path + " must exist");
            return File.ReadAllText(path);
        }

        [Fact]
        public void The_plan_builds_a_resolved_entry_ONLY_through_the_one_function_that_decides()
        {
            // A second place that hand-rolls the object is a second place that
            // can record a label where a name belongs - which is the whole bug.
            string plan = Source("Commands", "PlanFromCadCommand.cs");
            int handRolled = Regex.Matches(plan, @"\[""what""\]\s*=").Count;
            Assert.True(handRolled == 1,
                "resolved_names entries must be built by Resolved(...) alone - found " + handRolled +
                " place(s) writing [\"what\"] directly.");
            Assert.Contains("private static JObject Resolved(string what, Element element, string askedFor",
                            plan);
        }

        [Fact]
        public void That_function_records_the_name_the_apply_re_reads_and_never_a_decoration()
        {
            string plan = Source("Commands", "PlanFromCadCommand.cs");
            // The apply reads Element.Name; SafeName is exactly that, guarded.
            Assert.Contains("[\"name\"] = SafeName(element),", plan);
            // The pretty form lives beside it, under its own key.
            Assert.Contains("o[\"label\"] = label;", plan);
        }

        [Fact]
        public void The_apply_compares_the_NAME_and_reports_the_LABEL()
        {
            // The comparison must not quietly start using the decoration either:
            // that would trade a false stale_plan for a missed real one.
            string apply = Source("Commands", "ApplyCadPlanCommand.cs");
            Assert.Contains("if (now != null && string.Equals(nowName, was, StringComparison.Ordinal)) continue;",
                            apply);
            Assert.Contains("string shown = r.Value<string>(\"label\") ?? was;", apply);
        }
    }
}
