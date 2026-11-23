// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_health ANSWERS, always. When the call cannot reach a Revit, or Revit does
// not answer, the reply is a STATE (arrancando, ocupado, puente_inaccesible,
// sin_documento) with retry_after_ms and what to do - not an error and never an
// exception. The names and the thresholds live in HealthState (Core, shared with
// the tests); this file only decides what the server saw.
// -----------------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HealthState = Horizun.Revit.Core.HealthState;

namespace Horizun.Server
{
    internal static class HealthAnswer
    {
        internal const string ToolName = "horizun_health";

        /// <summary>Whether a Revit.exe exists on this machine. Replaceable for tests.</summary>
        internal static Func<bool> RevitProcessRunning = DefaultRevitRunning;

        private static bool DefaultRevitRunning()
        {
            Process[] found = Process.GetProcessesByName("Revit");
            bool any = found.Length > 0;
            foreach (Process p in found) p.Dispose();
            return any;
        }

        internal static bool IsHealth(JObject prms)
            => prms?["name"] != null && prms["name"].Type == JTokenType.String && (string)prms["name"] == ToolName
               && prms["task"] == null;

        /// <summary>An exception worth answering: not a protocol error, not a client's cancellation.</summary>
        internal static bool ShouldAnswer(Exception ex)
            => !(ex is McpError) && !(ex is InputRequiredException) && !(ex is OperationCanceledException);

        /// <summary>A failed health call becomes a state. A successful one passes through untouched.</summary>
        internal static JToken Apply(JToken result)
        {
            if (!(result is JObject r) || r["isError"]?.Type != JTokenType.Boolean || !(bool)r["isError"]) return result;
            JObject block = (r["content"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(b => (string)b["type"] == "text" && b["text"]?.Type == JTokenType.String);
            string text = (string)block?["text"];
            JObject detail = r["structuredContent"] as JObject;
            // A DELIBERATE refusal stays an error: a call a person quarantined, a tool switched
            // off, a permission the profile withholds, arguments that break the contract. Those
            // are decisions about the call, not a Revit that is unreachable - turning them into
            // a "state" would tell the agent to wait for something that will not change.
            // Several Revits with the bridge and none chosen: answered with the list, not as a failure.
            if (HealthState.IsMultipleInstancesRefusal(text)) return BuildMultiple(text);
            switch (Horizun.Revit.Core.ErrorCodes.Classify(text, detail).Code)
            {
                case Horizun.Revit.Core.ErrorCodes.LlamadaEnCuarentena:
                case Horizun.Revit.Core.ErrorCodes.HerramientaDesactivada:
                case Horizun.Revit.Core.ErrorCodes.PermisoInsuficiente:
                case Horizun.Revit.Core.ErrorCodes.EsquemaInvalido:
                    return result;
            }
            return Build(text, detail);
        }

        internal static JToken FromException(Exception ex)
        {
            JObject detail = ex.Data["horizun_transport_detail"] as JObject;
            return Apply(McpResult.ExceptionError(ToolName, ex, detail, bridge: true));
        }

        /// <summary>The live instances, each asked for its build and active document. Replaceable for tests.</summary>
        internal static Func<JArray> ListInstances = DefaultInstances;

        internal const int InstanceProbeTimeoutMs = 4000;

        private static JArray DefaultInstances()
        {
            // Serial probes took four seconds per instance and exceeded the client deadline.
            var all = PipeClient.ListAll().Where(x => x.ProcessAlive).OrderBy(x => x.Year).ThenBy(x => x.Pid).ToArray();
            return ProbeInstances(all, ProbeInstance, InstanceProbeTimeoutMs + 500);
        }

        internal static JArray ProbeInstances(Discovered[] all, Func<Discovered, JObject> probe, int timeoutMs)
        {
            var tasks = all.Select(d => System.Threading.Tasks.Task.Run(() =>
            {
                try { return probe(d); }
                catch (Exception ex) { return InstanceFallback(d, ex.GetType().Name + ": " + ex.Message); }
            })).ToArray();
            System.Threading.Tasks.Task.WaitAll(tasks, timeoutMs);
            return new JArray(all.Select((d, i) => tasks[i].Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                ? tasks[i].Result : InstanceFallback(d, "Revit did not answer the instance probe within the deadline.")));
        }

        private static JObject InstanceFallback(Discovered d, string reason) => new JObject
        {
            ["pid"] = d.Pid, ["year"] = d.Year, ["addin_version"] = d.AddinVersion,
            ["started_utc"] = d.StartedUtc, ["active_document"] = null, ["probe_error"] = reason
        };

        internal static JObject ProbeInstance(Discovered d)
        {
            JObject row = InstanceFallback(d, "Revit did not return data.");
            try
            {
                JObject reply = PipeClient.Send(d, ToolName, new JObject(), InstanceProbeTimeoutMs, probe: true);
                JObject data = reply?["data"] as JObject;
                if (data != null)
                {
                    row.Remove("probe_error");
                    row["build"] = data["revit_build"];
                    row["active_document"] = data["active_document"];
                    row["open_documents"] = data["open_documents"];
                    row["open_documents_complete"] = data["open_documents_complete"];
                    row["no_active_document"] = data["no_active_document"];
                    row["modal_dialog_policy"] = data["modal_dialog_policy"];
                }
                else row["probe_error"] = (string)reply?["error"] ?? "Revit returned no data.";
            }
            catch (Exception ex) { row["probe_error"] = ex.GetType().Name + ": " + ex.Message; }
            return row;
        }

        private static JToken BuildMultiple(string text)
        {
            JArray instances;
            try { instances = ListInstances(); } catch { instances = new JArray(); }
            JObject body = HealthState.MultipleInstances(InstanceRouting.Candidates(instances), text);
            body["modal_dialog_policy"] = UnreachableModalPolicy();
            body["server"] = Protocol.ProvenanceStamp.Compact();
            body["edition"] = Horizun.Contracts.Edition.Name;
            body["last_error"] = new JObject { ["message"] = text, ["scope"] = "this health request" };
            body["modal_dialog_observation"] = "unknown";
            StampServerFacts(body, instances, null);
            return McpResult.Structured(body, body.ToString(Formatting.Indented));
        }

        private static JObject Build(string text, JObject detail)
        {
            bool? running;
            try { running = RevitProcessRunning(); } catch { running = null; }
            if (string.IsNullOrWhiteSpace(text)) text = "Revit did not answer health; the bridge returned no error message.";
            JObject body = HealthState.Unavailable(text, detail, running);
            body["edition"] = Horizun.Contracts.Edition.Name;
            body["modal_dialog_policy"] = UnreachableModalPolicy();
            body["server"] = Protocol.ProvenanceStamp.Compact();
            JArray described = null;
            try { described = DescribeInstances(PipeClient.ListAll()); body["instances"] = described; }
            catch { body["instances"] = null; }
            body["last_error"] = new JObject
            {
                ["message"] = text,
                ["detail"] = detail?.DeepClone(),
                ["scope"] = "this health request"
            };
            body["active_document"] = null;
            body["modal_dialog_observation"] = detail?["modal_dialog"] == null ? "unknown" : "reported_by_transport";
            StampServerFacts(body, described, detail?["modal_dialog"] != null);
            return McpResult.Structured(body, body.ToString(Formatting.Indented));
        }

        /// <summary>
        /// Field 2026-11 (Colon): several Revits open, one pinned with horizun_target, and the pid changes at every
        /// restart. A health that answers from the pinned one said only a count (answered_by.other_live_instances);
        /// this names the others and what to do. Null when this Revit is the only live one.
        /// </summary>
        internal static JObject OtherInstancesNotice(System.Collections.Generic.IEnumerable<Discovered> live, int answeredPid)
        {
            var others = (live ?? Enumerable.Empty<Discovered>())
                .Where(d => d.ProcessAlive && d.Pid != answeredPid)
                .OrderBy(d => d.Year).ThenBy(d => d.Pid).ToArray();
            if (others.Length == 0) return null;
            return new JObject
            {
                ["count"] = others.Length,
                ["answered_pid"] = answeredPid,
                ["instances"] = new JArray(others.Select(d => new JObject
                    { ["pid"] = d.Pid, ["year"] = d.Year, ["started_utc"] = d.StartedUtc })),
                ["warning"] = others.Length + " other Revit instance(s) are running (" +
                    string.Join(", ", others.Select(d => "Revit " + d.Year + " pid " + d.Pid)) +
                    "). This answer comes from pid " + answeredPid + ". A pid is not stable: it changes every time a " +
                    "Revit restarts, so a target chosen earlier may now name nothing or another session. After any " +
                    "restart call horizun_target again and check answered_by in each reply before writing."
            };
        }

        internal static void StampSuccess(JObject health, Discovered answered, System.Collections.Generic.IEnumerable<Discovered> discovered)
        {
            var registrations = (discovered ?? Enumerable.Empty<Discovered>())
                .Where(d => d.Pid != answered?.Pid).ToList();
            if (answered != null) registrations.Add(answered);
            health["instances"] = DescribeInstances(registrations, answered?.Pid);
            if (answered != null)
            {
                var notice = OtherInstancesNotice(registrations, answered.Pid);
                if (notice != null) health["other_revit_instances"] = notice;
            }
            HealthState.StampSuccess(health);
        }

        /// <summary>
        /// The live instances as the SERVER can describe them with no help from Revit: the
        /// registration each one published (add-in version, contract hash, start time), whether
        /// its process is alive, and whether its contract hash equals this server's. A missing
        /// hash is null ("not published"), never false ("differs").
        /// </summary>
        internal static JArray DescribeInstances(System.Collections.Generic.IEnumerable<Discovered> all, int? answeredPid = null)
        {
            string serverHash = Horizun.Contracts.Contract.Hash;
            return new JArray((all ?? Enumerable.Empty<Discovered>())
                .Where(d => d.ProcessAlive || d.Pid == answeredPid)
                .OrderBy(d => d.Year).ThenBy(d => d.Pid)
                .Select(d => new JObject
                {
                    ["pid"] = d.Pid, ["year"] = d.Year, ["addin_version"] = d.AddinVersion, ["started_utc"] = d.StartedUtc,
                    ["process_alive"] = d.ProcessAlive || d.Pid == answeredPid,
                    ["contract_hash"] = string.IsNullOrWhiteSpace(d.ContractHash) ? null : d.ContractHash,
                    ["contract_matches_server"] = string.IsNullOrWhiteSpace(d.ContractHash)
                        ? (JToken)JValue.CreateNull()
                        : (JToken)string.Equals(d.ContractHash, serverHash, StringComparison.OrdinalIgnoreCase),
                    ["choose_with"] = "horizun_target pid=" + d.Pid
                }));
        }

        /// <summary>
        /// Says what this answer is built from. `verified` lists only facts the server checked
        /// itself, each with how; `not_verified` lists what only a running Revit could tell and
        /// that nobody told: it is never filled in from the absence of an answer.
        /// </summary>
        private static void StampServerFacts(JObject body, JArray instances, bool? modalReportedByTransport)
        {
            var verified = new JArray
            {
                new JObject { ["fact"] = "server_build", ["how"] = "this server's own provenance stamp" },
                new JObject { ["fact"] = "live_instances", ["count"] = instances?.Count ?? 0,
                              ["how"] = "discovery files published by Revit, process liveness checked" }
            };
            if (instances != null)
                foreach (JObject i in instances.OfType<JObject>())
                    verified.Add(new JObject { ["fact"] = "instance_registration", ["pid"] = i["pid"],
                                               ["how"] = "discovery file and process check; contract hash compared with this server's" });
            body["verified"] = verified;
            var not = new JArray { "active_document", "command_in_progress", "last_checkpoint" };
            if (modalReportedByTransport != true) not.Add("modal_dialog");
            body["not_verified"] = not;
            body["last_server_error"] = ServerFacts.LastFailure();
        }

        private static JObject UnreachableModalPolicy()
        {
            JObject policy = Horizun.Revit.Core.Settings.ModalDialogPolicy();
            policy["auto_close_external_tool_failure"] = null;
            policy["save_reminder_auto_close"] = null;
            policy["auto_close_save_reminder"] = null;
            policy["reason"] = "Revit has not answered; its process session override is unknown.";
            return policy;
        }
    }
}
