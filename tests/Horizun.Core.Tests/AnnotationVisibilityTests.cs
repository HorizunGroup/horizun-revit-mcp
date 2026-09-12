// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// The verdict arithmetic behind annotation layout. Every property here exists
// because getting it wrong produces a specific, expensive lie:
//
//   * a blocking verdict that stops blocking  -> "collision-free" over an
//     annotation nobody measured;
//   * an accepted id that still reports complete clearance -> the same lie with
//     the caller's fingerprints on it;
//   * a refusal without ids -> a client that cannot act and retries blind.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AnnotationVisibilityTests
    {
        private static JObject Row(long id, string verdict, string category = "Space Tags",
                                   string cls = "SpatialElementTag", long owner = 9948)
            => AnnotationVisibility.Entry(id, category, cls, owner, verdict, "evidence for " + id);

        [Fact]
        public void OnlyUnknownVerdictsBlock()
        {
            Assert.True(AnnotationVisibility.IsBlocking(AnnotationVisibility.UnknownUnreadableExtent));
            Assert.True(AnnotationVisibility.IsBlocking(AnnotationVisibility.UnknownVisibilityUndecidable));
            foreach (string verdict in AnnotationVisibility.Verdicts.Where(v => !v.StartsWith("unknown_")))
                Assert.False(AnnotationVisibility.IsBlocking(verdict));
        }

        [Fact]
        public void AcceptedUnmeasurableIsNeitherAnExclusionNorBlocking()
        {
            // It is its own state on purpose: the annotation is still there, the
            // caller merely agreed to proceed without measuring it.
            Assert.False(AnnotationVisibility.IsBlocking(AnnotationVisibility.AcceptedUnmeasurable));
            Assert.False(AnnotationVisibility.IsExclusion(AnnotationVisibility.AcceptedUnmeasurable));
        }

        [Fact]
        public void EveryExclusionNamesTheProbeThatDecidedIt()
        {
            foreach (string verdict in AnnotationVisibility.Verdicts.Where(AnnotationVisibility.IsExclusion))
            {
                JObject entry = AnnotationVisibility.Entry(7, "Dimensions", "Dimension", 3, verdict, "because " + verdict);
                Assert.Equal(verdict, entry.Value<string>("verdict"));
                Assert.False(string.IsNullOrWhiteSpace(entry.Value<string>("evidence")));
            }
        }

        [Fact]
        public void UnknownVerdictIsRefusedRatherThanRecorded()
        {
            Assert.Throws<ArgumentException>(() =>
                AnnotationVisibility.Entry(1, "Tags", "IndependentTag", 2, "probably_fine", "no"));
        }

        [Fact]
        public void CoverageIsCompleteWhenNothingBlocks()
        {
            JObject coverage = AnnotationVisibility.Coverage(9948, 100, 0, null, "probe", new[]
            {
                Row(1, AnnotationVisibility.Measured),
                Row(2, AnnotationVisibility.ExcludedElementHidden),
                Row(3, AnnotationVisibility.ExcludedNotVisibleInView)
            });
            Assert.True(coverage.Value<bool>("coverage_complete"));
            Assert.Equal("complete", coverage.Value<string>("clearance_scope"));
            Assert.Equal(3, coverage.Value<int>("considered"));
            Assert.Equal(1, coverage["counts"].Value<int>(AnnotationVisibility.Measured));
            Assert.Empty((JArray)coverage["blocking"]);
        }

        [Fact]
        public void OneUnknownEntryBlocksTheWholeCoverage()
        {
            JObject coverage = AnnotationVisibility.Coverage(9948, 100, 0, null, "probe", new[]
            {
                Row(1, AnnotationVisibility.Measured),
                Row(1455410, AnnotationVisibility.UnknownUnreadableExtent)
            });
            Assert.False(coverage.Value<bool>("coverage_complete"));
            Assert.Equal("undecided", coverage.Value<string>("clearance_scope"));
            Assert.Single((JArray)coverage["blocking"]);
            Assert.Equal(1455410, ((JArray)coverage["blocking"])[0].Value<long>("element_id"));
        }

        [Fact]
        public void AcceptingAnUnmeasurableAnnotationDowngradesClearanceToPartial()
        {
            JObject coverage = AnnotationVisibility.Coverage(9948, 100, 0, null, "probe", new[]
            {
                Row(1, AnnotationVisibility.Measured),
                Row(1455410, AnnotationVisibility.AcceptedUnmeasurable)
            });
            Assert.True(coverage.Value<bool>("coverage_complete"));
            // Complete coverage is NOT complete clearance. This is the distinction the
            // whole acceptance mechanism exists to keep visible.
            Assert.Equal("partial", coverage.Value<string>("clearance_scope"));
            Assert.Equal(new long[] { 1455410 }, ((JArray)coverage["accepted_unmeasurable"]).Values<long>().ToArray());
        }

        [Fact]
        public void DependentViewIsReportedWithItsPrimary()
        {
            JObject coverage = AnnotationVisibility.Coverage(9948, 50, 4242, null, "probe", new JObject[0]);
            Assert.True(coverage.Value<bool>("is_dependent_view"));
            Assert.Equal(4242, coverage.Value<long>("primary_view_id"));

            JObject independent = AnnotationVisibility.Coverage(9948, 50, 0, null, "probe", new JObject[0]);
            Assert.False(independent.Value<bool>("is_dependent_view"));
            Assert.Equal(JTokenType.Null, independent["primary_view_id"].Type);
        }

        [Fact]
        public void EmptyViewIsCompleteAndCountsEveryVerdictAtZero()
        {
            JObject coverage = AnnotationVisibility.Coverage(1, 100, 0, null, null, null);
            Assert.True(coverage.Value<bool>("coverage_complete"));
            Assert.Equal(0, coverage.Value<int>("considered"));
            foreach (string verdict in AnnotationVisibility.Verdicts)
                Assert.Equal(0, coverage["counts"].Value<int>(verdict));
            Assert.Equal("not_required", coverage.Value<string>("visibility_probe"));
        }

        [Fact]
        public void RefusalNamesTheBlockingIdsAndTheWayOut()
        {
            JObject coverage = AnnotationVisibility.Coverage(9948, 100, 0, null, "collector listed it", new[]
            {
                Row(1455410, AnnotationVisibility.UnknownUnreadableExtent)
            });
            string message = AnnotationVisibility.RefusalMessage(coverage);
            Assert.Contains("1455410", message);
            Assert.Contains("Space Tags", message);
            Assert.Contains("Nothing was written", message);
            Assert.Contains("layout_accept_unmeasurable", message);
            // And it never claims the annotation is absent.
            Assert.DoesNotContain("does not exist", message);
        }

        [Fact]
        public void RefusalBoundsHowManyIdsItPrintsButKeepsTheCount()
        {
            var rows = new List<JObject>();
            for (long i = 1; i <= 25; i++) rows.Add(Row(i, AnnotationVisibility.UnknownUnreadableExtent));
            string message = AnnotationVisibility.RefusalMessage(
                AnnotationVisibility.Coverage(9948, 100, 0, null, "probe", rows));
            Assert.Contains("25 potentially visible annotation(s)", message);
            Assert.Contains("and 15 more", message);
        }

        [Fact]
        public void RefusalMessageSurvivesAMissingCoverage()
        {
            Assert.Contains("no coverage report", AnnotationVisibility.RefusalMessage(null));
        }

        [Fact]
        public void CoverageExceptionCarriesTheCoverageItRefusedOn()
        {
            JObject coverage = AnnotationVisibility.Coverage(9948, 100, 0, null, "probe", new[]
            {
                Row(1455410, AnnotationVisibility.UnknownUnreadableExtent)
            });
            var ex = new AnnotationCoverageException(coverage);
            Assert.Same(coverage, ex.Coverage);
            Assert.Contains("1455410", ex.Message);
        }

        [Fact]
        public void AcceptanceListTakesOnlyPositiveIdsAndNeverAWildcard()
        {
            Assert.Empty(AnnotationVisibility.ReadAccepted(null));
            Assert.Empty(AnnotationVisibility.ReadAccepted(JValue.CreateNull()));
            Assert.Equal(new HashSet<long> { 5, 9 }, AnnotationVisibility.ReadAccepted(new JArray(5, 9)));

            Assert.Throws<ArgumentException>(() => AnnotationVisibility.ReadAccepted(new JValue("all")));
            Assert.Throws<ArgumentException>(() => AnnotationVisibility.ReadAccepted(new JArray("*")));
            Assert.Throws<ArgumentException>(() => AnnotationVisibility.ReadAccepted(new JArray(0)));
            Assert.Throws<ArgumentException>(() => AnnotationVisibility.ReadAccepted(new JArray(-3)));
            Assert.Throws<ArgumentException>(() =>
                AnnotationVisibility.ReadAccepted(new JArray(Enumerable.Range(1, 201).Select(i => (object)i).ToArray())));
        }

        [Fact]
        public void BoundsSourceTravelsWithTheCoverage()
        {
            var bounds = new JObject { ["source"] = "annotation_crop" };
            JObject coverage = AnnotationVisibility.Coverage(9948, 100, 0, bounds, "probe", new JObject[0]);
            Assert.Equal("annotation_crop", coverage["bounds"].Value<string>("source"));

            JObject none = AnnotationVisibility.Coverage(9948, 100, 0, null, "probe", new JObject[0]);
            Assert.Equal("none", none["bounds"].Value<string>("source"));
        }
    }
}
