// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHY THIS MODEL MIGHT BE HEAVY - as a ranked set of candidates, never as an
// answer in bytes.
//
// Revit does not publish how many bytes a category contributes to an .rvt. The
// file is a compound document whose streams do not decompose along the lines
// anybody cares about, and no API returns "your in-place families cost 84 MB".
// Every tool that shows such a number computed it from a heuristic and then
// printed it without the word.
//
// So this ranks CANDIDATES, and every row carries the class of thing it is:
//
//   measured    - a count this bridge actually took, with the population it came
//                 from. "There are 412 in-place families" is measured.
//   indicator   - a measured count whose RELEVANCE to weight is a rule of thumb.
//                 In-place families are widely held to be expensive; that belief
//                 is not a measurement and is labelled.
//   hypothesis  - a statement about cause. "This model is slow BECAUSE of the
//                 groups" is a hypothesis even when the group count is enormous.
//
// A caller may sort candidates by a WEIGHT PROFILE, which is versioned, supplied
// by the caller, and reported beside the ranking. There is no built-in default,
// because a default would be one organisation's opinion compiled into a bridge
// that is meant to be neutral - and a ranking whose weights nobody can see is
// indistinguishable from a fact.
//
// THE THING THAT MUST NOT HAPPEN: a contributor nobody could count coming back
// as zero. A category that threw, a workset that was closed, a link that would
// not load - each is `not_assessable`, ranks nowhere, and says so. Zero is an
// answer; "I could not look" is a different answer, and a heavy model whose
// heaviest category was unreadable must not be reported as light.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What kind of claim a row is. Never mixed.</summary>
    public static class EvidenceClass
    {
        public const string Measured = "measured";
        public const string Indicator = "indicator";
        public const string Hypothesis = "hypothesis";

        public static readonly string[] All = { Measured, Indicator, Hypothesis };
    }

    /// <summary>Whether a contributor could be counted at all.</summary>
    public static class ContributorStatus
    {
        public const string Counted = "counted";
        public const string LowerBound = "lower_bound";
        public const string NotAssessable = "not_assessable";
        public const string NotRequested = "not_requested";

        public static readonly string[] All = { Counted, LowerBound, NotAssessable, NotRequested };
    }

    public static class WeightCodes
    {
        public const string NoProfile = "no_weight_profile";
        public const string UnknownProfileKey = "unknown_weight_key";
        public const string BadWeight = "invalid_weight";
        public const string NoProfileVersion = "weight_profile_has_no_version";
    }

    /// <summary>One thing that might make a model heavy, and what is known about it.</summary>
    public sealed class Contributor
    {
        /// <summary>Stable key, e.g. "in_place_families". Matches the profile.</summary>
        public string Kind;

        /// <summary>How many were found. Meaningless unless Status is counted/lower_bound.</summary>
        public long Count;

        /// <summary>How many elements were looked at to produce Count.</summary>
        public long Examined;

        /// <summary>How many could not be read. A non-zero value makes the count a lower bound.</summary>
        public long Unreadable;

        public string Status = ContributorStatus.Counted;

        /// <summary>Why it could not be assessed. Required when Status says so.</summary>
        public string Limitation;

        /// <summary>Element ids or names a person can go and look at.</summary>
        public List<string> Evidence = new List<string>();

        /// <summary>Measured, or a rule of thumb about relevance.</summary>
        public string Class = EvidenceClass.Measured;
    }

    public sealed class WeightProfile
    {
        public bool Ok;
        public string Code;
        public string Message;

        public string Version;
        public readonly Dictionary<string, double> Weights =
            new Dictionary<string, double>(StringComparer.Ordinal);

        public static WeightProfile Refused(string code, string message) =>
            new WeightProfile { Ok = false, Code = code, Message = message };
    }

    public sealed class RankedContributor
    {
        public string Kind;
        public long Count;
        public double Score;
        public double Weight;
        public string Class;
        public string Status;
        public string WhyItRanks;
        public string Limitation;
        public List<string> Evidence = new List<string>();
    }

    public sealed class WeightAttribution
    {
        public bool Ranked;
        public string WhyNotRanked;
        public string ProfileVersion;
        public List<RankedContributor> Candidates = new List<RankedContributor>();
        public List<RankedContributor> NotAssessable = new List<RankedContributor>();
        public List<string> Limitations = new List<string>();

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["bytes_are_not_known"] =
                    "Revit does not publish how many bytes any category contributes to the file, so nothing here " +
                    "is a size. These are CANDIDATES, ranked by a profile the caller supplied and named below.",
                ["ranked"] = Ranked,
                ["profile_version"] = ProfileVersion,
                ["candidates"] = new JArray(Candidates.Select(Row)),
                ["not_assessable"] = new JArray(NotAssessable.Select(Row)),
                ["limitations"] = new JArray(Limitations.Select(l => (JToken)l)),
            };
            if (!Ranked) o["why_not_ranked"] = WhyNotRanked;
            return o;
        }

        private static JObject Row(RankedContributor c) => new JObject
        {
            ["kind"] = c.Kind,
            ["count"] = c.Count,
            ["evidence_class"] = c.Class,
            ["status"] = c.Status,
            ["weight"] = c.Weight,
            ["score"] = c.Score,
            ["why_it_ranks"] = c.WhyItRanks,
            ["limitation"] = c.Limitation,
            ["evidence"] = new JArray(c.Evidence.Select(e => (JToken)e)),
        };
    }

    public static class WeightAttributionRules
    {
        /// <summary>
        /// Read a caller-supplied weight profile.
        ///
        /// It must carry a version. An unversioned profile produces a ranking
        /// nobody can reproduce later, and a ranking that cannot be reproduced is
        /// presented with exactly the same confidence as one that can.
        /// </summary>
        public static WeightProfile ReadProfile(JToken profile, IReadOnlyCollection<string> knownKinds)
        {
            if (profile == null || profile.Type == JTokenType.Null)
                return WeightProfile.Refused(WeightCodes.NoProfile,
                    "no weight profile was supplied, so the candidates are reported UNRANKED. There is no " +
                    "built-in default on purpose: a default would be one organisation's opinion about what makes " +
                    "a model heavy, compiled into a bridge that is meant to be neutral, and a ranking whose " +
                    "weights nobody can see reads exactly like a fact.");

            if (profile.Type != JTokenType.Object)
                return WeightProfile.Refused(WeightCodes.UnknownProfileKey,
                    "a weight profile must be an object with 'version' and 'weights'.");

            var obj = (JObject)profile;
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "version", "weights" };
            foreach (JProperty p in obj.Properties())
                if (!allowed.Contains(p.Name))
                    return WeightProfile.Refused(WeightCodes.UnknownProfileKey,
                        "'" + p.Name + "' is not part of a weight profile. It takes 'version' and 'weights'.");

            string version = obj.Value<string>("version");
            if (string.IsNullOrWhiteSpace(version))
                return WeightProfile.Refused(WeightCodes.NoProfileVersion,
                    "this weight profile has no 'version'. A ranking is only meaningful next to the rules that " +
                    "produced it, and a version is how a later reader knows whether two rankings are comparable.");

            var result = new WeightProfile { Ok = true, Version = version };

            JToken weights = obj["weights"];
            if (weights == null || weights.Type != JTokenType.Object)
                return WeightProfile.Refused(WeightCodes.UnknownProfileKey,
                    "a weight profile needs a 'weights' object mapping a contributor kind to a number.");

            var known = new HashSet<string>(knownKinds ?? new string[0], StringComparer.Ordinal);
            foreach (JProperty w in ((JObject)weights).Properties())
            {
                if (known.Count > 0 && !known.Contains(w.Name))
                    return WeightProfile.Refused(WeightCodes.UnknownProfileKey,
                        "'" + w.Name + "' is not a contributor this scan reports. The kinds are: " +
                        string.Join(", ", known.OrderBy(k => k, StringComparer.Ordinal)) + ". A weight for a kind " +
                        "that does not exist would silently do nothing.");

                if (w.Value.Type != JTokenType.Integer && w.Value.Type != JTokenType.Float)
                    return WeightProfile.Refused(WeightCodes.BadWeight,
                        "the weight for '" + w.Name + "' must be a number.");

                double v = w.Value.Value<double>();
                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0)
                    return WeightProfile.Refused(WeightCodes.BadWeight,
                        "the weight for '" + w.Name + "' is " + v.ToString(CultureInfo.InvariantCulture) +
                        ". Weights must be finite and not negative: a negative weight would let one contributor " +
                        "cancel another out and produce a model that looks lighter the more of it there is.");

                result.Weights[w.Name] = v;
            }

            return result;
        }

        /// <summary>
        /// Rank the candidates, or report them unranked with the reason.
        ///
        /// Contributors that could not be counted are separated out entirely. They
        /// do not score zero - zero would sort them last and read as "nothing
        /// here", which is the one thing they do not mean.
        /// </summary>
        public static WeightAttribution Attribute(IEnumerable<Contributor> contributors, WeightProfile profile)
        {
            var result = new WeightAttribution();
            List<Contributor> all = (contributors ?? Enumerable.Empty<Contributor>()).Where(c => c != null).ToList();

            foreach (Contributor c in all.Where(c => c.Status == ContributorStatus.NotAssessable ||
                                                     c.Status == ContributorStatus.NotRequested))
            {
                result.NotAssessable.Add(new RankedContributor
                {
                    Kind = c.Kind,
                    Count = 0,
                    Weight = 0,
                    Score = 0,
                    Class = c.Class,
                    Status = c.Status,
                    Limitation = c.Limitation,
                    Evidence = c.Evidence ?? new List<string>(),
                    WhyItRanks = c.Status == ContributorStatus.NotRequested
                        ? "this contributor was not requested, so it was not counted. That is not the same as " +
                          "there being none."
                        : "this contributor could not be counted, so it is not ranked. A count of zero would " +
                          "sort it last and read as 'nothing here', which is the one thing it does not mean.",
                });
                if (!string.IsNullOrWhiteSpace(c.Limitation)) result.Limitations.Add(c.Kind + ": " + c.Limitation);
            }

            List<Contributor> countable = all.Where(c => c.Status == ContributorStatus.Counted ||
                                                         c.Status == ContributorStatus.LowerBound).ToList();

            if (profile == null || !profile.Ok)
            {
                result.Ranked = false;
                result.WhyNotRanked = profile == null
                    ? "no weight profile was supplied."
                    : profile.Message;
                result.ProfileVersion = null;
                // Still reported, in a fixed order, so the facts are usable even
                // though the priority is not this bridge's to assert.
                result.Candidates = countable
                    .OrderBy(c => c.Kind, StringComparer.Ordinal)
                    .Select(c => new RankedContributor
                    {
                        Kind = c.Kind,
                        Count = c.Count,
                        Weight = 0,
                        Score = 0,
                        Class = c.Class,
                        Status = c.Status,
                        Evidence = c.Evidence ?? new List<string>(),
                        Limitation = c.Limitation,
                        WhyItRanks = "unranked: no weight profile was supplied, so the order here is alphabetical " +
                                     "and carries no claim about importance.",
                    })
                    .ToList();
                return result;
            }

            result.Ranked = true;
            result.ProfileVersion = profile.Version;

            result.Candidates = countable
                .Select(c =>
                {
                    double w = profile.Weights.TryGetValue(c.Kind, out double x) ? x : 0.0;
                    double score = w * c.Count;
                    string why = w <= 0
                        ? "the profile gives '" + c.Kind + "' a weight of 0, so it scores nothing however many " +
                          "there are. That is the profile's judgement, not a measurement."
                        : c.Count.ToString(CultureInfo.InvariantCulture) + " x weight " +
                          w.ToString("0.###", CultureInfo.InvariantCulture) + " = " +
                          score.ToString("0.###", CultureInfo.InvariantCulture) +
                          ", under profile '" + profile.Version + "'." +
                          (c.Status == ContributorStatus.LowerBound
                              ? " The count is a LOWER BOUND: " + c.Unreadable.ToString(CultureInfo.InvariantCulture) +
                                " of the population could not be read, so the real score is at least this."
                              : "");
                    return new RankedContributor
                    {
                        Kind = c.Kind,
                        Count = c.Count,
                        Weight = w,
                        Score = score,
                        Class = c.Class,
                        Status = c.Status,
                        Evidence = c.Evidence ?? new List<string>(),
                        Limitation = c.Limitation,
                        WhyItRanks = why,
                    };
                })
                // Descending score, then Kind ordinally so the order is total and
                // two runs over the same model agree.
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Kind, StringComparer.Ordinal)
                .ToList();

            foreach (Contributor c in countable.Where(c => c.Status == ContributorStatus.LowerBound))
                result.Limitations.Add(c.Kind + ": " + c.Unreadable.ToString(CultureInfo.InvariantCulture) +
                                       " of " + c.Examined.ToString(CultureInfo.InvariantCulture) +
                                       " could not be read, so its count is a lower bound.");

            return result;
        }
    }
}
