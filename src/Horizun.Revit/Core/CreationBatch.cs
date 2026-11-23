using System;
using System.Collections.Generic;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    // Keys an expansion writes on the rows it produces; a caller row carrying one is refused.
    internal static class ExpansionKeys
    {
        // Caller elements index of a row an architectural recipe produced or moved.
        internal const string RecipeFrom = "recipe_from";
    }

    // Revit reference resolution and transactions stay in the command. This class
    // owns row validation and the complete rehearsal response sent to the pipe.
    internal sealed class CreationBatch<T> where T : class
    {
        internal readonly List<T> Plans = new List<T>();
        internal readonly JArray Errors = new JArray();
        internal readonly List<ActionOutcome> Outcomes = new List<ActionOutcome>();

        private readonly int _requested;

        private CreationBatch(int requested) { _requested = requested; }

        internal static CreationBatch<T> Plan(JArray input,
            Func<int, JObject, string, (T Plan, string Error, string Gap)> planItem)
        {
            var batch = new CreationBatch<T>(input.Count);
            for (int index = 0; index < input.Count; index++)
            {
                var item = input[index] as JObject;
                if (item == null)
                {
                    batch.RefuseRow(index, "entry is not an object");
                    continue;
                }

                int? sourceIndex = SourceIndex(item);
                string kind = (item.Value<string>("kind") ?? "").ToLowerInvariant();
                JObject publicRow = item;
                if (sourceIndex != null)
                {
                    // The expansions reject these keys on caller rows and add them to expanded rows.
                    publicRow = (JObject)item.DeepClone();
                    publicRow.Remove("enclosed_from");
                    publicRow.Remove("circuit_area_m2");
                    publicRow.Remove(ExpansionKeys.RecipeFrom);
                }
                string error = ToolInputRules.ValidateCreation(publicRow, kind, out var foreign);
                if (error != null)
                {
                    var detail = foreign == null ? null : CreationFieldErrors.Describe(kind, sourceIndex ?? index, foreign);
                    batch.RefuseRow(index, error, sourceIndex, detail);
                    continue;
                }
                var planned = planItem(index, item, kind);
                if (planned.Plan != null) batch.Plans.Add(planned.Plan);
                else batch.RefuseRow(index, planned.Error, sourceIndex, gap: planned.Gap);
            }

            return batch;
        }

        internal CommandResult CompleteRehearsal(JObject preview)
        {
            // Ok classifies the result immediately, including a rejected rehearsal.
            // Give it the row diagnostics and explicit dry_run before that boundary.
            preview["dry_run"] = true;
            preview["requested"] = _requested;
            preview["valid"] = Plans.Count;
            preview["invalid"] = Errors.Count;
            preview["errors"] = Errors;
            CreationFieldErrors.StampBatch(preview, Errors);
            ApplicationOutcome.StampRehearsal(preview, _requested, Errors.Count, 0, 0);
            return FallbackDecision.Attach(CommandResult.Ok(preview), FallbackDecision.Decide(Outcomes, writeStarted: false));
        }

        // The caller's elements index of a row an expansion produced (all_enclosed or an architectural recipe).
        internal static int? SourceIndex(JObject item) =>
            item?.Value<int?>("enclosed_from") ?? item?.Value<int?>(ExpansionKeys.RecipeFrom);

        internal void RefuseRow(int index, string error, int? sourceIndex = null, JObject detail = null, string gap = null)
        {
            var row = detail ?? new JObject();
            row["index"] = index;
            row["error"] = error;
            if (sourceIndex != null) row["elements_index"] = sourceIndex.Value;
            Errors.Add(row);
            Outcomes.Add(new ActionOutcome { Index = sourceIndex ?? index, Error = error, UnsupportedReason = gap,
                FallbackDenialReason = detail?.Value<string>("error_code") });
        }
    }
}
