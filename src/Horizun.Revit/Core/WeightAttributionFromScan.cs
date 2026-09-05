// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE ROUTE from what the scan measured to what the attribution ranks.
//
// WeightAttributionRules is arithmetic over numbers somebody else took. This is
// where those numbers come from: the sections horizun_model_scan has ALREADY
// emitted. Reading them here rather than collecting again is deliberate - a
// second collector over the same population is how two answers about one model
// begin to disagree, and the scan's counts already carry their own coverage.
//
// WHAT THIS FILE IS MOSTLY MADE OF: the ways a fact can be absent, and keeping
// them apart.
//
//   * the section was not requested        -> not_requested
//   * the section ran and threw            -> not_assessable, with its reason
//   * the section ran and the key is there -> counted
//   * the key is there but null            -> not_assessable: the collector
//                                             failed and left a null rather than
//                                             a zero, on purpose
//   * the count came from a bucket whose
//     total is a lower bound               -> lower_bound
//
// None of those is zero. A model whose heaviest population could not be read
// must not come back looking light, and that is the single property this file
// exists to keep.
//
// It is Revit-free: it takes the reply the scan built. That makes the whole
// route provable at a desk against the exact shapes the emitters produce.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Where one contributor's number is read from, and what it means.</summary>
    internal sealed class ContributorSource
    {
        public string Kind;
        public string Section;
        /// <summary>A plain integer key on the section.</summary>
        public string CountKey;
        /// <summary>Or a bucket, whose `total` is the count and whose `total_is_exact` matters.</summary>
        public string BucketKey;
        public string Category;
        public string Unit = "elements";
        public string Class = EvidenceClass.Measured;
        public string Why;
        public string Recommendation;
    }

    public static class WeightAttributionFromScan
    {
        /// <summary>
        /// Every contributor this bridge can speak about, and where each one comes
        /// from. A kind is listed here even when the scan cannot produce it, so the
        /// reply says `not_assessable` about a named thing instead of staying silent.
        /// </summary>
        private static readonly ContributorSource[] Sources =
        {
            new ContributorSource { Kind = "in_place_families", Section = "cleanliness",
                BucketKey = "families_in_place", Category = "Families",
                Class = EvidenceClass.Indicator,
                Why = "an in-place family is unique to its host model and cannot be reused or purged like a " +
                      "loadable one. That it tends to cost more is a rule of thumb, not a measurement.",
                Recommendation = "convert repeated in-place geometry to a loadable family" },

            new ContributorSource { Kind = "types_without_instances", Section = "types",
                CountKey = "family_symbols_no_instances", Category = "Types",
                Why = "a type nobody placed still travels in the file and in every schedule that lists types.",
                Recommendation = "purge unused types" },

            new ContributorSource { Kind = "group_types", Section = "cleanliness",
                CountKey = "group_types_total", Category = "Groups",
                Class = EvidenceClass.Indicator,
                Why = "group types multiply what the file stores.",
                Recommendation = "review whether variants could be one type" },

            new ContributorSource { Kind = "group_types_orphan", Section = "cleanliness",
                BucketKey = "group_types_orphan", Category = "Groups",
                Why = "a group type with no instance is stored and drawn by nothing.",
                Recommendation = "purge orphan group types" },

            new ContributorSource { Kind = "nested_groups", Section = "cleanliness",
                CountKey = "group_instances_nested", Category = "Groups",
                Class = EvidenceClass.Indicator,
                Why = "a group inside a group is widely held to be expensive to regenerate. Held, not measured.",
                Recommendation = "flatten nested groups where the nesting earns nothing" },

            new ContributorSource { Kind = "imported_cad", Section = "cleanliness",
                BucketKey = "cad_imported", Category = "CAD",
                Why = "an IMPORT embeds the drawing in this file, unlike a link.",
                Recommendation = "replace imports with links" },

            new ContributorSource { Kind = "linked_cad", Section = "cleanliness",
                BucketKey = "cad_linked", Category = "CAD",
                Why = "a linked drawing is not embedded, but each instance is still loaded and drawn.",
                Recommendation = "unload links that are not being used" },

            new ContributorSource { Kind = "raster_images", Section = "cleanliness",
                CountKey = "raster_images", Category = "Images",
                Why = "raster images are stored in the file at their full resolution.",
                Recommendation = "remove images that are no longer referenced" },

            new ContributorSource { Kind = "point_clouds", Section = "cleanliness",
                CountKey = "point_clouds", Category = "Point clouds",
                Class = EvidenceClass.Indicator,
                Why = "the cloud itself lives outside the file; the instance and its indexing do not.",
                Recommendation = "unload point clouds that are not in use" },

            new ContributorSource { Kind = "model_lines", Section = "lines",
                CountKey = "model_lines", Category = "Lines",
                Why = "model lines are real elements and are regenerated with the model.",
                Recommendation = "remove stray model lines" },

            new ContributorSource { Kind = "imported_line_patterns", Section = "cleanliness",
                CountKey = "line_patterns_import", Category = "Patterns",
                Why = "patterns arriving with an import accumulate and are rarely removed.",
                Recommendation = "purge imported patterns" },

            new ContributorSource { Kind = "imported_fill_patterns", Section = "cleanliness",
                CountKey = "fill_patterns_import", Category = "Patterns",
                Why = "as above, for fill patterns.",
                Recommendation = "purge imported patterns" },

            new ContributorSource { Kind = "revit_link_instances", Section = "links",
                CountKey = "rvt_link_instances", Category = "Links",
                Class = EvidenceClass.Indicator,
                Why = "each loaded link instance is opened and drawn alongside this model.",
                Recommendation = "unload links not needed for the current task" },

            new ContributorSource { Kind = "view_templates", Section = "cleanliness",
                CountKey = "view_templates_total", Category = "Views",
                Why = "templates are cheap individually; the count is here because it is asked for.",
                Recommendation = "consolidate near-identical templates" },

            new ContributorSource { Kind = "view_templates_unused", Section = "cleanliness",
                BucketKey = "view_templates_unused", Category = "Views",
                Why = "a template no view uses is stored and applied by nothing.",
                Recommendation = "delete unused templates" },

            new ContributorSource { Kind = "filters_unused", Section = "cleanliness",
                BucketKey = "filters_unused", Category = "Views",
                Why = "a filter no view uses is still evaluated when views are opened in some versions.",
                Recommendation = "delete unused filters" },

            new ContributorSource { Kind = "views_not_on_sheet", Section = "documentation",
                BucketKey = "views_not_on_sheet", Category = "Views",
                Class = EvidenceClass.Indicator,
                Why = "a view on no sheet is not automatically waste - working views are legitimate - so this " +
                      "is an indicator to look at, not a fault.",
                Recommendation = "review working views against what the deliverable needs" },

            new ContributorSource { Kind = "warnings", Section = "health",
                CountKey = "warnings_total", Category = "Warnings",
                Class = EvidenceClass.Indicator,
                Why = "Revit re-evaluates outstanding warnings; a large backlog is associated with slower " +
                      "regeneration. Associated, not measured here.",
                Recommendation = "work the warning list down by type, largest first" },

            new ContributorSource { Kind = "user_worksets", Section = "worksets",
                CountKey = "user_worksets", Category = "Worksharing",
                Why = "worksets themselves are cheap; the number is reported because it is asked for.",
                Recommendation = "no action implied by the count alone" },

            new ContributorSource { Kind = "design_options", Section = "design_options",
                BucketKey = "design_options", Category = "Design options",
                Class = EvidenceClass.Indicator,
                Why = "every option's content is stored, whether or not it is the active one.",
                Recommendation = "accept or delete options that are settled" },

            new ContributorSource { Kind = "mep_without_system", Section = "cleanliness",
                CountKey = "mep_curves_without_system", Category = "MEP",
                Why = "MEP geometry with no system is drawn but participates in nothing.",
                Recommendation = "connect it or remove it" },

            new ContributorSource { Kind = "dominant_categories", Section = "categories",
                BucketKey = "by_category", Category = "Categories",
                Unit = "categories",
                Class = EvidenceClass.Indicator,
                Why = "the number of distinct categories present. What matters is usually WHICH one dominates, " +
                      "and that is in the section's own bucket rather than in this number.",
                Recommendation = "read categories.by_category for the distribution" },
        };

        public static IReadOnlyCollection<string> Kinds => Sources.Select(s => s.Kind).ToList();

        /// <summary>
        /// Build the contributor list from the scan's own sections.
        ///
        /// `requested` is what the caller asked the scan to run. A section that was
        /// not requested yields `not_requested` - which is not the same answer as a
        /// section that ran and found nothing, and must never be shown as zero.
        /// </summary>
        public static List<Contributor> Build(JObject sections, IReadOnlyCollection<string> requested)
        {
            var list = new List<Contributor>();
            var asked = new HashSet<string>(requested ?? new string[0], StringComparer.OrdinalIgnoreCase);

            foreach (ContributorSource s in Sources)
            {
                var c = new Contributor { Kind = s.Kind, Class = s.Class };

                if (requested != null && !asked.Contains(s.Section))
                {
                    c.Status = ContributorStatus.NotRequested;
                    c.Limitation = "section '" + s.Section + "' was not requested, so this was never counted. " +
                                   "That is not the same as there being none.";
                    list.Add(c);
                    continue;
                }

                JToken section = sections?[s.Section];
                if (section == null || section.Type != JTokenType.Object)
                {
                    c.Status = ContributorStatus.NotAssessable;
                    c.Limitation = "section '" + s.Section + "' is not in the reply.";
                    list.Add(c);
                    continue;
                }

                var so = (JObject)section;
                string status = so.Value<string>("status");
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) && status != null)
                {
                    c.Status = ContributorStatus.NotAssessable;
                    c.Limitation = "section '" + s.Section + "' reports status '" + status + "'" +
                                   (so["reason"] != null ? ": " + so.Value<string>("reason") : "") +
                                   ", so nothing it would have counted is known.";
                    list.Add(c);
                    continue;
                }

                if (s.BucketKey != null) ReadBucket(c, so, s);
                else ReadCount(c, so, s);

                list.Add(c);
            }

            return list;
        }

        private static void ReadCount(Contributor c, JObject section, ContributorSource s)
        {
            JToken v = section[s.CountKey];
            if (v == null)
            {
                c.Status = ContributorStatus.NotAssessable;
                c.Limitation = "'" + s.Section + "." + s.CountKey + "' is not in the reply, so this bridge " +
                               "does not measure it yet.";
                return;
            }
            if (v.Type == JTokenType.Null)
            {
                // The emitter leaves null on purpose when its collector threw.
                c.Status = ContributorStatus.NotAssessable;
                string err = section.Value<string>(s.CountKey + "_error");
                c.Limitation = "'" + s.Section + "." + s.CountKey + "' is null: the collector failed" +
                               (err == null ? "" : " (" + err + ")") + ", and a failure is not a zero.";
                return;
            }
            if (v.Type != JTokenType.Integer)
            {
                c.Status = ContributorStatus.NotAssessable;
                c.Limitation = "'" + s.Section + "." + s.CountKey + "' is a " +
                               v.Type.ToString().ToLowerInvariant() + ", not a count.";
                return;
            }

            c.Count = v.Value<long>();
            c.Examined = c.Count;
            c.Status = ContributorStatus.Counted;
        }

        private static void ReadBucket(Contributor c, JObject section, ContributorSource s)
        {
            JToken b = section[s.BucketKey];
            if (b == null || b.Type != JTokenType.Object)
            {
                c.Status = ContributorStatus.NotAssessable;
                c.Limitation = "bucket '" + s.Section + "." + s.BucketKey + "' is not in the reply.";
                return;
            }

            var bo = (JObject)b;
            JToken total = bo["total"];
            if (total == null || total.Type != JTokenType.Integer)
            {
                c.Status = ContributorStatus.NotAssessable;
                c.Limitation = "bucket '" + s.Section + "." + s.BucketKey + "' reports no usable total" +
                               (bo["total_unknown_reason"] != null
                                   ? ": " + bo.Value<string>("total_unknown_reason") : "") + ".";
                return;
            }

            c.Count = total.Value<long>();
            c.Examined = c.Count;

            // A TRUNCATED PAGE IS STILL AN EXACT TOTAL. `returned` is what the caller
            // received; `total` is the population, and the two are deliberately
            // different numbers. Only total_is_exact:false makes the count a bound.
            // ABSENT IS NOT EXACT. This read `== null || value`, so a bucket that
            // never established its readability was ranked Counted - and since the
            // producer could not emit anything else, there was no value that meant
            // "I do not know". Unknown is not a pass here either: a bucket that did
            // not say is a LOWER BOUND, which is the safe direction, because only
            // the other one can produce a false clean.
            bool exact = bo["total_is_exact"] != null && bo.Value<bool>("total_is_exact");
            if (exact)
            {
                c.Status = ContributorStatus.Counted;
            }
            else
            {
                c.Status = ContributorStatus.LowerBound;
                c.Unreadable = bo["unreadable"] != null && bo["unreadable"].Type == JTokenType.Integer
                    ? bo.Value<long>("unreadable") : 0;
                c.Limitation = bo.Value<string>("total_note") ??
                               "part of this population could not be read, so the count is a lower bound.";
            }

            // Evidence a person can go and look at, from the page the caller has.
            JToken items = bo["items"];
            if (items is JArray arr)
                foreach (JToken it in arr.Take(10))
                {
                    string id = it is JObject io2 ? (io2.Value<string>("id") ?? io2.Value<string>("name")) : it.ToString();
                    if (!string.IsNullOrEmpty(id)) c.Evidence.Add(id);
                }
        }

        /// <summary>
        /// The reply. Every field the brief asks each candidate to carry, filled from
        /// the source table and the ranking - and `profile` / `profile_version`
        /// present even when there is no profile, because their ABSENCE is the thing
        /// a reader most needs to see.
        /// </summary>
        public static JObject ToJson(WeightAttribution attribution, IReadOnlyList<Contributor> built)
        {
            var byKind = Sources.ToDictionary(s => s.Kind, StringComparer.Ordinal);
            var facts = built.ToDictionary(c => c.Kind, StringComparer.Ordinal);

            JObject Row(RankedContributor r)
            {
                byKind.TryGetValue(r.Kind, out ContributorSource s);
                facts.TryGetValue(r.Kind, out Contributor f);
                return new JObject
                {
                    ["id"] = r.Kind,
                    ["name"] = r.Kind.Replace('_', ' '),
                    ["category"] = s?.Category,
                    ["evidence_class"] = r.Class,
                    ["observed_value"] = r.Count,
                    ["unit"] = s?.Unit ?? "elements",
                    ["examined_count"] = f?.Examined ?? 0,
                    ["total_count"] = r.Count,
                    ["total_is_exact"] = r.Status == ContributorStatus.Counted,
                    ["status"] = r.Status,
                    ["coverage"] = r.Status == ContributorStatus.Counted ? "complete"
                        : r.Status == ContributorStatus.LowerBound ? "partial" : "none",
                    ["confidence"] = r.Class == EvidenceClass.Measured && r.Status == ContributorStatus.Counted
                        ? "high"
                        : r.Status == ContributorStatus.Counted ? "medium" : "low",
                    ["evidence"] = new JArray((r.Evidence ?? new List<string>()).Select(e => (JToken)e)),
                    ["explanation"] = s?.Why,
                    ["limitations"] = r.Limitation,
                    ["recommendation"] = s?.Recommendation,
                    ["ranking_contribution"] = r.Score,
                    ["weight"] = r.Weight,
                    ["why_it_ranks"] = r.WhyItRanks,
                };
            }

            return new JObject
            {
                ["status"] = "ok",
                ["bytes_are_not_known"] =
                    "Revit does not publish how many bytes any category contributes to the file, so nothing " +
                    "here is a size or a percentage of one. These are CANDIDATES.",
                ["ranked"] = attribution.Ranked,
                ["why_not_ranked"] = attribution.Ranked ? null : attribution.WhyNotRanked,
                ["profile"] = attribution.ProfileVersion == null ? null : "caller-supplied",
                ["profile_version"] = attribution.ProfileVersion,
                ["candidates"] = new JArray(attribution.Candidates.Select(Row)),
                ["not_assessable"] = new JArray(attribution.NotAssessable.Select(Row)),
                ["limitations"] = new JArray(attribution.Limitations.Select(l => (JToken)l)),
            };
        }
    }
}
