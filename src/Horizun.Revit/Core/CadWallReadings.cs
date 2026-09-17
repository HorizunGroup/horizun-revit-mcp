// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// ONE WALL, HOWEVER MANY LINES DRAW IT.
//
// MEASURED on an architectural background exported from Revit: every wall of one
// apartment arrived as three to eight parallel lines - the stud faces, the board
// faces 15.9 mm outside them, and pieces of each that stop at different places
// because each layer wraps a door or a join differently. The line pairing takes
// one pair per line and never reuses a line, and that is still not enough: a wall
// drawn as six lines contains three disjoint pairs, so it was proposed THREE
// times - 152, 184 and 187 mm thick, all on the same centreline - and sixteen
// walls were built where the drawing shows eight. The identity function noticed
// three of those collisions; the other five differed by more than its grid and
// passed as distinct walls.
//
// So the question asked here is physical, not "how close are they":
//
//   Two walls cannot occupy the same space. Two readings whose SOLIDS intersect -
//   their bands overlap across the wall AND their extents overlap along it - are
//   one wall read more than once.
//
// and everything else is kept apart and said out loud:
//
//   CONTIGUOUS  the same band, end to end. Two segments; nothing is joined.
//   FACE TO FACE  parallel and alongside, with no shared solid. Two walls, even
//               when the gap between them is smaller than the point tolerance.
//
// with one exception, measured on a second apartment: readings that share a FACE
// LINE - one line the drawing runs continuously along both - are one wall when the
// merged band holds: one width, and every stretch where only that face runs is a
// join another wall's faces explain, no longer than a wall can be thick. Otherwise
// they stay two and the reason is listed.
//
// A merge is REFUSED, and both readings go to review, when:
//
//   - the merged wall would be thicker than the rule allows;
//   - the wall's thickness changes along its length by more than the tolerance,
//     so one straight wall of one width would misstate part of it;
//   - the drawing's own END CAPS close the band as two walls with a gap between
//     them. A cap is the short line across a wall's end; a compound wall is
//     capped across its whole width, two walls are capped one at a time.
//
// Direction is normalised here, once, so two readings of a wall drawn in
// opposite senses are the same wall and produce the same centreline.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>How two parallel wall readings relate, decided by their solids.</summary>
    public enum CadWallRelation
    {
        /// <summary>Not parallel, or too far apart to say anything about.</summary>
        Unrelated,
        /// <summary>The solids intersect: one wall, read more than once.</summary>
        SameWall,
        /// <summary>The same band, end to end: two segments of one wall line, kept apart.</summary>
        Contiguous,
        /// <summary>Parallel and alongside, sharing no solid: two walls.</summary>
        FaceToFace
    }

    /// <summary>One physical wall: the readings that describe it, and the band they bound.</summary>
    public sealed class CadWallBand
    {
        public readonly List<CadDoubleLine> Members = new List<CadDoubleLine>();

        /// <summary>Canonical direction: +x, or +y for a wall within the angle tolerance of vertical.</summary>
        public CadVector Direction;
        public CadVector Normal;

        /// <summary>Across the wall (along Normal) and along it (along Direction), in absolute mm.</summary>
        public double Lo, Hi, From, To;

        /// <summary>How far the members disagree about where each end is.</summary>
        public double FromSpreadMm, ToSpreadMm;

        public string Layer;

        /// <summary>Why this band cannot be applied without a person looking. Empty when it can.</summary>
        public readonly List<string> Review = new List<string>();

        /// <summary>What was assumed to arrive at this band, beyond the merge itself.</summary>
        public readonly List<string> Assumptions = new List<string>();

        /// <summary>What the ends of the band showed, for the reasoning.</summary>
        public JObject EndEvidence;

        public double ThicknessMm => Hi - Lo;
        public double LengthMm => To - From;

        /// <summary>
        /// Take a line lying just outside the paired faces as this wall's own
        /// outer layer. Called after consolidation; nothing recomputes the band
        /// afterwards.
        /// </summary>
        public void WidenTo(double lo, double hi)
        {
            Lo = Math.Min(Lo, lo);
            Hi = Math.Max(Hi, hi);
        }

        public CadPoint Start => At(From);
        public CadPoint End => At(To);

        /// <summary>A stretch of this band, with its own across-extent; everything else is shared.</summary>
        public CadWallBand Piece(double from, double to, double lo, double hi)
        {
            var b = new CadWallBand
            {
                Direction = Direction, Normal = Normal, Layer = Layer, EndEvidence = EndEvidence,
                Lo = lo, Hi = hi, From = from, To = to,
                FromSpreadMm = Math.Abs(from - From) < 1e-6 ? FromSpreadMm : 0,
                ToSpreadMm = Math.Abs(to - To) < 1e-6 ? ToSpreadMm : 0
            };
            b.Members.AddRange(Members);
            b.Review.AddRange(Review);
            b.Assumptions.AddRange(Assumptions);
            return b;
        }

        private CadPoint At(double along)
        {
            double across = (Lo + Hi) / 2.0;
            double z = Members.Count == 0 ? 0 : Members[0].Start.Z;
            return new CadPoint(Direction.X * along + Normal.X * across,
                                Direction.Y * along + Normal.Y * across, z);
        }

        /// <summary>The indices of every line a member was paired from, each once.</summary>
        public IEnumerable<int> Lines =>
            Members.SelectMany(m => new[] { m.SegmentIndexA, m.SegmentIndexB }).Distinct().OrderBy(i => i);

        /// <summary>The member that ranked first when the readings were chosen.</summary>
        public CadDoubleLine Primary => Members.Count == 0 ? null : Members[0];
    }

    /// <summary>
    /// One pairing's use of one line: which side of the line its partner lies on,
    /// which stretch of the line it reads, and how thick it reads.
    /// </summary>
    public sealed class CadLineUse
    {
        public const string OtherSide = "other_side";
        public const string StretchAlreadyRead = "stretch_already_read";
        public const string OtherThickness = "other_thickness";

        public int Line;
        /// <summary>+1 when the partner is left of the line as drawn, -1 right, 0 on it.</summary>
        public int Side;
        /// <summary>The stretch of the line the pairing covers, along the line as drawn, in mm.</summary>
        public double From, To;
        public double ThicknessMm;

        public static CadLineUse Of(IList<CadSegment> segments, int line, int partner, CadDoubleLine pair)
        {
            var use = new CadLineUse { Line = line, ThicknessMm = pair.ThicknessMm };
            if (segments == null || line < 0 || line >= segments.Count || partner < 0 || partner >= segments.Count)
                return use;
            CadSegment s = segments[line], o = segments[partner];
            double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0) return use;
            dx /= len; dy /= len;
            double mx = (o.A.X + o.B.X) / 2.0 - s.A.X, my = (o.A.Y + o.B.Y) / 2.0 - s.A.Y;
            double cross = dx * my - dy * mx;
            use.Side = cross > 1e-9 ? 1 : cross < -1e-9 ? -1 : 0;
            double a = pair.Start.X * dx + pair.Start.Y * dy, b = pair.End.X * dx + pair.End.Y * dy;
            use.From = Math.Min(a, b);
            use.To = Math.Max(a, b);
            return use;
        }

        /// <summary>
        /// Null when this use continues the wall the line's earlier uses read:
        /// the same side, a stretch none of them covers, the same thickness.
        /// Otherwise the code of the first test it fails.
        /// </summary>
        public string RefusalToContinue(IList<CadLineUse> earlier, double toleranceMm)
            => RefusalToContinue(earlier, toleranceMm, false);

        /// <summary>
        /// <paramref name="materialBoundary"/>: the drawing hatches different material on
        /// the two sides of this line, so a leaf on one side and a leaf on the other
        /// each have it as a face. Only the other-side refusal is lifted; the same
        /// side is still bound by stretch and thickness.
        /// </summary>
        public string RefusalToContinue(IList<CadLineUse> earlier, double toleranceMm, bool materialBoundary)
        {
            if (earlier == null || earlier.Count == 0) return null;
            if (Side == 0) return OtherSide;
            var sameSide = earlier.Where(e => e.Side == Side).ToList();
            if (materialBoundary && sameSide.Count == 0) return null;
            if (materialBoundary) earlier = sameSide;
            double closest = double.MaxValue;
            foreach (CadLineUse e in earlier)
            {
                if (e.Side != Side) return OtherSide;
                if (Math.Min(To, e.To) - Math.Max(From, e.From) > toleranceMm) return StretchAlreadyRead;
                closest = Math.Min(closest, Math.Abs(ThicknessMm - e.ThicknessMm));
            }
            return closest > toleranceMm ? OtherThickness : null;
        }
    }

    /// <summary>
    /// FINISH THAT IS DRAWN IN STRETCHES IS MODELLED IN STRETCHES.
    ///
    /// MEASURED (units 915F and 914): an unhatched finish line runs along part of a
    /// wall - 60 % of one, 46 % of another - and a wall of one width either carried
    /// the finish past where it stops (seven devices 15.8 mm proud of the face drawn
    /// at them) or dropped it where it is drawn (two devices 33.3 mm behind it).
    /// Under geometry.finish "follow", a band is cut along its length where the
    /// finish on either side starts or stops, and each piece is widened only by the
    /// lines that cover it whole. A break shorter than <c>minPieceMm</c> - a symbol
    /// drawn over the line - does not cut the wall.
    /// </summary>
    public static class CadFinishSegments
    {
        public sealed class Result
        {
            public List<CadWallBand> Pieces = new List<CadWallBand>();
            public Dictionary<CadWallBand, List<int>> LinesOf = new Dictionary<CadWallBand, List<int>>();
        }

        private sealed class Candidate { public int Line; public double Depth, Offset, From, To; }

        /// <summary>A line group at one offset covering this share of the band is a face of its core.</summary>
        public const double CoreCoverage = 0.9;

        public static Result Split(CadWallBand band, IList<CadSegment> lines, double maxFinishMm, double minPieceMm,
                                   double angleToleranceDegrees)
        {
            var result = new Result();
            var parallel = Parallel(band, lines, angleToleranceDegrees, band.Lo - maxFinishMm - 0.5,
                                    band.Hi + maxFinishMm + 0.5);
            double? coreLo = CoreFace(band, parallel, +1, maxFinishMm);
            double? coreHi = CoreFace(band, parallel, -1, maxFinishMm);
            if (coreLo == null || coreHi == null || coreHi.Value - coreLo.Value <= 1)
            {
                result.Pieces.Add(band);
                return result;
            }
            var lo = new List<Candidate>();
            var hi = new List<Candidate>();
            foreach (Candidate c in parallel)
            {
                if (c.Offset < coreLo.Value - 0.5 && c.Offset >= coreLo.Value - maxFinishMm)
                    lo.Add(new Candidate { Line = c.Line, Offset = c.Offset, From = c.From, To = c.To, Depth = coreLo.Value - c.Offset });
                else if (c.Offset > coreHi.Value + 0.5 && c.Offset <= coreHi.Value + maxFinishMm)
                    hi.Add(new Candidate { Line = c.Line, Offset = c.Offset, From = c.From, To = c.To, Depth = c.Offset - coreHi.Value });
            }
            List<double[]> loProfile = Profile(band, lo, minPieceMm);
            List<double[]> hiProfile = Profile(band, hi, minPieceMm);
            var cuts = new SortedSet<double> { band.From, band.To };
            foreach (double[] p in loProfile.Concat(hiProfile)) { cuts.Add(p[0]); cuts.Add(p[1]); }
            var pieces = new List<double[]>();   // from, to, loDepth, hiDepth
            double[] cut = cuts.ToArray();
            for (int i = 0; i + 1 < cut.Length; i++)
            {
                double mid = (cut[i] + cut[i + 1]) / 2;
                pieces.Add(new[] { cut[i], cut[i + 1], DepthAt(loProfile, mid), DepthAt(hiProfile, mid) });
            }
            Merge(pieces, minPieceMm);
            foreach (double[] p in pieces)
            {
                double newLo = coreLo.Value - p[2], newHi = coreHi.Value + p[3];
                CadWallBand piece = pieces.Count == 1 && Math.Abs(newLo - band.Lo) < 0.05 && Math.Abs(newHi - band.Hi) < 0.05
                    ? band
                    : band.Piece(p[0], p[1], newLo, newHi);
                if (piece != band)
                    piece.Assumptions.Add("finish drawn in stretches: this piece (" +
                        (p[1] - p[0]).ToString("0", CultureInfo.InvariantCulture) + " mm of " +
                        band.LengthMm.ToString("0", CultureInfo.InvariantCulture) + ") is its " +
                        (coreHi.Value - coreLo.Value).ToString("0.#", CultureInfo.InvariantCulture) +
                        " mm core with " + p[2].ToString("0.#", CultureInfo.InvariantCulture) + " mm and " +
                        p[3].ToString("0.#", CultureInfo.InvariantCulture) +
                        " mm of finish on its two faces, as drawn along it.");
                result.Pieces.Add(piece);
                var mine = new List<int>();
                foreach (Candidate c in lo.Concat(hi))
                    if (c.From <= p[0] + 1 && c.To >= p[1] - 1) mine.Add(c.Line);
                result.LinesOf[piece] = mine.Distinct().ToList();
            }
            return result;
        }

        private static List<Candidate> Parallel(CadWallBand band, IList<CadSegment> lines, double angleTol,
                                                double minOffset, double maxOffset)
        {
            var found = new List<Candidate>();
            double cosTol = Math.Cos(angleTol * Math.PI / 180.0);
            for (int i = 0; i < lines.Count; i++)
            {
                CadSegment s = lines[i];
                double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= 0 || Math.Abs((dx * band.Direction.X + dy * band.Direction.Y) / len) < cosTol) continue;
                double oa = s.A.X * band.Normal.X + s.A.Y * band.Normal.Y;
                double ob = s.B.X * band.Normal.X + s.B.Y * band.Normal.Y;
                if (Math.Abs(oa - ob) > 1.0) continue;
                double o = (oa + ob) / 2;
                if (o < minOffset || o > maxOffset) continue;
                double fa = s.A.X * band.Direction.X + s.A.Y * band.Direction.Y;
                double fb = s.B.X * band.Direction.X + s.B.Y * band.Direction.Y;
                double from = Math.Max(Math.Min(fa, fb), band.From), to = Math.Min(Math.Max(fa, fb), band.To);
                if (to - from <= 0) continue;
                found.Add(new Candidate { Line = i, Offset = o, From = from, To = to });
            }
            return found;
        }

        /// <summary>
        /// The core face on one side: walking inward from the band's face (+1 from Lo,
        /// -1 from Hi), the first offset whose lines together cover CoreCoverage of the band.
        /// </summary>
        private static double? CoreFace(CadWallBand band, List<Candidate> parallel, int inward, double maxDepthMm)
        {
            // Never deeper than a finish can be: a line further in is a layer of the wall, not its face.
            double face = inward > 0 ? band.Lo : band.Hi;
            double limit = Math.Min(maxDepthMm, band.ThicknessMm / 3.0);
            var groups = parallel.Where(c => inward > 0 ? c.Offset >= face - 0.5 && c.Offset <= face + limit
                                                        : c.Offset <= face + 0.5 && c.Offset >= face - limit)
                                 .OrderBy(c => inward * c.Offset).ToList();
            for (int i = 0; i < groups.Count;)
            {
                int j = i;
                while (j + 1 < groups.Count && Math.Abs(groups[j + 1].Offset - groups[i].Offset) <= 1.0) j++;
                var intervals = groups.Skip(i).Take(j - i + 1).Select(c => new[] { c.From, c.To })
                                      .OrderBy(x => x[0]).ToList();
                double covered = 0, curFrom = double.NaN, curTo = double.NaN;
                foreach (double[] iv in intervals)
                {
                    if (double.IsNaN(curFrom)) { curFrom = iv[0]; curTo = iv[1]; continue; }
                    if (iv[0] <= curTo) curTo = Math.Max(curTo, iv[1]);
                    else { covered += curTo - curFrom; curFrom = iv[0]; curTo = iv[1]; }
                }
                if (!double.IsNaN(curFrom)) covered += curTo - curFrom;
                if (covered >= CoreCoverage * band.LengthMm)
                    return groups.Skip(i).Take(j - i + 1).Average(c => c.Offset);
                i = j + 1;
            }
            return null;
        }

        /// <summary>Depth of finish along the band, as stretches; short breaks and short stretches smoothed away.</summary>
        private static List<double[]> Profile(CadWallBand band, List<Candidate> side, double minPieceMm)
        {
            var cuts = new SortedSet<double> { band.From, band.To };
            foreach (Candidate c in side) { cuts.Add(c.From); cuts.Add(c.To); }
            double[] cut = cuts.ToArray();
            var stretches = new List<double[]>();
            for (int i = 0; i + 1 < cut.Length; i++)
            {
                double a = cut[i], b = cut[i + 1];
                double depth = 0;
                foreach (Candidate c in side)
                    if (c.From <= a + 1e-6 && c.To >= b - 1e-6) depth = Math.Max(depth, c.Depth);
                stretches.Add(new[] { a, b, depth });
            }
            MergeSide(stretches, minPieceMm);
            return stretches;
        }

        private static void MergeSide(List<double[]> s, double minPieceMm)
        {
            for (int i = 0; i + 1 < s.Count;)
            {
                if (Math.Abs(s[i][2] - s[i + 1][2]) < 1.0) { s[i][1] = s[i + 1][1]; s.RemoveAt(i + 1); }
                else i++;
            }
            bool changed = true;
            while (changed && s.Count > 1)
            {
                changed = false;
                int shortest = -1;
                for (int i = 0; i < s.Count; i++)
                    if (s[i][1] - s[i][0] < minPieceMm && (shortest < 0 || s[i][1] - s[i][0] < s[shortest][1] - s[shortest][0]))
                        shortest = i;
                if (shortest < 0) break;
                int into = shortest == 0 ? 1
                         : shortest == s.Count - 1 ? shortest - 1
                         : (s[shortest - 1][1] - s[shortest - 1][0] >= s[shortest + 1][1] - s[shortest + 1][0]
                            ? shortest - 1 : shortest + 1);
                s[into][0] = Math.Min(s[into][0], s[shortest][0]);
                s[into][1] = Math.Max(s[into][1], s[shortest][1]);
                s.RemoveAt(shortest);
                for (int i = 0; i + 1 < s.Count;)
                {
                    if (Math.Abs(s[i][2] - s[i + 1][2]) < 1.0) { s[i][1] = s[i + 1][1]; s.RemoveAt(i + 1); }
                    else i++;
                }
                changed = true;
            }
        }

        private static void Merge(List<double[]> pieces, double minPieceMm)
        {
            for (int i = 0; i + 1 < pieces.Count;)
            {
                if (Math.Abs(pieces[i][2] - pieces[i + 1][2]) < 1.0 && Math.Abs(pieces[i][3] - pieces[i + 1][3]) < 1.0)
                { pieces[i][1] = pieces[i + 1][1]; pieces.RemoveAt(i + 1); }
                else i++;
            }
            while (pieces.Count > 1)
            {
                int shortest = -1;
                for (int i = 0; i < pieces.Count; i++)
                    if (pieces[i][1] - pieces[i][0] < minPieceMm &&
                        (shortest < 0 || pieces[i][1] - pieces[i][0] < pieces[shortest][1] - pieces[shortest][0]))
                        shortest = i;
                if (shortest < 0) break;
                int into = shortest == 0 ? 1 : shortest == pieces.Count - 1 ? shortest - 1
                         : (pieces[shortest - 1][1] - pieces[shortest - 1][0] >= pieces[shortest + 1][1] - pieces[shortest + 1][0]
                            ? shortest - 1 : shortest + 1);
                pieces[into][0] = Math.Min(pieces[into][0], pieces[shortest][0]);
                pieces[into][1] = Math.Max(pieces[into][1], pieces[shortest][1]);
                pieces.RemoveAt(shortest);
            }
        }

        private static double DepthAt(List<double[]> profile, double at)
        {
            foreach (double[] p in profile)
                if (at >= p[0] - 1e-6 && at <= p[1] + 1e-6) return p[2];
            return 0;
        }
    }

    public static class CadWallReadings
    {
        /// <summary>
        /// The direction every reading of this line is expressed in, whichever way
        /// it was drawn: +x, or +y when the line is within the angle tolerance of
        /// vertical - so a wall one hair off vertical does not flip between two
        /// readings of itself.
        /// </summary>
        public static CadVector CanonicalDirection(CadPoint a, CadPoint b, double angleToleranceDegrees)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= 0) return new CadVector(0, 0);
            dx /= len; dy /= len;
            double nearVertical = Math.Sin(Math.Max(angleToleranceDegrees, 0.001) * Math.PI / 180.0);
            bool flip = Math.Abs(dx) <= nearVertical ? dy < 0 : dx < 0;
            return flip ? new CadVector(-dx, -dy) : new CadVector(dx, dy);
        }

        /// <summary>A reading's band and extent, in a given frame.</summary>
        public static void Measure(CadDoubleLine p, CadVector u, CadVector n,
                                   out double lo, out double hi, out double from, out double to)
        {
            double centre = ((p.Start.X + p.End.X) / 2.0) * n.X + ((p.Start.Y + p.End.Y) / 2.0) * n.Y;
            lo = centre - p.ThicknessMm / 2.0;
            hi = centre + p.ThicknessMm / 2.0;
            double s = p.Start.X * u.X + p.Start.Y * u.Y;
            double e = p.End.X * u.X + p.End.Y * u.Y;
            from = Math.Min(s, e);
            to = Math.Max(s, e);
        }

        /// <summary>
        /// How a reading relates to a wall already formed. Overlaps are reported
        /// signed: negative across is a gap between faces, negative along is a gap
        /// between ends. <paramref name="overlapToleranceMm"/> is how far two
        /// solids may overlap and still be two walls; <paramref name="toleranceMm"/>
        /// is how close two ends must be to meet.
        /// </summary>
        public static CadWallRelation Relate(CadWallBand band, CadDoubleLine p,
                                             double angleToleranceDegrees, double toleranceMm, double overlapToleranceMm,
                                             out double acrossOverlapMm, out double alongOverlapMm)
        {
            acrossOverlapMm = double.NaN;
            alongOverlapMm = double.NaN;
            if (band == null || p == null) return CadWallRelation.Unrelated;

            CadVector v = CanonicalDirection(p.Start, p.End, angleToleranceDegrees);
            if (v.X == 0 && v.Y == 0) return CadWallRelation.Unrelated;
            if (band.Direction.UndirectedAngleDegrees(v) > angleToleranceDegrees) return CadWallRelation.Unrelated;

            double lo, hi, from, to;
            Measure(p, band.Direction, band.Normal, out lo, out hi, out from, out to);
            return Classify(Math.Min(band.Hi, hi) - Math.Max(band.Lo, lo),
                            Math.Min(band.To, to) - Math.Max(band.From, from),
                            toleranceMm, overlapToleranceMm, out acrossOverlapMm, out alongOverlapMm);
        }

        /// <summary>The same question between two formed walls.</summary>
        public static CadWallRelation Relate(CadWallBand a, CadWallBand b,
                                             double angleToleranceDegrees, double toleranceMm, double overlapToleranceMm,
                                             out double acrossOverlapMm, out double alongOverlapMm)
        {
            acrossOverlapMm = double.NaN;
            alongOverlapMm = double.NaN;
            if (a == null || b == null) return CadWallRelation.Unrelated;
            if (a.Direction.UndirectedAngleDegrees(b.Direction) > angleToleranceDegrees) return CadWallRelation.Unrelated;
            return Classify(Math.Min(a.Hi, b.Hi) - Math.Max(a.Lo, b.Lo),
                            Math.Min(a.To, b.To) - Math.Max(a.From, b.From),
                            toleranceMm, overlapToleranceMm, out acrossOverlapMm, out alongOverlapMm);
        }

        private static CadWallRelation Classify(double across, double along, double toleranceMm, double overlapToleranceMm,
                                                out double acrossOut, out double alongOut)
        {
            acrossOut = across;
            alongOut = along;
            if (across > overlapToleranceMm && along > toleranceMm) return CadWallRelation.SameWall;
            if (across > overlapToleranceMm && Math.Abs(along) <= toleranceMm) return CadWallRelation.Contiguous;
            if (along > toleranceMm && across <= overlapToleranceMm) return CadWallRelation.FaceToFace;
            return CadWallRelation.Unrelated;
        }

        /// <summary>
        /// Group the chosen readings into physical walls, in the order they were
        /// chosen. <paramref name="allPairs"/> is every pairing the lines admitted,
        /// chosen or not: when two chosen readings collide and cannot be one wall,
        /// the lines they were read from are paired again, and a re-pairing is
        /// kept only if it is the ONE conflict-free reading that explains at least
        /// as much of those lines. <paramref name="relations"/> receives every
        /// contiguous and face-to-face pair, every re-pairing and every refusal.
        /// </summary>
        public static List<CadWallBand> Consolidate(IList<CadDoubleLine> chosen, IList<CadDoubleLine> allPairs,
                                                    IList<CadSegment> layerSegments,
                                                    double minThicknessMm, double maxThicknessMm,
                                                    double angleToleranceDegrees, double toleranceMm,
                                                    double overlapToleranceMm,
                                                    out JArray relations)
        {
            relations = new JArray();
            var bands = new List<CadWallBand>();
            if (chosen == null) return bands;
            var ctx = new Context
            {
                Segments = layerSegments, MinT = minThicknessMm, MaxT = maxThicknessMm,
                Angle = angleToleranceDegrees, Tol = toleranceMm, Overlap = overlapToleranceMm
            };

            foreach (CadDoubleLine p in chosen)
            {
                if (p == null) continue;
                var same = bands.Where(b =>
                {
                    double across, along;
                    return Relate(b, p, ctx.Angle, ctx.Tol, ctx.Overlap, out across, out along) == CadWallRelation.SameWall;
                }).ToList();

                // A READING THAT SHARES A FACE LINE with a wall and continues it
                // along that line - end to end, or across a gap no longer than a
                // wall can be thick - is the same wall when the merged band holds:
                // one width, and every stretch where only the shared face runs is
                // a join another wall's faces explain. When it does not hold, the
                // two stay apart; they share no space, so neither goes to review.
                var joinable = bands.Where(b =>
                {
                    if (same.Contains(b) || !b.Lines.Contains(p.SegmentIndexA) && !b.Lines.Contains(p.SegmentIndexB))
                        return false;
                    double across, along;
                    Relate(b, p, ctx.Angle, ctx.Tol, ctx.Overlap, out across, out along);
                    return across > ctx.Overlap && along <= ctx.Tol && along >= -(ctx.MaxT + ctx.Tol);
                }).ToList();

                if (same.Count == 0 && joinable.Count == 0) { bands.Add(NewBand(p, ctx.Angle)); continue; }

                if (joinable.Count > 0)
                {
                    List<CadWallBand> all = same.Concat(joinable).ToList();
                    CadWallBand joined = Blank(all[0]);
                    foreach (CadWallBand b in all) joined.Members.AddRange(b.Members);
                    joined.Members.Add(p);
                    Recompute(joined);
                    double gap = joinable.Max(b =>
                    {
                        double across, along;
                        Relate(b, p, ctx.Angle, ctx.Tol, ctx.Overlap, out across, out along);
                        return -along;
                    });
                    if (MergeRefusal(joined, ctx) == null)
                    {
                        relations.Add(new JObject
                        {
                            ["relation"] = "joined_along_a_shared_face",
                            ["lines"] = new JArray(p.SegmentIndexA, p.SegmentIndexB),
                            ["shares_lines_with"] = new JArray(joinable.SelectMany(b => b.Lines).Distinct().OrderBy(i => i)
                                .Where(i => i == p.SegmentIndexA || i == p.SegmentIndexB)),
                            ["gap_mm"] = Math.Round(Math.Max(0, gap), 1),
                            ["means"] = "a face line runs continuously along both readings, and the merged wall holds: one wall."
                        });
                        Accept(bands, all, joined);
                        continue;
                    }
                    // Refused: said once the walls are final, below.
                    if (same.Count == 0) { bands.Add(NewBand(p, ctx.Angle)); continue; }
                }

                // Try the merge on a copy; a refused merge leaves every band as it was.
                var trial = Blank(same[0]);
                foreach (CadWallBand b in same) trial.Members.AddRange(b.Members);
                trial.Members.Add(p);
                Recompute(trial);

                string refusal = MergeRefusal(trial, ctx);
                if (refusal == null)
                {
                    Accept(bands, same, trial);
                    continue;
                }

                // THE READINGS COLLIDE AND ARE NOT ONE WALL. Pair their lines again.
                List<CadDoubleLine> conflicting = same.SelectMany(b => b.Members).Concat(new[] { p }).ToList();
                var others = bands.Where(b => !same.Contains(b)).ToList();
                string repairNote;
                List<CadWallBand> repaired = Repair(conflicting, allPairs, others, ctx, out repairNote);
                if (repaired != null)
                {
                    foreach (CadWallBand b in same) bands.Remove(b);
                    bands.AddRange(repaired);
                    relations.Add(new JObject
                    {
                        ["relation"] = "re_paired",
                        ["lines"] = new JArray(conflicting.SelectMany(x => new[] { x.SegmentIndexA, x.SegmentIndexB })
                                                          .Distinct().OrderBy(i => i)),
                        ["first_reading_refused_because"] = refusal,
                        ["walls_now"] = new JArray(repaired.Select(b => Math.Round(b.ThicknessMm, 1))),
                        ["means"] = repairNote
                    });
                    continue;
                }

                // Refused, and no single better reading: the readings still share
                // space, so neither can be built on its own authority.
                var alone = NewBand(p, ctx.Angle);
                string reason = "its solid overlaps another wall reading on the same layer, and the two were NOT " +
                                "merged because " + refusal + ". " + repairNote +
                                " One of them is wrong and the drawing does not say which.";
                alone.Review.Add(reason);
                foreach (CadWallBand b in same)
                    if (!b.Review.Contains(reason)) b.Review.Add(reason);
                bands.Add(alone);
                relations.Add(new JObject
                {
                    ["relation"] = "overlap_not_merged",
                    ["lines"] = new JArray(p.SegmentIndexA, p.SegmentIndexB),
                    ["overlaps_lines"] = new JArray(same.SelectMany(b => b.Lines).Distinct().OrderBy(i => i)),
                    ["reason"] = refusal,
                    ["re_pairing"] = repairNote
                });
            }

            // THE READING ORDER DOES NOT DECIDE A JOIN. Two walls that share a
            // face line and hold as one are made one, however their readings
            // arrived.
            for (bool again = true; again;)
            {
                again = false;
                for (int i = 0; i < bands.Count && !again; i++)
                    for (int j = i + 1; j < bands.Count && !again; j++)
                    {
                        if (!bands[i].Lines.Intersect(bands[j].Lines).Any()) continue;
                        double across, along;
                        CadWallRelation rel = Relate(bands[i], bands[j], ctx.Angle, ctx.Tol, ctx.Overlap, out across, out along);
                        if (rel == CadWallRelation.SameWall || !(across > ctx.Overlap) || -along > ctx.MaxT + ctx.Tol) continue;
                        CadWallBand trial = Blank(bands[i]);
                        trial.Members.AddRange(bands[i].Members);
                        trial.Members.AddRange(bands[j].Members);
                        Recompute(trial);
                        if (MergeRefusal(trial, ctx) != null) continue;
                        relations.Add(new JObject
                        {
                            ["relation"] = "joined_along_a_shared_face",
                            ["lines"] = new JArray(bands[j].Lines),
                            ["shares_lines_with"] = new JArray(bands[i].Lines.Intersect(bands[j].Lines)),
                            ["gap_mm"] = Math.Round(Math.Max(0, -along), 1),
                            ["means"] = "a face line runs continuously along both readings, and the merged wall holds: one wall."
                        });
                        Accept(bands, new List<CadWallBand> { bands[i], bands[j] }, trial);
                        again = true;
                    }
            }

            // What each band's ends show, and whether they show two walls.
            foreach (CadWallBand b in bands)
            {
                JObject evidence;
                bool separate = EndCapsSeparate(b, layerSegments, ctx.Angle, ctx.Tol, out evidence);
                b.EndEvidence = evidence;
                if (separate)
                    b.Review.Add("the drawing caps this band's end as more than one wall, with a gap between them, " +
                                 "so the pair of faces that bounds it is two walls read as one");
            }

            // Contiguous and face-to-face pairs, reported and left alone.
            for (int i = 0; i < bands.Count; i++)
                for (int j = i + 1; j < bands.Count; j++)
                {
                    double across, along;
                    CadWallRelation rel = Relate(bands[i], bands[j], ctx.Angle, ctx.Tol, ctx.Overlap, out across, out along);

                    // Two final walls on one face line that were not made one: why.
                    List<int> shared = bands[i].Lines.Intersect(bands[j].Lines).ToList();
                    if (shared.Count > 0 && rel != CadWallRelation.SameWall && across > ctx.Overlap)
                    {
                        string why;
                        if (-along > ctx.MaxT + ctx.Tol)
                            why = "the gap is " + Mm(-along) + ", longer than any wall this rule reads can be thick, " +
                                  "so it is not a join";
                        else
                        {
                            CadWallBand trial = Blank(bands[i]);
                            trial.Members.AddRange(bands[i].Members);
                            trial.Members.AddRange(bands[j].Members);
                            Recompute(trial);
                            why = MergeRefusal(trial, ctx);
                        }
                        relations.Add(new JObject
                        {
                            ["relation"] = "shared_face_not_joined",
                            ["walls"] = new JArray(i, j),
                            ["shares_lines"] = new JArray(shared),
                            ["gap_mm"] = Math.Round(Math.Max(0, -along), 1),
                            ["reason"] = why,
                            ["means"] = "the two walls share a face line but one wall across them would not hold, so " +
                                        "they stay two, and whatever lies between them is not built."
                        });
                    }
                    string kind = null;
                    if (rel == CadWallRelation.Contiguous) kind = "contiguous";
                    else if (rel == CadWallRelation.FaceToFace && -across <= maxThicknessMm) kind = "face_to_face";
                    if (kind == null) continue;
                    relations.Add(new JObject
                    {
                        ["relation"] = kind,
                        ["walls"] = new JArray(i, j),
                        [kind == "contiguous" ? "end_gap_mm" : "face_gap_mm"] =
                            Math.Round(kind == "contiguous" ? -along : -across, 1),
                        ["means"] = kind == "contiguous"
                            ? "the same band, end to end: kept as two walls. Nothing in the lines says whether they " +
                              "are one element in the source model."
                            : "parallel and alongside with no shared solid: two walls, however close."
                    });
                }
            return bands;
        }

        /// <summary>The first of <paramref name="merged"/> becomes the trial; the others leave.</summary>
        private static void Accept(List<CadWallBand> bands, List<CadWallBand> merged, CadWallBand trial)
        {
            CadWallBand keep = merged[0];
            keep.Members.Clear();
            keep.Members.AddRange(trial.Members);
            foreach (CadWallBand b in merged.Skip(1))
            {
                foreach (string x in b.Assumptions)
                    if (!keep.Assumptions.Contains(x)) keep.Assumptions.Add(x);
                foreach (string x in b.Review)
                    if (!keep.Review.Contains(x)) keep.Review.Add(x);
            }
            foreach (string x in trial.Assumptions)
                if (!keep.Assumptions.Contains(x)) keep.Assumptions.Add(x);
            Recompute(keep);
            for (int i = 1; i < merged.Count; i++) bands.Remove(merged[i]);
        }

        private sealed class Context
        {
            public IList<CadSegment> Segments;
            public double MinT, MaxT, Angle, Tol, Overlap;
        }

        /// <summary>Most lines a re-pairing will look at; beyond this it is not a local question.</summary>
        private const int RepairMaxLines = 8;

        /// <summary>
        /// Pair the conflicting readings' lines again. Every set of disjoint
        /// pairings among those lines is tried; one is VALID when its walls merge
        /// cleanly, no end cap splits them and none collides with a wall outside
        /// the conflict. The best valid one - most line length explained, then
        /// most capped ends - is returned only if it is the only one that good and
        /// it explains at least as much as the readings that collided. Otherwise
        /// null, and a sentence saying why.
        /// </summary>
        private static List<CadWallBand> Repair(List<CadDoubleLine> conflicting, IList<CadDoubleLine> allPairs,
                                                List<CadWallBand> others, Context ctx, out string note)
        {
            var lines = new HashSet<int>(conflicting.SelectMany(x => new[] { x.SegmentIndexA, x.SegmentIndexB }));
            if (lines.Count > RepairMaxLines || ctx.Segments == null || allPairs == null)
            {
                note = "The " + lines.Count + " lines involved are too many to pair again locally.";
                return null;
            }
            List<CadDoubleLine> options = allPairs
                .Where(x => x != null && lines.Contains(x.SegmentIndexA) && lines.Contains(x.SegmentIndexB))
                .ToList();

            double before = Explained(conflicting, lines, ctx);
            List<CadWallBand> best = null;
            double bestLength = -1;
            int bestCaps = -1, ties = 0;

            foreach (List<CadDoubleLine> matching in Matchings(options))
            {
                if (matching.Count == 0) continue;
                List<CadWallBand> built = BuildIfValid(matching, others, ctx);
                if (built == null) continue;
                double length = Explained(matching, lines, ctx);
                int caps = built.Sum(b => CappedEnds(b, ctx));
                int cmp = length > bestLength + ctx.Tol ? 1
                        : length < bestLength - ctx.Tol ? -1
                        : caps.CompareTo(bestCaps);
                if (cmp > 0) { best = built; bestLength = length; bestCaps = caps; ties = 0; }
                else if (cmp == 0) ties++;
            }

            if (best == null)
            {
                note = "No other pairing of those " + lines.Count + " lines avoids the conflict.";
                return null;
            }
            if (ties > 0)
            {
                note = (ties + 1) + " different pairings of those lines are equally good, so none is chosen.";
                return null;
            }
            if (bestLength < before - ctx.Tol)
            {
                note = "The only conflict-free pairing explains " + Mm(bestLength) + " of line where the colliding " +
                       "readings explained " + Mm(before) + ", so it would leave drawn geometry unaccounted for.";
                return null;
            }
            note = "The lines were paired again: the only reading of them with no two walls in the same space, " +
                   "explaining " + Mm(bestLength) + " of line" + (bestCaps > 0 ? " and closed by " + bestCaps +
                   " end cap" + (bestCaps == 1 ? "" : "s") : "") + ".";
            foreach (CadWallBand b in best)
                b.Assumptions.Add(note);
            return best;
        }

        /// <summary>Every set of pairings that share no line, including the empty one.</summary>
        private static IEnumerable<List<CadDoubleLine>> Matchings(List<CadDoubleLine> options)
        {
            var current = new List<CadDoubleLine>();
            var used = new HashSet<int>();
            return Walk(options, 0, current, used);
        }

        private static IEnumerable<List<CadDoubleLine>> Walk(List<CadDoubleLine> options, int from,
                                                             List<CadDoubleLine> current, HashSet<int> used)
        {
            yield return current.ToList();
            for (int k = from; k < options.Count; k++)
            {
                CadDoubleLine o = options[k];
                if (used.Contains(o.SegmentIndexA) || used.Contains(o.SegmentIndexB)) continue;
                used.Add(o.SegmentIndexA); used.Add(o.SegmentIndexB); current.Add(o);
                foreach (List<CadDoubleLine> m in Walk(options, k + 1, current, used)) yield return m;
                current.RemoveAt(current.Count - 1);
                used.Remove(o.SegmentIndexA); used.Remove(o.SegmentIndexB);
            }
        }

        /// <summary>The walls a set of pairings makes, or null if they cannot all stand.</summary>
        private static List<CadWallBand> BuildIfValid(List<CadDoubleLine> matching, List<CadWallBand> others, Context ctx)
        {
            var built = new List<CadWallBand>();
            foreach (CadDoubleLine q in matching)
            {
                var same = built.Where(b =>
                {
                    double x, y;
                    return Relate(b, q, ctx.Angle, ctx.Tol, ctx.Overlap, out x, out y) == CadWallRelation.SameWall;
                }).ToList();
                if (same.Count == 0) { built.Add(NewBand(q, ctx.Angle)); continue; }
                var trial = Blank(same[0]);
                foreach (CadWallBand b in same) trial.Members.AddRange(b.Members);
                trial.Members.Add(q);
                Recompute(trial);
                if (MergeRefusal(trial, ctx) != null) return null;
                foreach (CadWallBand b in same) built.Remove(b);
                built.Add(trial);
            }
            foreach (CadWallBand b in built)
            {
                JObject ignored;
                if (EndCapsSeparate(b, ctx.Segments, ctx.Angle, ctx.Tol, out ignored)) return null;
                foreach (CadWallBand o in others)
                {
                    double x, y;
                    if (Relate(o, b, ctx.Angle, ctx.Tol, ctx.Overlap, out x, out y) == CadWallRelation.SameWall) return null;
                }
            }
            return built;
        }

        /// <summary>
        /// How much of the given lines' length lies along the walls these readings
        /// make - within a wall's faces and within its extent.
        /// </summary>
        private static double Explained(List<CadDoubleLine> readings, HashSet<int> lines, Context ctx)
        {
            var walls = readings.Select(r => NewBand(r, ctx.Angle)).ToList();
            double total = 0;
            foreach (int ix in lines)
            {
                if (ix < 0 || ix >= ctx.Segments.Count) continue;
                CadSegment s = ctx.Segments[ix];
                var spans = new List<double[]>();
                foreach (CadWallBand w in walls)
                {
                    CadVector? d = s.PlanDirection;
                    if (d == null || d.Value.UndirectedAngleDegrees(w.Direction) > ctx.Angle) continue;
                    double ca = s.A.X * w.Normal.X + s.A.Y * w.Normal.Y;
                    double cb = s.B.X * w.Normal.X + s.B.Y * w.Normal.Y;
                    if (Math.Min(ca, cb) < w.Lo - ctx.Tol || Math.Max(ca, cb) > w.Hi + ctx.Tol) continue;
                    double a = s.A.X * w.Direction.X + s.A.Y * w.Direction.Y;
                    double z = s.B.X * w.Direction.X + s.B.Y * w.Direction.Y;
                    double lo = Math.Max(Math.Min(a, z), w.From), hi = Math.Min(Math.Max(a, z), w.To);
                    if (hi > lo) spans.Add(new[] { lo, hi });
                }
                spans.Sort((x, y) => x[0].CompareTo(y[0]));
                double end = double.MinValue;
                foreach (double[] sp in spans)
                {
                    double lo = Math.Max(sp[0], end);
                    if (sp[1] > lo) total += sp[1] - lo;
                    end = Math.Max(end, sp[1]);
                }
            }
            return total;
        }

        /// <summary>How many of a wall's two ends the drawing closes across its whole width.</summary>
        private static int CappedEnds(CadWallBand band, Context ctx)
        {
            JObject evidence;
            EndCapsSeparate(band, ctx.Segments, ctx.Angle, ctx.Tol, out evidence);
            int n = 0;
            foreach (string end in new[] { "from_end", "to_end" })
            {
                JToken e = evidence[end];
                double covered = e?.Value<double?>("covered_mm") ?? 0;
                if (covered >= band.ThicknessMm - 2 * ctx.Tol && e?["gap_mm"] == null) n++;
            }
            return n;
        }

        private static CadWallBand Blank(CadWallBand like)
        {
            var b = new CadWallBand { Layer = like.Layer, Direction = like.Direction, Normal = like.Normal };
            return b;
        }

        private static CadWallBand NewBand(CadDoubleLine p, double angleToleranceDegrees)
        {
            var b = new CadWallBand { Layer = p.Layer };
            b.Direction = CanonicalDirection(p.Start, p.End, angleToleranceDegrees);
            b.Normal = b.Direction.PerpendicularLeft();
            b.Members.Add(p);
            Recompute(b);
            return b;
        }

        private static void Recompute(CadWallBand b)
        {
            double lo = double.MaxValue, hi = double.MinValue, from = double.MaxValue, to = double.MinValue;
            double fromMax = double.MinValue, toMin = double.MaxValue;
            foreach (CadDoubleLine m in b.Members)
            {
                double l, h, f, t;
                Measure(m, b.Direction, b.Normal, out l, out h, out f, out t);
                lo = Math.Min(lo, l); hi = Math.Max(hi, h);
                from = Math.Min(from, f); to = Math.Max(to, t);
                fromMax = Math.Max(fromMax, f); toMin = Math.Min(toMin, t);
            }
            b.Lo = lo; b.Hi = hi; b.From = from; b.To = to;
            b.FromSpreadMm = fromMax - from;
            b.ToSpreadMm = to - toMin;
        }

        /// <summary>Null when the merged band is one wall; otherwise why it is not.</summary>
        private static string MergeRefusal(CadWallBand trial, Context ctx)
        {
            IList<CadSegment> layerSegments = ctx.Segments;
            double minThicknessMm = ctx.MinT, maxThicknessMm = ctx.MaxT;
            double angleToleranceDegrees = ctx.Angle, toleranceMm = ctx.Tol;
            if (trial.ThicknessMm > maxThicknessMm + toleranceMm)
                return "together they would be " + Mm(trial.ThicknessMm) + " thick and the rule allows at most " +
                       Mm(maxThicknessMm);
            if (trial.ThicknessMm < minThicknessMm - toleranceMm)
                return "together they would be " + Mm(trial.ThicknessMm) + " thick and the rule requires at least " +
                       Mm(minThicknessMm);

            // THE WIDTH MUST HOLD ALONG THE WHOLE LENGTH. Cut the extent at every
            // reading's and every line's end; in each piece, the faces present -
            // the readings covering it and the layer's own lines inside the band -
            // must reach both faces of the merged band within the tolerance.
            var cuts = new SortedSet<double> { trial.From, trial.To };
            var spans = new List<double[]>();     // lo, hi, from, to
            foreach (CadDoubleLine m in trial.Members)
            {
                double l, h, f, t;
                Measure(m, trial.Direction, trial.Normal, out l, out h, out f, out t);
                spans.Add(new[] { l, h, f, t });
            }
            if (layerSegments != null)
                foreach (CadSegment seg in layerSegments)
                {
                    if (seg == null) continue;
                    CadVector? d = seg.PlanDirection;
                    if (d == null || d.Value.UndirectedAngleDegrees(trial.Direction) > angleToleranceDegrees) continue;
                    double ca = seg.A.X * trial.Normal.X + seg.A.Y * trial.Normal.Y;
                    double cb = seg.B.X * trial.Normal.X + seg.B.Y * trial.Normal.Y;
                    if (Math.Min(ca, cb) < trial.Lo - toleranceMm || Math.Max(ca, cb) > trial.Hi + toleranceMm) continue;
                    double fa = seg.A.X * trial.Direction.X + seg.A.Y * trial.Direction.Y;
                    double fb = seg.B.X * trial.Direction.X + seg.B.Y * trial.Direction.Y;
                    double f0 = Math.Max(Math.Min(fa, fb), trial.From), f1 = Math.Min(Math.Max(fa, fb), trial.To);
                    if (f1 - f0 <= 0) continue;
                    double c = (ca + cb) / 2.0;
                    spans.Add(new[] { c, c, f0, f1 });
                }
            foreach (double[] r in spans)
            {
                if (r[2] > trial.From && r[2] < trial.To) cuts.Add(r[2]);
                if (r[3] > trial.From && r[3] < trial.To) cuts.Add(r[3]);
            }

            double[] xs = cuts.ToArray();
            var thin = new List<double[]>();      // from, to, width, short-low, short-high
            for (int k = 0; k + 1 < xs.Length; k++)
            {
                double a = xs[k], z = xs[k + 1];
                if (z - a <= 1e-6) continue;
                double mid = (a + z) / 2.0;
                double lo = double.MaxValue, hi = double.MinValue;
                bool readingHere = false;
                foreach (double[] r in spans)
                    if (r[2] <= mid && mid <= r[3])
                    {
                        lo = Math.Min(lo, r[0]); hi = Math.Max(hi, r[1]);
                        if (r[0] != r[1]) readingHere = true;
                    }
                // Where no reading covers the band but a face line still runs, the
                // stretch is thin like any other: it holds only as a join. Where
                // nothing is drawn at all, the wall is not there. A gap no longer
                // than the point tolerance is two readings end to end, exactly as
                // CONTIGUOUS is defined.
                if (!readingHere && z - a <= toleranceMm) continue;
                if (!readingHere && lo > hi)
                    return "no reading covers " + Mm(z - a) + " of their combined length";
                bool shortLow = lo - trial.Lo > toleranceMm, shortHigh = trial.Hi - hi > toleranceMm;
                if (!shortLow && !shortHigh) continue;
                // Adjacent thin pieces with the same short sides are one stretch.
                if (thin.Count > 0 && Math.Abs(thin[thin.Count - 1][1] - a) <= 1e-6 &&
                    (thin[thin.Count - 1][3] == 1) == shortLow && (thin[thin.Count - 1][4] == 1) == shortHigh)
                {
                    thin[thin.Count - 1][1] = z;
                    thin[thin.Count - 1][2] = Math.Min(thin[thin.Count - 1][2], hi - lo);
                }
                else
                    thin.Add(new[] { a, z, hi - lo, shortLow ? 1 : 0, shortHigh ? 1 : 0 });
            }

            // A THIN STRETCH IN THE MIDDLE IS A JOIN when another wall's faces
            // leave this band outwards at both of its ends - and only then.
            var joins = new List<string>();
            thin.RemoveAll(w =>
            {
                bool atEnd = Math.Abs(w[0] - trial.From) <= 1e-6 || Math.Abs(w[1] - trial.To) <= 1e-6;
                if (atEnd) return false;
                if (w[1] - w[0] > maxThicknessMm + toleranceMm) return false;
                bool low = w[3] == 1, high = w[4] == 1;
                bool met = (!low || (MeetsFrom(trial, w[0], false, ctx) && MeetsFrom(trial, w[1], false, ctx))) &&
                           (!high || (MeetsFrom(trial, w[0], true, ctx) && MeetsFrom(trial, w[1], true, ctx)));
                if (met) joins.Add(Mm(w[1] - w[0]));
                return met;
            });
            if (joins.Count > 0)
                trial.Assumptions.Add("its face is interrupted for " + string.Join(", ", joins) + " where another " +
                    "wall meets it: that wall's own faces leave this one outwards at both ends of each gap, which is " +
                    "a join and not a change of thickness.");

            // A thinner stretch is a JOIN only at an end, and only as long as a
            // wall can be thick.
            double fromEnd = 0, toEnd = 0, cursor = trial.From;
            int i0 = 0;
            while (i0 < thin.Count && Math.Abs(thin[i0][0] - cursor) <= 1e-6)
            { fromEnd += thin[i0][1] - thin[i0][0]; cursor = thin[i0][1]; i0++; }
            int i1 = thin.Count - 1;
            cursor = trial.To;
            while (i1 >= i0 && Math.Abs(thin[i1][1] - cursor) <= 1e-6)
            { toEnd += thin[i1][1] - thin[i1][0]; cursor = thin[i1][0]; i1--; }
            if (i0 <= i1)
            {
                double[] w = thin[i0];
                if (w[2] <= toleranceMm)
                    return "for " + Mm(w[1] - w[0]) + " of its length only one face is drawn, and no wall meeting it " +
                           "there explains the gap, so it is not one wall across it";
                return "the wall is " + Mm(w[2]) + " thick for " + Mm(w[1] - w[0]) + " of its length and " +
                       Mm(trial.ThicknessMm) + " elsewhere, so one straight wall of one width would misstate it";
            }
            if (fromEnd > maxThicknessMm + toleranceMm || toEnd > maxThicknessMm + toleranceMm)
                return "the wall is thinner for " + Mm(Math.Max(fromEnd, toEnd)) + " at one end - longer than any " +
                       "wall this rule reads can be thick, so it is not a join - and " + Mm(trial.ThicknessMm) +
                       " elsewhere, so one straight wall of one width would misstate it";
            if (fromEnd > 0 || toEnd > 0)
                trial.Assumptions.Add("the wall is thinner for " + Mm(fromEnd) + " at its start and " + Mm(toEnd) +
                    " at its end, where some of its layers stop short of the others. That is how a Revit-exported " +
                    "wall looks where it joins another, so it was taken as one wall.");

            JObject ignored;
            if (EndCapsSeparate(trial, layerSegments, angleToleranceDegrees, toleranceMm, out ignored))
                return "the drawing caps the end of the merged band as separate walls with a gap between them";
            return null;
        }

        /// <summary>
        /// Is there a line on the layer, perpendicular to the wall and at this
        /// position along it, that starts at the wall's face on the given side and
        /// runs AWAY from the wall? That is the face of a wall meeting this one.
        /// </summary>
        private static bool MeetsFrom(CadWallBand band, double along, bool highSide, Context ctx)
        {
            if (ctx.Segments == null) return false;
            foreach (CadSegment seg in ctx.Segments)
            {
                if (seg == null) continue;
                CadVector? d = seg.PlanDirection;
                if (d == null || d.Value.UndirectedAngleDegrees(band.Normal) > ctx.Angle) continue;
                double at = ((seg.A.X + seg.B.X) / 2.0) * band.Direction.X + ((seg.A.Y + seg.B.Y) / 2.0) * band.Direction.Y;
                if (Math.Abs(at - along) > ctx.Tol) continue;
                double ca = seg.A.X * band.Normal.X + seg.A.Y * band.Normal.Y;
                double cb = seg.B.X * band.Normal.X + seg.B.Y * band.Normal.Y;
                double lo = Math.Min(ca, cb), hi = Math.Max(ca, cb);
                if (highSide && hi > band.Hi + ctx.Tol && lo <= band.Hi + ctx.Tol && lo >= band.Lo - ctx.Tol) return true;
                if (!highSide && lo < band.Lo - ctx.Tol && hi >= band.Lo - ctx.Tol && hi <= band.Hi + ctx.Tol) return true;
            }
            return false;
        }

        /// <summary>
        /// Do the lines across this band's ends close it as more than one wall?
        ///
        /// A cap is a line on the layer perpendicular to the wall, near one of its
        /// ends, reaching into the band. The caps at an end are united; an
        /// uncovered stretch INSIDE the band, with caps on both sides of it, is a
        /// gap between two walls. No caps - a joined end - is no evidence either
        /// way, and says so.
        /// </summary>
        public static bool EndCapsSeparate(CadWallBand band, IList<CadSegment> layerSegments,
                                           double angleToleranceDegrees, double toleranceMm,
                                           out JObject evidence)
        {
            evidence = new JObject();
            bool separate = false;
            var ends = new[]
            {
                new { Name = "from_end", Lo = band.From - toleranceMm, Hi = band.From + band.FromSpreadMm + toleranceMm },
                new { Name = "to_end", Lo = band.To - band.ToSpreadMm - toleranceMm, Hi = band.To + toleranceMm }
            };
            foreach (var end in ends)
            {
                var spans = new List<double[]>();
                if (layerSegments != null)
                    foreach (CadSegment s in layerSegments)
                    {
                        if (s == null) continue;
                        CadVector? d = s.PlanDirection;
                        if (d == null) continue;
                        if (d.Value.UndirectedAngleDegrees(band.Normal) > angleToleranceDegrees) continue;
                        double along = ((s.A.X + s.B.X) / 2.0) * band.Direction.X + ((s.A.Y + s.B.Y) / 2.0) * band.Direction.Y;
                        if (along < end.Lo || along > end.Hi) continue;
                        double a = s.A.X * band.Normal.X + s.A.Y * band.Normal.Y;
                        double z = s.B.X * band.Normal.X + s.B.Y * band.Normal.Y;
                        double lo = Math.Max(Math.Min(a, z), band.Lo), hi = Math.Min(Math.Max(a, z), band.Hi);
                        if (hi - lo <= toleranceMm) continue;
                        spans.Add(new[] { lo, hi });
                    }

                var o = new JObject { ["caps"] = spans.Count };
                if (spans.Count == 0)
                {
                    o["means"] = "no line crosses this end: it is joined to something or open, and says nothing " +
                                 "about how many walls the band holds";
                    evidence[end.Name] = o;
                    continue;
                }

                // Unite what the caps cover.
                spans.Sort((x, y) => x[0].CompareTo(y[0]));
                var covered = new List<double[]>();
                foreach (double[] s in spans)
                {
                    if (covered.Count > 0 && s[0] <= covered[covered.Count - 1][1] + toleranceMm)
                        covered[covered.Count - 1][1] = Math.Max(covered[covered.Count - 1][1], s[1]);
                    else
                        covered.Add(new[] { s[0], s[1] });
                }
                double widestGap = 0;
                for (int k = 0; k + 1 < covered.Count; k++)
                    widestGap = Math.Max(widestGap, covered[k + 1][0] - covered[k][1]);

                o["covered_mm"] = Math.Round(covered.Sum(c => c[1] - c[0]), 1);
                o["band_mm"] = Math.Round(band.ThicknessMm, 1);
                if (widestGap > toleranceMm)
                {
                    separate = true;
                    o["gap_mm"] = Math.Round(widestGap, 1);
                    o["means"] = "the caps close this end as separate walls with a " + Mm(widestGap) + " gap between them";
                }
                else
                {
                    o["means"] = "the caps close this end as one wall";
                }
                evidence[end.Name] = o;
            }
            return separate;
        }

        /// <summary>
        /// Do two straight walls, each given by its centreline and width, occupy
        /// the same space - their bands overlap across by more than
        /// <paramref name="overlapToleranceMm"/> AND their extents overlap along by
        /// more than <paramref name="toleranceMm"/>? The same physical test the
        /// consolidation applies to readings, asked of a planned wall and one that
        /// already stands.
        /// </summary>
        public static bool SolidsIntersect(CadPoint a0, CadPoint a1, double widthA,
                                           CadPoint b0, CadPoint b1, double widthB,
                                           double angleToleranceDegrees, double toleranceMm, double overlapToleranceMm,
                                           out double acrossOverlapMm, out double alongOverlapMm)
        {
            acrossOverlapMm = double.NaN;
            alongOverlapMm = double.NaN;
            CadVector u = CanonicalDirection(a0, a1, angleToleranceDegrees);
            CadVector v = CanonicalDirection(b0, b1, angleToleranceDegrees);
            if (u.X == 0 && u.Y == 0 || v.X == 0 && v.Y == 0) return false;
            if (u.UndirectedAngleDegrees(v) > angleToleranceDegrees) return false;
            CadVector n = u.PerpendicularLeft();
            double ca = ((a0.X + a1.X) / 2.0) * n.X + ((a0.Y + a1.Y) / 2.0) * n.Y;
            double cb = ((b0.X + b1.X) / 2.0) * n.X + ((b0.Y + b1.Y) / 2.0) * n.Y;
            acrossOverlapMm = Math.Min(ca + widthA / 2.0, cb + widthB / 2.0) - Math.Max(ca - widthA / 2.0, cb - widthB / 2.0);
            double fa0 = a0.X * u.X + a0.Y * u.Y, fa1 = a1.X * u.X + a1.Y * u.Y;
            double fb0 = b0.X * u.X + b0.Y * u.Y, fb1 = b1.X * u.X + b1.Y * u.Y;
            alongOverlapMm = Math.Min(Math.Max(fa0, fa1), Math.Max(fb0, fb1)) - Math.Max(Math.Min(fa0, fa1), Math.Min(fb0, fb1));
            return Classify(acrossOverlapMm, alongOverlapMm, toleranceMm, overlapToleranceMm,
                            out acrossOverlapMm, out alongOverlapMm) == CadWallRelation.SameWall;
        }

        /// <summary>
        /// Is this single line one of the wall's layer boundaries: parallel, both
        /// ends between its faces, and running alongside it?
        /// </summary>
        public static bool IsInsideBand(CadSegment segment, CadWallBand band,
                                        double angleToleranceDegrees, double toleranceMm)
        {
            if (segment == null || band == null) return false;
            CadVector? d = segment.PlanDirection;
            if (d == null) return false;
            if (d.Value.UndirectedAngleDegrees(band.Direction) > angleToleranceDegrees) return false;
            foreach (CadPoint p in new[] { segment.A, segment.B })
            {
                double across = p.X * band.Normal.X + p.Y * band.Normal.Y;
                if (across < band.Lo - toleranceMm || across > band.Hi + toleranceMm) return false;
            }
            double a = segment.A.X * band.Direction.X + segment.A.Y * band.Direction.Y;
            double z = segment.B.X * band.Direction.X + segment.B.Y * band.Direction.Y;
            double lo = Math.Max(Math.Min(a, z), band.From), hi = Math.Min(Math.Max(a, z), band.To);
            return hi - lo > toleranceMm;
        }

        private static string Mm(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
    }
}
