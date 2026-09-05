// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A HEALTH INDEX, AND THE FIVE RULES THAT STOP IT BEING A LIE.
//
// One number for a model is the most requested and least defensible thing in
// this whole programme. "87%" is memorable, travels well, gets put on a slide,
// and answers no question anybody can act on. These rules exist so the number
// that comes out of here is worth having:
//
//  1. WEIGHTS ARE DECLARED, and the profile is versioned. Two scores are only
//     comparable under the same profile, and a score whose weights nobody can
//     see is a preference presented as a measurement.
//
//  2. NO GLOBAL SCORE WHEN A CRITICAL SECTION FAILED. If the coordinates check
//     died, the model's coordinates are UNKNOWN - and averaging the sections
//     that did run produces a confident number about a model nobody finished
//     looking at.
//
//  3. EVERY DEDUCTION NAMES ITS FINDING. A score a reader cannot take apart is
//     one they cannot act on, and a dimension that lost fourteen points without
//     saying why is an opinion.
//
//  4. UNREADABLE IS NOT NON-COMPLIANT. A check that could not read forty
//     elements has not found forty problems. Those elements leave the
//     denominator, and the dimension says its coverage was incomplete.
//
//  5. NOT APPLICABLE IS NOT A PASS. A model with no MEP is not a model with
//     perfect MEP. It scores nothing and is excluded from the roll-up, rather
//     than contributing a free hundred.
//
// The profile arrives as an argument. No weighting is compiled in, because a
// weighting is an organisation's opinion about what matters.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class HealthContext
    {
        public const string Project = "project";
        public const string Template = "template";
        public const string Family = "family";
        public const string Coordination = "coordination";
        public static readonly string[] All = { Project, Template, Family, Coordination };
    }

    public static class DimensionState
    {
        public const string Scored = "scored";
        public const string NotApplicable = "not_applicable";
        public const string NotAssessable = "not_assessable";
    }

    /// <summary>One dimension's declared weight and what it is allowed to do to the total.</summary>
    public sealed class HealthWeight
    {
        public string Dimension;
        public double Weight;
        /// <summary>When true, this dimension failing to run suppresses the global score entirely.</summary>
        public bool Critical;
    }

    public sealed class HealthProfile
    {
        public string Id;
        public string Version;
        public string Context = HealthContext.Project;
        public List<HealthWeight> Weights = new List<HealthWeight>();
    }

    /// <summary>What one dimension contributed, and exactly why.</summary>
    public sealed class HealthDeduction
    {
        public string Check;
        public double Points;
        public string Why;
    }

    public sealed class HealthDimension
    {
        public string Dimension;
        public string State;
        public double? Score;              // 0..100, null unless Scored
        public double Weight;
        public bool Critical;
        public bool CoverageComplete;
        public string Why;
        public List<HealthDeduction> Deductions = new List<HealthDeduction>();
    }

    public sealed class HealthIndex
    {
        public string ProfileId;
        public string ProfileVersion;
        public string Context;
        public double? Score;              // null when suppressed
        public string ScoreSuppressedBecause;
        public List<HealthDimension> Dimensions = new List<HealthDimension>();
        public string Means;

        /// <summary>
        /// The share of the profile's WEIGHT that actually produced a score, 0..1.
        /// A score of 92 over a fifth of the weight is a different claim from a
        /// score of 92 over all of it, and the number alone cannot tell them apart.
        /// </summary>
        public double? AssessedWeightShare;

        /// <summary>
        /// What the score could be once the unassessed dimensions are known: the
        /// worst case treats every one of them as 0, the best case as 100. When
        /// coverage is complete the range collapses onto the score.
        ///
        /// This is what stops an incomplete run from reporting 100/100 - the
        /// headline becomes "somewhere between 38 and 100", which is the truth.
        /// </summary>
        public double? PlausibleLow;
        public double? PlausibleHigh;

        /// <summary>Dimensions that could not be measured, named rather than counted.</summary>
        public List<string> Unassessed = new List<string>();
    }

    public static class HealthIndexRules
    {
        public const string CodeNoWeights = "profile_declares_no_weights";
        public const string CodeUnknownContext = "context_not_in_vocabulary";
        public const string CodeBadWeight = "weight_is_not_a_positive_number";
        public const string CodeDuplicateDimension = "dimension_weighted_twice";

        /// <summary>A non-null return is the refusal; nothing is scored.</summary>
        public static string ValidateProfile(HealthProfile profile, out List<string> codes)
        {
            codes = new List<string>();
            if (profile == null || profile.Weights == null || profile.Weights.Count == 0)
            {
                codes.Add(CodeNoWeights);
                return "a health profile must declare at least one weighted dimension. Nothing is compiled in: " +
                       "a weighting is an opinion about what matters, and this bridge does not hold one.";
            }
            if (Array.IndexOf(HealthContext.All, profile.Context ?? "") < 0)
            {
                codes.Add(CodeUnknownContext);
                return "context '" + profile.Context + "' is not one of: " + string.Join(", ", HealthContext.All) +
                       ". A template is not a project and must not be scored as one.";
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (HealthWeight w in profile.Weights)
            {
                if (w == null || string.IsNullOrWhiteSpace(w.Dimension))
                {
                    codes.Add(CodeDuplicateDimension);
                    return "every weight must name a dimension.";
                }
                if (!seen.Add(w.Dimension))
                {
                    codes.Add(CodeDuplicateDimension);
                    return "dimension '" + w.Dimension + "' is weighted twice.";
                }
                if (double.IsNaN(w.Weight) || double.IsInfinity(w.Weight) || w.Weight <= 0)
                {
                    codes.Add(CodeBadWeight);
                    return "dimension '" + w.Dimension + "' has weight '" + w.Weight +
                           "'. A weight must be a positive number; a dimension that should not count is left " +
                           "out of the profile, which is visible, rather than weighted zero, which is not.";
                }
            }
            return null;
        }

        /// <summary>
        /// Score one dimension from its deductions. The dimension starts at 100 and
        /// every deduction names the finding that caused it - rule 3.
        /// </summary>
        public static HealthDimension ScoreDimension(string dimension, IEnumerable<HealthDeduction> deductions,
                                                     bool applicable, bool assessable, bool coverageComplete,
                                                     double weight, bool critical)
        {
            var d = new HealthDimension
            {
                Dimension = dimension, Weight = weight, Critical = critical, CoverageComplete = coverageComplete
            };

            if (!applicable)
            {
                // RULE 5. A model with no MEP is not a model with perfect MEP.
                d.State = DimensionState.NotApplicable;
                d.Why = "nothing in this model is in scope for this dimension. It scores nothing and is left " +
                        "out of the roll-up entirely - a dimension that does not apply is not a dimension that " +
                        "passed.";
                return d;
            }
            if (!assessable)
            {
                // RULE 2's input. Whether it suppresses the whole score depends on
                // whether the profile called it critical.
                d.State = DimensionState.NotAssessable;
                d.Why = "this dimension could not be measured this run, so nothing is known about it. An " +
                        "unmeasured dimension is not a clean one.";
                return d;
            }

            double score = 100.0;
            foreach (HealthDeduction ded in deductions ?? new List<HealthDeduction>())
            {
                if (ded == null) continue;
                d.Deductions.Add(ded);
                score -= ded.Points;
            }
            if (score < 0) score = 0;
            if (score > 100) score = 100;

            d.State = DimensionState.Scored;
            d.Score = Math.Round(score, 2);
            d.Why = d.Deductions.Count == 0
                ? "no finding in this dimension."
                : (d.Deductions.Count + " deduction(s), each naming the finding that caused it.");
            if (!coverageComplete)
                // RULE 4.
                d.Why += " Coverage was INCOMPLETE: the elements that could not be read left the denominator " +
                         "rather than counting as problems, so this score is about what was readable.";
            return d;
        }

        /// <summary>
        /// Roll up. Returns null for the global score, with a reason, whenever a
        /// critical dimension could not be measured - rule 2.
        /// </summary>
        public static HealthIndex Roll(HealthProfile profile, IEnumerable<HealthDimension> dimensions)
        {
            var index = new HealthIndex
            {
                ProfileId = profile == null ? null : profile.Id,
                ProfileVersion = profile == null ? null : profile.Version,
                Context = profile == null ? null : profile.Context,
                Means = Means
            };
            foreach (HealthDimension d in dimensions ?? new List<HealthDimension>())
                if (d != null) index.Dimensions.Add(d);

            var blocking = new List<string>();
            foreach (HealthDimension d in index.Dimensions)
                if (d.Critical && d.State == DimensionState.NotAssessable) blocking.Add(d.Dimension);

            if (blocking.Count > 0)
            {
                index.Score = null;
                index.ScoreSuppressedBecause =
                    "no global score is published because " + string.Join(", ", blocking) +
                    " could not be measured, and " +
                    (blocking.Count == 1 ? "it is" : "they are") + " marked critical in this profile. Averaging " +
                    "the dimensions that DID run would produce a confident number about a model nobody " +
                    "finished looking at. The dimension scores below stand on their own.";
                return index;
            }

            double weighted = 0, totalWeight = 0;
            foreach (HealthDimension d in index.Dimensions)
            {
                if (d.State != DimensionState.Scored || !d.Score.HasValue) continue;
                weighted += d.Score.Value * d.Weight;
                totalWeight += d.Weight;
            }

            if (totalWeight <= 0)
            {
                index.Score = null;
                index.ScoreSuppressedBecause =
                    "not one weighted dimension produced a score, so there is nothing to average.";
                return index;
            }
            // COVERAGE, AND WHAT IT DOES TO THE HEADLINE.
            double allWeight = 0;
            foreach (HealthDimension d in index.Dimensions)
            {
                allWeight += d.Weight;
                if (d.State != DimensionState.Scored || !d.Score.HasValue) index.Unassessed.Add(d.Dimension);
            }

            index.AssessedWeightShare = allWeight > 0 ? Math.Round(totalWeight / allWeight, 4) : (double?)null;

            double point = weighted / totalWeight;
            double missing = allWeight - totalWeight;
            if (missing < 0) missing = 0;

            // The worst case gives every unmeasured dimension 0 and the best gives
            // it 100. With full coverage the two collapse onto the score itself.
            index.PlausibleLow = allWeight > 0 ? Math.Round(weighted / allWeight, 2) : (double?)null;
            index.PlausibleHigh = allWeight > 0
                ? Math.Round((weighted + missing * 100.0) / allWeight, 2) : (double?)null;

            // A MAJORITY UNMEASURED CANNOT PRODUCE A SCORE. Averaging the minority
            // that ran yields a confident number about a model mostly unexamined,
            // and 100/100 is the version of that which does the most damage.
            if (index.AssessedWeightShare.HasValue && index.AssessedWeightShare.Value < 0.5)
            {
                index.Score = null;
                index.ScoreSuppressedBecause =
                    "only " + Math.Round(index.AssessedWeightShare.Value * 100.0, 1) + "% of this profile's " +
                    "weight produced a score, so most of the model was not assessed. A single number over the " +
                    "minority that ran would be read as a verdict on the whole. The plausible range and the " +
                    "dimension scores below stand on their own.";
                return index;
            }

            index.Score = Math.Round(point, 2);
            return index;
        }

        /// <summary>
        /// What the score WOULD be if a named set of findings were fixed. This is the
        /// only form of prediction here, and it is honest because it re-runs the same
        /// arithmetic over the same deductions with some of them removed - it does
        /// not model the fix, it removes the finding.
        /// </summary>
        public static HealthIndex Simulate(HealthProfile profile, IEnumerable<HealthDimension> dimensions,
                                           ICollection<string> checksFixed)
        {
            var adjusted = new List<HealthDimension>();
            foreach (HealthDimension d in dimensions ?? new List<HealthDimension>())
            {
                if (d == null) continue;
                if (d.State != DimensionState.Scored) { adjusted.Add(d); continue; }

                var kept = new List<HealthDeduction>();
                foreach (HealthDeduction ded in d.Deductions)
                    if (checksFixed == null || !checksFixed.Contains(ded.Check)) kept.Add(ded);

                adjusted.Add(ScoreDimension(d.Dimension, kept, applicable: true, assessable: true,
                                            coverageComplete: d.CoverageComplete, weight: d.Weight,
                                            critical: d.Critical));
            }
            HealthIndex simulated = Roll(profile, adjusted);
            simulated.Means = "SIMULATED: the same arithmetic with the named findings removed from the " +
                              "deductions. It does not model a fix and does not predict one will work - it " +
                              "answers what this score would have been had those findings not been there.";
            return simulated;
        }

        public const string Means =
            "one number per dimension under a DECLARED, versioned profile, and a global score only when every " +
            "critical dimension was actually measured. Two scores are comparable only under the same profile " +
            "id and version. Unreadable elements leave the denominator rather than counting as problems, and a " +
            "dimension that does not apply scores nothing rather than a free hundred.";
    }
}
