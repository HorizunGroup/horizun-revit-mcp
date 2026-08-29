// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE ARITHMETIC BEHIND auto_dimension_*, with no Revit in it.
//
// "Dimension the grids on this plan" is one sentence and about eight decisions,
// every one of which a program can get wrong quietly:
//
//   * WHICH CHAIN does a reference belong to? Grids in a building run in two
//     directions and sometimes three. One chain across all of them measures
//     distances between lines that never meet, and the drawing looks plausible.
//     Direction grouping is explicit, with a stated angular tolerance.
//
//   * WHICH AXIS does a chain run along? "auto" has to mean something
//     defensible, and the defensible thing is the axis the references actually
//     spread along - measured, reported, and overridable by name.
//
//   * WHAT ORDER? A chain is positional: the same references in another order
//     are another dimension. The order is by projected position, and where two
//     references project to the same place the chain is REFUSED, because a
//     zero-length segment is not a dimension anybody asked for.
//
//   * WHAT IS ALREADY THERE? Re-running a plan must not double every dimension
//     on the sheet. An existing dimension over the same ordered reference set is
//     a duplicate, and duplicates are omitted with a reason rather than skipped
//     in silence.
//
//   * WHAT WAS LEFT OUT? Every reference that did not make it into a chain is
//     named with a structured code. A plan that quietly covers 9 of 12 grids is
//     worse than one that refuses, because the drawing looks finished.
//
// The Revit halves - finding grids, reading curtain grid lines, getting an
// opening's centre reference - live in PlanAnnotationsCommand. Everything below
// is arithmetic over doubles and strings, and is proved without a model.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// One reference a chain could be built from, already projected into the view
    /// plane by the command. X and Y are the view's right/up coordinates in internal
    /// feet; Direction is the reference's own direction in that plane, where it has
    /// one (a grid line does, a spot point does not).
    /// </summary>
    public sealed class AutoDimensionCandidate
    {
        public string StableRepresentation { get; set; }

        /// <summary>Projected position in the view plane, internal feet.</summary>
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>Unit direction in the view plane, or null where the reference has none.</summary>
        public double? DirectionX { get; set; }
        public double? DirectionY { get; set; }

        /// <summary>The element the reference belongs to: a host id, or a linked element id.</summary>
        public long SubjectId { get; set; }

        /// <summary>Null for host geometry; the RevitLinkInstance id otherwise.</summary>
        public long? LinkInstanceId { get; set; }

        /// <summary>What produced it - "grid", "level", "curtain_grid_u", "opening_center", "wall_face".</summary>
        public string Source { get; set; }

        /// <summary>What a human calls it: the grid's name, the level's name, the door's mark.</summary>
        public string Label { get; set; }

        /// <summary>A stable identity for duplicate detection across the whole plan.</summary>
        public string Identity => (LinkInstanceId.HasValue ? LinkInstanceId.Value + "/" : "h/") +
                                  SubjectId.ToString(CultureInfo.InvariantCulture) + "/" + (Source ?? "");

        /// <summary>
        /// The identity DUPLICATE DETECTION compares - the stable representation by
        /// default, or the collapsed form the command supplies where Revit is known to
        /// respell a reference once it lives on a dimension (measured 2026-08-26:
        /// `new Reference(grid)` serialises bare, the committed dimension's copy
        /// serialises `:0:SURFACE`, and parse-and-reserialize does not unify them).
        /// </summary>
        public string DedupIdentity { get; set; }

        public string EffectiveDedupIdentity => DedupIdentity ?? StableRepresentation;
    }

    /// <summary>One chain the planner decided on: its references, in order, and where its line goes.</summary>
    public sealed class AutoDimensionChain
    {
        public string GroupKey { get; set; }
        public string Axis { get; set; }              // "horizontal" | "vertical"
        public List<AutoDimensionCandidate> Ordered { get; } = new List<AutoDimensionCandidate>();

        /// <summary>Dimension-line endpoints in view-plane coordinates, internal feet.</summary>
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double EndX { get; set; }
        public double EndY { get; set; }
    }

    /// <summary>A reference that did not make it into any chain, and exactly why.</summary>
    public sealed class AutoDimensionOmission
    {
        public AutoDimensionOmission(AutoDimensionCandidate candidate, string code, string reason)
        {
            Candidate = candidate; Code = code; Reason = reason;
        }
        public AutoDimensionCandidate Candidate { get; }
        public string Code { get; }
        public string Reason { get; }
    }

    public static class AutoDimensionRules
    {
        // ---- the operation vocabulary, closed ------------------------------------

        public const string OpGrids = "auto_dimension_grids";
        public const string OpLevels = "auto_dimension_levels";
        public const string OpCurtainWalls = "auto_dimension_curtain_walls";
        public const string OpOpenings = "auto_dimension_openings";

        public static readonly IReadOnlyList<string> KnownOperations = new[]
        {
            OpGrids, OpLevels, OpCurtainWalls, OpOpenings
        };

        // ---- omission codes -------------------------------------------------------

        public const string CodeAlreadyDimensioned = "already_dimensioned";
        public const string CodeGroupTooSmall = "group_too_small";
        public const string CodeNoReference = "no_dimensionable_reference";
        public const string CodeNotVisible = "not_visible_in_view";
        public const string CodeNoDirection = "no_direction_in_view_plane";
        public const string CodeUnreadable = "unreadable";

        // ---- refusal codes (the whole plan, not one reference) --------------------

        public const string CodeCoincidentReferences = "coincident_references";
        public const string CodeNoSpread = "no_measurable_spread";
        public const string CodeNothingToDimension = "nothing_to_dimension";

        // ---- tolerances, named ----------------------------------------------------

        /// <summary>
        /// Two references closer than this on the chain axis are the same position, and
        /// a segment between them would measure zero. 0.1 mm, the same grid every other
        /// geometric decision in this codebase runs on.
        /// </summary>
        public const double CoincidenceToleranceFeet = 1.0 / 3048.0;

        /// <summary>
        /// How far two directions may differ and still belong to one chain. Grids are
        /// drawn to be parallel and are almost never off by more than rounding; 0.5
        /// degrees is wide enough to absorb that and narrow enough that a deliberately
        /// splayed grid becomes its own chain instead of joining the orthogonal one.
        /// </summary>
        public const double DirectionToleranceDegrees = 0.5;

        /// <summary>
        /// How far the dimension line runs past the outermost reference, as a fraction
        /// of the offset. A line that stops exactly on the last reference reads as a
        /// mistake; a quarter of the offset is a witness tail, not a design choice.
        /// </summary>
        public const double TailFraction = 0.25;

        public const int MinReferencesPerChain = 2;

        public const string OrderingNote =
            "within a chain, references are ordered by their projected position along the chain axis, ascending; " +
            "chains are ordered by group key, ordinal. The same model produces the same plan on every call.";

        // ---- axis resolution ------------------------------------------------------

        /// <summary>
        /// Which axis a chain runs along. "auto" measures: the axis the references
        /// actually spread along wins, and a tie goes to horizontal so the answer is
        /// deterministic rather than dependent on floating-point noise. An explicit
        /// axis is honoured even when it is the narrow one - the caller may know
        /// something the spread does not.
        /// </summary>
        public static string ResolveAxis(IEnumerable<AutoDimensionCandidate> candidates, string requested,
                                          out double spreadX, out double spreadY)
        {
            spreadX = 0; spreadY = 0;
            List<AutoDimensionCandidate> list = candidates == null
                ? new List<AutoDimensionCandidate>() : candidates.ToList();
            if (list.Count > 0)
            {
                spreadX = list.Max(c => c.X) - list.Min(c => c.X);
                spreadY = list.Max(c => c.Y) - list.Min(c => c.Y);
            }
            if (requested == "horizontal" || requested == "vertical") return requested;
            return spreadX >= spreadY ? "horizontal" : "vertical";
        }

        /// <summary>Valid values for the axis argument. Anything else refuses naming these.</summary>
        public static string ValidateAxis(string axis)
        {
            if (axis == "auto" || axis == "horizontal" || axis == "vertical") return null;
            return "axis must be auto, horizontal or vertical; '" + axis + "' was sent. 'auto' measures which " +
                   "way the references actually spread and reports what it chose.";
        }

        public static string ValidateSide(string side)
        {
            if (side == "positive" || side == "negative") return null;
            return "side must be positive or negative; '" + side + "' was sent. It selects which side of the " +
                   "references the dimension line sits on, in the view's own up/right frame.";
        }

        // ---- direction grouping ---------------------------------------------------

        /// <summary>
        /// Split candidates into chains by DIRECTION. Parallel references belong
        /// together; a reference with no direction in the view plane cannot be grouped
        /// this way and comes back in <paramref name="ungroupable"/> rather than being
        /// dropped into whichever group happens to be first.
        ///
        /// Antiparallel counts as parallel: a grid drawn right-to-left is the same
        /// family as one drawn left-to-right, and treating them as two would split
        /// every grid family somebody had rotated.
        /// </summary>
        public static List<List<AutoDimensionCandidate>> GroupByDirection(
            IEnumerable<AutoDimensionCandidate> candidates, double toleranceDegrees,
            out List<AutoDimensionCandidate> ungroupable)
        {
            ungroupable = new List<AutoDimensionCandidate>();
            var groups = new List<List<AutoDimensionCandidate>>();
            var axes = new List<double[]>();
            if (candidates == null) return groups;

            double cosLimit = Math.Cos(Math.Abs(toleranceDegrees) * Math.PI / 180.0);
            foreach (AutoDimensionCandidate c in candidates)
            {
                if (!c.DirectionX.HasValue || !c.DirectionY.HasValue) { ungroupable.Add(c); continue; }
                double dx = c.DirectionX.Value, dy = c.DirectionY.Value;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (double.IsNaN(length) || length < 1e-9) { ungroupable.Add(c); continue; }
                dx /= length; dy /= length;

                int found = -1;
                for (int i = 0; i < axes.Count; i++)
                {
                    // |dot| because antiparallel is the same family.
                    double dot = Math.Abs(axes[i][0] * dx + axes[i][1] * dy);
                    if (dot >= cosLimit) { found = i; break; }
                }
                if (found < 0)
                {
                    axes.Add(new[] { dx, dy });
                    groups.Add(new List<AutoDimensionCandidate> { c });
                }
                else groups[found].Add(c);
            }
            return groups;
        }

        /// <summary>
        /// A stable, human-readable key for a group of parallel references: the
        /// canonical direction to a tenth of a degree. Canonical because a direction
        /// and its opposite are one family and must not produce two keys.
        /// </summary>
        public static string GroupKey(IEnumerable<AutoDimensionCandidate> group)
        {
            AutoDimensionCandidate first = group == null ? null : group.FirstOrDefault(
                c => c.DirectionX.HasValue && c.DirectionY.HasValue);
            if (first == null) return "no-direction";
            double dx = first.DirectionX.Value, dy = first.DirectionY.Value;
            double degrees = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            // Fold onto [0, 180): a line has an orientation, not a heading.
            while (degrees < 0) degrees += 180.0;
            while (degrees >= 180.0) degrees -= 180.0;
            return "dir:" + Math.Round(degrees, 1).ToString("0.0", CultureInfo.InvariantCulture);
        }

        // ---- ordering and validation ----------------------------------------------

        /// <summary>
        /// Order one group along its axis and refuse the degenerate cases. The order is
        /// positional and the tiebreak is the stable representation, so two references
        /// that survive the coincidence check in different runs still order the same.
        /// </summary>
        public static string OrderAlongAxis(List<AutoDimensionCandidate> group, string axis,
                                             out List<AutoDimensionCandidate> ordered, out string code)
        {
            ordered = null; code = null;
            if (group == null || group.Count < MinReferencesPerChain)
            {
                code = CodeGroupTooSmall;
                return "a dimension chain needs at least " + MinReferencesPerChain + " references; this group has " +
                       (group == null ? 0 : group.Count) + ".";
            }
            bool horizontal = axis == "horizontal";
            List<AutoDimensionCandidate> sorted = group
                .OrderBy(c => horizontal ? c.X : c.Y)
                .ThenBy(c => c.StableRepresentation ?? "", StringComparer.Ordinal)
                .ToList();

            for (int i = 1; i < sorted.Count; i++)
            {
                double a = horizontal ? sorted[i - 1].X : sorted[i - 1].Y;
                double b = horizontal ? sorted[i].X : sorted[i].Y;
                if (Math.Abs(b - a) <= CoincidenceToleranceFeet)
                {
                    code = CodeCoincidentReferences;
                    return "references " + Describe(sorted[i - 1]) + " and " + Describe(sorted[i]) + " project to " +
                           "the same position on the " + axis + " axis (within 0.1 mm), so the segment between " +
                           "them would measure zero. Which of the two you meant is not something this planner " +
                           "can decide - name the references explicitly with intent_dimension, or drop one.";
                }
            }

            double spread = horizontal
                ? sorted[sorted.Count - 1].X - sorted[0].X
                : sorted[sorted.Count - 1].Y - sorted[0].Y;
            if (spread <= CoincidenceToleranceFeet)
            {
                code = CodeNoSpread;
                return "the whole group spans less than 0.1 mm on the " + axis + " axis; there is nothing to " +
                       "measure along it.";
            }

            ordered = sorted;
            return null;
        }

        // ---- the dimension line ----------------------------------------------------

        /// <summary>
        /// Where the dimension line goes, in view-plane coordinates. The line runs
        /// along the chain axis past both outermost references by a witness tail, and
        /// sits <paramref name="offsetFeet"/> beyond the extreme reference on the
        /// chosen side. Nothing here is a preference: the caller picks the offset and
        /// the side, and this places the line where those two say.
        /// </summary>
        public static void PlaceLine(List<AutoDimensionCandidate> ordered, string axis, string side,
                                      double offsetFeet, AutoDimensionChain chain)
        {
            if (ordered == null || ordered.Count == 0) throw new ArgumentException("A chain needs references.");
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            bool horizontal = axis == "horizontal";
            double tail = Math.Max(offsetFeet * TailFraction, CoincidenceToleranceFeet * 10);

            double alongMin = horizontal ? ordered.Min(c => c.X) : ordered.Min(c => c.Y);
            double alongMax = horizontal ? ordered.Max(c => c.X) : ordered.Max(c => c.Y);
            double across = horizontal
                ? (side == "positive" ? ordered.Max(c => c.Y) + offsetFeet : ordered.Min(c => c.Y) - offsetFeet)
                : (side == "positive" ? ordered.Max(c => c.X) + offsetFeet : ordered.Min(c => c.X) - offsetFeet);

            if (horizontal)
            {
                chain.StartX = alongMin - tail; chain.StartY = across;
                chain.EndX = alongMax + tail; chain.EndY = across;
            }
            else
            {
                chain.StartX = across; chain.StartY = alongMin - tail;
                chain.EndX = across; chain.EndY = alongMax + tail;
            }
            chain.Axis = axis;
        }

        /// <summary>
        /// Successive chains on the same side must not land on top of each other. The
        /// nth chain of a plan is pushed out by n * separation; separation defaults to
        /// the offset so two chains are as far apart as the first is from the model.
        /// </summary>
        public static double StackedOffset(double baseOffsetFeet, double separationFeet, int ordinal)
        {
            if (ordinal < 0) throw new ArgumentException("A chain ordinal cannot be negative.", nameof(ordinal));
            return baseOffsetFeet + separationFeet * ordinal;
        }

        // ---- duplicate detection ---------------------------------------------------

        /// <summary>
        /// The identity of an ORDERED reference set, for telling "this chain already
        /// exists" from "this chain is new". Order is part of it because a dimension's
        /// references are positional.
        ///
        /// LENGTH-PREFIXED, not merely separated. A separator alone is forgeable: with
        /// only a trailing 0x1F after each item, the two-reference set ["a", "b"] and
        /// the one-reference set ["a\x1Fb"] render to the same bytes and hash alike, so
        /// a chain could be mistaken for a different chain entirely. A stable
        /// representation probably never contains a control character - but "probably"
        /// is not a property, and the count costs one integer.
        /// </summary>
        public static string ChainIdentity(IEnumerable<string> stableRepresentations)
        {
            var items = (stableRepresentations ?? Enumerable.Empty<string>()).Select(s => s ?? "").ToList();
            var sb = new StringBuilder();
            sb.Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append((char)31);
            foreach (string s in items)
                sb.Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s).Append((char)31);
            return RequestFingerprint.Sha256Hex(sb.ToString());
        }

        /// <summary>
        /// An UNORDERED identity over the same set. Revit is free to hold a chain's
        /// references in the order it was created with, which is not necessarily the
        /// order this planner would choose; a duplicate check that only compared the
        /// ordered form would re-plan a chain that is already on the drawing.
        /// </summary>
        public static string ChainIdentityUnordered(IEnumerable<string> stableRepresentations)
        {
            var sorted = (stableRepresentations ?? Enumerable.Empty<string>())
                .Select(s => s ?? "").OrderBy(s => s, StringComparer.Ordinal).ToList();
            return ChainIdentity(sorted);
        }

        /// <summary>
        /// Which planned chains already exist. Compared UNORDERED, because a chain that
        /// is on the drawing is on the drawing however Revit happens to list it.
        /// </summary>
        public static bool IsDuplicate(IEnumerable<string> plannedReferences,
                                       IEnumerable<string> existingChainIdentities)
        {
            if (existingChainIdentities == null) return false;
            string identity = ChainIdentityUnordered(plannedReferences);
            foreach (string existing in existingChainIdentities)
                if (string.Equals(existing, identity, StringComparison.Ordinal)) return true;
            return false;
        }

        // ---- reporting -------------------------------------------------------------

        public static string Describe(AutoDimensionCandidate c)
        {
            if (c == null) return "(none)";
            string who = string.IsNullOrWhiteSpace(c.Label)
                ? "element " + c.SubjectId.ToString(CultureInfo.InvariantCulture)
                : "'" + c.Label + "'";
            return c.LinkInstanceId.HasValue
                ? who + " (in link instance " + c.LinkInstanceId.Value.ToString(CultureInfo.InvariantCulture) + ")"
                : who;
        }

        /// <summary>
        /// The verdict a caller branches on. "complete" is claimed ONLY when every
        /// reference that was found made it into a chain: a plan that covered most of
        /// the grids and said so is useful, and one that covered most of them and
        /// called itself complete is a drawing somebody will sign.
        /// </summary>
        public static string Coverage(int found, int planned, int omitted)
        {
            if (found == 0) return "nothing_found";
            if (omitted == 0 && planned == found) return "complete";
            if (planned == 0) return "none";
            return "partial";
        }

        public static string OperationError(string operation)
            => "operation '" + operation + "' is not one this command understands. Known: auto_tags, " +
               "intent_dimension, " + string.Join(", ", KnownOperations) + ".";
    }
}
