// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// IS THIS JUNCTION ALREADY MADE? What counts as proof, and what is "cannot say".
//
// The first version looked for a fitting reachable from ALL of a member's connectors, up to
// three fittings deep. That answers "do these runs share a fitting somewhere" - not "is THIS
// junction made": two runs joined at their far ends, or through a fitting elsewhere in the
// network, read as already connected here. The evidence is now:
//
//   1. each member's END AT THIS JUNCTION - the end connector clearly nearer the junction's
//      point than its other end. Two ends about as near: indeterminate, not a guess;
//   2. from that end only, the chain of fittings joined end to end (a transition Revit put on
//      a leg, then the tee) - never through a run, and never longer than the evidence bound;
//      a longer chain is reported as beyond the bound, not searched further;
//   3. exactly ONE fitting every member reaches that way. Of the proposed kind: made. Of
//      another kind: a different fitting is there. Several: more than one route - cannot say.
//      Some members reach it and others do not: partially connected.
//
// Pure: the Revit side builds this graph from connectors, so every case is testable here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class JunctionGraph
    {
        public sealed class End
        {
            public int Index;
            public double X, Y;
            /// <summary>Owners joined to this connector.</summary>
            public List<long> JoinedTo = new List<long>();
        }

        public sealed class Node
        {
            public long Id;
            public bool IsFitting;
            /// <summary>For a fitting: Elbow, Tee, Transition, ... (Revit's part type).</summary>
            public string PartType;
            public List<End> Ends = new List<End>();
        }

        public Dictionary<long, Node> Nodes = new Dictionary<long, Node>();

        public Node Add(long id, bool fitting, string part = null)
        {
            var n = new Node { Id = id, IsFitting = fitting, PartType = part };
            Nodes[id] = n;
            return n;
        }
    }

    public static class CadJunctionEvidence
    {
        /// <summary>Fittings a chain may pass through before the evidence stops (a transition on each leg, then the fitting).</summary>
        public const int MaxChain = 4;

        public static JObject Decide(JunctionGraph g, IList<long> members, double atX, double atY, string kind, double tolerance)
        {
            var perMember = new JArray();
            var reach = new List<Dictionary<long, int>>();
            foreach (long m in members)
            {
                JunctionGraph.Node node;
                if (!g.Nodes.TryGetValue(m, out node) || node.Ends.Count == 0)
                    return Result("indeterminate", null, "member " + m + " has no readable connector", perMember);
                var byDist = node.Ends.Select(e => new { e, d = Math.Sqrt((e.X - atX) * (e.X - atX) + (e.Y - atY) * (e.Y - atY)) })
                                      .OrderBy(x => x.d).ToList();
                // 1. the end at this junction: clearly the nearer one
                if (byDist.Count > 1 && byDist[1].d - byDist[0].d <= tolerance)
                    return Result("indeterminate", null, "member " + m + "'s two ends are about as near the junction (" +
                                  Math.Round(byDist[0].d) + " / " + Math.Round(byDist[1].d) + " mm): which end is meant cannot be shown",
                                  perMember);
                JunctionGraph.End end = byDist[0].e;
                // 2. the fitting chain from that end only
                var seen = new Dictionary<long, int>();
                bool beyond = false;
                var frontier = end.JoinedTo.Where(id => g.Nodes.ContainsKey(id) && g.Nodes[id].IsFitting)
                                           .Select(id => Tuple.Create(id, m)).ToList();
                int depth = 0;
                while (frontier.Count > 0)
                {
                    depth++;
                    var next = new List<Tuple<long, long>>();
                    foreach (var f in frontier)
                    {
                        if (seen.ContainsKey(f.Item1)) continue;
                        if (depth > MaxChain) { beyond = true; continue; }
                        seen[f.Item1] = depth;
                        foreach (JunctionGraph.End fe in g.Nodes[f.Item1].Ends)
                            foreach (long other in fe.JoinedTo)
                                if (other != f.Item2 && g.Nodes.ContainsKey(other) && g.Nodes[other].IsFitting)
                                    next.Add(Tuple.Create(other, f.Item1));
                    }
                    frontier = next;
                }
                reach.Add(seen);
                perMember.Add(new JObject
                {
                    ["member"] = m, ["end"] = end.Index, ["end_from_junction_mm"] = Math.Round(byDist[0].d, 1),
                    ["fittings_reached"] = new JArray(seen.Keys), ["chain_beyond_bound"] = beyond
                });
                if (beyond)
                    return Result("indeterminate", null, "member " + m + "'s chain of fittings at this end is longer than " +
                                  MaxChain + " - beyond what this evidence covers; it is not searched further", perMember);
            }
            var common = reach.Skip(1).Aggregate(new HashSet<long>(reach[0].Keys), (acc, r) => { acc.IntersectWith(r.Keys); return acc; });
            if (common.Count == 0)
            {
                if (reach.Any(r => r.Count > 0) && reach.Any(r => r.Count == 0))
                    return Result("indeterminate_partial", null, "some members have a fitting at this end and others have none - a partially " +
                                  "made junction; nothing is placed over it", perMember);
                if (reach.All(r => r.Count > 0))
                    return Result("occupied_by_other", null, "every member has a fitting at this end, but not the same one", perMember);
                return Result("not_connected", null, "no member has a fitting at this end", perMember);
            }
            var ofKind = common.Where(id => string.Equals(g.Nodes[id].PartType, kind, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ofKind.Count == 1)
            {
                var others = reach.SelectMany(r => r.Keys).Distinct().Where(id => !common.Contains(id)).ToList();
                return Result("already_connected", ofKind[0], "one " + kind.ToLowerInvariant() + " joins every member at this junction's end" +
                              (common.Count > 1 ? " (with " + (common.Count - 1) + " other shared fitting(s) on the way)" : ""), perMember);
            }
            if (ofKind.Count > 1)
                return Result("indeterminate", null, ofKind.Count + " fittings of kind " + kind + " are reached by every member - more " +
                              "than one route; which one is this junction cannot be shown", perMember);
            if (common.Count == 1)
                return Result("existing_fitting_differs", common.First(), "a " + g.Nodes[common.First()].PartType + " joins every member " +
                              "at this end where the drawing proposes a " + kind, perMember);
            return Result("indeterminate", null, common.Count + " shared fittings, none a " + kind + ": cannot say which is this junction",
                          perMember);
        }

        private static JObject Result(string state, long? fitting, string says, JArray perMember) => new JObject
        {
            ["state"] = state,
            ["fitting_id"] = fitting.HasValue ? (JToken)fitting.Value : JValue.CreateNull(),
            ["says"] = says,
            ["evidence"] = perMember,
            ["evidence_means"] = "each member's end at this junction (the end clearly nearest its point), and the fittings " +
                                 "joined end to end from THAT end only, at most " + MaxChain + " deep"
        };
    }
}
