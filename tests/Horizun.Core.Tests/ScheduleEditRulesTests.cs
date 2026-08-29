// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The arithmetic behind horizun_manage_schedules. The stakes are quiet ones: a
// schedule edited wrong is a table that is ABOUT something else and still looks
// right, and the two-Comments-columns ambiguity is the canonical way that
// happens. Each block pins one of those doors shut.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleEditRulesTests
    {
        private static List<ScheduleFieldFacts> Fields() => new List<ScheduleFieldFacts>
        {
            new ScheduleFieldFacts { Index = 0, ParameterId = -1002500, Name = "Family", Heading = "Familia" },
            new ScheduleFieldFacts { Index = 1, ParameterId = -1002501, Name = "Type", Heading = "Tipo" },
            new ScheduleFieldFacts { Index = 2, ParameterId = -1010106, Name = "Comments", Heading = "Notas" },
            new ScheduleFieldFacts { Index = 3, ParameterId = 991234, Name = "Comments", Heading = "Comentarios PRD" }
        };

        // ---- field resolution --------------------------------------------------

        [Fact]
        public void A_parameter_id_resolves_exactly_one_field()
        {
            ScheduleFieldFacts field;
            Assert.Null(ScheduleEditRules.ResolveField(Fields(), -1010106, null, out field));
            Assert.Equal(2, field.Index);
        }

        [Fact]
        public void An_unambiguous_name_resolves_case_insensitively()
        {
            ScheduleFieldFacts field;
            Assert.Null(ScheduleEditRules.ResolveField(Fields(), null, "family", out field));
            Assert.Equal(0, field.Index);
        }

        [Fact]
        public void A_name_that_matches_two_fields_refuses_listing_both_parameter_ids()
        {
            ScheduleFieldFacts field;
            string error = ScheduleEditRules.ResolveField(Fields(), null, "Comments", out field);

            Assert.Null(field);
            Assert.Contains("-1010106", error);
            Assert.Contains("991234", error);
            Assert.Contains("parameter_id", error);
        }

        [Fact]
        public void A_heading_never_resolves_a_field_because_headings_are_presentation()
        {
            // "Notas" is field 2's HEADING. Resolution by heading would break the day
            // somebody retitles the column, so it must not resolve at all.
            ScheduleFieldFacts field;
            string error = ScheduleEditRules.ResolveField(Fields(), null, "Notas", out field);
            Assert.NotNull(error);
            Assert.Contains("Comments", error); // the roster shows what IS resolvable
        }

        [Fact]
        public void A_missing_field_names_the_roster_so_the_caller_can_fix_the_request()
        {
            ScheduleFieldFacts field;
            string error = ScheduleEditRules.ResolveField(Fields(), 555, null, out field);
            Assert.Contains("555", error);
            Assert.Contains("Family", error);
        }

        [Fact]
        public void Resolving_against_no_fields_is_an_error_not_a_null_pass()
        {
            ScheduleFieldFacts field;
            Assert.NotNull(ScheduleEditRules.ResolveField(new List<ScheduleFieldFacts>(), null, "x", out field));
            Assert.NotNull(ScheduleEditRules.ResolveField(null, null, "x", out field));
        }

        // ---- the operator table ------------------------------------------------

        [Fact]
        public void Text_operators_take_text_and_number_operators_take_numbers()
        {
            Assert.Null(ScheduleEditRules.ValidateFilter("contains", hasTextValue: true, hasNumberValue: false));
            Assert.Null(ScheduleEditRules.ValidateFilter("greater_than", hasTextValue: false, hasNumberValue: true));
            Assert.Null(ScheduleEditRules.ValidateFilter("equal", hasTextValue: true, hasNumberValue: false));
            Assert.Null(ScheduleEditRules.ValidateFilter("equal", hasTextValue: false, hasNumberValue: true));

            Assert.Contains("compares text", ScheduleEditRules.ValidateFilter("contains", false, true));
            Assert.Contains("compares numbers", ScheduleEditRules.ValidateFilter("greater_than", true, false));
        }

        [Fact]
        public void Has_value_takes_no_value_at_all()
        {
            Assert.Null(ScheduleEditRules.ValidateFilter("has_value", false, false));
            Assert.Contains("takes no value", ScheduleEditRules.ValidateFilter("has_value", true, false));
            Assert.Contains("takes no value", ScheduleEditRules.ValidateFilter("has_no_value", false, true));
        }

        [Fact]
        public void Both_value_shapes_at_once_is_refused_before_the_operator_is_even_consulted()
        {
            Assert.Contains("never both", ScheduleEditRules.ValidateFilter("equal", true, true));
        }

        [Fact]
        public void An_unknown_operator_is_refused_naming_the_known_ones()
        {
            string error = ScheduleEditRules.ValidateFilter("matches_regex", true, false);
            Assert.Contains("matches_regex", error);
            Assert.Contains("contains", error);
            Assert.Contains("has_value", error);
        }

        // ---- the canonical snapshot --------------------------------------------

        private static string Snapshot(bool itemized = false, string heading = "Familia",
                                       IEnumerable<string> filters = null)
        {
            var fields = Fields();
            fields[0].Heading = heading;
            return ScheduleEditRules.CanonicalDefinition(fields,
                filters ?? new[] { "Comments contains 'ok'" },
                new[] { "Family ascending header" },
                itemized: itemized, grandTotal: true, headers: true);
        }

        [Fact]
        public void The_same_definition_snapshots_identically_and_fingerprints_identically()
        {
            Assert.Equal(Snapshot(), Snapshot());
            Assert.Equal(ScheduleEditRules.DefinitionFingerprint(Snapshot()),
                         ScheduleEditRules.DefinitionFingerprint(Snapshot()));
        }

        [Fact]
        public void A_changed_option_and_a_changed_field_show_up_as_their_own_sections()
        {
            List<string> sections = ScheduleEditRules.ChangedSections(Snapshot(), Snapshot(itemized: true));
            Assert.Equal(new[] { "itemized" }, sections.ToArray());

            sections = ScheduleEditRules.ChangedSections(Snapshot(), Snapshot(heading: "Familia y tipo"));
            Assert.Equal(new[] { "fields" }, sections.ToArray());

            sections = ScheduleEditRules.ChangedSections(Snapshot(),
                Snapshot(filters: new[] { "Comments contains 'no'" }));
            Assert.Equal(new[] { "filters" }, sections.ToArray());
        }

        [Fact]
        public void An_identical_pair_changes_nothing_which_is_what_idempotence_gets_to_claim()
        {
            Assert.Empty(ScheduleEditRules.ChangedSections(Snapshot(), Snapshot()));
        }

        // ---- vocabulary ---------------------------------------------------------

        [Fact]
        public void The_operation_and_kind_vocabularies_are_closed_and_redirect_to_create_schedule()
        {
            Assert.Null(ScheduleEditRules.ValidateOperation("set_filters"));
            string error = ScheduleEditRules.ValidateOperation("create_category_schedule");
            Assert.Contains("horizun_create_schedule", error);

            Assert.Null(ScheduleEditRules.ValidateCreateKind("sheet_list"));
            Assert.Contains("horizun_create_schedule", ScheduleEditRules.ValidateCreateKind("category"));
        }

        [Fact]
        public void Material_takeoff_is_the_kind_that_needs_a_category()
        {
            Assert.True(ScheduleEditRules.KindNeedsCategory("material_takeoff"));
            Assert.False(ScheduleEditRules.KindNeedsCategory("sheet_list"));
            Assert.False(ScheduleEditRules.KindNeedsCategory("revision_schedule"));
        }

        [Fact]
        public void Sort_direction_is_two_words_and_nothing_else()
        {
            Assert.Null(ScheduleEditRules.ValidateSortDirection("ascending"));
            Assert.Null(ScheduleEditRules.ValidateSortDirection("descending"));
            Assert.NotNull(ScheduleEditRules.ValidateSortDirection("up"));
        }
    }
}
