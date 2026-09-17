// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// ONE SYMBOL, ONE UNIT.
//
// MEASURED (E-300): the historical box of unit 912 reached 230 mm past the demising
// wall into unit 913, so two receptacles drawn on 913's side were counted in both
// units. A footprint drawn on the wall's centreline gives each side its own devices;
// a symbol ON that line belongs to neither until a person assigns it. These cases
// pin that hold, and membership of a symbol placed through a nested, turned and
// reflected block in a concave footprint.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadZoneMembershipTests
    {
        private static CadExtent Zone(string extent)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'z', 'version': '1' },
              'source': { 'units': 'millimeter', 'extent_mm': EXTENT },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r', 'layers': ['E-P'], 'produces': 'electrical_fixture',
                           'family_type': 'Duplex Receptacle: Standard', 'level': 'Level 1',
                           'geometry': { 'from': 'blocks', 'blocks': ['OUT2'] } } ]
            }";
            return CadRequirementSet.Load(JObject.Parse(doc.Replace("EXTENT", extent).Replace('\'', '"'))).ExtentMm;
        }

        // two units sharing a demising wall on y = 1000: A below, B above
        private const string A = "{ 'polygon': [[0,0],[3000,0],[3000,1000],[0,1000]], 'symbols_on_boundary_mm': 25 }";
        private const string B = "{ 'polygon': [[0,1000],[3000,1000],[3000,2000],[0,2000]], 'symbols_on_boundary_mm': 25 }";

        [Fact]
        public void A_symbol_drawn_beside_the_demising_wall_belongs_to_its_side_only()
        {
            CadExtent a = Zone(A), b = Zone(B);
            var south = new CadPoint(1500, 930);    // drawn on A's side of the wall
            var north = new CadPoint(1500, 1070);
            Assert.True(a.ContainsSymbol(south));
            Assert.False(b.ContainsSymbol(south));
            Assert.True(b.ContainsSymbol(north));
            Assert.False(a.ContainsSymbol(north));
        }

        [Fact]
        public void A_symbol_on_the_boundary_belongs_to_neither_until_assigned()
        {
            CadExtent a = Zone(A), b = Zone(B);
            var onLine = new CadPoint(1500, 1010);
            Assert.False(a.ContainsSymbol(onLine));
            Assert.False(b.ContainsSymbol(onLine));
            Assert.True(a.HoldsOnBoundary(onLine));
            Assert.True(b.HoldsOnBoundary(onLine));
            // without the key the edge stays inclusive, as before
            CadExtent open = Zone("{ 'polygon': [[0,0],[3000,0],[3000,1000],[0,1000]] }");
            Assert.True(open.ContainsSymbol(new CadPoint(1500, 1000)));
            Assert.Throws<CadRequirementSetException>(() => Zone("{ 'min_x': 0, 'min_y': 0, 'max_x': 1, 'max_y': 1, 'symbols_on_boundary_mm': -1 }"));
        }

        [Fact]
        public void A_nested_turned_and_reflected_symbol_is_judged_where_it_lands()
        {
            // an L-shaped unit: the stair core takes x < 1000, y < 1000
            CadExtent l = Zone("{ 'polygon': [[0,3000],[3000,3000],[3000,0],[1000,0],[1000,1000],[0,1000]] }");

            // a KITCHEN block inserted at (2000, 500), turned 90 degrees and reflected, holding
            // an OUT2 at its local (800, -300)
            var kitchen = new CadIrEntity
            {
                Id = "k", Handle = "K1", Kind = CadEntityKind.BlockInstance, BlockName = "KITCHEN", Layer = "0",
                Space = "model", RotationRadians = Math.PI / 2, ScaleX = -1, ScaleY = 1
            };
            kitchen.Points.Add(new CadPoint(2000, 500));
            var outlet = new CadIrEntity
            {
                Id = "o", Handle = "O1", Kind = CadEntityKind.BlockInstance, BlockName = "OUT2", Layer = "E-P",
                RotationRadians = 0, ScaleX = 1, ScaleY = 1
            };
            outlet.Points.Add(new CadPoint(800, -300));
            outlet.BlockPath.Add("KITCHEN");

            CadPlacementReading placed = CadBlockPlacement.Place(new List<CadIrEntity> { kitchen, outlet });
            CadPlacedBlock nested = placed.Placed.Single(p => p.Source == outlet);
            // local (800,-300): reflected -> (-800,-300); turned 90 -> (300,-800); moved -> (2300,-300)
            Assert.Equal(2300, nested.At.X, 6);
            Assert.Equal(-300, nested.At.Y, 6);
            Assert.True(nested.Mirrored);
            Assert.False(l.ContainsSymbol(nested.At));          // below the facade: outside

            // the same outlet at local (-400, 1500) lands in the leg of the L
            outlet.Points[0] = new CadPoint(-400, 1500);
            nested = CadBlockPlacement.Place(new List<CadIrEntity> { kitchen, outlet }).Placed.Single(p => p.Source == outlet);
            // reflected -> (400,1500); turned -> (-1500,400); moved -> (500,900)
            Assert.Equal(500, nested.At.X, 6);
            Assert.Equal(900, nested.At.Y, 6);
            Assert.False(l.ContainsSymbol(nested.At));          // inside the envelope, in the core notch
            outlet.Points[0] = new CadPoint(-400, -500);
            nested = CadBlockPlacement.Place(new List<CadIrEntity> { kitchen, outlet }).Placed.Single(p => p.Source == outlet);
            // reflected -> (400,-500); turned -> (500,400); moved -> (2500,900)
            Assert.True(l.ContainsSymbol(nested.At));
        }
    }
}
