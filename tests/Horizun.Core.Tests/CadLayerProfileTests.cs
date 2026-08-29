// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE PROFILER MEASURES AND REFUSES.
//
// Writing the first requirement set for an unfamiliar drawing is guesswork done
// blind, and the profiler exists to remove the measurable half of it: what would
// each geometry source actually find on each layer.
//
// The half it must NEVER remove is what a layer MEANS. A bridge that shipped one
// organisation's layer convention would convert the next organisation's drawing
// wrong into a model that looked entirely plausible, and nobody would find out
// until somebody stood in the building. So every `produces` it emits is null, and
// these tests fail the moment one is not.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;

using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadLayerProfileTests
    {
        /// <summary>A pair of parallel lines 200 mm apart - what a wall looks like in plan.</summary>
        private static IEnumerable<CadSegment> DoubleLine(string layer, double x0, double x1, double y)
        {
            yield return new CadSegment(new CadPoint(x0, y - 100), new CadPoint(x1, y - 100), layer);
            yield return new CadSegment(new CadPoint(x0, y + 100), new CadPoint(x1, y + 100), layer);
        }

        /// <summary>A closed rectangle - what a slab or a room outline looks like.</summary>
        private static IEnumerable<CadSegment> Ring(string layer, double x0, double y0, double x1, double y1)
        {
            yield return new CadSegment(new CadPoint(x0, y0), new CadPoint(x1, y0), layer);
            yield return new CadSegment(new CadPoint(x1, y0), new CadPoint(x1, y1), layer);
            yield return new CadSegment(new CadPoint(x1, y1), new CadPoint(x0, y1), layer);
            yield return new CadSegment(new CadPoint(x0, y1), new CadPoint(x0, y0), layer);
        }

        private static JObject Profile(params IEnumerable<CadSegment>[] parts)
        {
            var all = new List<CadSegment>();
            foreach (var part in parts) all.AddRange(part);
            return CadLayerProfiler.Profile(all, "millimeter", 40);
        }

        private static JObject Layer(JObject profile, string name)
        {
            return ((JArray)profile["layers"]).OfType<JObject>()
                .FirstOrDefault(l => (string)l["layer"] == name);
        }

        private static JObject Reading(JObject layer, string source)
        {
            return ((JArray)layer["would_read"]).OfType<JObject>()
                .FirstOrDefault(r => (string)r["from"] == source);
        }

        [Fact]
        public void It_finds_the_runs_a_double_line_rule_would_find()
        {
            JObject profile = Profile(DoubleLine("SOME-LAYER", 0, 6000, 0));
            JObject layer = Layer(profile, "SOME-LAYER");

            Assert.NotNull(layer);
            Assert.Equal(1, (int)Reading(layer, "double_lines")["candidates"]);
        }

        [Fact]
        public void And_reports_the_thickness_it_MEASURED_rather_than_one_it_assumed()
        {
            // The number a person writes a band from has to come from the drawing,
            // or the band they write excludes the runs they are looking at.
            JObject profile = Profile(DoubleLine("SOME-LAYER", 0, 6000, 0));
            JObject thickness = (JObject)Reading(Layer(profile, "SOME-LAYER"), "double_lines")["thickness_mm"];

            Assert.NotNull(thickness);
            Assert.Equal(200.0, (double)thickness["min"], 1);
            Assert.Equal(200.0, (double)thickness["max"], 1);
        }

        [Fact]
        public void A_ring_reads_as_a_ring_and_not_as_a_pair_of_lines()
        {
            JObject layer = Layer(Profile(Ring("RINGS", 0, 0, 4000, 3000)), "RINGS");

            Assert.Equal(1, (int)Reading(layer, "closed_loops")["candidates"]);
            Assert.Equal("closed_loops", (string)layer["best_reading"]["from"]);
        }

        [Fact]
        public void EVERY_source_is_tried_on_every_layer_because_that_is_the_question()
        {
            JObject layer = Layer(Profile(DoubleLine("SOME-LAYER", 0, 6000, 0)), "SOME-LAYER");
            foreach (string source in new[] { "double_lines", "double_arcs", "closed_loops",
                                              "single_lines", "point_clusters" })
                Assert.NotNull(Reading(layer, source));
        }

        [Fact]
        public void It_says_NOTHING_about_what_a_layer_means()
        {
            // The one property this file exists to hold. A skeleton that guessed
            // even once would be a convention compiled in, and the next drawing
            // that used that layer name for something else would convert wrong
            // and verify happily.
            JObject profile = Profile(DoubleLine("A-WALL", 0, 6000, 0), Ring("A-FLOR", 0, 0, 4000, 3000));

            var rules = (JArray)profile["requirement_set_skeleton"]["rules"];
            Assert.NotEmpty(rules);
            foreach (JObject rule in rules.OfType<JObject>())
            {
                Assert.Equal(JTokenType.Null, rule["produces"].Type);
                Assert.Equal(JTokenType.Null, rule["category"].Type);
            }
            Assert.Contains("produces", ((JArray)profile["you_must_supply"]).Select(t => (string)t));
        }

        [Fact]
        public void The_skeleton_it_emits_is_a_requirement_set_the_loader_REFUSES_until_filled_in()
        {
            // A skeleton that happened to load would be one somebody could apply
            // without ever deciding what the layers are.
            JObject profile = Profile(DoubleLine("A-WALL", 0, 6000, 0));
            var skeleton = (JObject)profile["requirement_set_skeleton"];

            CadRequirementSetException e = Assert.Throws<CadRequirementSetException>(
                () => CadRequirementSet.Load(skeleton));

            // AND FOR THE RIGHT REASON. It used to be refused for carrying a
            // `_measured` note the loader did not know - a refusal about the
            // annotation rather than about the decision nobody had made, on a
            // document this bridge had just produced. A test that only asserted
            // "it throws" was green throughout.
            Assert.Contains("must say what it produces", e.Message);
        }

        [Fact]
        public void The_note_it_writes_beside_each_rule_is_one_the_loader_ACCEPTS()
        {
            // The skeleton is meant to be edited and used. A key the loader refuses
            // means the bridge hands back a document it will not read, and the
            // refusal names no fix for it.
            JObject profile = Profile(DoubleLine("A-WALL", 0, 6000, 0));
            var rule = (JObject)((JArray)profile["requirement_set_skeleton"]["rules"])[0];
            Assert.NotNull(rule["_measured"]);

            rule["produces"] = "wall";
            rule["category"] = "OST_Walls";
            CadRequirementSet loaded = CadRequirementSet.Load((JObject)profile["requirement_set_skeleton"]);

            Assert.Single(loaded.Rules);
        }

        [Fact]
        public void The_band_it_emits_is_not_TIGHTER_than_the_one_it_measured_with()
        {
            // It used to hand back min_overlap_fraction 0.6 having counted with
            // 0.3, so the rule it gave you found fewer runs than the number
            // printed beside it - and none at all when every pair sat between the
            // two figures.
            JObject profile = Profile(DoubleLine("A-WALL", 0, 6000, 0));
            var geometry = (JObject)((JArray)profile["requirement_set_skeleton"]["rules"])[0]["geometry"];

            Assert.Equal(0.3, (double)geometry["min_overlap_fraction"], 3);
        }

        [Fact]
        public void The_bands_it_pre_fills_are_WIDER_than_what_it_measured()
        {
            // A band taken exactly from the observed range excludes the run that
            // sits on its boundary - which is the run that produced the number.
            JObject profile = Profile(DoubleLine("A-WALL", 0, 6000, 0));
            var geometry = (JObject)((JArray)profile["requirement_set_skeleton"]["rules"])[0]["geometry"];

            Assert.True((double)geometry["min_thickness_mm"] < 200.0);
            Assert.True((double)geometry["max_thickness_mm"] > 200.0);
        }

        [Fact]
        public void A_ring_is_offered_as_ONE_loop_and_not_as_four_loose_lines()
        {
            // THE RANKING RULE, stated as the case that found it. single_lines
            // reads every segment on every layer as its own candidate, so a
            // profiler that picked the reading with the most candidates picked
            // single_lines everywhere - and pre-filled a LENGTH band for
            // something that is a slab.
            JObject layer = Layer(Profile(Ring("RINGS", 0, 0, 4000, 3000)), "RINGS");

            Assert.Equal(4, (int)Reading(layer, "single_lines")["candidates"]);
            Assert.Equal(1, (int)Reading(layer, "closed_loops")["candidates"]);
            Assert.Equal("closed_loops", (string)layer["best_reading"]["from"]);
            Assert.Contains("fewest pieces", (string)layer["best_reading"]["chosen_because"]);
        }

        [Fact]
        public void A_layer_with_no_structure_says_so_by_what_it_does_NOT_read_as()
        {
            // A half-millimetre mark - the debris hatching and annotation leave
            // behind. MEASURED: single_lines refuses it for being under its own
            // minimum and point_clusters reads its ends as one cluster, so
            // something always wins. What says "there is nothing here" is that
            // every STRUCTURED reader found nothing, and a person reads that as
            // clearly as an empty layer - while the segment is still reported,
            // because it does exist.
            JObject profile = Profile(new[]
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(0.5, 0), "DEBRIS")
            });
            JObject layer = Layer(profile, "DEBRIS");

            Assert.NotNull(layer);
            Assert.Equal(1, (int)layer["segments"]);
            foreach (string structured in new[] { "double_lines", "double_arcs", "closed_loops" })
                Assert.Equal(0, (int)Reading(layer, structured)["candidates"]);
        }

        [Fact]
        public void What_it_did_NOT_profile_is_named_rather_than_silently_trimmed()
        {
            var parts = new List<CadSegment>();
            for (int i = 0; i < 5; i++) parts.AddRange(DoubleLine("LAYER-" + i, 0, 6000, i * 1000));

            JObject profile = CadLayerProfiler.Profile(parts, "millimeter", 2);

            Assert.Equal(2, (int)profile["layers_profiled"]);
            Assert.Equal(5, (int)profile["layers_in_drawing"]);
            Assert.Equal(3, (int)profile["layers_not_profiled"]);
            Assert.NotNull((string)profile["layers_not_profiled_means"]);
        }
    }
}
