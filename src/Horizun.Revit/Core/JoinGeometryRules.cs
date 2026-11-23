// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of horizun_join_geometry: which fields each operation takes,
// which class an element belongs to for cut priority, which element of a pair must
// cut, and the verdict on a pair after the commit.
//
// field cycle (video, 2026-10-24, E2/A3/A6): "vigas y muros con la prioridad de corte
// correcta, sin volumen duplicado - lo que más debe mejorar". JoinGeometry and
// SwitchJoinOrder lived only inside split_multilayer_walls and a Python recipe; a
// beam through a slab, a column through a beam or two overlapping walls counted
// the shared concrete twice and nothing typed could join them.
//
// THE PRIORITY, said once (the caller may pass another order):
//   structural_column > structural_framing > wall_structural > floor_structural
//   > structural_foundation > wall > floor
// The element of HIGHER priority cuts the other. Two elements of the same class
// are a tie: the order is left as Revit chose and reported as a tie, never decided
// by guess.
//
// THE VERDICT on a joined pair is three readings of the committed model: joined
// (AreElementsJoined), who cuts (IsCuttingElementInJoin, when an order was asked)
// and the volume the two solids still share - after a join that is the duplicated
// concrete, and it must be (near) zero. A volume that could not be read is unknown,
// never zero.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class JoinGeometryRules
    {
        public const string Read = "read";
        public const string Join = "join";
        public const string Unjoin = "unjoin";
        public const string SwitchOrder = "switch_order";
        /// <summary>Read-only: open wall corners (WallCornerRules).</summary>
        public const string Corners = "corners";
        /// <summary>Closes the open corners: allow the join at the end, move the end to the crossing.</summary>
        public const string FixCorners = "fix_corners";
        /// <summary>Every joinable element of one level: overlapping pairs found and joined by priority, volume before/after.</summary>
        public const string AutoPriority = "auto_priority";
        public const int MaxAutoElements = 1500;
        public const int MaxAutoPairs = 1000;

        public const string FirstCuts = "first_cuts";
        public const string ByPriority = "priority";
        public const string KeepRevit = "keep_revit";

        public const int MaxPairs = 200;
        public const int MaxElements = 300;

        /// <summary>Overlap left after a join that still counts as none: 1 cm3 (rounding of tessellated solids).</summary>
        public const double OverlapToleranceM3 = 1e-6;

        public static readonly string[] Classes =
        {
            "structural_column", "structural_framing", "wall_structural", "floor_structural",
            "structural_foundation", "wall", "floor", "roof"
        };

        public static readonly string[] DefaultPriority = Classes;

        /// <summary>The categories this tool joins (BuiltInCategory names).</summary>
        public static readonly string[] Categories =
            { "OST_Walls", "OST_Floors", "OST_StructuralFraming", "OST_StructuralColumns", "OST_StructuralFoundation", "OST_Roofs" };

        private static readonly string[] Common =
            { "target_document", "expected_document", "target_document_title", "units", "operation", "dry_run", "confirmation_token", "transaction_name", "idempotency_key" };

        public static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Read] = new[] { "pairs", "element_ids" },
            [Join] = new[] { "pairs", "element_ids", "cut_order", "priority", "verification_mode" },
            [Unjoin] = new[] { "pairs" },
            [SwitchOrder] = new[] { "pairs" },
            [Corners] = new[] { "element_ids", "tolerance" },
            [FixCorners] = new[] { "element_ids", "tolerance" },
            [AutoPriority] = new[] { "level_id", "priority", "verification_mode" },
            // A toposolid cut by another element: pairs are [toposolid, excavator].
            [ToposolidExcavationRules.Excavate] = new[] { "pairs", "element_ids" },
            [ToposolidExcavationRules.Unexcavate] = new[] { "pairs", "element_ids" }
        };

        /// <summary>Null when the request is well formed for its operation; otherwise why not.</summary>
        public static string Validate(JObject request)
        {
            if (request == null) return "parameters must be a JSON object.";
            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (!Fields.TryGetValue(op, out string[] own))
                return "operation must be one of " + string.Join(", ", Fields.Keys) + " for horizun_join_geometry.";
            var allowed = new HashSet<string>(Common.Concat(own), StringComparer.Ordinal);
            foreach (JProperty p in request.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to operation '" + op + "'. Nothing ran.";
            if (request["verification_mode"] != null && (string)request["verification_mode"] != "atomic" && (string)request["verification_mode"] != "per_pair")
                return "verification_mode must be atomic or per_pair (join and auto_priority only).";
            if (op != Read && op != Corners && string.IsNullOrWhiteSpace(ReadDocumentRules.Target(request)))
                return "target_document is required for " + op + ".";
            if (op == AutoPriority)
            {
                if (request["level_id"]?.Type != JTokenType.Integer || request.Value<long>("level_id") <= 0)
                    return "level_id must be a positive integer: the level whose elements are joined by priority.";
                // A level-wide operation must never silently choose a discipline's cut convention.
                return PriorityError(request["priority"]);
            }
            if (ToposolidExcavationRules.IsExcavation(op))
                return ToposolidExcavationRules.PairsError(request["pairs"], request["element_ids"]);
            if (op == Corners || op == FixCorners)
            {
                if (!(request["element_ids"] is JArray wallIds) || wallIds.Count < 2 || wallIds.Count > MaxElements || wallIds.Any(t => t.Type != JTokenType.Integer))
                    return "element_ids must list 2.." + MaxElements + " integer wall ids (one storey, or the walls to check).";
                if (wallIds.Select(t => t.Value<long>()).Distinct().Count() != wallIds.Count) return "element_ids names the same element twice.";
                JToken tol = request["tolerance"];
                if (tol != null && tol.Type != JTokenType.Null &&
                    !((tol.Type == JTokenType.Float || tol.Type == JTokenType.Integer) && (double)tol > 0))
                    return "tolerance must be a positive number in units (default 50 mm).";
                return null;
            }

            bool hasPairs = request["pairs"] != null && request["pairs"].Type != JTokenType.Null;
            bool hasIds = request["element_ids"] != null && request["element_ids"].Type != JTokenType.Null;
            if (hasPairs == hasIds)
                return op == Unjoin || op == SwitchOrder
                    ? "pairs is required: [[a, b], ...] - each pair is two element ids."
                    : "give pairs ([[a, b], ...]) OR element_ids (the pairs are then found among them by overlap), exactly one.";
            if (hasPairs)
            {
                string pe = PairsError(request["pairs"]);
                if (pe != null) return pe;
            }
            if (hasIds)
            {
                if (!(request["element_ids"] is JArray ids) || ids.Count < 2 || ids.Count > MaxElements || ids.Any(t => t.Type != JTokenType.Integer))
                    return "element_ids must list 2.." + MaxElements + " integer element ids.";
                if (ids.Select(t => t.Value<long>()).Distinct().Count() != ids.Count) return "element_ids names the same element twice.";
            }
            if (op == Join)
            {
                string order = request.Value<string>("cut_order") ?? (hasIds ? ByPriority : FirstCuts);
                if (order != FirstCuts && order != ByPriority && order != KeepRevit)
                    return "cut_order must be first_cuts, priority or keep_revit.";
                if (hasIds && order == FirstCuts)
                    return "cut_order=first_cuts needs pairs: with element_ids no element is 'first'. Use priority or keep_revit.";
                if (request["priority"] != null)
                {
                    if (order != ByPriority) return "priority applies only with cut_order=priority.";
                    string pr = PriorityError(request["priority"]);
                    if (pr != null) return pr;
                }
            }
            return null;
        }

        private static string PairsError(JToken token)
        {
            if (!(token is JArray pairs) || pairs.Count == 0 || pairs.Count > MaxPairs)
                return "pairs must list 1.." + MaxPairs + " pairs [a, b].";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Count; i++)
            {
                if (!(pairs[i] is JArray p) || p.Count != 2 || p.Any(t => t.Type != JTokenType.Integer))
                    return "pairs[" + i + "] must be [a, b], two integer element ids.";
                long a = p[0].Value<long>(), b = p[1].Value<long>();
                if (a == b) return "pairs[" + i + "] joins element " + a + " to itself.";
                if (!seen.Add(Key(a, b))) return "pairs[" + i + "] repeats the pair " + a + "/" + b + " (in either order).";
            }
            return null;
        }

        private static string PriorityError(JToken token)
        {
            if (!(token is JArray list) || list.Count == 0 || list.Any(t => t.Type != JTokenType.String))
                return "priority must be a list of classes, highest first: " + string.Join(", ", Classes) + ".";
            var names = list.Select(t => (string)t).ToList();
            string unknown = names.FirstOrDefault(n => Array.IndexOf(Classes, n) < 0);
            if (unknown != null) return "priority: '" + unknown + "' is not a class; the classes are " + string.Join(", ", Classes) + ".";
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) return "priority names a class twice.";
            return null;
        }

        /// <summary>The order-free key of a pair.</summary>
        public static string Key(long a, long b) => a < b ? a + "/" + b : b + "/" + a;

        /// <summary>The class an element takes in the cut priority; null for a category this tool does not join.</summary>
        public static string ClassOf(string categoryToken, bool? structural)
        {
            switch (categoryToken)
            {
                case "OST_StructuralColumns": return "structural_column";
                case "OST_StructuralFraming": return "structural_framing";
                case "OST_StructuralFoundation": return "structural_foundation";
                case "OST_Walls": return structural == true ? "wall_structural" : "wall";
                case "OST_Floors": return structural == true ? "floor_structural" : "floor";
                // Last by default: a concrete roof slab poured over beams, columns or a floor is the one cut.
                case "OST_Roofs": return "roof";
                default: return null;
            }
        }

        /// <summary>The priority list in effect: the caller's, then any class it left out in the default order.</summary>
        public static List<string> PriorityOf(JToken token)
        {
            var list = (token as JArray)?.Select(t => (string)t).ToList() ?? new List<string>();
            foreach (string c in DefaultPriority) if (!list.Contains(c)) list.Add(c);
            return list;
        }

        /// <summary>A level-wide rule must explicitly rank every class found on the level.</summary>
        public static string PriorityCoverageError(IEnumerable<string> classes, IList<string> priority)
        {
            string[] missing = classes.Distinct(StringComparer.Ordinal).Where(c => !priority.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToArray();
            return missing.Length == 0 ? null : "priority must explicitly rank every class on this level; missing: " + string.Join(", ", missing) + ".";
        }

        /// <summary>Sum of pair intersections, not geometric union volume. Unknown/invalid never becomes zero.</summary>
        public static double? SharedVolumeSum(IEnumerable<double?> readings)
        {
            double total = 0;
            foreach (double? reading in readings)
            {
                if (!reading.HasValue || double.IsNaN(reading.Value) || double.IsInfinity(reading.Value) || reading.Value < 0) return null;
                total += reading.Value;
                if (double.IsInfinity(total)) return null;
            }
            return total;
        }

        public static bool SharedVolumeClosed(double? sum)
            => sum.HasValue && !double.IsNaN(sum.Value) && !double.IsInfinity(sum.Value) && sum.Value >= 0 && sum.Value <= OverlapToleranceM3;

        /// <summary>A successful null/empty intersection is zero; a failed boolean is unknown even if it returned null.</summary>
        public static double? IntersectionVolume(bool booleanSucceeded, double? volume) =>
            booleanSucceeded ? SharedVolumeSum(new double?[] { volume ?? 0 }) : null;

        /// <summary>Independent supporting evidence; never substitutes for measuring shared volume.</summary>
        public static double? MemberVolumeReduction(double? before, double? after) =>
            SharedVolumeSum(new[] { before }).HasValue && SharedVolumeSum(new[] { after }).HasValue ? before - after : null;

        /// <summary>
        /// A geometry traversal is one measurement: discard everything if any branch fails.
        /// A partial traversal must never be mistaken for a complete set of solids.
        /// </summary>
        public static List<T> CollectComplete<T>(Action<List<T>> read)
        {
            var found = new List<T>();
            try { read(found); return found; }
            catch { return new List<T>(); }
        }

        /// <summary>
        /// Which element of the pair must cut: +1 = a, -1 = b, 0 = a tie (same rank) the priority cannot decide.
        /// </summary>
        public static int Cutter(string classA, string classB, IList<string> priority)
        {
            int ra = priority.IndexOf(classA), rb = priority.IndexOf(classB);
            if (ra < 0 || rb < 0 || ra == rb) return 0;
            return ra < rb ? 1 : -1;
        }

        /// <summary>What a join needs to do to one pair, from its current state.</summary>
        public static string JoinAction(bool joinedNow, bool? aCutsNow, int wantedCutter)
        {
            if (!joinedNow) return wantedCutter == 0 ? "join" : (wantedCutter > 0 ? "join_a_cuts" : "join_b_cuts");
            if (wantedCutter == 0) return "nothing";
            if (aCutsNow == null) return "switch_if_needed";
            bool aShould = wantedCutter > 0;
            return aCutsNow.Value == aShould ? "nothing" : "switch";
        }

        /// <summary>Fresh facts, never facts retained from the planning transaction.</summary>
        public sealed class CurrentPair
        {
            public string InvalidReason;
            public bool? Joined;
            public bool? ACuts;
            public bool Touching;
            public bool HasSolids;
        }

        /// <summary>
        /// The native-call ordering guard. Regeneration and a fresh resolution are mandatory
        /// between JoinGeometry and any cutting-order API. A native access violation cannot
        /// be caught here: callers must record the pending phase before entering Revit.
        /// </summary>
        public static void ApplyCurrentPair(string operation, int wantedCutter, Func<CurrentPair> read,
            Action<string> mutate, Action regenerate)
        {
            CurrentPair Current()
            {
                CurrentPair p = read();
                if (p == null || p.InvalidReason != null)
                    throw new InvalidOperationException(p?.InvalidReason ?? "pair_missing: the pair could not be resolved");
                if (!p.Joined.HasValue) throw new InvalidOperationException("join_state_unreadable: the current join state is unknown");
                return p;
            }
            CurrentPair current = Current();
            if (operation == Unjoin)
            {
                if (current.Joined == true) { mutate(Unjoin); regenerate(); }
                return;
            }
            if (operation == SwitchOrder && current.Joined != true)
                throw new InvalidOperationException("not_joined: cut order cannot be switched on an unjoined pair");
            if (current.Joined != true)
            {
                if (!current.Touching) throw new InvalidOperationException("no_longer_touching: pair geometry changed since planning");
                if (!current.HasSolids) throw new InvalidOperationException("no_readable_solid: a current member has no readable positive-volume solid");
                mutate(Join);
                regenerate();
                current = Current();
                if (current.Joined != true)
                    throw new InvalidOperationException("join_not_retained: native join did not survive regeneration; no cut-order API was called");
            }
            if (wantedCutter == 0) return;
            if (!current.ACuts.HasValue)
                throw new InvalidOperationException("cut_order_unreadable: current cutting order is unknown; no switch was attempted");
            if (current.ACuts.Value == (wantedCutter > 0)) return;
            // Re-resolve just before the switch as well: reading geometry may invalidate a
            // native wrapper and a previous pair may have removed/replaced a participant.
            current = Current();
            if (current.Joined != true)
                throw new InvalidOperationException("not_joined: pair lost its join before the switch");
            if (!current.ACuts.HasValue)
                throw new InvalidOperationException("cut_order_unreadable: cutting order disappeared before the switch");
            if (current.ACuts.Value == (wantedCutter > 0)) return;
            mutate(SwitchOrder);
            regenerate();
        }

        /// <summary>
        /// The verdict on a pair after the commit, for the joined/order/overlap readings. Null when it holds;
        /// otherwise the reason. overlapM3 = null means the shared volume could not be measured.
        /// </summary>
        public static string JoinedPairVerdict(bool joined, int wantedCutter, bool? aCuts, double? overlapM3)
        {
            if (!joined) return "not joined after the commit";
            if (wantedCutter != 0)
            {
                if (aCuts == null) return "who cuts could not be read";
                if (aCuts.Value != wantedCutter > 0) return "the wrong element cuts";
            }
            if (SharedVolumeSum(new[] { overlapM3 }) == null) return "the shared volume could not be measured";
            if (overlapM3.Value > OverlapToleranceM3) return "the two solids still share " + Math.Round(overlapM3.Value, 6) + " m3";
            return null;
        }

        /// <summary>Two boxes [minX,minY,minZ,maxX,maxY,maxZ] touch or overlap within tol.</summary>
        public static bool BoxesTouch(double[] a, double[] b, double tol)
        {
            if (a == null || b == null || a.Length != 6 || b.Length != 6) return false;
            for (int i = 0; i < 3; i++)
                if (a[i] > b[i + 3] + tol || b[i] > a[i + 3] + tol) return false;
            return true;
        }
    }
}
