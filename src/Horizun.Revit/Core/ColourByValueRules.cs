// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE PURE HALF OF horizun_manage_views color_by_value.
//
// MEASURED (field, 2026-11): a shared parameter bound to element TYPES (a budget
// code, say) was read on the instance only. Every element answered "no parameter",
// the legend came out as ONE row, "(empty)", and that row took the first colour of
// the palette: a red that read as an alarm over a model that was perfectly coded.
//
// So three decisions live here, where a test can reach them without Revit:
//   1. WHERE a value comes from: the instance when it holds one, else its type, else
//      nowhere (empty). The reply says which, per value and in total.
//   2. WHAT colour the empty value gets: a fixed neutral grey (or the one the caller
//      chose), and NEVER a slot of the palette - an empty value is the absence of a
//      value, not a value that happens to be first in the sort.
//   3. HOW the legend is ordered and capped: real values sorted, the palette walked
//      from its first colour, max_values counting real values only, empty last.
// The Revit half (Commands/ManageViewsGraphics.cs) reads parameters and writes filters.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class ColourByValueRules
    {
        public const string DefaultEmptyColour = "#AAAAAA";

        public const string FromInstance = "instance";
        public const string FromType = "type";
        public const string FromNone = "none";
        public const string FromMixed = "mixed";

        /// <summary>
        /// The empty-value colour as upper-case #RRGGBB. Absent or blank is the default
        /// grey; anything else must be exactly six hex digits (with or without the #).
        /// </summary>
        public static string NormalizeColour(string text, string field)
        {
            if (string.IsNullOrWhiteSpace(text)) return DefaultEmptyColour;
            string hex = text.Trim().TrimStart('#');
            bool valid = hex.Length == 6;
            if (valid)
                foreach (char c in hex)
                    if (!Uri.IsHexDigit(c)) { valid = false; break; }
            if (!valid)
                throw new ArgumentException((field ?? "empty_color") + " must be a six-digit hex colour like #AAAAAA, got '" + text + "'");
            return "#" + hex.ToUpperInvariant();
        }

        /// <summary>What one element contributed: its text and where the text was read.</summary>
        public sealed class ValueRead
        {
            public string Text;     // "" when the element has no value anywhere
            public string Source;   // FromInstance, FromType or FromNone
        }

        /// <summary>
        /// The instance's value when it has one; otherwise the type's; otherwise empty.
        /// A parameter that EXISTS on the instance but holds nothing does not stop the
        /// search - that is the "instance has no value" case a type-bound budget code
        /// lands in on elements whose family also carries an empty instance copy.
        /// </summary>
        public static ValueRead Choose(string instanceText, string typeText)
        {
            if (!string.IsNullOrEmpty(instanceText)) return new ValueRead { Text = instanceText, Source = FromInstance };
            if (!string.IsNullOrEmpty(typeText)) return new ValueRead { Text = typeText, Source = FromType };
            return new ValueRead { Text = "", Source = FromNone };
        }

        /// <summary>The distinct values of a view with, for each, how many elements carry it and from where.</summary>
        public sealed class Census
        {
            public sealed class Tally
            {
                public int Elements;
                public int FromInstance;
                public int FromType;

                /// <summary>instance, type, mixed or none - how this value's elements were read.</summary>
                public string Source =>
                    FromInstance > 0 && FromType > 0 ? ColourByValueRules.FromMixed
                    : FromInstance > 0 ? ColourByValueRules.FromInstance
                    : FromType > 0 ? ColourByValueRules.FromType
                    : ColourByValueRules.FromNone;
            }

            private readonly Dictionary<string, Tally> _byValue = new Dictionary<string, Tally>(StringComparer.Ordinal);

            public int InstanceElements { get; private set; }
            public int TypeElements { get; private set; }
            public int EmptyElements { get; private set; }

            public void Add(ValueRead read)
            {
                string text = read == null || read.Text == null ? "" : read.Text;
                if (!_byValue.TryGetValue(text, out Tally tally)) _byValue[text] = tally = new Tally();
                tally.Elements++;
                if (text.Length == 0) { EmptyElements++; return; }
                if (read.Source == FromType) { tally.FromType++; TypeElements++; }
                else { tally.FromInstance++; InstanceElements++; }
            }

            /// <summary>Distinct values, the empty one included when any element had no value.</summary>
            public int Count => _byValue.Count;

            public bool Has(string text) => _byValue.ContainsKey(text ?? "");

            public Tally Of(string text) => _byValue.TryGetValue(text ?? "", out Tally t) ? t : new Tally();

            public List<string> Values() => _byValue.Keys.ToList();
        }

        /// <summary>One row of the legend before any colour is a Revit object.</summary>
        public sealed class LegendEntry
        {
            public string Value;        // "" for the empty entry
            public bool IsEmpty;
            public int PaletteIndex;    // -1 for the empty entry: it never takes a palette slot
            public string FixedColour;  // the empty entry's #RRGGBB; null for a palette entry
        }

        /// <summary>
        /// The legend: real values sorted ordinally (same model, same colours), the
        /// palette walked from its first colour, at most maxValues of them; then, last,
        /// the empty value in its own colour when any element had none. The empty value
        /// does not count against maxValues and never moves a real value's colour.
        /// </summary>
        public static List<LegendEntry> PlanLegend(IEnumerable<string> distinctValues, int maxValues,
                                                    int paletteLength, string emptyColour,
                                                    out bool paletteWrapped, out int realValuesFound)
        {
            if (paletteLength < 1) throw new ArgumentOutOfRangeException(nameof(paletteLength));
            var all = (distinctValues ?? Enumerable.Empty<string>()).Select(v => v ?? "").Distinct(StringComparer.Ordinal).ToList();
            bool hasEmpty = all.Contains("");
            List<string> real = all.Where(v => v.Length > 0).ToList();
            real.Sort(StringComparer.Ordinal);
            realValuesFound = real.Count;

            var legend = new List<LegendEntry>();
            paletteWrapped = false;
            int index = 0;
            foreach (string value in real)
            {
                if (index >= maxValues) break;
                if (index >= paletteLength) paletteWrapped = true;
                legend.Add(new LegendEntry { Value = value, IsEmpty = false, PaletteIndex = index % paletteLength });
                index++;
            }
            if (hasEmpty)
                legend.Add(new LegendEntry
                {
                    Value = "", IsEmpty = true, PaletteIndex = -1,
                    FixedColour = NormalizeColour(emptyColour, "empty_color")
                });
            return legend;
        }

        /// <summary>#RRGGBB for a palette triple, in the same upper-case form the verification compares.</summary>
        public static string Hex(byte r, byte g, byte b) =>
            string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", r, g, b);
    }
}
