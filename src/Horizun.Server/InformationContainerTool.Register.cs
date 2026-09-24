// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_information_container, second half: TRANSMITTALS and the APPROVAL REGISTER
// (ISO 19650-2 §5.6-5.7: information is issued to a receiving party for a stated
// purpose, the receiving party reviews it and records an outcome).
//
//   transmittal    a numbered, immutable record of sealed containers issued from one CDE
//                  state: <root>/.horizun/transmittals/<project>-TR-0001.json (the record)
//                  plus .md and .csv renders. Every file is re-hashed against its sidecar
//                  at issue time; a mismatch refuses the whole transmittal. The number comes
//                  from a sequence held under an EXCLUSIVE file handle, so two concurrent
//                  issues can never share a number, and a number never reuses a file.
//   record_review  one append-only line in <root>/.horizun/reviews.jsonl: accepted,
//                  accepted_with_comments or rejected, by whom, when. It moves nothing and
//                  changes no state - a rejection is a record, not an action.
//   register       reads the three records (transitions log, transmittals, reviews log)
//                  and joins them per container, reporting the incoherences between them:
//                  a transmittal whose file changed afterwards, a review of a container or
//                  transmittal nobody issued, a publication logged without approved_by.
//
// Nothing here is overwritten, nothing is deleted, and every write is read back before it
// is reported. Writing is gated exactly like stamp/transition (RequireExternalWrite).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static partial class InformationContainerTool
    {
        internal const string TransmittalSchema = "horizun.transmittal/v1";
        internal const string ReviewSchema = "horizun.review/v1";
        internal const string SequenceSchema = "horizun.transmittal-sequence/v1";
        internal const string TransmittalsRelativePath = ".horizun/transmittals";
        internal const string ReviewsRelativePath = ".horizun/reviews.jsonl";

        internal static readonly string[] ReviewOutcomes = { "accepted", "accepted_with_comments", "rejected" };
        private static readonly HashSet<string> PartyKeys = new HashSet<string>(StringComparer.Ordinal) { "name", "organization", "role" };
        private static readonly Regex ProjectCode = new Regex("^[A-Za-z0-9]{1,16}$", RegexOptions.CultureInvariant);
        private static readonly Regex TransmittalId = new Regex("^(?<p>[A-Za-z0-9]{1,16})-TR-(?<n>[0-9]{4,9})$", RegexOptions.CultureInvariant);
        private static readonly TimeSpan SequenceLockBudget = TimeSpan.FromSeconds(20);
        private const int MaxTransmittalContainers = 500;

        // Register incoherences, most consequential first (the order they are sorted in).
        private static readonly string[] IncoherenceOrder =
        {
            "transmittal_hash_changed", "transmittal_file_missing", "transmittal_path_outside_root",
            "publication_without_approval", "review_unknown_transmittal", "review_unknown_container",
            "review_container_not_in_transmittal", "transmittal_unreadable", "log_line_unreadable"
        };

        // ---- transmittal ---------------------------------------------------------------

        internal static JObject Transmittal(JObject args, CancellationToken ct, DateTime nowUtc)
        {
            JObject context = LoadContext(args);
            Cde cde = ResolveCde(args, context);
            if (cde.Root == null)
                throw new ToolRefusal("transmittal needs the CDE root (root, or cde.root in the project context): the record lives in " +
                                      "<root>/" + TransmittalsRelativePath + ". Nothing was written.");
            string state = RequiredString(args, "state");
            if (Array.IndexOf(InformationContainer.States, state) < 0)
                throw new ToolRefusal("state must be one of shared, published, archived. Nothing was written.");
            if (state == InformationContainer.StateWip)
                throw new ToolRefusal("Work in progress is not issued: under ISO 19650-2 information leaves a task team through the " +
                                      "shared state. Transition it to shared first (operation=transition). Nothing was written.");
            string folder = StateFolder(cde, state);

            string project = OptionalString(args, "project") ?? (string)context?["project"]?["code"];
            if (string.IsNullOrWhiteSpace(project))
                throw new ToolRefusal("transmittal needs 'project' (or project.code in the project context): it prefixes the number, " +
                                      "<project>-TR-0001. Nothing was written.");
            if (!ProjectCode.IsMatch(project))
                throw new ToolRefusal("project '" + project + "' must be 1 to 16 letters or digits: it becomes part of a file name. Nothing was written.");

            JObject sender = Party(args["sender"], "sender");
            var recipientsArg = args["recipients"] as JArray;
            if (recipientsArg == null || recipientsArg.Count == 0)
                throw new ToolRefusal("recipients is required: a non-empty array of {name, organization, role}. Nothing was written.");
            var recipients = new JArray(recipientsArg.Select((r, i) => Party(r, "recipients[" + i + "]")));

            string purpose = RequiredString(args, "purpose");
            ContainerNaming rules = OptionalRules(args, context) ?? InformationContainer.ParseNaming(new JObject(), null);
            string purposeDescription = null;
            foreach (var kv in rules.StatusCodes)
                if (string.Equals(kv.Key, purpose, StringComparison.Ordinal)) { purposeDescription = kv.Value; break; }
            if (purposeDescription == null)
                throw new ToolRefusal("purpose '" + purpose + "' is not one of the declared status codes (" +
                                      string.Join(", ", rules.StatusCodes.Select(k => k.Key)) + "). The purpose of issue is a " +
                                      "suitability code, and its meaning is never invented. Nothing was written.");

            List<string> files = FilePaths(args);
            string approvedBy = OptionalString(args, "approved_by");
            string note = OptionalString(args, "note");
            bool dryRun = DryRun(args);

            var problems = new List<string>();
            var warnings = new JArray();
            List<JObject> entries = MeasureContainers(files, folder, state, cde.Root, problems, warnings);
            if (problems.Count > 0)
                throw new ToolRefusal("The transmittal cannot be issued: " + string.Join("; ", problems) + ". Nothing was written.");

            foreach (JObject e in entries)
                if (!string.Equals((string)e["status"], purpose, StringComparison.Ordinal))
                    warnings.Add(new JObject
                    {
                        ["container"] = e["name"], ["code"] = "status_differs_from_purpose",
                        ["reason"] = "the container carries " + (string)e["status"] + " and is issued for " + purpose
                    });
            if (state == InformationContainer.StatePublished && string.IsNullOrWhiteSpace(approvedBy))
            {
                List<string> unapproved = entries.Where(e => string.IsNullOrWhiteSpace((string)e["approved_by"])).Select(e => (string)e["name"]).ToList();
                if (unapproved.Count > 0)
                    throw new ToolRefusal("A transmittal of PUBLISHED information needs approved_by, and these containers carry no approval " +
                                          "in their sidecars: " + string.Join(", ", unapproved) + ". Pass approved_by (who authorised " +
                                          "the issue). Nothing was written.");
            }

            string dir = Path.Combine(cde.Root, ".horizun", "transmittals");
            var fingerprintSource = new JObject
            {
                ["project"] = project, ["state"] = state, ["sender"] = sender, ["recipients"] = recipients, ["purpose"] = purpose,
                ["containers"] = new JArray(entries.Select(e => new JObject { ["path"] = e["path"], ["sha256"] = e["sha256"] })),
                ["approved_by"] = approvedBy, ["note"] = note
            };
            string fingerprint = Sha256Hex(Encoding.UTF8.GetBytes(fingerprintSource.ToString(Formatting.None)));

            var result = new JObject
            {
                ["operation"] = "transmittal", ["dry_run"] = dryRun, ["project"] = project, ["state"] = state,
                ["purpose"] = new JObject { ["code"] = purpose, ["description"] = purposeDescription, ["description_source"] = rules.StatusCodesSource },
                ["containers"] = new JArray(entries.Select(e => (JObject)e.DeepClone())), ["warnings"] = warnings,
                ["transmittals_dir"] = dir, ["fingerprint"] = fingerprint
            };

            JObject previous = FindIssued(dir, project, fingerprint);
            if (previous != null) return AlreadyIssued(result, dir, previous);

            if (dryRun)
            {
                int next = Math.Max(ReadSequenceLast(Path.Combine(dir, project + ".sequence.json")), MaxIssuedNumber(dir, project)) + 1;
                result["written"] = false;
                result["next_id_preview"] = FormatId(project, next);
                result["id_reserved"] = false;
                result["note"] = "Rehearsal: every container matches its sidecar and the transmittal would be issued as " +
                                 FormatId(project, next) + " (the number is NOT reserved: it is taken when the transmittal is " +
                                 "issued). Nothing was written.";
                return result;
            }

            RequireExternalWrite("transmittal");
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(dir);
            string sequencePath = Path.Combine(dir, project + ".sequence.json");
            var steps = new JArray();
            string id;
            JObject doc;
            var written = new JObject();
            using (FileStream sequence = AcquireSequence(sequencePath, ct))
            {
                // Under the lock: a concurrent identical call may have issued it meanwhile.
                previous = FindIssued(dir, project, fingerprint);
                if (previous != null) return AlreadyIssued(result, dir, previous);

                int last = ReadSequence(sequence, sequencePath);
                int number = Math.Max(last, MaxIssuedNumber(dir, project)) + 1;
                id = FormatId(project, number);

                // The bytes are measured AGAIN immediately before the record is written: the
                // record must describe what was on disk when it was issued, not a minute before.
                var late = new List<string>();
                List<JObject> remeasured = MeasureContainers(files, folder, state, cde.Root, late, new JArray());
                for (int i = 0; i < remeasured.Count && late.Count == 0; i++)
                    if (!string.Equals((string)remeasured[i]["sha256"], (string)entries[i]["sha256"], StringComparison.Ordinal))
                        late.Add((string)entries[i]["name"] + " changed while the transmittal was being prepared");
                if (late.Count > 0)
                    throw new ToolRefusal("The transmittal cannot be issued: " + string.Join("; ", late) + ". Nothing was written.");

                doc = new JObject
                {
                    ["schema"] = TransmittalSchema,
                    ["id"] = id,
                    ["project"] = project,
                    ["sequence"] = number,
                    ["issued_utc"] = Utc(nowUtc),
                    ["sender"] = sender,
                    ["recipients"] = recipients,
                    ["purpose"] = result["purpose"].DeepClone(),
                    ["state"] = state,
                    ["containers"] = new JArray(entries),
                    ["approved_by"] = approvedBy,
                    ["note"] = note,
                    ["warnings"] = warnings.DeepClone(),
                    ["fingerprint"] = fingerprint,
                    ["tool"] = ToolName
                };

                string jsonPath = Path.Combine(dir, id + ".json");
                string mdPath = Path.Combine(dir, id + ".md");
                string csvPath = Path.Combine(dir, id + ".csv");
                foreach (string p in new[] { jsonPath, mdPath, csvPath })
                    if (File.Exists(p))
                        throw new ToolRefusal(p + " already exists and a transmittal is never overwritten. The sequence file may have " +
                                              "been edited by hand; inspect " + dir + ". Nothing was written.");

                // 1. The record first: it is the transmittal. The renders derive from it.
                written["json"] = WriteBytesNoOverwriteVerified(jsonPath, new UTF8Encoding(false).GetBytes(doc.ToString(Formatting.Indented)));
                steps.Add(new JObject { ["step"] = "record", ["done"] = true, ["path"] = jsonPath });

                // 2. The sequence, still under the lock and read back.
                WriteSequence(sequence, project, number, id, nowUtc);
                steps.Add(new JObject { ["step"] = "sequence", ["done"] = true, ["path"] = sequencePath, ["last"] = number });

                // 3. The renders. A failure here leaves an issued transmittal without a render:
                //    reported as such, never as success.
                try
                {
                    written["md"] = WriteBytesNoOverwriteVerified(mdPath, new UTF8Encoding(false).GetBytes(RenderMarkdown(doc)));
                    steps.Add(new JObject { ["step"] = "markdown", ["done"] = true, ["path"] = mdPath });
                    byte[] bom = new UTF8Encoding(true).GetPreamble();
                    written["csv"] = WriteBytesNoOverwriteVerified(csvPath, bom.Concat(new UTF8Encoding(false).GetBytes(RenderCsv(doc))).ToArray());
                    steps.Add(new JObject { ["step"] = "csv", ["done"] = true, ["path"] = csvPath });
                }
                catch (IOException ex)
                {
                    steps.Add(new JObject { ["step"] = "render", ["done"] = false, ["reason"] = ex.Message });
                    result["written"] = true;
                    result["verified"] = false;
                    result["id"] = id;
                    result["steps"] = steps;
                    result["files"] = written;
                    result["note"] = "Transmittal " + id + " is ISSUED (its record " + jsonPath + " was written and read back) but a " +
                                     "render could not be written: " + ex.Message;
                    return result;
                }
            }

            // Post-condition: the record parses back to what was intended, and every
            // container still matches the hash the record carries.
            string problem;
            JObject reread = ReadJsonFile(Path.Combine(dir, id + ".json"), out problem);
            // Compared as serialised JSON: a C# null string and a JSON null are the same record.
            bool recordHolds = reread != null && (string)reread["schema"] == TransmittalSchema && (string)reread["id"] == id &&
                               string.Equals(reread.ToString(Formatting.None), doc.ToString(Formatting.None), StringComparison.Ordinal);
            var postcheck = new JArray();
            bool hashesHold = true;
            foreach (JObject e in entries)
            {
                JObject check = InformationContainer.VerifyFile(Path.Combine(cde.Root, ((string)e["path"]).Replace('/', Path.DirectorySeparatorChar)));
                bool same = (string)check["verdict"] == "match" &&
                            string.Equals((string)check["sha256"], (string)e["sha256"], StringComparison.Ordinal);
                hashesHold &= same;
                postcheck.Add(new JObject { ["container"] = e["name"], ["verdict"] = check["verdict"], ["sha256_matches_record"] = same });
            }
            result["written"] = true;
            result["id"] = id;
            result["sequence"] = doc["sequence"];
            result["issued_utc"] = doc["issued_utc"];
            result["files"] = written;
            result["steps"] = steps;
            result["postcheck"] = postcheck;
            result["verified"] = recordHolds && hashesHold;
            result["note"] = recordHolds && hashesHold
                ? "Transmittal " + id + " issued: record, Markdown and CSV written without overwriting and read back; every container " +
                  "re-hashed against the record afterwards. No container was moved or changed."
                : "Transmittal " + id + " was written but " + (recordHolds ? "a container no longer matches the hash in the record" :
                  "the record does not read back as intended" + (problem != null ? " (" + problem + ")" : "")) + ".";
            return result;
        }

        /// <summary>
        /// Every file measured against its sidecar NOW. All problems are collected, not
        /// just the first, so one refusal is enough to fix the list.
        /// </summary>
        private static List<JObject> MeasureContainers(List<string> files, string folder, string state, string root,
                                                       List<string> problems, JArray warnings)
        {
            var entries = new List<JObject>();
            foreach (string file in files)
            {
                string rel = Path.GetRelativePath(folder, file);
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
                { problems.Add(file + " is not inside the " + state + " folder (" + folder + ")"); continue; }
                string relRoot = Path.GetRelativePath(root, file);
                if (relRoot.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relRoot))
                { problems.Add(file + " is not under the CDE root (" + root + ")"); continue; }
                if (!File.Exists(file)) { problems.Add(file + " does not exist"); continue; }
                if (InformationContainer.IsSidecar(file)) { problems.Add(file + " is a sidecar, not a container file"); continue; }

                JObject check = InformationContainer.VerifyFile(file);
                string verdict = (string)check["verdict"];
                if (verdict != "match")
                {
                    problems.Add(Path.GetFileName(file) + " does not match its sidecar (verdict " + verdict + ")" +
                                 (verdict == "missing_sidecar" ? " - stamp it first" : ""));
                    continue;
                }
                var sc = (JObject)check["sidecar"];
                string sealedState = (string)sc["state"];
                if (sealedState != null && sealedState != state)
                { problems.Add(Path.GetFileName(file) + " is sealed for the " + sealedState + " state but sits in " + state); continue; }
                string statusState = InformationContainer.StateOfStatus((string)sc["status"]);
                if (statusState != null && state != InformationContainer.StateArchived && statusState != state)
                    warnings.Add(new JObject
                    {
                        ["container"] = sc["name"], ["code"] = "state_status_mismatch",
                        ["reason"] = "status " + (string)sc["status"] + " belongs to the " + statusState + " state, the file sits in " + state
                    });
                entries.Add(new JObject
                {
                    ["name"] = sc["name"],
                    ["title"] = sc["title"],
                    ["status"] = sc["status"],
                    ["revision"] = sc["revision"],
                    ["state"] = state,
                    ["file"] = Path.GetFileName(file),
                    ["path"] = relRoot.Replace('\\', '/'),
                    ["bytes"] = check["bytes"],
                    ["sha256"] = check["sha256"],
                    ["approved_by"] = sc["approved_by"],
                    ["transition_id"] = sc["transition_id"]
                });
            }
            return entries;
        }

        private static List<string> FilePaths(JObject args)
        {
            var arr = args["file_paths"] as JArray;
            if (arr == null || arr.Count == 0)
                throw new ToolRefusal("file_paths is required: a non-empty array of absolute paths to sealed container files. Nothing was written.");
            if (arr.Count > MaxTransmittalContainers)
                throw new ToolRefusal("A transmittal carries at most " + MaxTransmittalContainers + " containers. Nothing was written.");
            var list = new List<string>();
            foreach (JToken t in arr)
            {
                if (t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t))
                    throw new ToolRefusal("file_paths entries must be non-empty strings. Nothing was written.");
                if (!Path.IsPathRooted((string)t)) throw new ToolRefusal("file_paths entries must be absolute: '" + (string)t + "'. Nothing was written.");
                string full = Path.GetFullPath((string)t);
                if (list.Contains(full, StringComparer.OrdinalIgnoreCase))
                    throw new ToolRefusal("file_paths names " + full + " twice. Nothing was written.");
                list.Add(full);
            }
            return list;
        }

        private static JObject Party(JToken token, string where)
        {
            var o = token as JObject;
            if (o == null) throw new ToolRefusal(where + " must be an object {name, organization, role}. Nothing was written.");
            foreach (JProperty p in o.Properties())
            {
                if (!PartyKeys.Contains(p.Name))
                    throw new ToolRefusal(where + " has an unknown key '" + p.Name + "'. Accepted: name, organization, role. Nothing was written.");
                if (p.Value.Type != JTokenType.String && p.Value.Type != JTokenType.Null)
                    throw new ToolRefusal(where + "." + p.Name + " must be a string. Nothing was written.");
            }
            string name = ((string)o["name"])?.Trim(), org = ((string)o["organization"])?.Trim(), role = ((string)o["role"])?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(org))
                throw new ToolRefusal(where + " needs a name and an organization: a transmittal says who issued it to whom. Nothing was written.");
            return new JObject { ["name"] = name, ["organization"] = org, ["role"] = string.IsNullOrEmpty(role) ? null : role };
        }

        private static JObject AlreadyIssued(JObject result, string dir, JObject previous)
        {
            string id = (string)previous["id"];
            result["written"] = false;
            result["already_issued"] = true;
            result["id"] = id;
            result["issued_utc"] = previous["issued_utc"];
            result["files"] = new JObject
            {
                ["json"] = new JObject { ["path"] = Path.Combine(dir, id + ".json") },
                ["md"] = new JObject { ["path"] = Path.Combine(dir, id + ".md") },
                ["csv"] = new JObject { ["path"] = Path.Combine(dir, id + ".csv") }
            };
            result["verified"] = true;
            result["note"] = "These exact containers (same bytes) were already issued to the same recipients for the same purpose as " + id +
                             ". Nothing was written. To issue them again on purpose, say why in 'note'.";
            return result;
        }

        private static JObject FindIssued(string dir, string project, string fingerprint)
        {
            if (!Directory.Exists(dir)) return null;
            foreach (string path in Directory.EnumerateFiles(dir, "*.json"))
            {
                if (path.EndsWith(".sequence.json", StringComparison.OrdinalIgnoreCase)) continue;
                string problem;
                JObject doc = ReadJsonFile(path, out problem);
                if (doc != null && (string)doc["schema"] == TransmittalSchema && (string)doc["project"] == project &&
                    (string)doc["fingerprint"] == fingerprint)
                    return doc;
            }
            return null;
        }

        private static string FormatId(string project, int number) =>
            project + "-TR-" + number.ToString("D4", CultureInfo.InvariantCulture);

        /// <summary>
        /// The highest number any file of this project already uses - .json, .md, .csv or a
        /// leftover. The next number is above it, so a number never reuses a file even when
        /// the sequence file was lost or edited.
        /// </summary>
        private static int MaxIssuedNumber(string dir, string project)
        {
            if (!Directory.Exists(dir)) return 0;
            int max = 0;
            foreach (string path in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileName(path);
                int dot = name.IndexOf('.');
                if (dot <= 0) continue;
                Match m = TransmittalId.Match(name.Substring(0, dot));
                if (!m.Success || !string.Equals(m.Groups["p"].Value, project, StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > max) max = n;
            }
            return max;
        }

        /// <summary>
        /// The sequence file opened with FileShare.None: the handle IS the lock. It dies with
        /// the process, so a crash never leaves a stale lock behind.
        /// </summary>
        private static FileStream AcquireSequence(string path, CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            var jitter = new Random(Guid.NewGuid().GetHashCode());
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (clock.Elapsed < SequenceLockBudget) { Thread.Sleep(10 + jitter.Next(40)); }
                catch (UnauthorizedAccessException) when (clock.Elapsed < SequenceLockBudget) { Thread.Sleep(10 + jitter.Next(40)); }
                catch (IOException ex)
                {
                    throw new ToolRefusal("The transmittal sequence " + path + " stayed locked for " + SequenceLockBudget.TotalSeconds +
                                          " s (another transmittal is being issued, or a process holds it): " + ex.Message + " Nothing was written.");
                }
            }
        }

        private static int ReadSequence(FileStream stream, string path)
        {
            stream.Position = 0;
            var buffer = new byte[stream.Length];
            int read = 0;
            while (read < buffer.Length) { int n = stream.Read(buffer, read, buffer.Length - read); if (n <= 0) break; read += n; }
            string text = Encoding.UTF8.GetString(buffer, 0, read).Trim();
            if (text.Length == 0) return 0;
            return ParseSequence(text, path);
        }

        private static int ReadSequenceLast(string path)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string text = r.ReadToEnd().Trim();
                    return text.Length == 0 ? 0 : ParseSequence(text, path);
                }
            }
            catch (IOException) { return 0; }   // a rehearsal while another issue holds the lock: preview only
        }

        private static int ParseSequence(string text, string path)
        {
            try
            {
                JObject o = JObject.Parse(text);
                JToken last = o["last"];
                if ((string)o["schema"] == SequenceSchema && last != null && last.Type == JTokenType.Integer && (long)last >= 0 && (long)last < 1000000000)
                    return (int)(long)last;
            }
            catch (JsonException) { }
            throw new ToolRefusal("The transmittal sequence " + path + " is not a readable " + SequenceSchema + " document. A number is " +
                                  "never guessed: inspect or restore that file. Nothing was written.");
        }

        private static void WriteSequence(FileStream stream, string project, int number, string id, DateTime nowUtc)
        {
            var o = new JObject
            {
                ["schema"] = SequenceSchema, ["project"] = project, ["last"] = number, ["last_id"] = id, ["updated_utc"] = Utc(nowUtc)
            };
            byte[] bytes = new UTF8Encoding(false).GetBytes(o.ToString(Formatting.Indented));
            stream.Position = 0;
            stream.SetLength(0);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
            stream.Position = 0;
            var back = new byte[stream.Length];
            int read = 0;
            while (read < back.Length) { int n = stream.Read(back, read, back.Length - read); if (n <= 0) break; read += n; }
            if (read != bytes.Length || !back.AsSpan(0, read).SequenceEqual(bytes))
                throw new IOException("the transmittal sequence did not read back as written after " + id);
        }

        // ---- renders ---------------------------------------------------------------------

        internal static string RenderMarkdown(JObject doc)
        {
            var sb = new StringBuilder();
            sb.Append("# Transmittal ").Append(Md((string)doc["id"])).Append('\n').Append('\n');
            sb.Append("| | |\n|---|---|\n");
            Row(sb, "Project", (string)doc["project"]);
            Row(sb, "Issued (UTC)", (string)doc["issued_utc"]);
            Row(sb, "From", PartyText((JObject)doc["sender"]));
            Row(sb, "To", string.Join("; ", ((JArray)doc["recipients"]).Select(r => PartyText((JObject)r))));
            Row(sb, "Purpose of issue", (string)doc["purpose"]["code"] + " - " + (string)doc["purpose"]["description"]);
            Row(sb, "CDE state", (string)doc["state"]);
            Row(sb, "Approved by", (string)doc["approved_by"] ?? "-");
            Row(sb, "Note", (string)doc["note"] ?? "-");
            var containers = (JArray)doc["containers"];
            sb.Append('\n').Append("## Containers (").Append(containers.Count.ToString(CultureInfo.InvariantCulture)).Append(")\n\n");
            sb.Append("| # | Container | Title | Status | Revision | File | Bytes | SHA-256 |\n");
            sb.Append("|---|---|---|---|---|---|---|---|\n");
            int i = 0;
            foreach (JToken c in containers)
            {
                i++;
                sb.Append("| ").Append(i.ToString(CultureInfo.InvariantCulture))
                  .Append(" | ").Append(Md((string)c["name"]))
                  .Append(" | ").Append(Md((string)c["title"] ?? ""))
                  .Append(" | ").Append(Md((string)c["status"]))
                  .Append(" | ").Append(Md((string)c["revision"]))
                  .Append(" | ").Append(Md((string)c["file"]))
                  .Append(" | ").Append(((long)c["bytes"]).ToString(CultureInfo.InvariantCulture))
                  .Append(" | `").Append((string)c["sha256"]).Append("` |\n");
            }
            sb.Append('\n').Append("Each file's SHA-256 was re-measured when this transmittal was issued and matched its sidecar. ")
              .Append("The record is ").Append(Md((string)doc["id"])).Append(".json (").Append(TransmittalSchema).Append(").\n");
            return sb.ToString();
        }

        internal static string RenderCsv(JObject doc)
        {
            var sb = new StringBuilder();
            string[] header = { "transmittal_id", "issued_utc", "purpose", "purpose_description", "state", "container", "title",
                                "status", "revision", "file", "bytes", "sha256", "sender", "recipients" };
            sb.Append(string.Join(",", header)).Append("\r\n");
            string recipients = string.Join("; ", ((JArray)doc["recipients"]).Select(r => PartyText((JObject)r)));
            foreach (JToken c in (JArray)doc["containers"])
            {
                string[] cells =
                {
                    (string)doc["id"], (string)doc["issued_utc"], (string)doc["purpose"]["code"], (string)doc["purpose"]["description"],
                    (string)doc["state"], (string)c["name"], (string)c["title"], (string)c["status"], (string)c["revision"],
                    (string)c["file"], ((long)c["bytes"]).ToString(CultureInfo.InvariantCulture), (string)c["sha256"],
                    PartyText((JObject)doc["sender"]), recipients
                };
                sb.Append(string.Join(",", cells.Select(Csv))).Append("\r\n");
            }
            return sb.ToString();
        }

        private static void Row(StringBuilder sb, string label, string value) =>
            sb.Append("| ").Append(label).Append(" | ").Append(Md(value)).Append(" |\n");

        private static string PartyText(JObject p)
        {
            string role = (string)p["role"];
            return (string)p["name"] + " (" + (string)p["organization"] + (string.IsNullOrEmpty(role) ? "" : ", " + role) + ")";
        }

        private static string Md(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

        /// <summary>
        /// RFC 4180 quoting, and a text that a spreadsheet would read as a formula
        /// (= + - @, tab, CR) is prefixed with an apostrophe: a person's name must not run.
        /// </summary>
        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s;
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // ---- record_review -------------------------------------------------------------

        internal static JObject RecordReview(JObject args, CancellationToken ct, DateTime nowUtc)
        {
            JObject context = LoadContext(args);
            Cde cde = ResolveCde(args, context, requireStates: false);
            if (cde.Root == null)
                throw new ToolRefusal("record_review needs the CDE root (root, or cde.root in the project context): the register lives in " +
                                      "<root>/" + ReviewsRelativePath + ". Nothing was written.");
            string outcome = RequiredString(args, "outcome");
            if (Array.IndexOf(ReviewOutcomes, outcome) < 0)
                throw new ToolRefusal("outcome must be one of " + string.Join(", ", ReviewOutcomes) + " (ISO 19650-2 §5.7 review). Nothing was written.");
            string reviewedBy = RequiredString(args, "reviewed_by").Trim();
            string organization = OptionalString(args, "reviewer_organization")?.Trim();
            string comments = OptionalString(args, "comments");
            if (outcome != "accepted" && string.IsNullOrWhiteSpace(comments))
                throw new ToolRefusal(outcome + " needs comments: the receiving party says what has to change. Nothing was written.");
            string reviewedOn = OptionalString(args, "reviewed_on");
            if (reviewedOn == null) reviewedOn = nowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            else
            {
                if (!DateTime.TryParseExact(reviewedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime on))
                    throw new ToolRefusal("reviewed_on must be YYYY-MM-DD. Nothing was written.");
                if (on.Date > nowUtc.Date.AddDays(1))
                    throw new ToolRefusal("reviewed_on " + reviewedOn + " is in the future: a review is recorded after it happened. Nothing was written.");
            }
            string transmittalId = OptionalString(args, "transmittal_id");
            string container = OptionalString(args, "container");
            string revision = OptionalString(args, "revision");
            if (transmittalId == null && container == null)
                throw new ToolRefusal("record_review needs what was reviewed: transmittal_id, container, or both. Nothing was written.");

            string dir = Path.Combine(cde.Root, ".horizun", "transmittals");
            var reviewed = new JArray();
            if (transmittalId != null)
            {
                if (!TransmittalId.IsMatch(transmittalId))
                    throw new ToolRefusal("transmittal_id '" + transmittalId + "' is not a transmittal number (<project>-TR-0001). Nothing was written.");
                string path = Path.Combine(dir, transmittalId + ".json");
                if (!File.Exists(path))
                    throw new ToolRefusal("No transmittal " + transmittalId + " exists at " + path + ". A review is recorded against what was " +
                                          "issued. Nothing was written.");
                string problem;
                JObject doc = ReadJsonFile(path, out problem);
                if (doc == null || (string)doc["schema"] != TransmittalSchema || (string)doc["id"] != transmittalId)
                    throw new ToolRefusal("The transmittal " + path + " is not a readable " + TransmittalSchema + " record" +
                                          (problem != null ? " (" + problem + ")" : "") + ". Nothing was written.");
                foreach (JToken c in (JArray)doc["containers"] ?? new JArray())
                {
                    if (container != null && (string)c["name"] != container) continue;
                    if (revision != null && (string)c["revision"] != revision) continue;
                    reviewed.Add(new JObject { ["name"] = c["name"], ["revision"] = c["revision"], ["sha256"] = c["sha256"], ["file"] = c["file"] });
                }
                if (reviewed.Count == 0)
                    throw new ToolRefusal("Transmittal " + transmittalId + " does not carry " + container + (revision != null ? " at revision " + revision : "") +
                                          ". Nothing was written.");
            }
            else
            {
                // A container reviewed outside a transmittal must at least EXIST, sealed, in a
                // declared state folder. Its revision must be unambiguous.
                List<Sealed> sealedCopies = SealedContainers(cde, ct).Where(s => (string)s.Sidecar["name"] == container &&
                    (revision == null || (string)s.Sidecar["revision"] == revision)).ToList();
                if (sealedCopies.Count == 0)
                    throw new ToolRefusal("No sealed container " + container + (revision != null ? " at revision " + revision : "") +
                                          " exists in the declared state folders" + (cde.States.Count == 0 ? " (none were declared: pass states or a project context)" : "") +
                                          ". Review a transmittal, or stamp the container first. Nothing was written.");
                List<string> revisions = sealedCopies.Select(s => (string)s.Sidecar["revision"]).Distinct(StringComparer.Ordinal).ToList();
                if (revisions.Count > 1)
                    throw new ToolRefusal(container + " exists at revisions " + string.Join(", ", revisions) + "; say which one was reviewed " +
                                          "(revision). Nothing was written.");
                foreach (Sealed s in sealedCopies)
                    reviewed.Add(new JObject { ["name"] = s.Sidecar["name"], ["revision"] = s.Sidecar["revision"], ["sha256"] = s.Sidecar["sha256"], ["file"] = s.Sidecar["file"], ["state"] = s.State });
            }

            var fingerprintSource = new JObject
            {
                ["transmittal_id"] = transmittalId, ["containers"] = reviewed, ["outcome"] = outcome, ["comments"] = comments,
                ["reviewed_by"] = reviewedBy, ["reviewer_organization"] = organization, ["reviewed_on"] = reviewedOn
            };
            string fingerprint = Sha256Hex(Encoding.UTF8.GetBytes(fingerprintSource.ToString(Formatting.None)));
            string logPath = Path.Combine(cde.Root, ".horizun", "reviews.jsonl");
            var line = new JObject
            {
                ["schema"] = ReviewSchema,
                ["review_id"] = null,
                ["utc"] = Utc(nowUtc),
                ["reviewed_on"] = reviewedOn,
                ["transmittal_id"] = transmittalId,
                ["containers"] = reviewed,
                ["outcome"] = outcome,
                ["comments"] = comments,
                ["reviewed_by"] = reviewedBy,
                ["reviewer_organization"] = organization,
                ["changes_state"] = false,
                ["fingerprint"] = fingerprint,
                ["tool"] = ToolName
            };
            bool dryRun = DryRun(args);
            var result = new JObject
            {
                ["operation"] = "record_review", ["dry_run"] = dryRun, ["log_path"] = logPath, ["outcome"] = outcome,
                ["transmittal_id"] = transmittalId, ["containers"] = reviewed.DeepClone(), ["changes_state"] = false
            };

            foreach (JObject previous in ReadJsonLines(logPath, null))
                if ((string)previous["fingerprint"] == fingerprint)
                {
                    result["written"] = false;
                    result["already_recorded"] = true;
                    result["review_id"] = previous["review_id"];
                    result["verified"] = true;
                    result["note"] = "This exact review is already in the register as " + (string)previous["review_id"] + ". Nothing was written.";
                    return result;
                }

            string stateNote = outcome == "rejected"
                ? " A rejection is recorded, not acted on: no file was moved and no state changed. The originator revises and re-issues."
                : " No file was moved and no state changed; promoting the container is a separate transition.";
            if (dryRun)
            {
                result["written"] = false;
                result["line_preview"] = line;
                result["note"] = "Rehearsal: the review would be appended as shown. Nothing was written." + stateNote;
                return result;
            }
            RequireExternalWrite("record_review");
            ct.ThrowIfCancellationRequested();
            string id = Guid.NewGuid().ToString("N");
            line["review_id"] = id;
            bool logged = AppendLineVerified(logPath, line, id, ct, out string logProblem);
            result["written"] = logged;
            result["review_id"] = id;
            result["verified"] = logged;
            result["line"] = line;
            result["note"] = logged
                ? "The review was appended to " + logPath + " and found again on re-read." + stateNote
                : "The review line could not be confirmed: " + logProblem;
            return result;
        }

        /// <summary>
        /// Append one line and find it again. Concurrent appenders meet a sharing violation
        /// and retry; the line is written whole or not at all.
        /// </summary>
        private static bool AppendLineVerified(string logPath, JObject line, string id, CancellationToken ct, out string problem)
        {
            problem = null;
            byte[] bytes = new UTF8Encoding(false).GetBytes(line.ToString(Formatting.None) + "\n");
            var clock = Stopwatch.StartNew();
            var jitter = new Random(Guid.NewGuid().GetHashCode());
            Directory.CreateDirectory(Path.GetDirectoryName(logPath));
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    break;
                }
                catch (IOException ex) when (clock.Elapsed < SequenceLockBudget) { problem = ex.Message; Thread.Sleep(10 + jitter.Next(40)); }
                catch (IOException ex) { problem = ex.Message; return false; }
                catch (UnauthorizedAccessException ex) { problem = ex.Message; return false; }
            }
            problem = null;
            foreach (JObject o in ReadJsonLines(logPath, null))
                if ((string)o["review_id"] == id) return true;
            problem = "the line was appended but is not found on re-read";
            return false;
        }

        // ---- register ------------------------------------------------------------------

        private sealed class Sealed
        {
            public string State, File;
            public JObject Sidecar;
        }

        private static IEnumerable<Sealed> SealedContainers(Cde cde, CancellationToken ct)
        {
            foreach (string state in InformationContainer.States)
            {
                if (!cde.States.TryGetValue(state, out string folder) || !Directory.Exists(folder)) continue;
                foreach (string path in Directory.EnumerateFiles(folder, "*" + InformationContainer.SidecarSuffix, SearchOption.AllDirectories)
                                                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Ignored(Path.GetRelativePath(folder, path))) continue;
                    JObject sc = InformationContainer.ReadSidecar(path, out string _);
                    if (sc == null || (string)sc["name"] == null) continue;
                    yield return new Sealed { State = state, File = path.Substring(0, path.Length - InformationContainer.SidecarSuffix.Length), Sidecar = sc };
                }
            }
        }

        private sealed class History
        {
            public string Container;
            public readonly List<JObject> Events = new List<JObject>();
            public readonly SortedSet<string> PresentIn = new SortedSet<string>(StringComparer.Ordinal);
        }

        internal static JObject Register(JObject args, CancellationToken ct)
        {
            JObject context = LoadContext(args);
            Cde cde = ResolveCde(args, context, requireStates: false);
            if (cde.Root == null)
                throw new ToolRefusal("register needs the CDE root (root, or cde.root in the project context): the records live in <root>/.horizun. Nothing was read.");
            string filterContainer = OptionalString(args, "container");
            string filterState = OptionalString(args, "state");
            if (filterState != null && Array.IndexOf(InformationContainer.States, filterState) < 0)
                throw new ToolRefusal("state must be one of wip, shared, published, archived. Nothing was read.");
            string since = DateArg(args, "since"), until = DateArg(args, "until");
            int offset = IntArg(args, "offset", 0, 0, int.MaxValue);
            int limit = IntArg(args, "limit", DefaultLimit, 1, MaxLimit);

            var histories = new Dictionary<string, History>(StringComparer.Ordinal);
            History For(string name)
            {
                if (!histories.TryGetValue(name, out History h)) histories[name] = h = new History { Container = name };
                return h;
            }
            var incoherences = new List<JObject>();
            var sources = new JObject();

            // 1. Transitions.
            string transitionsPath = Path.Combine(cde.Root, ".horizun", "cde-transitions.jsonl");
            var transitionUnreadable = new List<JObject>();
            List<JObject> transitions = ReadJsonLines(transitionsPath, transitionUnreadable);
            sources["transitions"] = Source(transitionsPath, transitions.Count, transitionUnreadable.Count);
            foreach (JObject bad in transitionUnreadable) incoherences.Add(Incoherence("log_line_unreadable", "warning", null, transitionsPath, bad));
            foreach (JObject t in transitions)
            {
                string name = (string)t["name"];
                if (string.IsNullOrEmpty(name)) { incoherences.Add(Incoherence("log_line_unreadable", "warning", null, transitionsPath, new JObject { ["reason"] = "a transition line without a container name", ["transition_id"] = t["transition_id"] })); continue; }
                For(name).Events.Add(new JObject
                {
                    ["kind"] = "transition", ["utc"] = t["utc"], ["date"] = DatePart((string)t["utc"]), ["from_state"] = t["from_state"],
                    ["to_state"] = t["to_state"], ["status"] = t["status"], ["revision"] = t["revision"], ["approved_by"] = t["approved_by"],
                    ["sha256"] = t["destination_sha256"], ["transition_id"] = t["transition_id"], ["note"] = t["note"]
                });
                if ((string)t["to_state"] == InformationContainer.StatePublished && string.IsNullOrWhiteSpace((string)t["approved_by"]))
                    incoherences.Add(Incoherence("publication_without_approval", "error", name, transitionsPath, new JObject
                    {
                        ["transition_id"] = t["transition_id"], ["utc"] = t["utc"], ["revision"] = t["revision"],
                        ["reason"] = "a shared->published transition was logged without approved_by (a log written before the requirement, or edited)"
                    }));
            }

            // 2. Transmittals: each re-measured against the file it cites.
            string dir = Path.Combine(cde.Root, ".horizun", "transmittals");
            var transmittalIds = new HashSet<string>(StringComparer.Ordinal);
            var transmittalContainers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            int transmittalFiles = 0, transmittalBad = 0;
            string rootFull = Path.GetFullPath(cde.Root);
            if (Directory.Exists(dir))
                foreach (string path in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    if (path.EndsWith(".sequence.json", StringComparison.OrdinalIgnoreCase)) continue;
                    transmittalFiles++;
                    JObject doc = ReadJsonFile(path, out string problem);
                    if (doc == null || (string)doc["schema"] != TransmittalSchema || !(doc["containers"] is JArray))
                    {
                        transmittalBad++;
                        incoherences.Add(Incoherence("transmittal_unreadable", "warning", null, path, new JObject
                        { ["reason"] = problem ?? "not a " + TransmittalSchema + " record" }));
                        continue;
                    }
                    string id = (string)doc["id"];
                    transmittalIds.Add(id);
                    var names = transmittalContainers[id] = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JToken c in (JArray)doc["containers"])
                    {
                        string name = (string)c["name"];
                        if (string.IsNullOrEmpty(name)) continue;
                        names.Add(name);
                        For(name).Events.Add(new JObject
                        {
                            ["kind"] = "transmittal", ["utc"] = doc["issued_utc"], ["date"] = DatePart((string)doc["issued_utc"]),
                            ["transmittal_id"] = id, ["purpose"] = doc["purpose"]?["code"], ["state"] = doc["state"],
                            ["status"] = c["status"], ["revision"] = c["revision"], ["sha256"] = c["sha256"],
                            ["approved_by"] = doc["approved_by"] ?? c["approved_by"],
                            ["recipients"] = new JArray(((doc["recipients"] as JArray) ?? new JArray()).Select(r => r?["organization"]))
                        });
                        string rel = (string)c["path"];
                        string full = rel == null ? null : Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
                        string back = full == null ? null : Path.GetRelativePath(rootFull, full);
                        if (full == null || back.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(back))
                        {
                            incoherences.Add(Incoherence("transmittal_path_outside_root", "error", name, path, new JObject
                            { ["transmittal_id"] = id, ["path"] = rel, ["reason"] = "the transmittal cites a path that does not resolve under the CDE root" }));
                            continue;
                        }
                        if (!File.Exists(full))
                        {
                            incoherences.Add(Incoherence("transmittal_file_missing", "warning", name, path, new JObject
                            { ["transmittal_id"] = id, ["file"] = full, ["reason"] = "the file this transmittal issued is no longer where it was issued from" }));
                            continue;
                        }
                        string now = InformationContainer.Sha256File(full, out long _);
                        if (!string.Equals(now, (string)c["sha256"], StringComparison.OrdinalIgnoreCase))
                            incoherences.Add(Incoherence("transmittal_hash_changed", "error", name, path, new JObject
                            {
                                ["transmittal_id"] = id, ["file"] = full, ["issued_sha256"] = c["sha256"], ["current_sha256"] = now,
                                ["reason"] = "the file changed after it was issued: the recipients hold different bytes from what sits in the CDE"
                            }));
                    }
                }
            sources["transmittals"] = new JObject
            {
                ["path"] = dir, ["exists"] = Directory.Exists(dir), ["records"] = transmittalFiles - transmittalBad, ["unreadable"] = transmittalBad
            };

            // 3. Sealed containers in the declared state folders (the "does it exist" side).
            var known = new HashSet<string>(histories.Keys, StringComparer.Ordinal);
            foreach (Sealed s in SealedContainers(cde, ct))
            {
                string name = (string)s.Sidecar["name"];
                known.Add(name);
                if (histories.TryGetValue(name, out History h)) h.PresentIn.Add(s.State);
            }
            sources["state_folders"] = new JObject
            {
                ["declared"] = cde.States.Count, ["walked"] = cde.States.Values.Count(Directory.Exists),
                ["note"] = cde.States.Count == 0 ? "no state folders declared: a container is 'known' only from the logs and transmittals" : null
            };

            // 4. Reviews.
            string reviewsPath = Path.Combine(cde.Root, ".horizun", "reviews.jsonl");
            var reviewUnreadable = new List<JObject>();
            List<JObject> reviews = ReadJsonLines(reviewsPath, reviewUnreadable);
            sources["reviews"] = Source(reviewsPath, reviews.Count, reviewUnreadable.Count);
            foreach (JObject bad in reviewUnreadable) incoherences.Add(Incoherence("log_line_unreadable", "warning", null, reviewsPath, bad));
            foreach (JObject r in reviews)
            {
                string tid = (string)r["transmittal_id"];
                if (tid != null && !transmittalIds.Contains(tid))
                    incoherences.Add(Incoherence("review_unknown_transmittal", "error", null, reviewsPath, new JObject
                    { ["review_id"] = r["review_id"], ["transmittal_id"] = tid, ["reason"] = "the review cites a transmittal that is not in the register" }));
                foreach (JToken c in (r["containers"] as JArray) ?? new JArray())
                {
                    string name = (string)c["name"];
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!known.Contains(name))
                        incoherences.Add(Incoherence("review_unknown_container", "error", name, reviewsPath, new JObject
                        {
                            ["review_id"] = r["review_id"], ["transmittal_id"] = tid,
                            ["reason"] = "no transition, transmittal or sealed copy of this container exists"
                        }));
                    else if (tid != null && transmittalContainers.TryGetValue(tid, out HashSet<string> carried) && !carried.Contains(name))
                        incoherences.Add(Incoherence("review_container_not_in_transmittal", "error", name, reviewsPath, new JObject
                        { ["review_id"] = r["review_id"], ["transmittal_id"] = tid, ["reason"] = "the review cites a container its transmittal did not carry" }));
                    For(name).Events.Add(new JObject
                    {
                        ["kind"] = "review", ["utc"] = r["utc"], ["date"] = r["reviewed_on"] ?? DatePart((string)r["utc"]),
                        ["review_id"] = r["review_id"], ["transmittal_id"] = tid, ["revision"] = c["revision"], ["outcome"] = r["outcome"],
                        ["reviewed_by"] = r["reviewed_by"], ["reviewer_organization"] = r["reviewer_organization"], ["comments"] = r["comments"]
                    });
                }
            }

            // 5. Rows: one per container with at least one event, filtered and paginated.
            var rows = new List<JObject>();
            foreach (History h in histories.Values.OrderBy(x => x.Container, StringComparer.Ordinal))
            {
                if (filterContainer != null && h.Container != filterContainer) continue;
                List<JObject> events = h.Events
                    .Where(e => InWindow((string)e["date"], since, until))
                    .OrderBy(e => (string)e["utc"] ?? "", StringComparer.Ordinal).ToList();
                if (events.Count == 0) continue;
                List<JObject> all = h.Events.OrderBy(e => (string)e["utc"] ?? "", StringComparer.Ordinal).ToList();
                var reached = new SortedSet<string>(Comparer<string>.Create((a, b) => InformationContainer.StateRank(a).CompareTo(InformationContainer.StateRank(b))));
                foreach (JObject e in all)
                {
                    if ((string)e["kind"] == "transition" && (string)e["to_state"] != null) { reached.Add((string)e["to_state"]); if ((string)e["from_state"] != null) reached.Add((string)e["from_state"]); }
                    if ((string)e["kind"] == "transmittal" && (string)e["state"] != null) reached.Add((string)e["state"]);
                }
                foreach (string s in h.PresentIn) reached.Add(s);
                if (filterState != null && !reached.Contains(filterState)) continue;
                JObject lastTransition = all.LastOrDefault(e => (string)e["kind"] == "transition");
                JObject lastReview = all.LastOrDefault(e => (string)e["kind"] == "review");
                rows.Add(new JObject
                {
                    ["container"] = h.Container,
                    ["states_reached"] = new JArray(reached.Where(s => InformationContainer.StateRank(s) >= 0).Cast<object>().ToArray()),
                    ["last_transition_to"] = lastTransition?["to_state"],
                    ["present_in"] = new JArray(h.PresentIn.Cast<object>().ToArray()),
                    ["approvals"] = new JArray(all.Where(e => (string)e["kind"] == "transition" && !string.IsNullOrWhiteSpace((string)e["approved_by"]))
                        .Select(e => new JObject { ["to_state"] = e["to_state"], ["approved_by"] = e["approved_by"], ["utc"] = e["utc"], ["revision"] = e["revision"], ["transition_id"] = e["transition_id"] })),
                    ["transmittals"] = new JArray(all.Where(e => (string)e["kind"] == "transmittal").Select(e => e["transmittal_id"]).Distinct(new JTokenEqualityComparer())),
                    ["latest_review"] = lastReview == null ? JValue.CreateNull() : new JObject
                    {
                        ["outcome"] = lastReview["outcome"], ["reviewed_by"] = lastReview["reviewed_by"], ["reviewed_on"] = lastReview["date"],
                        ["transmittal_id"] = lastReview["transmittal_id"], ["review_id"] = lastReview["review_id"]
                    },
                    ["incoherences"] = incoherences.Count(i => (string)i["container"] == h.Container),
                    ["history"] = new JArray(events)
                });
            }

            List<JObject> orderedIncoherences = incoherences
                .Where(i => filterContainer == null || (string)i["container"] == filterContainer || (string)i["container"] == null)
                .OrderBy(i => Array.IndexOf(IncoherenceOrder, (string)i["kind"]))
                .ThenBy(i => (string)i["container"] ?? "", StringComparer.Ordinal)
                .ToList();
            var counts = new JObject();
            foreach (string kind in IncoherenceOrder)
            {
                int c = orderedIncoherences.Count(i => (string)i["kind"] == kind);
                if (c > 0) counts[kind] = c;
            }
            List<JObject> page = rows.Skip(offset).Take(limit).ToList();
            bool more = offset + page.Count < rows.Count;
            List<JObject> shownIncoherences = orderedIncoherences.Take(MaxLimit).ToList();
            return new JObject
            {
                ["operation"] = "register",
                ["root"] = cde.Root,
                ["filters"] = new JObject { ["container"] = filterContainer, ["state"] = filterState, ["since"] = since, ["until"] = until },
                ["sources"] = sources,
                ["total_containers"] = rows.Count,
                ["offset"] = offset,
                ["limit"] = limit,
                ["containers"] = new JArray(page),
                ["truncated"] = more,
                ["next_offset"] = more ? (JToken)(offset + page.Count) : JValue.CreateNull(),
                ["incoherence_counts"] = counts,
                ["total_incoherences"] = orderedIncoherences.Count,
                ["incoherences"] = new JArray(shownIncoherences),
                ["incoherences_truncated"] = shownIncoherences.Count < orderedIncoherences.Count,
                ["note"] = "Read-only: nothing was written. Transmittal hashes were re-measured against the files now on disk. " +
                           "A source that does not exist is reported as such, never as an empty register."
            };
        }

        private static JObject Source(string path, int lines, int unreadable) =>
            new JObject { ["path"] = path, ["exists"] = File.Exists(path), ["records"] = lines, ["unreadable"] = unreadable };

        private static JObject Incoherence(string kind, string severity, string container, string source, JObject detail)
        {
            var o = new JObject { ["kind"] = kind, ["severity"] = severity, ["container"] = container, ["source"] = source };
            if (detail != null) foreach (JProperty p in detail.Properties()) o[p.Name] = p.Value;
            return o;
        }

        private static bool InWindow(string date, string since, string until)
        {
            if (since == null && until == null) return true;
            if (date == null) return false;
            if (since != null && string.CompareOrdinal(date, since) < 0) return false;
            if (until != null && string.CompareOrdinal(date, until) > 0) return false;
            return true;
        }

        private static string DatePart(string utc) => utc != null && utc.Length >= 10 ? utc.Substring(0, 10) : null;

        private static string DateArg(JObject args, string key)
        {
            string s = OptionalString(args, key);
            if (s != null && !DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime _))
                throw new ToolRefusal(key + " must be YYYY-MM-DD. Nothing was read.");
            return s;
        }

        // ---- shared I/O ------------------------------------------------------------------

        /// <summary>
        /// Every JSON-object line of a .jsonl file, without date promotion. Unreadable
        /// lines are collected (line number and reason) when a list is given - never
        /// silently dropped from a register.
        /// </summary>
        private static List<JObject> ReadJsonLines(string path, List<JObject> unreadable)
        {
            var list = new List<JObject>();
            if (!File.Exists(path)) return list;
            string[] lines;
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(s, Encoding.UTF8))
                lines = r.ReadToEnd().Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                try
                {
                    using (var reader = new JsonTextReader(new StringReader(l)) { DateParseHandling = DateParseHandling.None })
                    {
                        if (JToken.ReadFrom(reader) is JObject o) { list.Add(o); continue; }
                    }
                    unreadable?.Add(new JObject { ["line"] = i + 1, ["reason"] = "not a JSON object" });
                }
                catch (JsonException ex) { unreadable?.Add(new JObject { ["line"] = i + 1, ["reason"] = "not valid JSON: " + ex.Message }); }
            }
            return list;
        }

        private static JObject ReadJsonFile(string path, out string problem)
        {
            problem = null;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path, Encoding.UTF8))) { DateParseHandling = DateParseHandling.None })
                {
                    if (JToken.ReadFrom(reader) is JObject o) return o;
                    problem = "not a JSON object";
                    return null;
                }
            }
            catch (JsonException ex) { problem = "not valid JSON: " + ex.Message; return null; }
            catch (IOException ex) { problem = "could not be read: " + ex.Message; return null; }
            catch (UnauthorizedAccessException ex) { problem = "could not be read: " + ex.Message; return null; }
        }

        /// <summary>
        /// Temporary file in the same folder, a move that cannot overwrite, then the bytes
        /// read back and compared. Returns the evidence (path, bytes, sha256).
        /// </summary>
        private static JObject WriteBytesNoOverwriteVerified(string path, byte[] bytes)
        {
            if (File.Exists(path)) throw new IOException(path + " already exists and is never overwritten.");
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(tmp, path, false);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            byte[] back = File.ReadAllBytes(path);
            if (!back.AsSpan().SequenceEqual(bytes))
                throw new IOException(path + " was written but reads back different from what was intended; it is on disk and NOT trustworthy.");
            return new JObject { ["path"] = path, ["bytes"] = back.LongLength, ["sha256"] = Sha256Hex(back), ["reread_matches"] = true };
        }

        private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static string Utc(DateTime t) =>
            t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
