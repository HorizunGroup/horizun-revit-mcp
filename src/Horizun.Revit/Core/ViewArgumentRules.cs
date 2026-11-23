// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// E2 (field report, 2026-10-18): horizun_manage_views create_floor_plan accepted start/end
// and dropped them in silence, so a plan meant to be cropped came out with the whole
// building. An argument that would be ignored is refused instead.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ViewArgumentRules
    {
        private static readonly HashSet<string> PlanOperations = new HashSet<string>(StringComparer.Ordinal)
        {
            "create_floor_plan", "create_ceiling_plan", "create_structural_plan", "create_area_plan"
        };

        // Section-box / crop arguments other operations take, that a plan creation does not.
        private static readonly string[] SectionBoxArguments = { "start", "end", "depth", "height", "crop", "crop_box", "box", "far_clip" };

        /// <summary>The first argument a plan creation would ignore, or null.</summary>
        public static string PlanIgnoredArgument(string operation, IEnumerable<string> argumentNames)
        {
            if (operation == null || !PlanOperations.Contains(operation)) return null;
            var names = new HashSet<string>(argumentNames ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            return SectionBoxArguments.FirstOrDefault(names.Contains);
        }

        private static readonly HashSet<string> NamedViews = new HashSet<string>(PlanOperations, StringComparer.OrdinalIgnoreCase)
        {
            "create_3d", "create_room_isometric", "create_drafting", "create_section", "create_elevation",
            "create_callout", "duplicate_view", "create_template", "create_legend"
        };

        internal static CommandResult PlanBatch(JArray actions,
            Func<int, JObject, (string Error, string UnsupportedReason)> planAction,
            Func<IEnumerable<string>> existingNames, Func<JObject, IEnumerable<string>> perspectiveNames,
            JArray errors, List<ActionOutcome> outcomes, Func<CommandResult> continueBatch)
        {
            HashSet<string> reserved = null;
            CommandResult firstConflict = null;
            for (int index = 0; index < actions.Count; index++)
            {
                var action = actions[index] as JObject;
                // Finish validating the batch even after a conflict so later capability
                // gaps remain visible while the conflict still bars rehearsal and apply.
                string scaleError = ViewScaleError(action?["view_scale"]);
                var planned = scaleError == null ? planAction(index, action) : (Error: scaleError, UnsupportedReason: (string)null);
                if (planned.Error != null)
                {
                    errors.Add(new JObject { ["index"] = index, ["error"] = planned.Error,
                        ["error_code"] = planned.UnsupportedReason == null ? "invalid_action" : "unsupported_capability" });
                    outcomes.Add(new ActionOutcome { Index = index, Error = planned.Error, UnsupportedReason = planned.UnsupportedReason });
                    continue;
                }
                if (firstConflict != null) continue;
                string operation = action.Value<string>("operation");
                bool perspective = string.Equals(operation, "create_perspective", StringComparison.OrdinalIgnoreCase);
                if (!perspective && !NamedViews.Contains(operation)) continue; // Sheets can share names.
                string requested = action.Value<string>("name");
                if (string.IsNullOrWhiteSpace(requested)) continue;
                IEnumerable<string> names = perspective ? perspectiveNames(action) : new[] { requested };
                if (reserved == null) reserved = new HashSet<string>(existingNames(), StringComparer.OrdinalIgnoreCase);
                foreach (string name in names)
                    if (!reserved.Add(name))
                    {
                        string error = "View name '" + name + "' is already in use in the document or this batch. Nothing was created.";
                        outcomes.Add(new ActionOutcome { Index = index, Error = error,
                            FallbackDenialReason = ErrorCodes.PrecondicionNoCumplida });
                        firstConflict = CommandResult.FailCoded(ErrorCodes.PrecondicionNoCumplida, error,
                            extra: new JObject { ["code"] = "view_name_conflict", ["view_name"] = name,
                                ["index"] = index, ["write_started"] = false, ["changes_applied"] = false });
                        break;
                    }
            }
            if (firstConflict == null) return continueBatch();
            if (errors.Count > 0) firstConflict.Detail["errors"] = errors;
            return FallbackDecision.Attach(firstConflict, FallbackDecision.Decide(outcomes, writeStarted: false));
        }

        private static string ViewScaleError(JToken scale)
        {
            if (scale == null) return null;
            if (scale.Type == JTokenType.Integer &&
                int.TryParse(scale.ToString(Formatting.None), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                value >= 1 && value <= 24000) return null;
            return "view_scale must be an integer in 1..24000";
        }
    }
}
