// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The budget for the heavy readers (model_scan, query_model, audit_model, quantities,
// query_planimetry). The arithmetic is Revit-free and linked straight in; the wiring of each
// command is held with source-text assertions, because a command cannot run here.
//
// WHAT IS PROVED: the scope's three stop reasons and the unit cap; the position walk's
// skip / go / stop; the partial block (what was measured, what was not, no cursor where none
// is honest); that a request without the opt-in changes nothing; that a partial planimetry
// snapshot can never report complete coverage; that the budget comparison refuses a partial
// takeoff page; and that every command asks the scope where it must.
// WHAT IS NOT: how long a real model takes, and that Revit stays fluid. Those are measured
// live (docs/COOPERATIVE-BUDGET.md, "Scenario").
// -----------------------------------------------------------------------------
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    [Collection("CooperativeRead global state")]
    public class CooperativeBudgetReadersTests
    {
        [Fact]
        public void Deadline_stops_between_units_and_preserves_the_resume_position()
        {
            long now = 0;
            var scope = new CooperativeRead.Scope("annotations", 120000, 0, 0, () => now);
            var walk = new PositionWalk(scope, 0, 4);
            Assert.Equal(WalkStep.Go, walk.Next());
            Assert.Equal(WalkStep.Go, walk.Next());
            now = 120000;
            Assert.Equal(WalkStep.Stop, walk.Next());
            Assert.Equal(2, walk.StoppedAt);
            Assert.Equal("ui_budget_exhausted", scope.StoppedBecause);
            var resumed = new PositionWalk(CooperativeRead.Begin("annotations", 20000), walk.StoppedAt, 4);
            Assert.Equal(WalkStep.Skip, resumed.Next());
            Assert.Equal(WalkStep.Skip, resumed.Next());
            Assert.Equal(WalkStep.Go, resumed.Next());
            Assert.Equal(WalkStep.Go, resumed.Next());
        }

        [Fact]
        public void Bounded_planimetry_never_allows_more_than_two_minutes()
        {
            Assert.Equal(120000, CooperativeRead.BoundedPlanimetryBudget(600000));
            Assert.Equal(20000, CooperativeRead.BoundedPlanimetryBudget(null));
            Assert.Equal(10000, CooperativeRead.BoundedPlanimetryBudget(10000));
            string command = Source("src", "Horizun.Revit", "Commands", "QueryPlanimetryCommand.cs");
            Assert.Contains("PlanimetryReadBudget.TryResolve", command);
        }

        [Fact]
        public void Large_read_reports_progress_and_cancellation()
        {
            int reads = 0;
            var counts = new System.Collections.Generic.Dictionary<string, long> { ["host"] = 120000 };
            var refusal = LargeModelToolGate.Run("horizun_query_model", 120000, counts, false, () => ++reads);
            Assert.False(refusal.Executed);
            Assert.Equal(0, reads);
            try
            {
                CooperativeRead.Abandoned = () => reads >= 3;
                var accepted = LargeModelToolGate.Run("horizun_query_model", 120000, counts, true, () =>
                {
                    var scope = CooperativeRead.Begin("large read", 120000);
                    while (scope.Continue()) reads++;
                    return scope.Report();
                });
                Assert.True(accepted.Executed);
                Assert.False((bool)accepted.Value["complete"]);
                Assert.Equal(3, (int)accepted.Value["units_done"]);
                Assert.Equal("client_abandoned", (string)accepted.Value["stopped_because"]);
            }
            finally { CooperativeRead.Abandoned = null; }
        }
        // ---- the scope -------------------------------------------------------------

        [Fact]
        public void The_unit_cap_stops_a_scope_in_Continue_not_only_in_ContinueWithin()
        {
            CooperativeRead.Scope scope = CooperativeRead.Begin("t", 20000, 0, 3);
            Assert.True(scope.Continue());
            Assert.True(scope.Continue());
            Assert.True(scope.Continue());
            Assert.False(scope.Continue());
            Assert.Equal("max_units_exhausted", scope.StoppedBecause);
            Assert.Equal(3, scope.Done);
            Assert.False(scope.Complete);
        }

        [Fact]
        public void A_scope_without_a_cap_has_none()
        {
            CooperativeRead.Scope scope = CooperativeRead.Begin("t", 20000, 0, 0);
            for (int i = 0; i < 5000; i++) Assert.True(scope.Continue());
            Assert.True(scope.Complete);
        }

        [Fact]
        public void Poll_counts_nothing_and_asks_the_same_questions()
        {
            try
            {
                CooperativeRead.Scope scope = CooperativeRead.Begin("t", 20000, 0, 2);
                for (int i = 0; i < 10; i++) Assert.True(scope.Poll());
                Assert.Equal(0, scope.Done);

                Assert.True(scope.Continue());
                Assert.True(scope.Continue());
                Assert.False(scope.Poll());                     // cap reached: nothing new may start
                Assert.Equal("max_units_exhausted", scope.StoppedBecause);

                CooperativeRead.Abandoned = () => true;
                CooperativeRead.Scope gone = CooperativeRead.Begin("t", 20000, 0, 0);
                Assert.False(gone.Poll());
                Assert.Equal("client_abandoned", gone.StoppedBecause);
            }
            finally { CooperativeRead.Abandoned = null; }
        }

        // ---- the position walk -----------------------------------------------------

        private static int[] Walk(PositionWalk walk, int total, out int stoppedAt)
        {
            var visited = new System.Collections.Generic.List<int>();
            for (int i = 0; i < total; i++)
            {
                WalkStep step = walk.Next();
                if (step == WalkStep.Skip) continue;
                if (step == WalkStep.Stop) break;
                visited.Add(i);
            }
            stoppedAt = walk.StoppedAt;
            return visited.ToArray();
        }

        [Fact]
        public void Without_a_scope_the_walk_visits_everything_from_zero()
        {
            var walk = new PositionWalk(null, 7, 5);       // a cursor is ignored: nobody opted in
            int stopped;
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Walk(walk, 5, out stopped));
            Assert.Equal(0, walk.From);
            Assert.False(walk.Stopped);
        }

        [Fact]
        public void A_walk_stops_at_the_cap_and_a_continuation_resumes_exactly_there()
        {
            CooperativeRead.Scope first = CooperativeRead.Begin("t", 20000, 10, 4);
            var page1 = new PositionWalk(first, 0, 10);
            int stopped1;
            Assert.Equal(new[] { 0, 1, 2, 3 }, Walk(page1, 10, out stopped1));
            Assert.Equal(4, stopped1);
            Assert.True(page1.Stopped);
            Assert.Equal(4, page1.Measured);

            CooperativeRead.Scope second = CooperativeRead.Begin("t", 20000, 10, 4);
            var page2 = new PositionWalk(second, stopped1, 10);
            int stopped2;
            Assert.Equal(new[] { 4, 5, 6, 7 }, Walk(page2, 10, out stopped2));
            Assert.Equal(8, stopped2);

            CooperativeRead.Scope third = CooperativeRead.Begin("t", 20000, 10, 4);
            var page3 = new PositionWalk(third, stopped2, 10);
            int stopped3;
            Assert.Equal(new[] { 8, 9 }, Walk(page3, 10, out stopped3));
            Assert.False(page3.Stopped);
            Assert.Equal(10, stopped3);
            Assert.True(third.Complete);
            // Skipped positions are never counted against the next page's cap.
            Assert.Equal(2, third.Done);
        }

        [Fact]
        public void A_cursor_beyond_the_population_is_clamped_not_trusted()
        {
            CooperativeRead.Scope scope = CooperativeRead.Begin("t", 20000, 3, 0);
            var walk = new PositionWalk(scope, 99, 3);
            int stopped;
            Assert.Empty(Walk(walk, 3, out stopped));
            Assert.Equal(3, walk.From);
            Assert.Equal(0, walk.Measured);
        }

        // ---- the partial block -----------------------------------------------------

        private static CooperativeOptions Requested(int maxUnits)
        {
            return CooperativeOptions.Read(new JObject
            {
                ["cooperative"] = new JObject { ["max_units"] = maxUnits }
            }, "fp");
        }

        [Fact]
        public void A_request_without_the_opt_in_gets_no_new_field()
        {
            var result = JObject.Parse("{\"coverage_complete\":true,\"rows\":[1,2]}");
            string before = result.ToString();
            bool partial = CooperativePartial.Apply(result, CooperativeOptions.Read(new JObject(), "fp"), null, "fp",
                CooperativePartial.Account("x", 1, 1, null, null, "g"), true, 0, "");
            Assert.False(partial);
            Assert.Equal(before, result.ToString());
            Assert.Null(CooperativeOptions.Read(new JObject(), "fp").Begin("x", 0));   // no scope either
        }

        [Fact]
        public void A_complete_budgeted_read_says_so_and_does_not_touch_coverage()
        {
            CooperativeOptions options = Requested(10);
            CooperativeRead.Scope scope = options.Begin("x", 0);
            Assert.True(scope.Continue());
            var result = new JObject();
            bool partial = CooperativePartial.Apply(result, options, scope, "fp",
                CooperativePartial.Account("checks", 1, 1, new JArray("a"), null, "g"), true, 1, "");
            Assert.False(partial);
            Assert.False((bool)result["partial"]);
            Assert.Null(result["coverage_complete"]);
            Assert.True((bool)result["cooperative"]["complete"]);
        }

        [Fact]
        public void A_resumable_partial_names_what_was_and_was_not_measured_and_issues_a_cursor()
        {
            try
            {
                QueryCacheLifecycle.Ready = true;
                QueryCacheLifecycle.TestEpoch = 5;
                CooperativeOptions options = Requested(1);
                CooperativeRead.Scope scope = options.Begin("audit", 0);
                Assert.True(scope.Continue());
                Assert.False(scope.Continue());

                var result = new JObject { ["coverage_complete"] = true };
                bool partial = CooperativePartial.Apply(result, options, scope, "fp",
                    CooperativePartial.Account("checks", 1, 3, new JArray("warnings"), new JArray("rooms", "links"), "g"),
                    true, 1, "");

                Assert.True(partial);
                Assert.True((bool)result["partial"]);
                Assert.False((bool)result["coverage_complete"]);
                Assert.Equal("max_units_exhausted", (string)result["partial_reason"]);
                JObject account = (JObject)result["cooperative"]["measured_account"];
                Assert.Equal("warnings", (string)account["measured"][0]);
                Assert.Equal(2, ((JArray)account["not_measured"]).Count);
                Assert.Contains("never looked at", (string)account["reading_rule"]);
                Assert.Equal(JTokenType.String, result["cooperative"]["cursor"].Type);

                // The cursor is bound to the request and to the position it names.
                var resume = new JObject { ["cooperative"] = new JObject { ["cursor"] = (string)result["cooperative"]["cursor"] } };
                Assert.Equal(1, CooperativeOptions.Read(resume, "fp").StartAt);
                Assert.NotNull(CooperativeOptions.Read(resume, "another").Refusal);
            }
            finally { QueryCacheLifecycle.Ready = false; QueryCacheLifecycle.TestEpoch = 0; }
        }

        [Fact]
        public void A_partial_that_cannot_resume_issues_no_cursor_and_says_why()
        {
            try
            {
                QueryCacheLifecycle.Ready = true;
                CooperativeOptions options = Requested(1);
                CooperativeRead.Scope scope = options.Begin("planimetry", 0);
                Assert.True(scope.Continue());
                Assert.False(scope.Continue());
                var result = new JObject();
                bool partial = CooperativePartial.Apply(result, options, scope, "fp",
                    CooperativePartial.Account("passes", 0, 2, new JArray(), new JObject(), "g"),
                    false, 0, "narrow the sheets.");
                Assert.True(partial);
                Assert.Equal(JTokenType.Null, result["cooperative"]["cursor"].Type);
                Assert.False((bool)result["cooperative"]["collection_continuation_available"]);
                Assert.Contains("narrow the sheets.", (string)result["cooperative"]["cursor_means"]);
            }
            finally { QueryCacheLifecycle.Ready = false; }
        }

        [Fact]
        public void A_mode_that_cannot_honour_a_budget_refuses_it_by_name()
        {
            string message = CooperativePartial.RefuseUnhonoured("horizun_quantities", "carbon", "volume, takeoff");
            Assert.Contains("carbon", message);
            Assert.Contains("volume, takeoff", message);
            Assert.Contains("Nothing was read", message);
        }

        // ---- a partial planimetry collection ---------------------------------------

        [Fact]
        public void Annotations_resume_the_collection_not_the_row_pager()
        {
            string command = Source("src", "Horizun.Revit", "Commands", "QueryPlanimetryCommand.cs");
            Assert.Contains("bool resumeCollection = cooperative.Requested && (mode == \"annotations\" || mode == \"sheets\")", command);
            Assert.Contains("scope.ResumeCollection ? cooperative.StartAt + coopScope.Done : 0", command);
            Assert.DoesNotContain("cooperative.cursor is not used by horizun_query_planimetry", command);
            string inventory = Source("src", "Horizun.Revit", "Core", "PlanimetryInventory.cs");
            Assert.Contains("CollectionStep(snap, scope, \"annotations:\" + population)", inventory);
            Assert.Contains("CollectionStep(snap, scope, \"annotations:\" + kind)", inventory);
        }

        [Fact]
        public void Sheets_resume_the_same_ordered_collection_without_discarding_rows()
        {
            string command = Source("src", "Horizun.Revit", "Commands", "QueryPlanimetryCommand.cs");
            Assert.Contains("mode == \"annotations\" || mode == \"sheets\"", command);
            string inventory = Source("src", "Horizun.Revit", "Core", "PlanimetryInventory.cs");
            Assert.Contains("CollectionStep(snap, scope, \"sheets\")", inventory);
            var first = new PositionWalk(CooperativeRead.Begin("sheets", 20000, 0, 2), 0, 4);
            Assert.Equal(new[] { 0, 1 }, Walk(first, 4, out int next));
            Assert.Equal(2, next);
            var second = new PositionWalk(CooperativeRead.Begin("sheets", 20000, 0, 2), next, 4);
            Assert.Equal(new[] { 2, 3 }, Walk(second, 4, out int end));
            Assert.Equal(4, end);
        }

        [Fact]
        public void Resumable_collection_progress_does_not_claim_the_skipped_view_pass()
        {
            string command = Source("src", "Horizun.Revit", "Commands", "QueryPlanimetryCommand.cs");
            Assert.Contains("if (scope.ResumeCollection) needed.Add(scope.NeedSheets ? \"sheets\" : \"annotations\")", command);
        }

        [Fact]
        public void A_planimetry_snapshot_the_budget_cut_short_never_reports_complete_coverage()
        {
            var clean = new PlanimetrySnapshot();
            Assert.True(clean.CoverageComplete);
            Assert.False(clean.BudgetStopped);

            var cutMidway = new PlanimetrySnapshot();
            cutMidway.PassStopped["sheets"] = "ui_budget_exhausted";
            Assert.True(cutMidway.BudgetStopped);
            Assert.False(cutMidway.CoverageComplete);
            Assert.Contains("cooperative budget", cutMidway.CoverageNote());

            var neverStarted = new PlanimetrySnapshot();
            neverStarted.PassNotStarted.Add("annotations");
            Assert.False(neverStarted.CoverageComplete);
        }

        // ---- scan request rules ----------------------------------------------------

        [Fact]
        public void The_audit_accepts_the_opt_in_and_still_refuses_unknown_keys()
        {
            Assert.True(ScanRequestRules.CheckAudit(new JObject
            {
                ["target_document"] = "m", ["cooperative"] = new JObject { ["max_units"] = 3 }
            }).Ok);
            Assert.False(ScanRequestRules.CheckAudit(new JObject { ["target_document"] = "m", ["budget_ms"] = 3 }).Ok);
        }

        // ---- the budget comparison -------------------------------------------------

        [Fact]
        public void The_budget_comparison_refuses_a_partial_takeoff_page()
        {
            string problem;
            var reply = JObject.Parse("{\"mode\":\"takeoff\",\"partial\":true,\"rows\":[]}");
            Assert.Null(BudgetComparisonRules.ReadModelRows(reply, "classification_code", out problem));
            Assert.Contains("PARTIAL", problem);
        }

        // ---- wiring: each command asks where it must --------------------------------

        private static string Source(params string[] parts)
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d.FullName, Path.Combine(parts)));
        }

        [Fact]
        public void Model_scan_gates_each_section_and_lists_the_ones_it_did_not_start()
        {
            string src = Source("src", "Horizun.Revit", "Commands", "ModelScanCommand.cs");
            Assert.Contains("gate.Poll()", src);
            Assert.Contains("\"not_measured\"", src);
            Assert.Contains("sections_not_measured", src);
            // Also report weight when its sources were not measured in this call.
            Assert.Contains("if (coopScope != null || notMeasured.Count != 0) scanBody[\"sections_not_measured\"] = notMeasured", src);
            Assert.Contains("if (resume.Count != 0) scanBody[\"resume\"]", src);
        }

        [Fact]
        public void Query_model_and_list_elements_pass_the_callers_cap_to_the_scope()
        {
            Assert.Contains("ContinueWithin(cooperative?.MaxUnits ?? 0)",
                Source("src", "Horizun.Revit", "Commands", "QueryModelCommand.cs"));
            Assert.Contains("ContinueWithin(options?.MaxUnits ?? 0)",
                Source("src", "Horizun.Revit", "Commands", "ListElementsCommand.cs"));
        }

        [Fact]
        public void Audit_model_walks_its_checks_by_position_and_never_stores_a_partial_snapshot()
        {
            string src = Source("src", "Horizun.Revit", "Commands", "AuditModelCommand.cs");
            Assert.Contains("new PositionWalk(coop, startAt, checks.Count)", src);
            Assert.Contains("[\"state\"] = \"not_run\"", src);
            Assert.Contains("if (notAllChecksRan) storeSnapshot = false;", src);
            Assert.Contains("CooperativePartial.Apply(auditBody", src);
        }

        [Fact]
        public void Quantities_walks_volume_and_takeoff_and_refuses_the_modes_it_cannot_budget()
        {
            string src = Source("src", "Horizun.Revit", "Commands", "QuantitiesCommand.cs");
            Assert.Contains("new PositionWalk(coopScope, cooperative.StartAt, elements.Count)", src);
            Assert.Contains("new PositionWalk(coopScope, cooperative.StartAt, requested)", src);
            Assert.Contains("RefuseUnhonoured(Name, mode, \"volume, takeoff\")", src);
            Assert.Contains("cooperative cannot be combined with rows_file", src);
        }

        [Fact]
        public void Query_planimetry_installs_the_scope_around_the_collection_and_withholds_an_overlap_verdict()
        {
            string command = Source("src", "Horizun.Revit", "Commands", "QueryPlanimetryCommand.cs");
            Assert.Contains("PlanimetryReadBudget.Read(budget,", command);
            Assert.Contains("\"not_assessable\"", command);
            string inventory = Source("src", "Horizun.Revit", "Core", "PlanimetryInventory.cs");
            Assert.Contains("MayStart(snap, \"sheets\")", inventory);
            Assert.Contains("CollectionStep(snap, scope, \"sheets\")", inventory);
            Assert.Contains("Step(snap, \"references\")", inventory);
        }
    }
}
