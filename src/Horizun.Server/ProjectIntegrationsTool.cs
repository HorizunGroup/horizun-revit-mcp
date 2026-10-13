// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_project_context operation=integrations | register_model | resolve_model:
// the project registry, project-integrations.json, sibling of project-context.json
// (whose schema v1 is closed and does not admit the block).
//
// Decision D0 (owner, 2026-10-12): a model_ref is the registry's id, bound to strong
// evidence in the order creation_guid, central_guid, cloud_model_guid,
// unique_document_guid. A model with only Project Information is never registered
// and never matched; evidence read on Revit 2023 is reported identity_proven:false.
// The evidence is what get_document_info publishes as model_identity - the agent
// relays it, and this process re-derives every judgement from the raw keys rather
// than trusting a caller's binding_key or identity_proven.
//
// Host-resident, Revit-free. Writing the registry is a file outside the model:
// dry_run defaults to true, dry_run=false asks Settings.AllowsExternalSideEffect,
// writes atomically and re-reads before reporting it written.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ProjectIntegrationsTool
    {
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        internal static JObject Handle(string operation, JObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            switch (operation)
            {
                case "integrations": return Read(args);
                case "register_model": return Register(args, ct);
                case "resolve_model": return Resolve(args);
                default: throw new ToolRefusal("Unknown registry operation '" + operation + "'.");
            }
        }

        /// <summary>integrations_path, else project-integrations.json beside the project context's path.</summary>
        internal static string RegistryPath(JObject args, bool required)
        {
            JToken t = args["integrations_path"];
            if (t != null && t.Type != JTokenType.Null)
            {
                if (t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t))
                    throw new ToolRefusal("integrations_path must be a non-empty string. Nothing was read.");
                string p = (string)t;
                if (!Path.IsPathRooted(p))
                    throw new ToolRefusal("integrations_path must be absolute; '" + p + "' is relative. Nothing was read.");
                if (!p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    throw new ToolRefusal("integrations_path must name a .json file. Nothing was read.");
                return Path.GetFullPath(p);
            }
            string contextPath = ProjectContext.OptionalPath(args);
            if (contextPath != null)
                return Path.Combine(Path.GetDirectoryName(contextPath), ProjectIntegrations.FileName);
            if (required)
                throw new ToolRefusal("The registry needs integrations_path (project-integrations.json), or path (the project-context.json " +
                                      "it sits beside). Nothing was read.");
            return null;
        }

        /// <summary>Read and validate. A missing file is an empty registry, said so; an invalid one refuses.</summary>
        internal static JObject Load(string path, out bool exists, out string sha)
        {
            exists = File.Exists(path);
            sha = null;
            if (!exists) return null;
            byte[] bytes = File.ReadAllBytes(path);
            sha = ProjectContext.Sha256Hex(bytes);
            JToken parsed;
            string error;
            if (!ProjectContext.TryParse(bytes, out parsed, out error) || !(parsed is JObject))
                throw new ToolRefusal("'" + path + "' is not a JSON object (" + (error ?? "top level is not an object") + "). Nothing was changed.");
            List<string> errors = ProjectIntegrations.Validate(parsed);
            if (errors.Count > 0)
                throw new ToolRefusal("'" + path + "' is not a valid " + ProjectIntegrations.Schema + " registry: " +
                                      string.Join(" ", errors.Take(10)) + (errors.Count > 10 ? " (+" + (errors.Count - 10) + " more)" : "") +
                                      " Repair it before anything is registered or matched against it.");
            return (JObject)parsed;
        }

        private static JObject Read(JObject args)
        {
            string path = RegistryPath(args, true);
            bool exists;
            string sha;
            JObject doc = Load(path, out exists, out sha);
            var result = new JObject
            {
                ["operation"] = "integrations",
                ["integrations_path"] = path,
                ["exists"] = exists,
                ["sha256"] = sha
            };
            if (!exists)
            {
                result["models"] = new JArray();
                result["note"] = "No registry yet. register_model creates it beside the project context.";
                result["exchange_root"] = DefaultRoot(null);
                result["exchange_root_source"] = "default_local";
                return result;
            }
            result["project"] = doc["project"];
            result["models"] = new JArray(ProjectIntegrations.Models(doc).Select(Summary));
            string root = (string)doc["exchange_root"];
            result["exchange_root"] = root ?? DefaultRoot((string)doc["project"]?["code"]);
            result["exchange_root_source"] = root == null ? "default_local" : "registry";
            return result;
        }

        internal static string DefaultRoot(string projectCode) => ExchangeManifest.DefaultRootUnder(HorizunPaths.DataRoot(), projectCode);

        private static JObject Summary(JObject m)
        {
            List<string> ignored;
            JObject ev = ModelIdentity.Normalize(m["evidence"] as JObject, out ignored);
            return new JObject
            {
                ["model_ref"] = m["model_ref"],
                ["name"] = m["name"],
                ["discipline"] = m["discipline"],
                ["binding_key"] = ev?["binding_key"],
                ["identity_proven"] = ev?["identity_proven"],
                ["identity_note"] = ev?["identity_note"]
            };
        }

        private static JObject Evidence(JToken raw, string where)
        {
            if (!(raw is JObject o))
                throw new ToolRefusal(where + " needs 'evidence': the model_identity object get_document_info returns. Nothing was changed.");
            List<string> problems;
            JObject ev = ModelIdentity.Normalize(o, out problems);
            if (ev == null) throw new ToolRefusal("The evidence is not usable: " + string.Join(" ", problems) + " Nothing was changed.");
            return ev;
        }

        private static JObject Resolve(JObject args)
        {
            string path = RegistryPath(args, true);
            bool exists;
            string sha;
            JObject doc = Load(path, out exists, out sha);
            JObject observed = Evidence(args["evidence"], "resolve_model");
            if (ModelIdentity.BindingKey(observed) == null)
                throw new ToolRefusal("resolve_model refuses to match a model without strong evidence: " + (string)observed["identity_note"] +
                                      " Read it with get_document_info on Revit 2024+ (creation_guid), or from a central/cloud model.");
            JObject match = ModelIdentity.Match(ProjectIntegrations.Models(doc), observed);
            if ((string)match["state"] == ModelIdentity.Ambiguous)
                throw new ToolRefusal((string)match["reason"] + " Candidates: " + string.Join(", ", match["candidates"]) + ".");
            match["operation"] = "resolve_model";
            match["integrations_path"] = path;
            match["registry_exists"] = exists;
            match["observed"] = observed;
            return match;
        }

        private static JObject Register(JObject args, CancellationToken ct)
        {
            string path = RegistryPath(args, true);
            bool dryRun = (bool?)args["dry_run"] ?? true;
            if (!(args["model"] is JObject model))
                throw new ToolRefusal("register_model needs 'model': {name, discipline?, evidence, model_ref?}. Nothing was written.");
            string name = (string)model["name"];
            if (string.IsNullOrWhiteSpace(name))
                throw new ToolRefusal("model.name is required: the name people use for this model. Nothing was written.");
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "name", "discipline", "evidence", "model_ref", "notes" };
            var unknown = model.Properties().Select(p => p.Name).Where(n => !allowed.Contains(n)).ToList();
            if (unknown.Count > 0)
                throw new ToolRefusal("model carries unknown key(s) " + string.Join(", ", unknown) + ". Nothing was written.");
            JObject evidence = Evidence(model["evidence"], "register_model");
            string binding = ModelIdentity.BindingKey(evidence);
            if (binding == null)
                throw new ToolRefusal("register_model refuses a model without strong evidence: " + (string)evidence["identity_note"] +
                                      " Nothing was written.");

            bool exists;
            string baseSha;
            JObject doc = Load(path, out exists, out baseSha) ?? ProjectIntegrations.Empty((string)args["project_code"]);
            ct.ThrowIfCancellationRequested();

            JObject match = ModelIdentity.Match(ProjectIntegrations.Models(doc), evidence);
            string state = (string)match["state"];
            if (state == ModelIdentity.Matched)
                throw new ToolRefusal("This model is already registered as " + (string)match["model_ref"] + " (matched by " +
                                      (string)match["matched_by"] + "). A model is registered once; nothing was written.");
            if (state == ModelIdentity.Ambiguous)
                throw new ToolRefusal((string)match["reason"] + " Nothing was written.");

            string modelRef = (string)model["model_ref"];
            if (modelRef != null)
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(modelRef, "^mdl-[0-9a-f]{12}$"))
                    throw new ToolRefusal("model.model_ref must be 'mdl-' and twelve lower-case hex digits, or omitted so the registry assigns one. Nothing was written.");
                if (ProjectIntegrations.Models(doc).Any(m => (string)m["model_ref"] == modelRef))
                    throw new ToolRefusal(modelRef + " already names another model in this registry. Nothing was written.");
            }
            else modelRef = ModelIdentity.NewModelRef();

            var entry = new JObject
            {
                ["model_ref"] = modelRef,
                ["name"] = name.Trim(),
                ["discipline"] = model["discipline"] != null ? model["discipline"].DeepClone() : JValue.CreateNull(),
                ["evidence"] = evidence,
                ["binding_key"] = binding,
                ["identity_proven"] = evidence["identity_proven"],
                ["registered_utc"] = UtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            };
            if (model["notes"] != null) entry["notes"] = model["notes"].DeepClone();
            ((JArray)doc["models"]).Add(entry);
            List<string> errors = ProjectIntegrations.Validate(doc);
            if (errors.Count > 0)
                throw new ToolRefusal("The registry would not be valid after this registration: " + string.Join(" ", errors) + " Nothing was written.");

            var result = new JObject
            {
                ["operation"] = "register_model",
                ["dry_run"] = dryRun,
                ["integrations_path"] = path,
                ["base"] = exists ? "existing_file" : "new_file",
                ["base_sha256"] = baseSha,
                ["model"] = entry.DeepClone(),
                ["warnings"] = match["warnings"],
                ["document"] = doc.DeepClone()
            };
            if (dryRun)
            {
                result["written"] = false;
                result["note"] = "Rehearsal: nothing was written. Send the same call with dry_run=false to register; the file is read back before it is reported written.";
                return result;
            }
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir))
                throw new ToolRefusal("The folder '" + dir + "' does not exist. This tool does not create project folders. Nothing was written.");
            string profileRefusal;
            if (!Settings.AllowsExternalSideEffect(out profileRefusal))
                throw new ToolRefusal("Writing the project registry is a file outside the model, and that is what needs the profile: " +
                                      profileRefusal + " The registry document is in this rehearsal's 'document'.");

            // The file must still be the one this registration was built on.
            string nowSha = File.Exists(path) ? ProjectContext.Sha256Hex(File.ReadAllBytes(path)) : null;
            if (nowSha != baseSha)
                throw new ToolRefusal("'" + path + "' changed while this registration was being prepared (sha256 " + (baseSha ?? "none") +
                                      " -> " + (nowSha ?? "none") + "). Nothing was written; send the call again.");
            byte[] bytes = ProjectContext.Serialize(doc);
            AtomicFile.WriteAllBytes(path, bytes);

            // The bytes on disk are the bytes meant, and they still parse to a valid registry.
            byte[] onDisk = File.ReadAllBytes(path);
            JToken reread;
            string rereadError;
            bool same = ProjectContext.Sha256Hex(onDisk) == ProjectContext.Sha256Hex(bytes) &&
                        ProjectContext.TryParse(onDisk, out reread, out rereadError) &&
                        ProjectIntegrations.Validate(reread).Count == 0 &&
                        ProjectIntegrations.Models((JObject)reread).Any(m => (string)m["model_ref"] == modelRef);
            if (!same)
                throw new InvalidOperationException("'" + path + "' was written but reading it back does not return what was written. " +
                                                    "Treat the registry as unverified; nothing is reported written.");
            result["written"] = true;
            result["verification"] = new JObject
            {
                ["reread"] = true,
                ["bytes"] = onDisk.LongLength,
                ["sha256"] = ProjectContext.Sha256Hex(onDisk),
                ["content_equal"] = true
            };
            return result;
        }
    }
}
