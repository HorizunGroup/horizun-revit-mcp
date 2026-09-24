// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// The precedence report of horizun_manage_views explain_graphics. What must hold:
// element beats filter, a higher filter beats a lower one, a disabled or
// non-matching filter contributes nothing, the category fills what nobody else
// set, object style is named when nobody set anything, and ANY layer that hides
// wins visibility - a visible filter never brings back a hidden category.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GraphicsPrecedenceTests
    {
        private static GraphicsLayer L(string label, bool applies = true, bool? visible = null, params string[] kv)
        {
            var fields = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) fields[kv[i]] = kv[i + 1];
            return new GraphicsLayer { Label = label, Source = label.Split(':')[0], Applies = applies, Visible = visible, Fields = fields };
        }

        private static string From(JObject winners, string property) => (string)winners[property]["from"];

        [Fact]
        public void Element_beats_filters_and_filters_beat_category()
        {
            JObject w = GraphicsPrecedence.Resolve(new List<GraphicsLayer>
            {
                L("element", kv: new[] { "line_color", "#FF0000" }),
                L("filter:1", kv: new[] { "line_color", "#00FF00", "surface_color", "#0000FF" }),
                L("category:-2000011", kv: new[] { "surface_color", "#111111", "halftone", "true" })
            });
            Assert.Equal("element", From(w, "line_color"));
            Assert.Equal("#FF0000", (string)w["line_color"]["value"]);
            Assert.Equal("filter:1", From(w, "surface_color"));
            Assert.Equal("category:-2000011", From(w, "halftone"));
        }

        [Fact]
        public void The_higher_filter_wins_and_a_disabled_or_unmatched_one_is_skipped()
        {
            JObject w = GraphicsPrecedence.Resolve(new List<GraphicsLayer>
            {
                L("element"),
                L("filter:1", applies: false, kv: new[] { "line_color", "#AAAAAA" }),
                L("filter:2", kv: new[] { "line_color", "#BBBBBB" }),
                L("filter:3", kv: new[] { "line_color", "#CCCCCC" })
            });
            Assert.Equal("filter:2", From(w, "line_color"));
        }

        [Fact]
        public void Nothing_set_is_object_style_never_a_guess()
        {
            JObject w = GraphicsPrecedence.Resolve(new List<GraphicsLayer> { L("element"), L("category:1") });
            foreach (string property in GraphicsPrecedence.Properties)
            {
                Assert.Equal(GraphicsPrecedence.ObjectStyle, From(w, property));
                Assert.Equal(JTokenType.Null, w[property]["value"].Type);
            }
            Assert.True((bool)w["visible"]["value"]);
        }

        [Fact]
        public void Any_hiding_layer_wins_and_a_visible_filter_does_not_unhide_the_category()
        {
            JObject w = GraphicsPrecedence.Resolve(new List<GraphicsLayer>
            {
                L("element"),
                L("filter:1", visible: null),
                L("category:5", visible: false)
            });
            Assert.False((bool)w["visible"]["value"]);
            Assert.Equal("category:5", (string)w["visible"]["decided_by"]);
        }

        [Fact]
        public void A_hiding_filter_that_does_not_apply_hides_nothing()
        {
            JObject w = GraphicsPrecedence.Resolve(new List<GraphicsLayer>
            {
                L("element"),
                L("filter:9", applies: false, visible: false)
            });
            Assert.True((bool)w["visible"]["value"]);
        }
    }
}
