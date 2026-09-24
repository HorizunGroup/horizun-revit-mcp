// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The pure rules behind horizun_manage_phases and horizun_manage_assemblies_parts.
// -----------------------------------------------------------------------------
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;
using Horizun.Revit.Core;

namespace Horizun.Core.Tests
{
    public class PhaseRulesTests
    {
        [Theory]
        [InlineData(0, -1)]   // created in the first phase, never demolished
        [InlineData(1, 1)]    // created and demolished in the same phase: Revit's Temporary
        [InlineData(0, 2)]
        public void A_demolition_at_or_after_the_creation_is_in_order(int created, int demolished)
            => Assert.Null(PhaseRules.OrderError(created, demolished));

        [Fact]
        public void A_demolition_before_the_creation_is_refused_with_both_positions()
        {
            string e = PhaseRules.OrderError(2, 1);
            Assert.Contains("position 1", e);
            Assert.Contains("position 2", e);
        }

        [Fact]
        public void A_phase_that_is_not_in_the_document_is_refused()
        {
            Assert.NotNull(PhaseRules.OrderError(-1, -1));
            Assert.NotNull(PhaseRules.OrderError(0, -2));
        }

        [Fact]
        public void A_presentation_names_only_the_four_states_and_three_values()
        {
            Assert.Null(PhaseRules.PresentationError(null));
            Assert.Null(PhaseRules.PresentationError(JObject.Parse("{\"new\":\"overridden\",\"demolished\":\"hidden\"}")));
            Assert.Contains("future", PhaseRules.PresentationError(JObject.Parse("{\"future\":\"hidden\"}")));
            Assert.NotNull(PhaseRules.PresentationError(JObject.Parse("{\"new\":\"show\"}")));
            Assert.NotNull(PhaseRules.PresentationError(JObject.Parse("{\"new\":1}")));
            Assert.NotNull(PhaseRules.PresentationError(new JArray()));
        }

        [Fact]
        public void An_empty_presentation_is_a_mistake_not_a_no_op()
            => Assert.Contains("empty", PhaseRules.PresentationError(new JObject()));

        [Fact]
        public void Names_are_unique_without_case_and_a_rename_to_itself_changes_nothing()
        {
            var names = new[] { "Existing", "New Construction" };
            Assert.Null(PhaseRules.NameError("Phase 3", names, null));
            Assert.Contains("already used", PhaseRules.NameError("existing", names, null));
            Assert.Contains("nothing to change", PhaseRules.NameError("Existing", names, "Existing"));
            // Changing only the case of one's own name is a real rename.
            Assert.Null(PhaseRules.NameError("EXISTING", names, "Existing"));
            Assert.NotNull(PhaseRules.NameError(" padded", names, null));
            Assert.NotNull(PhaseRules.NameError("a:b", names, null));
            Assert.NotNull(PhaseRules.NameError("", names, null));
        }

        [Fact]
        public void Assembly_view_kinds_are_known_and_not_repeated()
        {
            Assert.Null(PhaseRules.ViewKindsError(new[] { "3d", "part_list" }));
            Assert.NotNull(PhaseRules.ViewKindsError(new string[0]));
            Assert.NotNull(PhaseRules.ViewKindsError(new[] { "3d", "3d" }));
            Assert.NotNull(PhaseRules.ViewKindsError(new[] { "isometric" }));
        }

        private static string Source(string file)
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", file));
        }

        [Fact]
        public void The_two_api_limits_are_typed_refusals_that_write_nothing()
        {
            string s = Source("ManagePhasesCommand.cs");
            Assert.Contains("\"no_phase_creation_api\"", s);
            Assert.Contains("\"no_design_option_assignment_api\"", s);
            // Both refusals are decided before any gate or transaction.
            Assert.True(s.IndexOf("no_design_option_assignment_api") < s.IndexOf("DocumentGate.ForMutation"));
            Assert.True(s.IndexOf("no_phase_creation_api") < s.IndexOf("DocumentGate.ForMutation"));
        }

        [Fact]
        public void The_staged_write_publishes_the_re_read_after_the_group_assimilates()
        {
            string s = Source("StagedGroupWrite.cs");
            int assimilate = s.IndexOf("group.Assimilate()");
            int committed = s.IndexOf("PostconditionCheck committed = w.Verify(doc)");
            Assert.True(assimilate > 0 && committed > assimilate);
            Assert.Contains("if (!reversible.AllVerified) throw", s);
            Assert.Contains("Guard.RollBack(group)", s);
        }
    }
}
