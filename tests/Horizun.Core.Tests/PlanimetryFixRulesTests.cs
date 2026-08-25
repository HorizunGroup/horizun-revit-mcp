// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE RULES OF CORRECTING A FINDING, proved without a Revit.
//
// horizun_fix_planimetry is the first planimetry command that WRITES, and every
// way it could write the wrong thing is arithmetic rather than API. So the whole
// decision surface is here, and the properties are ordered by how expensive
// getting them wrong is:
//
//   1. A STALE FINDING CANNOT BE CORRECTED. The finding must still exist under
//      the same identity AND still show the observed state the caller approved
//      a fix for. Either one moved is a refusal that writes nothing.
//   2. AN UNKNOWN IS NEVER A CORRECTION. A check that could not measure
//      something has not found a defect, and writing on top of it would be
//      writing over a fact nobody read.
//   3. AN OPERATION MAY ONLY CORRECT WHAT IT ADDRESSES. Universal rules name
//      their own remedies; a requirement-set rule is judged by entity kind. A
//      sheet-numbering rule "fixed" by moving a viewport is refused.
//   4. NOTHING IS CHOSEN IMPLICITLY. Every final value is explicit, validated,
//      and unique within the batch - two actions landing on one name would
//      collide inside the transaction that already wrote the first.
//   5. RESOLUTION IS THE AUDITOR'S VERDICT. A finding is resolved only when its
//      rule stops producing it, and a finding that appeared meanwhile is
//      reported as new rather than hidden by the one that was fixed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanimetryFixRulesTests
    {
        // ---------------------------------------------------------------------
        // Builders.
        // ---------------------------------------------------------------------
        private static JObject FindingJson(string rule = "view.no-template",
                                           string set = PlanimetryRules.UniversalId,
                                           string version = PlanimetryRules.UniversalVersion,
                                           string sha = null, string entity = "view",
                                           long? sheet = null, long? view = 501,
                                           long[] elements = null, JObject observed = null)
        {
            var o = new JObject
            {
                ["rule_id"] = rule,
                ["requirement_set"] = set,
                ["requirement_set_version"] = version,
                ["entity_kind"] = entity,
                ["element_ids"] = new JArray((elements ?? new long[] { 501 }).Select(e => (JToken)e)),
                ["observed"] = observed ?? new JObject { ["template_id"] = JValue.CreateNull() }
            };
            if (sha != null) o["requirement_set_sha256"] = sha;
            if (sheet.HasValue) o["sheet_id"] = sheet.Value;
            if (view.HasValue) o["view_id"] = view.Value;
            return o;
        }

        private static PlanimetryFinding Finding(string rule = "view.no-template",
                                                 string set = PlanimetryRules.UniversalId,
                                                 long? sheet = null, long? view = 501,
                                                 long[] elements = null, JObject observed = null,
                                                 string status = "failed", string severity = "advisory")
        {
            return new PlanimetryFinding
            {
                RuleId = rule,
                RequirementSetId = set,
                RequirementSetVersion = PlanimetryRules.UniversalVersion,
                Severity = severity,
                Status = status,
                EntityKind = "view",
                SheetId = sheet,
                ViewId = view,
                ElementIds = (elements ?? new long[] { 501 }).ToList(),
                Observed = observed ?? new JObject { ["template_id"] = JValue.CreateNull() }
            };
        }

        // =====================================================================
        // 1. THE CATALOG
        // =====================================================================

        [Fact]
        public void The_catalog_holds_exactly_the_nine_operations_this_phase_implements()
        {
            var expected = new[]
            {
                "set_view_template", "set_view_scale", "rename_view", "rename_sheet",
                "place_title_block", "move_viewport", "move_schedule",
                "clear_element_override", "set_crop"
            };
            Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal),
                         PlanimetryFixRules.Catalog.Select(o => o.Name).OrderBy(x => x, StringComparer.Ordinal));
        }

        [Fact]
        public void Every_catalog_entry_declares_a_target_field_that_is_one_of_its_own_fields()
        {
            foreach (PlanimetryFixOperation op in PlanimetryFixRules.Catalog)
            {
                Assert.False(string.IsNullOrWhiteSpace(op.TargetField), op.Name + " names no target field");
                Assert.Contains(op.TargetField, op.Fields);
                Assert.NotEmpty(op.EntityKinds);
                foreach (string required in op.RequiredFields)
                    Assert.Contains(required, op.Fields);
            }
        }

        [Fact]
        public void An_operation_outside_the_catalog_is_not_resolved()
        {
            Assert.Null(PlanimetryFixRules.Operation("pack_sheet"));
            Assert.Null(PlanimetryFixRules.Operation("auto_tag"));
            Assert.Null(PlanimetryFixRules.Operation(null));
            // The later phases are deliberately absent, not merely unimplemented.
            Assert.DoesNotContain("pack", PlanimetryFixRules.OperationsSentence(), StringComparison.Ordinal);
            Assert.DoesNotContain("tag", PlanimetryFixRules.OperationsSentence(), StringComparison.Ordinal);
        }

        // =====================================================================
        // 2. UNKNOWN NEVER BECOMES A CORRECTION
        // =====================================================================

        [Fact]
        public void An_unknown_severity_universal_rule_can_never_be_cited_by_any_operation()
        {
            string[] unknownRules = PlanimetryRules.Catalog
                .Where(c => c.Severity == "unknown").Select(c => c.Id).ToArray();
            Assert.NotEmpty(unknownRules);

            foreach (string rule in unknownRules)
                foreach (PlanimetryFixOperation op in PlanimetryFixRules.Catalog)
                {
                    string error = PlanimetryFixRules.RemedyError(rule, PlanimetryRules.UniversalId, "view", op);
                    Assert.NotNull(error);
                    Assert.Contains("could NOT be measured", error, StringComparison.Ordinal);
                }
        }

        [Fact]
        public void A_finding_whose_current_status_is_unknown_is_refused_before_any_write()
        {
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson());
            PlanimetryFinding current = Finding(status: "unknown");

            string error = PlanimetryFixRules.StaleError(cited, current);
            Assert.NotNull(error);
            Assert.Contains("UNKNOWN", error, StringComparison.Ordinal);
            Assert.Contains("Nothing was written", error, StringComparison.Ordinal);
        }

        // =====================================================================
        // 3. STALENESS
        // =====================================================================

        [Fact]
        public void A_finding_that_no_longer_exists_is_a_stale_finding()
        {
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson());

            string error = PlanimetryFixRules.StaleError(cited, null);
            Assert.NotNull(error);
            Assert.Contains("STALE FINDING", error, StringComparison.Ordinal);
            Assert.Contains("Nothing was written", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_finding_whose_observed_state_moved_is_a_stale_observation()
        {
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson(
                observed: new JObject { ["scale"] = 100 }));
            PlanimetryFinding current = Finding(observed: new JObject { ["scale"] = 50 });

            string error = PlanimetryFixRules.StaleError(cited, current);
            Assert.NotNull(error);
            Assert.Contains("STALE OBSERVATION", error, StringComparison.Ordinal);
            // The refusal names BOTH states: "it moved" without saying to what sends
            // somebody diffing JSON by hand.
            Assert.Contains("100", error, StringComparison.Ordinal);
            Assert.Contains("50", error, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unchanged_finding_passes_the_staleness_gate()
        {
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson(
                observed: new JObject { ["scale"] = 100, ["template_id"] = JValue.CreateNull() }));
            PlanimetryFinding current = Finding(
                observed: new JObject { ["template_id"] = JValue.CreateNull(), ["scale"] = 100 });

            // Key ORDER is not a fact about the model, so the same values in another
            // order are the same observation.
            Assert.Null(PlanimetryFixRules.StaleError(cited, current));
        }

        [Fact]
        public void Two_observed_blocks_that_are_identical_are_not_drift_even_when_every_value_is_null()
        {
            // MEASURED live on Revit 2023: an allowed_template finding whose observed
            // block is {"template_id":null,"template_name":null} was refused as a
            // STALE OBSERVATION against an identical block. The refusal printed both
            // sides and they were the same text.
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson(
                observed: new JObject { ["template_id"] = JValue.CreateNull(),
                                        ["template_name"] = JValue.CreateNull() }));
            PlanimetryFinding current = Finding(
                observed: new JObject { ["template_id"] = JValue.CreateNull(),
                                        ["template_name"] = JValue.CreateNull() });

            Assert.Null(PlanimetryFixRules.StaleError(cited, current));
        }

        [Fact]
        public void A_null_string_and_a_parsed_null_are_the_same_absence()
        {
            // MEASURED live on Revit 2023. The auditor builds observed blocks from C#
            // values - allowed_template writes ["template_name"] = v.TemplateName,
            // and TemplateName is a null string. Newtonsoft gives THAT JValue
            // JTokenType.String with a null value, while the same block parsed back
            // from JSON gives JTokenType.Null. Both render as `null`, and DeepEquals
            // compares the TYPES first, so a correction was refused as a STALE
            // OBSERVATION against a block identical to it - printing both sides as
            // the same text, which is the worst possible refusal to debug.
            string absentName = null;
            var built = new JObject
            {
                ["template_id"] = JValue.CreateNull(),
                ["template_name"] = absentName
            };
            var parsed = JObject.Parse(built.ToString(Newtonsoft.Json.Formatting.None));

            // They render identically...
            Assert.Equal(built.ToString(Newtonsoft.Json.Formatting.None),
                         parsed.ToString(Newtonsoft.Json.Formatting.None));
            // ...so they must compare identically. Absence is absence however it
            // arrived.
            Assert.Null(PlanimetryFixRules.StaleError(Parse(FindingJson(observed: parsed)),
                                                      Finding(observed: built)));
        }

        [Fact]
        public void Canonical_does_not_mutate_what_it_reads()
        {
            // If canonicalising STEALS a child token from its source, the second call
            // sees a different object than the first did - and two identical blocks
            // compare unequal for a reason no reader could ever find.
            var original = new JObject
            {
                ["a"] = JValue.CreateNull(),
                ["b"] = "text",
                ["c"] = true,
                ["d"] = new JArray("x", JValue.CreateNull())
            };
            string before = original.ToString(Newtonsoft.Json.Formatting.None);
            PlanimetryFixRules.Canonical(original);
            PlanimetryFixRules.Canonical(original);
            Assert.Equal(before, original.ToString(Newtonsoft.Json.Formatting.None));
        }

        [Fact]
        public void Numeric_formatting_alone_is_not_drift()
        {
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson(
                observed: new JObject { ["overlap_x"] = 12.0 }));
            PlanimetryFinding current = Finding(observed: new JObject { ["overlap_x"] = 12 });

            // 12.0 and 12 are one measurement. A refusal here would make every
            // geometric finding uncorrectable for a reason nobody could act on.
            Assert.Null(PlanimetryFixRules.StaleError(cited, current));
        }

        [Fact]
        public void The_identity_ignores_element_order_but_not_element_membership()
        {
            string a = PlanimetryFixRules.IdentityOf("r", "s", 1, 2, new long[] { 30, 10, 20 });
            string b = PlanimetryFixRules.IdentityOf("r", "s", 1, 2, new long[] { 10, 20, 30 });
            string c = PlanimetryFixRules.IdentityOf("r", "s", 1, 2, new long[] { 10, 20, 31 });

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void The_identity_separates_its_fields_so_two_findings_cannot_collide()
        {
            // Without a separator, rule "a" + set "bc" and rule "ab" + set "c" would be
            // one key - and a fix aimed at one would satisfy the staleness gate of the
            // other.
            Assert.NotEqual(PlanimetryFixRules.IdentityOf("a", "bc", null, null, new long[] { 1 }),
                            PlanimetryFixRules.IdentityOf("ab", "c", null, null, new long[] { 1 }));
        }

        [Fact]
        public void The_identity_does_not_fold_in_the_observed_state()
        {
            // Deliberate: identity finds the finding AGAIN, and the observed comparison
            // is what then decides staleness. Folding them together would report every
            // drift as "the finding is gone", which sends the caller looking for a
            // deletion that never happened.
            PlanimetryFinding one = Finding(observed: new JObject { ["scale"] = 100 });
            PlanimetryFinding two = Finding(observed: new JObject { ["scale"] = 50 });
            Assert.Equal(PlanimetryFixRules.IdentityOf(one), PlanimetryFixRules.IdentityOf(two));
        }

        // =====================================================================
        // 4. THE FINDING BLOCK ITSELF
        // =====================================================================

        [Fact]
        public void A_finding_is_refused_when_it_names_a_key_nobody_declared()
        {
            JObject json = FindingJson();
            json["severity"] = "blocking";
            string error;
            Assert.Null(PlanimetryFixRules.ParseFinding(json, out error));
            Assert.Contains("unknown key 'severity'", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_finding_without_observed_evidence_is_refused()
        {
            JObject json = FindingJson();
            json.Remove("observed");
            string error;
            Assert.Null(PlanimetryFixRules.ParseFinding(json, out error));
            Assert.Contains("observed is required", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_finding_with_no_element_ids_is_refused()
        {
            JObject json = FindingJson();
            json["element_ids"] = new JArray();
            string error;
            Assert.Null(PlanimetryFixRules.ParseFinding(json, out error));
            Assert.Contains("element_ids is required", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_universal_finding_from_another_catalog_version_is_refused()
        {
            JObject json = FindingJson(version: "0.9.0");
            string error;
            Assert.Null(PlanimetryFixRules.ParseFinding(json, out error));
            Assert.Contains("universal catalog is version", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_requirement_set_finding_without_its_sha256_is_refused()
        {
            JObject json = FindingJson(set: "acme-planimetry", version: "2.1.0", sha: null);
            string error;
            Assert.Null(PlanimetryFixRules.ParseFinding(json, out error));
            Assert.Contains("requirement_set_sha256 is required", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_requirement_set_finding_with_its_sha256_parses()
        {
            string error;
            PlanimetryFixRules.CitedFinding cited = PlanimetryFixRules.ParseFinding(
                FindingJson(set: "acme-planimetry", version: "2.1.0", sha: new string('a', 64)), out error);
            Assert.Null(error);
            Assert.NotNull(cited);
            Assert.False(cited.IsUniversal);
        }

        [Fact]
        public void A_non_integer_element_id_is_refused_rather_than_coerced()
        {
            JObject json = FindingJson();
            json["element_ids"] = new JArray("501");
            string error;
            Assert.Null(PlanimetryFixRules.ParseFinding(json, out error));
            Assert.Contains("must contain integers", error, StringComparison.Ordinal);
        }

        // =====================================================================
        // 5. REMEDIES - an operation may only correct what it addresses
        // =====================================================================

        [Fact]
        public void A_universal_rule_accepts_only_the_operations_its_remedy_table_names()
        {
            // The overlap is corrected by moving a placement, never by renaming a sheet.
            Assert.Null(PlanimetryFixRules.RemedyError("sheet.viewport-overlap", PlanimetryRules.UniversalId,
                "viewport", PlanimetryFixRules.Operation("move_viewport")));

            string error = PlanimetryFixRules.RemedyError("sheet.viewport-overlap", PlanimetryRules.UniversalId,
                "viewport", PlanimetryFixRules.Operation("rename_sheet"));
            Assert.NotNull(error);
            Assert.Contains("move_viewport", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_universal_rule_with_no_remedy_in_this_phase_says_so_and_names_the_honest_route()
        {
            // An orphaned tag is deleted, not corrected - and the refusal names the
            // command that does it rather than leaving the caller to guess.
            string error = PlanimetryFixRules.RemedyError("tag.orphaned", PlanimetryRules.UniversalId,
                "tag", PlanimetryFixRules.Operation("clear_element_override"));
            Assert.NotNull(error);
            Assert.Contains("horizun_delete_verified", error, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unplaced_view_is_refused_as_a_layout_decision_not_a_correction()
        {
            string error = PlanimetryFixRules.RemedyError("view.not-placed", PlanimetryRules.UniversalId,
                "view", PlanimetryFixRules.Operation("set_view_template"));
            Assert.NotNull(error);
            Assert.Contains("LAYOUT decision", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_rule_id_that_is_not_in_the_universal_catalog_is_refused()
        {
            string error = PlanimetryFixRules.RemedyError("sheet.invented-rule", PlanimetryRules.UniversalId,
                "sheet", PlanimetryFixRules.Operation("rename_sheet"));
            Assert.NotNull(error);
            Assert.Contains("not a universal planimetry check", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_requirement_set_rule_is_judged_by_entity_kind()
        {
            // The set's rules are the caller's, so their MEANING is unknowable here.
            // What is knowable: a viewport move does not address a sheet.
            Assert.Null(PlanimetryFixRules.RemedyError("acme.sheet-number", "acme-planimetry",
                "sheet", PlanimetryFixRules.Operation("rename_sheet")));

            string error = PlanimetryFixRules.RemedyError("acme.sheet-number", "acme-planimetry",
                "sheet", PlanimetryFixRules.Operation("move_viewport"));
            Assert.NotNull(error);
            Assert.Contains("this finding is about a sheet", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_requirement_set_rule_without_an_entity_kind_cannot_be_judged_and_is_refused()
        {
            string error = PlanimetryFixRules.RemedyError("acme.sheet-number", "acme-planimetry",
                null, PlanimetryFixRules.Operation("rename_sheet"));
            Assert.NotNull(error);
            Assert.Contains("entity_kind is required", error, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_remedy_the_table_names_is_a_real_operation_and_a_real_check()
        {
            foreach (var entry in PlanimetryFixRules.UniversalRemedyCatalog())
            {
                Assert.True(PlanimetryRules.Check(entry.Key) != null,
                    "the remedy table names '" + entry.Key + "', which is not a universal check");
                foreach (string operation in entry.Value)
                    Assert.True(PlanimetryFixRules.Operation(operation) != null,
                        "the remedy table names operation '" + operation + "', which is not in the catalog");
            }
        }

        // =====================================================================
        // 6. FIELDS AND VALUES - nothing is chosen implicitly
        // =====================================================================

        [Fact]
        public void An_unknown_field_is_refused_rather_than_ignored()
        {
            PlanimetryFixOperation op = PlanimetryFixRules.Operation("set_view_scale");
            string error = PlanimetryFixRules.UnknownFieldError(op,
                new[] { "operation", "finding", "view_id", "scale", "template_id" });
            Assert.NotNull(error);
            Assert.Contains("unknown field 'template_id'", error, StringComparison.Ordinal);
        }

        [Fact]
        public void The_common_fields_are_accepted_by_every_operation()
        {
            foreach (PlanimetryFixOperation op in PlanimetryFixRules.Catalog)
                Assert.Null(PlanimetryFixRules.UnknownFieldError(op,
                    new[] { "operation", "finding" }.Concat(op.Fields)));
        }

        [Fact]
        public void A_missing_required_field_is_named()
        {
            PlanimetryFixOperation op = PlanimetryFixRules.Operation("set_view_template");
            string error = PlanimetryFixRules.RequiredFieldError(op, f => f == "view_id");
            Assert.NotNull(error);
            Assert.Contains("requires 'template_id'", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_rename_sheet_that_names_nothing_is_refused()
        {
            PlanimetryFixOperation op = PlanimetryFixRules.Operation("rename_sheet");
            string error = PlanimetryFixRules.RequiredFieldError(op, f => f == "sheet_id");
            Assert.NotNull(error);
            Assert.Contains("renames nothing", error, StringComparison.Ordinal);

            // Either one alone is enough.
            Assert.Null(PlanimetryFixRules.RequiredFieldError(op, f => f == "sheet_id" || f == "new_number"));
            Assert.Null(PlanimetryFixRules.RequiredFieldError(op, f => f == "sheet_id" || f == "new_name"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void An_empty_name_is_refused(string value)
        {
            Assert.NotNull(PlanimetryFixRules.NameError("new_name", value));
        }

        [Fact]
        public void A_name_with_edge_whitespace_is_refused_because_Revit_would_strip_it()
        {
            // Revit trims silently, so the re-read would differ from the request and the
            // action would fail its own postcondition AFTER the write. Refused first.
            string error = PlanimetryFixRules.NameError("new_name", " PLANTA 01 ");
            Assert.NotNull(error);
            Assert.Contains("strips", error, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("A\\B")]
        [InlineData("A{B")]
        [InlineData("A}B")]
        [InlineData("A[B")]
        [InlineData("A]B")]
        [InlineData("A|B")]
        [InlineData("A;B")]
        [InlineData("A<B")]
        [InlineData("A>B")]
        [InlineData("A?B")]
        [InlineData("A`B")]
        [InlineData("A~B")]
        [InlineData("A:B")]
        public void A_name_carrying_a_character_Revit_refuses_is_refused_first(string value)
        {
            string error = PlanimetryFixRules.NameError("new_name", value);
            Assert.NotNull(error);
            Assert.Contains("refuses in element names", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_control_character_in_a_name_is_refused_and_named_by_code_point()
        {
            string error = PlanimetryFixRules.NameError("new_name", "PLANTA\u0007 01");
            Assert.NotNull(error);
            Assert.Contains("U+0007", error, StringComparison.Ordinal);
        }

        [Fact]
        public void An_ordinary_name_is_accepted()
        {
            Assert.Null(PlanimetryFixRules.NameError("new_name", "A-201 PLANTA PRIMER PISO"));
            Assert.Null(PlanimetryFixRules.NameError("new_number", "A-201"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(24001)]
        [InlineData(null)]
        public void A_scale_outside_Revits_range_is_refused(int? scale)
        {
            Assert.NotNull(PlanimetryFixRules.ScaleError(scale));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(50)]
        [InlineData(24000)]
        public void A_scale_inside_Revits_range_is_accepted(int scale)
        {
            Assert.Null(PlanimetryFixRules.ScaleError(scale));
        }

        [Fact]
        public void A_point_must_be_two_finite_numbers()
        {
            double x, y;
            Assert.Null(PlanimetryFixRules.PointError("point", new JArray(10.0, 20.0), out x, out y));
            Assert.Equal(10.0, x);
            Assert.Equal(20.0, y);

            Assert.NotNull(PlanimetryFixRules.PointError("point", new JArray(10.0), out x, out y));
            Assert.NotNull(PlanimetryFixRules.PointError("point", new JArray(10.0, 20.0, 30.0), out x, out y));
            Assert.NotNull(PlanimetryFixRules.PointError("point", new JArray("10", "20"), out x, out y));
            Assert.NotNull(PlanimetryFixRules.PointError("point", null, out x, out y));
        }

        [Fact]
        public void A_non_finite_coordinate_is_refused()
        {
            double x, y;
            Assert.NotNull(PlanimetryFixRules.PointError("point",
                new JArray(double.NaN, 1.0), out x, out y));
            Assert.NotNull(PlanimetryFixRules.PointError("point",
                new JArray(double.PositiveInfinity, 1.0), out x, out y));
            Assert.NotNull(PlanimetryFixRules.PointError("point",
                new JArray(1.0, double.NegativeInfinity), out x, out y));
        }

        [Fact]
        public void A_crop_must_have_min_strictly_below_max_on_both_axes()
        {
            double a, b, c, d;
            Assert.Null(PlanimetryFixRules.CropError(Crop(0, 0, 100, 50), out a, out b, out c, out d));

            Assert.NotNull(PlanimetryFixRules.CropError(Crop(0, 0, 0, 50), out a, out b, out c, out d));
            Assert.NotNull(PlanimetryFixRules.CropError(Crop(0, 0, 100, 0), out a, out b, out c, out d));
            Assert.NotNull(PlanimetryFixRules.CropError(Crop(100, 0, 0, 50), out a, out b, out c, out d));
        }

        [Fact]
        public void A_crop_with_an_unknown_key_is_refused()
        {
            JObject crop = Crop(0, 0, 100, 50);
            crop["rotation"] = 30;
            double a, b, c, d;
            string error = PlanimetryFixRules.CropError(crop, out a, out b, out c, out d);
            Assert.NotNull(error);
            Assert.Contains("unknown key 'rotation'", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_non_rectangular_crop_is_a_capability_question_not_a_typo()
        {
            JObject crop = Crop(0, 0, 100, 50);
            crop["loop"] = new JArray(new JArray(0, 0), new JArray(10, 0), new JArray(10, 10));
            Assert.True(PlanimetryFixRules.NonRectangularCrop(crop));
            Assert.False(PlanimetryFixRules.NonRectangularCrop(Crop(0, 0, 100, 50)));
        }

        private static JObject Crop(double minX, double minY, double maxX, double maxY)
            => new JObject { ["min"] = new JArray(minX, minY), ["max"] = new JArray(maxX, maxY) };

        [Fact]
        public void The_default_tolerance_is_a_tenth_of_a_millimetre_in_internal_feet()
        {
            double tolerance;
            Assert.Null(PlanimetryFixRules.ToleranceError(null, 1 / 304.8, out tolerance));
            Assert.Equal(0.1 / 304.8, tolerance, 12);
        }

        [Fact]
        public void An_explicit_tolerance_is_converted_from_the_calls_units()
        {
            double tolerance;
            Assert.Null(PlanimetryFixRules.ToleranceError(new JValue(1.0), 1 / 304.8, out tolerance));
            Assert.Equal(1.0 / 304.8, tolerance, 12);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void A_tolerance_that_is_not_a_positive_finite_number_is_refused(double value)
        {
            double tolerance;
            Assert.NotNull(PlanimetryFixRules.ToleranceError(new JValue(value), 1 / 304.8, out tolerance));
        }

        [Fact]
        public void A_tolerance_that_is_not_a_number_is_refused()
        {
            double tolerance;
            Assert.NotNull(PlanimetryFixRules.ToleranceError(new JValue("1mm"), 1 / 304.8, out tolerance));
        }

        // =====================================================================
        // 7. BATCH DISCIPLINE
        // =====================================================================

        [Fact]
        public void One_element_may_be_written_by_only_one_action_in_a_batch()
        {
            var claimed = new HashSet<long>();
            Assert.Null(PlanimetryFixRules.ClaimTargetError(claimed, 501));
            string error = PlanimetryFixRules.ClaimTargetError(claimed, 501);
            Assert.NotNull(error);
            Assert.Contains("more than one action", error, StringComparison.Ordinal);
        }

        [Fact]
        public void Two_actions_may_not_end_at_the_same_final_name()
        {
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            Assert.Null(PlanimetryFixRules.ClaimFinalValueError(claimed, "sheet number", "A-201"));
            string error = PlanimetryFixRules.ClaimFinalValueError(claimed, "sheet number", "A-201");
            Assert.NotNull(error);
            Assert.Contains("collide", error, StringComparison.Ordinal);
        }

        [Fact]
        public void The_final_value_claim_separates_its_kind_from_its_value()
        {
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            // "sheet number" + "X" and "sheet" + "numberX" must not be one claim.
            Assert.Null(PlanimetryFixRules.ClaimFinalValueError(claimed, "sheet number", "X"));
            Assert.Null(PlanimetryFixRules.ClaimFinalValueError(claimed, "sheet", "numberX"));
        }

        [Fact]
        public void A_null_final_value_claims_nothing()
        {
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            Assert.Null(PlanimetryFixRules.ClaimFinalValueError(claimed, "sheet name", null));
            Assert.Null(PlanimetryFixRules.ClaimFinalValueError(claimed, "sheet name", null));
            Assert.Empty(claimed);
        }

        // =====================================================================
        // 8. RECONCILIATION - resolved is the auditor's verdict
        // =====================================================================

        [Fact]
        public void A_finding_the_rule_stops_producing_is_resolved()
        {
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson());
            var before = new[] { Finding() };
            var after = new PlanimetryFinding[0];

            PlanimetryFixRules.Reconciliation rec =
                PlanimetryFixRules.Reconcile(new[] { cited }, before, after);

            Assert.Equal(1, rec.SelectedCount);
            Assert.Single(rec.ResolvedKeys);
            Assert.Empty(rec.Persistent);
            Assert.Empty(rec.New);
        }

        [Fact]
        public void A_finding_the_rule_still_produces_is_persistent_even_when_the_write_landed()
        {
            // The whole point: a verified postcondition does NOT make a finding
            // resolved. The rule is the verdict.
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson());
            var before = new[] { Finding() };
            var after = new[] { Finding() };

            PlanimetryFixRules.Reconciliation rec =
                PlanimetryFixRules.Reconcile(new[] { cited }, before, after);

            Assert.Empty(rec.ResolvedKeys);
            Assert.Single(rec.Persistent);
            Assert.Empty(rec.New);
        }

        [Fact]
        public void Resolving_one_finding_cannot_hide_that_another_appeared()
        {
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson());
            var before = new[] { Finding() };
            var after = new[] { Finding(rule: "sheet.viewport-overlap", view: null, sheet: 900,
                                        elements: new long[] { 700, 701 }) };

            PlanimetryFixRules.Reconciliation rec =
                PlanimetryFixRules.Reconcile(new[] { cited }, before, after);

            Assert.Single(rec.ResolvedKeys);
            Assert.Single(rec.New);
            Assert.Equal("sheet.viewport-overlap", rec.New[0].RuleId);
        }

        [Fact]
        public void A_finding_that_already_existed_before_the_fix_is_not_reported_as_new()
        {
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson());
            PlanimetryFinding other = Finding(rule: "sheet.no-titleblock", view: null, sheet: 900,
                                              elements: new long[] { 900 });
            var before = new[] { Finding(), other };
            var after = new[] { other };

            PlanimetryFixRules.Reconciliation rec =
                PlanimetryFixRules.Reconcile(new[] { cited }, before, after);

            Assert.Single(rec.ResolvedKeys);
            Assert.Empty(rec.New);
        }

        [Fact]
        public void A_passed_check_is_bookkeeping_and_never_counted_as_a_finding()
        {
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson());
            var before = new[] { Finding() };
            var after = new[] { Finding(status: "passed") };

            PlanimetryFixRules.Reconciliation rec =
                PlanimetryFixRules.Reconcile(new[] { cited }, before, after);

            // The rule PASSED, so the finding is resolved - and the passed row is not
            // then also reported as a new finding.
            Assert.Single(rec.ResolvedKeys);
            Assert.Empty(rec.New);
            Assert.Empty(rec.Persistent);
        }

        [Fact]
        public void The_same_finding_selected_twice_is_counted_once()
        {
            PlanimetryFixRules.CitedFinding one = Parse(FindingJson());
            PlanimetryFixRules.CitedFinding two = Parse(FindingJson());

            PlanimetryFixRules.Reconciliation rec = PlanimetryFixRules.Reconcile(
                new[] { one, two }, new[] { Finding() }, new PlanimetryFinding[0]);

            Assert.Equal(1, rec.SelectedCount);
            Assert.Single(rec.ResolvedKeys);
        }

        // ---- an uncollected population is not a resolved one -------------------

        [Fact]
        public void A_dead_collection_pass_makes_an_absent_finding_UNDETERMINED_not_resolved()
        {
            // PlanimetryInventory does not throw when a collection pass dies: it
            // records the failure and returns that population EMPTY. Classifying on
            // absence alone would then read a dead views pass as "every view finding
            // resolved" - the auditor's own JSON says "was NOT collected. Its contents
            // are unknown, not empty."
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson());
            var before = new[] { Finding() };
            var after = new PlanimetryFinding[0];

            PlanimetryFixRules.Reconciliation rec =
                PlanimetryFixRules.Reconcile(new[] { cited }, before, after,
                                             beforeCollectionFailures: 0, afterCollectionFailures: 1);

            Assert.Empty(rec.ResolvedKeys);
            Assert.Single(rec.UndeterminedKeys);
            Assert.Contains("DIED", rec.UndeterminedReason, StringComparison.Ordinal);
        }

        [Fact]
        public void A_finding_still_PRESENT_after_a_dead_pass_is_still_persistent()
        {
            // A positive observation is trustworthy whatever else failed: the rule
            // fired over those elements, and nothing about a dead pass elsewhere
            // changes that.
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson());
            var before = new[] { Finding() };
            var after = new[] { Finding() };

            PlanimetryFixRules.Reconciliation rec =
                PlanimetryFixRules.Reconcile(new[] { cited }, before, after, 0, 3);

            Assert.Single(rec.Persistent);
            Assert.Empty(rec.UndeterminedKeys);
            Assert.Empty(rec.ResolvedKeys);
        }

        [Fact]
        public void A_dead_pass_on_either_side_makes_the_new_list_a_lower_bound()
        {
            var before = new PlanimetryFinding[0];
            var after = new[] { Finding(rule: "sheet.no-titleblock", view: null, sheet: 900,
                                        elements: new long[] { 900 }) };

            Assert.True(PlanimetryFixRules.Reconcile(
                new PlanimetryFixRules.CitedFinding[0], before, after, 0, 0).NewIsComplete);
            Assert.False(PlanimetryFixRules.Reconcile(
                new PlanimetryFixRules.CitedFinding[0], before, after, 1, 0).NewIsComplete);
            Assert.False(PlanimetryFixRules.Reconcile(
                new PlanimetryFixRules.CitedFinding[0], before, after, 0, 1).NewIsComplete);
        }

        [Fact]
        public void With_every_pass_alive_an_absent_finding_is_resolved_as_before()
        {
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson());
            PlanimetryFixRules.Reconciliation rec = PlanimetryFixRules.Reconcile(
                new[] { cited }, new[] { Finding() }, new PlanimetryFinding[0], 0, 0);

            Assert.Single(rec.ResolvedKeys);
            Assert.Empty(rec.UndeterminedKeys);
            Assert.Null(rec.UndeterminedReason);
        }

        // ---- entity_kind is corroborated, not believed --------------------------

        [Fact]
        public void A_finding_whose_declared_entity_kind_contradicts_the_model_is_refused()
        {
            // The exploit this closes: a legitimate requirement-set finding over a
            // SHEET, re-sent as entity_kind "view" so it can be driven through
            // rename_view - which renames it without ever validating a sheet number,
            // because a ViewSheet IS a View to the Revit API.
            PlanimetryFixRules.CitedFinding cited = Parse(FindingJson(
                set: "acme", version: "1.0.0", sha: new string('b', 64), entity: "view"));
            PlanimetryFinding current = Finding(set: "acme");
            current.EntityKind = "sheet";

            string error = PlanimetryFixRules.StaleError(cited, current);
            Assert.NotNull(error);
            Assert.Contains("is about a sheet", error, StringComparison.Ordinal);
            Assert.Contains("Nothing was written", error, StringComparison.Ordinal);
        }

        [Fact]
        public void A_matching_entity_kind_passes_and_an_absent_one_does_not_invent_a_mismatch()
        {
            PlanimetryFixRules.CitedFinding matching = Parse(FindingJson(entity: "view"));
            Assert.Null(PlanimetryFixRules.StaleError(matching, Finding()));

            // A universal finding may legitimately omit entity_kind - the remedy table,
            // not the string, decides there - so its absence must not fabricate drift.
            JObject withoutKind = FindingJson();
            withoutKind.Remove("entity_kind");
            Assert.Null(PlanimetryFixRules.StaleError(Parse(withoutKind), Finding()));
        }

        // ---- the loop key, and the element-id bound -----------------------------

        [Fact]
        public void A_null_loop_is_still_a_request_for_a_loop()
        {
            // "a silently ignored field is a request the caller believes was honoured"
            // - the file's own rule. The KEY's presence is the request.
            JObject crop = Crop(0, 0, 100, 50);
            crop["loop"] = JValue.CreateNull();
            Assert.True(PlanimetryFixRules.NonRectangularCrop(crop));
        }

        [Fact]
        public void A_finding_naming_more_element_ids_than_the_schema_allows_is_refused()
        {
            JObject json = FindingJson();
            json["element_ids"] = new JArray(
                Enumerable.Range(1, PlanimetryFixRules.MaxFindingElementIds + 1).Select(i => (JToken)(long)i));
            string error;
            Assert.Null(PlanimetryFixRules.ParseFinding(json, out error));
            Assert.Contains("the limit is", error, StringComparison.Ordinal);
        }

        [Fact]
        public void Reconciliation_over_nothing_reports_nothing_rather_than_throwing()
        {
            PlanimetryFixRules.Reconciliation rec = PlanimetryFixRules.Reconcile(null, null, null);
            Assert.Equal(0, rec.SelectedCount);
            Assert.Empty(rec.ResolvedKeys);
            Assert.Empty(rec.Persistent);
            Assert.Empty(rec.New);
        }

        [Fact]
        public void New_findings_are_reported_in_a_deterministic_order()
        {
            var before = new PlanimetryFinding[0];
            var after = new[]
            {
                Finding(rule: "z.rule", view: 3, elements: new long[] { 3 }),
                Finding(rule: "a.rule", view: 1, elements: new long[] { 1 }),
                Finding(rule: "m.rule", view: 2, elements: new long[] { 2 })
            };

            PlanimetryFixRules.Reconciliation first =
                PlanimetryFixRules.Reconcile(new PlanimetryFixRules.CitedFinding[0], before, after);
            // Enumerable.Reverse, spelled out: on an array `after.Reverse()` binds to
            // MemoryExtensions.Reverse(Span<T>), which reverses IN PLACE and returns void.
            PlanimetryFixRules.Reconciliation second = PlanimetryFixRules.Reconcile(
                new PlanimetryFixRules.CitedFinding[0], before, Enumerable.Reverse(after).ToArray());

            Assert.Equal(first.New.Select(f => f.RuleId), second.New.Select(f => f.RuleId));
        }

        // =====================================================================
        // 9. THE TERMINAL STATE - one vocabulary for the whole bridge
        // =====================================================================

        [Fact]
        public void The_final_state_matrix_is_the_one_the_dimension_edits_earned()
        {
            Assert.Equal(DimensionEditRules.StateVerifiedApplied, PlanimetryFixRules.StateVerifiedApplied);
            Assert.Equal(DimensionEditRules.StateRolledBack, PlanimetryFixRules.StateRolledBack);
            Assert.Equal(DimensionEditRules.StateRefused, PlanimetryFixRules.StateRefused);
            Assert.Equal(DimensionEditRules.StateUncertain, PlanimetryFixRules.StateUncertain);
            Assert.Equal(DimensionEditRules.StateStalePlan, PlanimetryFixRules.StateStalePlan);
        }

        [Fact]
        public void A_committed_transaction_whose_re_read_disagrees_is_uncertain_never_partial()
        {
            Assert.Equal(PlanimetryFixRules.StateVerifiedApplied,
                PlanimetryFixRules.DecideFinalState(ApplicationOutcome.Committed, true));
            Assert.Equal(PlanimetryFixRules.StateUncertain,
                PlanimetryFixRules.DecideFinalState(ApplicationOutcome.Committed, false));
            Assert.Equal(PlanimetryFixRules.StateRolledBack,
                PlanimetryFixRules.DecideFinalState("RolledBack", false));
            Assert.Equal(PlanimetryFixRules.StateRefused,
                PlanimetryFixRules.DecideFinalState(ApplicationOutcome.NotStarted, false));
            Assert.Equal(PlanimetryFixRules.StateUncertain,
                PlanimetryFixRules.DecideFinalState("Pending", true));
        }

        [Fact]
        public void The_canonical_point_grid_is_the_one_the_dimension_rules_use()
        {
            Assert.Equal(DimensionEditRules.CanonicalTenthMillimetre(1.234),
                         PlanimetryFixRules.CanonicalTenthMillimetre(1.234));
            Assert.Equal("304.8,609.6", PlanimetryFixRules.CanonicalPoint2D(1.0, 2.0));
            // Negative zero and zero are one fact and must be one string, or a
            // before-value would drift on its own and refuse every apply.
            Assert.Equal(PlanimetryFixRules.CanonicalPoint2D(0.0, 0.0),
                         PlanimetryFixRules.CanonicalPoint2D(-0.0, -0.0));
        }

        // ---------------------------------------------------------------------
        private static PlanimetryFixRules.CitedFinding Parse(JObject json)
        {
            string error;
            PlanimetryFixRules.CitedFinding cited = PlanimetryFixRules.ParseFinding(json, out error);
            Assert.True(cited != null, "the finding did not parse: " + error);
            return cited;
        }
    }
}
