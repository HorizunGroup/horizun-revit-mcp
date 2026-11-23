using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanimetryReadBudgetTests
    {
        [Fact]
        public void Uses_a_marked_default_and_honors_the_explicit_budget()
        {
            Assert.True(PlanimetryReadBudget.TryResolve(null, null, out int budgetMs, out JObject assumption));
            Assert.Equal(20000, budgetMs);
            Assert.Equal("SUPUESTO", (string)assumption["flag"]);
            Assert.True(PlanimetryReadBudget.TryResolve(new JValue(2), 1000, out budgetMs, out assumption));
            Assert.Equal(2000, budgetMs);
            Assert.Null(assumption);
            Assert.False(PlanimetryReadBudget.TryResolve(new JValue(121), null, out _, out _));
        }

        [Theory]
        [InlineData(false, "ui_budget_exhausted")]
        [InlineData(true, "client_abandoned")]
        public void Stops_between_items_and_marks_the_measured_prefix(bool cancelled, string reason)
        {
            long elapsed = 0;
            bool abandoned = false;
            var oldAbandoned = CooperativeRead.Abandoned;
            CooperativeRead.Abandoned = () => abandoned;
            var budget = new CooperativeRead.Scope("query_planimetry", 1000, 0, elapsedMilliseconds: () => elapsed);
            int itemsPulled = 0;
            IEnumerable<int> Candidates()
            {
                for (int id = 1; id <= 3; id++)
                {
                    itemsPulled++;
                    yield return id;
                }
            }

            JObject partial;
            try
            {
                var result = PlanimetryReadBudget.Read(budget, rows =>
                {
                    foreach (int id in PlanimetryReadBudget.CheckEach(Candidates()))
                    {
                        rows.Add(new JObject { ["id"] = id });
                        if (cancelled) abandoned = true; else elapsed = 1000;
                    }
                    return CommandResult.Ok(new JObject { ["rows"] = rows });
                }, rows => new JObject { ["rows"] = rows });
                partial = Assert.IsType<JObject>(result.Data);
            }
            finally { CooperativeRead.Abandoned = oldAbandoned; }
            Assert.Equal(1, itemsPulled);
            Assert.True((bool)partial["partial"]);
            Assert.False((bool)partial["coverage_complete"]);
            Assert.Equal(reason, (string)partial["cooperative"]["stopped_because"]);
            Assert.Equal(1, (int)Assert.Single((JArray)partial["rows"])["id"]);
        }

        [Theory]
        [InlineData(false, "ui_budget_exhausted")]
        [InlineData(true, "client_abandoned")]
        public void Checks_after_the_last_unit_and_refreshes_the_prepared_report(bool cancelled, string reason)
        {
            long elapsed = 0;
            bool abandoned = false;
            var oldAbandoned = CooperativeRead.Abandoned;
            CooperativeRead.Abandoned = () => abandoned;
            try
            {
                var budget = new CooperativeRead.Scope("query_planimetry", 1000, 0, elapsedMilliseconds: () => elapsed);
                Assert.True(budget.Continue());
                var report = budget.Report();
                report["resume_cursor"] = "position";
                var partial = new JObject { ["cooperative"] = report, ["matched_total"] = 1, ["next_cursor"] = "row cursor" };

                if (cancelled) abandoned = true; else elapsed = 1000;
                PlanimetryReadBudget.StampPartial(partial, budget);

                Assert.True((bool)partial["partial"]);
                Assert.False((bool)report["complete"]);
                Assert.Equal(reason, (string)report["stopped_because"]);
                Assert.Equal("position", (string)report["resume_cursor"]);
                Assert.Equal(JTokenType.Null, partial["matched_total"].Type);
                Assert.Null(partial["next_cursor"]);
            }
            finally { CooperativeRead.Abandoned = oldAbandoned; }
        }

        [Fact]
        public void Late_deadline_keeps_the_next_cursor_when_there_is_no_resume_cursor()
        {
            long elapsed = 0;
            var budget = new CooperativeRead.Scope("query_planimetry", 1000, 0, elapsedMilliseconds: () => elapsed);
            var page = new JObject { ["next_cursor"] = "row cursor" };

            elapsed = 1000;
            PlanimetryReadBudget.StampPartial(page, budget);

            Assert.True((bool)page["partial"]);
            Assert.False((bool)page["coverage_complete"]);
            Assert.Equal("ui_budget_exhausted", (string)page["partial_reason"]);
            Assert.Equal("row cursor", (string)page["next_cursor"]);
        }

        [Fact]
        public void Reaching_the_unit_cap_on_the_last_unit_does_not_cut_the_read()
        {
            var budget = new CooperativeRead.Scope("query_planimetry", 1000, 0, unitCap: 1, elapsedMilliseconds: () => 0);
            Assert.True(budget.Continue());
            var result = new JObject();
            PlanimetryReadBudget.StampPartial(result, budget);
            Assert.Null(result["partial"]);
            Assert.True(budget.Complete);
        }
    }
}
