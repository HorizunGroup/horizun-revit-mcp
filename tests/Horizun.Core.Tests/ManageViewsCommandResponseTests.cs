using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ManageViewsCommandResponseTests
    {
        private static (Document Doc, UIApplication App) Scene()
        {
            var doc = new Document
            {
                Title = Guid.NewGuid().ToString(),
                TestInitialDetailLevel = ViewDetailLevel.Fine,
                TestConfirmationStore = new ConfirmationStore()
            };
            doc.Elements.Add(1, new ViewFamilyType { Id = new ElementId(1), Document = doc });
            doc.Elements.Add(2, new FamilySymbol { Id = new ElementId(2), Document = doc });
            return (doc, new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } });
        }

        [Theory]
        [InlineData("create_sheet", "title_block_type_id=none", "send title_block_type_id")]
        [InlineData("create_drafting", "detail_level=Revit default", "use set_view_properties with detail_level afterwards")]
        public void Command_reports_creation_assumptions_in_rehearsal_and_verified_apply(string operation, string what, string overrideHow)
        {
            var (doc, app) = Scene();
            var action = new JObject { ["operation"] = operation, ["name"] = "New documentation" };
            var cmd = new ManageViewsCommand();
            var request = new JObject { ["actions"] = new JArray(action) };

            var preview = cmd.Execute(app, request.ToString());
            AssertAssumption(preview, what, overrideHow);
            int expectedRehearsals = operation == "create_sheet" ? 1 : 0;
            Assert.Equal(expectedRehearsals, doc.TestTransactionStarts);
            Assert.Equal(expectedRehearsals, doc.TestRollbacks);
            Assert.Empty(doc.Elements.Values.OfType<View>());
            if (operation == "create_sheet")
            {
                var rehearsal = (JObject)((JObject)preview.Data)["api_rehearsal"];
                Assert.Equal("RolledBack", (string)rehearsal["transaction_status"]);
                Assert.True(rehearsal.Value<bool>("provisional_elements_absent"));
                Assert.True((bool)Assert.Single((JArray)rehearsal["actions"])["verified_in_transaction"]);
            }

            var applied = cmd.Execute(app, ApplyArgs(request, preview));
            JObject response = AssertAssumption(applied, what, overrideHow);
            Assert.Equal("Committed", (string)response["transaction_status"]);
            Assert.Equal(1, response.Value<int>("actions_verified"));
            JToken row = Assert.Single((JArray)response["rows"]);
            Assert.True((bool)row["verified"]);
            var created = doc.GetElement(new ElementId((long)row["element_id"]));
            Assert.Equal("New documentation", created.Name);
            if (operation == "create_sheet")
            {
                var sheet = Assert.IsType<ViewSheet>(created);
                Assert.DoesNotContain(doc.Elements.Values, e => e.TestOwnerView == sheet.Id.Value &&
                    e.TestCategory == BuiltInCategory.OST_TitleBlocks);
            }
            else
            {
                var view = Assert.IsType<ViewDrafting>(created);
                Assert.Equal(ViewDetailLevel.Fine, view.DetailLevel);
            }
        }

        [Fact]
        public void An_existing_view_name_refuses_the_production_batch_as_a_precondition()
        {
            var (doc, app) = Scene();
            doc.Elements.Add(3, new ViewDrafting { Id = new ElementId(3), Name = "Occupied" });
            var result = new ManageViewsCommand().Execute(app,
                "{actions:[{operation:'create_drafting',name:'occupied'}]}");

            Assert.False(result.Success);
            Assert.Equal("precondicion_no_cumplida", result.Detail.Value<string>("error_code"));
            Assert.Equal("view_name_conflict", result.Detail.Value<string>("code"));
            Assert.Null(result.Detail["confirmation_token"]);
            Assert.Equal(0, doc.TestTransactionStarts);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Sheet_with_omitted_title_block_detects_a_block_after_commit_even_if_hidden(bool hidden)
        {
            var (doc, app) = Scene();
            doc.TestAfterCommit = () => doc.AddTitleBlock(doc.Elements.Values.OfType<ViewSheet>().Single(), new ElementId(2), hidden);
            var result = Apply(app, "{actions:[{operation:'create_sheet'}]}");

            Assert.False(result.Success);
            Assert.Contains("post-commit verification", result.Error);
            Assert.Contains("SUPUESTO", result.Error);
            Assert.True(result.Detail.Value<bool>("has_assumptions"));
            Assert.Equal("actions[0].title_block_type_id=none", (string)Assert.Single((JArray)result.Detail["assumptions"])["what"]);
            Assert.Equal(1, doc.TestRollbacks);
            Assert.Single(doc.Elements.Values.OfType<ViewSheet>());
        }

        [Fact]
        public void Sheet_rehearsal_with_an_unexpected_title_block_rolls_back_and_withholds_the_token()
        {
            var (doc, app) = Scene();
            doc.TestSubstituteTitleBlockType = new ElementId(2);
            var result = new ManageViewsCommand().Execute(app, "{actions:[{operation:'create_sheet'}]}");

            Assert.False(result.Success);
            Assert.Contains("failed verification while the transaction was still reversible", result.Error);
            Assert.Contains("SUPUESTO", result.Error);
            Assert.True(result.Detail.Value<bool>("has_assumptions"));
            Assert.Null(result.Detail["confirmation_token"]);
            Assert.Equal(1, doc.TestTransactionStarts);
            Assert.Equal(1, doc.TestRollbacks);
            Assert.Empty(doc.Elements.Values.OfType<ViewSheet>());
            Assert.Empty(doc.Elements.Values.OfType<FamilyInstance>());
        }

        [Fact]
        public void Sheet_apply_rolls_back_if_a_title_block_appears_after_a_clean_rehearsal()
        {
            var (doc, app) = Scene();
            var cmd = new ManageViewsCommand();
            var request = JObject.Parse("{actions:[{operation:'create_sheet'}]}");
            var preview = cmd.Execute(app, request.ToString());

            doc.TestSubstituteTitleBlockType = new ElementId(2);
            var result = cmd.Execute(app, ApplyArgs(request, preview));

            Assert.False(result.Success);
            Assert.Contains("failed verification while the transaction was still reversible", result.Error);
            Assert.True(result.Detail.Value<bool>("has_assumptions"));
            Assert.Equal(2, doc.TestRollbacks);
            Assert.Empty(doc.Elements.Values.OfType<ViewSheet>());
            Assert.Empty(doc.Elements.Values.OfType<FamilyInstance>());
        }

        [Fact]
        public void Sheet_without_title_block_ignores_a_hidden_title_block_owned_by_another_sheet()
        {
            var (doc, app) = Scene();
            ViewSheet existing = ViewSheet.Create(doc, ElementId.InvalidElementId);
            doc.AddTitleBlock(existing, new ElementId(2), hidden: true);
            var result = Apply(app, "{actions:[{operation:'create_sheet'}]}");

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, doc.Elements.Values.OfType<ViewSheet>().Count());
            Assert.Equal(existing.Id.Value, Assert.Single(doc.Elements.Values.OfType<FamilyInstance>()).TestOwnerView);
        }

        [Fact]
        public void Sheet_title_block_read_error_after_commit_preserves_the_write_and_the_assumption()
        {
            var (doc, app) = Scene();
            doc.TestAfterCommit = () => doc.TestTitleBlockReadError = new InvalidOperationException("Title block census unavailable");
            var result = Apply(app, "{actions:[{operation:'create_sheet'}]}");

            Assert.False(result.Success);
            Assert.Contains("The transaction committed", result.Error);
            Assert.Contains("Title block census unavailable", result.Error);
            Assert.Contains("SUPUESTO", result.Error);
            Assert.Equal(ErrorCodes.VerificacionFallida, (string)result.Detail["error_code"]);
            Assert.Equal("Committed", (string)result.Detail["transaction_status"]);
            Assert.True(result.Detail.Value<bool>("changes_applied"));
            Assert.True(SyncLedgerRules.ShouldRecordResult(result.Success, result.Detail));
            Assert.Equal(0, result.Detail.Value<int>("actions_verified"));
            Assert.True(result.Detail.Value<bool>("has_assumptions"));
            Assert.Equal("actions[0].title_block_type_id=none", (string)Assert.Single((JArray)result.Detail["assumptions"])["what"]);
            JToken row = Assert.Single((JArray)result.Detail["rows"]);
            Assert.False((bool)row["verified"]);
            Assert.Equal("Title block census unavailable", (string)row["verification_error"]);
            Assert.Equal(Assert.Single(doc.Elements.Values.OfType<ViewSheet>()).Id.Value, (long)row["element_id"]);
            Assert.Equal(1, doc.TestRollbacks);
        }

        [Theory]
        [InlineData(1, true, false)]
        [InlineData(2, true, true)]
        [InlineData(2, false, true)]
        public void Mixed_batch_keeps_all_committed_rows_and_blocks_sync_on_read_errors(
            int failingCenterRead, bool sheetReadFails, bool viewportVerified)
        {
            var (doc, app) = Scene();
            doc.IsWorkshared = true;
            doc.Elements.Add(3, new ViewDrafting { Id = new ElementId(3), Document = doc });
            doc.TestAfterCommit = () =>
            {
                if (sheetReadFails) doc.TestTitleBlockReadError = new InvalidOperationException("Title block census unavailable");
                // Fail either the verification read or the later read that builds the response.
                doc.Elements.Values.OfType<Viewport>().Single().TestBoxCenterReadsUntilError = failingCenterRead;
            };
            var cmd = new ManageViewsCommand();
            var request = JObject.Parse(@"{actions:[
                {operation:'create_drafting',name:'Before the failed reads'},
                {operation:'create_sheet',key:'sheet'},
                {operation:'place_view',sheet_key:'sheet',view_id:3,point:[0,0]},
                {operation:'create_drafting',name:'After the failed reads'}
            ]}");
            string applyArgs = ApplyArgs(request, cmd.Execute(app, request.ToString()));
            var result = cmd.Execute(app, applyArgs);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.VerificacionFallida, (string)result.Detail["error_code"]);
            Assert.Equal("Committed", (string)result.Detail["transaction_status"]);
            Assert.True((bool)result.Detail["changes_applied"]);
            Assert.True((bool)result.Detail["write_started"]);
            Assert.True((bool)result.Detail["has_assumptions"]);
            Assert.Contains((JArray)result.Detail["assumptions"], a => (string)a["what"] == "actions[1].title_block_type_id=none");
            var rows = (JArray)result.Detail["rows"];
            long sheetId = Assert.Single(doc.Elements.Values.OfType<ViewSheet>()).Id.Value;
            long viewportId = Assert.Single(doc.Elements.Values.OfType<Viewport>()).Id.Value;
            long DraftingId(string name) => doc.Elements.Values.OfType<ViewDrafting>().Single(v => v.Name == name).Id.Value;
            Assert.Equal(4, rows.Count);
            AssertRow(0, "create_drafting", DraftingId("Before the failed reads"), true);
            AssertRow(1, "create_sheet", sheetId, !sheetReadFails, sheetReadFails ? "Title block census unavailable" : null);
            AssertRow(2, "place_view", viewportId, viewportVerified,
                viewportVerified ? null : "Viewport center unavailable");
            Assert.Equal(viewportVerified ? "Viewport center unavailable" : null, (string)rows[2]["detail_error"]);
            if (viewportVerified)
            {
                Assert.Equal(sheetId, (long)rows[2]["sheet_id"]);
                Assert.Equal(3, (long)rows[2]["view_id"]);
            }
            AssertRow(3, "create_drafting", DraftingId("After the failed reads"), true);
            Assert.Equal(2 + (sheetReadFails ? 0 : 1) + (viewportVerified ? 1 : 0), (int)result.Detail["actions_verified"]);
            Assert.Equal(1, doc.TestRollbacks);

            var changes = new ChangeWatch.DocChanges { Document = doc };
            changes.Added.UnionWith(doc.Elements.Keys.Where(id => id > 3));
            var watch = new ChangeWatch();
            watch.Documents.Add(changes);
            try
            {
                SyncReminder.AfterCommand(app, cmd.Name, applyArgs, watch, result);
                var pending = Assert.IsType<JObject>(result.Detail["pending_sync"]);
                Assert.Equal(1, (int)pending["blockers_failed_checks"]);
                var blockers = SyncLedgerRules.Blockers(doc.Title);
                var blocker = Assert.Single(blockers);
                Assert.Equal("failed_check", blocker.BlockKind);
                Assert.Equal(changes.Added.OrderBy(id => id), blocker.Ids.OrderBy(id => id));
                var acceptance = SyncLedgerRules.Accept(blockers, acceptFindings: false, acceptIds: null, strict: false);
                Assert.Equal(SyncLedgerRules.UnverifiedCode, acceptance.Code);
            }
            finally { SyncLedgerRules.MarkSynced(doc.Title, DateTime.UtcNow); }

            void AssertRow(int index, string operation, long id, bool verified, string error = null)
            {
                JToken row = rows[index];
                Assert.Equal(index, (int)row["index"]);
                Assert.Equal(operation, (string)row["operation"]);
                Assert.Equal(id, (long)row["element_id"]);
                Assert.True((bool)row["present_after_commit"]);
                Assert.Equal(verified, (bool)row["verified"]);
                Assert.Equal(error, (string)row["verification_error"]);
            }
        }

        [Fact]
        public void Transaction_cleanup_error_keeps_the_committed_and_verified_row()
        {
            var (doc, app) = Scene();
            doc.TestCommittedTransactionDisposeError = new InvalidOperationException("Transaction cleanup unavailable");
            var result = Apply(app, "{actions:[{operation:'create_sheet'}]}");

            Assert.False(result.Success);
            Assert.Equal("Committed", (string)result.Detail["transaction_status"]);
            Assert.True((bool)result.Detail["changes_applied"]);
            Assert.Equal("Transaction cleanup unavailable", (string)result.Detail["transaction_cleanup_error"]);
            Assert.Equal(1, (int)result.Detail["actions_verified"]);
            var row = Assert.Single((JArray)result.Detail["rows"]);
            Assert.True((bool)row["verified"]);
            Assert.Equal(Assert.Single(doc.Elements.Values.OfType<ViewSheet>()).Id.Value, (long)row["element_id"]);
        }

        [Fact]
        public void Mixed_rehearsal_reports_which_provisional_elements_were_checked_after_rollback()
        {
            var (doc, app) = Scene();
            var preview = new ManageViewsCommand().Execute(app, "{actions:[{operation:'create_sheet',title_block_type_id:2},{operation:'create_drafting'}]}");

            Assert.True(preview.Success, preview.Error);
            var rehearsal = ((JObject)preview.Data)["api_rehearsal"];
            Assert.True((bool)rehearsal["provisional_elements_absent"]);
            Assert.Equal("create_sheet_and_selection_filter_result_ids", (string)rehearsal["provisional_elements_absence_scope"]);
            Assert.Empty(doc.Elements.Values.OfType<View>());
            Assert.Empty(doc.Elements.Values.OfType<FamilyInstance>());
        }

        [Theory]
        [InlineData(2, true)]
        [InlineData(3, false)]
        public void Explicit_title_block_must_match_the_requested_type(long actualType, bool verified)
        {
            var (doc, app) = Scene();
            doc.TestSubstituteTitleBlockType = new ElementId(actualType);
            var cmd = new ManageViewsCommand();
            var request = JObject.Parse("{actions:[{operation:'create_sheet',title_block_type_id:2}]}");
            var preview = cmd.Execute(app, request.ToString());
            Assert.Equal(verified, preview.Success);
            var result = verified ? cmd.Execute(app, ApplyArgs(request, preview)) : preview;
            Assert.Equal(verified, result.Success);
            if (verified)
            {
                var response = Assert.IsType<JObject>(result.Data);
                Assert.Null(response["has_assumptions"]);
                Assert.Null(response["assumptions"]);
            }
            else Assert.Equal(1, doc.TestRollbacks);
        }

        private static JObject AssertAssumption(CommandResult result, string what, string overrideHow)
        {
            Assert.True(result.Success, result.Error);
            var response = Assert.IsType<JObject>(result.Data);
            Assert.True(response.Value<bool>("has_assumptions"));
            var row = Assert.IsType<JObject>(Assert.Single(Assert.IsType<JArray>(response["assumptions"])));
            Assert.Equal(new[] { "flag", "what", "why", "override" }, row.Properties().Select(p => p.Name));
            Assert.Equal("SUPUESTO", row.Value<string>("flag"));
            Assert.Equal("actions[0]." + what, row.Value<string>("what"));
            Assert.False(string.IsNullOrWhiteSpace(row.Value<string>("why")));
            Assert.Equal(overrideHow, row.Value<string>("override"));
            return response;
        }

        private static CommandResult Apply(UIApplication app, string json)
        {
            var cmd = new ManageViewsCommand();
            var request = JObject.Parse(json);
            return cmd.Execute(app, ApplyArgs(request, cmd.Execute(app, request.ToString())));
        }

        private static string ApplyArgs(JObject request, CommandResult preview)
        {
            Assert.True(preview.Success, preview.Error);
            var token = ((JObject)preview.Data).Value<string>("confirmation_token");
            Assert.False(string.IsNullOrWhiteSpace(token));
            return new JObject(request)
            {
                ["dry_run"] = false,
                ["confirmation_token"] = token,
                ["idempotency_key"] = Guid.NewGuid().ToString()
            }.ToString();
        }
    }
}
