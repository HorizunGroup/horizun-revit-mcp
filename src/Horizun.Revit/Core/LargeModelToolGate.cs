using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class LargeModelToolGate
    {
        public const long ScopeThreshold = 50000;
        private static readonly HashSet<string> Tools = new HashSet<string>(StringComparer.Ordinal)
        {
            "horizun_query_model", "horizun_list_elements", "horizun_model_scan",
            "horizun_audit_model", "horizun_quantities"
        };
        public static JObject SuggestedCall(string tool, JObject args)
        {
            var copy = (JObject)args.DeepClone();
            copy["confirm_large_operation"] = true;
            return new JObject { ["tool"] = tool, ["arguments"] = copy };
        }
        public static bool Applies(string tool) => Tools.Contains(tool);
        public static LargeModelExecutionGate.Result<T> Run<T>(string tool, int? modelElements,
            IDictionary<string, long> counts, bool confirmed, Func<T> operation)
        {
            if (!Applies(tool)) throw new ArgumentException("Unsupported tool", nameof(tool));
            return LargeModelExecutionGate.Run(modelElements, counts, ScopeThreshold, confirmed, operation,
                scopeOnly: tool == "horizun_query_model");
        }
    }
}
