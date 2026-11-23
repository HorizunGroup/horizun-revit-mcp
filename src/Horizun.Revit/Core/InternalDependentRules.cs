// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// LP-P2-10: WHAT BELONGS TO THE ELEMENT YOU ARE DELETING IS NOT SOMEBODY ELSE'S.
//
// In a workshared model a delete is refused when it would take with it elements that are
// not ours (allow_foreign_dependents opens that). Deleting one filled region of our own
// counted 64 dependents as foreign: its sketch, its sketch lines and the dimensions that
// constrain those lines. None of those has an author of its own - they exist because the
// region does - so the refusal pointed at nobody and sent the caller to open the very
// switch that is meant for other people's work.
//
// The rule is deliberately narrow, because the cost of calling something internal that is
// not is a delete over another user's work:
//   * a Sketch element, or a curve of a sketch-lines category, is internal ONLY when its owner is a target (INT-6 m-8):
//     a sketch's owner is the element it belongs to, a sketch line's owner is its sketch (itself internal). A sketch part
//     whose owner is unknown, or is some other element, goes through the ownership evidence like anything else;
//   * a Dimension is internal only when EVERY element it references is a target or one of
//     those sketch parts. A dimension that also reaches a wall nobody here owns is not.
// Everything else keeps going through the ownership evidence (mark, session, creator).
//
// Revit-free: the command reads class and category names and the dimension's references,
// and this decides. Unknown is never internal.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class InternalDependentRules
    {
        public const string SketchPart = "internal_sketch";
        public const string ConstrainingDimension = "internal_dimension";

        private static readonly HashSet<string> SketchCategories = new HashSet<string>(StringComparer.Ordinal)
        {
            "OST_Sketch", "OST_SketchLines"
        };

        /// <summary>True for a sketch or a curve belonging to a sketch.</summary>
        public static bool IsSketchPart(string className, string builtInCategory)
        {
            if (string.Equals(className, "Sketch", StringComparison.Ordinal)) return true;
            return builtInCategory != null && SketchCategories.Contains(builtInCategory);
        }

        /// <summary>
        /// null = not internal (ask the ownership evidence). Otherwise the reason it is internal.
        /// <paramref name="referencedIds"/> is the dimension's references (null for a non-dimension);
        /// <paramref name="targetIds"/> the elements being deleted; <paramref name="sketchPartIds"/> the
        /// dependents already classified as sketch parts. <paramref name="ownerId"/> is who owns a sketch or a sketch line
        /// (the element the sketch belongs to; the sketch for a line): a sketch part counts as internal only when that owner is
        /// a target or an internal sketch part. Null/unknown owner: not internal.
        /// </summary>
        public static string Classify(string className, string builtInCategory, bool isDimension,
                                      IReadOnlyCollection<long> referencedIds,
                                      ISet<long> targetIds, ISet<long> sketchPartIds, long? ownerId = null, bool provenSketchCurve = false)
        {
            if (!isDimension)
            {
                if ((!IsSketchPart(className, builtInCategory) && !provenSketchCurve) || !ownerId.HasValue) return null;
                return (targetIds != null && targetIds.Contains(ownerId.Value)) || (sketchPartIds != null && sketchPartIds.Contains(ownerId.Value))
                    ? SketchPart : null;
            }
            if (referencedIds == null || referencedIds.Count == 0) return null;   // no references read: unknown
            targetIds = targetIds ?? new HashSet<long>();
            sketchPartIds = sketchPartIds ?? new HashSet<long>();
            return referencedIds.All(id => targetIds.Contains(id) || sketchPartIds.Contains(id)) ? ConstrainingDimension : null;
        }

        /// <summary>
        /// Revit's automatic sketch dimensions (OST_WeakDims) are made by Revit for a sketch and nobody authors them; their
        /// references are not readable through the API. One is internal ONLY when what took it with it in the cascade (its
        /// parent) is a target or an internal sketch part. Any other parent, or none, goes through the ownership evidence.
        /// </summary>
        public static bool IsInternalAutomaticSketchDimension(string builtInCategory, long? cascadedBy, ISet<long> targetIds, ISet<long> sketchPartIds)
        {
            if (!string.Equals(builtInCategory, "OST_WeakDims", StringComparison.Ordinal) || !cascadedBy.HasValue) return false;
            return (targetIds != null && targetIds.Contains(cascadedBy.Value)) || (sketchPartIds != null && sketchPartIds.Contains(cascadedBy.Value));
        }

        public static string Means =>
            "internal dependents are the sketch, sketch lines and the dimensions that reference only the deleted element and its " +
            "sketch, and Revit's automatic sketch dimensions taken by them: they exist because the element does and have no author of their own, so they never need allow_foreign_dependents.";
    }
}
