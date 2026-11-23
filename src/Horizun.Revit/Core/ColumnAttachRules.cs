using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// horizun_transform_elements column_attach / column_detach: joining the base or the top of a structural
    /// column to a beam, brace, floor, roof or ceiling (Revit's ColumnAttachment). No Revit calls.
    /// </summary>
    public static class ColumnAttachRules
    {
        public const string Attach = "column_attach";
        public const string Detach = "column_detach";

        public static bool IsColumnOperation(string operation) =>
            string.Equals(operation, Attach, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(operation, Detach, StringComparison.OrdinalIgnoreCase);

        /// <summary>Revit's baseOrTop: 0 attaches the base, 1 the top. -1 for anything else.</summary>
        public static int EndIndex(string columnEnd)
        {
            switch ((columnEnd ?? "").Trim().ToLowerInvariant())
            {
                case "base": return 0;
                case "top": return 1;
                default: return -1;
            }
        }

        /// <summary>Which batch shapes are accepted: every operation is a column operation, or none is.</summary>
        public static bool BatchIsPure(IEnumerable<string> operations)
        {
            var list = operations.ToList();
            return list.All(IsColumnOperation);
        }

        private static readonly string[] Common = { "operation", "element_ids", "unique_ids", "expected_category", "expected_type", "identity", "column_end" };

        /// <summary>Null when the operation is well formed; otherwise why not. Nothing is written either way.</summary>
        public static string Validate(JObject op)
        {
            if (op == null) return "entry is not an object";
            string name = (op.Value<string>("operation") ?? "").ToLowerInvariant();
            if (!IsColumnOperation(name)) return "operation must be column_attach or column_detach.";
            var allowed = new HashSet<string>(Common, StringComparer.Ordinal);
            if (name == Attach) allowed.Add("attachment_target_id");
            foreach (JProperty p in op.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to " + name;
            if (EndIndex(op.Value<string>("column_end")) < 0)
                return "column_end must be 'base' or 'top': which end of the column is " + (name == Attach ? "attached." : "detached.");
            var ids = op["element_ids"] as JArray;
            if (ids == null || ids.Count == 0 || ids.Count > 2000 || ids.Any(t => t.Type != JTokenType.Integer))
                return "element_ids must contain 1..2000 integer ids of structural columns.";
            if (ids.Select(t => t.Value<long>()).Distinct().Count() != ids.Count) return "element_ids names the same column twice.";
            if (name == Attach)
            {
                if (op["attachment_target_id"]?.Type != JTokenType.Integer || op.Value<long>("attachment_target_id") <= 0)
                    return "attachment_target_id must be the id of the beam, brace, floor, roof or ceiling the column is attached to.";
            }
            return null;
        }
    }
}
