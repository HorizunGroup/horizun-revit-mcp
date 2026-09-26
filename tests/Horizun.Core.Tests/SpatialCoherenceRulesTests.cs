using Horizun.Revit.Core;
using Xunit;
using K = Horizun.Revit.Core.SpatialCoherenceRules.Kind;

namespace Horizun.Core.Tests
{
    public class SpatialCoherenceRulesTests
    {
        private static SpatialCoherenceRules.Verdict V(string a, string b, double? shared = 0.5, bool sameType = false,
            bool host = false, bool joined = false, bool connected = false, bool assembly = false, double? va = 2, double? vb = 3) =>
            SpatialCoherenceRules.Classify(new SpatialCoherenceRules.Pair
            {
                CategoryA = a, CategoryB = b, SharedVolume = shared, SameType = sameType, HostRelation = host,
                Joined = joined, Connected = connected, SameAssembly = assembly, VolumeA = va, VolumeB = vb
            });

        [Fact]
        public void A_door_and_a_column_in_the_same_place_is_an_error_measured_in_the_field()
        {
            var v = V("OST_Doors", "OST_StructuralColumns");
            Assert.Equal(K.Conflict, v.Kind);
            Assert.Equal("error", v.Severity);
            Assert.Contains("door", v.Reason);
            Assert.Equal(K.Conflict, V("OST_Columns", "OST_Windows").Kind);
        }

        [Fact]
        public void A_door_in_its_own_host_wall_is_expected_but_in_another_wall_is_not()
        {
            Assert.Equal(K.Expected, V("OST_Doors", "OST_Walls", host: true).Kind);
            Assert.Equal("error", V("OST_Doors", "OST_Walls").Severity);
            // Joining does not excuse an opening: a joined column still blocks the door.
            Assert.Equal("error", V("OST_Doors", "OST_StructuralColumns", joined: true).Severity);
        }

        [Fact]
        public void Touching_faces_are_not_findings_and_an_unmeasured_intersection_is_never_dropped()
        {
            Assert.Equal(K.None, V("OST_Doors", "OST_StructuralColumns", shared: 1e-5).Kind);
            Assert.Equal(K.Conflict, V("OST_Doors", "OST_StructuralColumns", shared: null).Kind);
            Assert.Equal("warning", V("OST_Doors", "OST_StructuralColumns", shared: null).Severity);
        }

        [Fact]
        public void Two_elements_of_the_same_type_in_the_same_space_are_a_duplicate()
        {
            var v = V("OST_Walls", "OST_Walls", shared: 1.98, sameType: true, va: 2, vb: 2);
            Assert.Equal(K.Duplicate, v.Kind);
            Assert.Equal("error", v.Severity);
            // Same type, partial overlap: an overlap warning, not a duplicate.
            Assert.Equal(K.Overlap, V("OST_Walls", "OST_Walls", shared: 0.5, sameType: true, va: 2, vb: 2).Kind);
            // Joined walls meet by design.
            Assert.Equal(K.Expected, V("OST_Walls", "OST_Walls", joined: true).Kind);
        }

        [Fact]
        public void The_structural_frame_and_walls_on_slabs_meet_by_construction()
        {
            Assert.Equal(K.Expected, V("OST_StructuralFraming", "OST_StructuralColumns").Kind);
            Assert.Equal(K.Expected, V("OST_Floors", "OST_Walls").Kind);
            Assert.Equal(K.Expected, V("OST_StructuralColumns", "OST_Floors").Kind);
        }

        [Fact]
        public void Mep_through_structure_is_an_error_through_an_enclosure_is_expected_and_connected_is_fine()
        {
            Assert.Equal("error", V("OST_DuctCurves", "OST_StructuralFraming").Severity);
            Assert.Equal(K.Expected, V("OST_PipeCurves", "OST_Walls").Kind);
            Assert.Equal(K.Expected, V("OST_PipeCurves", "OST_PipeFitting", connected: true).Kind);
            Assert.Equal("warning", V("OST_PipeCurves", "OST_DuctCurves").Severity);
        }

        [Fact]
        public void Furniture_in_a_column_is_a_warning_and_rebar_is_never_judged()
        {
            Assert.Equal("warning", V("OST_Furniture", "OST_StructuralColumns").Severity);
            Assert.Equal(K.None, V("OST_Rebar", "OST_StructuralColumns").Kind);
            Assert.Equal(K.None, V("OST_Rooms", "OST_Walls").Kind);
        }

        [Fact]
        public void Duplicate_needs_both_volumes_close_and_nearly_fully_shared()
        {
            Assert.True(SpatialCoherenceRules.IsDuplicate(0.99, 1.0, 1.0));
            Assert.False(SpatialCoherenceRules.IsDuplicate(0.99, 1.0, 2.0));
            Assert.False(SpatialCoherenceRules.IsDuplicate(0.5, 1.0, 1.0));
            Assert.False(SpatialCoherenceRules.IsDuplicate(1, null, 1.0));
        }

        [Fact]
        public void A_column_in_front_of_a_door_blocks_it_and_furniture_is_a_warning()
        {
            Assert.Equal("error", SpatialCoherenceRules.Clearance("OST_StructuralColumns", false).Severity);
            Assert.Equal("error", SpatialCoherenceRules.Clearance("OST_Walls", false).Severity);
            Assert.Equal("warning", SpatialCoherenceRules.Clearance("OST_Furniture", false).Severity);
            // The door's own host wall, the slab it stands on and a beam overhead are not obstacles.
            Assert.Equal(K.None, SpatialCoherenceRules.Clearance("OST_Walls", true).Kind);
            Assert.Equal(K.None, SpatialCoherenceRules.Clearance("OST_Floors", false).Kind);
            Assert.Equal(K.None, SpatialCoherenceRules.Clearance("OST_StructuralFraming", false).Kind);
            Assert.Equal(K.None, SpatialCoherenceRules.Clearance("OST_Doors", false).Kind);
        }

        [Fact]
        public void A_door_frame_in_its_floor_or_beside_wall_is_contact_calibrated_on_a_real_model()
        {
            const double L = 1 / 28.316846592;   // one litre in ft3
            // Measured on a real architecture model: 0.12 L (door/wall), 0.21 L (door/floor), 1.1 L max.
            Assert.Equal(K.Expected, V("OST_Doors", "OST_Walls", shared: 0.12 * L).Kind);
            Assert.Equal(K.Expected, V("OST_Doors", "OST_Floors", shared: 0.21 * L).Kind);
            Assert.Equal(K.Expected, V("OST_Doors", "OST_Walls", shared: 1.1 * L).Kind);
            // Measured in Revit 2026: a column in a doorway shares 51 L.
            Assert.Equal("error", V("OST_Doors", "OST_Columns", shared: 51 * L).Severity);
            Assert.Equal("error", V("OST_Windows", "OST_Walls", shared: 4 * L).Severity);
        }

        [Fact]
        public void Labels_read_like_words()
        {
            Assert.Equal("structural column", SpatialCoherenceRules.Label("OST_StructuralColumns"));
            Assert.Equal("mechanical equipment", SpatialCoherenceRules.Label("OST_MechanicalEquipment"));
        }
    }
}
