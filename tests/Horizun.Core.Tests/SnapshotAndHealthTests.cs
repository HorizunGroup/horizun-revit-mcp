using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// TWO WAYS TO PRODUCE A FLATTERING LIE, AND THE RULES THAT REFUSE THEM.
    ///
    /// A trend report's characteristic failure is showing progress that is really
    /// a smaller sample: the second run could not read everything, the count fell,
    /// and "improved" goes on the slide. Its other one is comparing two runs
    /// judged against different standards, where the model improved because the
    /// requirement got easier.
    ///
    /// A health index's characteristic failure is a confident number about a model
    /// nobody finished looking at - the coordinates check died, its dimension was
    /// dropped from the average, and the remaining ones produced 91%.
    ///
    /// Every test here is one of those.
    /// </summary>
    public class SnapshotAndHealthTests
    {
        private static SnapshotCheck C(string name, double? count, bool lowerBound = false, bool complete = true)
        {
            return new SnapshotCheck
            {
                Check = name, Count = count, CountIsLowerBound = lowerBound,
                CoverageComplete = complete, IsIssue = count.HasValue && count.Value > 0
            };
        }

        private static DiagnosticsSnapshot S(string fingerprint, string reqSha, params SnapshotCheck[] checks)
        {
            return new DiagnosticsSnapshot
            {
                ModelFingerprint = fingerprint, DocumentTitle = "M", RequirementSetSha256 = reqSha,
                TakenUtc = "2026-08-29T00:00:00Z", Checks = checks.ToList()
            };
        }

        // ------------------------------------------------------- comparability

        [Fact]
        public void Two_snapshots_of_different_models_are_never_a_trend()
        {
            var c = SnapshotRules.Compare(S("aaa", "r1", C("warnings", 10)), S("bbb", "r1", C("warnings", 2)));
            Assert.False(c.Comparable);
            Assert.Contains("DIFFERENT models", c.WhyNot);
            Assert.Empty(c.Changes);
        }

        [Fact]
        public void Two_runs_judged_against_different_requirement_sets_are_not_a_trend_either()
        {
            // The counts might look comparable. The verdicts answer different
            // questions, and a model that "improved" because the standard got easier
            // is exactly the lie a trend report exists to prevent.
            var c = SnapshotRules.Compare(S("aaa", "strict", C("warnings", 10)), S("aaa", "lenient", C("warnings", 2)));
            Assert.False(c.Comparable);
            Assert.Contains("different questions", c.WhyNot);
        }

        [Fact]
        public void A_snapshot_with_no_fingerprint_cannot_be_shown_to_be_about_the_same_model()
        {
            var c = SnapshotRules.Compare(S(null, "r1", C("warnings", 1)), S("aaa", "r1", C("warnings", 1)));
            Assert.False(c.Comparable);
            Assert.Contains("without a model fingerprint", c.WhyNot);
        }

        // -------------------------------------------------------- the five words

        [Fact]
        public void New_resolved_persistent_worsened_and_improved_are_each_produced()
        {
            var before = S("aaa", "r1",
                C("a_new", 0), C("b_resolved", 5), C("c_persistent", 3), C("d_worsened", 2), C("e_improved", 9));
            var after = S("aaa", "r1",
                C("a_new", 4), C("b_resolved", 0), C("c_persistent", 3), C("d_worsened", 7), C("e_improved", 4));

            var c = SnapshotRules.Compare(before, after);
            Assert.True(c.Comparable);
            var byCheck = c.Changes.ToDictionary(x => x.Check, x => x.Kind);

            Assert.Equal(SnapshotChangeKind.New, byCheck["a_new"]);
            Assert.Equal(SnapshotChangeKind.Resolved, byCheck["b_resolved"]);
            Assert.Equal(SnapshotChangeKind.Persistent, byCheck["c_persistent"]);
            Assert.Equal(SnapshotChangeKind.Worsened, byCheck["d_worsened"]);
            Assert.Equal(SnapshotChangeKind.Improved, byCheck["e_improved"]);
            Assert.Equal(1, c.New);
            Assert.Equal(1, c.Resolved);
            Assert.Equal(1, c.Persistent);
            Assert.Equal(1, c.Worsened);
            Assert.Equal(1, c.Improved);
        }

        [Fact]
        public void A_LOWER_BOUND_CANNOT_PROVE_AN_IMPROVEMENT()
        {
            // THE CHARACTERISTIC LIE. The number fell because the second run read
            // less of the model, and "improved" is the most flattering possible
            // reading of that.
            var c = SnapshotRules.Compare(
                S("aaa", "r1", C("warnings", 40)),
                S("aaa", "r1", C("warnings", 9, lowerBound: true, complete: false)));

            var one = Assert.Single(c.Changes);
            Assert.Equal(SnapshotChangeKind.NotComparable, one.Kind);
            Assert.Contains("smaller sample rather than an improvement", one.Why);
        }

        [Fact]
        public void Nor_can_it_prove_a_resolution()
        {
            var c = SnapshotRules.Compare(
                S("aaa", "r1", C("warnings", 40)),
                S("aaa", "r1", C("warnings", 0, lowerBound: true, complete: false)));

            Assert.Equal(SnapshotChangeKind.NotComparable, Assert.Single(c.Changes).Kind);
            Assert.Contains("smaller sample rather than a fixed model", c.Changes[0].Why);
        }

        [Fact]
        public void A_lower_bound_CAN_still_prove_a_worsening()
        {
            // The asymmetry is the point: a count that is at least 60 is provably
            // worse than 40, whatever was missed.
            var c = SnapshotRules.Compare(
                S("aaa", "r1", C("warnings", 40)),
                S("aaa", "r1", C("warnings", 60, lowerBound: true, complete: false)));
            Assert.Equal(SnapshotChangeKind.Worsened, Assert.Single(c.Changes).Kind);
        }

        [Fact]
        public void A_check_that_did_not_run_in_one_of_them_is_not_comparable_in_either_direction()
        {
            var c = SnapshotRules.Compare(
                S("aaa", "r1", C("only_before", 3)),
                S("aaa", "r1", C("only_after", 3)));

            Assert.Equal(2, c.Changes.Count);
            Assert.All(c.Changes, ch => Assert.Equal(SnapshotChangeKind.NotComparable, ch.Kind));
            Assert.Contains(c.Changes, ch => ch.Why.Contains("has not found nothing"));
            Assert.Contains(c.Changes, ch => ch.Why.Contains("absence is not a resolution"));
        }

        // ------------------------------------------------------- health profile

        [Fact]
        public void A_profile_with_no_weights_is_refused_because_nothing_is_compiled_in()
        {
            List<string> codes;
            Assert.NotNull(HealthIndexRules.ValidateProfile(new HealthProfile(), out codes));
            Assert.Contains(HealthIndexRules.CodeNoWeights, codes);
        }

        [Fact]
        public void A_zero_weight_is_refused_because_leaving_it_out_is_visible_and_zero_is_not()
        {
            List<string> codes;
            var p = new HealthProfile { Id = "p", Version = "1", Context = HealthContext.Project };
            p.Weights.Add(new HealthWeight { Dimension = "warnings", Weight = 0 });
            string refusal = HealthIndexRules.ValidateProfile(p, out codes);
            Assert.NotNull(refusal);
            Assert.Contains("left out of the profile, which is visible", refusal);
        }

        [Fact]
        public void A_template_is_not_a_project_and_the_context_vocabulary_is_closed()
        {
            List<string> codes;
            var p = new HealthProfile { Id = "p", Version = "1", Context = "whatever" };
            p.Weights.Add(new HealthWeight { Dimension = "warnings", Weight = 1 });
            Assert.NotNull(HealthIndexRules.ValidateProfile(p, out codes));
            Assert.Contains(HealthIndexRules.CodeUnknownContext, codes);

            p.Context = HealthContext.Template;
            Assert.Null(HealthIndexRules.ValidateProfile(p, out codes));
        }

        // --------------------------------------------------------- the five rules

        [Fact]
        public void NO_GLOBAL_SCORE_WHEN_A_CRITICAL_DIMENSION_COULD_NOT_BE_MEASURED()
        {
            // THE CHARACTERISTIC LIE. Coordinates died, its dimension left the
            // average, and the rest produced a confident number about a model
            // nobody finished looking at.
            var profile = new HealthProfile { Id = "p", Version = "1", Context = HealthContext.Project };
            var dims = new List<HealthDimension>
            {
                HealthIndexRules.ScoreDimension("warnings", new List<HealthDeduction>(),
                    applicable: true, assessable: true, coverageComplete: true, weight: 1, critical: false),
                HealthIndexRules.ScoreDimension("coordinates", null,
                    applicable: true, assessable: false, coverageComplete: false, weight: 2, critical: true),
            };
            var index = HealthIndexRules.Roll(profile, dims);

            Assert.Null(index.Score);
            Assert.Contains("coordinates", index.ScoreSuppressedBecause);
            Assert.Contains("nobody finished looking at", index.ScoreSuppressedBecause);
            // and the dimensions that DID run still stand on their own
            Assert.Equal(100, index.Dimensions[0].Score.Value, 6);
        }

        [Fact]
        public void A_non_critical_dimension_that_could_not_be_measured_does_not_suppress_the_score()
        {
            var profile = new HealthProfile { Id = "p", Version = "1", Context = HealthContext.Project };
            var dims = new List<HealthDimension>
            {
                HealthIndexRules.ScoreDimension("warnings",
                    new[] { new HealthDeduction { Check = "warnings", Points = 20, Why = "20 warnings" } },
                    true, true, true, weight: 1, critical: false),
                HealthIndexRules.ScoreDimension("mep", null, true, assessable: false,
                    coverageComplete: true, weight: 1, critical: false),
            };
            var index = HealthIndexRules.Roll(profile, dims);
            Assert.Equal(80, index.Score.Value, 6);
        }

        [Fact]
        public void NOT_APPLICABLE_SCORES_NOTHING_rather_than_a_free_hundred()
        {
            // A model with no MEP is not a model with perfect MEP.
            var profile = new HealthProfile { Id = "p", Version = "1", Context = HealthContext.Project };
            var dims = new List<HealthDimension>
            {
                HealthIndexRules.ScoreDimension("warnings",
                    new[] { new HealthDeduction { Check = "warnings", Points = 40, Why = "many" } },
                    true, true, true, weight: 1, critical: false),
                HealthIndexRules.ScoreDimension("mep", null, applicable: false, assessable: true,
                    coverageComplete: true, weight: 1, critical: false),
            };
            var index = HealthIndexRules.Roll(profile, dims);

            // 60, not 80. The inapplicable dimension left the roll-up entirely.
            Assert.Equal(60, index.Score.Value, 6);
            Assert.Equal(DimensionState.NotApplicable, dims[1].State);
            Assert.Contains("not a dimension that passed", dims[1].Why);
        }

        [Fact]
        public void EVERY_DEDUCTION_NAMES_ITS_FINDING()
        {
            var d = HealthIndexRules.ScoreDimension("hygiene", new[]
            {
                new HealthDeduction { Check = "warnings", Points = 10, Why = "40 warnings against a limit of 10" },
                new HealthDeduction { Check = "imported_cad", Points = 25, Why = "3 imported CAD instances" },
            }, true, true, true, weight: 1, critical: false);

            Assert.Equal(65, d.Score.Value, 6);
            Assert.Equal(2, d.Deductions.Count);
            Assert.All(d.Deductions, x => Assert.False(string.IsNullOrWhiteSpace(x.Check)));
            Assert.All(d.Deductions, x => Assert.False(string.IsNullOrWhiteSpace(x.Why)));
        }

        [Fact]
        public void UNREADABLE_LEAVES_THE_DENOMINATOR_and_the_dimension_says_so()
        {
            var d = HealthIndexRules.ScoreDimension("hygiene", new List<HealthDeduction>(),
                applicable: true, assessable: true, coverageComplete: false, weight: 1, critical: false);
            Assert.Equal(100, d.Score.Value, 6);
            Assert.Contains("Coverage was INCOMPLETE", d.Why);
            Assert.Contains("rather than counting as problems", d.Why);
        }

        [Fact]
        public void A_score_cannot_fall_below_zero_or_rise_above_a_hundred()
        {
            var floor = HealthIndexRules.ScoreDimension("x",
                new[] { new HealthDeduction { Check = "a", Points = 500, Why = "everything" } },
                true, true, true, 1, false);
            Assert.Equal(0, floor.Score.Value, 6);

            var ceiling = HealthIndexRules.ScoreDimension("x",
                new[] { new HealthDeduction { Check = "a", Points = -50, Why = "credit" } },
                true, true, true, 1, false);
            Assert.Equal(100, ceiling.Score.Value, 6);
        }

        [Fact]
        public void Weights_actually_weight()
        {
            var profile = new HealthProfile { Id = "p", Version = "1", Context = HealthContext.Project };
            var dims = new List<HealthDimension>
            {
                HealthIndexRules.ScoreDimension("a",
                    new[] { new HealthDeduction { Check = "x", Points = 100, Why = "" } }, true, true, true, 3, false),
                HealthIndexRules.ScoreDimension("b", new List<HealthDeduction>(), true, true, true, 1, false),
            };
            // (0*3 + 100*1) / 4 = 25
            Assert.Equal(25, HealthIndexRules.Roll(profile, dims).Score.Value, 6);
        }

        [Fact]
        public void Simulation_removes_findings_and_says_that_is_all_it_did()
        {
            var profile = new HealthProfile { Id = "p", Version = "1", Context = HealthContext.Project };
            var dims = new List<HealthDimension>
            {
                HealthIndexRules.ScoreDimension("hygiene", new[]
                {
                    new HealthDeduction { Check = "warnings", Points = 30, Why = "" },
                    new HealthDeduction { Check = "imported_cad", Points = 20, Why = "" },
                }, true, true, true, 1, false),
            };
            Assert.Equal(50, HealthIndexRules.Roll(profile, dims).Score.Value, 6);

            var simulated = HealthIndexRules.Simulate(profile, dims, new HashSet<string> { "imported_cad" });
            Assert.Equal(70, simulated.Score.Value, 6);
            Assert.Contains("does not model a fix and does not predict one will work", simulated.Means);
        }

        [Fact]
        public void With_nothing_scored_there_is_no_average_to_publish()
        {
            var profile = new HealthProfile { Id = "p", Version = "1", Context = HealthContext.Project };
            var dims = new List<HealthDimension>
            {
                HealthIndexRules.ScoreDimension("a", null, applicable: false, assessable: true,
                                                coverageComplete: true, weight: 1, critical: false),
            };
            var index = HealthIndexRules.Roll(profile, dims);
            Assert.Null(index.Score);
            Assert.Contains("nothing to average", index.ScoreSuppressedBecause);
        }
    }
}
