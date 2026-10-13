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

        // #18: every option BuildOptions reads is bound by the add token.
        [Fact]
        public void The_cad_add_token_binds_every_import_option()
        {
            string src = Source("src/Horizun.Revit/Commands/ManageCadLinksCommand.cs");
            string build = Between(src, "private static DWGImportOptions BuildOptions", "\n        }\n");
            var read = Regex.Matches(build, @"request(?:\.Value<[^>]*>\(|\[)""([a-z_]+)""")
                            .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.NotEmpty(read);
            string scope = Between(src, "AddTokenScope =", "};");
            foreach (string key in read.Concat(new[] { "operation", "file_path", "view_id", "allow_duplicate" }))
                Assert.Contains("\"" + key + "\"", scope);
            Assert.Contains("DocumentGate.PlanHash(request, AddTokenScope)", src);
            Assert.Contains("{ \"options\", declared.ToString(", src);
        }

        // #14: an audit narrowed by host_ids lists no orphans from the hosts it was not given.
        [Fact]
        public void A_host_ids_audit_reads_orphans_only_in_its_hosts()
        {
            string src = Source("src/Horizun.Revit/Commands/AuditReinforcementCommand.cs");
            string orphans = Between(src, "var orphans = new JArray();", "result_orphans = orphans;");
            Assert.Contains("HashSet<long> narrowSet = narrow.Count > 0 ? new HashSet<long>(narrow) : null;", orphans);
            Assert.Contains("if (narrowSet != null && !narrowSet.Contains(p.HostElementId)) continue;", orphans);
            Assert.Contains("if (chunk != null && !chunk.PieceHosts.Contains(p.HostElementId)) continue;", orphans);
            Assert.True(orphans.IndexOf("narrowSet.Contains", StringComparison.Ordinal) <
                        orphans.IndexOf("RebarFinding.RuleBuiltNothing", StringComparison.Ordinal));
        }

        // #15 (already resolved at L10, 92a48b93; pinned here): a chunked apply seals every bar
        // with the WHOLE set's digest, so the audit of the whole set does not read the bars
        // of each piece as stale_requirement_set.
        [Fact]
        public void A_piece_is_sealed_with_the_whole_sets_digest()
        {
            string src = Source("src/Horizun.Revit/Commands/ApplyReinforcementCommand.cs");
            Assert.Contains("JObject setJson = request[\"requirement_set\"] as JObject;", src);
            Assert.Contains("string setSha = StructuralRequirementSet.Sha256Of(setJson);", src);
            Assert.DoesNotContain("setJson = chunk", src);
            Assert.DoesNotContain("Sha256Of(chunk", src);
        }

        // #16 (already resolved at L10, 92a48b93; pinned here): the cut's inventory is read by
        // the chunking itself, with the category as its BuiltInCategory name - the form a host
        // selector is written in and the resolver parses - not query_model's localized name.
        [Fact]
        public void The_cut_inventory_names_categories_as_selectors_do()
        {
            string src = Source("src/Horizun.Revit/Commands/ReinforcementChunking.cs");
            Assert.Contains("category = e.Category == null ? null : ((BuiltInCategory)Rid.Value(e.Category.Id)).ToString();", src);
            Assert.DoesNotContain("QueryModel", src);
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
                         "req.Result = cmd.Execute(app, req.ParamsJson);",
                         "try { result = cmd.Execute(app, work.ParamsJson);",
                         "try { r = cmd.Execute(app, step.Arguments.ToString(Newtonsoft.Json.Formatting.None)); }"
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
            Assert.Contains("DocumentGate.RecordResolvedPlan(rehearsedCleanly ? GraphPlan(app, gate, rows, actions) : null);", plan);
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

        // #33: change_path names the worksets it opens.
        [Fact]
        public void Change_path_says_it_opens_every_workset()
        {
            string src = Source("src/Horizun.Revit/Commands/ManageLinksCommand.cs");
            Assert.DoesNotContain("new WorksetConfiguration())", src);
            Assert.Contains("new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets)", src);
            string changePath = Between(src, "private static CommandResult ChangePath(", "private static int ClearSelectionIntoLink(");
            Assert.Equal(2, Regex.Matches(changePath, @"\[""worksets_after""\] = ChangePathWorksets").Count);
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

        // #51: add and repoint read the transaction's status and the load result.
        [Fact]
        public void Cad_add_and_repoint_judge_their_commit_and_load()
        {
            string src = Source("src/Horizun.Revit/Commands/ManageCadLinksCommand.cs");
            Assert.Contains("addStatus = t.Commit();", src);
            Assert.Contains("if (addStatus != TransactionStatus.Committed)", src);
            Assert.Contains("repointStatus = t.Commit();", src);
            Assert.Contains("bool repointVerified = arrived && repointCommitted && repointLoaded;", src);
            Assert.Contains("[\"host_verified\"] = repointVerified", src);
            Assert.DoesNotContain("[\"host_verified\"] = arrived,", src);
        }
    }
}
