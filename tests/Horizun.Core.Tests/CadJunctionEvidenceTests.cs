// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// "ALREADY CONNECTED" NEEDS PROOF AT THIS JUNCTION, not somewhere on the runs.
// The negative cases the first version got wrong or could not tell apart.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadJunctionEvidenceTests
    {
        // Two runs meeting at (0,0): A from (-5000,0) to (-150,0), B from (0,150) to (0,5000).
        private static JunctionGraph TwoRuns(out JunctionGraph.End aNear, out JunctionGraph.End aFar,
                                             out JunctionGraph.End bNear, out JunctionGraph.End bFar)
        {
            var g = new JunctionGraph();
            var a = g.Add(1, false);
            aFar = new JunctionGraph.End { Index = 0, X = -5000, Y = 0 };
            aNear = new JunctionGraph.End { Index = 1, X = -150, Y = 0 };
            a.Ends.AddRange(new[] { aFar, aNear });
            var b = g.Add(2, false);
            bNear = new JunctionGraph.End { Index = 0, X = 0, Y = 150 };
            bFar = new JunctionGraph.End { Index = 1, X = 0, Y = 5000 };
            b.Ends.AddRange(new[] { bNear, bFar });
            return g;
        }

        private static void Join(JunctionGraph g, JunctionGraph.End e, long owner, long fitting, JunctionGraph.End fittingEnd)
        {
            e.JoinedTo.Add(fitting);
            fittingEnd.JoinedTo.Add(owner);
        }

        private static JunctionGraph.Node Fitting(JunctionGraph g, long id, string part, int ends)
        {
            var f = g.Add(id, true, part);
            for (int i = 0; i < ends; i++) f.Ends.Add(new JunctionGraph.End { Index = i });
            return f;
        }

        private static string State(JunctionGraph g, string kind = "Elbow") =>
            CadJunctionEvidence.Decide(g, new List<long> { 1, 2 }, 0, 0, kind, 25).Value<string>("state");

        [Fact]
        public void One_elbow_at_this_end_is_already_connected()
        {
            var g = TwoRuns(out var an, out _, out var bn, out _);
            var e = Fitting(g, 10, "Elbow", 2);
            Join(g, an, 1, 10, e.Ends[0]); Join(g, bn, 2, 10, e.Ends[1]);
            Assert.Equal("already_connected", State(g));
        }

        [Fact]
        public void The_same_two_runs_joined_at_their_OTHER_ends_are_not_connected_here()
        {
            var g = TwoRuns(out _, out var af, out _, out var bf);
            var e = Fitting(g, 10, "Elbow", 2);
            Join(g, af, 1, 10, e.Ends[0]); Join(g, bf, 2, 10, e.Ends[1]);
            Assert.Equal("not_connected", State(g));
        }

        [Fact]
        public void A_fitting_shared_elsewhere_through_a_third_run_is_not_this_junction()
        {
            var g = TwoRuns(out var an, out var af, out var bn, out var bf);
            var tee = Fitting(g, 10, "Tee", 3);
            Join(g, af, 1, 10, tee.Ends[0]); Join(g, bf, 2, 10, tee.Ends[1]);   // shared, but at the far ends
            Assert.Equal("not_connected", State(g));
        }

        [Fact]
        public void Revit_transitions_on_the_legs_before_the_tee_are_followed()
        {
            var g = TwoRuns(out var an, out _, out var bn, out _);
            var t1 = Fitting(g, 11, "Transition", 2);
            var tee = Fitting(g, 10, "Tee", 3);
            Join(g, an, 1, 11, t1.Ends[0]);
            t1.Ends[1].JoinedTo.Add(10); tee.Ends[0].JoinedTo.Add(11);
            Join(g, bn, 2, 10, tee.Ends[1]);
            Assert.Equal("already_connected", State(g, "Tee"));
        }

        [Fact]
        public void A_chain_longer_than_the_evidence_bound_is_indeterminate_not_searched_further()
        {
            var g = TwoRuns(out var an, out _, out var bn, out _);
            long prev = 1; JunctionGraph.End prevEnd = an;
            for (int k = 0; k < CadJunctionEvidence.MaxChain + 1; k++)
            {
                var t = Fitting(g, 20 + k, "Transition", 2);
                prevEnd.JoinedTo.Add(20 + k); t.Ends[0].JoinedTo.Add(prev);
                prev = 20 + k; prevEnd = t.Ends[1];
            }
            var e = Fitting(g, 10, "Elbow", 2);
            prevEnd.JoinedTo.Add(10); e.Ends[0].JoinedTo.Add(prev);
            Join(g, bn, 2, 10, e.Ends[1]);
            JObject r = CadJunctionEvidence.Decide(g, new List<long> { 1, 2 }, 0, 0, "Elbow", 25);
            Assert.Equal("indeterminate", r.Value<string>("state"));
            Assert.Contains("longer than", r.Value<string>("says"));
        }

        [Fact]
        public void Two_elbows_both_reached_are_more_than_one_route()
        {
            var g = TwoRuns(out var an, out _, out var bn, out _);
            var e1 = Fitting(g, 10, "Elbow", 2);
            var e2 = Fitting(g, 11, "Elbow", 2);
            // both members reach both elbows through a joined pair
            Join(g, an, 1, 10, e1.Ends[0]); e1.Ends[1].JoinedTo.Add(11); e2.Ends[0].JoinedTo.Add(10);
            Join(g, bn, 2, 11, e2.Ends[1]);
            Assert.Equal("indeterminate", State(g));
        }

        [Fact]
        public void A_shared_fitting_of_another_kind_is_reported_as_such()
        {
            var g = TwoRuns(out var an, out _, out var bn, out _);
            var t = Fitting(g, 10, "Transition", 2);
            Join(g, an, 1, 10, t.Ends[0]); Join(g, bn, 2, 10, t.Ends[1]);
            Assert.Equal("existing_fitting_differs", State(g));
        }

        [Fact]
        public void One_member_joined_and_the_other_free_is_partial_never_a_new_fitting_over_it()
        {
            var g = TwoRuns(out var an, out _, out _, out _);
            var e = Fitting(g, 10, "Elbow", 2);
            Join(g, an, 1, 10, e.Ends[0]);
            Assert.Equal("indeterminate_partial", State(g));
        }

        [Fact]
        public void A_member_whose_two_ends_are_equally_near_is_indeterminate()
        {
            var g = new JunctionGraph();
            var a = g.Add(1, false);
            a.Ends.Add(new JunctionGraph.End { Index = 0, X = -100, Y = 0 });
            a.Ends.Add(new JunctionGraph.End { Index = 1, X = 100, Y = 0 });
            var b = g.Add(2, false);
            b.Ends.Add(new JunctionGraph.End { Index = 0, X = 0, Y = 150 });
            b.Ends.Add(new JunctionGraph.End { Index = 1, X = 0, Y = 5000 });
            Assert.Equal("indeterminate", State(g));
        }
    }
}
