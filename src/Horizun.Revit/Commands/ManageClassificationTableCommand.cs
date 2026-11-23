using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>Set or reload a project's keynote or Assembly Code resource, then read the committed table.</summary>
    public sealed class ManageClassificationTableCommand : ICommand
    {
        public string Name => "horizun_manage_classification_table";
        public string Description => "Set an absolute or project-relative keynote/Assembly Code table path, or reload its current resource; verify path, load state and entry count after commit.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            if (doc.IsFamilyDocument) return CommandResult.Fail("Classification tables belong to a project document. Open a project RVT.");
            string tableName = (request.Value<string>("table") ?? "keynote").ToLowerInvariant();
            if (tableName != "keynote" && tableName != "assembly_code")
                return CommandResult.Fail("table must be keynote or assembly_code.");
            string operation = (request.Value<string>("operation") ?? "set_path").ToLowerInvariant();
            if (operation != "set_path" && operation != "reload" && operation != "diagnose")
                return CommandResult.Fail("operation must be set_path, reload or diagnose.");
            if (operation == "diagnose" && tableName != "keynote")
                return CommandResult.Fail("diagnose reads keynote files (key, text, parent). Assembly Code tables have another layout.");
            if (operation == "diagnose" && request["dry_run"] != null)
                return CommandResult.Fail("diagnose writes nothing and has no rehearsal; omit dry_run.");
            if (operation == "reload" && request["path"] != null)
                return CommandResult.Fail("reload uses the table's existing path; omit path or select set_path.");

            KeyBasedTreeEntryTable table = tableName == "keynote"
                ? (KeyBasedTreeEntryTable)KeynoteTable.GetKeynoteTable(doc)
                : AssemblyCodeTable.GetAssemblyCodeTable(doc);
            if (table == null) return CommandResult.Fail(tableName + " table is unavailable in this project.");
            ExternalResourceType resourceType = tableName == "keynote"
                ? ExternalResourceTypes.BuiltInExternalResourceTypes.KeynoteTable
                : ExternalResourceTypes.BuiltInExternalResourceTypes.AssemblyCodeTable;
            JObject before = ReadState(table, resourceType);
            if (operation == "diagnose") return Diagnose(doc, request, before);
            string path = null;
            PathType pathType = PathType.Absolute;
            if (operation == "set_path")
            {
                string requested = request.Value<string>("path");
                if (string.IsNullOrWhiteSpace(requested)) return CommandResult.Fail("path is required for set_path.");
                bool relative = !Path.IsPathRooted(requested);
                if (relative && string.IsNullOrWhiteSpace(doc.PathName))
                    return CommandResult.Fail("A relative path needs a saved project path. Save the RVT or use an absolute path.");
                try { path = Path.GetFullPath(relative ? Path.Combine(Path.GetDirectoryName(doc.PathName), requested) : requested); }
                catch (Exception ex) { return CommandResult.Fail("Invalid table path: " + ex.Message); }
                if (!File.Exists(path)) return CommandResult.Fail("Table file does not exist: " + path);
                pathType = relative ? PathType.Relative : PathType.Absolute;
            }
            else
            {
                path = before.Value<string>("path");
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path))
                    return CommandResult.Fail("The current table has no readable local file to reload. Use set_path with an existing file; current path: " + (path ?? "<none>"));
            }
            string fileHash;
            try { using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path))
                fileHash = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
            catch (Exception ex) { return CommandResult.Fail("Cannot read table file: " + ex.Message); }
            // A keynote file is diagnosed BEFORE it is loaded: Revit accepts duplicated keys and
            // missing parents and only shows the damage later, in a tag that reads "?".
            JObject diagnosis = null;
            if (tableName == "keynote")
            {
                KeynoteTableRules.Diagnosis dx;
                try { dx = KeynoteTableRules.Diagnose(File.ReadAllBytes(path)); }
                catch (Exception ex) { return CommandResult.Fail("Cannot read table file: " + ex.Message); }
                diagnosis = dx.ToJson();
                if (!dx.Ok)
                    return CommandResult.Fail("The keynote file has errors; nothing was loaded. Fix the file and rehearse again. Diagnosis: " +
                                              diagnosis.ToString(Formatting.None));
            }
            string planHash = DocumentGate.PlanHash(request, "table", "operation", "path");
            var resolved = new ResolvedPlan
            {
                Command = Name, DocumentKey = gate.Fingerprint,
                RevitVersion = app?.Application?.VersionNumber,
                DocumentFingerprint = gate.Identity?.FingerprintDigest(),
                ContextFingerprint = "file=" + fileHash + ";path=" + path + ";before=" + before.ToString(Formatting.None)
            };
            resolved.Elements.Add(new PlannedElement
            {
                UniqueId = table.UniqueId, Category = tableName, Action = PlannedAction.Modify,
                BeforeValues = new System.Collections.Generic.Dictionary<string, string>
                { { "path", before.Value<string>("path") ?? "" }, { "entries", before.Value<int?>("entries")?.ToString() ?? "" } }
            });
            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            if (dryRun)
            {
                var preview = new JObject { ["dry_run"] = true, ["table"] = tableName, ["operation"] = operation,
                    ["path"] = path, ["path_type"] = pathType.ToString(), ["file_sha256"] = fileHash, ["before"] = before,
                    ["diagnosis"] = diagnosis };
                DocumentGate.RecordResolvedPlan(resolved);
                DocumentGate.StampConfirmation(preview, gate, Name, planHash, true,
                    "the token binds the current table state and the SHA-256 of the file to load; changes require a new rehearsal.");
                return CommandResult.Ok(preview);
            }
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash, resolved, null);
            if (refusal != null) return refusal;
            refusal = DocumentGate.StillTheSame(app, gate.Fingerprint, Name);
            if (refusal != null) return refusal;
            ExternalResourceLoadStatus loadStatus;
            try
            {
                using (var tx = new Transaction(doc, "Horizun: " + operation + " " + tableName + " table"))
                {
                    tx.Start();
                    try
                    {
                        using (var details = new KeyBasedTreeEntriesLoadResults())
                        {
                            if (operation == "reload") loadStatus = table.Reload(details);
                            else
                            {
                                var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
                                var reference = ExternalResourceReference.CreateLocalResource(doc, resourceType, modelPath, pathType);
                                if (!table.ServerSupports(reference))
                                    throw new InvalidOperationException("Revit's local resource server does not support this " + tableName + " table.");
                                loadStatus = table.LoadFrom(reference, details);
                            }
                        }
                        if (loadStatus != ExternalResourceLoadStatus.Success && loadStatus != ExternalResourceLoadStatus.ResourceAlreadyCurrent)
                            throw new InvalidOperationException("Revit table load returned " + loadStatus + ".");
                        Guard.Commit(tx, "manage classification table");
                    }
                    catch { if (tx.GetStatus() == TransactionStatus.Started) Guard.RollBack(tx); throw; }
                }
            }
            catch (Exception ex) { return CommandResult.Fail("Classification table update failed: " + ex.Message); }
            KeyBasedTreeEntryTable fresh = tableName == "keynote"
                ? (KeyBasedTreeEntryTable)KeynoteTable.GetKeynoteTable(doc)
                : AssemblyCodeTable.GetAssemblyCodeTable(doc);
            JObject after = ReadState(fresh, resourceType);
            string readPath = after.Value<string>("path");
            bool samePath = !string.IsNullOrWhiteSpace(readPath) && Path.IsPathRooted(readPath) &&
                string.Equals(Path.GetFullPath(readPath), path, StringComparison.OrdinalIgnoreCase);
            bool loaded = string.Equals(after.Value<string>("link_status"), "Loaded", StringComparison.OrdinalIgnoreCase);
            bool current = string.Equals(after.Value<string>("version_status"), "Current", StringComparison.OrdinalIgnoreCase);
            if (!samePath || !loaded || !current || after.Value<int?>("entries") == null)
                return CommandResult.Fail("The transaction committed but the table did not re-read as loaded at the requested path. Post-commit state: " + after.ToString(Formatting.None));
            return CommandResult.Ok(new JObject { ["dry_run"] = false, ["table"] = tableName,
                ["operation"] = operation, ["load_status"] = loadStatus.ToString(), ["file_sha256"] = fileHash,
                ["before"] = before, ["after"] = after, ["diagnosis"] = diagnosis, ["verified_after_commit"] = true });
        }

        /// <summary>
        /// Read-only: is the requested file (or the table's current file) a sound keynote
        /// table, and is it the one the project is using? Nothing is loaded or written.
        /// </summary>
        private static CommandResult Diagnose(Document doc, JObject request, JObject before)
        {
            string current = before.Value<string>("path");
            string requested = request.Value<string>("path");
            string path;
            if (string.IsNullOrWhiteSpace(requested))
            {
                if (string.IsNullOrWhiteSpace(current) || !Path.IsPathRooted(current))
                    return CommandResult.Fail("The project has no readable keynote file to diagnose; pass path. Current path: " + (current ?? "<none>"));
                path = current;
            }
            else
            {
                bool relative = !Path.IsPathRooted(requested);
                if (relative && string.IsNullOrWhiteSpace(doc.PathName))
                    return CommandResult.Fail("A relative path needs a saved project path. Save the RVT or use an absolute path.");
                try { path = Path.GetFullPath(relative ? Path.Combine(Path.GetDirectoryName(doc.PathName), requested) : requested); }
                catch (Exception ex) { return CommandResult.Fail("Invalid table path: " + ex.Message); }
            }
            bool exists = File.Exists(path);
            bool sameAsCurrent = !string.IsNullOrWhiteSpace(current) && Path.IsPathRooted(current) &&
                                 string.Equals(Path.GetFullPath(current), path, StringComparison.OrdinalIgnoreCase);
            var result = new JObject
            {
                ["operation"] = "diagnose", ["table"] = "keynote", ["path"] = path, ["exists"] = exists,
                ["current"] = before, ["same_file_as_current"] = sameAsCurrent,
                ["path_note"] = sameAsCurrent ? null : "The project's keynote table points to another file (" + (current ?? "<none>") +
                    "). Codes are validated against the LOADED table; load this file with set_path before assigning codes from it."
            };
            if (!exists)
            {
                result["diagnosis"] = new JObject { ["ok"] = false, ["readable"] = false, ["errors"] = new JArray("File not found: " + path) };
                return CommandResult.Ok(result);
            }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                result["diagnosis"] = new JObject { ["ok"] = false, ["readable"] = false, ["errors"] = new JArray("Cannot read the file: " + ex.Message) };
                return CommandResult.Ok(result);
            }
            using (var sha = SHA256.Create())
                result["file_sha256"] = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            KeynoteTableRules.Diagnosis dx = KeynoteTableRules.Diagnose(bytes);
            result["diagnosis"] = dx.ToJson();
            int? loaded = before.Value<int?>("entries");
            if (sameAsCurrent && loaded.HasValue && loaded.Value != dx.ByKey.Count)
                result["entries_mismatch"] = "The loaded table has " + loaded.Value + " entries but the file has " + dx.ByKey.Count +
                                             ": the file changed after it was loaded. Use operation=reload.";
            return CommandResult.Ok(result);
        }

        private static JObject ReadState(KeyBasedTreeEntryTable table, ExternalResourceType resourceType)
        {
            var result = new JObject { ["element_id"] = Rid.Value(table.Id) };
            ExternalResourceReference reference = table.GetExternalResourceReference(resourceType);
            result["path"] = reference?.InSessionPath;
            result["version_status"] = reference?.GetResourceVersionStatus().ToString();
            result["link_status"] = table.IsExternalFileReference()
                ? table.GetExternalFileReference().GetLinkedFileStatus().ToString() : null;
            try
            {
                KeyBasedTreeEntries entries = table.GetKeyBasedTreeEntries();
                result["entries"] = entries == null ? (int?)null : entries.Cast<KeyBasedTreeEntry>().Count(x => x != null);
            }
            catch (Exception ex) { result["entries"] = null; result["entries_error"] = ex.Message; }
            return result;
        }
    }
}
