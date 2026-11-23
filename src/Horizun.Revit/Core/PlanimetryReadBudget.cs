using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class PlanimetryReadBudget
    {
        internal static bool TryResolve(JToken seconds, int? legacyMilliseconds, out int milliseconds, out JObject assumption)
        {
            assumption = null;
            milliseconds = 0;
            if (seconds != null && seconds.Type != JTokenType.Null)
            {
                if (seconds.Type != JTokenType.Integer || !int.TryParse(seconds.ToString(), out int value) || value < 1 || value > 120)
                    return false;
                milliseconds = value * 1000;
                return true;
            }
            milliseconds = CooperativeRead.BoundedPlanimetryBudget(legacyMilliseconds);
            if (!legacyMilliseconds.HasValue)
                assumption = AssumptionNotes.Row("time_budget_s=20", "No time budget was supplied.", "Set time_budget_s (1–120).");
            return true;
        }

        internal sealed class Interrupted : Exception { }

        internal static void Checkpoint()
        {
            if (!CooperativeRead.AmbientContinue()) throw new Interrupted();
        }

        internal static IEnumerable<T> CheckEach<T>(IEnumerable<T> source)
        {
            using (var items = source.GetEnumerator())
            {
                while (true)
                {
                    if (CooperativeRead.Ambient?.Poll() == false) throw new Interrupted();
                    if (!items.MoveNext()) yield break;
                    Checkpoint();
                    yield return items.Current;
                }
            }
        }

        internal static CommandResult Read(CooperativeRead.Scope budget, Func<JArray, CommandResult> read,
            Func<JArray, JObject> interruptedPayload)
        {
            var rows = new JArray();
            using (CooperativeRead.Install(budget))
            {
                CommandResult result;
                try { result = read(rows); }
                catch (Interrupted) { result = CommandResult.Ok(interruptedPayload(rows)); }
                if (result.Data is JObject payload) StampPartial(payload, budget);
                return result;
            }
        }

        internal static void StampPartial(JObject result, CooperativeRead.Scope budget)
        {
            // Closing a read does not start a new unit. Using exactly the cap is allowed.
            if (budget.Poll(checkUnitCap: false)) return;
            JObject report = budget.Report();
            if (result["cooperative"] is JObject cooperative)
            {
                foreach (JProperty field in report.Properties()) cooperative[field.Name] = field.Value;
            }
            else result["cooperative"] = report;
            result["partial"] = true;
            result["coverage_complete"] = false;
            result["partial_reason"] = budget.StoppedBecause;
            if ((bool?)result["clean"] == true)
            {
                result["clean"] = false;
                result["clean_note"] = "The read stopped before complete coverage could be confirmed.";
            }
            if (result["matched_total"] != null) result["matched_total"] = JValue.CreateNull();
            if (result["unmatched_ids"] != null) result["unmatched_ids"] = JValue.CreateNull();
            if (result["truncated"] != null) result["truncated"] = true;
            // Keep the page cursor when it is the only way to continue.
            if (result["next_cursor"]?.Type == JTokenType.Null ||
                !string.IsNullOrWhiteSpace((string)result["cooperative"]["resume_cursor"]))
                result["next_cursor"]?.Parent?.Remove();
            result["result_set_fingerprint"]?.Parent?.Remove();
        }
    }
}
