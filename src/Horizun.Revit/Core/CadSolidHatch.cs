// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// WHERE THE DRAWING SAYS "THIS IS WALL".
//
// MEASURED on the units plan of a permit set: between two bathrooms, two stud
// walls stand back to back with a 152 mm plumbing chase between them. Their faces
// are four parallel lines, 152 mm apart, and the two inner ones run exactly the
// same length - so the line pairing, which prefers lines that start and stop
// together, read the CHASE as a 152 mm wall, used up both inner faces, and left
// the two real walls unread. Lines alone cannot say which strips are solid: four
// equally spaced lines are one wall between two voids or two walls around one.
// The drawing CAN say it: the architect hatched the wall material and left the
// chase empty. Checked against every wall reading of two apartments, the hatch
// also exposed a reading centred on a 51 mm joint between two concrete masses.
//
// So a wall rule may name the layers its drawing hatches wall material on, and a
// pair of faces with no such hatch between them, at any of its stations, is not
// a wall. The hatch boundary is read from the DWG (Revit's import does not carry
// it), placed through every block and reference that holds it, and used as
// evidence only: its fill is not modelled, and a hatch never CREATES a wall.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Hatch boundary loops, decoded from the extractor's group-code string.</summary>
    public static class CadHatchLoops
    {
        /// <summary>
        /// The loops of one hatch, in millimetres of the frame it is drawn in.
        /// <paramref name="codes"/> is "code:value;code:value;..." with the DXF
        /// boundary groups in order (92 93 72 73 10 11 40 42 50 51 97). Line and
        /// arc edges and polyline paths are decoded; an ellipse or spline edge
        /// ends the decoding and sets <paramref name="partial"/>, because a loop
        /// with a missing edge is not a boundary anything should be tested against.
        /// </summary>
        public static List<List<CadPoint>> Parse(string codes, double mmPerUnit, out bool partial)
        {
            partial = false;
            var loops = new List<List<CadPoint>>();
            if (string.IsNullOrWhiteSpace(codes)) return loops;
            var toks = new List<KeyValuePair<int, string>>();
            foreach (string part in codes.Split(';'))
            {
                int colon = part.IndexOf(':');
                int code;
                if (colon <= 0 || !int.TryParse(part.Substring(0, colon), NumberStyles.Integer,
                                                CultureInfo.InvariantCulture, out code)) continue;
                toks.Add(new KeyValuePair<int, string>(code, part.Substring(colon + 1)));
            }
            int i = 0;
            try
            {
                while (i < toks.Count)
                {
                    if (toks[i].Key != 92) { i++; continue; }
                    int flag = Int(toks[i++].Value);
                    var pts = new List<CadPoint>();
                    if ((flag & 2) != 0)
                    {
                        bool bulge = Int(Expect(toks, i++, 72)) != 0;
                        Expect(toks, i++, 73);
                        int n = Int(Expect(toks, i++, 93));
                        for (int k = 0; k < n; k++)
                        {
                            pts.Add(Pt(Expect(toks, i++, 10), mmPerUnit));
                            if (bulge && i < toks.Count && toks[i].Key == 42) i++;
                        }
                    }
                    else
                    {
                        int n = Int(Expect(toks, i++, 93));
                        for (int k = 0; k < n; k++)
                        {
                            int type = Int(Expect(toks, i++, 72));
                            if (type == 1)
                            {
                                pts.Add(Pt(Expect(toks, i++, 10), mmPerUnit));
                                pts.Add(Pt(Expect(toks, i++, 11), mmPerUnit));
                            }
                            else if (type == 2)
                            {
                                CadPoint c = Pt(Expect(toks, i++, 10), mmPerUnit);
                                double r = Num(Expect(toks, i++, 40)) * mmPerUnit;
                                double a0 = Num(Expect(toks, i++, 50)) * Math.PI / 180.0;
                                double a1 = Num(Expect(toks, i++, 51)) * Math.PI / 180.0;
                                bool ccw = Int(Expect(toks, i++, 73)) != 0;
                                pts.AddRange(Arc(c, r, a0, a1, ccw));
                            }
                            else
                            {
                                partial = true;
                                return loops;
                            }
                        }
                    }
                    if (pts.Count >= 3) loops.Add(pts);
                }
            }
            catch (FormatException)
            {
                partial = true;
            }
            return loops;
        }

        private static string Expect(List<KeyValuePair<int, string>> toks, int i, int code)
        {
            if (i >= toks.Count || toks[i].Key != code)
                throw new FormatException("hatch boundary: expected group " + code + " at " + i);
            return toks[i].Value;
        }

        private static int Int(string s) => (int)Math.Round(Num(s));

        private static double Num(string s)
        {
            double d;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("hatch boundary: not a number: " + s);
            return d;
        }

        private static CadPoint Pt(string s, double mm)
        {
            string[] xy = s.Split(',');
            if (xy.Length < 2) throw new FormatException("hatch boundary: not a point: " + s);
            return new CadPoint(Num(xy[0]) * mm, Num(xy[1]) * mm, 0);
        }

        // DXF keeps a clockwise arc edge's angles in the clockwise sense: it runs
        // from -start to -end counter-clockwise, reversed.
        private static IEnumerable<CadPoint> Arc(CadPoint c, double r, double a0, double a1, bool ccw)
        {
            if (!ccw) { double s = -a1; a1 = -a0; a0 = s; }
            while (a1 < a0) a1 += 2 * Math.PI;
            const int steps = 8;
            var pts = new List<CadPoint>();
            for (int k = 0; k <= steps; k++)
            {
                double a = a0 + (a1 - a0) * k / steps;
                pts.Add(new CadPoint(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a), 0));
            }
            if (!ccw) pts.Reverse();
            return pts;
        }
    }

    /// <summary>What the hatch says about one pair of wall faces.</summary>
    public sealed class CadSolidVerdict
    {
        public int Stations;
        /// <summary>Stations where every sample across the band is inside hatched material.</summary>
        public int Hatched;
        public int Samples;
        public int HatchedSamples;
        public List<string> Patterns = new List<string>();
        /// <summary>At no station does hatched material span the band between the faces.</summary>
        public bool Void => Stations > 0 && Hatched == 0;

        public JObject ToJson() => new JObject
        {
            ["stations"] = Stations,
            ["hatched"] = Hatched,
            ["samples"] = Samples,
            ["hatched_samples"] = HatchedSamples,
            ["patterns"] = new JArray(Patterns.Cast<object>().ToArray())
        };
    }

    /// <summary>
    /// Every wall hatch of a drawing, in MODEL millimetres, and the one question
    /// asked of them: is this point inside hatched material on these layers?
    /// </summary>
    public sealed class CadSolidHatch
    {
        private sealed class Ring
        {
            public double MinX, MinY, MaxX, MaxY;
            public List<CadPoint> Points;
            public string Instance;     // one placed hatch: its loops are even-odd together
            public string Layer;
            public string Pattern;
        }

        private const double Cell = 2000.0;
        private readonly List<Ring> _rings = new List<Ring>();
        private readonly Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();

        public int Hatches { get; private set; }
        public int PlacedHatches { get; private set; }
        public int PartialHatches { get; private set; }
        public int Rings => _rings.Count;
        public List<string> Layers { get; } = new List<string>();

        /// <summary>
        /// Place every hatch whose layer matches <paramref name="layerPatterns"/>:
        /// once as drawn when it is in model space, once per placement of its
        /// block when it is inside one. <paramref name="toModel"/> takes a point in
        /// the drawing's frame to the model's. Paper space is never evidence.
        /// </summary>
        public static CadSolidHatch Build(IList<CadIrEntity> entities, CadPlacementReading placements,
                                          IList<string> layerPatterns, Func<CadPoint, CadPoint> toModel,
                                          bool caseSensitive = false)
        {
            var s = new CadSolidHatch();
            if (entities == null || layerPatterns == null || layerPatterns.Count == 0) return s;
            toModel = toModel ?? (p => p);
            var byBlock = new Dictionary<string, List<CadPlacedBlock>>(StringComparer.OrdinalIgnoreCase);
            if (placements != null)
                foreach (CadPlacedBlock p in placements.Placed)
                {
                    if (p.Space != null && p.Space != "model") continue;
                    string name = p.BlockName ?? "";
                    List<CadPlacedBlock> list;
                    if (!byBlock.TryGetValue(name, out list)) byBlock[name] = list = new List<CadPlacedBlock>();
                    list.Add(p);
                }
            var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CadIrEntity e in entities)
            {
                if (e == null || e.HatchLoops == null || e.HatchLoops.Count == 0) continue;
                if (!layerPatterns.Any(p => CadGlob.IsMatch(e.Layer, p, caseSensitive))) continue;
                s.Hatches++;
                if (e.HatchLoopsPartial) s.PartialHatches++;
                layers.Add(e.Layer ?? "");
                if (e.BlockPath.Count == 0)
                {
                    if (e.Space != "model") continue;
                    s.Add(e, e.Handle ?? e.Id, q => toModel(q));
                    continue;
                }
                List<CadPlacedBlock> hosts;
                if (!byBlock.TryGetValue(e.BlockPath[e.BlockPath.Count - 1], out hosts)) continue;
                foreach (CadPlacedBlock p in hosts)
                {
                    CadPlacedBlock frame = p;
                    s.Add(e, frame.Key + "/" + (e.Handle ?? e.Id), q => toModel(frame.Place(q)));
                }
            }
            s.Layers.AddRange(layers.OrderBy(l => l, StringComparer.OrdinalIgnoreCase));
            return s;
        }

        private void Add(CadIrEntity e, string instance, Func<CadPoint, CadPoint> place)
        {
            PlacedHatches++;
            foreach (List<CadPoint> loop in e.HatchLoops)
            {
                var pts = loop.Select(place).ToList();
                var r = new Ring
                {
                    Points = pts, Instance = instance, Layer = e.Layer, Pattern = e.HatchPattern,
                    MinX = pts.Min(q => q.X), MinY = pts.Min(q => q.Y),
                    MaxX = pts.Max(q => q.X), MaxY = pts.Max(q => q.Y)
                };
                int k = _rings.Count;
                _rings.Add(r);
                for (long gx = (long)Math.Floor(r.MinX / Cell); gx <= (long)Math.Floor(r.MaxX / Cell); gx++)
                    for (long gy = (long)Math.Floor(r.MinY / Cell); gy <= (long)Math.Floor(r.MaxY / Cell); gy++)
                    {
                        long key = (gx << 32) ^ (gy & 0xffffffffL);
                        List<int> bucket;
                        if (!_grid.TryGetValue(key, out bucket)) _grid[key] = bucket = new List<int>();
                        bucket.Add(k);
                    }
            }
        }

        /// <summary>
        /// The patterns of the hatches on <paramref name="layerPatterns"/> the point
        /// is inside - even-odd over each placed hatch's loops, so an island is a
        /// hole. Empty when it is in none.
        /// </summary>
        public List<string> PatternsAt(CadPoint p, IList<string> layerPatterns, bool caseSensitive = false)
        {
            var parity = new Dictionary<string, bool>(StringComparer.Ordinal);
            var patternOf = new Dictionary<string, string>(StringComparer.Ordinal);
            long key = ((long)Math.Floor(p.X / Cell) << 32) ^ ((long)Math.Floor(p.Y / Cell) & 0xffffffffL);
            List<int> bucket;
            if (!_grid.TryGetValue(key, out bucket)) return new List<string>();
            foreach (int k in bucket)
            {
                Ring r = _rings[k];
                if (p.X < r.MinX || p.X > r.MaxX || p.Y < r.MinY || p.Y > r.MaxY) continue;
                if (layerPatterns != null && !layerPatterns.Any(lp => CadGlob.IsMatch(r.Layer, lp, caseSensitive)))
                    continue;
                if (!Inside(p, r.Points)) continue;
                bool v;
                parity[r.Instance] = !(parity.TryGetValue(r.Instance, out v) && v);
                patternOf[r.Instance] = r.Pattern ?? "";
            }
            return parity.Where(kv => kv.Value).Select(kv => patternOf[kv.Key])
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// How far in from each face the band is not asked to be hatched: the
        /// finish layers a wall is drawn with are lines, not hatch (8 to 17.5 mm
        /// measured on this permit set). Never more than a quarter of the band.
        /// </summary>
        public const double FinishAllowanceMm = 20.0;
        /// <summary>The widest step between two samples across a band.</summary>
        public const double AcrossStepMm = 10.0;

        /// <summary>
        /// Stations along the pair's centreline, at <paramref name="fractions"/> of
        /// its length, each sampled ACROSS the whole interior of the band - every
        /// <see cref="AcrossStepMm"/> at most, clear of the finish allowance at
        /// each face: a station is solid when every sample stands in hatched
        /// material. MEASURED twice: a single sample on the centreline passed a
        /// chase paired with the wall beside it (that pair's centreline is the
        /// wall's face), and samples at the thirds passed a pair whose band held a
        /// 51 mm joint between two walls, because the joint sat between them.
        /// </summary>
        public CadSolidVerdict Judge(CadDoubleLine pair, IList<string> layerPatterns, bool caseSensitive = false,
                                     double[] fractions = null)
        {
            fractions = fractions ?? new[] { 0.1, 0.3, 0.5, 0.7, 0.9 };
            var v = new CadSolidVerdict();
            var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double dx = pair.End.X - pair.Start.X, dy = pair.End.Y - pair.Start.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0) return v;
            double nx = -dy / len, ny = dx / len;
            double half = pair.ThicknessMm / 2.0 - Math.Min(FinishAllowanceMm, pair.ThicknessMm / 4.0);
            int steps = Math.Max(2, (int)Math.Ceiling(2 * half / AcrossStepMm));
            var across = new double[steps + 1];
            for (int k = 0; k <= steps; k++) across[k] = -half + 2 * half * k / steps;
            foreach (double f in fractions)
            {
                double cx = pair.Start.X + dx * f, cy = pair.Start.Y + dy * f;
                v.Stations++;
                bool solid = true;
                foreach (double o in across)
                {
                    v.Samples++;
                    List<string> here = PatternsAt(new CadPoint(cx + nx * o, cy + ny * o, 0), layerPatterns,
                                                   caseSensitive);
                    if (here.Count == 0) { solid = false; continue; }
                    v.HatchedSamples++;
                    foreach (string h in here) patterns.Add(h);
                }
                if (solid) v.Hatched++;
            }
            v.Patterns = patterns.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return v;
        }

        /// <summary>One vertex of every ring: enough to check the hatches share the model's frame.</summary>
        public IEnumerable<CadPoint> Anchors() => _rings.Select(r => r.Points[0]);

        public JObject SummaryJson() => new JObject
        {
            ["hatches_on_declared_layers"] = Hatches,
            ["placed"] = PlacedHatches,
            ["rings"] = Rings,
            ["with_undecoded_edges"] = PartialHatches,
            ["layers"] = new JArray(Layers.Cast<object>().ToArray())
        };

        private static bool Inside(CadPoint p, List<CadPoint> poly)
        {
            bool c = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                CadPoint a = poly[i], b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y))
                {
                    double x = a.X + (p.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    if (x > p.X) c = !c;
                }
            }
            return c;
        }
    }
}
