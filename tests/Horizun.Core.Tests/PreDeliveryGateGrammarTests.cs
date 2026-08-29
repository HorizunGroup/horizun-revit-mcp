using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// THE GATE COULD SAY ONE THING PER FINDING, AND SIX STORIES NEEDED THREE.
    ///
    /// PreDeliveryGateRules had exactly two requirement shapes: a non-negative
    /// number for max_, and a bool for forbid_. Every accepted key produced one
    /// row whose measurement was a SINGLE count from ONE finding. That left three
    /// things unsayable:
    ///
    ///   * a LIST requirement - "these five things must be present" is not a limit
    ///     on a number, it is five questions, and a reader needs to know WHICH one
    ///     failed;
    ///   * TWO requirements reading DIFFERENT counts out of the SAME finding -
    ///     "how many levels share a name" and "how many sit on top of each other"
    ///     are one area and two numbers;
    ///   * a TOLERANCE, which configures a check rather than asserting on it, and
    ///     which as a requirement key refused the whole call.
    ///
    /// The hard part was not adding the three. It was adding them without moving a
    /// single existing row, which is what the first test is for.
    /// </summary>
    public class PreDeliveryGateGrammarTests
    {
        private static GateMeasurement M(double? count, bool complete = true, bool ran = true)
        {
            return new GateMeasurement { Count = count, Ran = ran, CoverageComplete = complete };
        }

        private static KeyValuePair<string, object> R(string k, object v)
        {
            return new KeyValuePair<string, object>(k, v);
        }

        // ------------------------------------------------- nothing old moved

        [Fact]
        public void Every_shape_that_worked_before_still_produces_the_same_row()
        {
            var measurements = new Dictionary<string, GateMeasurement>
            {
                { AuditCheckNames.Warnings, M(3) },
                { AuditCheckNames.ImportedCad, M(0) },
            };
            List<GateRow> rows; string verdict;
            string refusal = PreDeliveryGateRules.Evaluate(
                new[] { R("max_warnings", 5), R("forbid_imported_cad", true), R("forbid_room_problems", false) },
                measurements, out rows, out verdict);

            Assert.Null(refusal);
            Assert.Equal(3, rows.Count);
            Assert.Equal("pass", rows[0].Status);
            Assert.Equal("pass", rows[1].Status);
            Assert.Equal("waived", rows[2].Status);
            Assert.All(rows, r => Assert.Null(r.Item));
        }

        [Fact]
        public void An_unknown_requirement_still_refuses_the_whole_gate()
        {
            List<GateRow> rows; string verdict;
            string refusal = PreDeliveryGateRules.Evaluate(
                new[] { R("max_warnings", 5), R("max_nonsense", 1) },
                new Dictionary<string, GateMeasurement> { { AuditCheckNames.Warnings, M(0) } },
                out rows, out verdict);

            Assert.NotNull(refusal);
            Assert.Contains("max_nonsense", refusal);
            Assert.Empty(rows);
        }

        // ---------------------------------------- two counts from one finding

        [Fact]
        public void Two_requirements_can_read_different_parts_of_one_finding()
        {
            var datums = M(null);
            datums.Parts = new Dictionary<string, GateMeasurement>
            {
                { DatumCheckParts.DuplicateLevelNames, M(0) },
                { DatumCheckParts.CoincidentLevels, M(2) },
            };
            List<GateRow> rows; string verdict;
            string refusal = PreDeliveryGateRules.Evaluate(
                new[] { R("max_duplicate_level_names", 0), R("max_coincident_levels", 0) },
                new Dictionary<string, GateMeasurement> { { AuditCheckNames.Datums, datums } },
                out rows, out verdict);

            Assert.Null(refusal);
            Assert.Equal(2, rows.Count);
            Assert.Equal("pass", rows[0].Status);
            Assert.Equal(0, rows[0].Measured.Value);
            Assert.Equal("fail", rows[1].Status);
            Assert.Equal(2, rows[1].Measured.Value);
            Assert.Equal("fail", verdict);
        }

        [Fact]
        public void A_part_that_does_not_exist_is_not_measurable_and_never_a_pass()
        {
            var datums = M(null);
            datums.Parts = new Dictionary<string, GateMeasurement>();
            List<GateRow> rows; string verdict;
            PreDeliveryGateRules.Evaluate(
                new[] { R("max_coincident_levels", 0) },
                new Dictionary<string, GateMeasurement> { { AuditCheckNames.Datums, datums } },
                out rows, out verdict);

            Assert.Equal("not_measurable", Assert.Single(rows).Status);
            Assert.Equal("not_assessable", verdict);
        }

        [Fact]
        public void The_lower_bound_rule_applies_to_a_part_exactly_as_to_a_finding()
        {
            var datums = M(null);
            datums.Parts = new Dictionary<string, GateMeasurement>
            {
                // Under the limit, but the check could not read everything - so being
                // under the limit proves nothing.
                { DatumCheckParts.CoincidentLevels, M(1, complete: false) },
                // Over the limit even as a lower bound: provably failed.
                { DatumCheckParts.GridsOffAxis, M(9, complete: false) },
            };
            List<GateRow> rows; string verdict;
            PreDeliveryGateRules.Evaluate(
                new[] { R("max_coincident_levels", 5), R("max_grids_off_axis", 5) },
                new Dictionary<string, GateMeasurement> { { AuditCheckNames.Datums, datums } },
                out rows, out verdict);

            Assert.Equal("not_measurable", rows[0].Status);
            Assert.Contains("LOWER BOUND", rows[0].Reason);
            Assert.Equal("fail", rows[1].Status);
            Assert.Contains("at least this", rows[1].Reason);
        }

        // ------------------------------------------------- list requirements

        [Fact]
        public void A_list_requirement_produces_one_row_per_item_and_names_the_one_that_failed()
        {
            var coords = M(null);
            coords.Parts = new Dictionary<string, GateMeasurement>
            {
                { CoordinateCheckParts.ControlPoints, new GateMeasurement
                    {
                        Ran = true, CoverageComplete = true,
                        Items = CoordinateRules.ReadabilityItems(new CoordinateFacts
                        {
                            InternalOrigin = new PointFact { Readable = true },
                            ProjectBasePoint = new PointFact { Readable = true },
                            SurveyPoint = new PointFact { Readable = false, Why = "no survey point" },
                        })
                    } },
            };
            List<GateRow> rows; string verdict;
            string refusal = PreDeliveryGateRules.Evaluate(
                new[] { R("require_coordinate_facts",
                          new List<string> { "internal_origin", "project_base_point", "survey_point" }) },
                new Dictionary<string, GateMeasurement> { { AuditCheckNames.Coordinates, coords } },
                out rows, out verdict);

            Assert.Null(refusal);
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "internal_origin", "project_base_point", "survey_point" },
                         rows.Select(r => r.Item).ToArray());
            Assert.Equal("pass", rows[0].Status);
            Assert.Equal("fail", rows[2].Status);
            Assert.Contains("survey_point", rows[2].Reason);
            Assert.Equal("fail", verdict);
        }

        [Fact]
        public void An_item_nobody_measured_is_not_measurable_and_says_which_item()
        {
            var coords = M(null);
            coords.Parts = new Dictionary<string, GateMeasurement>
            {
                { CoordinateCheckParts.ControlPoints, new GateMeasurement
                    {
                        Ran = true, CoverageComplete = true,
                        Items = CoordinateRules.ReadabilityItems(new CoordinateFacts())
                    } },
            };
            List<GateRow> rows; string verdict;
            PreDeliveryGateRules.Evaluate(
                new[] { R("require_coordinate_facts", new List<string> { "true_north", "not_a_thing" }) },
                new Dictionary<string, GateMeasurement> { { AuditCheckNames.Coordinates, coords } },
                out rows, out verdict);

            Assert.All(rows, r => Assert.Equal("not_measurable", r.Status));
            Assert.Contains("true_north", rows[0].Reason);
            Assert.Contains("not_a_thing", rows[1].Reason);
            Assert.Equal("not_assessable", verdict);
        }

        [Fact]
        public void A_bare_string_is_refused_because_one_name_is_not_a_list()
        {
            List<GateRow> rows; string verdict;
            string refusal = PreDeliveryGateRules.Evaluate(
                new[] { R("require_coordinate_facts", "survey_point") },
                new Dictionary<string, GateMeasurement>(), out rows, out verdict);

            Assert.NotNull(refusal);
            Assert.Contains("A single string is not a list", refusal);
            Assert.Empty(rows);
        }

        [Fact]
        public void A_duplicated_name_is_refused_rather_than_producing_two_rows_about_one_thing()
        {
            List<GateRow> rows; string verdict;
            string refusal = PreDeliveryGateRules.Evaluate(
                new[] { R("require_coordinate_facts", new List<string> { "survey_point", "survey_point" }) },
                new Dictionary<string, GateMeasurement>(), out rows, out verdict);

            Assert.NotNull(refusal);
            Assert.Contains("twice", refusal);
        }

        [Fact]
        public void An_empty_list_is_a_recorded_waiver_and_not_a_pass()
        {
            List<GateRow> rows; string verdict;
            PreDeliveryGateRules.Evaluate(
                new[] { R("require_coordinate_facts", new List<string>()) },
                new Dictionary<string, GateMeasurement>(), out rows, out verdict);

            Assert.Equal("waived", Assert.Single(rows).Status);
            Assert.Equal("pass", verdict);
        }

        // ----------------------------------------------------- tolerances

        [Fact]
        public void A_tolerance_in_the_requirement_set_is_refused_and_says_where_it_belongs()
        {
            List<GateRow> rows; string verdict;
            string refusal = PreDeliveryGateRules.Evaluate(
                new[] { R(DatumRules.ToleranceLevelCoincidence, 1.0) },
                new Dictionary<string, GateMeasurement>(), out rows, out verdict);

            Assert.NotNull(refusal);
            Assert.Contains("is a TOLERANCE, not a requirement", refusal);
            Assert.Contains("sibling 'tolerances' object", refusal);
            Assert.Empty(rows);
        }

        [Fact]
        public void The_same_value_in_the_tolerances_object_is_accepted_and_produces_no_row()
        {
            Assert.Null(PreDeliveryGateRules.ValidateTolerances(
                new[] { R(DatumRules.ToleranceLevelCoincidence, 1.0),
                        R(CoordinateRules.ToleranceFarRadius, 500000.0) }));
        }

        [Fact]
        public void An_unknown_tolerance_is_refused_because_a_silently_ignored_one_leaves_the_default_running()
        {
            string refusal = PreDeliveryGateRules.ValidateTolerances(new[] { R("wobble_mm", 1.0) });
            Assert.NotNull(refusal);
            Assert.Contains("wobble_mm", refusal);
            Assert.Contains("running on its", refusal);
        }

        [Fact]
        public void A_negative_or_unparseable_tolerance_is_refused()
        {
            Assert.NotNull(PreDeliveryGateRules.ValidateTolerances(
                new[] { R(DatumRules.ToleranceLevelCoincidence, -1.0) }));
            Assert.NotNull(PreDeliveryGateRules.ValidateTolerances(
                new[] { R(DatumRules.ToleranceLevelCoincidence, "wide") }));
        }

        [Fact]
        public void No_tolerances_at_all_is_fine()
        {
            Assert.Null(PreDeliveryGateRules.ValidateTolerances(null));
        }

        // --------------------------------------------- and the name agreement

        [Fact]
        public void Every_part_the_gate_maps_onto_belongs_to_a_finding_that_exists()
        {
            // The same guarantee AuditCheckNameTests gives for whole findings, one
            // level down: "datums.coincident_levels" is only measurable if `datums`
            // is a finding the audit can emit.
            foreach (string mapped in PreDeliveryGateRules.MappedCheckNames())
                Assert.True(AuditCheckNames.IsMeasurable(mapped), mapped);

            Assert.False(AuditCheckNames.IsMeasurable("not_a_finding.part"));
            Assert.False(AuditCheckNames.IsMeasurable("datums."));
        }
    }
}
