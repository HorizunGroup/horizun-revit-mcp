// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
// Exits per level and ramp geometry for horizun_code_check, Revit-free.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CodeCheckGeometryTests
    {
        private static JObject Config(string json) => JObject.Parse(json.Replace('\'', '"'));

        private static CheckedElement Level(double[] roomAreas, params string[] doorMarks)
        {
            var level = new CheckedElement { Id = 100, CategoryToken = "OST_Levels", Name = "L1", Rooms = new List<CheckedElement>(), Doors = new List<CheckedElement>() };
            long id = 1;
            foreach (double a in roomAreas) level.Rooms.Add(new CheckedElement { Id = id++, AreaM2 = a });
            foreach (string m in doorMarks) level.Doors.Add(new CheckedElement { Id = id++, Mark = m });
            return level;
        }

        private const string Table = "'required_exits': [ { 'max_load': 50, 'exits': 1 }, { 'max_load': 500, 'exits': 2 }, { 'exits': 3 } ]";

        [Fact]
        public void Exits_are_compared_with_the_load_the_set_declares()
        {
            // 1000 m2 at 10 m2/person = 100 people -> 2 exits; one door marked as exit.
            MeasuredValue m = CodeCheckRules.ExitsVsRequired(
                Config("{ 'area_per_person_m2': 10, 'exit_door': { 'mark_prefix': 'EX' }, " + Table + " }"),
                Level(new[] { 600.0, 400.0 }, "EX-1", "P-2"));
            Assert.Equal(-1, m.Value);
            Assert.Equal(100, (double)m.Detail["occupant_load"]);
            Assert.Equal(2, (int)m.Detail["required_exits"]);
            Assert.Equal(1, (int)m.Detail["exit_doors"]);
        }

        [Fact]
        public void Without_rooms_an_exit_rule_or_a_table_the_level_is_not_decidable()
        {
            JObject ok = Config("{ 'area_per_person_m2': 10, 'exit_door': { 'mark_prefix': 'EX' }, " + Table + " }");
            Assert.NotNull(CodeCheckRules.ExitsVsRequired(ok, Level(new double[0], "EX-1")).Unavailable);
            Assert.NotNull(CodeCheckRules.ExitsVsRequired(ok, Level(new[] { 0.0 }, "EX-1")).Unavailable);   // unplaced room only
            Assert.NotNull(CodeCheckRules.ExitsVsRequired(Config("{ 'area_per_person_m2': 10, " + Table + " }"), Level(new[] { 10.0 })).Unavailable);
            Assert.NotNull(CodeCheckRules.ExitsVsRequired(Config("{ 'area_per_person_m2': 10, 'exit_door': { 'mark_prefix': 'EX' }, 'required_exits': [] }"), Level(new[] { 10.0 })).Unavailable);
            Assert.NotNull(CodeCheckRules.ExitsVsRequired(null, Level(new[] { 10.0 })).Unavailable);
        }

        [Fact]
        public void A_room_missing_the_declared_occupant_parameter_makes_the_load_incomplete()
        {
            CheckedElement level = Level(new[] { 50.0, 50.0 }, "EX-1");
            level.Rooms[0].Params["Occ"] = new ParamFact { Exists = true, Number = 20 };
            MeasuredValue m = CodeCheckRules.ExitsVsRequired(
                Config("{ 'occupant_load_parameter': 'Occ', 'exit_door': { 'mark_prefix': 'EX' }, " + Table + " }"), level);
            Assert.Null(m.Value);
            Assert.Contains("incomplete", m.Unavailable);
        }

        private static PlanarFaceFact Face(double nx, double ny, double nz, params double[][] pts)
        {
            var f = new PlanarFaceFact { Nx = nx, Ny = ny, Nz = nz };
            f.Points.AddRange(pts);
            return f;
        }

        [Fact]
        public void Ramp_slope_run_width_and_landing_come_from_the_faces()
        {
            // Flight along +X: 3000 mm run, 250 mm rise (8.33 %), 1200 mm wide; then a 1500 mm landing.
            double s = 250.0 / 3000.0, n = System.Math.Sqrt(1 + s * s);
            var faces = new List<PlanarFaceFact>
            {
                Face(-s / n, 0, 1 / n, new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 250 }, new[] { 3000.0, 1200, 250 }, new[] { 0.0, 1200, 0 }),
                Face(0, 0, 1, new[] { 3000.0, 0, 250 }, new[] { 4500.0, 0, 250 }, new[] { 4500.0, 1200, 250 }, new[] { 3000.0, 1200, 250 }),
                Face(0, 0, -1, new[] { 0.0, 0, -100 }, new[] { 4500.0, 0, -100 }, new[] { 4500.0, 1200, -100 }),     // bottom: ignored
                Face(0, -1, 0, new[] { 0.0, 0, 0 }, new[] { 3000.0, 0, 250 }, new[] { 0.0, 0, -100 })                // side: ignored
            };
            Dictionary<string, MeasuredValue> m = CodeCheckRules.RampMeasures(faces);
            Assert.Equal(8.333, m["ramp_slope_percent"].Value.Value, 2);
            Assert.Equal(3000, m["ramp_run_length_mm"].Value.Value, 1);
            Assert.Equal(1200, m["ramp_width_mm"].Value.Value, 1);
            Assert.Equal(1500, m["ramp_landing_length_mm"].Value.Value, 1);
        }

        [Fact]
        public void A_ramp_with_no_sloped_face_yields_no_measure_and_no_landing_is_not_zero()
        {
            Dictionary<string, MeasuredValue> none = CodeCheckRules.RampMeasures(new List<PlanarFaceFact>());
            Assert.NotNull(none["ramp_slope_percent"].Unavailable);
            double s = 0.05, n = System.Math.Sqrt(1 + s * s);
            Dictionary<string, MeasuredValue> single = CodeCheckRules.RampMeasures(new List<PlanarFaceFact>
            { Face(0, -s / n, 1 / n, new[] { 0.0, 0, 0 }, new[] { 1500.0, 0, 0 }, new[] { 1500.0, 2000, 100 }, new[] { 0.0, 2000, 100 }) });
            Assert.Equal(5.0, single["ramp_slope_percent"].Value.Value, 3);
            Assert.Equal(2000, single["ramp_run_length_mm"].Value.Value, 1);   // gradient along +Y
            Assert.Null(single["ramp_landing_length_mm"].Value);
            Assert.NotNull(single["ramp_landing_length_mm"].Unavailable);
        }
    }
}
