// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Groups and design options, proved by running the rules. Two confusions, one
// per area:
//
//   a group type with no instances is not an empty group
//   a document with no design options has not passed a design-option check
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GroupOptionTests
    {
        private static GroupTypeFact T(string name, int instances, int? members)
        {
            return new GroupTypeFact
            {
                ElementId = 1,
                Name = name,
                InstanceCount = instances,
                MemberCount = members,
                MembersReadable = members.HasValue
            };
        }

        // ------------------------------------------------------------ groups

        [Fact]
        public void A_type_with_no_instances_is_unplaced_and_not_empty()
        {
            // THE ONE THAT MATTERS. It carries its full geometry in the file and
            // nothing draws it - a purge candidate, not a definition holding nothing.
            GroupTypeFact f = T("Bathroom Pod", instances: 0, members: 14);
            Assert.True(f.Unplaced);
            Assert.False(f.Empty);

            JObject j = GroupOptionRules.ToJson(f);
            Assert.True(j.Value<bool>("unplaced"));
            Assert.False(j.Value<bool>("empty"));
        }

        [Fact]
        public void A_type_holding_no_members_is_empty_and_may_still_be_placed()
        {
            GroupTypeFact f = T("Hollow", instances: 3, members: 0);
            Assert.False(f.Unplaced);
            Assert.True(f.Empty);
        }

        [Fact]
        public void The_two_counts_are_reported_separately_and_never_merged()
        {
            // The two counts must DIFFER here, or code computing one from the other
            // passes by coincidence. a is unplaced only; b and d are empty only.
            JObject t = GroupOptionRules.GroupTotals(
                new[] { T("a", 0, 5), T("b", 2, 0), T("d", 4, 0) },
                new GroupInstanceFact[0]);

            Assert.Equal(1, t.Value<int>("types_with_no_instances"));   // a
            Assert.Equal(2, t.Value<int>("types_with_no_members"));     // b and d
            Assert.Contains("NOT an empty group", GroupOptionRules.GroupsMean);
        }

        [Fact]
        public void A_type_whose_members_could_not_be_read_is_neither_empty_nor_full()
        {
            GroupTypeFact f = T("Unreadable", instances: 1, members: null);
            Assert.Null(f.Empty);

            JObject t = GroupOptionRules.GroupTotals(new[] { f }, new GroupInstanceFact[0]);
            Assert.Equal(0, t.Value<int>("types_with_no_members"));
            Assert.Equal(1, t.Value<int>("types_whose_members_are_unreadable"));
        }

        [Fact]
        public void Nested_instances_are_counted_and_unknown_nesting_is_not_counted_as_flat()
        {
            var flat = new GroupInstanceFact { ElementId = 1, IsNested = false };
            var nested = new GroupInstanceFact { ElementId = 2, IsNested = true };
            var unknown = new GroupInstanceFact { ElementId = 3, IsNested = null };

            JObject t = GroupOptionRules.GroupTotals(new GroupTypeFact[0], new[] { flat, nested, unknown });
            Assert.Equal(1, t.Value<int>("nested_instances"));
            Assert.Equal(1, t.Value<int>("nesting_unreadable"));
        }

        [Fact]
        public void Member_categories_are_ranked_largest_first_and_stably()
        {
            GroupTypeFact f = T("g", 1, 3);
            f.MemberCategories["Zebra"] = 2;
            f.MemberCategories["Alpha"] = 2;
            f.MemberCategories["Walls"] = 9;

            JArray cats = (JArray)GroupOptionRules.ToJson(f)["dominant_categories"];
            Assert.Equal("Walls", cats[0].Value<string>("category"));
            Assert.Equal("Alpha", cats[1].Value<string>("category"));
            Assert.Equal("Zebra", cats[2].Value<string>("category"));
        }

        [Fact]
        public void An_empty_model_reports_zeros_without_throwing()
        {
            JObject t = GroupOptionRules.GroupTotals(null, null);
            Assert.Equal(0, t.Value<int>("group_types"));
            Assert.Equal(0, t.Value<int>("group_instances"));
        }

        // ---------------------------------------------------- design options

        [Fact]
        public void A_document_with_no_design_options_is_not_applicable_rather_than_clean()
        {
            // Reporting a pass tells a team their options are tidy in a file that
            // never had any.
            JObject j = GroupOptionRules.NoDesignOptions();
            Assert.Equal("not_applicable", j.Value<string>("status"));
            Assert.Contains("no design option sets", j.Value<string>("reason"));
            Assert.Contains("has not PASSED", GroupOptionRules.OptionsMean);

            // And it carries no counts at all - a zero here would be a check that ran.
            Assert.Null(j["options"]);
            Assert.Null(j["element_count"]);
        }

        [Fact]
        public void An_option_reports_its_set_its_primacy_and_its_element_count()
        {
            var f = new DesignOptionFact
            {
                ElementId = 5,
                Name = "Scheme B",
                SetName = "Facade",
                IsPrimary = false,
                ElementCount = 42
            };
            JObject j = GroupOptionRules.ToJson(f);
            Assert.Equal("Facade", j.Value<string>("option_set"));
            Assert.False(j.Value<bool>("is_primary"));
            Assert.Equal(42, j.Value<long>("element_count"));
        }

        [Fact]
        public void An_option_whose_primacy_could_not_be_read_is_null_and_not_secondary()
        {
            // False would say Revit told us it is not primary. It did not.
            var f = new DesignOptionFact { ElementId = 5, Name = "X", IsPrimary = null };
            Assert.Null(GroupOptionRules.ToJson(f)["is_primary"].Value<bool?>());
        }

        [Fact]
        public void An_option_with_no_elements_is_reported_as_such()
        {
            // A real zero: the option exists and holds nothing.
            var f = new DesignOptionFact { ElementId = 5, Name = "Empty", ElementCount = 0 };
            Assert.Equal(0, GroupOptionRules.ToJson(f).Value<long>("element_count"));
        }
    }
}
