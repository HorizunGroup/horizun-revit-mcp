// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH references a dimension may hang off, decided as arithmetic.
//
// horizun_get_dimension_references answers "what can I dimension to on this
// element, in this view?". The ANSWER needs a live Revit - faces, edges and
// stable representations only exist there. The RULES do not, and they are the
// part that has to be provably right, because every one of them guards against
// a specific way of quietly answering the wrong question:
//
//   * selector parsing        - an unknown selector must be refused naming the
//                               known ones, not silently dropped so the caller
//                               believes it was applied;
//   * probe requirement       - nearest/farthest without a probe point is a
//                               question with no subject: refuse, never pick a
//                               default point the caller did not name;
//   * applicability           - "exterior_face of a duct" has no answer. The
//                               selector is reported as not applicable, never
//                               approximated with some other face;
//   * ambiguity               - when a single-answer selector finds several
//                               equivalent candidates, ALL of them come back
//                               marked ambiguous. Choosing one would be a guess
//                               wearing the shape of an answer;
//   * ordering and paging     - the same model must produce the same rows in
//                               the same order on every call, and a truncated
//                               answer must say so beside the exact total;
//   * the fingerprint         - a reference approved against one face must be
//                               recognisable as THAT face later. The fingerprint
//                               quantises every number on a 0.1 mm grid
//                               (1/3048 ft) so regeneration jitter does not
//                               change identity while a real move does.
//
// Revit-free on purpose: no `using Autodesk`. The command feeds this file plain
// strings and doubles; the tests prove the rules without a building.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// What kind of element a selector is being asked about, as plain facts. The
    /// command reads these off the live element; the rules never see Revit types.
    /// </summary>
    public sealed class ElementTraits
    {
        public bool IsGrid { get; set; }
        public bool IsLevel { get; set; }
        public bool IsReferencePlane { get; set; }

        /// <summary>A wall or other HostObject - the only class with side faces.</summary>
        public bool IsHostObject { get; set; }

        /// <summary>The element has a location CURVE (not a point).</summary>
        public bool HasLocationCurve { get; set; }

        /// <summary>Solid geometry with at least one face was read.</summary>
        public bool HasSolidGeometry { get; set; }

        /// <summary>Standalone curve geometry (model lines and the like) was read.</summary>
        public bool HasCurveGeometry { get; set; }

        /// <summary>True for grids, levels and reference planes - the datum classes.</summary>
        public bool IsDatum => IsGrid || IsLevel || IsReferencePlane;
    }

    /// <summary>A structured "this reference cannot carry a dimension" verdict.</summary>
    public sealed class IncompatibilityReason
    {
        public string Code { get; }
        public string Message { get; }

        public IncompatibilityReason(string code, string message)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("An incompatibility must name its code - a client branches on it.", nameof(code));
            Code = code;
            Message = message ?? "";
        }
    }

    /// <summary>
    /// The named geometric facts one candidate reference is made of, canonicalised
    /// for fingerprinting. Every number is INTERNAL FEET and is quantised on the
    /// 0.1 mm grid before it enters the hash; names are sorted ordinally so the
    /// order facts were added in cannot change the fingerprint.
    /// </summary>
    public sealed class GeometryFacts
    {
        private readonly SortedDictionary<string, string> _facts =
            new SortedDictionary<string, string>(StringComparer.Ordinal);

        public GeometryFacts Add(string name, double feet)
        {
            // A NaN or infinite fact means the geometry was not actually read, and a
            // fingerprint over it would hand out a stable-looking identity for a value
            // nobody measured. Refuse here; the command reports the candidate as
            // unreadable instead of fingerprinting it.
            if (double.IsNaN(feet) || double.IsInfinity(feet))
                throw new ArgumentException("Fact '" + name + "' is not a finite number; a fingerprint over it would " +
                                            "be an identity for something that was never measured.", nameof(feet));
            Store(name, "q:" + DimensionReferenceRules.QuantizeFeet(feet).ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public GeometryFacts Add(string name, string value)
        {
            Store(name, "s:" + (value ?? ""));
            return this;
        }

        public GeometryFacts AddXyz(string name, double xFeet, double yFeet, double zFeet)
        {
            Add(name + ".x", xFeet);
            Add(name + ".y", yFeet);
            Add(name + ".z", zFeet);
            return this;
        }

        private void Store(string name, string rendered)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A fact needs a name.", nameof(name));
            if (_facts.ContainsKey(name))
                throw new ArgumentException("Fact '" + name + "' was added twice; the second value would silently " +
                                            "shadow the first inside the fingerprint.", nameof(name));
            _facts[name] = rendered;
        }

        /// <summary>Deterministic rendering: sorted names, one per line.</summary>
        public string Canonical()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> f in _facts)
                sb.Append(f.Key).Append('=').Append(f.Value).Append('\n');
            return sb.ToString();
        }

        public int Count => _facts.Count;
    }

    /// <summary>The sort key of one candidate row. Built by the command, compared here.</summary>
    public sealed class CandidateKey
    {
        public long ElementId { get; set; }
        public string Selector { get; set; }
        public string ReferenceType { get; set; }
        public string Fingerprint { get; set; }

        /// <summary>Final tiebreak, for two references over coincident geometry.</summary>
        public string StableRepresentation { get; set; }
    }

    /// <summary>One page over an exact total. Never an estimate.</summary>
    public sealed class PageSlice
    {
        public int Total { get; set; }
        public int Offset { get; set; }
        public int Count { get; set; }
        public bool Truncated { get; set; }
    }

    public static class DimensionReferenceRules
    {
        // ---- the selector vocabulary, closed --------------------------------------

        public const string SelectorCenterline = "centerline";
        public const string SelectorGrid = "grid";
        public const string SelectorLevel = "level";
        public const string SelectorReferencePlane = "reference_plane";
        public const string SelectorExteriorFace = "exterior_face";
        public const string SelectorInteriorFace = "interior_face";
        public const string SelectorNearestFace = "nearest_face";
        public const string SelectorFarthestFace = "farthest_face";
        public const string SelectorEdge = "edge";
        public const string SelectorEndpoint = "endpoint";

        /// <summary>
        /// Every selector this command understands, in the canonical order the default
        /// set is emitted in. "axis" from the field vocabulary is deliberately NOT an
        /// alias: the centerline of a linear element is what "axis" means, and one
        /// name per concept keeps two spellings from looking like two capabilities.
        /// </summary>
        public static readonly IReadOnlyList<string> KnownSelectors = new[]
        {
            SelectorCenterline, SelectorExteriorFace, SelectorInteriorFace,
            SelectorEdge, SelectorEndpoint,
            SelectorNearestFace, SelectorFarthestFace,
            SelectorGrid, SelectorLevel, SelectorReferencePlane
        };

        // ---- the structured codes -------------------------------------------------
        // incompatibility_reason.code values (the row exists, a dimension cannot use it):
        public const string CodeNonPlanarFace = "non_planar_face";
        public const string CodeNoStableCenterline = "no_stable_centerline";
        public const string CodeUnsupportedEdgeCurve = "unsupported_edge_curve";
        public const string CodeMepCenterlineRejected = "mep_centerline_rejected_by_dimension_api";
        // warning codes (no row was produced, or a row carries a caveat):
        public const string WarningSelectorNotApplicable = "selector_not_applicable";
        public const string WarningViewGeometryFallback = "view_geometry_fallback";
        public const string WarningEndpointReferenceUnavailable = "endpoint_reference_unavailable";
        public const string WarningStableRepresentationUnavailable = "stable_representation_unavailable";
        public const string WarningCandidateUnreadable = "candidate_unreadable";
        public const string WarningNoApplicableSelectors = "no_applicable_selectors";
        public const string WarningDuplicateElementIds = "duplicate_element_ids";
        public const string WarningProbePointUnused = "probe_point_unused";
        // coverage.unreadable code:
        public const string CodeLinkReferencesNotSupported = "link_references_not_supported";

        // ---- limits ---------------------------------------------------------------

        public const int MaxTargets = 200;
        public const int MinResults = 1;
        public const int MaxResults = 500;
        public const int DefaultResults = 100;

        // ---- the 0.1 mm grid ------------------------------------------------------
        //
        // Revit's internal unit is the decimal foot; 1 ft = 304.8 mm, so 0.1 mm is
        // exactly 1/3048 ft. Every number entering a fingerprint or an equivalence
        // decision is quantised on that grid: regeneration jitter (1e-9 ft) never
        // crosses a 0.1 mm boundary in practice, while a real 0.2 mm move always
        // lands at least one grid step away. Values that sit ON a boundary can flip
        // by sub-grid noise - that is inherent to any grid and is why the tolerance
        // is far below anything a dimension could represent. Unit-vector components
        // ride the same grid (a step of ~3.3e-4, about 0.02 degrees) so there is ONE
        // documented rule instead of a rule per fact.
        public const double TicksPerFoot = 3048.0;
        public const double EquivalenceToleranceFeet = 1.0 / 3048.0;

        public const string RoundingNote =
            "geometry_fingerprint quantises every internal-feet value on a 0.1 mm grid (1/3048 ft) before " +
            "hashing, so regeneration jitter keeps the identity and a real move of 0.2 mm or more changes it.";

        public const string OrderingNote =
            "rows are ordered by (element_id ascending, selector, reference_type, geometry_fingerprint, " +
            "stable_representation), all ordinal - the same candidates arrive in the same order on every call.";

        public static long QuantizeFeet(double feet)
            => (long)Math.Round(feet * TicksPerFoot, MidpointRounding.AwayFromZero);

        // ---- selector parsing -----------------------------------------------------

        /// <summary>
        /// Parse the caller's selector list. Null in means "no selectors argument":
        /// selectors comes out null and the command uses the per-element defaults.
        /// An unknown selector refuses NAMING the known ones - dropping it silently
        /// would let the caller believe it was applied. Duplicates are collapsed;
        /// an explicitly empty list is refused because it can only be a mistake.
        /// </summary>
        public static bool TryParseSelectors(IEnumerable<string> requested, out List<string> selectors, out string error)
        {
            selectors = null;
            error = null;
            if (requested == null) return true;

            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in KnownSelectors) known[s] = s;

            var result = new List<string>();
            foreach (string raw in requested)
            {
                string trimmed = (raw ?? "").Trim();
                string canonical;
                if (trimmed.Length == 0 || !known.TryGetValue(trimmed, out canonical))
                {
                    error = "selector '" + trimmed + "' is not one this command understands. Known selectors: " +
                            string.Join(", ", KnownSelectors) + ". ('axis' of a linear element is 'centerline'.)";
                    return false;
                }
                if (!result.Contains(canonical)) result.Add(canonical);
            }
            if (result.Count == 0)
            {
                error = "selectors, when present, must contain at least one selector. Omit the field to get the " +
                        "defaults applicable to each element's class.";
                return false;
            }
            selectors = result;
            return true;
        }

        // ---- probe point ----------------------------------------------------------

        public static bool RequiresProbePoint(IEnumerable<string> selectors)
            => selectors != null && selectors.Any(s => s == SelectorNearestFace || s == SelectorFarthestFace);

        /// <summary>
        /// Error when nearest/farthest was asked without a probe point; null otherwise.
        /// Inventing a default point would answer "nearest to WHERE?" with a guess.
        /// </summary>
        public static string ValidateProbeRequirement(IEnumerable<string> selectors, bool probeProvided)
        {
            if (probeProvided || selectors == null) return null;
            var needy = selectors.Where(s => s == SelectorNearestFace || s == SelectorFarthestFace).ToList();
            if (needy.Count == 0) return null;
            return string.Join(" and ", needy) + " rank candidate faces by their distance to probe_point, which " +
                   "was not provided. Pass probe_point [x, y, z] in the request's units; nothing was inspected.";
        }

        // ---- element_ids XOR filter ----------------------------------------------

        /// <summary>Exactly one target specification. Both or neither is refused, never guessed.</summary>
        public static string ValidateTargetChoice(bool hasElementIds, bool hasFilter)
        {
            if (hasElementIds && hasFilter)
                return "element_ids and filter are two ways of naming the SAME thing - the targets - and a call " +
                       "carrying both is ambiguous about which one it means. Send exactly one of them.";
            if (!hasElementIds && !hasFilter)
                return "Either element_ids or filter is required: without a target specification there is nothing " +
                       "to inspect, and inspecting the whole model would answer a question that was not asked.";
            return null;
        }

        public static string ValidateElementIdCount(int count)
        {
            if (count < 1) return "element_ids must contain at least one id.";
            if (count > MaxTargets)
                return "element_ids contains " + count + " ids; the limit is " + MaxTargets + " targets per call. " +
                       "Split the request - the ordering is deterministic, so pages compose.";
            return null;
        }

        public static string FilterTooBroadError(int matched)
            => "The filter matched " + matched + " elements; the limit is " + MaxTargets + " targets per call. " +
               "Narrow the filter (categories, family, type, name, level) or pass explicit element_ids. " +
               "Nothing was inspected.";

        // ---- applicability --------------------------------------------------------

        /// <summary>
        /// The DEFAULT selector set for an element of this class, in canonical order.
        /// nearest_face/farthest_face are deliberately absent: they require a probe
        /// point, and a default that fails on every call without one would make the
        /// no-arguments path unusable. They must be asked for by name.
        /// </summary>
        public static IReadOnlyList<string> ApplicableSelectors(ElementTraits traits)
        {
            if (traits == null) return new string[0];
            var result = new List<string>();
            foreach (string s in KnownSelectors)
            {
                if (s == SelectorNearestFace || s == SelectorFarthestFace) continue;
                if (SelectorApplies(s, traits)) result.Add(s);
            }
            return result;
        }

        /// <summary>Whether one selector has any meaning for an element of this class.</summary>
        public static bool SelectorApplies(string selector, ElementTraits traits)
        {
            if (traits == null) return false;
            switch (selector)
            {
                case SelectorGrid: return traits.IsGrid;
                case SelectorLevel: return traits.IsLevel;
                case SelectorReferencePlane: return traits.IsReferencePlane;
                case SelectorCenterline: return !traits.IsDatum && traits.HasLocationCurve;
                case SelectorExteriorFace:
                case SelectorInteriorFace: return traits.IsHostObject;
                case SelectorEdge:
                case SelectorNearestFace:
                case SelectorFarthestFace: return !traits.IsDatum && traits.HasSolidGeometry;
                case SelectorEndpoint: return !traits.IsDatum && (traits.HasCurveGeometry || traits.HasSolidGeometry);
                default: return false;
            }
        }

        /// <summary>Why a selector does not apply - for the structured warning, never a guess.</summary>
        public static string WhyNotApplicable(string selector, long elementId, ElementTraits traits)
        {
            string requirement;
            switch (selector)
            {
                case SelectorGrid: requirement = "applies only to Grid elements"; break;
                case SelectorLevel: requirement = "applies only to Level elements"; break;
                case SelectorReferencePlane: requirement = "applies only to ReferencePlane elements"; break;
                case SelectorCenterline: requirement = "requires an element with a location curve"; break;
                case SelectorExteriorFace:
                case SelectorInteriorFace:
                    requirement = "applies only to host elements (walls and other HostObject classes, whose side " +
                                  "faces Revit itself can name)"; break;
                case SelectorEdge:
                case SelectorNearestFace:
                case SelectorFarthestFace: requirement = "requires solid geometry"; break;
                case SelectorEndpoint: requirement = "requires curve or solid geometry with references"; break;
                default: requirement = "is not a known selector"; break;
            }
            return "selector '" + selector + "' " + requirement + "; element " + elementId + " is not one. " +
                   "No candidate was produced for it - reporting a substitute would be a guess.";
        }

        // ---- compatibility --------------------------------------------------------

        /// <summary>
        /// Whether a dimension can hang off a candidate of this shape, and if not,
        /// why - as a code, so a client branches instead of parsing prose. Planar
        /// faces, linear edges, arc edges (radial/arc-length), endpoints and datum
        /// references carry dimensions; a non-planar face and a spline edge do not.
        /// </summary>
        public static IncompatibilityReason ClassifyForDimension(string referenceType, string geometryKind)
        {
            if (referenceType == "face")
            {
                if (geometryKind == "plane") return null;
                return new IncompatibilityReason(CodeNonPlanarFace,
                    "the face is " + (geometryKind ?? "of unknown kind") + ", not planar; Revit dimensions " +
                    "measure to planes, so this reference cannot carry one.");
            }
            if (referenceType == "edge")
            {
                if (geometryKind == "line" || geometryKind == "arc") return null;
                return new IncompatibilityReason(CodeUnsupportedEdgeCurve,
                    "the edge's curve is " + (geometryKind ?? "of unknown kind") + "; only linear and arc edges " +
                    "carry dimensions (arcs via radial/arc-length).");
            }
            // centerline, endpoint, grid, level, reference_plane: all dimensionable.
            return null;
        }

        /// <summary>The verdict for an element whose location curve has no stable reference.</summary>
        public static IncompatibilityReason NoStableCenterline(string detail)
            => new IncompatibilityReason(CodeNoStableCenterline,
                "no reference-carrying curve in the element's geometry coincides with its location curve" +
                (string.IsNullOrWhiteSpace(detail) ? "" : " (" + detail + ")") +
                ", so there is no stable centerline reference to hand out. Dimension to faces or edges instead.");

        /// <summary>
        /// MEASURED, not assumed: on live Revit 2025 (2026-08-24), NewDimension refuses
        /// the centerline reference of an MEP curve - two pipes' centerline references,
        /// parsed and valid, answered "Invalid number of references" - while the very
        /// same call succeeds against grid references. The reference itself is real and
        /// stable (it travels in the row for callers with other uses); what it is NOT
        /// is consumable by dimension creation, and claiming otherwise would fail at
        /// apply time with Revit's least helpful sentence.
        /// </summary>
        public static IncompatibilityReason MepCenterlineRejected()
            => new IncompatibilityReason(CodeMepCenterlineRejected,
                "Revit's NewDimension rejects MEP-curve centerline references (measured live: 'Invalid number of " +
                "references'). The reference is stable and real, but not consumable by dimension creation - " +
                "dimension MEP runs to grids, reference planes or neighbouring geometry instead.");

        /// <summary>Why link elements are refused - the phrase travels with the coverage entry.</summary>
        public static string LinkReferencesMessage(long elementId)
            => "element " + elementId + " is a Revit link instance. References into linked models have not been " +
               "proven live to be consumable by dimension creation on this bridge, so they are refused rather " +
               "than guessed at. The element was NOT inspected; open its own model to dimension it there.";

        // ---- ambiguity ------------------------------------------------------------

        /// <summary>
        /// Selectors whose question names ONE reference. When several equivalent
        /// candidates answer it, all are returned marked ambiguous - the enumerating
        /// selectors (edge, endpoint, datums) expect plurality and are never marked.
        /// </summary>
        public static bool ExpectsSingleAnswer(string selector)
            => selector == SelectorCenterline || selector == SelectorExteriorFace ||
               selector == SelectorInteriorFace || selector == SelectorNearestFace ||
               selector == SelectorFarthestFace;

        /// <summary>
        /// Mark a selector's candidate set. More than one answer to a single-answer
        /// selector makes EVERY candidate ambiguous, under one group id, so a caller
        /// sees the whole tie instead of a confident-looking arbitrary pick.
        /// </summary>
        public static void ShapeAmbiguity(string selector, long elementId, int candidateCount,
                                          out bool ambiguous, out string ambiguityGroup)
        {
            ambiguous = candidateCount > 1 && ExpectsSingleAnswer(selector);
            ambiguityGroup = ambiguous
                ? elementId.ToString(CultureInfo.InvariantCulture) + ":" + selector
                : null;
        }

        /// <summary>
        /// Which entries tie for nearest (or farthest) within the 0.1 mm equivalence
        /// tolerance. Returns EVERY tied index: two faces 0.05 mm apart in distance
        /// are the same answer to the caller's question, and returning only one of
        /// them would be choosing between candidates the measurement cannot separate.
        /// </summary>
        public static List<int> TiedIndices(IList<double> distancesFeet, bool farthest)
        {
            var result = new List<int>();
            if (distancesFeet == null || distancesFeet.Count == 0) return result;
            double best = farthest ? distancesFeet.Max() : distancesFeet.Min();
            for (int i = 0; i < distancesFeet.Count; i++)
                if (Math.Abs(distancesFeet[i] - best) <= EquivalenceToleranceFeet)
                    result.Add(i);
            return result;
        }

        // ---- fingerprint ----------------------------------------------------------

        /// <summary>
        /// SHA-256 hex over the canonical facts. Field order cannot matter (names are
        /// sorted) and units cannot matter (facts are internal feet, quantised on the
        /// 0.1 mm grid before hashing). The fingerprint identifies GEOMETRY: the same
        /// face reached through two selectors hashes the same.
        /// </summary>
        public static string GeometryFingerprint(GeometryFacts facts)
        {
            if (facts == null || facts.Count == 0)
                throw new ArgumentException("A fingerprint over zero facts would give every empty candidate the " +
                                            "same identity.", nameof(facts));
            return RequestFingerprint.Sha256Hex(facts.Canonical());
        }

        // ---- ordering -------------------------------------------------------------

        /// <summary>
        /// The one comparison behind the deterministic order. Everything ordinal, so
        /// no culture can reorder an answer; stable_representation is the final
        /// tiebreak for two references over coincident geometry.
        /// </summary>
        public static int CompareCandidates(CandidateKey a, CandidateKey b)
        {
            if (a == null || b == null)
                throw new ArgumentException("Candidate keys must not be null - a null key has no place in the order.");
            int c = a.ElementId.CompareTo(b.ElementId);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Selector ?? "", b.Selector ?? "");
            if (c != 0) return c;
            c = string.CompareOrdinal(a.ReferenceType ?? "", b.ReferenceType ?? "");
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Fingerprint ?? "", b.Fingerprint ?? "");
            if (c != 0) return c;
            return string.CompareOrdinal(a.StableRepresentation ?? "", b.StableRepresentation ?? "");
        }

        // ---- paging ---------------------------------------------------------------

        /// <summary>Range checks with explicit errors - a silently clamped limit answers a different question.</summary>
        public static string ValidatePaging(int? maxResults, int? offset, out int effectiveMax, out int effectiveOffset)
        {
            effectiveMax = maxResults ?? DefaultResults;
            effectiveOffset = offset ?? 0;
            if (effectiveMax < MinResults || effectiveMax > MaxResults)
                return "max_results must be between " + MinResults + " and " + MaxResults + " (default " +
                       DefaultResults + "); " + effectiveMax + " was sent.";
            if (effectiveOffset < 0)
                return "offset must be >= 0; " + effectiveOffset + " was sent.";
            return null;
        }

        /// <summary>
        /// One page over an exact, already-computed total. Candidates are ALL computed
        /// first and paged after, so Total and Truncated are facts about the whole
        /// answer, never about how much of it happened to be looked at. An offset past
        /// the end returns an empty page with Truncated=false - the caller has walked
        /// past the last row, and the exact Total says so.
        /// </summary>
        public static PageSlice Page(int total, int maxResults, int offset)
        {
            if (total < 0) throw new ArgumentException("total cannot be negative.", nameof(total));
            int count = Math.Max(0, Math.Min(maxResults, total - offset));
            return new PageSlice
            {
                Total = total,
                Offset = offset,
                Count = count,
                Truncated = offset + count < total
            };
        }

        // ---- units ----------------------------------------------------------------

        /// <summary>mm | m | feet, to and from internal feet. Anything else refuses.</summary>
        public static bool TryUnitScales(string units, out double toFeet, out double fromFeet)
        {
            switch (units)
            {
                case "feet": toFeet = 1.0; fromFeet = 1.0; return true;
                case "m": toFeet = 1.0 / 0.3048; fromFeet = 0.3048; return true;
                case "mm": toFeet = 1.0 / 304.8; fromFeet = 304.8; return true;
                default: toFeet = 0; fromFeet = 0; return false;
            }
        }
    }
}
