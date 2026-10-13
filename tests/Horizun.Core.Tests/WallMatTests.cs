// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Acero 3.0, L5: the body of a wall - a retaining wall, a tank wall - takes no new
// schema. It is two mats, one under each face (face_normal = plus and minus the
// wall's own normal), and their layers must not stand in one another. And a wall's
// horizontal bars may end in hooks at the wall's ends (start_hook / end_hook on a mat
// component, the hook types declared): only the ends at the host's edges carry
// them, never the ends a trimmed opening leaves.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WallMatTests
    {
        // A wall 6000 long along +X, 200 thick (y -100..100), 3000 tall.
        private static HostMesh Wall() => HostMesh.Box(new double[] { 0, -100, 0 }, new double[] { 6000, 100, 3000 });

        private static readonly double[] X = { 1, 0, 0 };
        private static readonly double[] Z = { 0, 0, 1 };

        private static MatComponentRequest Comp(string name, double[] dir, double offset) => new MatComponentRequest
        {
            Name = name, DirectionMm = dir, BarTypeId = "N16", OffsetFromFaceMm = offset, EndCoverMm = 40, SideCoverMm = 40,
            Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 200 }
        };

        private static StructuralMatRule Mat(string id, double[] normal) => new StructuralMatRule
        {
            Id = id, FaceNormalMm = normal,
            // Verticals outside, horizontals inside them: 48 = 40 + 8, 64 = 48 + 16.
            Components = new List<MatComponentRequest> { Comp("v", Z, 48), Comp("h", X, 64) }
        };

        private static double Dia(string id) => 16;

        [Fact]
        public void Two_mats_on_opposite_faces_of_a_wall_expand_without_their_layers_meeting()
        {
            var all = new List<StructuralRebarRule>();
            foreach (StructuralMatRule m in new[] { Mat("W-out", new double[] { 0, 1, 0 }), Mat("W-in", new double[] { 0, -1, 0 }) })
            {
                MatResult r = MatRules.Expand(m, Wall(), Dia, out List<StructuralRebarRule> made);
                Assert.True(r.Ok, r.Why);
                all.AddRange(made);
            }
            Assert.Equal(4, all.Count);
            Assert.Equal(all.Count, all.Select(x => x.Id).Distinct().Count());
            // Every set lies in one plane across the thickness, inside the cover on its own face.
            var planes = new Dictionary<string, double>();
            foreach (StructuralRebarRule b in all)
            {
                Assert.Equal(b.CurvesMm[0][1], b.CurvesMm[1][1], 9);
                Assert.Equal(0, b.NormalMm[1], 9);
                Assert.True(System.Math.Abs(b.CurvesMm[0][1]) + 8 <= 100 - 40 + 1e-9, b.Id);
                planes[b.Id] = b.CurvesMm[0][1];
            }
            Assert.Equal(52, planes["W-out#v"], 9); Assert.Equal(36, planes["W-out#h"], 9);
            Assert.Equal(-52, planes["W-in#v"], 9); Assert.Equal(-36, planes["W-in#h"], 9);
            // No two layers closer than two bar radii: the faces' layers stand apart.
            double[] ys = planes.Values.OrderBy(y => y).ToArray();
            for (int i = 1; i < ys.Length; i++) Assert.True(ys[i] - ys[i - 1] >= 16 - 1e-9);
            // And every bar is inside the wall.
            foreach (StructuralRebarRule b in all)
                foreach (double[] p in b.CurvesMm)
                {
                    Assert.InRange(p[0], 0, 6000);
                    Assert.InRange(p[2], 0, 3000);
                }
        }

        // ------------------------------------------------------------------ end hooks

        private static JObject Doc(string component) => JObject.Parse(@"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter',
  'requirement_set': { 'id': 'l5', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' } ],
  'hook_types': [ { 'id': 'H180', 'type_name': 'Standard - 180 deg.' } ],
  'mat_rules': [ { 'id': 'W1', 'host': { 'element_ids': [ 7 ] }, 'face_normal': [ 0, 1, 0 ],
    'components': [ " + component + @" ] } ] }");

        private const string Horizontal = @"'name': 'h', 'bar_type': 'N16', 'direction': [ 1, 0, 0 ], 'offset_from_face_mm': 48,
      'end_cover_mm': 40, 'side_cover_mm': 40, 'layout': { 'rule': 'maximum_spacing', 'spacing_mm': 200 }, 'allow_new_shape': true";

        [Fact]
        public void A_mat_component_declares_hooks_at_its_bars_ends()
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(Doc("{ " + Horizontal +
                @", 'start_hook': { 'hook_type': 'H180', 'orientation': 'left' }, 'end_hook': { 'hook_type': 'H180', 'orientation': 'right' } }"));
            Assert.True(s.Ok, s.Error);
            MatComponentRequest c = s.MatRules.Single().Components.Single();
            Assert.Equal("H180", c.Start.HookTypeId); Assert.Equal("left", c.Start.Orientation);
            Assert.Equal("H180", c.End.HookTypeId); Assert.Equal("right", c.End.Orientation);

            MatResult r = MatRules.Expand(s.MatRules.Single(), Wall(), Dia, out List<StructuralRebarRule> made);
            Assert.True(r.Ok, r.Why);
            StructuralRebarRule bar = made.Single();
            Assert.Equal("H180", bar.Start.HookTypeId); Assert.Equal("left", bar.Start.Orientation);
            Assert.Equal("H180", bar.End.HookTypeId); Assert.Equal("right", bar.End.Orientation);
        }

        [Fact]
        public void Without_hooks_a_mat_bar_ends_straight_as_before()
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(Doc("{ " + Horizontal + " }"));
            Assert.True(s.Ok, s.Error);
            MatRules.Expand(s.MatRules.Single(), Wall(), Dia, out List<StructuralRebarRule> made);
            Assert.Null(made.Single().Start.HookTypeId);
            Assert.Null(made.Single().End.HookTypeId);
        }

        [Fact]
        public void A_hook_type_nobody_declared_is_refused_by_path()
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(Doc("{ " + Horizontal + ", 'end_hook': { 'hook_type': 'H90' } }"));
            Assert.Equal(StructuralRequirementSet.CodeUnresolvedReference, s.Code);
            Assert.Equal("mat_rules['W1'].components[0].end_hook.hook_type", s.Path);
        }

        [Fact]
        public void A_misspelt_key_in_a_component_hook_is_refused()
        {
            StructuralRequirementSet s = StructuralRequirementSet.Load(Doc("{ " + Horizontal + ", 'end_hook': { 'hook': 'H180' } }"));
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Equal("mat_rules['W1'].components[0].end_hook.hook", s.Path);
        }

        [Fact]
        public void A_trimmed_bar_keeps_its_hooks_only_at_the_hosts_edges()
        {
            // The holed slab of MatOpeningsTests: 6000 x 4000 x 200, a 1000 x 1000 hole at x 2000..3000.
            HostMesh holed = MatOpeningsTests.SlabWithHole(0, 0, 6000, 4000, 0, 200, 2000, 1500, 3000, 2500);
            MatComponentRequest c = new MatComponentRequest
            {
                Name = "x", DirectionMm = X, BarTypeId = "N16", OffsetFromFaceMm = 31, EndCoverMm = 25, SideCoverMm = 25,
                Layout = new RebarLayoutRequest { Layout = RebarLayout.MaximumSpacing, SpacingMm = 200 },
                Start = new StructuralTermination { HookTypeId = "H180" },
                End = new StructuralTermination { HookTypeId = "H180" }
            };
            var rule = new StructuralMatRule
            {
                Id = "S1", FaceNormalMm = Z, Components = new List<MatComponentRequest> { c },
                Openings = new StructuralMatOpenings { Policy = StructuralMatOpenings.PolicyTrim, MinimumSizeMm = 0, ClearanceMm = 25 }
            };
            MatResult r = MatRules.Expand(rule, holed, Dia, out List<StructuralRebarRule> made);
            Assert.True(r.Ok, r.Why);
            List<StructuralRebarRule> pieces = made.Where(b => b.Id.Contains("#seg")).ToList();
            Assert.NotEmpty(pieces);
            foreach (StructuralRebarRule p in pieces)
            {
                bool atStartEdge = System.Math.Abs(p.CurvesMm[0][0] - 25) < 1e-6;
                bool atEndEdge = System.Math.Abs(p.CurvesMm[1][0] - 5975) < 1e-6;
                Assert.True(atStartEdge ^ atEndEdge, p.Id);
                Assert.Equal(atStartEdge ? "H180" : null, p.Start.HookTypeId);
                Assert.Equal(atEndEdge ? "H180" : null, p.End.HookTypeId);
            }
            // The bars the hole does not touch run edge to edge and keep both.
            foreach (StructuralRebarRule w in made.Where(b => !b.Id.Contains("#seg")))
            {
                Assert.Equal("H180", w.Start.HookTypeId);
                Assert.Equal("H180", w.End.HookTypeId);
            }
        }
    }
}
