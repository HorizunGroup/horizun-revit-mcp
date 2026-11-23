using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class FieldCallRules
    {
        public static JObject CloudOpenRetry(OpenFacts facts, OpenIntent intent, OpenVerdict verdict)
        {
            return !verdict.Ok && facts.IsCloud && facts.IsCentral != false && !intent.Detach && !intent.OpenCentral &&
                verdict.Refusal.StartsWith("REFUSING TO OPEN: a model in ACC", StringComparison.Ordinal)
                ? new JObject { ["detach"] = true } : null;
        }
        public static string SaveOperation(JObject request) => request.Value<string>("operation") ?? "save_local";
        public static JObject CaptureSheetRetry(IEnumerable<long> sheets)
        {
            var ids = sheets.Distinct().ToArray();
            return ids.Length == 1 ? new JObject { ["view_id"] = ids[0] } : null;
        }
    }
}
