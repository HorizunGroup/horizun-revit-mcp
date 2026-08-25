// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE RULES OF EDITING A DIMENSION, without a Revit in the room.
//
// A dimension is two different objects wearing one class. A single-segment
// dimension carries its overrides (prefix, suffix, value override, lock) on the
// element itself; a multi-segment dimension carries them on each DimensionSegment
// and throws when the element-level properties are touched. The EQ toggle exists
// only when there are segments to equalise. Get the split wrong and the failure
// is not a crash: Revit accepts some of the writes, ignores or throws on others,
// and a batch reports work it half did.
//
// So the split is a TABLE, here, where every row can be proved - which fields
// demand a single segment, which demand several, which do not care - and both
// the query and the edit command read the same table. The same file owns the
// other decisions that are arithmetic rather than API: how a shape name is
// classified for the wire, how a segment index is judged against the count, what
// "value_override: ''" means (REMOVE the override - Revit stores no override as
// an empty string, so writing one back is the deletion), the canonical 0.1 mm
// rounding used for before-values (Revit's own regeneration jitters the last
// digits of a coordinate, and a fingerprint that changes on its own would refuse
// every apply), the exact-move comparison with its declared tolerance, and the
// terminal-state matrix that turns a TransactionStatus plus a verification
// verdict into the one word a caller may branch on.
//
// Revit-free on purpose: no `using Autodesk`. The facts need Revit; these
// decisions do not, and the states that matter most - a rollback that returned
// Pending, a commit whose re-read disagrees - are exactly the ones a live Revit
// will not produce on demand.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Horizun.Revit.Core
{
    public static class DimensionEditRules
    {
        // ---------------------------------------------------------------------
        // Shapes on the wire.
        // ---------------------------------------------------------------------

        /// <summary>
        /// The closed set of shape names a caller may filter by. Spot dimensions are
        /// three shapes out here even though Revit reports all of them as
        /// DimensionShape.Spot, because "an elevation marker" and "a slope marker" are
        /// different questions and the style type tells them apart.
        /// </summary>
        public static readonly string[] KnownShapes =
        {
            "linear", "angular", "radial", "diameter", "arc_length",
            "spot_elevation", "spot_coordinate", "spot_slope"
        };

        /// <summary>One sentence naming the whole set, for every refusal that needs it.</summary>
        public static string KnownShapesSentence()
            => string.Join(", ", KnownShapes);

        /// <summary>
        /// Validate a requested shape filter. Unknown names are refused NAMING the known
        /// set - a filter that silently matched nothing would read exactly like a model
        /// with no such dimensions. Names are case-insensitive; duplicates collapse.
        /// </summary>
        public static bool TryParseShapes(IEnumerable<string> requested, out HashSet<string> shapes,
                                          out string error)
        {
            shapes = new HashSet<string>(StringComparer.Ordinal);
            error = null;
            if (requested == null) return true;
            var known = new HashSet<string>(KnownShapes, StringComparer.Ordinal);
            foreach (string raw in requested)
            {
                string s = (raw ?? "").Trim().ToLowerInvariant();
                if (!known.Contains(s))
                {
                    shapes = null;
                    error = "shapes entry '" + (raw ?? "(null)") + "' is not a known dimension shape. Known: " +
                            KnownShapesSentence() + ".";
                    return false;
                }
                shapes.Add(s);
            }
            return true;
        }

        /// <summary>
        /// Classify a dimension for the wire from the two names Revit reports:
        /// Dimension.DimensionShape and DimensionType.StyleType, both as ToString().
        /// Names rather than enum members ON PURPOSE - the classification then compiles
        /// identically on every Revit year, and a member this year does not have cannot
        /// break the build for the years that do.
        ///
        /// Returns null when the combination is not one this table recognises; the
        /// caller reports "unknown" WITH the raw names, never a guess.
        /// </summary>
        public static string ClassifyShape(string dimensionShapeName, string styleTypeName)
        {
            switch (dimensionShapeName)
            {
                case "Linear": return "linear";
                case "Angular": return "angular";
                case "Radial": return "radial";
                case "Diameter": return "diameter";
                case "ArcLength": return "arc_length";
                case "Spot": return SpotShapeFromStyle(styleTypeName);
            }
            // The shape could not be read or is a value this table has never seen. The
            // style type alone still answers for every style the wire names, so use it
            // before giving up - it is a fact off the same element, not a guess.
            switch (styleTypeName)
            {
                case "Linear": return "linear";
                case "Angular": return "angular";
                case "Radial": return "radial";
                case "Diameter": return "diameter";
                case "ArcLength": return "arc_length";
                default: return SpotShapeFromStyle(styleTypeName);
            }
        }

        private static string SpotShapeFromStyle(string styleTypeName)
        {
            switch (styleTypeName)
            {
                case "SpotElevation": return "spot_elevation";
                case "SpotCoordinate": return "spot_coordinate";
                case "SpotSlope": return "spot_slope";
                default: return null;
            }
        }

        // ---------------------------------------------------------------------
        // Action fields.
        // ---------------------------------------------------------------------

        /// <summary>The one non-edit field every action carries.</summary>
        public const string FieldElementId = "element_id";

        /// <summary>
        /// Every edit an action may request. The refusal for an action that names
        /// none, and the refusal for a field outside this set, both quote this list.
        /// </summary>
        public static readonly string[] EditFields =
        {
            "set_type_id", "move_by", "prefix", "suffix", "above", "below",
            "value_override", "eq", "lock", "segments", "reset_text_position"
        };

        public static string EditFieldsSentence()
            => string.Join(", ", EditFields);

        /// <summary>
        /// Spellings of "swap this dimension's references". They get their own class
        /// because the honest answer is neither "fix your arguments" nor "write Python":
        /// the Revit API has no setter for Dimension.References in any version this
        /// bridge supports, so NO path - typed or scripted - can do it, and the refusal
        /// must say that instead of waving at a fallback that would fail the same way.
        /// </summary>
        private static readonly string[] ReferenceReplacementFields =
        {
            "replace_references", "references", "set_references"
        };

        public enum ActionFieldClass
        {
            /// <summary>element_id - names the target, edits nothing.</summary>
            Identity,

            /// <summary>A typed edit this command implements.</summary>
            Edit,

            /// <summary>A reference swap - impossible in the API itself, on every path.</summary>
            ReferenceReplacement,

            /// <summary>Anything else - no typed capability here covers it.</summary>
            Unknown
        }

        /// <summary>Classify one property name of an action object. Case-insensitive,
        /// so "References" cannot slide past the refusal that "references" gets.</summary>
        public static ActionFieldClass ClassifyActionField(string name)
        {
            if (name == null) return ActionFieldClass.Unknown;
            if (string.Equals(name, FieldElementId, StringComparison.OrdinalIgnoreCase))
                return ActionFieldClass.Identity;
            foreach (string f in ReferenceReplacementFields)
                if (string.Equals(name, f, StringComparison.OrdinalIgnoreCase))
                    return ActionFieldClass.ReferenceReplacement;
            foreach (string f in EditFields)
                if (string.Equals(name, f, StringComparison.OrdinalIgnoreCase))
                    return ActionFieldClass.Edit;
            return ActionFieldClass.Unknown;
        }

        // ---------------------------------------------------------------------
        // Segment eligibility. THE TABLE.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Element-level overrides that Revit only honours on a single-segment
        /// dimension. On a multi-segment one each segment owns its own copy, and the
        /// eligibility error points the caller at segments[] rather than merely saying no.
        /// </summary>
        public static readonly string[] SingleSegmentOnlyFields =
        {
            "prefix", "suffix", "above", "below", "value_override", "lock"
        };

        /// <summary>Fields that only mean anything when there are segments to address.</summary>
        public static readonly string[] MultiSegmentOnlyFields = { "eq", "segments" };

        /// <summary>Fields indifferent to the segment count.</summary>
        public static readonly string[] AnySegmentFields = { "set_type_id", "move_by", "reset_text_position" };

        /// <summary>
        /// Revit reports a single-segment dimension as NumberOfSegments == 0 (the value
        /// lives on the element; Segments is empty). A count of 1 should not occur, but
        /// if a Revit ever reports it, the element-level path is the one that can work -
        /// so "single" is &lt;= 1, not == 0, and the choice is written down here rather
        /// than made twice.
        /// </summary>
        public static bool IsSingleSegment(int numberOfSegments) => numberOfSegments <= 1;

        /// <summary>
        /// Whether one edit field may be applied to a dimension with this many
        /// segments. Null means eligible; anything else is the refusal, worded to say
        /// where the edit BELONGS instead of only where it does not.
        /// </summary>
        public static string EligibilityError(string field, int numberOfSegments)
        {
            bool single = IsSingleSegment(numberOfSegments);

            foreach (string f in SingleSegmentOnlyFields)
                if (string.Equals(field, f, StringComparison.Ordinal))
                    return single ? null :
                        "'" + field + "' applies to single-segment dimensions only; this dimension has " +
                        numberOfSegments.ToString(CultureInfo.InvariantCulture) + " segments, each with its own '" +
                        field + "'. Use segments[] to edit them per segment.";

            if (string.Equals(field, "eq", StringComparison.Ordinal))
                return single ?
                    "'eq' equalises the segments of a multi-segment dimension; this dimension has none to " +
                    "equalise." : null;

            if (string.Equals(field, "segments", StringComparison.Ordinal))
                return single ?
                    "'segments' addresses the segments of a multi-segment dimension; this dimension is " +
                    "single-segment - its overrides live on the element itself (prefix, suffix, above, below, " +
                    "value_override, lock)." : null;

            foreach (string f in AnySegmentFields)
                if (string.Equals(field, f, StringComparison.Ordinal))
                    return null;

            // Not an edit field at all. Classification catches this earlier; answering
            // "eligible" for a field the table does not know would be a quiet yes.
            return "'" + field + "' is not an edit field this table knows. Known: " + EditFieldsSentence() + ".";
        }

        /// <summary>
        /// Judge a segment index against the measured count. Null when addressable.
        /// </summary>
        public static string SegmentIndexError(long index, int numberOfSegments)
        {
            if (index < 0)
                return "segment index " + index.ToString(CultureInfo.InvariantCulture) + " is negative; indices are 0-based.";
            if (index >= numberOfSegments)
                return "segment index " + index.ToString(CultureInfo.InvariantCulture) + " is out of range: this dimension has " +
                       numberOfSegments.ToString(CultureInfo.InvariantCulture) + " segment(s), so valid indices are 0.." +
                       (numberOfSegments - 1).ToString(CultureInfo.InvariantCulture) + ".";
            return null;
        }

        /// <summary>
        /// The ids that appear more than once, in first-seen order. Two actions editing
        /// one dimension in one batch are order-dependent in a way the caller never
        /// stated, so the batch is refused rather than sequenced by guesswork.
        /// </summary>
        public static List<long> DuplicateIds(IEnumerable<long> ids)
        {
            var seen = new HashSet<long>();
            var reported = new HashSet<long>();
            var duplicates = new List<long>();
            if (ids == null) return duplicates;
            foreach (long id in ids)
            {
                if (seen.Add(id)) continue;
                if (reported.Add(id)) duplicates.Add(id);
            }
            return duplicates;
        }

        // ---------------------------------------------------------------------
        // Text override semantics.
        // ---------------------------------------------------------------------

        /// <summary>
        /// An explicit empty string REMOVES the override - that is how Revit stores
        /// "no override", so writing "" back is the deletion, and the verification for
        /// it is the same re-read as any other value: the model must answer empty.
        /// </summary>
        public static bool ClearsOverride(string requested)
            => requested != null && requested.Length == 0;

        /// <summary>Null and "" are the same stored fact to Revit's text fields.</summary>
        public static string NormalizeText(string s) => s ?? "";

        /// <summary>
        /// Requested vs re-read, with the null/empty identity applied on both sides -
        /// asking for "" and reading back null is the override removed, not a mismatch.
        /// </summary>
        public static bool TextMatches(string requested, string readBack)
            => string.Equals(NormalizeText(requested), NormalizeText(readBack), StringComparison.Ordinal);

        // ---------------------------------------------------------------------
        // Canonical rounding for before-values.
        // ---------------------------------------------------------------------

        public const double MillimetresPerFoot = 304.8;

        /// <summary>
        /// One internal-feet length as a canonical 0.1 mm string. Rounded because
        /// Revit's own regeneration jitters the last digits of a coordinate, and a
        /// before-value that changes on its own would refuse every apply as stale;
        /// 0.1 mm because a real edit to a dimension's position is never smaller.
        /// Invariant culture, fixed format, and negative zero collapsed - "-0.0" and
        /// "0.0" are one fact and must be one string.
        /// </summary>
        public static string CanonicalTenthMillimetre(double feet)
        {
            double mm = Math.Round(feet * MillimetresPerFoot, 1, MidpointRounding.AwayFromZero);
            if (mm == 0) mm = 0;   // collapse -0.0
            return mm.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>A point in internal feet as one canonical string.</summary>
        public static string CanonicalPoint(double xFeet, double yFeet, double zFeet)
            => CanonicalTenthMillimetre(xFeet) + "," + CanonicalTenthMillimetre(yFeet) + "," +
               CanonicalTenthMillimetre(zFeet);

        // ---------------------------------------------------------------------
        // The exact-move comparison.
        // ---------------------------------------------------------------------

        /// <summary>
        /// The tolerance the move verification declares, in internal feet. 1e-6 ft is
        /// ~0.0003 mm: far below anything a model records deliberately, far above
        /// floating-point noise.
        /// </summary>
        public const double DefaultMoveToleranceFeet = 1e-6;

        /// <summary>Euclidean distance between two [x,y,z] points, both in feet.</summary>
        public static double Distance(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Did the sampled geometry move by EXACTLY the requested vector, within the
        /// declared tolerance? Every sample point must land at before + vector. A
        /// two-point sample is also accepted in reversed order, because Revit may
        /// normalise a curve by swapping its endpoints - that is the same committed
        /// geometry, not a failed move. No samples is a fail, never a vacuous pass:
        /// an empty comparison proves nothing and must not read like proof.
        /// </summary>
        public static bool MovedExactly(IList<double[]> before, IList<double[]> after,
                                        double[] vector, double toleranceFeet)
        {
            if (before == null || after == null || vector == null) return false;
            if (before.Count == 0 || before.Count != after.Count) return false;

            if (MovedExactlyOrdered(before, after, vector, toleranceFeet, reversed: false)) return true;
            return before.Count == 2 && MovedExactlyOrdered(before, after, vector, toleranceFeet, reversed: true);
        }

        private static bool MovedExactlyOrdered(IList<double[]> before, IList<double[]> after,
                                                double[] vector, double toleranceFeet, bool reversed)
        {
            for (int i = 0; i < before.Count; i++)
            {
                double[] b = before[i];
                double[] a = after[reversed ? after.Count - 1 - i : i];
                var expected = new[] { b[0] + vector[0], b[1] + vector[1], b[2] + vector[2] };
                if (Distance(expected, a) > toleranceFeet) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------------
        // The terminal-state matrix.
        // ---------------------------------------------------------------------

        public const string StateVerifiedApplied = "verified_applied";
        public const string StateRolledBack = "rolled_back";
        public const string StateRefused = "refused";
        public const string StateUncertain = "uncertain";

        /// <summary>
        /// Issued by the confirmation machinery, not by this matrix - named here so the
        /// command's state vocabulary is closed in one place.
        /// </summary>
        public const string StateStalePlan = "stale_plan";

        /// <summary>
        /// The one word for what happened, from the transaction's TERMINAL status and
        /// whether every requested field verified against a re-read. The order is the
        /// rule: the transaction first - nothing measured inside a transaction that did
        /// not commit means anything - and only a Committed with every field verified
        /// earns the state a caller may build on. A Committed whose post-commit re-read
        /// disagrees is UNCERTAIN, not partial: the reversible-state verification said
        /// yes and the committed model says no, and two measurements that contradict
        /// each other are the absence of knowledge, not half of it. Anything that is
        /// neither Committed, RolledBack nor not_started - Pending, Error, a name from
        /// a future Revit - is uncertainty and must never be smoothed into "clean".
        /// </summary>
        public static string DecideFinalState(string terminalTransactionStatus, bool allVerified)
        {
            if (string.IsNullOrEmpty(terminalTransactionStatus)) return StateUncertain;
            if (string.Equals(terminalTransactionStatus, ApplicationOutcome.NotStarted, StringComparison.Ordinal))
                return StateRefused;
            if (PlanFailure.IsConfirmedRollback(terminalTransactionStatus))
                return StateRolledBack;
            if (string.Equals(terminalTransactionStatus, ApplicationOutcome.Committed, StringComparison.Ordinal))
                return allVerified ? StateVerifiedApplied : StateUncertain;
            return StateUncertain;
        }
    }
}
