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

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        // ============================================== pure geometry checks

        private static HostMesh Slab() => HostMesh.Box(new double[] { 0, 0, 0 }, new double[] { 4000, 3000, 200 });

        private static List<double[]> Pts(params double[][] p) => p.ToList();

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        

        [Fact]
        public void What_the_audit_does_not_compare_is_named()
        {
            Assert.Contains("area_reinforcement_geometry", RebarFinding.NotChecked);
            Assert.Contains("path_reinforcement_geometry", RebarFinding.NotChecked);
            Assert.Contains("fabric_sheet_layout", RebarFinding.NotChecked);
            Assert.Contains(RebarFinding.SystemParameterDiffers, RebarFinding.All);
        }

        
}
}
