using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using static Horizun.Core.Tests.ReliabilityRound13Tests;

namespace Horizun.Core.Tests
{
    public class ReliabilityRound14Tests
    {
        [Theory]
        [InlineData("unchanged")]
        [InlineData("expanded")]
        [InlineData("same_count_different_members")]
        public void Hide_binds_the_rehearsed_elements_before_the_child_can_write(string scenario)
        {
            var doc = new Document { Title = "Visibility plan", FixtureConfirmations = new ConfirmationStore() };
            var view = new View { Id = new ElementId(10) };
            var first = new TypedElement(101, 20, view);
            var second = new TypedElement(102, 21, view);
            doc.Elements.Add(10, view);
            doc.Elements.Add(20, new ElementType { Id = new ElementId(20), FamilyName = "Fixture", Name = "A" });
            doc.Elements.Add(21, new ElementType { Id = new ElementId(21), FamilyName = "Fixture", Name = "B" });
            doc.Elements.Add(101, first);
            doc.Elements.Add(102, second);
            doc.CaptureFixtureState = () =>
            {
                long firstType = first.TypeId;
                long secondType = second.TypeId;
                return () => { first.TypeId = firstType; second.TypeId = secondType; view.HiddenIds.Clear(); };
            };
            var tool = new ViewVisibilityCommand();
            var plan = PlanWith(tool, () =>
            {
                if (scenario != "unchanged") second.TypeId = 20;
                if (scenario == "same_count_different_members") first.TypeId = 21;
            });
            JObject request = Graph(tool, JObject.Parse("{'operation':'hide','view_id':10,'type_names':['A']}"));

            CommandResult applied = Apply(plan, doc, request, preview =>
                Assert.Equal(101, Assert.Single((JArray)preview["actions"][1]["data"]["plan"]["element_ids"]).Value<long>()));

            if (scenario == "unchanged")
            {
                Assert.True(applied.Success, applied.Error);
                Assert.Equal(1, view.HideWrites);
                Assert.Equal(101, Assert.Single(view.HiddenIds));
                return;
            }

            Assert.False(applied.Success);
            AssertChildRefusedAndGroupRolledBack(applied.Detail, "view visibility edit");
            Assert.Equal(0, view.HideWrites);
            Assert.Equal(0, doc.TransactionStarts);
            Assert.Empty(view.HiddenIds);
            Assert.Equal(20, first.TypeId);
            Assert.Equal(21, second.TypeId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Batch_save_as_discloses_a_source_without_a_readable_hash(bool readable)
        {
            var request = JObject.Parse(@"{
                'operation':'batch','models':[
                    {'file_path':'D:/source.rvt','close':'save','save_as_path':'D:/copy.rvt'}]}");
            var store = new ConfirmationStore();
            int saves = 0;
            CommandResult Run() => DocumentBatchRun.Run(request, "2026", store,
                path => readable ? new JObject { ["path"] = path, ["sha256"] = new string('A', 64) } :
                    new JObject { ["path"] = path, ["error"] = "IOException" },
                () => false, (_, row, dry, _, save) =>
                {
                    if (!dry) save(() => { saves++; return CommandResult.Ok(new JObject()); });
                    row["success"] = true;
                });

            CommandResult result = Run();
            Assert.True(result.Success, result.Error);
            var preview = Assert.IsType<JObject>(result.Data);
            request["confirmation_token"] = preview["confirmation_token"];
            request["idempotency_key"] = "r14-save-as";
            request["dry_run"] = false;
            result = Run();

            Assert.True(result.Success, result.Error);
            Assert.Equal(1, saves);
            var applied = Assert.IsType<JObject>(result.Data);
            foreach (var check in new[] { (Data: preview, Stage: "before opening"), (Data: applied, Stage: "before saving") })
            {
                var row = Assert.IsType<JObject>(Assert.Single((JArray)check.Data["models"]));
                Assert.Equal(new JValue(readable), row["source_verified"]);
                var error = row["source_verification_error"];
                if (readable) Assert.Null(error);
                else
                {
                    Assert.Equal(check.Stage, error.Value<string>("stage"));
                    Assert.Equal("IOException", error.Value<string>("approved"));
                    Assert.Equal("IOException", error.Value<string>("current"));
                }
            }
            Assert.False(applied.Value<bool>("source_files_saved"));
        }

        private sealed class TypedElement : Element
        {
            internal TypedElement(long id, long type, View view)
            {
                Id = new ElementId(id);
                TypeId = type;
                TestOwnerView = view.Id.Value;
            }
            internal long TypeId { get; set; }
            public override ElementId GetTypeId() => new ElementId(TypeId);
        }
    }

    [Collection("CooperativeRead global state")]
    public class ExitApplicationLateGuardTests
    {
        [Theory]
        [InlineData("clean", false, false)]
        [InlineData("already_dirty_and_approved", false, false)]
        [InlineData("next_document", true, false)]
        [InlineData("preauthorized_clean_document", true, false)]
        [InlineData("already_dirty_then_edited", true, false)]
        [InlineData("target_document", true, true)]
        [InlineData("edited_dirty_target", true, true)]
        [InlineData("different_document", true, false)]
        [InlineData("unknown_edited_document", true, true)]
        public void Exit_rechecks_each_close_and_preserves_edits_from_activation(string scenario, bool stops, bool firstStaysOpen)
        {
            var app = new UIApplication();
            var a = new Document { Title = "First", PathName = @"D:\first.rvt",
                IsModified = scenario == "different_document" || scenario == "edited_dirty_target" ||
                    scenario == "unknown_edited_document" };
            var b = new Document { Title = "Second", PathName = @"D:\second.rvt",
                IsModified = scenario.StartsWith("already_dirty", StringComparison.Ordinal) };
            app.Application.Documents.AddRange(new[] { a, b });
            app.ActiveUIDocument = new UIDocument { Document = a };
            bool originalReady = QueryCacheLifecycle.Ready;
            try
            {
                QueryCacheLifecycle.Ready = true;
                app.OnFixtureActivation = (closing, activated) =>
                {
                    if (closing != a || !stops) return;
                    Document edited = firstStaysOpen ? closing : activated;
                    edited.IsModified = true;
                    QueryCacheLifecycle.ObserveFixtureEdit(scenario == "unknown_edited_document" ? null : edited);
                };
                var request = new JObject { ["operation"] = "exit_application", ["discard_documents"] = new JArray() };
                CommandResult Run() => DocumentSessionCommand.RunOwnedExit(app, request, 91, "2026-11-19T12:00:00Z");
                var census = Assert.IsType<JObject>(Run().Data);
                var authorized = (JArray)request["discard_documents"];
                if (a.IsModified) authorized.Add(census["documents"][0]["fingerprint"]);
                if (b.IsModified || scenario == "preauthorized_clean_document")
                    authorized.Add(census["documents"][1]["fingerprint"]);
                var preview = Assert.IsType<JObject>(Run().Data);
                Assert.True(preview.Value<bool>("ready"));
                Assert.All(app.Application.Documents, d => Assert.True(d.IsValidObject));
                request["confirmation_token"] = preview["confirmation_token"];
                Assert.False(string.IsNullOrWhiteSpace(request.Value<string>("confirmation_token")));
                request["idempotency_key"] = "r14-exit";
                request["dry_run"] = false;

                CommandResult result = Run();

                Assert.Equal(!stops, result.Success);
                Assert.Equal(firstStaysOpen, a.IsValidObject);
                Assert.Equal(stops, b.IsValidObject);
                var evidence = result.Success ? Assert.IsType<JObject>(result.Data) : result.Detail;
                Assert.Equal(!stops, evidence.Value<bool>("exit_queued"));
                Assert.False(evidence.Value<bool>("termination_verified"));
                Assert.Equal(stops ? 0 : 1, app.PostedCommands);
                if (stops)
                {
                    Assert.True((firstStaysOpen ? a : b).IsModified);
                    Assert.True(evidence.Value<bool>("write_started"));
                    var blocked = ((JArray)evidence["close_results"]).Last["detail"];
                    Assert.Equal("stale_plan", blocked.Value<string>("state"));
                    Assert.True(blocked["current"].Value<bool>("modified"));
                    Assert.False(blocked.Value<bool>("close_started"));
                    Assert.False(blocked.Value<bool>("closed"));
                    Assert.Equal(firstStaysOpen, blocked.Value<bool>("write_started"));
                    Assert.Equal(scenario == "already_dirty_then_edited" || scenario == "edited_dirty_target",
                        blocked.Value<bool>("edits_since_apply_started"));
                    Assert.Equal(scenario == "unknown_edited_document", blocked.Value<bool>("edit_document_unknown"));
                    if (firstStaysOpen)
                    {
                        Assert.Same(b, app.ActiveUIDocument.Document);
                        Assert.Equal(b.Title, blocked["activation"].Value<string>("activated_title"));
                        Assert.Equal(b.PathName, blocked["activation"].Value<string>("activated_path"));
                    }
                }
                if (!firstStaysOpen)
                    Assert.Equal(a.IsModified, evidence["close_results"][0]["data"].Value<bool>("discard_requested"));
                if (!stops)
                    Assert.Equal(b.IsModified, evidence["close_results"][1]["data"].Value<bool>("discarded_unsaved_changes"));
            }
            finally
            {
                Dispatcher.ClearFixtureExitPending();
                QueryCacheLifecycle.Ready = originalReady;
            }
        }
    }
}
