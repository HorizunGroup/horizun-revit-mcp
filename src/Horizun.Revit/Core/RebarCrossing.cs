// -----------------------------------------------------------------------------
// Horizun Revit MCP - crossing_order: which of two bars of different rules passes
// over the other, DECLARED. Original Horizun code. No Revit types: it reads the
// overlap check's own pairs and the plan's centrelines, so every decision here is
// provable at a desk.
//
// WHY THIS EXISTS. overlapping_bar refuses both sets of any two whose bars pass
// through each other (MEASURED, Revit 2024 and 2026: Revit pushes a crossing bar
// until it touches the other, and says nothing). That was right, and it left real
// buildings unbuildable: at a beam-beam joint the two beams' bottom bars cross at
// one level, a slab mat runs through the beam bars, the top and bottom U bars of a
// beam put their legs in one plane at one station (an anonymized regression case).
// Which bar passes over is a detailing decision, so it is DECLARED here - never
// compiled in: the bridge has no order of its own.
//
// WHAT IT DOES. For every clashing pair the overlap check found on the declared
// centrelines, the orders are read: a pair one order covers (one set in `stays`,
// the other in `moves`) displaces the MOVING set, once, by the declared offset:
//   - `translate`: the whole set, along a declared direction, or `toward_host_core`
//     - the common perpendicular of the two crossing bars, to the side of the
//     moving set's host centre. Derived only where it reads one way: bars running
//     alongside each other have no crossing axis, and a host centre on the plane
//     the bars cross in is either way. Both are refused by name.
//   - `legs`: the end legs of an open bar move inward along its run (the bottom U
//     nests inside the top U); the run is shorter by what the legs moved.
// The plan then measures EVERYTHING again: containment and cover on the displaced
// bar, and the overlap check on the result. A displacement that breaks cover is
// refused (crossing_displacement_breaks_cover), never squeezed; one that still
// clashes is refused as before (bars_intersect).
//
// NEVER GUESSED: two orders that disagree on which bar moves, one set sent two
// ways, a toward_host_core that reads both ways - each is a refusal by name.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>How far a moving set goes: `diameter` (its own model diameter), `contact` (the two radii: the bars end touching), or `{ mm }`.</summary>
    public sealed class CrossingOffset
    {
        public const string KindDiameter = "diameter";
        public const string KindContact = "contact";
        public const string KindMm = "mm";

        public string Kind;
        public double Mm;

        public double Resolve(double moverRadiusMm, double otherRadiusMm) =>
            Kind == KindMm ? Mm : Kind == KindContact ? moverRadiusMm + otherRadiusMm : 2 * moverRadiusMm;

        public JToken ToJson() => Kind == KindMm ? (JToken)new JObject { ["mm"] = Mm } : Kind;
    }

    /// <summary>One declared order: the sets that stay, the sets that move, how they move.</summary>
    public sealed class CrossingOrderEntry
    {
        public string Id;
        public List<string> Stays = new List<string>();
        public List<string> Moves = new List<string>();
        /// <summary>RebarCrossing.ModeTranslate or ModeLegs.</summary>
        public string Move;
        /// <summary>The declared direction (translate), as declared; null with toward_host_core.</summary>
        public double[] Direction;
        public bool TowardHostCore;
        /// <summary>RebarCrossing.LegsBoth, LegsStart or LegsEnd (legs).</summary>
        public string Legs;
        /// <summary>This order's own offset, or null for the set's.</summary>
        public CrossingOffset Offset;
    }

    /// <summary>crossing_order (etapa 1c), as the requirement set declares it.</summary>
    public sealed class StructuralCrossingOrder
    {
        public CrossingOffset Offset;
        public List<CrossingOrderEntry> Orders = new List<CrossingOrderEntry>();

        /// <summary>Read a crossing_order object on its own (no rule ids to resolve refs against). Throws on a refusal.</summary>
        public static StructuralCrossingOrder Parse(JObject o) => StructuralRequirementSet.ParseCrossingOrder(o);

        public JObject ToJson() => new JObject
        {
            ["offset"] = Offset?.ToJson(),
            ["orders"] = new JArray(Orders.Select(e => (object)new JObject
            {
                ["id"] = e.Id,
                ["stays"] = new JArray(e.Stays),
                ["moves"] = new JArray(e.Moves),
                ["move"] = e.Move,
                ["direction"] = e.Move != RebarCrossing.ModeTranslate ? null
                                : e.TowardHostCore ? (JToken)RebarCrossing.TowardHostCore : new JArray(e.Direction[0], e.Direction[1], e.Direction[2]),
                ["legs"] = e.Legs,
                ["offset"] = e.Offset?.ToJson()
            }).ToArray())
        };
    }

    /// <summary>What happens to one set (one rule in one host): the displacement the plan builds.</summary>
    public sealed class CrossingMove
    {
        public string RuleId;
        public long HostId;
        public string Mode;
        /// <summary>The translation in model millimetres (translate). Null for legs.</summary>
        public double[] VectorMm;
        public string Legs;
        public double OffsetMm;
        public List<string> OrderIds = new List<string>();
        /// <summary>The sets it moved away from: rule, host, order, the clash found before.</summary>
        public JArray Against = new JArray();
        public string Reason;

        public string Describe()
        {
            string by = RebarCrossing.Mm(OffsetMm) + " mm";
            return Mode == RebarCrossing.ModeLegs
                ? (Legs == RebarCrossing.LegsBoth ? "both end legs" : "the " + Legs + " leg") + " moved " + by + " inward along the run"
                : "moved " + by + " along (" + string.Join(", ", RebarCrossing.Unit(VectorMm).Select(v => RebarCrossing.Mm3(v))) + ")";
        }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["rule_id"] = RuleId,
                ["host_id"] = HostId,
                ["mode"] = Mode,
                ["moved_by_mm"] = Math.Round(OffsetMm, 3)
            };
            if (Mode == RebarCrossing.ModeTranslate)
            {
                double[] u = RebarCrossing.Unit(VectorMm);
                o["direction"] = new JArray(Clean(u[0]), Clean(u[1]), Clean(u[2]));
                o["vector_mm"] = new JArray(Clean(VectorMm[0]), Clean(VectorMm[1]), Clean(VectorMm[2]));
            }
            else
            {
                o["legs"] = Legs;
                o["direction"] = "inward along the run";
            }
            o["order_ids"] = new JArray(OrderIds);
            o["against"] = Against;
            o["reason"] = Reason;
            return o;
        }

        private static double Clean(double v) => Math.Round(v, 9) + 0.0;
    }

    public sealed class CrossingRefusal
    {
        public string RuleId;
        public long HostId;
        public string Code;
        public string Why;

        public JObject ToJson() => new JObject { ["rule_id"] = RuleId, ["host_id"] = HostId, ["code"] = Code, ["why"] = Why };
    }

    /// <summary>What a crossing_order decided from one overlap check: the moves, and the sets refused for an order that cannot be read one way.</summary>
    public sealed class CrossingDecision
    {
        public StructuralCrossingOrder Order;
        public Dictionary<string, CrossingMove> Moves = new Dictionary<string, CrossingMove>(StringComparer.Ordinal);
        public Dictionary<string, CrossingRefusal> Refused = new Dictionary<string, CrossingRefusal>(StringComparer.Ordinal);
        /// <summary>Clashing pairs an order covered, and pairs none did (those are left to the overlap check).</summary>
        public int PairsCovered, PairsNotCovered;

        public static string Key(string ruleId, long hostId) => (ruleId ?? "") + "\u0001" + hostId.ToString(CultureInfo.InvariantCulture);

        public JObject ToJson()
        {
            return new JObject
            {
                ["declared"] = Order != null,
                ["offset"] = Order?.Offset?.ToJson(),
                ["orders"] = Order == null ? 0 : Order.Orders.Count,
                ["pairs_covered"] = PairsCovered,
                ["pairs_not_covered"] = PairsNotCovered,
                ["displacements"] = new JArray(Moves.Values.OrderBy(m => m.RuleId, StringComparer.Ordinal).ThenBy(m => m.HostId)
                                                           .Select(m => (object)m.ToJson()).ToArray()),
                ["refused"] = new JArray(Refused.Values.OrderBy(r => r.RuleId, StringComparer.Ordinal).ThenBy(r => r.HostId)
                                                        .Select(r => (object)r.ToJson()).ToArray()),
                ["means"] =
                    "each displacement is a set the declared crossing_order moved, once, from its declared position: the whole set " +
                    "(translate) or its end legs inward along the run (legs), by the declared offset. The plan then measured the " +
                    "displaced set again - containment and cover, and the overlap check on the result - so a displacement listed here " +
                    "whose row is refused did not fit. A pair no order covers is refused by the overlap check as before."
            };
        }
    }

    public static class RebarCrossing
    {
        /// <summary>
        /// The hooks'-corner seat a row keeps once a crossing_order has moved it (second steel duel,
        /// 2026-10-12). A row moved further than its seat has left the stirrup's corner, so Revit has no
        /// bend there to seat it in, and a difference past the tolerance is not that seat: it stays the
        /// geometry_differs it is rather than being accepted under a name that does not describe it.
        /// </summary>
        public static double? SeatAfterMove(double? seatMm, double movedByMm)
        {
            if (!seatMm.HasValue) return null;
            return movedByMm > seatMm.Value + 1e-9 ? (double?)null : seatMm;
        }

        public const string ModeTranslate = "translate";
        public const string ModeLegs = "legs";
        public const string TowardHostCore = "toward_host_core";
        public const string LegsBoth = "both";
        public const string LegsStart = "start";
        public const string LegsEnd = "end";

        public static readonly string[] Modes = { ModeTranslate, ModeLegs };
        public static readonly string[] LegChoices = { LegsBoth, LegsStart, LegsEnd };

        // Refusals: in ReinforcementResolver.AllCodes.
        public const string CodeAmbiguous = "crossing_order_ambiguous";
        public const string CodeConflict = "crossing_order_conflict";
        public const string CodeNotApplicable = "crossing_displacement_not_applicable";
        public const string CodeBreaksCover = "crossing_displacement_breaks_cover";
        public static readonly string[] AllCodes = { CodeAmbiguous, CodeConflict, CodeNotApplicable, CodeBreaksCover };

        /// <summary>Two directions within this angle are one direction (a set moved the same way by two pairs).</summary>
        public const double SameDirectionDegrees = 1.0;

        // ------------------------------------------------------------- refs

        /// <summary>
        /// A ref matches a set by its declared rule id (every row a section, mat or zone rule expands to) or by
        /// the row's own rule id (one layer: `V1#h*#bottom`, `M1#bottom_*`). `*` is any run of characters,
        /// `?` one; everything else is literal and case-sensitive.
        /// </summary>
        public static bool Matches(string pattern, OverlapMember m)
        {
            if (pattern == null || m == null) return false;
            return Glob(pattern, m.RuleId) || Glob(pattern, m.DeclaredRuleId ?? m.RuleId);
        }

        public static bool MatchesAny(IEnumerable<string> patterns, OverlapMember m) => patterns != null && patterns.Any(p => Matches(p, m));

        /// <summary>Whether a ref can name a rule the set declares: the part before its first '#' matches a declared id.</summary>
        public static bool RefDeclared(string pattern, IEnumerable<string> declaredIds)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            int hash = pattern.IndexOf('#');
            string head = hash < 0 ? pattern : pattern.Substring(0, hash);
            if (hash >= 0 && head.Length == 0) return false;
            return declaredIds.Any(id => Glob(head, id));
        }

        public static bool Glob(string pattern, string text)
        {
            if (pattern == null || text == null) return false;
            int p = 0, t = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) { p++; t++; }
                else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
                else if (star >= 0) { p = star + 1; t = ++mark; }
                else return false;
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }

        // ------------------------------------------------------------ decide

        private sealed class Proposal
        {
            public CrossingOrderEntry Entry;
            public OverlapMember Mover, Stayer;
            public OverlapPair Pair;
            public double Magnitude;
            public double[] Unit;
        }

        /// <summary>
        /// The moves a crossing_order makes of one overlap check's pairs (measured on the DECLARED centrelines).
        /// Each moving set moves once; two pairs that move it the same way move it by the larger offset.
        /// </summary>
        public static CrossingDecision Decide(OverlapReport report, StructuralCrossingOrder order, double tolMm)
        {
            var d = new CrossingDecision { Order = order };
            if (order == null || report == null) return d;
            double tol = tolMm > 0 && !double.IsNaN(tolMm) ? tolMm : 0;
            var proposals = new Dictionary<string, List<Proposal>>(StringComparer.Ordinal);
            var movers = new Dictionary<string, OverlapMember>(StringComparer.Ordinal);

            foreach (OverlapPair p in report.Pairs)
            {
                // ONE BAR BUILT TWICE is not a crossing (lot 4): no order moves one copy off the other, and it is not
                // refused as an ambiguous crossing either - it stays duplicate_bar, by name, in the re-check.
                if (p.IsDuplicate) continue;
                var cands = new List<KeyValuePair<CrossingOrderEntry, bool>>();   // value: A moves
                foreach (CrossingOrderEntry e in order.Orders)
                {
                    if (MatchesAny(e.Stays, p.A) && MatchesAny(e.Moves, p.B)) cands.Add(new KeyValuePair<CrossingOrderEntry, bool>(e, false));
                    if (MatchesAny(e.Stays, p.B) && MatchesAny(e.Moves, p.A)) cands.Add(new KeyValuePair<CrossingOrderEntry, bool>(e, true));
                }
                if (cands.Count == 0) { d.PairsNotCovered++; continue; }
                d.PairsCovered++;

                if (cands.Any(c => c.Value) && cands.Any(c => !c.Value))
                {
                    string why = "the declared orders disagree on which of '" + p.A.RuleId + "' (element " + p.A.HostId + ") and '" +
                                 p.B.RuleId + "' (element " + p.B.HostId + ") moves: " +
                                 string.Join("; ", cands.Select(c => "order '" + c.Key.Id + "' moves '" + (c.Value ? p.A.RuleId : p.B.RuleId) + "'")) +
                                 ". Which bar passes over is a detailing decision, and this one reads both ways - both sets are refused " +
                                 "rather than one chosen. Name each set in only one side of the orders that cover the pair.";
                    Refuse(d, p.A, CodeAmbiguous, why);
                    Refuse(d, p.B, CodeAmbiguous, why);
                    continue;
                }

                foreach (KeyValuePair<CrossingOrderEntry, bool> c in cands)
                {
                    CrossingOrderEntry e = c.Key;
                    OverlapMember mover = c.Value ? p.A : p.B, stayer = c.Value ? p.B : p.A;
                    CrossingOffset off = e.Offset ?? order.Offset;
                    var pr = new Proposal
                    {
                        Entry = e, Mover = mover, Stayer = stayer, Pair = p,
                        Magnitude = off.Resolve(mover.RadiusMm, stayer.RadiusMm)
                    };
                    if (e.Move == ModeTranslate)
                    {
                        if (e.TowardHostCore)
                        {
                            string why;
                            pr.Unit = TowardCore(p, mover, tol, out why);
                            if (pr.Unit == null)
                            {
                                Refuse(d, mover, CodeAmbiguous, "order '" + e.Id + "' moves '" + mover.RuleId + "' (element " + mover.HostId +
                                       ") toward its host's core away from '" + stayer.RuleId + "' (element " + stayer.HostId + "), and " + why +
                                       " Declare the direction as a vector, or move the legs.");
                                continue;
                            }
                        }
                        else pr.Unit = Unit(e.Direction);
                    }
                    string key = CrossingDecision.Key(mover.RuleId, mover.HostId);
                    List<Proposal> list;
                    if (!proposals.TryGetValue(key, out list)) { list = new List<Proposal>(); proposals[key] = list; }
                    list.Add(pr);
                    movers[key] = mover;
                }
            }

            double cosSame = Math.Cos(SameDirectionDegrees * Math.PI / 180.0);
            foreach (KeyValuePair<string, List<Proposal>> kv in proposals)
            {
                if (d.Refused.ContainsKey(kv.Key)) continue;
                List<Proposal> list = kv.Value;
                OverlapMember mover = movers[kv.Key];
                Proposal first = list[0];
                string conflict = null;
                foreach (Proposal pr in list.Skip(1))
                {
                    if (pr.Entry.Move != first.Entry.Move)
                        conflict = "order '" + first.Entry.Id + "' moves it by " + first.Entry.Move + " and order '" + pr.Entry.Id + "' by " + pr.Entry.Move;
                    else if (pr.Entry.Move == ModeTranslate && Dot(pr.Unit, first.Unit) < cosSame)
                        conflict = "order '" + first.Entry.Id + "' moves it along " + Show(first.Unit) + " (away from '" + first.Stayer.RuleId +
                                   "') and order '" + pr.Entry.Id + "' along " + Show(pr.Unit) + " (away from '" + pr.Stayer.RuleId + "')";
                    else if (pr.Entry.Move == ModeLegs && pr.Entry.Legs != first.Entry.Legs)
                        conflict = "order '" + first.Entry.Id + "' moves its " + first.Entry.Legs + " leg(s) and order '" + pr.Entry.Id + "' its " + pr.Entry.Legs;
                    if (conflict != null) break;
                }
                if (conflict != null)
                {
                    Refuse(d, mover, CodeConflict, "set '" + mover.RuleId + "' (element " + mover.HostId + ") is asked to move two ways: " + conflict +
                           ". A set moves once, by one displacement; two different ones are not added up. Declare one order for it, or " +
                           "an explicit direction and offset that clears both.");
                    continue;
                }
                Proposal largest = list.OrderByDescending(x => x.Magnitude).First();
                var move = new CrossingMove
                {
                    RuleId = mover.RuleId,
                    HostId = mover.HostId,
                    Mode = first.Entry.Move,
                    Legs = first.Entry.Move == ModeLegs ? first.Entry.Legs : null,
                    OffsetMm = largest.Magnitude,
                    VectorMm = first.Entry.Move == ModeTranslate ? Scale(first.Unit, largest.Magnitude) : null
                };
                foreach (Proposal pr in list)
                {
                    if (!move.OrderIds.Contains(pr.Entry.Id)) move.OrderIds.Add(pr.Entry.Id);
                    move.Against.Add(new JObject
                    {
                        ["rule_id"] = pr.Stayer.RuleId,
                        ["host_id"] = pr.Stayer.HostId,
                        ["order_id"] = pr.Entry.Id,
                        ["kind"] = pr.Pair.Kind,
                        ["relation"] = pr.Pair.Relation,
                        ["clear_mm_as_declared"] = Math.Round(pr.Pair.ClearMm, 3),
                        ["offset_mm"] = Math.Round(pr.Magnitude, 3)
                    });
                }
                move.Reason = "crossing_order " + string.Join(", ", move.OrderIds.Select(x => "'" + x + "'")) + ": '" + mover.RuleId +
                              "' gives way to " + string.Join(", ", list.Select(x => "'" + x.Stayer.RuleId + "' (element " + x.Stayer.HostId + ")").Distinct()) +
                              " - " + move.Describe() + ".";
                d.Moves[kv.Key] = move;
            }
            return d;
        }

        private static void Refuse(CrossingDecision d, OverlapMember m, string code, string why)
        {
            string key = CrossingDecision.Key(m.RuleId, m.HostId);
            CrossingRefusal r;
            if (d.Refused.TryGetValue(key, out r)) { r.Why += " Also: " + why; return; }
            d.Refused[key] = new CrossingRefusal { RuleId = m.RuleId, HostId = m.HostId, Code = code, Why = why };
            d.Moves.Remove(key);
        }

        /// <summary>
        /// toward_host_core: the common perpendicular of the two bars where they meet, to the side of the moving
        /// set's host centre. Null, with why, when it does not read one way.
        /// </summary>
        public static double[] TowardCore(OverlapPair p, OverlapMember mover, double tolMm, out string why)
        {
            why = null;
            if (p.Relation == RebarOverlap.RelationAlongside || p.DirA == null || p.DirB == null)
            {
                why = "the two bars run alongside each other where they meet (within " + RebarOverlap.AlongsideDegrees.ToString("0", CultureInfo.InvariantCulture) +
                      " degrees), so there is no crossing axis to move along.";
                return null;
            }
            double[] axis = Unit(Cross(p.DirA, p.DirB));
            if (axis == null)
            {
                why = "the two bars run alongside each other where they meet, so there is no crossing axis to move along.";
                return null;
            }
            if (mover.HostCentreMm == null)
            {
                why = "its host's centre could not be read, so which side is the core is not known. Unknown is not a direction.";
                return null;
            }
            double[] to = Sub(mover.HostCentreMm, p.PointMm);
            double s = Dot(axis, to);
            if (Math.Abs(s) <= Math.Max(tolMm, 1e-6))
            {
                why = "its host's centre lies on the plane the two bars cross in (" + Mm(Math.Abs(s)) + " mm off it, within the length " +
                      "tolerance), so toward the core reads either way.";
                return null;
            }
            return s > 0 ? axis : Scale(axis, -1);
        }

        // ----------------------------------------------------------- displace

        /// <summary>
        /// The displaced centreline (and arc points) of one set, or why it cannot be displaced that way. The
        /// declared points are never changed in place.
        /// </summary>
        public static string Displace(List<double[]> curves, Dictionary<int, double[]> arcs, bool closed, CrossingMove m,
                                      out List<double[]> moved, out Dictionary<int, double[]> movedArcs)
        {
            moved = (curves ?? new List<double[]>()).Select(p => new[] { p[0], p[1], p[2] }).ToList();
            movedArcs = new Dictionary<int, double[]>();
            if (arcs != null) foreach (KeyValuePair<int, double[]> kv in arcs) movedArcs[kv.Key] = new[] { kv.Value[0], kv.Value[1], kv.Value[2] };
            if (m == null) return "no displacement.";
            if (moved.Count < 2) return "the set carries no centreline to displace.";

            if (m.Mode == ModeTranslate)
            {
                if (m.VectorMm == null) return "the displacement carries no vector.";
                foreach (double[] p in moved) Add(p, m.VectorMm);
                foreach (double[] p in movedArcs.Values) Add(p, m.VectorMm);
                return null;
            }
            if (m.Mode != ModeLegs) return "'" + m.Mode + "' is not a way to move a set.";

            if (closed) return "a closed bar (a stirrup or a hoop) has no end legs to move.";
            if (movedArcs.Count > 0)
                return "the bar bends in an arc, and moving a leg of an arc chain is not implemented: the arc would have to be redrawn, not moved.";
            bool start = m.Legs == LegsBoth || m.Legs == LegsStart, end = m.Legs == LegsBoth || m.Legs == LegsEnd;
            int n = moved.Count;
            if (start && end && n < 4)
                return "moving both legs needs a leg at each end and a run between them - three segments at least - and this bar has " + (n - 1) + ".";
            if (n < 3) return "moving a leg needs a leg and a run - two segments at least - and this bar has " + (n - 1) + ".";
            if (start)
            {
                double[] dir = Unit(Sub(curves[2], curves[1]));
                if (dir == null) return "the run after the start leg has no length.";
                double[] v = Scale(dir, m.OffsetMm);
                Add(moved[0], v);
                Add(moved[1], v);
            }
            if (end)
            {
                double[] dir = Unit(Sub(curves[n - 3], curves[n - 2]));
                if (dir == null) return "the run before the end leg has no length.";
                double[] v = Scale(dir, m.OffsetMm);
                Add(moved[n - 2], v);
                Add(moved[n - 1], v);
            }
            // THE RUN MUST STILL RUN THE SAME WAY: a leg moved past the other end folds the bar over itself.
            if (start && Dot(Sub(moved[2], moved[1]), Sub(curves[2], curves[1])) <= 0)
                return "the run is shorter than the legs' move (" + Mm(m.OffsetMm) + " mm each), so the bar would fold over itself.";
            if (end && Dot(Sub(moved[n - 2], moved[n - 3]), Sub(curves[n - 2], curves[n - 3])) <= 0)
                return "the run is shorter than the legs' move (" + Mm(m.OffsetMm) + " mm each), so the bar would fold over itself.";
            return null;
        }

        /// <summary>A displaced row the plan's own checks then refused (containment, cover, the fit): refused BY NAME, never squeezed.</summary>
        public static void RefuseMovedRow(CrossingMove m, string code, string why, out string newCode, out string newWhy)
        {
            newCode = CodeBreaksCover;
            newWhy = "the declared crossing_order displaced this set (" + (m == null ? "?" : m.Describe()) + "), and the displaced bars no longer " +
                     "fit: " + code + " - " + why + " The displacement is refused, never squeezed to fit: declare another offset, direction " +
                     "or order, or move the other set.";
        }

        // ----------------------------------------------------------- measure

        /// <summary>
        /// The audit's measurement of one displacement: where the built bar's ends are against the DECLARED
        /// (undisplaced) centreline, along the displacement. `agrees` is null when the bar would not read.
        /// </summary>
        public static JObject Measure(CrossingMove m, List<double[]> declared, List<double[]> observed, double tolMm)
        {
            var o = m.ToJson();
            o.Remove("reason");
            o.Remove("against");
            if (declared == null || declared.Count < 2 || observed == null || observed.Count < 2)
            {
                o["agrees"] = JValue.CreateNull();
                o["why"] = "the bar's hook-free centreline would not read, so where its ends stand against the declared ones is unknown.";
                return o;
            }
            double[] d0 = declared[0], d1 = declared[declared.Count - 1];
            double[] o0 = observed[0], o1 = observed[observed.Count - 1];
            // The model may report the bar from the other end.
            if (Dist(o0, d1) + Dist(o1, d0) < Dist(o0, d0) + Dist(o1, d1)) { double[] t = o0; o0 = o1; o1 = t; }

            double[] u0, u1;
            double e0, e1;
            if (m.Mode == ModeTranslate)
            {
                u0 = u1 = Unit(m.VectorMm);
                e0 = e1 = m.OffsetMm;
            }
            else
            {
                int n = declared.Count;
                u0 = n >= 3 ? Unit(Sub(declared[2], declared[1])) : null;
                u1 = n >= 3 ? Unit(Sub(declared[n - 3], declared[n - 2])) : null;
                e0 = m.Legs == LegsBoth || m.Legs == LegsStart ? m.OffsetMm : 0;
                e1 = m.Legs == LegsBoth || m.Legs == LegsEnd ? m.OffsetMm : 0;
            }
            if (u0 == null || u1 == null)
            {
                o["agrees"] = JValue.CreateNull();
                o["why"] = "the declared centreline has no run to measure the legs along.";
                return o;
            }
            double[] s0 = Sub(o0, d0), s1 = Sub(o1, d1);
            double a0 = Dot(s0, u0), a1 = Dot(s1, u1);
            double x0 = Norm(Sub(s0, Scale(u0, a0))), x1 = Norm(Sub(s1, Scale(u1, a1)));
            o["expected_start_mm"] = Math.Round(e0, 3);
            o["expected_end_mm"] = Math.Round(e1, 3);
            o["measured_start_mm"] = Math.Round(a0, 3);
            o["measured_end_mm"] = Math.Round(a1, 3);
            o["off_the_displacement_mm"] = Math.Round(Math.Max(x0, x1), 3);
            o["tolerance_mm"] = tolMm;
            o["agrees"] = Math.Abs(a0 - e0) <= tolMm && Math.Abs(a1 - e1) <= tolMm &&
                          (m.Mode == ModeLegs || Math.Max(x0, x1) <= tolMm);
            o["measured_means"] = "the built bar's hook-free ends against the DECLARED (undisplaced) ends, along the displacement " +
                                  (m.Mode == ModeLegs ? "(each leg inward along the run)" : "") +
                                  "; off_the_displacement_mm is how far they also moved across it.";
            return o;
        }

        // ------------------------------------------------------------ vectors

        internal static double[] Unit(double[] v)
        {
            if (v == null) return null;
            double n = Norm(v);
            return n < 1e-12 ? null : new[] { v[0] / n, v[1] / n, v[2] / n };
        }
        private static double Norm(double[] v) => Math.Sqrt(Dot(v, v));
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        private static void Add(double[] p, double[] v) { p[0] += v[0]; p[1] += v[1]; p[2] += v[2]; }
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double Dist(double[] a, double[] b) => Norm(Sub(a, b));
        private static string Show(double[] u) => "(" + string.Join(", ", u.Select(Mm3)) + ")";
        internal static string Mm(double v) => Math.Round(v, 1).ToString("0.###", CultureInfo.InvariantCulture);
        internal static string Mm3(double v) => (Math.Round(v, 3) + 0.0).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
