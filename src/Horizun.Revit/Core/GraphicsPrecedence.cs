// -----------------------------------------------------------------------------
// Horizun Revit MCP - which graphic override WINS for one element in one view.
// Original Horizun code. Pure: no Revit types, so the rule is tested without Revit.
//
// Field use (33 scripts on filters and overrides) kept asking the same question
// with print statements: "I set the colour on the filter, why is the wall still
// red?" Revit answers it with a fixed order that nobody sees in the UI:
//
//   1. an override on the ELEMENT in this view;
//   2. the view's FILTERS, top of the list first - a filter higher in the list
//      beats a lower one, property by property, and a disabled filter or one the
//      element does not pass contributes nothing;
//   3. the CATEGORY (and subcategory) row of Visibility/Graphics;
//   4. Object Styles, which this does not read and names as the fallback.
//
// A view TEMPLATE is not a fifth layer: when it governs V/G, the filter and
// category layers ARE the template's, and the reply says so on each row.
//
// Visibility is not a precedence at all. Any layer that hides wins: a filter set
// visible does not bring back a hidden category, and an element hidden in the view
// stays hidden whatever its filters say.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One source of graphics for an element, in precedence order.</summary>
    public sealed class GraphicsLayer
    {
        /// <summary>"element", "filter" or "category".</summary>
        public string Source;
        /// <summary>How the reply names the layer: "element", "filter:123", "category:-2000011".</summary>
        public string Label;
        /// <summary>False for a disabled filter or one the element does not pass.</summary>
        public bool Applies = true;
        /// <summary>False hides the element; null is no opinion.</summary>
        public bool? Visible;
        /// <summary>Only the override fields this layer actually SETS.</summary>
        public IDictionary<string, string> Fields = new Dictionary<string, string>();
    }

    public static class GraphicsPrecedence
    {
        public const string ObjectStyle = "object_style";

        /// <summary>Every override field the report judges.</summary>
        public static readonly string[] Properties =
        {
            "line_color", "line_weight", "line_pattern", "cut_line_color", "surface_color",
            "surface_pattern", "cut_color", "transparency", "halftone"
        };

        /// <summary>
        /// Winner per property, and the visibility verdict. <paramref name="ordered"/> is
        /// element first, then filters top-down, then category - the caller builds that
        /// order; this decides nothing about it.
        /// </summary>
        public static JObject Resolve(IList<GraphicsLayer> ordered)
        {
            var winners = new JObject();
            foreach (string property in Properties)
            {
                JObject winner = null;
                foreach (GraphicsLayer layer in ordered)
                {
                    if (layer == null || !layer.Applies || layer.Fields == null) continue;
                    if (!layer.Fields.TryGetValue(property, out string value)) continue;
                    winner = new JObject { ["value"] = value, ["from"] = layer.Label };
                    break;
                }
                winners[property] = winner ?? new JObject { ["value"] = null, ["from"] = ObjectStyle };
            }

            string hiddenBy = null;
            foreach (GraphicsLayer layer in ordered)
            {
                if (layer == null || !layer.Applies) continue;
                if (layer.Visible == false) { hiddenBy = layer.Label; break; }
            }
            winners["visible"] = new JObject
            {
                ["value"] = hiddenBy == null,
                ["decided_by"] = hiddenBy ?? "no layer hides it"
            };
            return winners;
        }
    }
}
