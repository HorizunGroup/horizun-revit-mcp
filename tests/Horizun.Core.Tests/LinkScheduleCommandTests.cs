using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class LinkScheduleCommandTests : IDisposable
    {
        private const string Key = "HRZ_TAREA_UID", Output = "HRZ_ACTIVIDAD";
        private const string Header = "id,name,start,finish,wbs\n";
        private const string Walls = "17,Muros Nivel 1 Z1,2027-03-29,2027-04-16,MUR-N1-Z1\n";
        private const string Duplicate = "17,Fachada flotante Z2,2027-04-19,2027-04-30,FAC-Z2\n";
        private const string Slab = "18,Losa,2027-04-19,2027-04-30,LOS\n";

        private string path = Path.Combine(Path.GetTempPath(), "hz-schedule-" + Guid.NewGuid().ToString("N") + ".csv");
        private readonly Document doc = new Document { TestScheduleHost = true, Title = Guid.NewGuid().ToString("N") };
        private readonly LinkScheduleCommand command = new LinkScheduleCommand();

        public void Dispose() => File.Delete(path);

        [Theory]
        [InlineData(false, "id", "17", "18")]
        [InlineData(true, "id", "17", "18")]
        [InlineData(false, "wbs", "MUR-N1-Z1", "LOS")]
        [InlineData(true, "wbs", "FAC-Z2", "LOS")]
        [InlineData(false, "rules", "17", "18")]
        [InlineData(true, "rules", "17", "18")]
        public void Duplicate_activity_ids_never_choose_a_row_even_when_the_csv_order_changes(
            bool reverse, string matchBy, string ambiguousKey, string uniqueKey)
        {
            File.WriteAllText(path, Header + (reverse ? Duplicate + Walls : Walls + Duplicate) + Slab);
            var wall = Wall(10, ambiguousKey);
            var slab = Wall(20, uniqueKey);

            var request = Request("write");
            if (matchBy == "rules")
                request["match"] = new JObject { ["rules"] = new JArray(Rule("17"), Rule("18")) };
            else
                request["match"]["key"] = matchBy;

            var preview = Data(Call(request));
            Assert.Equal(2, (int)preview["schedule"]["rejected_rows"]);
            Assert.Equal(1, (int)preview["schedule"]["activities"]);
            var issue = Assert.Single((JArray)preview["ambiguous"]);
            Assert.Equal(wall.UniqueId, (string)issue["unique_id"]);
            Assert.Equal(new[] { 2, 3 }, ((JArray)issue["schedule_lines"]).Values<int>());
            Assert.Contains("duplicated", (string)Assert.Single((JArray)preview["unresolved"])["reason"]);
            Assert.Equal("partially_rehearsed", (string)preview["application"]["state"]);

            var applied = Data(Apply(request, preview));
            Assert.Equal("partial", (string)applied["application"]["state"]);
            Assert.Equal(1, (int)applied["application"]["unresolved"]);
            Assert.Equal(1, (int)applied["application"]["verified"]);
            Assert.Equal("before", wall.LookupParameter(Output).AsString());
            Assert.Equal("Losa", slab.LookupParameter(Output).AsString());
            Assert.Equal(slab.UniqueId, (string)Assert.Single((JArray)applied["verification"])["unique_id"]);
        }

        [Theory]
        [InlineData(" 17 ", "wbs")]
        [InlineData("+0017", "wbs")]
        [InlineData("017", "id")]
        [InlineData("+0017", "rules")]
        public void Equivalent_mspdi_uids_leave_both_activities_unresolved_without_a_token(string equivalentUid, string matchBy)
        {
            path = Path.ChangeExtension(path, ".xml");
            File.WriteAllText(path, "<Project xmlns=\"http://schemas.microsoft.com/project\"><Tasks>\n" +
                "<Task><UID>17</UID><Name>Muros</Name><WBS>MUROS</WBS><Start>2027-03-29</Start><Finish>2027-04-16</Finish></Task>\n" +
                $"<Task><UID>{equivalentUid}</UID><Name>Fachada</Name><WBS>FACHADA</WBS><Start>2027-04-19</Start><Finish>2027-04-30</Finish></Task>\n" +
                "</Tasks></Project>");
            var wall = Wall(10, matchBy == "wbs" ? "MUROS" : "17");
            var facade = Wall(20, matchBy == "wbs" ? "FACHADA" : equivalentUid);
            var request = Request("write");
            request["max_rows"] = 3;
            if (matchBy == "rules")
                request["match"] = new JObject { ["rules"] = new JArray(Rule("17"), Rule(equivalentUid)) };
            else
                request["match"]["key"] = matchBy;

            var preview = Rejection(Call(request));
            Assert.Null(preview["confirmation_token"]);
            Assert.Equal(0, (int)preview["schedule"]["activities"]);
            Assert.Equal(new[] { 2, 3 }, ((JArray)preview["schedule"]["rejected"]).Select(row => (int)row["line"]));
            Assert.Equal(2, (int)preview["application"]["unresolved"]);
            Assert.Equal(new[] { wall.UniqueId, facade.UniqueId }, ((JArray)preview["ambiguous"]).Select(row => (string)row["unique_id"]));
            Assert.All((JArray)preview["ambiguous"], issue =>
            {
                Assert.Equal("17", (string)issue["activity"]);
                Assert.Equal(new[] { 2, 3 }, ((JArray)issue["schedule_lines"]).Values<int>());
            });
            Assert.Equal(ApplicationOutcome.NotStarted, (string)Rejection(Apply(request, preview))["application"]["transaction_status"]);
            Assert.Equal(0, doc.TestTransactionStarts);
            Assert.Equal("before", wall.LookupParameter(Output).AsString());
            Assert.Equal("before", facade.LookupParameter(Output).AsString());
        }

        [Theory]
        [InlineData("id")]
        [InlineData("rules")]
        public void Equivalent_mspdi_reference_links_a_unique_activity_and_writes_its_name(string matchBy)
        {
            path = Path.ChangeExtension(path, ".xml");
            File.WriteAllText(path, "<Project xmlns=\"http://schemas.microsoft.com/project\"><Tasks>\n" +
                "<Task><UID>17</UID><Name>Muros Nivel 1 Z1</Name><Start>2027-03-29</Start><Finish>2027-04-16</Finish></Task>\n" +
                "</Tasks></Project>");
            var wall = Wall(10, "+0017");
            var request = Request("write");
            if (matchBy == "rules")
                request["match"] = new JObject { ["rules"] = new JArray(Rule("+0017")) };
            else
                request["match"]["key"] = "id";

            var preview = Data(Call(request));
            Assert.Equal(1, (int)preview["schedule"]["activities"]);
            Assert.Equal(0, (int)preview["application"]["unresolved"]);
            Assert.False(string.IsNullOrEmpty((string)preview["confirmation_token"]));
            Assert.Equal(0, doc.TestTransactionStarts);
            Assert.Equal("before", wall.LookupParameter(Output).AsString());

            var applied = Data(Apply(request, preview));
            Assert.Equal("verified_applied", (string)applied["application"]["state"]);
            var row = Assert.Single((JArray)applied["verification"]);
            Assert.Equal(wall.UniqueId, (string)row["unique_id"]);
            Assert.Equal("17", (string)row["activity"]);
            Assert.Equal("Muros Nivel 1 Z1", (string)row["values"][Output]);
            Assert.Equal("Muros Nivel 1 Z1", wall.LookupParameter(Output).AsString());
        }

        [Theory]
        [InlineData("wbs")]
        [InlineData("rules")]
        public void Conflicting_matches_report_every_relevant_duplicate_activity_and_its_lines(string matchBy)
        {
            const string secondWbs = "18,Losa Z1,2027-04-19,2027-04-30,MUR-N1-Z1\n";
            File.WriteAllText(path, Header + (matchBy == "wbs" ? Walls + secondWbs + Duplicate + Slab : Walls + Slab + Duplicate));
            var wall = Wall(10, matchBy == "wbs" ? "MUR-N1-Z1" : "shared-key");
            var request = Request("write");
            if (matchBy == "rules")
            {
                var rules = new JArray(Rule("18"), Rule("17"));
                foreach (var rule in rules) rule["value"] = "shared-key";
                request["match"] = new JObject { ["rules"] = rules };
            }
            else request["match"]["key"] = "wbs";

            var preview = Rejection(Call(request));
            Assert.Null(preview["confirmation_token"]);
            Assert.Equal(1, (int)preview["application"]["unresolved"]);
            var issue = Assert.Single((JArray)preview["ambiguous"]);
            Assert.Equal(wall.UniqueId, (string)issue["unique_id"]);
            Assert.Equal(new[] { "17", "18" }, Assert.IsType<JArray>(issue["activities"]).Values<string>());
            Assert.Contains(matchBy == "wbs" ? "match key" : "match rules", (string)issue["reason"]);
            var duplicates = Assert.IsType<JArray>(issue["duplicate_activities"]).ToDictionary(d => (string)d["activity"]);
            Assert.Equal(matchBy == "wbs" ? 2 : 1, duplicates.Count);
            Assert.Equal(new[] { 2, 4 }, ((JArray)duplicates["17"]["schedule_lines"]).Values<int>());
            if (matchBy == "wbs")
                Assert.Equal(new[] { 3, 5 }, ((JArray)duplicates["18"]["schedule_lines"]).Values<int>());
            Assert.Equal(0, doc.TestTransactionStarts);
            Assert.Equal("before", wall.LookupParameter(Output).AsString());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Write_uses_all_homonyms_and_issues_a_real_token_only_for_ready_rows(bool includeUniqueWall)
        {
            File.WriteAllText(path, Header + Walls + Slab);
            var ambiguous = Wall(10, "17", "18");
            Element ready = includeUniqueWall ? Wall(20, "17") : null;
            var request = Request("write");
            var result = Call(request);
            JObject reply = includeUniqueWall ? Data(result) : Rejection(result);
            Assert.Equal(includeUniqueWall ? "partially_rehearsed" : "rejected", (string)reply["application"]["state"]);
            Assert.Equal(0, doc.TestTransactionStarts);
            Assert.Equal("before", ambiguous.LookupParameter(Output).AsString());
            var issue = Assert.Single((JArray)reply["unresolved"]);
            Assert.Equal(ambiguous.UniqueId, (string)issue["unique_id"]);
            Assert.Contains("2 parameters", (string)issue["reason"]);
            if (!includeUniqueWall)
            {
                Assert.Null(reply["confirmation_token"]);
                Assert.Equal(ApplicationOutcome.NotStarted, (string)Rejection(Apply(request, reply))["application"]["transaction_status"]);
                Assert.Equal(0, doc.TestTransactionStarts);
                return;
            }

            var planned = Assert.Single((JArray)reply["change_preview"]["rows"]);
            Assert.Equal(ready.UniqueId, (string)planned["unique_id"]);
            Assert.Equal("Muros Nivel 1 Z1", (string)planned["proposed_values"][Output]);

            // A newly resolvable wall must not slip into the already confirmed plan.
            var second = ambiguous.GetParameters(Key)[1];
            ambiguous.Parameters.Remove(second);
            Assert.Equal("StalePlan", (string)Rejection(Apply((JObject)request.DeepClone(), reply))["confirmation_state"]);
            Assert.Equal(0, doc.TestTransactionStarts);
            ambiguous.Parameters.Add(second);

            var applied = Data(Apply(request, reply));
            Assert.Equal("partial", (string)applied["application"]["state"]);
            Assert.Equal("Muros Nivel 1 Z1", ready.LookupParameter(Output).AsString());
            Assert.Equal("before", ambiguous.LookupParameter(Output).AsString());
        }

        [Fact]
        public void Write_corrects_the_activity_name_and_verifies_dates_without_setting_an_unchanged_date()
        {
            var request = SetupWriteWithDates(out var wall, out var activity, out var start, out var finish);
            var preview = Data(Call(request));
            var applied = Data(Apply(request, preview));

            Assert.Equal("verified_applied", (string)applied["application"]["state"]);
            Assert.Equal(0, doc.TestRollbacks);
            Assert.Equal(1, activity.TestSetCalls);
            Assert.Equal(0, start.TestSetCalls);
            Assert.Equal(1, finish.TestSetCalls);
            var verified = Assert.Single((JArray)applied["verification"]);
            Assert.True((bool)verified["verified"]);
            Assert.Equal(wall.UniqueId, (string)verified["unique_id"]);
            Assert.Equal("Muros Nivel 1 Z1", (string)verified["values"][Output]);
            Assert.Equal("2027-03-29", (string)verified["values"][start.Name]);
            Assert.Equal("2027-04-16", (string)verified["values"][finish.Name]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Write_reports_cleanup_failure_and_preserves_post_commit_evidence(bool readThrows)
        {
            var request = SetupWriteWithDates(out var wall, out var activity, out _, out var finish);
            doc.IsWorkshared = true;
            doc.TestCommittedTransactionDisposeError = new InvalidOperationException("schedule transaction cleanup failure");
            var preview = Data(Call(request));
            doc.TestAfterCommit = () =>
            {
                doc.IsModified = true;
                finish.TestScheduleReadThrows = readThrows;
            };

            var result = Apply(request, preview);
            var detail = AssertCommittedCleanupFailure(result, readThrows, verified: 1);
            Assert.Equal("Muros Nivel 1 Z1", activity.Text);
            Assert.Equal(0, doc.TestRollbacks);
            var row = Assert.Single((JArray)detail["verification"]);
            Assert.Equal(wall.UniqueId, (string)row["unique_id"]);
            Assert.Equal("Muros Nivel 1 Z1", (string)row["values"][Output]);
            Assert.Equal(!readThrows, (bool)row["verified"]);
            if (readThrows)
                Assert.Equal("parameter read failure", (string)row["parameter_read_errors"][finish.Name]);
            AssertSyncBlocked(request, result, wall);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Write_records_a_failed_post_commit_read_in_the_sync_ledger(bool uniqueIdLookupThrows)
        {
            var request = SetupWriteWithDates(out var wall, out var activity, out var start, out var finish);
            doc.IsWorkshared = true;
            var preview = Data(Call(request));
            doc.TestAfterCommit = () =>
            {
                doc.IsModified = true;
                if (uniqueIdLookupThrows)
                    doc.TestScheduleUniqueIdLookup = uid => throw new InvalidOperationException("UniqueId lookup failure");
                else
                    finish.TestScheduleReadThrows = true;
            };

            var result = Apply(request, preview);
            var detail = Rejection(result);
            Assert.Equal("Muros Nivel 1 Z1", activity.Text);
            Assert.Equal("2027-03-29", start.Text);
            Assert.Equal("2027-04-16", finish.Text);
            Assert.Equal(0, start.TestSetCalls);
            Assert.Equal(0, doc.TestRollbacks);
            Assert.Equal("Committed", (string)detail["application"]["transaction_status"]);
            Assert.Equal("uncertain", (string)detail["application"]["state"]);
            Assert.Equal(uniqueIdLookupThrows ? 0 : 1, (int)detail["application"]["applied"]);
            Assert.Equal(1, (int)detail["application"]["unknown"]);
            Assert.Equal(0, (int)detail["application"]["failed"]);
            Assert.Equal(0, (int)detail["rows_verified"]);

            Assert.True((bool)detail["changes_applied"]);
            AssertSyncBlocked(request, result, wall);

            var row = Assert.Single((JArray)detail["verification"]);
            Assert.Equal(wall.UniqueId, (string)row["unique_id"]);
            Assert.False((bool)row["verified"]);
            Assert.Equal(JTokenType.Null, row["values"][finish.Name].Type);
            if (uniqueIdLookupThrows)
            {
                Assert.Equal(JTokenType.Null, row["values"][Output].Type);
                Assert.Contains("UniqueId lookup failure", (string)row["element_read_error"]);
            }
            else
            {
                Assert.Equal("Muros Nivel 1 Z1", (string)row["values"][Output]);
                Assert.Equal("2027-03-29", (string)row["values"][start.Name]);
                Assert.Contains("parameter read failure", (string)row["parameter_read_errors"][finish.Name]);
            }
        }

        [Fact]
        public void Write_counts_a_measured_parameter_mismatch_as_failed()
        {
            File.WriteAllText(path, Header + Walls);
            var wall = Wall(17, "17");
            var activity = wall.LookupParameter(Output);
            var request = Request("write");
            var preview = Data(Call(request));
            doc.TestAfterCommit = () => activity.Text = "17";

            JObject detail = Rejection(Apply(request, preview));
            Assert.Equal("Committed", (string)detail["application"]["transaction_status"]);
            Assert.Equal("failed", (string)detail["application"]["state"]);
            Assert.Equal(0, (int)detail["application"]["unknown"]);
            Assert.Equal(1, (int)detail["application"]["failed"]);
            var row = Assert.Single((JArray)detail["verification"]);
            Assert.Equal(wall.UniqueId, (string)row["unique_id"]);
            Assert.Equal("17", (string)row["values"][Output]);
        }

        [Theory]
        [InlineData("17", "partial", 1)]
        [InlineData("Muros Nivel 1 Z1", "failed", 0)]
        public void Write_reports_a_partial_row_only_when_a_changed_value_is_confirmed(
            string originalActivity, string expectedState, int appliedRows)
        {
            var request = SetupWriteWithDates(out var wall, out var activity, out var start, out var finish);
            activity.Text = originalActivity;
            doc.IsWorkshared = true;
            var preview = Data(Call(request));
            doc.TestAfterCommit = () =>
            {
                doc.IsModified = true;
                finish.Text = "2027-04-17";
            };

            var result = Apply(request, preview);
            JObject detail = Rejection(result);
            var application = detail["application"];
            Assert.Equal("Committed", (string)application["transaction_status"]);
            Assert.True((bool)detail["changes_applied"]);
            Assert.Equal(expectedState, (string)application["state"]);
            Assert.False((bool)application["fully_applied"]);
            Assert.Equal(1, (int)application["requested"]);
            Assert.Equal(appliedRows, (int)application["applied"]);
            Assert.Equal(0, (int)application["verified"]);
            Assert.Equal(0, (int)application["unresolved"]);
            Assert.Equal(1, (int)application["failed"]);
            Assert.Equal(0, (int)application["unknown"]);
            Assert.Equal(0, (int)detail["rows_verified"]);
            var row = Assert.Single((JArray)detail["verification"]);
            Assert.False((bool)row["verified"]);
            Assert.Equal(wall.UniqueId, (string)row["unique_id"]);
            Assert.Equal("Muros Nivel 1 Z1", (string)row["values"][Output]);
            Assert.Equal("2027-03-29", (string)row["values"][start.Name]);
            Assert.Equal("2027-04-16", (string)row["expected"][finish.Name]);
            Assert.Equal("2027-04-17", (string)row["values"][finish.Name]);
            AssertSyncBlocked(request, result, wall);
        }

        private void AssertSyncBlocked(JObject request, CommandResult result, Element element, bool added = false)
        {
            var changes = new ChangeWatch.DocChanges { Document = doc };
            (added ? changes.Added : changes.Modified).Add(element.Id.Value);
            var watch = new ChangeWatch();
            watch.Documents.Add(changes);
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            try
            {
                SyncReminder.AfterCommand(app, command.Name, request.ToString(), watch, result);
                var blockers = SyncLedgerRules.Blockers(doc.Title);
                var blocker = Assert.Single(blockers);
                Assert.Equal("postcommit_verification_failed", blocker.BlockReason);
                Assert.Equal(element.Id.Value, Assert.Single(blocker.Ids));
                Assert.Equal(SyncLedgerRules.UnverifiedCode, SyncLedgerRules.Accept(blockers, false, null, false).Code);
                Assert.Equal(1, (int)result.Detail["pending_sync"]["blockers_failed_checks"]);
            }
            finally { SyncLedgerRules.MarkSynced(doc.Title, DateTime.UtcNow); }
        }

        private JObject SetupWriteWithDates(out Element wall, out Parameter activity, out Parameter start, out Parameter finish)
        {
            File.WriteAllText(path, Header + Walls);
            wall = Wall(17, "17");
            activity = wall.LookupParameter(Output);
            activity.Text = "17";
            start = new Parameter { Name = "HRZ_INICIO", Id = new ElementId(51), Text = "2027-03-29" };
            finish = new Parameter { Name = "HRZ_FIN", Id = new ElementId(52), Text = "" };
            wall.Parameters.Add(start);
            wall.Parameters.Add(finish);
            var request = Request("write");
            request["match"]["key"] = "id";
            request["write"]["start_parameter"] = start.Name;
            request["write"]["finish_parameter"] = finish.Name;
            return request;
        }

        [Theory]
        [InlineData(false, false, "no_op", null)]
        [InlineData(false, true, "rehearsed", "verified_applied")]
        [InlineData(true, false, "rejected", null)]
        [InlineData(true, true, "partially_rehearsed", "partial")]
        public void Status_view_accounts_for_ambiguous_walls_and_does_not_confirm_an_empty_plan(
            bool includeAmbiguous, bool includeUnique, string previewState, string applyState)
        {
            File.WriteAllText(path, Header + Walls + Slab);
            var source = Add(new View { Id = new ElementId(1), Name = "N1", Document = doc, ViewTemplateId = new ElementId(99) });
            Add(new FillPatternElement { Id = new ElementId(2) });
            Element ambiguous = includeAmbiguous ? Wall(10, "17", "18") : null;
            Element ready = includeUnique ? Wall(20, "17") : null;
            var request = Request("status_view");
            request["view_id"] = source.Id.Value;
            request["as_of"] = "2027-04-16";

            var rehearsed = Call(request);
            JObject preview = includeAmbiguous && !includeUnique ? Rejection(rehearsed) : Data(rehearsed);
            int unresolved = includeAmbiguous ? 1 : 0;
            int requested = (includeUnique ? 2 : 0) + unresolved;
            Assert.Equal(previewState, (string)preview["application"]["state"]);
            Assert.Equal(requested, (int)preview["application"]["requested"]);
            Assert.Equal(unresolved, (int)preview["application"]["unresolved"]);
            Assert.Equal(unresolved, (int)preview["ambiguous_elements"]);
            Assert.Equal(0, (int)preview["application"]["applied"]);
            Assert.Equal(0, doc.TestTransactionStarts);
            if (includeAmbiguous)
                Assert.Equal(ambiguous.UniqueId, (string)Assert.Single((JArray)preview["ambiguous"])["unique_id"]);
            if (!includeUnique)
            {
                Assert.Null(preview["confirmation_token"]);
                var refusal = Rejection(Apply(request, preview));
                Assert.Equal(ApplicationOutcome.NotStarted, (string)refusal["application"]["transaction_status"]);
                Assert.Equal(unresolved, (int)refusal["application"]["unresolved"]);
                Assert.Equal(0, doc.TestTransactionStarts);
                Assert.Single(doc.Elements.Values.OfType<View>());
                return;
            }

            Assert.Equal(2, (int)preview["plan_resolved"]["elements"]);
            Assert.StartsWith("SUPUESTO", (string)Assert.Single((JArray)preview["assumptions"])["flag"]);
            var applied = Data(Apply(request, preview));
            Assert.Equal(applyState, (string)applied["application"]["state"]);
            Assert.Equal(!includeAmbiguous, (bool)applied["application"]["fully_applied"]);
            Assert.Equal(requested, (int)applied["application"]["requested"]);
            Assert.Equal(unresolved, (int)applied["application"]["unresolved"]);
            Assert.Equal(2, (int)applied["application"]["verified"]);
            Assert.Equal(1, (int)applied["overrides_verified"]);
            var copy = Assert.IsType<View>(doc.Elements[(long)applied["view_id"]]);
            Assert.Empty(source.TestOverrides);
            Assert.Equal(ready.Id.Value, Assert.Single(copy.TestOverrides).Key);
            Assert.Equal(153, copy.GetElementOverrides(ready.Id).CutForegroundPatternColor.Green);
        }

        [Fact]
        public void Status_view_token_rejects_a_replacement_source_with_the_same_numeric_id_and_name()
        {
            var request = SetupStatusView(out var source, out _);
            var preview = Data(Call(request));
            var replacement = new View { Id = source.Id, Name = source.Name, TestUniqueId = "replacement-view", Document = doc };
            doc.Elements[source.Id.Value] = replacement;

            var refusal = Rejection(Apply((JObject)request.DeepClone(), preview));
            Assert.Equal("StalePlan", (string)refusal["confirmation_state"]);
            Assert.Equal(0, doc.TestTransactionStarts);
            Assert.Same(replacement, Assert.Single(doc.Elements.Values.OfType<View>()));

            var freshPreview = Data(Call(request));
            Assert.Equal(replacement.UniqueId, (string)freshPreview["source_view_unique_id"]);
            Assert.Equal("verified_applied", (string)Data(Apply(request, freshPreview))["application"]["state"]);
        }

        [Theory]
        [InlineData(TransactionStatus.RolledBack, "rolled_back", true)]
        [InlineData(TransactionStatus.Pending, "uncertain", false)]
        public void Status_view_reports_the_rollback_result_after_commit_throws(
            TransactionStatus rollbackStatus, string applicationState, bool confirmed)
        {
            var request = SetupStatusView(out var source, out var wall);
            var preview = Data(Call(request));
            doc.TestScheduleRollbackStatus = rollbackStatus;
            doc.TestScheduleBeforeCommit = tx =>
            {
                var copy = Assert.Single(doc.Elements.Values.OfType<View>(), v => v != source);
                Assert.Equal(wall.Id.Value, Assert.Single(copy.TestOverrides).Key);
                throw new InvalidOperationException("commit rejected by Revit");
            };

            CommandResult result = Apply(request, preview);
            JObject detail = Rejection(result);
            Assert.Contains("commit rejected by Revit", result.Error);
            Assert.Equal(rollbackStatus.ToString(), (string)detail["transaction_status"]);
            Assert.Equal(rollbackStatus.ToString(), (string)detail["rollback_status"]);
            Assert.Equal(rollbackStatus.ToString(), (string)detail["application"]["transaction_status"]);
            Assert.Equal(applicationState, (string)detail["application"]["state"]);
            Assert.True((bool)detail["write_started"]);
            Assert.True((bool)detail["rollback_attempted"]);
            Assert.Equal(confirmed, (bool)detail["rollback_confirmed"]);
            Assert.Equal(!confirmed, (bool)detail["model_may_have_changed"]);
            Assert.Equal(0, (int)detail["application"]["verified"]);
            Assert.Equal(confirmed ? 2 : 0, (int)detail["application"]["failed"]);
            Assert.Equal(confirmed ? 0 : 2, (int)detail["application"]["unknown"]);
            Assert.Equal(1, doc.TestRollbacks);
            Assert.Equal(confirmed ? 1 : 2, doc.Elements.Values.OfType<View>().Count());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Status_view_reports_a_pending_commit_without_claiming_a_rollback_attempt(bool commitThrows)
        {
            var request = SetupStatusView(out _, out _);
            var preview = Data(Call(request));
            if (commitThrows)
                doc.TestScheduleBeforeCommit = tx =>
                {
                    tx.Status = TransactionStatus.Pending;
                    throw new InvalidOperationException("commit raised while pending");
                };
            else
                doc.TestScheduleCommitStatus = TransactionStatus.Pending;

            CommandResult result = Apply(request, preview);
            JObject detail = Rejection(result);
            Assert.Equal("Pending", (string)detail["transaction_status"]);
            Assert.Equal("Pending", (string)detail["application"]["transaction_status"]);
            Assert.Equal("uncertain", (string)detail["application"]["state"]);
            Assert.True((bool)detail["write_started"]);
            Assert.False((bool)detail["rollback_attempted"]);
            Assert.Equal(PlanFailure.NotAttempted, (string)detail["rollback_status"]);
            Assert.False((bool)detail["rollback_confirmed"]);
            Assert.True(PlanFailure.ModelMayHaveChanged(detail));
            Assert.NotEqual(true, detail.Value<bool?>("changes_applied"));
            Assert.Equal(0, (int)detail["application"]["verified"]);
            Assert.Equal(0, (int)detail["application"]["failed"]);
            Assert.Equal(2, (int)detail["application"]["unknown"]);
            var post = Assert.IsType<JObject>(detail["postcondition"]);
            var exists = Assert.Single((JArray)post["properties"], row => (string)row["property"] == "view_exists");
            Assert.False((bool)post["all_measured"]);
            Assert.False((bool)exists["measured"]);
            Assert.Equal(JTokenType.Null, exists["found_in_committed_model"].Type);
            Assert.Equal(JTokenType.Null, exists["matches"].Type);
            Assert.Contains("Pending", (string)exists["error"]);
            Assert.Equal(0, doc.TestRollbacks);
            Assert.Contains("Pending", result.Error);
            Assert.DoesNotContain("Rollback: Pending", result.Error);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Status_view_reports_cleanup_failure_and_preserves_post_commit_evidence(bool readThrows)
        {
            var request = SetupStatusView(out var source, out var wall);
            doc.IsWorkshared = true;
            doc.TestCommittedTransactionDisposeError = new InvalidOperationException("schedule transaction cleanup failure");
            request["identity"] = new JObject { ["view_id"] = source.UniqueId };
            var preview = Data(Call(request));
            doc.TestAfterCommit = () =>
            {
                doc.IsModified = true;
                if (readThrows)
                    Assert.Single(doc.Elements.Values.OfType<View>(), v => v != source).TestScheduleOverrideReadError =
                        new InvalidOperationException("override read failure");
            };

            var result = Apply(request, preview);
            var detail = AssertCommittedCleanupFailure(result, readThrows, verified: 2);
            var copy = Assert.Single(doc.Elements.Values.OfType<View>(), v => v != source);
            Assert.Equal(copy.Id.Value, (long)detail["view_id"]);
            Assert.True((bool)detail["postcondition"]["all_verified"]);
            Assert.Equal(readThrows ? 0 : 1, (int)detail["overrides_verified"]);
            Assert.Empty(source.TestOverrides);
            Assert.Equal(0, doc.TestRollbacks);
            if (readThrows)
            {
                var error = Assert.Single((JArray)detail["override_read_errors"]);
                Assert.Equal(wall.UniqueId, (string)error["unique_id"]);
                Assert.Equal("override read failure", (string)error["error"]);
            }
            AssertSyncBlocked(request, result, copy, added: true);
        }

        private static JObject AssertCommittedCleanupFailure(CommandResult result, bool readThrows, int verified)
        {
            var detail = Rejection(result);
            Assert.Equal("Committed", (string)detail["transaction_status"]);
            Assert.Equal("Committed", (string)detail["application"]["transaction_status"]);
            Assert.True((bool)detail["changes_applied"]);
            Assert.True((bool)detail["write_started"]);
            Assert.Equal("schedule transaction cleanup failure", (string)detail["transaction_cleanup_error"]);
            Assert.Contains("schedule transaction cleanup failure", result.Error);
            Assert.Equal(readThrows ? "uncertain" : "verified_applied", (string)detail["application"]["state"]);
            Assert.Equal(verified - (readThrows ? 1 : 0), (int)detail["application"]["verified"]);
            Assert.Equal(0, (int)detail["application"]["failed"]);
            Assert.Equal(readThrows ? 1 : 0, (int)detail["application"]["unknown"]);
            return detail;
        }

        [Theory]
        [InlineData("view_lookup", "uncertain", 0, 0, 2)]
        [InlineData("override_lookup", "uncertain", 1, 0, 1)]
        [InlineData("override_read", "uncertain", 1, 0, 1)]
        [InlineData("cut_pattern", "partial", 1, 1, 0)]
        [InlineData("projection_line_color", "partial", 1, 1, 0)]
        public void Status_view_records_unverified_post_commit_results_in_the_sync_ledger(
            string fault, string state, int verified, int failed, int unknown)
        {
            var request = SetupStatusView(out var source, out var wall);
            doc.IsWorkshared = true;
            doc.IsModified = true;
            request["identity"] = new JObject { ["view_id"] = source.UniqueId };
            var preview = Data(Call(request));
            doc.TestAfterCommit = () =>
            {
                var copy = Assert.Single(doc.Elements.Values.OfType<View>(), v => v != source);
                var settings = copy.GetElementOverrides(wall.Id);
                switch (fault)
                {
                    case "view_lookup":
                        doc.Assimilated = true;
                        doc.ThrowAfterAssimilation = true;
                        break;
                    case "override_lookup":
                        doc.TestScheduleUniqueIdLookup = uid => throw new InvalidOperationException("override target lookup failure");
                        break;
                    case "override_read":
                        copy.TestScheduleOverrideReadError = new InvalidOperationException("override read failure");
                        break;
                    case "cut_pattern":
                        settings.SetCutForegroundPatternId(new ElementId(3));
                        break;
                    case "projection_line_color":
                        settings.SetProjectionLineColor(new Color(0, 0, 0));
                        break;
                }
            };

            var result = Apply(request, preview);
            var detail = Rejection(result);
            Assert.Equal("Committed", (string)detail["application"]["transaction_status"]);
            Assert.Equal(state, (string)detail["application"]["state"]);
            Assert.Equal(verified, (int)detail["application"]["verified"]);
            Assert.Equal(failed, (int)detail["application"]["failed"]);
            Assert.Equal(unknown, (int)detail["application"]["unknown"]);
            Assert.Equal(0, (int)detail["overrides_verified"]);
            Assert.Equal(wall.Id.Value, (long)Assert.Single((JArray)detail["overrides_not_verified"]));
            Assert.Empty((JArray)detail["overrides_refused_by_revit"]);
            if (fault == "override_lookup" || fault == "override_read")
            {
                var error = Assert.Single(Assert.IsType<JArray>(detail["override_read_errors"]));
                Assert.Equal(wall.Id.Value, (long)error["element_id"]);
                Assert.Equal(wall.UniqueId, (string)error["unique_id"]);
                Assert.Equal(fault == "override_lookup" ? "override target lookup failure" : "override read failure", (string)error["error"]);
            }
            else Assert.Null(detail["override_read_errors"]);
            if (fault == "view_lookup")
            {
                var existsCheck = Assert.Single((JArray)detail["postcondition"]["properties"], row => (string)row["property"] == "view_exists");
                Assert.False((bool)existsCheck["measured"]);
                Assert.Equal(JTokenType.Null, existsCheck["found_in_committed_model"].Type);
                Assert.Contains("post-commit read failure", (string)existsCheck["error"]);
            }

            var copy = Assert.Single(doc.Elements.Values.OfType<View>(), v => v != source);
            Assert.True((bool)detail["changes_applied"]);
            AssertSyncBlocked(request, result, copy, added: true);
        }

        private JObject SetupStatusView(out View source, out Element wall)
        {
            File.WriteAllText(path, Header + Walls);
            source = Add(new View { Id = new ElementId(1), TestUniqueId = "original-view", Name = "N1", Document = doc });
            Add(new FillPatternElement { Id = new ElementId(2) });
            wall = Wall(17, "17");
            var request = Request("status_view");
            request["view_id"] = source.Id.Value;
            request["as_of"] = "2027-04-16";
            return request;
        }

        private static JObject Rule(string id) => new JObject
            { ["activity"] = id, ["category"] = "OST_Walls", ["parameter"] = Key, ["value"] = id };

        private JObject Request(string operation) => new JObject
        {
            ["operation"] = operation, ["schedule_path"] = path, ["max_rows"] = 1,
            ["match"] = new JObject { ["parameter"] = Key },
            ["write"] = new JObject { ["activity_parameter"] = Output, ["allow_unmarked"] = true }
        };

        private Element Wall(long id, params string[] keys)
        {
            var element = Add(new Element { Id = new ElementId(id), TestCategory = BuiltInCategory.OST_Walls });
            for (int i = 0; i < keys.Length; i++)
                element.Parameters.Add(new Parameter { Name = Key, Id = new ElementId(i + 1), Text = keys[i] });
            element.Parameters.Add(new Parameter { Name = Output, Id = new ElementId(50), Text = "before" });
            return element;
        }

        private T Add<T>(T element) where T : Element
        {
            doc.Elements.Add(element.Id.Value, element);
            return element;
        }

        private CommandResult Call(JObject request) =>
            command.Execute(new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } }, request.ToString());

        private CommandResult Apply(JObject request, JObject preview)
        {
            request["dry_run"] = false;
            request["confirmation_token"] = (string)preview["confirmation_token"];
            request["idempotency_key"] = Guid.NewGuid().ToString("N");
            return Call(request);
        }

        private static JObject Data(CommandResult reply)
        {
            Assert.True(reply.Success, reply.Error);
            return Assert.IsType<JObject>(reply.Data);
        }

        private static JObject Rejection(CommandResult reply)
        {
            Assert.False(reply.Success);
            return Assert.IsType<JObject>(reply.Detail);
        }
    }
}
