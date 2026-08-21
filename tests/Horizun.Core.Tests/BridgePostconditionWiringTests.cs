// -----------------------------------------------------------------------------
// Revit-host wiring tests. These commands cannot be constructed without Revit,
// so the regression surface that review found is pinned at source level while the
// production assemblies are compiled against every supported Revit API in CI.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class BridgePostconditionWiringTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        private static string Source(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        [Fact]
        public void Open_commands_refuse_unproven_active_or_cloud_identity()
        {
            string open = Source("src/Horizun.Revit/Commands/OpenDocumentCommand.cs");
            string session = Source("src/Horizun.Revit/Commands/DocumentSessionCommand.cs");

            Assert.Contains("if (!activeConfirmed || !cloudIdentityConfirmed)", open);
            Assert.Contains("TryReadCloudIdentity(nowActive", open);
            Assert.Contains("if (!activeConfirmed || !cloudIdentityConfirmed)", session);
            Assert.Contains("TryReadCloudIdentity(nowActive", session);
            Assert.Contains("if (!isRequested || !returnedDocumentIsActive)", session);
            Assert.Contains("already_open_activated", session);
            Assert.Contains("OpenGuard.SameDocument(activeAfter, already)", session);
        }

        [Fact]
        public void Relinquish_verifies_element_checkouts_and_does_not_claim_true_on_unknown()
        {
            string src = Source("src/Horizun.Revit/Commands/RelinquishAllCommand.cs");

            Assert.Contains("GetRelinquishedElements()", src);
            Assert.Contains("GetRelinquishedWorksets()", src);
            Assert.Contains("WorksharingUtils.GetCheckoutStatus(doc, id)", src);
            Assert.Contains("CheckoutStatus.OwnedByCurrentUser", src);
            Assert.Contains("relinquished = complete == true", src);
            Assert.Contains("fully_relinquished = complete", src);
            Assert.DoesNotContain("relinquished = true", src);
        }

        [Fact]
        public void Dialog_and_failure_observation_are_subscribed_and_reported_independently()
        {
            string src = Source("src/Horizun.Revit/Core/Interference.cs");
            string python = Source("src/Horizun.Revit/Commands/ExecutePythonCommand.cs");

            Assert.Contains("SetSubscribed(\"dialogs\", true)", src);
            Assert.Contains("SetSubscribed(\"failures\", true)", src);
            Assert.Contains("dialogs_observed = DialogsObserved", src);
            Assert.Contains("failures_observed = FailuresObserved", src);
            Assert.Contains("if (_seen.Count == 0 && FullyObserved) return null", src);
            Assert.DoesNotContain("if (_seen.Count == 0) return null", src);
            Assert.Contains("dialogs_observed = raised[\"dialogs_observed\"]", python);
            Assert.Contains("failures_observed = raised[\"failures_observed\"]", python);
            Assert.Contains("observation_complete = raised[\"observation_complete\"]", python);
            Assert.Contains("if (w == null || !w.FullyObserved) return null", python);
            Assert.Contains("Kind = \"observer_error\"", src);
            Assert.Contains("MarkProcessingFailure(\"failures\"", src);
        }

        [Fact]
        public void Python_permission_ui_follows_the_revit_language_with_english_fallback()
        {
            string ribbon = Source("src/Horizun.Revit/Ribbon.cs");
            string request = Source("src/Horizun.Revit/Commands/RequestPythonAccessCommand.cs");

            Assert.Contains("ControlledApplication.Language", ribbon);
            Assert.Contains("data.Application.Application.Language", ribbon);
            Assert.Contains("app.Application.Language", request);
            Assert.Contains("Horizun — Python permission", ribbon);
            Assert.Contains("Enable Python until I disable it", ribbon);
            Assert.Contains("Activar Python hasta que yo lo desactive", ribbon);
            Assert.Contains("return value.IndexOf(\"Spanish\"", ribbon);
        }

        [Fact]
        public void Create_schedule_has_guarded_commit_and_only_then_sets_committed()
        {
            string src = Source("src/Horizun.Revit/Commands/CreateScheduleCommand.cs");

            int guard = src.IndexOf("commitStatus = Guard.Commit(tx, \"create schedule\")", StringComparison.Ordinal);
            int terminal = src.IndexOf("if (commitStatus != TransactionStatus.Committed)", guard,
                                       StringComparison.Ordinal);
            Assert.True(guard >= 0, "create_schedule does not call Guard.Commit");
            Assert.True(terminal > guard, "commit status is not decided after Guard.Commit returns");
            Assert.DoesNotContain("commitStatus = tx.Commit()", src);
        }
    }
}
