// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// WHICH annotations take part in a view layout, said out loud.
//
// The rule this file encodes came out of a measured blockage: a Space Tag in
// the sample model had no readable extent in the view, was not hidden, and its
// category was not hidden either - so a layout that refused every unreadable
// box refused for ever, and a layout that skipped unreadable boxes would have
// claimed "collision-free" over an annotation it never measured.
//
// Neither answer is acceptable, so the classification is explicit:
//
//   measured                     -> a real obstacle; its extent was read.
//   excluded_*                   -> verifiably NOT in the view, with the probe
//                                   that decided it named in the evidence.
//   accepted_unmeasurable        -> the caller named this exact id as accepted;
//                                   clearance is then reported as partial and
//                                   never as "collision-free".
//   unknown_*                    -> could not be decided. The operation refuses
//                                   and says which ids and which probe fell
//                                   short. "I could not measure it" never
//                                   becomes "it is not there".
//
// Revit-free on purpose: the verdict arithmetic, the completeness rule and the
// refusal text are unit-tested without a Revit in the room.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Structured refusal: the coverage travels with the failure.</summary>
    public sealed class AnnotationCoverageException : InvalidOperationException
    {
        public AnnotationCoverageException(JObject coverage)
            : base(AnnotationVisibility.RefusalMessage(coverage))
        {
            Coverage = coverage;
        }

        public JObject Coverage { get; }
    }

    public static class AnnotationVisibility
    {
        public const string Schema = "horizun.annotation-coverage/1";

        public const string Measured = "measured";
        public const string ExcludedElementHidden = "excluded_element_hidden";
        public const string ExcludedCategoryHidden = "excluded_category_hidden";
        public const string ExcludedAnnotationCategoriesHidden = "excluded_annotation_categories_hidden";
        public const string ExcludedNotVisibleInView = "excluded_not_visible_in_view";
        public const string ExcludedInvalidElement = "excluded_invalid_element";
        public const string AcceptedUnmeasurable = "accepted_unmeasurable";
        public const string UnknownUnreadableExtent = "unknown_unreadable_extent";
        public const string UnknownVisibilityUndecidable = "unknown_visibility_undecidable";

        private static readonly string[] AllVerdicts =
        {
            Measured, ExcludedElementHidden, ExcludedCategoryHidden, ExcludedAnnotationCategoriesHidden,
            ExcludedNotVisibleInView, ExcludedInvalidElement, AcceptedUnmeasurable,
            UnknownUnreadableExtent, UnknownVisibilityUndecidable
        };

        public static IEnumerable<string> Verdicts { get { return AllVerdicts; } }

        public static bool IsKnownVerdict(string verdict)
        {
            return AllVerdicts.Contains(verdict);
        }

        /// <summary>A verdict that must stop the operation instead of being skipped.</summary>
        public static bool IsBlocking(string verdict)
        {
            return verdict == UnknownUnreadableExtent || verdict == UnknownVisibilityUndecidable;
        }

        /// <summary>A verdict that removes the annotation from the layout with evidence.</summary>
        public static bool IsExclusion(string verdict)
        {
            return verdict == ExcludedElementHidden || verdict == ExcludedCategoryHidden ||
                   verdict == ExcludedAnnotationCategoriesHidden || verdict == ExcludedNotVisibleInView ||
                   verdict == ExcludedInvalidElement;
        }

        public static JObject Entry(long elementId, string category, string className, long ownerViewId,
                                    string verdict, string evidence)
        {
            if (!IsKnownVerdict(verdict))
                throw new ArgumentException("Unknown annotation visibility verdict: " + verdict);
            return new JObject
            {
                ["element_id"] = elementId,
                ["category"] = string.IsNullOrEmpty(category) ? "(none)" : category,
                ["class"] = string.IsNullOrEmpty(className) ? "(unknown)" : className,
                ["owner_view_id"] = ownerViewId,
                ["verdict"] = verdict,
                ["evidence"] = evidence ?? ""
            };
        }

        /// <summary>
        /// The whole picture for one view: every annotation considered, what was
        /// decided about it, and whether the layout may claim complete clearance.
        /// </summary>
        public static JObject Coverage(long viewId, int viewScale, long primaryViewId, JObject bounds,
                                       string visibilityProbe, IEnumerable<JObject> entries)
        {
            List<JObject> rows = (entries ?? Enumerable.Empty<JObject>()).ToList();
            var counts = new JObject();
            foreach (string verdict in AllVerdicts)
                counts[verdict] = rows.Count(r => (string)r["verdict"] == verdict);
            List<JObject> blocking = rows.Where(r => IsBlocking((string)r["verdict"])).ToList();
            List<JObject> accepted = rows.Where(r => (string)r["verdict"] == AcceptedUnmeasurable).ToList();
            bool complete = blocking.Count == 0;
            return new JObject
            {
                ["schema"] = Schema,
                ["view_id"] = viewId,
                ["view_scale"] = viewScale,
                ["primary_view_id"] = primaryViewId > 0 ? (JToken)primaryViewId : JValue.CreateNull(),
                ["is_dependent_view"] = primaryViewId > 0,
                ["bounds"] = bounds ?? new JObject { ["source"] = "none" },
                ["visibility_probe"] = string.IsNullOrEmpty(visibilityProbe) ? "not_required" : visibilityProbe,
                ["considered"] = rows.Count,
                ["counts"] = counts,
                ["coverage_complete"] = complete,
                // Clearance is only "complete" when nothing was accepted unmeasured.
                // A partial scope must never be reported as collision-free.
                ["clearance_scope"] = !complete ? "undecided" : (accepted.Count == 0 ? "complete" : "partial"),
                ["accepted_unmeasurable"] = new JArray(accepted.Select(r => r["element_id"].DeepClone())),
                ["blocking"] = new JArray(blocking.Select(r => r.DeepClone())),
                ["entries"] = new JArray(rows.Select(r => r.DeepClone())),
                ["means"] = "measured entries are obstacles; excluded_* entries were removed by the named probe; " +
                            "accepted_unmeasurable entries were named by the caller and downgrade clearance to " +
                            "partial; unknown_* entries stop the operation and are never treated as absent."
            };
        }

        /// <summary>The refusal a client can act on: which ids, which probe, what to do.</summary>
        public static string RefusalMessage(JObject coverage)
        {
            if (coverage == null) return "Annotation coverage is unknown and no coverage report was produced.";
            JArray blocking = coverage["blocking"] as JArray ?? new JArray();
            long viewId = coverage.Value<long?>("view_id") ?? 0;
            if (blocking.Count == 0)
                return "Annotation coverage for view " + viewId + " reported no blocking entry.";
            IEnumerable<string> named = blocking.Take(10).Select(b =>
                b.Value<long>("element_id").ToString(CultureInfo.InvariantCulture) + " (" +
                b.Value<string>("category") + " / " + b.Value<string>("class") + ": " +
                b.Value<string>("evidence") + ")");
            string more = blocking.Count > 10
                ? " and " + (blocking.Count - 10).ToString(CultureInfo.InvariantCulture) + " more"
                : "";
            return "Annotation coverage for view " + viewId + " is incomplete: " +
                   blocking.Count.ToString(CultureInfo.InvariantCulture) +
                   " potentially visible annotation(s) could not be measured, so no layout here can be called " +
                   "collision-free. Nothing was written. Unmeasurable: " + string.Join("; ", named) + more +
                   ". Act on it by hiding or repairing the named elements in this view, or by naming those exact " +
                   "ids in layout_accept_unmeasurable - which records them as accepted and reports the resulting " +
                   "clearance as partial rather than complete.";
        }

        /// <summary>Parse and validate an explicit acceptance list; empty when absent.</summary>
        public static HashSet<long> ReadAccepted(JToken token)
        {
            var accepted = new HashSet<long>();
            if (token == null || token.Type == JTokenType.Null) return accepted;
            JArray array = token as JArray;
            if (array == null || array.Count > 200)
                throw new ArgumentException("layout_accept_unmeasurable must be an array of at most 200 element ids.");
            foreach (JToken id in array)
            {
                if (id.Type != JTokenType.Integer || (long)id <= 0)
                    throw new ArgumentException("layout_accept_unmeasurable takes positive element ids only; " +
                                                "a blanket 'ignore unknown annotations' switch does not exist.");
                accepted.Add((long)id);
            }
            return accepted;
        }
    }
}
