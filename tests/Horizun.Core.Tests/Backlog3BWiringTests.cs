// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Revit-host wiring for the phase 3B backlog fixes (backlog-post-2.3.0.md). These
// commands cannot be constructed without Revit, so each fix is pinned at source
// level while the add-in is compiled against every supported Revit API.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class Backlog3BWiringTests
    {
        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            return d.FullName;
        }

        private static string Source(string relative) =>
            File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar)));

        private static string Between(string src, string start, string end)
        {
            int a = src.IndexOf(start, StringComparison.Ordinal);
            Assert.True(a >= 0, "not found: " + start);
            int b = src.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            Assert.True(b >= 0, "not found after start: " + end);
            return src.Substring(a, b - a);
        }

        // #17: a plan recorded and never stamped does not reach the next command.
        [Fact]
        public void A_recorded_plan_never_outlives_its_command()
        {
            string gate = Source("src/Horizun.Revit/Core/DocumentGate.cs");
            Assert.Contains("public static void ClearResolvedPlan() { _pendingPlan = null; }", gate);

            string dispatcher = Source("src/Horizun.Revit/Core/Dispatcher.cs");
            foreach (string call in new[]
                     {
                         "result = _commands[name].Execute(null, paramsJson);",
                         "req.Result = IdentityPrepass.Execute(app, cmd, req.ParamsJson, LargeModelDispatch.Execute);",
                         "try { result = IdentityPrepass.Execute(app, cmd, work.ParamsJson, LargeModelDispatch.Execute);",
                         "try { r = IdentityPrepass.Execute(app, cmd, step.Arguments.ToString(Newtonsoft.Json.Formatting.None), LargeModelDispatch.Execute); }"
                     })
            {
                int at = dispatcher.IndexOf(call, StringComparison.Ordinal);
                Assert.True(at > 0, "dispatch site not found: " + call);
                string before = dispatcher.Substring(Math.Max(0, at - 160), 160);
                Assert.Contains("DocumentGate.ClearResolvedPlan();", before);
            }
            Assert.Contains("finally { DocumentGate.ClearResolvedPlan(); }", dispatcher);

            string corrections = Source("src/Horizun.Revit/Commands/ApplyCorrectionsCommand.cs");
            Assert.Contains("DocumentGate.RecordResolvedPlan(rehearsedCleanly ? plan : null);", corrections);
            Assert.Contains("DocumentGate.ClearResolvedPlan();   // the last child's plan", corrections);
            string plan = Source("src/Horizun.Revit/Commands/ExecutePlanCommand.cs");
            Assert.Contains("DocumentGate.RecordResolvedPlan(rehearsedCleanly ? ResolveGraph(app, gate, rows) : null);", plan);
        }

        // #19: the checkout census never builds a collector without a filter.
        [Fact]
        public void The_relinquish_census_uses_filtered_collectors()
        {
            string src = Source("src/Horizun.Revit/Commands/RelinquishAllCommand.cs");
            Assert.DoesNotContain("new FilteredElementCollector(doc).ToElementIds()", src);
            Assert.Contains("new FilteredElementCollector(doc).WhereElementIsElementType().ToElementIds()", src);
            Assert.Contains("new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElementIds()", src);
        }

        // #52: an isolate is verified only when nothing else hideable is still shown.
        [Fact]
        public void An_isolate_checks_that_everything_else_was_hidden()
        {
            string src = Source("src/Horizun.Revit/Commands/ManageViewsGraphics.cs");
            string isolate = Between(src, "if (!(e is View isolating)", "case \"reset_temporary\":");
            Assert.Contains("return IsolateLeftNothingElseVisible(doc, isolating, isolated);", isolate);
            string helper = Between(src, "private static bool IsolateLeftNothingElseVisible", "private static IList<ElementId> ReadElementIds");
            Assert.Contains("CanBeHidden(view)", helper);
            Assert.Contains("IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, other.Id)) return false;", helper);
            Assert.Contains("catch { return false; }", helper);
            Assert.DoesNotContain("IsolateCheckLimit", helper);
            Assert.DoesNotContain("hideable = false", helper);
        }

    }
}
