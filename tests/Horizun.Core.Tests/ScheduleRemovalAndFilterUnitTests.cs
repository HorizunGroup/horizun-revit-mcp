// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// remove_fields resolves every position before the first removal, and a filter's
// number_value is read in a declared unit (audit 2026-10-08, 6.7).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleRemovalAndFilterUnitTests
    {
        private static List<string> Fields() => new List<string> { "A", "B", "C", "D" };

        [Fact]
        public void Removing_positions_1_and_2_of_ABCD_leaves_A_and_D()
        {
            var fields = Fields();
            var entries = new[] { new JObject { ["field_index"] = 1 }, new JObject { ["field_index"] = 2 } };
            List<string> removed = ScheduleEditRules.ResolveThenRemove(entries,
                e => fields[e.Value<int>("field_index")], id => fields.Remove(id));
            Assert.Equal(new[] { "B", "C" }, removed);
            Assert.Equal(new[] { "A", "D" }, fields);
        }

        [Fact]
        public void Interleaving_resolution_and_removal_is_the_defect_this_replaces()
        {
            var fields = Fields();
            foreach (int i in new[] { 1, 2 }) fields.Remove(fields[i]);
            Assert.Equal(new[] { "A", "C" }, fields);   // B then D: the wrong two
        }

        [Fact]
        public void Two_entries_naming_one_field_remove_nothing()
        {
            var fields = Fields();
            var entries = new[] { new JObject { ["field_index"] = 1 }, new JObject { ["name"] = "B" } };
            Assert.Throws<InvalidOperationException>(() => ScheduleEditRules.ResolveThenRemove(entries,
                e => e["name"] != null ? e.Value<string>("name") : fields[e.Value<int>("field_index")], id => fields.Remove(id)));
            Assert.Equal(4, fields.Count);
        }

        [Fact]
        public void A_filter_number_is_read_in_the_unit_its_field_measures()
        {
            string why;
            Assert.Equal("millimeters", ScheduleEditRules.FilterNumberUnit(true, true, false, false, "length", out why));
            Assert.Equal("square_meters", ScheduleEditRules.FilterNumberUnit(true, false, true, false, "area", out why));
            Assert.Equal("cubic_meters", ScheduleEditRules.FilterNumberUnit(true, false, false, true, "volume", out why));
            Assert.Null(ScheduleEditRules.FilterNumberUnit(false, false, false, false, "number", out why));
            Assert.Null(why);
            Assert.Null(ScheduleEditRules.FilterNumberUnit(true, false, false, false, "airFlow", out why));
            Assert.Contains("airFlow", why);
        }
    }
}
