// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// dialog_policy of horizun_export (4.2-c). Cancel is the default and the safe
// unattended answer; continue_known answers only dialogs in a CLOSED catalog;
// reload is never automatic; every decision is recorded and the cause leads the
// failure message.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ExportDialogPolicyTests
    {
        [Fact]
        public void Absent_is_cancel_and_a_typo_is_an_error_not_a_fallback()
        {
            Assert.Equal(ExportDialogMode.Cancel, ExportDialogPolicy.Parse(null, out string e1));
            Assert.Null(e1);
            Assert.Equal(ExportDialogMode.Cancel, ExportDialogPolicy.Parse("  ", out string e2));
            Assert.Null(e2);
            Assert.Equal(ExportDialogMode.ContinueKnown, ExportDialogPolicy.Parse(" CONTINUE_KNOWN ", out string e3));
            Assert.Null(e3);
            ExportDialogPolicy.Parse("dismiss", out string e4);
            Assert.Contains("dialog_policy must be", e4);
            ExportDialogPolicy.Parse("reload", out string e5);
            Assert.NotNull(e5);
        }

        [Fact]
        public void Default_cancels_even_a_known_dialog_and_says_how_to_continue()
        {
            ExportDialogDecision d = ExportDialogPolicy.Decide(ExportDialogMode.Cancel, "TaskDialog_Update_Resources", "out of date");
            Assert.Equal("cancel", d.Action);
            Assert.Equal(2, d.Result);
            Assert.True(d.InCatalog);
            Assert.Equal("update_resources", d.CatalogKey);
            Assert.Contains("continue_known", d.Reason);
        }

        [Fact]
        public void Continue_known_continues_a_catalog_dialog_without_reloading_and_flags_the_unverified_button()
        {
            ExportDialogDecision d = ExportDialogPolicy.Decide(ExportDialogMode.ContinueKnown, "TaskDialog_Update_Resources", "out of date");
            Assert.Equal("continue", d.Action);
            Assert.NotEqual(2, d.Result);
            Assert.True(d.InCatalog);
            Assert.False(d.ButtonVerified);
            Assert.Contains("WITHOUT reloading", d.Reason);
        }

        [Fact]
        public void Continue_known_still_cancels_what_is_not_in_the_catalog()
        {
            ExportDialogDecision d = ExportDialogPolicy.Decide(ExportDialogMode.ContinueKnown, "TaskDialog_Something_Else", "hmm");
            Assert.Equal("cancel", d.Action);
            Assert.False(d.InCatalog);
            Assert.Contains("not in the catalog", d.Reason);
        }

        [Fact]
        public void A_dialog_that_offers_a_reload_is_cancelled_whatever_the_policy()
        {
            foreach (ExportDialogMode m in new[] { ExportDialogMode.Cancel, ExportDialogMode.ContinueKnown })
            {
                ExportDialogDecision d = ExportDialogPolicy.Decide(m, "TaskDialog_Reload_Latest", "Reload latest?");
                Assert.Equal("cancel", d.Action);
                Assert.Contains("never automatic", d.Reason);
            }
            Assert.Equal("cancel", ExportDialogPolicy.Decide(ExportDialogMode.ContinueKnown, "TaskDialog_X", "Do you want to Reload?").Action);
        }

        [Fact]
        public void Known_id_cannot_override_a_reload_offer_or_match_a_different_id()
        {
            var reload = ExportDialogPolicy.Decide(ExportDialogMode.ContinueKnown, "TaskDialog_Update_Resources", "Reload resources?");
            Assert.Equal("cancel", reload.Action);
            Assert.Contains("never automatic", reload.Reason);
            var impostor = ExportDialogPolicy.Decide(ExportDialogMode.ContinueKnown, "TaskDialog_Update_Resources_Reload", "out of date");
            Assert.Equal("cancel", impostor.Action);
            Assert.Equal("cancel", ExportDialogPolicy.Decide(ExportDialogMode.ContinueKnown, "Other_Update_Resources_Dialog", "out of date").Action);
        }

        [Fact]
        public void No_catalog_answer_is_ever_a_cancel_value_or_named_reload()
        {
            foreach (JObject e in ExportDialogPolicy.CatalogJson())
            {
                Assert.DoesNotContain("reload", ((string)e["dialog_id_contains"]).ToLowerInvariant());
                ExportDialogDecision d = ExportDialogPolicy.Decide(ExportDialogMode.ContinueKnown, (string)e["dialog_id_contains"], null);
                Assert.Equal("continue", d.Action);
                Assert.NotEqual(ExportDialogPolicy.CancelResult, d.Result);
            }
        }

        [Fact]
        public void The_block_records_the_request_the_default_and_every_decision()
        {
            var decisions = new List<ExportDialogDecision>
            {
                ExportDialogPolicy.Decide(ExportDialogMode.ContinueKnown, "TaskDialog_Update_Resources", "m"),
                ExportDialogPolicy.Decide(ExportDialogMode.ContinueKnown, "Other", "m")
            };
            JObject b = ExportDialogPolicy.Block(ExportDialogMode.ContinueKnown, true, decisions);
            Assert.Equal("continue_known", (string)b["policy"]);
            Assert.True((bool)b["requested"]);
            Assert.Equal("cancel", (string)b["default"]);
            Assert.Equal(2, (int)b["dialogs_seen"]);
            Assert.Equal("continue", (string)b["decisions"][0]["action"]);
            Assert.Equal("cancel", (string)b["decisions"][1]["action"]);
            Assert.True((bool)b["reload_is_never_automatic"]);

            JObject quiet = ExportDialogPolicy.Block(ExportDialogMode.Cancel, false, null);
            Assert.Equal(0, (int)quiet["dialogs_seen"]);
            Assert.False((bool)quiet["requested"]);
        }

        [Fact]
        public void The_cause_leads_the_failure_message_and_a_quiet_run_keeps_it_unchanged()
        {
            Assert.Equal("PDF exporter rejected output x.pdf", ExportDialogPolicy.CauseFirst(null, "PDF exporter rejected output x.pdf"));
            var d = new List<ExportDialogDecision> { ExportDialogPolicy.Decide(ExportDialogMode.Cancel, "TaskDialog_Update_Resources", "m") };
            string msg = ExportDialogPolicy.CauseFirst(d, "PDF exporter rejected output x.pdf");
            Assert.StartsWith("Revit raised the dialog 'TaskDialog_Update_Resources'", msg);
            Assert.EndsWith("PDF exporter rejected output x.pdf", msg);
        }

        [Fact]
        public void The_export_command_reads_the_policy_scopes_it_to_the_export_and_reports_it()
        {
            string src = File.ReadAllText(FindRepoFile("src/Horizun.Revit/Commands/ExportCommand.cs"));
            Assert.Contains("ExportDialogPolicy.Parse(request.Value<string>(\"dialog_policy\")", src);
            Assert.Contains("Interference.WithExportDialogs(dialogMode)", src);
            Assert.Contains("\"dialog_policy\"", src);                 // in the plan hash list
            Assert.Contains("exportResult[\"dialog_policy\"]", src);   // in the reply
            Assert.Contains("ExportDialogPolicy.CauseFirst", src);     // cause leads the failure
        }

        private static string FindRepoFile(string rel)
        {
            string dir = Directory.GetCurrentDirectory();
            while (dir != null && !File.Exists(Path.Combine(dir, rel))) dir = Path.GetDirectoryName(dir);
            return Path.Combine(dir ?? "", rel);
        }
    }
}
