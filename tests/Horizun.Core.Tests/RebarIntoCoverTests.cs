// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The cover a bar that runs into other elements is held to (acero 3.0, L1): the
// SMALLEST cover any of the elements declares. Found in the second steel duel
// (2026-10-12): a set that declared the footings "cast against earth" 75 mm in
// cover_rules and a column's section at 40 mm had every dowel AND every column
// bar refused as bar_short_of_the_declared_cover, because the column's 40 mm was
// declared on the section rule, which the union never counted - it held the whole
// union, the column above included, to the footing's 75 mm.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RebarIntoCoverTests
    {


        private static SectionHostFrame Column() => new SectionHostFrame
        {
            ElementId = 7, OriginMm = new[] { 0.0, 0, 0 }, U = new[] { 0, 0, 1.0 }, V = new[] { 1.0, 0, 0 }, W = new[] { 0, 1.0, 0 },
            UMin = 0, UMax = 3000, VMin = -200, VMax = 200, WMin = -200, WMax = 200
        };









        [Fact]
        public void ARuleDeclaredAsItselfCarriesNoSectionCover() =>
            Assert.Null(new StructuralRebarRule().SectionCoverMm);

        // ------------------------------------------------------ the cover held

        [Fact]
        public void TheFootingsLargerCoverDoesNotHoldTheColumnBars()
        {
            // The duel: footing 75 (cover_rules), column and column above none, section 40.
            Assert.Equal(40, RebarContainment.UnionCoverMm(new double?[] { null, 75, null }, 40).Value, 6);
        }

        [Fact]
        public void TheSmallestDeclaredCoverIsHeldWhicheverDeclaresIt()
        {
            Assert.Equal(40, RebarContainment.UnionCoverMm(new double?[] { 50, 75 }, 40).Value, 6);
            Assert.Equal(30, RebarContainment.UnionCoverMm(new double?[] { 30, 75 }, 40).Value, 6);
        }

        [Fact]
        public void APlainRuleIntoTheFootingIsHeldToWhatTheCoverRulesDeclare() =>
            Assert.Equal(75, RebarContainment.UnionCoverMm(new double?[] { null, 75 }, null).Value, 6);

        [Fact]
        public void ASetWithoutCoverRulesStillHoldsTheUnionToNoCover()
        {
            // Unchanged: a host measured alone is not held to its section's cover either, so a union is not.
            Assert.Null(RebarContainment.UnionCoverMm(new double?[] { null, null, null }, 40));
            Assert.Null(RebarContainment.UnionCoverMm(new double?[0], null));
        }
    }
}
