// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF of horizun_wall_sweep: which fields each operation takes,
// and the geometric test a placed sweep has to pass.
//
// INFORME_P6 6.7: no typed command created a wall sweep, so a baseboard
// (guardaescoba) had no instance anyone could check. A sweep is a TYPE (its
// profile and material live there - WallSweep.Create ignores the ones in
// WallSweepInfo, RevitAPI.xml 2023) placed on a wall at a distance from its base
// or top, on one side. Each is said, never defaulted where it changes the result:
// which side of the wall a baseboard goes on is the modeller's call.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class WallSweepRules
    {
        private static readonly string[] Common =
            { "target_document", "units", "operation", "dry_run", "confirmation_token", "transaction_name", "idempotency_key" };

        public static readonly Dictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["read"] = new[] { "element_id", "host_id" },
            ["create_type"] = new[] { "source_type_id", "new_name", "profile_id", "material_id" },
            ["place"] = new[] { "type_id", "wall_ids", "height", "measured_from", "side", "wall_offset" },
            ["place_edge"] = HostedEdgeRules.PlaceEdgeFields
        };

        public const int MaxWalls = 200;

        /// <summary>Null when the request is well formed for its operation; otherwise why not.</summary>
        public static string Validate(JObject request)
        {
            if (request == null) return "parameters must be a JSON object.";
            string op = (request.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            if (!Fields.TryGetValue(op, out string[] own))
                return "operation must be one of " + string.Join(", ", Fields.Keys) + " for horizun_wall_sweep.";
            var allowed = new HashSet<string>(Common.Concat(own), StringComparer.Ordinal);
            foreach (JProperty p in request.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to operation '" + op + "'. Nothing ran.";
            if (op == "create_type")
            {
                if (request["source_type_id"]?.Type != JTokenType.Integer) return "source_type_id (an existing wall sweep or reveal TYPE to duplicate) is required.";
                if (string.IsNullOrWhiteSpace(request.Value<string>("new_name"))) return "new_name is required and cannot be blank.";
                if (request["profile_id"]?.Type != JTokenType.Integer) return "profile_id (a loaded Profile family type) is required: the type's profile is what the sweep draws.";
                if (request["material_id"] != null && request["material_id"].Type != JTokenType.Integer) return "material_id must be an integer element id.";
            }
            if (op == "place")
            {
                if (request["type_id"]?.Type != JTokenType.Integer) return "type_id (a wall sweep or reveal type) is required.";
                if (!(request["wall_ids"] is JArray walls) || walls.Count == 0 || walls.Count > MaxWalls || walls.Any(w => w.Type != JTokenType.Integer))
                    return "wall_ids must list 1.." + MaxWalls + " wall element ids.";
                if (walls.Select(w => w.Value<long>()).Distinct().Count() != walls.Count) return "wall_ids names the same wall twice.";
                if (request["height"] == null || (request["height"].Type != JTokenType.Integer && request["height"].Type != JTokenType.Float))
                    return "height (distance from the wall's base, or top with measured_from=top, in units) is required.";
                string side = request.Value<string>("side");
                if (side == null) return "side is required: exterior or interior - which face of the wall the sweep goes on is not guessed.";
                if (side != "exterior" && side != "interior") return "side must be exterior or interior.";
                string from = request.Value<string>("measured_from");
                if (from != null && from != "base" && from != "top") return "measured_from must be base or top.";
                if (request["wall_offset"] != null && request["wall_offset"].Type != JTokenType.Integer && request["wall_offset"].Type != JTokenType.Float)
                    return "wall_offset must be a number.";
            }
            if (op == "place_edge") return HostedEdgeRules.Validate(request);
            if (op == "read" && request["element_id"] != null && request["element_id"].Type != JTokenType.Integer)
                return "element_id must be a wall's integer id.";
            if (op == "read" && request["host_id"] != null)
            {
                if (request["host_id"].Type != JTokenType.Integer) return "host_id must be a floor's or roof's integer id.";
                if (request["element_id"] != null) return "read takes element_id (a wall's sweeps) or host_id (a floor's or roof's edges and the slab edge, fascia and gutter types), not both.";
            }
            return null;
        }

        /// <summary>
        /// The sweep's body stands at the height asked: the target elevation lies within its vertical extent (a
        /// profile is drawn from its insertion point, so the body starts, ends or straddles it).
        /// </summary>
        public static bool BodyAtHeight(double minZ, double maxZ, double targetZ, double toleranceFeet)
            => targetZ >= minZ - toleranceFeet && targetZ <= maxZ + toleranceFeet;
    }
}
