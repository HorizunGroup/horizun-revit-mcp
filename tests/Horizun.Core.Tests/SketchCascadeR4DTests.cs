using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SketchCascadeR4DTests
    {
        [Fact]
        public void SketchCurveNeedsProvenMembershipAndInternalOwner()
        {
            var targets = new HashSet<long> { 1 };
            var sketches = new HashSet<long> { 2 };
            Assert.Equal(InternalDependentRules.SketchPart, InternalDependentRules.Classify("DetailLine", "OST_Lines", false, null, targets, sketches, 2, true));
            Assert.Null(InternalDependentRules.Classify("DetailLine", "OST_Lines", false, null, targets, sketches, 2));
            Assert.Null(InternalDependentRules.Classify("DetailLine", "OST_Lines", false, null, targets, sketches, 9, true));
            Assert.Equal(InternalDependentRules.ConstrainingDimension, InternalDependentRules.Classify("Dimension", null, true, new long[] { 1, 2 }, targets, sketches));
            Assert.Null(InternalDependentRules.Classify("Dimension", null, true, new long[] { 2, 9 }, targets, sketches));
            Assert.Null(InternalDependentRules.Classify("Dimension", null, true, null, targets, sketches));
        }

        [Fact]
        public void AutomaticSketchDimensionIsInternalOnlyUnderATargetOrSketchPart()
        {
            var targets = new HashSet<long> { 1 };
            var sketches = new HashSet<long> { 2 };
            Assert.True(InternalDependentRules.IsInternalAutomaticSketchDimension("OST_WeakDims", 1, targets, sketches));
            Assert.True(InternalDependentRules.IsInternalAutomaticSketchDimension("OST_WeakDims", 2, targets, sketches));
            Assert.False(InternalDependentRules.IsInternalAutomaticSketchDimension("OST_WeakDims", 9, targets, sketches));
            Assert.False(InternalDependentRules.IsInternalAutomaticSketchDimension("OST_WeakDims", null, targets, sketches));
            Assert.False(InternalDependentRules.IsInternalAutomaticSketchDimension("OST_Dimensions", 1, targets, sketches));
        }
    }
}
