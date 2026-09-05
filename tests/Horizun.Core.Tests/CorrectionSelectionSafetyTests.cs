// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// FOUR THINGS THE CORRECTION CYCLE GOT WRONG, each proved by running it.
//
// Every one was found by writing the live harness that would have to demonstrate
// the published behaviour against a real Revit, and discovering that the
// published behaviour was not what the code did:
//
//   * required_inputs came back EMPTY on the one row where a client reads it -
//     the action whose state is requires_input. The prose named the input; the
//     machine-readable field did not.
//   * a DELETE with no element_ids acted on every element the finding named,
//     while the registry's own destructive_means said "narrowed to the ids the
//     caller listed". An absent selection is not "all of them" - that is the
//     same argument `actions: []` is refused with, one level down and with an
//     irreversible delete on the other side.
//   * a caller who narrowed to ids the finding LISTED was refused because the
//     finding's TAIL was cut. The scope of "these two views" is those two views
//     whatever happened past the cut, and without this no model with more views
//     than `top` could have a single one corrected.
//   * the rehearsal declaration counted its total in STEPS and its unresolved
//     column in ACTIONS, so the two did not reconcile.
//
// Revit-free, like everything they decide.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CorrectionSelectionSafetyTests
    {
        private const string Doc = "Tower";
        private const string DocFp = "doc-fp-1";
        private const string SetFp = "fs:abc";

        /// <summary>
        /// An audit with one of each shape the fixes are about: a destructive
        /// finding, a destructive finding with a typed filter, a finding that asks
        /// for an input, and a finding whose list was CUT at top.
        /// </summary>
        private static FindingSetRecord Audit()
        {
            var findings = new JArray(
                new JObject
                {
                    ["check"] = AuditCheckNames.OrphanGroupTypes, ["finding_id"] = "f:groups", ["is_issue"] = true,
                    ["shown"] = 3, ["total"] = 3,
                    ["items"] = new JArray(new JObject { ["group_type_id"] = "50" },
                                           new JObject { ["group_type_id"] = "51" },
                                           new JObject { ["group_type_id"] = "52" })
                },
                new JObject
                {
                    ["check"] = AuditCheckNames.Rooms, ["finding_id"] = "f:rooms", ["is_issue"] = true,
                    ["shown"] = 2, ["total"] = 2,
                    ["items"] = new JArray(
                        new JObject { ["id"] = "40", ["problem_code"] = RoomProblemCode.Unplaced },
                        new JObject { ["id"] = "41", ["problem_code"] = RoomProblemCode.NotEnclosed })
                },
                new JObject
                {
                    ["check"] = AuditCheckNames.ViewsWithoutTemplate, ["finding_id"] = "f:views", ["is_issue"] = true,
                    ["shown"] = 2, ["total"] = 2,
                    ["items"] = new JArray(new JObject { ["element_id"] = "30" },
                                           new JObject { ["element_id"] = "31" })
                },
                // CUT AT TOP: 2 of 40 views listed.
                new JObject
                {
                    ["check"] = AuditCheckNames.UnpinnedLinks, ["finding_id"] = "f:cut", ["is_issue"] = true,
                    ["shown"] = 2, ["total"] = 40,
                    ["items"] = new JArray(new JObject { ["element_id"] = "10" },
                                           new JObject { ["element_id"] = "11" })
                });
            return FindingSetRecord.From(SetFp, Doc, DocFp, 2, "2026-01-01T00:00:00Z", findings);
        }

        private static List<CorrectionAction> Select(params JObject[] actions)
            => CorrectionSelection.Select(Audit(), new JArray(actions), CorrectionRegistry.Default);

        private static JObject Act(string id) => new JObject { ["finding_id"] = id };

        private static JObject Act(string id, params long[] ids)
            => new JObject { ["finding_id"] = id, ["element_ids"] = new JArray(ids.Select(x => (JToken)x)) };

        // ------------------------------------------------- required_inputs, named

        [Fact]
        public void A_requires_input_action_names_the_input_in_the_field_and_not_only_in_the_prose()
        {
            CorrectionAction a = Select(Act("f:views")).Single();

            Assert.Equal(CorrectionActionState.RequiresInput, a.State);
            Assert.Contains("template_view_id", a.Why);
            // THE POINT. This used to be an empty array on exactly this row.
            Assert.Equal(new[] { "template_view_id" }, a.RequiredInputs.ToArray());
            Assert.Equal("horizun_manage_views", a.Tool);
            Assert.Empty(a.Steps);

            JObject row = CorrectionReply.ActionJson(a);
            Assert.Equal(new[] { "template_view_id" },
                         ((JArray)row["required_inputs"]).Select(t => (string)t).ToArray());
        }

        [Fact]
        public void The_answered_action_still_publishes_the_input_it_was_answered_with()
        {
            var act = new JObject
            {
                ["finding_id"] = "f:views",
                ["element_ids"] = new JArray(30),
                ["inputs"] = new JObject { ["template_view_id"] = 777 }
            };
            CorrectionAction a = Select(act).Single();

            Assert.Equal(CorrectionActionState.Rehearsed, a.State);
            Assert.Equal(new[] { "template_view_id" }, a.RequiredInputs.ToArray());
        }

        // --------------------------------------------- a delete names its elements

        [Fact]
        public void A_delete_with_no_element_ids_is_requires_input_rather_than_all_of_them()
        {
            CorrectionAction a = Select(Act("f:groups")).Single();

            Assert.Equal(CorrectionActionState.RequiresInput, a.State);
            Assert.Equal(ProposalRefusal.BadArguments, a.RefusalCode);
            Assert.Equal("horizun_delete_verified", a.Tool);
            Assert.Contains("element_ids", a.RequiredInputs);
            Assert.Contains("must LIST the element_ids", a.Why);
            // AND NOTHING WAS PLANNED. A refusal that still assembled the call would
            // leave the ids one bug away from being deleted.
            Assert.Empty(a.Steps);
            Assert.Empty(a.SelectedElementIds);
        }

        [Fact]
        public void The_refusal_says_how_many_elements_are_eligible_so_the_caller_can_choose()
        {
            // The rooms finding lists two problems and only one is deletable; the
            // refusal counts the eligible ones and says the other is filtered out.
            CorrectionAction a = Select(Act("f:rooms")).Single();

            Assert.Equal(CorrectionActionState.RequiresInput, a.State);
            Assert.Contains("names 1 element(s)", a.Why);
            Assert.Contains("excluded by its typed filter", a.Why);
            Assert.Equal(new long[] { 41 }, a.ExcludedElementIds.ToArray());
        }

        [Fact]
        public void Listing_the_ids_makes_the_same_delete_actionable_over_exactly_those_ids()
        {
            CorrectionAction a = Select(Act("f:groups", 51)).Single();

            Assert.Equal(CorrectionActionState.Rehearsed, a.State);
            Assert.Equal(new long[] { 51 }, a.SelectedElementIds.ToArray());
            Assert.Equal(new long[] { 51 }, a.Steps.Single().Arguments["ids"].Select(t => (long)t).ToArray());
        }

        [Fact]
        public void A_reversible_correction_keeps_the_old_default_of_every_element_the_finding_named()
        {
            // Pinning is not deleting. Naming the finding is a decision about the
            // elements it named, and the recipe says so by not asking for the list.
            Assert.False(CorrectionRegistry.Default[AuditCheckNames.UnpinnedLinks].RequiresExplicitSelection);
            Assert.True(CorrectionRegistry.Default[AuditCheckNames.OrphanGroupTypes].RequiresExplicitSelection);
            Assert.True(CorrectionRegistry.Default[AuditCheckNames.Rooms].RequiresExplicitSelection);
        }

        [Fact]
        public void The_registry_publishes_which_entries_demand_an_explicit_selection()
        {
            JObject described = CorrectionRegistry.Describe();
            var entries = (JArray)described["entries"];
            JObject groups = entries.OfType<JObject>()
                                    .Single(e => (string)e["finding_type"] == AuditCheckNames.OrphanGroupTypes);
            Assert.True((bool)groups["requires_explicit_selection"]);
            JObject links = entries.OfType<JObject>()
                                   .Single(e => (string)e["finding_type"] == AuditCheckNames.UnpinnedLinks);
            Assert.False((bool)links["requires_explicit_selection"]);
            Assert.Contains("LISTING THE IDS IS REQUIRED", (string)described["destructive_means"]);
        }

        // ------------------------------------------- a cut list and a named scope

        [Fact]
        public void A_finding_cut_at_top_is_still_unknown_in_scope_when_nobody_narrowed()
        {
            CorrectionAction a = Select(Act("f:cut")).Single();

            Assert.Equal(CorrectionActionState.RequiresInput, a.State);
            Assert.Equal(ProposalRefusal.Truncated, a.RefusalCode);
            Assert.Empty(a.Steps);
        }

        [Fact]
        public void Narrowing_to_ids_the_cut_list_did_show_is_actionable_because_that_scope_is_known()
        {
            CorrectionAction a = Select(Act("f:cut", 11)).Single();

            Assert.Equal(CorrectionActionState.Rehearsed, a.State);
            Assert.Single(a.Steps);
            Assert.Equal(11L, (long)a.Steps[0].Arguments["link_instance_id"]);
        }

        [Fact]
        public void Narrowing_past_a_cut_list_is_still_refused_as_widening()
        {
            // 39 of the 40 unpinned links were never shown. Naming one of them is
            // naming an element this audit did not list, cut or not.
            CorrectionAction a = Select(Act("f:cut", 12)).Single();

            Assert.Equal(CorrectionActionState.Unsafe, a.State);
            Assert.Equal(ProposalRefusal.ScopeWidened, a.RefusalCode);
            Assert.Empty(a.Steps);
        }

        // --------------------------------------------------- one call, many states

        [Fact]
        public void One_call_can_hold_a_refusal_and_a_rehearsal_and_each_keeps_its_own_verdict()
        {
            List<CorrectionAction> actions = Select(Act("f:views"), Act("f:cut", 10, 11));

            Assert.Equal(CorrectionActionState.RequiresInput, actions[0].State);
            Assert.Equal(CorrectionActionState.Rehearsed, actions[1].State);
            Assert.Equal(2, actions[1].Steps.Count);
            // AND THE WHOLE TOKEN IS WITHHELD. A token over "the ones that worked"
            // authorises a set nobody read as such.
            Assert.False(CorrectionSelection.RehearsedCleanly(actions));

            JObject tally = CorrectionReply.Tally(actions);
            Assert.Equal(1, (int)tally["requires_input"]);
            Assert.Equal(1, (int)tally["rehearsed"]);
        }

        [Fact]
        public void The_rehearsal_declaration_counts_actions_in_both_columns_so_the_numbers_reconcile()
        {
            // ONE action with two steps, one action that refused. Counting the total
            // in steps and the unresolved column in actions read "2 requested, 1
            // unresolved" over a call with two actions - and the unresolved one had
            // contributed nothing to the 2.
            List<CorrectionAction> actions = Select(Act("f:cut", 10, 11), Act("f:views"));
            var payload = new JObject();
            ApplicationOutcome.StampRehearsal(payload, actions.Count,
                actions.Count(a => a.State != CorrectionActionState.Rehearsed), 0, 0);

            JObject declaration = (JObject)payload[ApplicationOutcome.Key];
            Assert.Equal(2, (int)declaration["requested"]);
            Assert.Equal(1, (int)declaration["unresolved"]);
            Assert.Equal(ApplicationOutcome.Name(ApplicationState.Partial), (string)declaration["state"]);
        }
    }
}
