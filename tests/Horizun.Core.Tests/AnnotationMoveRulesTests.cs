using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AnnotationMoveRulesTests
    {
        private static AnnotationMoveRules.Curve Line(params double[][] points) =>
            new AnnotationMoveRules.Curve { Kind = "Line", Points = new List<double[]>(points) };
        private static List<List<AnnotationMoveRules.Curve>> Loops(params AnnotationMoveRules.Curve[] curves) =>
            new List<List<AnnotationMoveRules.Curve>> { new List<AnnotationMoveRules.Curve>(curves) };
        private static double[] P(double x, double y) => new[] { x, y, 0.0 };

        [Fact]
        public void NoteWithoutLocationIsVerifiedByTranslatedCoord()
        {
            Assert.True(AnnotationMoveRules.PointMoved(P(1, 2), P(4, 6), P(3, 4)));
            Assert.False(AnnotationMoveRules.PointMoved(P(1, 2), P(4, 5), P(3, 4)));
        }

        [Fact]
        public void ProjectionUsesViewNormalAndRejectsInvalidPlane()
        {
            Assert.Equal(new[] { 2.0, 3.0, 0.0 }, AnnotationMoveRules.ProjectToPlane(new[] { 2.0, 3.0, 4.0 }, new[] { 0.0, 0.0, 2.0 }));
            Assert.Equal(new[] { 0.0, 3.0, 4.0 }, AnnotationMoveRules.ProjectToPlane(new[] { 2.0, 3.0, 4.0 }, new[] { 1.0, 0.0, 0.0 }));
            Assert.Throws<System.ArgumentException>(() => AnnotationMoveRules.ProjectToPlane(new[] { 1.0, 2.0, 3.0 }, new[] { 0.0, 0.0, 0.0 }));
            Assert.False(AnnotationMoveRules.PointMoved(P(1, 2), P(1, 2), P(1, 0), AnnotationMoveRules.PositionToleranceFeet));
        }

        [Fact]
        public void RegionRequiresTranslatedContoursEvenWhenBoxWouldMatch()
        {
            var before = Loops(Line(P(0, 0), P(2, 0)), Line(P(2, 0), P(1, 2)));
            var moved = Loops(Line(P(4, 3), P(3, 5)), Line(P(4, 3), P(2, 3)));
            var wrong = Loops(Line(P(2, 3), P(4, 3)), Line(P(4, 3), P(2, 5)));
            Assert.True(AnnotationMoveRules.ContoursMoved(before, moved, P(2, 3)));
            Assert.False(AnnotationMoveRules.ContoursMoved(before, wrong, P(2, 3)));
            Assert.False(AnnotationMoveRules.ContoursMoved(before, null, P(2, 3)));
        }

        [Fact]
        public void LargeBatchIsRejectedByEstimateBeforeRehearsal()
        {
            Assert.True(AnnotationMoveRules.WithinBudget(10, 10));
            Assert.False(AnnotationMoveRules.WithinBudget(11, 11));
            Assert.False(AnnotationMoveRules.WithinBudget(1, 400));
            Assert.Equal(600, AnnotationMoveRules.EstimatedSeconds(1, 400));
        }

    }
}
