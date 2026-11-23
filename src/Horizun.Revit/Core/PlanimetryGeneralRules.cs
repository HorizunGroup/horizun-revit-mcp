// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE GENERAL CASES OF AUTOMATIC PLANIMETRY, with no Revit in them.
//
// The first planimetry pass (Horizun Scan, October 2026) proved the rectangular
// room with one door per wall. Three generalisations were left open, and each one
// has a quiet way of producing a finished-looking but wrong drawing:
//
//   * ORTHOGONAL ROOMS (L, U, T...). The rectangle code picked a wall's inner
//     face as "the one closest to the room centre". In an L the centre of the
//     bounding box can sit OUTSIDE the room, and the re-entrant walls then swap
//     inner and outer faces - every interior dimension measures a wall thickness
//     too much. Faces are classified here against the boundary line itself and
//     its outward normal, which is right for any simple orthogonal loop.
//
//   * OPENINGS WITHOUT STRONG LEFT/RIGHT REFERENCES. The fallback measured the
//     family bounding box, which includes trim and frame, and accepted any wall
//     face within 50 mm. Here the jamb is decided by the wall's own void: the
//     face across the wall tangent that is nearest the opening centre on each
//     side AND FACES the opening. A neighbouring opening's jamb, or the wall's
//     own end face, faces the other way and can never be picked.
//
//   * THE TOP OF A WALL WHOSE TOP IS SLOPED. A linear dimension needs parallel
//     references; a sloped face is never parallel to a level, and a section's cut
//     edge is not a model edge. The rules below say which ASSOCIATIVE reference a
//     given view can actually carry - the horizontal top face, the two end points
//     of an edge-on top, or a horizontal end edge seen beyond the cut - and refuse
//     (with the alternative) when none can, instead of falling back to a datum
//     that will not follow the wall.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    // =========================================================================
    // Orthogonal rooms
    // =========================================================================

    /// <summary>One side of a simple orthogonal room loop, after collinear merging.</summary>
    public sealed class OrthogonalRoomSide
    {
        public int Index { get; set; }
        public long WallId { get; set; }

        /// <summary>"horizontal" when the side runs along the view's right axis, "vertical" otherwise.</summary>
        public string Axis { get; set; }

        /// <summary>The constant coordinate of the boundary line (Y for horizontal, X for vertical).</summary>
        public double Across { get; set; }

        /// <summary>Along-axis extent of the boundary segment.</summary>
        public double AlongMin { get; set; }
        public double AlongMax { get; set; }

        /// <summary>+1 or -1: which way is OUT of the room, on the across axis.</summary>
        public int Outward { get; set; }

        /// <summary>"top", "bottom", "right" or "left": the facade this side belongs to.</summary>
        public string Facade => Axis == "horizontal" ? (Outward > 0 ? "top" : "bottom")
                                                     : (Outward > 0 ? "right" : "left");
    }

    /// <summary>A face reference a room chain can be built from, already projected into the view.</summary>
    public sealed class RoomFaceFact
    {
        public string Stable { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>What one chain of the room plan is made of, before any Revit reference is attached.</summary>
    public sealed class RoomChainSpec
    {
        public string Role { get; set; }
        public string Kind { get; set; }             // interior | exterior_total | exterior_partial
        public string Axis { get; set; }             // horizontal | vertical
        public string Side { get; set; }             // positive | negative (view frame)

        /// <summary>The across coordinate the dimension line sits at, internal feet.</summary>
        public double LineAcross { get; set; }

        /// <summary>(side index, "room_inner" | "room_outer") in chain order.</summary>
        public List<KeyValuePair<int, string>> Members { get; } = new List<KeyValuePair<int, string>>();
    }

    public static class OrthogonalRoomRules
    {
        public const double Tolerance = AutoDimensionRules.CoincidenceToleranceFeet;

        /// <summary>
        /// Turn a room's boundary (in loop order, view-plane coordinates) into a simple
        /// orthogonal polygon. Consecutive collinear segments of the SAME wall merge
        /// (Revit splits a boundary where another element touches it); every other
        /// condition - a diagonal or curved side, a gap, a wall bounding the room on
        /// two separate sides, two collinear walls on one side - is refused by name,
        /// because each one makes "the inner face of this side" ambiguous.
        /// </summary>
        public static bool TryOrthogonalLoop(IEnumerable<RoomBoundarySide> raw,
            out List<OrthogonalRoomSide> loop, out string error)
        {
            loop = null;
            var segments = (raw ?? Enumerable.Empty<RoomBoundarySide>()).ToList();
            if (segments.Count < 4)
            {
                error = "An orthogonal room needs at least four boundary segments; " + segments.Count + " were read.";
                return false;
            }
            for (int i = 0; i < segments.Count; i++)
            {
                RoomBoundarySide s = segments[i], n = segments[(i + 1) % segments.Count];
                if (Math.Abs(s.X1 - n.X0) > Tolerance || Math.Abs(s.Y1 - n.Y0) > Tolerance)
                {
                    error = "The boundary is not a closed loop in order: segment " + i + " ends where segment " +
                            ((i + 1) % segments.Count) + " does not start.";
                    return false;
                }
                bool h = Math.Abs(s.Y0 - s.Y1) <= Tolerance && Math.Abs(s.X0 - s.X1) > Tolerance;
                bool v = Math.Abs(s.X0 - s.X1) <= Tolerance && Math.Abs(s.Y0 - s.Y1) > Tolerance;
                if (!h && !v)
                {
                    error = "Boundary segment " + i + " (wall " + s.WallId + ") is not aligned with the view axes; " +
                            "only orthogonal rooms are planned automatically.";
                    return false;
                }
            }

            // Merge consecutive collinear runs. The loop may START in the middle of a
            // run, so rotate to a corner first.
            int start = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                if (AxisOf(segments[i]) != AxisOf(segments[(i - 1 + segments.Count) % segments.Count])) { start = i; break; }
                if (i == segments.Count - 1)
                {
                    error = "The boundary has no corner.";
                    return false;
                }
            }
            var runs = new List<List<RoomBoundarySide>>();
            for (int k = 0; k < segments.Count; k++)
            {
                RoomBoundarySide s = segments[(start + k) % segments.Count];
                if (runs.Count > 0 && AxisOf(runs[runs.Count - 1][0]) == AxisOf(s)) runs[runs.Count - 1].Add(s);
                else runs.Add(new List<RoomBoundarySide> { s });
            }
            if (runs.Count < 4 || runs.Count % 2 != 0)
            {
                error = "The merged boundary has " + runs.Count + " sides; an orthogonal loop needs an even number of at least four.";
                return false;
            }

            double area2 = 0;
            foreach (RoomBoundarySide s in segments) area2 += s.X0 * s.Y1 - s.X1 * s.Y0;
            if (Math.Abs(area2) <= Tolerance * Tolerance)
            {
                error = "The boundary encloses no area.";
                return false;
            }
            bool ccw = area2 > 0;

            var result = new List<OrthogonalRoomSide>();
            foreach (List<RoomBoundarySide> run in runs)
            {
                var walls = run.Select(r => r.WallId).Distinct().ToList();
                if (walls.Count != 1)
                {
                    error = "One straight side of the room is bounded by " + walls.Count + " walls (" +
                            string.Join(", ", walls) + "); a chain over it would need a choice this planner does not make.";
                    return false;
                }
                RoomBoundarySide first = run[0], last = run[run.Count - 1];
                string axis = AxisOf(first);
                double dx = last.X1 - first.X0, dy = last.Y1 - first.Y0;
                // Interior is on the LEFT of a counter-clockwise walk, so outward is the right normal (dy, -dx).
                double ox = ccw ? dy : -dy, oy = ccw ? -dx : dx;
                var side = new OrthogonalRoomSide
                {
                    Index = result.Count, WallId = walls[0], Axis = axis,
                    Across = axis == "horizontal" ? run.Average(r => (r.Y0 + r.Y1) * 0.5) : run.Average(r => (r.X0 + r.X1) * 0.5),
                    AlongMin = axis == "horizontal" ? Math.Min(first.X0, last.X1) : Math.Min(first.Y0, last.Y1),
                    AlongMax = axis == "horizontal" ? Math.Max(first.X0, last.X1) : Math.Max(first.Y0, last.Y1),
                    Outward = axis == "horizontal" ? Math.Sign(oy) : Math.Sign(ox)
                };
                if (side.Outward == 0)
                {
                    error = "The outward direction of side " + side.Index + " could not be decided.";
                    return false;
                }
                result.Add(side);
            }
            var repeated = result.GroupBy(s => s.WallId).FirstOrDefault(g => g.Count() > 1);
            if (repeated != null)
            {
                error = "Wall " + repeated.Key + " bounds the room on " + repeated.Count() + " separate sides; its inner " +
                        "face is not one reference.";
                return false;
            }
            loop = result;
            error = null;
            return true;
        }

        public static bool IsRectangle(IReadOnlyCollection<OrthogonalRoomSide> loop) => loop != null && loop.Count == 4;

        /// <summary>
        /// Place a chain's dimension line at an explicit across coordinate (rather than
        /// relative to the references, which for a wall face is the middle of the wall):
        /// along the axis it spans the ordered references plus a witness tail.
        /// </summary>
        public static void PlaceLineAt(IList<AutoDimensionCandidate> ordered, string axis, double across,
            double tail, AutoDimensionChain chain)
        {
            if (ordered == null || ordered.Count == 0) throw new ArgumentException("A chain needs references.");
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            bool horizontal = axis == "horizontal";
            double lo = horizontal ? ordered.Min(c => c.X) : ordered.Min(c => c.Y);
            double hi = horizontal ? ordered.Max(c => c.X) : ordered.Max(c => c.Y);
            if (horizontal) { chain.StartX = lo - tail; chain.EndX = hi + tail; chain.StartY = chain.EndY = across; }
            else { chain.StartY = lo - tail; chain.EndY = hi + tail; chain.StartX = chain.EndX = across; }
            chain.Axis = axis;
        }

        private static string AxisOf(RoomBoundarySide s)
            => Math.Abs(s.Y0 - s.Y1) <= Tolerance ? "horizontal" : "vertical";

        /// <summary>
        /// Which of a wall's side faces is the room's (inner) and which is the outside
        /// (outer), judged against the boundary line and its outward normal - not
        /// against a room centre, which an L-shaped room can put outside itself. The
        /// inner face is the one ON the boundary line (Revit draws the room to the
        /// finish face); the outer face is the farthest one outward. A face inward of
        /// the boundary, or two faces at one position, refuses.
        /// </summary>
        public static bool TryClassifyFaces(OrthogonalRoomSide side, IEnumerable<RoomFaceFact> faces,
            double maxThickness, out RoomFaceFact inner, out RoomFaceFact outer, out string error)
        {
            inner = outer = null;
            if (side == null) throw new ArgumentNullException(nameof(side));
            var rows = (faces ?? Enumerable.Empty<RoomFaceFact>()).Where(f => !string.IsNullOrEmpty(f.Stable))
                .Select(f => new { Face = f, Signed = ((side.Axis == "horizontal" ? f.Y : f.X) - side.Across) * side.Outward })
                .ToList();
            if (rows.Count < 2)
            {
                error = "fewer than two side faces of wall " + side.WallId + " were read.";
                return false;
            }
            const double finishTolerance = 5.0 / 304.8;
            var onBoundary = rows.Where(r => Math.Abs(r.Signed) <= finishTolerance).OrderBy(r => Math.Abs(r.Signed)).ToList();
            var outside = rows.Where(r => r.Signed > finishTolerance && r.Signed <= maxThickness).OrderByDescending(r => r.Signed).ToList();
            if (onBoundary.Count == 0)
            {
                error = "no face of wall " + side.WallId + " lies on the room boundary (within 5 mm); the room may be " +
                        "computed to the wall centre or core, so its inner face is not the boundary.";
                return false;
            }
            if (onBoundary.Count > 1 && Math.Abs(Math.Abs(onBoundary[1].Signed) - Math.Abs(onBoundary[0].Signed)) <= Tolerance &&
                onBoundary[0].Face.Stable != onBoundary[1].Face.Stable)
            {
                error = "two faces of wall " + side.WallId + " lie on the room boundary at the same position.";
                return false;
            }
            if (outside.Count == 0)
            {
                error = "no face of wall " + side.WallId + " lies outward of the room boundary within the wall thickness.";
                return false;
            }
            inner = onBoundary[0].Face;
            outer = outside[0].Face;
            error = null;
            return true;
        }

        /// <summary>
        /// The architectural set for a simple orthogonal room, as specs over side
        /// indices. Per side: the clear interior span between its two neighbours'
        /// inner faces, drawn inside the room. Per facade (top, bottom, left, right):
        /// the overall exterior total between the extreme outer faces, and - where the
        /// outline steps - a partial chain through every outer face across that axis.
        /// Duplicate reference sets are dropped (a rectangle's two interior chains per
        /// axis are one measurement). Thickness and openings are added by the caller.
        /// </summary>
        public static List<RoomChainSpec> PlanChains(IReadOnlyList<OrthogonalRoomSide> loop,
            IReadOnlyDictionary<int, double> outerAcross, double insideOffset, double outsideOffset,
            double separation)
        {
            if (loop == null || loop.Count < 4) throw new ArgumentException("An orthogonal loop is required.", nameof(loop));
            if (outerAcross == null) throw new ArgumentNullException(nameof(outerAcross));
            var specs = new List<RoomChainSpec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int n = loop.Count;
            foreach (OrthogonalRoomSide side in loop)
            {
                OrthogonalRoomSide prev = loop[(side.Index - 1 + n) % n], next = loop[(side.Index + 1) % n];
                string key = "interior:" + Math.Min(prev.Index, next.Index) + "/" + Math.Max(prev.Index, next.Index);
                if (!seen.Add(key)) continue;
                var spec = new RoomChainSpec
                {
                    Role = "interior_w" + side.WallId.ToString(CultureInfo.InvariantCulture),
                    Kind = "interior", Axis = side.Axis,
                    Side = side.Outward > 0 ? "negative" : "positive",
                    LineAcross = side.Across - side.Outward * insideOffset
                };
                spec.Members.Add(new KeyValuePair<int, string>(prev.Index, "room_inner"));
                spec.Members.Add(new KeyValuePair<int, string>(next.Index, "room_inner"));
                specs.Add(spec);
            }

            foreach (string facade in new[] { "top", "bottom", "right", "left" })
            {
                bool horizontalChain = facade == "top" || facade == "bottom";
                int outward = facade == "top" || facade == "right" ? 1 : -1;
                var facing = loop.Where(s => s.Facade == facade).ToList();
                if (facing.Count == 0) continue;
                // The chain runs along the facade; its references are the outer faces of
                // the sides PERPENDICULAR to it, ordered along the chain axis.
                var across = loop.Where(s => s.Axis == (horizontalChain ? "vertical" : "horizontal"))
                    .Where(s => outerAcross.ContainsKey(s.Index))
                    .OrderBy(s => outerAcross[s.Index]).ToList();
                if (across.Count < 2) continue;
                double extreme = facing.Max(s => outerAcross.TryGetValue(s.Index, out double v) ? v * outward : s.Across * outward) * outward;
                // A perpendicular side is a STEP of this facade when one of its neighbours
                // faces the facade; the overall extremes always bound the chain. A notch
                // on the opposite facade is not drawn on this one.
                OrthogonalRoomSide first = across[0], last = across[across.Count - 1];
                var distinct = new List<OrthogonalRoomSide>();
                foreach (OrthogonalRoomSide s in across)
                {
                    bool step = s == first || s == last ||
                                loop[(s.Index - 1 + n) % n].Facade == facade || loop[(s.Index + 1) % n].Facade == facade;
                    if (!step) continue;
                    if (distinct.Count == 0 || Math.Abs(outerAcross[s.Index] - outerAcross[distinct[distinct.Count - 1].Index]) > Tolerance)
                        distinct.Add(s);
                }
                if (distinct.Count < 2) continue;
                var total = new RoomChainSpec
                {
                    Role = "exterior_" + facade + "_total", Kind = "exterior_total",
                    Axis = horizontalChain ? "horizontal" : "vertical",
                    Side = outward > 0 ? "positive" : "negative",
                    LineAcross = extreme + outward * (outsideOffset + separation + (distinct.Count > 2 ? separation : 0))
                };
                total.Members.Add(new KeyValuePair<int, string>(distinct[0].Index, "room_outer"));
                total.Members.Add(new KeyValuePair<int, string>(distinct[distinct.Count - 1].Index, "room_outer"));
                specs.Add(total);
                if (distinct.Count > 2)
                {
                    var partial = new RoomChainSpec
                    {
                        Role = "exterior_" + facade + "_partial", Kind = "exterior_partial",
                        Axis = total.Axis, Side = total.Side,
                        LineAcross = extreme + outward * (outsideOffset + separation)
                    };
                    foreach (OrthogonalRoomSide s in distinct) partial.Members.Add(new KeyValuePair<int, string>(s.Index, "room_outer"));
                    specs.Add(partial);
                }
            }
            return specs;
        }
    }

    // =========================================================================
    // Openings measured on the wall's own void
    // =========================================================================

    /// <summary>
    /// A planar face of a host wall, reduced to what jamb, head and sill selection
    /// needs. Along is the face's position on the wall tangent (feet from any fixed
    /// origin on the location line); NormalAlong and NormalZ are the components of
    /// its unit normal on the tangent and on Z; the extents are the face's own.
    /// </summary>
    public sealed class WallVoidFace
    {
        public string Stable { get; set; }
        public double Along { get; set; }
        public double Z { get; set; }
        public double NormalAlong { get; set; }
        public double NormalZ { get; set; }
        public double AlongMin { get; set; }
        public double AlongMax { get; set; }
        public double ZMin { get; set; }
        public double ZMax { get; set; }
    }

    public sealed class OpeningVoidChoice
    {
        public WallVoidFace Left { get; set; }
        public WallVoidFace Right { get; set; }
        public WallVoidFace Head { get; set; }
        public WallVoidFace Sill { get; set; }
        public double? Width => Left != null && Right != null ? Right.Along - Left.Along : (double?)null;
        public double? Height => Head != null ? Head.Z - (Sill?.Z ?? double.NaN) : (double?)null;
        public List<string> Notes { get; } = new List<string>();
    }

    public static class OpeningVoidRules
    {
        /// <summary>A face is "across the tangent" when its normal is within ~11 degrees of it.</summary>
        public const double AxisCosine = 0.98;

        /// <summary>
        /// The jambs, head and sill of ONE opening, read from the wall's void. A jamb
        /// faces the opening: the left one (smaller Along) has its normal pointing
        /// toward +tangent, the right one toward -tangent; the nearest such face on
        /// each side of the centre that spans the opening's mid height wins. The head
        /// faces down and is the lowest such face above mid height; the sill faces up
        /// and is the highest such face below it, absent for a door whose void reaches
        /// the wall base. The wall's own end faces face AWAY from every opening and a
        /// neighbouring opening's jambs are farther than this one's, so neither can be
        /// chosen by accident.
        /// </summary>
        public static OpeningVoidChoice Choose(IEnumerable<WallVoidFace> faces, double centerAlong, double midZ,
            double? nominalWidth, out string error)
        {
            var list = (faces ?? Enumerable.Empty<WallVoidFace>()).Where(f => !string.IsNullOrEmpty(f.Stable)).ToList();
            var choice = new OpeningVoidChoice();
            double t = AutoDimensionRules.CoincidenceToleranceFeet;
            var jambs = list.Where(f => Math.Abs(f.NormalAlong) >= AxisCosine && f.ZMin - t <= midZ && midZ <= f.ZMax + t).ToList();
            choice.Left = jambs.Where(f => f.NormalAlong > 0 && f.Along < centerAlong - t)
                .OrderByDescending(f => f.Along).ThenBy(f => f.Stable, StringComparer.Ordinal).FirstOrDefault();
            choice.Right = jambs.Where(f => f.NormalAlong < 0 && f.Along > centerAlong + t)
                .OrderBy(f => f.Along).ThenBy(f => f.Stable, StringComparer.Ordinal).FirstOrDefault();
            if (choice.Left == null || choice.Right == null)
            {
                error = "the wall void has no " + (choice.Left == null ? "left" : "right") + " jamb face facing the " +
                        "opening at its mid height; the opening may not cut its host (a face-based or unhosted family).";
                return null;
            }
            double lo = choice.Left.Along, hi = choice.Right.Along;
            var caps = list.Where(f => Math.Abs(f.NormalZ) >= AxisCosine &&
                                       f.AlongMin - t <= lo + t && hi - t <= f.AlongMax + t).ToList();
            // A head/sill face must span the jambs; a face that spans more is the wall's
            // own top or bottom and faces the other way.
            choice.Head = caps.Where(f => f.NormalZ < 0 && f.Z > midZ + t)
                .OrderBy(f => f.Z).ThenBy(f => f.Stable, StringComparer.Ordinal).FirstOrDefault();
            choice.Sill = caps.Where(f => f.NormalZ > 0 && f.Z < midZ - t)
                .OrderByDescending(f => f.Z).ThenBy(f => f.Stable, StringComparer.Ordinal).FirstOrDefault();
            if (choice.Head == null)
                choice.Notes.Add("no head face spans the jambs; the void may reach the top of the wall.");
            if (choice.Sill == null)
                choice.Notes.Add("no sill face: the void reaches the wall base, so the sill is the base level (a door).");
            if (nominalWidth.HasValue && Math.Abs(choice.Width.Value - nominalWidth.Value) > 5.0 / 304.8)
                choice.Notes.Add("the void is " + Math.Round((choice.Width.Value - nominalWidth.Value) * 304.8, 1)
                    .ToString(CultureInfo.InvariantCulture) + " mm wider than the type width (rough opening or frame).");
            error = null;
            return choice;
        }

        /// <summary>The void face a family-reference selector falls back to, or null.</summary>
        public static WallVoidFace ForSelector(OpeningVoidChoice choice, string selector)
        {
            if (choice == null) return null;
            switch (selector)
            {
                case DimensionReferenceRules.SelectorOpeningLeft: return choice.Left;
                case DimensionReferenceRules.SelectorOpeningRight: return choice.Right;
                case DimensionReferenceRules.SelectorOpeningHead: return choice.Head;
                case DimensionReferenceRules.SelectorOpeningSill: return choice.Sill;
                default: return null;
            }
        }
    }

    // =========================================================================
    // The top of a wall, in the view that will carry the dimension
    // =========================================================================

    /// <summary>One point of a top-face edge, in the view frame: U = right, Depth = along the view direction from the cut.</summary>
    public sealed class ViewPointFact
    {
        public double U { get; set; }
        public double Z { get; set; }
        public double Depth { get; set; }
        public string Stable { get; set; }
    }

    public sealed class TopEdgeFact
    {
        public string Stable { get; set; }
        public ViewPointFact Start { get; set; }
        public ViewPointFact End { get; set; }
    }

    public sealed class TopFaceFact
    {
        public string Stable { get; set; }
        public double NormalZ { get; set; }

        /// <summary>|normal . view direction|: 0 when the face is seen edge-on.</summary>
        public double NormalDotView { get; set; }
        public double Elevation { get; set; }
        public double DepthMin { get; set; }
        public double DepthMax { get; set; }
        public List<TopEdgeFact> Edges { get; } = new List<TopEdgeFact>();
    }

    public sealed class TopReferenceChoice
    {
        public string Mode { get; set; }            // horizontal_face | edge_on_end_points | end_edge_beyond_cut
        public List<KeyValuePair<string, ViewPointFact>> References { get; } = new List<KeyValuePair<string, ViewPointFact>>();
        public string Note { get; set; }
    }

    public static class WallTopReferenceRules
    {
        public const string ModeHorizontalFace = "horizontal_face";
        public const string ModeEdgeOnEndPoints = "edge_on_end_points";
        public const string ModeEndEdgeBeyondCut = "end_edge_beyond_cut";
        public const string CodeNotDimensionable = "sloped_top_not_dimensionable_in_view";

        /// <summary>cos(0.5 deg): how horizontal a face or edge must be to be parallel to a level.</summary>
        public const double LevelCosine = 0.99996192;
        public const double EdgeOnTolerance = 0.0087;   // sin(0.5 deg)

        /// <summary>
        /// An edge AT the cut plane is the section's own cut line, not a model edge: Revit
        /// takes its CUT_EDGE reference into a dimension and measures zero (live, 2026).
        /// An end edge must lie at least 1 mm beyond the cut.
        /// </summary>
        public const double BeyondCutMinimum = 1.0 / 304.8;

        /// <summary>
        /// Which associative reference a vertical dimension from a level can take to
        /// the top of a wall in THIS view. In order:
        ///   1. a horizontal top face (parallel to the level) - the face itself, the one
        ///      the cut crosses first, highest first;
        ///   2. a sloped top seen edge-on (an elevation or a section parallel to the
        ///      wall): the two END POINTS of that line, one dimension each, because one
        ///      sloped face is not one height;
        ///   3. a sloped top seen obliquely (a section across the wall): a horizontal
        ///      edge of the top face lying parallel to the view plane at or beyond the
        ///      cut - the wall's end at that side, which IS a model edge, unlike the
        ///      cut line. Its height is the end height, and the note says how far
        ///      beyond the cut it is;
        /// and otherwise a refusal naming the views and annotations that can.
        /// </summary>
        public static TopReferenceChoice Choose(IEnumerable<TopFaceFact> faces, out string code, out string error)
        {
            code = null; error = null;
            var list = (faces ?? Enumerable.Empty<TopFaceFact>()).Where(f => f.NormalZ > 0.1).ToList();
            if (list.Count == 0)
            {
                code = CodeNotDimensionable;
                error = "the wall exposes no upward-facing top face with a reference.";
                return null;
            }
            double t = AutoDimensionRules.CoincidenceToleranceFeet;
            var horizontal = list.Where(f => f.NormalZ >= LevelCosine && !string.IsNullOrEmpty(f.Stable)).ToList();
            if (horizontal.Count > 0)
            {
                TopFaceFact best = horizontal
                    .OrderBy(f => f.DepthMin - t <= 0 && 0 <= f.DepthMax + t ? 0 : 1)
                    .ThenBy(f => Math.Min(Math.Abs(f.DepthMin), Math.Abs(f.DepthMax)))
                    .ThenByDescending(f => f.Elevation)
                    .ThenBy(f => f.Stable, StringComparer.Ordinal).First();
                var choice = new TopReferenceChoice { Mode = ModeHorizontalFace,
                    Note = "horizontal top face: parallel to the level and associative." };
                choice.References.Add(new KeyValuePair<string, ViewPointFact>("top", new ViewPointFact {
                    Stable = best.Stable, Z = best.Elevation, Depth = (best.DepthMin + best.DepthMax) * 0.5 }));
                return choice;
            }

            var edgeOn = list.Where(f => f.NormalDotView <= EdgeOnTolerance).ToList();
            if (edgeOn.Count > 0)
            {
                var points = edgeOn.SelectMany(f => f.Edges).SelectMany(e => new[] { e.Start, e.End })
                    .Where(p => p != null && !string.IsNullOrEmpty(p.Stable)).ToList();
                if (points.Count >= 2)
                {
                    double uMin = points.Min(p => p.U), uMax = points.Max(p => p.U);
                    if (uMax - uMin > t)
                    {
                        ViewPointFact Pick(double u) => points.Where(p => Math.Abs(p.U - u) <= t)
                            .OrderByDescending(p => p.Z).ThenBy(p => p.Depth).ThenBy(p => p.Stable, StringComparer.Ordinal).First();
                        var choice = new TopReferenceChoice { Mode = ModeEdgeOnEndPoints,
                            Note = "sloped top seen edge-on: one vertical dimension from the level to each end point of the top edge." };
                        choice.References.Add(new KeyValuePair<string, ViewPointFact>("start", Pick(uMin)));
                        choice.References.Add(new KeyValuePair<string, ViewPointFact>("end", Pick(uMax)));
                        return choice;
                    }
                }
            }

            var endEdges = list.SelectMany(f => f.Edges)
                .Where(e => e.Start != null && e.End != null && !string.IsNullOrEmpty(e.Stable) &&
                            Math.Abs(e.Start.Z - e.End.Z) <= t &&                  // horizontal
                            Math.Abs(e.Start.Depth - e.End.Depth) <= t &&          // parallel to the view plane
                            Math.Abs(e.Start.U - e.End.U) > t &&                   // seen as a line, not a point
                            Math.Min(e.Start.Depth, e.End.Depth) > BeyondCutMinimum) // strictly beyond the cut
                .OrderBy(e => e.Start.Depth).ThenByDescending(e => e.Start.Z).ThenBy(e => e.Stable, StringComparer.Ordinal)
                .ToList();
            if (endEdges.Count > 0)
            {
                TopEdgeFact edge = endEdges[0];
                var choice = new TopReferenceChoice { Mode = ModeEndEdgeBeyondCut,
                    Note = "sloped top seen across: the horizontal top edge at the wall end " +
                           Math.Round(edge.Start.Depth * 304.8, 1).ToString(CultureInfo.InvariantCulture) +
                           " mm beyond the cut. It measures the end height, not the height at the cut; " +
                           "the rehearsal decides whether it is visible." };
                choice.References.Add(new KeyValuePair<string, ViewPointFact>("end_edge", new ViewPointFact {
                    Stable = edge.Stable, U = (edge.Start.U + edge.End.U) * 0.5, Z = edge.Start.Z, Depth = edge.Start.Depth }));
                return choice;
            }

            code = CodeNotDimensionable;
            error = "the top of this wall is sloped and this view sees it neither edge-on nor through a horizontal end " +
                    "edge, so no associative linear dimension to a level exists here. Dimension it in an elevation or a " +
                    "section parallel to the wall (two end points), or place a spot elevation on the top face.";
            return null;
        }
    }

    // =========================================================================
    // Composition: what lies far outside a view, and how far a chain may move
    // =========================================================================

    public static class ViewCompositionRules
    {
        /// <summary>
        /// An element whose view-plane box lies ENTIRELY outside the crop grown by
        /// margin. Measured live (Revit 2026, Imperial template): far elevation markers
        /// and base points stretch a viewport past an A-D sheet even with the
        /// annotation crop on, and pack_sheets rightly refuses. Touching counts as inside.
        /// </summary>
        public static bool IsFarOutside(PlanBox element, PlanBox crop, double margin)
        {
            if (!element.Valid || !crop.Valid) return false;
            PlanBox grown = PlanBox.FromCorners(crop.MinX - margin, crop.MinY - margin, crop.MaxX + margin, crop.MaxY + margin);
            return !PlanimetryGeometry.Overlaps(grown, element, 0) && !PlanimetryGeometry.Contains(grown, element, 0);
        }

        /// <summary>
        /// The box a room dimension line may move within. A line that starts INSIDE the
        /// room (a clear interior span) stays inside the room box; any other chain stays
        /// within the room box grown by margin. Measured live: without this the collision
        /// shift moved an interior chain 3150 mm out of an L-shaped room.
        /// </summary>
        public static PlanBox LayoutLimit(PlanBox room, PlanBox line, double margin)
            => LayoutLimit(room, line, margin, IsInteriorLine(room, line));

        /// <summary>As above, with the caller naming whether the chain is an interior span.</summary>
        public static PlanBox LayoutLimit(PlanBox room, PlanBox line, double margin, bool interior)
        {
            // An interior chain spans the room ALONG its axis and its witness tails reach
            // past the walls (measured live: every interior chain was refused when the
            // room box bounded both directions). Only the ACROSS direction is the room's.
            bool horizontal = line.Width >= line.Height;
            if (interior)
                return horizontal
                    ? PlanBox.FromCorners(room.MinX - margin, room.MinY, room.MaxX + margin, room.MaxY)
                    : PlanBox.FromCorners(room.MinX, room.MinY - margin, room.MaxX, room.MaxY + margin);
            return PlanBox.FromCorners(room.MinX - margin, room.MinY - margin, room.MaxX + margin, room.MaxY + margin);
        }

        /// <summary>A line whose centre lies strictly inside the room box: an interior chain.</summary>
        public static bool IsInteriorLine(PlanBox room, PlanBox line)
        {
            double cx = (line.MinX + line.MaxX) * 0.5, cy = (line.MinY + line.MaxY) * 0.5;
            return room.Valid && line.Valid && cx > room.MinX && cx < room.MaxX && cy > room.MinY && cy < room.MaxY;
        }

        /// <summary>The intersection of two boxes, or an invalid box when they do not meet.</summary>
        public static PlanBox Intersect(PlanBox a, PlanBox b)
        {
            if (!a.Valid || !b.Valid) return PlanBox.Unreadable;
            double x0 = Math.Max(a.MinX, b.MinX), y0 = Math.Max(a.MinY, b.MinY), x1 = Math.Min(a.MaxX, b.MaxX), y1 = Math.Min(a.MaxY, b.MaxY);
            return x1 < x0 || y1 < y0 ? PlanBox.Unreadable : PlanBox.FromCorners(x0, y0, x1, y1);
        }
    }}
