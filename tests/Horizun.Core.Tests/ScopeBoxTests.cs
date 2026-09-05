// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Scope boxes, proved by running the rules. The distinction the whole area
// turns on is that "no scope box" is three different situations - a decision,
// a failed read, and a box that is assigned but will not report its extents -
// and only one of them is somebody choosing not to use one.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScopeBoxTests
    {
        private static ScopeBoxAssignment A(string name, bool readable = true, bool geometryMissing = false)
        {
            return new ScopeBoxAssignment
            {
                OwnerId = 1,
                OwnerKind = "grid",
                ScopeBoxName = name,
                ScopeBoxId = name == null ? (long?)null : 99,
                Readable = readable,
                GeometryMissing = geometryMissing
            };
        }

        private static ScopeBoxFact Box(string name, bool geometry = true)
        {
            return new ScopeBoxFact
            {
                ElementId = 99,
                Name = name,
                GeometryReadable = geometry,
                MinXMm = geometry ? 0 : (double?)null,
                MaxXMm = geometry ? 1000 : (double?)null,
                MinYMm = geometry ? 0 : (double?)null,
                MaxYMm = geometry ? 2000 : (double?)null,
                MinZMm = geometry ? 0 : (double?)null,
                MaxZMm = geometry ? 3000 : (double?)null
            };
        }

        // ------------------------------------------------------ the four states

        [Fact]
        public void A_datum_with_no_scope_box_is_not_assigned_rather_than_unreadable()
        {
            Assert.Equal(ScopeBoxState.NotAssigned, A(null).State);
            Assert.Equal(ScopeBoxState.NotAssigned, A("   ").State);
        }

        [Fact]
        public void A_failed_read_is_unreadable_and_never_not_assigned()
        {
            // "Not assigned" is a decision somebody made. A read that threw is not
            // evidence of any decision at all.
            Assert.Equal(ScopeBoxState.Unreadable, A(null, readable: false).State);
        }

        [Fact]
        public void An_assigned_box_whose_extents_will_not_come_back_keeps_its_assignment()
        {
            // THE ONE THAT MATTERS. The datum IS scoped, and saying "not assigned"
            // because the geometry failed would report a decision nobody made.
            ScopeBoxAssignment a = A("Tower A", geometryMissing: true);
            Assert.Equal(ScopeBoxState.GeometryAbsent, a.State);
            Assert.Equal("Tower A", ScopeBoxRules.ToJson(a).Value<string>("scope_box"));
        }

        [Fact]
        public void An_ordinary_assignment_reports_assigned_and_names_the_box()
        {
            Assert.Equal(ScopeBoxState.Assigned, A("Tower A").State);
        }

        [Fact]
        public void Every_state_appears_in_the_tally()
        {
            JObject t = ScopeBoxRules.Tally(new ScopeBoxAssignment[0], new ScopeBoxFact[0]);
            foreach (string s in ScopeBoxState.All) Assert.NotNull(t[s]);
            Assert.True(t.Value<bool>("counts_are_exact"));
        }

        [Fact]
        public void One_unreadable_assignment_makes_the_counts_inexact()
        {
            JObject t = ScopeBoxRules.Tally(new[] { A("A"), A(null, readable: false) }, null);
            Assert.Equal(1, t.Value<int>(ScopeBoxState.Unreadable));
            Assert.False(t.Value<bool>("counts_are_exact"));
        }

        // -------------------------------------------------------- the geometry

        [Fact]
        public void The_extents_are_the_scope_boxs_own_and_the_reply_says_so()
        {
            // The mandate's explicit warning: substituting the bounding box of the
            // elements a scope box crops would be a guess shaped like a measurement.
            JObject j = ScopeBoxRules.ToJson(Box("Tower A"));
            Assert.Equal(1000.0, j.Value<double>("width_mm"));
            Assert.Equal(2000.0, j.Value<double>("depth_mm"));
            Assert.Equal(3000.0, j.Value<double>("height_mm"));

            Assert.Contains("scope box's OWN bounding box", ScopeBoxRules.GeometryMeans);
            Assert.Contains("not derived from the elements it contains", ScopeBoxRules.GeometryMeans);
        }

        [Fact]
        public void A_box_with_no_readable_geometry_reports_null_spans_and_not_zero()
        {
            // Zero width is a measurement. Absence of one is not.
            JObject j = ScopeBoxRules.ToJson(Box("Tower A", geometry: false));
            Assert.Null(j["width_mm"].Value<double?>());
            Assert.Null(j["min_x_mm"].Value<double?>());
            Assert.False(j.Value<bool>("geometry_readable"));

            JObject t = ScopeBoxRules.Tally(null, new[] { Box("A", geometry: false) });
            Assert.Equal(1, t.Value<int>("scope_boxes_without_geometry"));
        }

        // ------------------------------------------------------------- sharing

        [Fact]
        public void Datums_sharing_a_box_are_grouped_largest_first()
        {
            List<KeyValuePair<string, long>> rows = ScopeBoxRules.ByScopeBox(new[]
            {
                A("Tower A"), A("Tower A"), A("Tower B")
            });
            Assert.Equal("Tower A", rows[0].Key);
            Assert.Equal(2, rows[0].Value);
            Assert.Equal("Tower B", rows[1].Key);
        }

        [Fact]
        public void Unassigned_and_unreadable_owners_are_not_grouped_under_a_box()
        {
            // Grouping them would invent a scope box named after nothing.
            Assert.Empty(ScopeBoxRules.ByScopeBox(new[] { A(null), A(null, readable: false) }));
        }

        [Fact]
        public void An_assigned_box_with_absent_geometry_still_counts_toward_sharing()
        {
            // The assignment is what makes two datums share a box; the geometry is
            // a separate question about the box itself.
            Assert.Single(ScopeBoxRules.ByScopeBox(new[] { A("Tower A", geometryMissing: true) }));
        }
    }
}
