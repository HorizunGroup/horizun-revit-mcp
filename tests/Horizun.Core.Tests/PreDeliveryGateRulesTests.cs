// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The pre-delivery gate. The case that matters most is the epistemic one: a
// lower bound can FAIL a limit and can never PASS one.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PreDeliveryGateRulesTests
    {
        private static Dictionary<string, GateMeasurement> Measured(
            string check, double count, bool ran = true, bool complete = true)
            => new Dictionary<string, GateMeasurement>
            {
                { check, new GateMeasurement { Check = check, Count = count, Ran = ran, CoverageComplete = complete } }
            };

        private static List<KeyValuePair<string, object>> Set(string name, object value)
            => new List<KeyValuePair<string, object>> { new KeyValuePair<string, object>(name, value) };

        [Fact]
        public void A_count_under_the_limit_with_complete_coverage_passes()
        {
            string error = PreDeliveryGateRules.Evaluate(Set("max_warnings", 50.0),
                Measured("warnings", 12), out List<GateRow> rows, out string verdict);
            Assert.Null(error);
            Assert.Equal(PreDeliveryGateRules.VerdictPass, verdict);
            Assert.Equal(PreDeliveryGateRules.StatusPass, rows[0].Status);
        }

        [Fact]
        public void A_count_over_the_limit_fails()
        {
            PreDeliveryGateRules.Evaluate(Set("max_warnings", 5.0),
                Measured("warnings", 12), out List<GateRow> rows, out string verdict);
            Assert.Equal(PreDeliveryGateRules.VerdictFail, verdict);
            Assert.Contains("12 measured against a limit of 5", rows[0].Reason);
        }

        [Fact]
        public void A_lower_bound_over_the_limit_fails_provably()
        {
            PreDeliveryGateRules.Evaluate(Set("max_warnings", 5.0),
                Measured("warnings", 12, complete: false), out List<GateRow> rows, out string verdict);
            Assert.Equal(PreDeliveryGateRules.VerdictFail, verdict);
            Assert.Contains("at least this", rows[0].Reason);
        }

        [Fact]
        public void A_lower_bound_under_the_limit_proves_nothing()
        {
            PreDeliveryGateRules.Evaluate(Set("max_warnings", 50.0),
                Measured("warnings", 12, complete: false), out List<GateRow> rows, out string verdict);
            Assert.Equal(PreDeliveryGateRules.VerdictNotAssessable, verdict);
            Assert.Equal(PreDeliveryGateRules.StatusNotMeasurable, rows[0].Status);
            Assert.Contains("LOWER BOUND", rows[0].Reason);
        }

        [Fact]
        public void A_check_that_never_ran_blocks_the_pass()
        {
            PreDeliveryGateRules.Evaluate(Set("max_warnings", 50.0),
                new Dictionary<string, GateMeasurement>(), out List<GateRow> rows, out string verdict);
            Assert.Equal(PreDeliveryGateRules.VerdictNotAssessable, verdict);
            Assert.Contains("never happened", rows[0].Reason);
        }

        [Fact]
        public void An_unknown_requirement_refuses_the_whole_gate()
        {
            string error = PreDeliveryGateRules.Evaluate(Set("max_warings", 5.0),
                Measured("warnings", 1), out _, out _);
            Assert.NotNull(error);
            Assert.Contains("max_warnings", error);          // the known list is in the refusal
            Assert.Contains("silently", error);
        }

        [Fact]
        public void Forbid_true_is_a_zero_limit_and_false_is_a_recorded_waiver()
        {
            PreDeliveryGateRules.Evaluate(Set("forbid_imported_cad", true),
                Measured("imported_cad", 1), out List<GateRow> rows, out string verdict);
            Assert.Equal(PreDeliveryGateRules.VerdictFail, verdict);

            PreDeliveryGateRules.Evaluate(Set("forbid_imported_cad", false),
                Measured("imported_cad", 7), out rows, out verdict);
            Assert.Equal(PreDeliveryGateRules.VerdictPass, verdict);
            Assert.Equal(PreDeliveryGateRules.StatusWaived, rows[0].Status);
            Assert.Contains("recorded", rows[0].Reason);
        }

        [Fact]
        public void One_failure_outranks_everything_else()
        {
            var set = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("max_warnings", 5.0),
                new KeyValuePair<string, object>("max_in_place_families", 100.0)
            };
            var measured = Measured("warnings", 12);
            measured["in_place_families"] = new GateMeasurement { Check = "in_place_families", Count = 1, Ran = true, CoverageComplete = false };
            PreDeliveryGateRules.Evaluate(set, measured, out _, out string verdict);
            Assert.Equal(PreDeliveryGateRules.VerdictFail, verdict);
        }

        [Fact]
        public void The_open_connector_requirement_reads_its_census()
        {
            PreDeliveryGateRules.Evaluate(Set("max_open_mep_connectors", 0.0),
                Measured("open_mep_connectors", 3), out List<GateRow> rows, out string verdict);
            Assert.Equal(PreDeliveryGateRules.VerdictFail, verdict);
            Assert.Equal("open_mep_connectors", rows[0].Check);
        }

        [Fact]
        public void The_link_and_template_requirements_read_their_checks()
        {
            PreDeliveryGateRules.Evaluate(Set("max_unpinned_links", 0.0),
                Measured("unpinned_links", 2), out List<GateRow> rows, out string verdict);
            Assert.Equal(PreDeliveryGateRules.VerdictFail, verdict);
            PreDeliveryGateRules.Evaluate(Set("max_views_without_template", 5.0),
                Measured("views_without_template", 3), out rows, out verdict);
            Assert.Equal(PreDeliveryGateRules.VerdictPass, verdict);
        }

        [Fact]
        public void An_invalid_limit_value_names_the_requirement()
        {
            string error = PreDeliveryGateRules.Evaluate(Set("max_warnings", "cinco"),
                Measured("warnings", 1), out _, out _);
            Assert.NotNull(error);
            Assert.Contains("max_warnings", error);
        }

        [Fact]
        public void An_empty_set_is_not_a_gate()
        {
            string error = PreDeliveryGateRules.Evaluate(new List<KeyValuePair<string, object>>(),
                Measured("warnings", 1), out _, out _);
            Assert.NotNull(error);
            Assert.Contains("at least one", error);
        }
    }
}
