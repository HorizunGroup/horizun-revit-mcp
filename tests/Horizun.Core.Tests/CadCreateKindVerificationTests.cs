// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A KIND THAT CAN BE BUILT AND CANNOT BE RE-READ.
//
// horizun_create_elements verifies every row by asking the model what it got:
// the element is fetched back after the commit and checked against the kind that
// was requested. The check is a switch, and its default answer is "no".
//
// So a new kind added to the builder and not to that switch BUILDS PERFECTLY and
// then reports itself unverified - or, in a version where the default had been
// friendlier, would have reported success for whatever Revit happened to make.
// It happened to both kinds added in one sitting, and the only reason it surfaced
// is that the contract refuses to claim what it did not read.
//
// This is the coupling stated once, so the next kind is caught at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadCreateKindVerificationTests
    {
        private static string KindMatchesBody()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit")))
                dir = dir.Parent;
            Assert.True(dir != null, "the repository root must be findable from the test binary");
            string path = Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "CreateElementsCommand.cs");
            Assert.True(File.Exists(path), path + " must exist");

            string source = File.ReadAllText(path);
            int start = source.IndexOf("private static bool KindMatches(", StringComparison.Ordinal);
            Assert.True(start >= 0, "CreateElementsCommand must still verify a row's kind by re-reading it");
            int end = source.IndexOf("private static T Need<", start, StringComparison.Ordinal);
            Assert.True(end > start, "the end of KindMatches must be findable");
            return source.Substring(start, end - start);
        }

        [Fact]
        public void Every_kind_a_requirement_set_can_ask_for_can_be_RE_READ_after_the_commit()
        {
            string body = KindMatchesBody();
            var unverifiable = new List<string>();
            foreach (string kind in CadConversionPlanRules.CreateKinds)
                if (!body.Contains("case \"" + kind + "\":")) unverifiable.Add(kind);

            Assert.True(unverifiable.Count == 0,
                        "these kinds can be planned from a drawing and cannot be verified after they are " +
                        "built, so create_elements would report them unverified - or, worse, verify them as " +
                        "whatever Revit happened to make: " + string.Join(", ", unverifiable));
        }

        [Fact]
        public void The_plan_emits_a_separator_as_TWO_points_and_the_builder_must_take_that()
        {
            // ONE LINE ACROSS A ROOM is the most ordinary separator there is, and
            // the plan emits exactly that. The builder read it through the loop
            // reader, which demands three points and CLOSES what it is given - so
            // the ordinary case was refused outright, and a three-point chain that
            // did get through was quietly closed into a triangle nobody drew.
            var set = CadRequirementSet.Load(Newtonsoft.Json.Linq.JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 's', 'version': '1.0.0', 'title': 'Separators' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': [{ 'id': 'r', 'precedence': 10, 'layers': ['A-*'], 'produces': 'room_separator',
                          'category': 'OST_RoomSeparationLines', 'level': 'L1',
                          'geometry': { 'from': 'single_lines' } }]
            }".Replace('\'', '"')));

            var segments = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(4000, 0), "A-SEP")
            };
            CadInterpretation read = CadInterpretationRules.Interpret(segments, set, "sha");
            CadConversionPlan plan = CadConversionPlanRules.Plan(read, set, "fp", true);
            List<Newtonsoft.Json.Linq.JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, "M");
            Assert.NotEmpty(requests);
            var row = (Newtonsoft.Json.Linq.JObject)((Newtonsoft.Json.Linq.JArray)requests[0]["elements"])[0];

            Assert.Equal("room_separator", (string)row["kind"]);
            var chain = (Newtonsoft.Json.Linq.JArray)((Newtonsoft.Json.Linq.JArray)row["profile"])[0];
            Assert.Equal(2, chain.Count);
        }

        [Fact]
        public void And_the_builder_reads_that_profile_as_a_CHAIN_rather_than_closing_it()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit")))
                dir = dir.Parent;
            string source = File.ReadAllText(Path.Combine(
                dir.FullName, "src", "Horizun.Revit", "Commands", "CreateElementsCommand.cs"));
            // BOUNDED BY THE NEXT CASE, not by a character count: a window of N
            // characters stops covering the arm the moment anybody adds a check
            // to it, and then the test passes for the wrong reason or fails for
            // one that has nothing to do with the claim.
            int sep = source.IndexOf("case \"room_separator\":", StringComparison.Ordinal);
            Assert.True(sep >= 0);
            int next = source.IndexOf("case \"slab_opening\":", sep, StringComparison.Ordinal);
            Assert.True(next > sep, "the arm must be bounded by the case that follows it");
            string arm = source.Substring(sep, next - sep);
            Assert.Contains("Chains(", arm);
            Assert.DoesNotContain("Loops(item[\"profile\"]", arm);
        }

        [Fact]
        public void A_shaft_is_verified_as_a_SHAFT_and_not_merely_as_an_opening()
        {
            // "is it an Opening" is true of a hole in one floor as well, which is
            // exactly the thing a shaft is not. The category is what separates
            // them, and a verification that skipped it would pass the mistake the
            // separate kind exists to prevent.
            string body = KindMatchesBody();
            int shaft = body.IndexOf("case \"shaft\":", StringComparison.Ordinal);
            Assert.True(shaft >= 0, "shaft must be verifiable");
            string arm = body.Substring(shaft, Math.Min(220, body.Length - shaft));
            Assert.Contains("OST_ShaftOpening", arm);
        }

        [Fact]
        public void A_room_separator_is_verified_as_a_boundary_and_not_as_any_line()
        {
            // A detail line drawn in the same place looks identical in the view
            // and bounds nothing, which is the whole failure this kind is for.
            string body = KindMatchesBody();
            int sep = body.IndexOf("case \"room_separator\":", StringComparison.Ordinal);
            Assert.True(sep >= 0, "room_separator must be verifiable");
            string arm = body.Substring(sep, Math.Min(220, body.Length - sep));
            Assert.Contains("OST_RoomSeparationLines", arm);
        }
    }
}
