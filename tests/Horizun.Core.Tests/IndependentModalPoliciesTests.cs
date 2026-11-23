using Horizun.Revit.Core;
using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IndependentModalPoliciesTests
    {
        [Fact]
        public void Posted_message_does_not_prove_dialog_closed_or_model_unchanged()
        {
            var decision = ModalAutoClose.Decide("Project Not Saved Recently", null, new[] { "Cancel" });
            JObject report = decision.ToJson("horizun_health", System.DateTime.UtcNow);
            Assert.Null(report["closed_utc"]);
            Assert.Null(report["model_changed"]);
            JObject verified = decision.ToJson("horizun_health", System.DateTime.UtcNow, true);
            Assert.True((bool)verified["closed_verified"]);
            Assert.NotNull(verified["closed_utc"]);
            Assert.Null(verified["model_changed"]);
        }
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Each_policy_controls_only_its_own_notice(bool external, bool reminder)
        {
            Assert.Equal(external, ModalAutoClose.Decide("External Tools - External Tool Failure", null,
                new[] { "Close" }, external, reminder) != null);
            Assert.Equal(reminder, ModalAutoClose.Decide("Project Not Saved Recently", null,
                new[] { "Cancel" }, external, reminder) != null);
            Assert.Null(ModalAutoClose.Decide("Security - Unsigned Add-In", null,
                new[] { "Always Load" }, external, reminder));
            Assert.Equal(reminder ? 2 : 0, ModalAutoClose.EventNoticeResult("TaskDialog_Reminder_To_Save", null, external, reminder));
            Assert.Equal(reminder ? 2 : 0, ModalAutoClose.EventNoticeResult("TaskDialog_Unknown", "This project has not been saved recently.", external, reminder));
            Assert.Equal(external ? 8 : 0, ModalAutoClose.EventNoticeResult("TaskDialog_External_Tool_Failure", null, external, reminder));
            Assert.Null(ModalAutoClose.EventNoticeResult("TaskDialog_Security", "Unsigned add-in", external, reminder));
            Assert.Null(ModalAutoClose.EventNoticeResult("TaskDialog_Save_File", "The project has not been saved. Save changes?", external, reminder));
        }

        [Fact]
        public void Command_watcher_enforces_notice_policy_before_generic_cancel_or_dismiss()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "Horizun.Revit"))) root = root.Parent;
            Assert.NotNull(root);
            string source = File.ReadAllText(Path.Combine(root.FullName, "src", "Horizun.Revit", "Core", "Interference.cs"));
            int notice = source.IndexOf("else if (noticeResult == 0)", StringComparison.Ordinal);
            int dismiss = source.IndexOf("else if (_openDialogPolicy == DialogAnswer.Dismiss)", StringComparison.Ordinal);
            Assert.True(notice > 0 && notice < dismiss);
            Assert.DoesNotContain("OverrideResult", source.Substring(notice, source.IndexOf("else if (noticeResult.HasValue)", notice, StringComparison.Ordinal) - notice));
            Assert.Contains("Settings.AutoCloseExternalToolFailure, Settings.AutoCloseSaveReminder", source);
        }

        [Theory]
        [InlineData(null, false, null, true)]
        [InlineData(true, false, "false", false)]
        [InlineData(true, false, "0", false)]
        [InlineData(false, false, "true", false)]
        [InlineData(null, true, null, false)]
        public void Save_reminder_owner_and_session_policy_matches_the_external_policy(bool? owner, bool malformed, string session, bool expected)
        {
            var config = new JObject { ["auto_close_external_tool_failure"] = true };
            if (owner.HasValue) config["auto_close_save_reminder"] = owner.Value;
            Assert.Equal(expected, Settings.SaveReminderAutoCloseFrom(config, malformed, session));
            Assert.Equal(!malformed, Settings.ExternalToolFailureAutoCloseFrom(config, malformed, null));
        }

        [Fact]
        public void Health_policy_names_each_control_and_its_effective_value()
        {
            JObject policy = Settings.ModalDialogPolicy();
            Assert.Equal("HORIZUN_AUTO_CLOSE_SAVE_REMINDER", (string)policy["save_reminder_session_environment"]);
            Assert.Equal("HORIZUN_AUTO_CLOSE_EXTERNAL_TOOL_FAILURE", (string)policy["session_environment"]);
            Assert.Equal(policy["auto_close_save_reminder"], policy["save_reminder_auto_close"]);
            Assert.Equal(JTokenType.Boolean, policy["auto_close_save_reminder"].Type);
            Assert.Equal(JTokenType.Boolean, policy["auto_close_external_tool_failure"].Type);
            Assert.Equal(JTokenType.Integer, policy["queued_busy_retry"]["max_attempts"].Type);
            Assert.Equal(250, (int)policy["queued_busy_retry"]["base_delay_ms"]);
            Assert.Contains("HORIZUN_BUSY_RETRY_BUDGET_MS", (string)policy["queued_busy_retry"]["configuration"]);
            Assert.Contains("never executes the command twice", (string)policy["queued_busy_retry"]["scope"]);
        }
    }
}
