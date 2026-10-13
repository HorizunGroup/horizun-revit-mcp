// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// L10 (acero 3.0): the contract publishes the three things this lot wires to tools -
// `chunk` on plan / apply / audit reinforcement, mode=parse_notation and
// mode=compare_bars on horizun_query_structure - and judges their arguments strictly:
// a write that runs one piece must quote the cut's digest, compare_bars has no default
// tolerance, and a key nobody declared is refused. Existing calls without the new keys
// read exactly as before.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Contracts;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class SteelScaleContractTests
    {
        private static JToken Schema(string tool) => Contract.Find(tool).InputSchema;

        private static readonly string Sha = new string('0', 63) + "a";

        private static JObject Set() => JObject.Parse("{ 'schema': 'horizun.structural-requirements/1' }");

        // ------------------------------------------------------------------ chunk

        [Theory]
        [InlineData("horizun_plan_reinforcement")]
        [InlineData("horizun_audit_reinforcement")]
        public void A_read_takes_a_chunk_with_or_without_the_digest(string tool)
        {
            var args = new JObject
            {
                ["requirement_set"] = Set(),
                ["chunk"] = JObject.Parse("{ 'by': 'level', 'hosts_per_piece': 50, 'piece_index': 0 }")
            };
            Assert.Empty(ContractSchemaCheck.Validate(args, Schema(tool)));
            args["chunk"]["partition_sha256"] = Sha;
            Assert.Empty(ContractSchemaCheck.Validate(args, Schema(tool)));
        }

        [Fact]
        public void A_write_of_one_piece_must_quote_the_digest_of_its_cut()
        {
            var args = new JObject
            {
                ["requirement_set"] = Set(),
                ["target_document"] = "HZ",
                ["chunk"] = JObject.Parse("{ 'by': 'host_type', 'hosts_per_piece': 50, 'piece_index': 3 }")
            };
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Schema("horizun_apply_reinforcement")));
            args["chunk"]["partition_sha256"] = Sha;
            Assert.Empty(ContractSchemaCheck.Validate(args, Schema("horizun_apply_reinforcement")));
        }

        [Theory]
        [InlineData("{ 'by': 'storey', 'hosts_per_piece': 50, 'piece_index': 0 }")]
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 0, 'piece_index': 0 }")]
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 2001, 'piece_index': 0 }")]
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 50, 'piece_index': -1 }")]
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 50 }")]
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 50, 'piece_index': 0, 'partition_sha256': 'ABC' }")]
        [InlineData("{ 'by': 'level', 'hosts_per_piece': 50, 'piece_index': 0, 'time_budget_s': 60 }")]
        public void A_chunk_that_is_not_exactly_right_breaks_the_contract(string chunk)
        {
            var args = new JObject { ["requirement_set"] = Set(), ["chunk"] = JObject.Parse(chunk) };
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Schema("horizun_plan_reinforcement")));
        }

        [Theory]
        [InlineData("horizun_plan_reinforcement")]
        [InlineData("horizun_audit_reinforcement")]
        [InlineData("horizun_apply_reinforcement")]
        public void A_call_without_the_new_key_reads_as_before(string tool)
        {
            var args = new JObject { ["requirement_set"] = Set(), ["host_ids"] = new JArray(1, 2) };
            if (tool == "horizun_apply_reinforcement") args["target_document"] = "HZ";
            Assert.Empty(ContractSchemaCheck.Validate(args, Schema(tool)));
            Assert.Contains("requirement_set", ((JArray)Schema(tool)["required"]).Select(x => (string)x));
            Assert.DoesNotContain("chunk", ((JArray)Schema(tool)["required"]).Select(x => (string)x));
        }

        // ---------------------------------------------------------- parse_notation

        [Fact]
        public void Parse_notation_takes_lines_of_text()
        {
            var args = new JObject { ["mode"] = "parse_notation", ["lines"] = new JArray("4#5 L=6.20", "E#3 c/0.10") };
            Assert.Empty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
            args["lines"] = new JArray("4#5", 5);
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
            args["lines"] = new JArray();
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
        }

        // ------------------------------------------------------------ compare_bars

        [Fact]
        public void Compare_bars_takes_two_sides_and_three_declared_tolerances()
        {
            var args = new JObject
            {
                ["mode"] = "compare_bars",
                ["element_ids_a"] = new JArray(101, 102),
                ["host_id_b"] = 2001,
                ["bar_tolerances"] = JObject.Parse("{ 'identical_mm': 5, 'near_mm': 50, 'length_mm': 10 }")
            };
            Assert.Empty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
        }

        [Theory]
        [InlineData("{ 'identical_mm': 5, 'near_mm': 50 }")]
        [InlineData("{ 'identical_mm': 5, 'length_mm': 10 }")]
        [InlineData("{ 'near_mm': 50, 'length_mm': 10 }")]
        [InlineData("{ 'identical_mm': -5, 'near_mm': 50, 'length_mm': 10 }")]
        [InlineData("{ 'identical_mm': 5, 'near_mm': 50, 'length_mm': 10, 'angle_deg': 2 }")]
        public void Compare_bars_has_no_default_tolerance(string tolerances)
        {
            var args = new JObject
            {
                ["mode"] = "compare_bars", ["element_ids_a"] = new JArray(1), ["element_ids_b"] = new JArray(2),
                ["bar_tolerances"] = JObject.Parse(tolerances)
            };
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
        }

        [Fact]
        public void The_reconcile_tolerances_are_untouched()
        {
            // compare_bars has its own key, so reconcile's length_pct stays required.
            JToken tol = Schema("horizun_query_structure")["properties"]["tolerances"];
            Assert.Equal(new[] { "length_pct" }, ((JArray)tol["required"]).Select(x => (string)x).ToArray());
        }

        [Fact]
        public void Rebar_mode_takes_include_constraints_as_a_boolean_off_by_default()
        {
            // Stage 1c: the constraint Revit holds each handle with, typed.
            var args = new JObject { ["mode"] = "rebar", ["element_ids"] = new JArray(885782), ["include_constraints"] = true };
            Assert.Empty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
            args["include_constraints"] = "yes";
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
            JToken p = Schema("horizun_query_structure")["properties"]["include_constraints"];
            Assert.Equal("boolean", (string)p["type"]);
            Assert.False((bool)p["default"]);
        }

        [Fact]
        public void The_mode_enum_publishes_both_new_modes()
        {
            var modes = ((JArray)Schema("horizun_query_structure")["properties"]["mode"]["enum"]).Select(x => (string)x).ToList();
            Assert.Contains("parse_notation", modes);
            Assert.Contains("compare_bars", modes);
            Assert.Contains("reconcile", modes);
            Assert.Contains("steel_review", modes);
        }

        // ------------------------------------------------------------ steel_review

        [Fact]
        public void Steel_review_takes_a_tolerance_and_optional_clearances_without_rule_pairs()
        {
            // Stage 1c: every bar of a model against every other, no requirement set.
            var args = new JObject { ["mode"] = "steel_review", ["tolerance_mm"] = 2, ["max_rows"] = 200 };
            Assert.Empty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
            args["clearances"] = new JObject { ["between_rules_mm"] = 25, ["crossing_mm"] = 0 };
            Assert.Empty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
            // Pairs name rules, and a model with no requirement set has none.
            args["clearances"] = new JObject { ["pairs"] = new JArray() };
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
            args["clearances"] = new JObject { ["between_rules_mm"] = 600 };
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
            args.Remove("clearances");
            args["tolerance_mm"] = 0;
            Assert.NotEmpty(ContractSchemaCheck.Validate(args, Schema("horizun_query_structure")));
        }
    }
}
