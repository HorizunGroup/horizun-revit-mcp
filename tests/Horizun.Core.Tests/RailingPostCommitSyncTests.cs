using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RailingPostCommitSyncTests
    {
        [Theory]
        [InlineData("partial")]
        [InlineData("not_measured")]
        public void Spatial_outcome_from_real_post_write_stage_blocks_after_501_real_commands(string spatialStatus) => RailingSyncScenario.ExerciseSpatial(spatialStatus, null);

        [Fact]
        public void Real_railing_command_records_a_post_assimilation_read_exception_as_a_sync_blocker() => RailingSyncScenario.ExercisePostCommit(null);
    }

    public static class RailingSyncScenario
    {
        public static void ExerciseSpatial(string spatialStatus, Action<Document> assertSyncRefused)
        {
            Assert.True(SpatialAfterWrite.Enabled);
            var doc = new Document { Title = Guid.NewGuid().ToString(), IsWorkshared = true, IsModified = true };
            doc.Elements[1] = new RailingType { Id = new ElementId(1) };
            doc.Elements[2] = new Level { Id = new ElementId(2) };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new CreateRailingCommand();
            const string request = "{\"dry_run\":false,\"units\":\"feet\",\"type_id\":1,\"level_id\":2,\"path\":[[0,0,0],[2,0,0]]}";
            for (int i = 0; i <= 501; i++)
            {
                doc.Assimilated = false;
                doc.TestSpatialStatus = i == 0 ? spatialStatus : "clean";
                var result = command.Execute(app, request);
                Assert.True(result.Success, result.Error);
                Assert.True(doc.Assimilated);
                var change = new ChangeWatch.DocChanges { Document = doc };
                change.Added.Add(doc.LatestCreatedId);
                var watch = new ChangeWatch(); watch.Documents.Add(change);
                SpatialAfterWrite.Attach(command.Name, watch, result);
                Assert.Equal(doc.TestSpatialStatus, (string)((Newtonsoft.Json.Linq.JObject)result.Data)["spatial_check"]["status"]);
                SyncReminder.AfterCommand(app, command.Name, request, watch, result);
                if (i == 0 || i == 501)
                {
                    assertSyncRefused?.Invoke(doc);
                    var blocker = Assert.Single(SyncLedgerRules.Blockers(doc.Title));
                    Assert.Equal("spatial_check_incomplete", blocker.BlockReason);
                    Assert.Equal(1, (int)SyncReminder.ForHealth(doc)["sync_blockers"]);
                }
            }
            Assert.Equal(502, doc.Elements.Count - 2);
            SyncLedgerRules.MarkSynced(doc.Title, DateTime.UtcNow);
        }

        public static void ExercisePostCommit(Action<Document> assertSyncRefused)
        {
            var doc = new Document { Title = Guid.NewGuid().ToString(), IsWorkshared = true, IsModified = true, ThrowAfterAssimilation = true };
            doc.Elements[1] = new RailingType { Id = new ElementId(1) };
            doc.Elements[2] = new Level { Id = new ElementId(2) };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = doc } };
            var command = new CreateRailingCommand();
            var result = command.Execute(app, "{\"dry_run\":false,\"units\":\"feet\",\"type_id\":1,\"level_id\":2,\"path\":[[0,0,0],[2,0,0]]}");
            Assert.True(doc.Assimilated);
            Assert.True(doc.Elements.ContainsKey(100));
            Assert.False(result.Success);
            Assert.True((bool)result.Detail["changes_applied"]);
            Assert.Equal("postcommit_verification_failed", (string)result.Detail["code"]);
            Assert.Contains("post-commit read failure", (string)result.Detail["verification_error"]);
            var change = new ChangeWatch.DocChanges { Document = doc };
            change.Added.Add(100);
            var watch = new ChangeWatch(); watch.Documents.Add(change);
            SyncReminder.AfterCommand(app, command.Name, "{}", watch, result);
            assertSyncRefused?.Invoke(doc);
            var blocker = Assert.Single(SyncLedgerRules.Blockers(doc.Title));
            Assert.Equal("postcommit_verification_failed", blocker.BlockReason);
            Assert.Equal(1, (int)SyncReminder.ForHealth(doc)["sync_blockers"]);
            SyncLedgerRules.MarkSynced(doc.Title, DateTime.UtcNow);
        }
    }
}
