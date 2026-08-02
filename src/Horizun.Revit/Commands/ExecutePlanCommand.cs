// -----------------------------------------------------------------------------
// Compose verified typed commands into one confirmed, atomic Revit operation.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ExecutePlanCommand : ICommand
    {
        private readonly Func<string, ICommand> _resolve;
        private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "horizun_write_params_verified", "horizun_delete_verified", "horizun_create_schedule",
            "horizun_set_keynote", "horizun_family_apply", "horizun_bind_shared_param",
            "horizun_create_elements", "horizun_manage_system_types", "horizun_transform_elements", "horizun_manage_views", "horizun_annotate",
            "horizun_split_floor_loops", "horizun_split_multilayer_walls", "horizun_split_multilayer_slabs",
            "horizun_ungroup_and_mark", "horizun_regroup_by_param", "horizun_copy_slab_elevations",
            "horizun_embed_floors_in_toposolid", "horizun_grade_toposolid_around_floors",
            "horizun_rectangularize_walls"
        };

        public ExecutePlanCommand(Func<string, ICommand> resolve) { _resolve = resolve; }
        public string Name => "horizun_execute_plan";
        public string Description => "Run an ordered graph of typed Revit writes as one atomic, verified plan.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            JArray actions = request["actions"] as JArray;
            if (actions == null || actions.Count == 0 || actions.Count > 100)
                return CommandResult.Fail("actions must contain 1..100 entries.");

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var errors = new JArray();
            for (int i = 0; i < actions.Count; i++)
            {
                JObject action = actions[i] as JObject;
                string key = action?.Value<string>("key");
                string tool = action?.Value<string>("tool");
                if (action == null) errors.Add(Error(i, "action is not an object"));
                else if (string.IsNullOrWhiteSpace(key)) errors.Add(Error(i, "key is required"));
                else if (!keys.Add(key)) errors.Add(Error(i, "key '" + key + "' is duplicated"));
                else if (!Allowed.Contains(tool ?? "")) errors.Add(Error(i, "tool '" + tool + "' is not allowed in an atomic plan"));
                else if (_resolve(tool) == null) errors.Add(Error(i, "tool '" + tool + "' is not installed in this add-in"));
                else if (!(action["arguments"] is JObject)) errors.Add(Error(i, "arguments must be an object"));
                else foreach (string referenced in PlanReferences.ReferenceKeys(action["arguments"]))
                    if (string.Equals(referenced, key, StringComparison.Ordinal) || !keys.Contains(referenced))
                        errors.Add(Error(i, "reference to '" + referenced + "' is not a prior action"));
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "actions");
            if (errors.Count > 0)
                return CommandResult.Fail("Invalid atomic plan; nothing ran: " + errors.ToString(Formatting.None));

            if (dryRun)
            {
                var known = new Dictionary<string, JToken>(StringComparer.Ordinal);
                var rows = new JArray();
                for (int i = 0; i < actions.Count; i++)
                {
                    JObject action = (JObject)actions[i];
                    string key = action.Value<string>("key");
                    JObject supplied = (JObject)action["arguments"];
                    if (PlanReferences.HasReference(supplied))
                    {
                        string resolveError;
                        JToken resolved = PlanReferences.Resolve(supplied, known, out resolveError);
                        if (resolveError != null)
                        {
                            rows.Add(new JObject { ["index"] = i, ["key"] = key, ["tool"] = action.Value<string>("tool"),
                                ["status"] = "deferred_until_execution", ["reason"] = resolveError });
                            continue;
                        }
                        supplied = (JObject)resolved;
                    }
                    JObject child = ChildArguments(supplied, gate, true);
                    CommandResult rehearsal = _resolve(action.Value<string>("tool")).Execute(app, child.ToString(Formatting.None));
                    JObject row = ResultRow(i, key, action.Value<string>("tool"), rehearsal, "rehearsed");
                    rows.Add(row);
                    if (!rehearsal.Success)
                        return CommandResult.Fail("Atomic plan rehearsal failed at action '" + key + "': " + rehearsal.Error +
                            ". Nothing ran. Earlier rows: " + rows.ToString(Formatting.None));
                    known[key] = ToToken(rehearsal.Data);
                }
                var data = new JObject { ["dry_run"] = true, ["transaction_status"] = "not_started",
                    ["actions"] = rows, ["note"] = "Independent actions were semantically rehearsed. References whose values only exist after creation are resolved during the confirmed atomic execution; any failure then rolls back every action." };
                DocumentGate.StampConfirmation(data, gate, Name, planHash, true,
                    "one token authorizes this exact ordered graph; external I/O and arbitrary code are never allowed inside it");
                return CommandResult.Ok(data);
            }

            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash);
            if (refusal != null) return refusal;
            string groupName = request.Value<string>("transaction_name");
            if (string.IsNullOrWhiteSpace(groupName)) groupName = "Horizun: atomic plan";
            var results = new Dictionary<string, JToken>(StringComparer.Ordinal);
            var executed = new JArray();
            using (var group = new TransactionGroup(gate.Document, groupName))
            {
                if (group.Start() != TransactionStatus.Started)
                    return CommandResult.Fail("Revit refused to start the atomic TransactionGroup. Nothing ran.");
                try
                {
                    using (DocumentGate.EnterConfirmedAtomicPlan())
                    {
                        for (int i = 0; i < actions.Count; i++)
                        {
                            JObject action = (JObject)actions[i];
                            string key = action.Value<string>("key");
                            string resolveError;
                            JObject child = PlanReferences.Resolve(action["arguments"], results, out resolveError) as JObject;
                            if (resolveError != null) throw new InvalidOperationException("action '" + key + "': " + resolveError);
                            child = ChildArguments(child, gate, false);
                            CommandResult result = _resolve(action.Value<string>("tool")).Execute(app, child.ToString(Formatting.None));
                            executed.Add(ResultRow(i, key, action.Value<string>("tool"), result, "executed"));
                            if (!result.Success) throw new InvalidOperationException("action '" + key + "' failed: " + result.Error);
                            results[key] = ToToken(result.Data);
                        }
                    }
                    Guard.Assimilate(group, groupName);
                }
                catch (Exception ex)
                {
                    if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                    return CommandResult.Fail("Atomic plan failed and EVERY action was rolled back: " + ex.Message +
                        ". Executed trace (outcomes are diagnostic only; none were retained): " + executed.ToString(Formatting.None));
                }
            }
            return CommandResult.Ok(new JObject { ["transaction_status"] = "Committed", ["transaction_name"] = groupName,
                ["actions_verified"] = executed.Count, ["actions"] = executed, ["results"] = new JObject(results) });
        }

        private static JObject ChildArguments(JObject source, GateResult gate, bool dryRun)
        {
            JObject child = source == null ? new JObject() : (JObject)source.DeepClone();
            child["target_document"] = gate.Identity.Path ?? gate.Identity.Title;
            child["target_document_title"] = gate.Identity.Title;
            child["dry_run"] = dryRun;
            child.Remove("confirmation_token"); child.Remove("idempotency_key");
            return child;
        }

        private static JObject Error(int index, string error) => new JObject { ["index"] = index, ["error"] = error };
        private static JToken ToToken(object data) => data == null ? JValue.CreateNull() :
            (data as JToken)?.DeepClone() ?? JToken.FromObject(data);
        private static JObject ResultRow(int index, string key, string tool, CommandResult result, string status) =>
            new JObject { ["index"] = index, ["key"] = key, ["tool"] = tool, ["status"] = status,
                ["success"] = result.Success, ["data"] = result.Success ? ToToken(result.Data) : JValue.CreateNull(),
                ["error"] = result.Success ? JValue.CreateNull() : new JValue(result.Error) };
    }
}
