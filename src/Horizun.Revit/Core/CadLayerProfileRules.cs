// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT IS ON THIS LAYER, AND WHAT WOULD READ IT.
//
// Writing the first requirement set for an unfamiliar drawing is guesswork done
// blind: you pick a layer, guess a geometry source, guess a thickness band, run
// the conversion, and find out. Every wrong guess costs a round trip through a
// model.
//
// This answers the measurable half of that question and REFUSES the other half.
//
// WHAT IT ANSWERS: for each layer, what each geometry source would actually find
// there, measured by running THE SAME READER the conversion runs. Not a
// heuristic that resembles it - the reader itself, so a count here is a count
// there. And the ranges it observed: the thicknesses the double lines actually
// sit at, the areas the rings actually enclose.
//
// WHAT IT REFUSES: to say what a layer MEANS. Nothing here maps A-WALL to walls,
// and nothing ever should - a bridge that shipped one organisation's layer
// convention would quietly convert the next organisation's drawing wrong, and
// the model would look entirely plausible. The drawing says where the geometry
// is; it does not say what the building is, and a person supplies that.
//
// So the output is measurements and a SKELETON with every `produces` left empty.
// Filling those in is the decision this file exists to inform and not to make.
// It lives in Core and carries no `using Autodesk.*`, for the same reason every
// other decision here does: what a reader would find on a layer is arithmetic
// over geometry, and it should be arguable at a desk rather than only against a
// model somebody has to build first.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class CadLayerProfiler
    {
        /// <summary>
        /// Every source a rule may declare. Each is tried against every layer,
        /// because "which of these reads this layer" is exactly the question a
        /// person is guessing at.
        /// </summary>
        private static readonly string[] Sources =
        {
            "double_lines", "double_arcs", "closed_loops", "single_lines", "point_clusters"
        };

        /// <summary>
        /// Measure each layer against each geometry source.
        ///
        /// <paramref name="maxLayers"/> bounds the work: a site plan can carry
        /// hundreds of layers and this runs the reader once per layer per source.
        /// What is dropped is NAMED in the reply rather than silently trimmed -
        /// a profile that quietly stopped at fifty layers reads as a drawing that
        /// has fifty.
        /// </summary>
        public static JObject Profile(IList<CadSegment> segments, string declaredUnits, int maxLayers)
        {
            var byLayer = new Dictionary<string, List<CadSegment>>(StringComparer.OrdinalIgnoreCase);
            int unlayered = 0;
            foreach (CadSegment s in segments ?? new List<CadSegment>())
            {
                if (string.IsNullOrEmpty(s?.Layer)) { unlayered++; continue; }
                List<CadSegment> bucket;
                if (!byLayer.TryGetValue(s.Layer, out bucket)) byLayer[s.Layer] = bucket = new List<CadSegment>();
                bucket.Add(s);
            }

            List<KeyValuePair<string, List<CadSegment>>> ordered = byLayer
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var profiled = new JArray();
            var skeleton = new JArray();
            int taken = 0;
            foreach (var kv in ordered)
            {
                if (taken >= maxLayers) break;
                taken++;
                JObject row = ProfileOne(kv.Key, kv.Value, declaredUnits);
                profiled.Add(row);

                var best = row["best_reading"] as JObject;
                if (best != null) skeleton.Add(SkeletonRule(kv.Key, best, taken));
            }

            return new JObject
            {
                ["layers_profiled"] = taken,
                ["layers_in_drawing"] = byLayer.Count,
                ["layers_not_profiled"] = Math.Max(0, byLayer.Count - taken),
                ["layers_not_profiled_means"] = byLayer.Count > taken
                    ? "the busiest " + taken + " layers were measured and " + (byLayer.Count - taken) +
                      " were not. Raise max_layers to see them - this is a bound on the work, not a " +
                      "judgement that the rest are empty."
                    : null,
                ["segments_without_a_layer"] = unlayered,
                ["layers"] = profiled,
                ["requirement_set_skeleton"] = new JObject
                {
                    ["schema"] = "horizun.cad-requirements/1",
                    ["requirement_set"] = new JObject
                    {
                        ["id"] = "(you name it)",
                        ["version"] = "0.1.0",
                        ["title"] = "(you title it)"
                    },
                    ["source"] = new JObject { ["units"] = declaredUnits },
                    ["tolerances"] = new JObject
                    {
                        ["point_mm"] = 1.0,
                        ["gap_mm"] = 25.0,
                        ["angle_degrees"] = 2.0,
                        ["arc_sagitta_mm"] = 5.0
                    },
                    ["rules"] = skeleton
                },
                ["you_must_supply"] = new JArray("produces", "category", "level", "family_type"),
                ["refuses_to_say"] =
                    "WHAT EACH LAYER MEANS. Every `produces` above is null and stays null: this bridge carries no " +
                    "organisation's layer convention, and one that did would convert the next organisation's " +
                    "drawing wrong into a model that looked entirely plausible. A layer name is a string in " +
                    "somebody else's file. What is measured here is what each reader FOUND, by running the same " +
                    "reader the conversion runs - the rest is yours.",
                ["means"] =
                    "candidates is what a rule with that geometry source would produce on that layer, counted by " +
                    "the reader itself rather than estimated. The ranges are what it MEASURED, so a thickness " +
                    "band you write from them cannot exclude the runs you are looking at."
            };
        }

        private static JObject ProfileOne(string layer, List<CadSegment> segments, string units)
        {
            double minX = segments.Min(s => Math.Min(s.A.X, s.B.X));
            double maxX = segments.Max(s => Math.Max(s.A.X, s.B.X));
            double minY = segments.Min(s => Math.Min(s.A.Y, s.B.Y));
            double maxY = segments.Max(s => Math.Max(s.A.Y, s.B.Y));

            // WHICH READING EXPLAINS THE MOST OF THIS LAYER, WITH THE FEWEST PIECES.
            //
            // Not the one with the most candidates: single_lines reads every
            // segment on every layer as its own candidate, so counting winners
            // makes it win everywhere. A ring of four segments came back as four
            // lines rather than one loop, and the skeleton then pre-filled a
            // length band for something that is a slab.
            //
            // Coverage first - how much of the layer the reading consumed - and
            // the fewest candidates for the same coverage, because four lines and
            // one loop over the same four segments are the same drawing read at
            // two levels and the structured one is the one worth offering. It is
            // a RANKING OVER MEASUREMENTS and not a claim about meaning; what the
            // layer IS remains unanswered here on purpose.
            var readings = new JArray();
            JObject best = null;
            double bestCoverage = 0;
            int bestPieces = int.MaxValue;
            foreach (string source in Sources)
            {
                JObject reading = Read(layer, segments, units, source);
                readings.Add(reading);

                int found = reading.Value<int?>("candidates") ?? 0;
                if (found <= 0) continue;
                int consumed = reading.Value<int?>("segments_consumed") ?? 0;
                double coverage = segments.Count == 0 ? 0 : (double)consumed / segments.Count;
                reading["covers_layer"] = Round(coverage);

                bool better = coverage > bestCoverage + 1e-9 ||
                              (Math.Abs(coverage - bestCoverage) <= 1e-9 && found < bestPieces);
                if (!better) continue;
                bestCoverage = coverage; bestPieces = found; best = reading;
            }
            if (best != null)
                best["chosen_because"] =
                    "it consumed " + Round(bestCoverage * 100) + "% of this layer's segments in " + bestPieces +
                    " candidate(s) - the most of the layer, in the fewest pieces. That is a ranking over what " +
                    "was MEASURED and not a statement about what the layer is.";

            var row = new JObject
            {
                ["layer"] = layer,
                ["segments"] = segments.Count,
                ["straight"] = segments.Count(s => s.SourceKind == CadCurveKind.Line ||
                                                   s.SourceKind == CadCurveKind.Polyline),
                ["from_curves"] = segments.Count(s => s.SourceKind == CadCurveKind.Arc ||
                                                      s.SourceKind == CadCurveKind.Spline),
                ["extent_mm"] = new JObject
                {
                    ["min"] = new JArray(Round(minX), Round(minY)),
                    ["max"] = new JArray(Round(maxX), Round(maxY))
                },
                ["would_read"] = readings
            };
            if (best != null) row["best_reading"] = best;
            else row["best_reading_means"] = "no geometry source produced a single candidate on this layer, which " +
                                             "means every reader refused its geometry or threw on it.";
            row["structure_found"] = readings.OfType<JObject>().Any(
                r => (r.Value<int?>("candidates") ?? 0) > 0 &&
                     ((string)r["from"] == "double_lines" || (string)r["from"] == "double_arcs" ||
                      (string)r["from"] == "closed_loops"));
            row["structure_means"] = "false when nothing here reads as a run, a curved run or a ring. Hatching, " +
                                     "text underlays and annotation land here: their marks are still counted, " +
                                     "because they exist, and single_lines or point_clusters will always claim " +
                                     "something. What says the layer has no building on it is this being false.";
            return row;
        }

        /// <summary>
        /// Run ONE geometry source over ONE layer, through the real reader.
        ///
        /// The bounds are deliberately as wide as the schema allows, because the
        /// point is to find out what is there rather than to confirm a guess. The
        /// narrow band belongs in the rule a person writes afterwards, informed by
        /// the range this reports.
        /// </summary>
        private static JObject Read(string layer, List<CadSegment> segments, string units, string source)
        {
            var reading = new JObject { ["from"] = source };
            try
            {
                CadRequirementSet probe = ProbeSet(layer, units, source);
                CadInterpretation read = CadInterpretationRules.Interpret(segments, probe, "profile");
                List<CadCandidate> found = read.Candidates ?? new List<CadCandidate>();

                reading["candidates"] = found.Count;
                reading["segments_consumed"] = read.SegmentsConsumed;
                if (found.Count == 0) return reading;

                Range(reading, "thickness_mm", found.Where(c => c.ThicknessMm.HasValue).Select(c => c.ThicknessMm.Value));
                Range(reading, "area_mm2", found.Where(c => c.AreaMm2.HasValue).Select(c => c.AreaMm2.Value));
                Range(reading, "length_mm", found.Where(c => c.Geometry != null && c.Geometry.Count >= 2)
                                                 .Select(c => c.Geometry[0].PlanDistanceTo(c.Geometry[c.Geometry.Count - 1])));
                Range(reading, "confidence", found.Select(c => c.Confidence));
            }
            catch (Exception e)
            {
                // A reader that threw is not a layer with nothing on it, and the
                // difference matters: one is a measurement and the other is a gap
                // in what this can tell you.
                reading["candidates"] = 0;
                reading["unreadable"] = true;
                reading["why"] = e.Message;
            }
            return reading;
        }

        private static void Range(JObject into, string name, IEnumerable<double> values)
        {
            List<double> all = values?.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList()
                               ?? new List<double>();
            if (all.Count == 0) return;
            into[name] = new JObject
            {
                ["min"] = Round(all.Min()),
                ["max"] = Round(all.Max()),
                ["measured_on"] = all.Count
            };
        }

        /// <summary>
        /// A rule that reads and produces NOTHING. produces is required by the
        /// schema, so the probe names a kind - and it is deliberately the most
        /// harmless one there is, because this set is never applied to anything.
        /// </summary>
        private static CadRequirementSet ProbeSet(string layer, string units, string source)
        {
            var geometry = new JObject { ["from"] = source };
            switch (source)
            {
                case "double_lines":
                case "double_arcs":
                    geometry["min_thickness_mm"] = 20.0;
                    geometry["max_thickness_mm"] = 2000.0;
                    geometry["min_overlap_fraction"] = 0.3;
                    break;
                case "closed_loops":
                    geometry["min_area_mm2"] = 1000.0;
                    break;
                case "single_lines":
                    geometry["min_length_mm"] = 1.0;
                    break;
                case "point_clusters":
                    geometry["cluster_radius_mm"] = 1200.0;
                    break;
            }

            var doc = new JObject
            {
                ["schema"] = "horizun.cad-requirements/1",
                ["requirement_set"] = new JObject
                {
                    ["id"] = "horizun.profile",
                    ["version"] = "1.0.0",
                    ["title"] = "Measurement only - never applied"
                },
                // MILLIMETRES, ALWAYS, and not the link's declaration.
                //
                // The harvest is already in millimetres and the interpretation
                // never converts. Passing the link's own word through meant that a
                // drawing declaring 'default' or 'custom' - which the loader
                // refuses - made every reader throw, so every layer came back
                // unreadable and the skeleton came back EMPTY, blaming the
                // geometry for a units string.
                ["source"] = new JObject { ["units"] = "millimeter" },
                ["tolerances"] = new JObject
                {
                    ["point_mm"] = 1.0,
                    ["gap_mm"] = 25.0,
                    ["angle_degrees"] = 2.0,
                    ["arc_sagitta_mm"] = 5.0
                },
                ["rules"] = new JArray(new JObject
                {
                    ["id"] = "probe",
                    ["precedence"] = 10,
                    // THE LAYER NAME IS A LITERAL, and layers is a GLOB. A name
                    // carrying * or ? would silently profile a different set of
                    // layers than the one this row is about, so the two
                    // metacharacters are escaped by the only escape the matcher
                    // has: a single-character wildcard matches them exactly once.
                    ["layers"] = new JArray(layer.Replace("*", "?").Replace("[", "?")),
                    ["produces"] = "generic_model",
                    ["geometry"] = geometry
                })
            };
            return CadRequirementSet.Load(doc);
        }

        /// <summary>
        /// One rule, pre-filled with everything MEASURED and nothing decided.
        /// The bands come from what was actually found and are widened by a tenth
        /// at each end, so a run that sits exactly on the boundary is not excluded
        /// by the number this reported about it.
        /// </summary>
        private static JObject SkeletonRule(string layer, JObject best, int precedence)
        {
            string source = (string)best["from"];
            var geometry = new JObject { ["from"] = source };

            var thickness = best["thickness_mm"] as JObject;
            if (thickness != null && (source == "double_lines" || source == "double_arcs"))
            {
                double lo = thickness.Value<double>("min"), hi = thickness.Value<double>("max");
                geometry["min_thickness_mm"] = Round(Math.Max(0, lo - Math.Max(1, (hi - lo) * 0.1)));
                geometry["max_thickness_mm"] = Round(hi + Math.Max(1, (hi - lo) * 0.1));
                // THE FRACTION THE PROFILE ACTUALLY MEASURED WITH. Emitting a
                // tighter one hands back a rule that finds fewer runs than the
                // number printed beside it - and reports the layer as empty when
                // every pair sat between the two figures.
                geometry["min_overlap_fraction"] = 0.3;
            }
            var area = best["area_mm2"] as JObject;
            if (area != null && source == "closed_loops")
                geometry["min_area_mm2"] = Round(Math.Max(1, area.Value<double>("min") * 0.9));
            var length = best["length_mm"] as JObject;
            if (length != null && source == "single_lines")
                geometry["min_length_mm"] = Round(Math.Max(1, length.Value<double>("min") * 0.9));
            if (source == "point_clusters")
                geometry["cluster_radius_mm"] = 1200.0;

            return new JObject
            {
                ["id"] = "rule-" + precedence.ToString(CultureInfo.InvariantCulture),
                ["precedence"] = precedence * 10,
                ["layers"] = new JArray(layer),
                ["produces"] = null,
                ["category"] = null,
                ["geometry"] = geometry,
                // AN UNDERSCORE KEY, which the loader now skips by name. The
                // skeleton is meant to be edited and used, and a note the loader
                // refuses makes the bridge hand back a document it will not read.
                ["_measured"] = "this layer yielded " + (best.Value<int?>("candidates") ?? 0) + " candidate(s) " +
                                "through " + source + ". produces and category are yours: nothing in the drawing " +
                                "says what this layer is."
            };
        }

        private static double Round(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
    }
}
