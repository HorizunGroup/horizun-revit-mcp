// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// A WALL DOES NOT END WHERE ONE OF ITS FACES IS BROKEN.
//
// MEASURED (E-300 units 915F, 914 and 912): where a partition meets a wall, the
// drawing breaks that wall's face for the partition's width - 10 mm, 70 mm - while
// the opposite face, or the finish, runs straight through. The pairing read the
// wall as ending at the break; the stretch beyond it was shorter than the minimum
// overlap and was never read, and five devices drawn on it had no host.
//
// Under geometry.face_breaks_mm a break in a face no longer than that is bridged,
// a band is extended as far as BOTH its faces run (bridged), and two collinear
// bands of one width that then meet are one wall. Nothing is extended past the
// point where either face stops for longer than the bridge.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CadWallContinuity
    {
        /// <summary>How far inside a band's face a line may be and still be that face (a finish, as drawn).</summary>
        public const double FaceDepthMm = 40.0;

        public static List<CadWallBand> Bridge(List<CadWallBand> bands, IList<CadSegment> lines, double breakMm,
                                               double angleToleranceDegrees, JArray evidence)
        {
            var extended = new List<double[]>();
            foreach (CadWallBand b in bands)
            {
                List<double[]> loRaw, hiRaw;
                double[] lo = FaceExtent(b, lines, b.Lo, +1, breakMm, angleToleranceDegrees, out loRaw);
                double[] hi = FaceExtent(b, lines, b.Hi, -1, breakMm, angleToleranceDegrees, out hiRaw);
                double from = b.From, to = b.To;
                if (lo != null && hi != null)
                {
                    // never across a place where BOTH faces stop: that is another wall passing through
                    double[] open = Unbroken(loRaw.Concat(hiRaw).ToList(), (b.From + b.To) / 2);
                    from = Math.Min(from, Math.Max(Math.Max(lo[0], hi[0]), open[0]));
                    to = Math.Max(to, Math.Min(Math.Min(lo[1], hi[1]), open[1]));
                }
                extended.Add(new[] { from, to });
            }

            // an extension stops where another reading already occupies the same space
            for (int i = 0; i < bands.Count; i++)
            {
                CadWallBand a = bands[i];
                for (int j = 0; j < bands.Count; j++)
                {
                    if (i == j) continue;
                    CadWallBand c = bands[j];
                    if (Math.Abs(a.Direction.X * c.Direction.X + a.Direction.Y * c.Direction.Y) < 0.999) continue;
                    if (Math.Abs(a.Normal.X - c.Normal.X) > 1e-6 || Math.Abs(a.Normal.Y - c.Normal.Y) > 1e-6) continue;
                    if (Math.Abs(a.Lo - c.Lo) <= 1.0 && Math.Abs(a.Hi - c.Hi) <= 1.0) continue;   // merged below
                    double across = Math.Min(a.Hi, c.Hi) - Math.Max(a.Lo, c.Lo);
                    if (across < 0.5 * Math.Min(a.ThicknessMm, c.ThicknessMm)) continue;
                    if (c.To <= a.From + 1.0 && c.To > extended[i][0]) extended[i][0] = c.To;
                    if (c.From >= a.To - 1.0 && c.From < extended[i][1]) extended[i][1] = c.From;
                    if (c.From < a.To - 1.0 && c.To > a.From + 1.0)
                    {
                        // already overlapping: this reading is not extended at all
                        extended[i][0] = a.From;
                        extended[i][1] = a.To;
                    }
                }
            }

            // two different readings extended into the same break share it at its middle
            for (int i = 0; i < bands.Count; i++)
                for (int j = 0; j < bands.Count; j++)
                {
                    if (i == j) continue;
                    CadWallBand a = bands[i], c = bands[j];
                    if (Math.Abs(a.Direction.X * c.Direction.X + a.Direction.Y * c.Direction.Y) < 0.999) continue;
                    if (Math.Abs(a.Normal.X - c.Normal.X) > 1e-6 || Math.Abs(a.Normal.Y - c.Normal.Y) > 1e-6) continue;
                    if (Math.Abs(a.Lo - c.Lo) <= 1.0 && Math.Abs(a.Hi - c.Hi) <= 1.0) continue;
                    double across = Math.Min(a.Hi, c.Hi) - Math.Max(a.Lo, c.Lo);
                    if (across < 0.5 * Math.Min(a.ThicknessMm, c.ThicknessMm)) continue;
                    if (a.To <= c.From + 1.0 && extended[i][1] > extended[j][0])
                    {
                        double mid = (a.To + c.From) / 2;
                        extended[i][1] = Math.Min(extended[i][1], Math.Max(mid, a.To));
                        extended[j][0] = Math.Max(extended[j][0], Math.Min(mid, c.From));
                    }
                }

            // collinear bands of one width whose extended ranges meet are one wall
            var order = Enumerable.Range(0, bands.Count).ToList();
            var groupOf = order.ToArray();
            int Find(int i) { while (groupOf[i] != i) i = groupOf[i] = groupOf[groupOf[i]]; return i; }
            for (int i = 0; i < bands.Count; i++)
                for (int j = i + 1; j < bands.Count; j++)
                {
                    CadWallBand a = bands[i], c = bands[j];
                    if (Math.Abs(a.Direction.X * c.Direction.X + a.Direction.Y * c.Direction.Y) < 0.999) continue;
                    if (Math.Abs(a.Normal.X - c.Normal.X) > 1e-6 || Math.Abs(a.Normal.Y - c.Normal.Y) > 1e-6) continue;
                    if (Math.Abs(a.Lo - c.Lo) > 1.0 || Math.Abs(a.Hi - c.Hi) > 1.0) continue;
                    double overlap = Math.Min(extended[i][1], extended[j][1]) - Math.Max(extended[i][0], extended[j][0]);
                    if (overlap < 0) continue;
                    groupOf[Find(j)] = Find(i);
                }

            var result = new List<CadWallBand>();
            foreach (var group in order.GroupBy(Find))
            {
                List<int> members = group.ToList();
                CadWallBand first = bands[members[0]];
                double from = members.Min(i => extended[i][0]), to = members.Max(i => extended[i][1]);
                bool changed = members.Count > 1 || from < first.From - 0.5 || to > first.To + 0.5;
                if (!changed)
                {
                    result.Add(first);
                    continue;
                }
                CadWallBand merged = first.Piece(from, to, members.Min(i => bands[i].Lo), members.Max(i => bands[i].Hi));
                foreach (int k in members.Skip(1))
                {
                    foreach (CadDoubleLine m in bands[k].Members)
                        if (!merged.Members.Contains(m)) merged.Members.Add(m);
                    foreach (string r in bands[k].Review)
                        if (!merged.Review.Contains(r)) merged.Review.Add(r);
                }
                string why = (members.Count > 1 ? members.Count + " collinear readings of one width" : "the reading") +
                             " extended from " + (first.LengthMm).ToString("0", CultureInfo.InvariantCulture) +
                             " to " + (to - from).ToString("0", CultureInfo.InvariantCulture) +
                             " mm: both faces run on across breaks of at most " +
                             breakMm.ToString("0", CultureInfo.InvariantCulture) +
                             " mm, where a meeting wall interrupts one face as drawn.";
                merged.Assumptions.Add(why);
                evidence?.Add(new JObject
                {
                    ["readings"] = members.Count,
                    ["from_length_mm"] = Math.Round(first.LengthMm, 1),
                    ["to_length_mm"] = Math.Round(to - from, 1),
                    ["thickness_mm"] = Math.Round(merged.ThicknessMm, 1),
                    ["means"] = why
                });
                result.Add(merged);
            }
            return result;
        }

        /// <summary>The run, containing <paramref name="at"/>, where at least one of the intervals is present.</summary>
        private static double[] Unbroken(List<double[]> intervals, double at)
        {
            var runs = new List<double[]>();
            foreach (double[] iv in intervals.OrderBy(x => x[0]))
            {
                if (runs.Count > 0 && iv[0] <= runs[runs.Count - 1][1] + 1.0)
                    runs[runs.Count - 1][1] = Math.Max(runs[runs.Count - 1][1], iv[1]);
                else runs.Add(new[] { iv[0], iv[1] });
            }
            foreach (double[] r in runs)
                if (at >= r[0] - 1.0 && at <= r[1] + 1.0) return r;
            return new[] { at, at };
        }

        /// <summary>Pieces that describe the same stretch of the same wall, as one.</summary>
        public static List<CadWallBand> Dedupe(List<CadWallBand> bands)
        {
            var kept = new List<CadWallBand>();
            foreach (CadWallBand b in bands)
            {
                CadWallBand same = kept.FirstOrDefault(k =>
                    Math.Abs(k.Direction.X * b.Direction.X + k.Direction.Y * b.Direction.Y) > 0.999 &&
                    Math.Abs(k.Normal.X - b.Normal.X) < 1e-6 && Math.Abs(k.Normal.Y - b.Normal.Y) < 1e-6 &&
                    Math.Abs(k.Lo - b.Lo) <= 0.5 && Math.Abs(k.Hi - b.Hi) <= 0.5 &&
                    Math.Abs(k.From - b.From) <= 0.5 && Math.Abs(k.To - b.To) <= 0.5);
                if (same == null) { kept.Add(b); continue; }
                foreach (CadDoubleLine m in b.Members)
                    if (!same.Members.Contains(m)) same.Members.Add(m);
            }
            return kept;
        }

        /// <summary>
        /// The longest bridged run, overlapping the band, of the line groups lying from the
        /// face <paramref name="face"/> up to <see cref="FaceDepthMm"/> inward.
        /// </summary>
        private static double[] FaceExtent(CadWallBand band, IList<CadSegment> lines, double face, int inward,
                                           double breakMm, double angleTol, out List<double[]> raw)
        {
            raw = new List<double[]>();
            double cosTol = Math.Cos(angleTol * Math.PI / 180.0);
            var byOffset = new List<Tuple<double, double, double>>();   // offset, from, to
            for (int i = 0; i < lines.Count; i++)
            {
                CadSegment s = lines[i];
                double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= 0 || Math.Abs((dx * band.Direction.X + dy * band.Direction.Y) / len) < cosTol) continue;
                double oa = s.A.X * band.Normal.X + s.A.Y * band.Normal.Y;
                double ob = s.B.X * band.Normal.X + s.B.Y * band.Normal.Y;
                if (Math.Abs(oa - ob) > 1.0) continue;
                double o = (oa + ob) / 2;
                double depth = inward * (o - face);
                if (depth < -1.0 || depth > FaceDepthMm) continue;
                double fa = s.A.X * band.Direction.X + s.A.Y * band.Direction.Y;
                double fb = s.B.X * band.Direction.X + s.B.Y * band.Direction.Y;
                byOffset.Add(Tuple.Create(o, Math.Min(fa, fb), Math.Max(fa, fb)));
            }
            double[] best = null;
            foreach (var group in Cluster(byOffset))
            {
                var runs = group.OrderBy(t => t.Item2).ToList();
                double curFrom = double.NaN, curTo = double.NaN;
                foreach (var t in runs.Concat(new[] { Tuple.Create(0.0, double.MaxValue, double.MaxValue) }))
                {
                    if (!double.IsNaN(curFrom) && t.Item2 <= curTo + breakMm)
                    {
                        curTo = Math.Max(curTo, t.Item3);
                        continue;
                    }
                    if (!double.IsNaN(curFrom) && curTo >= band.From - breakMm && curFrom <= band.To + breakMm &&
                        (best == null || curTo - curFrom > best[1] - best[0]))
                    {
                        best = new[] { curFrom, curTo };
                        double bf = curFrom, bt = curTo;
                        raw = runs.Where(r => r.Item3 >= bf && r.Item2 <= bt).Select(r => new[] { r.Item2, r.Item3 }).ToList();
                    }
                    curFrom = t.Item2;
                    curTo = t.Item3;
                }
            }
            return best;
        }

        private static IEnumerable<List<Tuple<double, double, double>>> Cluster(List<Tuple<double, double, double>> rows)
        {
            var sorted = rows.OrderBy(r => r.Item1).ToList();
            for (int i = 0; i < sorted.Count;)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1].Item1 - sorted[i].Item1 <= 1.0) j++;
                yield return sorted.Skip(i).Take(j - i + 1).ToList();
                i = j + 1;
            }
        }
    }
}
