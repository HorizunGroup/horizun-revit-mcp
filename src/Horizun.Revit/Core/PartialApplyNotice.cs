// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// A rehearsal that is clean (it resolved everything it will do, so a direct call gets its token) but
// already knows its apply will be PARTIAL against what was asked: create_schedule with a requested field
// Revit does not offer for the category. Product rule: a datum that is missing does not block - the field
// is a SUPUESTO and the direct call goes on. A PLAN is different: execute_plan and apply_corrections bind
// one confirmation to every step, and require every step to apply in full.
//
// No Revit types: linked into the Core tests.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PartialApplyNotice
    {
        public const string Key = "apply_will_be_partial";
        public const string WhyKey = "apply_will_be_partial_why";

        /// <summary>Marks <paramref name="payload"/> as a rehearsal whose apply will be partial, and why.</summary>
        public static JObject Stamp(JObject payload, string why)
        {
            if (payload == null) return null;
            payload[Key] = true;
            payload[WhyKey] = why ?? "";
            return payload;
        }

        /// <summary>True only for an explicit boolean true under <see cref="Key"/>.</summary>
        public static bool Announced(object data)
            => data is JObject o && o[Key] is JValue v && v.Type == JTokenType.Boolean && (bool)v.Value;

        /// <summary>
        /// Why a plan step that rehearsed cleanly is still not applied by the plan; null when it was not announced.
        /// </summary>
        public static string PlanRefusal(string tool, object data)
        {
            if (!Announced(data)) return null;
            string why = (data as JObject)?[WhyKey]?.ToString();
            return "'" + tool + "' rehearsed cleanly but announced that its apply will be partial" +
                   (string.IsNullOrEmpty(why) ? "" : " (" + why + ")") +
                   ". Plans require every step to apply in full. Fix the step and rehearse the plan again, " +
                   "or run the command on its own to accept the declared partial result.";
        }
    }
}
