// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The general planimetry cases (P7 of the Scan planimetry work): orthogonal rooms
// that are not rectangles, openings whose family has no strong Left/Right, and the
// top of a wall whose top is sloped. Each test pins a way the drawing could look
// finished and be wrong.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanimetryGeneralRulesTests
    {
        private const double Mm = 1.0 / 304.8;

        private static List<RoomBoundarySide> Loop(long firstWall, params double[] xy)
        {
            var sides = new List<RoomBoundarySide>();
            int n = xy.Length / 2;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                sides.Add(new RoomBoundarySide { WallId = firstWall + i,
                    X0 = xy[2 * i] * Mm, Y0 = xy[2 * i + 1] * Mm, X1 = xy[2 * j] * Mm, Y1 = xy[2 * j + 1] * Mm });
            }
            return sides;
        }

        // An L: 6000 x 4000 with the top-right 2500 x 1500 corner removed. Counter-clockwise.
        private static List<RoomBoundarySide> LRoom() => Loop(1,
            0, 0, 6000, 0, 6000, 2500, 3500, 2500, 3500, 4000, 0, 4000);

        [Fact]
        public void L_room_is_six_sides_with_outward_normals_out_of_the_room()
        {
            Assert.True(OrthogonalRoomRules.TryOrthogonalLoop(LRoom(), out var loop, out var error), error);
            Assert.Equal(6, loop.Count);
            var byWall = loop.ToDictionary(s => s.WallId);
            Assert.Equal("bottom", byWall[1].Facade);
            Assert.Equal("right", byWall[2].Facade);
            Assert.Equal("top", byWall[3].Facade);     // the re-entrant step at y = 2500 faces up
            Assert.Equal("right", byWall[4].Facade);   // the re-entrant step at x = 3500 faces right
            Assert.Equal("top", byWall[5].Facade);
            Assert.Equal("left", byWall[6].Facade);
        }

        [Fact]
        public void Clockwise_input_gives_the_same_outward_normals()
        {
            var cw = LRoom().AsEnumerable().Reverse().Select(s => new RoomBoundarySide { WallId = s.WallId,
                X0 = s.X1, Y0 = s.Y1, X1 = s.X0, Y1 = s.Y0 }).ToList();
            Assert.True(OrthogonalRoomRules.TryOrthogonalLoop(cw, out var loop, out var error), error);
            Assert.Equal("top", loop.Single(s => s.WallId == 3).Facade);
            Assert.Equal("left", loop.Single(s => s.WallId == 6).Facade);
        }

        [Fact]
        public void Collinear_pieces_of_one_wall_merge_and_the_loop_may_start_mid_run()
        {
            // The bottom wall is split in two by a touching element, and the loop starts on its second half.
            var sides = new List<RoomBoundarySide>
            {
                new RoomBoundarySide { WallId = 1, X0 = 2000 * Mm, Y0 = 0, X1 = 4000 * Mm, Y1 = 0 },
                new RoomBoundarySide { WallId = 2, X0 = 4000 * Mm, Y0 = 0, X1 = 4000 * Mm, Y1 = 3000 * Mm },
                new RoomBoundarySide { WallId = 3, X0 = 4000 * Mm, Y0 = 3000 * Mm, X1 = 0, Y1 = 3000 * Mm },
                new RoomBoundarySide { WallId = 4, X0 = 0, Y0 = 3000 * Mm, X1 = 0, Y1 = 0 },
                new RoomBoundarySide { WallId = 1, X0 = 0, Y0 = 0, X1 = 2000 * Mm, Y1 = 0 },
            };
            Assert.True(OrthogonalRoomRules.TryOrthogonalLoop(sides, out var loop, out var error), error);
            Assert.Equal(4, loop.Count);
            var bottom = loop.Single(s => s.WallId == 1);
            Assert.Equal(0, bottom.AlongMin, 6);
            Assert.Equal(4000 * Mm, bottom.AlongMax, 6);
        }

        [Fact]
        public void Two_walls_on_one_straight_side_and_diagonals_are_refused_by_name()
        {
            var twoWalls = Loop(1, 0, 0, 2000, 0, 4000, 0, 4000, 3000, 0, 3000);
            Assert.False(OrthogonalRoomRules.TryOrthogonalLoop(twoWalls, out _, out var e1));
            Assert.Contains("bounded by 2 walls", e1);
            var diagonal = Loop(1, 0, 0, 4000, 0, 4000, 3000, 1000, 3500, 0, 3000);
            Assert.False(OrthogonalRoomRules.TryOrthogonalLoop(diagonal, out _, out var e2));
            Assert.Contains("not aligned", e2);
        }

        [Fact]
        public void A_wall_bounding_two_separate_sides_is_refused()
        {
            var u = Loop(1, 0, 0, 6000, 0, 6000, 4000, 4000, 4000, 4000, 2000, 2000, 2000, 2000, 4000, 0, 4000);
            u[6].WallId = u[4].WallId;   // the same wall on both inner flanks of the notch... and not adjacent
            Assert.False(OrthogonalRoomRules.TryOrthogonalLoop(u, out _, out var error));
            Assert.Contains("separate sides", error);
        }

        [Fact]
        public void Faces_are_classified_against_the_boundary_not_a_centre()
        {
            Assert.True(OrthogonalRoomRules.TryOrthogonalLoop(LRoom(), out var loop, out _));
            // The re-entrant wall at y = 2500 faces up; its inner face is ON y = 2500 and its outer face is
            // 150 mm ABOVE, which is also closer to the bounding-box centre (3000, 2000) - the old rule swapped them.
            var step = loop.Single(s => s.WallId == 3);
            var faces = new[]
            {
                new RoomFaceFact { Stable = "outer", X = 4750 * Mm, Y = 2650 * Mm },
                new RoomFaceFact { Stable = "inner", X = 4750 * Mm, Y = 2500 * Mm },
            };
            Assert.True(OrthogonalRoomRules.TryClassifyFaces(step, faces, 600 * Mm, out var inner, out var outer, out var error), error);
            Assert.Equal("inner", inner.Stable);
            Assert.Equal("outer", outer.Stable);
        }

        [Fact]
        public void A_room_drawn_to_the_wall_centre_has_no_face_on_its_boundary()
        {
            Assert.True(OrthogonalRoomRules.TryOrthogonalLoop(LRoom(), out var loop, out _));
            var bottom = loop.Single(s => s.WallId == 1);
            var faces = new[]
            {
                new RoomFaceFact { Stable = "a", X = 0, Y = 75 * Mm },
                new RoomFaceFact { Stable = "b", X = 0, Y = -75 * Mm },
            };
            Assert.False(OrthogonalRoomRules.TryClassifyFaces(bottom, faces, 600 * Mm, out _, out _, out var error));
            Assert.Contains("boundary", error);
        }

        [Fact]
        public void Rectangle_plans_two_interior_and_four_totals_like_the_rectangular_planner()
        {
            Assert.True(OrthogonalRoomRules.TryOrthogonalLoop(Loop(1, 0, 0, 4000, 0, 4000, 3000, 0, 3000), out var loop, out _));
            var outer = loop.ToDictionary(s => s.Index, s => s.Across + s.Outward * 150 * Mm);
            var specs = OrthogonalRoomRules.PlanChains(loop, outer, 300 * Mm, 600 * Mm, 400 * Mm);
            Assert.Equal(2, specs.Count(s => s.Kind == "interior"));
            Assert.Equal(4, specs.Count(s => s.Kind == "exterior_total"));
            Assert.DoesNotContain(specs, s => s.Kind == "exterior_partial");
        }

        [Fact]
        public void L_room_plans_a_clear_span_per_side_and_a_partial_chain_where_the_outline_steps()
        {
            Assert.True(OrthogonalRoomRules.TryOrthogonalLoop(LRoom(), out var loop, out _));
            var outer = loop.ToDictionary(s => s.Index, s => s.Across + s.Outward * 150 * Mm);
            var specs = OrthogonalRoomRules.PlanChains(loop, outer, 300 * Mm, 600 * Mm, 400 * Mm);
            Assert.Equal(6, specs.Count(s => s.Kind == "interior"));
            Assert.Equal(new[] { "exterior_top_partial", "exterior_right_partial" },
                specs.Where(s => s.Kind == "exterior_partial").Select(s => s.Role).OrderByDescending(r => r));
            Assert.Equal(4, specs.Count(s => s.Kind == "exterior_total"));
            Assert.Equal(specs.Count, specs.Select(s => s.Role).Distinct().Count());

            // The top facade's chain sits above the HIGHEST top-facing outer face (4000 + 150), not the step.
            var topTotal = specs.Single(s => s.Role == "exterior_top_total");
            var topPartial = specs.Single(s => s.Role == "exterior_top_partial");
            Assert.Equal((4150 + 600 + 400 + 400) * Mm, topTotal.LineAcross, 6);
            Assert.Equal((4150 + 600 + 400) * Mm, topPartial.LineAcross, 6);
            Assert.Equal(3, topPartial.Members.Count);

            // The clear span along the re-entrant step runs between the inner faces of its two neighbours,
            // inside the room, below the step.
            var step = loop.Single(s => s.WallId == 3);
            var span = specs.Single(s => s.Role == "interior_w3");
            Assert.Equal(new[] { 1, 3 }, span.Members.Select(m => m.Key));
            Assert.All(span.Members, m => Assert.Equal("room_inner", m.Value));
            Assert.Equal(step.Across - 300 * Mm, span.LineAcross, 6);
        }

        [Fact]
        public void A_line_placed_at_an_explicit_coordinate_spans_its_references_with_a_tail()
        {
            var refs = new List<AutoDimensionCandidate> { new AutoDimensionCandidate { X = 1, Y = 5 }, new AutoDimensionCandidate { X = 4, Y = 9 } };
            var chain = new AutoDimensionChain();
            OrthogonalRoomRules.PlaceLineAt(refs, "horizontal", -2, 0.5, chain);
            Assert.Equal((0.5, -2.0, 4.5, -2.0), (chain.StartX, chain.StartY, chain.EndX, chain.EndY));
            OrthogonalRoomRules.PlaceLineAt(refs, "vertical", 7, 0.5, chain);
            Assert.Equal((7.0, 4.5, 7.0, 9.5), (chain.StartX, chain.StartY, chain.EndX, chain.EndY));
        }

        // ---- openings on the wall void ------------------------------------------

        private static WallVoidFace Jamb(string id, double along, int facing, double zMin = 0, double zMax = 3000)
            => new WallVoidFace { Stable = id, Along = along * Mm, NormalAlong = facing, ZMin = zMin * Mm, ZMax = zMax * Mm };

        private static WallVoidFace Cap(string id, double z, int facing, double aMin, double aMax)
            => new WallVoidFace { Stable = id, Z = z * Mm, NormalZ = facing, AlongMin = aMin * Mm, AlongMax = aMax * Mm };

        private static List<WallVoidFace> TwoOpeningWall() => new List<WallVoidFace>
        {
            Jamb("wall_start", 0, -1), Jamb("wall_end", 6000, +1),
            // door 1000..1900 (to the base), window 3000..4500 with sill 900 and head 2100
            Jamb("d_left", 1000, +1, 0, 2100), Jamb("d_right", 1900, -1, 0, 2100),
            Cap("d_head", 2100, -1, 1000, 1900),
            Jamb("w_left", 3000, +1, 900, 2100), Jamb("w_right", 4500, -1, 900, 2100),
            Cap("w_head", 2100, -1, 3000, 4500), Cap("w_sill", 900, +1, 3000, 4500),
            Cap("wall_top", 3000, +1, 0, 6000), Cap("wall_base", 0, -1, 0, 6000),
        };

        [Fact]
        public void Each_opening_takes_its_own_jambs_never_a_neighbour_or_the_wall_end()
        {
            var door = OpeningVoidRules.Choose(TwoOpeningWall(), 1450 * Mm, 1050 * Mm, 900 * Mm, out var e1);
            Assert.Null(e1);
            Assert.Equal(("d_left", "d_right", "d_head"), (door.Left.Stable, door.Right.Stable, door.Head.Stable));
            Assert.Null(door.Sill);
            Assert.Contains(door.Notes, n => n.Contains("base"));

            var window = OpeningVoidRules.Choose(TwoOpeningWall(), 3750 * Mm, 1500 * Mm, 1500 * Mm, out var e2);
            Assert.Null(e2);
            Assert.Equal(("w_left", "w_right", "w_head", "w_sill"),
                (window.Left.Stable, window.Right.Stable, window.Head.Stable, window.Sill.Stable));
            Assert.Equal(1500 * Mm, window.Width.Value, 6);
        }

        [Fact]
        public void A_family_that_does_not_cut_its_wall_is_refused_not_measured_to_the_wall_ends()
        {
            var plain = new List<WallVoidFace> { Jamb("wall_start", 0, -1), Jamb("wall_end", 6000, +1) };
            Assert.Null(OpeningVoidRules.Choose(plain, 3000 * Mm, 1000 * Mm, null, out var error));
            Assert.Contains("jamb", error);
        }

        [Fact]
        public void A_rough_opening_wider_than_the_type_is_reported()
        {
            var faces = new List<WallVoidFace> { Jamb("l", 980, +1), Jamb("r", 1920, -1) };
            var choice = OpeningVoidRules.Choose(faces, 1450 * Mm, 1000 * Mm, 900 * Mm, out _);
            Assert.Contains(choice.Notes, n => n.Contains("40 mm wider"));
            Assert.Equal("l", OpeningVoidRules.ForSelector(choice, "opening_left").Stable);
            Assert.Null(OpeningVoidRules.ForSelector(choice, "opening_center"));
        }

        // ---- the top of a wall ----------------------------------------------------

        private static ViewPointFact P(string id, double u, double z, double depth)
            => new ViewPointFact { Stable = id, U = u * Mm, Z = z * Mm, Depth = depth * Mm };

        [Fact]
        public void A_horizontal_top_face_is_the_reference()
        {
            var faces = new[]
            {
                new TopFaceFact { Stable = "far", NormalZ = 1, Elevation = 3000 * Mm, DepthMin = 2000 * Mm, DepthMax = 4000 * Mm },
                new TopFaceFact { Stable = "cut", NormalZ = 1, Elevation = 2700 * Mm, DepthMin = -500 * Mm, DepthMax = 500 * Mm },
            };
            var choice = WallTopReferenceRules.Choose(faces, out var code, out var error);
            Assert.Null(code);
            Assert.Equal(WallTopReferenceRules.ModeHorizontalFace, choice.Mode);
            Assert.Equal("cut", choice.References.Single().Value.Stable);
        }

        [Fact]
        public void A_sloped_top_seen_edge_on_gives_both_end_points()
        {
            var face = new TopFaceFact { Stable = "slope", NormalZ = 0.97, NormalDotView = 0 };
            face.Edges.Add(new TopEdgeFact { Stable = "front", Start = P("f0", 0, 2148, 0), End = P("f1", 5968, 4635, 0) });
            face.Edges.Add(new TopEdgeFact { Stable = "back", Start = P("b0", 0, 2148, 150), End = P("b1", 5968, 4635, 150) });
            var choice = WallTopReferenceRules.Choose(new[] { face }, out var code, out _);
            Assert.Null(code);
            Assert.Equal(WallTopReferenceRules.ModeEdgeOnEndPoints, choice.Mode);
            Assert.Equal(new[] { "f0", "f1" }, choice.References.Select(r => r.Value.Stable));
            Assert.Equal(2148 * Mm, choice.References[0].Value.Z, 6);
            Assert.Equal(4635 * Mm, choice.References[1].Value.Z, 6);
        }

        [Fact]
        public void A_sloped_top_seen_across_uses_the_horizontal_end_edge_beyond_the_cut()
        {
            // Section across the wall: the slope runs along the view direction.
            var face = new TopFaceFact { Stable = "slope", NormalZ = 0.97, NormalDotView = 0.24 };
            face.Edges.Add(new TopEdgeFact { Stable = "near_end", Start = P("n0", 0, 2000, -1200), End = P("n1", 150, 2000, -1200) });
            face.Edges.Add(new TopEdgeFact { Stable = "far_end", Start = P("e0", 0, 2148, 800), End = P("e1", 150, 2148, 800) });
            // The section's own cut line (CUT_EDGE) sits AT the cut: Revit measures a dimension to it as zero.
            face.Edges.Add(new TopEdgeFact { Stable = "cut_edge", Start = P("c0", 0, 3082, 0), End = P("c1", 150, 3082, 0) });
            face.Edges.Add(new TopEdgeFact { Stable = "long", Start = P("l0", 0, 2000, -1200), End = P("l1", 0, 2148, 800) });
            var choice = WallTopReferenceRules.Choose(new[] { face }, out var code, out _);
            Assert.Null(code);
            Assert.Equal(WallTopReferenceRules.ModeEndEdgeBeyondCut, choice.Mode);
            Assert.Equal("far_end", choice.References.Single().Value.Stable);   // the one behind the viewer is not shown
            Assert.Contains("800 mm beyond the cut", choice.Note);
        }

        [Fact]
        public void A_sloped_top_with_no_usable_edge_is_refused_with_the_alternative()
        {
            var face = new TopFaceFact { Stable = "slope", NormalZ = 0.97, NormalDotView = 0.24 };
            Assert.Null(WallTopReferenceRules.Choose(new[] { face }, out var code, out var error));
            Assert.Equal(WallTopReferenceRules.CodeNotDimensionable, code);
            Assert.Contains("spot elevation", error);
        }

        [Fact]
        public void A_level_parallel_height_face_outranks_a_nearer_sloped_one()
        {
            var faces = new[]
            {
                new SectionFaceFact { Stable = "sloped", CutDistance = 0, Elevation = 3, NormalZ = 0.97 },
                new SectionFaceFact { Stable = "flat", CutDistance = 2, Elevation = 2, NormalZ = 1 },
            };
            Assert.Equal("flat", DimensionReferenceRules.ChooseSectionFace(faces, top: true).Stable);
            Assert.False(DimensionReferenceRules.IsLevelParallel(faces[0]));
            Assert.True(DimensionReferenceRules.IsLevelParallel(new SectionFaceFact { Stable = "unknown" }));
        }

        [Fact]
        public void Dwg_export_says_where_its_dimensions_live()
        {
            Assert.Contains("companion file", ExportFormatRules.DwgAnnotationLocation(ExportFormatRules.XrefsLinked));
            Assert.Contains("counts zero", ExportFormatRules.DwgAnnotationLocation(ExportFormatRules.XrefsLinked));
            Assert.Contains("block", ExportFormatRules.DwgAnnotationLocation(ExportFormatRules.XrefsBound));
        }
        // ---- validation 6: composition and wall attachment ---------------------

        [Fact]
        public void Markers_far_outside_the_crop_are_found_and_touching_ones_are_not()
        {
            var crop = PlanBox.FromCorners(0, 0, 10, 10);
            Assert.True(ViewCompositionRules.IsFarOutside(PlanBox.FromCorners(30, 30, 31, 31), crop, 1));
            Assert.False(ViewCompositionRules.IsFarOutside(PlanBox.FromCorners(10.5, 2, 11, 3), crop, 1));
            Assert.False(ViewCompositionRules.IsFarOutside(PlanBox.FromCorners(2, 2, 3, 3), crop, 0));
            Assert.False(ViewCompositionRules.IsFarOutside(PlanBox.Unreadable, crop, 0));
        }

        [Fact]
        public void An_interior_chain_is_limited_to_the_room_and_an_exterior_one_to_the_margin()
        {
            var room = PlanBox.FromCorners(0, 0, 10, 8);
            var interior = PlanBox.FromCorners(1, 3, 9, 3.2);
            var exterior = PlanBox.FromCorners(1, 9, 9, 9.2);
            var across = ViewCompositionRules.LayoutLimit(room, interior, 3);
            Assert.Equal((-3.0, 0.0, 13.0, 8.0), (across.MinX, across.MinY, across.MaxX, across.MaxY));   // tails may pass the walls
            // A full-length interior chain (tails beyond both walls) still fits; the old box refused it.
            Assert.True(AutoDimensionRules.TryShiftDimensionLine(PlanBox.FromCorners(-0.5, 3, 10.5, 3.2), new PlanBox[0],
                ViewCompositionRules.LayoutLimit(room, PlanBox.FromCorners(-0.5, 3, 10.5, 3.2), 3), "horizontal", 1, 0.5, 120, out _));
            var grown = ViewCompositionRules.LayoutLimit(room, exterior, 3);
            Assert.Equal((-3.0, -3.0, 13.0, 11.0), (grown.MinX, grown.MinY, grown.MaxX, grown.MaxY));
            // The L-room case: an interior chain pushed 3150 mm down leaves the room box and is refused.
            Assert.False(AutoDimensionRules.TryShiftDimensionLine(interior, new[] { PlanBox.FromCorners(0.5, 2.5, 9.5, 7.9) },
                ViewCompositionRules.LayoutLimit(room, interior, 3), "horizontal", 1, 0.5, 120, out _));
            Assert.False(ViewCompositionRules.Intersect(PlanBox.FromCorners(0, 0, 1, 1), PlanBox.FromCorners(2, 2, 3, 3)).Valid);
        }

        private static List<double[]> Rect(double y, double x0, double x1, double z0a, double z0b, double topA, double topB)
            => new List<double[]>
            {
                new[] { x0, y, z0a, x1, y, z0b },          // base
                new[] { x1, y, z0b, x1, y, topB },          // end
                new[] { x1, y, topB, x0, y, topA },         // top
                new[] { x0, y, topA, x0, y, z0a },          // start
            };

        [Fact]
        public void A_top_sloped_along_the_wall_reads_at_the_location_ends_not_a_width_further()
        {
            // Wall along +X from 0 to 10 ft, faces at y = +-0.25, top z = 10 + 0.1 x, faces trimmed 0.25 short at x = 10.
            var faces = new List<IReadOnlyList<double[]>> { Rect(0.25, 0, 9.75, 0, 0, 10, 10.975), Rect(-0.25, 0, 9.75, 0, 0, 10, 10.975) };
            var z = WallAttachmentRules.EndElevationsOnLocation(faces, new double[] { 0, 0 }, new double[] { 10, 0 }, true);
            Assert.Equal(10.0, z[0].Value, 6);
            Assert.Equal(11.0, z[1].Value, 6);       // extrapolated along the top edge, not the corner's 10.975
        }

        [Fact]
        public void A_top_sloped_across_the_wall_reads_its_value_on_the_location_line()
        {
            var faces = new List<IReadOnlyList<double[]>> { Rect(0.25, 0, 10, 0, 0, 10.05, 10.05), Rect(-0.25, 0, 10, 0, 0, 9.95, 9.95) };
            var z = WallAttachmentRules.EndElevationsOnLocation(faces, new double[] { 0, 0 }, new double[] { 10, 0 }, true);
            Assert.Equal(10.0, z[0].Value, 6);
            Assert.Equal(10.0, z[1].Value, 6);
            // The legacy reading took the higher face: half a width of slope too high.
            var legacy = WallAttachmentRules.EndElevations(faces.SelectMany(f => f).Select(s => new[] { s[0], s[1], s[2] }).ToList(),
                new double[] { 0, 0 }, new double[] { 10, 0 }, 0.5, true);
            Assert.Equal(10.05, legacy[0].Value, 6);
        }

        [Fact]
        public void A_roof_plane_gives_the_height_above_a_point()
        {
            // 5 degrees rising +Y through (0, -2376 mm, 2700 mm): a footprint roof with only its south edge sloped.
            double t = System.Math.Tan(5 * System.Math.PI / 180);
            double[] normal = { 0, -t, 1 };
            double z = WallAttachmentRules.PlaneZ(new double[] { 0, -2376, 2700 }, normal, 3413, 1500).Value;
            Assert.Equal(2700 + t * (1500 + 2376), z, 6);
            Assert.Null(WallAttachmentRules.PlaneZ(new double[] { 0, 0, 0 }, new double[] { 1, 0, 0 }, 1, 1));
        }
        [Fact]
        public void A_dimension_collides_by_its_line_and_text_not_by_its_witness_lines()
        {
            // A horizontal dimension at y = 10 whose witness lines reach down to y = 0.
            var box = PlanBox.FromCorners(0, 0, 20, 11);
            var band = SheetAnnotationOverlapRules.DimensionBand(box, true, 10, 0.5);
            Assert.Equal((0.0, 9.5, 20.0, 10.5), (band.MinX, band.MinY, band.MaxX, band.MaxY));
            var other = PlanBox.FromCorners(5, 2, 6, 3);   // a tag between the witness lines
            Assert.True(PlanimetryGeometry.Overlaps(box, other, 0));
            Assert.False(PlanimetryGeometry.Overlaps(band, other, 0));
            Assert.Equal(box, SheetAnnotationOverlapRules.DimensionBand(box, true, double.NaN, 0.5));
        }
        private static AnnotationFact Dim(bool horizontal, double line, double lo, double hi, double textAt)
        {
            var band = horizontal ? PlanBox.FromCorners(lo, line - 0.01, hi, line + 0.01) : PlanBox.FromCorners(line - 0.01, lo, line + 0.01, hi);
            var text = horizontal ? PlanBox.FromCorners(textAt - 0.4, line + 0.05, textAt + 0.4, line + 0.3)
                                  : PlanBox.FromCorners(line - 0.3, textAt - 0.4, line - 0.05, textAt + 0.4);
            var whole = horizontal ? PlanBox.FromCorners(lo, line - 5, hi, line + 0.3) : PlanBox.FromCorners(line - 0.3, lo, line + 5, hi);
            return new AnnotationFact { Kind = "dimension", Box = whole, LineBand = band, LineHorizontal = horizontal, TextBoxes = new List<PlanBox> { text } };
        }

        [Fact]
        public void Perpendicular_dimension_lines_may_cross_but_text_on_a_line_collides()
        {
            // Interior spans of an L: a horizontal one at y=1 from 0..10 and a vertical one at x=1 from 0..8.
            var h = Dim(true, 1, 0, 10, 5);
            var v = Dim(false, 1, 0, 8, 4);
            Assert.False(SheetAnnotationOverlapRules.Collide(h, v));
            // Move the vertical's text onto the horizontal line: now it collides.
            var vOnLine = Dim(false, 1, 0, 8, 1);
            Assert.True(SheetAnnotationOverlapRules.Collide(h, vOnLine));
            // Two parallel lines on top of each other collide even with texts apart.
            Assert.True(SheetAnnotationOverlapRules.Collide(h, Dim(true, 1, 2, 12, 9)));
            // A tag box without parts falls back to its box against the other's text and line.
            var tag = new AnnotationFact { Kind = "tag", Box = PlanBox.FromCorners(4.8, 1.1, 5.2, 1.2) };
            Assert.True(SheetAnnotationOverlapRules.Collide(h, tag));
        }
    }
}