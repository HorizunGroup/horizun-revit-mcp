// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// REPEATED UNITS: the reason a tower is not four hundred separate conversions.
//
// A residential tower is drawn as a handful of unit TYPES and then placed: the
// same apartment, mirrored across a corridor, rotated at a corner, stacked on
// thirty floors. Converting the drawing literally builds each occurrence from
// its own lines, which works and is also the difference between a conversion
// that takes an afternoon and one that takes a fortnight — and, worse, between a
// model where one correction fixes every instance and one where it fixes one.
//
// So this file does two separable things, and keeping them separate is the whole
// design:
//
//   RECOGNITION    given regions of a drawing, decide which are the SAME layout
//                  under a rigid transform. This is inference. It can be wrong,
//                  it carries its evidence, and it refuses rather than guesses.
//
//   INSTANTIATION  given one unit type and a list of placements, produce the
//                  geometry of every occurrence. This is arithmetic. It cannot
//                  be wrong, and it is what multiplies one careful reading into
//                  a tower.
//
// HOW RECOGNITION AVOIDS BEING A SIMILARITY SCORE. Two regions are compared in
// two stages. First a SIGNATURE of rotation- and translation-invariant facts —
// per layer, how many segments and how much total length, and the sorted pair of
// bounding-box sides. Regions whose signatures differ are not compared further.
// Regions whose signatures agree are then FITTED: each of the eight rigid maps
// that takes one bounding box onto the other is applied, and a match is declared
// only when EVERY segment of one lands on a segment of the other within
// tolerance, one for one. A signature match with no fitting transform is
// reported as exactly that — two regions that look alike and are not the same —
// rather than as a weak match.
//
// WHAT IT WILL NOT DO. It will not name a unit type. The name of a unit lives in
// the drawing's text, and whether that text is reachable is a property of the
// reader — so a type this file recognises is numbered, and marked as unnamed,
// until a caller or a reader that can see text supplies the name. A unit called
// "type_3" that is really A1-mirrored is honest; one called "A1" because it was
// the first found is not.
//
// Revit-free.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// A rigid map in plan: rotate by a quarter turn, optionally mirror in X,
    /// then translate.
    ///
    /// ONLY QUARTER TURNS AND MIRRORS. A building drawn at 37 degrees exists, and
    /// this does not pretend to find it: an arbitrary-angle fit needs a
    /// correspondence to fit against, and guessing that correspondence is how a
    /// recogniser starts matching a bathroom to a kitchen. A unit at a real angle
    /// is reported as unrecognised, which is true.
    /// </summary>
    public struct CadUnitTransform
    {
        /// <summary>0, 1, 2 or 3 quarter turns anticlockwise.</summary>
        public int QuarterTurns;
        /// <summary>Whether X is negated BEFORE the rotation.</summary>
        public bool MirrorX;
        public double OffsetX, OffsetY, OffsetZ;

        public CadUnitTransform(int quarterTurns, bool mirrorX, double dx, double dy, double dz = 0)
        {
            QuarterTurns = ((quarterTurns % 4) + 4) % 4;
            MirrorX = mirrorX; OffsetX = dx; OffsetY = dy; OffsetZ = dz;
        }

        public static CadUnitTransform Identity => new CadUnitTransform(0, false, 0, 0, 0);

        public double RotationRadians => QuarterTurns * Math.PI / 2.0;

        public CadPoint Apply(CadPoint p)
        {
            double x = MirrorX ? -p.X : p.X;
            double y = p.Y;
            double rx, ry;
            switch (QuarterTurns)
            {
                case 1: rx = -y; ry = x; break;
                case 2: rx = -x; ry = -y; break;
                case 3: rx = y; ry = -x; break;
                default: rx = x; ry = y; break;
            }
            return new CadPoint(rx + OffsetX, ry + OffsetY, p.Z + OffsetZ);
        }

        /// <summary>The same map without its translation - for comparing orientations alone.</summary>
        public CadUnitTransform RotationOnly => new CadUnitTransform(QuarterTurns, MirrorX, 0, 0, 0);

        public JObject ToJson() => new JObject
        {
            ["quarter_turns"] = QuarterTurns,
            ["rotation_degrees"] = QuarterTurns * 90,
            ["mirror_x"] = MirrorX,
            ["offset_mm"] = new JArray(Math.Round(OffsetX, 4, MidpointRounding.AwayFromZero),
                                       Math.Round(OffsetY, 4, MidpointRounding.AwayFromZero),
                                       Math.Round(OffsetZ, 4, MidpointRounding.AwayFromZero)),
            ["means"] = "apply to a point in the unit type's LOCAL frame to get its place in the drawing: " +
                        "mirror X first when mirror_x, then rotate, then translate"
        };

        public static CadUnitTransform FromJson(JObject o)
        {
            if (o == null) return Identity;
            var off = o["offset_mm"] as JArray;
            return new CadUnitTransform(
                o.Value<int?>("quarter_turns") ?? 0,
                o.Value<bool?>("mirror_x") ?? false,
                off != null && off.Count > 0 ? off[0].Value<double>() : 0,
                off != null && off.Count > 1 ? off[1].Value<double>() : 0,
                off != null && off.Count > 2 ? off[2].Value<double>() : 0);
        }
    }

    /// <summary>
    /// The facts about a region that survive moving it, turning it, and
    /// mirroring it. Two regions whose signatures differ cannot be the same
    /// layout, so comparing them further is wasted work.
    /// </summary>
    public sealed class CadUnitSignature
    {
        /// <summary>The bounding box sides, SORTED, so a quarter turn does not change them.</summary>
        public double ShortSideMm, LongSideMm;

        /// <summary>layer -&gt; (segment count, total length in mm, rounded).</summary>
        public SortedDictionary<string, Tuple<int, double>> PerLayer =
            new SortedDictionary<string, Tuple<int, double>>(StringComparer.OrdinalIgnoreCase);

        public int SegmentCount;

        /// <summary>
        /// ASCII unit separator: it cannot occur in a layer name, which is the only
        /// caller-supplied text in this key.
        ///
        /// It was ':' and ';' and '|'. A layer name may contain all three, so two
        /// DIFFERENT signatures could build the same string - a layer called
        /// "A:2" with one segment against a layer called "A" with two - and land in
        /// one bucket. The fitter would reject the pair afterwards, so the cost was
        /// a wasted comparison rather than a wrong answer; it is still a key that
        /// does not distinguish what it is for.
        /// </summary>
        private const char Sep = '\u001f';

        /// <summary>The canonical string two signatures are compared by.</summary>
        public string Key(double toleranceMm)
        {
            double q = Math.Max(toleranceMm, 1e-6);
            var sb = new System.Text.StringBuilder();
            sb.Append(Snap(ShortSideMm, q)).Append(Sep).Append(Snap(LongSideMm, q)).Append(Sep)
              .Append(SegmentCount).Append(Sep);
            foreach (var kv in PerLayer)
                sb.Append(kv.Key).Append(Sep).Append(kv.Value.Item1).Append(Sep)
                  .Append(Snap(kv.Value.Item2, q)).Append(Sep);
            return sb.ToString();
        }

        private static string Snap(double v, double q) =>
            (Math.Round(v / q, MidpointRounding.AwayFromZero) * q)
                .ToString("0.###", CultureInfo.InvariantCulture);

        public JObject ToJson() => new JObject
        {
            ["short_side_mm"] = Math.Round(ShortSideMm, 3, MidpointRounding.AwayFromZero),
            ["long_side_mm"] = Math.Round(LongSideMm, 3, MidpointRounding.AwayFromZero),
            ["segment_count"] = SegmentCount,
            ["per_layer"] = new JObject(PerLayer.Select(kv =>
                new JProperty(kv.Key, new JObject
                {
                    ["segments"] = kv.Value.Item1,
                    ["total_length_mm"] = Math.Round(kv.Value.Item2, 3, MidpointRounding.AwayFromZero)
                })))
        };
    }

    /// <summary>
    /// A region of the drawing a caller asked to be treated as one unit, with
    /// the geometry that falls inside it.
    /// </summary>
    public sealed class CadUnitRegion
    {
        /// <summary>The caller's name for this region. Never invented here.</summary>
        public string Id;

        /// <summary>The boundary, as a closed ring in drawing coordinates.</summary>
        public List<CadPoint> Boundary = new List<CadPoint>();

        /// <summary>The segments that fall inside the boundary, in DRAWING coordinates.</summary>
        public List<CadSegment> Contents = new List<CadSegment>();

        /// <summary>The level this region belongs to, when the caller said. Null otherwise.</summary>
        public string Level;

        public CadPoint Min, Max;

        public double WidthMm => Max.X - Min.X;
        public double HeightMm => Max.Y - Min.Y;
    }

    /// <summary>One layout, held once, in its own local frame.</summary>
    public sealed class CadUnitType
    {
        public string Id;

        /// <summary>
        /// The name, when somebody who can read the drawing's text supplied one.
        /// Null means UNNAMED, not "no name in the drawing" - see the header.
        /// </summary>
        public string Name;

        /// <summary>Geometry in the type's own frame: origin at the region's minimum corner.</summary>
        public List<CadSegment> Local = new List<CadSegment>();

        public CadUnitSignature Signature;
        public double WidthMm, HeightMm;

        /// <summary>The region this type was first taken from, so a reviewer can go and look at it.</summary>
        public string DefinedByRegion;

        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["named"] = Name != null,
            ["name_means"] = Name == null
                ? "this type has NO NAME because the name lives in the drawing's text and nothing here could " +
                  "read it. It is not anonymous in the drawing; it is anonymous in this reading."
                : "supplied by the caller or by a reader that can reach the drawing's text",
            ["defined_by_region"] = DefinedByRegion,
            ["width_mm"] = Math.Round(WidthMm, 3, MidpointRounding.AwayFromZero),
            ["height_mm"] = Math.Round(HeightMm, 3, MidpointRounding.AwayFromZero),
            ["segment_count"] = Local.Count,
            ["signature"] = Signature == null ? (JToken)JValue.CreateNull() : Signature.ToJson()
        };
    }

    /// <summary>One occurrence of a type: where it sits and how it is turned.</summary>
    public sealed class CadUnitOccurrence
    {
        public string UnitTypeId;
        public string RegionId;
        public CadUnitTransform Transform;
        public string Level;

        /// <summary>How many of the type's segments landed on one of this region's, within tolerance.</summary>
        public int MatchedSegments;
        public int TypeSegments;

        /// <summary>Segments of this region that NOTHING in the type accounts for.</summary>
        public int UnexplainedSegments;

        /// <summary>
        /// How many of the eight rigid maps fitted this region EXACTLY.
        ///
        /// More than one means the layout is symmetric under a quarter turn or a
        /// mirror, so the transform reported here is one correct answer among
        /// several - not the measured orientation of the unit. The geometry it
        /// produces is the same either way; what is ambiguous is what to CALL it,
        /// and a schedule built on a mirrored type name would be wrong.
        /// </summary>
        public int ExactFitCount = 1;

        /// <summary>
        /// True when the height in the transform came from a DECLARED level
        /// elevation, false when nobody supplied one and it defaulted to zero.
        ///
        /// A declared zero is a ground floor. An undeclared one is a level nobody
        /// gave a height to - and a stack of ten of those occupies one floor's
        /// worth of space while looking, in plan, exactly like a stack of ten.
        /// </summary>
        public bool ElevationDeclared = true;

        public bool OrientationAmbiguous => Exact && ExactFitCount > 1;

        public bool Exact => MatchedSegments == TypeSegments && UnexplainedSegments == 0;

        public JObject ToJson() => new JObject
        {
            ["unit_type"] = UnitTypeId,
            ["region"] = RegionId,
            ["level"] = Level,
            ["transform"] = Transform.ToJson(),
            ["matched_segments"] = MatchedSegments,
            ["type_segments"] = TypeSegments,
            ["unexplained_segments"] = UnexplainedSegments,
            ["exact"] = Exact,
            ["exact_fit_count"] = ExactFitCount,
            ["orientation_ambiguous"] = OrientationAmbiguous,
            ["elevation_declared"] = ElevationDeclared,
            ["elevation_means"] = ElevationDeclared
                ? "the height in the transform above came from a declared level elevation."
                : "NOBODY DECLARED A HEIGHT FOR THIS LEVEL and the transform above carries zero. A declared " +
                  "zero is a ground floor; this is not one. Stack several levels this way and every copy " +
                  "occupies the same space, which in plan is indistinguishable from a correct stack.",
            ["orientation_means"] = OrientationAmbiguous
                ? "this layout is SYMMETRIC: " + ExactFitCount.ToString(CultureInfo.InvariantCulture) +
                  " of the eight rigid maps reproduce it exactly, so the transform above is one correct " +
                  "answer among several rather than the unit's measured orientation. The geometry is the " +
                  "same either way; what a mirrored or rotated instance should be CALLED is not."
                : "exactly one rigid map reproduces this region, so the transform above is the unit's " +
                  "orientation and not a choice",
            ["means"] = Exact
                ? "every segment of the type landed on one of this region's, and this region has nothing the " +
                  "type does not explain. The two are the same layout."
                : "this region matched the type's signature but the fitted transform leaves " +
                  UnexplainedSegments.ToString(CultureInfo.InvariantCulture) + " of its segments unexplained " +
                  "and " + (TypeSegments - MatchedSegments).ToString(CultureInfo.InvariantCulture) +
                  " of the type's unaccounted for. It is a VARIANT, not an instance, and nothing was built " +
                  "from it."
        };
    }

    /// <summary>Two regions that look alike and are not the same. Reported, never rounded to a match.</summary>
    public sealed class CadUnitNearMiss
    {
        public string RegionA, RegionB;
        public string Why;

        public JObject ToJson() => new JObject
        {
            ["region_a"] = RegionA,
            ["region_b"] = RegionB,
            ["why"] = Why,
            ["means"] = "these two regions agree on every rotation-invariant fact this file measures and " +
                        "still do not map onto each other. Treating them as one type would put one " +
                        "apartment's outlets in another's walls."
        };
    }

    /// <summary>What one recognition pass concluded, including everything it would not conclude.</summary>
    public sealed class CadUnitReading
    {
        public List<CadUnitType> Types = new List<CadUnitType>();
        public List<CadUnitOccurrence> Occurrences = new List<CadUnitOccurrence>();
        public List<CadUnitNearMiss> NearMisses = new List<CadUnitNearMiss>();
        public List<string> UnrecognisedRegions = new List<string>();

        /// <summary>
        /// Regions that enclose nothing and were excluded from the comparison
        /// entirely. Named rather than silently absent: a boundary in the wrong
        /// place looks exactly like one whose content this reader drops.
        /// </summary>
        public List<string> EmptyRegions = new List<string>();

        public double ToleranceMm;

        public JObject ToJson() => new JObject
        {
            ["tolerance_mm"] = ToleranceMm,
            ["types"] = new JArray(Types.Select(t => (JToken)t.ToJson())),
            ["occurrences"] = new JArray(Occurrences.Select(o => (JToken)o.ToJson())),
            ["near_misses"] = new JArray(NearMisses.Select(n => (JToken)n.ToJson())),
            ["unrecognised_regions"] = new JArray(UnrecognisedRegions.Select(s => (JToken)s)),
            ["empty_regions"] = new JArray(EmptyRegions.Select(s => (JToken)s)),
            ["empty_regions_mean"] = EmptyRegions.Count == 0
                ? "every region given enclosed something"
                : "these enclose nothing this reading produced and took no part in the comparison. Two " +
                  "empty regions have identical signatures and would match each other exactly, which is a " +
                  "unit type with no geometry.",
            ["summary"] = new JObject
            {
                ["type_count"] = Types.Count,
                ["occurrence_count"] = Occurrences.Count,
                // EVERY OCCURRENCE IS EXACT, by construction: a partial fit is a
                // near miss. The field stays so that a reader who remembers the
                // old shape sees the zero rather than wondering where it went.
                ["exact_occurrences"] = Occurrences.Count(o => o.Exact),
                ["partial_occurrences"] = Occurrences.Count(o => !o.Exact),
                ["ambiguous_orientation"] = Occurrences.Count(o => o.OrientationAmbiguous),
                ["near_misses"] = NearMisses.Count,
                ["unrecognised"] = UnrecognisedRegions.Count,
                ["means"] = "an occurrence is a region EVERY segment of its type lands on, one for one. " +
                            "Anything less is a near miss carrying its numbers, and a region that is a near " +
                            "miss must be read on its own - a tower with three genuine layouts and one that " +
                            "differs by a wall is a tower where that difference matters. " +
                            (Occurrences.Count(o => o.OrientationAmbiguous) == 0
                                ? "No occurrence here is orientation-ambiguous."
                                : "Where orientation is ambiguous the layout is symmetric, so the transform " +
                                  "reported is one correct answer among several - the geometry is the same " +
                                  "either way and what the instance should be CALLED is not.")
            }
        };
    }

    public static class CadUnitRules
    {
        /// <summary>
        /// Group regions into types and occurrences.
        ///
        /// The first region of each signature group defines the type; the others
        /// are fitted against it. A region that fits nothing becomes its own type
        /// only if the caller asked for that - otherwise it is unrecognised, and
        /// "unrecognised" is a better answer than a type with one member.
        /// </summary>
        public static CadUnitReading Recognise(IList<CadUnitRegion> regions, double toleranceMm,
                                               bool singletonsBecomeTypes = false)
        {
            var reading = new CadUnitReading { ToleranceMm = toleranceMm };
            if (regions == null || regions.Count == 0) return reading;

            foreach (CadUnitRegion r in regions) EnsureBounds(r);

            var groups = new Dictionary<string, List<CadUnitRegion>>(StringComparer.Ordinal);
            var signatures = new Dictionary<string, CadUnitSignature>(StringComparer.Ordinal);

            foreach (CadUnitRegion r in regions)
            {
                // AN EMPTY REGION IS NEVER A TYPE, wherever the call came from.
                //
                // Two boundaries enclosing nothing have identical signatures - zero
                // segments, zero length, a zero-by-zero box - so they match each
                // other EXACTLY, and the reading reports a unit type with two
                // occurrences and no geometry. That is the most confident wrong
                // answer this file can produce, and the caller that supplies the
                // regions is not always the one that checked them.
                if (r.Contents.Count == 0)
                {
                    reading.EmptyRegions.Add(r.Id);
                    continue;
                }
                CadUnitSignature sig = Signature(r, toleranceMm);
                signatures[r.Id] = sig;
                string key = sig.Key(toleranceMm);
                List<CadUnitRegion> bucket;
                if (!groups.TryGetValue(key, out bucket)) groups[key] = bucket = new List<CadUnitRegion>();
                bucket.Add(r);
            }

            // TWO PASSES, BECAUSE THE SIGNATURE IS A FAST PATH AND NOT THE ANSWER.
            //
            // The signature snaps sums of lengths to a quantum, so two regions
            // that ARE the same layout can land in different buckets when their
            // totals fall either side of one - accumulated drafting noise over a
            // few hundred segments is easily that much. Grouping by signature
            // first and then giving every leftover region a real fit against
            // every type found keeps the speed and removes the failure: the
            // bucket decides who is compared cheaply, the fitter decides who
            // matches.
            var leftovers = new List<CadUnitRegion>();

            foreach (var kv in groups.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                List<CadUnitRegion> bucket = kv.Value;
                if (bucket.Count == 1) { leftovers.Add(bucket[0]); continue; }

                CadUnitRegion first = bucket[0];
                var type = new CadUnitType
                {
                    Id = "u" + (reading.Types.Count + 1).ToString(CultureInfo.InvariantCulture),
                    DefinedByRegion = first.Id,
                    Signature = signatures[first.Id],
                    WidthMm = first.WidthMm,
                    HeightMm = first.HeightMm,
                    Local = ToLocal(first)
                };
                reading.Types.Add(type);

                reading.Occurrences.Add(new CadUnitOccurrence
                {
                    UnitTypeId = type.Id,
                    RegionId = first.Id,
                    Level = first.Level,
                    Transform = new CadUnitTransform(0, false, first.Min.X, first.Min.Y, 0),
                    MatchedSegments = type.Local.Count,
                    TypeSegments = type.Local.Count,
                    UnexplainedSegments = 0
                });

                for (int i = 1; i < bucket.Count; i++)
                {
                    // ONLY AN EXACT FIT IS AN OCCURRENCE, on this path and on the
                    // leftover path below.
                    //
                    // This used to accept a partial fit here and require an exact
                    // one there, so the very same pair of regions got a different
                    // verdict depending on whether their signatures happened to
                    // land in one bucket - and the bucket is a performance detail,
                    // not a fact about the building.
                    CadUnitOccurrence fitted = Fit(type, bucket[i], toleranceMm);
                    if (fitted != null && fitted.Exact) { reading.Occurrences.Add(fitted); continue; }

                    reading.NearMisses.Add(new CadUnitNearMiss
                    {
                        RegionA = first.Id,
                        RegionB = bucket[i].Id,
                        Why = fitted == null
                            ? "identical signature; none of the eight rigid maps between their bounding " +
                              "boxes puts every segment of one on a segment of the other within " +
                              toleranceMm.ToString("0.##", CultureInfo.InvariantCulture) + " mm"
                            : "identical signature, and the best of the eight rigid maps explains " +
                              fitted.MatchedSegments.ToString(CultureInfo.InvariantCulture) + " of the " +
                              fitted.TypeSegments.ToString(CultureInfo.InvariantCulture) +
                              " segments of the type, leaving " +
                              fitted.UnexplainedSegments.ToString(CultureInfo.InvariantCulture) +
                              " of this region's unaccounted for. A partial fit is not a placement."
                    });
                    reading.UnrecognisedRegions.Add(bucket[i].Id);
                }
            }

            // THE SECOND PASS. A region whose signature bucket held only itself
            // is fitted against every type already found before it is called
            // unrecognised.
            foreach (CadUnitRegion r in leftovers)
            {
                CadUnitOccurrence hit = null;
                foreach (CadUnitType t in reading.Types)
                {
                    CadUnitOccurrence attempt = Fit(t, r, toleranceMm);
                    if (attempt != null && attempt.Exact) { hit = attempt; break; }
                }
                if (hit != null) { reading.Occurrences.Add(hit); continue; }

                if (!singletonsBecomeTypes) { reading.UnrecognisedRegions.Add(r.Id); continue; }

                var solo = new CadUnitType
                {
                    Id = "u" + (reading.Types.Count + 1).ToString(CultureInfo.InvariantCulture),
                    DefinedByRegion = r.Id,
                    Signature = signatures[r.Id],
                    WidthMm = r.WidthMm,
                    HeightMm = r.HeightMm,
                    Local = ToLocal(r)
                };
                reading.Types.Add(solo);
                reading.Occurrences.Add(new CadUnitOccurrence
                {
                    UnitTypeId = solo.Id,
                    RegionId = r.Id,
                    Level = r.Level,
                    Transform = new CadUnitTransform(0, false, r.Min.X, r.Min.Y, 0),
                    MatchedSegments = solo.Local.Count,
                    TypeSegments = solo.Local.Count,
                    UnexplainedSegments = 0
                });
            }
            return reading;
        }

        /// <summary>
        /// Try the eight rigid maps that take the region's bounding box onto the
        /// type's, and return the occurrence for the first that fits EXACTLY.
        ///
        /// Returns the best partial fit when no map is exact, so that a variant
        /// is reported as a variant with numbers on it, rather than as nothing.
        /// Null when no map explains even half the segments - at that point the
        /// two are not related and saying "24% matched" is noise.
        /// </summary>
        public static CadUnitOccurrence Fit(CadUnitType type, CadUnitRegion region, double toleranceMm)
        {
            if (type == null || region == null) return null;
            EnsureBounds(region);
            List<CadSegment> local = ToLocal(region);

            CadUnitOccurrence best = null;
            CadUnitOccurrence firstExact = null;
            int exactFits = 0;

            for (int turns = 0; turns < 4; turns++)
                for (int m = 0; m < 2; m++)
                {
                    bool mirror = m == 1;
                    // The map is built in LOCAL frames: take the type's local
                    // geometry through the rotation, then line its minimum corner
                    // up with the region's. Working in local frames is what keeps
                    // the translation out of the comparison, so a unit forty
                    // metres away compares exactly like one at the origin.
                    var rotation = new CadUnitTransform(turns, mirror, 0, 0, 0);
                    List<CadSegment> mapped = Map(type.Local, rotation);
                    CadPoint min, max;
                    Bounds(mapped, out min, out max);
                    var align = new CadUnitTransform(turns, mirror, -min.X, -min.Y, 0);
                    mapped = Map(type.Local, align);

                    int matched, unexplained;
                    Compare(mapped, local, toleranceMm, out matched, out unexplained);

                    var occurrence = new CadUnitOccurrence
                    {
                        UnitTypeId = type.Id,
                        RegionId = region.Id,
                        Level = region.Level,
                        // The transform a caller applies goes from the TYPE's local
                        // frame to the DRAWING, so the region's own origin is added
                        // back on here and nowhere else.
                        Transform = new CadUnitTransform(turns, mirror,
                                                         align.OffsetX + region.Min.X,
                                                         align.OffsetY + region.Min.Y, 0),
                        MatchedSegments = matched,
                        TypeSegments = type.Local.Count,
                        UnexplainedSegments = unexplained
                    };

                    if (occurrence.Exact)
                    {
                        // EVERY EXACT FIT IS COUNTED, not just the first.
                        //
                        // A layout symmetric under a quarter turn or a mirror fits
                        // several ways, and returning the first meant the reported
                        // orientation was decided by loop order. The geometry is
                        // identical either way - this is not a wrong placement -
                        // but an orientation that looks measured and is not will be
                        // read as one, and somebody will build a mirrored family
                        // schedule from it.
                        exactFits++;
                        if (firstExact == null) firstExact = occurrence;
                        continue;
                    }
                    if (best == null || occurrence.MatchedSegments > best.MatchedSegments) best = occurrence;
                }

            if (firstExact != null)
            {
                firstExact.ExactFitCount = exactFits;
                return firstExact;
            }

            if (best == null || type.Local.Count == 0) return null;
            return best.MatchedSegments * 2 >= type.Local.Count ? best : null;
        }

        /// <summary>
        /// STACK A UNIT: one type, one plan position, many levels.
        ///
        /// This is the arithmetic half, and it is deliberately dumb. It does not
        /// decide which floors repeat - a podium level with a different core is
        /// not a typical floor, and only somebody who knows the building can say
        /// where the typical range starts. It takes the levels it is given.
        /// </summary>
        public static List<CadUnitOccurrence> Stack(CadUnitOccurrence at, IEnumerable<string> levels,
                                                    IDictionary<string, double> levelElevationsMm = null)
        {
            var result = new List<CadUnitOccurrence>();
            if (at == null || levels == null) return result;
            foreach (string level in levels)
            {
                // A ZERO THAT WAS DECLARED AND A ZERO NOBODY SUPPLIED ARE NOT THE
                // SAME NUMBER. TryGetValue leaves z at 0 for a level the map does
                // not name - a missing map, or one level spelled differently - and
                // the result is a stack of copies in one place that reads, in plan,
                // as a correct stack.
                double z = 0;
                bool declared = levelElevationsMm != null &&
                                levelElevationsMm.TryGetValue(level ?? "", out z);
                result.Add(new CadUnitOccurrence
                {
                    ElevationDeclared = declared,
                    UnitTypeId = at.UnitTypeId,
                    RegionId = at.RegionId,
                    Level = level,
                    Transform = new CadUnitTransform(at.Transform.QuarterTurns, at.Transform.MirrorX,
                                                     at.Transform.OffsetX, at.Transform.OffsetY, z),
                    MatchedSegments = at.MatchedSegments,
                    TypeSegments = at.TypeSegments,
                    UnexplainedSegments = at.UnexplainedSegments
                });
            }
            return result;
        }

        /// <summary>The geometry of one occurrence, in drawing coordinates, ready to be interpreted.</summary>
        public static List<CadSegment> Instantiate(CadUnitType type, CadUnitOccurrence occurrence)
        {
            if (type == null || occurrence == null) return new List<CadSegment>();
            return Map(type.Local, occurrence.Transform);
        }

        // ---------------------------------------------------------------------
        /// <summary>The rotation- and translation-invariant facts about a region.</summary>
        public static CadUnitSignature Signature(CadUnitRegion region, double toleranceMm)
        {
            var sig = new CadUnitSignature();
            EnsureBounds(region);
            double w = region.WidthMm, h = region.HeightMm;
            sig.ShortSideMm = Math.Min(w, h);
            sig.LongSideMm = Math.Max(w, h);
            sig.SegmentCount = region.Contents.Count;

            foreach (var g in region.Contents.GroupBy(s => s.Layer ?? "", StringComparer.OrdinalIgnoreCase))
                sig.PerLayer[g.Key] = Tuple.Create(g.Count(), g.Sum(s => s.PlanLength));
            return sig;
        }

        /// <summary>Geometry moved so the region's minimum corner is the origin.</summary>
        public static List<CadSegment> ToLocal(CadUnitRegion region)
        {
            EnsureBounds(region);
            var move = new CadUnitTransform(0, false, -region.Min.X, -region.Min.Y, 0);
            return Map(region.Contents, move);
        }

        private static List<CadSegment> Map(IList<CadSegment> segments, CadUnitTransform t)
        {
            var result = new List<CadSegment>(segments.Count);
            foreach (CadSegment s in segments)
                result.Add(new CadSegment(t.Apply(s.A), t.Apply(s.B), s.Layer, s.SourceKind,
                                          s.SourceIndex, s.SourceCurveId));
            return result;
        }

        /// <summary>
        /// How many of A's segments land on one of B's, and how many of B's are
        /// left over.
        ///
        /// Matching is ONE FOR ONE: a used segment of B cannot account for a
        /// second segment of A, or a region drawn with one long wall would
        /// "explain" a type with ten short ones.
        /// </summary>
        private static void Compare(IList<CadSegment> a, IList<CadSegment> b, double toleranceMm,
                                    out int matched, out int unexplained)
        {
            matched = 0;
            var used = new bool[b.Count];
            double cell = Math.Max(toleranceMm, 1e-6);

            // A GRID THAT IS PROBED, NOT A KEY THAT MUST AGREE.
            //
            // The first version hashed both ends to a quantized string and
            // required the strings to be equal. Two points 0.001 mm apart across
            // a bucket edge produce different strings, so a unit placed on a
            // hundredth of a millimetre offset - which is what a real drawing
            // does - failed to match for a reason that has nothing to do with
            // the building. The bucket is now only a way of finding candidates;
            // the DISTANCE decides.
            var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < b.Count; i++)
            {
                string key = Cell(Lower(b[i]), cell);
                List<int> bucket;
                if (!index.TryGetValue(key, out bucket)) index[key] = bucket = new List<int>();
                bucket.Add(i);
            }

            foreach (CadSegment s in a)
            {
                CadPoint anchor = Lower(s);
                int pick = -1;
                foreach (int i in Neighbourhood(index, anchor, cell))
                {
                    if (used[i]) continue;
                    if (!string.Equals(b[i].Layer ?? "", s.Layer ?? "", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!SameSegment(s, b[i], toleranceMm)) continue;
                    pick = i;
                    break;
                }
                if (pick < 0) continue;
                used[pick] = true;
                matched++;
            }

            unexplained = 0;
            for (int i = 0; i < used.Length; i++) if (!used[i]) unexplained++;
        }

        /// <summary>The same two ends within tolerance, in either direction.</summary>
        private static bool SameSegment(CadSegment x, CadSegment y, double toleranceMm) =>
            (x.A.PlanDistanceTo(y.A) <= toleranceMm && x.B.PlanDistanceTo(y.B) <= toleranceMm) ||
            (x.A.PlanDistanceTo(y.B) <= toleranceMm && x.B.PlanDistanceTo(y.A) <= toleranceMm);

        /// <summary>The end used to bucket a segment: whichever comes first, so direction does not matter.</summary>
        private static CadPoint Lower(CadSegment s)
        {
            if (s.A.X < s.B.X) return s.A;
            if (s.A.X > s.B.X) return s.B;
            return s.A.Y <= s.B.Y ? s.A : s.B;
        }

        private static string Cell(CadPoint p, double cell) =>
            string.Format(CultureInfo.InvariantCulture, "{0}|{1}",
                          (long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));

        private static IEnumerable<int> Neighbourhood(Dictionary<string, List<int>> index,
                                                      CadPoint p, double cell)
        {
            long cx = (long)Math.Floor(p.X / cell), cy = (long)Math.Floor(p.Y / cell);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                {
                    List<int> bucket;
                    string key = string.Format(CultureInfo.InvariantCulture, "{0}|{1}", cx + dx, cy + dy);
                    if (index.TryGetValue(key, out bucket))
                        foreach (int i in bucket) yield return i;
                }
        }

        private static void Bounds(IList<CadSegment> segments, out CadPoint min, out CadPoint max)
        {
            if (segments == null || segments.Count == 0)
            {
                min = new CadPoint(0, 0, 0); max = new CadPoint(0, 0, 0);
                return;
            }
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (CadSegment s in segments)
                foreach (CadPoint p in new[] { s.A, s.B })
                {
                    if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                    if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
                }
            min = new CadPoint(minX, minY, minZ);
            max = new CadPoint(maxX, maxY, maxZ);
        }

        /// <summary>
        /// The region's bounds, taken from its CONTENTS rather than its boundary.
        ///
        /// A boundary drawn generously - a rectangle a metre outside the walls -
        /// would give two identical apartments different bounding boxes and make
        /// them different types. What is inside is what is being compared.
        /// </summary>
        private static void EnsureBounds(CadUnitRegion region)
        {
            if (region == null) return;
            if (region.Max.X > region.Min.X || region.Max.Y > region.Min.Y) return;
            CadPoint min, max;
            Bounds(region.Contents, out min, out max);
            region.Min = min;
            region.Max = max;
        }

        /// <summary>
        /// Everything inside a closed boundary, for a caller that has a ring and
        /// a heap of segments and needs the ones that belong to it.
        ///
        /// A segment is inside when BOTH ends are. One end in and one out is a
        /// segment crossing the boundary - a corridor wall shared with the unit
        /// next door - and including it would make two adjacent units differ by
        /// which one claimed the party wall.
        /// </summary>
        public static List<CadSegment> Inside(IList<CadPoint> boundary, IList<CadSegment> segments)
        {
            var inside = new List<CadSegment>();
            if (boundary == null || boundary.Count < 3 || segments == null) return inside;
            foreach (CadSegment s in segments)
                if (CadTopologyRules.ContainsPoint(boundary, s.A) &&
                    CadTopologyRules.ContainsPoint(boundary, s.B))
                    inside.Add(s);
            return inside;
        }
    }
}
