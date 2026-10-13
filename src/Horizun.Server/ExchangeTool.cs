// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_exchange: the 3.0.0 interoperability surface that needs no Revit.
//
//   emit       wrap payload files that already exist (a ledger CSV, a BCF, quantity
//              rows, an MSPDI file) into a horizun.exchange/0 hop: payload first,
//              each copy re-hashed, manifest last (ExchangeManifest.Emit). The root
//              is LOCAL by default (%USERPROFILE%\.horizun\exchange\<project>); a CDE
//              folder is a separate, explicit publication. A wrap made here cannot
//              see how the payload was produced, so it is AT MOST
//              self_reported_verified - a `verified` declared by the caller is kept
//              as the claim and capped as the state.
//   verify     the consumer's half: delivered or not, sha256 of every file, the
//              effective state, the source model against the project registry
//              (decision D0), and may_continue - whether a downstream hop may run.
//   correction_plan_check / correction_plan_reverify
//              horizun-review's plan_correcciones_revit: which steps may run and
//              which placeholders remain; after the approved applies, whether the
//              model carries what the plan asked, and the re-review still owed
//              (ReviewCorrectionPlan).
//
// MCP servers never call each other: the agent in the client is the bus, and
// passes manifest PATHS between them, not large JSON inline.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ExchangeTool
    {
        internal const string ToolName = "horizun_exchange";

        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        internal static JObject Handle(JObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            args = args ?? new JObject();
            string operation = (string)args["operation"];
            switch (operation)
            {
                case "emit": return Emit(args, ct);
                case "verify": return Verify(args);
                case "correction_plan_check": return PlanCheck(args);
                case "correction_plan_reverify": return PlanReverify(args);
                case null:
                    throw new ToolRefusal("operation is required: emit, verify, correction_plan_check or correction_plan_reverify. Nothing was read.");
                default:
                    throw new ToolRefusal("Unknown operation '" + operation + "'. Use emit, verify, correction_plan_check or " +
                                          "correction_plan_reverify. Nothing was read.");
            }
        }

        // ---- emit ------------------------------------------------------------------

        private static JObject Emit(JObject args, CancellationToken ct)
        {
            bool dryRun = (bool?)args["dry_run"] ?? true;
            string registryPath = ProjectIntegrationsTool.RegistryPath(args, false);
            JObject registry = null;
            if (registryPath != null)
            {
                bool exists;
                string sha;
                registry = ProjectIntegrationsTool.Load(registryPath, out exists, out sha);
                if (!exists) throw new ToolRefusal("integrations_path '" + registryPath + "' does not exist. Nothing was written.");
            }

            string declared = (string)args["verification_state"];
            if (declared == null)
                throw new ToolRefusal("verification_state is required: the state the PRODUCING reply reported (verified, " +
                                      "self_reported_verified, partial or failed). Nothing was written.");
            var r = new ExchangeManifest.Request
            {
                Kind = (string)args["kind"],
                ProjectCode = (string)args["project_code"] ?? (string)registry?["project"]?["code"],
                CorrelationId = (string)args["correlation_id"],
                State = declared == ExchangeManifest.Verified ? ExchangeManifest.SelfReported : declared,
                Method = (string)args["verification_method"] ?? "wrapped_by_horizun_exchange",
                HostVerified = false
            };
            JObject producer = args["producer"] as JObject;
            string version, commit;
            OwnVersion(out version, out commit);
            r.ProducerApp = (string)producer?["app"] ?? "horizun-revit";
            r.ProducerVersion = (string)producer?["version"] ?? (producer == null ? version : null);
            r.ProducerCommit = (string)producer?["commit"] ?? (producer == null ? commit : null);
            if (args["spatial_ref"] is JObject spatial) r.SpatialRef = spatial;
            foreach (JToken c in (args["caveats"] as JArray) ?? new JArray())
                if (c.Type == JTokenType.String) r.Caveats.Add((string)c);
            if (declared == ExchangeManifest.Verified)
                r.Caveats.Insert(0, "The producing reply declared verified; this manifest was written by horizun_exchange, which " +
                                    "did not produce the payload, so its state is self_reported_verified.");

            foreach (JToken t in (args["files"] as JArray) ?? new JArray())
            {
                if (!(t is JObject f)) throw new ToolRefusal("files must be objects {path, format, rows?, name?}. Nothing was written.");
                r.Files.Add(new ExchangeManifest.PayloadFile
                {
                    SourcePath = (string)f["path"],
                    Name = (string)f["name"],
                    Format = (string)f["format"],
                    Rows = (long?)f["rows"]
                });
            }

            string modelRef = (string)args["model_ref"];
            if (modelRef != null)
            {
                if (registry == null)
                    throw new ToolRefusal("model_ref needs the project registry (integrations_path or path) to attach its evidence. Nothing was written.");
                JObject m = ProjectIntegrations.Models(registry).FirstOrDefault(x => (string)x["model_ref"] == modelRef);
                if (m == null) throw new ToolRefusal("The project registry has no " + modelRef + ". Nothing was written.");
                r.ModelRef = modelRef;
                r.ModelEvidence = (JObject)m["evidence"];
                if ((bool?)m["identity_proven"] != true)
                    r.Caveats.Add("The source model's identity is not proven (" + ((string)m["evidence"]?["identity_note"] ?? "see the registry") + ").");
            }
            else r.Caveats.Add("No source model is named: nothing here ties the payload to a model in the project registry.");

            List<string> errors = ExchangeManifest.Check(r);
            if (errors.Count > 0) throw new ToolRefusal("The exchange cannot be emitted: " + string.Join(" ", errors) + " Nothing was written.");

            string root = (string)args["exchange_root"];
            string rootSource = "argument";
            if (root == null && registry != null && (string)registry["exchange_root"] != null) { root = (string)registry["exchange_root"]; rootSource = "registry"; }
            if (root == null) { root = ProjectIntegrationsTool.DefaultRoot(r.ProjectCode); rootSource = "default_local"; }
            if (!Path.IsPathRooted(root)) throw new ToolRefusal("exchange_root must be absolute. Nothing was written.");

            string id = ExchangeManifest.NewExchangeId(UtcNow(), Guid.NewGuid().ToString("N"));
            ct.ThrowIfCancellationRequested();
            if (dryRun)
            {
                return new JObject
                {
                    ["operation"] = "emit",
                    ["dry_run"] = true,
                    ["written"] = false,
                    ["exchange_root"] = root,
                    ["exchange_root_source"] = rootSource,
                    ["directory_would_be"] = Path.Combine(root, r.Kind, "<exchange_id assigned at write>"),
                    ["manifest"] = ExchangeManifest.Build(r, id, ExchangeManifest.Sha256File),
                    ["state_capped_from"] = declared == ExchangeManifest.Verified ? declared : null,
                    ["note"] = "Rehearsal: nothing was written. dry_run=false copies the payload (re-hashed), then writes the manifest last and re-reads the exchange."
                };
            }
            string profileRefusal;
            if (!Settings.AllowsExternalSideEffect(out profileRefusal))
                throw new ToolRefusal("Emitting an exchange writes files outside the model, and that is what needs the profile: " + profileRefusal);
            JObject written;
            try { written = ExchangeManifest.Emit(r, root, id); }
            catch (ArgumentException ex) { throw new ToolRefusal(ex.Message + " Nothing was written."); }
            written["operation"] = "emit";
            written["dry_run"] = false;
            written["written"] = true;
            written["exchange_root"] = root;
            written["exchange_root_source"] = rootSource;
            written["state_capped_from"] = declared == ExchangeManifest.Verified ? declared : null;
            return written;
        }

        private static void OwnVersion(out string version, out string commit)
        {
            version = null; commit = null;
            try
            {
                var attr = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    Assembly.GetExecutingAssembly(), typeof(AssemblyInformationalVersionAttribute));
                string v = attr?.InformationalVersion;
                if (string.IsNullOrEmpty(v)) return;
                int plus = v.IndexOf('+');
                version = plus > 0 ? v.Substring(0, plus) : v;
                commit = plus > 0 ? v.Substring(plus + 1) : null;
            }
            catch { }
        }

        // ---- verify ----------------------------------------------------------------

        private static JObject Verify(JObject args)
        {
            JToken t = args["manifest_path"];
            if (t == null || t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t) || !Path.IsPathRooted((string)t))
                throw new ToolRefusal("verify needs manifest_path: the absolute path of exchange.hzx.json or of its exchange folder. Nothing was read.");
            string registryPath = ProjectIntegrationsTool.RegistryPath(args, false);
            IEnumerable<JObject> models = null;
            if (registryPath != null)
            {
                bool exists;
                string sha;
                JObject registry = ProjectIntegrationsTool.Load(registryPath, out exists, out sha);
                if (!exists) throw new ToolRefusal("integrations_path '" + registryPath + "' does not exist. Nothing was verified.");
                models = ProjectIntegrations.Models(registry).ToList();
            }
            JObject v = ExchangeManifest.Verify(Path.GetFullPath((string)t), models);
            string expectedKind = (string)args["kind"];
            if (expectedKind != null && v["kind"] != null && (string)v["kind"] != expectedKind)
            {
                v["may_continue"] = false;
                ((JArray)v["reasons"]).Add("The exchange is a " + v["kind"] + ", not the " + expectedKind + " this hop expects.");
            }
            v["operation"] = "verify";
            return v;
        }

        // ---- the review -> correction -> re-review cycle -------------------------------

        private static JObject Plan(JObject args)
        {
            if (!(args["plan"] is JObject plan) || !(plan["pasos"] is JArray))
                throw new ToolRefusal("plan is required: the object horizun-review's plan_correcciones_revit returns (with 'pasos'). Nothing was read.");
            return plan;
        }

        private static JObject PlanCheck(JObject args)
        {
            JObject r = ReviewCorrectionPlan.Check(Plan(args), tool => Contract.Find(tool) != null);
            r["operation"] = "correction_plan_check";
            return r;
        }

        private static JObject PlanReverify(JObject args)
        {
            if (!(args["applied"] is JArray applied))
                throw new ToolRefusal("applied is required: one {step_id, arguments, reply} per applied step - the arguments sent with " +
                                      "dry_run=false and the typed tool's reply. Nothing was judged.");
            JObject r = ReviewCorrectionPlan.Reverify(Plan(args), applied);
            r["operation"] = "correction_plan_reverify";
            return r;
        }
    }
}
