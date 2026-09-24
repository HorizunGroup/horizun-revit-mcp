// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_information_container - a HOST-RESIDENT tool. It never touches Revit and
// never calls a cloud API: every file it reads or writes is in a local or synced
// folder the caller names. The ISO 19650 concepts (container name, suitability
// status, revision, CDE states) are modelled; every concrete rule - fields, patterns,
// codes, folders - arrives as an argument or from the caller's project-context.json.
//
// Eight operations:
//   name        compose and validate a name. Read-only.
//   stamp       write "<file>.container.json" for an existing file. dry_run by default.
//   verify      does the file still match its sidecar (hash, name, bytes). Read-only.
//   inspect     walk the CDE state folders and report what does not hold, and cross
//               the MIDP deliverables when they are declared. Read-only, paginated.
//   transition  COPY a container to the next CDE state, rename, stamp, verify by
//               SHA-256 and append to <root>/.horizun/cde-transitions.jsonl. Never moves,
//               never deletes, never overwrites. dry_run by default.
//   transmittal   issue a numbered transmittal (<project>-TR-0001) for sealed containers
//                 in one state: <root>/.horizun/transmittals/<id>.json/.md/.csv, each hash
//                 re-measured against its sidecar. dry_run by default.
//                 (InformationContainerTool.Register.cs)
//   record_review append the receiving party's review outcome to
//                 <root>/.horizun/reviews.jsonl. Moves nothing. dry_run by default.
//   register      the approval register: transitions + transmittals + reviews joined per
//                 container, with the incoherences between them. Read-only, paginated.
//
// PERMISSION. Classified ExternalSideEffectOnRequest: every profile may call it, and
// the operations that write (stamp, transition, transmittal, record_review with
// dry_run=false) ask
// Settings.AllowsExternalSideEffect before they touch a file.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static partial class InformationContainerTool
    {
        internal const string ToolName = "horizun_information_container";
        internal const string LogRelativePath = ".horizun/cde-transitions.jsonl";
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;
        private const int DefaultMaxFiles = 20000;
        private const int MaxMaxFiles = 200000;

        private static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
        {
            "operation", "information_container", "naming", "file_path", "root", "states", "project_context_path",
            "deliverables", "as_of", "offset", "limit", "max_files", "from_state", "to_state", "status", "revision",
            "approved_by", "note", "dry_run", "source_document", "revit_year",
            // transmittal / record_review / register
            "project", "state", "file_paths", "sender", "recipients", "purpose", "transmittal_id", "container",
            "outcome", "comments", "reviewed_by", "reviewer_organization", "reviewed_on", "since", "until"
        };

        internal static JObject Handle(JObject args, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            args = args ?? new JObject();
            foreach (JProperty p in args.Properties())
                if (!Keys.Contains(p.Name))
                    throw new ToolRefusal("Unknown argument '" + p.Name + "'. Nothing was read or written.");
            string op = (string)args["operation"];
            try
            {
                switch (op)
                {
                    case "name": return Name(args);
                    case "stamp": return Stamp(args);
                    case "verify": return Verify(args);
                    case "inspect": return Inspect(args, ct, DateTime.UtcNow);
                    case "transition": return Transition(args, ct);
                    case "transmittal": return Transmittal(args, ct, DateTime.UtcNow);
                    case "record_review": return RecordReview(args, ct, DateTime.UtcNow);
                    case "register": return Register(args, ct);
                    default:
                        throw new ToolRefusal("operation must be one of name, stamp, verify, inspect, transition, transmittal, " +
                                              "record_review, register. Nothing was read or written.");
                }
            }
            catch (ContainerRuleException ex) { throw new ToolRefusal(ex.Message + " Nothing was written."); }
        }

        // ---- name ----------------------------------------------------------------------

        private static JObject Name(JObject args)
        {
            ContainerSpec spec = InformationContainer.ParseSpec(Required(args, "information_container"));
            ContainerValidation v = InformationContainer.Validate(spec, false);
            JObject result = v.ToJson();
            result["operation"] = "name";
            result["status"] = spec.Status;
            result["revision"] = spec.Revision;
            result["naming"] = spec.Naming.Describe();
            result["note"] = v.Valid
                ? "Composed and validated; nothing was written."
                : "The name does not hold: every problem is listed. Nothing was written.";
            return result;
        }

        // ---- stamp ---------------------------------------------------------------------

        private static JObject Stamp(JObject args)
        {
            ContainerSpec spec = InformationContainer.ParseSpec(Required(args, "information_container"));
            string file = AbsoluteExistingFile(args, "file_path");
            bool dryRun = DryRun(args);
            ContainerValidation v = InformationContainer.Validate(spec, true);
            if (!v.Valid)
                throw new ToolRefusal("The container does not validate: " + Describe(v.Problems) + ". Nothing was written.");
            string stem = Path.GetFileNameWithoutExtension(file);
            if (!string.Equals(stem, v.FileStem, StringComparison.Ordinal))
                throw new ToolRefusal("The file is named '" + stem + "' but the container composes '" + v.FileStem +
                                      "'. A sidecar that disagrees with its file's name would be a false record; rename " +
                                      "the file (or fix the fields) first. Nothing was written.");

            string sidecarPath = InformationContainer.SidecarPath(file);
            if (File.Exists(sidecarPath))
            {
                JObject existing = InformationContainer.VerifyFile(file);
                JObject sc = existing["sidecar"] as JObject;
                bool same = (string)existing["verdict"] == "match" && sc != null &&
                            (string)sc["name"] == v.Name && (string)sc["status"] == spec.Status &&
                            (string)sc["revision"] == spec.Revision;
                if (same)
                    return new JObject
                    {
                        ["operation"] = "stamp", ["dry_run"] = dryRun, ["already_stamped"] = true, ["written"] = false,
                        ["verified"] = true, ["sidecar_path"] = sidecarPath, ["verification"] = existing,
                        ["note"] = "A sidecar with this name, status and revision already describes these exact bytes. Nothing was written."
                    };
                throw new ToolRefusal("A different sidecar already exists at " + sidecarPath + " (verdict " + (string)existing["verdict"] +
                                      "). Sidecars are never overwritten by this tool; rename or remove it yourself if it is stale. Nothing was written.");
            }

            long bytes;
            string sha = InformationContainer.Sha256File(file, out bytes);
            JObject sidecar = InformationContainer.BuildSidecar(spec, v, file, bytes, sha, ToolName,
                OptionalString(args, "source_document"), OptionalString(args, "revit_year"), DateTime.UtcNow,
                new JObject { ["state"] = JValue.CreateNull() });

            var result = new JObject
            {
                ["operation"] = "stamp", ["dry_run"] = dryRun, ["file"] = file, ["name"] = v.Name,
                ["sidecar_path"] = sidecarPath, ["warnings"] = v.Warnings, ["naming"] = spec.Naming.Describe()
            };
            if (dryRun)
            {
                result["written"] = false;
                result["sidecar_preview"] = sidecar;
                result["note"] = "Rehearsal: the container validates and the sidecar above would be written. Nothing was written.";
                return result;
            }
            RequireExternalWrite("stamp");
            result["verification"] = InformationContainer.WriteSidecarVerified(file, sidecar);
            result["written"] = true;
            result["verified"] = true;
            result["sidecar"] = sidecar;
            result["note"] = "The sidecar was written, read back exactly and the file re-hashed afterwards.";
            return result;
        }

        // ---- verify --------------------------------------------------------------------

        private static JObject Verify(JObject args)
        {
            string file = AbsolutePath(args, "file_path");
            JObject result = InformationContainer.VerifyFile(file);
            result["operation"] = "verify";
            ContainerNaming rules = OptionalRules(args, null);
            JObject sidecar = result["sidecar"] as JObject;
            if (rules != null && sidecar != null)
            {
                var spec = new ContainerSpec { Naming = rules, Status = (string)sidecar["status"], Revision = (string)sidecar["revision"] };
                foreach (JProperty p in ((sidecar["fields"] as JObject) ?? new JObject()).Properties()) spec.Fields[p.Name] = (string)p.Value;
                ContainerValidation v = InformationContainer.Validate(spec, true);
                result["rules_checked"] = true;
                result["rules_check"] = v.ToJson();
            }
            else result["rules_checked"] = false;
            return result;
        }

        // ---- inspect -------------------------------------------------------------------

        internal static JObject Inspect(JObject args, CancellationToken ct, DateTime nowUtc)
        {
            JObject context = LoadContext(args);
            Cde cde = ResolveCde(args, context);
            ContainerNaming naming = OptionalRules(args, context) ?? InformationContainer.ParseNaming(new JObject(), null);
            int offset = IntArg(args, "offset", 0, 0, int.MaxValue);
            int limit = IntArg(args, "limit", DefaultLimit, 1, MaxLimit);
            int maxFiles = IntArg(args, "max_files", DefaultMaxFiles, 1, MaxMaxFiles);
            DateTime asOf = nowUtc.Date;
            string asOfText = OptionalString(args, "as_of");
            if (asOfText != null && !DateTime.TryParseExact(asOfText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out asOf))
                throw new ToolRefusal("as_of must be YYYY-MM-DD. Nothing was read.");

            var findings = new List<JObject>();
            var records = new List<ContainerRecord>();
            var perState = new JObject();
            var coverage = new JArray();
            int walked = 0;
            bool truncatedWalk = false;

            foreach (string state in InformationContainer.States)
            {
                string folder;
                if (!cde.States.TryGetValue(state, out folder))
                {
                    coverage.Add(new JObject { ["state"] = state, ["covered"] = false, ["reason"] = "no folder declared for this state" });
                    continue;
                }
                if (!Directory.Exists(folder))
                {
                    coverage.Add(new JObject { ["state"] = state, ["folder"] = folder, ["covered"] = false, ["reason"] = "the folder does not exist" });
                    continue;
                }
                int files = 0, sidecars = 0, compliant = 0, stamped = 0;
                var sidecarPaths = new List<string>();
                bool stateComplete = true;
                foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                                                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    string rel = Path.GetRelativePath(folder, path);
                    if (Ignored(rel)) continue;
                    if (walked >= maxFiles) { truncatedWalk = true; stateComplete = false; break; }
                    walked++;
                    if (InformationContainer.IsSidecar(path)) { sidecars++; sidecarPaths.Add(path); continue; }
                    files++;
                    var rec = new ContainerRecord { State = state, Path = path, Relative = rel, Extension = Path.GetExtension(path) };
                    if (ContainerInspection.CheckName(naming, rec, Path.GetFileNameWithoutExtension(path), findings)) compliant++;

                    if (File.Exists(InformationContainer.SidecarPath(path)))
                    {
                        rec.HasSidecar = true;
                        stamped++;
                        JObject check = InformationContainer.VerifyFile(path);
                        string verdict = (string)check["verdict"];
                        JObject sc = check["sidecar"] as JObject;
                        if (sc != null)
                        {
                            rec.Name = (string)sc["name"] ?? rec.Name;
                            rec.Status = (string)sc["status"] ?? rec.Status;
                            rec.Revision = (string)sc["revision"] ?? rec.Revision;
                            rec.Sha256 = verdict == "modified" ? null : (string)sc["sha256"];
                            string statusState = InformationContainer.StateOfStatus(rec.Status);
                            if (statusState != null && state != InformationContainer.StateArchived && statusState != state)
                                findings.Add(ContainerInspection.Finding("state_status_mismatch", state, path, new JObject
                                {
                                    ["status"] = rec.Status, ["status_belongs_to"] = statusState,
                                    ["reason"] = "the sidecar's status belongs to the " + statusState + " state, the file sits in " + state
                                }));
                        }
                        if (verdict == "modified")
                            findings.Add(ContainerInspection.Finding("hash_mismatch", state, path, new JObject
                            {
                                ["mismatches"] = check["mismatches"],
                                ["reason"] = "the file's bytes changed after it was sealed"
                            }));
                        else if (verdict != "match")
                            findings.Add(ContainerInspection.Finding("sidecar_inconsistent", state, path, new JObject
                            {
                                ["verdict"] = verdict, ["mismatches"] = check["mismatches"], ["reason"] = check["reason"]
                            }));
                    }
                    else findings.Add(ContainerInspection.Finding("missing_sidecar", state, path, null));
                    records.Add(rec);
                }
                foreach (string sc in sidecarPaths)
                {
                    string target = sc.Substring(0, sc.Length - InformationContainer.SidecarSuffix.Length);
                    if (!File.Exists(target))
                        findings.Add(ContainerInspection.Finding("orphan_sidecar", state, sc, new JObject { ["expected_file"] = target }));
                }
                perState[state] = new JObject
                {
                    ["folder"] = folder, ["files"] = files, ["sidecars"] = sidecars, ["compliant_names"] = compliant,
                    ["stamped"] = stamped, ["complete"] = stateComplete
                };
                coverage.Add(new JObject { ["state"] = state, ["folder"] = folder, ["covered"] = stateComplete,
                                           ["reason"] = stateComplete ? null : "max_files reached; the walk stopped here" });
                if (truncatedWalk) break;
            }

            ContainerInspection.CrossStates(records, findings);
            JArray deliverables = ContainerInspection.Deliverables(
                args["deliverables"] as JArray ?? context?["deliverables"] as JArray, records, naming, asOf, findings);

            var result = new JObject
            {
                ["operation"] = "inspect",
                ["root"] = cde.Root,
                ["as_of"] = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["naming"] = naming.Describe(),
                ["states"] = perState,
                ["coverage"] = coverage,
                ["coverage_complete"] = !truncatedWalk && coverage.All(c => (bool)c["covered"]),
                ["files_walked"] = walked
            };
            ContainerInspection.Page(findings, offset, limit, result);
            result["deliverables"] = deliverables;
            result["note"] = "Read-only: nothing was written. 'covered=false' is a folder that was not read, never an empty one.";
            return result;
        }

        private static bool Ignored(string relative)
        {
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => string.Equals(p, ".horizun", StringComparison.OrdinalIgnoreCase))) return true;
            string name = parts[parts.Length - 1];
            return name.StartsWith("~$", StringComparison.Ordinal) ||
                   name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase);
        }

        // ---- transition ----------------------------------------------------------------

        private static readonly string[][] AllowedTransitions =
        {
            new[] { InformationContainer.StateWip, InformationContainer.StateShared },
            new[] { InformationContainer.StateShared, InformationContainer.StatePublished },
            new[] { InformationContainer.StatePublished, InformationContainer.StateArchived }
        };

        private static JObject Transition(JObject args, CancellationToken ct)
        {
            JObject context = LoadContext(args);
            Cde cde = ResolveCde(args, context);
            string from = RequiredString(args, "from_state"), to = RequiredString(args, "to_state");
            if (!AllowedTransitions.Any(t => t[0] == from && t[1] == to))
                throw new ToolRefusal("A transition goes wip->shared, shared->published or published->archived; '" + from + "->" + to +
                                      "' is not one of them. Nothing was written.");
            bool dryRun = DryRun(args);
            string approvedBy = OptionalString(args, "approved_by");
            if (from == InformationContainer.StateShared && to == InformationContainer.StatePublished && string.IsNullOrWhiteSpace(approvedBy))
                throw new ToolRefusal("shared->published needs approved_by: the name of whoever authorised the publication. " +
                                      "It is recorded in the sidecar and the transition log. Nothing was written.");

            string fromFolder = StateFolder(cde, from), toFolder = StateFolder(cde, to);
            string source = AbsoluteExistingFile(args, "file_path");
            string rel = Path.GetRelativePath(fromFolder, source);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                throw new ToolRefusal("file_path is not inside the " + from + " folder (" + fromFolder + "). Nothing was written.");

            // The source must be SEALED and still match its seal: a transition carries the
            // record forward, and a record of different bytes is not a record.
            JObject sourceCheck = InformationContainer.VerifyFile(source);
            if ((string)sourceCheck["verdict"] != "match")
                throw new ToolRefusal("The source does not match its sidecar (verdict " + (string)sourceCheck["verdict"] + "). " +
                                      ((string)sourceCheck["verdict"] == "missing_sidecar"
                                          ? "Stamp it first (operation=stamp)."
                                          : "Re-stamp or restore it before a transition.") + " Nothing was written.");
            JObject sourceSidecar = (JObject)sourceCheck["sidecar"];

            ContainerNaming rules = OptionalRules(args, context) ?? InformationContainer.ParseNaming(new JObject(), null);
            var sourceNaming = (JObject)sourceSidecar["naming"];
            rules.FieldOrder = ((JArray)sourceNaming["field_order"]).Select(t => (string)t).ToList();
            rules.Separator = (string)sourceNaming["separator"];
            rules.FileNameMode = (string)sourceNaming["file_name"] ?? InformationContainer.FileNameName;
            var spec = new ContainerSpec
            {
                Naming = rules,
                Status = OptionalString(args, "status") ?? (string)sourceSidecar["status"],
                Revision = OptionalString(args, "revision") ?? (string)sourceSidecar["revision"],
                Title = (string)sourceSidecar["title"]
            };
            foreach (JProperty p in ((JObject)sourceSidecar["fields"]).Properties()) spec.Fields[p.Name] = (string)p.Value;
            ContainerValidation v = InformationContainer.Validate(spec, true);
            if (!v.Valid)
                throw new ToolRefusal("The container in its new state does not validate: " + Describe(v.Problems) + ". Nothing was written.");
            var warnings = new JArray(v.Warnings);
            string statusState = InformationContainer.StateOfStatus(spec.Status);
            if (to != InformationContainer.StateArchived)
            {
                if (statusState == null)
                    warnings.Add(new JObject { ["field"] = "status", ["code"] = "state_not_assessed",
                        ["reason"] = "status '" + spec.Status + "' is not an ISO 19650-2 code this tool maps to a state; its fit with " + to + " was not assessed" });
                else if (statusState != to)
                    throw new ToolRefusal("status " + spec.Status + " belongs to the " + statusState + " state, not " + to +
                                          ". Pass the status the container carries in " + to + ". Nothing was written.");
            }
            string expectedApprover = (string)context?["cde"]?["approvals"]?[from + "_to_" + to];
            if (approvedBy != null && expectedApprover != null &&
                !string.Equals(approvedBy.Trim(), expectedApprover.Trim(), StringComparison.OrdinalIgnoreCase))
                warnings.Add(new JObject { ["field"] = "approved_by", ["code"] = "approver_differs",
                    ["reason"] = "the project context names '" + expectedApprover + "' for " + from + "->" + to });

            string destDir = Path.Combine(toFolder, Path.GetDirectoryName(rel) ?? "");
            string dest = Path.Combine(destDir, v.FileStem + Path.GetExtension(source));
            string destSidecar = InformationContainer.SidecarPath(dest);
            string logPath = Path.Combine(cde.Root ?? toFolder, ".horizun", "cde-transitions.jsonl");
            string sourceSha = (string)sourceCheck["sha256"];

            var result = new JObject
            {
                ["operation"] = "transition", ["dry_run"] = dryRun, ["from_state"] = from, ["to_state"] = to,
                ["source"] = source, ["source_sha256"] = sourceSha, ["destination"] = dest, ["destination_sidecar"] = destSidecar,
                ["name"] = v.Name, ["status"] = spec.Status, ["revision"] = spec.Revision, ["approved_by"] = approvedBy,
                ["expected_approver"] = expectedApprover, ["log_path"] = logPath, ["warnings"] = warnings
            };

            // Replay: the same transition already landed. Answer with what is on disk.
            if (File.Exists(dest) || File.Exists(destSidecar))
            {
                JObject existing = InformationContainer.VerifyFile(dest);
                JObject sc = existing["sidecar"] as JObject;
                bool same = (string)existing["verdict"] == "match" && sc != null &&
                            string.Equals((string)existing["sha256"], sourceSha, StringComparison.OrdinalIgnoreCase) &&
                            (string)sc["status"] == spec.Status && (string)sc["revision"] == spec.Revision &&
                            (string)sc["state"] == to && (string)sc["transitioned_from"]?["sha256"] == sourceSha;
                if (same)
                {
                    result["already_transitioned"] = true;
                    result["written"] = false;
                    result["verified"] = true;
                    result["verification"] = existing;
                    result["note"] = "This transition already landed: the destination and its sidecar match. Nothing was written.";
                    return result;
                }
                throw new ToolRefusal("The destination already exists (" + (File.Exists(dest) ? dest : destSidecar) +
                                      ") and is not this transition. A transition never overwrites. Nothing was written.");
            }

            var extra = new JObject
            {
                ["state"] = to,
                ["approved_by"] = approvedBy,
                ["note"] = OptionalString(args, "note"),
                ["transitioned_from"] = new JObject
                {
                    ["state"] = from, ["file"] = Path.GetFileName(source), ["sha256"] = sourceSha,
                    ["status"] = sourceSidecar["status"], ["revision"] = sourceSidecar["revision"]
                },
                ["transition_id"] = null
            };
            if (dryRun)
            {
                result["written"] = false;
                result["would_create_directory"] = !Directory.Exists(destDir);
                result["note"] = "Rehearsal: every check passed. Nothing was copied, stamped or logged.";
                return result;
            }

            RequireExternalWrite("transition");
            ct.ThrowIfCancellationRequested();
            string id = Guid.NewGuid().ToString("N");
            extra["transition_id"] = id;
            var steps = new JArray();
            Directory.CreateDirectory(destDir);

            // 1. Copy through a temporary name, prove the bytes, then a no-overwrite move.
            string tmp = dest + "." + id + ".tmp";
            long destBytes;
            try
            {
                File.Copy(source, tmp, false);
                string tmpSha = InformationContainer.Sha256File(tmp, out destBytes);
                if (!string.Equals(tmpSha, sourceSha, StringComparison.OrdinalIgnoreCase))
                    throw new ToolRefusal("The copy's SHA-256 (" + tmpSha + ") is not the source's (" + sourceSha + "). The copy was discarded; nothing was written.");
                File.Move(tmp, dest, false);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            steps.Add(new JObject { ["step"] = "copy", ["done"] = true, ["path"] = dest });

            // 2. The sidecar, read back and the copy re-hashed inside WriteSidecarVerified.
            JObject sidecar = InformationContainer.BuildSidecar(spec, v, dest, destBytes, sourceSha, ToolName,
                (string)sourceSidecar["source_document"], (string)sourceSidecar["revit_year"], DateTime.UtcNow, extra);
            try { result["sidecar_verification"] = InformationContainer.WriteSidecarVerified(dest, sidecar); }
            catch (IOException ex)
            {
                steps.Add(new JObject { ["step"] = "sidecar", ["done"] = false, ["reason"] = ex.Message });
                throw new ToolRefusal("The copy exists at " + dest + " but its sidecar could not be written and verified: " + ex.Message +
                                      " Nothing was logged. The copy is left in place for you to inspect; it is not a published record.");
            }
            steps.Add(new JObject { ["step"] = "sidecar", ["done"] = true, ["path"] = destSidecar });

            // 3. The append-only log, re-read for this transition's line.
            var line = new JObject
            {
                ["transition_id"] = id, ["utc"] = sidecar["created_utc"], ["from_state"] = from, ["to_state"] = to,
                ["source"] = source, ["source_sha256"] = sourceSha, ["destination"] = dest, ["destination_sha256"] = sourceSha,
                ["name"] = v.Name, ["status"] = spec.Status, ["revision"] = spec.Revision, ["approved_by"] = approvedBy,
                ["note"] = extra["note"], ["tool"] = ToolName
            };
            bool logged = AppendLogVerified(logPath, line, id, out string logProblem);
            steps.Add(new JObject { ["step"] = "log", ["done"] = logged, ["path"] = logPath, ["reason"] = logProblem });

            JObject final = InformationContainer.VerifyFile(dest);
            bool verified = (string)final["verdict"] == "match";
            result["written"] = true;
            result["transition_id"] = id;
            result["steps"] = steps;
            result["verification"] = final;
            result["verified"] = verified && logged;
            result["note"] = verified && logged
                ? "Copied, stamped and logged; the destination was re-hashed against the source. The source was not touched."
                : "The copy and its sidecar exist, but " + (logged ? "the final re-verification did not match" : "the log line could not be confirmed: " + logProblem) + ".";
            return result;
        }

        private static bool AppendLogVerified(string logPath, JObject line, string id, out string problem)
        {
            problem = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                string text = line.ToString(Formatting.None) + "\n";
                using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                foreach (string l in File.ReadLines(logPath).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    if (l.IndexOf(id, StringComparison.Ordinal) >= 0) return true;
                }
                problem = "the line was appended but is not found on re-read";
                return false;
            }
            catch (IOException ex) { problem = ex.Message; return false; }
            catch (UnauthorizedAccessException ex) { problem = ex.Message; return false; }
        }

        // ---- CDE and project context ----------------------------------------------------

        private sealed class Cde
        {
            public string Root;
            public Dictionary<string, string> States = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal static JObject LoadContext(JObject args)
        {
            string path = OptionalString(args, "project_context_path");
            if (path == null) return null;
            if (!Path.IsPathRooted(path)) throw new ToolRefusal("project_context_path must be absolute. Nothing was read.");
            if (!File.Exists(path)) throw new ToolRefusal("project_context_path not found: " + path + ". Nothing was read.");
            try
            {
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path, Encoding.UTF8))) { DateParseHandling = DateParseHandling.None })
                {
                    var o = JToken.ReadFrom(reader) as JObject;
                    if (o == null) throw new ToolRefusal("project_context_path is not a JSON object. Nothing was read.");
                    if (o["schema_version"] == null || o["schema_version"].Type != JTokenType.Integer || (int)o["schema_version"] != 1)
                        throw new ToolRefusal("project_context_path: schema_version must be 1. Nothing was read.");
                    return o;
                }
            }
            catch (JsonException ex) { throw new ToolRefusal("project_context_path is not valid JSON: " + ex.Message); }
        }

        private static Cde ResolveCde(JObject args, JObject context, bool requireStates = true)
        {
            var cde = new Cde { Root = OptionalString(args, "root") ?? (string)context?["cde"]?["root"] };
            JObject states = args["states"] as JObject ?? context?["cde"]?["states"] as JObject;
            if (!requireStates && (states == null || !states.Properties().Any()))
            {
                if (cde.Root != null && !Path.IsPathRooted(cde.Root)) throw new ToolRefusal("root must be absolute. Nothing was read.");
                return cde;
            }
            if (states == null || !states.Properties().Any())
                throw new ToolRefusal("Declare the CDE state folders: 'states' {wip, shared, published, archived} (with 'root' when they are relative), " +
                                      "or a project_context_path whose cde.states does. Nothing was read.");
            if (cde.Root != null && !Path.IsPathRooted(cde.Root)) throw new ToolRefusal("root must be absolute. Nothing was read.");
            foreach (JProperty p in states.Properties())
            {
                if (Array.IndexOf(InformationContainer.States, p.Name) < 0)
                    throw new ToolRefusal("states has an unknown state '" + p.Name + "'; the CDE states are wip, shared, published, archived. Nothing was read.");
                if (p.Value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)p.Value))
                    throw new ToolRefusal("states." + p.Name + " must be a folder path. Nothing was read.");
                string folder = (string)p.Value;
                if (!Path.IsPathRooted(folder))
                {
                    if (cde.Root == null) throw new ToolRefusal("states." + p.Name + " is relative and no root was given. Nothing was read.");
                    folder = Path.Combine(cde.Root, folder);
                }
                cde.States[p.Name] = Path.GetFullPath(folder);
            }
            return cde;
        }

        private static string StateFolder(Cde cde, string state)
        {
            string folder;
            if (!cde.States.TryGetValue(state, out folder))
                throw new ToolRefusal("No folder is declared for the " + state + " state. Nothing was written.");
            if (!Directory.Exists(folder))
                throw new ToolRefusal("The " + state + " folder does not exist: " + folder + ". CDE state folders are never created implicitly. Nothing was written.");
            return folder;
        }

        /// <summary>naming argument, else information_container's rules, else the context's naming, else null.</summary>
        internal static ContainerNaming OptionalRules(JObject args, JObject context)
        {
            if (args["naming"] is JObject naming) return InformationContainer.ParseNaming(naming, null);
            if (args["information_container"] is JObject container)
            {
                var rulesOnly = (JObject)container.DeepClone();
                foreach (string k in new[] { "fields", "status", "revision", "title" }) rulesOnly.Remove(k);
                return InformationContainer.ParseNaming(rulesOnly, null);
            }
            if (context == null) context = LoadContext(args);
            if (context?["naming"] is JObject) return InformationContainer.NamingFromProjectContext(context);
            return null;
        }

        // ---- small helpers ------------------------------------------------------------

        private static void RequireExternalWrite(string op)
        {
            string refusal;
            if (!Settings.AllowsExternalSideEffect(out refusal))
                throw new ToolRefusal(op + " with dry_run=false writes files outside the model, and that needs the profile: " + refusal +
                                      " The rehearsal (dry_run=true) and the read-only operations stay available.");
        }

        private static string Describe(JArray problems) =>
            string.Join("; ", problems.Select(p => (string)p["field"] + ": " + (string)p["reason"]));

        private static JToken Required(JObject args, string key)
        {
            JToken t = args[key];
            if (t == null || t.Type == JTokenType.Null) throw new ToolRefusal(key + " is required for this operation. Nothing was read or written.");
            return t;
        }

        private static string RequiredString(JObject args, string key)
        {
            string s = OptionalString(args, key);
            if (string.IsNullOrWhiteSpace(s)) throw new ToolRefusal(key + " is required for this operation. Nothing was written.");
            return s;
        }

        private static string OptionalString(JObject args, string key)
        {
            JToken t = args[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.String) throw new ToolRefusal(key + " must be a string.");
            return (string)t;
        }

        private static string AbsolutePath(JObject args, string key)
        {
            string path = RequiredString(args, key);
            if (!Path.IsPathRooted(path)) throw new ToolRefusal(key + " must be absolute. Nothing was read.");
            return Path.GetFullPath(path);
        }

        private static string AbsoluteExistingFile(JObject args, string key)
        {
            string path = AbsolutePath(args, key);
            if (!File.Exists(path)) throw new ToolRefusal(key + " does not exist: " + path + ". Nothing was written.");
            if (InformationContainer.IsSidecar(path)) throw new ToolRefusal(key + " names a sidecar, not a container file. Nothing was written.");
            return path;
        }

        private static bool DryRun(JObject args)
        {
            JToken t = args["dry_run"];
            if (t == null || t.Type == JTokenType.Null) return true;
            if (t.Type != JTokenType.Boolean) throw new ToolRefusal("dry_run must be a boolean.");
            return (bool)t;
        }

        private static int IntArg(JObject args, string key, int dflt, int min, int max)
        {
            JToken t = args[key];
            if (t == null || t.Type == JTokenType.Null) return dflt;
            if (t.Type != JTokenType.Integer) throw new ToolRefusal(key + " must be an integer.");
            long v = (long)t;
            if (v < min || v > max) throw new ToolRefusal(key + " must be between " + min + " and " + max + ".");
            return (int)v;
        }
    }
}
