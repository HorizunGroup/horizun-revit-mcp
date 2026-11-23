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
        public void Mspdi_reads_integer_ids_and_rejects_a_malformed_uid_by_line()
        {
            var r = ScheduleImport.Parse("p.xml", "<Project><Tasks>\n" +
                "<Task><UID> -000 </UID></Task>\n" +
                "<Task><UID>+00017</UID><ID>2</ID><Name>Muros</Name></Task>\n" +
                "<Task><ID> 018 </ID><Name>Losa</Name></Task>\n" +
                "<Task><UID>17.5</UID><ID>19</ID></Task>\n" +
                "</Tasks></Project>");

            Assert.Equal(1, r.SummariesSkipped);
            Assert.Equal(new[] { "17", "18" }, r.Activities.Select(a => a.Id));
            var rejected = Assert.Single(r.Rejected);
            Assert.Equal(5, (int)rejected["line"]);
            Assert.Contains("UID '17.5'", (string)rejected["reason"]);
        }

        [Fact]
        public void Mspdi_with_zero_percent_everywhere_is_planned_status_never_late()
        {
            // Field case (4D review, 5.2): an MSPDI that was never statused carries <PercentComplete>0</PercentComplete>
            // on every task; read as progress, everything that should have started came out red, "late".
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<Project xmlns=\"http://schemas.microsoft.com/project\"><Name>P</Name><Tasks>\n" +
                "<Task><UID>1</UID><ID>1</ID><Name>Muros N1</Name><WBS>1.1</WBS><Start>2026-01-05T08:00:00</Start><Finish>2026-01-20T17:00:00</Finish><PercentComplete>0</PercentComplete><Summary>0</Summary></Task>\n" +
                "<Task><UID>2</UID><ID>2</ID><Name>Losa N2</Name><WBS>1.2</WBS><Start>2026-02-01T08:00:00</Start><Finish>2026-02-28T17:00:00</Finish><PercentComplete>0</PercentComplete><Summary>0</Summary></Task>\n" +
                "<Task><UID>3</UID><ID>3</ID><Name>Cubierta</Name><WBS>1.3</WBS><Start>2026-03-01T08:00:00</Start><Finish>2026-03-20T17:00:00</Finish><PercentComplete>0</PercentComplete><Summary>0</Summary></Task>\n" +
                "</Tasks></Project>";
            ScheduleImportResult r = ScheduleImport.Parse("p.xml", xml);
            Assert.Equal(3, r.Activities.Count);
            Assert.All(r.Activities, a => { Assert.Equal(0, a.PercentComplete); Assert.True(a.ZeroWithoutActuals); Assert.False(a.HasProgress); });

            var asOf = new DateTime(2026, 2, 10);
            var statuses = new System.Collections.Generic.List<string>();
            foreach (ScheduleActivity a in r.Activities)
            {
                statuses.Add(ScheduleLinkRules.Status(a, asOf, out bool fromProgress));
                Assert.False(fromProgress);
            }
            Assert.Equal(new[] { "done", "in_progress", "future" }, statuses);
            Assert.DoesNotContain("late", statuses);
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
                         "A1;Duplicada;2026-01-05;2026-01-06;1.3;\n" +
                         "A3;Viga;2026-01-05;2026-01-06;1.4;\n";
            ScheduleImportResult r = ScheduleImport.Parse("s.csv", csv);
            Assert.Single(r.Activities);
            Assert.Equal("A3", r.Activities[0].Id);
            Assert.Equal("Muros; primer piso", r.DuplicateActivities[0].Name);
            Assert.Equal(50, r.DuplicateActivities[0].PercentComplete);
            Assert.Contains(r.Rejected, x => (int)x["line"] == 2 && ((string)x["reason"]).Contains("duplicate"));
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
            var e = new ScheduleElementFact { Id = id, UniqueId = "uid-" + id, CategoryToken = cat, Level = level };
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
            // 0 with no actual dates is not progress (MS Project writes it for every unstatused task): planned status.
            Assert.Equal("in_progress", ScheduleLinkRules.Status(P("2026-02-01", "2026-03-01", 0), asOf, out bool p0)); Assert.False(p0);
            Assert.Equal("in_progress", ScheduleLinkRules.Status(P("2026-02-01", "2026-03-01", 30), asOf, out _));
            Assert.Equal("future", ScheduleLinkRules.Status(P("2026-03-01", "2026-04-01", 0), asOf, out _));
            Assert.Equal("done", ScheduleLinkRules.Status(P("2026-01-01", "2026-03-01", 10, "2026-02-05"), asOf, out _));
            // No progress data: planned status, and a past finish is "done", never "late".
            Assert.Equal("done", ScheduleLinkRules.Status(P("2026-01-01", "2026-01-20"), asOf, out bool p2)); Assert.False(p2);
            Assert.Equal("in_progress", ScheduleLinkRules.Status(P("2026-02-01", "2026-03-01"), asOf, out _));
            Assert.Equal("future", ScheduleLinkRules.Status(P("2026-03-01", "2026-04-01"), asOf, out _));
        }

        [Theory]
        [InlineData(null, "Muros N1", "Losa N2")]
        [InlineData("name", "Muros N1", "Losa N2")]
        [InlineData("id", "17", "23")]
        [InlineData("wbs", "1.1", "1.2")]
        public void Write_assigns_and_verifies_the_selected_activity_field_for_each_matched_element(
            string field, string first, string second)
        {
            var schedule = ScheduleImport.Parse("s.csv", "id,name,start,finish,wbs\n" +
                "17,Muros N1,2026-02-01,2026-02-10,1.1\n23,Losa N2,2026-02-11,2026-02-20,1.2\n");
            var match = ScheduleLinkRules.Match(JObject.Parse("{\"parameter\":\"Task Key\"}"), schedule.Activities,
                new[] { El(10, "OST_Walls", "N1", ("Task Key", "23")), El(20, "OST_Walls", "N1", ("Task Key", "17")) });
            var wall = new WriteElement();
            var slab = new WriteElement();
            WriteElement Resolve(string uid) => uid == "uid-10" ? slab : uid == "uid-20" ? wall : null;
            var rows = ScheduleLinkWriteRules.Prepare(match, WriteOptions(field, "Start", "Finish"),
                Resolve, e => e.Owned, Inspect, HasMark);

            // The display name now points at a different same-name parameter. Only the
            // resolved identity may be written or read after the plan has been accepted.
            wall.Names["Activity"] = new[] { "id:200" };
            Assert.Null(ScheduleLinkWriteRules.ApplyValues(rows, Resolve, e => e.Owned, Set, null, new JObject(), 0, null, HasMark));
            Assert.Equal(second, slab.Values["id:100"]);
            Assert.Equal(first, wall.Values["id:100"]);
            Assert.Empty(wall.Values["id:200"]);
            Assert.Equal("2026-02-11", slab.Values["id:101"]);
            Assert.Equal("2026-02-10", wall.Values["id:102"]);

            var reply = ScheduleLinkWriteRules.CompleteAfterCommit("Committed", rows, Resolve, Read, new JObject(), 0, 10, null);
            Assert.True(reply.Success);
            var data = Assert.IsType<JObject>(reply.Data);
            Assert.Equal(2, (int)data["rows_verified"]);
            Assert.Equal(second, (string)data["verification"][0]["values"]["Activity"]);
            Assert.Equal(first, (string)data["verification"][1]["values"]["Activity"]);
            slab.Values["id:100"] = first;
            slab.Values["id:200"] = second;
            slab.Names["Activity"] = new[] { "id:200" };
            Assert.False(rows[0].Verify(Resolve, Read, out JObject swapped, out _, out _));
            Assert.Equal(first, (string)swapped["Activity"]);
        }

        private const string ActivityWriteRequest = @"{
            ""operation"":""write"", ""schedule_path"":""cronograma-4d.csv"",
            ""match"":{""parameter"":""HRZ_TAREA_UID"", ""key"":""id""},
            ""write"":{""activity_parameter"":""HRZ_ACTIVIDAD"", ""start_parameter"":""HRZ_INICIO"", ""finish_parameter"":""HRZ_FIN""},
            ""max_rows"":3
        }";

        [Theory]
        [InlineData(null, "Muros Nivel 1 Z1")]
        [InlineData("id", "17")]
        public void Write_request_uses_the_imported_activity_name_unless_id_is_requested(string field, string expected)
        {
            var request = JObject.Parse(ActivityWriteRequest);
            if (field != null) request["write"]["activity_field"] = field;
            Assert.Null(ScheduleWriteOptions.Read(request, out var options));
            var match = MatchActivityRequest(request,
                El(10, "OST_Walls", "N1", ("HRZ_TAREA_UID", "17")),
                El(20, "OST_Walls", "N1", ("HRZ_TAREA_UID", "18")));
            var wall = ActivityWall();
            var otherWall = ActivityWall();
            WriteElement Resolve(string uid) => uid == "uid-10" ? wall : otherWall;
            var rows = ScheduleLinkWriteRules.Prepare(match, options, Resolve, e => e.Owned, Inspect, HasMark);
            Assert.Equal(2, rows.Count);
            var row = rows[0];
            Assert.Null(row.Unresolved);
            Assert.Equal(expected, row.Values["HRZ_ACTIVIDAD"]);
            Assert.Null(ScheduleLinkWriteRules.ApplyValues(rows, Resolve, e => e.Owned, Set, null, new JObject(), 0, null, HasMark));
            Assert.Equal(expected, wall.Values["id:100"]);
            Assert.Equal("2027-03-29", wall.Values["id:101"]);
            Assert.Equal("2027-04-16", wall.Values["id:102"]);
            Assert.True(row.Verify(Resolve, Read, out var actual, out _, out _));
            Assert.Equal(expected, (string)actual["HRZ_ACTIVIDAD"]);
            Assert.True(rows[1].Verify(Resolve, Read, out var otherActual, out _, out _));
            Assert.Equal(field == "id" ? "18" : "Fachada flotante Z2", (string)otherActual["HRZ_ACTIVIDAD"]);
        }

        private static ScheduleMatch MatchActivityRequest(JObject request, params ScheduleElementFact[] facts)
        {
            var schedule = ScheduleImport.Parse(request.Value<string>("schedule_path"),
                "id,name,start,finish,wbs\n17,Muros Nivel 1 Z1,2027-03-29,2027-04-16,MUR-N1-Z1\n" +
                "18,Fachada flotante Z2,2027-04-19,2027-04-30,FAC-Z2\n");
            var spec = (JObject)request["match"];
            Assert.Null(ScheduleLinkRules.ValidateSpec(spec, schedule));
            return ScheduleLinkRules.Match(spec, schedule, facts);
        }

        private static WriteElement ActivityWall() => new WriteElement
        {
            Names =
            {
                ["HRZ_ACTIVIDAD"] = new[] { "id:100" },
                ["HRZ_INICIO"] = new[] { "id:101" },
                ["HRZ_FIN"] = new[] { "id:102" }
            }
        };

        private const string ActivityKey = "HRZ_TAREA_UID";

        private static ScheduleElementFact ReadActivityKey(long id, string[] instance, string[] type)
        {
            var fact = El(id, "OST_Walls", "N1");
            fact.ReadParameter(ActivityKey, () => instance, () => type,
                key => new ParamFact { Exists = true, Text = key }, _ => "id:200");
            return fact;
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        [InlineData(false, true)]
        public void Duplicate_match_keys_cannot_assign_or_write_an_activity(bool owned, bool byRules)
        {
            var request = JObject.Parse(ActivityWriteRequest);
            request["write"]["allow_unmarked"] = true;
            if (byRules)
                request["match"] = JObject.Parse(@"{""rules"":[
                    {""activity"":""17"",""category"":""OST_Walls"",""level"":""N1""},
                    {""activity"":""18"",""category"":""OST_Walls"",""parameter"":""HRZ_TAREA_UID"",""value"":""18""}]}");

            var match = MatchActivityRequest(request,
                ReadActivityKey(10, new[] { "18", "17" }, new[] { "17" }),
                ReadActivityKey(11, Array.Empty<string>(), new[] { "17", "18" }),
                ReadActivityKey(20, Array.Empty<string>(), new[] { "17" }),
                ReadActivityKey(21, new[] { "17" }, new[] { "17", "18" }));
            Assert.Equal(new long[] { 20, 21 }, match.Links.Keys);
            Assert.Equal(new long[] { 10, 11 }, match.Ambiguous.Select(row => (long)row["element_id"]));
            Assert.All(match.Ambiguous, row =>
            {
                Assert.Equal(ActivityKey, (string)row["parameter"]);
                Assert.Equal("uid-" + (long)row["element_id"], (string)row["unique_id"]);
                Assert.Contains("is ambiguous (2 parameters", (string)row["reason"]);
            });

            var elements = new[] { ActivityWall(), ActivityWall(), ActivityWall(), ActivityWall() };
            foreach (var element in elements) { element.Owned = owned; element.Mark = owned; }
            WriteElement Resolve(string uid) => uid switch
            {
                "uid-10" => elements[0], "uid-11" => elements[1],
                "uid-20" => elements[2], "uid-21" => elements[3], _ => null
            };
            Assert.Null(ScheduleWriteOptions.Read(request, out var options));
            var rows = ScheduleLinkWriteRules.Prepare(match, options, Resolve, e => e.Owned, Inspect, HasMark);
            var ready = rows.Where(row => row.Unresolved == null).ToList();
            Assert.Equal(new long[] { 20, 21 }, ready.Select(row => row.Id));

            const string command = "horizun_link_schedule", fileHash = "csv";
            string planHash = ScheduleLinkRules.PlanHash(request, fileHash, "write");
            var tokens = new ConfirmationStore();
            var document = new DocIdentity { Title = "Schedule model" };
            var plan = new ResolvedPlan { Command = command, DocumentKey = document.Fingerprint() };
            ScheduleLinkWriteRules.BindPlan(plan, ready);
            var rehearsed = ScheduleLinkWriteRules.Rehearse(new JObject(), rows, ready, maxRows: 1,
                tokens, document, planHash, plan, assumptions: null);
            Assert.True(rehearsed.Success);
            var rehearsal = Assert.IsType<JObject>(rehearsed.Data);
            Assert.Equal(2, (int)rehearsal["rows_ready"]);
            Assert.Equal(2, (int)rehearsal["rows_unresolved"]);
            Assert.Equal("uid-10", (string)Assert.Single((JArray)rehearsal["unresolved"])["unique_id"]);
            Assert.Equal("uid-20", (string)Assert.Single((JArray)rehearsal["preview"])["unique_id"]);
            var rehearsalOutcome = (JObject)rehearsal[ApplicationOutcome.Key];
            Assert.Equal("partially_rehearsed", (string)rehearsalOutcome["state"]);
            Assert.Equal(ApplicationOutcome.NotStarted, (string)rehearsalOutcome["transaction_status"]);
            Assert.False((bool)rehearsalOutcome["fully_applied"]);
            Assert.Equal(0, (int)rehearsalOutcome["applied"]);
            Assert.Equal(0, (int)rehearsalOutcome["verified"]);
            Assert.Equal(4, (int)rehearsalOutcome["requested"]);
            Assert.Equal(2, (int)rehearsalOutcome["unresolved"]);
            Assert.Equal(owned ? 0 : 2, (int)rehearsal["rows_unmarked"]);
            Assert.Equal(owned ? 0 : 2, (int)rehearsal["rows_unmarked_override_ready"]);
            Assert.All(elements, element => Assert.Empty(element.Values["id:100"]));
            Assert.Equal(ApplicationState.PartiallyRehearsed, ApplicationOutcome.Read(rehearsal));
            Assert.False(ApplicationOutcome.IsValidRehearsal(ApplicationOutcome.Read(rehearsal)));
            Assert.Equal(new[] { "uid-20", "uid-21" }, plan.Elements.Select(e => e.UniqueId));

            string token = (string)rehearsal["confirmation_token"];
            Assert.False(string.IsNullOrWhiteSpace(token));
            var applyPlan = new ResolvedPlan { Command = command, DocumentKey = document.Fingerprint() };
            ScheduleLinkWriteRules.BindPlan(applyPlan, ready);
            Assert.True(tokens.Validate(token, command, document.Fingerprint(), planHash, applyPlan, null).Ok);

            var applied = new JObject();
            int unresolved = ScheduleLinkWriteRules.StampPreparation(applied, rows, ready, maxRows: 1, dryRun: false);
            Assert.Null(ScheduleLinkWriteRules.ApplyValues(ready, Resolve, e => e.Owned, Set, null, applied, unresolved, null, HasMark));
            var reply = ScheduleLinkWriteRules.CompleteAfterCommit("Committed", ready, Resolve, Read, applied, unresolved, 1, null);
            Assert.True(reply.Success);
            var data = Assert.IsType<JObject>(reply.Data);
            Assert.Equal(2, (int)data["rows_verified"]);
            var applyOutcome = (JObject)data[ApplicationOutcome.Key];
            Assert.Equal("partial", (string)applyOutcome["state"]);
            Assert.False((bool)applyOutcome["fully_applied"]);
            Assert.Equal(4, (int)applyOutcome["requested"]);
            Assert.Equal(2, (int)applyOutcome["verified"]);
            Assert.Equal(2, (int)applyOutcome["unresolved"]);
            Assert.All(elements.Take(2), element => Assert.Empty(element.Values["id:100"]));
            Assert.All(elements.Skip(2), element => Assert.Equal("Muros Nivel 1 Z1", element.Values["id:100"]));
        }

        [Fact]
        public void All_ambiguous_match_keys_leave_requested_rows_unresolved_without_opening_a_transaction()
        {
            var request = JObject.Parse(ActivityWriteRequest);
            var match = MatchActivityRequest(request,
                ReadActivityKey(10, new[] { "17", "18" }, Array.Empty<string>()),
                ReadActivityKey(20, Array.Empty<string>(), new[] { "17", "18" }),
                ReadActivityKey(30, new[] { "99" }, Array.Empty<string>()));
            Assert.Empty(match.Links);
            Assert.Equal(30L, Assert.Single(match.WithoutActivity));
            Assert.Null(ScheduleWriteOptions.Read(request, out var options));
            var rows = ScheduleLinkWriteRules.Prepare<WriteElement>(match, options,
                _ => throw new InvalidOperationException("unexpected target resolution"), e => e.Owned, Inspect, HasMark);
            var ready = rows.Where(row => row.Unresolved == null).ToList();
            Assert.Empty(ready);

            var rehearsed = ScheduleLinkWriteRules.Rehearse(new JObject(), rows, ready, maxRows: 1,
                new ConfirmationStore(), new DocIdentity(), "csv", new ResolvedPlan { Command = "horizun_link_schedule" },
                assumptions: new[] { ScheduleLinkWriteRules.ActivityFieldAssumption(options.RequestedField, options.ActivityParameter) });
            Assert.False(rehearsed.Success);
            var rehearsal = Assert.IsType<JObject>(rehearsed.Detail);
            Assert.Equal(0, (int)rehearsal["rows_ready"]);
            Assert.Equal(2, (int)rehearsal["rows_unresolved"]);
            Assert.Equal("uid-10", (string)Assert.Single((JArray)rehearsal["unresolved"])["unique_id"]);
            Assert.Empty((JArray)rehearsal["preview"]);
            Assert.Null(rehearsal["confirmation_token"]);
            Assert.Equal("rejected", (string)rehearsal[ApplicationOutcome.Key]["state"]);
            Assert.Equal(2, (int)rehearsal[ApplicationOutcome.Key]["requested"]);
            Assert.Equal(2, (int)rehearsal[ApplicationOutcome.Key]["unresolved"]);
            Assert.True((bool)rehearsal["has_assumptions"]);

            var applied = new JObject();
            int unresolved = ScheduleLinkWriteRules.StampPreparation(applied, rows, ready, maxRows: 1, dryRun: false);
            var reply = ScheduleLinkWriteRules.NothingToWrite(applied, unresolved, null);
            Assert.False(reply.Success);
            var outcome = (JObject)reply.Detail[ApplicationOutcome.Key];
            Assert.Equal("failed", (string)outcome["state"]);
            Assert.Equal(ApplicationOutcome.NotStarted, (string)outcome["transaction_status"]);
            Assert.Equal(2, (int)outcome["requested"]);
            Assert.Equal(2, (int)outcome["unresolved"]);
            Assert.Equal(0, (int)outcome["verified"]);
        }

        [Theory]
        [InlineData(false, "the match key names more than one schedule activity.")]
        [InlineData(true, "the match rules assign more than one activity.")]
        public void Conflicting_activities_keep_the_match_reason_and_unique_id_in_unresolved(bool byRules, string reason)
        {
            var request = JObject.Parse(ActivityWriteRequest);
            request["match"] = byRules
                ? JObject.Parse(@"{""rules"":[
                    {""activity"":""17"",""category"":""OST_Walls"",""level"":""N1""},
                    {""activity"":""18"",""category"":""OST_Walls"",""level"":""N1""}]}")
                : JObject.Parse(@"{""parameter"":""HRZ_TAREA_UID"",""key"":""wbs""}");
            var schedule = ScheduleImport.Parse("schedule.csv", "id,name,start,finish,wbs\n" +
                "17,Muros,2027-03-29,2027-04-16,Z1\n18,Fachada,2027-04-19,2027-04-30,Z1\n");
            var spec = (JObject)request["match"];
            Assert.Null(ScheduleLinkRules.ValidateSpec(spec, schedule.Activities));
            var match = ScheduleLinkRules.Match(spec, schedule.Activities,
                new[] { ReadActivityKey(10, new[] { "Z1" }, Array.Empty<string>()) });
            Assert.Empty(match.Links);
            Assert.Equal(new[] { "17", "18" }, ((JArray)Assert.Single(match.Ambiguous)["activities"]).Values<string>());
            Assert.Null(ScheduleWriteOptions.Read(request, out var options));
            var rows = ScheduleLinkWriteRules.Prepare<WriteElement>(match, options,
                _ => throw new InvalidOperationException("unexpected target resolution"), e => e.Owned, Inspect, HasMark);
            var reply = ScheduleLinkWriteRules.Rehearse(new JObject(), rows, Array.Empty<ScheduleWriteRow>(),
                maxRows: 1, new ConfirmationStore(), new DocIdentity(), "csv",
                new ResolvedPlan { Command = "horizun_link_schedule" }, assumptions: null);
            Assert.False(reply.Success);
            var data = Assert.IsType<JObject>(reply.Detail);
            var unresolved = Assert.Single((JArray)data["unresolved"]);
            Assert.Equal(10, (long)unresolved["element_id"]);
            Assert.Equal("uid-10", (string)unresolved["unique_id"]);
            Assert.Equal(reason, (string)unresolved["reason"]);
            Assert.Equal(1, (int)data[ApplicationOutcome.Key]["unresolved"]);
        }

        [Fact]
        public void A_rehearsal_without_write_candidates_is_a_no_op_without_confirmation()
        {
            var rows = Array.Empty<ScheduleWriteRow>();
            var reply = ScheduleLinkWriteRules.Rehearse(new JObject(), rows, rows, maxRows: 1,
                new ConfirmationStore(), new DocIdentity(), "csv", new ResolvedPlan { Command = "horizun_link_schedule" }, assumptions: null);
            Assert.True(reply.Success);
            var data = Assert.IsType<JObject>(reply.Data);
            Assert.Null(data["confirmation_token"]);
            Assert.Equal(ApplicationState.NoOp, ApplicationOutcome.Read(data));
        }

        [Theory]
        [InlineData("activity_parameter", "HRZ_TAREA_UID", false)]
        [InlineData("start_parameter", "HRZ_TAREA_UID", false)]
        [InlineData("finish_parameter", "HRZ_TAREA_UID", false)]
        [InlineData("activity_parameter", "HRZ_ALIAS", false)]
        [InlineData("activity_parameter", "HRZ_ALIAS", true)]
        public void A_schedule_output_cannot_overwrite_the_resolved_match_parameter(string output, string name, bool byRules)
        {
            var request = JObject.Parse(ActivityWriteRequest);
            request["write"]["allow_unmarked"] = true;
            request["write"][output] = name;
            if (byRules)
                request["match"] = JObject.Parse(@"{""rules"":[
                    {""activity"":""17"",""category"":""OST_Walls"",""level"":""N1""},
                    {""activity"":""18"",""category"":""OST_Walls"",""parameter"":""HRZ_TAREA_UID"",""value"":""18""}]}");

            var controlFact = ReadActivityKey(20, new[] { "18" }, Array.Empty<string>());
            controlFact.Level = "N2";
            var match = MatchActivityRequest(request, ReadActivityKey(10, new[] { "17" }, Array.Empty<string>()), controlFact);
            var wall = ActivityWall();
            wall.Owned = false;
            wall.Mark = false;
            wall.Names[name] = new[] { "id:200" };
            wall.Values["id:200"] = "17";
            var control = ActivityWall();
            control.Values["id:200"] = "18";
            control.Names[name] = new[] { "id:300" };
            control.Values["id:300"] = "";
            WriteElement Resolve(string uid) => uid == "uid-10" ? wall : control;
            Assert.Null(ScheduleWriteOptions.Read(request, out var options));
            var rows = ScheduleLinkWriteRules.Prepare(match, options, Resolve, e => e.Owned, Inspect, HasMark);
            var row = Assert.Single(rows, r => r.Unresolved != null);
            Assert.Contains("output parameter '" + name + "' is also a match parameter", row.Unresolved);
            Assert.DoesNotContain(name, row.Fields.Keys);

            var ready = rows.Where(r => r.Unresolved == null).ToList();
            var controlRow = Assert.Single(ready);
            Assert.Equal("uid-20", controlRow.UniqueId);
            var writes = new List<WriteElement>();
            bool RecordSet(WriteElement element, string identity, string value)
            {
                writes.Add(element);
                return Set(element, identity, value);
            }
            Assert.Null(ScheduleLinkWriteRules.ApplyValues(ready, Resolve, e => e.Owned, RecordSet, null, new JObject(), 1, null, HasMark));
            Assert.Equal(3, writes.Count);
            Assert.All(writes, target => Assert.Same(control, target));
            Assert.Equal("17", wall.Values["id:200"]);
            Assert.Equal("18", control.Values["id:200"]);
            var reply = ScheduleLinkWriteRules.CompleteAfterCommit("Committed", ready, Resolve, Read, new JObject(), 1, 3, null);
            Assert.True(reply.Success);
            var verification = Assert.Single((JArray)((JObject)reply.Data)["verification"]);
            Assert.True((bool)verification["verified"]);
            Assert.Equal("uid-20", (string)verification["unique_id"]);
            Assert.Equal("Fachada flotante Z2", (string)verification["values"][options.ActivityParameter]);
        }

        [Fact]
        public void Unmarked_override_rehearses_and_verifies_only_eligible_elements_by_unique_id()
        {
            var request = JObject.Parse(ActivityWriteRequest);
            var elements = new Dictionary<string, WriteElement>(StringComparer.Ordinal);
            foreach (int id in new[] { 10, 20, 30, 40, 50, 60 })
                elements.Add("uid-" + id, ActivityWall());
            elements["uid-20"].Owned = false;
            elements["uid-20"].Mark = false;
            elements["uid-30"].Owned = false;
            elements["uid-40"].Owned = false;
            elements["uid-40"].Mark = null;
            elements["uid-50"].Owned = false;
            elements["uid-50"].Mark = false;
            elements["uid-50"].Names["HRZ_INICIO"] = Array.Empty<string>();
            elements["uid-60"].Mark = false; // Recorded creation in this session, without a physical mark.
            var facts = elements.Keys.Select((_, i) => El(10 * (i + 1), "OST_Walls", "N1", ("HRZ_TAREA_UID", "17"))).ToList();
            var missingIdentity = El(70, "OST_Walls", "N1", ("HRZ_TAREA_UID", "17"));
            missingIdentity.UniqueId = null;
            facts.Add(missingIdentity);
            var match = MatchActivityRequest(request, facts.ToArray());
            WriteElement Resolve(string uid) => elements[uid];
            List<ScheduleWriteRow> Prepare()
            {
                Assert.Null(ScheduleWriteOptions.Read(request, out var options));
                return ScheduleLinkWriteRules.Prepare(match, options, Resolve, e => e.Owned, Inspect, HasMark);
            }

            var rows = Prepare();
            var head = new JObject();
            ScheduleLinkWriteRules.StampOwnership(head, rows);
            Assert.Equal(3, (int)head["rows_unmarked"]);
            Assert.Equal(1, (int)head["rows_unmarked_ready"]);
            Assert.Equal(0, (int)head["rows_unmarked_override_ready"]);
            Assert.Contains("Rehearse with write.allow_unmarked=true", (string)head["unmarked_note"]);
            Assert.Contains("has no Horizun ownership mark", rows[1].Unresolved);
            Assert.Equal(new long[] { 10, 60 }, rows.Where(r => r.Unresolved == null).Select(r => r.Id));

            request["write"]["allow_unmarked"] = true;
            rows = Prepare();
            head = new JObject();
            ScheduleLinkWriteRules.StampOwnership(head, rows);
            Assert.Equal(3, (int)head["rows_unmarked"]);
            Assert.Equal(2, (int)head["rows_unmarked_ready"]);
            Assert.Equal(1, (int)head["rows_unmarked_override_ready"]);
            Assert.Contains("untrusted Horizun ownership mark", rows[2].Unresolved);
            Assert.Contains("could not be verified", rows[3].Unresolved);
            Assert.Contains("no instance parameter", rows[4].Unresolved);
            Assert.Contains("UniqueId", rows[6].Unresolved);
            var ready = rows.Where(r => r.Unresolved == null).ToList();
            Assert.Equal(new long[] { 10, 20, 60 }, ready.Select(r => r.Id));
            Assert.Equal("uid-20", Assert.Single(ready, r => r.UsesUnmarkedOverride).UniqueId);
            Assert.Null(ScheduleLinkWriteRules.ApplyValues(ready, Resolve, e => e.Owned, Set, null, head, 4, null, HasMark));
            var reply = ScheduleLinkWriteRules.CompleteAfterCommit("Committed", ready, Resolve, Read, head, 4, 1, null);
            Assert.True(reply.Success);
            var actual = Assert.IsType<JObject>(reply.Data);
            Assert.Equal(3, (int)actual["rows_verified"]);
            Assert.Equal(1, (int)actual["rows_unmarked_override_ready"]);
            Assert.Equal("uid-10", (string)Assert.Single((JArray)actual["verification"])["unique_id"]);
            Assert.Equal("Muros Nivel 1 Z1", elements["uid-20"].Values["id:100"]);
            Assert.All(new[] { "uid-30", "uid-40", "uid-50" }, uid => Assert.Empty(elements[uid].Values["id:100"]));

            elements["uid-20"].Mark = true;
            var refused = ScheduleLinkWriteRules.ApplyValues(new[] { ready[1] }, Resolve, e => e.Owned,
                (_, _, _) => throw new InvalidOperationException("unexpected Set"), () => "RolledBack", head, 4, null, HasMark);
            Assert.Contains("untrusted Horizun ownership mark", refused.Error);
            Assert.True((bool)refused.Detail["rollback_confirmed"]);
        }

        [Fact]
        public void Unmarked_override_binds_the_option_and_each_admitted_unique_id_to_the_token()
        {
            const string command = "horizun_link_schedule", document = "doc", fileHash = "csv";
            var request = JObject.Parse(ActivityWriteRequest);
            var match = MatchActivityRequest(request,
                El(10, "OST_Walls", "N1", ("HRZ_TAREA_UID", "17")), El(20, "OST_Walls", "N1", ("HRZ_TAREA_UID", "17")));
            var first = ActivityWall();
            var second = ActivityWall();
            WriteElement Resolve(string uid) => uid == "uid-10" ? first : second;
            ResolvedPlan Plan()
            {
                Assert.Null(ScheduleWriteOptions.Read(request, out var options));
                var rows = ScheduleLinkWriteRules.Prepare(match, options, Resolve, e => e.Owned, Inspect, HasMark);
                var plan = new ResolvedPlan { Command = command, DocumentKey = document };
                ScheduleLinkWriteRules.BindPlan(plan, rows.Where(r => r.Unresolved == null).ToList());
                return plan;
            }

            var tokens = new ConfirmationStore();
            string Hash() => ScheduleLinkRules.PlanHash(request, fileHash, "write");
            string Issue(ResolvedPlan plan) =>
                tokens.Issue(command, document, Hash(), elementFingerprint: plan.Fingerprint(), rehearsedPlan: plan).Token;
            string defaultToken = Issue(Plan());
            request["write"]["allow_unmarked"] = true;
            var changed = tokens.Validate(defaultToken, command, document, Hash(), Plan(), null);
            Assert.Equal(ConfirmationState.PlanChanged, changed.State);

            first.Owned = false;
            first.Mark = false;
            var approved = Plan();
            string staleToken = Issue(approved);
            Assert.True(tokens.Validate(Issue(approved), command, document, Hash(), Plan(), null).Ok);

            first.Owned = true;
            first.Mark = true;
            second.Owned = false;
            second.Mark = false;
            Assert.Equal(ConfirmationState.StalePlan,
                tokens.Validate(staleToken, command, document, Hash(), Plan(), null).State);

            request["write"]["start_parameter"] = "Comments";
            Assert.Contains("requires HRZ_* output parameters", ScheduleWriteOptions.Read(request, out _));
        }

        private sealed class WriteElement
        {
            public bool Owned = true;
            public bool? Mark = true;
            public readonly Dictionary<string, string[]> Names = new Dictionary<string, string[]>
                { ["Activity"] = new[] { "id:100" }, ["Start"] = new[] { "id:101" }, ["Finish"] = new[] { "id:102" } };
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>
                { ["id:100"] = "", ["id:101"] = "", ["id:102"] = "", ["id:200"] = "" };
        }

        private static bool? HasMark(WriteElement element) => element.Mark;

        private static ScheduleWriteOptions WriteOptions(string field = null, string start = null, string finish = null) =>
            new ScheduleWriteOptions
            {
                ActivityParameter = "Activity", RequestedField = field, StartParameter = start, FinishParameter = finish
            };

        private static string Read(WriteElement element, string identity) =>
            element.Values.TryGetValue(identity, out string value) ? value : null;

        private static bool Set(WriteElement element, string identity, string value)
        {
            if (!element.Values.ContainsKey(identity)) return false;
            element.Values[identity] = value;
            return true;
        }

        private static string Inspect(WriteElement element, string name, out ScheduleWriteField field)
        {
            field = null;
            string refusal = ScheduleLinkRules.ResolveParameterName(name, element.Names[name], out string identity);
            if (refusal != null) return refusal;
            if (identity == null) return "no instance parameter '" + name + "'.";
            field = new ScheduleWriteField { Identity = identity, Before = Read(element, identity) };
            return null;
        }

        private static ScheduleMatch WriteMatch(params ScheduleElementFact[] facts) =>
            ScheduleLinkRules.Match(JObject.Parse("{\"parameter\":\"Task Key\"}"),
                new[] { new ScheduleActivity { Id = "17", Name = "Muros N1" } }, facts);

        private static ScheduleElementFact WriteFact(long id) => El(id, "OST_Walls", "N1", ("Task Key", "17"));

        [Theory]
        [InlineData("id:100", "id:200")]
        [InlineData("guid:11111111-1111-1111-1111-111111111111", "guid:22222222-2222-2222-2222-222222222222")]
        public void The_schedule_confirmation_rejects_a_replacement_parameter_with_the_same_name_and_value(
            string original, string replacement)
        {
            var element = new WriteElement();
            element.Values[original] = element.Values[replacement] = "Existing";
            var match = WriteMatch(WriteFact(10));
            const string command = "horizun_link_schedule", document = "doc", requestHash = "same-request";
            ResolvedPlan Plan(string identity)
            {
                element.Names["Activity"] = new[] { identity };
                var row = Assert.Single(ScheduleLinkWriteRules.Prepare(match, WriteOptions(),
                    _ => element, e => e.Owned, Inspect, HasMark));
                return new ResolvedPlan { Command = command, DocumentKey = document, Elements = { row.PlanElement() } };
            }

            ResolvedPlan rehearsal = Plan(original);
            var store = new ConfirmationStore();
            string Issue() => store.Issue(command, document, requestHash, elementFingerprint: rehearsal.Fingerprint(),
                rehearsedPlan: rehearsal).Token;
            Assert.Equal(ConfirmationState.StalePlan,
                store.Validate(Issue(), command, document, requestHash, Plan(replacement), null).State);
            Assert.True(store.Validate(Issue(), command, document, requestHash, Plan(original), null).Ok);
        }

        [Theory]
        [InlineData("Activity", "id:100")]
        [InlineData("Start", "id:101")]
        [InlineData("Finish", "id:102")]
        public void Rehearsal_leaves_rows_with_homonymous_output_parameters_unresolved(string name, string identity)
        {
            var ambiguous = new WriteElement();
            var unambiguous = new WriteElement();
            ambiguous.Names[name] = new[] { identity, "id:200" };
            ambiguous.Values[identity] = ambiguous.Values["id:200"] = "Existing";
            WriteElement Resolve(string uid) => uid == "uid-10" ? ambiguous : unambiguous;
            var rows = ScheduleLinkWriteRules.Prepare(WriteMatch(WriteFact(10), WriteFact(20)), WriteOptions(start: "Start", finish: "Finish"),
                Resolve, e => e.Owned, Inspect, HasMark);

            Assert.Contains("parameter name '" + name + "' is ambiguous", rows[0].Unresolved);
            Assert.DoesNotContain(name, rows[0].Fields.Keys);
            var ready = Assert.Single(rows, row => row.Unresolved == null);
            Assert.Equal(20, ready.PlanElement().ElementId);
            Assert.Null(ScheduleLinkWriteRules.ApplyValues(new[] { ready }, Resolve, e => e.Owned, Set, null, new JObject(), 1, null, HasMark));
            Assert.Equal("Muros N1", unambiguous.Values["id:100"]);
            Assert.Equal("Existing", ambiguous.Values[identity]);
            Assert.Equal("Existing", ambiguous.Values["id:200"]);
        }

        [Fact]
        public void Matched_rows_use_the_captured_unique_id_and_check_ownership_before_inspection_and_again_before_set()
        {
            var owned = new WriteElement();
            var foreign = new WriteElement { Owned = false };
            var match = WriteMatch(WriteFact(10), WriteFact(20), WriteFact(30));
            // The former numeric IDs can now point at other elements. Only the captured UID
            // may select a target, and an absent UID must not end up on a replacement.
            WriteElement Resolve(string uid) => uid == "uid-10" ? owned : uid == "uid-20" ? foreign : null;
            string InspectOwned(WriteElement element, string name, out ScheduleWriteField field)
            {
                Assert.Same(owned, element);
                return Inspect(element, name, out field);
            }
            var rows = ScheduleLinkWriteRules.Prepare(match, WriteOptions(), Resolve, e => e.Owned, InspectOwned, HasMark);
            Assert.Null(rows[0].Unresolved);
            Assert.Contains("ownership", rows[1].Unresolved);
            Assert.Contains("UniqueId", rows[2].Unresolved);
            Assert.Empty(foreign.Values["id:100"]);
            owned.Owned = false;
            int sets = 0;
            var failure = ScheduleLinkWriteRules.ApplyValues(new[] { rows[0] }, Resolve, e => e.Owned,
                (e, id, value) => { sets++; return Set(e, id, value); }, () => "RolledBack", new JObject(), 2, null, HasMark);
            Assert.False(failure.Success);
            Assert.Contains("ownership", failure.Error);
            Assert.Equal(0, sets);
        }

        [Theory]
        [InlineData(false, "RolledBack", false)]
        [InlineData(true, "RolledBack", false)]
        [InlineData(true, "Pending", true)]
        [InlineData(true, "Error", true)]
        [InlineData(false, "Pending", true)]
        [InlineData(true, null, true)]
        public void A_set_failure_after_an_earlier_write_preserves_rollback_evidence_and_the_default_field(
            bool throws, string rollbackStatus, bool mayHaveChanged)
        {
            var first = new WriteElement();
            var second = new WriteElement();
            WriteElement Resolve(string uid) => uid == "uid-10" ? first : second;
            var rows = ScheduleLinkWriteRules.Prepare(WriteMatch(WriteFact(10), WriteFact(20)), WriteOptions(),
                Resolve, e => e.Owned, Inspect, HasMark);
            bool SetOrFail(WriteElement e, string identity, string value)
            {
                if (ReferenceEquals(e, first)) return Set(e, identity, value);
                if (throws) throw new InvalidOperationException("Set failed");
                return false;
            }
            int rollbacks = 0;
            string RollBack()
            {
                rollbacks++;
                Assert.Equal("Muros N1", first.Values["id:100"]);
                if (rollbackStatus == "RolledBack") first.Values["id:100"] = "";
                return rollbackStatus ?? throw new InvalidOperationException("Rollback failed");
            }
            var failure = ScheduleLinkWriteRules.ApplyValues(rows, Resolve, e => e.Owned, SetOrFail, RollBack, new JObject(), 0,
                new[] { ScheduleLinkWriteRules.ActivityFieldAssumption(null, "Activity") }, HasMark);

            Assert.Equal(1, rollbacks);
            Assert.False(failure.Success);
            Assert.Contains(throws ? "Set failed" : "refused to set", failure.Error);
            Assert.Contains(mayHaveChanged ? "model may have changed" : "Nothing was changed", failure.Error);
            Assert.Equal(rollbackStatus ?? "unknown", (string)failure.Detail["rollback_status"]);
            Assert.Equal(rollbackStatus ?? "unknown", (string)failure.Detail[ApplicationOutcome.Key]["transaction_status"]);
            Assert.Equal(!mayHaveChanged, (bool)failure.Detail["rollback_confirmed"]);
            Assert.Equal(mayHaveChanged, PlanFailure.ModelMayHaveChanged(failure.Detail));
            Assert.True((bool)failure.Detail["has_assumptions"]);
            Assert.Equal("SUPUESTO", (string)failure.Detail["assumptions"][0]["flag"]);
            if (rollbackStatus == null) Assert.Equal("Rollback failed", (string)failure.Detail["rollback_error"]);
        }

        [Theory]
        [InlineData("Pending", true, "uncertain")]
        [InlineData("Error", true, "uncertain")]
        [InlineData("RolledBack", false, "rolled_back")]
        public void An_uncommitted_write_reports_its_transaction_outcome_without_verifying_values(
            string status, bool mayHaveChanged, string applicationState)
        {
            var element = new WriteElement();
            var rows = ScheduleLinkWriteRules.Prepare(WriteMatch(WriteFact(10)), WriteOptions(),
                _ => element, e => e.Owned, Inspect, HasMark);
            Assert.Null(ScheduleLinkWriteRules.ApplyValues(rows, _ => element, e => e.Owned, Set, null, new JObject(), 0, null, HasMark));
            Assert.Equal("Muros N1", element.Values["id:100"]);

            var failure = ScheduleLinkWriteRules.CompleteAfterCommit<WriteElement>(status, rows,
                _ => throw new InvalidOperationException("An uncommitted write cannot be re-read."), Read,
                new JObject(), 0, 10, new[] { ScheduleLinkWriteRules.ActivityFieldAssumption(null, "Activity") });

            Assert.False(failure.Success);
            Assert.Contains("commit " + status, failure.Error);
            Assert.Equal(mayHaveChanged, PlanFailure.ModelMayHaveChanged(failure.Detail));
            Assert.Equal(mayHaveChanged, failure.Error.Contains("The model may have changed."));
            Assert.Equal(status, (string)failure.Detail[ApplicationOutcome.Key]["transaction_status"]);
            Assert.Equal(applicationState, (string)failure.Detail[ApplicationOutcome.Key]["state"]);
            Assert.Equal(0, (int)failure.Detail["rows_verified"]);
            Assert.Null((string)failure.Detail["verification"][0]["values"]["Activity"]);
            Assert.False((bool)failure.Detail["verification"][0]["verified"]);
            Assert.Equal("SUPUESTO", (string)failure.Detail["assumptions"][0]["flag"]);
        }

        [Fact]
        public void An_empty_selected_field_has_no_implicit_id_fallback()
        {
            var activity = new ScheduleActivity { Id = "17", Name = " " };
            Assert.Null(ScheduleLinkRules.WriteValues(activity, "name", "Activity", null, null));
            Assert.Null(ScheduleLinkRules.WriteValues(activity, "wbs", "Activity", null, null));
        }

        [Theory]
        [InlineData(null, "done", "done", "in_progress")]
        [InlineData(true, "done", "done", "in_progress")]
        [InlineData(false, "in_progress", "in_progress", "future")]
        public void Status_cutoff_applies_to_planned_and_actual_calendar_days(bool? include, string plannedStatus,
            string actualStatus, string startStatus)
        {
            var day = new DateTime(2026, 2, 10);
            var activity = new ScheduleActivity { Start = day.AddDays(-1), Finish = day.AddHours(18) };
            Assert.Equal(include == false ? "2026-02-09" : "2026-02-10",
                ScheduleImport.Day(ScheduleLinkRules.LastIncludedDay(day.AddHours(12), include)));
            Assert.Equal(plannedStatus, ScheduleLinkRules.Status(activity, day, out bool progress, include));
            Assert.False(progress);

            activity.ActualStart = day.AddDays(-1);
            activity.ActualFinish = day.AddHours(22);
            activity.PercentComplete = 100;
            Assert.Equal(actualStatus, ScheduleLinkRules.Status(activity, day, out progress, include));
            Assert.True(progress);
            Assert.Equal("done", ScheduleLinkRules.Status(activity, day.AddDays(1), out _, include));
            activity.ActualStart = day.AddHours(8);
            activity.ActualFinish = null;
            activity.PercentComplete = 30;
            Assert.Equal(startStatus, ScheduleLinkRules.Status(activity, day, out _, include));

            var reply = AssumptionNotes.Stamp(new JObject(), new[] { ScheduleLinkRules.StatusCutoffAssumption(include) });
            if (include.HasValue) Assert.Null(reply["assumptions"]);
            else
            {
                Assert.True((bool)reply["has_assumptions"]);
                Assert.Equal("SUPUESTO", (string)reply["assumptions"][0]["flag"]);
                Assert.Contains("include_as_of_day", (string)reply["assumptions"][0]["override"]);
            }
        }

        [Fact]
        public void Exclusive_cutoff_at_the_first_calendar_day_includes_no_day()
        {
            var firstDay = DateTime.MinValue;
            var activity = new ScheduleActivity { Start = firstDay, Finish = firstDay };
            Assert.Null(ScheduleLinkRules.LastIncludedDay(firstDay, false));
            Assert.Equal("future", ScheduleLinkRules.Status(activity, firstDay, out _, false));
            Assert.Equal("done", ScheduleLinkRules.Status(activity, firstDay, out _, true));
        }

        [Fact]
        public void Status_basis_identifies_only_activities_done_from_undated_snapshot_progress()
        {
            var day = new DateTime(2026, 2, 10);
            ScheduleActivity Activity(string id, double? progress, DateTime? finish, DateTime? start = null) =>
                new ScheduleActivity { Id = id, PercentComplete = progress, ActualFinish = finish, ActualStart = start,
                    Start = day.AddDays(-4), Finish = day.AddDays(-1) };
            var match = new ScheduleMatch();
            var undated = Activity("snapshot", 100, null);
            match.Links.Add(1, undated);
            match.Links.Add(2, undated);
            match.Links.Add(3, Activity("dated", 100, day.AddDays(-1)));
            match.Links.Add(4, Activity("planned", null, null));
            match.Links.Add(5, Activity("later-finish", 100, day));
            match.Links.Add(6, Activity("later-start", 100, null, day));
            match.Links.Add(7, Activity("another-snapshot", 100, null));

            JObject basis = ScheduleLinkRules.StatusBasis(match, day, false, 1, out var status);
            Assert.Equal("done", status[1]);
            Assert.Equal("done", status[3]);
            Assert.Equal("done", status[4]);
            Assert.NotEqual("done", status[5]);
            Assert.NotEqual("done", status[6]);
            Assert.Equal(2, (int)basis["done_from_undated_progress_activity_count"]);
            Assert.Equal(new[] { "another-snapshot" }, basis["done_from_undated_progress_activity_ids"].Values<string>());

            basis = ScheduleLinkRules.StatusBasis(match, day, false, 10, out _);
            Assert.Equal(new[] { "another-snapshot", "snapshot" }, basis["done_from_undated_progress_activity_ids"].Values<string>());
        }
    }
}
