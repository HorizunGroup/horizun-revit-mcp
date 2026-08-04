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
                DocumentGate.RecordResolvedPlan(GraphPlan(app, gate, rows, actions));
                DocumentGate.StampConfirmation(data, gate, Name, planHash, true,
                    "one token authorizes this exact ordered graph, AND it is bound to what each independent " +
                    "action's own rehearsal resolved: the apply re-rehearses them (read-only) and refuses as a " +
                    "stale plan if any resolves differently. Actions that only resolve after a creation are " +
                    "protected by their own validation inside the group. External I/O and arbitrary code are " +
                    "never allowed inside a plan.");
                return CommandResult.Ok(data);
            }

            // ---- The materialised plan, RECOMPUTED: re-rehearse every independent action
            // read-only before anything runs. Inside the confirmed group the children's
            // own plan checks degrade to a document-identity check (EnterConfirmedAtomicPlan),
            // so this is the moment the element-level guarantee is enforced for a graph.
            // The cost is running each child's dry run twice per apply; the alternative is
            // an approval that froze the words of the graph and nothing it resolved to.
            var recheck = new JArray();
            {
                var known = new Dictionary<string, JToken>(StringComparer.Ordinal);
                for (int i = 0; i < actions.Count; i++)
                {
                    JObject action = (JObject)actions[i];
                    string key = action.Value<string>("key");
                    JObject supplied = (JObject)action["arguments"];
                    if (PlanReferences.HasReference(supplied))
                    {
                        string resolveError;
                        JToken resolvedArgs = PlanReferences.Resolve(supplied, known, out resolveError);
                        if (resolveError != null)
                        {
                            recheck.Add(new JObject { ["index"] = i, ["key"] = key, ["tool"] = action.Value<string>("tool"),
                                ["status"] = "deferred_until_execution", ["reason"] = resolveError });
                            continue;
                        }
                        supplied = (JObject)resolvedArgs;
                    }
                    JObject childArgs = ChildArguments(supplied, gate, true);
                    CommandResult again = _resolve(action.Value<string>("tool")).Execute(app, childArgs.ToString(Formatting.None));
                    recheck.Add(ResultRow(i, key, action.Value<string>("tool"), again, "rehearsed"));
                    if (!again.Success)
                        return CommandResult.Fail("Pre-apply rehearsal failed at action '" + key + "': " + again.Error +
                            ". Nothing ran - the model has moved since this graph was approved.");
                    known[key] = ToToken(again.Data);
                }
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash,
                                                                     GraphPlan(app, gate, recheck, actions), null);
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

        /// <summary>
        /// The graph's materialised plan, built identically from the rehearsal rows and
        /// from the pre-apply recheck rows. One element per action; the fact that binds is
        /// the CHILD's own plan fingerprint where the child materialises one - the same
        /// mechanism, composed. A child that does not (or a row deferred behind a
        /// reference) contributes its status instead, so wired and unwired children are
        /// distinguishable in what the token covers.
        /// </summary>
        private static Core.ResolvedPlan GraphPlan(UIApplication app, GateResult gate, JArray rows, JArray actions)
        {
            var plan = new Core.ResolvedPlan
            {
                Command = "horizun_execute_plan",
                DocumentKey = gate.Fingerprint,
                RevitVersion = SafeVersion(app),
                DocumentFingerprint = gate.Identity?.FingerprintDigest()
            };
            for (int i = 0; i < rows.Count; i++)
            {
                JObject row = rows[i] as JObject;
                if (row == null) continue;
                string childFp = null;
                try { childFp = (string)row["data"]?["plan_resolved"]?["fingerprint"]; } catch { }
                plan.Elements.Add(new Core.PlannedElement
                {
                    UniqueId = "action:" + (row.Value<int?>("index") ?? i),
                    Category = row.Value<string>("tool"),
                    Action = Core.PlannedAction.Modify,
                    BeforeValues = new Dictionary<string, string>
                    {
                        { "key", row.Value<string>("key") ?? "" },
                        { "child", childFp ?? (row.Value<string>("status") == "deferred_until_execution"
                                                  ? "deferred" : "no_child_plan") }
                    }
                });
            }
            return plan;
        }

        private static string SafeVersion(UIApplication app)
        {
            try { return app?.Application?.VersionNumber; } catch { return null; }
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
