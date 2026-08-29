// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THE ACTIONS ARE PART OF THE BINDING, or the binding means nothing.
//
// The defect these were written against: horizun_apply_cad_plan received
// apply_binding and the actions SEPARATELY, checked the drawing and the rules,
// and then applied whatever actions arrived. A caller could take a legitimate
// binding from a real plan and send different coordinates, a different family
// type, extra elements or fewer - and every check passed, because nothing the
// command verified covered the thing it was about to build.
//
// So the plan now fingerprints the EXACT actions it emitted, the apply
// recomputes that fingerprint over what actually arrived, and any difference is
// a refusal. These tests are that promise.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadActionsBindingTests
    {
        private static JArray Actions() => JArray.Parse(@"[
          { 'key': 'cad-stage-1-batch-0', 'tool': 'horizun_create_elements', 'arguments': {
              'target_document': 'HZ_TARGET', 'units': 'mm', 'stage': 1, 'batch_of_stage': 0,
              'elements': [
                { 'kind': 'wall', 'start': [0,0,0], 'end': [6000,0,0], 'height': 3000, 'type_name': 'Basic Wall: Generic - 200mm' }
              ] } },
          { 'key': 'cad-stage-2-batch-0', 'tool': 'horizun_create_elements', 'arguments': {
              'target_document': 'HZ_TARGET', 'units': 'mm', 'stage': 2, 'batch_of_stage': 0,
              'elements': [
                { 'kind': 'floor', 'profile': [[0,0,0],[4000,0,0],[4000,3000,0],[0,3000,0]] }
              ] } }
        ]".Replace('\'', '"'));

        private static string Fp(JArray a) => CadConversionPlanRules.ActionsFingerprint(a);

        [Fact]
        public void The_same_actions_fingerprint_the_same_way()
        {
            Assert.Equal(Fp(Actions()), Fp(Actions()));
        }

        [Fact]
        public void Reformatting_the_json_does_not_change_the_fingerprint()
        {
            JArray reparsed = JArray.Parse(Actions().ToString(Newtonsoft.Json.Formatting.Indented));
            Assert.Equal(Fp(Actions()), Fp(reparsed));
        }

        [Fact]
        public void Reordering_the_keys_inside_an_action_does_not_change_it()
        {
            // Property order is a serializer's business, not a change to the plan.
            JArray a = Actions();
            var original = (JObject)a[0]["arguments"];
            var reordered = new JObject();
            foreach (JProperty prop in original.Properties().Reverse()) reordered[prop.Name] = prop.Value;
            a[0]["arguments"] = reordered;
            Assert.Equal(Fp(Actions()), Fp(a));
        }

        [Fact]
        public void MOVING_A_WALL_CHANGES_THE_FINGERPRINT()
        {
            // The attack this exists to stop: a legitimate binding, a wall at
            // different coordinates.
            JArray tampered = Actions();
            tampered[0]["arguments"]["elements"][0]["end"] = new JArray(9000, 0, 0);
            Assert.NotEqual(Fp(Actions()), Fp(tampered));
        }

        [Fact]
        public void CHANGING_THE_FAMILY_TYPE_CHANGES_THE_FINGERPRINT()
        {
            JArray tampered = Actions();
            tampered[0]["arguments"]["elements"][0]["type_name"] = "Basic Wall: Exterior - Brick on CMU";
            Assert.NotEqual(Fp(Actions()), Fp(tampered));
        }

        [Fact]
        public void ADDING_AN_ELEMENT_CHANGES_THE_FINGERPRINT()
        {
            JArray tampered = Actions();
            ((JArray)tampered[0]["arguments"]["elements"]).Add(JObject.Parse(
                "{\"kind\":\"wall\",\"start\":[0,9000,0],\"end\":[6000,9000,0],\"height\":3000}"));
            Assert.NotEqual(Fp(Actions()), Fp(tampered));
        }

        [Fact]
        public void REMOVING_AN_ELEMENT_CHANGES_THE_FINGERPRINT()
        {
            JArray tampered = Actions();
            ((JArray)tampered[0]["arguments"]["elements"]).RemoveAt(0);
            Assert.NotEqual(Fp(Actions()), Fp(tampered));
        }

        [Fact]
        public void REMOVING_A_WHOLE_ACTION_CHANGES_THE_FINGERPRINT()
        {
            JArray tampered = Actions();
            tampered.RemoveAt(1);
            Assert.NotEqual(Fp(Actions()), Fp(tampered));
        }

        [Fact]
        public void CHANGING_THE_TARGET_DOCUMENT_CHANGES_THE_FINGERPRINT()
        {
            // A plan rehearsed against one model must not be applied to another.
            JArray tampered = Actions();
            tampered[0]["arguments"]["target_document"] = "SOMEBODY_ELSES_MODEL";
            Assert.NotEqual(Fp(Actions()), Fp(tampered));
        }

        [Fact]
        public void CHANGING_THE_UNITS_CHANGES_THE_FINGERPRINT()
        {
            // 6000 mm and 6000 feet are different buildings.
            JArray tampered = Actions();
            tampered[0]["arguments"]["units"] = "feet";
            Assert.NotEqual(Fp(Actions()), Fp(tampered));
        }

        [Fact]
        public void REORDERING_THE_STAGES_CHANGES_THE_FINGERPRINT()
        {
            // Order is a dependency here, so it is part of what was agreed.
            JArray tampered = Actions();
            var first = tampered[0];
            tampered.RemoveAt(0);
            tampered.Add(first);
            Assert.NotEqual(Fp(Actions()), Fp(tampered));
        }

        [Fact]
        public void An_unknown_field_smuggled_into_an_action_changes_the_fingerprint()
        {
            // This used to use dry_run as its example, and the live chain showed
            // that was the wrong choice: dry_run says whether the call writes at
            // all, not what it builds, and covering it made the rehearsal's own
            // token impossible to send back. The ASSERTION is unchanged - a field
            // nobody declared still moves the fingerprint - only the example,
            // which is now something that really is unaccounted for.
            JArray tampered = Actions();
            ((JObject)tampered[0]["arguments"]).Add("workset_id", 7);
            Assert.NotEqual(Fp(Actions()), Fp(tampered));
        }

        [Fact]
        public void An_empty_action_list_still_fingerprints_rather_than_throwing()
        {
            Assert.False(string.IsNullOrWhiteSpace(Fp(new JArray())));
            Assert.NotEqual(Fp(new JArray()), Fp(Actions()));
        }

        [Fact]
        public void The_fingerprint_is_prefixed_so_it_cannot_be_confused_with_another_hash()
        {
            Assert.StartsWith("cadacts:", Fp(Actions()));
        }

        [Fact]
        public void The_plan_publishes_the_fingerprint_of_the_actions_it_actually_emitted()
        {
            // End to end: whatever AsCreateRequests produced is what the binding
            // covers, so a caller that sends it back unchanged is accepted and a
            // caller that edits it is not.
            CadRequirementSet set = CadRequirementSet.Load(JObject.Parse(@"{
              'schema': 'horizun.cad-requirements/1',
              'requirement_set': { 'id': 'demo', 'version': '1', 'title': 't' },
              'source': { 'units': 'millimeter' },
              'tolerances': { 'point_mm': 1, 'gap_mm': 25, 'angle_degrees': 2, 'arc_sagitta_mm': 5 },
              'rules': [{ 'id': 'walls', 'layers': ['A-WALL*'], 'produces': 'wall', 'height_mm': 3000,
                          'category': 'OST_Walls', 'family_type': 'Basic Wall: Generic - 200mm',
                          'geometry': { 'from': 'double_lines', 'min_thickness_mm': 80, 'max_thickness_mm': 500 } }]
            }".Replace('\'', '"')));
            var segs = new List<CadSegment>
            {
                new CadSegment(new CadPoint(0, 0), new CadPoint(6000, 0), "A-WALL-EXTR"),
                new CadSegment(new CadPoint(0, 200), new CadPoint(6000, 200), "A-WALL-EXTR")
            };
            CadConversionPlan plan = CadConversionPlanRules.Plan(
                CadInterpretationRules.Interpret(segs, set, "sha"), set, "cadsrc:x");

            List<JObject> creates = CadConversionPlanRules.AsCreateRequests(plan, "HZ_TARGET");
            JArray emitted = new JArray(creates.Select((c, i) => new JObject
            {
                ["key"] = "cad-stage-" + (int)c["stage"] + "-batch-" + (int)c["batch_of_stage"],
                ["tool"] = "horizun_create_elements",
                ["arguments"] = c
            }));

            string published = CadConversionPlanRules.ActionsFingerprint(emitted);
            Assert.Equal(published, CadConversionPlanRules.ActionsFingerprint(
                JArray.Parse(emitted.ToString(Newtonsoft.Json.Formatting.None))));

            JArray tampered = JArray.Parse(emitted.ToString());
            tampered[0]["arguments"]["elements"][0]["height"] = 2400;
            Assert.NotEqual(published, CadConversionPlanRules.ActionsFingerprint(tampered));
        }

        // ---------------------------------------------------------------------
        // WHAT IS BUILT vs HOW THE CALL IS MADE
        // ---------------------------------------------------------------------

        private static JArray OneAction(string extraOnAction = null, string extraOnArgs = null)
        {
            var args = new JObject
            {
                ["target_document"] = "M",
                ["units"] = "mm",
                ["elements"] = new JArray(new JObject
                {
                    ["kind"] = "wall",
                    ["start"] = new JArray(0, 0, 0),
                    ["end"] = new JArray(6000, 0, 0),
                    ["level_id"] = 1234,
                    ["height"] = 3000
                })
            };
            if (extraOnArgs != null) args[extraOnArgs] = "x";
            var action = new JObject { ["key"] = "k", ["tool"] = "horizun_create_elements", ["arguments"] = args };
            if (extraOnAction != null) action[extraOnAction] = "x";
            return new JArray(action);
        }

        [Fact]
        public void The_rehearsals_token_does_not_change_what_is_built()
        {
            // MEASURED on the live chain, 2026-08-27. The rehearsal issues a
            // confirmation token, the caller sends it back with the real apply,
            // and a fingerprint over every byte then declared the actions had
            // moved - so the two-phase apply this bridge is built around could
            // never be completed. Nothing about the model had changed.
            string bare = CadConversionPlanRules.ActionsFingerprint(OneAction());
            Assert.Equal(bare, CadConversionPlanRules.ActionsFingerprint(OneAction(extraOnAction: "confirmation_token")));
            Assert.Equal(bare, CadConversionPlanRules.ActionsFingerprint(OneAction(extraOnArgs: "confirmation_token")));
            Assert.Equal(bare, CadConversionPlanRules.ActionsFingerprint(OneAction(extraOnArgs: "dry_run")));
            Assert.Equal(bare, CadConversionPlanRules.ActionsFingerprint(OneAction(extraOnArgs: "idempotency_key")));
        }

        [Fact]
        public void Everything_that_DOES_change_what_is_built_still_moves_the_fingerprint()
        {
            string bare = CadConversionPlanRules.ActionsFingerprint(OneAction());

            JArray moved = OneAction();
            ((JArray)moved[0]["arguments"]["elements"][0]["end"])[0] = 6001;
            Assert.NotEqual(bare, CadConversionPlanRules.ActionsFingerprint(moved));

            JArray retyped = OneAction();
            moved = retyped;
            ((JObject)retyped[0]["arguments"]["elements"][0])["type_id"] = 99;
            Assert.NotEqual(bare, CadConversionPlanRules.ActionsFingerprint(retyped));

            JArray relevelled = OneAction();
            ((JObject)relevelled[0]["arguments"]["elements"][0])["level_id"] = 4321;
            Assert.NotEqual(bare, CadConversionPlanRules.ActionsFingerprint(relevelled));

            JArray extra = OneAction();
            ((JArray)extra[0]["arguments"]["elements"]).Add(new JObject { ["kind"] = "wall" });
            Assert.NotEqual(bare, CadConversionPlanRules.ActionsFingerprint(extra));

            JArray retargeted = OneAction();
            retargeted[0]["arguments"]["target_document"] = "OTHER";
            Assert.NotEqual(bare, CadConversionPlanRules.ActionsFingerprint(retargeted));
        }

        [Fact]
        public void A_key_of_the_same_name_on_an_ELEMENT_row_is_still_part_of_what_is_built()
        {
            // The exclusion is scoped to the action and its top-level arguments.
            // Deeper down, a key called dry_run is data about an element, and
            // ignoring it would be a hole in the binding rather than a fix.
            JArray a = OneAction();
            ((JObject)a[0]["arguments"]["elements"][0])["dry_run"] = "smuggled";
            Assert.NotEqual(CadConversionPlanRules.ActionsFingerprint(OneAction()),
                            CadConversionPlanRules.ActionsFingerprint(a));
        }
    }
}
