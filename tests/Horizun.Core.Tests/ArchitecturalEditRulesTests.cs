using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ArchitecturalEditRulesTests
    {
        private static JObject J(string s) => JObject.Parse(s);

        [Fact]
        public void Linear_step_is_the_vector_for_second_and_a_share_of_it_for_last()
        {
            Assert.Equal(new[] { 3.0, 0, 0 }, ArchitecturalEditRules.ArrayStepVector(new[] { 3.0, 0, 0 }, 4, false));
            Assert.Equal(new[] { 1.0, 2, 0 }, ArchitecturalEditRules.ArrayStepVector(new[] { 3.0, 6, 0 }, 4, true));
        }

        [Fact]
        public void Radial_last_splits_a_full_turn_count_ways_and_a_partial_turn_count_minus_one()
        {
            Assert.Equal(Math.PI / 2, ArchitecturalEditRules.ArrayStepAngle(Math.PI / 2, 4, false), 12);
            Assert.Equal(2 * Math.PI / 4, ArchitecturalEditRules.ArrayStepAngle(2 * Math.PI, 4, true), 12);
            Assert.Equal(Math.PI / 3, ArchitecturalEditRules.ArrayStepAngle(Math.PI, 4, true), 12);
        }

        [Theory]
        [InlineData(false, 1, false)] [InlineData(false, 2, true)] [InlineData(true, 2, false)]
        [InlineData(true, 3, true)] [InlineData(true, 200, true)] [InlineData(false, 201, false)]
        public void Array_counts_follow_revits_ranges(bool radial, int count, bool ok) =>
            Assert.Equal(ok, ArchitecturalEditRules.ValidateArrayCount(radial, count) == null);

        [Fact]
        public void Slab_vertex_reading_names_the_convention_that_held()
        {
            Assert.Equal("absolute", ArchitecturalEditRules.SlabVertexConvention(10.5, 10, 0.5, 1e-4));
            Assert.Equal("offset", ArchitecturalEditRules.SlabVertexConvention(0.5, 10, 0.5, 1e-4));
            Assert.Null(ArchitecturalEditRules.SlabVertexConvention(3, 10, 0.5, 1e-4));
        }

        // Backlog #20: each vertex passed if EITHER reading of Z fitted it, point by point, so
        // a vertex Revit never moved could pass under the other reading. One edit has one
        // convention; a vertex that only fits the other one fails.
        [Fact]
        public void One_edit_reads_every_vertex_with_one_convention()
        {
            // top 3000; vertex 0 asked at offset 0 reads 3000 (absolute); vertex 1 asked at 500
            // reads 500 - which only the OFFSET reading accepts.
            double?[] z = { 3000, 500 };
            double[] offsets = { 0, 500 };
            string convention = ArchitecturalEditRules.SlabEditConvention(z, 3000, offsets, 1e-4);
            Assert.Equal("absolute", convention);
            Assert.Equal(-2500, ArchitecturalEditRules.SlabOffsetRead(500, 3000, convention), 6);
            Assert.Equal(0, ArchitecturalEditRules.SlabOffsetRead(3000, 3000, convention), 6);
        }

        [Fact]
        public void An_unmoved_vertex_cannot_choose_the_reading_that_would_pass_it()
        {
            // XYZ Position is a model coordinate. At elevation 10 an unchanged vertex
            // also happens to equal a requested offset of 10; that is NOT an applied edit.
            Assert.Equal("absolute", ArchitecturalEditRules.SlabEditConvention(new double?[] { 10 }, 10, new[] { 10.0 }, 1e-4));
            Assert.Equal(0, ArchitecturalEditRules.SlabOffsetRead(10, 10, "absolute"));
            Assert.Equal("absolute", ArchitecturalEditRules.SlabEditConvention(new double?[] { 10.5, 10.25 }, 10, new[] { 0.5, 0.25 }, 1e-4));
            Assert.Equal("absolute", ArchitecturalEditRules.SlabEditConvention(new double?[] { 3, null }, 10, new[] { 0.5, 1 }, 1e-4));
        }

        [Fact]
        public void A_roof_shape_top_is_one_thickness_above_its_base_offset_and_a_floor_top_is_its_offset()
        {
            // Field case: add_point on a flat roof read the new vertex +50 mm (the roof thickness) and rolled back.
            double mm = 1 / 304.8;
            Assert.Equal((3000 + 200 + 50) * mm, ArchitecturalEditRules.SlabShapeTop(3000 * mm, 200 * mm, true, 50 * mm), 9);
            Assert.Equal((3000 + 200) * mm, ArchitecturalEditRules.SlabShapeTop(3000 * mm, 200 * mm, false, 50 * mm), 9);
            Assert.Equal((3000 + 200) * mm, ArchitecturalEditRules.SlabShapeTop(3000 * mm, 200 * mm, true, null), 9);
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "src", "Horizun.Revit", "Commands", "SlabShapeCommand.cs"));
            Assert.Contains("ArchitecturalEditRules.SlabShapeTop(", src);
            Assert.Contains("ROOF_ATTR_THICKNESS_PARAM", src);
        }

        [Fact]
        public void The_slab_shape_verify_reads_one_convention_for_the_edit()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(Root(), "src", "Horizun.Revit", "Commands", "SlabShapeCommand.cs"));
            Assert.Contains("ArchitecturalEditRules.SlabEditConvention(", src);
            Assert.Contains("ArchitecturalEditRules.SlabOffsetRead(", src);
            Assert.DoesNotContain("double found = how == \"absolute\" ? v.Position.Z - top : v.Position.Z;", src);
        }

        private static string Root()
        {
            var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            return d.FullName;
        }

        [Fact]
        public void Covered_length_merges_overlaps_and_clips()
        {
            double c = ArchitecturalEditRules.CoveredLength(new[] { new[] { 0.0, 4 }, new[] { 3.0, 6 }, new[] { 9.0, 12 } }, 0, 10);
            Assert.Equal(7, c, 9);
            Assert.Equal(0, ArchitecturalEditRules.CoveredLength(null, 0, 10));
        }

        [Fact]
        public void Flat_slab_returns_boundary_vertices_and_keeps_an_opening_as_its_own_loop()
        {
            double[][] Loop(double x, double y, double side) => new[] { new[] { x, y, 3 },
                new[] { x + side, y, 3 }, new[] { x + side, y + side, 3 }, new[] { x, y + side, 3 } };
            JObject result = ArchitecturalEditRules.FlatSlabContour(new[] { Loop(0, 0, 10), Loop(3, 3, 4) }, 0.5);
            Assert.Equal(JTokenType.Boolean, result["enabled"].Type);
            Assert.False(result.Value<bool>("enabled"));
            Assert.Equal("top_face_boundary", result.Value<string>("vertex_source"));
            Assert.True(result.Value<bool>("boundary_read_complete"));
            Assert.Equal(8, ((JArray)result["vertices"]).Count);
            Assert.Equal(2, ((JArray)result["boundary_loops"]).Count);
            Assert.Equal(new[] { 6d, 6d, 6d }, result["boundary_loops"][1][0].Take(3).Values<double>());
            Assert.Empty((JArray)result["creases"]);
            Assert.False(ArchitecturalEditRules.FlatSlabContour(new double[0][][], 1).Value<bool>("boundary_read_complete"));
        }

        [Theory]
        [InlineData(false, 2)]
        [InlineData(true, 1)]
        public void Slab_split_regenerates_before_reading_the_editor_and_after_enabling(bool initiallyEnabled, int refreshes)
        {
            int generation = 0;
            bool enabled = initiallyEnabled;
            int editor = ArchitecturalEditRules.RefreshSlabEditor(() => generation++,
                () => generation > 0 ? generation : throw new InvalidOperationException("The new floor has not regenerated."),
                current => { Assert.Equal(generation, current); return enabled; },
                current => { Assert.Equal(generation, current); enabled = true; }, out bool wasEnabled);
            Assert.Equal(initiallyEnabled, wasEnabled);
            Assert.True(enabled);
            Assert.Equal(refreshes, generation);
            Assert.Equal(generation, editor);
        }

        [Fact]
        public void Split_with_two_new_endpoints_refreshes_after_each_insertion_before_using_vertices()
        {
            int generation = 0;
            var pending = new System.Collections.Generic.List<int>();
            var committed = new System.Collections.Generic.HashSet<int> { 5 };
            bool inserted = false;
            ArchitecturalEditRules.InsertSlabSplit(10, 20,
                () =>
                {
                    committed.UnionWith(pending);
                    pending.Clear();
                    return ++generation;
                },
                (epoch, point) => committed.Contains(point) ? Tuple.Create(epoch, point) : null,
                point => ArchitecturalEditRules.SlabEndpoint.Interior,
                (epoch, point) =>
                {
                    Assert.Equal(generation, epoch);
                    Assert.Empty(pending);
                    pending.Add(point);
                },
                (epoch, start, end, insertedBoundaryVertex) =>
                {
                    Assert.False(insertedBoundaryVertex);
                    Assert.Equal(3, epoch);
                    Assert.Equal(Tuple.Create(epoch, 10), start);
                    Assert.Equal(Tuple.Create(epoch, 20), end);
                    inserted = true;
                },
                _ => throw new InvalidOperationException("Interior endpoints do not use a support line."));
            Assert.True(inserted);
        }

        [Fact]
        public void Edge_to_edge_split_uses_a_support_line_when_boundary_insertion_is_unavailable()
        {
            int supportCalls = 0;
            ArchitecturalEditRules.InsertSlabSplit<int, string, object>(0, 4000,
                () => "enabled editor", (_, _) => null, _ => ArchitecturalEditRules.SlabEndpoint.Boundary,
                (_, _) => throw new InvalidOperationException("AddPoint requires an interior point."),
                (_, _, _, _) => throw new InvalidOperationException("Neither end is an existing vertex."),
                editor => { Assert.Equal("enabled editor", editor); supportCalls++; },
                canAddBoundaryVertex: false);
            Assert.Equal(1, supportCalls);
            Assert.Equal(9.25, ArchitecturalEditRules.FlatFloorSupportElevation(new[] { 10.0, 10.0 }, new[] { 9.25 }, 0.001));
            Assert.Null(ArchitecturalEditRules.FlatFloorSupportElevation(new[] { 10.0, 10.1 }, new[] { 9.25 }, 0.001));
            Assert.Null(ArchitecturalEditRules.FlatFloorSupportElevation(new[] { 10.0 }, new[] { 9.25, double.NaN }, 0.001));
            Assert.Null(ArchitecturalEditRules.FlatFloorSupportElevation(new[] { 10.0 }, Array.Empty<double>(), 0.001));
        }

        [Fact]
        public void Revit_with_boundary_vertex_insertion_uses_the_exact_two_edge_points()
        {
            var points = new HashSet<int>();
            var vertices = new Dictionary<int, object>();
            int epoch = 0, addedAtEpoch = 0;
            bool split = false;
            ArchitecturalEditRules.InsertSlabSplit<int, int, object>(40000, 44000,
                () =>
                {
                    vertices = points.ToDictionary(point => point, _ => new object());
                    return ++epoch;
                },
                (editor, point) =>
                {
                    Assert.Equal(epoch, editor);
                    return vertices.TryGetValue(point, out object vertex) ? vertex : null;
                },
                _ => ArchitecturalEditRules.SlabEndpoint.Boundary,
                (editor, point) =>
                {
                    Assert.True(editor > addedAtEpoch);
                    addedAtEpoch = editor;
                    Assert.True(points.Add(point));
                },
                (editor, a, b, insertedBoundaryVertex) =>
                {
                    Assert.True(insertedBoundaryVertex);
                    Assert.True(editor > addedAtEpoch);
                    Assert.Same(vertices[40000], a);
                    Assert.Same(vertices[44000], b);
                    split = true;
                },
                _ => throw new InvalidOperationException("The boundary vertices are available."), canAddBoundaryVertex: true);
            Assert.Equal(2, vertices.Count);
            Assert.True(split);
        }

        [Theory]
        [InlineData(ArchitecturalEditRules.SlabEndpoint.Outside, "outside")]
        [InlineData(ArchitecturalEditRules.SlabEndpoint.Boundary, "cannot insert")]
        public void A_missing_noninterior_endpoint_does_not_reach_AddPoint(ArchitecturalEditRules.SlabEndpoint location, string reason)
        {
            var refusal = Assert.Throws<ArgumentException>(() =>
                ArchitecturalEditRules.InsertSlabSplit<int, int, object>(0, 4000, () => 1, (_, _) => null,
                    point => point == 0 ? location : ArchitecturalEditRules.SlabEndpoint.Interior,
                    (_, _) => throw new InvalidOperationException("No points should have been added."),
                    (_, _, _, _) => throw new InvalidOperationException("No vertex split should have run."),
                    _ => throw new InvalidOperationException("This is not an edge-to-edge chord.")));
            Assert.Contains(reason, refusal.Message);
        }

        [Fact]
        public void Curtain_operations_refuse_fields_that_do_not_belong()
        {
            Assert.Null(ArchitecturalEditRules.ValidateCurtain(J("{operation:'read',element_id:1}")));
            Assert.Contains("mode", ArchitecturalEditRules.ValidateCurtain(J("{operation:'read',element_id:1,mode:'add'}")));
            Assert.Contains("exactly one", ArchitecturalEditRules.ValidateCurtain(J("{operation:'add_grid_line',element_id:1,direction:'u'}")));
            Assert.Null(ArchitecturalEditRules.ValidateCurtain(J("{operation:'add_grid_line',element_id:1,direction:'v',offset:900}")));
            Assert.Contains("mullion_type_id", ArchitecturalEditRules.ValidateCurtain(J("{operation:'set_mullions',element_id:1,grid_line_id:2,mode:'add'}")));
            Assert.Contains("operation must", ArchitecturalEditRules.ValidateCurtain(J("{operation:'explode',element_id:1}")));
            Assert.Contains("exactly one", ArchitecturalEditRules.ValidateCurtain(J("{operation:'set_panel_type',element_id:1,type_id:3}")));
        }

        [Fact]
        public void Slab_shape_modify_takes_vertices_or_one_crease()
        {
            Assert.Null(ArchitecturalEditRules.ValidateSlabShape(J("{operation:'modify_subelement',element_id:1,points:[[0,0,50]]}")));
            Assert.Null(ArchitecturalEditRules.ValidateSlabShape(J("{operation:'modify_subelement',element_id:1,start:[0,0],end:[1,0],offset:5}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateSlabShape(J("{operation:'modify_subelement',element_id:1,points:[[0,0,5]],offset:5}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateSlabShape(J("{operation:'add_point',element_id:1}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateSlabShape(J("{operation:'reset_shape'}")));
        }

        // Measured on Revit 2026 (2026-09-24): add_grid_line, add_point and reset_shape
        // refused EVERY apply as "THE MODEL MOVED AFTER THE DRY RUN" because the plan
        // hashed the raw request, and the apply differs from its rehearsal by dry_run and
        // confirmation_token by construction.
        private static ResolvedPlan HostPlan(JObject request, string typeId = "7", string box = "0,0,0,1,1,1")
        {
            var plan = new ResolvedPlan { Command = "horizun_manage_curtain", DocumentKey = "doc", RevitVersion = "2026", DocumentFingerprint = "fp" };
            plan.Elements.Add(new PlannedElement
            {
                UniqueId = "host-uid", ElementId = 42, Category = "Walls", Action = PlannedAction.Modify, GeometryFingerprint = box,
                BeforeValues = new System.Collections.Generic.Dictionary<string, string> { { "type_id", typeId } },
                ProposedValues = new System.Collections.Generic.Dictionary<string, string> { { "request", ArchitecturalEditRules.ProposedRequest(request) } }
            });
            return plan;
        }

        [Fact]
        public void A_rehearsal_and_its_apply_resolve_to_the_same_plan()
        {
            var rehearsal = J("{target_document:'HZ_WRITE',operation:'add_grid_line',element_id:42,direction:'v',offset:1234}");
            var apply = J("{offset:1234,direction:'v',element_id:42,operation:'add_grid_line',target_document:'HZ_WRITE'," +
                          "dry_run:false,confirmation_token:'tok-1',idempotency_key:'k',transaction_name:'x'}");
            Assert.Equal(ArchitecturalEditRules.ProposedRequest(rehearsal), ArchitecturalEditRules.ProposedRequest(apply));
            Assert.Equal(HostPlan(rehearsal).Fingerprint(), HostPlan(apply).Fingerprint());
            var slab = J("{target_document:'HZ_WRITE',operation:'reset_shape',element_id:9}");
            var slabApply = J("{target_document:'HZ_WRITE',operation:'reset_shape',element_id:9,dry_run:false,confirmation_token:'t'}");
            Assert.Equal(HostPlan(slab).Fingerprint(), HostPlan(slabApply).Fingerprint());
        }

        [Fact]
        public void The_plan_still_refuses_a_different_edit_or_a_host_somebody_changed()
        {
            var rehearsal = J("{target_document:'HZ_WRITE',operation:'add_grid_line',element_id:42,direction:'v',offset:1234}");
            var otherOffset = J("{target_document:'HZ_WRITE',operation:'add_grid_line',element_id:42,direction:'v',offset:1500,dry_run:false,confirmation_token:'t'}");
            Assert.NotEqual(HostPlan(rehearsal).Fingerprint(), HostPlan(otherOffset).Fingerprint());
            // Same request, but the host's type or shape moved between the dry run and the apply.
            Assert.NotEqual(HostPlan(rehearsal).Fingerprint(), HostPlan(rehearsal, typeId: "8").Fingerprint());
            Assert.NotEqual(HostPlan(rehearsal).Fingerprint(), HostPlan(rehearsal, box: "0,0,0,1,1,2").Fingerprint());
            Assert.Contains("type_id", ResolvedPlan.DescribeDrift(HostPlan(rehearsal), HostPlan(rehearsal, typeId: "8")));
            Assert.Contains("geometry", ResolvedPlan.DescribeDrift(HostPlan(rehearsal), HostPlan(rehearsal, box: "0,0,0,1,1,2")));
            Assert.Contains("not the model", ResolvedPlan.DescribeDrift(HostPlan(rehearsal), HostPlan(otherOffset)));
        }

        [Theory]
        [InlineData("WallType", "Basic", "OST_Walls", false, true)]
        [InlineData("WallType", "Curtain", "OST_Walls", false, false)]
        [InlineData("WallType", "Stacked", "OST_Walls", false, false)]
        [InlineData("PanelType", null, "OST_CurtainWallPanels", false, true)]
        [InlineData("FamilySymbol", null, "OST_CurtainWallPanels", false, true)]
        [InlineData("FamilySymbol", null, "OST_Doors", true, true)]
        [InlineData("FamilySymbol", null, "OST_Windows", true, true)]
        [InlineData("FamilySymbol", null, "OST_Doors", false, false)]
        [InlineData("FamilySymbol", null, "OST_Furniture", false, false)]
        [InlineData("MullionType", null, "OST_CurtainWallMullions", false, false)]
        public void A_curtain_panel_takes_panel_types_curtain_doors_and_basic_walls_only(string cls, string kind, string cat, bool panelFamily, bool ok) =>
            Assert.Equal(ok, ArchitecturalEditRules.CurtainPanelTypeRefusal(cls, kind, cat, panelFamily) == null);

        [Fact]
        public void A_refused_panel_type_says_what_it_is_not_that_a_wall_type_is_fine()
        {
            string why = ArchitecturalEditRules.CurtainPanelTypeRefusal("WallType", "Curtain", "OST_Walls", false);
            Assert.Contains("curtain wall type", why);
            Assert.Contains("BASIC", why);
        }

        [Fact]
        public void Railing_takes_a_host_or_a_path_never_both()
        {
            Assert.Null(ArchitecturalEditRules.ValidateRailing(J("{type_id:1,host_id:2,placement:'stringer'}")));
            Assert.Null(ArchitecturalEditRules.ValidateRailing(J("{type_id:1,path:[[0,0,0],[1000,0,0]],level_id:3}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateRailing(J("{type_id:1,host_id:2,path:[[0,0,0],[1,0,0]],level_id:3}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateRailing(J("{type_id:1,path:[[0,0,0],[1,0,0]]}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateRailing(J("{host_id:2}")));
            Assert.NotNull(ArchitecturalEditRules.ValidateRailing(J("{type_id:1,path:[[0,0,0],[1,0,0]],level_id:3,placement:'treads'}")));
        }
    }
}
