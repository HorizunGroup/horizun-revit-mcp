using System.Collections.Generic;
using Xunit;
using Horizun.Revit.Core;

namespace Horizun.Core.Tests
{
    public class AuditCoherenceRulesTests
    {
        [Theory] [InlineData(11, true)] [InlineData(10, false)] [InlineData(0, false)]
        public void SupportThreshold(double d, bool expected) => Assert.Equal(expected, AuditCoherenceRules.IsUnsupported(d));
        [Theory] [InlineData(.0011, true)] [InlineData(.001, false)] [InlineData(0, false)]
        public void OverlapThreshold(double v, bool expected) => Assert.Equal(expected, AuditCoherenceRules.IsStructuralOverlap(v));
        [Theory] [InlineData(49.9, true)] [InlineData(-49.9, true)] [InlineData(50, false)] [InlineData(70, false)]
        public void FinishTolerance(double d, bool expected) => Assert.Equal(expected, AuditCoherenceRules.IsFloorNearLevel(d));
        [Theory] [InlineData(1000, true)] [InlineData(1000.1, false)] [InlineData(0, true)]
        public void BalconyAccessDistance(double d, bool expected) => Assert.Equal(expected, AuditCoherenceRules.IsBalconyDoorNearEdge(d));
        [Theory] [InlineData(301, true)] [InlineData(300, false)] [InlineData(0, false)]
        public void DoorDropThreshold(double d, bool expected) => Assert.Equal(expected, AuditCoherenceRules.IsDropBeyondTolerance(d));
        [Theory]
        [InlineData("Default", null, null, null, false, true)]
        [InlineData("Por defecto", null, null, null, false, true)]
        [InlineData("<Por categoría>", null, null, null, false, true)]
        [InlineData("Concrete", 140, 141, 140, false, true)]
        [InlineData("Concrete", 140, 141, 140, true, false)]
        [InlineData("Blue", 20, 40, 180, false, false)]
        [InlineData("Concrete", null, null, null, false, false)]
        public void DefaultMaterialClassification(string name, int? r, int? g, int? b, bool texture, bool expected) =>
            Assert.Equal(expected, AuditCoherenceRules.IsDefaultOrNeutralGrey(name, r, g, b, texture));
        [Theory]
        [InlineData(true, 1, false, false, 0, true)]
        [InlineData(false, 4, false, false, 0, false)]
        [InlineData(false, 0, true, false, 0, true)]
        [InlineData(false, 0, false, true, 0, true)]
        [InlineData(false, 0, false, false, 1, true)]
        [InlineData(false, 0, false, false, 0, false)]
        public void RailingInfill(bool pattern, int count, bool panel, bool glass, int rails, bool expected) =>
            Assert.Equal(expected, AuditCoherenceRules.HasRailingInfill(pattern, count, panel, glass, rails));
        [Fact] public void RoomDoorMatchesEitherSide() => Assert.True(AuditCoherenceRules.HasRoomDoor(new[] { "r1" }, null, "r1"));
        [Fact] public void RoomDoorDoesNotMatchOtherRoom() => Assert.False(AuditCoherenceRules.HasRoomDoor(new[] { "r1" }, null, "r2"));
        [Fact] public void RoomWithoutIdHasNoDoor() => Assert.False(AuditCoherenceRules.HasRoomDoor(new[] { "r1" }, null, null));
        [Theory]
        [InlineData(0, 0, 0, true)] [InlineData(.5, 10, 10, true)]
        [InlineData(2, 0, 0, false)] [InlineData(0, 11, 0, false)] [InlineData(0, 0, 11, false)]
        public void CollinearContiguous(double angle, double offset, double gap, bool expected) =>
            Assert.Equal(expected, AuditCoherenceRules.AreCollinearAndContiguous(angle, offset, gap));
        [Fact] public void SectionsGroupByExactKey()
        {
            var groups = AuditCoherenceRules.GroupSections(new[] { "A", "B", "A" }, x => x);
            Assert.Equal(2, groups.Count); Assert.Equal(2, groups["A"].Count); Assert.Single(groups["B"]);
        }
        [Fact] public void NullRowsProduceEmptyGroups() =>
            Assert.Empty(AuditCoherenceRules.GroupSections<string>(null, x => x));

        private static CoherenceBeam Beam(long id, long type, double x1, double y1, double x2, double y2, double z = 3000) =>
            new CoherenceBeam { Id = id, TypeId = type, TypeName = "T" + type, X1 = x1, Y1 = y1, Z1 = z, X2 = x2, Y2 = y2, Z2 = z };

        [Fact] public void TwoBeamsEndToEndOnOneAxisFormOneRun()
        {
            var runs = AuditCoherenceRules.CollinearRuns(new[] { Beam(1, 1, 0, 0, 4000, 0), Beam(2, 2, 4000, 0, 11000, 0) });
            Assert.Single(runs); Assert.Equal(2, runs[0].Count);
            Assert.DoesNotContain(runs[0], b => b.OverlapsAnother);
        }
        [Fact] public void BeamsOnParallelAxesDoNotJoin() =>
            Assert.Empty(AuditCoherenceRules.CollinearRuns(new[] { Beam(1, 1, 0, 0, 4000, 0), Beam(2, 2, 4000, 6000, 8000, 6000) }));
        [Fact] public void BeamsWithAGapBeyondToleranceDoNotJoin() =>
            Assert.Empty(AuditCoherenceRules.CollinearRuns(new[] { Beam(1, 1, 0, 0, 4000, 0), Beam(2, 2, 4112, 0, 8000, 0) }));
        [Fact] public void BeamsOnDifferentLevelsDoNotJoin() =>
            Assert.Empty(AuditCoherenceRules.CollinearRuns(new[] { Beam(1, 1, 0, 0, 4000, 0), Beam(2, 2, 4000, 0, 8000, 0, 6000) }));
        [Fact] public void PerpendicularBeamsDoNotJoin() =>
            Assert.Empty(AuditCoherenceRules.CollinearRuns(new[] { Beam(1, 1, 0, 0, 4000, 0), Beam(2, 2, 4000, 0, 4000, 4000) }));
        [Fact] public void OverlappingBeamsAreMarked()
        {
            var runs = AuditCoherenceRules.CollinearRuns(new[] { Beam(1, 1, 0, 0, 4000, 0), Beam(2, 1, 3000, 0, 7000, 0) });
            Assert.Single(runs); Assert.All(runs[0], b => Assert.True(b.OverlapsAnother));
        }
        [Fact] public void ReversedBeamStillJoinsItsNeighbour() =>
            Assert.Single(AuditCoherenceRules.CollinearRuns(new[] { Beam(1, 1, 0, 0, 4000, 0), Beam(2, 2, 8000, 0, 4000, 0) }));
        [Fact] public void ChainOfThreeIsOneRun()
        {
            var runs = AuditCoherenceRules.CollinearRuns(new[] {
                Beam(1, 1, 0, 0, 0, 3000), Beam(2, 1, 0, 3000, 0, 6000), Beam(3, 2, 0, 6000, 0, 9000) });
            Assert.Single(runs); Assert.Equal(3, runs[0].Count);
        }
        [Fact] public void NullAndShortBeamsAreIgnored() =>
            Assert.Empty(AuditCoherenceRules.CollinearRuns(new[] { null, Beam(1, 1, 0, 0, 5, 0), Beam(2, 1, 5, 0, 10, 0) }));
        [Theory]
        [InlineData(4000, 8000, 0)] [InlineData(4100, 8000, 100)] [InlineData(3000, 8000, -1000)] [InlineData(-3000, -1000, 1000)]
        public void GapAlongTheLine(double bStart, double bEnd, double expected) =>
            Assert.Equal(expected, AuditCoherenceRules.GapAlongMm(Beam(1, 1, 0, 0, 4000, 0), Beam(2, 1, bStart, 0, bEnd, 0)), 6);
        [Fact] public void OffsetFromALine() =>
            Assert.Equal(5, AuditCoherenceRules.PointLineOffsetMm(2000, 5, 0, 0, 4000, 0), 6);
        [Fact] public void AngleOfOppositeDirectionsIsZero() =>
            Assert.Equal(0, AuditCoherenceRules.AngleBetweenDegrees(Beam(1, 1, 0, 0, 1, 0), Beam(2, 1, 1, 0, 0, 0)), 6);
    }
}
