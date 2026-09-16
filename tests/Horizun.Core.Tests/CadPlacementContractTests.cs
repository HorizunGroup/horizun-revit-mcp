// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE FAILURES THIS ROUTE ACTUALLY HAD, EACH ONE PINNED.
//
// Every case here was a real refusal on a real drawing, in order, over one
// session. They are kept as regressions because each was invisible to every test
// that existed at the time - the two halves of the pipeline were each correct
// about their own half.
//
//   1. the plan emitted keys the writer refuses               (flip, category)
//   2. a work-plane based family went through the wall overload and Revit
//      returned an instance with Host == null, raising nothing
//   3. a symbol drawn inside a wall's thickness was reported as drawn past the
//      end of it
//   4. a mirror was refused because ONE api said no, while another said yes
//   5. a claim of symmetry was accredited by a tolerance borrowed from the host
//      search: 152 mm of slack on a 300 mm symbol
//   6. rows were withdrawn after the plan counted them, so a reply said 21
//      modelled and emitted 8
//
// What cannot be pinned here is named where it is: the Revit-side behaviour
// (placement routes, host faces, reflected copies) lives in the live evidence,
// because it needs a document.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadPlacementContractTests
    {
        private static CadRequirementSet Set(string extra = "")
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'placement', 'version': '1' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 250, 'gap_mm': 500, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r-out', 'layers': ['E-P'], 'produces': 'electrical_fixture',
                           'family_type': 'Duplex Receptacle: Standard', 'level': 'Level 1',
                           'hosted_on': 'wall',
                           'geometry': { 'from': 'blocks', 'blocks': ['OUT2'] } EXTRA } ]
            }";
            doc = doc.Replace("EXTRA", extra).Replace("EXTRATOL", "").Replace('\'', '"');
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static CadIrEntity Insert(string name, double x, double y, double? rotation = 0,
                                          double scaleX = 1)
        {
            var e = new CadIrEntity
            {
                Id = "i" + name + x,
                Kind = CadEntityKind.BlockInstance,
                Layer = "E-P",
                BlockName = name,
                RotationRadians = rotation,
                ScaleX = scaleX,
                ScaleY = 1,
                Space = "model"
            };
            e.Points.Add(new CadPoint(x, y));
            return e;
        }

        private static List<JObject> RowsFor(CadRequirementSet set, params CadIrEntity[] instances)
        {
            CadBlockReading reading = CadBlockRules.Interpret(instances.ToList(), set, "hash");
            var interpretation = new CadInterpretation();
            interpretation.Candidates.AddRange(reading.Candidates);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interpretation, set, "src", false);
            return CadConversionPlanRules.AsCreateRequests(plan, "HZ_TEST");
        }

        /// <summary>REGRESSION 1: the plan must not emit a key the writer refuses.</summary>
        [Fact]
        public void Every_key_the_plan_emits_is_a_key_the_writer_accepts()
        {
            List<JObject> rows = RowsFor(Set(), Insert("OUT2", 1000, 2000), Insert("OUT2", 2000, 2000, 0, -1));
            Assert.NotEmpty(rows);

            foreach (JObject batch in rows)
            foreach (JObject element in (JArray)batch["elements"])
            {
                var sent = (JObject)element.DeepClone();
                foreach (string resolved in new[] { "level_name", "type_name", "system_type_name",
                                                    "hosted_on", "host_point", "host_level_name" })
                    sent.Remove(resolved);
                sent["level_id"] = 1;
                sent["type_id"] = 1;
                sent["host_id"] = 1;

                string refusal = ToolInputRules.ValidateCreation(sent, (string)sent["kind"]);
                Assert.True(refusal == null, "the plan emits a row the writer refuses: " + refusal);
            }
        }

        /// <summary>
        /// REGRESSION 1c: THE SAME GATE, OVER A WALL ROW.
        ///
        /// The first version of this gate only saw the rows a blocks rule emits,
        /// so it missed `join_rule` - parsed by the schema since the schema
        /// existed, described as "applied by the writer", accepted by nobody, and
        /// refused on all sixteen rows of a real wall conversion. A gate that
        /// covers one producer covers one producer.
        /// </summary>
        [Fact]
        public void Every_key_a_wall_row_emits_is_a_key_the_writer_accepts()
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'walls', 'version': '1' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 25, 'gap_mm': 150, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [ { 'id': 'r-wall', 'layers': ['A-WALL'], 'produces': 'wall',
                           'family_type': 'Basic Wall: Generic', 'level': 'Level 1',
                           'height_mm': 2700, 'join_rule': 'none', 'structural': false,
                           'geometry': { 'from': 'double_lines', 'min_thickness_mm': 60,
                                         'max_thickness_mm': 400, 'min_overlap_mm': 200 } } ]
            }".Replace('\'', '"');
            CadRequirementSet set = CadRequirementSet.Load(JObject.Parse(doc));

            var lines = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(3000, 0), "A-WALL", CadCurveKind.Line, 0),
                new CadSegment(new CadPoint(0, 150), new CadPoint(3000, 150), "A-WALL", CadCurveKind.Line, 0)
            };
            CadConversionPlan plan = CadConversionPlanRules.Plan(
                CadInterpretationRules.Interpret(lines, set, "hash"), set, "src", false);
            List<JObject> rows = CadConversionPlanRules.AsCreateRequests(plan, "HZ_TEST");
            Assert.NotEmpty(rows);

            foreach (JObject batch in rows)
            foreach (JObject element in (JArray)batch["elements"])
            {
                var sent = (JObject)element.DeepClone();
                foreach (string resolved in new[] { "level_name", "type_name", "system_type_name" })
                    sent.Remove(resolved);
                sent["level_id"] = 1;
                sent["type_id"] = 1;

                string refusal = ToolInputRules.ValidateCreation(sent, (string)sent["kind"]);
                Assert.True(refusal == null, "the plan emits a wall row the writer refuses: " + refusal);
            }

            // ...and the declaration really is on the row, not lost on the way.
            Assert.Equal("none", (string)((JArray)rows[0]["elements"])[0]["join_rule"]);
        }

        /// <summary>
        /// REGRESSION 1b: a row that asks for a HOST must carry one the writer can use.
        /// hosted_on is the plan's word and host_id is the writer's; the row that
        /// reaches horizun_create_elements must carry the second, never the first.
        /// </summary>
        [Fact]
        public void The_hosted_word_is_the_plans_and_never_reaches_the_writer()
        {
            List<JObject> rows = RowsFor(Set(), Insert("OUT2", 1000, 2000));
            JObject row = (JObject)((JArray)rows[0]["elements"])[0];

            Assert.Equal("wall", (string)row["hosted_on"]);
            Assert.Null(ToolInputRules.ValidateCreation(
                new JObject
                {
                    ["kind"] = "family_instance",
                    ["coordinate_mode"] = "absolute",
                    ["point"] = row["point"],
                    ["type_id"] = 1,
                    ["level_id"] = 1,
                    ["host_id"] = 1
                }, "family_instance"));
        }

        /// <summary>REGRESSION 4 and 5: a mirror is resolved, and symmetry is accredited.</summary>
        [Fact]
        public void A_mirror_is_asked_for_unless_something_measured_says_it_would_do_nothing()
        {
            // Nothing measured: the row asks for the reflection.
            List<JObject> asIs = RowsFor(Set(), Insert("OUT2", 1000, 2000, 0, -1));
            Assert.True((bool)((JArray)asIs[0]["elements"])[0]["flip"]);

            // Measured symmetric: the row does not ask, because it would do nothing.
            CadRequirementSet set = Set();
            CadBlockReading reading = CadBlockRules.Interpret(
                new List<CadIrEntity> { Insert("OUT2", 1000, 2000, 0, -1) }, set, "hash");
            reading.Candidates[0].MirrorResolution = "symmetric_by_measurement";
            var interpretation = new CadInterpretation();
            interpretation.Candidates.AddRange(reading.Candidates);
            List<JObject> measured = CadConversionPlanRules.AsCreateRequests(
                CadConversionPlanRules.Plan(interpretation, set, "src", false), "HZ_TEST");
            Assert.Null(((JArray)measured[0]["elements"])[0]["flip"]);
        }

        /// <summary>REGRESSION 4b: an unresolved mirror is deferred, not built unmirrored.</summary>
        [Fact]
        public void A_mirror_nobody_could_resolve_is_deferred_with_its_reason()
        {
            CadRequirementSet set = Set();
            CadBlockReading reading = CadBlockRules.Interpret(
                new List<CadIrEntity> { Insert("OUT2", 1000, 2000, 0, -1) }, set, "hash");
            reading.Candidates[0].MirrorResolution = "pending_symmetry_not_accredited";
            reading.Candidates[0].MirrorEvidence = new JObject { ["means"] = "the residual was 52 mm" };

            var interpretation = new CadInterpretation();
            interpretation.Candidates.AddRange(reading.Candidates);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interpretation, set, "src", false);

            Assert.Empty(plan.Actions);
            CadDeferred deferred = Assert.Single(plan.Deferred);
            Assert.Contains("MIRRORED", string.Join(" ", deferred.Reasons));
            Assert.Contains("52 mm", string.Join(" ", deferred.Reasons));
        }

        /// <summary>
        /// REGRESSION 6: every emitted row carries a stable name, so a row can be
        /// withdrawn without moving anybody else's provenance.
        /// </summary>
        [Fact]
        public void Every_emitted_row_names_its_own_candidate()
        {
            List<JObject> rows = RowsFor(Set(),
                Insert("OUT2", 1000, 2000), Insert("OUT2", 2000, 2000), Insert("OUT2", 3000, 2000));

            var elements = (JArray)rows[0]["elements"];
            Assert.Equal(3, elements.Count);

            var names = elements.Select(e => (int)e["source_row"]).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.All(names, n => Assert.True(n >= 1));

            // Withdrawing the FIRST row must not renumber the others: that is the
            // whole point of naming them.
            elements.RemoveAt(0);
            Assert.Equal(new[] { names[1], names[2] },
                         elements.Select(e => (int)e["source_row"]).ToArray());
        }
    }
}
