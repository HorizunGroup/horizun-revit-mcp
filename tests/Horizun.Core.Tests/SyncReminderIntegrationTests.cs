using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Autodesk.Revit.UI
{
    public class UIDocument {
        public void RequestViewChange(Autodesk.Revit.DB.View view) => ActiveView = view; public Document Document { get; set; } public View ActiveView { get; set; } public View ActiveGraphicalView { get; set; } }
    public partial class UIApplication { public UIDocument ActiveUIDocument { get; set; } public TestApplication Application { get; set; } = new TestApplication(); }
    public partial class TestApplication
    {
        public string VersionNumber => "2026"; public string Username => "test"; public string LoginUserId => "test";
        // INT-11 A: the open documents, so a reply can be stamped with the pending_sync of the document a tool named.
        public System.Collections.Generic.List<Autodesk.Revit.DB.Document> Documents { get; } = new System.Collections.Generic.List<Autodesk.Revit.DB.Document>();
        internal event Action<string, ChangeWatch.DocChanges> Committed;
        internal void RaiseCommitted(string name, ChangeWatch.DocChanges changes) => Committed?.Invoke(name, changes);
        internal int CommitSubscribers => Committed?.GetInvocationList().Length ?? 0;
        internal Action ReleaseAfterCommand { get; set; }
        internal void StartNextCommand()
        {
            ReleaseAfterCommand?.Invoke();
            ReleaseAfterCommand = null;
        }
    }
}

namespace Horizun.Revit.Core
{
    internal sealed partial class ChangeWatch
    {
        private readonly TestApplication app;
        private readonly Document target;
        private readonly string transactionName;
        private readonly Action commitObserved;
        private bool attached;

        public ChangeWatch() { }
        public ChangeWatch(TestApplication app, Document target, string transactionName, Action commitObserved)
        {
            this.app = app;
            this.target = target;
            this.transactionName = transactionName;
            this.commitObserved = commitObserved;
            app.Committed += Capture;
            attached = true;
        }

        public bool ObservationComplete => true;
        public void ReleaseAtNextCommand() { if (attached) app.ReleaseAfterCommand = Dispose; }
        private void Capture(string name, DocChanges changes)
        {
            if (changes.Document != target || name != transactionName) return;
            Documents.Add(changes);
            Dispose();
            commitObserved();
        }

        public void Dispose()
        {
            DetachGroupObserver();
            if (!attached) return;
            app.Committed -= Capture;
            attached = false;
        }

        internal sealed class DocChanges
        {
            public Document Document;
            public HashSet<long> Added = new HashSet<long>();
            public HashSet<long> Modified = new HashSet<long>();
            public HashSet<long> Deleted = new HashSet<long>();
            public HashSet<long> Transient = new HashSet<long>();
            public HashSet<long> InternalTouches = new HashSet<long>();
        }
        public List<DocChanges> Documents = new List<DocChanges>();
        public void Settle() { }
        public static string Key(Document d) => d.Title;
    }
    internal static partial class ChangeLedger
    {
        internal sealed class Entry
        {
            public DateTime AtUtc = default;
            public string Tool = null;
            public long[] Added = null, Modified = null;
        }
        public static Entry For(Document d) => null;
        public static void Record(string tool, ChangeWatch.DocChanges change) { }
        public static IReadOnlyList<Entry> HistoryFor(Document d) => Array.Empty<Entry>();
    }
    internal static class Rid
    {
        public static bool CanRepresent(long value) => true;
        public static Autodesk.Revit.DB.ElementId Make(long value) => new Autodesk.Revit.DB.ElementId(value);
        public static long Value(Autodesk.Revit.DB.ElementId value) => value.Value;
        public static long GetId(Autodesk.Revit.DB.ElementId value) => Value(value);
        public static long? GetIdOrNull(Autodesk.Revit.DB.ElementId value) => value?.Value;
    }
}

namespace Horizun.Core.Tests
{
    public class SyncReminderIntegrationTests
    {
        [Fact]
        public void Partial_read_does_not_clear_pending_work_or_its_reminder()
        {
            string key = "bounded-read-" + Guid.NewGuid();
            var document = new Document { Title = key, IsWorkshared = true, IsModified = true };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = document } };
            var change = new ChangeWatch.DocChanges { Document = document };
            change.Added.Add(123);
            var watch = new ChangeWatch();
            watch.Documents.Add(change);
            SyncReminder.AfterCommand(app, "horizun_create_elements", "{}", watch,
                CommandResult.Ok(new JObject { ["spatial_check"] = new JObject { ["status"] = "partial" } }));
            var before = SyncReminder.ForHealth(document);
            SyncReminder.AfterCommand(app, "horizun_query_planimetry", "{}", new ChangeWatch(),
                CommandResult.Ok(new JObject { ["partial"] = true }));
            var after = SyncReminder.ForHealth(document);
            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.Equal((int)before["sync_blockers"], (int)after["sync_blockers"]);
            Assert.Equal(1, (int)after["sync_blockers"]);
            SyncLedgerRules.MarkSynced(key, DateTime.UtcNow);
        }
        [Theory]
        [InlineData("partial", false)]
        [InlineData("not_measured", false)]
        [InlineData("warnings", true)]
        public void Incomplete_spatial_result_survives_501_recorded_writes(string status, bool partial)
        {
            string key = "r2e-" + Guid.NewGuid();
            var document = new Document { Title = key, IsWorkshared = true, IsModified = true };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = document } };
            var change = new ChangeWatch.DocChanges { Document = document };
            change.Added.Add(123);
            var watch = new ChangeWatch();
            watch.Documents.Add(change);
            var first = CommandResult.Ok(new JObject { ["spatial_check"] = new JObject {
                ["status"] = status, ["partial"] = partial } });
            SyncReminder.AfterCommand(app, "horizun_create_elements", "{}", watch, first);
            Assert.Equal(1, (int)((JObject)first.Data)["pending_sync"]["sync_blockers"]);
            for (int i = 0; i < 501; i++)
                SyncReminder.AfterCommand(app, "horizun_create_elements", "{}", watch,
                    CommandResult.Ok(new JObject { ["spatial_check"] = new JObject { ["status"] = "clean" } }));
            var blockers = SyncLedgerRules.Blockers(key);
            Assert.Single(blockers);
            Assert.Equal("spatial_check_incomplete", blockers[0].BlockReason);
            Assert.Equal(1, (int)SyncReminder.ForHealth(document)["sync_blockers"]);
            SyncLedgerRules.MarkSynced(key, DateTime.UtcNow);
        }

        [Fact]
        public void Failed_postcommit_result_with_observed_change_blocks_sync()
        {
            const string key = "c3-postcommit-integration";
            SyncLedgerRules.ClearForTests();
            var document = new Document { Title = key, IsWorkshared = true, IsModified = true };
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = document } };
            var change = new ChangeWatch.DocChanges { Document = document };
            change.Added.Add(123);
            var watch = new ChangeWatch();
            watch.Documents.Add(change);
            var failed = CommandResult.FailWithDetail("Post-commit verification failed", new JObject { ["changes_applied"] = true });
            SyncReminder.AfterCommand(app, "horizun_create_elements", "{}", watch, failed);
            Assert.Single(SyncLedgerRules.Blockers(key));
            Assert.Equal("postcommit_verification_failed", SyncLedgerRules.Blockers(key)[0].BlockReason);
            Assert.Equal(1, (int)SyncLedgerRules.PendingBlock(key, true, true)["sync_blockers"]);
            Assert.Equal(0, SyncLedgerRules.ClearVerified(key, new long[] { 123 }, false));
            SyncLedgerRules.MarkSynced(key, DateTime.UtcNow);
            Assert.Empty(SyncLedgerRules.Blockers(key));
        }
    }
}
