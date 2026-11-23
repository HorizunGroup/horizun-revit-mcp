// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Acero 3.0, L7: the circular hoop and the spiral, built from a PLACED rebar shape
// because Rebar.CreateFromCurves fails on every chain with an arc in it (measured on
// Revit 2024). The shape is named by the caller and its definition is READ before
// anything is built; the kind, the pitch and the finishing turns are declared.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlacedShapeTests
    {
        // A cylinder of radius 250 and height 3000 on the origin, 64 sides.
        private static HostMesh Cylinder(double r = 250, int n = 64)
        {
            var m = new HostMesh { AnyCurvedFace = true, ChordToleranceMm = r * (1 - Math.Cos(Math.PI / n)) };
            int c0 = m.AddVertex(0, 0, 0), c1 = m.AddVertex(0, 0, 3000);
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                m.AddVertex(r * Math.Cos(a), r * Math.Sin(a), 0);
                m.AddVertex(r * Math.Cos(a), r * Math.Sin(a), 3000);
            }
            for (int i = 0; i < n; i++)
            {
                int b0 = 2 + 2 * i, t0 = b0 + 1, b1 = 2 + 2 * ((i + 1) % n), t1 = b1 + 1;
                m.AddTriangle(c0, b1, b0); m.AddTriangle(c1, t0, t1);
                m.AddTriangle(b0, b1, t1); m.AddTriangle(b0, t1, t0);
            }
            return m;
        }





        private const string Hoop = "{ 'shape': 'M_T6', 'kind': 'lapped_circle' }";
        private const string Spiral = "{ 'shape': 'M_SP', 'kind': 'spiral', 'pitch_mm': 75, 'base_finishing_turns': 1, 'top_finishing_turns': 2 }";









































        // ---------------------------------------------------------- re-reading

        private static PlacedShapeGeometry HoopAt(double z) => new PlacedShapeGeometry
        {
            Kind = StructuralShapePlacement.KindLappedCircle, CentreMm = new[] { 0.0, 0, z }, AxisMm = new[] { 0.0, 0, 1 },
            XVec = new[] { 1.0, 0, 0 }, YVec = new[] { 0.0, 1, 0 }, CentrelineRadiusMm = 205
        };

        private static List<double[]> Circle(double r, double z, double fromDeg = 0, double toDeg = 380, int n = 80)
        {
            var pts = new List<double[]>();
            for (int i = 0; i <= n; i++)
            {
                double a = (fromDeg + (toDeg - fromDeg) * i / n) * Math.PI / 180;
                pts.Add(new[] { r * Math.Cos(a), r * Math.Sin(a), z });
            }
            return pts;
        }













        // --------------------------------------------------------------- audit

        private static JObject ExpectedPlaced()
        {
            return new JObject
            {
                ["rule_id"] = "CC#h9#stirrups#all#s3",
                ["host_id"] = 9,
                ["bar_type"] = new JObject { ["id"] = 300, ["nominal_diameter_mm"] = 10.0 },
                ["shape"] = new JObject { ["declared"] = true, ["id"] = 400 },
                ["style"] = "standard",
                ["closed"] = true,
                ["curve_mm"] = new JArray(new JArray(205, 0, 300), new JArray(-205, 0, 300)),
                ["normal"] = new JObject { ["x"] = 0.0, ["y"] = 0.0, ["z"] = 1.0 },
                ["expected_total_steel_length_mm"] = 1288.05,
                ["layout"] = new JObject { ["rule"] = "single", ["number_of_bar_positions"] = 1, ["quantity"] = 1 },
                ["terminations"] = new JObject
                {
                    ["start"] = new JObject { ["hook_type_id"] = -1, ["orientation"] = "left" },
                    ["end"] = new JObject { ["hook_type_id"] = -1, ["orientation"] = "left" }
                },
                ["placement"] = HoopAt(300).ToJson()
            };
        }

        private static JObject ObservedPlaced()
        {
            return new JObject
            {
                ["id"] = 9001,
                ["host"] = new JObject { ["id"] = 9, ["resolved"] = true },
                ["bar_type"] = new JObject { ["resolved"] = true, ["id"] = 300, ["nominal_diameter_mm"] = 10.0 },
                ["shape"] = new JObject { ["id"] = 400, ["resolved"] = true },
                ["style_horizun"] = "standard",
                ["layout"] = new JObject { ["rule_horizun"] = "single", ["number_of_bar_positions"] = 1, ["quantity"] = 1,
                                           ["normal"] = new JObject { ["x"] = 1.0, ["y"] = 0.0, ["z"] = 0.0 } },
                ["terminations"] = new JArray(
                    new JObject { ["end"] = 0, ["hook_type_id"] = -1, ["hook_readable"] = true, ["orientation"] = "right" },
                    new JObject { ["end"] = 1, ["hook_type_id"] = -1, ["hook_readable"] = true, ["orientation"] = "right" }),
                ["geometry"] = new JObject { ["centreline_points_mm"] = new JArray(Circle(205, 300).Select(p => new JArray(p[0], p[1], p[2]))) },
                ["measured"] = new JObject { ["total_length_mm"] = 1500.0, ["bar_centreline_length_mm"] = 1500.0 }
            };
        }







        [Fact]
        public void The_placement_survives_its_own_json()
        {
            PlacedShapeGeometry back = PlacedShapeGeometry.FromJson(HoopAt(300).ToJson());
            Assert.Equal(205, back.CentrelineRadiusMm, 9);
            Assert.Equal(300, back.CentreMm[2], 9);
            Assert.Equal(StructuralShapePlacement.KindLappedCircle, back.Kind);
            Assert.Contains(RebarFinding.PlacedShapeDiffers, RebarFinding.All);
        }
    }
}
