// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE COVERAGE RULE.
//
// A code review found that this capability's contract asserted openings, sweeps,
// reveals, embedded curtain walls, dimensions and tags were "preserved by
// identity", while the only class anything re-read was the family instances. Six
// assertions with nothing behind them.
//
// The fix is not "remember to write the other six verifiers". It is that the
// disposition is DERIVED from whether a verifier exists, in one place, and that
// this file fails if the two ever come apart again:
//
//     preserved_by_identity  <=>  a verifier is registered for that kind.
//
// The Revit half of the rule - that every registered kind is actually dispatched
// in WallSplitVerifier - is asserted in the Revit assembly's own switch by having
// no default branch that could quietly succeed; here we pin the arithmetic.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WallDependencyCoverageTests
    {
        [Fact]
        public void EveryKindWithAVerifierIsPreservedByIdentity()
        {
            Assert.NotEmpty(DependencyKinds.WithVerifier);
            foreach (string kind in DependencyKinds.WithVerifier)
            {
                Assert.True(DependencyKinds.HasVerifier(kind), kind + " must have a verifier");
                Assert.Equal(DependencyDisposition.PreservedByIdentity, DependencyKinds.DispositionFor(kind));
            }
        }

        [Fact]
        public void AKindWithNoVerifierIsBlockingAndNeverPreserved()
        {
            // This is the whole rule. A new dependency class that somebody adds to the
            // collector without writing its verifier lands here, is blocking, and the wall
            // is refused - rather than being reported as preserved by an assertion.
            foreach (string kind in new[] { "unrecognised", "mep_curve", "duct", "something_new", "" })
                Assert.Equal(DependencyDisposition.UnsupportedBlocking, DependencyKinds.DispositionFor(kind));

            Assert.Equal(DependencyDisposition.UnsupportedBlocking, DependencyKinds.DispositionFor(null));
        }

        [Fact]
        public void TheOnlyNonBlockingNonPreservedKindIsTheStructuralOne()
        {
            // Sketches and types cannot be "lost" the way an instance can. They are the one
            // legitimate not_applicable, and they are named rather than pattern-matched.
            Assert.Equal(DependencyDisposition.NotApplicable,
                         DependencyKinds.DispositionFor(DependencyKinds.Structural));

            var byDisposition = DependencyKinds.All
                .GroupBy(DependencyKinds.DispositionFor)
                .ToDictionary(g => g.Key, g => g.ToList());

            Assert.Single(byDisposition[DependencyDisposition.NotApplicable]);
            Assert.Equal(DependencyKinds.Structural, byDisposition[DependencyDisposition.NotApplicable][0]);
        }

        [Fact]
        public void EveryKnownKindIsAccountedForExactlyOnce()
        {
            Assert.Equal(DependencyKinds.All.Length, DependencyKinds.All.Distinct(StringComparer.Ordinal).Count());
            foreach (string kind in DependencyKinds.WithVerifier)
                Assert.Contains(kind, DependencyKinds.All);

            // Nothing is left in a third state: every kind is preserved, blocking, or the
            // one structural exception.
            foreach (string kind in DependencyKinds.All)
            {
                string disposition = DependencyKinds.DispositionFor(kind);
                Assert.Contains(disposition, new[]
                {
                    DependencyDisposition.PreservedByIdentity,
                    DependencyDisposition.UnsupportedBlocking,
                    DependencyDisposition.NotApplicable
                });
            }
        }

        [Fact]
        public void EveryStructuralClassHasItsOwnKindRatherThanOneGenericBucket()
        {
            // A continuous footing is not a bar set and neither is a fabric sheet. One
            // generic "structural" kind would mean verifying all of them the way the weakest
            // one can be verified - which is how a footing ends up "preserved" because a bar
            // survived.
            foreach (string kind in new[]
                     {
                         DependencyKinds.WallFoundation, DependencyKinds.Rebar, DependencyKinds.RebarContainer,
                         DependencyKinds.AreaReinforcement, DependencyKinds.PathReinforcement,
                         DependencyKinds.FabricArea, DependencyKinds.FabricSheet
                     })
            {
                Assert.True(DependencyKinds.HasVerifier(kind), kind + " must have its own verifier");
                Assert.Equal(DependencyDisposition.PreservedByIdentity, DependencyKinds.DispositionFor(kind));
            }

            // Seven structural kinds, each with a verifier. Counted directly rather than
            // through an arithmetic identity that would pass by coincidence.
            Assert.Equal(7, DependencyKinds.All.Count(k =>
                k == DependencyKinds.WallFoundation || DependencyKinds.IsReinforcement(k)));
        }

        [Fact]
        public void AWallWithAFootingOrReinforcementIsNoLongerRefusedOutright()
        {
            // The operational consequence the director rejected: the closed coverage rule
            // made an ORDINARY structural wall unconvertible, because WallFoundation and
            // Rebar had no verifier and therefore blocked.
            Assert.NotEqual(DependencyDisposition.UnsupportedBlocking,
                            DependencyKinds.DispositionFor(DependencyKinds.WallFoundation));
            Assert.NotEqual(DependencyDisposition.UnsupportedBlocking,
                            DependencyKinds.DispositionFor(DependencyKinds.Rebar));
        }

        [Fact]
        public void TheReinforcementFamilyIsNamedAndClosed()
        {
            foreach (string kind in DependencyKinds.Reinforcement)
                Assert.True(DependencyKinds.IsReinforcement(kind));

            Assert.False(DependencyKinds.IsReinforcement(DependencyKinds.WallFoundation));
            Assert.False(DependencyKinds.IsReinforcement(DependencyKinds.FamilyInstance));
            Assert.False(DependencyKinds.IsReinforcement(null));
        }

        [Fact]
        public void TheStructuralFailureCodesAreDistinctAndPublished()
        {
            foreach (string code in new[]
                     {
                         WallSplitCodes.VerifyFoundationRelation, WallSplitCodes.VerifyFoundationGeometry,
                         WallSplitCodes.VerifyRebarIdentity, WallSplitCodes.VerifyRebarLayout,
                         WallSplitCodes.RebarOutsideCoreCarrier, WallSplitCodes.VerifyReinforcementMembers,
                         WallSplitCodes.UnsupportedReinforcementKind
                     })
                Assert.Contains(code, WallSplitCodes.All);

            // The mandate names this one exactly. It is not a generic geometry failure: it
            // says the bars fitted the compound wall and do not fit the core.
            Assert.Equal("rebar_outside_core_carrier", WallSplitCodes.RebarOutsideCoreCarrier);
        }

        [Fact]
        public void AReinforcementSystemThatCannotBeVerifiedGetsItsOwnCodeNotUnrecognised()
        {
            // The director's rule: a type that cannot be verified completely stays blocking,
            // but it must say WHICH type - not fall into the anonymous bucket.
            Assert.Equal("unsupported_reinforcement_kind", WallSplitCodes.UnsupportedReinforcementKind);
            Assert.NotEqual(WallSplitCodes.UnsupportedDependency, WallSplitCodes.UnsupportedReinforcementKind);
        }

        [Fact]
        public void TheSixClassesTheReviewFoundUnverifiedAreAllCoveredNow()
        {
            // Named individually and deliberately: this is the list from the review, and a
            // regression that drops one of them should read as exactly that.
            foreach (string kind in new[]
                     {
                         DependencyKinds.Opening,
                         DependencyKinds.WallSweep,
                         DependencyKinds.Reveal,
                         DependencyKinds.EmbeddedWall,
                         DependencyKinds.Dimension,
                         DependencyKinds.Tag
                     })
                Assert.True(DependencyKinds.HasVerifier(kind), kind + " was asserted without a verifier once already");
        }

        [Fact]
        public void KindsAreLowerSnakeCaseAndDistinct()
        {
            foreach (string kind in DependencyKinds.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(kind));
                Assert.Equal(kind.ToLowerInvariant(), kind);
                Assert.DoesNotContain(" ", kind);
            }
        }

        // ---- the codes the new verifiers answer with ------------------------------

        [Fact]
        public void EveryNewFailureCodeIsInTheClosedSet()
        {
            foreach (string code in new[]
                     {
                         WallSplitCodes.VerifyDependencyIdentity,
                         WallSplitCodes.VerifyDependencyRelation,
                         WallSplitCodes.VerifyDependencyGeometry,
                         WallSplitCodes.VerifyJoinNotRestored,
                         WallSplitCodes.ProvenanceVerificationFailed,
                         WallSplitCodes.VerifySiblingSetIncomplete
                     })
                Assert.Contains(code, WallSplitCodes.All);
        }

        [Fact]
        public void ProvenanceFailureHasItsOwnCodeRatherThanBeingFoldedIntoAnother()
        {
            // A stamp that cannot be written is not "a geometry problem". It has its own
            // code because the consequence is specific: the next run cannot tell this wall
            // from one nobody has touched, and would split it again.
            Assert.Equal("provenance_verification_failed", WallSplitCodes.ProvenanceVerificationFailed);
            Assert.NotEqual(WallSplitCodes.ProvenanceVerificationFailed, WallSplitCodes.VerifyLayerGeometry);
        }
    }

    /// <summary>
    /// The type-identity alignment the review asked for: whatever the fingerprint is made
    /// of, the matcher must compare and the builder must apply. These tests pin the LIST
    /// so the three cannot drift apart silently again.
    /// </summary>
    public class WallTypeIdentityAlignmentTests
    {
        private static WallLayerFacts Layer(double mm, string function = "Structure", string uid = "mat-1")
            => new WallLayerFacts
            {
                Index = 0,
                WidthFeet = mm / WallLayerRules.MmPerFoot,
                Function = function,
                MaterialUniqueId = uid,
                MaterialName = "Concreto"
            };

        [Fact]
        public void TheFingerprintIsMadeOfExactlyTheDocumentedFacts()
        {
            Assert.Equal(new[] { "material_unique_id", "width_ticks", "function", "base_wall_kind",
                                 "opening_wrapping", "end_cap" },
                         WallLayerRules.TypeIdentityFacts);
        }

        [Fact]
        public void EveryFactInTheListActuallyChangesTheFingerprint()
        {
            // A fact that is documented as part of identity but does not move the digest is
            // a fact the matcher would compare and the digest would ignore.
            string baseline = WallLayerRules.LayerTypeFingerprint(Layer(200), "Basic", "Exterior", "Exterior");

            Assert.NotEqual(baseline, WallLayerRules.LayerTypeFingerprint(
                Layer(200, uid: "mat-2"), "Basic", "Exterior", "Exterior"));          // material_unique_id
            Assert.NotEqual(baseline, WallLayerRules.LayerTypeFingerprint(
                Layer(201), "Basic", "Exterior", "Exterior"));                        // width_ticks
            Assert.NotEqual(baseline, WallLayerRules.LayerTypeFingerprint(
                Layer(200, function: "Finish1"), "Basic", "Exterior", "Exterior"));   // function
            Assert.NotEqual(baseline, WallLayerRules.LayerTypeFingerprint(
                Layer(200), "Stacked", "Exterior", "Exterior"));                      // base_wall_kind
            Assert.NotEqual(baseline, WallLayerRules.LayerTypeFingerprint(
                Layer(200), "Basic", "Interior", "Exterior"));                        // opening_wrapping
            Assert.NotEqual(baseline, WallLayerRules.LayerTypeFingerprint(
                Layer(200), "Basic", "Exterior", "Interior"));                        // end_cap
        }

        [Fact]
        public void EveryExcludedPropertyCarriesTheReasonItIsExcluded()
        {
            foreach (string excluded in new[] { "is_variable_width", "deck_profile", "deck_embedding", "is_core" })
            {
                Assert.True(WallLayerRules.TypeIdentityExclusions.ContainsKey(excluded),
                            excluded + " must be excluded on the record, not by omission");
                Assert.False(string.IsNullOrWhiteSpace(WallLayerRules.TypeIdentityExclusions[excluded]));
            }
        }

        [Fact]
        public void AnExcludedPropertyDoesNotChangeTheFingerprint()
        {
            // The other half of the alignment: a property the builder cannot apply must not
            // be able to make two identical types look different.
            WallLayerFacts plain = Layer(200);
            WallLayerFacts variable = Layer(200);
            variable.IsVariableWidth = true;
            variable.DeckProfileUniqueId = "deck-1";
            variable.DeckEmbeddingType = "Overlay";

            Assert.Equal(WallLayerRules.LayerTypeFingerprint(plain, "Basic", "Exterior", "Exterior"),
                         WallLayerRules.LayerTypeFingerprint(variable, "Basic", "Exterior", "Exterior"));
        }
    }
}
