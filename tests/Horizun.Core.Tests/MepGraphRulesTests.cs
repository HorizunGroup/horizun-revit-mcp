// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The local MEP graph of a create_elements batch: what is refused BEFORE Revit builds
// anything (tee over three collinear axes, an elbow returning 180 degrees on itself,
// fittings over curves that do not meet, a curve with no room between two fittings),
// and what is only warned (estimated room, mixed sizes at an elbow, a type with no
// routing preference).
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MepGraphRulesTests
    {
        private const double Ft = 304.8;

        private static JObject Pipe(double[] a, double[] b, double? diameter = null, long? type = null)
        {
            var o = new JObject { ["kind"] = "pipe", ["start"] = new JArray(a), ["end"] = new JArray(b) };
            if (diameter.HasValue) o["diameter"] = diameter.Value;
            if (type.HasValue) o["type_id"] = type.Value;
            return o;
        }

        private static JObject Fit(string sub, params int[] members) => new JObject
        {
            ["kind"] = "fitting", ["fitting"] = sub,
            ["elements"] = new JArray(members.Select(m => new JObject { ["batch_index"] = m }))
        };

        private static List<MepGraphIssue> Run(JArray rows, double? insertionMm = null)
        {
            MepGraphRules.ReadBatch(rows, 1.0 / Ft, out var curves, out var fittings);
            return MepGraphRules.Check(curves, fittings, insertionMm.HasValue ? insertionMm / Ft : null);
        }

        [Fact]
        public void An_L_with_an_elbow_has_no_issue()
        {
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }), Pipe(new[] { 3000.0, 0, 0 }, new[] { 3000.0, 2000, 0 }), Fit("elbow", 0, 1));
            Assert.Empty(Run(rows));
        }

        [Fact]
        public void An_elbow_over_two_curves_that_return_on_themselves_is_refused()
        {
            // the second pipe goes back along the first: a 180 degree return
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }), Pipe(new[] { 3000.0, 0, 0 }, new[] { 1000.0, 0, 0 }), Fit("elbow", 0, 1));
            MepGraphIssue issue = Assert.Single(Run(rows));
            Assert.True(issue.Refusal);
            Assert.Equal(MepGraphRules.CodeHairpin, issue.Code);
            Assert.Equal(2, issue.Index);
        }

        [Fact]
        public void A_return_a_few_degrees_off_is_still_a_return()
        {
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }), Pipe(new[] { 3000.0, 0, 0 }, new[] { 1000.0, 100, 0 }), Fit("elbow", 0, 1));
            Assert.Equal(MepGraphRules.CodeHairpin, Assert.Single(Run(rows)).Code);
        }

        [Fact]
        public void An_elbow_over_collinear_curves_asks_for_a_union()
        {
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }), Pipe(new[] { 3000.0, 0, 0 }, new[] { 5000.0, 0, 0 }), Fit("elbow", 0, 1));
            MepGraphIssue issue = Assert.Single(Run(rows));
            Assert.Equal(MepGraphRules.CodeCollinear, issue.Code);
            Assert.Contains("union", issue.Message);
        }

        [Fact]
        public void A_union_over_a_corner_is_refused_and_a_union_over_a_straight_joint_is_not()
        {
            var corner = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }), Pipe(new[] { 3000.0, 0, 0 }, new[] { 3000.0, 2000, 0 }), Fit("union", 0, 1));
            Assert.Equal(MepGraphRules.CodeJointTurn, Assert.Single(Run(corner)).Code);
            var straight = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }), Pipe(new[] { 3000.0, 0, 0 }, new[] { 5000.0, 0, 0 }), Fit("union", 0, 1));
            Assert.Empty(Run(straight));
        }

        [Fact]
        public void Curves_that_do_not_meet_are_refused_with_the_gap()
        {
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }), Pipe(new[] { 3050.0, 0, 0 }, new[] { 3050.0, 2000, 0 }), Fit("elbow", 0, 1));
            MepGraphIssue issue = Assert.Single(Run(rows));
            Assert.Equal(MepGraphRules.CodeNotMeeting, issue.Code);
            Assert.Contains("50.0 mm", issue.Message);
        }

        [Fact]
        public void A_perpendicular_tee_has_no_issue()
        {
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 2000.0, 0, 0 }), Pipe(new[] { 2000.0, 0, 0 }, new[] { 4000.0, 0, 0 }),
                                  Pipe(new[] { 2000.0, 0, 0 }, new[] { 2000.0, 1500, 0 }), Fit("tee", 0, 1, 2));
            Assert.Empty(Run(rows));
        }

        [Fact]
        public void A_tee_whose_branch_lies_along_the_run_is_refused()
        {
            // three axes on the X line: the branch overlaps the second run curve
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 2000.0, 0, 0 }), Pipe(new[] { 2000.0, 0, 0 }, new[] { 4000.0, 0, 0 }),
                                  Pipe(new[] { 2000.0, 0, 0 }, new[] { 3000.0, 0, 0 }), Fit("tee", 0, 1, 2));
            MepGraphIssue issue = Assert.Single(Run(rows));
            Assert.Equal(MepGraphRules.CodeTeeCollinear, issue.Code);
            Assert.True(issue.Refusal);
            Assert.Equal(3, issue.Index);
        }

        [Fact]
        public void A_tee_whose_run_returns_on_itself_is_refused()
        {
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 2000.0, 0, 0 }), Pipe(new[] { 2000.0, 0, 0 }, new[] { 500.0, 0, 0 }),
                                  Pipe(new[] { 2000.0, 0, 0 }, new[] { 2000.0, 1500, 0 }), Fit("tee", 0, 1, 2));
            Assert.Equal(MepGraphRules.CodeTeeHairpin, Assert.Single(Run(rows)).Code);
        }

        [Fact]
        public void An_oblique_branch_is_left_to_the_tee_path()
        {
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 2000.0, 0, 0 }), Pipe(new[] { 2000.0, 0, 0 }, new[] { 4000.0, 0, 0 }),
                                  Pipe(new[] { 2000.0, 0, 0 }, new[] { 3000.0, 1000, 0 }), Fit("tee", 0, 1, 2));
            Assert.Empty(Run(rows));
        }

        [Fact]
        public void A_fitting_over_an_existing_element_is_not_judged_here()
        {
            var fitting = new JObject { ["kind"] = "fitting", ["fitting"] = "elbow", ["elements"] = new JArray(new JObject { ["element_id"] = 5 }, new JObject { ["batch_index"] = 0 }) };
            Assert.Empty(Run(new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }), fitting)));
        }

        [Fact]
        public void A_short_curve_between_two_elbows_is_refused_when_the_insertion_is_given()
        {
            // 150 mm leg between two elbows that take 100 mm each
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }), Pipe(new[] { 3000.0, 0, 0 }, new[] { 3000.0, 150, 0 }),
                                  Pipe(new[] { 3000.0, 150, 0 }, new[] { 5000.0, 150, 0 }), Fit("elbow", 0, 1), Fit("elbow", 1, 2));
            MepGraphIssue issue = Assert.Single(Run(rows, 100));
            Assert.Equal(MepGraphRules.CodeShortBetween, issue.Code);
            Assert.True(issue.Refusal);
            Assert.Equal(1, issue.Index);
            Assert.Empty(Run(rows, 70));   // 2 x 70 = 140 <= 150: it fits
        }

        [Fact]
        public void Without_the_insertion_a_tight_curve_only_warns_and_says_it_is_an_estimate()
        {
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }, 100), Pipe(new[] { 3000.0, 0, 0 }, new[] { 3000.0, 150, 0 }, 100),
                                  Pipe(new[] { 3000.0, 150, 0 }, new[] { 5000.0, 150, 0 }, 100), Fit("elbow", 0, 1), Fit("elbow", 1, 2));
            MepGraphIssue issue = Assert.Single(Run(rows));
            Assert.False(issue.Refusal);
            Assert.Equal(MepGraphRules.CodeTightBetween, issue.Code);
            Assert.Contains("ESTIMATING", issue.Message);
        }

        [Fact]
        public void An_elbow_between_close_sizes_warns_to_use_one_size()
        {
            var rows = new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }, 50), Pipe(new[] { 3000.0, 0, 0 }, new[] { 3000.0, 2000, 0 }, 63), Fit("elbow", 0, 1));
            MepGraphIssue issue = Assert.Single(Run(rows));
            Assert.False(issue.Refusal);
            Assert.Equal(MepGraphRules.CodeMixedSizesAtElbow, issue.Code);
            Assert.Contains("63.0 mm", issue.Message);
        }

        [Fact]
        public void An_elbow_between_very_different_sizes_or_equal_sizes_says_nothing()
        {
            Assert.Empty(Run(new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }, 50), Pipe(new[] { 3000.0, 0, 0 }, new[] { 3000.0, 2000, 0 }, 110), Fit("elbow", 0, 1))));
            Assert.Empty(Run(new JArray(Pipe(new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 0 }, 63), Pipe(new[] { 3000.0, 0, 0 }, new[] { 3000.0, 2000, 0 }, 63), Fit("elbow", 0, 1))));
        }

        [Theory]
        [InlineData(1.39, true)]
        [InlineData(1.4, false)]
        [InlineData(1.0, false)]
        public void The_single_size_threshold_is_1_4_times(double ratio, bool advises)
        {
            double small = 50.0 / Ft;
            Assert.Equal(advises, MepGraphRules.SingleSizeAdvice(small, small * ratio) != null);
        }

        [Fact]
        public void The_existing_element_path_judges_from_connector_directions()
        {
            // connectors point OUT of their elements; two curves meeting head-on have antiparallel outward bases
            double[] a = { 1, 0, 0 }, b = { -1, 0, 0 }, up = { 0, 1, 0 };
            Assert.Null(MepGraphRules.ExistingJointProblem("elbow", a, up, null));
            Assert.NotNull(MepGraphRules.ExistingJointProblem("elbow", a, b, null));          // collinear: a union
            Assert.NotNull(MepGraphRules.ExistingJointProblem("elbow", a, a, null));          // same side: a 180 degree return
            Assert.Null(MepGraphRules.ExistingJointProblem("tee", a, b, up));
            Assert.NotNull(MepGraphRules.ExistingJointProblem("tee", a, b, a));               // branch along the run
            Assert.NotNull(MepGraphRules.ExistingJointProblem("tee", a, a, up));              // run returns on itself
        }

        [Theory]
        [InlineData("elbow", "elbows")]
        [InlineData("tee", "junctions")]
        [InlineData("transition", "transitions")]
        public void A_type_without_a_rule_for_the_fitting_gets_a_named_warning(string subtype, string group)
        {
            string w = MepGraphRules.RoutingPreferenceWarning("pipe", "Default", subtype, 0);
            Assert.NotNull(w);
            Assert.Contains("'Default'", w);
            Assert.Contains(group, w);
        }

        [Fact]
        public void A_type_with_a_rule_or_a_duct_union_gets_no_warning()
        {
            Assert.Null(MepGraphRules.RoutingPreferenceWarning("pipe", "PVC", "elbow", 2));
            Assert.Null(MepGraphRules.RoutingPreferenceWarning("duct", "Rect", "union", 0));
        }
    }
}
