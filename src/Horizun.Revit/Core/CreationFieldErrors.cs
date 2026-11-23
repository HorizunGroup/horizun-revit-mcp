using System;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class CreationFieldErrors
    {
        internal static JObject Describe(string kind, int index, Tuple<string, string> foreign)
        {
            return new JObject
            {
                ["error_code"] = ErrorCodes.EsquemaInvalido,
                ["field_path"] = "elements[" + index + "]." + foreign.Item1,
                ["suggested_field"] = foreign.Item2,
                ["message"] = ToolInputRules.CreationFieldMessage(kind, foreign.Item1, foreign.Item2),
                ["contract_uri"] = "horizun://contract/tools/horizun_create_elements/" + kind,
                ["write_started"] = false,
                ["changes_applied"] = false
            };
        }

        // Keep the batch diagnostics and expose the first schema error to callers
        // that read only the top-level fields shared with the single-row refusal.
        internal static bool StampBatch(JObject result, JArray errors)
        {
            var first = errors.OfType<JObject>().FirstOrDefault(row => row["field_path"] != null);
            if (first == null) return false;
            foreach (string key in new[] { "error_code", "field_path", "suggested_field", "message", "contract_uri", "write_started", "changes_applied" })
                result[key] = first[key].DeepClone();
            return true;
        }
    }
}
