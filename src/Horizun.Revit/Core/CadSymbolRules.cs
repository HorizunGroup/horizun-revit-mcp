// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// SYMBOLS: the same problem as repeated units, three orders of magnitude smaller.
//
// An electrical unit plan is mostly symbols. Outlets, switches, fixtures, data
// points, smoke detectors — hundreds of them, each drawn as a handful of arcs and
// lines, each meaning a family somebody has to choose. The existing interpreter
// reads them as POINT CLUSTERS: geometry within a radius becomes one candidate at
// its centroid. That places something in the right spot and says nothing about
// WHAT it is, so every cluster on a layer becomes the same family — and a drawing
// where switches and receptacles share a layer converts to a wall of receptacles.
//
// THE OBSERVATION THIS FILE IS BUILT ON. Two instances of one symbol are the same
// line work under a rigid transform, which is exactly what `CadUnitRules` already
// decides for apartments. An apartment is four thousand millimetres across and a
// receptacle is forty; nothing in the reasoning depends on the scale. So this file
// does one thing the unit recogniser cannot do for itself — turn loose geometry
// into candidate symbol footprints — and hands them to it.
//
// GROUPING IS BY CONNECTIVITY, NOT BY RADIUS. A symbol is drawn as line work that
// touches: the circle and the two ticks of a duplex receptacle share points. A
// radius groups whatever happens to be near, so two symbols 30 mm apart become
// one thing with a signature that matches nothing. Connectivity groups what was
// drawn together, which is what a draughtsman means by a symbol.
//
// AND IT IS BOUNDED, because connectivity does not know when to stop. A symbol
// touching its home run is connected to the whole circuit, and without a bound
// the first component would be the entire drawing. A group larger than the
// declared footprint is REJECTED as not-a-symbol and named, rather than being
// silently shrunk or silently kept.
//
// WHAT IT WILL NOT DO. It will not say what a symbol IS. The name of a symbol
// lives in the drawing's legend, which is text, and whether text is reachable is a
// property of the reader. What this produces is: these 84 marks are the same
// thing, here is where each one is and how it is turned. Naming that thing once is
// a decision a person makes in a sentence, and it then applies to all 84 — which
// is the entire point.
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
    /// <summary>Geometry that was grouped and then rejected, with the reason.</summary>
    public sealed class CadSymbolRejection
    {
        public string Reason;
        public int SegmentCount;
        public double WidthMm, HeightMm;
        public CadPoint At;

        public JObject ToJson() => new JObject
        {
            ["reason"] = Reason,
            ["segment_count"] = SegmentCount,
            ["width_mm"] = Math.Round(WidthMm, 3, MidpointRounding.AwayFromZero),
            ["height_mm"] = Math.Round(HeightMm, 3, MidpointRounding.AwayFromZero),
            ["at_mm"] = new JArray(Math.Round(At.X, 3, MidpointRounding.AwayFromZero),
                                   Math.Round(At.Y, 3, MidpointRounding.AwayFromZero))
        };
    }

    /// <summary>What one grouping pass produced, including everything it threw out.</summary>
    public sealed class CadSymbolGrouping
    {
        public List<CadUnitRegion> Groups = new List<CadUnitRegion>();
        public List<CadSymbolRejection> Rejected = new List<CadSymbolRejection>();

        /// <summary>True when the walk stopped at its bound, so the grouping is PARTIAL.</summary>
        public bool Truncated;

        public JObject SummaryJson() => new JObject
        {
            ["groups"] = Groups.Count,
            ["rejected"] = Rejected.Count,
            ["truncated"] = Truncated,
            ["rejected_by_reason"] = new JObject(Rejected
                .GroupBy(x => x.Reason, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => new JProperty(g.Key, g.Count()))),
            ["means"] = Truncated
                ? "THE GROUPING IS PARTIAL: the walk stopped at its bound, so geometry past it was never " +
                  "grouped and is absent from every count here."
                : "every piece of the geometry given was either grouped or rejected with a reason; nothing " +
                  "was dropped in silence"
        };
    }

    public static class CadSymbolRules
    {
        /// <summary>
        /// Turn loose line work into candidate symbol footprints, by CONNECTIVITY.
        ///
        /// <paramref name="maxFootprintMm"/> bounds what a symbol may be. It is the
        /// caller's number and there is no default here: a receptacle is forty
        /// millimetres across on one drawing and four hundred on another, and a
        /// number chosen in this file would be a number nobody can argue with in a
        /// review.
        /// </summary>
        public static CadSymbolGrouping Group(IList<CadSegment> segments, double snapToleranceMm,
                                              double maxFootprintMm, int minSegments = 2,
                                              int maxGroups = 20000)
        {
            var result = new CadSymbolGrouping();
            if (segments == null || segments.Count == 0) return result;
            if (maxFootprintMm <= 0) return result;

            CadNodeIndex nodes = CadTopologyRules.BuildNodes(segments, snapToleranceMm);

            // node key -> the segments that touch it, so the walk can step from a
            // segment to its neighbours without rescanning.
            var atNode = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var keysOf = new List<string[]>(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                CadSegment s = segments[i];
                if (s == null) { keysOf.Add(new string[0]); continue; }
                CadNode a = nodes.Find(s.A), b = nodes.Find(s.B);
                var keys = new List<string>(2);
                if (a != null) keys.Add(a.Key);
                if (b != null && (a == null || b.Key != a.Key)) keys.Add(b.Key);
                keysOf.Add(keys.ToArray());
                foreach (string k in keys)
                {
                    List<int> bucket;
                    if (!atNode.TryGetValue(k, out bucket)) atNode[k] = bucket = new List<int>();
                    bucket.Add(i);
                }
            }

            var seen = new bool[segments.Count];
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < segments.Count; i++)
            {
                if (seen[i] || segments[i] == null) continue;
                if (result.Groups.Count + result.Rejected.Count >= maxGroups)
                {
                    result.Truncated = true;
                    break;
                }

                // A BREADTH-FIRST WALK OVER WHAT TOUCHES, stopped by the footprint.
                //
                // Connectivity does not know when to stop: a symbol touching its
                // home run is connected to the whole circuit. So the walk carries
                // the running bounding box and abandons the component the moment it
                // outgrows the declared footprint - the component is REJECTED whole
                // rather than truncated, because half a circuit is not a symbol
                // either.
                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(i);
                seen[i] = true;
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                bool tooBig = false;

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    component.Add(current);
                    CadSegment s = segments[current];
                    foreach (CadPoint p in new[] { s.A, s.B })
                    {
                        if (p.X < minX) minX = p.X;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.Y > maxY) maxY = p.Y;
                    }

                    // THE COMPONENT IS ALWAYS DISCOVERED WHOLE, and the size only
                    // decides what to DO with it.
                    //
                    // The first version tested the footprint before expanding, and
                    // then "kept draining" a queue that was still empty: a component
                    // whose very FIRST segment already outgrows the bound - a 500 mm
                    // line against a 100 mm footprint - was never expanded at all,
                    // so none of its other segments were marked seen and each of
                    // them started a walk of its own. A twenty-segment run came back
                    // as twenty rejections of one segment each: precisely the "one
                    // rejection per line" this loop says it prevents, in a drawing
                    // where every line is longer than a symbol.
                    foreach (string k in keysOf[current])
                    {
                        List<int> bucket;
                        if (!atNode.TryGetValue(k, out bucket)) continue;
                        foreach (int n in bucket) if (!seen[n]) { seen[n] = true; queue.Enqueue(n); }
                    }

                    // Rejected WHOLE rather than truncated, so the reported box is
                    // the box of the thing being rejected. Once it is too big it
                    // stays too big; the walk carries on only to finish the
                    // component and mark it seen.
                    if (!tooBig && (maxX - minX > maxFootprintMm || maxY - minY > maxFootprintMm))
                        tooBig = true;
                }

                var centre = new CadPoint(
                    minX == double.MaxValue ? 0 : (minX + maxX) / 2,
                    minY == double.MaxValue ? 0 : (minY + maxY) / 2);

                if (tooBig)
                {
                    result.Rejected.Add(new CadSymbolRejection
                    {
                        Reason = "larger_than_the_declared_footprint",
                        SegmentCount = component.Count,
                        WidthMm = maxX - minX,
                        HeightMm = maxY - minY,
                        At = centre
                    });
                    continue;
                }

                if (component.Count < minSegments)
                {
                    result.Rejected.Add(new CadSymbolRejection
                    {
                        Reason = "fewer_segments_than_a_symbol_needs",
                        SegmentCount = component.Count,
                        WidthMm = maxX - minX,
                        HeightMm = maxY - minY,
                        At = centre
                    });
                    continue;
                }

                // The id is the group's PLACE, so two runs of this over the same
                // drawing name the same group the same way - an ordinal would rename
                // everything the moment one segment moved. Two groups that quantize
                // to one key are made distinct rather than allowed to collide,
                // because an id that names two things names neither.
                string id = "sym@" + centre.Key(snapToleranceMm);
                string unique = id;
                int suffix = 1;
                while (!usedIds.Add(unique))
                    unique = id + "#" + (++suffix).ToString(CultureInfo.InvariantCulture);

                result.Groups.Add(new CadUnitRegion
                {
                    Id = unique,
                    Contents = component.Select(ix => segments[ix]).ToList()
                });
            }

            return result;
        }

        /// <summary>
        /// The whole reading: group, then recognise, using the SAME machinery that
        /// decides whether two apartments are the same layout.
        ///
        /// Nothing about that reasoning depends on scale, and having one
        /// implementation means a fix to either reading is a fix to both.
        /// </summary>
        public static CadSymbolReading Read(IList<CadSegment> segments, double snapToleranceMm,
                                            double maxFootprintMm, double matchToleranceMm,
                                            int minSegments = 2)
        {
            CadSymbolGrouping grouping = Group(segments, snapToleranceMm, maxFootprintMm, minSegments);
            CadUnitReading recognition = CadUnitRules.Recognise(grouping.Groups, matchToleranceMm);
            return new CadSymbolReading { Grouping = grouping, Recognition = recognition };
        }

        /// <summary>The prefix a group's id carries when it came from the legend.</summary>
        public const string LegendPrefix = "legend/";

        /// <summary>
        /// READ A DRAWING AGAINST ITS LEGEND.
        ///
        /// The difficulty with symbols is that what they MEAN is text, and text is
        /// not reachable through this reader. But a permit set carries a legend
        /// sheet where each symbol is drawn once beside the words that name it. A
        /// person reads that sheet in two minutes; what they cannot do in two
        /// minutes is find every occurrence of each mark across a floor plan.
        ///
        /// So both are grouped and recognised TOGETHER: a legend mark and a plan
        /// mark that are the same line work land in one type, and naming that type
        /// becomes reading one legend entry.
        ///
        /// RECOGNITION IS RIGID, NOT SIMILARITY. A legend drawn at a different
        /// scale from the plan matches nothing, and the reply says how many types
        /// had no legend occurrence rather than leaving that to be discovered.
        /// </summary>
        public static CadSymbolReading ReadAgainstLegend(IList<CadSegment> drawing,
                                                         IList<CadSegment> legend,
                                                         double snapToleranceMm, double maxFootprintMm,
                                                         double matchToleranceMm, int minSegments = 2)
        {
            CadSymbolGrouping own = Group(drawing, snapToleranceMm, maxFootprintMm, minSegments);
            CadSymbolGrouping theirs = Group(legend, snapToleranceMm, maxFootprintMm, minSegments);

            // The legend's groups are marked in their ids, so an occurrence can be
            // told apart afterwards without carrying a parallel list that could
            // drift out of step with the reading.
            foreach (CadUnitRegion g in theirs.Groups) g.Id = LegendPrefix + g.Id;

            var all = new List<CadUnitRegion>(own.Groups.Count + theirs.Groups.Count);
            all.AddRange(own.Groups);
            all.AddRange(theirs.Groups);

            // SINGLETONS BECOME TYPES HERE, and only here. A legend mark that
            // appears nowhere on this drawing is still worth naming - it is a
            // symbol the sheet defines and this drawing does not use, which is
            // information - and a plan mark with no legend entry is the finding
            // this whole cross-reference exists to surface.
            CadUnitReading recognition = CadUnitRules.Recognise(all, matchToleranceMm, true);

            var combined = new CadSymbolGrouping { Truncated = own.Truncated || theirs.Truncated };
            combined.Groups.AddRange(all);
            combined.Rejected.AddRange(own.Rejected);
            combined.Rejected.AddRange(theirs.Rejected);

            return new CadSymbolReading
            {
                Grouping = combined,
                Recognition = recognition,
                LegendWasRead = true
            };
        }
    }

    /// <summary>A grouping and the recognition over it, reported together.</summary>
    public sealed class CadSymbolReading
    {
        public CadSymbolGrouping Grouping;
        public CadUnitReading Recognition;

        /// <summary>True when a legend drawing took part in the recognition.</summary>
        public bool LegendWasRead;

        private static bool FromLegend(string regionId) =>
            regionId != null && regionId.StartsWith(CadSymbolRules.LegendPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Every occurrence as the typed creation row it would become, in the
        /// drawing's own millimetres, with the family type left for a person.
        /// </summary>
        public JArray Placements()
        {
            var rows = new JArray();
            if (Recognition == null) return rows;

            var typeById = new Dictionary<string, CadUnitType>(StringComparer.Ordinal);
            foreach (CadUnitType t in Recognition.Types) typeById[t.Id] = t;

            foreach (CadUnitOccurrence o in Recognition.Occurrences)
            {
                CadUnitType type;
                if (!typeById.TryGetValue(o.UnitTypeId, out type)) continue;

                // THE CENTRE OF THE SYMBOL WHERE IT SITS IN THE DRAWING. The type's
                // local frame has its minimum corner at the origin, so the centre
                // of its bounding box, taken through the occurrence's transform, is
                // where this instance goes. Taking the transform's offset alone
                // would place every symbol at its lower-left corner.
                var local = new CadPoint(type.WidthMm / 2.0, type.HeightMm / 2.0, 0);
                CadPoint at = o.Transform.Apply(local);

                bool placeable = !o.Transform.MirrorX;
                var row = new JObject
                {
                    ["symbol_type"] = o.UnitTypeId,
                    ["group"] = o.RegionId,
                    ["placeable"] = placeable,
                    ["row"] = new JObject
                    {
                        ["kind"] = "family_instance",
                        ["coordinate_mode"] = "absolute",
                        ["point"] = new JArray(Math.Round(at.X, 4, MidpointRounding.AwayFromZero),
                                               Math.Round(at.Y, 4, MidpointRounding.AwayFromZero),
                                               Math.Round(at.Z, 4, MidpointRounding.AwayFromZero)),
                        ["rotation_degrees"] = o.Transform.QuarterTurns * 90,
                        ["type_name"] = ""
                    }
                };
                if (!placeable)
                    row["why_not"] =
                        "this occurrence is MIRRORED. A mirror is not a rotation of a family instance: it " +
                        "needs a flipped instance or a different family, and placing it rotated puts the " +
                        "symbol's face the wrong way round. On the drawing the two look identical.";
                if (o.OrientationAmbiguous)
                    row["orientation_ambiguous"] =
                        "this symbol is symmetric, so " +
                        o.ExactFitCount.ToString(CultureInfo.InvariantCulture) + " of the eight rigid maps " +
                        "reproduce it and the rotation above is one correct answer among several.";
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// Which symbol types the legend accounts for, and which it does not.
        ///
        /// A type with a legend occurrence can be named by reading one entry. A
        /// type WITHOUT one is either a mark the legend does not define, or the
        /// legend drawn at a different scale - recognition is rigid, not
        /// similarity, so those look the same from here and both are worth saying.
        /// </summary>
        public JObject LegendCrossReference()
        {
            if (!LegendWasRead || Recognition == null)
                return new JObject
                {
                    ["read"] = false,
                    ["means"] = "no legend drawing was given, so every symbol type here has to be named " +
                                "from the drawing itself"
                };

            int inBoth = 0, legendOnly = 0, drawingOnly = 0;
            var rows = new JArray();
            foreach (CadUnitType t in Recognition.Types)
            {
                int fromLegend = 0, fromDrawing = 0;
                foreach (CadUnitOccurrence o in Recognition.Occurrences)
                {
                    if (!string.Equals(o.UnitTypeId, t.Id, StringComparison.Ordinal)) continue;
                    if (FromLegend(o.RegionId)) fromLegend++; else fromDrawing++;
                }
                if (fromLegend > 0 && fromDrawing > 0) inBoth++;
                else if (fromLegend > 0) legendOnly++;
                else drawingOnly++;

                rows.Add(new JObject
                {
                    ["symbol_type"] = t.Id,
                    ["in_legend"] = fromLegend,
                    ["in_drawing"] = fromDrawing,
                    ["nameable_from_the_legend"] = fromLegend > 0,
                    ["means"] = fromLegend > 0 && fromDrawing > 0
                        ? "this mark is on the legend sheet AND on the drawing. Read its name off the " +
                          "legend once and it applies to all " +
                          fromDrawing.ToString(CultureInfo.InvariantCulture) + " occurrences here."
                        : fromLegend > 0
                            ? "the legend defines this symbol and this drawing does not use it"
                            : "this mark is on the drawing and NOT on the legend. Either the legend does " +
                              "not define it, or the legend is drawn at a different scale - recognition is " +
                              "rigid, not similarity, and those two look the same from here."
                });
            }

            return new JObject
            {
                ["read"] = true,
                ["types_in_both"] = inBoth,
                ["types_only_in_the_legend"] = legendOnly,
                ["types_only_in_the_drawing"] = drawingOnly,
                ["types"] = rows,
                // A VACUOUS TRUTH IS NOT A RESULT. With no types at all - every group
                // rejected by the footprint, or a layer filter that excluded the
                // symbols - drawingOnly is zero and "every type appears on the
                // legend" is true of nothing, which reads as complete coverage.
                ["means"] = Recognition.Types.Count == 0
                    ? "NO SYMBOL TYPE WAS FOUND AT ALL, on the drawing or on the legend, so there is nothing " +
                      "to cross-reference. Check the rejected groups: a footprint too small rejects every " +
                      "symbol, and a layer filter that excluded them leaves nothing to group."
                    : drawingOnly == 0
                    ? "every symbol type on this drawing appears on the legend, so every one of them can be " +
                      "named by reading one entry."
                    : drawingOnly.ToString(CultureInfo.InvariantCulture) + " symbol type(s) on this drawing " +
                      "have no legend occurrence. That is the finding this cross-reference exists for: " +
                      "either the legend does not define them, or it is drawn at a different scale and " +
                      "matched nothing at all - if types_in_both is zero, it is the scale."
            };
        }

        public JObject ToJson()
        {
            var byType = new JObject();
            if (Recognition != null)
                foreach (var g in Recognition.Occurrences.GroupBy(o => o.UnitTypeId, StringComparer.Ordinal)
                                             .OrderByDescending(g => g.Count()))
                    byType[g.Key] = g.Count();

            int named = Recognition == null ? 0 : Recognition.Types.Count;
            return new JObject
            {
                ["grouping"] = Grouping == null ? (JToken)JValue.CreateNull() : Grouping.SummaryJson(),
                ["rejected"] = Grouping == null
                    ? new JArray()
                    : new JArray(Grouping.Rejected.Take(500).Select(x => (JToken)x.ToJson())),
                ["symbol_types"] = Recognition == null
                    ? (JToken)JValue.CreateNull()
                    : new JArray(Recognition.Types.Select(t => (JToken)t.ToJson())),
                ["occurrences_by_type"] = byType,
                ["occurrences"] = Recognition == null
                    ? new JArray()
                    : new JArray(Recognition.Occurrences.Select(o => (JToken)o.ToJson())),
                ["unrecognised"] = Recognition == null
                    ? new JArray()
                    : new JArray(Recognition.UnrecognisedRegions.Select(x => (JToken)x)),
                ["placements"] = Placements(),
                ["placements_mean"] =
                    "each occurrence as the family_instance row horizun_create_elements takes, with " +
                    "type_name left EMPTY. Fill in one type_name per symbol type and the rows are sendable - " +
                    "which is the difference between a tool that reduces the work and one that relocates it. " +
                    "A row marked placeable:false is MIRRORED: a rigid map with a mirror is not a rotation " +
                    "of a family instance, and placing it rotated puts its face the wrong way round, which " +
                    "looks identical on the drawing and is not identical in the model.",
                ["type_count"] = named,
                ["legend"] = LegendCrossReference(),
                ["means"] =
                    "each symbol TYPE is a group of marks that are the same line work under a rigid " +
                    "transform. None of them is NAMED: what a symbol means lives in the drawing's legend, " +
                    "which is text, and whether text is reachable is a property of the reader - " +
                    "horizun_cad_extract says which. Naming a type is one sentence from a person, and it " +
                    "then applies to every occurrence of it, which is the whole point of grouping them.",
                ["unrecognised_means"] =
                    "a group that matched no other group. On a real drawing these are the one-offs - a " +
                    "panel, a riser mark, a note symbol - and occasionally a symbol drawn slightly " +
                    "differently the second time, which is worth knowing about."
            };
        }
    }
}
