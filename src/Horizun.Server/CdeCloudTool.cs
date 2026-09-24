// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_cde_cloud - a HOST-RESIDENT, READ-ONLY reader of a cloud CDE. It never
// touches Revit and never writes to the cloud: every request is a GET, except the
// OpenCDE document-versions QUERY (a POST whose body is a list of ids) and the
// OAuth token request itself.
//
// Two providers:
//
//   acc      Autodesk Construction Cloud / BIM 360 Docs through the APS Data
//            Management API: hubs -> projects -> topFolders -> folders/contents ->
//            items -> versions, with data:read.
//   opencde  a buildingSMART OpenCDE server: Foundation API discovery
//            (GET /foundation/versions, GET /foundation/{v}/auth) and the Documents
//            API 1.0 query surface (POST /document-versions, the server-provided
//            document_versions link). The Documents API has NO non-interactive way
//            to enumerate a project - selection happens in the CDE's own web UI - so
//            this reader reads the documents it is given by id and says so.
//
// Three operations:
//
//   list_states  map the cloud folders to the four ISO 19650 states from cde.states
//                of the project context ("Project Files/01_WIP" style paths). Never
//                guesses a folder: an exact name or not found. Sibling folders that
//                are no state are reported as unmapped.
//   inspect      per state: files, version, date, size, name compliance, and the MIDP
//                cross - through ContainerInspection, the SAME core the local inspect
//                of horizun_information_container uses.
//   versions     the version history of one item / document.
//
// Coverage is the contract: anything that could not be read (401/403, a spent call
// budget, a folder that was not found, a depth limit) makes coverage_complete=false
// and is named. A cloud reader that says "empty" when it means "could not look" is
// worse than no reader.
//
// PERMISSION. Classified ReadOnly with openWorldHint: it changes nothing anywhere.
// The one local write it can make is the user's own 3-legged token file, rewritten
// after a refresh because APS replaces a refresh token when it is used.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class CdeCloudTool
    {
        internal const string ToolName = "horizun_cde_cloud";
        internal const string ApsBase = "https://developer.api.autodesk.com";
        internal const string OpenCdeTokenName = "HORIZUN_OPENCDE_ACCESS_TOKEN";
        private const int DefaultMaxCalls = 300, MaxMaxCalls = 5000;
        // Fixed walk limits. They are not arguments because tools/list has a byte budget;
        // reaching any of them is reported as a coverage gap, never as an empty folder.
        private const int MaxFiles = 5000;
        private const int MaxDepth = 8;
        private const int FilesLimit = 500;
        private const int DefaultLimit = 200, MaxLimit = 1000;
        private const int PageLimit = 200;
        private const int MaxPages = 500;
        private const int MaxHubs = 50;
        private const int MaxDocumentIds = 500;

        private static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
        {
            "operation", "provider", "project_context_path", "hub_id", "project_id", "states", "naming", "deliverables",
            "as_of", "offset", "limit", "max_calls", "item_id", "server_url", "document_ids", "document_id"
        };

        internal static JObject Handle(JObject args, CancellationToken ct) => Handle(args, ct, new CdeCloudEnvironment());

        internal static JObject Handle(JObject args, CancellationToken ct, CdeCloudEnvironment env)
        {
            ct.ThrowIfCancellationRequested();
            args = args ?? new JObject();
            foreach (JProperty p in args.Properties())
                if (!Keys.Contains(p.Name))
                    throw new ToolRefusal("Unknown argument '" + p.Name + "'. Nothing was read.");
            string op = Str(args, "operation");
            if (op != "list_states" && op != "inspect" && op != "versions")
                throw new ToolRefusal("operation must be one of list_states, inspect, versions. Nothing was read.");
            string provider = Str(args, "provider");
            try
            {
                switch (provider)
                {
                    case "acc": return Acc(op, args, ct, env);
                    case "opencde": return OpenCde(op, args, ct, env);
                    default: throw new ToolRefusal("provider must be 'acc' (Autodesk Construction Cloud / BIM 360 Docs) or 'opencde'. Nothing was read.");
                }
            }
            catch (ContainerRuleException ex) { throw new ToolRefusal(ex.Message + " Nothing was read."); }
        }

        // ================================================================================
        // ACC / BIM 360 through the APS Data Management API
        // ================================================================================

        private sealed class Folder
        {
            public string Id, Name, Path;
        }

        private sealed class StateTarget
        {
            public string State, Declared, Reason;
            public string[] Segments;
            public Folder Folder;
            public bool Resolved => Folder != null;
        }

        private static JObject Acc(string op, JObject args, CancellationToken ct, CdeCloudEnvironment env)
        {
            // Every argument is checked and the credential's PRESENCE is decided before the
            // first request: a machine with nothing configured is refused with zero calls.
            JObject context = InformationContainerTool.LoadContext(args);
            string platform = (string)context?["cde"]?["platform"];
            if (platform != null && platform != "acc" && platform != "bim360")
                throw new ToolRefusal("The project context declares cde.platform='" + platform + "', not acc or bim360. " +
                                      "provider=acc will not read another platform's folders as if they were ACC's. Nothing was read.");
            string projectId = ProjectId(Str(args, "project_id") ?? (string)context?["cde"]?["project_ref"]);
            string hubId = HubId(Str(args, "hub_id"));
            int maxCalls = Int(args, "max_calls", DefaultMaxCalls, 1, MaxMaxCalls);
            string itemId = null;
            List<StateTarget> targets = null;
            ContainerNaming naming = null;
            if (op == "versions")
            {
                itemId = Str(args, "item_id");
                if (string.IsNullOrWhiteSpace(itemId)) throw new ToolRefusal("versions needs item_id (an item URN, urn:adsk.wipprod:dm.lineage:...). Nothing was read.");
            }
            else
            {
                targets = StateTargets(args, context);
                naming = op == "inspect" ? (InformationContainerTool.OptionalRules(args, context) ?? InformationContainer.ParseNaming(new JObject(), null)) : null;
            }
            if (!ApsAuth.AnyConfigured(env)) throw new ToolRefusal(ApsAuth.NotConfiguredMessage(env));

            using (var http = new CdeCloudHttp(env, maxCalls, ct))
            {
                http.AllowHost(ApsAuth.Host);
                CloudCredential cred = ApsAuth.Resolve(env, http);
                http.BearerToken = cred.Token;

                if (op == "versions") return AccVersions(http, cred, projectId, itemId);

                var result = new JObject { ["operation"] = op, ["provider"] = "acc" };
                if (hubId == null) hubId = FindHub(http, projectId);
                result["hub_id"] = hubId;
                result["project_id"] = projectId;
                if (hubId == null)
                {
                    result["coverage_complete"] = false;
                    result["reason"] = "No hub visible to this credential contains project " + projectId + ". Pass hub_id, or check the " +
                                       "credential's access. Nothing was read from the project.";
                    result["auth"] = cred.Describe();
                    result["http"] = http.Describe();
                    return result;
                }

                var levels = new Dictionary<string, List<Folder>>(StringComparer.Ordinal);
                bool mapComplete = ResolveStates(http, hubId, projectId, targets, levels);
                result["states"] = DescribeTargets(targets);
                result["unmapped_folders"] = Unmapped(targets, levels);

                if (op == "list_states")
                {
                    bool complete = mapComplete && targets.All(t => t.Resolved) && targets.Count == InformationContainer.States.Length;
                    result["coverage_complete"] = complete;
                    result["auth"] = cred.Describe();
                    result["http"] = http.Describe();
                    result["note"] = "Read-only: nothing was written anywhere. A state is mapped only to a folder whose path matches " +
                                     "cde.states exactly; 'resolved=false' names what was not found, and is never a guess.";
                    return result;
                }
                return AccInspect(args, http, cred, projectId, targets, naming, context, mapComplete, result);
            }
        }

        private static JObject AccInspect(JObject args, CdeCloudHttp http, CloudCredential cred, string projectId, List<StateTarget> targets,
                                          ContainerNaming naming, JObject context, bool mapComplete, JObject result)
        {
            int offset = Int(args, "offset", 0, 0, int.MaxValue);
            int limit = Int(args, "limit", DefaultLimit, 1, MaxLimit);
            DateTime asOf = AsOf(args);

            var findings = new List<JObject>();
            var records = new List<ContainerRecord>();
            var files = new JArray();
            var coverage = new JArray();
            var perState = new JObject();
            int walked = 0;

            foreach (string state in InformationContainer.States)
            {
                StateTarget t = targets.FirstOrDefault(x => x.State == state);
                if (t == null) { coverage.Add(new JObject { ["state"] = state, ["covered"] = false, ["reason"] = "no folder declared for this state" }); continue; }
                if (!t.Resolved) { coverage.Add(new JObject { ["state"] = state, ["declared"] = t.Declared, ["covered"] = false, ["reason"] = t.Reason }); continue; }

                int stateFiles = 0, compliant = 0;
                var gaps = new JArray();
                var queue = new Queue<KeyValuePair<Folder, int>>();
                queue.Enqueue(new KeyValuePair<Folder, int>(t.Folder, 0));
                while (queue.Count > 0)
                {
                    var next = queue.Dequeue();
                    Folder folder = next.Key;
                    List<JObject> data, included;
                    string error = ListContents(http, projectId, folder.Id, false, out data, out included);
                    if (error != null) gaps.Add(new JObject { ["folder"] = folder.Path, ["reason"] = error });
                    var versions = included.Where(v => (string)v["type"] == "versions")
                                           .GroupBy(v => (string)v["id"]).ToDictionary(g => g.Key, g => g.First());
                    foreach (JObject entry in data)
                    {
                        string type = (string)entry["type"];
                        string name = (string)entry["attributes"]?["displayName"] ?? (string)entry["attributes"]?["name"];
                        if (type == "folders")
                        {
                            var child = new Folder { Id = (string)entry["id"], Name = name, Path = folder.Path + "/" + name };
                            if (next.Value + 1 > MaxDepth) gaps.Add(new JObject { ["folder"] = child.Path, ["reason"] = "depth limit " + MaxDepth + " reached; not read" });
                            else queue.Enqueue(new KeyValuePair<Folder, int>(child, next.Value + 1));
                            continue;
                        }
                        if (type != "items" || name == null) continue;
                        if (walked >= MaxFiles) { gaps.Add(new JObject { ["folder"] = folder.Path, ["reason"] = "file limit " + MaxFiles + " reached; the walk stopped" }); queue.Clear(); break; }
                        walked++;
                        stateFiles++;
                        string path = folder.Path + "/" + name;
                        var rec = new ContainerRecord
                        {
                            State = state, Path = path, Relative = path.Substring(t.Folder.Path.Length).TrimStart('/'),
                            Extension = Path.GetExtension(name)
                        };
                        bool ok = ContainerInspection.CheckName(naming, rec, Path.GetFileNameWithoutExtension(name), findings);
                        if (ok) compliant++;
                        string statusState = InformationContainer.StateOfStatus(rec.Status);
                        if (ok && statusState != null && state != InformationContainer.StateArchived && statusState != state)
                            findings.Add(ContainerInspection.Finding("state_status_mismatch", state, path, new JObject
                            {
                                ["status"] = rec.Status, ["status_belongs_to"] = statusState,
                                ["reason"] = "the name's status belongs to the " + statusState + " state, the file sits in " + state
                            }));
                        records.Add(rec);

                        string tipId = (string)entry["relationships"]?["tip"]?["data"]?["id"];
                        JObject tip = tipId != null && versions.ContainsKey(tipId) ? versions[tipId] : null;
                        JObject va = tip?["attributes"] as JObject;
                        if (files.Count < FilesLimit)
                            files.Add(new JObject
                            {
                                ["state"] = state, ["path"] = path, ["item_id"] = entry["id"],
                                ["version_id"] = tipId, ["version_number"] = va?["versionNumber"],
                                ["last_modified"] = va?["lastModifiedTime"] ?? entry["attributes"]?["lastModifiedTime"],
                                ["size_bytes"] = va?["storageSize"], ["file_type"] = va?["fileType"],
                                ["version_known"] = va != null,
                                ["name_compliant"] = ok, ["container"] = rec.Name, ["status"] = rec.Status, ["revision"] = rec.Revision
                            });
                    }
                }
                perState[state] = new JObject
                {
                    ["folder"] = t.Folder.Path, ["folder_id"] = t.Folder.Id, ["files"] = stateFiles, ["compliant_names"] = compliant,
                    ["complete"] = gaps.Count == 0
                };
                coverage.Add(new JObject
                {
                    ["state"] = state, ["folder"] = t.Folder.Path, ["covered"] = gaps.Count == 0,
                    ["gaps"] = gaps, ["reason"] = gaps.Count == 0 ? null : "part of this state was not read; see gaps"
                });
            }

            bool complete = mapComplete && coverage.All(c => (bool)c["covered"]);
            return Finish(result, args, context, records, naming, asOf, findings, offset, limit, perState, coverage, complete, walked,
                          files, cred, http,
                          "Read-only: nothing was written anywhere. The API gives no content hash, so the SHA-256 rules of the local " +
                          "inspect do not apply here; status and revision come from the file NAME. 'covered=false' is a folder " +
                          "that was not read, never an empty one.");
        }

        private static JObject Finish(JObject result, JObject args, JObject context, List<ContainerRecord> records, ContainerNaming naming,
                                      DateTime asOf, List<JObject> findings, int offset, int limit, JObject perState, JArray coverage,
                                      bool complete, int walked, JArray files, CloudCredential cred,
                                      CdeCloudHttp http, string note)
        {
            ContainerInspection.CrossStates(records, findings);
            JArray deliverables = ContainerInspection.Deliverables(
                args["deliverables"] as JArray ?? context?["deliverables"] as JArray, records, naming, asOf, findings);
            if (!complete)
                foreach (JObject d in deliverables)
                    if ((string)d["verdict"] == "missing" || ((string)d["verdict"] == "overdue" && !((JArray)d["copies"]).Any()))
                        d["caveat"] = "coverage is incomplete: the container may sit in a part of the CDE that was not read";

            result["as_of"] = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            result["naming"] = naming.Describe();
            result["per_state"] = perState;
            result["coverage"] = coverage;
            result["coverage_complete"] = complete;
            result["files_walked"] = walked;
            ContainerInspection.Page(findings, offset, limit, result);
            result["deliverables"] = deliverables;
            result["files"] = files;
            result["files_truncated"] = walked > files.Count;
            if (cred != null) result["auth"] = cred.Describe();
            result["http"] = http.Describe();
            result["note"] = note;
            return result;
        }

        private static JObject AccVersions(CdeCloudHttp http, CloudCredential cred, string projectId, string itemId)
        {
            var versions = new JArray();
            List<JObject> data, included;
            string error = Paged(http, ApsBase + "/data/v1/projects/" + Esc(projectId) + "/items/" + Esc(itemId) + "/versions",
                                 out data, out included);
            foreach (JObject v in data.Where(d => (string)d["type"] == "versions"))
            {
                JObject a = v["attributes"] as JObject;
                versions.Add(new JObject
                {
                    ["version_id"] = v["id"], ["version_number"] = a?["versionNumber"], ["name"] = a?["displayName"] ?? a?["name"],
                    ["create_time"] = a?["createTime"], ["last_modified"] = a?["lastModifiedTime"],
                    ["size_bytes"] = a?["storageSize"], ["file_type"] = a?["fileType"]
                });
            }
            return new JObject
            {
                ["operation"] = "versions", ["provider"] = "acc", ["project_id"] = projectId, ["item_id"] = itemId,
                ["versions"] = new JArray(versions.OrderByDescending(v => v["version_number"]?.Type == JTokenType.Integer ? (int)v["version_number"] : -1)),
                ["count"] = versions.Count,
                ["coverage_complete"] = error == null,
                ["reason"] = error,
                ["auth"] = cred.Describe(),
                ["http"] = http.Describe(),
                ["note"] = "Read-only: nothing was written anywhere."
            };
        }

        /// <summary>Finds the hub holding the project: the hubs this credential sees, then a direct project read in each.</summary>
        private static string FindHub(CdeCloudHttp http, string projectId)
        {
            List<JObject> hubs, _;
            Paged(http, ApsBase + "/project/v1/hubs", out hubs, out _);
            foreach (JObject hub in hubs.Take(MaxHubs))
            {
                string id = (string)hub["id"];
                if (id == null) continue;
                CloudResponse r = http.Get(ApsBase + "/project/v1/hubs/" + Esc(id) + "/projects/" + Esc(projectId));
                if (r.Ok) return id;
                if (r.BudgetExhausted) return null;
            }
            return null;
        }

        /// <summary>
        /// Resolves each declared state path segment by segment, by EXACT display name:
        /// the first segment among the project's top folders, the rest among each
        /// folder's child folders. Every level listed is kept for the unmapped report.
        /// Returns false when a listing could not be read completely.
        /// </summary>
        private static bool ResolveStates(CdeCloudHttp http, string hubId, string projectId, List<StateTarget> targets,
                                          Dictionary<string, List<Folder>> levels)
        {
            bool complete = true;
            var toResolve = targets.Where(t => t.Segments != null).ToList();
            if (toResolve.Count == 0) return true;

            List<JObject> topData, _;
            string topError = Paged(http, ApsBase + "/project/v1/hubs/" + Esc(hubId) + "/projects/" + Esc(projectId) + "/topFolders",
                                    out topData, out _);
            var levelErrors = new Dictionary<string, string>(StringComparer.Ordinal);
            if (topError != null) { complete = false; levelErrors[""] = topError; }
            levels[""] = topData.Where(d => (string)d["type"] == "folders").Select(d => new Folder
            {
                Id = (string)d["id"], Name = (string)d["attributes"]?["displayName"] ?? (string)d["attributes"]?["name"],
                Path = (string)d["attributes"]?["displayName"] ?? (string)d["attributes"]?["name"]
            }).ToList();

            foreach (StateTarget t in toResolve)
            {
                string parentPath = "";
                Folder current = null;
                for (int i = 0; i < t.Segments.Length; i++)
                {
                    List<Folder> children;
                    if (!levels.TryGetValue(parentPath, out children))
                    {
                        List<JObject> data, inc;
                        string error = ListContents(http, projectId, current.Id, true, out data, out inc);
                        if (error != null) { complete = false; levelErrors[parentPath] = error; }
                        children = data.Where(d => (string)d["type"] == "folders").Select(d =>
                        {
                            string n = (string)d["attributes"]?["displayName"] ?? (string)d["attributes"]?["name"];
                            return new Folder { Id = (string)d["id"], Name = n, Path = parentPath + "/" + n };
                        }).ToList();
                        levels[parentPath] = children;
                    }
                    string levelError;
                    if (levelErrors.TryGetValue(parentPath, out levelError) &&
                        !children.Any(c => string.Equals(c.Name, t.Segments[i], StringComparison.Ordinal)))
                    {
                        t.Reason = "'" + (parentPath == "" ? "(top folders)" : parentPath) + "' could not be listed completely (" +
                                   levelError + "), so '" + t.Segments[i] + "' was not found - which is not the same as absent";
                        current = null;
                        break;
                    }
                    Folder match = children.FirstOrDefault(c => string.Equals(c.Name, t.Segments[i], StringComparison.Ordinal));
                    if (match == null)
                    {
                        Folder near = children.FirstOrDefault(c => string.Equals(c.Name, t.Segments[i], StringComparison.OrdinalIgnoreCase));
                        t.Reason = "no folder named exactly '" + t.Segments[i] + "' under '" + (parentPath == "" ? "(top folders)" : parentPath) + "'" +
                                   (near != null ? "; '" + near.Name + "' differs only in case and is NOT taken" : "") +
                                   ". Available: " + string.Join(", ", children.Select(c => "'" + c.Name + "'").Take(40));
                        current = null;
                        break;
                    }
                    current = match;
                    parentPath = match.Path;
                }
                t.Folder = current;
            }
            return complete;
        }

        private static JObject DescribeTargets(List<StateTarget> targets)
        {
            var o = new JObject();
            foreach (string state in InformationContainer.States)
            {
                StateTarget t = targets.FirstOrDefault(x => x.State == state);
                o[state] = t == null
                    ? new JObject { ["declared"] = null, ["resolved"] = false, ["reason"] = "no folder declared for this state" }
                    : new JObject
                    {
                        ["declared"] = t.Declared, ["resolved"] = t.Resolved, ["folder_id"] = t.Folder?.Id, ["path"] = t.Folder?.Path,
                        ["reason"] = t.Resolved ? null : t.Reason
                    };
            }
            return o;
        }

        /// <summary>
        /// Every folder that was listed on the way to a state folder and is neither a
        /// state folder nor on the path to one. Subfolders INSIDE a state folder are its
        /// contents, not unmapped folders.
        /// </summary>
        private static JArray Unmapped(List<StateTarget> targets, Dictionary<string, List<Folder>> levels)
        {
            var onPath = new HashSet<string>(StringComparer.Ordinal);
            foreach (StateTarget t in targets.Where(x => x.Segments != null))
                for (int i = 1; i <= t.Segments.Length; i++) onPath.Add(string.Join("/", t.Segments.Take(i)));
            var result = new JArray();
            foreach (var level in levels.OrderBy(l => l.Key, StringComparer.Ordinal))
                foreach (Folder f in level.Value.Where(f => !onPath.Contains(f.Path)))
                    result.Add(new JObject { ["path"] = f.Path, ["folder_id"] = f.Id, ["parent"] = level.Key == "" ? null : level.Key });
            return result;
        }

        private static string ListContents(CdeCloudHttp http, string projectId, string folderId, bool foldersOnly,
                                           out List<JObject> data, out List<JObject> included)
        {
            string url = ApsBase + "/data/v1/projects/" + Esc(projectId) + "/folders/" + Esc(folderId) + "/contents?page%5Blimit%5D=" + PageLimit +
                         (foldersOnly ? "&filter%5Btype%5D=folders" : "");
            return Paged(http, url, out data, out included);
        }

        /// <summary>
        /// Follows links.next until there is none. Returns null when every page was read,
        /// else the reason the listing is incomplete (what WAS read is still returned).
        /// </summary>
        private static string Paged(CdeCloudHttp http, string url, out List<JObject> data, out List<JObject> included)
        {
            data = new List<JObject>();
            included = new List<JObject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int page = 0; url != null; page++)
            {
                if (page >= MaxPages) return "more than " + MaxPages + " pages; the listing stopped";
                if (!seen.Add(url)) return "the server repeated a page link; the listing stopped";
                CloudResponse r = http.Get(url);
                if (!r.Ok) return r.Error;
                if (r.Body["data"] is JArray d) data.AddRange(d.OfType<JObject>());
                else if (r.Body["data"] is JObject single) data.Add(single);
                if (r.Body["included"] is JArray inc) included.AddRange(inc.OfType<JObject>());
                JToken next = r.Body["links"]?["next"];
                string href = next?.Type == JTokenType.Object ? (string)next["href"] : next?.Type == JTokenType.String ? (string)next : null;
                url = string.IsNullOrWhiteSpace(href) ? null : new Uri(new Uri(ApsBase), href).AbsoluteUri;
            }
            return null;
        }

        private static List<StateTarget> StateTargets(JObject args, JObject context)
        {
            JObject states = args["states"] as JObject ?? context?["cde"]?["states"] as JObject;
            if (states == null || !states.Properties().Any())
                throw new ToolRefusal("Declare the CDE state folders: 'states' {wip, shared, published, archived} as cloud folder paths " +
                                      "('Project Files/01_WIP'), or a project_context_path whose cde.states does. Nothing was read.");
            var list = new List<StateTarget>();
            foreach (JProperty p in states.Properties())
            {
                if (Array.IndexOf(InformationContainer.States, p.Name) < 0)
                    throw new ToolRefusal("states has an unknown state '" + p.Name + "'; the CDE states are wip, shared, published, archived. Nothing was read.");
                if (p.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)p.Value))
                    throw new ToolRefusal("states." + p.Name + " must be a folder path. Nothing was read.");
                string declared = (string)p.Value;
                var t = new StateTarget { State = p.Name, Declared = declared };
                if (Path.IsPathRooted(declared) || Regex.IsMatch(declared, @"^[A-Za-z]:"))
                    t.Reason = "'" + declared + "' is a local path (a synced folder), not a cloud folder path; declare it as " +
                               "'<top folder>/<folder>/...' to read it in the cloud, or read the synced copy with horizun_information_container";
                else
                    t.Segments = declared.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
                                         .Where(s => s.Length > 0).ToArray();
                if (t.Segments != null && t.Segments.Length == 0) { t.Segments = null; t.Reason = "empty folder path"; }
                list.Add(t);
            }
            return list;
        }

        /// <summary>b.&lt;guid&gt;, a bare GUID, or an ACC/BIM 360 URL containing /projects/&lt;guid&gt;. Never guessed further.</summary>
        internal static string ProjectId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ToolRefusal("project_id is required (b.<guid> or the project GUID), or cde.project_ref in the project context. Nothing was read.");
            raw = raw.Trim();
            Guid g;
            if (raw.StartsWith("b.", StringComparison.Ordinal) && Guid.TryParse(raw.Substring(2), out g)) return "b." + g.ToString("D");
            if (Guid.TryParse(raw, out g)) return "b." + g.ToString("D");
            Match m = Regex.Match(raw, @"/projects/(?:b\.)?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
            if (m.Success) return "b." + Guid.Parse(m.Groups[1].Value).ToString("D");
            throw new ToolRefusal("project_id '" + raw + "' is not b.<guid>, a GUID, or a URL with /projects/<guid>. Nothing was read.");
        }

        private static string HubId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();
            Guid g;
            if (raw.StartsWith("b.", StringComparison.Ordinal) && Guid.TryParse(raw.Substring(2), out g)) return "b." + g.ToString("D");
            if (Guid.TryParse(raw, out g)) return "b." + g.ToString("D");
            throw new ToolRefusal("hub_id must be b.<account guid> or the account GUID. Nothing was read.");
        }

        // ================================================================================
        // OpenCDE (buildingSMART Foundation API 1.x + Documents API 1.0)
        // ================================================================================

        private sealed class OpenCdeServer
        {
            public string FoundationBase, DocumentsBase, DocumentsVersion;
            public JObject Auth;
            public string Error;
        }

        private static JObject OpenCde(string op, JObject args, CancellationToken ct, CdeCloudEnvironment env)
        {
            JObject context = InformationContainerTool.LoadContext(args);
            string server = Str(args, "server_url");
            Uri serverUri;
            if (string.IsNullOrWhiteSpace(server) || !Uri.TryCreate(server.Trim(), UriKind.Absolute, out serverUri) || serverUri.Scheme != Uri.UriSchemeHttps)
                throw new ToolRefusal("provider=opencde needs server_url: the https base URL of the OpenCDE server (where /foundation/versions lives). Nothing was read.");
            if (!string.IsNullOrEmpty(serverUri.UserInfo))
                throw new ToolRefusal("server_url must not carry credentials. Nothing was read.");
            int maxCalls = Int(args, "max_calls", DefaultMaxCalls, 1, MaxMaxCalls);
            string token = env.Variable(OpenCdeTokenName);
            bool tokenConfigured = !string.IsNullOrWhiteSpace(token);

            List<string> ids = null;
            ContainerNaming naming = null;
            string documentId = null;
            if (op == "inspect")
            {
                ids = StringList(args, "document_ids");
                if (ids == null || ids.Count == 0)
                    throw new ToolRefusal("provider=opencde inspect needs document_ids. The OpenCDE Documents API 1.0 cannot enumerate a " +
                                          "project: documents are chosen in the CDE's own web UI (select-documents), so this reader reads " +
                                          "the ids it is given. Nothing was read.");
                if (ids.Count > MaxDocumentIds) throw new ToolRefusal("document_ids holds at most " + MaxDocumentIds + " ids. Nothing was read.");
                naming = InformationContainerTool.OptionalRules(args, context) ?? InformationContainer.ParseNaming(new JObject(), null);
            }
            else if (op == "versions")
            {
                documentId = Str(args, "document_id");
                if (string.IsNullOrWhiteSpace(documentId))
                    throw new ToolRefusal("provider=opencde versions needs document_id. Nothing was read.");
            }
            if (op != "list_states" && !tokenConfigured)
                throw new ToolRefusal("OpenCDE authentication is not configured. Set " + OpenCdeTokenName + " (an OAuth2 access token issued " +
                                      "by the server's oauth2_token_url) in the MCP server's environment. Credentials are never accepted in " +
                                      "tool arguments. This reader does not run the interactive OAuth2 browser flow. No request was made.");

            using (var http = new CdeCloudHttp(env, maxCalls, ct))
            {
                http.AllowHost(serverUri.Host);
                OpenCdeServer s = Discover(http, serverUri);
                var result = new JObject
                {
                    ["operation"] = op, ["provider"] = "opencde", ["server_url"] = serverUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                    ["foundation_base"] = s.FoundationBase, ["documents_api"] = s.DocumentsBase == null ? null :
                        new JObject { ["version"] = s.DocumentsVersion, ["base_url"] = s.DocumentsBase },
                    ["auth_discovery"] = s.Auth, ["token_configured"] = tokenConfigured
                };
                if (s.Error != null)
                {
                    result["coverage_complete"] = false;
                    result["reason"] = s.Error;
                    result["http"] = http.Describe();
                    return result;
                }
                http.AllowHost(new Uri(s.DocumentsBase).Host);
                http.BearerToken = tokenConfigured ? token.Trim() : null;
                var auth = new JObject { ["mode"] = tokenConfigured ? "access_token" : "none", ["source"] = tokenConfigured ? OpenCdeTokenName : null };

                if (op == "list_states")
                {
                    result["states"] = null;
                    result["states_mappable"] = false;
                    result["coverage_complete"] = false;
                    result["reason"] = "The OpenCDE Documents API 1.0 has no folders and no non-interactive listing: documents are selected " +
                                       "in the CDE's web UI (POST /select-documents returns a browser URL). The four ISO 19650 states " +
                                       "cannot be mapped from this API; read known documents with operation=inspect and document_ids.";
                    result["auth"] = auth;
                    result["http"] = http.Describe();
                    result["note"] = "Discovery only: the Foundation endpoints were read without a token. Nothing was written.";
                    return result;
                }

                if (op == "versions")
                {
                    string versionsUrl;
                    {
                        CloudResponse latest = http.PostJson(s.DocumentsBase + "/document-versions", new JObject { ["document_ids"] = new JArray(documentId) });
                        JObject doc = latest.Ok ? (latest.Body["versions"] as JArray)?.OfType<JObject>().FirstOrDefault() : null;
                        versionsUrl = (string)doc?["links"]?["document_versions"]?["url"];
                        if (versionsUrl == null)
                        {
                            result["coverage_complete"] = false;
                            result["reason"] = latest.Ok ? "the server returned no version for document_id " + documentId : latest.Error;
                            result["auth"] = auth;
                            result["http"] = http.Describe();
                            return result;
                        }
                    }
                    CloudResponse r = http.Get(Absolute(s.DocumentsBase, versionsUrl));
                    var list = new JArray();
                    if (r.Ok)
                        foreach (JObject v in (r.Body["documents"] as JArray ?? new JArray()).OfType<JObject>().OrderByDescending(v => (int?)v["version_index"] ?? -1))
                            list.Add(DescribeDocument(v));
                    result["versions"] = list;
                    result["count"] = list.Count;
                    result["coverage_complete"] = r.Ok;
                    result["reason"] = r.Error;
                    result["auth"] = auth;
                    result["http"] = http.Describe();
                    result["note"] = "Read-only: nothing was written anywhere.";
                    return result;
                }

                // inspect
                int offset = Int(args, "offset", 0, 0, int.MaxValue);
                int limit = Int(args, "limit", DefaultLimit, 1, MaxLimit);
                DateTime asOf = AsOf(args);
                var findings = new List<JObject>();
                var records = new List<ContainerRecord>();
                var files = new JArray();
                var returned = new HashSet<string>(StringComparer.Ordinal);
                CloudResponse q = http.PostJson(s.DocumentsBase + "/document-versions", new JObject { ["document_ids"] = new JArray(ids) });
                if (q.Ok)
                    foreach (JObject v in (q.Body["versions"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        string name = (string)v["file_description"]?["name"] ?? (string)v["title"];
                        if (name == null) continue;
                        returned.Add((string)v["document_id"] ?? "");
                        var rec = new ContainerRecord { State = null, Path = name, Relative = name, Extension = Path.GetExtension(name) };
                        bool ok = ContainerInspection.CheckName(naming, rec, Path.GetFileNameWithoutExtension(name), findings);
                        records.Add(rec);
                        if (files.Count < FilesLimit)
                        {
                            JObject f = DescribeDocument(v);
                            f["state"] = null;
                            f["name_compliant"] = ok; f["container"] = rec.Name; f["status"] = rec.Status; f["revision"] = rec.Revision;
                            files.Add(f);
                        }
                    }
                var notReturned = new JArray(ids.Where(i => !returned.Contains(i)));
                result["documents_not_returned"] = notReturned;
                var coverage = new JArray
                {
                    new JObject
                    {
                        ["scope"] = "document_ids", ["requested"] = ids.Count, ["returned"] = returned.Count, ["covered"] = false,
                        ["reason"] = q.Ok
                            ? "only the listed documents were read: the Documents API cannot enumerate a project, and it carries no CDE state"
                            : q.Error
                    }
                };
                result["auth"] = auth;
                return Finish(result, args, context, records, naming, asOf, findings, offset, limit, new JObject(), coverage, false,
                              records.Count, files, null, http,
                              "Read-only: nothing was written anywhere. OpenCDE documents carry no ISO 19650 state here (state=null); " +
                              "status and revision come from the file name. coverage_complete is false by construction.");
            }
        }

        private static JObject DescribeDocument(JObject v) => new JObject
        {
            ["document_id"] = v["document_id"], ["title"] = v["title"], ["path"] = v["file_description"]?["name"],
            ["version_number"] = v["version_number"], ["version_index"] = v["version_index"],
            ["last_modified"] = v["creation_date"], ["size_bytes"] = v["file_description"]?["size_in_bytes"],
            ["versions_url"] = v["links"]?["document_versions"]?["url"]
        };

        private static OpenCdeServer Discover(CdeCloudHttp http, Uri server)
        {
            var s = new OpenCdeServer();
            string root = server.GetLeftPart(UriPartial.Path).TrimEnd('/');
            CloudResponse versions = http.Get(root + "/foundation/versions", withToken: false);
            if (!versions.Ok) { s.Error = "GET /foundation/versions failed: " + versions.Error; return s; }
            JObject docs = null, foundation = null;
            foreach (JObject v in (versions.Body["versions"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string api = (string)v["api_id"];
                if (api == "documents" && ((string)v["version_id"] ?? "").StartsWith("1", StringComparison.Ordinal)) docs = docs ?? v;
                if (api == "foundation") foundation = foundation ?? v;
            }
            s.FoundationBase = TrimSlash((string)foundation?["api_base_url"]) ?? (foundation != null ? root + "/foundation/" + (string)foundation["version_id"] : null);
            if (docs == null) { s.Error = "the server's /foundation/versions lists no Documents API 1.x"; return s; }
            s.DocumentsVersion = (string)docs["version_id"];
            s.DocumentsBase = TrimSlash((string)docs["api_base_url"]);
            Uri db;
            if (s.DocumentsBase == null || !Uri.TryCreate(s.DocumentsBase, UriKind.Absolute, out db) || db.Scheme != Uri.UriSchemeHttps)
            {
                s.Error = "the Documents API entry has no https api_base_url";
                s.DocumentsBase = null;
                return s;
            }
            if (s.FoundationBase != null)
            {
                Uri fb;
                if (Uri.TryCreate(s.FoundationBase, UriKind.Absolute, out fb) && string.Equals(fb.Host, server.Host, StringComparison.OrdinalIgnoreCase))
                {
                    CloudResponse auth = http.Get(s.FoundationBase + "/auth", withToken: false);
                    if (auth.Ok)
                        s.Auth = new JObject
                        {
                            ["oauth2_token_url"] = auth.Body["oauth2_token_url"], ["oauth2_auth_url"] = auth.Body["oauth2_auth_url"],
                            ["supported_oauth2_flows"] = auth.Body["supported_oauth2_flows"], ["http_basic_supported"] = auth.Body["http_basic_supported"]
                        };
                }
            }
            return s;
        }

        private static string TrimSlash(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('/');

        private static string Absolute(string baseUrl, string url)
        {
            Uri u;
            if (Uri.TryCreate(url, UriKind.Absolute, out u)) return u.AbsoluteUri;
            return new Uri(new Uri(baseUrl + "/"), url).AbsoluteUri;
        }

        // ================================================================================
        // small helpers
        // ================================================================================

        private static string Esc(string s) => Uri.EscapeDataString(s);

        private static DateTime AsOf(JObject args)
        {
            string text = Str(args, "as_of");
            if (text == null) return DateTime.UtcNow.Date;
            DateTime d;
            if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                throw new ToolRefusal("as_of must be YYYY-MM-DD. Nothing was read.");
            return d;
        }

        private static string Str(JObject args, string key)
        {
            JToken t = args[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.String) throw new ToolRefusal(key + " must be a string. Nothing was read.");
            return (string)t;
        }

        private static List<string> StringList(JObject args, string key)
        {
            JToken t = args[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (!(t is JArray a) || a.Any(x => x.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)x)))
                throw new ToolRefusal(key + " must be an array of non-empty strings. Nothing was read.");
            return a.Select(x => (string)x).Distinct(StringComparer.Ordinal).ToList();
        }

        private static bool Bool(JObject args, string key, bool dflt)
        {
            JToken t = args[key];
            if (t == null || t.Type == JTokenType.Null) return dflt;
            if (t.Type != JTokenType.Boolean) throw new ToolRefusal(key + " must be a boolean. Nothing was read.");
            return (bool)t;
        }

        private static int Int(JObject args, string key, int dflt, int min, int max)
        {
            JToken t = args[key];
            if (t == null || t.Type == JTokenType.Null) return dflt;
            if (t.Type != JTokenType.Integer) throw new ToolRefusal(key + " must be an integer. Nothing was read.");
            long v = (long)t;
            if (v < min || v > max) throw new ToolRefusal(key + " must be between " + min + " and " + max + ". Nothing was read.");
            return (int)v;
        }
    }
}
