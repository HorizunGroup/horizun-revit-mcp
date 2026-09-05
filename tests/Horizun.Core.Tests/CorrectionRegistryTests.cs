// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REGISTRY IS THE SAFETY MODEL, so it is the thing tested hardest.
//
// A correction surface is dangerous in exactly one way: a tool name and an
// argument object assembled from a finding's text. Everything here is about
// that not being possible - the tool must be in the list, the constants come
// from the list, and the two arguments that decide WHERE and WHETHER (the
// target document and the rehearsal flag) cannot be set by a registry entry at
// all.
//
// The four entries also carry the honest ratio: one correction that can be
// made, one that needs an input nobody supplied, and two that say no and say
// why. Most audit findings are not mechanically correctable.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CorrectionRegistryTests
    {
        private const string Doc = "Tower";
        private const string Fp = "fp-1";

        private static Finding F(string type, params long[] ids)
        {
            return new Finding
            {
                FindingId = type,
                FindingType = type,
                DocumentTitle = Doc,
                DocumentFingerprint = Fp,
                ElementIds = ids.ToList()
            };
        }

        private static CorrectionProposal Propose(Finding f)
        {
            return GuidedCorrectionRules.Propose(f, CorrectionRegistry.Default, Doc, Fp, null, null);
        }

        [Fact]
        public void An_unpinned_link_becomes_one_actionable_typed_call_per_link()
        {
            CorrectionProposal p = Propose(F(AuditCheckNames.UnpinnedLinks, 4242));

            Assert.Equal(ProposalState.Actionable, p.State);
            Assert.Equal("horizun_manage_links", p.Tool);
            Assert.Equal("pin", (string)p.Arguments["operation"]);
            Assert.Equal(4242L, (long)p.Arguments["link_instance_id"]);
            Assert.Equal(Doc, (string)p.Arguments["target_document"]);
            // PROPOSED, NEVER PERFORMED.
            Assert.True((bool)p.Arguments["dry_run"]);
            Assert.True(p.ConfirmationRequired);
        }

        [Fact]
        public void A_single_element_tool_handed_four_elements_refuses_rather_than_taking_the_first()
        {
            CorrectionProposal p = Propose(F(AuditCheckNames.UnpinnedLinks, 1, 2, 3, 4));

            Assert.Equal(ProposalState.RequiresInput, p.State);
            Assert.Equal(ProposalRefusal.BadArguments, p.RefusalCode);
            Assert.Contains("acts on one element", p.Why);
            // Acting on the first would narrow the correction to a scope nobody chose.
            Assert.Null(p.Arguments?["link_instance_id"]);
        }

        [Fact]
        public void A_finding_that_names_no_element_says_the_elements_are_missing_not_an_argument()
        {
            // An issue with no element ids usually means the check could not read
            // some of them. Telling the caller to "split it into one proposal per
            // element" is advice that cannot be followed.
            CorrectionProposal p = Propose(F(AuditCheckNames.UnpinnedLinks));

            Assert.Equal(ProposalState.RequiresInput, p.State);
            Assert.Contains("nothing here to act on", p.Why);
            Assert.DoesNotContain("Split it into one proposal", p.Why);
        }

        [Fact]
        public void A_view_without_a_template_returns_the_question_rather_than_choosing_a_template()
        {
            CorrectionProposal p = Propose(F(AuditCheckNames.ViewsWithoutTemplate, 77));

            Assert.Equal(ProposalState.RequiresInput, p.State);
            // The template is an argument nobody supplied, and this bridge compiles no
            // organisation's standards in.
            Assert.Contains("template_view_id", p.Why);
        }

        [Fact]
        public void An_in_place_family_says_why_it_cannot_be_automated_rather_than_guessing()
        {
            CorrectionProposal p = Propose(F(AuditCheckNames.InPlaceFamilies, 9));

            Assert.Equal(ProposalState.Unsupported, p.State);
            Assert.Contains("MODELLED again", p.Why);
            Assert.Null(p.Tool);
        }

        [Fact]
        public void An_imported_cad_is_somebody_elses_decision()
        {
            CorrectionProposal p = Propose(F(AuditCheckNames.ImportedCad, 5));
            Assert.Equal(ProposalState.Unsupported, p.State);
            Assert.Contains("conversation", p.Why);
        }

        [Fact]
        public void A_finding_with_no_registered_correction_is_unsupported_rather_than_improvised()
        {
            CorrectionProposal p = Propose(F("some_check_nobody_registered", 1));

            Assert.Equal(ProposalState.Unsupported, p.State);
            Assert.Equal(ProposalRefusal.NoSuchTool, p.RefusalCode);
            Assert.Null(p.Tool);
            // The refusal names the honest reason rather than inventing a tool.
            Assert.Contains("improvising one would mean composing a tool call", p.Why);
        }

        [Fact]
        public void A_truncated_finding_requires_input_rather_than_correcting_what_fitted_in_the_reply()
        {
            Finding f = F(AuditCheckNames.UnpinnedLinks, 1);
            f.Truncated = true;
            CorrectionProposal p = Propose(f);

            Assert.Equal(ProposalState.RequiresInput, p.State);
            Assert.Equal(ProposalRefusal.Truncated, p.RefusalCode);
        }

        // ---- what the registry may not contain or do -------------------------

        [Fact]
        public void The_registry_names_no_tool_that_runs_arbitrary_code()
        {
            var tools = CorrectionRegistry.ToolsJson().Select(x => (string)x).ToList();
            Assert.NotEmpty(tools);
            Assert.DoesNotContain("horizun_execute_python", tools);
            Assert.DoesNotContain("horizun_request_python_access", tools);
            // A correction surface with an arbitrary-code escape hatch has no safety
            // model at all - it has a list of suggestions and a way around the list.
            Assert.All(tools, t => Assert.StartsWith("horizun_", t));
        }

        [Fact]
        public void A_recipe_may_not_redirect_the_document_or_turn_a_rehearsal_into_a_write()
        {
            foreach (string forbidden in new[] { "target_document", "dry_run" })
            {
                var registry = new Dictionary<string, CorrectionRecipe>(StringComparer.Ordinal)
                {
                    {
                        "x", new CorrectionRecipe
                        {
                            FindingType = "x",
                            Tool = "horizun_manage_links",
                            FixedArguments = new JObject { [forbidden] = "somewhere else" }
                        }
                    }
                };
                CorrectionProposal p = GuidedCorrectionRules.Propose(
                    F("x", 1), registry, Doc, Fp, null, null);

                Assert.Equal(ProposalState.Unsafe, p.State);
                Assert.Contains("may not set '" + forbidden + "'", p.Why);
            }
        }

        [Fact]
        public void Every_registry_entry_either_names_a_tool_or_says_why_it_cannot()
        {
            foreach (KeyValuePair<string, CorrectionRecipe> kv in CorrectionRegistry.Default)
            {
                bool named = !string.IsNullOrWhiteSpace(kv.Value.Tool);
                bool explained = !string.IsNullOrWhiteSpace(kv.Value.CannotAutomateBecause);
                Assert.True(named ^ explained,
                    "'" + kv.Key + "' must either name a tool or say why it cannot be automated, not both " +
                    "and not neither.");
                if (named)
                {
                    Assert.False(string.IsNullOrWhiteSpace(kv.Value.ExpectedOutcome),
                        "'" + kv.Key + "' names a tool and does not say what the result would be.");
                    Assert.False(string.IsNullOrWhiteSpace(kv.Value.Verification),
                        "'" + kv.Key + "' names a tool and does not say how the result would be verified.");
                }
            }
        }

        [Fact]
        public void The_registry_is_published_so_a_caller_can_see_what_will_never_be_proposed()
        {
            JObject d = CorrectionRegistry.Describe();
            var entries = (JArray)d["entries"];
            Assert.Equal(CorrectionRegistry.Default.Count, entries.Count);
            Assert.NotNull(d["registry_means"]);
            Assert.NotNull(d["refusal_means"]);
        }

        // ---- the widened registry ---------------------------------------------

        [Fact]
        public void Every_finding_type_the_audit_emits_has_an_entry_that_acts_or_says_why_not()
        {
            foreach (string check in AuditCheckNames.Findings.Concat(new[] { AuditCheckNames.WorksetPlacement }))
                Assert.True(CorrectionRegistry.Default.ContainsKey(check),
                    "'" + check + "' has no registry entry: an absent entry reads like an oversight rather than a decision.");
        }

        [Fact]
        public void No_recipe_carries_a_placeholder_and_every_required_input_is_named()
        {
            foreach (KeyValuePair<string, CorrectionRecipe> kv in CorrectionRegistry.Default)
            {
                string rendered = kv.Value.FixedArguments?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
                Assert.DoesNotContain("<CHOOSE", rendered);
                Assert.DoesNotContain("<", rendered);
                foreach (string input in kv.Value.RequiredArguments)
                    Assert.False(string.IsNullOrWhiteSpace(input));
            }
        }

        [Fact]
        public void Orphan_group_types_become_one_typed_delete_over_the_ids_and_say_they_are_destructive()
        {
            CorrectionProposal p = Propose(F(AuditCheckNames.OrphanGroupTypes, 50, 51));
            Assert.Equal(ProposalState.Actionable, p.State);
            Assert.Equal("horizun_delete_verified", p.Tool);
            Assert.Equal("ids", (string)p.Arguments["mode"]);
            Assert.Equal(new long[] { 50, 51 }, p.Arguments["ids"].Select(t => (long)t).ToArray());
            Assert.Null(p.Arguments["element_ids"]);
            Assert.Equal("high", p.Risk);
            Assert.False(p.Reversible);
            Assert.Contains("OFFER", p.ExpectedOutcome);
        }

        [Fact]
        public void The_rooms_recipe_acts_only_on_the_unplaced_code_and_the_links_recipe_only_on_unloaded()
        {
            CorrectionRecipe rooms = CorrectionRegistry.Default[AuditCheckNames.Rooms];
            Assert.Equal("problem_code", rooms.ItemFilterField);
            Assert.Equal(new[] { RoomProblemCode.Unplaced }, rooms.ItemFilterValues.ToArray());
            Assert.Equal("horizun_delete_verified", rooms.Tool);

            CorrectionRecipe links = CorrectionRegistry.Default[AuditCheckNames.Links];
            Assert.Equal("status", links.ItemFilterField);
            Assert.Contains("Unloaded", links.ItemFilterValues);
            Assert.DoesNotContain("NotFound", links.ItemFilterValues);
            CorrectionProposal p = Propose(F(AuditCheckNames.Links, 70));
            Assert.Equal(ProposalState.Actionable, p.State);
            Assert.Equal("reload", (string)p.Arguments["operation"]);
            Assert.Equal(70L, (long)p.Arguments["link_type_id"]);
        }

        [Fact]
        public void A_template_supplied_as_an_input_makes_the_view_correction_actionable_inside_an_actions_envelope()
        {
            CorrectionProposal p = GuidedCorrectionRules.Propose(F(AuditCheckNames.ViewsWithoutTemplate, 30),
                CorrectionRegistry.Default, Doc, Fp, null, null, new JObject { ["template_view_id"] = 9 });
            Assert.Equal(ProposalState.Actionable, p.State);
            var actions = (JArray)p.Arguments["actions"];
            Assert.Equal("apply_template", (string)actions[0]["operation"]);
            Assert.Equal(30L, (long)actions[0]["view_id"]);
            Assert.Equal(9L, (long)actions[0]["template_view_id"]);
            // The surface's two fields stay outside the envelope.
            Assert.Equal(Doc, (string)p.Arguments["target_document"]);
            Assert.True((bool)p.Arguments["dry_run"]);
            // And the caveat about overriding graphics still travels.
            Assert.NotEmpty(p.Ambiguities);
        }

        [Fact]
        public void The_views_off_sheets_and_workset_entries_refuse_with_a_reason_that_names_the_decision()
        {
            Assert.Contains("documentation decision", Propose(F(AuditCheckNames.ViewsOffSheets, 1)).Why);
            Assert.Contains("no typed command", Propose(F(AuditCheckNames.WorksetPlacement, 1)).Why);
            Assert.Equal(ProposalState.Unsupported, Propose(F(AuditCheckNames.Warnings, 1)).State);
            Assert.Equal(ProposalState.Unsupported, Propose(F(AuditCheckNames.DesignOptions, 1)).State);
        }

        [Fact]
        public void A_proposal_for_another_document_is_refused_as_unsafe()
        {
            Finding f = F(AuditCheckNames.UnpinnedLinks, 1);
            f.DocumentTitle = "SomeoneElse";
            CorrectionProposal p = Propose(f);

            Assert.Equal(ProposalState.Unsafe, p.State);
            Assert.Equal(ProposalRefusal.WrongDocument, p.RefusalCode);
        }

        [Fact]
        public void A_proposal_against_a_changed_fingerprint_is_refused_because_ids_may_name_other_elements()
        {
            Finding f = F(AuditCheckNames.UnpinnedLinks, 1);
            f.DocumentFingerprint = "fp-2";
            CorrectionProposal p = Propose(f);

            Assert.Equal(ProposalState.Unsafe, p.State);
            Assert.Equal(ProposalRefusal.FingerprintChanged, p.RefusalCode);
        }
    }
}
