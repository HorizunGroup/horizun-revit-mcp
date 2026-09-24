// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
// horizun_link_schedule, Revit-free: MSPDI / CSV / XER parsing, matching, status.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleLinkTests
    {
        private const string Mspdi =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<Project xmlns=\"http://schemas.microsoft.com/project\"><Name>P</Name><Tasks>\n" +
            "<Task><UID>0</UID><ID>0</ID><Name>Project</Name><Summary>1</Summary></Task>\n" +
            "<Task><UID>1</UID><ID>1</ID><Name>Estructura</Name><WBS>1</WBS><Summary>1</Summary></Task>\n" +
            "<Task><UID>2</UID><ID>2</ID><Name>Muros N1</Name><WBS>1.1</WBS><Start>2026-01-05T08:00:00</Start><Finish>2026-01-20T17:00:00</Finish><PercentComplete>100</PercentComplete><Summary>0</Summary></Task>\n" +
            "<Task><UID>3</UID><ID>3</ID><Name>Losa N2</Name><WBS>1.2</WBS><Start>2026-02-01T08:00:00</Start><Finish>2026-02-28T17:00:00</Finish><PercentComplete>0</PercentComplete><Summary>0</Summary></Task>\n" +
            "</Tasks></Project>";

        [Fact]
        public void Mspdi_skips_summaries_and_reads_dates_wbs_and_progress()
        {
            ScheduleImportResult r = ScheduleImport.Parse("p.xml", Mspdi);
            Assert.Equal("mspdi", r.Format);
            Assert.Equal(2, r.SummariesSkipped);
            Assert.Equal(new[] { "2", "3" }, r.Activities.Select(a => a.Id));
            Assert.Equal("1.1", r.Activities[0].Wbs);
            Assert.Equal(new DateTime(2026, 1, 20), r.Activities[0].Finish);
            Assert.Equal(100, r.Activities[0].PercentComplete);
        }

        [Fact]
        public void Mspdi_refuses_a_dtd_and_a_non_project_root()
        {
            Assert.Throws<ScheduleImportException>(() => ScheduleImport.Parse("p.xml", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"x\">]><Project/>"));
            Assert.Throws<ScheduleImportException>(() => ScheduleImport.Parse("p.xml", "<Other/>"));
        }

        [Fact]
        public void Csv_reads_aliases_quotes_and_semicolons_and_rejects_non_iso_dates_by_line()
        {
            string csv = "codigo;nombre;inicio;fin;edt;avance\n" +
                         "A1;\"Muros; primer piso\";2026-01-05;2026-01-20;1.1;50\n" +
                         "A2;Losa;05/02/2026;2026-02-28;1.2;\n" +
                         "A1;Duplicada;2026-01-05;2026-01-06;1.3;\n";
            ScheduleImportResult r = ScheduleImport.Parse("s.csv", csv);
            Assert.Single(r.Activities);
            Assert.Equal("Muros; primer piso", r.Activities[0].Name);
            Assert.Equal(50, r.Activities[0].PercentComplete);
            Assert.Contains(r.Rejected, x => (int)x["line"] == 3 && ((string)x["reason"]).Contains("ISO"));
            Assert.Contains(r.Rejected, x => (int)x["line"] == 4 && ((string)x["reason"]).Contains("duplicate"));
        }

        [Fact]
        public void Csv_without_a_required_column_is_refused_naming_it()
        {
            var ex = Assert.Throws<ScheduleImportException>(() => ScheduleImport.Parse("s.csv", "id,name,start\nA,B,2026-01-01\n"));
            Assert.Contains("finish", ex.Message);
        }

        [Fact]
        public void Xer_reads_tasks_and_resolves_wbs_paths_without_the_project_node()
        {
            string xer = string.Join("\n",
                "ERMHDR\t19.12",
                "%T\tPROJWBS", "%F\twbs_id\twbs_short_name\tparent_wbs_id",
                "%R\t10\tPRJ\t", "%R\t11\tEST\t10", "%R\t12\tN1\t11",
                "%T\tTASK", "%F\ttask_id\ttask_code\ttask_name\twbs_id\ttask_type\ttarget_start_date\ttarget_end_date\tact_start_date\tact_end_date\tphys_complete_pct",
                "%R\t1\tA100\tMuros\t12\tTT_Task\t2026-01-05 08:00\t2026-01-20 17:00\t2026-01-05 08:00\t\t40",
                "%R\t2\tW\tResumen\t11\tTT_WBS\t\t\t\t\t",
                "%E");
            ScheduleImportResult r = ScheduleImport.Parse("s.xer", xer);
            Assert.Equal("xer", r.Format);
            ScheduleActivity a = Assert.Single(r.Activities);
            Assert.Equal("A100", a.Id);
            Assert.Equal("EST.N1", a.Wbs);
            Assert.Equal(new DateTime(2026, 1, 5), a.ActualStart);
            Assert.Equal(40, a.PercentComplete);
            Assert.Equal(1, r.SummariesSkipped);
        }

        private static ScheduleElementFact El(long id, string cat, string level, params (string k, string v)[] ps)
        {
            var e = new ScheduleElementFact { Id = id, CategoryToken = cat, Level = level };
            foreach (var p in ps) e.Params[p.k] = p.v;
            return e;
        }

        [Fact]
        public void Match_by_parameter_links_reports_gaps_and_never_picks_between_two_activities()
        {
            var acts = new List<ScheduleActivity>
            {
                new ScheduleActivity { Id = "A1", Wbs = "1.1" }, new ScheduleActivity { Id = "A2", Wbs = "1.2" },
                new ScheduleActivity { Id = "A3", Wbs = "1.2" }, new ScheduleActivity { Id = "A4", Wbs = "9" }
            };
            var els = new[]
            {
                El(1, "OST_Walls", "N1", ("COD", "1.1")), El(2, "OST_Walls", "N1", ("COD", "1.2")),
                El(3, "OST_Walls", "N1", ("COD", "")), El(4, "OST_Walls", "N1", ("COD", "7.7")), El(5, "OST_Doors", "N1")
            };
            var spec = JObject.Parse("{\"parameter\":\"COD\",\"key\":\"wbs\"}");
            Assert.Null(ScheduleLinkRules.ValidateSpec(spec, acts));
            ScheduleMatch m = ScheduleLinkRules.Match(spec, acts, els);
            Assert.Equal("A1", m.Links[1].Id);
            Assert.Single(m.Ambiguous);                                   // 1.2 names A2 and A3
            Assert.Equal(new long[] { 3, 4 }, m.WithoutActivity);          // 5 has no parameter: not a candidate
            Assert.Equal(new[] { "A2", "A3", "A4" }, m.ActivitiesWithoutElements.Select(a => a.Id));
        }

        [Fact]
        public void Match_by_rules_uses_category_level_and_parameter_and_refuses_unknown_activities()
        {
            var acts = new List<ScheduleActivity> { new ScheduleActivity { Id = "A1" }, new ScheduleActivity { Id = "A2" } };
            var spec = JObject.Parse("{\"rules\":[{\"activity\":\"A1\",\"category\":\"OST_Walls\",\"level\":\"N1\"}," +
                                     "{\"activity\":\"A2\",\"category\":\"OST_Walls\",\"parameter\":\"Zona\",\"value\":\"B\"}]}");
            ScheduleMatch m = ScheduleLinkRules.Match(spec, acts, new[]
            {
                El(1, "OST_Walls", "N1", ("Zona", "A")), El(2, "OST_Walls", "N2", ("Zona", "B")),
                El(3, "OST_Walls", "N1", ("Zona", "B")), El(4, "OST_Walls", "N2", ("Zona", "C")), El(5, "OST_Floors", "N1")
            });
            Assert.Equal("A1", m.Links[1].Id);
            Assert.Equal("A2", m.Links[2].Id);
            Assert.Contains(m.Ambiguous, a => (long)a["element_id"] == 3);
            Assert.Equal(new long[] { 4 }, m.WithoutActivity);
            Assert.Contains("A9", ScheduleLinkRules.ValidateSpec(JObject.Parse("{\"rules\":[{\"activity\":\"A9\",\"category\":\"OST_Walls\"}]}"), acts));
            Assert.NotNull(ScheduleLinkRules.ValidateSpec(JObject.Parse("{\"rules\":[{\"activity\":\"A1\"}]}"), acts));
        }

        [Fact]
        public void Status_uses_progress_when_present_and_never_calls_a_plan_late()
        {
            var asOf = new DateTime(2026, 2, 10);
            ScheduleActivity P(string s, string f, double? pc = null, string af = null) => new ScheduleActivity
            {
                Start = DateTime.Parse(s), Finish = DateTime.Parse(f), PercentComplete = pc,
                ActualFinish = af == null ? (DateTime?)null : DateTime.Parse(af)
            };
            Assert.Equal("done", ScheduleLinkRules.Status(P("2026-01-01", "2026-01-20", 100), asOf, out bool p1)); Assert.True(p1);
            Assert.Equal("late", ScheduleLinkRules.Status(P("2026-01-01", "2026-01-20", 60), asOf, out _));
            Assert.Equal("late", ScheduleLinkRules.Status(P("2026-02-01", "2026-03-01", 0), asOf, out _));   // should have started
            Assert.Equal("in_progress", ScheduleLinkRules.Status(P("2026-02-01", "2026-03-01", 30), asOf, out _));
            Assert.Equal("future", ScheduleLinkRules.Status(P("2026-03-01", "2026-04-01", 0), asOf, out _));
            Assert.Equal("done", ScheduleLinkRules.Status(P("2026-01-01", "2026-03-01", 10, "2026-02-05"), asOf, out _));
            // No progress data: planned status, and a past finish is "done", never "late".
            Assert.Equal("done", ScheduleLinkRules.Status(P("2026-01-01", "2026-01-20"), asOf, out bool p2)); Assert.False(p2);
            Assert.Equal("in_progress", ScheduleLinkRules.Status(P("2026-02-01", "2026-03-01"), asOf, out _));
            Assert.Equal("future", ScheduleLinkRules.Status(P("2026-03-01", "2026-04-01"), asOf, out _));
        }
    }
}
