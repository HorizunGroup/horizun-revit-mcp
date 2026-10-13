// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// ONE MARK, ONE BAR. In reinforcement practice the same mark on several sets of
// identical bars is correct - one schedule line per position - and Revit itself
// numbers identical bars with the same Rebar Number. What is wrong is one mark
// covering bars that are NOT the same: another bar type, another shape, or a
// different length. These pin both halves: identical sets are not a finding, and
// different bars under one mark still fail, naming what differs with both values.
//
// The rows are the shape RebarFacts.Describe emits: measured.schedule_mark,
// bar_type.id/name, shape.id/name, measured.bar_centreline_length_mm,
// measured.total_length_mm and measured.quantity.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarMarkConflictTests
    {
        internal static JObject Bar(long id, string mark, long typeId = 300, string typeName = "12M",
                                    long shapeId = 400, string shapeName = "M_00",
                                    double? centrelineMm = 3000.0, double? totalMm = 15000.0, int? quantity = 5,
                                    long hostId = 5001)
        {
            return new JObject
            {
                ["id"] = id,
                ["host"] = new JObject { ["id"] = hostId, ["resolved"] = true },
                ["bar_type"] = new JObject
                {
                    ["resolved"] = true, ["id"] = typeId, ["name"] = typeName, ["nominal_diameter_mm"] = 12.0
                },
                ["shape"] = new JObject { ["id"] = shapeId, ["name"] = shapeName, ["resolved"] = true },
                ["layout"] = new JObject { ["quantity"] = quantity },
                ["measured"] = new JObject
                {
                    ["total_length_mm"] = totalMm,
                    ["quantity"] = quantity,
                    ["schedule_mark"] = mark,
                    ["bar_centreline_length_mm"] = centrelineMm
                }
            };
        }

        private static JObject Single(JArray f)
        {
            return f.OfType<JObject>().Single();
        }

        // ----------------------------------------------------- the same bar

        [Fact]
        public void IDENTICAL_sets_sharing_a_mark_are_not_a_finding()
        {
            JArray f = RebarAuditRules.MarkConflicts(new[] { Bar(1, "A1"), Bar(2, "A1"), Bar(3, "A1") }, 2.0);
            Assert.Empty(f);
        }

        [Fact]
        public void Identical_sets_sharing_a_mark_are_published_as_shared_not_hidden()
        {
            JArray shared = RebarAuditRules.SharedMarks(new[] { Bar(1, "A1"), Bar(2, "A1"), Bar(3, "B1") }, 2.0);
            JObject one = Single(shared);
            Assert.Equal("A1", (string)one["mark"]);
            Assert.Equal(new[] { 1L, 2L }, ((JArray)one["rebar_ids"]).Select(t => (long)t));
        }

        [Fact]
        public void A_length_difference_INSIDE_the_tolerance_is_the_same_bar()
        {
            JArray f = RebarAuditRules.MarkConflicts(
                new[] { Bar(1, "A1", centrelineMm: 3000.0), Bar(2, "A1", centrelineMm: 3001.5) }, 2.0);
            Assert.Empty(f);
        }

        // ------------------------------------------------- different bars

        [Fact]
        public void One_mark_on_two_BAR_TYPES_is_a_conflict_naming_both_types()
        {
            JArray f = RebarAuditRules.MarkConflicts(
                new[] { Bar(1, "A1"), Bar(2, "A1", typeId: 301, typeName: "16M") }, 2.0);
            JObject one = Single(f);
            Assert.Equal(RebarFinding.BarMarkConflict, (string)one["code"]);
            Assert.Equal(RebarSeverity.Error, (string)one["severity"]);
            Assert.Equal(new[] { 1L, 2L }, ((JArray)one["rebar_ids"]).Select(t => (long)t));
            JObject d = ((JArray)one["differences"]).OfType<JObject>().Single();
            Assert.Equal("bar_type", (string)d["property"]);
            Assert.Equal(1L, (long)d["reference_rebar_id"]);
            Assert.Equal(2L, (long)d["rebar_id"]);
            Assert.Equal("12M", (string)d["reference_value"]["name"]);
            Assert.Equal("16M", (string)d["value"]["name"]);
            Assert.Contains("12M", (string)one["observed"]);
            Assert.Contains("16M", (string)one["observed"]);
        }

        [Fact]
        public void One_mark_on_two_SHAPES_is_a_conflict()
        {
            JArray f = RebarAuditRules.MarkConflicts(
                new[] { Bar(1, "A1"), Bar(2, "A1", shapeId: 401, shapeName: "M_11") }, 2.0);
            JObject d = ((JArray)Single(f)["differences"]).OfType<JObject>().Single();
            Assert.Equal("shape", (string)d["property"]);
            Assert.Equal("M_00", (string)d["reference_value"]["name"]);
            Assert.Equal("M_11", (string)d["value"]["name"]);
        }

        [Fact]
        public void One_mark_on_two_LENGTHS_beyond_the_tolerance_is_a_conflict_with_both_lengths()
        {
            JArray f = RebarAuditRules.MarkConflicts(
                new[] { Bar(1, "A1", centrelineMm: 3000.0), Bar(2, "A1", centrelineMm: 3200.0) }, 2.0);
            JObject one = Single(f);
            Assert.Equal(RebarFinding.BarMarkConflict, (string)one["code"]);
            Assert.Contains("2 mm", (string)one["tolerance"]);
            JObject d = ((JArray)one["differences"]).OfType<JObject>().Single();
            Assert.Equal("one_bar_length_mm", (string)d["property"]);
            Assert.Equal(3000.0, (double)d["reference_value"], 3);
            Assert.Equal(3200.0, (double)d["value"], 3);
        }

        [Fact]
        public void Without_a_centreline_length_one_bar_is_the_total_over_the_quantity()
        {
            // 15000 / 5 = 3000 against 16000 / 5 = 3200: different bars, measured the fallback way.
            JArray f = RebarAuditRules.MarkConflicts(new[]
            {
                Bar(1, "A1", centrelineMm: null, totalMm: 15000.0, quantity: 5),
                Bar(2, "A1", centrelineMm: null, totalMm: 16000.0, quantity: 5)
            }, 2.0);
            JObject d = ((JArray)Single(f)["differences"]).OfType<JObject>().Single();
            Assert.Equal("one_bar_length_mm", (string)d["property"]);
            Assert.Equal(3200.0, (double)d["value"], 3);
            Assert.Equal("total_length_mm/quantity", (string)d["value_source"]);
        }

        [Fact]
        public void EVERY_difference_is_named_not_only_the_first()
        {
            JArray f = RebarAuditRules.MarkConflicts(new[]
            {
                Bar(1, "A1"),
                Bar(2, "A1", typeId: 301, typeName: "16M", shapeId: 401, shapeName: "M_11", centrelineMm: 4000.0)
            }, 2.0);
            var props = ((JArray)Single(f)["differences"]).Select(t => (string)t["property"]).ToList();
            Assert.Equal(new[] { "bar_type", "shape", "one_bar_length_mm" }, props);
        }

        [Fact]
        public void A_third_set_differing_from_the_first_two_is_still_caught()
        {
            JArray f = RebarAuditRules.MarkConflicts(new[]
            {
                Bar(1, "A1"), Bar(2, "A1"), Bar(3, "A1", centrelineMm: 2500.0)
            }, 2.0);
            JObject one = Single(f);
            Assert.Equal(new[] { 1L, 2L, 3L }, ((JArray)one["rebar_ids"]).Select(t => (long)t));
            Assert.Equal(3L, (long)((JArray)one["differences"])[0]["rebar_id"]);
        }

        // ---------------------------------------------------------- unknown

        [Fact]
        public void A_set_whose_length_cannot_be_read_is_UNKNOWN_not_the_same_bar()
        {
            JArray f = RebarAuditRules.MarkConflicts(new[]
            {
                Bar(1, "A1"), Bar(2, "A1", centrelineMm: null, totalMm: null)
            }, 2.0);
            JObject one = Single(f);
            Assert.Equal(RebarSeverity.Unknown, (string)one["severity"]);
            Assert.Equal(RebarFinding.Unreadable, (string)one["code"]);
            Assert.Equal(RebarFinding.BarMarkConflict, (string)one["about"]);
            Assert.Contains("one_bar_length_mm", ((JArray)one["unreadable_properties"]).Select(t => (string)t));
        }

        [Fact]
        public void An_unresolved_bar_type_is_UNKNOWN()
        {
            JObject b = Bar(2, "A1");
            b["bar_type"] = new JObject { ["resolved"] = false };
            JObject one = Single(RebarAuditRules.MarkConflicts(new[] { Bar(1, "A1"), b }, 2.0));
            Assert.Equal(RebarSeverity.Unknown, (string)one["severity"]);
        }

        [Fact]
        public void A_known_difference_beside_an_unreadable_one_is_still_an_ERROR()
        {
            JObject b = Bar(2, "A1", typeId: 301, typeName: "16M", centrelineMm: null, totalMm: null);
            JObject one = Single(RebarAuditRules.MarkConflicts(new[] { Bar(1, "A1"), b }, 2.0));
            Assert.Equal(RebarSeverity.Error, (string)one["severity"]);
            Assert.Equal(RebarFinding.BarMarkConflict, (string)one["code"]);
        }

        // ------------------------------------------------------- vocabulary

        [Fact]
        public void Blank_marks_and_single_sets_are_never_compared()
        {
            Assert.Empty(RebarAuditRules.MarkConflicts(new[]
            {
                Bar(1, ""), Bar(2, "", typeId: 301), Bar(3, null, typeId: 302), Bar(4, "Z9")
            }, 2.0));
        }

        [Fact]
        public void The_code_is_bar_mark_conflict_and_the_old_code_is_gone()
        {
            Assert.Contains("bar_mark_conflict", RebarFinding.All);
            Assert.DoesNotContain("bar_mark_duplicate", RebarFinding.All);
        }
    }
}
