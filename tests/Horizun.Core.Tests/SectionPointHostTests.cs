// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// ONLY A COLUMN IS FRAMED AS A COLUMN (backlog P0 #8, 2026-10-10). A section rule's host
// placed at a point was framed as a column - axis up world Z, the facing as the section's
// up - whatever it was. A section rule {face: bottom, count: 5} on an isolated footing laid
// short vertical bars along a side face; they sat inside the concrete, so the plan accepted
// them and the audit agreed. A point-placed host that is not a structural column is now
// refused by name; footings are reinforced with mat_rules.
// -----------------------------------------------------------------------------
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SectionPointHostTests
    {
        [Fact]
        public void A_structural_column_is_framed()
        {
            Assert.Null(SectionRules.PointHostRefusal("OST_StructuralColumns"));
        }

        [Theory]
        [InlineData("OST_StructuralFoundation")]
        [InlineData("OST_GenericModel")]
        [InlineData("OST_Columns")]
        [InlineData(null)]
        public void Any_other_point_placed_host_is_refused_and_the_refusal_names_the_way_out(string category)
        {
            string why = SectionRules.PointHostRefusal(category);
            Assert.NotNull(why);
            Assert.Contains("not a structural column", why);
            Assert.Contains("mat_rules", why);
        }

        // A PILE (acero 3.0, L3) is a point-placed FOUNDATION that is a vertical member. It is framed
        // as one only when the rule SAYS so - axis: vertical - so P0 #8 stands for every footing that
        // does not say it.
        [Fact]
        public void A_foundation_declared_vertical_is_framed()
        {
            Assert.Null(SectionRules.PointHostRefusal("OST_StructuralFoundation", true));
            Assert.NotNull(SectionRules.PointHostRefusal("OST_StructuralFoundation", false));
        }

        [Fact]
        public void Declaring_vertical_does_not_make_anything_else_a_column() =>
            Assert.NotNull(SectionRules.PointHostRefusal("OST_GenericModel", true));

        [Fact]
        public void The_axis_is_read_and_only_vertical_is_a_word()
        {
            StructuralRequirementSet ok = StructuralRequirementSet.Load(Newtonsoft.Json.Linq.JObject.Parse(Body("'axis': 'vertical', ")));
            Assert.True(ok.Ok, ok.Error);
            Assert.Equal("vertical", ok.SectionRules[0].Axis);
            Assert.Equal(StructuralRequirementSet.CodeUnknownValue,
                StructuralRequirementSet.Load(Newtonsoft.Json.Linq.JObject.Parse(Body("'axis': 'sideways', "))).Code);
        }

        private static string Body(string axis) => @"{
  'schema': 'horizun.structural-requirements/1', 'units': 'millimeter', 'requirement_set': { 'id': 'p', 'version': '1' },
  'bar_types': [ { 'id': 'N16', 'type_name': '16M' } ],
  'section_rules': [ { 'id': 'P1', 'host': { 'element_ids': [ 5 ] }, " + axis + @"'cover': { 'distance_mm': 50 },
    'layers': [ { 'face': 'perimeter', 'count': 4, 'bar_type': 'N16', 'extend': 'host_length' } ] } ] }";

        [Fact]
        public void The_resolver_asks_before_framing_a_point_placed_host()
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            string text = File.ReadAllText(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", "ReinforcementResolver.cs"));
            Assert.Contains("SectionRules.PointHostRefusal(", text);
        }
    }
}
