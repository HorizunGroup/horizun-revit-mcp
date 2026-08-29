// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A SHAFT IS NOT A HOLE IN A SLAB.
//
// MEASURED across Revit 2023-2027, by reflection over each year's own
// RevitAPI.dll: there are four NewOpening overloads and they build different
// things. NewOpening(hostElement, profile, perpendicular) cuts the ONE element
// it is hosted in. NewOpening(bottomLevel, topLevel, profile) makes a SHAFT,
// which cuts every floor, roof and ceiling its extent passes through.
//
// Reading the second as the first is the tempting shortcut and it is wrong in a
// way nobody sees for months: a shaft built as one opening per floor stops
// existing the day somebody adds a storey, and is a different element in every
// schedule.
//
// So `opening` and `shaft` map to different builders, and a shaft must name the
// two levels it runs between - a drawing shows one ring and says nothing at all
// about height.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadOpeningShaftTests
    {
        private const string Sha = "sha-of-the-drawing";

        private static string SetJson(string produces, string category, string extra = "")
        {
            return @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'openings', 'version': '1.0.0', 'title': 'Openings' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-*'], 'produces': 'PRODUCES',
                          'category': 'CATEGORY'EXTRA,
                          'geometry': { 'from': 'closed_loops', 'min_area_mm2': 100 } }]
            }".Replace('\'', '"').Replace("PRODUCES", produces).Replace("CATEGORY", category)
              .Replace("EXTRA", extra);
        }

        private static CadRequirementSet Set(string produces, string category, string extra = "")
        {
            return CadRequirementSet.Load(JObject.Parse(SetJson(produces, category, extra)));
        }

        private static CadRequirementSetException Refused(string produces, string category, string extra)
        {
            return Assert.Throws<CadRequirementSetException>(() => Set(produces, category, extra));
        }

        /// <summary>A closed rectangle as four segments - the ring a drawing gives.</summary>
        private static List<CadSegment> Ring(double x0, double y0, double x1, double y1)
        {
            return new List<CadSegment>
            {
                new CadSegment(new CadPoint(x0, y0), new CadPoint(x1, y0), "A-SHAFT"),
                new CadSegment(new CadPoint(x1, y0), new CadPoint(x1, y1), "A-SHAFT"),
                new CadSegment(new CadPoint(x1, y1), new CadPoint(x0, y1), "A-SHAFT"),
                new CadSegment(new CadPoint(x0, y1), new CadPoint(x0, y0), "A-SHAFT")
            };
        }

        private static JObject FirstRow(CadRequirementSet set, List<CadSegment> segs)
        {
            CadInterpretation r = CadInterpretationRules.Interpret(segs, set, Sha);
            CadConversionPlan plan = CadConversionPlanRules.Plan(r, set, "fp", true);
            List<JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, "M");
            return requests.Count == 0 ? null : (JObject)((JArray)requests[0]["elements"])[0];
        }

        // ------------------------------------------------------------- shaft

        [Fact]
        public void A_shaft_is_planned_as_its_OWN_kind_and_not_as_a_slab_opening()
        {
            JObject row = FirstRow(
                Set("shaft", "OST_ShaftOpening", ", 'base_level': 'Level 1', 'top_level': 'Level 2'"),
                Ring(0, 0, 2000, 3000));

            Assert.NotNull(row);
            Assert.Equal("shaft", (string)row["kind"]);
            Assert.NotEqual("slab_opening", (string)row["kind"]);
        }

        [Fact]
        public void It_carries_its_PROFILE_and_both_level_names()
        {
            JObject row = FirstRow(
                Set("shaft", "OST_ShaftOpening", ", 'base_level': 'Level 1', 'top_level': 'Roof'"),
                Ring(0, 0, 2000, 3000));

            Assert.Equal("Level 1", (string)row["base_level_name"]);
            Assert.Equal("Roof", (string)row["top_level_name"]);
            var profile = (JArray)row["profile"];
            Assert.Single(profile);
            Assert.True(((JArray)profile[0]).Count >= 4, "the ring must reach the builder as a ring");
        }

        [Fact]
        public void A_shaft_rule_that_names_only_ONE_level_is_refused()
        {
            // A default would be a shaft stopping at a storey nobody chose, and
            // that looks entirely correct in plan.
            Assert.Contains("no top_level",
                Refused("shaft", "OST_ShaftOpening", ", 'base_level': 'Level 1'").Message);
            Assert.Contains("no base_level",
                Refused("shaft", "OST_ShaftOpening", ", 'top_level': 'Level 2'").Message);
        }

        [Fact]
        public void A_shaft_between_a_level_and_ITSELF_is_refused()
        {
            Assert.Contains("no height",
                Refused("shaft", "OST_ShaftOpening",
                        ", 'base_level': 'Level 1', 'top_level': 'Level 1'").Message);
        }

        [Fact]
        public void Only_a_shaft_may_declare_two_levels()
        {
            // A key that reaches a builder which ignores it is a promise nothing
            // keeps - and a floor with a top_level would read as a shaft to
            // anybody skimming the set.
            Assert.Contains("promise nothing keeps",
                Refused("floor", "OST_Floors", ", 'base_level': 'Level 1', 'top_level': 'Level 2'").Message);
        }

        // ----------------------------------------------------------- opening

        [Fact]
        public void An_opening_becomes_a_SLAB_opening_with_a_centre_and_a_size()
        {
            // The typed slab opening takes a centre and a size; a drawing gives a
            // ring. A rectangle converts exactly, which is why the conversion is
            // stated here rather than assumed.
            JObject row = FirstRow(Set("opening", "OST_ShaftOpening"), Ring(1000, 2000, 3000, 5000));

            Assert.Equal("slab_opening", (string)row["kind"]);
            Assert.Equal("rectangular", (string)row["shape"]);
            Assert.Equal(2000.0, (double)row["center"][0], 3);
            Assert.Equal(3500.0, (double)row["center"][1], 3);
            Assert.Equal(2000.0, (double)row["width"], 3);
            Assert.Equal(3000.0, (double)row["height"], 3);
        }

        [Fact]
        public void An_opening_says_WHAT_it_needs_to_be_hosted_in()
        {
            JObject row = FirstRow(Set("opening", "OST_ShaftOpening"), Ring(0, 0, 1000, 1000));
            Assert.Equal("slab", (string)row["hosted_on"]);
        }

        // ---------------------------------------- cutting a load-bearing slab

        [Fact]
        public void Permission_to_cut_a_STRUCTURAL_slab_travels_only_when_the_set_gave_it()
        {
            // A hole through a load-bearing floor is an engineering decision.
            // create_elements refuses one without an explicit opt-in per row, so
            // the row either carries the permission or it does not exist - and an
            // absent key must stay absent rather than becoming a false, which
            // would read as a decision somebody made to say no.
            JObject silent = FirstRow(Set("opening", "OST_ShaftOpening"), Ring(0, 0, 1000, 1000));
            Assert.Null(silent["allow_structural"]);

            JObject given = FirstRow(Set("opening", "OST_ShaftOpening", ", 'allow_structural': true"),
                                     Ring(0, 0, 1000, 1000));
            Assert.True((bool)given["allow_structural"]);
        }

        [Fact]
        public void Permission_is_NOT_the_same_key_as_saying_the_slab_is_structural()
        {
            // `structural` describes what an element IS. `allow_structural` is
            // what a person accepts. Reading one as the other would let a rule
            // that merely describes a load-bearing slab authorise cutting it.
            JObject describes = FirstRow(Set("opening", "OST_ShaftOpening", ", 'structural': true"),
                                         Ring(0, 0, 1000, 1000));
            Assert.Null(describes["allow_structural"]);
        }

        [Fact]
        public void A_rule_that_cuts_NOTHING_may_not_declare_that_permission()
        {
            // The key would reach a builder that ignores it, and would sit in the
            // requirement set reading as an authorisation this bridge asked for.
            CadRequirementSetException e = Refused("wall", "OST_Walls", ", 'allow_structural': true");
            Assert.Contains("allow_structural", e.Message);
            Assert.Contains("cuts nothing", e.Message);
        }

        // --------------------------------------------------- room_separator

        [Fact]
        public void A_room_separator_read_from_a_RING_is_refused_before_it_leaks()
        {
            // The plan emits a separator's profile as the candidate's point list,
            // and for a closed loop that is the ring WITHOUT its closing edge. The
            // boundary would be drawn on three sides of four: built, verified (it
            // IS a curve in the right category), and the room inside bleeding
            // through the missing side into the next space, with every area and
            // every schedule line wrong and nothing saying why.
            CadRequirementSetException e = Assert.Throws<CadRequirementSetException>(
                () => Set("room_separator", "OST_RoomSeparationLines"));
            Assert.Contains("closing edge", e.Message);
            Assert.Contains("single_lines", e.Message);
        }

        [Fact]
        public void A_room_separator_reaches_the_plan_as_its_own_kind_with_curves()
        {
            // From single_lines, which is what a line that divides a room IS.
            CadRequirementSet set = CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'seps', 'version': '1.0.0', 'title': 'Separators' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-*'], 'produces': 'room_separator',
                          'category': 'OST_RoomSeparationLines',
                          'geometry': { 'from': 'single_lines' } }]
            }".Replace('\'', '"')));
            JObject row = FirstRow(set, new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(4000, 0), "A-SEP")
            });

            Assert.Equal("room_separator", (string)row["kind"]);
            Assert.NotNull(row["profile"]);
            // level_id is NOT here, and should not be: an id is something only
            // the open document can supply, and this layer is deliberately
            // Revit-free. horizun_plan_from_cad resolves it, and refuses when it
            // cannot - a separator on the wrong storey bounds a room nobody meant.
            Assert.Null(row["level_id"]);
        }

        [Fact]
        public void EVERY_kind_in_the_produces_vocabulary_now_has_a_builder_or_a_stated_reason()
        {
            // The gap this phase closed: opening, shaft and room_separator were in
            // the vocabulary, in the staging order, and absent from the map that
            // turns a candidate into a create row - so they deferred with "no
            // typed way to build a X exists", which is a promise the vocabulary
            // had already made.
            string source = System.IO.File.ReadAllText(SourceFile());
            foreach (string produces in new[] { "opening", "shaft", "room_separator" })
                Assert.True(source.Contains("[\"" + produces + "\"] = \""),
                            produces + " is in the produces vocabulary and has no create kind");
        }

        private static string SourceFile()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(
                       System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit"))) dir = dir.Parent;
            Assert.True(dir != null, "the repository root must be findable from the test binary");
            return System.IO.Path.Combine(dir.FullName, "src", "Horizun.Revit", "Core",
                                          "CadConversionPlanRules.cs");
        }
    }
}
