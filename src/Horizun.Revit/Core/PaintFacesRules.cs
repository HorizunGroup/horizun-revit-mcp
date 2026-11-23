using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PaintFacesRules
    {
        public const int MaxFaces = 200;
        public static string Validate(JObject request)
        {
            if (request == null) return "parameters must be an object.";
            var allowed = new HashSet<string>(new[] { "operation", "target_document", "units", "dry_run", "confirmation_token", "transaction_name", "idempotency_key", "pairs" });
            foreach (JProperty p in request.Properties()) if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to paint_faces.";
            if (!string.Equals((string)request["operation"], "paint_faces", StringComparison.OrdinalIgnoreCase)) return "operation must be paint_faces.";
            if (string.IsNullOrWhiteSpace((string)request["target_document"])) return "target_document is required.";
            if (!(request["pairs"] is JArray pairs) || pairs.Count == 0 || pairs.Count > MaxFaces) return "pairs must contain 1..200 face actions.";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken token in pairs)
            {
                if (!(token is JObject row)) return "each pairs entry must be an object.";
                foreach (JProperty p in row.Properties())
                    if (p.Name != "element_id" && p.Name != "face_reference" && p.Name != "action" && p.Name != "material_id") return "Unknown face action field: " + p.Name;
                if (row["element_id"]?.Type != JTokenType.Integer || (long)row["element_id"] <= 0) return "element_id must be a positive integer.";
                if (row["face_reference"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)row["face_reference"])) return "face_reference must be a stable face reference.";
                if (!seen.Add((string)row["face_reference"])) return "pairs names the same face twice.";
                string action = (string)row["action"];
                if (action != "paint" && action != "remove") return "face action must be paint or remove.";
                if (action == "paint" && (row["material_id"]?.Type != JTokenType.Integer || (long)row["material_id"] <= 0)) return "paint requires a positive material_id.";
                if (action == "remove" && row["material_id"] != null) return "remove does not accept material_id.";
            }
            return null;
        }

        public static bool Matches(string action, long? expectedMaterial, bool? painted, long? measuredMaterial)
        {
            if (!painted.HasValue) return false;
            if (action == "remove") return !painted.Value;
            return action == "paint" && painted.Value && expectedMaterial.HasValue && measuredMaterial == expectedMaterial;
        }
    }
}
