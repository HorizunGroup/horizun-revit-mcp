using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Autodesk.Revit.DB
{
    public partial class Document
    {
        // INT-11 A: a linked document is never the target of a save.
        public bool IsLinked { get; set; }
    }
}

namespace Horizun.Core.Tests
{
    /// <summary>
    /// INT-11 (live, 2026-11-01): A - the reply of synchronizing L2 carried the pending_sync of the ACTIVE document (L3);
    /// D.5 - accept_findings=true also accepted failed checks. These tests compile against the source before the change and fail on it.
    /// </summary>
    public class Int11Tests
    {
        private static string RepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        private static Document Doc(string what, bool modified = true) =>
            new Document { Title = "int11-" + what + "-" + Guid.NewGuid().ToString("N"), IsWorkshared = true, IsModified = modified };

        private static void Writes(Document doc, int n)
        {
            for (int i = 0; i < n; i++)
                SyncLedgerRules.RecordWrite(doc.Title, new SyncLedgerEntry { Tool = "horizun_create_elements", AtUtc = DateTime.UtcNow, Ids = new long[] { 1000 + i } });
        }

        /// <summary>L2 behind (target), L3 in front (active), each with its own local work.</summary>
        private static (UIApplication app, Document target, Document active) TwoDocuments(int targetWrites, int activeWrites, bool targetModified = true)
        {
            Document target = Doc("L2", targetModified), active = Doc("L3");
            Writes(target, targetWrites);
            Writes(active, activeWrites);
            var app = new UIApplication { ActiveUIDocument = new UIDocument { Document = active } };
            app.Application.Documents.Add(target);
            app.Application.Documents.Add(active);
            return (app, target, active);
        }

        private static JObject Run(UIApplication app, string tool, JObject request)
        {
            var result = CommandResult.Ok(new JObject { ["operation"] = (string)request["operation"] ?? "save_local" });
            SyncReminder.AfterCommand(app, tool, request.ToString(), new ChangeWatch(), result);
            return (JObject)result.Data;
        }

        private static void Done(params Document[] docs)
        {
            foreach (Document d in docs) SyncLedgerRules.MarkSynced(d.Title, DateTime.UtcNow);
        }

        // ------------------------------------------------------------------ A

        [Theory]
        [InlineData("horizun_save_document", "sync_with_central")]
        [InlineData("horizun_save_document", "save_local")]
        [InlineData("horizun_save_document", "relinquish_own")]
        [InlineData("horizun_document_session", "sync_with_central")]
        public void The_pending_sync_of_a_reply_is_the_one_of_the_document_operated_on_and_the_active_one_goes_apart(string tool, string operation)
        {
            var (app, target, active) = TwoDocuments(targetWrites: 2, activeWrites: 5);
            try
            {
                JObject data = Run(app, tool, new JObject { ["operation"] = operation, ["target_document"] = target.Title });
                Assert.Equal(target.Title, (string)data["pending_sync"]["document"]);
                Assert.Equal(2, (int)data["pending_sync"]["writes_since_sync"]);
                Assert.Equal(active.Title, (string)data["active_document_pending_sync"]["document"]);
                Assert.Equal(5, (int)data["active_document_pending_sync"]["writes_since_sync"]);
            }
            finally { Done(target, active); }
        }

        [Fact]
        public void After_the_target_synchronized_its_reply_has_no_pending_sync_of_its_own_and_never_the_active_ones()
        {
            var (app, target, active) = TwoDocuments(targetWrites: 0, activeWrites: 3, targetModified: false);
            try
            {
                JObject data = Run(app, "horizun_save_document", new JObject { ["operation"] = "sync_with_central", ["target_document"] = target.Title });
                Assert.Null(data["pending_sync"]);
                Assert.Equal(active.Title, (string)data["active_document_pending_sync"]["document"]);
            }
            finally { Done(target, active); }
        }

        [Fact]
        public void The_expected_document_alias_and_a_full_path_name_the_target_too()
        {
            var (app, target, active) = TwoDocuments(targetWrites: 1, activeWrites: 4);
            target.PathName = @"C:\int11\" + target.Title + ".rvt";
            try
            {
                JObject byAlias = Run(app, "horizun_save_document", new JObject { ["operation"] = "sync_with_central", ["expected_document"] = target.Title });
                Assert.Equal(target.Title, (string)byAlias["pending_sync"]["document"]);
                JObject byPath = Run(app, "horizun_save_document", new JObject { ["operation"] = "save_local", ["target_document"] = target.PathName });
                Assert.Equal(target.Title, (string)byPath["pending_sync"]["document"]);
                Assert.Equal(1, (int)byPath["pending_sync"]["writes_since_sync"]);
            }
            finally { Done(target, active); }
        }

        [Fact]
        public void When_the_target_is_the_active_document_there_is_one_block_and_other_tools_keep_the_active_one()
        {
            var (app, target, active) = TwoDocuments(targetWrites: 2, activeWrites: 5);
            try
            {
                JObject same = Run(app, "horizun_save_document", new JObject { ["operation"] = "save_local", ["target_document"] = active.Title });
                Assert.Equal(active.Title, (string)same["pending_sync"]["document"]);
                Assert.Null(same["active_document_pending_sync"]);
                // A tool that does not operate on a named document keeps the active document's reminder, as before.
                JObject other = Run(app, "horizun_create_elements", new JObject { ["target_document"] = target.Title });
                Assert.Equal(active.Title, (string)other["pending_sync"]["document"]);
                Assert.Null(other["active_document_pending_sync"]);
                // document_session operations other than sync_with_central keep the active document's reminder too.
                JObject borrow = Run(app, "horizun_document_session", new JObject { ["operation"] = "borrow", ["target_document"] = target.Title });
                Assert.Equal(active.Title, (string)borrow["pending_sync"]["document"]);
            }
            finally { Done(target, active); }
        }

        [Fact]
        public void An_unresolved_target_never_borrows_the_active_documents_block()
        {
            var (app, target, active) = TwoDocuments(targetWrites: 2, activeWrites: 5);
            try
            {
                JObject data = Run(app, "horizun_save_document", new JObject { ["operation"] = "sync_with_central", ["target_document"] = "no such model" });
                Assert.Null(data["pending_sync"]);
                Assert.Equal(active.Title, (string)data["active_document_pending_sync"]["document"]);
            }
            finally { Done(target, active); }
        }

        // ------------------------------------------------------------------ D.5: contract and token

        [Fact]
        public void Accept_findings_ids_is_declared_by_save_document_only_as_an_array_of_integers()
        {
            JObject save = (JObject)Contract.Find("horizun_save_document").InputSchema["properties"];
            JObject ids = (JObject)save["accept_findings_ids"];
            Assert.NotNull(ids);
            Assert.Equal("array", (string)ids["type"]);
            Assert.Equal("integer", (string)ids["items"]["type"]);
            JObject session = (JObject)Contract.Find("horizun_document_session").InputSchema["properties"];
            Assert.Null(session["accept_findings_ids"]);
            Assert.Null(session["accept_findings"]);
        }

        [Fact]
        public void The_confirmation_token_of_a_sync_is_bound_to_the_accepted_ids()
        {
            Assert.Contains("accept_findings_ids", SyncWithCentralRules.RequestFields);
            JObject Estimate(params long[] ids) => new JObject
            {
                ["document"] = "d", ["comment"] = "c", ["relinquish"] = "all", ["compact"] = false, ["accept_findings_ids"] = new JArray(ids)
            };
            string a = ConfirmationStore.PlanHash(Estimate(1, 2), SyncWithCentralRules.RequestFields);
            Assert.Equal(a, ConfirmationStore.PlanHash(Estimate(1, 2), SyncWithCentralRules.RequestFields));
            Assert.NotEqual(a, ConfirmationStore.PlanHash(Estimate(1, 3), SyncWithCentralRules.RequestFields));
            Assert.NotEqual(a, ConfirmationStore.PlanHash(Estimate(1), SyncWithCentralRules.RequestFields));
            Assert.NotEqual(a, ConfirmationStore.PlanHash(Estimate(), SyncWithCentralRules.RequestFields));
            string sync = Source("src", "Horizun.Revit", "Commands", "DocumentSessionSync.cs");
            Assert.Contains("estimate[\"accept_findings_ids\"]", sync);
        }

        [Fact]
        public void The_save_operations_that_do_not_synchronize_refuse_accept_findings_ids()
        {
            string save = Source("src", "Horizun.Revit", "Commands", "SaveDocumentCommand.cs");
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(save, "\"accept_findings\", \"accept_findings_ids\"").Count);
        }

        [Fact]
        public void The_guidance_says_accept_findings_takes_only_scripts_and_failed_checks_need_their_ids()
        {
            foreach (string text in new[] { Source("AGENTS.md"), Source("src", "Horizun.Server", "ServerInstructions.cs") })
            {
                Assert.Contains("accept_findings_ids", text);
                Assert.Contains("failed_check", text);
            }
        }
    }
}
