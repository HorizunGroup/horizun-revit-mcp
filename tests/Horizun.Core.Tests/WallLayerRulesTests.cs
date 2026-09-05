// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The wall-layer arithmetic, proved without a Revit.
//
// These tests are written against the four defects the previous implementation
// shipped, because each one produced a committed, "verified" wrong building and
// none of them were reproducible by reading a reply:
//
//   * the core is not "the first Structure layer", and a wall without a core is
//     not layer 0;
//   * the location curve is not the centreline;
//   * a zero-width membrane is not absent, and it does not renumber the layers
//     behind it;
//   * a type is not its name.
//
// Where a rule exists to REFUSE, the test is the refusal.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WallLayerRulesTests
    {
        // ---- fixtures -------------------------------------------------------------

        private const double Mm = 1.0 / WallLayerRules.MmPerFoot;

        private static WallLayerFacts Layer(int index, double mm, string function,
                                            string material = "Mat", string uid = null,
                                            bool variable = false)
            => new WallLayerFacts
            {
                Index = index,
                WidthFeet = mm * Mm,
                Function = function,
                MaterialName = material,
                MaterialUniqueId = uid ?? ("uid-" + material),
                IsVariableWidth = variable
            };

        /// <summary>
        /// A five-layer exterior wall, exterior first: brick / air / insulation /
        /// concrete core / plaster. Core is the single concrete layer, index 3.
        /// Total 350 mm.
        /// </summary>
        private static WallAssemblyFacts FiveLayer(string locationLine = "WallCenterline")
            => new WallAssemblyFacts
            {
                WallTypeName = "EXT_Muro Fachada 25cm",
                WallTypeUniqueId = "wt-1",
                WallKind = "Basic",
                LocationLine = locationLine,
                CoreFirstIndex = 3,
                CoreLastIndex = 3,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0, 100, "Finish1", "Ladrillo"),
                    Layer(1,  30, "Membrane", "Aire"),
                    Layer(2,  50, "Insulation", "Lana"),
                    Layer(3, 150, "Structure", "Concreto"),
                    Layer(4,  20, "Finish2", "Yeso"),
                }
            };

        // ---- the `u` axis and the offset equation ---------------------------------

        [Fact]
        public void LayerCentresWalkFromTheExteriorFace()
        {
            var layers = FiveLayer().Layers;
            Assert.Equal(50, WallLayerRules.FeetToMm(WallLayerRules.LayerCenterU(layers, 0)), 6);
            Assert.Equal(115, WallLayerRules.FeetToMm(WallLayerRules.LayerCenterU(layers, 1)), 6);
            Assert.Equal(155, WallLayerRules.FeetToMm(WallLayerRules.LayerCenterU(layers, 2)), 6);
            Assert.Equal(255, WallLayerRules.FeetToMm(WallLayerRules.LayerCenterU(layers, 3)), 6);
            Assert.Equal(340, WallLayerRules.FeetToMm(WallLayerRules.LayerCenterU(layers, 4)), 6);
            Assert.Equal(350, WallLayerRules.FeetToMm(WallLayerRules.TotalWidth(layers)), 6);
        }

        [Theory]
        [InlineData("WallCenterline", 175.0)]
        [InlineData("FinishFaceExterior", 0.0)]
        [InlineData("FinishFaceInterior", 350.0)]
        [InlineData("CoreExterior", 180.0)]
        [InlineData("CoreInterior", 330.0)]
        [InlineData("CoreCenterline", 255.0)]
        public void EachLocationLineLandsWhereItSays(string line, double expectedMm)
        {
            WallAssemblyFacts facts = FiveLayer(line);
            Assert.True(WallLayerRules.TryLocationU(line, facts.Layers, facts.CoreFirstIndex,
                                                    facts.CoreLastIndex, out double u, out string error));
            Assert.Null(error);
            Assert.Equal(expectedMm, WallLayerRules.FeetToMm(u), 6);
        }

        [Fact]
        public void AnUnknownLocationLineIsRefusedRatherThanAssumedToBeTheCentreline()
        {
            WallAssemblyFacts facts = FiveLayer();
            Assert.False(WallLayerRules.TryLocationU("SomethingElse", facts.Layers, 3, 3,
                                                     out double _, out string error));
            Assert.Contains("WallCenterline", error);
            Assert.Contains("displaced every layer by half a wall", error);
        }

        [Fact]
        public void AnUnknownLocationLineGetsItsOwnCodeNotSomeoneElses()
        {
            // A caller branches on the code. Reporting an unreadable location line as
            // "not a basic wall" would send it looking at the wrong thing entirely.
            WallSplitPlan plan = WallLayerRules.Plan(FiveLayer("NotALocationLine"));
            Assert.False(plan.Eligible);
            Assert.Equal(WallSplitCodes.UnsupportedLocationLine, plan.Rejection.Code);
        }

        [Fact]
        public void TheOldArithmeticIsExactlyTheWallCentrelineCase()
        {
            // The previous implementation started at total/2 and walked inwards. That is
            // this equation with u_loc = T/2 - which is why it was right on a centreline
            // wall and wrong on the other five.
            var layers = FiveLayer().Layers;
            double total = WallLayerRules.TotalWidth(layers);

            double acc = total / 2.0;
            foreach (WallLayerFacts layer in layers)
            {
                double oldCentre = acc - layer.WidthFeet / 2.0;
                acc -= layer.WidthFeet;

                double now = WallLayerRules.OffsetForLayer(total / 2.0,
                                                           WallLayerRules.LayerCenterU(layers, layer.Index));
                Assert.Equal(oldCentre, now, 12);
            }
        }

        [Fact]
        public void OnAnExteriorFaceWallEveryLayerIsDisplacedByHalfTheWall()
        {
            // The measured consequence of D-03: the same wall, drawn on its exterior
            // finish face, has every offset shifted by exactly T/2 = 175 mm.
            var layers = FiveLayer().Layers;
            double centre = WallLayerRules.TotalWidth(layers) / 2.0;

            for (int i = 0; i < layers.Count; i++)
            {
                double c = WallLayerRules.LayerCenterU(layers, i);
                double onCentreline = WallLayerRules.OffsetForLayer(centre, c);
                double onExteriorFace = WallLayerRules.OffsetForLayer(0.0, c);
                Assert.Equal(175.0, WallLayerRules.FeetToMm(onCentreline - onExteriorFace), 6);
            }
        }

        [Fact]
        public void TheExteriorLayerSitsOnTheExteriorSideOfTheCentreline()
        {
            // Sign discipline: positive offset means towards the exterior.
            var layers = FiveLayer().Layers;
            double u = WallLayerRules.TotalWidth(layers) / 2.0;
            Assert.True(WallLayerRules.OffsetForLayer(u, WallLayerRules.LayerCenterU(layers, 0)) > 0);
            Assert.True(WallLayerRules.OffsetForLayer(u, WallLayerRules.LayerCenterU(layers, 4)) < 0);
        }

        // ---- zero-width layers ----------------------------------------------------

        [Fact]
        public void AZeroWidthMembraneKeepsItsNumberAndDoesNotShiftTheLayersBehindIt()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 2, CoreLastIndex = 2,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0, 100, "Finish1", "Ladrillo"),
                    Layer(1,   0, "Membrane", "Barrera"),   // zero width
                    Layer(2, 200, "Structure", "Concreto"),
                    Layer(3,  20, "Finish2", "Yeso"),
                }
            };

            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.True(plan.Eligible);

            // Three walls for four layers - only the ones with volume.
            Assert.Equal(3, plan.WouldProduceWalls);

            WallLayerPlan membrane = plan.Layers[1];
            Assert.False(membrane.Materialised);
            Assert.Equal("zero_width_membrane", membrane.NotMaterialisedReason);
            Assert.Equal(2, membrane.LayerNumber);

            // And the layers behind it keep THEIR numbers: the membrane does not renumber.
            Assert.Equal(3, plan.Layers[2].LayerNumber);
            Assert.Equal("03", plan.Layers[2].LayerNumberText);
            Assert.Equal(4, plan.Layers[3].LayerNumber);

            // The membrane still occupies a position, so the concrete centre is measured
            // from a 320 mm wall, not a 320 mm wall with a layer deleted.
            Assert.Equal(320, WallLayerRules.FeetToMm(plan.TotalWidthFeet), 6);
            Assert.Equal(200, WallLayerRules.FeetToMm(plan.Layers[2].CenterUFeet), 6);
        }

        [Fact]
        public void AMembraneInsideTheCoreCannotBeTheCarrier()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 1, CoreLastIndex = 2,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0, 100, "Finish1", "Ladrillo"),
                    Layer(1,   0, "Membrane", "Barrera"),    // in the core, but no volume
                    Layer(2, 200, "Structure", "Concreto"),
                    Layer(3,  20, "Finish2", "Yeso"),
                }
            };

            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.True(plan.Eligible);
            Assert.Equal(2, plan.CoreCarrierLayerIndex);
        }

        // ---- core carrier selection -----------------------------------------------

        [Fact]
        public void ASingleStructuralLayerInTheCoreIsTheCarrier()
        {
            WallSplitPlan plan = WallLayerRules.Plan(FiveLayer());
            Assert.Equal(3, plan.CoreCarrierLayerIndex);
            Assert.Equal("single_structural_layer_in_core", plan.CoreCarrierSelectionReason);
            Assert.Equal(3, plan.CoreFirstLayerIndex);
            Assert.Equal(3, plan.CoreLastLayerIndex);
        }

        [Fact]
        public void WithSeveralStructuralCoreLayersTheThickestWins()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 1, CoreLastIndex = 3,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0,  20, "Finish1", "Yeso"),
                    Layer(1, 100, "Structure", "Bloque"),
                    Layer(2,  50, "Substrate", "Mortero"),
                    Layer(3, 200, "Structure", "Concreto"),
                    Layer(4,  20, "Finish2", "Pintura"),
                }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.Equal(3, plan.CoreCarrierLayerIndex);
            Assert.Equal("thickest_structural_layer_in_core", plan.CoreCarrierSelectionReason);
        }

        [Fact]
        public void OnATieTheLowestOriginalIndexWinsAndSaysSo()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 1, CoreLastIndex = 3,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0,  20, "Finish1", "Yeso"),
                    Layer(1, 150, "Structure", "Bloque"),
                    Layer(2,  50, "Substrate", "Mortero"),
                    Layer(3, 150, "Structure", "Concreto"),
                    Layer(4,  20, "Finish2", "Pintura"),
                }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.Equal(1, plan.CoreCarrierLayerIndex);
            Assert.Equal("thickest_structural_layer_in_core_tie_lowest_index", plan.CoreCarrierSelectionReason);
        }

        [Fact]
        public void ATieWithinATenthOfAMillimetreIsStillATie()
        {
            // Decided on the 0.1 mm grid, not by double equality: two layers a nanometre
            // apart are the tie a human would call it.
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 0, CoreLastIndex = 1,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0, 150.00, "Structure", "A"),
                    Layer(1, 150.02, "Structure", "B"),
                }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.Equal(0, plan.CoreCarrierLayerIndex);
            Assert.Equal("thickest_structural_layer_in_core_tie_lowest_index", plan.CoreCarrierSelectionReason);
        }

        [Fact]
        public void WithNoStructuralLayerInTheCoreTheThickestCoreLayerCarries()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 1, CoreLastIndex = 2,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0,  20, "Finish1", "Yeso"),
                    Layer(1,  80, "Substrate", "Mortero"),
                    Layer(2, 120, "Insulation", "Lana"),
                    Layer(3,  20, "Finish2", "Pintura"),
                }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.Equal(2, plan.CoreCarrierLayerIndex);
            Assert.Equal("thickest_core_layer_no_structural", plan.CoreCarrierSelectionReason);
        }

        [Fact]
        public void AStructuralLayerOUTSIDETheCoreIsNotTheCarrier()
        {
            // The distinction the previous implementation could not make: Function=Structure
            // and "in the core" are different facts, and it only ever looked at the first.
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 2, CoreLastIndex = 2,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0, 100, "Structure", "Ladrillo estructural"),   // Structure, OUTSIDE the core
                    Layer(1,  50, "Insulation", "Lana"),
                    Layer(2, 200, "Substrate", "Concreto"),               // the core, not Structure
                    Layer(3,  20, "Finish2", "Yeso"),
                }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.Equal(2, plan.CoreCarrierLayerIndex);
            Assert.Equal("thickest_core_layer_no_structural", plan.CoreCarrierSelectionReason);
        }

        [Fact]
        public void AWallWithNoValidCoreIsRefusedByNameRatherThanFallingBackToLayerZero()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 2, CoreLastIndex = 1,  // inverted
                Layers = new List<WallLayerFacts>
                {
                    Layer(0, 100, "Finish1", "Ladrillo"),
                    Layer(1, 200, "Structure", "Concreto"),
                }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.False(plan.Eligible);
            Assert.Equal(WallSplitCodes.NoValidCore, plan.Rejection.Code);
            Assert.Contains("layer 0", plan.Rejection.Message);
        }

        [Fact]
        public void ACoreOfNothingButMembranesIsNotAValidCore()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 1, CoreLastIndex = 1,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0, 100, "Finish1", "Ladrillo"),
                    Layer(1,   0, "Membrane", "Barrera"),
                    Layer(2, 200, "Finish2", "Yeso"),
                }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.False(plan.Eligible);
            Assert.Equal(WallSplitCodes.NoValidCore, plan.Rejection.Code);
        }

        [Fact]
        public void SelectCoreCarrierRefusesToBeAskedAboutAWallWithNoCore()
        {
            var layers = new List<WallLayerFacts> { Layer(0, 100, "Finish1"), Layer(1, 100, "Finish2") };
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => WallLayerRules.SelectCoreCarrier(layers, 3, 1, out string _));
            Assert.Contains(WallSplitCodes.NoValidCore, ex.Message);
        }

        // ---- eligibility ----------------------------------------------------------

        [Fact]
        public void AStackedWallIsRefusedWithItsOwnCodeAndTheReasonWhy()
        {
            WallAssemblyFacts facts = FiveLayer();
            facts.WallKind = "Stacked";
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.False(plan.Eligible);
            Assert.Equal(WallSplitCodes.UnsupportedStackedWall, plan.Rejection.Code);
            Assert.Contains("deleted the doors with it", plan.Rejection.Message);
        }

        [Fact]
        public void ACurtainWallIsRefused()
        {
            WallAssemblyFacts facts = FiveLayer();
            facts.WallKind = "Curtain";
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.False(plan.Eligible);
            Assert.Equal(WallSplitCodes.NotBasicWall, plan.Rejection.Code);
        }

        [Fact]
        public void ASingleLayerWallHasNothingToSplit()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 0, CoreLastIndex = 0,
                Layers = new List<WallLayerFacts> { Layer(0, 200, "Structure", "Concreto") }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.False(plan.Eligible);
            Assert.Equal(WallSplitCodes.SingleLayer, plan.Rejection.Code);
        }

        [Fact]
        public void OneRealLayerPlusMembranesIsStillASingleLayerWall()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 1, CoreLastIndex = 1,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0,   0, "Membrane", "Barrera"),
                    Layer(1, 200, "Structure", "Concreto"),
                    Layer(2,   0, "Membrane", "Pintura"),
                }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.False(plan.Eligible);
            Assert.Equal(WallSplitCodes.SingleLayer, plan.Rejection.Code);
        }

        [Fact]
        public void AWallTypeWithNoCompoundStructureIsRefused()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", Layers = new List<WallLayerFacts>()
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.False(plan.Eligible);
            Assert.Equal(WallSplitCodes.NoCompoundStructure, plan.Rejection.Code);
        }

        // ---- cardinality ----------------------------------------------------------

        [Fact]
        public void NLayersWithVolumeProduceExactlyNWallsAndTheCarrierIsOneOfThem()
        {
            WallSplitPlan plan = WallLayerRules.Plan(FiveLayer());
            Assert.Equal(5, plan.Layers.Count);
            Assert.Equal(5, plan.WouldProduceWalls);
            Assert.Equal(1, plan.Layers.Count(l => l.IsCoreCarrier));
            Assert.True(plan.Layers.Single(l => l.IsCoreCarrier).Materialised);
        }

        // ---- roles ----------------------------------------------------------------

        [Fact]
        public void EveryLayerGetsARoleFromTheClosedSet()
        {
            WallSplitPlan plan = WallLayerRules.Plan(FiveLayer());
            Assert.All(plan.Layers, l => Assert.Contains(l.Role, LayerRole.All));

            Assert.Equal(LayerRole.Finish, plan.Layers[0].Role);       // Finish1, outside the core
            Assert.Equal(LayerRole.Shell, plan.Layers[1].Role);        // Membrane, outside the core
            Assert.Equal(LayerRole.Shell, plan.Layers[2].Role);        // Insulation, outside the core
            Assert.Equal(LayerRole.CoreCarrier, plan.Layers[3].Role);
            Assert.Equal(LayerRole.Finish, plan.Layers[4].Role);       // Finish2
        }

        [Fact]
        public void ACoreLayerThatIsNotTheCarrierIsCoreSecondary()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "T", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 0, CoreLastIndex = 1,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0, 100, "Structure", "Bloque"),
                    Layer(1, 200, "Structure", "Concreto"),
                }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.Equal(LayerRole.CoreSecondary, plan.Layers[0].Role);
            Assert.Equal(LayerRole.CoreCarrier, plan.Layers[1].Role);
        }

        // ---- naming ---------------------------------------------------------------

        [Fact]
        public void TheNameIsOriginalTypeThenMaterialThenTwoDigitLayerNumber()
        {
            WallSplitPlan plan = WallLayerRules.Plan(FiveLayer());
            Assert.Equal("EXT_Muro Fachada 25cm - Ladrillo - 01", plan.Layers[0].ExpectedTypeName);
            Assert.Equal("EXT_Muro Fachada 25cm - Aire - 02", plan.Layers[1].ExpectedTypeName);
            Assert.Equal("EXT_Muro Fachada 25cm - Lana - 03", plan.Layers[2].ExpectedTypeName);
            Assert.Equal("EXT_Muro Fachada 25cm - Concreto - 04", plan.Layers[3].ExpectedTypeName);
            Assert.Equal("EXT_Muro Fachada 25cm - Yeso - 05", plan.Layers[4].ExpectedTypeName);
        }

        [Fact]
        public void TheExteriorLayerIsAlwaysZeroOne()
        {
            WallSplitPlan plan = WallLayerRules.Plan(FiveLayer());
            Assert.Equal("01", plan.Layers[0].LayerNumberText);
            Assert.EndsWith(" - 01", plan.Layers[0].ExpectedTypeName);
        }

        [Fact]
        public void LayerNumbersPastNineAreStillPaddedAndPastNinetyNineAreNotTruncated()
        {
            Assert.Equal("01", WallLayerRules.FormatLayerNumber(1));
            Assert.Equal("09", WallLayerRules.FormatLayerNumber(9));
            Assert.Equal("10", WallLayerRules.FormatLayerNumber(10));
            Assert.Equal("11", WallLayerRules.FormatLayerNumber(11));
            Assert.Equal("100", WallLayerRules.FormatLayerNumber(100));
        }

        [Fact]
        public void LayerNumbersAreOneBasedAndZeroIsARefusal()
            => Assert.Throws<ArgumentOutOfRangeException>(() => WallLayerRules.FormatLayerNumber(0));

        [Fact]
        public void TwoLayersOfTheSameMaterialAreDistinguishedByTheirNumber()
        {
            var facts = new WallAssemblyFacts
            {
                WallTypeName = "MUR", WallTypeUniqueId = "wt", WallKind = "Basic",
                LocationLine = "WallCenterline", CoreFirstIndex = 1, CoreLastIndex = 1,
                Layers = new List<WallLayerFacts>
                {
                    Layer(0, 20, "Finish1", "Yeso"),
                    Layer(1, 200, "Structure", "Concreto"),
                    Layer(2, 20, "Finish2", "Yeso"),
                }
            };
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.Equal("MUR - Yeso - 01", plan.Layers[0].ExpectedTypeName);
            Assert.Equal("MUR - Yeso - 03", plan.Layers[2].ExpectedTypeName);
            Assert.NotEqual(plan.Layers[0].ExpectedTypeName, plan.Layers[2].ExpectedTypeName);
        }

        [Fact]
        public void AMissingMaterialBecomesMaterialSinAsignar()
        {
            Assert.Equal("MATERIAL_SIN_ASIGNAR", WallLayerRules.ResolveMaterialName(null));
            Assert.Equal("MATERIAL_SIN_ASIGNAR", WallLayerRules.ResolveMaterialName(""));
            Assert.Equal("MATERIAL_SIN_ASIGNAR", WallLayerRules.ResolveMaterialName("   "));
            Assert.Equal("T - MATERIAL_SIN_ASIGNAR - 02", WallLayerRules.ComposeTypeName("T", null, 2));
        }

        [Fact]
        public void OnlyCharactersRevitForbidsAreCleaned()
        {
            // Accents, spaces, digits, slashes, dots and quotes all survive: the rule is
            // "clean what Revit prohibits", not "normalise somebody's naming convention".
            Assert.Equal("Hormigón armado 25/30 \"visto\" 1.5",
                         WallLayerRules.SanitizeNamePart("Hormigón armado 25/30 \"visto\" 1.5"));
            Assert.Equal("A_B_C", WallLayerRules.SanitizeNamePart("A{B}C"));
            Assert.Equal("A_B", WallLayerRules.SanitizeNamePart("A:B"));
            Assert.Equal("trimmed", WallLayerRules.SanitizeNamePart("  trimmed  "));
        }

        [Fact]
        public void TheMaterialNameIsNeverTranslatedOrShortened()
        {
            const string longName = "Mampostería de ladrillo cerámico perforado tipo H-10 a la vista";
            Assert.Equal(longName, WallLayerRules.ResolveMaterialName(longName));
            Assert.Contains(longName, WallLayerRules.ComposeTypeName("Muro", longName, 1));
        }

        [Fact]
        public void TheOriginalTypeNameIsKeptWholeIncludingItsOwnHyphens()
        {
            string name = WallLayerRules.ComposeTypeName("EXT - Fachada - Tipo A", "Ladrillo", 1);
            Assert.Equal("EXT - Fachada - Tipo A - Ladrillo - 01", name);
        }

        [Fact]
        public void NoRoleWordLeaksIntoTheName()
        {
            WallSplitPlan plan = WallLayerRules.Plan(FiveLayer());
            foreach (WallLayerPlan layer in plan.Layers)
            {
                Assert.DoesNotContain("Core", layer.ExpectedTypeName);
                Assert.DoesNotContain("core_carrier", layer.ExpectedTypeName);
                Assert.DoesNotContain("Finish", layer.ExpectedTypeName);
                Assert.DoesNotContain("Structure", layer.ExpectedTypeName);
            }
        }

        [Fact]
        public void TheVariantNameIsTheExpectedNamePlusEightHexCharacters()
        {
            WallSplitPlan plan = WallLayerRules.Plan(FiveLayer());
            WallLayerPlan layer = plan.Layers[0];

            Assert.StartsWith(layer.ExpectedTypeName + " - ", layer.VariantTypeName);
            string suffix = layer.VariantTypeName.Substring(layer.ExpectedTypeName.Length + 3);
            Assert.Equal(8, suffix.Length);
            Assert.All(suffix, c => Assert.True(Uri.IsHexDigit(c), "digest must be hex: " + suffix));
            Assert.Equal(suffix, layer.ShortDigest);
        }

        [Fact]
        public void TheVariantNameIsDeterministicSoRerunsDoNotPileUpTypes()
        {
            string a = WallLayerRules.Plan(FiveLayer()).Layers[0].VariantTypeName;
            string b = WallLayerRules.Plan(FiveLayer()).Layers[0].VariantTypeName;
            Assert.Equal(a, b);
        }

        [Fact]
        public void FlippedDoesNotEnterTheNameAtAll()
        {
            // Rule 6: wall.Flipped changes which face of the building is outside; it does
            // not renumber the CompoundStructure. The plan takes no flip argument at all,
            // which is the strongest possible form of that guarantee.
            WallSplitPlan plan = WallLayerRules.Plan(FiveLayer());
            Assert.Equal("EXT_Muro Fachada 25cm - Ladrillo - 01", plan.Layers[0].ExpectedTypeName);
            Assert.Equal(0, plan.Layers.First(l => l.LayerNumber == 1).LayerIndex);
        }

        // ---- type identity --------------------------------------------------------

        [Fact]
        public void TwoLayersWithTheSameNameButDifferentMaterialsAreDifferentTypes()
        {
            var a = Layer(0, 200, "Structure", "Concreto", uid: "material-A");
            var b = Layer(0, 200, "Structure", "Concreto", uid: "material-B");

            Assert.Equal(WallLayerRules.ComposeTypeName("T", a.MaterialName, 1),
                         WallLayerRules.ComposeTypeName("T", b.MaterialName, 1));
            Assert.NotEqual(WallLayerRules.LayerTypeFingerprint(a, "Basic", "Wrap", "Cap"),
                            WallLayerRules.LayerTypeFingerprint(b, "Basic", "Wrap", "Cap"));
        }

        [Fact]
        public void TwoThicknessesThatRoundToTheSameNameAreDifferentTypes()
        {
            // "Concreto_20.0cm" was the same name for 200.0 mm and 200.4 mm. The digest
            // separates them; the name never could.
            var a = Layer(0, 200.0, "Structure", "Concreto");
            var b = Layer(0, 200.4, "Structure", "Concreto");
            Assert.NotEqual(WallLayerRules.LayerTypeFingerprint(a, "Basic", "Wrap", "Cap"),
                            WallLayerRules.LayerTypeFingerprint(b, "Basic", "Wrap", "Cap"));
        }

        [Fact]
        public void AWidthDifferenceBelowTheGridDoesNotChangeTheType()
        {
            var a = Layer(0, 200.0, "Structure", "Concreto");
            var b = Layer(0, 200.0 + 1e-6, "Structure", "Concreto");
            Assert.Equal(WallLayerRules.LayerTypeFingerprint(a, "Basic", "Wrap", "Cap"),
                         WallLayerRules.LayerTypeFingerprint(b, "Basic", "Wrap", "Cap"));
        }

        [Fact]
        public void FunctionIsPartOfTypeIdentity()
        {
            var layer = Layer(0, 200, "Structure", "Concreto");
            var other = Layer(0, 200, "Substrate", "Concreto");
            Assert.NotEqual(WallLayerRules.LayerTypeFingerprint(layer, "Basic", "Wrap", "Cap"),
                            WallLayerRules.LayerTypeFingerprint(other, "Basic", "Wrap", "Cap"));
        }

        [Fact]
        public void WrappingAndEndCapAreBothPartOfTypeIdentity()
        {
            // They are in the digest BECAUSE the builder sets them and the matcher re-reads
            // them. Anything in one of those three places has to be in all three.
            var layer = Layer(0, 200, "Structure", "Concreto");
            Assert.NotEqual(WallLayerRules.LayerTypeFingerprint(layer, "Basic", "Exterior", "Cap"),
                            WallLayerRules.LayerTypeFingerprint(layer, "Basic", "Interior", "Cap"));
            Assert.NotEqual(WallLayerRules.LayerTypeFingerprint(layer, "Basic", "Wrap", "Exterior"),
                            WallLayerRules.LayerTypeFingerprint(layer, "Basic", "Wrap", "Interior"));
        }

        [Fact]
        public void CoreMembershipIsNotPartOfTypeIdentityAndTheReasonIsRecorded()
        {
            // In a single-layer structure the one layer IS the core, so core membership is
            // a fact about the SOURCE assembly and not about the resulting type. Two layers
            // that differ only by it legitimately share a type - and the exclusion is
            // documented rather than implied by its absence.
            Assert.True(WallLayerRules.TypeIdentityExclusions.ContainsKey("is_core"));
            Assert.Contains("single-layer", WallLayerRules.TypeIdentityExclusions["is_core"]);
        }

        [Fact]
        public void TheFingerprintIsHexAndStableAcrossCalls()
        {
            var layer = Layer(0, 200, "Structure", "Concreto");
            string one = WallLayerRules.LayerTypeFingerprint(layer, "Basic", "Wrap", "Cap");
            string two = WallLayerRules.LayerTypeFingerprint(layer, "Basic", "Wrap", "Cap");
            Assert.Equal(one, two);
            Assert.Equal(64, one.Length);
            Assert.All(one, c => Assert.True(Uri.IsHexDigit(c)));
        }

        // ---- tolerance ------------------------------------------------------------

        [Fact]
        public void DeviationIsReportedInMillimetresAndComparedAgainstTheOneTolerance()
        {
            double expected = WallLayerRules.MmToFeet(100.0);
            Assert.Equal(0.0, WallLayerRules.DeviationMm(expected, expected), 9);
            Assert.True(WallLayerRules.WithinTolerance(expected, WallLayerRules.MmToFeet(100.4)));
            Assert.False(WallLayerRules.WithinTolerance(expected, WallLayerRules.MmToFeet(100.6)));
            Assert.Equal(0.6, WallLayerRules.DeviationMm(expected, WallLayerRules.MmToFeet(100.6)), 6);
        }

        [Fact]
        public void DeviationHasNoSignBecauseTheTwoOffsetsAlreadyCarryIt()
        {
            double e = WallLayerRules.MmToFeet(100.0);
            Assert.Equal(WallLayerRules.DeviationMm(e, WallLayerRules.MmToFeet(101.0)),
                         WallLayerRules.DeviationMm(e, WallLayerRules.MmToFeet(99.0)), 9);
        }

        [Fact]
        public void ToleranceIsHalfAMillimetreExpressedInFeet()
        {
            Assert.Equal(0.5, WallLayerRules.ToleranceMm, 9);
            Assert.Equal(0.5, WallLayerRules.FeetToMm(WallLayerRules.ToleranceFeet), 9);
        }

        // ---- the plan fingerprint -------------------------------------------------

        private static string Fingerprint(WallAssemblyFacts facts, WallSplitPlan plan,
                                          bool flipped = false, string top = "Level 2",
                                          IEnumerable<string> deps = null,
                                          IEnumerable<double> curve = null,
                                          string wallUniqueId = "wall-uid",
                                          long elementId = 1234,
                                          string joins = "join-digest")
            => WallLayerRules.WallPlanFingerprint("doc-key", wallUniqueId, elementId, facts, plan,
                                                  flipped, curve ?? new[] { 0.0, 0.0, 0.0, 10.0, 0.0, 0.0 },
                                                  deps ?? new[] { "door-1", "window-2" },
                                                  joins,
                                                  // The wall's own constraints ride their own digest now.
                                                  new FactBook().Add("top", top).Digest(),
                                                  "structural_in_core_then_thickest");

        [Fact]
        public void TheSamePlanFingerprintsTheSameWay()
        {
            WallAssemblyFacts facts = FiveLayer();
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.Equal(Fingerprint(facts, plan), Fingerprint(facts, plan));
        }

        [Fact]
        public void TheOrderDependenciesArriveInDoesNotChangeTheFingerprint()
        {
            WallAssemblyFacts facts = FiveLayer();
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.Equal(Fingerprint(facts, plan, deps: new[] { "a", "b", "c" }),
                         Fingerprint(facts, plan, deps: new[] { "c", "a", "b" }));
        }

        [Fact]
        public void ADependencyAppearingAfterTheDryRunChangesTheFingerprint()
        {
            WallAssemblyFacts facts = FiveLayer();
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.NotEqual(Fingerprint(facts, plan, deps: new[] { "door-1" }),
                            Fingerprint(facts, plan, deps: new[] { "door-1", "door-2" }));
        }

        [Fact]
        public void ChangingTheWallTypeUnderneathTheApplyChangesTheFingerprint()
        {
            WallAssemblyFacts before = FiveLayer();
            WallSplitPlan planBefore = WallLayerRules.Plan(before);

            WallAssemblyFacts after = FiveLayer();
            after.Layers[3].WidthFeet = 250 * Mm;              // somebody edited the core
            WallSplitPlan planAfter = WallLayerRules.Plan(after);

            Assert.NotEqual(Fingerprint(before, planBefore), Fingerprint(after, planAfter));
        }

        [Fact]
        public void ChangingOnlyTheLocationLineChangesTheFingerprint()
        {
            WallAssemblyFacts a = FiveLayer("WallCenterline");
            WallAssemblyFacts b = FiveLayer("CoreExterior");
            Assert.NotEqual(Fingerprint(a, WallLayerRules.Plan(a)),
                            Fingerprint(b, WallLayerRules.Plan(b)));
        }

        [Fact]
        public void MovingTheWallChangesTheFingerprint()
        {
            WallAssemblyFacts facts = FiveLayer();
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.NotEqual(Fingerprint(facts, plan, curve: new[] { 0.0, 0.0, 0.0, 10.0, 0.0, 0.0 }),
                            Fingerprint(facts, plan, curve: new[] { 0.0, 0.0, 0.0, 10.5, 0.0, 0.0 }));
        }

        [Fact]
        public void FlippingTheWallChangesTheFingerprint()
        {
            WallAssemblyFacts facts = FiveLayer();
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.NotEqual(Fingerprint(facts, plan, flipped: false), Fingerprint(facts, plan, flipped: true));
        }

        [Fact]
        public void ChangingTheTopConstraintChangesTheFingerprint()
        {
            WallAssemblyFacts facts = FiveLayer();
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.NotEqual(Fingerprint(facts, plan, top: "Level 2"), Fingerprint(facts, plan, top: "Level 3"));
        }

        [Fact]
        public void TwoDIFFERENTWallsWithTheSameShapeDoNotShareAFingerprint()
        {
            // The hole the count-based token had: two walls whose plans produce the same
            // numbers are not the same wall, and approving one must not approve the other.
            WallAssemblyFacts facts = FiveLayer();
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.NotEqual(Fingerprint(facts, plan, wallUniqueId: "wall-A", elementId: 1),
                            Fingerprint(facts, plan, wallUniqueId: "wall-B", elementId: 2));
        }

        [Fact]
        public void AnUnmeasuredCurveFactIsRefusedRatherThanFingerprinted()
        {
            WallAssemblyFacts facts = FiveLayer();
            WallSplitPlan plan = WallLayerRules.Plan(facts);
            Assert.Throws<ArgumentException>(() => Fingerprint(facts, plan, curve: new[] { 0.0, double.NaN }));
        }

        // ---- vocabulary -----------------------------------------------------------

        [Fact]
        public void TheDependencyVocabularyHasNoGenericWarningMember()
        {
            Assert.False(DependencyDisposition.IsKnown("warning"));
            Assert.False(DependencyDisposition.IsKnown("unknown"));
            Assert.Equal(5, DependencyDisposition.All.Length);
            Assert.All(DependencyDisposition.All, v => Assert.True(DependencyDisposition.IsKnown(v)));
        }

        [Fact]
        public void EveryCodeIsDistinctAndLowerSnakeCase()
        {
            Assert.Equal(WallSplitCodes.All.Length, WallSplitCodes.All.Distinct(StringComparer.Ordinal).Count());
            Assert.All(WallSplitCodes.All, c =>
            {
                Assert.False(string.IsNullOrWhiteSpace(c));
                Assert.Equal(c.ToLowerInvariant(), c);
                Assert.DoesNotContain(" ", c);
            });
        }

        [Fact]
        public void ARejectionCannotBeBuiltWithoutACode()
            => Assert.Throws<ArgumentException>(() => new WallSplitRejection("", "no code"));

        [Fact]
        public void EveryRejectionThisFileEmitsComesFromTheClosedSet()
        {
            var cases = new List<WallAssemblyFacts>();

            WallAssemblyFacts stacked = FiveLayer(); stacked.WallKind = "Stacked"; cases.Add(stacked);
            WallAssemblyFacts curtain = FiveLayer(); curtain.WallKind = "Curtain"; cases.Add(curtain);
            WallAssemblyFacts empty = FiveLayer(); empty.Layers = new List<WallLayerFacts>(); cases.Add(empty);
            WallAssemblyFacts noCore = FiveLayer(); noCore.CoreFirstIndex = 4; noCore.CoreLastIndex = 2; cases.Add(noCore);
            WallAssemblyFacts badLine = FiveLayer("NotALocationLine"); cases.Add(badLine);
            cases.Add(new WallAssemblyFacts
            {
                WallTypeName = "T", WallKind = "Basic", LocationLine = "WallCenterline",
                CoreFirstIndex = 0, CoreLastIndex = 0,
                Layers = new List<WallLayerFacts> { Layer(0, 200, "Structure") }
            });

            foreach (WallAssemblyFacts facts in cases)
            {
                WallSplitPlan plan = WallLayerRules.Plan(facts);
                Assert.False(plan.Eligible);
                Assert.Contains(plan.Rejection.Code, WallSplitCodes.All);
                Assert.NotEqual("", plan.Rejection.Message);
            }
        }

        // ------------------------------------------------------------------
        // CutClaim - what may be said about a hole nobody looked for.
        //
        // The live evidence this exists for
        // (artifacts/live/wallsplit-20260830-190310/call-003-apply-1664073.json):
        //     cut_coverage.probed = false
        //     cut_checks          = 0
        //     cut_verified        = TRUE on all seven layers
        // including two zero-width membranes that have no wall at all. One
        // unguarded .All() over an empty sequence, and a field that reads as a
        // measurement said the hole was proved.
        // ------------------------------------------------------------------

        [Fact]
        public void A_layer_nobody_probed_makes_no_claim()
        {
            // THE REGRESSION, in the exact shape it shipped: a materialised secondary
            // layer, the probe never ran, no checks. It used to answer true.
            Assert.Null(WallLayerRules.CutClaim(
                isCoreCarrier: false, materialised: true,
                coverageProbed: false, checksForLayer: 0, checksClear: 0));
        }

        [Fact]
        public void A_probe_that_ran_but_not_on_this_layer_makes_no_claim()
        {
            // The wall was probed - it carries a door - but no row came back for THIS
            // layer. That is not a pass; it is a gap, and .All() called it a pass.
            Assert.Null(WallLayerRules.CutClaim(
                isCoreCarrier: false, materialised: true,
                coverageProbed: true, checksForLayer: 0, checksClear: 0));
        }

        [Fact]
        public void A_layer_with_no_volume_makes_no_claim()
        {
            // Layers 03 and 06 of the measured wall: zero-width membranes, no wall
            // exists, and both published cut_verified true.
            Assert.Null(WallLayerRules.CutClaim(
                isCoreCarrier: false, materialised: false,
                coverageProbed: true, checksForLayer: 3, checksClear: 3));
        }

        [Fact]
        public void The_carrier_makes_no_claim_because_the_test_does_not_apply_to_it()
        {
            // It keeps the original inserts natively. "Verified" would be describing a
            // test that does not apply, which is the same lie in a smaller font.
            Assert.Null(WallLayerRules.CutClaim(
                isCoreCarrier: true, materialised: true,
                coverageProbed: false, checksForLayer: 0, checksClear: 0));
        }

        [Fact]
        public void A_layer_whose_every_ray_came_back_clear_passes()
        {
            Assert.True(WallLayerRules.CutClaim(
                isCoreCarrier: false, materialised: true,
                coverageProbed: true, checksForLayer: 5, checksClear: 5));
        }

        [Fact]
        public void One_ray_that_still_found_material_fails_the_layer()
        {
            Assert.False(WallLayerRules.CutClaim(
                isCoreCarrier: false, materialised: true,
                coverageProbed: true, checksForLayer: 5, checksClear: 4));
        }

        [Fact]
        public void The_reason_is_stated_whenever_no_claim_is_made()
        {
            // A null with no reason is just a shrug. Every state that declines to
            // answer says why, and the one that answers says nothing.
            var cases = new[]
            {
                new { carrier = false, mat = false, probed = true,  checks = 0 },
                new { carrier = true,  mat = true,  probed = true,  checks = 0 },
                new { carrier = false, mat = true,  probed = false, checks = 0 },
                new { carrier = false, mat = true,  probed = true,  checks = 0 },
            };
            foreach (var c in cases)
            {
                Assert.Null(WallLayerRules.CutClaim(c.carrier, c.mat, c.probed, c.checks, 0));
                string why = WallLayerRules.CutNotProbedReason(c.carrier, c.mat, c.probed, c.checks);
                Assert.False(string.IsNullOrWhiteSpace(why), "no reason given for a declined claim");
            }

            Assert.Null(WallLayerRules.CutNotProbedReason(false, true, true, 5));
        }

        [Fact]
        public void The_measured_canary_would_now_claim_nothing_on_any_layer()
        {
            // The seven layers of the wall that produced the evidence, replayed through
            // the rule: carrier at 05, membranes at 03 and 06, nothing probed. Every one
            // of them answered true before; not one of them may answer true now.
            var layers = new[]
            {
                new { number = 1, carrier = false, mat = true },
                new { number = 2, carrier = false, mat = true },
                new { number = 3, carrier = false, mat = false },
                new { number = 4, carrier = false, mat = true },
                new { number = 5, carrier = true,  mat = true },
                new { number = 6, carrier = false, mat = false },
                new { number = 7, carrier = false, mat = true },
            };
            foreach (var l in layers)
                Assert.Null(WallLayerRules.CutClaim(l.carrier, l.mat, false, 0, 0));
        }

        // ------------------------------------------------------------------
        // The parameter policy. One table, read by the copier and the verifier.
        // ------------------------------------------------------------------

        [Fact]
        public void A_parameter_Revit_computes_is_never_copied_and_may_change()
        {
            // THE ONE THAT ROLLED BACK EVERY WALL WITH A DOOR. The carrier keeps its
            // identity and becomes one layer thick, so the areas and volumes Revit derives
            // from it necessarily change.
            foreach (string key in new[] { "bip:HOST_AREA_COMPUTED", "bip:HOST_VOLUME_COMPUTED",
                                           "bip:HOST_PERIMETER_COMPUTED",
                                           "bip:LAYER_ELEM_AREA_COMPUTED", "bip:LAYER_ELEM_VOLUME_COMPUTED",
                                           "bip:REINFORCEMENT_VOLUME", "bip:REIN_EST_BAR_VOLUME",
                                           "bip:REBAR_MIN_LENGTH", "bip:REBAR_MAX_LENGTH",
                                           "bip:REBAR_ELEM_LENGTH", "bip:REBAR_ELEM_TOTAL_LENGTH" })
            {
                Assert.Equal(WallLayerRules.ParameterKind.ComputedByRevit, WallLayerRules.KindOf(key));
                Assert.False(WallLayerRules.ShouldCopy(key), key + " must not be copied");
                Assert.True(WallLayerRules.MayChangeWithoutExplanation(key), key + " must be allowed to change");
                Assert.False(string.IsNullOrWhiteSpace(WallLayerRules.ParameterReason(key)));
            }
        }

        [Fact]
        public void Identity_is_never_copied_and_never_excused()
        {
            // The two questions are different, and the old tables confused them. Not
            // copying a parameter is no reason to accept it changing by itself: a door
            // whose family or type changed is the failure this verification exists for.
            foreach (string key in new[] { "bip:ELEM_TYPE_PARAM", "bip:ELEM_FAMILY_PARAM",
                                           "bip:ELEM_FAMILY_AND_TYPE_PARAM",
                                           "bip:ELEM_CATEGORY_PARAM", "bip:ELEM_CATEGORY_PARAM_MT" })
            {
                Assert.Equal(WallLayerRules.ParameterKind.Identity, WallLayerRules.KindOf(key));
                Assert.False(WallLayerRules.ShouldCopy(key));
                Assert.False(WallLayerRules.MayChangeWithoutExplanation(key), key + " must NOT be excused");
            }
        }

        [Fact]
        public void What_the_operation_sets_itself_is_not_also_copied_generically()
        {
            foreach (string key in new[] { "bip:WALL_KEY_REF_PARAM", "bip:WALL_BASE_CONSTRAINT",
                                           "bip:WALL_BASE_OFFSET", "bip:WALL_HEIGHT_TYPE",
                                           "bip:WALL_TOP_OFFSET", "bip:WALL_USER_HEIGHT_PARAM" })
            {
                Assert.Equal(WallLayerRules.ParameterKind.SetExplicitly, WallLayerRules.KindOf(key));
                Assert.False(WallLayerRules.ShouldCopy(key));
                Assert.False(WallLayerRules.MayChangeWithoutExplanation(key));
            }
            Assert.Equal(WallLayerRules.ParameterKind.ControlledByType,
                         WallLayerRules.KindOf("bip:WALL_ATTR_WIDTH_PARAM"));
        }

        [Fact]
        public void The_room_parameters_are_the_ones_that_exist()
        {
            // The old verifier table named bip:FROM_ROOM_MODULE and bip:TO_ROOM_MODULE.
            // Neither is a member of BuiltInParameter on Revit 2026 - checked by
            // reflection over RevitAPI.dll - so those two entries could never match
            // anything at all. From/to room are FamilyInstance properties. These are real.
            foreach (string key in new[] { "bip:ELEM_ROOM_ID", "bip:ELEM_ROOM_NUMBER", "bip:ELEM_ROOM_NAME" })
            {
                Assert.Equal(WallLayerRules.ParameterKind.ContextDerived, WallLayerRules.KindOf(key));
                Assert.True(WallLayerRules.MayChangeWithoutExplanation(key));
            }
            Assert.DoesNotContain("bip:FROM_ROOM_MODULE", WallLayerRules.ClassifiedParameterKeys);
            Assert.DoesNotContain("bip:TO_ROOM_MODULE", WallLayerRules.ClassifiedParameterKeys);
        }

        [Fact]
        public void An_unlisted_parameter_is_authored_so_it_is_copied_and_its_change_reported()
        {
            // The default fails LOUDLY and on purpose. An unlisted computed parameter
            // surfaces as a named mismatch, which is exactly how HOST_AREA_COMPUTED was
            // found; excusing anything unfamiliar would have hidden it.
            foreach (string key in new[] { "bip:INSTANCE_SILL_HEIGHT_PARAM", "bip:INSTANCE_HEAD_HEIGHT_PARAM",
                                           "guid:1234abcd", "def:Mark", "bip:PHASE_DEMOLISHED" })
            {
                Assert.Equal(WallLayerRules.ParameterKind.Authored, WallLayerRules.KindOf(key));
                Assert.True(WallLayerRules.ShouldCopy(key), key + " is the user's own data");
                Assert.False(WallLayerRules.MayChangeWithoutExplanation(key), key + " must not be excused");
                Assert.Null(WallLayerRules.ParameterReason(key));
            }
            Assert.Equal(WallLayerRules.ParameterKind.Authored, WallLayerRules.KindOf(null));
        }

        // ------------------------------------------------------------------
        // The chain.
        // ------------------------------------------------------------------

        [Fact]
        public void Two_layers_touch_when_the_gap_between_them_is_nothing()
        {
            // The measured seven-layer wall, in feet. Widths 92 / 75 / 19.5 / 150 / 12.5 mm
            // and offsets +128.5 / +45 / -2.25 / -87 / -168.25 from the location line.
            double w1 = WallLayerRules.MmToFeet(92.0), o1 = WallLayerRules.MmToFeet(128.5);
            double w2 = WallLayerRules.MmToFeet(75.0), o2 = WallLayerRules.MmToFeet(45.0);
            double w4 = WallLayerRules.MmToFeet(19.5), o4 = WallLayerRules.MmToFeet(-2.25);
            double w5 = WallLayerRules.MmToFeet(150.0), o5 = WallLayerRules.MmToFeet(-87.0);
            double w7 = WallLayerRules.MmToFeet(12.5), o7 = WallLayerRules.MmToFeet(-168.25);

            // The chain: every consecutive pair touches.
            Assert.True(WallLayerRules.LayersTouch(o1, w1, o2, w2));
            Assert.True(WallLayerRules.LayersTouch(o2, w2, o4, w4));
            Assert.True(WallLayerRules.LayersTouch(o4, w4, o5, w5));
            Assert.True(WallLayerRules.LayersTouch(o5, w5, o7, w7));

            // The star: the carrier is 94.5 mm from layer 01 and 19.5 mm from layer 02,
            // and those are exactly the two pairs Revit warned about.
            Assert.False(WallLayerRules.LayersTouch(o5, w5, o1, w1));
            Assert.False(WallLayerRules.LayersTouch(o5, w5, o2, w2));
        }

        [Fact]
        public void The_chain_links_consecutive_layers_and_nothing_else()
        {
            // Layer indices 0, 1, 3, 4, 6 - the materialised ones; the zero-width
            // membranes at 2 and 5 produce no wall, so 1 and 3 are neighbours.
            var edges = WallLayerRules.ChainEdges(new[] { 0, 1, 3, 4, 6 });
            Assert.Equal(4, edges.Count);
            Assert.Equal(new[] { 0, 1 }, edges[0]);
            Assert.Equal(new[] { 1, 3 }, edges[1]);
            Assert.Equal(new[] { 3, 4 }, edges[2]);
            Assert.Equal(new[] { 4, 6 }, edges[3]);

            // A star would have joined the carrier (4) to all of 0, 1, 3, 6.
            Assert.DoesNotContain(edges, e => e[0] == 4 && e[1] == 0);
            Assert.DoesNotContain(edges, e => e[0] == 4 && e[1] == 1);
        }

        [Fact]
        public void A_single_layer_needs_no_edges_and_none_are_invented()
        {
            Assert.Empty(WallLayerRules.ChainEdges(new[] { 3 }));
            Assert.Empty(WallLayerRules.ChainEdges(new int[0]));
            Assert.Empty(WallLayerRules.ChainEdges(null));
        }

        [Fact]
        public void An_edge_key_does_not_depend_on_which_end_you_start_from()
        {
            Assert.Equal(WallLayerRules.EdgeKey(10, 20), WallLayerRules.EdgeKey(20, 10));
            Assert.NotEqual(WallLayerRules.EdgeKey(10, 20), WallLayerRules.EdgeKey(10, 21));
        }
    }
}
