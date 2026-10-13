// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// SYNTHETIC schedule sheets for the L10 card reader. Every sheet here is drawn by
// this file, in drawing millimetres (y up), with the conventions a reinforcement
// schedule uses: a framed card with a big title, axis labels above the frame,
// bars as horizontal lines with a callout just above, 90-degree hook ticks at the
// bar ends, stirrup zones as dimension segments with their callouts, the stated
// stirrup total and member count, wall cards with vertical bars as polylines under
// rotated callouts and level lines, and bending tables. Nothing here comes from a
// real project; the marks, numbers and words are invented for the tests.
//
// The same fixture is read three ways: as generic primitives (this JSON), as the
// CAD IR entity shape the accoreconsole DWG reading produces (ToCadEntities), and
// - in the server tests, which link this file - drawn into a vector PDF.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Core.Tests
{
    /// <summary>A sheet being drawn: segments, polylines and texts in drawing millimetres.</summary>
    public sealed class SyntheticSheet
    {
        public const string Black = "#000000", Red = "#ff0000";
        public double Width = 420, Height = 297;   // an A3 sheet, landscape
        public readonly JArray Segments = new JArray(), Polylines = new JArray(), Texts = new JArray();

        public SyntheticSheet Line(double x0, double y0, double x1, double y1, string color = Black)
        {
            Segments.Add(new JObject { ["a"] = new JArray(x0, y0), ["b"] = new JArray(x1, y1), ["color"] = color });
            return this;
        }

        public SyntheticSheet Rect(double x0, double y0, double x1, double y1, string color = Black)
            => Line(x0, y0, x1, y0, color).Line(x1, y0, x1, y1, color).Line(x1, y1, x0, y1, color).Line(x0, y1, x0, y0, color);

        public SyntheticSheet Poly(string color, params double[] xy)
        {
            var pts = new JArray();
            for (int i = 0; i + 1 < xy.Length; i += 2) pts.Add(new JArray(xy[i], xy[i + 1]));
            Polylines.Add(new JObject { ["points"] = pts, ["color"] = color });
            return this;
        }

        /// <summary>Approximate advance of a sans-serif string: 0.6 of the height per character.</summary>
        public static double WidthOf(string s, double h) => Math.Round(0.6 * h * s.Length, 4);

        /// <summary>A text whose baseline starts at (x, y), read along the angle in degrees.</summary>
        public SyntheticSheet Text(string s, double x, double y, double h, double degrees = 0)
        {
            double r = degrees * Math.PI / 180.0;
            Texts.Add(new JObject
            {
                ["text"] = s, ["at"] = new JArray(x, y), ["height"] = h,
                ["direction"] = new JArray(Math.Round(Math.Cos(r), 12), Math.Round(Math.Sin(r), 12)),
                ["width"] = WidthOf(s, h)
            });
            return this;
        }

        /// <summary>A horizontal text CENTRED on x, its baseline gap above y.</summary>
        public SyntheticSheet Above(string s, double cx, double y, double h, double gap = 0.6)
            => Text(s, cx - WidthOf(s, h) / 2, y + gap, h);

        public JObject ToJson() => new JObject
        {
            ["units"] = "drawing_mm",
            ["segments"] = Segments,
            ["polylines"] = Polylines,
            ["texts"] = Texts
        };

        /// <summary>
        /// The same sheet as CAD IR entities (CadIrEntity.ToJson, the accoreconsole reading):
        /// lines and polylines with their points in millimetres
        /// (x, y, z), texts with ONE anchor point, a height and a rotation, and the
        /// AutoCAD colour index rather than an RGB. The extent of a text is not in the IR:
        /// with middle=true each text's point is the middle of its baseline (a text justified
        /// middle), otherwise its start (AutoCAD's default left justification).
        /// </summary>
        public JArray ToCadEntities(bool middle = true)
        {
            var a = new JArray();
            int n = 0;
            foreach (JObject s in Segments)
                a.Add(new JObject
                {
                    ["id"] = "e" + (++n), ["kind"] = "line", ["layer"] = Layer((string)s["color"]),
                    ["points"] = new JArray(new JArray((double)s["a"][0], (double)s["a"][1], 0.0),
                                            new JArray((double)s["b"][0], (double)s["b"][1], 0.0)),
                    ["color_index"] = Aci((string)s["color"])
                });
            foreach (JObject p in Polylines)
                a.Add(new JObject
                {
                    ["id"] = "e" + (++n), ["kind"] = "polyline", ["layer"] = Layer((string)p["color"]),
                    ["points"] = new JArray(p["points"].Select(q => (JToken)new JArray((double)q[0], (double)q[1], 0.0))),
                    ["color_index"] = Aci((string)p["color"])
                });
            foreach (JObject t in Texts)
                a.Add(new JObject
                {
                    ["id"] = "e" + (++n), ["kind"] = "text", ["layer"] = "TEXT",
                    ["points"] = new JArray { Anchor(t, middle) },
                    ["text"] = (string)t["text"], ["text_height_mm"] = Math.Round((double)t["height"] * ScheduleCards.CapHeight, 6),   // AutoCAD writes the height of the capitals
                    ["rotation_radians"] = Math.Atan2((double)t["direction"][1], (double)t["direction"][0])
                });
            // Something the reader must count and leave alone: an axis bubble.
            a.Add(new JObject { ["id"] = "e" + (++n), ["kind"] = "circle", ["layer"] = "AXES", ["points"] = new JArray { new JArray(0.0, 0.0, 0.0) }, ["radius_mm"] = 3.0 });
            return a;
        }

        private static JArray Anchor(JObject t, bool middle)
        {
            double x = (double)t["at"][0], y = (double)t["at"][1], w = middle ? (double)t["width"] / 2 : 0;
            return new JArray(x + w * (double)t["direction"][0], y + w * (double)t["direction"][1], 0.0);
        }

        private static string Layer(string color) => color == Red ? "BARS" : "LINES";
        private static int Aci(string color) => color == Red ? 1 : 7;
    }

    /// <summary>What a beam card says; tests change one thing and expect one refusal or one failed check.</summary>
    public sealed class BeamCardSpec
    {
        public string Title = "V-101";
        public double OriginX = 20, OriginY = 150;
        public bool Frame = true;
        /// <summary>A frame with rounded corners as a PDF carries it: the arcs are curves the reader leaves out, so only the top and bottom lines remain.</summary>
        public bool RoundedFrame;
        public string[] Axes = { "A", "B" };
        public double AxisSpanM = 6.00;
        public string TopCallout = "2#6 L=6.40";
        public double TopFromM = -0.20, TopToM = 6.20;
        public string BottomCallout = "3#4 L=6.50";
        public double BottomFromM = 0.0, BottomToM = 6.0, HookLegM = 0.25;
        public bool BottomHooks = true;
        public bool TopLine = true;
        /// <summary>Zones: callout, from, to (metres from the first axis).</summary>
        public List<Tuple<string, double, double>> Zones = new List<Tuple<string, double, double>>
        {
            Tuple.Create("11@.08", 0.15, 0.95),
            Tuple.Create("14#3@.30", 1.05, 4.95),
            Tuple.Create("11@.08", 5.05, 5.85)
        };
        public string StirrupTotal = "SON 36";
        public string Members = "Son 2 Vigas";
        public string Section = "B=25 H=40";
        /// <summary>Extra drawing hooks: a test adds a second line, a stray text, a second tick.</summary>
        public Action<SyntheticSheet, BeamCardSpec> Extra;

        public const double H = 2.5, TitleH = 5.0, Scale = 50;
        public double X(double m) => OriginX + 20 + m * 1000.0 / Scale;   // the first axis 20 mm into the card
        public double TopY => OriginY + 55;
        public double BottomY => OriginY + 30;
        public double ZoneY => OriginY + 42;
    }

    public static class ScheduleCardFixtures
    {
        public const double H = BeamCardSpec.H;

        public static SyntheticSheet BeamCard(SyntheticSheet s, BeamCardSpec c)
        {
            double ox = c.OriginX, oy = c.OriginY;
            if (c.Frame && !c.RoundedFrame) s.Rect(ox, oy, ox + 200, oy + 80);
            if (c.Frame && c.RoundedFrame) s.Line(ox + 3, oy, ox + 197, oy).Line(ox + 3, oy + 80, ox + 197, oy + 80);
            s.Text(c.Title, ox + 5, oy + 68, BeamCardSpec.TitleH);
            for (int i = 0; i < c.Axes.Length; i++)
            {
                double ax = c.X(i == 0 ? 0 : c.AxisSpanM * i / (c.Axes.Length - 1));
                s.Above(c.Axes[i], ax, oy + 82, H, 0);
            }
            if (c.TopLine) s.Line(c.X(c.TopFromM), c.TopY, c.X(c.TopToM), c.TopY, SyntheticSheet.Red);
            if (c.TopCallout != null) s.Above(c.TopCallout, (c.X(c.TopFromM) + c.X(c.TopToM)) / 2, c.TopY, H);
            s.Line(c.X(c.BottomFromM), c.BottomY, c.X(c.BottomToM), c.BottomY, SyntheticSheet.Red);
            if (c.BottomHooks)
            {
                double leg = c.HookLegM * 1000.0 / BeamCardSpec.Scale;
                s.Line(c.X(c.BottomFromM), c.BottomY, c.X(c.BottomFromM), c.BottomY - leg, SyntheticSheet.Red);
                s.Line(c.X(c.BottomToM), c.BottomY, c.X(c.BottomToM), c.BottomY - leg, SyntheticSheet.Red);
            }
            if (c.BottomCallout != null) s.Above(c.BottomCallout, (c.X(c.BottomFromM) + c.X(c.BottomToM)) / 2, c.BottomY, H);
            foreach (Tuple<string, double, double> z in c.Zones)
            {
                s.Line(c.X(z.Item2), c.ZoneY, c.X(z.Item3), c.ZoneY);
                // A zone written as "60e#3" carries its spacing on a second line just under the first.
                string[] parts = z.Item1.Split('|');
                double cx = (c.X(z.Item2) + c.X(z.Item3)) / 2;
                if (parts.Length == 1) s.Above(parts[0], cx, c.ZoneY, H);
                else
                {
                    s.Above(parts[0], cx, c.ZoneY + H + 0.8, H);
                    s.Above(parts[1], cx, c.ZoneY, H);
                }
            }
            if (c.StirrupTotal != null) s.Text(c.StirrupTotal, ox + 170, oy + 40, H);
            if (c.Section != null) s.Text(c.Section, ox + 165, oy + 30, H);
            if (c.Members != null) s.Text(c.Members, ox + 150, oy + 70, H);
            c.Extra?.Invoke(s, c);
            return s;
        }

        public static SyntheticSheet BeamSheet(Action<BeamCardSpec> tweak = null)
        {
            var c = new BeamCardSpec();
            tweak?.Invoke(c);
            return BeamCard(new SyntheticSheet(), c);
        }

        /// <summary>What a wall card says.</summary>
        public sealed class WallCardSpec
        {
            public string Title = "M-1";
            public double OriginX = 250, OriginY = 20;
            public string VerticalCallout = "4#4 L=2.90";
            public string SecondLevel = "+2.50";
            public double SecondLevelYmm = 70;     // 2.50 m at 1:50 = 50 mm above the first
            public string Horizontal = "2#3", HorizontalSpacing = "c/.20";
            public string Members = "Son 3 Muros";
            public bool Polyline = true;
            /// <summary>The level value written just UNDER its line, the line crossing the top of the text.</summary>
            public bool LevelsUnderLine;
            /// <summary>The horizontal bars' label written turned 90 degrees, its spacing beside it.</summary>
            public bool HorizontalTurned;
            public Action<SyntheticSheet, WallCardSpec> Extra;
        }

        public static SyntheticSheet WallCard(SyntheticSheet s, WallCardSpec w)
        {
            double ox = w.OriginX, oy = w.OriginY;
            s.Rect(ox, oy, ox + 120, oy + 160);
            s.Text(w.Title, ox + 5, oy + 150, BeamCardSpec.TitleH);
            double lv = w.LevelsUnderLine ? -0.75 * H : 0.6;
            s.Line(ox + 60, oy + 20, ox + 110, oy + 20).Text("+0.00", ox + 70, oy + 20 + lv, H);
            s.Line(ox + 60, oy + w.SecondLevelYmm, ox + 110, oy + w.SecondLevelYmm).Text(w.SecondLevel, ox + 70, oy + w.SecondLevelYmm + lv, H);
            if (w.Polyline)
                s.Poly(SyntheticSheet.Red, ox + 34, oy + 20, ox + 30, oy + 20, ox + 30, oy + 70, ox + 34, oy + 70);
            if (w.VerticalCallout != null) s.Text(w.VerticalCallout, ox + 29.4, oy + 33, H, 90);
            if (w.HorizontalTurned)
            {
                if (w.Horizontal != null) s.Text(w.Horizontal, ox + 80, oy + 100, H, 90);
                if (w.HorizontalSpacing != null) s.Text(w.HorizontalSpacing, ox + 84, oy + 100, H, 90);
            }
            else
            {
                if (w.Horizontal != null) s.Text(w.Horizontal, ox + 80, oy + 100, H);
                if (w.HorizontalSpacing != null) s.Text(w.HorizontalSpacing, ox + 80, oy + 96, H);
            }
            if (w.Members != null) s.Text(w.Members, ox + 60, oy + 150, H);
            w.Extra?.Invoke(s, w);
            return s;
        }

        /// <summary>A bending table: header row, rows of mark / bar / count / length / total, a totals row.</summary>
        public static SyntheticSheet Table(SyntheticSheet s, double ox, double oy, string[][] rows, string total = "77.80")
        {
            double[] cx = { ox + 10, ox + 35, ox + 60, ox + 85, ox + 110 };
            string[] head = { "MARCA", "BARRA", "CANT", "LONG", "TOTAL" };
            for (int i = 0; i < head.Length; i++) s.Text(head[i], cx[i] - SyntheticSheet.WidthOf(head[i], H) / 2, oy + 50, H);
            double y = oy + 44;
            foreach (string[] r in rows)
            {
                for (int i = 0; i < r.Length; i++)
                    if (r[i] != null) s.Text(r[i], cx[i] - SyntheticSheet.WidthOf(r[i], H) / 2, y, H);
                y -= 6;
            }
            if (total != null)
            {
                s.Text("TOTAL", cx[0] - SyntheticSheet.WidthOf("TOTAL", H) / 2, y, H);
                s.Text(total, cx[4] - SyntheticSheet.WidthOf(total, H) / 2, y, H);
            }
            return s;
        }

        public static string[][] TableRows() => new[]
        {
            new[] { "A1", "#4", "10", "6.50", "65.00" },
            new[] { "A2", "#5", "4", "3.20", "12.80" }
        };

        public static JObject TableSpec() => new JObject
        {
            ["columns"] = new JObject { ["mark"] = "MARCA", ["bar"] = "BARRA", ["count"] = "CANT", ["length"] = "LONG", ["total"] = "TOTAL" },
            ["total_label"] = "TOTAL"
        };

        /// <summary>The arguments every reading needs: the declared scale and tolerances.</summary>
        public static JObject Args(JObject extra = null)
        {
            var a = new JObject
            {
                ["scale"] = BeamCardSpec.Scale,
                ["tolerances"] = new JObject { ["length_mm"] = 30, ["zone_span_mm"] = 600 }
            };
            if (extra != null) foreach (JProperty p in extra.Properties()) a[p.Name] = p.Value.DeepClone();
            return a;
        }

        public static JObject Proposal() => new JObject
        {
            ["set_id"] = "schedule-v101",
            ["bar_type_names"] = new JObject { ["#6"] = "N6", ["#4"] = "N4", ["#3"] = "N3" },
            ["hook_type_names"] = new JObject { ["90"] = "Standard - 90 deg." }
        };

        public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
