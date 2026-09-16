// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// WHICH WAY IS DOWNHILL.
//
// A sanitary drain has a slope, and a plan drawing shows it as a note beside the
// run: "1% FALL", "1/4 IN PER FT". The note is TEXT, which this reader cannot
// reach — but the requirement set can declare the slope per layer, and it already
// does: `slope_percent` has been a validated key of a rule since before this
// campaign.
//
// It was parsed, validated, and then dropped. It never reached the candidate and
// never reached the plan, so a rule that declared 1% produced a horizontal pipe,
// silently, in the one discipline where that is the whole design. This file is
// what makes the number mean something.
//
// THE HARD PART IS NOT THE ARITHMETIC, IT IS THE DIRECTION. A slope needs an
// upstream and a downstream end, and a drawn line has a first point and a second
// point — which is drawing order, not hydraulics. Building a fall from drawing
// order gives a network that slopes the right amount in whatever direction the
// draughtsman happened to click, and the plan looks identical either way.
//
// SO THE DIRECTION COMES FROM THE NETWORK, not from the geometry. Water runs
// downhill to ONE place: the outfall, the stack, the connection to the sewer. The
// caller names that point, and every invert follows from it —
//
//     invert(node) = invert(outfall) + path length to the outfall × slope
//
// — which is how a drainage layout is actually set out, and which needs no note,
// no arrow and no text.
//
// WHAT IT REFUSES, each for its own reason:
//
//   NO OUTFALL         without one there is no downhill. Refused, not defaulted
//                      to the first node or the lowest one.
//
//   TWO PATHS          a node reachable from the outfall two ways, with the two
//                      paths giving different inverts, has no single invert.
//                      That is a looped drain, and it is a design decision.
//
//   A RUN WITH NO      the fall through it cannot be computed, so every node
//   DECLARED SLOPE     beyond it is unknown. The run is named and the walk stops
//                      there rather than assuming level.
//
//   UNREACHABLE        a run in another component never meets this outfall. It
//                      is named, because a drainage network in two pieces is a
//                      finding rather than a smaller network.
//
// Revit-free.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>The invert this walk worked out for one node, and how it got there.</summary>
    public sealed class CadInvert
    {
        public string NodeKey;
        public CadPoint At;
        /// <summary>Height above the outfall, in millimetres. Zero at the outfall itself.</summary>
        public double RiseMm;
        /// <summary>Distance travelled along the network to reach it.</summary>
        public double PathLengthMm;
        /// <summary>How many runs were crossed. A long path through many runs accumulates their rounding.</summary>
        public int Hops;

        public JObject ToJson() => new JObject
        {
            ["node"] = NodeKey,
            ["at"] = new JArray(Math.Round(At.X, 4, MidpointRounding.AwayFromZero),
                                Math.Round(At.Y, 4, MidpointRounding.AwayFromZero)),
            ["rise_above_outfall_mm"] = Math.Round(RiseMm, 3, MidpointRounding.AwayFromZero),
            ["path_length_mm"] = Math.Round(PathLengthMm, 3, MidpointRounding.AwayFromZero),
            ["hops"] = Hops
        };
    }

    /// <summary>A run with its two ends at the heights the fall gives them.</summary>
    public sealed class CadRunFall
    {
        public string RunId;
        public double StartZMm, EndZMm;
        public double SlopePercent;
        /// <summary>Which end is higher: "start" or "end".</summary>
        public string Upstream;

        public JObject ToJson() => new JObject
        {
            ["run_id"] = RunId,
            ["start_z_mm"] = Math.Round(StartZMm, 3, MidpointRounding.AwayFromZero),
            ["end_z_mm"] = Math.Round(EndZMm, 3, MidpointRounding.AwayFromZero),
            ["slope_percent"] = Math.Round(SlopePercent, 6, MidpointRounding.AwayFromZero),
            ["upstream_end"] = Upstream,
            ["means"] = "the two ends at the heights the fall gives them, measured along the network from " +
                        "the outfall. The upstream end is decided by the NETWORK, not by which point the " +
                        "drawing happened to list first."
        };
    }

    /// <summary>What a fall walk concluded, and everything it would not conclude.</summary>
    public sealed class CadFall
    {
        public string OutfallNode;
        public double OutfallInvertMm;
        public List<CadInvert> Inverts = new List<CadInvert>();
        public List<CadRunFall> Runs = new List<CadRunFall>();

        /// <summary>Runs the walk could not pass, each with its reason. The walk stops at one.</summary>
        public List<JObject> Blocked = new List<JObject>();

        /// <summary>Nodes reachable two ways with inconsistent inverts. A looped drain.</summary>
        public List<JObject> Conflicts = new List<JObject>();

        /// <summary>Runs this outfall never reaches.</summary>
        public List<string> Unreachable = new List<string>();

        public string Refusal;

        public bool Ok => Refusal == null;

        public JObject ToJson() => new JObject
        {
            ["outfall_node"] = OutfallNode,
            ["outfall_invert_mm"] = Math.Round(OutfallInvertMm, 3, MidpointRounding.AwayFromZero),
            ["refused"] = Refusal,
            ["runs"] = new JArray(Runs.Select(r => (JToken)r.ToJson())),
            ["inverts"] = new JArray(Inverts.Select(i => (JToken)i.ToJson())),
            ["blocked"] = new JArray(Blocked),
            ["conflicts"] = new JArray(Conflicts),
            ["unreachable_runs"] = new JArray(Unreachable.Select(x => (JToken)x)),
            ["summary"] = new JObject
            {
                ["runs_with_a_fall"] = Runs.Count,
                ["runs_blocked"] = Blocked.Count,
                ["runs_unreachable"] = Unreachable.Count,
                ["nodes_in_conflict"] = Conflicts.Count,
                ["highest_rise_mm"] = Inverts.Count == 0
                    ? 0 : Math.Round(Inverts.Max(i => i.RiseMm), 3, MidpointRounding.AwayFromZero),
                ["means"] =
                    "every run with a fall has two ends at different heights, measured along the network " +
                    "from the outfall. A BLOCKED run has no declared slope, so nothing beyond it is known " +
                    "either - the walk stops rather than assuming level. A CONFLICT is a node the outfall " +
                    "reaches two ways with different inverts, which is a looped drain and a design decision. " +
                    "UNREACHABLE runs never meet this outfall at all, which for a drainage network is a " +
                    "finding rather than a smaller network."
            }
        };
    }

    public static class CadFallRules
    {
        /// <summary>
        /// How far two paths to one node may disagree before it is a conflict.
        ///
        /// Not zero: a path through forty runs accumulates their rounding, and a
        /// tenth of a millimetre over a fifty-metre drain is arithmetic, not a loop.
        /// </summary>
        public const double InvertAgreementMm = 1.0;

        /// <summary>
        /// Work out every invert from the outfall outwards.
        ///
        /// <paramref name="slopeOf"/> is asked, per run, for the slope the
        /// requirement set declares for its layer. Returning null means the rule
        /// declared none, and the walk STOPS at that run - a slope this file chose
        /// would be a fall nobody designed.
        /// </summary>
        public static CadFall Compute(CadNetwork network, string outfallNode, double outfallInvertMm,
                                      Func<CadRun, double?> slopeOf)
        {
            var fall = new CadFall { OutfallNode = outfallNode, OutfallInvertMm = outfallInvertMm };
            if (network == null || network.Runs.Count == 0)
            {
                fall.Refusal = "no_network";
                return fall;
            }
            if (string.IsNullOrWhiteSpace(outfallNode))
            {
                fall.Refusal = "no_outfall_named";
                return fall;
            }

            var runsAt = new Dictionary<string, List<CadRun>>(StringComparer.Ordinal);
            foreach (CadRun r in network.Runs)
            {
                Add(runsAt, r.StartNode, r);
                Add(runsAt, r.EndNode, r);
            }
            if (!runsAt.ContainsKey(outfallNode))
            {
                fall.Refusal = "outfall_is_not_a_node_of_this_network";
                return fall;
            }

            var pointOf = new Dictionary<string, CadPoint>(StringComparer.Ordinal);
            foreach (CadJunction j in network.Junctions) pointOf[j.NodeKey] = j.Point;

            var rise = new Dictionary<string, double>(StringComparer.Ordinal) { { outfallNode, 0 } };
            var length = new Dictionary<string, double>(StringComparer.Ordinal) { { outfallNode, 0 } };
            var hops = new Dictionary<string, int>(StringComparer.Ordinal) { { outfallNode, 0 } };
            var doneRuns = new HashSet<string>(StringComparer.Ordinal);

            // A BREADTH-FIRST WALK OUTWARDS. Breadth-first rather than
            // depth-first so that the shortest path to a node is the one that sets
            // its invert, and a second, longer path that disagrees is reported as a
            // conflict against the sensible value rather than against whichever
            // branch recursion happened to enter first.
            var queue = new Queue<string>();
            queue.Enqueue(outfallNode);

            while (queue.Count > 0)
            {
                string node = queue.Dequeue();
                List<CadRun> here;
                if (!runsAt.TryGetValue(node, out here)) continue;

                foreach (CadRun run in here)
                {
                    if (doneRuns.Contains(run.Id)) continue;

                    double? slope = slopeOf == null ? null : slopeOf(run);
                    if (!slope.HasValue)
                    {
                        doneRuns.Add(run.Id);
                        fall.Blocked.Add(new JObject
                        {
                            ["run_id"] = run.Id,
                            ["layer"] = run.Layer,
                            ["reason"] = "no_declared_slope",
                            ["means"] = "the requirement set declares no slope for this layer, so the fall " +
                                        "through this run is unknown and so is every invert beyond it. The " +
                                        "walk stops here rather than assuming the run is level - a drain " +
                                        "laid level is a drain that does not drain."
                        });
                        continue;
                    }

                    string other = run.StartNode == node ? run.EndNode : run.StartNode;
                    double runLength = run.LengthMm;
                    double climb = runLength * slope.Value / 100.0;
                    double newRise = rise[node] + climb;
                    double newLength = length[node] + runLength;

                    double existing;
                    if (rise.TryGetValue(other, out existing))
                    {
                        if (Math.Abs(existing - newRise) > InvertAgreementMm)
                            fall.Conflicts.Add(new JObject
                            {
                                ["node"] = other,
                                ["already_mm"] = Math.Round(existing, 3, MidpointRounding.AwayFromZero),
                                ["this_path_mm"] = Math.Round(newRise, 3, MidpointRounding.AwayFromZero),
                                ["via_run"] = run.Id,
                                ["means"] = "the outfall reaches this node two ways and the two paths give " +
                                            "different inverts. A drain cannot be at two heights: either " +
                                            "the network loops, or one of the slopes is wrong. NOT resolved " +
                                            "here - the first invert stands and the disagreement is reported."
                            });
                    }
                    else
                    {
                        rise[other] = newRise;
                        length[other] = newLength;
                        hops[other] = hops[node] + 1;
                        queue.Enqueue(other);
                    }

                    // THE UPSTREAM END IS THE ONE FURTHER FROM THE OUTFALL, which is
                    // the whole point: it is decided by the network and not by which
                    // point the drawing listed first.
                    doneRuns.Add(run.Id);

                    // THE NODE'S ESTABLISHED INVERT, not this path's arithmetic.
                    //
                    // On a looped network the far node already has an invert from
                    // the shorter path, and the conflict above says so. Using
                    // newRise here would give this run an end height that disagrees
                    // with the invert reported for the node it ends at - two numbers
                    // for one place, in the reply that exists to say what the
                    // heights are.
                    double zHere = outfallInvertMm + rise[node];
                    double zThere = outfallInvertMm + rise[other];
                    fall.Runs.Add(new CadRunFall
                    {
                        RunId = run.Id,
                        SlopePercent = slope.Value,
                        StartZMm = run.StartNode == node ? zHere : zThere,
                        EndZMm = run.StartNode == node ? zThere : zHere,
                        Upstream = run.StartNode == node ? "end" : "start"
                    });
                }
            }

            foreach (var kv in rise.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                CadPoint at;
                pointOf.TryGetValue(kv.Key, out at);
                fall.Inverts.Add(new CadInvert
                {
                    NodeKey = kv.Key,
                    At = at,
                    RiseMm = kv.Value,
                    PathLengthMm = length[kv.Key],
                    Hops = hops[kv.Key]
                });
            }

            foreach (CadRun r in network.Runs)
                if (!doneRuns.Contains(r.Id)) fall.Unreachable.Add(r.Id);

            return fall;
        }

        private static void Add(Dictionary<string, List<CadRun>> map, string key, CadRun run)
        {
            if (key == null) return;
            List<CadRun> list;
            if (!map.TryGetValue(key, out list)) map[key] = list = new List<CadRun>();
            list.Add(run);
        }

        /// <summary>
        /// The node nearest a declared point, so a caller can name the outfall by
        /// where it is on the drawing rather than by a key they would have to read
        /// out of a previous reply.
        ///
        /// Returns null when nothing is within tolerance - and that is a refusal
        /// rather than "the nearest node anyway", because an outfall placed on the
        /// wrong node inverts an entire drainage layout while looking plausible.
        /// </summary>
        public static string NodeNear(CadNetwork network, CadPoint at, double toleranceMm)
        {
            if (network == null) return null;
            string best = null;
            double bestDistance = double.MaxValue;
            foreach (CadJunction j in network.Junctions)
            {
                double d = j.Point.PlanDistanceTo(at);
                if (d > toleranceMm) continue;
                if (d < bestDistance ||
                    (d == bestDistance && best != null && string.CompareOrdinal(j.NodeKey, best) < 0))
                { best = j.NodeKey; bestDistance = d; }
            }
            return best;
        }
    }
}
