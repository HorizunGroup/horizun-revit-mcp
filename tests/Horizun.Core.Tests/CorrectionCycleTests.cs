// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE CORRECTION CYCLE'S DECISIONS, proved by running them: the request shape,
// the selection against a recorded audit, narrowing that may never widen, the
// typed filter, inputs that answer a recipe's question, the stale check, and
// the re-audit's per-element verdicts.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CorrectionCycleTests
    {
        private const string Doc = "Tower";
        private const string DocFp = "doc-fp-1";
        private const string SetFp = "fs:abc";

        private static FindingSetRecord Audit()
        {
            var findings = new JArray(
                new JObject
                {
                    ["check"] = AuditCheckNames.UnpinnedLinks, ["finding_id"] = "f:links", ["is_issue"] = true,
                    ["shown"] = 2, ["total"] = 2,
                    ["items"] = new JArray(new JObject { ["element_id"] = "10" }, new JObject { ["element_id"] = "11" })
                },
                new JObject
                {
                    ["check"] = AuditCheckNames.ViewsWithoutTemplate, ["finding_id"] = "f:views", ["is_issue"] = true,
                    ["shown"] = 1, ["total"] = 1,
                    ["items"] = new JArray(new JObject { ["element_id"] = "30" })
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
                    ["check"] = AuditCheckNames.OrphanGroupTypes, ["finding_id"] = "f:groups", ["is_issue"] = true,
                    ["shown"] = 2, ["total"] = 2,
                    ["items"] = new JArray(new JObject { ["group_type_id"] = "50" }, new JObject { ["group_type_id"] = "51" })
                },
                new JObject
                {
                    ["check"] = AuditCheckNames.InPlaceFamilies, ["finding_id"] = "f:inplace", ["is_issue"] = true,
                    ["shown"] = 1, ["total"] = 1,
                    ["items"] = new JArray(new JObject { ["example_id"] = "60", ["family"] = "Stair" })
                },
                new JObject
                {
                    ["check"] = AuditCheckNames.Links, ["finding_id"] = "f:linktypes", ["is_issue"] = true,
                    ["shown"] = 2, ["total"] = 9,
                    ["items"] = new JArray(new JObject { ["id"] = "70", ["status"] = "Unloaded" },
                                           new JObject { ["id"] = "71", ["status"] = "Loaded" })
                },
                new JObject
                {
                    ["check"] = AuditCheckNames.Warnings, ["finding_id"] = "f:warn", ["is_issue"] = false,
                    ["shown"] = 0, ["total"] = 0, ["items"] = new JArray()
                });
            return FindingSetRecord.From(SetFp, Doc, DocFp, 20, "2026-01-01T00:00:00Z", findings);
        }

        private static JArray Actions(params JObject[] actions) => new JArray(actions);
        private static JObject Act(string id) => new JObject { ["finding_id"] = id };

        private static List<CorrectionAction> Select(params JObject[] actions)
            => CorrectionSelection.Select(Audit(), Actions(actions), CorrectionRegistry.Default);

        // ---------------------------------------------------------- the request

        [Fact]
        public void An_empty_selection_is_refused_rather_than_read_as_everything()
        {
            ScanRequestVerdict v = CorrectionRequestRules.Check(JObject.Parse(
                @"{ ""target_document"": ""T"", ""finding_set_fingerprint"": ""fs:x"", ""actions"": [] }"));
            Assert.False(v.Ok);
            Assert.Equal("empty_actions", v.Code);
            Assert.Contains("NOT 'every finding'", v.Message);
        }

        [Fact]
        public void Unknown_keys_at_either_level_and_a_duplicate_finding_are_refused_by_name()
        {
            Assert.Equal(ScanRequestCodes.UnknownKey, CorrectionRequestRules.Check(JObject.Parse(
                @"{ ""target_document"": ""T"", ""finding_set_fingerprint"": ""fs:x"", ""actions"": [{ ""finding_id"": ""f:1"" }], ""acions"": 1 }")).Code);
            Assert.Equal(ScanRequestCodes.UnknownKey, CorrectionRequestRules.Check(JObject.Parse(
                @"{ ""target_document"": ""T"", ""finding_set_fingerprint"": ""fs:x"", ""actions"": [{ ""finding_id"": ""f:1"", ""ids"": [1] }] }")).Code);
            ScanRequestVerdict dup = CorrectionRequestRules.Check(JObject.Parse(
                @"{ ""target_document"": ""T"", ""finding_set_fingerprint"": ""fs:x"", ""actions"": [{ ""finding_id"": ""f:1"" }, { ""finding_id"": ""f:1"" }] }"));
            Assert.False(dup.Ok);
            Assert.Contains("a second time", dup.Message);
        }

        [Fact]
        public void A_missing_fingerprint_an_empty_narrowing_and_a_non_integer_id_are_refused()
        {
            Assert.Equal("missing_finding_set_fingerprint", CorrectionRequestRules.Check(JObject.Parse(
                @"{ ""target_document"": ""T"", ""actions"": [{ ""finding_id"": ""f:1"" }] }")).Code);
            Assert.Contains("selects nothing", CorrectionRequestRules.Check(JObject.Parse(
                @"{ ""target_document"": ""T"", ""finding_set_fingerprint"": ""fs:x"", ""actions"": [{ ""finding_id"": ""f:1"", ""element_ids"": [] }] }")).Message);
            Assert.Contains("not an integer id", CorrectionRequestRules.Check(JObject.Parse(
                @"{ ""target_document"": ""T"", ""finding_set_fingerprint"": ""fs:x"", ""actions"": [{ ""finding_id"": ""f:1"", ""element_ids"": [""10""] }] }")).Message);
            Assert.True(CorrectionRequestRules.Check(JObject.Parse(
                @"{ ""target_document"": ""T"", ""finding_set_fingerprint"": ""fs:x"", ""actions"": [{ ""finding_id"": ""f:1"", ""element_ids"": [10], ""inputs"": { ""template_view_id"": 3 } }], ""dry_run"": true }")).Ok);
        }

        // ---------------------------------------------------------- selection

        [Fact]
        public void A_per_element_recipe_expands_to_one_typed_step_per_element()
        {
            CorrectionAction a = Select(Act("f:links")).Single();

            Assert.Equal(CorrectionActionState.Rehearsed, a.State);
            Assert.Equal("horizun_manage_links", a.Tool);
            Assert.Equal(2, a.Steps.Count);
            Assert.Equal(10L, (long)a.Steps[0].Arguments["link_instance_id"]);
            Assert.Equal("pin", (string)a.Steps[0].Arguments["operation"]);
            Assert.Equal("prop:f:links:11", a.Steps[1].ProposalId);
            // Rehearsal is the surface's flag; the recipe cannot turn it off.
            Assert.True((bool)a.Steps[0].Arguments["dry_run"]);
        }

        [Fact]
        public void A_list_recipe_makes_one_typed_delete_over_the_selected_ids()
        {
            CorrectionAction a = Select(new JObject
            {
                ["finding_id"] = "f:groups", ["element_ids"] = new JArray(50, 51)
            }).Single();

            Assert.Equal(CorrectionActionState.Rehearsed, a.State);
            Assert.Single(a.Steps);
            Assert.Equal("horizun_delete_verified", a.Steps[0].Tool);
            Assert.Equal("ids", (string)a.Steps[0].Arguments["mode"]);
            Assert.Equal(new long[] { 50, 51 }, a.Steps[0].Arguments["ids"].Select(t => (long)t).ToArray());
            Assert.Equal("high", a.Risk);
            Assert.False(a.Reversible);
        }

        [Fact]
        public void Narrowing_to_the_findings_own_elements_is_honoured_and_widening_is_refused()
        {
            var narrowed = new JObject { ["finding_id"] = "f:links", ["element_ids"] = new JArray(11) };
            CorrectionAction a = Select(narrowed).Single();
            Assert.Equal(CorrectionActionState.Rehearsed, a.State);
            Assert.Single(a.Steps);
            Assert.Equal(11L, (long)a.Steps[0].Arguments["link_instance_id"]);

            var widened = new JObject { ["finding_id"] = "f:links", ["element_ids"] = new JArray(11, 999) };
            CorrectionAction w = Select(widened).Single();
            Assert.Equal(CorrectionActionState.Unsafe, w.State);
            Assert.Equal(ProposalRefusal.ScopeWidened, w.RefusalCode);
            Assert.Contains("999", w.Why);
            Assert.Empty(w.Steps);
        }

        [Fact]
        public void The_typed_filter_excludes_the_unenclosed_room_and_names_it_as_excluded()
        {
            CorrectionAction a = Select(new JObject
            {
                ["finding_id"] = "f:rooms", ["element_ids"] = new JArray(40)
            }).Single();

            Assert.Equal(CorrectionActionState.Rehearsed, a.State);
            Assert.Equal(new long[] { 40 }, a.SelectedElementIds.ToArray());
            Assert.Equal(new long[] { 41 }, a.ExcludedElementIds.ToArray());
            Assert.Equal(new long[] { 40 }, a.Steps.Single().Arguments["ids"].Select(t => (long)t).ToArray());

            // Naming the excluded one explicitly is refused as outside the correction,
            // not as unknown to the finding.
            CorrectionAction e = Select(new JObject { ["finding_id"] = "f:rooms", ["element_ids"] = new JArray(41) }).Single();
            Assert.Equal(CorrectionActionState.RequiresInput, e.State);
            Assert.Contains("problem_code", e.Why);
        }

        [Fact]
        public void A_missing_required_input_is_requires_input_naming_it_while_the_rest_still_select()
        {
            List<CorrectionAction> actions = Select(Act("f:views"), Act("f:links"));

            CorrectionAction views = actions[0];
            Assert.Equal(CorrectionActionState.RequiresInput, views.State);
            Assert.Contains("template_view_id", views.Why);
            Assert.Empty(views.Steps);

            Assert.Equal(CorrectionActionState.Rehearsed, actions[1].State);
            Assert.False(CorrectionSelection.RehearsedCleanly(actions));
        }

        [Fact]
        public void The_input_answers_the_question_and_the_call_is_wrapped_as_manage_views_actions()
        {
            var act = new JObject { ["finding_id"] = "f:views", ["inputs"] = new JObject { ["template_view_id"] = 777 } };
            CorrectionAction a = Select(act).Single();

            Assert.Equal(CorrectionActionState.Rehearsed, a.State);
            JObject args = a.Steps.Single().Arguments;
            Assert.Equal(Doc, (string)args["target_document"]);
            Assert.True((bool)args["dry_run"]);
            var actions = (JArray)args["actions"];
            Assert.Single(actions);
            Assert.Equal("apply_template", (string)actions[0]["operation"]);
            Assert.Equal(30L, (long)actions[0]["view_id"]);
            Assert.Equal(777L, (long)actions[0]["template_view_id"]);
            Assert.Null(args["view_id"]);
            // The second ambiguity travels as a caveat rather than blocking.
            Assert.Contains(a.Caveats, c => c.Contains("OVERRIDES"));
        }

        [Fact]
        public void An_input_the_recipe_never_asked_for_is_refused()
        {
            var act = new JObject { ["finding_id"] = "f:links", ["inputs"] = new JObject { ["operation"] = "unpin" } };
            CorrectionAction a = Select(act).Single();
            Assert.Equal(CorrectionActionState.RequiresInput, a.State);
            Assert.Contains("not one", a.Why);
            Assert.Empty(a.Steps);
        }

        [Fact]
        public void Unknown_resolved_unsupported_and_truncated_findings_each_get_their_own_state()
        {
            List<CorrectionAction> actions = Select(Act("f:nope"), Act("f:warn"), Act("f:inplace"), Act("f:linktypes"));

            Assert.Equal(CorrectionActionState.UnknownFinding, actions[0].State);
            Assert.Contains(FindingIdentity.TopMeans, actions[0].Why);
            Assert.Equal(CorrectionActionState.AlreadyResolved, actions[1].State);
            Assert.Equal(CorrectionActionState.Unsupported, actions[2].State);
            Assert.Contains("MODELLED again", actions[2].Why);
            // 2 of 9 link types shown: the scope is unknown, even though one is Unloaded.
            Assert.Equal(CorrectionActionState.RequiresInput, actions[3].State);
            Assert.Equal(ProposalRefusal.Truncated, actions[3].RefusalCode);
        }

        [Fact]
        public void Skipped_lists_the_issue_findings_nobody_named_and_never_the_clean_ones()
        {
            List<string> skipped = CorrectionSelection.Skipped(Audit(), Actions(Act("f:links"), Act("f:nope")));
            Assert.Equal(new[] { "f:views", "f:rooms", "f:groups", "f:inplace", "f:linktypes" }, skipped.ToArray());
            Assert.DoesNotContain("f:warn", skipped);
        }

        [Fact]
        public void The_token_is_withheld_until_every_step_rehearsed_cleanly()
        {
            List<CorrectionAction> actions = Select(Act("f:links"));
            Assert.False(CorrectionSelection.RehearsedCleanly(actions));
            actions[0].Steps[0].RehearsalOk = true;
            Assert.False(CorrectionSelection.RehearsedCleanly(actions));
            actions[0].Steps[1].RehearsalOk = true;
            Assert.True(CorrectionSelection.RehearsedCleanly(actions));
            Assert.False(CorrectionSelection.RehearsedCleanly(new List<CorrectionAction>()));
        }

        // ---------------------------------------------------------- staleness

        [Fact]
        public void A_re_run_check_that_no_longer_reproduces_the_finding_id_is_drift_and_a_dead_check_is_named()
        {
            FindingSetRecord audit = Audit();
            var same = new Dictionary<string, string> { { AuditCheckNames.UnpinnedLinks, "f:links" } };
            Assert.Null(FindingSetDrift.Describe(audit, new[] { AuditCheckNames.UnpinnedLinks }, same, new List<string>()));

            var moved = new Dictionary<string, string> { { AuditCheckNames.UnpinnedLinks, "f:other" } };
            string drift = FindingSetDrift.Describe(audit, new[] { AuditCheckNames.UnpinnedLinks }, moved, new List<string>());
            Assert.Contains("f:other", drift);
            Assert.Contains("approved: f:links", drift);

            string dead = FindingSetDrift.Describe(audit, new[] { AuditCheckNames.UnpinnedLinks }, same,
                                                   new List<string> { AuditCheckNames.UnpinnedLinks });
            Assert.Contains("could not be re-run", dead);
        }

        // ---------------------------------------------------------- re-audit

        private static CorrectionAction Applied(bool ok, params long[] ids)
        {
            CorrectionAction a = Select(new JObject { ["finding_id"] = "f:links", ["element_ids"] = new JArray(ids.Select(x => (JToken)x)) }).Single();
            foreach (CorrectionStep s in a.Steps) s.ApplyOk = ok;
            return a;
        }

        private static JObject Fresh(bool truncated, params long[] stillListed)
        {
            return new JObject
            {
                ["check"] = AuditCheckNames.UnpinnedLinks, ["finding_id"] = "f:after", ["is_issue"] = stillListed.Length > 0,
                ["count"] = stillListed.Length, ["truncated"] = truncated,
                ["items"] = new JArray(stillListed.Select(id => (JToken)new JObject { ["element_id"] = id.ToString() }))
            };
        }

        [Fact]
        public void Corrected_means_every_selected_element_is_gone_from_the_re_run_finding()
        {
            JObject r = ReAuditRules.Compare(Applied(true, 10, 11), Fresh(false), false);
            Assert.Equal(ReAuditOutcome.Corrected, (string)r["outcome"]);
            Assert.Equal(2, (int)r["counts"]["corrected"]);
            Assert.Equal("f:after", (string)r["after"]["finding_id"]);
        }

        [Fact]
        public void Persistent_means_the_audit_still_lists_it_whatever_the_typed_call_said()
        {
            JObject r = ReAuditRules.Compare(Applied(true, 10, 11), Fresh(false, 11), false);
            Assert.Equal(ReAuditOutcome.Persistent, (string)r["outcome"]);
            Assert.Equal(new long[] { 10 }, r["elements"]["corrected"].Select(t => (long)t).ToArray());
            Assert.Equal(new long[] { 11 }, r["elements"]["persistent"].Select(t => (long)t).ToArray());
            Assert.Contains("the audit is the judge", (string)r["why"]);
        }

        [Fact]
        public void Failed_means_no_typed_call_applied_and_the_elements_are_listed_as_failed()
        {
            JObject r = ReAuditRules.Compare(Applied(false, 10), Fresh(false, 10), false);
            Assert.Equal(ReAuditOutcome.Failed, (string)r["outcome"]);
            Assert.Equal(1, (int)r["counts"]["failed"]);
        }

        [Fact]
        public void Not_verifiable_when_the_check_died_or_the_list_was_cut_and_the_element_is_past_the_cut()
        {
            JObject dead = ReAuditRules.Compare(Applied(true, 10), null, true);
            Assert.Equal(ReAuditOutcome.NotVerifiable, (string)dead["outcome"]);
            Assert.True((bool)dead["after"]["check_failed"]);

            JObject cut = ReAuditRules.Compare(Applied(true, 10), Fresh(true, 11), false);
            Assert.Equal(ReAuditOutcome.NotVerifiable, (string)cut["outcome"]);
            Assert.Contains("larger top", (string)cut["why"]);
        }

        [Fact]
        public void The_reply_rows_carry_state_why_scope_and_every_step()
        {
            List<CorrectionAction> actions = Select(Act("f:links"), Act("f:views"));
            JObject row = CorrectionReply.ActionJson(actions[0]);
            Assert.Equal("rehearsed", (string)row["state"]);
            Assert.Equal(2, ((JArray)row["steps"]).Count);
            Assert.Equal(2, ((JArray)row["selected_element_ids"]).Count);
            JObject tally = CorrectionReply.Tally(actions);
            Assert.Equal(1, (int)tally["rehearsed"]);
            Assert.Equal(1, (int)tally["requires_input"]);
            Assert.Equal(new[] { AuditCheckNames.UnpinnedLinks, AuditCheckNames.ViewsWithoutTemplate },
                         CorrectionSelection.ChecksOf(actions).ToArray());
        }
    }
}
