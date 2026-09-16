// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// FROM A SYMBOL SOMEBODY DREW TO A ROW THAT BUILDS IT, WITH NOTHING TYPED IN
// BETWEEN.
//
// The procedure this bridge shipped asked the caller to prepare `elements` and
// fill in `type_name` - which for an electrical unit plan means writing a few
// hundred rows by hand, and the conversion's contribution is a coordinate list.
// These cases are the other shape: the caller writes a MAPPING, once, and every
// occurrence of every mapped symbol becomes a row.
//
// The last case does it against a real drawing when one is pointed at, because a
// mapping that works on three synthetic blocks proves very little about a permit
// set where the same symbol arrives nested through an external reference with a
// name like "UNIT-XREF|VANITY-LIGHT".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadBlockRouteTests
    {
        private const string Hash = "drawing-sha";

        private static CadRequirementSet Set(string rulesJson)
        {
            string doc = @"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'elec', 'version': '1.0.0', 'title': 'Electrical symbols' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1.0, 'gap_mm': 25.0, 'angle_degrees': 2.0, 'arc_sagitta_mm': 5.0 },
              'rules': RULES
            }".Replace('\'', '"').Replace("RULES", rulesJson);
            return CadRequirementSet.Load(JObject.Parse(doc));
        }

        private static CadIrEntity Insert(string name, double x, double y, string layer = "E-PWR",
                                          double? rotation = 0, double scaleX = 1,
                                          Dictionary<string, string> attributes = null, string handle = null)
        {
            var e = new CadIrEntity
            {
                Id = "i" + name + x.ToString("0"),
                Kind = CadEntityKind.BlockInstance,
                Layer = layer,
                BlockName = name,
                RotationRadians = rotation,
                ScaleX = scaleX,
                ScaleY = 1,
                Attributes = attributes,
                Handle = handle
            };
            e.Points.Add(new CadPoint(x, y, 0));
            return e;
        }

        private static readonly string Outlets = @"[
          { 'id': 'receptacles', 'precedence': 20, 'layers': ['E-*'], 'produces': 'electrical_fixture',
            'category': 'OST_ElectricalFixtures', 'family_type': 'Duplex Receptacle: Standard',
            'level': 'Level 1',
            'geometry': { 'from': 'blocks', 'blocks': ['OUT2', 'OUT*'] } }
        ]".Replace('\'', '"');

        // ---- identity ----------------------------------------------------------

        [Fact]
        public void A_symbols_id_does_not_depend_on_what_came_before_it_in_the_reading()
        {
            // MEASURED: block candidates were identified as "b" + their position in
            // the reading, and provenance recorded that - so removing one symbol
            // renumbered every later one and the audit paired the wrong symbols.
            var first = CadBlockRules.Interpret(new List<CadIrEntity>
            {
                Insert("OUT2", 1000, 1000), Insert("OUT2", 2000, 1000), Insert("OUT2", 3000, 1000)
            }, Set(Outlets), Hash);
            var second = CadBlockRules.Interpret(new List<CadIrEntity>
            {
                Insert("OUT2", 2000, 1000), Insert("OUT2", 3000, 1000)
            }, Set(Outlets), Hash);

            string idAt3000 = first.Candidates.Single(c => c.Geometry[0].X == 3000).Id;
            Assert.Equal(idAt3000, second.Candidates.Single(c => c.Geometry[0].X == 3000).Id);
            Assert.StartsWith("cadrev:", idAt3000);
            Assert.Contains(idAt3000, first.Candidates.Single(c => c.Geometry[0].X == 3000).SourceSurrogates);
        }

        [Fact]
        public void The_same_symbol_in_a_reissued_file_has_a_different_revision_id_and_the_same_semantic_id()
        {
            var one = CadBlockRules.Interpret(new List<CadIrEntity> { Insert("OUT2", 1000, 1000) }, Set(Outlets), "issue-1");
            var two = CadBlockRules.Interpret(new List<CadIrEntity> { Insert("OUT2", 1000, 1000) }, Set(Outlets), "issue-2");

            Assert.NotEqual(one.Candidates.Single().Id, two.Candidates.Single().Id);
            Assert.Equal(one.Candidates.Single().SemanticId, two.Candidates.Single().SemanticId);
        }

        [Fact]
        public void Two_instances_the_identity_cannot_tell_apart_both_go_to_review()
        {
            var reading = CadBlockRules.Interpret(new List<CadIrEntity>
            {
                Insert("OUT2", 1000, 1000), Insert("OUT2", 1000.2, 1000)
            }, Set(Outlets), Hash);

            Assert.Equal(2, reading.Candidates.Count);
            Assert.All(reading.Candidates, c => Assert.False(c.EligibleForAutomaticApply));
            Assert.All(reading.Candidates, c => Assert.Contains(c.IneligibleReasons, r => r.Contains("share the identity")));
        }

        // ---- the shape of the thing ------------------------------------------

        [Fact]
        public void Every_instance_of_a_mapped_block_becomes_a_candidate()
        {
            var instances = new List<CadIrEntity>
            {
                Insert("OUT2", 1000, 1000), Insert("OUT2", 2000, 1000), Insert("OUT2", 3000, 1000)
            };
            CadBlockReading r = CadBlockRules.Interpret(instances, Set(Outlets), Hash);

            Assert.Equal(3, r.Candidates.Count);
            Assert.Equal(3, r.InstancesClaimed);
            Assert.Equal(1.0, r.Coverage);
            Assert.All(r.Candidates, c => Assert.Equal("Duplex Receptacle: Standard", c.FamilyType));

            // Three symbols, three DIFFERENT identities - they are in three places.
            Assert.Equal(3, r.Candidates.Select(c => c.GeometryId).Distinct().Count());
        }

        [Fact]
        public void A_block_no_rule_claims_is_reported_with_what_was_seen_of_it()
        {
            var instances = new List<CadIrEntity>
            {
                Insert("OUT2", 0, 0),
                Insert("TEL-DATA", 500, 0, attributes: new Dictionary<string, string> { ["NAME"] = "T1" }),
                Insert("TEL-DATA", 900, 0)
            };
            CadBlockReading r = CadBlockRules.Interpret(instances, Set(Outlets), Hash);

            // This IS the mapping's worklist: the drawing's own vocabulary, by
            // frequency, with the attribute tags each symbol carries.
            CadUnclaimedBlock u = Assert.Single(r.Unclaimed);
            Assert.Equal("TEL-DATA", u.BlockName);
            Assert.Equal(2, u.Count);
            Assert.Contains("NAME", u.AttributeTags);
            Assert.Equal(1.0 / 3.0, r.Coverage, 4);
        }

        [Fact]
        public void An_xref_qualified_name_matches_the_bare_pattern()
        {
            // Measured on a real permit set: every symbol in the unit plan arrives
            // as "DRAWING|NAME" because it is nested through an external reference.
            // A caller writing a mapping should not have to know that.
            var instances = new List<CadIrEntity> { Insert("UNIT-XREF|OUT2", 0, 0) };
            CadBlockReading r = CadBlockRules.Interpret(instances, Set(Outlets), Hash);
            Assert.Single(r.Candidates);
            Assert.Equal("UNIT-XREF|OUT2", r.Candidates[0].SourceBlockName);
        }

        [Fact]
        public void Rotation_and_mirroring_survive_into_the_plan()
        {
            var instances = new List<CadIrEntity>
            {
                Insert("OUT2", 0, 0, rotation: Math.PI / 2),
                Insert("OUT2", 1000, 0, rotation: 0, scaleX: -1)
            };
            CadRequirementSet set = Set(Outlets);
            CadBlockReading r = CadBlockRules.Interpret(instances, set, Hash);

            Assert.Equal(Math.PI / 2, r.Candidates[0].RotationRadians.Value, 6);
            Assert.False(r.Candidates[0].Mirrored);
            Assert.True(r.Candidates[1].Mirrored);

            var interp = new CadInterpretation();
            interp.Candidates.AddRange(r.Candidates);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, "src", false);

            JObject turned = plan.Actions[0].Arguments;
            Assert.Equal(90, turned.Value<double>("rotation_degrees"), 4);

            // A MIRROR IS NOT AN ANGLE. No rotation reproduces a reflection, and a
            // mirrored fixture placed unmirrored has its handing reversed.
            Assert.True(plan.Actions[1].Arguments.Value<bool>("flip"));
        }

        [Fact]
        public void An_attribute_reaches_a_parameter_only_where_the_rule_asked_for_it()
        {
            string rules = @"[
              { 'id': 'panels', 'precedence': 20, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'category': 'OST_ElectricalFixtures', 'family_type': 'Panelboard: 42-circuit',
                'level': 'Level 1',
                'parameters': { 'Panel Name': { 'from_block_attribute': 'NAME' } },
                'geometry': { 'from': 'blocks', 'blocks': ['PNL'] } }
            ]".Replace('\'', '"');

            var instances = new List<CadIrEntity>
            {
                Insert("PNL", 0, 0, attributes: new Dictionary<string, string>
                    { ["NAME"] = "LP-1", ["VOLTAGE"] = "208/120" })
            };
            CadCandidate c = CadBlockRules.Interpret(instances, Set(rules), Hash).Candidates.Single();

            CadParameterWrite w = Assert.Single(c.Parameters);
            Assert.Equal("Panel Name", w.Parameter);
            Assert.Equal("LP-1", (string)w.Value);

            // VOLTAGE was on the symbol and nobody mapped it. It is reported as
            // evidence and NOT written: a parameter name this bridge invented is
            // data nobody can trace.
            Assert.Equal("208/120", c.ObservedAttributes["VOLTAGE"]);
            Assert.DoesNotContain(c.Parameters, p => p.Parameter == "VOLTAGE");
        }

        [Fact]
        public void A_missing_attribute_writes_nothing_rather_than_an_empty_string()
        {
            string rules = @"[
              { 'id': 'panels', 'precedence': 20, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'category': 'OST_ElectricalFixtures', 'family_type': 'Panelboard: 42-circuit',
                'level': 'Level 1',
                'parameters': { 'Panel Name': { 'from_block_attribute': 'NAME' } },
                'geometry': { 'from': 'blocks', 'blocks': ['PNL'] } }
            ]".Replace('\'', '"');

            CadCandidate c = CadBlockRules.Interpret(
                new List<CadIrEntity> { Insert("PNL", 0, 0) }, Set(rules), Hash).Candidates.Single();
            Assert.Empty(c.Parameters);
        }

        [Fact]
        public void Two_rules_of_equal_precedence_produce_nothing_and_say_so()
        {
            string rules = @"[
              { 'id': 'as_fixture', 'precedence': 20, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'category': 'OST_ElectricalFixtures', 'family_type': 'A: A', 'level': 'Level 1',
                'geometry': { 'from': 'blocks', 'blocks': ['S'] } },
              { 'id': 'as_equipment', 'precedence': 20, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'category': 'OST_MechanicalEquipment', 'family_type': 'B: B', 'level': 'Level 1',
                'geometry': { 'from': 'blocks', 'blocks': ['S'] } }
            ]".Replace('\'', '"');

            CadBlockReading r = CadBlockRules.Interpret(
                new List<CadIrEntity> { Insert("S", 0, 0), Insert("S", 100, 0) }, Set(rules), Hash);

            Assert.Empty(r.Candidates);
            CadBlockTie tie = Assert.Single(r.Ties);
            Assert.Equal(2, tie.Count);
            Assert.Equal(new[] { "as_equipment", "as_fixture" }, tie.RuleIds);
        }

        [Fact]
        public void An_anonymous_block_is_believed_less_than_one_somebody_named()
        {
            string rules = @"[
              { 'id': 'everything', 'precedence': 20, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'category': 'OST_ElectricalFixtures', 'family_type': 'A: A', 'level': 'Level 1',
                'geometry': { 'from': 'blocks' } }
            ]".Replace('\'', '"');

            CadBlockReading r = CadBlockRules.Interpret(new List<CadIrEntity>
            {
                Insert("OUT2", 0, 0),
                Insert("A$C1E4116A0", 500, 0)
            }, Set(rules), Hash);

            Assert.Equal(2, r.Candidates.Count);
            Assert.True(r.Candidates[0].Confidence > r.Candidates[1].Confidence,
                "a name AutoCAD generated is not a statement about what the symbol is");
            Assert.True(CadBlockRules.IsAnonymous("A$C1E4116A0"));
            Assert.True(CadBlockRules.IsAnonymous("*U479"));
            Assert.False(CadBlockRules.IsAnonymous("XREF|OUT2"));
        }

        [Fact]
        public void A_rule_that_names_blocks_and_reads_line_work_is_refused_whole()
        {
            string rules = @"[
              { 'id': 'confused', 'precedence': 20, 'layers': ['E-*'], 'produces': 'electrical_fixture',
                'category': 'OST_ElectricalFixtures', 'family_type': 'A: A', 'level': 'Level 1',
                'geometry': { 'from': 'single_lines', 'blocks': ['OUT2'] } }
            ]".Replace('\'', '"');

            // The names would be silently ignored, and the author would believe
            // their mapping was in force.
            CadRequirementSetException bad = Assert.Throws<CadRequirementSetException>(() => Set(rules));
            Assert.Contains("only means something with", bad.Message);
        }

        // ---- against a real drawing -------------------------------------------

        /// <summary>
        /// The whole route on a REAL extraction: read the symbols, map them, and
        /// count the rows that come out - rows nobody wrote.
        ///
        /// Set HORIZUN_DWG_TSV to an extraction to run it. Client drawings are not
        /// in this repository and neither are their readings; what this writes is
        /// counts.
        /// </summary>
        [Fact]
        public void A_composed_rotation_stays_inside_one_turn()
        {
            // Four levels of nesting on a real drawing produced 675 degrees: the
            // same orientation as 315, and a number nobody can read.
            var parent = new CadIrEntity
            {
                Id = "p", Kind = CadEntityKind.BlockInstance, BlockName = "UNIT",
                Layer = "E", RotationRadians = Math.PI * 1.5, ScaleX = 1, ScaleY = 1, Handle = "P1"
            };
            parent.Points.Add(new CadPoint(0, 0, 0));
            var child = Insert("OUT2", 100, 0, rotation: Math.PI * 1.25);
            child.BlockPath.Add("UNIT");

            CadPlacementReading r = CadBlockPlacement.Place(new List<CadIrEntity> { parent, child });
            CadPlacedBlock placed = r.Placed.Single(p => p.BlockName == "OUT2");
            Assert.InRange(placed.RotationRadians, 0, 2 * Math.PI);
            Assert.Equal(Math.PI * 0.75, placed.RotationRadians, 6);
        }

        [Fact]
        public void A_real_drawings_symbols_become_rows_nobody_typed()
        {
            string path = Environment.GetEnvironmentVariable("HORIZUN_DWG_TSV");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Assert.True(true, "HORIZUN_DWG_TSV not set: this case checked nothing.");
                return;
            }

            CadDwgReading reading = CadDwgExtract.Parse(File.ReadAllLines(path));

            // NESTED SYMBOLS CARRY THEIR BLOCK'S COORDINATES, not the drawing's.
            // On this permit set every unit symbol is drawn inside a unit block, so
            // reading the raw insertion points would put the whole building at the
            // origin. Placement composes the chain; Distinct collapses the copies
            // that sit on one point and names them.
            CadPlacementReading placement = CadBlockPlacement.Place(reading.Entities);
            List<JObject> duplicates;
            List<CadPlacedBlock> distinct = CadBlockPlacement.Distinct(placement.Placed, 1.0, out duplicates);
            List<CadIrEntity> instances = distinct.Select(p => p.AsEntity()).ToList();
            Assert.True(instances.Count > 100, "this drawing has too few block instances to prove anything");

            // A mapping written from what the drawing ACTUALLY contains - the
            // vocabulary the unclaimed list reports on a first pass.
            string rules = @"[
              { 'id': 'receptacles', 'precedence': 30, 'layers': ['*'], 'produces': 'electrical_fixture',
                'category': 'OST_ElectricalFixtures', 'family_type': 'Duplex Receptacle: Standard',
                'level': 'Level 9',
                'geometry': { 'from': 'blocks', 'blocks': ['OUT2', 'OUT', 'OUT-A-C'] } },
              { 'id': 'switches', 'precedence': 30, 'layers': ['*'], 'produces': 'electrical_fixture',
                'category': 'OST_ElectricalFixtures', 'family_type': 'Switch: Single Pole',
                'level': 'Level 9',
                'geometry': { 'from': 'blocks', 'blocks': ['S'] } },
              { 'id': 'data', 'precedence': 30, 'layers': ['*'], 'produces': 'electrical_fixture',
                'category': 'OST_CommunicationDevices', 'family_type': 'Data Outlet: Standard',
                'level': 'Level 9',
                'geometry': { 'from': 'blocks', 'blocks': ['TEL-DATA', 'TV-1'] } },
              { 'id': 'meters', 'precedence': 30, 'layers': ['*'], 'produces': 'electrical_fixture',
                'category': 'OST_ElectricalEquipment', 'family_type': 'Meter: Unit',
                'level': 'Level 9',
                'geometry': { 'from': 'blocks', 'blocks': ['MTR'] } },
              { 'id': 'downlights', 'precedence': 30, 'layers': ['*'], 'produces': 'electrical_fixture',
                'category': 'OST_LightingFixtures', 'family_type': 'Downlight: 150mm',
                'level': 'Level 9',
                'geometry': { 'from': 'blocks', 'blocks': ['*DOWNLIGHT*', '*COOPER_LIGHTING*'] } },
              { 'id': 'vanity_lights', 'precedence': 30, 'layers': ['*'], 'produces': 'electrical_fixture',
                'category': 'OST_LightingFixtures', 'family_type': 'Vanity Light: 900mm',
                'level': 'Level 9',
                'geometry': { 'from': 'blocks', 'blocks': ['E-LTS-VANITY'] } },
              { 'id': 'panels', 'precedence': 30, 'layers': ['*'], 'produces': 'electrical_fixture',
                'category': 'OST_ElectricalEquipment', 'family_type': 'Panelboard: 42-circuit',
                'level': 'Level 9',
                'geometry': { 'from': 'blocks', 'blocks': ['PNL'] } },
              { 'id': 'appliances', 'precedence': 31, 'layers': ['*'], 'produces': 'mechanical_equipment',
                'category': 'OST_MechanicalEquipment', 'family_type': 'AHU-EWH: Unit',
                'level': 'Level 9',
                'geometry': { 'from': 'blocks', 'blocks': ['AHU-EWH', 'WM'] } }
            ]".Replace('\'', '"');

            CadRequirementSet set = Set(rules);
            CadBlockReading r = CadBlockRules.Interpret(instances, set, "real");

            Assert.NotEmpty(r.Candidates);
            Assert.NotEmpty(r.Unclaimed);

            var interp = new CadInterpretation();
            interp.Candidates.AddRange(r.Candidates);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interp, set, "real-src", false);

            int rows = CadConversionPlanRules.AsCreateRequests(plan, "Test.rvt")
                .Sum(q => ((JArray)q["elements"]).Count);

            Assert.Equal(r.Candidates.Count, rows);

            // Every row carries a point and a type, and no two carry the same
            // identity - which is what stops a second run building them again.
            Assert.Equal(plan.Actions.Count, plan.Actions.Select(a => a.SemanticId).Distinct().Count());

            // THE ROWS THEMSELVES, written out - so what goes to Revit is what this
            // route produced and not something retyped beside it.
            string outPath = Environment.GetEnvironmentVariable("HORIZUN_ROWS_OUT");
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                var requests = new JArray(CadConversionPlanRules
                    .AsCreateRequests(plan, "HZ_MEP_TEST")
                    .Select(q => (JToken)q));
                File.WriteAllText(outPath, requests.ToString(Newtonsoft.Json.Formatting.None));
                Console.WriteLine("ROWS WRITTEN: " + outPath + " (" + rows + " rows)");
            }

            Console.WriteLine("REAL ROUTE: " + new JObject
            {
                ["placement"] = placement.SummaryJson(),
                ["coincident_duplicates"] = new JArray(duplicates.Take(6).Select(d => (JToken)d)),
                ["block_instances"] = instances.Count,
                ["claimed"] = r.InstancesClaimed,
                ["coverage"] = Math.Round(r.Coverage, 4),
                ["candidates"] = r.Candidates.Count,
                ["rows_emitted"] = rows,
                ["unclaimed_names"] = r.Unclaimed.Count,
                ["top_unclaimed"] = new JArray(r.Unclaimed.Take(12).Select(u => (JToken)u.ToJson()))
            }.ToString(Newtonsoft.Json.Formatting.None));
        }
        // =====================================================================
        // Which SPACE a symbol was drawn in
        // =====================================================================

        /// <summary>
        /// MEASURED on the real electrical drawing: reading both spaces into one bag
        /// made the symbols cover 168 x 117 m against the 64 x 72 m the drawing
        /// occupies, because a sheet's legend and title block are the same blocks on
        /// the same layers at page coordinates. The plan converts model space; this
        /// fixes that the reading can still TELL them apart.
        /// </summary>
        [Fact]
        public void A_symbol_knows_which_space_it_was_drawn_in()
        {
            var inModel = Insert("OUT2", 1000, 1000);
            inModel.Space = "model";
            var onSheet = Insert("OUT2", 20, 15);
            onSheet.Space = "paper";

            CadPlacementReading r = CadBlockPlacement.Place(new List<CadIrEntity> { inModel, onSheet });

            Assert.Equal(2, r.Placed.Count);
            Assert.Single(r.Placed, p => p.Space == "model");
            Assert.Single(r.Placed, p => p.Space == "paper");
        }

        /// <summary>
        /// A symbol nested inside a block is in whatever space placed the OUTERMOST
        /// block - it cannot know on its own, and a legend built from nested blocks
        /// is still a legend.
        /// </summary>
        [Fact]
        public void A_nested_symbol_takes_the_space_of_the_block_that_placed_it()
        {
            var root = Insert("UNIT-A", 0, 0);
            root.Space = "paper";
            var nested = Insert("OUT2", 100, 100);
            nested.BlockPath.Add("UNIT-A");
            nested.Space = null;      // an entity inside a definition cannot know

            CadPlacementReading r = CadBlockPlacement.Place(new List<CadIrEntity> { root, nested });

            CadPlacedBlock symbol = r.Placed.Single(p => p.BlockName == "OUT2");
            Assert.Equal("paper", symbol.Space);
        }

        /// <summary>
        /// THE TWO HALVES OF THE PIPELINE, CHECKED AGAINST EACH OTHER.
        ///
        /// MEASURED on a real conversion: 21 rows produced from an electrical plan,
        /// 21 rows refused by horizun_create_elements - "flip is not applicable to
        /// kind 'family_instance'", "category is not applicable". Both halves were
        /// written here and neither had ever met the other, because every test until
        /// now checked the plan's rows against what the plan meant rather than
        /// against what the writer accepts.
        ///
        /// This asks the contract's own validator, which is the thing that refused.
        /// </summary>
        [Fact]
        public void Every_row_the_plan_emits_is_a_row_the_writer_accepts()
        {
            CadRequirementSet set = Set(Outlets);
            var reading = CadBlockRules.Interpret(new List<CadIrEntity>
            {
                Insert("OUT2", 1000, 2000, "E-PWR", Math.PI / 2),
                Insert("OUT2", 3000, 2000, "E-PWR", 0, -1),     // mirrored
                Insert("OUT", 5000, 2000, "E-PWR")
            }, set, Hash);

            Assert.NotEmpty(reading.Candidates);
            List<JObject> rows = CadConversionPlanRules.AsCreateRequests(
                CadConversionPlanRules.Plan(new CadInterpretation { Candidates = reading.Candidates },
                                            set, "src", false), "HZ_TEST");
            Assert.NotEmpty(rows);

            foreach (JObject row in rows)
            foreach (JObject element in (JArray)row["elements"])
            {
                string kind = (string)element["kind"];
                // The names the plan resolves away before the row is sent.
                var sent = (JObject)element.DeepClone();
                foreach (string resolved in new[] { "level_name", "type_name", "system_type_name",
                                                    "hosted_on", "host_point", "host_level_name" })
                    sent.Remove(resolved);
                if (sent["level_name"] == null && sent["level_id"] == null) sent["level_id"] = 1;
                if (sent["type_id"] == null) sent["type_id"] = 1;

                string refusal = ToolInputRules.ValidateCreation(sent, kind);
                Assert.True(refusal == null,
                    "the plan emits a row horizun_create_elements refuses: " + refusal +
                    " Row: " + sent.ToString(Newtonsoft.Json.Formatting.None));
            }
        }

    }
}
