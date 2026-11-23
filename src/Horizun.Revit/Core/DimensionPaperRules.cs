// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The decisions behind "what the PAPER shows" for a dimension (backlog P4, field
// feedback 2026-11-17, Revit 2024 workshared model with links). Pure rules, no
// Revit: the commands read Revit and hand the facts here.
//
//   * DimensionPrintRules   - will the dimension print (a dimension whose reference
//                             lives in a hidden link or element exists, has no box
//                             in the view and never reaches the sheet), and the text
//                             the sheet prints (the TYPE's unit format, not the
//                             project's - Dimension.ValueString uses the project's).
//   * DimensionPatternRules - surface-pattern (tile joint) references
//                             '<face stable>/k', the bounded search for the joint
//                             nearest a point, and the "segment falls outside the
//                             element" check (a joint is an infinite line).
//   * SpotElevationRules    - why NewSpotElevation refuses: a point on the edge of
//                             the face, or outside the visible depth of the view.
//   * DimensionLinkRules    - references Revit drops in silence (a linked grid
//                             handed to NewDimension), host level planes as
//                             'level:<id>', and symbol-geometry references wrapped
//                             as instance references.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Whether a dimension reaches the sheet, and what it prints.</summary>
    public static class DimensionPrintRules
    {
        public const string ReasonNoBox = "no_box_in_view";
        public const string ReasonElementHidden = "referenced_element_hidden";
        public const string ReasonCategoryHidden = "referenced_category_hidden";
        public const string ReasonLinkHidden = "link_instance_hidden";
        public const string ReasonLinkCategoryHidden = "links_category_hidden";
        public const string ReasonOutsideCrop = "outside_crop";
        public const string ReasonDimensionHidden = "dimension_hidden";
        public const string ReasonDimensionCategoryHidden = "dimension_category_hidden";
        public const string ReasonTemplateHidesCategory = "view_template_hides_category";
        public const string ReasonWorksetHidden = "workset_hidden";

        public const string SourceDimensionType = "dimension_type";
        public const string SourceProject = "project";

        public sealed class Verdict
        {
            /// <summary>true prints, false will not print, null could not be decided.</summary>
            public bool? WillPrint { get; set; }
            public List<string> Reasons { get; } = new List<string>();
            public List<string> Unknown { get; } = new List<string>();
        }

        /// <summary>
        /// hasBox: the dimension's bounding box in its view (null = not read, e.g. in
        /// a rehearsal where Revit has not materialised it). hidden: one entry per
        /// reference that is hidden, in "code: detail" form. unknown: facts that could
        /// not be read. Any hidden reference or a missing box decides "will not print";
        /// otherwise an unread fact keeps the verdict open, never true.
        /// </summary>
        public static Verdict Evaluate(bool? hasBox, IEnumerable<string> hidden, IEnumerable<string> unknown)
        {
            var v = new Verdict();
            if (hidden != null) v.Reasons.AddRange(hidden.Where(h => !string.IsNullOrEmpty(h)));
            if (hasBox == false)
                v.Reasons.Add(ReasonNoBox + ": the dimension has no bounding box in its view, so nothing of it is drawn");
            if (unknown != null) v.Unknown.AddRange(unknown.Where(u => !string.IsNullOrEmpty(u)));
            if (v.Reasons.Count > 0) v.WillPrint = false;
            else if (hasBox == true && v.Unknown.Count == 0) v.WillPrint = true;
            else v.WillPrint = null;
            return v;
        }

        /// <summary>
        /// Facts of the dimension itself in its view. Null means "not read" and goes to
        /// unknown. View filters are not evaluated (their rules are arbitrary): any filter
        /// on the view goes to unknown. Worksets only count in a workshared model.
        /// insideCrop false is a reason also when the caller allowed outside_crop: it is
        /// allowed to exist there, not to print.
        /// </summary>
        public static void DimensionFacts(bool? insideCrop, bool? dimensionHidden, bool? categoryHidden,
                                          bool? templateHidesCategory, int? viewFilterCount, bool workshared,
                                          bool? worksetVisible, List<string> hidden, List<string> unknown)
        {
            if (insideCrop == false)
                hidden.Add(ReasonOutsideCrop + ": the dimension lies outside the view's crop region");
            else if (insideCrop == null) unknown.Add("crop: whether the dimension lies inside the crop was not read");
            if (dimensionHidden == true) hidden.Add(ReasonDimensionHidden + ": the dimension is hidden in this view");
            else if (dimensionHidden == null) unknown.Add("dimension_hidden: not read");
            if (categoryHidden == true)
                hidden.Add(ReasonDimensionCategoryHidden + ": the dimension's category is hidden in this view");
            else if (categoryHidden == null) unknown.Add("dimension_category: visibility not read");
            if (templateHidesCategory == true)
                hidden.Add(ReasonTemplateHidesCategory + ": the view template hides the dimension's category");
            else if (templateHidesCategory == null) unknown.Add("view_template: visibility of the category not read");
            if (viewFilterCount == null) unknown.Add(UnknownViewFilters + ": not read");
            else if (viewFilterCount.Value > 0)
                unknown.Add(UnknownViewFilters + ": " + viewFilterCount.Value + " filter(s) on the view are not evaluated");
            if (workshared)
            {
                if (worksetVisible == false) hidden.Add(ReasonWorksetHidden + ": the dimension's workset is hidden in this view");
                else if (worksetVisible == null) unknown.Add("workset: visibility not read");
            }
        }

        /// <summary>
        /// Whether the view's TEMPLATE hides the dimension's category. A template that
        /// does not control V/G Annotation (VIS_GRAPHICS_ANNOTATION is among its
        /// non-controlled parameters) leaves annotation visibility to the view, so its
        /// own category setting does not count. No template: false. Unread: null.
        /// </summary>
        public static bool? TemplateHidesCategory(bool hasTemplate, bool? controlsAnnotation, bool? templateCategoryHidden)
        {
            if (!hasTemplate) return false;
            if (controlsAnnotation == false) return false;
            if (controlsAnnotation == null) return null;
            return templateCategoryHidden;
        }

        public const string UnknownViewFilters = "view_filters";

        /// <summary>
        /// The refusal for require_printable, or null when the dimension may stand.
        /// Committed: only a confirmed true stands (null is not printable). Rehearsal:
        /// the box does not exist yet, so a false or any unknown fact refuses.
        /// </summary>
        public static string RequirePrintableError(int index, Verdict v, bool committed)
        {
            if (v == null) return null;
            if (v.WillPrint == false)
                return "actions[" + index + "]: the dimension would exist but NOT PRINT (" + string.Join("; ", v.Reasons) +
                       "). require_printable=true refuses it. Show the link/element/category in this view, or dimension " +
                       "to something visible. Nothing was written.";
            bool undecided = committed ? v.WillPrint != true : v.Unknown.Count > 0;
            if (!undecided) return null;
            string filters = v.Unknown.FirstOrDefault(u => u.StartsWith(UnknownViewFilters + ":", StringComparison.Ordinal));
            if (filters != null)
                return "actions[" + index + "]: refused because the view's FILTERS cannot be evaluated (" + filters +
                       "): a filter may hide the dimension or what it measures, so it is not confirmed to print. " +
                       "require_printable=true does not accept that. Disable the filters in this view, or dimension in " +
                       "a view without them. Nothing was written.";
            return "actions[" + index + "]: whether the dimension PRINTS could not be confirmed (" +
                   (v.Unknown.Count > 0 ? string.Join("; ", v.Unknown) : "no box read in the view") +
                   "). require_printable=true does not count an undecided verdict as printable. Nothing was written.";
        }

        /// <summary>
        /// Which unit format the sheet uses. A dimension TYPE that does not "use project
        /// settings" prints with its own format; Dimension.ValueString formats with the
        /// project's, which is what the field report caught printing a different text.
        /// </summary>
        public static string FormatSource(bool? typeUsesProjectUnits)
            => typeUsesProjectUnits == false ? SourceDimensionType : SourceProject;

        /// <summary>
        /// The text one segment prints: the override REPLACES the measured value; prefix
        /// and suffix stay around it. Above/below are separate lines and are not part of it.
        /// </summary>
        public static string Compose(string prefix, string formattedValue, string suffix, string valueOverride)
        {
            string core = !string.IsNullOrEmpty(valueOverride) ? valueOverride : formattedValue;
            if (core == null) return null;
            return (prefix ?? "") + core + (suffix ?? "");
        }
    }

    /// <summary>Surface-pattern (tile joint) references.</summary>
    public static class DimensionPatternRules
    {
        /// <summary>Highest k tried when looking for the joint nearest a point.</summary>
        public const int MaxIndex = 256;
        /// <summary>The search stops after this many consecutive indices Revit would not dimension.</summary>
        public const int GiveUpAfterMisses = 64;
        public const string NearestToken = "/?";
        /// <summary>A pattern line has no readable geometry: Revit resolves it to the whole face.</summary>
        public const string CodePatternLine = "pattern_line_geometry_not_measured";

        /// <summary>'&lt;face stable&gt;/k' with k a non-negative integer.</summary>
        public static bool TryParse(string stable, out string face, out int k)
        {
            face = null; k = -1;
            if (string.IsNullOrEmpty(stable)) return false;
            int slash = stable.LastIndexOf('/');
            if (slash <= 0 || slash == stable.Length - 1) return false;
            string tail = stable.Substring(slash + 1);
            if (!tail.All(char.IsDigit)) return false;
            if (!int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out k)) return false;
            string head = stable.Substring(0, slash);
            // A link reference is 'host:0:RVTLINK/linked...': the part after its slash is
            // not all digits, so it never reaches here. A face reference ends in SURFACE.
            if (!head.EndsWith(":SURFACE", StringComparison.Ordinal)) { k = -1; return false; }
            face = head;
            return true;
        }

        /// <summary>'&lt;face stable&gt;/?' asks for the joint nearest a pattern point.</summary>
        public static bool IsNearestRequest(string stable, out string face)
        {
            face = null;
            if (string.IsNullOrEmpty(stable) || !stable.EndsWith(NearestToken, StringComparison.Ordinal)) return false;
            face = stable.Substring(0, stable.Length - NearestToken.Length);
            return face.Length > 0;
        }

        /// <summary>How far outside the face the temporary probe plane goes.</summary>
        public const double ProbePlaneClearanceFeet = 1.0;
        /// <summary>Offset from the point when the face extent is unreadable (an odd 4.2 mm, not a tile module).</summary>
        public const double ProbePlaneFallbackOffsetFeet = 4.2 / 304.8;

        /// <summary>
        /// One probe dimension, from the plane at planeS to joint k: its midpoint is
        /// halfway, so the joint sits at 2*mid - planeS. The distance to the point is
        /// measured from that position. Null when the value or midpoint is unread, or
        /// they disagree by more than 1 mm (the probe did not measure what it should).
        /// </summary>
        public static JointCandidate FromProbe(int k, double planeS, double? value, double? midS, double pointS)
        {
            if (!value.HasValue || !midS.HasValue || value.Value < 0 || double.IsNaN(value.Value)) return null;
            double pos = 2 * midS.Value - planeS;
            if (Math.Abs(Math.Abs(pos - planeS) - value.Value) > 1.0 / 304.8) return null;
            return new JointCandidate { K = k, Distance = Math.Abs(pos - pointS), Position = pos };
        }

        /// <summary>One probed joint: its index, its distance from the point, and its position along the line.</summary>
        public sealed class JointCandidate
        {
            public int K { get; set; }
            public double Distance { get; set; }
            /// <summary>Scalar position along the dimension direction; null when unreadable.</summary>
            public double? Position { get; set; }
        }

        public sealed class JointChoice
        {
            public int K { get; set; }
            public double Distance { get; set; }
            public double? Position { get; set; }
            /// <summary>The point lies ON the joint (distance within the tolerance).</summary>
            public bool Exact { get; set; }
            /// <summary>Other indices that name the same physical joint (same position).</summary>
            public List<int> Aliases { get; } = new List<int>();
        }

        /// <summary>
        /// The joint nearest the point. A distance within the tolerance is an exact hit
        /// (the point ON the joint is the commonest request). Indices at the same
        /// position are one physical joint, kept as aliases of the lowest k, never
        /// "ambiguous". Two DIFFERENT joints within the tolerance of the best distance
        /// are ambiguous: choosing would be a guess. An index without a readable
        /// position is never merged.
        /// </summary>
        public static JointChoice Nearest(IReadOnlyList<JointCandidate> measured, double toleranceFeet, out string error)
        {
            error = null;
            var valid = measured == null
                ? new List<JointCandidate>()
                : measured.Where(m => m != null && !double.IsNaN(m.Distance) && m.Distance >= -toleranceFeet).ToList();
            if (valid.Count == 0)
            {
                error = "no pattern line of this face could be dimensioned from the point along the dimension line " +
                        "(indices 0.." + (MaxIndex - 1) + " tried). The face may have no surface pattern, or its joints " +
                        "run parallel to the dimension line.";
                return null;
            }
            var ordered = valid.OrderBy(m => Math.Max(0, m.Distance)).ThenBy(m => m.K).ToList();
            var joints = new List<JointChoice>();
            foreach (JointCandidate c in ordered)
            {
                JointChoice same = c.Position.HasValue
                    ? joints.FirstOrDefault(j => j.Position.HasValue && Math.Abs(j.Position.Value - c.Position.Value) <= toleranceFeet)
                    : null;
                if (same != null) { same.Aliases.Add(c.K); continue; }
                joints.Add(new JointChoice { K = c.K, Distance = Math.Max(0, c.Distance), Position = c.Position });
            }
            foreach (JointChoice j in joints)
            {
                // The representative is the lowest index of the physical joint.
                int low = j.Aliases.Count == 0 ? j.K : Math.Min(j.K, j.Aliases.Min());
                if (low != j.K) { j.Aliases.Remove(low); j.Aliases.Add(j.K); j.K = low; }
                j.Aliases.Sort();
            }
            JointChoice best = joints[0];
            if (joints.Count > 1 && joints[1].Distance - best.Distance <= toleranceFeet)
            {
                error = "the point is equidistant from pattern lines /" + best.K + " and /" + joints[1].K +
                        " (two different joints); move it towards the joint you mean.";
                return null;
            }
            best.Exact = best.Distance <= toleranceFeet;
            return best;
        }

        /// <summary>
        /// The post-commit joint check, read from the committed dimension's own segment
        /// ends: it needs no face extent (a family or linked face has none to give).
        /// verified | failed, or unavailable only when the segment ends are unreadable.
        /// </summary>
        public static string JointCheck(IReadOnlyList<double> boundaries, IReadOnlyList<double> jointScalars,
                                        double toleranceFeet, out List<int> missing)
        {
            missing = new List<int>();
            if (boundaries == null || boundaries.Count == 0 || jointScalars == null) return "unavailable";
            missing = JointsMissingFromBoundaries(boundaries, jointScalars, toleranceFeet);
            return missing.Count == 0 ? "verified" : "failed";
        }

        /// <summary>
        /// Post-commit: every chosen joint must be where a segment of the committed
        /// dimension ends. Returns the indices (into jointScalars) that no boundary
        /// accounts for.
        /// </summary>
        public static List<int> JointsMissingFromBoundaries(IReadOnlyList<double> boundaries,
                                                            IReadOnlyList<double> jointScalars, double toleranceFeet)
        {
            var missing = new List<int>();
            if (jointScalars == null) return missing;
            for (int i = 0; i < jointScalars.Count; i++)
                if (boundaries == null || !boundaries.Any(b => Math.Abs(b - jointScalars[i]) <= toleranceFeet))
                    missing.Add(i);
            return missing;
        }

        /// <summary>
        /// Positions, along the dimension direction, where its segments start and end:
        /// each segment contributes origin ± value/2. Coincident ends of neighbouring
        /// segments collapse into one boundary.
        /// </summary>
        public static List<double> BoundaryScalars(IReadOnlyList<double> originScalars, IReadOnlyList<double> values,
                                                   double toleranceFeet)
        {
            var raw = new List<double>();
            if (originScalars == null || values == null || originScalars.Count != values.Count) return raw;
            for (int i = 0; i < values.Count; i++)
            {
                raw.Add(originScalars[i] - values[i] / 2);
                raw.Add(originScalars[i] + values[i] / 2);
            }
            raw.Sort();
            var result = new List<double>();
            foreach (double s in raw)
                if (result.Count == 0 || s - result[result.Count - 1] > toleranceFeet) result.Add(s);
            return result;
        }

        public sealed class ExtentVerdict
        {
            /// <summary>verified | failed | unavailable</summary>
            public string Status { get; set; }
            public List<double> PatternBoundaries { get; } = new List<double>();
            public List<double> Outside { get; } = new List<double>();
            public string Reason { get; set; }
        }

        /// <summary>
        /// A joint is an infinite line: Revit measures to it wherever the dimension
        /// crosses it, also beyond the tiled face. The boundaries that a measurable
        /// (non-pattern) reference accounts for are set aside; the rest belong to the
        /// pattern references and each must fall inside the face's extent along the
        /// dimension. When the count does not add up the verdict is "unavailable",
        /// never a pass.
        /// </summary>
        public static ExtentVerdict CheckBoundaries(IReadOnlyList<double> boundaries, IReadOnlyList<double?> knownScalars,
                                                    int patternCount, double extentMin, double extentMax,
                                                    double toleranceFeet)
        {
            var v = new ExtentVerdict();
            if (boundaries == null || boundaries.Count == 0)
            { v.Status = "unavailable"; v.Reason = "the dimension's segment positions could not be read"; return v; }
            var remaining = new List<double>(boundaries);
            if (knownScalars != null)
                foreach (double? known in knownScalars)
                {
                    if (!known.HasValue) continue;
                    int at = remaining.FindIndex(b => Math.Abs(b - known.Value) <= toleranceFeet);
                    if (at >= 0) remaining.RemoveAt(at);
                }
            if (remaining.Count != patternCount)
            {
                v.Status = "unavailable";
                v.Reason = "the pattern references could not be told apart from the others (" + remaining.Count +
                           " unexplained boundaries for " + patternCount + " pattern reference(s))";
                return v;
            }
            v.PatternBoundaries.AddRange(remaining);
            foreach (double b in remaining)
                if (b < extentMin - toleranceFeet || b > extentMax + toleranceFeet) v.Outside.Add(b);
            v.Status = v.Outside.Count == 0 ? "verified" : "failed";
            if (v.Outside.Count > 0)
                v.Reason = v.Outside.Count + " segment end(s) on a pattern line fall OUTSIDE the patterned face (a joint " +
                           "is an infinite line; Revit measures to it beyond the element)";
            return v;
        }
    }

    /// <summary>NewSpotElevation's silent conditions, made explicit.</summary>
    public static class SpotElevationRules
    {
        /// <summary>A point closer than this to the face boundary is "on the edge".</summary>
        public const double EdgeToleranceFeet = 1.0 / 304.8;

        /// <summary>
        /// Face.Project's answer as "inside the face": no projection at all is NOT proof
        /// of "off the face" (it also happens on faces Revit cannot project to), so it is
        /// null - no pre-check, the rehearsal decides.
        /// </summary>
        public static bool? InsideFromProjection(bool projected, double distanceFeet, double toleranceFeet)
            => projected ? distanceFeet <= toleranceFeet : (bool?)null;

        /// <summary>insideFace/distanceToEdge null = could not be measured (no refusal).</summary>
        public static string EdgeError(int index, bool? insideFace, double? distanceToEdgeFeet)
        {
            if (insideFace == false)
                return "actions[" + index + "]: the spot point does not project INSIDE the referenced face; " +
                       "NewSpotElevation needs a point on the face. Move 'point' onto the face. Nothing was written.";
            if (distanceToEdgeFeet.HasValue && distanceToEdgeFeet.Value < EdgeToleranceFeet)
                return "actions[" + index + "]: the spot point lies on the EDGE of the referenced face (" +
                       (distanceToEdgeFeet.Value * 304.8).ToString("0.###", CultureInfo.InvariantCulture) +
                       " mm from it); NewSpotElevation fails there. Move 'point' at least 1 mm inside the face. " +
                       "Nothing was written.";
            return null;
        }

        /// <summary>
        /// depth: how far behind the cut plane (sections/elevations) or the elevation
        /// (plans) the point is; min/max: the visible band. Outside it Revit does not
        /// see the face and refuses the spot.
        /// </summary>
        public static string DepthError(int index, string viewKind, double value, double? min, double? max)
        {
            const double tol = 1.0 / 304.8;
            bool below = min.HasValue && value < min.Value - tol;
            bool above = max.HasValue && value > max.Value + tol;
            if (!below && !above) return null;
            string band = (min.HasValue ? (min.Value * 304.8).ToString("0", CultureInfo.InvariantCulture) : "-inf") + " .. " +
                          (max.HasValue ? (max.Value * 304.8).ToString("0", CultureInfo.InvariantCulture) : "+inf") + " mm";
            return "actions[" + index + "]: the spot point is OUTSIDE the visible " +
                   (viewKind == "plan" ? "view range of this plan (elevation " : "depth of this view (depth ") +
                   (value * 304.8).ToString("0", CultureInfo.InvariantCulture) + " mm, visible " + band +
                   "); Revit does not see the face there and NewSpotElevation fails. " +
                   (viewKind == "plan"
                       ? "Widen the view range (view depth / top, or cut plane / view depth on a ceiling plan), or use a view that sees the face."
                       : "Extend the far clip offset of the view, or move the section so it sees the face.") +
                   " Nothing was written.";
        }

        /// <summary>
        /// Whether a plan looks UP (reflected ceiling plan). Revit's ViewDirection points
        /// towards the viewer: a floor plan has Z &gt; 0, a ceiling plan Z &lt; 0. The view
        /// type and the sign must agree; when they do not, null (the pre-check is skipped
        /// and the rehearsal decides).
        /// </summary>
        public static bool? LooksUp(bool isCeilingPlan, double viewDirectionZ)
        {
            const double eps = 1e-6;
            if (isCeilingPlan && viewDirectionZ < -eps) return true;
            if (!isCeilingPlan && viewDirectionZ > eps) return false;
            return null;
        }

        /// <summary>
        /// The visible elevation band of a plan. Looking down: from the view depth up to
        /// the top clip. Looking up (ceiling plan): from the cut plane up to the view
        /// depth. False (not measurable, no pre-check) when the direction is unknown or
        /// the planes contradict it.
        /// </summary>
        public static bool PlanBand(bool? looksUp, double? cut, double? top, double? depth,
                                    out double? min, out double? max)
        {
            min = null; max = null;
            if (!looksUp.HasValue) return false;
            if (looksUp.Value)
            {
                if (cut.HasValue && depth.HasValue && depth.Value < cut.Value) return false;
                min = cut; max = depth;
            }
            else
            {
                if (top.HasValue && depth.HasValue && depth.Value > top.Value) return false;
                min = depth; max = top;
            }
            return true;
        }

        /// <summary>Revit's own failure, with the two causes named when the plan could not pre-check them.</summary>
        public static string FailureHint(string revitMessage)
            => (revitMessage ?? "NewSpotElevation failed") + " (NewSpotElevation fails when the point is on the edge " +
               "of the face or the face is outside the view's visible depth: move the point inside the face, or " +
               "widen the view depth.)";

        /// <summary>spot_slope has no creation API; the documented way to get one.</summary>
        public const string SlopeAlternative =
            "Alternative: two spot_elevation on the same face (the slope is their difference over their distance), " +
            "or place the spot slope by hand in Revit (Annotate > Spot Slope) and read it with horizun_query_dimensions.";
    }

    /// <summary>References that Revit drops, wraps or reaches by a token.</summary>
    public static class DimensionLinkRules
    {
        public const string LevelTokenPrefix = "level:";

        /// <summary>'level:&lt;id&gt;': the plane of a HOST level (Level.GetPlaneReference).</summary>
        public static bool TryParseLevelToken(string text, out long levelId)
        {
            levelId = 0;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(LevelTokenPrefix, StringComparison.Ordinal)) return false;
            string tail = text.Substring(LevelTokenPrefix.Length);
            return tail.Length > 0 && tail.All(char.IsDigit) &&
                   long.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out levelId) && levelId > 0;
        }

        /// <summary>
        /// A reference read from SYMBOL geometry names the family type, not the placed
        /// instance; Revit's own instance form is '&lt;instance uid&gt;:0:INSTANCE:&lt;symbol stable&gt;'.
        /// Null when it is already an instance reference or cannot be wrapped.
        /// </summary>
        public static string InstanceWrapped(string instanceUniqueId, string symbolStable)
        {
            if (string.IsNullOrEmpty(instanceUniqueId) || string.IsNullOrEmpty(symbolStable)) return null;
            if (symbolStable.StartsWith(instanceUniqueId + ":", StringComparison.Ordinal)) return null;
            if (symbolStable.IndexOf(":INSTANCE:", StringComparison.Ordinal) >= 0) return null;
            if (symbolStable.IndexOf(":RVTLINK", StringComparison.Ordinal) >= 0) return null;
            return instanceUniqueId + ":0:INSTANCE:" + symbolStable;
        }

        /// <summary>
        /// The requested references the committed dimension does NOT carry, by index.
        /// NewDimension ignores a linked grid in silence (field, Revit 2024): the
        /// dimension is created with one reference less and no error. Compared by owner
        /// key and canonical type, one read entry consumed per match.
        /// </summary>
        public static List<int> Dropped(IReadOnlyList<string> requestedReps, IReadOnlyList<string> requestedTypes,
                                        IReadOnlyList<string> readReps, IReadOnlyList<string> readTypes)
        {
            var dropped = new List<int>();
            if (requestedReps == null) return dropped;
            // A drop leaves FEWER references than asked. With as many read back as requested,
            // a key that does not match is the same reference spelled another way - a linked
            // face read back from the committed dimension does not always serialize as it was
            // requested (MEASURED 2026-11-21, Revit 2025: two linked wall faces reported dropped
            // from a dimension that cannot exist with one reference).
            if (readReps != null && readReps.Count >= requestedReps.Count) return dropped;
            var pool = new List<string>();
            if (readReps != null)
                for (int i = 0; i < readReps.Count; i++)
                    pool.Add(Key(readReps[i], readTypes != null && i < readTypes.Count ? readTypes[i] : null));
            for (int i = 0; i < requestedReps.Count; i++)
            {
                string key = Key(requestedReps[i], requestedTypes != null && i < requestedTypes.Count ? requestedTypes[i] : null);
                int at = pool.IndexOf(key);
                if (at >= 0) pool.RemoveAt(at); else dropped.Add(i);
            }
            return dropped;
        }

        private static string Key(string rep, string type)
            => DimensionPlanRules.ReferenceOwnerKeys(new[] { rep })[0] + "|" + DimensionPlanRules.CanonicalReferenceType(rep, type);

        public static string DroppedMessage(IReadOnlyList<int> dropped, IReadOnlyList<string> requestedReps)
        {
            if (dropped == null || dropped.Count == 0) return null;
            var named = dropped.Select(i => "references[" + i + "]" +
                (requestedReps != null && i < requestedReps.Count ? " (" + requestedReps[i] + ")" : ""));
            return "Revit created the dimension WITHOUT " + string.Join(", ", named) + ": NewDimension ignored " +
                   (dropped.Count == 1 ? "it" : "them") + " in silence (it does so with grids and levels of a link). " +
                   "The dimension is refused rather than measured to fewer references than asked; dimension the " +
                   "linked geometry (faces, edges) or a host datum instead.";
        }
    }

    /// <summary>
    /// The base a spot elevation / coordinate measures from. Revit exposes it only as
    /// the type parameter's display name (SPOT_ELEV_BASE / SPOT_COORDINATE_BASE), so
    /// the name is classified in the languages Revit ships with the common wording;
    /// an unrecognised name is null and the caller prints no text rather than guess.
    /// </summary>
    public static class SpotBaseRules
    {
        public const string ProjectBasePoint = "project_base_point";
        public const string SurveyPoint = "survey_point";
        public const string InternalOrigin = "internal_origin";
        public const string Relative = "relative";

        public static string Classify(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;
            string n = displayName.Trim().ToLowerInvariant();
            if (n.Contains("relativ")) return Relative;
            if (n.Contains("survey") || n.Contains("reconoc") || n.Contains("topogr") || n.Contains("shared") || n.Contains("compartid") ||
                n.Contains("partag") || n.Contains("vermess")) return SurveyPoint;
            if (n.Contains("internal") || n.Contains("interno") || n.Contains("interne") || n.Contains("intern"))
                return InternalOrigin;
            if (n.Contains("project") || n.Contains("proyecto") || n.Contains("projet") || n.Contains("projekt") ||
                n.Contains("progetto") || n.Contains("projeto")) return ProjectBasePoint;
            return null;
        }
    }
}
