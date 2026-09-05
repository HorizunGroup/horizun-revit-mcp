// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHAT THE CONFIRMATION TOKEN IS BOUND TO.
//
// A code review found that the token carried the wall's own state plus THE LIST
// OF UNIQUE IDS of its dependencies. That detects a door appearing or
// disappearing and nothing else: a door that was moved, re-typed, re-phased,
// re-hosted or re-parameterised between the dry run and the apply left the
// number identical, and the apply proceeded against a model nobody approved.
//
// Every fingerprint in this capability is now built through FactBook, so the
// properties that make a fingerprint trustworthy are proved once, here, instead
// of being hoped for at each call site:
//
//   * a real change moves it;
//   * re-ordering a dictionary does NOT;
//   * jitter below the quantisation grid does NOT;
//   * a duplicate key or an unmeasured number is refused rather than hashed.
//
// The Revit-side builders (dependency snapshots, joins, wall state) are pinned
// by WallSplitWiringTests, because they need a Document to construct.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FactBookTests
    {
        [Fact]
        public void TheSameFactsInAnyOrderGiveTheSameDigest()
        {
            // A Dictionary's enumeration order is not a fact about the model. A fingerprint
            // that moved when a collector re-ordered would refuse every honest apply.
            string a = new FactBook().Add("b", "2").Add("a", "1").Add("c", 3L).Digest();
            string b = new FactBook().Add("c", 3L).Add("a", "1").Add("b", "2").Digest();
            Assert.Equal(a, b);
        }

        [Fact]
        public void AnUnorderedListIgnoresOrderAndAnOrderedListDoesNot()
        {
            string set1 = new FactBook().AddList("x", new[] { "a", "b", "c" }, ordered: false).Digest();
            string set2 = new FactBook().AddList("x", new[] { "c", "a", "b" }, ordered: false).Digest();
            Assert.Equal(set1, set2);

            string seq1 = new FactBook().AddList("x", new[] { "a", "b", "c" }, ordered: true).Digest();
            string seq2 = new FactBook().AddList("x", new[] { "c", "a", "b" }, ordered: true).Digest();
            Assert.NotEqual(seq1, seq2);
        }

        [Fact]
        public void AnOrderedListStillNoticesAMissingElement()
        {
            Assert.NotEqual(new FactBook().AddList("x", new[] { "a", "b" }, ordered: true).Digest(),
                            new FactBook().AddList("x", new[] { "a" }, ordered: true).Digest());
            Assert.NotEqual(new FactBook().AddList("x", new[] { "a", "b" }, ordered: false).Digest(),
                            new FactBook().AddList("x", new[] { "a" }, ordered: false).Digest());
        }

        [Fact]
        public void AMapIgnoresEnumerationOrderButNotContent()
        {
            var one = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("a", "1"),
                new KeyValuePair<string, string>("b", "2")
            };
            var reversed = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("b", "2"),
                new KeyValuePair<string, string>("a", "1")
            };
            var changed = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("a", "1"),
                new KeyValuePair<string, string>("b", "3")
            };

            Assert.Equal(new FactBook().AddMap("p", one).Digest(),
                         new FactBook().AddMap("p", reversed).Digest());
            Assert.NotEqual(new FactBook().AddMap("p", one).Digest(),
                            new FactBook().AddMap("p", changed).Digest());
        }

        [Fact]
        public void JitterBelowTheGridDoesNotMoveTheDigestAndARealMoveDoes()
        {
            double baseline = 10.0;
            Assert.Equal(new FactBook().AddFeet("x", baseline).Digest(),
                         new FactBook().AddFeet("x", baseline + 1e-9).Digest());

            // 0.2 mm is above the 0.1 mm grid and must always land at least one step away.
            Assert.NotEqual(new FactBook().AddFeet("x", baseline).Digest(),
                            new FactBook().AddFeet("x", baseline + WallLayerRules.MmToFeet(0.2)).Digest());
        }

        [Fact]
        public void AnAngleMovesTheDigestWhenItActuallyRotates()
        {
            Assert.Equal(new FactBook().AddAngle("r", 1.0).Digest(),
                         new FactBook().AddAngle("r", 1.0 + 1e-9).Digest());
            Assert.NotEqual(new FactBook().AddAngle("r", 1.0).Digest(),
                            new FactBook().AddAngle("r", 1.001).Digest());
        }

        [Fact]
        public void ADuplicateKeyIsRefusedRatherThanShadowing()
        {
            var book = new FactBook().Add("a", "1");
            ArgumentException ex = Assert.Throws<ArgumentException>(() => book.Add("a", "2"));
            Assert.Contains("silently shadow", ex.Message);
        }

        [Fact]
        public void AnUnmeasuredNumberIsRefusedRatherThanFingerprinted()
        {
            Assert.Throws<ArgumentException>(() => new FactBook().AddFeet("x", double.NaN));
            Assert.Throws<ArgumentException>(() => new FactBook().AddFeet("x", double.PositiveInfinity));
            Assert.Throws<ArgumentException>(() => new FactBook().AddAngle("r", double.NaN));
        }

        [Fact]
        public void AFactWithNoNameIsRefused()
            => Assert.Throws<ArgumentException>(() => new FactBook().Add("  ", "x"));

        [Fact]
        public void TheSchemaVersionIsAlwaysPartOfEveryDigest()
        {
            Assert.Contains("schema", new FactBook().Names);
            Assert.NotEqual(new FactBook().Add("x", "1").Digest(),
                            new FactBook("other_schema").Add("x", "1").Digest());
        }

        [Fact]
        public void TheDigestIsSixtyFourHexCharacters()
        {
            string digest = new FactBook().Add("x", "1").Digest();
            Assert.Equal(64, digest.Length);
            Assert.All(digest, c => Assert.True(Uri.IsHexDigit(c)));
        }

        [Fact]
        public void NullAndEmptyAreTheSameFactButNullIsNotTheStringNull()
        {
            Assert.Equal(new FactBook().Add("x", (string)null).Digest(),
                         new FactBook().Add("x", "").Digest());
            Assert.NotEqual(new FactBook().Add("x", (string)null).Digest(),
                            new FactBook().Add("x", "null").Digest());
        }
    }

    public class WallPlanFingerprintTests
    {
        private const double Mm = 1.0 / WallLayerRules.MmPerFoot;

        private static WallAssemblyFacts Facts() => new WallAssemblyFacts
        {
            WallTypeName = "EXT",
            WallTypeUniqueId = "wt-1",
            WallKind = "Basic",
            LocationLine = "WallCenterline",
            CoreFirstIndex = 1,
            CoreLastIndex = 1,
            OpeningWrapping = "Exterior",
            EndCap = "Exterior",
            Layers = new List<WallLayerFacts>
            {
                new WallLayerFacts { Index = 0, WidthFeet = 100 * Mm, Function = "Finish1",
                                     MaterialName = "Ladrillo", MaterialUniqueId = "m1" },
                new WallLayerFacts { Index = 1, WidthFeet = 200 * Mm, Function = "Structure",
                                     MaterialName = "Concreto", MaterialUniqueId = "m2" }
            }
        };

        private static string Print(IEnumerable<string> deps = null, string joins = "j0", string state = "s0",
                                    IEnumerable<double> curve = null, bool flipped = false)
        {
            WallAssemblyFacts facts = Facts();
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            return WallLayerRules.WallPlanFingerprint(
                "doc", "wall-uid", 1234, facts, plan, flipped,
                curve ?? new[] { 0.0, 0.0, 0.0, 10.0, 0.0, 0.0 },
                deps ?? new[] { "dep-a", "dep-b" }, joins, state,
                "structural_in_core_then_thickest");
        }

        [Fact]
        public void TheSamePlanFingerprintsTheSameWay() => Assert.Equal(Print(), Print());

        [Fact]
        public void ADependencyWhoseSTATEChangedMovesTheFingerprint()
        {
            // THE hole this replaces. The dependencies arrive as digests of their whole
            // state now, so a door that MOVED - same id, same UniqueId - produces a
            // different string here and the apply refuses as stale.
            Assert.NotEqual(Print(deps: new[] { "door-state-1" }),
                            Print(deps: new[] { "door-state-2" }));
        }

        [Fact]
        public void TheORDERDependenciesArriveInDoesNotMoveTheFingerprint()
        {
            Assert.Equal(Print(deps: new[] { "a", "b", "c" }),
                         Print(deps: new[] { "c", "b", "a" }));
        }

        [Fact]
        public void ADependencyAppearingOrDisappearingMovesTheFingerprint()
        {
            Assert.NotEqual(Print(deps: new[] { "a" }), Print(deps: new[] { "a", "b" }));
            Assert.NotEqual(Print(deps: new[] { "a", "b" }), Print(deps: new string[0]));
        }

        [Fact]
        public void AChangedJOINMovesTheFingerprint()
            => Assert.NotEqual(Print(joins: "joins-before"), Print(joins: "joins-after"));

        [Fact]
        public void AChangedWALLSTATEMovesTheFingerprint()
            => Assert.NotEqual(Print(state: "constraints-before"), Print(state: "constraints-after"));

        [Fact]
        public void MovingTheWallMovesTheFingerprint()
            => Assert.NotEqual(Print(curve: new[] { 0.0, 0.0, 0.0, 10.0, 0.0, 0.0 }),
                               Print(curve: new[] { 0.0, 0.0, 0.0, 10.5, 0.0, 0.0 }));

        [Fact]
        public void CurveJitterBelowTheGridDoesNotMoveIt()
            => Assert.Equal(Print(curve: new[] { 0.0, 0.0, 0.0, 10.0, 0.0, 0.0 }),
                            Print(curve: new[] { 0.0, 0.0, 0.0, 10.0 + 1e-9, 0.0, 0.0 }));

        [Fact]
        public void FlippingTheWallMovesTheFingerprint()
            => Assert.NotEqual(Print(flipped: false), Print(flipped: true));

        [Fact]
        public void AnUnmeasuredCurveFactIsRefusedRatherThanFingerprinted()
            => Assert.Throws<ArgumentException>(() => Print(curve: new[] { 0.0, double.NaN }));

        [Fact]
        public void EverythingTheTokenCoversIsNamedOnTheRecord()
        {
            // The director asked for the list of what the fingerprint includes. It is
            // published rather than described, so it can be read and asserted.
            foreach (string covered in new[]
                     {
                         "dependencies", "joins", "wall_state", "compound_structure",
                         "layer_plan", "curve", "location_line", "flipped"
                     })
                Assert.Contains(covered, WallLayerRules.PlanFingerprintCovers);
        }

        [Fact]
        public void TheLayerPlanIsPartOfTheFingerprintSoARetypedWallRefuses()
        {
            WallAssemblyFacts before = Facts();
            WallAssemblyFacts after = Facts();
            after.Layers[1].WidthFeet = 250 * Mm;

            string one = WallLayerRules.WallPlanFingerprint("doc", "u", 1, before, WallLayerRules.Plan(before),
                                                            false, new[] { 0.0 }, new[] { "a" }, "j", "s", "p");
            string two = WallLayerRules.WallPlanFingerprint("doc", "u", 1, after, WallLayerRules.Plan(after),
                                                            false, new[] { 0.0 }, new[] { "a" }, "j", "s", "p");
            Assert.NotEqual(one, two);
        }
    }
}
