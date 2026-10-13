// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Area reinforcement, path reinforcement and fabric (acero 3.0, L4b).
//
// Three Revit systems that each take a boundary or a path on ONE face of ONE host,
// and a handful of declared numbers. Everything Revit-free about them is proved
// here: the strict schema, the polygon and the polyline, whether they lie on a face
// of the host's own boundary, and the comparisons the apply and the audit run on
// the parameters they read back. Nothing is defaulted, and every refusal names its
// field.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SystemReinforcementTests
    {
        private const string Head = @"{
  'schema': 'horizun.structural-requirements/1',
  'requirement_set': { 'id': 'slab-s1', 'version': '1.0.0' },
  'units': 'millimeter',
  'bar_types': [ { 'id': 'T10', 'type_name': '10M' }, { 'id': 'T12', 'type_name': '12M' } ],";

        private static StructuralRequirementSet Load(string body)
        {
            return StructuralRequirementSet.Load(JObject.Parse((Head + body + "}").Replace('\'', '"')));
        }

        private const string Area = @"
  'area_rules': [ {
      'id': 'A1',
      'host': { 'element_ids': [ 501 ] },
      'boundary_mm': [[0,0,200],[4000,0,200],[4000,3000,200],[0,3000,200]],
      'major_direction': [1,0,0],
      'area_type_name': 'Area Reinforcement 1',
      'layers': {
        'top_major': { 'bar_type': 'T10', 'spacing_mm': 200 },
        'top_minor': { 'bar_type': 'T10', 'spacing_mm': 250 },
        'bottom_major': { 'bar_type': 'T12', 'spacing_mm': 150 }
      },
      'mark': 'AR1'
  } ]";

        private const string Path = @"
  'path_rules': [ {
      'id': 'P1',
      'host': { 'element_ids': [ 501 ] },
      'path_mm': [[0,0,200],[4000,0,200],[4000,3000,200]],
      'path_type_name': 'Path Reinforcement 1',
      'flip': false,
      'primary': { 'bar_type': 'T12', 'spacing_mm': 200, 'length_mm': 1200 }
  } ]";

        private const string Fabric = @"
  'fabric_rules': [ {
      'id': 'F1',
      'host': { 'element_ids': [ 501 ] },
      'boundary_mm': [[0,0,200],[4000,0,200],[4000,3000,200],[0,3000,200]],
      'major_direction': [1,0,0],
      'major_direction_origin_mm': [0,0,200],
      'fabric_area_type_name': 'Fabric Area 1',
      'fabric_sheet_type_name': 'A193',
      'cover_offset_mm': 10
  } ]";

        // ============================================================ schema

        [Fact]
        public void The_three_arrays_are_admitted_at_the_root()
        {
            Assert.Contains("area_rules", StructuralRequirementSet.RootKeys);
            Assert.Contains("path_rules", StructuralRequirementSet.RootKeys);
            Assert.Contains("fabric_rules", StructuralRequirementSet.RootKeys);
        }

        [Fact]
        public void An_area_rule_loads_with_every_declared_value()
        {
            StructuralRequirementSet s = Load(Area);
            Assert.True(s.Ok, s.Error);
            StructuralAreaRule a = Assert.Single(s.AreaRules);
            Assert.Equal("A1", a.Id);
            Assert.Equal(new long[] { 501 }, a.Host.ElementIds);
            Assert.Equal(4, a.BoundaryMm.Count);
            Assert.Equal(new double[] { 1, 0, 0 }, a.MajorDirectionMm);
            Assert.Equal("Area Reinforcement 1", a.AreaTypeName);
            Assert.Equal(3, a.Layers.Count);
            StructuralSystemLayer bm = a.Layers.Single(l => l.Name == "bottom_major");
            Assert.Equal("T12", bm.BarTypeId);
            Assert.Equal(150, bm.SpacingMm);
            Assert.Equal("AR1", a.Mark);
            Assert.True(a.Required);
            Assert.Equal(SystemReinforcementRules.FamilySlab, a.LayerFamily);
        }

        [Fact]
        public void A_set_with_only_system_rules_asks_for_something()
        {
            Assert.True(Load(Path).Ok);
            Assert.True(Load(Fabric).Ok);
        }

        [Fact]
        public void A_path_rule_loads_with_every_declared_value()
        {
            StructuralRequirementSet s = Load(Path);
            StructuralPathRule p = Assert.Single(s.PathRules);
            Assert.Equal(3, p.PathMm.Count);
            Assert.False(p.Flip);
            Assert.Equal("T12", p.PrimaryBarTypeId);
            Assert.Equal(200, p.PrimarySpacingMm);
            Assert.Equal(1200, p.PrimaryLengthMm);
            Assert.Equal("Path Reinforcement 1", p.PathTypeName);
        }

        [Fact]
        public void A_fabric_rule_loads_with_every_declared_value()
        {
            StructuralRequirementSet s = Load(Fabric);
            StructuralFabricRule f = Assert.Single(s.FabricRules);
            Assert.Equal("Fabric Area 1", f.FabricAreaTypeName);
            Assert.Equal("A193", f.FabricSheetTypeName);
            Assert.Equal(10, f.CoverOffsetMm);
            Assert.Equal(new double[] { 0, 0, 200 }, f.MajorDirectionOriginMm);
        }

        [Fact]
        public void A_fabric_rule_without_cover_offset_leaves_it_undeclared_not_zero()
        {
            StructuralRequirementSet s = Load(Fabric.Replace(",\n      'cover_offset_mm': 10", ""));
            Assert.True(s.Ok, s.Error);
            Assert.Null(s.FabricRules[0].CoverOffsetMm);
        }

        [Theory]
        [InlineData("area_rules", "'boundry_mm'", "boundary_mm")]
        [InlineData("path_rules", "'path_type'", "path_type_name")]
        [InlineData("fabric_rules", "'cover_offset'", "cover_offset_mm")]
        public void An_unknown_key_on_a_system_rule_is_refused_with_its_path(string array, string bad, string meant)
        {
            string body = array == "area_rules" ? Area.Replace("'boundary_mm'", bad)
                        : array == "path_rules" ? Path.Replace("'path_type_name'", bad)
                        : Fabric.Replace("'cover_offset_mm'", bad);
            StructuralRequirementSet s = Load(body);
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.StartsWith(array + "[0].", s.Path);
            Assert.Contains(meant, s.DidYouMean);
        }

        [Fact]
        public void An_unknown_layer_name_is_refused_as_an_unknown_key()
        {
            StructuralRequirementSet s = Load(Area.Replace("'top_minor'", "'top_middle'"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, s.Code);
            Assert.Equal("area_rules['A1'].layers.top_middle", s.Path);
        }

        [Fact]
        public void An_unknown_key_inside_a_layer_or_primary_is_refused()
        {
            StructuralRequirementSet a = Load(Area.Replace("'spacing_mm': 250", "'spacing': 250"));
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, a.Code);
            Assert.Equal("area_rules['A1'].layers.top_minor.spacing", a.Path);
            StructuralRequirementSet p = Load(Path.Replace("'length_mm'", "'lenght_mm'"));
            Assert.Equal(StructuralRequirementSet.CodeUnknownKey, p.Code);
            Assert.Equal("path_rules['P1'].primary.lenght_mm", p.Path);
        }

        [Fact]
        public void A_slab_layer_and_a_wall_layer_in_one_rule_are_refused()
        {
            StructuralRequirementSet s = Load(Area.Replace("'bottom_major'", "'interior_major'"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
            Assert.Contains("top", s.Error);
            Assert.Contains("interior", s.Error);
        }

        [Fact]
        public void Wall_layers_load_as_the_wall_family()
        {
            StructuralRequirementSet s = Load(Area
                .Replace("'top_major'", "'exterior_major'").Replace("'top_minor'", "'exterior_minor'")
                .Replace("'bottom_major'", "'interior_major'"));
            Assert.True(s.Ok, s.Error);
            Assert.Equal(SystemReinforcementRules.FamilyWall, s.AreaRules[0].LayerFamily);
        }

        [Fact]
        public void An_area_rule_with_no_layer_is_refused()
        {
            StructuralRequirementSet s = Load(Area.Replace(@"'layers': {
        'top_major': { 'bar_type': 'T10', 'spacing_mm': 200 },
        'top_minor': { 'bar_type': 'T10', 'spacing_mm': 250 },
        'bottom_major': { 'bar_type': 'T12', 'spacing_mm': 150 }
      }", "'layers': {}"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.Equal("area_rules['A1'].layers", s.Path);
        }

        [Theory]
        [InlineData("'bar_type': 'T10', 'spacing_mm': 250", "'spacing_mm': 250", StructuralRequirementSet.CodeMissing)]
        [InlineData("'bar_type': 'T10', 'spacing_mm': 250", "'bar_type': 'T10'", StructuralRequirementSet.CodeMissing)]
        [InlineData("'bar_type': 'T10', 'spacing_mm': 250", "'bar_type': 'T99', 'spacing_mm': 250", StructuralRequirementSet.CodeUnresolvedReference)]
        [InlineData("'bar_type': 'T10', 'spacing_mm': 250", "'bar_type': 'T10', 'spacing_mm': 0", StructuralRequirementSet.CodeUnknownValue)]
        [InlineData("'bar_type': 'T10', 'spacing_mm': 250", "'bar_type': 'T10', 'spacing_mm': '250'", StructuralRequirementSet.CodeNotANumber)]
        public void A_layer_must_declare_a_known_bar_type_and_a_positive_spacing(string from, string to, string code)
        {
            StructuralRequirementSet s = Load(Area.Replace(from, to));
            Assert.False(s.Ok);
            Assert.Equal(code, s.Code);
            Assert.StartsWith("area_rules['A1'].layers.top_minor", s.Path);
        }

        [Theory]
        [InlineData("'area_type_name': 'Area Reinforcement 1',", "")]
        [InlineData("'major_direction': [1,0,0],", "")]
        [InlineData("'boundary_mm': [[0,0,200],[4000,0,200],[4000,3000,200],[0,3000,200]],", "")]
        public void An_area_rule_missing_a_declared_value_is_refused(string from, string to)
        {
            StructuralRequirementSet s = Load(Area.Replace(from, to));
            Assert.False(s.Ok);
            Assert.True(s.Code == StructuralRequirementSet.CodeMissing || s.Code == StructuralRequirementSet.CodeGeometry, s.Code);
            Assert.StartsWith("area_rules['A1']", s.Path);
        }

        [Theory]
        [InlineData("{ 'category': 'OST_Floors' }")]
        [InlineData("{ 'element_ids': [ 501, 502 ] }")]
        [InlineData("{ 'element_ids': [] }")]
        public void A_system_rule_is_hosted_by_exactly_one_element_id(string host)
        {
            StructuralRequirementSet s = Load(Area.Replace("{ 'element_ids': [ 501 ] }", host));
            Assert.False(s.Ok);
            Assert.Equal("area_rules['A1'].host", s.Path);
        }

        [Fact]
        public void A_system_rule_id_shares_the_namespace_of_every_other_rule()
        {
            StructuralRequirementSet s = Load(Area + "," + Path.Replace("'P1'", "'A1'"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeDuplicateId, s.Code);
        }

        [Fact]
        public void A_refused_set_carries_no_system_rules()
        {
            StructuralRequirementSet s = Load(Area + "," + Path + "," + Fabric.Replace("'A193'", "''"));
            Assert.False(s.Ok);
            Assert.Empty(s.AreaRules);
            Assert.Empty(s.PathRules);
            Assert.Empty(s.FabricRules);
        }

        // -------------------------------------------------- boundary polygon

        [Theory]
        [InlineData("[[0,0,200],[4000,0,200]]", "at least three")]
        [InlineData("[[0,0,200],[4000,0,200],[4000,3000,200],[0,0,200]]", "repeat")]
        [InlineData("[[0,0,200],[4000,0,200],[4000,0,200],[0,3000,200]]", "repeat")]
        [InlineData("[[0,0,200],[4000,0,200],[4000,3000,200],[0,3000,260]]", "plane")]
        [InlineData("[[0,0,200],[4000,0,200],[8000,0,200]]", "encloses")]
        [InlineData("[[0,0,200],[4000,3000,200],[4000,0,200],[0,3000,200]]", "crosses")]
        public void A_boundary_must_be_a_closed_planar_simple_polygon(string boundary, string says)
        {
            StructuralRequirementSet s = Load(Area.Replace(
                "[[0,0,200],[4000,0,200],[4000,3000,200],[0,3000,200]]", boundary));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeGeometry, s.Code);
            Assert.Equal("area_rules['A1'].boundary_mm", s.Path);
            Assert.Contains(says, s.Error);
        }

        [Fact]
        public void A_boundary_within_the_length_tolerance_of_its_plane_is_planar()
        {
            StructuralRequirementSet s = Load(Area.Replace(
                "[[0,0,200],[4000,0,200],[4000,3000,200],[0,3000,200]]",
                "[[0,0,200],[4000,0,200],[4000,3000,200.5],[0,3000,200]]"));
            Assert.True(s.Ok, s.Error);
        }

        [Fact]
        public void The_major_direction_must_lie_in_the_boundary_plane()
        {
            StructuralRequirementSet s = Load(Area.Replace("'major_direction': [1,0,0]", "'major_direction': [1,0,1]"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeGeometry, s.Code);
            Assert.Equal("area_rules['A1'].major_direction", s.Path);
        }

        [Fact]
        public void A_fabric_origin_off_the_boundary_plane_is_refused()
        {
            StructuralRequirementSet s = Load(Fabric.Replace("'major_direction_origin_mm': [0,0,200]",
                                                             "'major_direction_origin_mm': [0,0,0]"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeGeometry, s.Code);
            Assert.Equal("fabric_rules['F1'].major_direction_origin_mm", s.Path);
        }

        [Theory]
        [InlineData("'major_direction_origin_mm': [0,0,200],", "")]
        [InlineData("'fabric_area_type_name': 'Fabric Area 1',", "")]
        [InlineData("'fabric_sheet_type_name': 'A193',", "")]
        public void A_fabric_rule_missing_a_declared_value_is_refused(string from, string to)
        {
            StructuralRequirementSet s = Load(Fabric.Replace(from, to));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
        }

        [Theory]
        [InlineData("-5")]
        [InlineData("'ten'")]
        public void A_fabric_cover_offset_must_be_a_number_not_below_zero(string v)
        {
            StructuralRequirementSet s = Load(Fabric.Replace("'cover_offset_mm': 10", "'cover_offset_mm': " + v));
            Assert.False(s.Ok);
            Assert.Equal("fabric_rules['F1'].cover_offset_mm", s.Path);
        }

        // ------------------------------------------------------------- path

        [Theory]
        [InlineData("[[0,0,200]]", "at least two")]
        [InlineData("[[0,0,200],[4000,0,200],[4000,0,200]]", "repeat")]
        [InlineData("[[0,0,200],[4000,0,200],[0,0,200]]", "repeat")]
        [InlineData("[[0,0,200],[4000,0,200],[4000,3000,200],[0,3000,900]]", "plane")]
        public void A_path_must_be_an_open_planar_polyline(string path, string says)
        {
            StructuralRequirementSet s = Load(Path.Replace("[[0,0,200],[4000,0,200],[4000,3000,200]]", path));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeGeometry, s.Code);
            Assert.Equal("path_rules['P1'].path_mm", s.Path);
            Assert.Contains(says, s.Error);
        }

        [Theory]
        [InlineData("'flip': false,", "", StructuralRequirementSet.CodeMissing)]
        [InlineData("'flip': false,", "'flip': 'no',", StructuralRequirementSet.CodeUnknownValue)]
        public void Flip_is_declared_as_a_boolean_never_defaulted(string from, string to, string code)
        {
            StructuralRequirementSet s = Load(Path.Replace(from, to));
            Assert.False(s.Ok);
            Assert.Equal(code, s.Code);
            Assert.Equal("path_rules['P1'].flip", s.Path);
        }

        [Theory]
        [InlineData("'bar_type': 'T12', ", "")]
        [InlineData("'spacing_mm': 200, ", "")]
        [InlineData(", 'length_mm': 1200", "")]
        public void The_primary_bars_declare_type_spacing_and_length(string from, string to)
        {
            StructuralRequirementSet s = Load(Path.Replace(from, to));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeMissing, s.Code);
            Assert.StartsWith("path_rules['P1'].primary", s.Path);
        }

        [Fact]
        public void A_path_length_of_zero_is_refused()
        {
            StructuralRequirementSet s = Load(Path.Replace("'length_mm': 1200", "'length_mm': 0"));
            Assert.False(s.Ok);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue, s.Code);
        }

        [Fact]
        public void Required_must_be_a_boolean()
        {
            StructuralRequirementSet s = Load(Path.Replace("'flip': false,", "'flip': false, 'required': 'yes',"));
            Assert.False(s.Ok);
            Assert.Equal("path_rules['P1'].required", s.Path);
        }

        // ============================================== pure geometry checks

        private static HostMesh Slab() => HostMesh.Box(new double[] { 0, 0, 0 }, new double[] { 4000, 3000, 200 });

        private static List<double[]> Pts(params double[][] p) => p.ToList();

        [Fact]
        public void A_boundary_on_the_top_face_is_on_a_face_and_reports_its_outward_normal()
        {
            var b = Pts(new double[] { 100, 100, 200 }, new double[] { 3900, 100, 200 },
                        new double[] { 3900, 2900, 200 }, new double[] { 100, 2900, 200 });
            FaceFit fit = SystemReinforcementRules.OnHostFace(b, Slab(), 1.0, 1.0);
            Assert.True(fit.Ok, fit.Why);
            Assert.Equal(0, fit.NormalMm[0], 6);
            Assert.Equal(0, fit.NormalMm[1], 6);
            Assert.Equal(1, fit.NormalMm[2], 6);
        }

        [Fact]
        public void A_boundary_on_the_bottom_face_reports_the_bottom_normal()
        {
            var b = Pts(new double[] { 0, 0, 0 }, new double[] { 4000, 0, 0 }, new double[] { 4000, 3000, 0 });
            FaceFit fit = SystemReinforcementRules.OnHostFace(b, Slab(), 1.0, 1.0);
            Assert.True(fit.Ok, fit.Why);
            Assert.Equal(-1, fit.NormalMm[2], 6);
        }

        [Fact]
        public void A_boundary_inside_the_concrete_is_on_no_face()
        {
            var b = Pts(new double[] { 100, 100, 100 }, new double[] { 3900, 100, 100 }, new double[] { 3900, 2900, 100 });
            FaceFit fit = SystemReinforcementRules.OnHostFace(b, Slab(), 1.0, 1.0);
            Assert.False(fit.Ok);
            Assert.Equal(SystemReinforcementRules.CodeNotOnHostFace, fit.Code);
        }

        [Fact]
        public void A_boundary_in_the_face_plane_that_runs_past_the_face_is_outside_it()
        {
            var b = Pts(new double[] { 100, 100, 200 }, new double[] { 4500, 100, 200 }, new double[] { 4500, 2900, 200 });
            FaceFit fit = SystemReinforcementRules.OnHostFace(b, Slab(), 1.0, 1.0);
            Assert.False(fit.Ok);
            Assert.Equal(SystemReinforcementRules.CodeOutsideHostFace, fit.Code);
            Assert.Contains("1", fit.Why);
        }

        [Fact]
        public void A_boundary_on_the_face_outline_itself_is_on_the_face()
        {
            var b = Pts(new double[] { 0, 0, 200 }, new double[] { 4000, 0, 200 },
                        new double[] { 4000, 3000, 200 }, new double[] { 0, 3000, 200 });
            Assert.True(SystemReinforcementRules.OnHostFace(b, Slab(), 1.0, 1.0).Ok);
        }

        [Fact]
        public void A_path_on_a_side_face_reports_the_side_normal()
        {
            var p = Pts(new double[] { 100, 0, 50 }, new double[] { 3900, 0, 50 });
            FaceFit fit = SystemReinforcementRules.OnHostFace(p, Slab(), 1.0, 1.0);
            Assert.True(fit.Ok, fit.Why);
            Assert.Equal(-1, fit.NormalMm[1], 6);
        }

        [Fact]
        public void No_mesh_is_not_a_face()
        {
            FaceFit fit = SystemReinforcementRules.OnHostFace(
                Pts(new double[] { 0, 0, 0 }, new double[] { 1, 0, 0 }), null, 1.0, 1.0);
            Assert.False(fit.Ok);
            Assert.Equal(SystemReinforcementRules.CodeNotOnHostFace, fit.Code);
        }

        [Theory]
        [InlineData(1, 0, 0, true)]
        [InlineData(0, 1, 0, true)]
        [InlineData(1, 0, 0.001, true)]
        [InlineData(1, 0, 1, false)]
        [InlineData(0, 0, 1, false)]
        public void A_direction_lies_in_a_plane_within_the_angle_tolerance(double x, double y, double z, bool inPlane)
        {
            Assert.Equal(inPlane, SystemReinforcementRules.InPlane(new[] { x, y, z }, new double[] { 0, 0, 1 }, 1.0));
        }

        // ================================================ read-back checks

        [Fact]
        public void A_length_read_back_within_tolerance_is_verified_and_an_unreadable_one_is_not()
        {
            Assert.True((bool)SystemReinforcementRules.CheckLengthMm(200, 200.4, 1.0)["verified"]);
            Assert.False((bool)SystemReinforcementRules.CheckLengthMm(200, 203, 1.0)["verified"]);
            JObject unread = SystemReinforcementRules.CheckLengthMm(200, null, 1.0);
            Assert.False((bool)unread["verified"]);
            Assert.NotNull(unread["why"]);
        }

        [Fact]
        public void A_direction_read_back_is_compared_as_an_axis()
        {
            Assert.True((bool)SystemReinforcementRules.CheckDirection(new double[] { 1, 0, 0 }, new double[] { -1, 0, 0 }, 1.0)["verified"]);
            Assert.False((bool)SystemReinforcementRules.CheckDirection(new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, 1.0)["verified"]);
            Assert.False((bool)SystemReinforcementRules.CheckDirection(new double[] { 1, 0, 0 }, null, 1.0)["verified"]);
        }

        [Fact]
        public void At_least_one_bar_must_have_been_produced()
        {
            Assert.True((bool)SystemReinforcementRules.CheckAtLeast(1, 14)["verified"]);
            Assert.False((bool)SystemReinforcementRules.CheckAtLeast(1, 0)["verified"]);
            Assert.False((bool)SystemReinforcementRules.CheckAtLeast(1, null)["verified"]);
        }

        private static Dictionary<string, SystemLayerWant> Want() => new Dictionary<string, SystemLayerWant>
        {
            ["top_major"] = new SystemLayerWant { BarTypeId = 11, SpacingMm = 200 },
            ["bottom_major"] = new SystemLayerWant { BarTypeId = 12, SpacingMm = 150 }
        };

        private static Dictionary<string, SystemLayerRead> ReadAsBuilt() => new Dictionary<string, SystemLayerRead>
        {
            ["top_major"] = new SystemLayerRead { Active = true, SpacingMm = 200, BarTypeId = 11, HookTypeId = -1 },
            ["top_minor"] = new SystemLayerRead { Active = false },
            ["bottom_major"] = new SystemLayerRead { Active = true, SpacingMm = 150.0004, BarTypeId = 12, HookTypeId = -1 },
            ["bottom_minor"] = new SystemLayerRead { Active = false }
        };

        [Fact]
        public void Area_layers_read_as_declared_are_verified()
        {
            JObject checks = SystemReinforcementRules.AreaLayerChecks(SystemReinforcementRules.FamilySlab, Want(), ReadAsBuilt(), 1.0);
            Assert.True(SystemReinforcementRules.AllVerified(checks), checks.ToString());
            Assert.NotNull(checks["top_minor.active"]);
            Assert.NotNull(checks["bottom_major.spacing_mm"]);
        }

        [Fact]
        public void An_undeclared_layer_left_active_is_not_verified()
        {
            Dictionary<string, SystemLayerRead> read = ReadAsBuilt();
            read["top_minor"] = new SystemLayerRead { Active = true, SpacingMm = 300, BarTypeId = 11, HookTypeId = -1 };
            JObject checks = SystemReinforcementRules.AreaLayerChecks(SystemReinforcementRules.FamilySlab, Want(), read, 1.0);
            Assert.False(SystemReinforcementRules.AllVerified(checks));
            Assert.False((bool)checks["top_minor.active"]["verified"]);
        }

        [Theory]
        [InlineData("spacing")]
        [InlineData("type")]
        [InlineData("hook")]
        [InlineData("unreadable")]
        public void A_declared_layer_that_reads_back_differently_is_not_verified(string what)
        {
            Dictionary<string, SystemLayerRead> read = ReadAsBuilt();
            SystemLayerRead r = read["bottom_major"];
            if (what == "spacing") r.SpacingMm = 180;
            if (what == "type") r.BarTypeId = 99;
            if (what == "hook") r.HookTypeId = 77;
            if (what == "unreadable") r.SpacingMm = null;
            JObject checks = SystemReinforcementRules.AreaLayerChecks(SystemReinforcementRules.FamilySlab, Want(), read, 1.0);
            Assert.False(SystemReinforcementRules.AllVerified(checks));
        }

        [Fact]
        public void A_missing_layer_read_is_not_verified()
        {
            Dictionary<string, SystemLayerRead> read = ReadAsBuilt();
            read.Remove("top_minor");
            Assert.False(SystemReinforcementRules.AllVerified(
                SystemReinforcementRules.AreaLayerChecks(SystemReinforcementRules.FamilySlab, Want(), read, 1.0)));
        }

        [Fact]
        public void A_not_applicable_check_does_not_fail_and_an_unknown_one_does()
        {
            var checks = new JObject
            {
                ["a"] = SystemReinforcementRules.CheckLengthMm(1, 1, 1),
                ["b"] = new JObject { ["verified"] = null, ["applicable"] = false, ["why"] = "x" }
            };
            Assert.True(SystemReinforcementRules.AllVerified(checks));
            checks["c"] = new JObject { ["verified"] = null, ["why"] = "unread" };
            Assert.False(SystemReinforcementRules.AllVerified(checks));
            Assert.False(SystemReinforcementRules.AllVerified(new JObject()));
        }

        [Fact]
        public void The_layer_vocabulary_is_closed_and_split_by_host_family()
        {
            Assert.Equal(SystemReinforcementRules.FamilySlab, SystemReinforcementRules.FamilyOfLayer("bottom_minor"));
            Assert.Equal(SystemReinforcementRules.FamilyWall, SystemReinforcementRules.FamilyOfLayer("interior_minor"));
            Assert.Null(SystemReinforcementRules.FamilyOfLayer("front_major"));
            Assert.Equal(4, SystemReinforcementRules.LayersOf(SystemReinforcementRules.FamilyWall).Length);
            Assert.Equal(SystemReinforcementRules.AllCodes.Length, SystemReinforcementRules.AllCodes.Distinct().Count());
        }

        [Fact]
        public void What_the_audit_does_not_compare_is_named()
        {
            Assert.Contains("area_reinforcement_geometry", RebarFinding.NotChecked);
            Assert.Contains("path_reinforcement_geometry", RebarFinding.NotChecked);
            Assert.Contains("fabric_sheet_layout", RebarFinding.NotChecked);
            Assert.Contains(RebarFinding.SystemParameterDiffers, RebarFinding.All);
        }

        [Fact]
        public void A_type_not_found_names_the_types_the_document_has()
        {
            // Live 2024 (acero L4b): the template's area and path reinforcement types do not list
            // under their categories, and a refusal that only said "not named X" left the harness
            // guessing the template's names. The refusal now names what is there - or that nothing is.
            string why = SystemReinforcementRules.TypeNotFoundWhy("area reinforcement type", "Area 9", new[] { "B", "A", "A" });
            Assert.Contains("no area reinforcement type in this document is named 'Area 9'", why);
            Assert.Contains("it has: 'A', 'B'", why);
            Assert.Contains("this document has no area reinforcement type at all",
                SystemReinforcementRules.TypeNotFoundWhy("area reinforcement type", "Area 9", new string[0]));
            string many = SystemReinforcementRules.TypeNotFoundWhy("path reinforcement type", "P", System.Linq.Enumerable.Range(0, 30).Select(n => "T" + n.ToString("00")));
            Assert.Contains("and 18 more", many);
        }
}
}
