// -----------------------------------------------------------------------------
// Horizun Revit MCP - ONE PIECE of a large requirement set (acero 3.0, L10).
// Original Horizun code. Reads the model; writes nothing.
//
// horizun_plan_reinforcement, horizun_apply_reinforcement and
// horizun_audit_reinforcement accept `chunk: { by, hosts_per_piece, piece_index,
// partition_sha256 }`. This reads the host inventory the set's selectors reach - by the
// resolver's OWN host selection, so the cut sees exactly the hosts a whole-set run would
// - cuts it with RequirementSetChunks (Core, tested without Revit) and hands the command
// the piece to run. A building of tens of thousands of bars then becomes N calls a caller
// submits one by one with horizun_submit_job, each holding Revit for one piece.
//
// The inventory holds, per element: id, BuiltInCategory name, type name, Mark and level
// name - the five things a selector filters on, read exactly as ReinforcementResolver
// reads them. For a selector naming element_ids, every id that resolves is in it BEFORE
// the type, Mark and level filters, so the cut applies the same filters the resolver does
// rather than mistaking a filtered-out id for an unknown one.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class ChunkOutcome
    {
        public bool Ok;
        public CommandResult Refusal;
        public StructuralRequirementSet PieceSet;
        public JObject Chunking;
        /// <summary>The hosts of the piece: what an audit of one piece may call its own.</summary>
        public HashSet<long> PieceHosts = new HashSet<long>();
    }

    internal static class ReinforcementChunking
    {
        /// <summary>
        /// Null when the request carries no `chunk` - the command then runs exactly as before.
        /// `partitionRequired` is true on the write path.
        /// </summary>
        public static ChunkOutcome Resolve(Document doc, JObject request, JObject setJson, StructuralRequirementSet set,
                                           bool partitionRequired)
        {
            JToken chunk = request["chunk"];
            if (chunk == null) return null;

            if ((request["host_ids"] as JArray)?.Count > 0)
                return Refuse(RequirementSetChunks.CodeBadRequest,
                    "chunk and host_ids together are refused: a piece already names its hosts, and intersecting it with " +
                    "another list would run a piece of a cut nobody planned. Send one or the other. Nothing was run.");

            string code, error;
            RequirementSetChunks.Request req = RequirementSetChunks.ReadRequest(chunk, out code, out error);
            if (req == null) return Refuse(code, error);

            List<RequirementSetChunks.Host> hosts = Inventory(doc, set);
            RequirementSetChunks.Selection sel = RequirementSetChunks.Select(setJson, hosts, req, partitionRequired);
            if (!sel.Ok) return Refuse(sel.Code, sel.Why, sel.ToJson());

            StructuralRequirementSet piece = StructuralRequirementSet.Load(sel.Piece.RequirementSet);
            if (!piece.Ok)
                return Refuse(RequirementSetChunks.CodeBadRequest,
                    "piece " + sel.Piece.Index + " of the cut did not load as a requirement set (" + piece.Error + "). " +
                    "That is a defect of the cut, not of the set; nothing was run.");

            JObject chunking = sel.ToJson();
            chunking["inventory_hosts_read"] = hosts.Count;
            return new ChunkOutcome { Ok = true, PieceSet = piece, Chunking = chunking,
                                      PieceHosts = new HashSet<long>(sel.Piece.ElementIds) };
        }

        /// <summary>Every element any selector of the set reaches, with the five fields a selector filters on.</summary>
        private static List<RequirementSetChunks.Host> Inventory(Document doc, StructuralRequirementSet set)
        {
            var selectors = new List<StructuralHostSelector>();
            selectors.AddRange(set.CoverRules.Select(r => r.Host));
            selectors.AddRange(set.RebarRules.Select(r => r.Host));
            selectors.AddRange(set.StirrupZoneRules.Select(r => r.Host));
            selectors.AddRange(set.MatRules.Select(r => r.Host));
            selectors.AddRange(set.SectionRules.Select(r => r.Host));

            var byId = new Dictionary<long, RequirementSetChunks.Host>();
            foreach (StructuralHostSelector s in selectors.Where(x => x != null))
            {
                List<long> unresolved;
                foreach (Element e in ReinforcementResolver.Hosts(doc, s, null, out unresolved)) Add(doc, e, byId);
                foreach (long id in s.ElementIds)
                {
                    if (byId.ContainsKey(id) || !Rid.CanRepresent(id)) continue;
                    Element e = doc.GetElement(Rid.Make(id));
                    if (e != null) Add(doc, e, byId);
                }
            }
            return byId.Values.OrderBy(h => h.ElementId).ToList();
        }

        private static void Add(Document doc, Element e, Dictionary<long, RequirementSetChunks.Host> byId)
        {
            long id = Rid.Value(e.Id);
            if (byId.ContainsKey(id)) return;
            string category = null, typeName = null;
            try { category = e.Category == null ? null : ((BuiltInCategory)Rid.Value(e.Category.Id)).ToString(); } catch { }
            try { typeName = (doc.GetElement(e.GetTypeId()) as ElementType)?.Name; } catch { }
            byId[id] = new RequirementSetChunks.Host
            {
                ElementId = id,
                Category = category,
                TypeName = typeName,
                Mark = ReinforcementResolver.MarkOf(e),
                Level = ReinforcementResolver.LevelNameOf(doc, e)
            };
        }

        private static ChunkOutcome Refuse(string code, string why, JObject detail = null)
        {
            JObject d = detail ?? new JObject { ["ok"] = false, ["code"] = code, ["why"] = why };
            return new ChunkOutcome
            {
                Ok = false,
                Refusal = CommandResult.FailWithDetail(code + ": " + why, new JObject { ["chunk"] = d, ["code"] = code })
            };
        }
    }
}
