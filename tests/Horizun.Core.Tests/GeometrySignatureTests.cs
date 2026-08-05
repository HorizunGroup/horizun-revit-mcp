// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// family_apply had a field called geometry_invariant that could say
// "proven_unchanged" while comparing the COUNT of Double parameters and whether
// a parameter named IsCustom existed. Every case below is a geometry change that
// check passes: a Double whose VALUE moved, one Double swapped for another, a
// formula driving a dimension with no parameter change at all.
//
// The rule under test is the one that replaces it - and the harder half of it is
// that a dimension nobody could measure must never be counted as one that did not
// change.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GeometrySignatureTests
    {
        private static GeometrySignature Sig(string name, double volume, double area = 10, int solids = 1,
                                             List<string> connectors = null)
        {
            var s = new GeometrySignature { TypeName = name, Connectors = connectors ?? new List<string>() };
            s.Add(GeoDimension.Of("solid_volume", volume));
            s.Add(GeoDimension.Of("surface_area", area));
            s.Add(GeoDimension.Of("solid_count", solids));
            return s;
        }

        [Fact]
        public void An_identical_shape_is_unchanged_and_fully_verified()
        {
            var v = GeometryCompare.Compare(new[] { Sig("Type 1", 5.0) }, new[] { Sig("Type 1", 5.0) });

            Assert.Equal("unchanged", v.Status);
            Assert.True(v.FullyVerified);
            Assert.False(v.AnyChange);
            Assert.Contains("none changed", v.Summary());
        }

        [Fact]
        public void A_moved_dimension_is_caught_even_though_the_parameter_count_did_not_change()
        {
            // THE CASE THE OLD CHECK PASSED: a Double's value moved. Same count, same
            // IsCustom, different extrusion.
            var v = GeometryCompare.Compare(new[] { Sig("Type 1", 5.0) }, new[] { Sig("Type 1", 7.5) });

            Assert.Equal("changed", v.Status);
            Assert.Single(v.Changed);
            Assert.Contains("solid_volume", v.Changed[0].Dimension);
            Assert.Contains("CHANGED", v.Summary());
        }

        [Fact]
        public void A_shape_change_that_preserves_volume_is_still_caught()
        {
            // Same volume, different surface area: the form was reshaped.
            var v = GeometryCompare.Compare(new[] { Sig("T", 5.0, area: 10) }, new[] { Sig("T", 5.0, area: 14) });

            Assert.Equal("changed", v.Status);
            Assert.Contains(v.Changed, c => c.Dimension.EndsWith("surface_area"));
        }

        [Fact]
        public void A_form_split_in_two_keeps_volume_and_area_and_is_still_caught()
        {
            var v = GeometryCompare.Compare(new[] { Sig("T", 5.0, 10, solids: 1) },
                                            new[] { Sig("T", 5.0, 10, solids: 2) });

            Assert.Equal("changed", v.Status);
            Assert.Contains(v.Changed, c => c.Dimension.EndsWith("solid_count"));
        }

        [Fact]
        public void Floating_point_noise_from_a_regenerate_is_not_a_change()
        {
            var v = GeometryCompare.Compare(new[] { Sig("T", 5.0) }, new[] { Sig("T", 5.0 + 1e-12) });

            Assert.Equal("unchanged", v.Status);
        }

        [Fact]
        public void A_real_move_just_above_the_tolerance_is_a_change()
        {
            var v = GeometryCompare.Compare(new[] { Sig("T", 1.0) }, new[] { Sig("T", 1.0 + 1e-4) });

            Assert.Equal("changed", v.Status);
        }

        [Fact]
        public void A_dimension_that_could_not_be_measured_is_never_counted_as_unchanged()
        {
            // THE HARDER HALF. Nothing detectably moved, but one dimension was never read,
            // so this must NOT say "unchanged".
            var before = Sig("T", 5.0);
            var after = new GeometrySignature { TypeName = "T", Connectors = new List<string>() };
            after.Add(GeoDimension.Of("solid_volume", 5.0));
            after.Add(GeoDimension.Of("surface_area", 10));
            after.Add(GeoDimension.Unmeasured("solid_count", "the geometry read threw"));

            var v = GeometryCompare.Compare(new[] { before }, new[] { after });

            Assert.Equal("unchanged_where_measured", v.Status);
            Assert.False(v.FullyVerified);
            Assert.False(v.AnyChange);
            Assert.Contains("NOT proven unchanged", v.Summary());
            Assert.Contains(v.NotVerified, s => s.Contains("solid_count"));
        }

        [Fact]
        public void A_type_that_disappeared_is_a_change()
        {
            var v = GeometryCompare.Compare(new[] { Sig("A", 1), Sig("B", 2) }, new[] { Sig("A", 1) });

            Assert.Equal("changed", v.Status);
            Assert.Contains("B", v.TypesRemoved);
            Assert.Contains("disappeared", v.Summary());
        }

        [Fact]
        public void A_rename_is_reported_as_one_gone_and_one_arrived_rather_than_guessed()
        {
            var v = GeometryCompare.Compare(new[] { Sig("Old", 1) }, new[] { Sig("New", 1) });

            Assert.Equal("changed", v.Status);
            Assert.Contains("Old", v.TypesRemoved);
            Assert.Contains("New", v.TypesAdded);
        }

        // ---- A rename the CALLER declared is paired, not read as a disappearance. -------------
        // Regression: family_apply's own `family_name` renames the surviving type, and matching
        // by name alone made the command trip its own guard on the work it was told to do -
        // "changed" with ZERO dimensions compared, whole transaction rolled back. Measured live
        // on a Prodesa family, add-in 0.5.0 and again on 0.6.1.

        private static Dictionary<string, string> Renamed(string from, string to) =>
            new Dictionary<string, string>(StringComparer.Ordinal) { [from] = to };

        [Fact]
        public void A_declared_rename_is_paired_and_the_shape_is_actually_compared()
        {
            var v = GeometryCompare.Compare(new[] { Sig("Old", 1) }, new[] { Sig("New", 1) },
                                            Renamed("Old", "New"));

            Assert.Equal("unchanged", v.Status);
            Assert.False(v.AnyChange);
            Assert.Empty(v.TypesRemoved);
            Assert.Empty(v.TypesAdded);
            Assert.Contains("Old -> New", v.TypesRenamed);
            // The point of the fix: dimensions were COMPARED, not skipped.
            Assert.True(v.Unchanged > 0);
        }

        [Fact]
        public void A_declared_rename_still_catches_a_shape_that_moved_underneath_it()
        {
            var v = GeometryCompare.Compare(new[] { Sig("Old", 1) }, new[] { Sig("New", 99) },
                                            Renamed("Old", "New"));

            Assert.Equal("changed", v.Status);
            Assert.Contains("Old -> New", v.TypesRenamed);
            Assert.NotEmpty(v.Changed);
        }

        [Fact]
        public void A_deletion_cannot_hide_behind_a_declared_rename()
        {
            // "Old" was renamed to "New", and "Other" quietly vanished. The rename is paired;
            // the deletion is still reported. This is the fear the old comment named.
            var v = GeometryCompare.Compare(new[] { Sig("Old", 1), Sig("Other", 2) },
                                            new[] { Sig("New", 1) },
                                            Renamed("Old", "New"));

            Assert.Equal("changed", v.Status);
            Assert.Contains("Other", v.TypesRemoved);
            Assert.Contains("Old -> New", v.TypesRenamed);
        }

        [Fact]
        public void An_undeclared_rename_keeps_the_old_conservative_behaviour()
        {
            // A rename map that does not mention this type changes nothing: still removed+added.
            var v = GeometryCompare.Compare(new[] { Sig("Old", 1) }, new[] { Sig("New", 1) },
                                            Renamed("Something", "Else"));

            Assert.Equal("changed", v.Status);
            Assert.Contains("Old", v.TypesRemoved);
            Assert.Contains("New", v.TypesAdded);
            Assert.Empty(v.TypesRenamed);
        }

        [Fact]
        public void A_rename_is_not_by_itself_a_geometry_change()
        {
            var v = GeometryCompare.Compare(new[] { Sig("Old", 1) }, new[] { Sig("New", 1) },
                                            Renamed("Old", "New"));

            Assert.DoesNotContain("CHANGED", v.Summary());
            Assert.Single(v.TypesRenamed);
        }

        [Fact]
        public void A_connector_that_moved_is_caught()
        {
            var before = Sig("T", 1, connectors: new List<string> { "p=0,0,0 d=0,0,1" });
            var after = Sig("T", 1, connectors: new List<string> { "p=0,0,5 d=0,0,1" });

            var v = GeometryCompare.Compare(new[] { before }, new[] { after });

            Assert.Equal("changed", v.Status);
            Assert.Contains(v.Changed, c => c.Dimension.EndsWith("connectors"));
        }

        [Fact]
        public void A_lost_connector_is_caught_by_count()
        {
            var before = Sig("T", 1, connectors: new List<string> { "a", "b" });
            var after = Sig("T", 1, connectors: new List<string> { "a" });

            var v = GeometryCompare.Compare(new[] { before }, new[] { after });

            Assert.Contains(v.Changed, c => c.Dimension.EndsWith("connector_count"));
        }

        [Fact]
        public void Connector_enumeration_order_is_not_a_change()
        {
            var before = Sig("T", 1, connectors: new List<string> { "a", "b" });
            var after = Sig("T", 1, connectors: new List<string> { "b", "a" });

            Assert.Equal("unchanged", GeometryCompare.Compare(new[] { before }, new[] { after }).Status);
        }

        [Fact]
        public void Connectors_that_could_not_be_read_are_not_verified()
        {
            var before = Sig("T", 1, connectors: null);
            var after = Sig("T", 1, connectors: new List<string>());
            before.Connectors = null;

            var v = GeometryCompare.Compare(new[] { before }, new[] { after });

            Assert.Equal("unchanged_where_measured", v.Status);
            Assert.Contains(v.NotVerified, s => s.Contains("connectors"));
        }

        [Fact]
        public void Comparing_nothing_against_nothing_is_not_a_proof()
        {
            // An empty family on both sides: no dimensions compared. This must not read as
            // a clean "unchanged" verdict with authority behind it.
            var v = GeometryCompare.Compare(new List<GeometrySignature>(), new List<GeometrySignature>());

            Assert.Equal(0, v.Unchanged);
            Assert.False(v.AnyChange);
            Assert.Equal("unchanged", v.Status);   // nothing was found to differ...
            Assert.Contains("0 comparison(s)", v.Summary());   // ...and the summary says how much was checked
        }
    }
}
