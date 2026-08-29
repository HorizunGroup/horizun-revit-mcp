// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_coordination - the durable side of clash detection. horizun_clash
// with record_findings=true measures and folds; this command is how people
// WORK the ledger: list what is open, move a finding through its states,
// export the whole thing for the meeting.
//
// The ledger is BRIDGE state, not model state: nothing here opens a Revit
// transaction, so there is no token - but every write is still re-read from
// disk before success is claimed, because the contract does not bend for
// small files. resolved_by_model cannot be asserted here at all; that status
// is detection's verdict (see CoordinationRules).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class CoordinationCommand : ICommand
    {
        public string Name => "horizun_coordination";
        public string Description =>
            "List, update and export the durable clash-finding ledger horizun_clash record_findings maintains: " +
            "stable pair identities, open/assigned/accepted_risk/closed_by_decision states, and the measured " +
            "resolved_by_model that only a complete detection run can set.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string operation = (request.Value<string>("operation") ?? "list").ToLowerInvariant();
            string ledgerPath = CoordinationLedger.PathFor(doc.Title, SafePath(doc));

            switch (operation)
            {
                case "evidence": return Evidence(doc, request, ledgerPath);
                case "list": return List(doc, request, ledgerPath);
                case "update": return Update(doc, request, ledgerPath);
                case "export": return Export(doc, request, ledgerPath);
                default:
                    return CommandResult.Fail("operation '" + operation + "' (known: list, update, export, evidence) is not one this command understands. " +
                        "Known: list, update, export. (Detection and folding live in horizun_clash record_findings.)");
            }
        }

        private static CommandResult List(Document doc, JObject request, string ledgerPath)
        {
            string statusFilter = request.Value<string>("status");
            string assigneeFilter = request.Value<string>("assignee");
            int maxRows = Math.Max(1, Math.Min(500, request.Value<int?>("max_rows") ?? 100));

            Dictionary<string, CoordinationFinding> findings = CoordinationLedger.Load(ledgerPath, out _);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (CoordinationFinding f in findings.Values)
            {
                int n; counts.TryGetValue(f.Status, out n); counts[f.Status] = n + 1;
            }

            List<CoordinationFinding> selected = findings.Values
                .Where(f => statusFilter == null || string.Equals(f.Status, statusFilter, StringComparison.Ordinal))
                .Where(f => assigneeFilter == null || string.Equals(f.Assignee, assigneeFilter, StringComparison.Ordinal))
                .OrderBy(f => f.Status, StringComparer.Ordinal)
                .ThenByDescending(f => f.LastSeenUtc, StringComparer.Ordinal)
                .ToList();

            var rows = new JArray(selected.Take(maxRows).Select(f =>
            {
                JObject row = CoordinationLedger.ToJson(f);
                row["finding_id"] = f.Id;
                return row;
            }));
            var statusCounts = new JObject();
            foreach (var pair in counts.OrderBy(p => p.Key, StringComparer.Ordinal)) statusCounts[pair.Key] = pair.Value;
            return CommandResult.Ok(new JObject
            {
                ["document"] = doc.Title,
                ["ledger_path"] = ledgerPath,
                ["ledger_exists"] = File.Exists(ledgerPath),
                ["total"] = findings.Count,
                ["matched"] = selected.Count,
                ["returned"] = rows.Count,
                ["truncated"] = selected.Count > rows.Count,
                ["status_counts"] = statusCounts,
                ["rows"] = rows,
                ["note"] = File.Exists(ledgerPath)
                    ? null
                    : "No ledger exists for this document yet. Run horizun_clash with record_findings=true to open one."
            });
        }

        private static CommandResult Update(Document doc, JObject request, string ledgerPath)
        {
            string findingId = request.Value<string>("finding_id");
            if (string.IsNullOrWhiteSpace(findingId))
                return CommandResult.Fail("finding_id is required for update; take it from operation=list.");
            string newStatus = request.Value<string>("status");
            string assignee = request.Value<string>("assignee");
            string note = request.Value<string>("note");
            string comment = request.Value<string>("comment");
            if (newStatus == null && assignee == null && note == null && comment == null)
                return CommandResult.Fail("update needs at least one of status, assignee, note, comment. Nothing was changed.");

            Dictionary<string, CoordinationFinding> findings = CoordinationLedger.Load(ledgerPath, out _);
            CoordinationFinding finding;
            if (!findings.TryGetValue(findingId, out finding))
                return CommandResult.Fail("finding '" + findingId + "' does not exist in this document's ledger. " +
                    "Nothing was changed. operation=list shows the ids that do.");

            string statusBefore = finding.Status;
            string nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (newStatus != null)
            {
                string reason;
                if (!CoordinationRules.CanTransition(finding.Status, newStatus, out reason))
                    return CommandResult.Fail("Refused: " + reason + " Nothing was changed.");
                finding.Status = newStatus;
                if (newStatus == CoordinationRules.StatusOpen) finding.Regression = false;
                CoordinationRules.AppendEvent(finding, "status", statusBefore + " -> " + newStatus, nowUtc);
            }
            if (assignee != null)
            {
                finding.Assignee = assignee.Length == 0 ? null : assignee;
                CoordinationRules.AppendEvent(finding, "assignee",
                    assignee.Length == 0 ? "(cleared)" : assignee, nowUtc);
            }
            if (note != null) finding.Note = note.Length == 0 ? null : note;
            if (comment != null)
            {
                if (comment.Trim().Length == 0)
                    return CommandResult.Fail("comment must carry text; the history is append-only and an empty " +
                        "entry says nothing. Nothing was changed.");
                CoordinationRules.AppendEvent(finding, "comment", comment, nowUtc);
            }
            finding.UpdatedUtc = nowUtc;

            CoordinationLedger.Save(ledgerPath, doc.Title, findings);

            // Re-read from disk: the claim is "the ledger now says", so the ledger answers.
            Dictionary<string, CoordinationFinding> reread = CoordinationLedger.Load(ledgerPath, out _);
            CoordinationFinding verified;
            bool ok = reread.TryGetValue(findingId, out verified) &&
                      (newStatus == null || string.Equals(verified.Status, newStatus, StringComparison.Ordinal)) &&
                      (assignee == null || string.Equals(verified.Assignee, assignee.Length == 0 ? null : assignee, StringComparison.Ordinal)) &&
                      (note == null || string.Equals(verified.Note, note.Length == 0 ? null : note, StringComparison.Ordinal)) &&
                      (comment == null || (verified.History.Count > 0 &&
                          string.Equals(verified.History[verified.History.Count - 1].Text, comment, StringComparison.Ordinal)));
            if (!ok)
                return CommandResult.Fail("The ledger was written but the re-read does not show the update; " +
                    "inspect " + ledgerPath + ". Success is not claimed.");

            JObject row = CoordinationLedger.ToJson(verified);
            row["finding_id"] = findingId;
            return CommandResult.Ok(new JObject
            {
                ["document"] = doc.Title,
                ["ledger_path"] = ledgerPath,
                ["status_before"] = statusBefore,
                ["verified_after_reread"] = true,
                ["row"] = row
            });
        }

        private static CommandResult Export(Document doc, JObject request, string ledgerPath)
        {
            string path = request.Value<string>("path");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                return CommandResult.Fail("path is required and must be absolute.");
            string format = (request.Value<string>("format") ?? "csv").ToLowerInvariant();
            if (format != "csv" && format != "json" && format != "bcf")
                return CommandResult.Fail("format must be csv, json or bcf.");
            if (File.Exists(path) && request.Value<bool?>("overwrite") != true)
                return CommandResult.Fail("'" + path + "' already exists. Pass overwrite=true to replace it. " +
                    "Nothing was written.");

            Dictionary<string, CoordinationFinding> findings = CoordinationLedger.Load(ledgerPath, out _);
            List<CoordinationFinding> ordered = findings.Values
                .OrderBy(f => f.Status, StringComparer.Ordinal)
                .ThenBy(f => f.Id, StringComparer.Ordinal).ToList();

            if (format == "bcf") return ExportBcf(doc, path, ordered);

            string content;
            if (format == "csv")
            {
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(",", CoordinationRules.CsvHeader));
                foreach (CoordinationFinding f in ordered) sb.AppendLine(CoordinationRules.CsvRow(f));
                content = sb.ToString();
            }
            else
            {
                var rows = new JArray(ordered.Select(f =>
                {
                    JObject row = CoordinationLedger.ToJson(f);
                    row["finding_id"] = f.Id;
                    return row;
                }));
                content = new JObject
                {
                    ["schema"] = CoordinationLedger.Schema,
                    ["document"] = doc.Title,
                    ["exported_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["findings"] = rows
                }.ToString();
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, new UTF8Encoding(false));

            // The evidence is the file on disk, so the file on disk is what gets measured.
            byte[] written = File.ReadAllBytes(path);
            string sha;
            using (var hasher = SHA256.Create())
                sha = BitConverter.ToString(hasher.ComputeHash(written)).Replace("-", "").ToLowerInvariant();
            return CommandResult.Ok(new JObject
            {
                ["document"] = doc.Title,
                ["path"] = path,
                ["format"] = format,
                ["findings_exported"] = ordered.Count,
                ["bytes"] = written.Length,
                ["sha256"] = sha,
                ["verified_by_reread"] = true
            });
        }

        // ---- BCF 2.1: structurally verified, and EXACTLY that claimed. ----------
        private static CommandResult ExportBcf(Document doc, string path, List<CoordinationFinding> ordered)
        {
            if (File.Exists(path)) File.Delete(path);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (FileStream stream = File.Create(path))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                WriteZipEntry(zip, "bcf.version", CoordinationRules.BcfVersionXml());
                foreach (CoordinationFinding f in ordered)
                    WriteZipEntry(zip, CoordinationRules.BcfTopicGuid(f.Id) + "/markup.bcf",
                                  CoordinationRules.BcfMarkupXml(f, doc.Title));
            }

            // Verify by RE-READING the zip: every entry re-parsed as XML, topics counted.
            int topics = 0;
            using (FileStream stream = File.OpenRead(path))
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read))
            {
                if (zip.GetEntry("bcf.version") == null)
                    return CommandResult.Fail("The BCF was written but re-reading it finds no bcf.version entry; " +
                        "inspect " + path + ". Success is not claimed.");
                foreach (System.IO.Compression.ZipArchiveEntry entry in zip.Entries)
                {
                    if (!entry.FullName.EndsWith("/markup.bcf", StringComparison.Ordinal)) continue;
                    var xml = new System.Xml.XmlDocument();
                    using (Stream entryStream = entry.Open()) xml.Load(entryStream);
                    if (xml.DocumentElement?.Name != "Markup" ||
                        xml.DocumentElement.SelectSingleNode("Topic") == null)
                        return CommandResult.Fail("Entry '" + entry.FullName + "' re-read as XML but is not a " +
                            "BCF Markup with a Topic. Success is not claimed.");
                    topics++;
                }
            }
            if (topics != ordered.Count)
                return CommandResult.Fail("The BCF re-read holds " + topics + " topic(s) for " + ordered.Count +
                    " finding(s). Success is not claimed.");

            byte[] written = File.ReadAllBytes(path);
            string sha;
            using (var hasher = SHA256.Create())
                sha = BitConverter.ToString(hasher.ComputeHash(written)).Replace("-", "").ToLowerInvariant();
            return CommandResult.Ok(new JObject
            {
                ["document"] = doc.Title,
                ["path"] = path,
                ["format"] = "bcf",
                ["findings_exported"] = ordered.Count,
                ["bytes"] = written.Length,
                ["sha256"] = sha,
                ["verified_by_reread"] = true,
                ["verification_scope"] = "STRUCTURAL: the zip was re-read, every markup.bcf re-parsed as XML and " +
                    "counted against the ledger. No consumer's round-trip is proven - a tool that rejects this " +
                    "file is a finding to bring back, not one this export can rule out."
            });
        }

        private static void WriteZipEntry(System.IO.Compression.ZipArchive zip, string name, string content)
        {
            System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry(name);
            using (Stream stream = entry.Open())
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        // ---- evidence: the section that SHOWS a finding, ready to create. -------
        private static CommandResult Evidence(Document doc, JObject request, string ledgerPath)
        {
            string findingId = request.Value<string>("finding_id");
            if (string.IsNullOrWhiteSpace(findingId))
                return CommandResult.Fail("finding_id is required for evidence; take it from operation=list.");
            Dictionary<string, CoordinationFinding> findings = CoordinationLedger.Load(ledgerPath, out _);
            CoordinationFinding finding;
            if (!findings.TryGetValue(findingId, out finding))
                return CommandResult.Fail("finding '" + findingId + "' does not exist in this document's ledger.");
            if (finding.PointMm == null)
                return CommandResult.Fail("finding '" + findingId + "' carries no clash point, so no view can be " +
                    "aimed at it. Re-run horizun_clash with record_findings=true to refresh it.");

            double halfMm = request.Value<double?>("window_mm") ?? 1500;
            if (halfMm < 100 || halfMm > 20000)
                return CommandResult.Fail("window_mm must be between 100 and 20000.");
            double x = finding.PointMm[0], y = finding.PointMm[1];
            return CommandResult.Ok(new JObject
            {
                ["finding_id"] = findingId,
                ["point_mm"] = new JArray(finding.PointMm),
                ["note"] = "Create the section below (it crosses the finding, looking along +Y, a " +
                           (2 * halfMm) + " mm window), then call horizun_capture_view on the id it returns.",
                ["next_arguments"] = new JObject
                {
                    ["tool"] = "horizun_manage_views",
                    ["arguments"] = new JObject
                    {
                        ["target_document"] = doc.Title,
                        ["units"] = "mm",
                        ["actions"] = new JArray(new JObject
                        {
                            ["key"] = "finding-section",
                            ["operation"] = "create_section",
                            ["name"] = "HZ_FINDING_" + findingId.Substring(0, 8),
                            ["line_start"] = new JArray(x - halfMm, y, 0),
                            ["line_end"] = new JArray(x + halfMm, y, 0),
                            ["depth"] = halfMm
                        })
                    }
                }
            });
        }

        private static string SafePath(Document doc)
        {
            try { return doc.PathName; } catch { return null; }
        }
    }
}
