// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// THREE INCIDENCES, TWO RUNS, AND THE WHOLE READING DIED.
//
// Measured live in campaign 6 on a real corridor supply plan read through Revit:
// horizun_cad_networks threw "Sequence contains no matching element" from the
// tee arm of the junction classifier (CadNetworkRules.cs, the BranchRunId pick).
// A junction had three incidences of which two carried the same run id - one run
// meeting the node with both of its ends - and the branch was chosen as "the run
// whose id is neither of the through pair", which is nobody.
//
// The live reading's segmentation is Revit's, not reproducible offline from the
// drawing, so the classifier is exercised directly with that incidence list.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadNetworkDoubledBackTests
    {
        private static CadNetworkOptions Options() => new CadNetworkOptions
        {
            ConnectToleranceMm = 25.4,
            IdentityToleranceMm = 1.0,
            CollinearToleranceDegrees = 2.0,
            GapReviewDistanceMm = 250.0,
            ThroughToleranceDegrees = 15.0
        };

        private static CadJunction Classify(List<CadIncidence> incident, params string[] runIds)
        {
            var runs = runIds.Distinct().ToDictionary(id => id, id => new CadRun
            {
                Id = id, Layer = "M-DUCT", Start = new CadPoint(0, 0), End = new CadPoint(1000, 0)
            });
            MethodInfo core = typeof(CadNetworkRules).GetMethod("ClassifyCore", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(core);
            return (CadJunction)core.Invoke(null, new object[] { "n1", new CadPoint(0, 0), incident, runs, Options() });
        }

        private static CadIncidence At(string run, string end, double bearing) =>
            new CadIncidence { RunId = run, AtEnd = end, BearingDegrees = bearing };

        [Fact]
        public void One_run_meeting_the_node_with_both_ends_is_left_for_a_person_not_thrown()
        {
            // A and B are the most opposed pair; the third incidence is A again.
            var incident = new List<CadIncidence> { At("A", "start", 0), At("B", "end", 180), At("A", "end", 90) };
            CadJunction j = Classify(incident, "A", "B");
            Assert.Equal(CadJunctionKind.Irregular, j.Kind);
            Assert.False(j.Automatic);
            Assert.Contains("both of its ends", j.Says);
        }

        [Fact]
        public void Three_distinct_runs_are_still_a_tee_with_the_branch_by_position()
        {
            var incident = new List<CadIncidence> { At("A", "start", 0), At("B", "end", 180), At("C", "start", 90) };
            CadJunction j = Classify(incident, "A", "B", "C");
            Assert.Equal(CadJunctionKind.Tee, j.Kind);
            Assert.True(j.Automatic);
            Assert.Equal("C", j.BranchRunId);
            Assert.Equal(new[] { "A", "B" }, j.ThroughRunIds.OrderBy(x => x));
        }
    }
}
