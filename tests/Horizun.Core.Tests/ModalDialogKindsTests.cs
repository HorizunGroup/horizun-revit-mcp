using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // field cycle 2026-10-24: health said "ocupado" and cut its message before naming the dialog's
    // tool; "Project Not Saved Recently" held a Revit through a whole attempt. These pin what an
    // agent is told for each dialog the field actually raised.
    public class ModalDialogKindsTests
    {
        [Fact]
        public void External_tool_failure_names_a_third_party_add_in_and_says_it_is_not_horizun()
        {
            var v = ModalDialogKinds.Classify("External Tools - External Tool Failure",
                new[] { "External Tool Failure", "Failed to initialize the add-in \"Autodesk Insights\" because the add-in registration is missing a value." });
            Assert.Equal(ModalDialogKinds.ExternalToolFailure, v.Kind);
            Assert.Equal("Autodesk Insights", v.FailingTool);
            Assert.False(v.FailingToolIsHorizun);
            Assert.Contains("terceros", v.QueHacer);
        }

        [Fact]
        public void External_tool_failure_of_the_horizun_add_in_says_so()
        {
            var v = ModalDialogKinds.Classify("External Tools - External Tool Failure",
                new[] { @"Could not load the external application from D:\Addins\2023\Horizun\Horizun.Revit.dll" });
            Assert.Equal(ModalDialogKinds.ExternalToolFailure, v.Kind);
            Assert.EndsWith("Horizun.Revit.dll", v.FailingTool);
            Assert.True(v.FailingToolIsHorizun);
        }

        [Fact]
        public void External_tool_failure_without_a_name_does_not_invent_one()
        {
            var v = ModalDialogKinds.Classify("External Tools - External Tool Failure", new[] { "An error occurred." });
            Assert.Equal(ModalDialogKinds.ExternalToolFailure, v.Kind);
            Assert.Null(v.FailingTool);
            Assert.Null(v.FailingToolIsHorizun);
        }

        [Fact]
        public void Project_not_saved_recently_is_a_reminder_that_changes_nothing()
        {
            var v = ModalDialogKinds.Classify("Project Not Saved Recently",
                new[] { "This project has not been saved recently." }, new[] { "Save the project", "Do not save the project" });
            Assert.Equal(ModalDialogKinds.SaveReminder, v.Kind);
            Assert.Contains("Do not save the project", v.QueHacer);
        }

        [Fact]
        public void Security_unsigned_add_in_asks_for_always_load_after_verifying()
        {
            var v = ModalDialogKinds.Classify("Security - Unsigned Add-In", new[] { "Publisher unknown" }, new[] { "Always Load", "Load Once", "Do Not Load" });
            Assert.Equal(ModalDialogKinds.SecurityAddinLoad, v.Kind);
            Assert.Contains("Always Load", v.QueHacer);
        }

        [Fact]
        public void Anything_else_is_other_and_points_at_the_dialog_text()
        {
            var v = ModalDialogKinds.Classify("New Project", null);
            Assert.Equal(ModalDialogKinds.Other, v.Kind);
            Assert.Contains("modal_dialog", v.QueHacer);
        }

        [Fact]
        public void Health_carries_the_whole_dialog_and_its_kind_when_revit_is_on_one()
        {
            string longText = "Error: Revit has a MODAL DIALOG open: 'External Tools - External Tool Failure' [#32770]. 'horizun_health' was queued " +
                              new string('x', 600);
            var detail = new JObject
            {
                ["error_code"] = ErrorCodes.RevitOcupado,
                ["modal_dialog"] = new JObject
                {
                    ["dialog_window_found"] = true, ["title"] = "External Tools - External Tool Failure",
                    ["main_text"] = "Failed to initialize the add-in \"Autodesk Insights\".",
                    ["all_text"] = new JArray("Failed to initialize the add-in \"Autodesk Insights\"."),
                    ["buttons"] = new JArray("Close")
                }
            };
            JObject body = HealthState.Unavailable(longText, detail, true);
            Assert.Equal("ocupado", (string)body["estado"]);
            Assert.Equal("external_tool_failure", (string)body["dialog_kind"]);
            Assert.Equal("Autodesk Insights", (string)body["failing_tool"]);
            Assert.False((bool)body["failing_tool_is_horizun"]);
            Assert.Equal("Failed to initialize the add-in \"Autodesk Insights\".", (string)body["modal_dialog"]["main_text"]);
            Assert.Contains("Autodesk Insights", (string)body["que_hacer"]);
        }

        [Fact]
        public void Health_without_a_dialog_block_keeps_its_generic_advice()
        {
            JObject body = HealthState.Unavailable("No reply from Revit within 30000 ms.", null, true);
            Assert.Null(body["dialog_kind"]);
            Assert.Null(body["modal_dialog"]);
        }
    }
}
