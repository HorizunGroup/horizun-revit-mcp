// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// horizun_get_dimension_references explain_references / dimension_id (4.2-N3, CT-P2-2f):
// WHY a reference cannot carry a dimension. The field complaint was Revit's own sentence,
// "Invalid number of references", which names nothing. The decisions live here, proved without
// a model; DimensionReferenceExplain.cs reads the facts from Revit.
//
// Every reason has a code a client can branch on, the cause in words and the next step.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class ReferenceFacts
    {
        public bool Parsed = true;
        public bool ElementExists = true;
        public bool IsLinked;
        public bool LinkInstanceExists = true;
        public bool LinkLoaded = true;
        public bool LinkedElementExists = true;
        /// <summary>The element (host, or inside the link) is a grid, level or reference plane.</summary>
        public bool IsDatum;
        public bool HiddenInView;
        public bool CategoryHiddenInView;
        /// <summary>
        /// The unit direction of the reference as it lies IN the view plane (a wall face seen edge-on is a
        /// line); null when it could not be read. <see cref="FaceOnToView"/> says why it is null when the
        /// reference is parallel to the view plane.
        /// </summary>
        public double[] InViewDirection;
        public bool FaceOnToView;
        public bool GeometryUnreadable;
    }

    public sealed class ReferenceReason
    {
        public string Code, Message, NextStep;
        public JObject ToJson() => new JObject { ["code"] = Code, ["message"] = Message, ["next_step"] = NextStep };
    }

    public static class DimensionReferenceExplainRules
    {
        public const int MaxReferences = 50;

        public const string Unparseable = "reference_unparseable";
        public const string ElementMissing = "element_missing";
        public const string LinkInstanceMissing = "link_instance_missing";
        public const string LinkUnloaded = "link_unloaded";
        public const string LinkedElementMissing = "linked_element_missing";
        public const string LinkedDatum = DimensionReferenceRules.CodeLinkedDatumRejected;
        public const string HiddenInView = "element_hidden_in_view";
        public const string CategoryHiddenInView = "category_hidden_in_view";
        public const string FaceOnToView = "reference_parallel_to_view_plane";
        public const string GeometryUnreadable = "reference_geometry_unreadable";
        public const string PairNotParallel = "pair_not_parallel";
        public const string PairSameReference = "pair_same_reference";
        public const string PairDirectionUnknown = "pair_direction_unknown";

        private static ReferenceReason R(string code, string message, string next) =>
            new ReferenceReason { Code = code, Message = message, NextStep = next };

        /// <summary>The reasons a single reference cannot carry a dimension (empty = nothing found against it).</summary>
        public static List<ReferenceReason> Explain(ReferenceFacts f)
        {
            var why = new List<ReferenceReason>();
            if (!f.Parsed)
            {
                why.Add(R(Unparseable, "The text is not a stable reference of this document (Revit could not parse it).",
                    "Take the reference from horizun_get_dimension_references or from horizun_query_dimensions; a stable reference is only valid in the document and session that produced it."));
                return why;
            }
            if (f.IsLinked)
            {
                if (!f.LinkInstanceExists)
                {
                    why.Add(R(LinkInstanceMissing, "The reference points INTO a link, but the link instance that owned it is no longer in this document.",
                        "Reload or re-place the link and read the reference again."));
                    return why;
                }
                if (!f.LinkLoaded)
                {
                    why.Add(R(LinkUnloaded, "The link is not loaded, so nothing inside it can be dimensioned or even looked at.",
                        "Load the link (horizun_manage_links (Pro edition)) and read the reference again."));
                    return why;
                }
                if (!f.LinkedElementExists)
                {
                    why.Add(R(LinkedElementMissing, "The element inside the link that this reference pointed to is gone from the linked model.",
                        "The reference is dead: take a new one from horizun_get_dimension_references linked_targets."));
                    return why;
                }
                if (f.IsDatum)
                    why.Add(R(LinkedDatum, DimensionReferenceRules.LinkedDatumRejected().Message,
                        "Dimension the linked model's walls/columns by their faces, or dimension to the host's own grids and levels. " +
                        "A grid of the link can be shown in the view (copy_link_overrides) but not measured to."));
            }
            else if (!f.ElementExists)
            {
                why.Add(R(ElementMissing, "The element this reference pointed to no longer exists in the document.",
                    "The reference is dead: the dimension that used it is broken. Take a new one from horizun_get_dimension_references and recreate the dimension with horizun_annotate."));
                return why;
            }
            if (f.HiddenInView)
                why.Add(R(HiddenInView, "The element is hidden in this view, and a dimension cannot use what the view does not draw.",
                    "Unhide it (horizun_view_visibility unhide) or dimension in a view where it is visible."));
            if (f.CategoryHiddenInView)
                why.Add(R(CategoryHiddenInView, "The element's category is turned off in this view.",
                    "Turn the category on (horizun_view_visibility unhide categories) or use another view."));
            if (f.FaceOnToView)
                why.Add(R(FaceOnToView, "The reference lies parallel to the view plane (a face seen from the front), so in this view it has no position to measure to.",
                    "Dimension in a view that cuts across it (a plan for a wall face, a section for a floor face)."));
            else if (f.GeometryUnreadable && f.InViewDirection == null)
                why.Add(R(GeometryUnreadable, "Revit would not give the geometry behind this reference, so its direction in the view is unknown.",
                    "Treat the answer for this reference as unknown, not as usable."));
            return why;
        }

        /// <summary>The reasons two references cannot be the two ends of one linear dimension.</summary>
        public static List<ReferenceReason> ExplainPair(string stableA, string stableB, double[] directionA, double[] directionB)
        {
            var why = new List<ReferenceReason>();
            if (stableA != null && string.Equals(stableA, stableB, StringComparison.Ordinal))
            {
                why.Add(R(PairSameReference, "Both ends are the same reference; a dimension needs two different ones.",
                    "Pick two different references."));
                return why;
            }
            if (directionA == null || directionB == null)
            {
                why.Add(R(PairDirectionUnknown, "The direction of at least one reference in the view is unknown, so parallelism cannot be judged.",
                    "Explain each reference alone first and fix what it names."));
                return why;
            }
            if (!Parallel(directionA, directionB))
                why.Add(R(PairNotParallel, "The two references are not parallel in this view, and a linear dimension measures between PARALLEL references.",
                    "Use an aligned dimension on an angled pair, an angular dimension for the angle between them, or pick references with the same direction."));
            return why;
        }

        /// <summary>Parallel or anti-parallel within 0.1 degree (2D or 3D vectors, unit or not).</summary>
        public static bool Parallel(double[] a, double[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            double na = Math.Sqrt(a.Sum(x => x * x)), nb = Math.Sqrt(b.Sum(x => x * x));
            if (na < 1e-12 || nb < 1e-12) return false;
            double dot = a.Zip(b, (x, y) => x * y).Sum() / (na * nb);
            return Math.Abs(dot) >= Math.Cos(0.1 * Math.PI / 180.0);
        }

        /// <summary>Argument rules: what explain mode accepts, refused before Revit is touched.</summary>
        public static string Validate(JObject request)
        {
            JToken refs = request["explain_references"], dim = request["dimension_id"];
            if (refs == null && dim == null) return null;
            if (refs != null && dim != null) return "explain_references and dimension_id are two ways to name what to explain; give one.";
            if (refs != null)
            {
                var arr = refs as JArray;
                if (arr == null || arr.Count == 0 || arr.Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t)))
                    return "explain_references must be a non-empty array of stable-reference strings.";
                if (arr.Count > MaxReferences) return "explain_references takes at most " + MaxReferences + " references per call.";
            }
            else if (dim.Type != JTokenType.Integer) return "dimension_id must be an integer element id.";
            foreach (string f in new[] { "element_ids", "linked_targets", "filter", "selectors", "reference_name", "probe_point" })
                if (request[f] != null && request[f].Type != JTokenType.Null)
                    return f + " does not apply when explaining references (explain_references / dimension_id). Nothing was inspected.";
            return null;
        }
    }
}
