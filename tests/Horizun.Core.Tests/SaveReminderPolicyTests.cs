// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Revit's save reminder, answered between commands only while the bridge is in use.
//
// Both sides of the rule cost something real. Answering too little is the measured
// failure: the v3.0.5 release gate lost 52 requests in Revit 2023 and 2024 behind
// one unanswered reminder. Answering too much takes a person's reminder away while
// they work alone, or answers a dialog that was information, not a nag.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SaveReminderPolicyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData("TaskDialog_Save_Reminder")]
        [InlineData("SaveReminderDlg")]
        [InlineData("TaskDialog_Project_Not_Saved_Recently")]
        [InlineData("taskdialog_save_reminder")]
        public void The_save_reminder_is_recognised_by_its_id(string id)
        {
            Assert.True(SaveReminderPolicy.IsSaveReminder(id));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("TaskDialog_Save_Changes_To_Local_File")]
        [InlineData("TaskDialog_Close_Project_Without_Saving")]
        [InlineData("TaskDialog_Unresolved_References")]
        [InlineData("Dialog_Revit_DocWarnDialog")]
        public void Other_dialogs_are_not_the_save_reminder(string id)
        {
            // Saving to a local file or closing without saving are decisions about the
            // user's work. They are never answered by this rule.
            Assert.False(SaveReminderPolicy.IsSaveReminder(id));
        }

        [Fact]
        public void A_waiting_request_means_the_bridge_is_in_use()
        {
            Assert.True(SaveReminderPolicy.BridgeInUse(default(DateTime), Now, requestPending: true));
        }

        [Fact]
        public void Recent_activity_means_in_use_and_old_activity_does_not()
        {
            Assert.True(SaveReminderPolicy.BridgeInUse(Now - TimeSpan.FromMinutes(2), Now, false));
            Assert.True(SaveReminderPolicy.BridgeInUse(Now - SaveReminderPolicy.ActiveWindow, Now, false));
            Assert.False(SaveReminderPolicy.BridgeInUse(Now - SaveReminderPolicy.ActiveWindow - TimeSpan.FromSeconds(1), Now, false));
        }

        [Fact]
        public void A_bridge_never_used_does_not_answer_a_persons_reminder()
        {
            Assert.False(SaveReminderPolicy.ShouldAnswer("TaskDialog_Save_Reminder", default(DateTime), Now, false));
        }

        [Fact]
        public void The_reminder_is_answered_only_while_in_use()
        {
            Assert.True(SaveReminderPolicy.ShouldAnswer("TaskDialog_Save_Reminder", Now - TimeSpan.FromMinutes(1), Now, false));
            Assert.False(SaveReminderPolicy.ShouldAnswer("TaskDialog_Save_Reminder", Now - TimeSpan.FromHours(1), Now, false));
            Assert.False(SaveReminderPolicy.ShouldAnswer("TaskDialog_Save_Changes_To_Local_File", Now, Now, true));
        }

        [Fact]
        public void A_clock_that_went_backwards_is_not_activity()
        {
            Assert.False(SaveReminderPolicy.BridgeInUse(Now + TimeSpan.FromMinutes(5), Now, false));
        }

        [Fact]
        public void What_was_answered_is_drained_once_oldest_first()
        {
            SaveReminderPolicy.Drain();
            SaveReminderPolicy.Record(new Interruption { Kind = "dialog", Description = "first" });
            SaveReminderPolicy.Record(new Interruption { Kind = "dialog", Description = "second" });

            var drained = SaveReminderPolicy.Drain();

            Assert.Equal(new[] { "first", "second" }, drained.ConvertAll(i => i.Description));
            Assert.Empty(SaveReminderPolicy.Drain());
        }
    }
}
