using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReliabilityRound17Tests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(10)]
        [InlineData(42)]
        public void Graphics_copy_refuses_reused_ids_with_identical_overrides(long replaced)
        {
            var doc = new Document { Title = "Graphics identity", FixtureConfirmations = new ConfirmationStore() };
            var source = new View { Id = new ElementId(10), ElementTransparency = 17 };
            var target = new View { Id = new ElementId(11) };
            doc.Elements.Add(10, source);
            doc.Elements.Add(11, target);
            doc.Elements.Add(42, new Wall { Id = new ElementId(42), OwnerViewId = ElementId.InvalidElementId });
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var tool = new ViewVisibilityCommand();
            var request = JObject.Parse("{'operation':'copy_view_graphics','view_id':10,'view_ids':[11],'include':['elements'],'element_ids':[42]}");

            CommandResult preview = tool.Execute(app, request.ToString());
            Assert.True(preview.Success, preview.Error);
            request["confirmation_token"] = Assert.IsType<JObject>(preview.Data)["confirmation_token"];
            request["dry_run"] = false;
            request["idempotency_key"] = "r17-graphics";
            if (replaced == 10)
                doc.Elements[10] = new View { Id = source.Id, TestUniqueId = "replacement-view", ElementTransparency = 17 };
            if (replaced == 42)
                doc.Elements[42] = new Wall { Id = new ElementId(42), TestUniqueId = "replacement-wall", OwnerViewId = ElementId.InvalidElementId };

            CommandResult applied = tool.Execute(app, request.ToString());

            if (replaced == 0)
            {
                Assert.True(applied.Success, applied.Error);
                Assert.Equal(1, target.OverrideWrites);
                Assert.Equal(17, target.ElementTransparency);
            }
            else
            {
                Assert.False(applied.Success);
                Assert.Equal("StalePlan", applied.Detail.Value<string>("confirmation_state"));
                Assert.Equal(0, doc.TransactionStarts);
                Assert.Equal(0, target.OverrideWrites);
            }
        }

        [Theory]
        [InlineData("conflicts", false)]
        [InlineData("partial", false)]
        [InlineData("not_measured", false)]
        [InlineData("clean", true)]
        [InlineData("warnings", true)]
        public void Batch_saves_only_when_the_spatial_readback_passed(string status, bool shouldSave)
        {
            string fileName = "r17-" + Guid.NewGuid().ToString("N") + ".rvt";
            string path = OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, fileName) : @"D:\fixtures\" + fileName;
            string nextPath = Path.ChangeExtension(path, ".next.rvt");
            try
            {
                File.WriteAllText(path, "Batch spatial fixture");
                File.WriteAllText(nextPath, "Next model fixture");
                var app = new UIApplication();
                var models = new List<Document>();
                int saves = 0;
                Document OpenFixture()
                {
                    var doc = new Document { FixtureConfirmations = new ConfirmationStore(), TestSpatialStatus = status };
                    doc.TestBatchSave = () => { saves++; doc.IsModified = false; };
                    doc.Elements.Add(42, new Wall { Id = new ElementId(42) });
                    models.Add(doc);
                    return doc;
                }

                app.Application.ObserveBatchChanges = watch =>
                {
                    Document doc = app.ActiveUIDocument.Document;
                    var changed = new ChangeWatch.DocChanges { Document = doc };
                    changed.Modified.Add(42);
                    watch.Documents.Add(changed);
                };

                var mover = new BatchMove();
                var session = new DocumentSessionCommand(name => name == mover.Name ? mover : null, OpenFixture);
                JObject request = JObject.Parse(@"{
                    'operation':'batch','models':[
                        {'close':'save','actions':[
                            {'key':'move','tool':'horizun_transform_elements','arguments':{'operations':[
                                {'operation':'move','element_ids':[42],'vector':[1000,0,0]}]}}]},
                        {'close':'discard'}]}");
                request["models"][0]["file_path"] = path;
                request["models"][1]["file_path"] = nextPath;
                CommandResult dryRun = session.RunFixtureBatch(app, request);
                Assert.True(dryRun.Success, dryRun.Error);
                request["confirmation_token"] = Assert.IsType<JObject>(dryRun.Data)["confirmation_token"];
                request["dry_run"] = false;
                request["idempotency_key"] = "r17-batch-spatial";

                CommandResult applied = session.RunFixtureBatch(app, request);

                Assert.Equal(shouldSave, applied.Success);
                JObject data = shouldSave ? Assert.IsType<JObject>(applied.Data) : applied.Detail;
                var rows = Assert.IsType<JArray>(data["models"]);
                Assert.Equal(2, rows.Count);
                Assert.Equal(shouldSave, rows[0].Value<bool>("success"));
                Assert.Equal(status, rows[0]["steps_apply"]["data"]["spatial_check"].Value<string>("status"));
                Assert.Equal(!shouldSave, rows[0]["close"]["data"].Value<bool>("discarded_unsaved_changes"));
                if (!shouldSave)
                {
                    Assert.Equal("spatial_check_failed", rows[0].Value<string>("state"));
                    Assert.Null(rows[0]["save"]);
                }
                Assert.Equal(shouldSave ? 1 : 0, saves);
                Assert.Equal(shouldSave, data.Value<bool>("source_files_saved"));
                Assert.True(rows[1].Value<bool>("success"));
                Assert.All(models, model => Assert.False(model.IsValidObject));
                Assert.Empty(app.Application.Documents);
            }
            finally
            {
                File.Delete(path);
                File.Delete(nextPath);
            }
        }

        [Theory]
        [InlineData("disconnected", false)]
        [InlineData("other_end", false)]
        [InlineData("other_peer", false)]
        [InlineData("unchanged", true)]
        public void Set_curve_token_binds_join_peers_per_end_even_with_the_same_neighbor_census(string scenario, bool matches)
        {
            ResolvedPlan Resolve(string[] firstEnd, string[] secondEnd)
            {
                var wall = new PlannedElement
                {
                    ElementId = 101, UniqueId = "wall", Action = PlannedAction.Modify, GeometryFingerprint = "same wall bounds",
                    BeforeValues = new Dictionary<string, string> { ["released_end_joins"] = "True,True" }
                };
                TransformElementsCommand.BindEndJoinPeers(wall, new[] { firstEnd, secondEnd });
                return new ResolvedPlan
                {
                    Command = "horizun_transform_elements", DocumentKey = "fixture",
                    Elements = { wall,
                        new PlannedElement { ElementId = 102, UniqueId = "neighbor", GeometryFingerprint = "same neighbor bounds" },
                        new PlannedElement { ElementId = 103, UniqueId = "other", GeometryFingerprint = "same other bounds" } }
                };
            }

            ResolvedPlan before = Resolve(new[] { "neighbor" }, Array.Empty<string>());
            ResolvedPlan now = Resolve(
                scenario == "other_peer" ? new[] { "other" } : scenario == "unchanged" ? new[] { "neighbor" } : Array.Empty<string>(),
                scenario == "other_end" ? new[] { "neighbor" } : Array.Empty<string>());
            var store = new ConfirmationStore();
            string token = store.Issue(before.Command, before.DocumentKey, "same request", elementFingerprint: before.Fingerprint(), rehearsedPlan: before).Token;

            var checkedToken = store.Validate(token, before.Command, before.DocumentKey, "same request", now, null);

            Assert.Equal(matches, checkedToken.Ok);
            if (!matches) Assert.Equal(ConfirmationState.StalePlan, checkedToken.State);
            CommandResult refusal = now.RefuseChangedChild(before.Fingerprint(), "wall end joins");
            if (matches) Assert.Null(refusal);
            else
            {
                Assert.Equal("stale_plan", refusal.Detail.Value<string>("state"));
                Assert.False(refusal.Detail.Value<bool>("write_started"));
            }
        }

        private sealed class BatchMove : ICommand
        {
            public string Name => "horizun_transform_elements";
            public string Description => "Simulate a committed move before the real batch spatial check.";

            public CommandResult Execute(UIApplication app, string json)
            {
                var result = new JObject();
                if (JObject.Parse(json).Value<bool>("dry_run"))
                    ApplicationOutcome.StampRehearsal(result, 1, 0, 0, 0);
                else
                {
                    app.ActiveUIDocument.Document.IsModified = true;
                    ApplicationOutcome.Stamp(result, WriteTally.OneObject("Committed", true));
                }
                return CommandResult.Ok(result);
            }
        }
    }
}
