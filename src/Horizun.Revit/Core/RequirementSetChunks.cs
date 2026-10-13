// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// A LARGE REQUIREMENT SET, CUT INTO PIECES BY LEVEL OR BY HOST TYPE (refuerzo
// desde cartilla, F3), so each piece can run as its own horizun_submit_job and a
// building of tens of thousands of bars is not one call that holds Revit for an hour.
//
// The hosts come from an inventory the caller read (horizun_query_model: element id,
// category, type name, Mark, level). Each rule's selector is matched exactly as
// ReinforcementResolver reads it: explicit element_ids win and the category is then
// not applied; type_name, mark and level are exact. Each piece is the original set
// with every rule narrowed to the element_ids it targets in that piece - its other
// filters kept, so Revit re-checks them - and the rules that target nothing there
// left out. A rule that targets nothing anywhere is reported, never silently dropped.
//
// L10 wires it to horizun_plan/apply/audit_reinforcement through `chunk`: Select below
// chooses one piece and names the whole cut by a digest every call of a loop quotes.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class RequirementSetChunks
    {
        public const string ByLevel = "level", ByHostType = "host_type";
        public const string CodeBadDimension = "chunk_bad_dimension";
        public const string CodeBadSize = "chunk_bad_size";
        public const string CodeNoRules = "chunk_no_rules";
        public const string CodeNoHosts = "chunk_no_hosts";
        public const string CodeMissingKey = "chunk_host_without_key";

        public sealed class Host
        {
            public long ElementId;
            public string Category, TypeName, Mark, Level;
        }

        public sealed class Chunk
        {
            public int Index;
            public string Key;
            public List<long> ElementIds = new List<long>();
            public JObject RequirementSet;
        }

        public sealed class Plan
        {
            public bool Ok;
            public string Code, Why;
            public string By;
            public List<Chunk> Chunks = new List<Chunk>();
            public List<string> RulesMatchingNothing = new List<string>();
            public List<long> UnknownElementIds = new List<long>();
            public int HostsCovered;

            public JObject ToJson() => new JObject
            {
                ["ok"] = Ok, ["code"] = Code, ["why"] = Why, ["by"] = By,
                ["hosts_covered"] = HostsCovered,
                ["rules_matching_nothing"] = new JArray(RulesMatchingNothing),
                ["unknown_element_ids"] = new JArray(UnknownElementIds),
                ["chunks"] = new JArray(Chunks.Select(c => new JObject
                {
                    ["index"] = c.Index, ["key"] = c.Key, ["element_ids"] = new JArray(c.ElementIds),
                    ["requirement_set"] = c.RequirementSet
                }))
            };
        }

        // ---- L10: one piece of the cut, chosen by a tool call --------------------------
        //
        // horizun_plan_reinforcement, horizun_apply_reinforcement and
        // horizun_audit_reinforcement accept `chunk: { by, hosts_per_piece, piece_index,
        // partition_sha256 }`. The command reads the host inventory off the model, cuts the
        // set exactly as Split does, and runs ONE piece. Every piece of a loop must be a
        // piece of the SAME cut - a host added, a Mark edited or the set changed between two
        // submissions moves hosts between pieces, and a loop that kept counting would build
        // some hosts twice and others never - so the cut is named by a digest, and a write
        // must quote the digest it was planned against.

        public const string CodeBadRequest = "chunk_bad_request";
        public const string CodePieceOutOfRange = "chunk_piece_out_of_range";
        public const string CodePartitionChanged = "chunk_partition_changed";
        public const string CodePartitionRequired = "chunk_partition_required";

        /// <summary>The same ceiling as host_ids: a piece is never larger than one call may name.</summary>
        public const int MaxHostsPerPiece = 2000;

        public static readonly string[] RequestKeys = { "by", "hosts_per_piece", "piece_index", "partition_sha256" };

        public sealed class Request
        {
            public string By;
            public int HostsPerPiece;
            public int PieceIndex;
            /// <summary>Null when the caller did not quote one.</summary>
            public string PartitionSha256;
        }

        public sealed class Selection
        {
            public bool Ok;
            public string Code, Why;
            public Request Request;
            /// <summary>Null on a refusal that would otherwise hand the caller a digest it never read.</summary>
            public string PartitionSha256;
            public string SetSha256;
            public int PieceCount;
            public Chunk Piece;
            public Plan Plan;

            public JObject ToJson()
            {
                var o = new JObject { ["ok"] = Ok };
                if (!Ok)
                {
                    o["code"] = Code; o["why"] = Why;
                    if (PieceCount > 0) o["piece_count"] = PieceCount;
                    return o;
                }
                o["by"] = Request.By;
                o["hosts_per_piece"] = Request.HostsPerPiece;
                o["piece_index"] = Piece.Index;
                o["piece_count"] = PieceCount;
                o["next_piece_index"] = Piece.Index + 1 < PieceCount ? (JToken)(Piece.Index + 1) : JValue.CreateNull();
                o["partition_sha256"] = PartitionSha256;
                o["partition_means"] =
                    "the digest of this cut: the requirement set, the dimension, the piece size and every piece's hosts. " +
                    "Quote it as chunk.partition_sha256 on every call of the loop; a write without it is refused, and " +
                    "a call whose cut has moved (a host added, a Mark or level edited, the set changed) is refused " +
                    "as chunk_partition_changed rather than run on a different piece.";
                o["requirement_set_sha256"] = SetSha256;
                o["piece"] = new JObject
                {
                    ["index"] = Piece.Index,
                    ["key"] = Piece.Key,
                    ["hosts"] = Piece.ElementIds.Count,
                    ["element_ids"] = new JArray(Piece.ElementIds),
                    ["requirement_set_sha256"] = StructuralRequirementSet.Sha256Of(Piece.RequirementSet)
                };
                o["pieces"] = new JArray(Plan.Chunks.Select(c => new JObject { ["index"] = c.Index, ["key"] = c.Key, ["hosts"] = c.ElementIds.Count }));
                o["hosts_covered"] = Plan.HostsCovered;
                o["rules_matching_nothing"] = new JArray(Plan.RulesMatchingNothing);
                o["unknown_element_ids"] = new JArray(Plan.UnknownElementIds);
                return o;
            }
        }

        /// <summary>Read `chunk`. Null, with code and error, when it is not exactly right; nothing is defaulted.</summary>
        public static Request ReadRequest(JToken chunk, out string code, out string error)
        {
            code = null; error = null;
            var o = chunk as JObject;
            if (o == null) return Bad(out code, out error, "chunk must be an object { by, hosts_per_piece, piece_index[, partition_sha256] }.");
            foreach (JProperty p in o.Properties())
                if (Array.IndexOf(RequestKeys, p.Name) < 0)
                    return Bad(out code, out error, "chunk." + p.Name + " is not a known key (" + string.Join(", ", RequestKeys) + ").");
            var r = new Request();
            JToken by = o["by"];
            if (by == null || by.Type != JTokenType.String || ((string)by != ByLevel && (string)by != ByHostType))
                return Bad(out code, out error, "chunk.by is required and must be '" + ByLevel + "' or '" + ByHostType + "'.");
            r.By = (string)by;
            JToken size = o["hosts_per_piece"];
            if (size == null || size.Type != JTokenType.Integer || (long)size < 1 || (long)size > MaxHostsPerPiece)
                return Bad(out code, out error, "chunk.hosts_per_piece is required and must be a whole number from 1 to " + MaxHostsPerPiece + ".");
            r.HostsPerPiece = (int)(long)size;
            JToken index = o["piece_index"];
            if (index == null || index.Type != JTokenType.Integer || (long)index < 0 || (long)index > int.MaxValue)
                return Bad(out code, out error, "chunk.piece_index is required and must be a whole number, zero or more.");
            r.PieceIndex = (int)(long)index;
            JToken sha = o["partition_sha256"];
            if (sha != null)
            {
                string text = sha.Type == JTokenType.String ? (string)sha : null;
                if (text == null || text.Length != 64 || !text.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
                    return Bad(out code, out error, "chunk.partition_sha256 must be the 64 lower-case hex digits a previous call of this loop returned.");
                r.PartitionSha256 = text;
            }
            return r;
        }

        /// <summary>
        /// Cut the set (Split) and choose the asked piece. `partitionRequired` is true on a
        /// write: the caller must quote the digest of the cut it planned against.
        /// </summary>
        public static Selection Select(JObject set, IList<Host> hosts, Request request, bool partitionRequired)
        {
            var s = new Selection { Request = request, SetSha256 = StructuralRequirementSet.Sha256Of(set) };
            Plan plan = Split(set, hosts, request.By, request.HostsPerPiece);
            s.Plan = plan;
            if (!plan.Ok) return Refuse(s, plan.Code, plan.Why + " Nothing was run.");
            string sha = PartitionSha256(s.SetSha256, plan, request.HostsPerPiece);
            s.PieceCount = plan.Chunks.Count;
            if (request.PartitionSha256 == null && partitionRequired)
                return Refuse(s, CodePartitionRequired,
                    "a write that runs one piece must quote chunk.partition_sha256 - the digest of the cut it was planned " +
                    "against. Run horizun_plan_reinforcement with the same requirement_set and chunk and copy its " +
                    "chunk.partition_sha256 into every call of the loop. Nothing was run.");
            if (request.PartitionSha256 != null && !string.Equals(request.PartitionSha256, sha, StringComparison.Ordinal))
                return Refuse(s, CodePartitionChanged,
                    "the cut is no longer the one chunk.partition_sha256 names: the requirement set, the piece size, the " +
                    "dimension or the hosts it reads (their level, type, Mark or number) changed since it was planned. " +
                    "Running piece " + request.PieceIndex + " of a different cut would build some hosts twice and others " +
                    "never. Plan the loop again. Nothing was run.");
            if (request.PieceIndex >= plan.Chunks.Count)
                return Refuse(s, CodePieceOutOfRange,
                    "piece_index " + request.PieceIndex + " is past the last piece: this cut has " + plan.Chunks.Count +
                    " piece(s), numbered from 0. Nothing was run.");
            s.PartitionSha256 = sha;
            s.Piece = plan.Chunks[request.PieceIndex];
            s.Ok = true;
            return s;
        }

        /// <summary>The set, the dimension, the size and every piece's key and hosts - one line each, hashed.</summary>
        private static string PartitionSha256(string setSha, Plan plan, int size)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("set=").Append(setSha).Append('\n');
            sb.Append("by=").Append(plan.By).Append('\n');
            sb.Append("hosts_per_piece=").Append(size.ToString(inv)).Append('\n');
            foreach (Chunk c in plan.Chunks)
            {
                sb.Append(c.Index.ToString(inv)).Append('|')
                  .Append(Newtonsoft.Json.JsonConvert.ToString(c.Key)).Append('|')
                  .Append(string.Join(",", c.ElementIds.Select(x => x.ToString(inv))))
                  .Append('\n');
            }
            using (var h = System.Security.Cryptography.SHA256.Create())
            {
                byte[] d = h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
                return string.Concat(d.Select(b => b.ToString("x2", inv)));
            }
        }

        private static Request Bad(out string code, out string error, string why)
        {
            code = CodeBadRequest; error = why + " Nothing was run.";
            return null;
        }

        private static Selection Refuse(Selection s, string code, string why) { s.Ok = false; s.Code = code; s.Why = why; s.Piece = null; return s; }

        public static Plan Split(JObject set, IList<Host> hosts, string by, int maxHostsPerChunk)
        {
            var plan = new Plan { By = by };
            if (by != ByLevel && by != ByHostType)
                return Refuse(plan, CodeBadDimension, "by must be '" + ByLevel + "' or '" + ByHostType + "', not '" + by + "'.");
            if (maxHostsPerChunk < 1)
                return Refuse(plan, CodeBadSize, "max_hosts_per_chunk must be at least 1.");
            List<string> ruleArrays = RuleArrays(set);
            if (ruleArrays.Count == 0)
                return Refuse(plan, CodeNoRules, "the set has no rule array whose entries name a host.");
            if (hosts == null || hosts.Count == 0)
                return Refuse(plan, CodeNoHosts, "no host inventory was given; read it first (horizun_query_model) and pass it.");

            Dictionary<long, Host> byId = hosts.GroupBy(h => h.ElementId).ToDictionary(g => g.Key, g => g.First());
            // rule key "<array>/<index>" -> the host ids it targets
            var targets = new Dictionary<string, List<long>>();
            var unknown = new SortedSet<long>();
            foreach (string array in ruleArrays)
            {
                var rules = (JArray)set[array];
                for (int i = 0; i < rules.Count; i++)
                {
                    var rule = rules[i] as JObject;
                    var sel = rule?["host"] as JObject;
                    if (sel == null) continue;
                    List<long> ids = Matching(sel, hosts, byId, unknown);
                    if (ids.Count == 0) plan.RulesMatchingNothing.Add((string)rule["id"] ?? (array + "[" + i + "]"));
                    targets[array + "/" + i] = ids;
                }
            }
            plan.UnknownElementIds = unknown.ToList();

            var covered = new SortedSet<long>(targets.Values.SelectMany(x => x));
            if (covered.Count == 0)
                return Refuse(plan, CodeNoHosts, "no host in the inventory is targeted by any rule of the set.");
            foreach (long id in covered)
            {
                if (string.IsNullOrEmpty(KeyOf(byId[id], by)))
                    return Refuse(plan, CodeMissingKey, "host " + id + " has no " + (by == ByLevel ? "level" : "category and type name") +
                                  ", so it cannot be placed in a piece by " + by + ". Nothing was split.");
            }
            plan.HostsCovered = covered.Count;

            int index = 0;
            foreach (IGrouping<string, long> group in covered.GroupBy(id => KeyOf(byId[id], by)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                List<long> ids = group.OrderBy(x => x).ToList();
                for (int start = 0; start < ids.Count; start += maxHostsPerChunk)
                {
                    var piece = new HashSet<long>(ids.Skip(start).Take(maxHostsPerChunk));
                    plan.Chunks.Add(new Chunk
                    {
                        Index = index++,
                        Key = group.Key,
                        ElementIds = piece.OrderBy(x => x).ToList(),
                        RequirementSet = Narrow(set, ruleArrays, targets, piece)
                    });
                }
            }
            plan.Ok = true;
            return plan;
        }

        /// <summary>Top-level arrays whose entries are objects carrying a "host" selector.</summary>
        private static List<string> RuleArrays(JObject set) =>
            set.Properties().Where(p => p.Value is JArray a && a.Count > 0 && a.All(x => x is JObject o && o["host"] is JObject))
               .Select(p => p.Name).ToList();

        private static List<long> Matching(JObject sel, IList<Host> hosts, Dictionary<long, Host> byId, SortedSet<long> unknown)
        {
            string category = (string)sel["category"], type = (string)sel["type_name"], mark = (string)sel["mark"], level = (string)sel["level"];
            IEnumerable<Host> found;
            var ids = (sel["element_ids"] as JArray)?.Select(t => (long)t).Distinct().ToList() ?? new List<long>();
            if (ids.Count > 0)
            {
                // As the resolver reads it: explicit ids, and then the category is not applied.
                foreach (long id in ids) if (!byId.ContainsKey(id)) unknown.Add(id);
                found = ids.Where(byId.ContainsKey).Select(id => byId[id]);
            }
            else
                found = hosts.Where(h => string.IsNullOrWhiteSpace(category) || string.Equals(h.Category, category, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(type)) found = found.Where(h => string.Equals(h.TypeName, type, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(mark)) found = found.Where(h => string.Equals(h.Mark, mark, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(level)) found = found.Where(h => string.Equals(h.Level, level, StringComparison.Ordinal));
            return found.Select(h => h.ElementId).Distinct().OrderBy(x => x).ToList();
        }

        private static JObject Narrow(JObject set, List<string> ruleArrays, Dictionary<string, List<long>> targets, HashSet<long> piece)
        {
            var copy = (JObject)set.DeepClone();
            foreach (string array in ruleArrays)
            {
                var original = (JArray)set[array];
                var kept = new JArray();
                for (int i = 0; i < original.Count; i++)
                {
                    List<long> here;
                    if (!targets.TryGetValue(array + "/" + i, out here)) continue;
                    List<long> ids = here.Where(piece.Contains).ToList();
                    if (ids.Count == 0) continue;
                    var rule = (JObject)original[i].DeepClone();
                    var sel = (JObject)rule["host"];
                    // Provenance: the selector as written, before it was narrowed to this piece's ids.
                    // A rule that already carries one (a piece cut again) keeps THAT one: the narrowed
                    // selector is not what anybody wrote (backlog #53).
                    var originalHost = (JObject)original[i]["host"];
                    var source = originalHost["source_selector"] is JObject written
                        ? (JObject)written.DeepClone()
                        : (JObject)originalHost.DeepClone();
                    source.Remove("source_selector");
                    sel.Remove("category");
                    sel["source_selector"] = source;
                    sel["element_ids"] = new JArray(ids);
                    kept.Add(rule);
                }
                if (kept.Count > 0) copy[array] = kept; else copy.Remove(array);
            }
            // A DECLARED CLEARANCE PAIR (overlapping_bar) names rules by id. A piece that no longer carries
            // one of them keeps the pair out - it could not apply there, and the piece would be refused for
            // naming a rule it does not declare. between_rules_mm and crossing_mm hold in every piece.
            if (copy["clearances"] is JObject cl && cl["pairs"] is JArray pairs)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (string array in ruleArrays)
                    foreach (JToken rule in copy[array] as JArray ?? new JArray())
                        if (rule is JObject ro && ro["id"] != null && ro["id"].Type == JTokenType.String) ids.Add((string)ro["id"]);
                var keptPairs = new JArray(pairs.Where(p => !(p is JObject po) || !(po["rules"] is JArray names) ||
                                                            names.All(n => n.Type != JTokenType.String || ids.Contains((string)n)))
                                                .Select(p => p.DeepClone()));
                cl["pairs"] = keptPairs;
            }
            // A DECLARED CROSSING ORDER (crossing_order) names rules by id or glob, and a ref that names no rule the
            // set declares is refused. A piece keeps every ref that still names one of its rules; an order left with no
            // stays or no moves cannot apply in this piece and goes, and with no order left the block goes. The pairs it
            // would have covered across two pieces are not in either piece: in a chunked run they are not compared at all
            // (overlapping_bar's not_compared says so).
            if (copy["crossing_order"] is JObject co && co["orders"] is JArray orders)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (string array in ruleArrays)
                    foreach (JToken rule in copy[array] as JArray ?? new JArray())
                        if (rule is JObject ro && ro["id"] != null && ro["id"].Type == JTokenType.String) ids.Add((string)ro["id"]);
                var keptOrders = new JArray();
                foreach (JToken t in orders)
                {
                    if (!(t is JObject eo)) { keptOrders.Add(t.DeepClone()); continue; }
                    var e = (JObject)eo.DeepClone();
                    bool usable = true;
                    foreach (string side in new[] { "stays", "moves" })
                    {
                        if (!(e[side] is JArray refs)) continue;
                        var keptRefs = new JArray(refs.Where(r => r.Type != JTokenType.String || RebarCrossing.RefDeclared((string)r, ids))
                                                      .Select(r => r.DeepClone()));
                        e[side] = keptRefs;
                        if (keptRefs.Count == 0) usable = false;
                    }
                    if (usable) keptOrders.Add(e);
                }
                if (keptOrders.Count == 0) copy.Remove("crossing_order");
                else co["orders"] = keptOrders;
            }
            return copy;
        }

        private static string KeyOf(Host h, string by) =>
            by == ByLevel ? h.Level
                          : (string.IsNullOrEmpty(h.Category) || string.IsNullOrEmpty(h.TypeName) ? null : h.Category + ":" + h.TypeName);

        private static Plan Refuse(Plan p, string code, string why) { p.Ok = false; p.Code = code; p.Why = why; return p; }
    }
}
