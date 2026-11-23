using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    // Field case (22-nov): horizun_transform_elements copy of corner-joined walls to another level rolled back
    // because only 1 of the copies matched. A copied wall whose joins changed (a partner left behind, or new
    // partners at the destination) keeps its authored axis, height, type and layers but has other faces and bounds.
    public class CopyJoinedWallRulesTests
    {
        private const double Dz = 13.123359580052492; // 4000 mm

        private static CopySignature Wall(long id, double x1, double y1, double x2, double y2, double baseZ, double topZ,
            string type = "Autodesk.Revit.DB.WallType|Basic Wall: Generic - 200 mm", double thickness = 0.65616797900262469)
        {
            double h = thickness / 2;
            return new CopySignature
            {
                Id = id, Category = "OST_Walls", Type = type, Position = new[] { (x1 + x2) / 2, (y1 + y2) / 2, baseZ },
                BoundsMin = new[] { System.Math.Min(x1, x2) - h, System.Math.Min(y1, y2) - h, baseZ },
                BoundsMax = new[] { System.Math.Max(x1, x2) + h, System.Math.Max(y1, y2) + h, topZ },
                GeometryPoints = new[] { new[] { x1 - h, y1 - h, baseZ }, new[] { x2 + h, y2 + h, topZ } },
                Layers = new[] { new CopyLayer { Width = thickness, Definition = "Structure|Concrete|True|False|True|False|" } },
                Layout = new CopyLayout
                {
                    Kind = "wall_axis", Base = baseZ, Top = topZ, Thickness = thickness, Facing = new[] { 0d, -1d, 0d },
                    Curves = new[] { new[] { new[] { x1, y1, 0d }, new[] { x2, y2, 0d } } }
                }
            };
        }

        private static CopySignature Expected(CopySignature source) => CopyRepresentationRules.Translate(source, 0, 0, Dz);

        // The copy lost its corner partner: the join no longer extends the end, so the box and the faces moved.
        private static CopySignature CopyWithChangedJoin(CopySignature expected, long id)
        {
            CopySignature copy = Wall(id, 0, 0, 26.246719160104988, 0, expected.Layout.Base, expected.Layout.Top);
            copy.BoundsMax[0] -= 0.328;
            copy.GeometryPoints[1][0] -= 0.328;
            copy.Position = expected.Position;
            return copy;
        }

        [Fact]
        public void A_copied_wall_whose_corner_join_changed_fails_the_exact_box_match()
        {
            CopySignature expected = Expected(Wall(1, 0, 0, 26.246719160104988, 0, 0, 9.186351706036746));
            CopySignature copy = CopyWithChangedJoin(expected, 11);
            // The plain signature (no layout) is what the transform reads: bounds and faces are compared exactly.
            CopySignature plainExpected = Expected(Wall(1, 0, 0, 26.246719160104988, 0, 0, 9.186351706036746));
            plainExpected.Layout = null;
            CopySignature plainCopy = CopyWithChangedJoin(expected, 11);
            plainCopy.Layout = null;
            Assert.Empty(CopyRepresentationRules.Map(new[] { plainExpected }, new[] { plainCopy }, null));
            Assert.Equal("authored_wall_join_changed", CopyRepresentationRules.JoinChangedWallBasis(expected, copy));
        }

        [Fact]
        public void A_wall_at_another_place_height_type_or_build_up_is_not_the_copy()
        {
            CopySignature expected = Expected(Wall(1, 0, 0, 26.246719160104988, 0, 0, 9.186351706036746));

            CopySignature shifted = CopyWithChangedJoin(expected, 11);
            shifted.Layout.Curves[0][1][0] += 0.01;
            Assert.Null(CopyRepresentationRules.JoinChangedWallBasis(expected, shifted));

            CopySignature taller = CopyWithChangedJoin(expected, 12);
            taller.Layout.Top += 0.01;
            taller.BoundsMax[2] += 0.01;
            Assert.Null(CopyRepresentationRules.JoinChangedWallBasis(expected, taller));

            CopySignature otherType = CopyWithChangedJoin(expected, 13);
            otherType.Type = "Autodesk.Revit.DB.WallType|Basic Wall: Generic - 300 mm";
            Assert.Null(CopyRepresentationRules.JoinChangedWallBasis(expected, otherType));

            CopySignature otherLayers = CopyWithChangedJoin(expected, 14);
            otherLayers.Layers = new[] { new CopyLayer { Width = otherLayers.Layers[0].Width, Definition = "Structure|Brick|True|False|True|False|" } };
            Assert.Null(CopyRepresentationRules.JoinChangedWallBasis(expected, otherLayers));

            // The box height is read from the model, not from the layout: a body that does not reach its own top is refused.
            CopySignature shortBody = CopyWithChangedJoin(expected, 15);
            shortBody.BoundsMax[2] -= 1;
            Assert.Null(CopyRepresentationRules.JoinChangedWallBasis(expected, shortBody));
        }

        [Fact]
        public void Only_an_authored_wall_axis_qualifies()
        {
            CopySignature expected = Expected(Wall(1, 0, 0, 26.246719160104988, 0, 0, 9.186351706036746));
            CopySignature copy = CopyWithChangedJoin(expected, 11);

            CopySignature noLayout = CopyWithChangedJoin(expected, 12);
            noLayout.Layout = null;
            Assert.Null(CopyRepresentationRules.JoinChangedWallBasis(expected, noLayout));

            CopySignature floorExpected = Expected(Wall(1, 0, 0, 26.246719160104988, 0, 0, 9.186351706036746));
            floorExpected.Layout.Kind = "floor_sketch";
            CopySignature floorCopy = CopyWithChangedJoin(floorExpected, 13);
            floorCopy.Layout.Kind = "floor_sketch";
            Assert.Null(CopyRepresentationRules.JoinChangedWallBasis(floorExpected, floorCopy));

            CopySignature unreadLayers = CopyWithChangedJoin(expected, 14);
            unreadLayers.Layers = null;
            unreadLayers.RequiresHostEvidence = true;
            Assert.Null(CopyRepresentationRules.JoinChangedWallBasis(expected, unreadLayers));

            Assert.Null(CopyRepresentationRules.JoinChangedWallBasis(null, copy));
            Assert.Null(CopyRepresentationRules.JoinChangedWallBasis(expected, null));
        }
    }
}
