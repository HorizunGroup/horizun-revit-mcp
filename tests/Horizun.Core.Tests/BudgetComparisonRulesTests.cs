// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The quantity-to-budget join, proved at a desk. Every test here is a way the
// join could lie and the sentence it must refuse instead: a converted unit
// nobody declared, a price nobody agreed, a lower bound reported as a quantity,
// a fragment wearing a code's name, a trace that lost its element ids.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class BudgetComparisonRulesTests
    {
        // ---- builders -------------------------------------------------------

        private static JObject Row(string id, string code, string doc = "Host.rvt", string link = null,
                                   params JProperty[] quantities)
        {
            var q = new JObject();
            foreach (JProperty p in quantities) q.Add(p);
            var row = new JObject
            {
                ["element_id"] = id,
                ["document"] = doc,
                ["category"] = "Walls",
                ["type"] = "Generic",
                ["classification_code"] = code,
                ["quantities"] = q
            };
            if (link != null) row["link_instance_id"] = link;
            return row;
        }

        private static JProperty Measured(string name, double value, string unit = "m3") =>
            new JProperty(name, new JObject { ["value"] = value, ["state"] = "measured", ["unit"] = unit, ["reason"] = null });

        private static JProperty State(string name, string state, string unit = "m3", string reason = "why") =>
            new JProperty(name, new JObject { ["value"] = null, ["state"] = state, ["unit"] = unit, ["reason"] = reason });

        private static JObject Line(string code, string unit, JToken quantity, JToken unitPrice = null, int rowIndex = 0,
                                    string description = null, string currency = null)
        {
            var o = new JObject { ["code"] = code, ["unit"] = unit, ["quantity"] = quantity, ["row_index"] = rowIndex };
            if (unitPrice != null) o["unit_price"] = unitPrice;
            if (description != null) o["description"] = description;
            if (currency != null) o["currency"] = currency;
            return o;
        }

        private static JObject Run(JArray rows, JArray baseline, JObject mapping = null)
        {
            string problem;
            BudgetComparisonMapping m = BudgetComparisonRules.ReadMapping(mapping, out problem);
            Assert.True(problem == null, problem);
            var model = BudgetComparisonRules.ReadModelRows(rows, m.CodeField, out problem);
            Assert.True(problem == null, problem);
            int skipped;
            var lines = BudgetComparisonRules.ReadBaseline(baseline, out skipped, out problem);
            Assert.True(problem == null, problem);
            return BudgetComparisonRules.Compare(model, lines, m);
        }

        private static JObject LineOf(JObject result, string code) =>
            result["lines"].OfType<JObject>().Single(l => (string)l["code"] == code);

        // ---- statuses --------------------------------------------------------

        [Fact]
        public void Added_removed_modified_and_unchanged_are_told_apart()
        {
            var rows = new JArray
            {
                Row("1", "A-1", "Host.rvt", null, Measured("volume", 10)),
                Row("2", "A-1", "Host.rvt", null, Measured("volume", 5)),
                Row("3", "B-1", "Host.rvt", null, Measured("volume", 7)),
                Row("4", "C-1", "Host.rvt", null, Measured("volume", 1))
            };
            var baseline = new JArray
            {
                Line("A-1", "m3", 15, rowIndex: 2),
                Line("B-1", "m3", 10, rowIndex: 3),
                Line("D-1", "m3", 4, rowIndex: 4)
            };
            JObject r = Run(rows, baseline);

            Assert.Equal("unchanged", (string)LineOf(r, "A-1")["status"]);
            Assert.Equal("modified", (string)LineOf(r, "B-1")["status"]);
            Assert.Equal("added", (string)LineOf(r, "C-1")["status"]);
            Assert.Equal("removed", (string)LineOf(r, "D-1")["status"]);

            JObject summary = (JObject)r["summary"];
            Assert.Equal(1, (int)summary["added"]);
            Assert.Equal(1, (int)summary["removed"]);
            Assert.Equal(1, (int)summary["modified"]);
            Assert.Equal(1, (int)summary["unchanged"]);
            Assert.Equal(0, (int)summary["not_comparable"]);

            JObject delta = (JObject)LineOf(r, "B-1")["quantity_delta"];
            Assert.Equal(-3.0, (double)delta["abs"]);
            Assert.Equal(-30.0, (double)delta["pct"]);
        }

        [Fact]
        public void Tolerance_absolute_or_percentage_turns_modified_into_unchanged()
        {
            var rows = new JArray { Row("1", "A-1", "Host.rvt", null, Measured("volume", 10.4)) };
            var baseline = new JArray { Line("A-1", "m3", 10) };

            Assert.Equal("modified", (string)LineOf(Run(rows, baseline), "A-1")["status"]);
            Assert.Equal("unchanged", (string)LineOf(Run(rows, baseline,
                new JObject { ["tolerances"] = new JObject { ["quantity_abs"] = 0.5 } }), "A-1")["status"]);
            Assert.Equal("unchanged", (string)LineOf(Run(rows, baseline,
                new JObject { ["tolerances"] = new JObject { ["quantity_pct"] = 5 } }), "A-1")["status"]);
            Assert.Equal("modified", (string)LineOf(Run(rows, baseline,
                new JObject { ["tolerances"] = new JObject { ["quantity_pct"] = 1 } }), "A-1")["status"]);
        }

        // ---- units -----------------------------------------------------------

        [Fact]
        public void An_undeclared_unit_pair_is_unit_incompatible_never_converted()
        {
            var rows = new JArray { Row("1", "A-1", "Host.rvt", null, Measured("volume", 10, "m3")) };
            var baseline = new JArray { Line("A-1", "m2", 10) };
            JObject line = LineOf(Run(rows, baseline), "A-1");
            Assert.Equal("not_comparable", (string)line["status"]);
            Assert.Equal("unit_incompatible", (string)line["reason"]);
            Assert.Null(line["quantity_delta"].Type == JTokenType.Null ? null : line["quantity_delta"]);
            Assert.Contains("Nothing is converted silently", (string)line["detail"]);
        }

        [Fact]
        public void A_declared_factor_converts_in_the_declared_direction_only()
        {
            var rows = new JArray { Row("1", "A-1", "Host.rvt", null, Measured("volume", 2, "m3")) };
            var baseline = new JArray { Line("A-1", "ft3", 70.63) };
            var mapping = new JObject
            {
                ["unit_conversions"] = new JArray { new JObject { ["from"] = "m3", ["to"] = "ft3", ["factor"] = 35.3147 } },
                ["tolerances"] = new JObject { ["quantity_abs"] = 0.01 }
            };
            JObject line = LineOf(Run(rows, baseline, mapping), "A-1");
            Assert.Equal("unchanged", (string)line["status"]);
            Assert.Equal(35.3147, (double)line["model"]["selected"]["conversion_factor"]);

            // The inverse pair is not implied by the declared one.
            var reversedRows = new JArray { Row("1", "A-1", "Host.rvt", null, Measured("volume", 70.63, "ft3")) };
            var reversedBaseline = new JArray { Line("A-1", "m3", 2) };
            JObject reversed = LineOf(Run(reversedRows, reversedBaseline, mapping), "A-1");
            Assert.Equal("unit_incompatible", (string)reversed["reason"]);
        }

        [Fact]
        public void Two_model_quantities_in_the_baseline_unit_are_a_tie_that_is_refused_unless_pinned()
        {
            var rows = new JArray
            {
                Row("1", "A-1", "Host.rvt", null, Measured("gross_area", 10, "m2"), Measured("net_area", 8, "m2"))
            };
            var baseline = new JArray { Line("A-1", "m2", 8) };
            JObject tie = LineOf(Run(rows, baseline), "A-1");
            Assert.Equal("ambiguous_quantity", (string)tie["reason"]);

            JObject pinned = LineOf(Run(rows, baseline, new JObject { ["quantity_field"] = "net_area" }), "A-1");
            Assert.Equal("unchanged", (string)pinned["status"]);
            Assert.Equal("net_area", (string)pinned["model"]["selected"]["quantity_name"]);
        }

        // ---- zero / absent / incomplete / invalid ----------------------------

        [Fact]
        public void A_measured_zero_is_a_quantity_and_compares()
        {
            var rows = new JArray { Row("1", "A-1", "Host.rvt", null, Measured("volume", 0)) };
            JObject line = LineOf(Run(rows, new JArray { Line("A-1", "m3", 0) }), "A-1");
            Assert.Equal("unchanged", (string)line["status"]);
            Assert.Equal(0.0, (double)line["quantity_delta"]["model"]);
            Assert.Equal(JTokenType.Null, line["quantity_delta"]["pct"].Type);
        }

        [Fact]
        public void An_unreadable_element_makes_the_code_incomplete_read_and_the_partial_sum_is_a_lower_bound()
        {
            var rows = new JArray
            {
                Row("1", "A-1", "Host.rvt", null, Measured("volume", 10)),
                Row("2", "A-1", "Host.rvt", null, State("volume", "unreadable"))
            };
            JObject line = LineOf(Run(rows, new JArray { Line("A-1", "m3", 10) }), "A-1");
            Assert.Equal("not_comparable", (string)line["status"]);
            Assert.Equal("incomplete_read", (string)line["reason"]);
            Assert.Contains("LOWER BOUND", (string)line["detail"]);
            JObject coverage = (JObject)line["model"]["selected"]["coverage"];
            Assert.Equal(10.0, (double)coverage["known_total"]);
            Assert.Equal(1, (int)coverage["unreadable"]);
            Assert.False((bool)coverage["complete"]);
        }

        [Fact]
        public void An_absent_quantity_on_every_element_is_model_absent_not_zero()
        {
            var rows = new JArray { Row("1", "A-1", "Host.rvt", null, State("volume", "absent")) };
            JObject line = LineOf(Run(rows, new JArray { Line("A-1", "m3", 10) }), "A-1");
            Assert.Equal("model_absent", (string)line["reason"]);
            Assert.Equal(JTokenType.Null, line["quantity_delta"].Type);
        }

        [Fact]
        public void An_invalid_value_is_model_invalid_not_zero()
        {
            var rows = new JArray
            {
                Row("1", "A-1", "Host.rvt", null, Measured("volume", 3)),
                Row("2", "A-1", "Host.rvt", null, State("volume", "invalid", reason: "text parameter"))
            };
            JObject line = LineOf(Run(rows, new JArray { Line("A-1", "m3", 3) }), "A-1");
            Assert.Equal("model_invalid", (string)line["reason"]);
        }

        [Fact]
        public void Partial_coverage_is_refused_by_default_and_compared_only_by_explicit_rule()
        {
            var rows = new JArray
            {
                Row("1", "A-1", "Host.rvt", null, Measured("volume", 10)),
                Row("2", "A-1", "Host.rvt", null, State("volume", "empty"))
            };
            var baseline = new JArray { Line("A-1", "m3", 10) };
            JObject refused = LineOf(Run(rows, baseline), "A-1");
            Assert.Equal("partial_coverage", (string)refused["reason"]);

            JObject compared = LineOf(Run(rows, baseline,
                new JObject { ["rules"] = new JObject { ["compare_partial_coverage"] = true } }), "A-1");
            Assert.Equal("unchanged", (string)compared["status"]);
            Assert.False((bool)compared["quantity_delta"]["coverage_complete"]);
        }

        [Fact]
        public void Baseline_blank_and_non_numeric_quantities_are_distinct_refusals()
        {
            var rows = new JArray { Row("1", "A-1", "Host.rvt", null, Measured("volume", 1)), Row("2", "B-1", "Host.rvt", null, Measured("volume", 1)) };
            var baseline = new JArray { Line("A-1", "m3", JValue.CreateNull()), Line("B-1", "m3", "ten") };
            JObject r = Run(rows, baseline);
            Assert.Equal("baseline_absent", (string)LineOf(r, "A-1")["reason"]);
            Assert.Equal("baseline_invalid", (string)LineOf(r, "B-1")["reason"]);
        }

        [Fact]
        public void A_baseline_code_split_across_two_units_is_ambiguous_not_summed()
        {
            var rows = new JArray { Row("1", "A-1", "Host.rvt", null, Measured("volume", 1)) };
            var baseline = new JArray { Line("A-1", "m3", 1, rowIndex: 1), Line("A-1", "m2", 1, rowIndex: 2) };
            JObject line = LineOf(Run(rows, baseline), "A-1");
            Assert.Equal("baseline_ambiguous_unit", (string)line["reason"]);
            Assert.Equal(new[] { 1, 2 }, line["trace"]["baseline_rows"].Select(t => (int)t).ToArray());
        }

        // ---- price -----------------------------------------------------------

        [Fact]
        public void Price_delta_uses_the_baseline_unit_price_and_is_not_available_without_one()
        {
            var rows = new JArray
            {
                Row("1", "A-1", "Host.rvt", null, Measured("volume", 12)),
                Row("2", "B-1", "Host.rvt", null, Measured("volume", 12))
            };
            var baseline = new JArray
            {
                Line("A-1", "m3", 10, unitPrice: 100, currency: "COP"),
                Line("B-1", "m3", 10)
            };
            JObject r = Run(rows, baseline);

            JObject priced = (JObject)LineOf(r, "A-1")["price"];
            Assert.Equal("measured", (string)priced["state"]);
            Assert.Equal(1000.0, (double)priced["baseline_amount"]);
            Assert.Equal(1200.0, (double)priced["model_amount"]);
            Assert.Equal(200.0, (double)priced["amount_delta"]);
            Assert.Equal("COP", (string)priced["currency"]);

            JObject unpriced = (JObject)LineOf(r, "B-1")["price"];
            Assert.Equal("not_available", (string)unpriced["state"]);
            Assert.Equal(JTokenType.Null, unpriced["model_amount"].Type);
            Assert.Contains("never invented", (string)unpriced["reason"]);

            JObject summary = (JObject)r["summary"];
            Assert.Equal(1, (int)summary["priced_lines_compared"]);
            Assert.Equal(200.0, (double)summary["amount_delta_over_priced_compared_lines"]);
            Assert.False((bool)summary["amounts_are_complete"]);
        }

        [Fact]
        public void A_removed_priced_line_keeps_its_baseline_amount_and_no_model_amount()
        {
            var rows = new JArray { Row("1", "Z-9", "Host.rvt", null, Measured("volume", 1)) };
            var baseline = new JArray { Line("A-1", "m3", 4, unitPrice: 25) };
            JObject price = (JObject)LineOf(Run(rows, baseline), "A-1")["price"];
            Assert.Equal("baseline_only", (string)price["state"]);
            Assert.Equal(100.0, (double)price["baseline_amount"]);
            Assert.Equal(JTokenType.Null, price["model_amount"].Type);
            Assert.Equal(JTokenType.Null, price["amount_delta"].Type);
        }

        [Fact]
        public void Disagreeing_unit_prices_for_one_code_produce_no_rate()
        {
            var rows = new JArray { Row("1", "A-1", "Host.rvt", null, Measured("volume", 2)) };
            var baseline = new JArray { Line("A-1", "m3", 1, unitPrice: 10, rowIndex: 1), Line("A-1", "m3", 1, unitPrice: 12, rowIndex: 2) };
            JObject line = LineOf(Run(rows, baseline), "A-1");
            Assert.Equal("unchanged", (string)line["status"]);      // quantities still compare: 2 vs 1+1
            Assert.Equal("not_available", (string)line["price"]["state"]);
            Assert.Contains("disagree", (string)line["price"]["reason"]);
        }

        // ---- classification --------------------------------------------------

        [Fact]
        public void Unclassified_elements_are_pooled_by_non_value_and_never_become_added_codes()
        {
            var rows = new JArray
            {
                Row("1", "(no such parameter)", "Host.rvt", null, Measured("volume", 1)),
                Row("2", "(empty)", "Host.rvt", null, Measured("volume", 1)),
                Row("3", "(unreadable)", "Host.rvt", null, Measured("volume", 1)),
                Row("4", "A-1", "Host.rvt", null, Measured("volume", 1))
            };
            JObject r = Run(rows, new JArray { Line("A-1", "m3", 1) });
            Assert.Single(r["lines"]);
            JObject u = (JObject)r["unclassified"];
            Assert.Equal(3, (int)u["elements"]);
            Assert.Equal("1", (string)u["no_such_parameter"]["element_ids"][0]);
            Assert.Equal("2", (string)u["empty"]["element_ids"][0]);
            Assert.Equal("3", (string)u["unreadable"]["element_ids"][0]);
            Assert.Equal(3, (int)r["summary"]["model_elements_unclassified"]);
        }

        [Fact]
        public void A_catalogue_when_supplied_names_groups_and_unknown_codes_and_never_guesses_without_one()
        {
            var rows = new JArray
            {
                Row("1", "A", "Host.rvt", null, Measured("volume", 1)),
                Row("2", "A-1", "Host.rvt", null, Measured("volume", 1)),
                Row("3", "Q-7", "Host.rvt", null, Measured("volume", 1))
            };
            var baseline = new JArray { Line("A", "m3", 1), Line("A-1", "m3", 1), Line("Q-7", "m3", 1) };
            var mapping = new JObject
            {
                ["catalogue"] = new JObject
                {
                    ["version"] = "test-1",
                    ["codes"] = new JObject { ["A"] = false, ["A-1"] = true }
                }
            };
            JObject r = Run(rows, baseline, mapping);
            Assert.Equal("group_not_terminal", (string)LineOf(r, "A")["classification"]["delta"]);
            Assert.False((bool)LineOf(r, "A")["classification"]["is_leaf"]);
            Assert.Equal("none", (string)LineOf(r, "A-1")["classification"]["delta"]);
            Assert.Equal("not_in_catalogue", (string)LineOf(r, "Q-7")["classification"]["delta"]);

            JObject without = Run(rows, baseline);
            Assert.Equal("catalogue_not_supplied", (string)LineOf(without, "A")["classification"]["catalogue_status"]);
            Assert.Equal(JTokenType.Null, LineOf(without, "A")["classification"]["is_leaf"].Type);
        }

        /// <summary>
        /// The comparison restates the catalogue vocabulary so the server can link it
        /// without the readiness chain. Restated is fine; diverged is not - a report that
        /// says 'group_not_terminal' in one tool and something else in another is two
        /// words for one finding.
        /// </summary>
        [Fact]
        public void The_catalogue_status_vocabulary_is_the_readiness_audits_vocabulary()
        {
            Assert.Equal(CodeStatus.Leaf, BudgetCodeStatus.Leaf);
            Assert.Equal(CodeStatus.GroupNotTerminal, BudgetCodeStatus.GroupNotTerminal);
            Assert.Equal(CodeStatus.NotInCatalogue, BudgetCodeStatus.NotInCatalogue);
            Assert.Equal(CodeStatus.Invalid, BudgetCodeStatus.Invalid);
            Assert.Equal(CodeStatus.CatalogueNotSupplied, BudgetCodeStatus.CatalogueNotSupplied);

            // And the two readers agree on the same catalogue.
            var catalogue = new JObject { ["version"] = "v", ["codes"] = new JObject { ["A"] = false, ["A-1"] = true } };
            string problem;
            BudgetCatalogue mine = BudgetCatalogue.Read(catalogue, out problem);
            Assert.Null(problem);
            ClassificationCatalogue theirs = ClassificationCatalogueRules.Read(catalogue);
            Assert.True(theirs.Ok);
            foreach (string code in new[] { "A", "A-1", "Q", "" })
                Assert.Equal(ClassificationCatalogueRules.Classify(code, theirs), mine.Classify(code));
        }

        // ---- traceability ----------------------------------------------------

        [Fact]
        public void Every_line_keeps_element_ids_documents_link_instances_and_baseline_rows()
        {
            var rows = new JArray
            {
                Row("11", "A-1", "Host.rvt", null, Measured("volume", 1)),
                Row("22", "A-1", "Struct.rvt", "9001", Measured("volume", 1))
            };
            var baseline = new JArray { Line("A-1", "m3", 2, rowIndex: 7), Line("A-1", "m3", 0, rowIndex: 8) };
            JObject trace = (JObject)LineOf(Run(rows, baseline), "A-1")["trace"];
            Assert.Equal(new[] { "11", "22" }, trace["element_ids"].Select(t => (string)t).ToArray());
            Assert.Equal(new[] { "Host.rvt", "Struct.rvt" }, trace["documents"].Select(t => (string)t).ToArray());
            Assert.Equal(new[] { "9001" }, trace["link_instance_ids"].Select(t => (string)t).ToArray());
            Assert.Equal(new[] { 7, 8 }, trace["baseline_rows"].Select(t => (int)t).ToArray());
        }

        // ---- the sheet ---------------------------------------------------------

        [Fact]
        public void Sheet_rows_carry_the_header_one_row_per_code_and_blanks_where_no_number_exists()
        {
            var rows = new JArray
            {
                Row("1", "A-1", "Host.rvt", null, Measured("volume", 12)),
                Row("2", "B-1", "Host.rvt", null, Measured("volume", 1, "m3"))
            };
            var baseline = new JArray { Line("A-1", "m3", 10, unitPrice: 2, description: "Concrete"), Line("B-1", "m2", 1) };
            List<IList<object>> sheet = BudgetComparisonRules.SheetRows(Run(rows, baseline));

            Assert.Equal(3, sheet.Count);
            Assert.Equal(BudgetComparisonRules.SheetHeader.Length, sheet[0].Count);
            Assert.Equal("status", sheet[0][0]);
            int amountDelta = Array.IndexOf(BudgetComparisonRules.SheetHeader, "amount_delta");
            int qtyDelta = Array.IndexOf(BudgetComparisonRules.SheetHeader, "quantity_delta");
            Assert.Equal("modified", sheet[1][0]);
            Assert.Equal("Concrete", sheet[1][2]);
            Assert.Equal(2.0, (double)sheet[1][qtyDelta]);
            Assert.Equal(4.0, (double)sheet[1][amountDelta]);
            // The incompatible line has NO delta and NO amount: blank, not zero.
            Assert.Equal("not_comparable", sheet[2][0]);
            Assert.Null(sheet[2][qtyDelta]);
            Assert.Null(sheet[2][amountDelta]);
            Assert.Contains("elements: 2", (string)sheet[2][sheet[2].Count - 1]);

            JArray pbi = BudgetComparisonRules.PowerBiRows(Run(rows, baseline), "run-1");
            Assert.Equal(2, pbi.Count);
            Assert.Equal("run-1", (string)pbi[0]["run_id"]);
            Assert.Equal(JTokenType.Null, pbi[1]["amount_delta"].Type);
        }

        // ---- refusals of malformed input ----------------------------------------

        [Fact]
        public void Unknown_mapping_keys_and_bad_factors_are_refused()
        {
            string problem;
            Assert.Null(BudgetComparisonRules.ReadMapping(new JObject { ["tolerance"] = 1 }, out problem));
            Assert.Contains("mapping.tolerance is not a known key", problem);
            Assert.Null(BudgetComparisonRules.ReadMapping(new JObject
            {
                ["unit_conversions"] = new JArray { new JObject { ["from"] = "m3", ["to"] = "ft3", ["factor"] = 0 } }
            }, out problem));
            Assert.Contains("factor", problem);
            Assert.Null(BudgetComparisonRules.ReadMapping(new JObject
            {
                ["tolerances"] = new JObject { ["quantity_pct"] = -1 }
            }, out problem));
            Assert.Contains("quantity_pct", problem);
        }

        [Fact]
        public void A_truncated_takeoff_reply_is_refused_and_a_complete_one_is_unwrapped()
        {
            string problem;
            var truncated = new JObject { ["mode"] = "takeoff", ["truncated"] = true, ["rows_matching"] = 900, ["shown"] = 200, ["rows"] = new JArray() };
            Assert.Null(BudgetComparisonRules.ReadModelRows(truncated, "classification_code", out problem));
            Assert.Contains("TRUNCATED", problem);

            var complete = new JObject { ["mode"] = "takeoff", ["truncated"] = false,
                ["rows"] = new JArray { Row("1", "A-1", "Host.rvt", null, Measured("volume", 1)) } };
            var rows = BudgetComparisonRules.ReadModelRows(complete, "classification_code", out problem);
            Assert.Null(problem);
            Assert.Single(rows);

            var volumeMode = new JObject { ["mode"] = "volume", ["rows"] = new JArray() };
            Assert.Null(BudgetComparisonRules.ReadModelRows(volumeMode, "classification_code", out problem));
            Assert.Contains("takeoff", problem);
        }

        [Fact]
        public void Baseline_rows_with_a_blank_code_are_skipped_and_counted()
        {
            string problem; int skipped;
            var lines = BudgetComparisonRules.ReadBaseline(new JArray
            {
                Line("A-1", "m3", 1), Line("", "m3", 99), Line(null, "m3", 5)
            }, out skipped, out problem);
            Assert.Null(problem);
            Assert.Single(lines);
            Assert.Equal(2, skipped);
        }
    }
}
