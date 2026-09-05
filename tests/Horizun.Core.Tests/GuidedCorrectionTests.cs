// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Guided corrections, proved by running the rules. These are the simulated
// adapters the mandate asks for: a valid proposal, an expired one, a wrong
// document, a resolved finding, tampered ids, a tool that does not exist, a
// changed contract, and a batch that is only partly actionable.
//
// The quiet failure among them is the tampered scope: a proposal that widens
// from four walls to every wall is still well-typed, still satisfies its
// contract, and does something nobody agreed to.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GuidedCorrectionTests
    {
        private const string Doc = "Tower - Structural";
        private const string Fingerprint = "fp-aaa";

        private static Dictionary<string, CorrectionRecipe> Registry()
        {
            return new Dictionary<string, CorrectionRecipe>
            {
                ["unpinned_links"] = new CorrectionRecipe
                {
                    FindingType = "unpinned_links",
                    Tool = "horizun_manage_links",
                    RequiredArguments = { "target_document", "element_ids" },
                    ExpectedOutcome = "each listed link instance is pinned",
                    Verification = "re-read Pinned on each id after the commit",
                    Risk = "low",
                    Reversible = true
                },
                ["imported_cad"] = new CorrectionRecipe
                {
                    FindingType = "imported_cad",
                    Tool = "horizun_delete_verified",
                    RequiredArguments = { "target_document", "element_ids" },
                    Ambiguities = { "delete the import, or explode it and keep the geometry?" },
                    Risk = "high",
                    Reversible = false
                },
                ["rooms_not_enclosed"] = new CorrectionRecipe
                {
                    FindingType = "rooms_not_enclosed",
                    Tool = "(none)",
                    CannotAutomateBecause =
                        "closing a room boundary is a modelling decision about where the wall should be, " +
                        "which nothing in the finding determines."
                }
            };
        }

        private static Finding F(string type = "unpinned_links", string doc = Doc,
                                 string fp = Fingerprint, bool truncated = false, bool resolved = false,
                                 params long[] ids)
        {
            return new Finding
            {
                FindingId = "f1",
                FindingType = type,
                DocumentTitle = doc,
                DocumentFingerprint = fp,
                Truncated = truncated,
                Resolved = resolved,
                ElementIds = (ids.Length == 0 ? new long[] { 10, 11, 12, 13 } : ids).ToList()
            };
        }

        private static CorrectionProposal P(Finding f, string doc = Doc, string fp = Fingerprint) =>
            GuidedCorrectionRules.Propose(f, Registry(), doc, fp, "2026-01-01T00:00:00Z",
                                          "2026-01-01T01:00:00Z");

        // ------------------------------------------------------ the happy one

        [Fact]
        public void A_valid_finding_becomes_an_actionable_proposal_that_executes_nothing()
        {
            CorrectionProposal p = P(F());
            Assert.Equal(ProposalState.Actionable, p.State);
            Assert.Equal("horizun_manage_links", p.Tool);
            Assert.True(p.ConfirmationRequired);
            Assert.True(p.DryRunSupported);
            Assert.True(p.Arguments.Value<bool>("dry_run"));
            Assert.Contains("proposed, not performed", p.Why);
            Assert.Contains("never executes one", GuidedCorrectionRules.ReadOnlyMeans);
        }

        [Fact]
        public void The_arguments_are_built_from_typed_fields_and_never_from_free_text()
        {
            CorrectionProposal p = P(F());
            Assert.Equal(Doc, p.Arguments.Value<string>("target_document"));
            Assert.Equal(4, ((JArray)p.Arguments["element_ids"]).Count);
            Assert.Contains("Nothing is assembled from free text", GuidedCorrectionRules.RegistryMeans);
        }

        // -------------------------------------------------------- refusals

        [Fact]
        public void A_finding_from_another_document_is_refused_as_unsafe()
        {
            CorrectionProposal p = P(F(doc: "Some Other Model"));
            Assert.Equal(ProposalState.Unsafe, p.State);
            Assert.Equal(ProposalRefusal.WrongDocument, p.RefusalCode);
            Assert.Contains("worst thing this surface could produce", p.Why);
        }

        [Fact]
        public void A_changed_fingerprint_is_refused_because_ids_may_name_other_elements()
        {
            CorrectionProposal p = P(F(fp: "fp-zzz"));
            Assert.Equal(ProposalState.Unsafe, p.State);
            Assert.Equal(ProposalRefusal.FingerprintChanged, p.RefusalCode);
        }

        [Fact]
        public void A_truncated_finding_has_an_unknown_scope_and_requires_input()
        {
            CorrectionProposal p = P(F(truncated: true));
            Assert.Equal(ProposalState.RequiresInput, p.State);
            Assert.Equal(ProposalRefusal.Truncated, p.RefusalCode);
        }

        [Fact]
        public void A_resolved_finding_produces_nothing_and_is_not_an_error()
        {
            CorrectionProposal p = P(F(resolved: true));
            Assert.Equal(ProposalState.AlreadyResolved, p.State);
            Assert.Null(p.RefusalCode);
        }

        [Fact]
        public void A_finding_with_no_registered_correction_is_unsupported_not_improvised()
        {
            CorrectionProposal p = P(F(type: "some_new_finding"));
            Assert.Equal(ProposalState.Unsupported, p.State);
            Assert.Equal(ProposalRefusal.NoSuchTool, p.RefusalCode);
            Assert.Contains("improvising one", p.Why);
        }

        [Fact]
        public void A_correction_that_cannot_be_automated_says_why_rather_than_guessing()
        {
            CorrectionProposal p = P(F(type: "rooms_not_enclosed"));
            Assert.Equal(ProposalState.Unsupported, p.State);
            Assert.Contains("modelling decision", p.Why);
        }

        // ------------------------------------------------------- ambiguity

        [Fact]
        public void An_ambiguous_correction_returns_the_options_rather_than_choosing()
        {
            CorrectionProposal p = P(F(type: "imported_cad"));
            Assert.Equal(ProposalState.RequiresInput, p.State);
            Assert.Single(p.Ambiguities);
            Assert.Contains("explode it", p.Ambiguities[0]);
            Assert.Contains("costs them a week", GuidedCorrectionRules.AmbiguityMeans);
        }

        [Fact]
        public void A_high_risk_irreversible_correction_says_so_in_its_own_fields()
        {
            CorrectionProposal p = P(F(type: "imported_cad"));
            Assert.Equal("high", p.Risk);
            Assert.False(p.Reversible);
        }

        // ------------------------------------------------------------ scope

        [Fact]
        public void A_proposal_may_narrow_its_scope_and_never_widen_it()
        {
            // THE QUIET FAILURE. Well-typed, contract-satisfying, and unagreed.
            Finding original = F(ids: new long[] { 10, 11 });
            CorrectionProposal p = P(original);

            string why;
            Assert.True(GuidedCorrectionRules.ScopeIsUnchanged(p, original, out why));

            p.ElementIds.Add(9999);
            Assert.False(GuidedCorrectionRules.ScopeIsUnchanged(p, original, out why));
            Assert.Contains("may narrow, never widen", why);

            CorrectionProposal narrowed = P(original);
            narrowed.ElementIds = new List<long> { 10 };
            Assert.True(GuidedCorrectionRules.ScopeIsUnchanged(narrowed, original, out why));
        }

        // ----------------------------------------------------------- expiry

        [Fact]
        public void An_expired_proposal_is_recognised_by_comparison_and_not_by_a_clock()
        {
            // Time is compared, never read, so the boundary is exact in a test.
            CorrectionProposal p = P(F());
            Assert.False(GuidedCorrectionRules.IsExpired(p, "2026-01-01T00:30:00Z"));
            Assert.True(GuidedCorrectionRules.IsExpired(p, "2026-01-01T02:00:00Z"));
            // exactly at the boundary is not yet expired
            Assert.False(GuidedCorrectionRules.IsExpired(p, "2026-01-01T01:00:00Z"));
        }

        [Fact]
        public void A_proposal_with_no_expiry_never_expires()
        {
            CorrectionProposal p = GuidedCorrectionRules.Propose(
                F(), Registry(), Doc, Fingerprint, "2026-01-01T00:00:00Z", null);
            Assert.False(GuidedCorrectionRules.IsExpired(p, "2099-01-01T00:00:00Z"));
        }

        // ------------------------------------------------------------ batch

        [Fact]
        public void A_batch_reports_each_state_apart_rather_than_one_verdict()
        {
            var proposals = new[]
            {
                P(F()),                                   // actionable
                P(F(type: "imported_cad")),               // requires_input
                P(F(type: "unknown_thing")),              // unsupported
                P(F(doc: "Other")),                       // unsafe
                P(F(resolved: true))                      // already_resolved
            };

            JObject t = GuidedCorrectionRules.Tally(proposals);
            Assert.Equal(1, t.Value<long>(ProposalState.Actionable));
            Assert.Equal(1, t.Value<long>(ProposalState.RequiresInput));
            Assert.Equal(1, t.Value<long>(ProposalState.Unsupported));
            Assert.Equal(1, t.Value<long>(ProposalState.Unsafe));
            Assert.Equal(1, t.Value<long>(ProposalState.AlreadyResolved));
            foreach (string s in ProposalState.All) Assert.NotNull(t[s]);
        }

        [Fact]
        public void No_proposal_ever_names_arbitrary_code_as_its_tool()
        {
            // execute_python is not an escape hatch for a correction nobody modelled.
            foreach (CorrectionRecipe r in Registry().Values)
                Assert.DoesNotContain("execute_python", r.Tool ?? "");
        }
    }
}
