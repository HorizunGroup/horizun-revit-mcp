// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_budget_compare - a HOST-RESIDENT tool. It never touches Revit: the
// takeoff already happened (horizun_quantities, mode 'takeoff'), the budget is
// a workbook on disk, and the join between them is arithmetic that lives in
// Core/BudgetComparisonRules.cs so it can be proved without a building.
//
// What this file adds to the rules is the plumbing, and the plumbing has one
// principle: EACH DESTINATION IS ITS OWN STORY. The comparison is computed once
// and always returned. Then, for every destination the caller declared, the
// tool writes and reports SEPARATELY - written | replayed | skipped | failed |
// in_doubt, with the evidence that status rests on. There is no global
// transaction to claim, because none exists: a workbook on disk and a Power BI
// table are two systems with no shared commit, and a reply that pretended
// otherwise would be the kind of assurance this bridge refuses to give. A
// failed Excel write does not undo a Power BI push; the reply says which is
// which.
//
// Nothing is written twice. The whole call claims a durable idempotency key,
// and each destination goes through the writer that already owns its ledger -
// ExcelWriteRows for the workbook (lock, backup, re-read verification),
// PowerBiPush for the table (dry_run by default, no automatic retry: a lost
// HTTP answer is in_doubt and stays that way until a person decides - that is
// PowerBiPush's design decision, not restated here).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class BudgetCompare
    {
        internal const string ToolName = "horizun_budget_compare";
        private const int PowerBiMaxRows = 10000;

        internal static JObject Handle(JObject args, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Handle(args,
                new DurableCommandLedger(retentionLog: message => Log.Info(message)),
                request => PowerBiPush.Handle(request, cancellationToken),
                cancellationToken);
        }

        /// <summary>
        /// The testable entry: the ledger and the Power BI sender are injected so the
        /// Excel destination can be proved against a disposable workbook and the Power BI
        /// one against a recording HttpClient, in dry_run only.
        /// </summary>
        internal static JObject Handle(JObject args, DurableCommandLedger ledger, Func<JObject, JObject> powerBi,
                                       CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            args = args ?? new JObject();

            // ---- arguments, all of them, before any file is opened. ----
            RefuseUnknownKeys(args, "", "model_rows", "model_rows_path", "baseline", "mapping", "outputs", "idempotency_key");

            JToken modelRowsTok = args["model_rows"];
            string modelRowsPath = (string)args["model_rows_path"];
            bool inline = modelRowsTok != null && modelRowsTok.Type != JTokenType.Null;
            bool fromPath = !string.IsNullOrWhiteSpace(modelRowsPath);
            if (inline == fromPath)
                throw new ToolRefusal("Pass exactly one of model_rows (the takeoff rows or the whole horizun_quantities mode='takeoff' reply) " +
                                      "or model_rows_path (a JSON file holding the same). Nothing was read.");

            string problem;
            BudgetComparisonMapping mapping = BudgetComparisonRules.ReadMapping(args["mapping"], out problem);
            if (mapping == null) throw new ToolRefusal(problem + " Nothing was read.");

            BaselineSpec baselineSpec = ReadBaselineSpec(args["baseline"]);
            OutputSpec outputs = ReadOutputSpec(args["outputs"]);

            // ---- THE PROFILE, PER CALL, BEFORE ANY FILE IS OPENED. ----
            //
            // This tool is classified ToolEffect.ExternalSideEffectOnRequest, which means
            // every permission_profile ADMITS it - the comparison alone reads a workbook
            // and writes nothing, and a read_only machine is entitled to arithmetic. What
            // needs full_write is a DESTINATION, and this is where that is enforced. The
            // classification without this check would be a hole rather than a fix.
            //
            // A dry-run Power BI destination is gated too. It sends nothing, but its reply
            // reports credentials_configured for this machine, and a profile that forbids
            // reaching outside the model forbids answering questions about what is out
            // there as well. Refusing the DECLARATION also keeps one rule instead of two.
            if (outputs.Excel != null || outputs.PowerBi != null)
            {
                string profileRefusal;
                if (!Settings.AllowsExternalSideEffect(out profileRefusal))
                    throw new ToolRefusal("outputs declares " + string.Join(" and ", DeclaredDestinations(outputs)) +
                                          ", and that is what needs the profile: " + profileRefusal +
                                          " Nothing was read and nothing was written. The comparison ITSELF is " +
                                          "available at this profile - send the same call without 'outputs' and " +
                                          "the result comes back in full.");
            }

            string key = (string)args["idempotency_key"];
            bool needsKey = outputs.Excel != null || (outputs.PowerBi != null && !outputs.PowerBi.DryRun);
            if (needsKey && string.IsNullOrWhiteSpace(key))
                throw new ToolRefusal("idempotency_key is required when outputs.excel is declared or outputs.power_bi.dry_run is false: " +
                                      "a workbook written twice is two workbooks, and rows pushed twice are counted twice. Generate a new " +
                                      "UUID for each deliberate run and keep it unchanged only for retries. Nothing was read or written.");
            if (key != null && key.Length > 200) throw new ToolRefusal("idempotency_key must be at most 200 characters.");

            // ---- the model rows. ----
            JToken modelToken = modelRowsTok;
            if (fromPath)
            {
                if (!File.Exists(modelRowsPath)) throw new FileNotFoundException("model_rows_path not found: " + modelRowsPath);
                string text;
                try { text = File.ReadAllText(modelRowsPath, Encoding.UTF8); }
                catch (IOException ex) { throw new ToolRefusal("model_rows_path could not be read: " + ex.Message); }
                try { modelToken = JToken.Parse(text); }
                catch (JsonException ex) { throw new ToolRefusal("model_rows_path is not valid JSON: " + ex.Message); }
            }
            List<BudgetComparisonRules.ModelRow> modelRows = BudgetComparisonRules.ReadModelRows(modelToken, mapping.CodeField, out problem);
            if (modelRows == null) throw new ToolRefusal(problem);

            // ---- the baseline, through the reader that already exists. ----
            cancellationToken.ThrowIfCancellationRequested();
            JObject baselineRead = ExcelReadRows.Handle(new JObject
            {
                ["file_path"] = baselineSpec.FilePath,
                ["sheet"] = baselineSpec.Sheet,
                ["max_rows"] = baselineSpec.MaxRows
            });
            JObject baselineSource;
            JArray baselineLinesJson = BaselineLinesFrom(baselineRead, baselineSpec, out baselineSource);
            int skippedBlankCode;
            List<BudgetComparisonRules.BaselineLine> baseline = BudgetComparisonRules.ReadBaseline(baselineLinesJson, out skippedBlankCode, out problem);
            if (baseline == null) throw new ToolRefusal("baseline: " + problem);
            baselineSource["lines"] = baseline.Count;
            baselineSource["rows_skipped_blank_code"] = skippedBlankCode;
            if (baseline.Count == 0)
                throw new ToolRefusal("The baseline sheet '" + (string)baselineRead["sheet"] + "' has no line with a non-blank code below header_row " +
                                      baselineSpec.HeaderRow + " (" + skippedBlankCode + " row(s) had a blank code). A comparison against an empty " +
                                      "budget would report every model code as 'added', which is not a finding about the budget.");

            // ---- the comparison. Always computed, always returned. ----
            JObject comparison = BudgetComparisonRules.Compare(modelRows, baseline, mapping);

            var reply = new JObject
            {
                ["comparison"] = comparison,
                ["model_source"] = new JObject
                {
                    ["from"] = fromPath ? "model_rows_path" : "model_rows",
                    ["path"] = fromPath ? modelRowsPath : null,
                    ["rows"] = modelRows.Count
                },
                ["baseline_source"] = baselineSource,
                ["destinations"] = new JArray(),
                ["destinations_note"] = "each destination is written and reported ON ITS OWN. There is no global transaction: a " +
                                        "failed Excel write does not undo a Power BI push and a refused Power BI push does not " +
                                        "remove the workbook. Read every entry, not the first.",
                ["idempotency_key"] = key,
                ["replayed"] = false
            };

            if (outputs.Excel == null && outputs.PowerBi == null) return reply;

            // ---- destinations, under one durable claim. ----
            //
            // The claim is over the whole call. A retry with the same key after a lost
            // reply gets the recorded reply back - every destination's status included -
            // and writes nothing. Each destination then uses a DERIVED key in the writer
            // that owns its ledger, so a crash between the two leaves the second writer's
            // own record to answer for it, not this tool's guess.
            DurableCommandDecision decision = null;
            if (needsKey)
            {
                string fingerprint = RequestFingerprint.OfOperation(ToolName, ScopeOf(outputs), args, "idempotency_key");
                decision = ledger.Claim(key, ToolName, fingerprint);
                if (decision.Outcome == DurableCommandOutcome.Replay) return Replay(decision.ReplayResult);
                if (!decision.IsFresh) throw new ToolRefusal(decision.Message);
            }

            var destinations = (JArray)reply["destinations"];
            if (outputs.Excel != null)
                destinations.Add(WriteExcel(outputs.Excel, comparison, key, ledger, cancellationToken));
            if (outputs.PowerBi != null)
                destinations.Add(PushPowerBi(outputs.PowerBi, comparison, key, powerBi, cancellationToken));

            if (decision != null) ledger.Complete(decision, CommandResult.Ok(reply));
            return reply;
        }

        // ------------------------------------------------------------------
        // Destinations.
        // ------------------------------------------------------------------

        private static JObject WriteExcel(ExcelSpec spec, JObject comparison, string key, DurableCommandLedger ledger,
                                          CancellationToken cancellationToken)
        {
            var report = new JObject { ["destination"] = "excel", ["file_path"] = spec.FilePath, ["sheet"] = spec.Sheet };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool existed = File.Exists(spec.FilePath);
                if (existed && spec.OverwritePolicy == "refuse")
                {
                    report["status"] = "skipped";
                    report["reason"] = "the file already exists and overwrite_policy is 'refuse' (the default). Nothing was touched. " +
                                       "Pass overwrite_policy='replace' to back it up and replace it, or a different file_path.";
                    return report;
                }

                string backupPath = null;
                if (existed)
                {
                    string stamp = System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    backupPath = spec.FilePath + "." + stamp + ".horizunbak";
                    File.Copy(spec.FilePath, backupPath, false);
                }
                string dir = Path.GetDirectoryName(spec.FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(spec.FilePath, ExcelWriteRows.MinimalWorkbook(spec.Sheet));

                var rowsJson = new JArray();
                foreach (IList<object> row in BudgetComparisonRules.SheetRows(comparison))
                {
                    var cells = new JArray();
                    foreach (object cell in row) cells.Add(cell == null ? JValue.CreateNull() : JToken.FromObject(cell));
                    rowsJson.Add(cells);
                }
                JObject written = ExcelWriteRows.Handle(new JObject
                {
                    ["file_path"] = spec.FilePath,
                    ["sheet"] = spec.Sheet,
                    ["rows"] = rowsJson,
                    ["idempotency_key"] = key + "/excel"
                }, ledger, cancellationToken);

                bool replayed = written["replayed"] != null && written["replayed"].Type == JTokenType.Boolean && (bool)written["replayed"];
                report["status"] = replayed ? "replayed" : "written";
                report["created"] = !existed;
                report["replaced_backup_path"] = backupPath;
                report["evidence"] = new JObject
                {
                    ["rows_written"] = written["rows_written"],
                    ["header_row"] = 1,
                    ["first_data_row"] = 2,
                    ["last_row"] = written["last_new_row"],
                    ["bytes"] = written["bytes_after"],
                    ["sha256"] = written["sha256_after"],
                    ["verified"] = written["verified"],
                    ["verified_means"] = written["verified_means"],
                    ["writer_backup_path"] = written["backup_path"],
                    ["ledger_key"] = key + "/excel"
                };
                return report;
            }
            catch (OperationCanceledException) { throw; }
            catch (ToolRefusal ex)
            {
                report["status"] = "failed";
                report["error"] = ex.Message;
                report["evidence"] = new JObject { ["ledger_key"] = key + "/excel" };
                return report;
            }
            catch (Exception ex)
            {
                report["status"] = "failed";
                report["error"] = ex.GetType().Name + ": " + ex.Message;
                report["evidence"] = new JObject { ["ledger_key"] = key + "/excel" };
                return report;
            }
        }

        private static JObject PushPowerBi(PowerBiSpec spec, JObject comparison, string key, Func<JObject, JObject> powerBi,
                                           CancellationToken cancellationToken)
        {
            var report = new JObject
            {
                ["destination"] = "power_bi",
                ["dataset_id"] = spec.DatasetId,
                ["workspace_id"] = spec.WorkspaceId,
                ["table"] = spec.Table,
                ["dry_run"] = spec.DryRun
            };
            string derivedKey = key == null ? null : key + "/powerbi";
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                JArray rows = BudgetComparisonRules.PowerBiRows(comparison, key ?? "dry-run");
                if (rows.Count == 0)
                {
                    report["status"] = "skipped";
                    report["reason"] = "the comparison produced no lines, and horizun_power_bi_push refuses an empty batch.";
                    return report;
                }
                if (rows.Count > PowerBiMaxRows)
                {
                    report["status"] = "failed";
                    report["error"] = "the comparison has " + rows.Count + " lines and one push carries at most " + PowerBiMaxRows +
                                      ". Nothing was sent; split the takeoff by category or code range.";
                    return report;
                }
                var request = new JObject
                {
                    ["dataset_id"] = spec.DatasetId,
                    ["table"] = spec.Table,
                    ["rows"] = rows,
                    ["dry_run"] = spec.DryRun
                };
                if (spec.WorkspaceId != null) request["workspace_id"] = spec.WorkspaceId;
                if (derivedKey != null) request["idempotency_key"] = derivedKey;

                JObject answer = powerBi(request);
                if (spec.DryRun)
                {
                    report["status"] = "skipped";
                    report["reason"] = "dry_run: the rows and the destination were validated and NOTHING was sent. Pass dry_run=false with an idempotency_key to push.";
                }
                else
                {
                    report["status"] = "written";
                }
                report["evidence"] = answer;
                if (derivedKey != null) report["evidence"]["ledger_key"] = derivedKey;
                return report;
            }
            catch (OperationCanceledException) { throw; }
            catch (ToolRefusal ex)
            {
                // PowerBiPush names the one outcome it cannot know in its own words. That
                // string is the contract its tests pin, and the only signal it emits.
                bool inDoubt = ex.Message.IndexOf("in_doubt", StringComparison.Ordinal) >= 0;
                report["status"] = inDoubt ? "in_doubt" : "failed";
                report["error"] = ex.Message;
                report["evidence"] = new JObject
                {
                    ["ledger_key"] = derivedKey,
                    ["note"] = inDoubt
                        ? "Microsoft may have received these rows. Nothing is re-sent automatically - inspect the table, then choose a NEW key only if you decide the push must happen again."
                        : "definitive: no rows were accepted. A retry with the same key replays this failure; use a new key after fixing the cause."
                };
                return report;
            }
            catch (Exception ex)
            {
                report["status"] = "failed";
                report["error"] = ex.GetType().Name + ": " + ex.Message;
                report["evidence"] = new JObject { ["ledger_key"] = derivedKey };
                return report;
            }
        }

        private static JObject Replay(CommandResult result)
        {
            if (result == null) throw new ToolRefusal("The durable replay record had no result.");
            if (!result.Success)
                throw new ToolRefusal((result.Error ?? "The recorded comparison failed.") +
                                      " This is the recorded answer for that idempotency_key; nothing was written now.");
            if (!(result.Data is JObject recorded))
                throw new ToolRefusal("The durable replay record for this key is not a comparison result.");
            var clone = (JObject)recorded.DeepClone();
            clone["replayed"] = true;
            clone["replay_note"] = "This reply was recorded by an EARLIER call with this same idempotency_key. No file was read or " +
                                   "written and nothing was pushed now; every destination status above describes that first run.";
            return clone;
        }

        // ------------------------------------------------------------------
        // The baseline sheet -> budget lines.
        // ------------------------------------------------------------------

        private sealed class BaselineSpec
        {
            public string FilePath, Sheet;
            public int HeaderRow = 1, MaxRows = ExcelReadRows.MaxRows;
            public Dictionary<string, JToken> Columns = new Dictionary<string, JToken>(StringComparer.Ordinal);
        }

        private static readonly string[] ColumnRoles = { "code", "description", "unit", "quantity", "unit_price", "currency" };

        private static BaselineSpec ReadBaselineSpec(JToken token)
        {
            var o = token as JObject;
            if (o == null) throw new ToolRefusal("baseline is required: {file_path, sheet?, header_row?, max_rows?, columns: {code, description?, unit, quantity, unit_price?, currency?}}.");
            RefuseUnknownKeys(o, "baseline.", "file_path", "sheet", "header_row", "max_rows", "columns");
            var spec = new BaselineSpec { FilePath = (string)o["file_path"], Sheet = (string)o["sheet"] };
            if (string.IsNullOrWhiteSpace(spec.FilePath)) throw new ToolRefusal("baseline.file_path is required.");
            if (o["header_row"] != null)
            {
                if (o["header_row"].Type != JTokenType.Integer || (int)o["header_row"] < 1)
                    throw new ToolRefusal("baseline.header_row must be an integer >= 1.");
                spec.HeaderRow = (int)o["header_row"];
            }
            if (o["max_rows"] != null)
            {
                if (o["max_rows"].Type != JTokenType.Integer || (int)o["max_rows"] < 1 || (int)o["max_rows"] > ExcelReadRows.MaxRows)
                    throw new ToolRefusal("baseline.max_rows must be 1.." + ExcelReadRows.MaxRows + ".");
                spec.MaxRows = (int)o["max_rows"];
            }
            var columns = o["columns"] as JObject;
            if (columns == null) throw new ToolRefusal("baseline.columns is required: {code, description?, unit, quantity, unit_price?, currency?}, each a header name or a 1-based column index.");
            RefuseUnknownKeys(columns, "baseline.columns.", ColumnRoles);
            foreach (string role in ColumnRoles)
            {
                JToken sel = columns[role];
                if (sel == null || sel.Type == JTokenType.Null) continue;
                if (sel.Type == JTokenType.Integer)
                {
                    if ((int)sel < 1) throw new ToolRefusal("baseline.columns." + role + " as an index must be >= 1 (1-based).");
                }
                else if (sel.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)sel))
                    throw new ToolRefusal("baseline.columns." + role + " must be a header name or a 1-based column index.");
                spec.Columns[role] = sel;
            }
            foreach (string required in new[] { "code", "unit", "quantity" })
                if (!spec.Columns.ContainsKey(required))
                    throw new ToolRefusal("baseline.columns." + required + " is required.");
            return spec;
        }

        /// <summary>
        /// Resolve every declared column against the header row, then read each stored
        /// row below it into a budget line. row_index is the 1-based POSITION among the
        /// rows horizun_excel_read_rows returned, which equals the sheet's row number only
        /// when the sheet stores no empty rows - said in the reply rather than assumed.
        /// </summary>
        private static JArray BaselineLinesFrom(JObject read, BaselineSpec spec, out JObject source)
        {
            var rows = (JArray)read["rows"];
            if (rows.Count < spec.HeaderRow)
                throw new ToolRefusal("baseline.header_row is " + spec.HeaderRow + " but the sheet '" + (string)read["sheet"] + "' returned only " +
                                      rows.Count + " row(s). Sheets present: " + string.Join(", ", read["sheets"].Select(t => (string)t)) + ".");
            var header = rows[spec.HeaderRow - 1] as JArray ?? new JArray();
            var headerNames = header.Select(c => BudgetComparisonRules.TextOf(c)).ToList();

            var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
            var resolvedJson = new JObject();
            foreach (var kv in spec.Columns)
            {
                int index;
                if (kv.Value.Type == JTokenType.Integer) index = (int)kv.Value - 1;
                else
                {
                    string wanted = ((string)kv.Value).Trim();
                    var matches = new List<int>();
                    for (int i = 0; i < headerNames.Count; i++)
                        if (headerNames[i] != null && string.Equals(headerNames[i].Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                            matches.Add(i);
                    if (matches.Count == 0)
                        throw new ToolRefusal("baseline.columns." + kv.Key + " = '" + wanted + "' matches no header in row " + spec.HeaderRow +
                                              ". Headers present: " + string.Join(" | ", headerNames.Where(h => !string.IsNullOrWhiteSpace(h))) +
                                              ". Name one of them, or pass the 1-based column index.");
                    if (matches.Count > 1)
                        throw new ToolRefusal("baseline.columns." + kv.Key + " = '" + wanted + "' matches " + matches.Count + " headers (columns " +
                                              string.Join(", ", matches.Select(m => ExcelWriteRows.ColumnLetter(m))) + "). Pass the column index instead.");
                    index = matches[0];
                }
                resolved[kv.Key] = index;
                resolvedJson[kv.Key] = new JObject
                {
                    ["column"] = ExcelWriteRows.ColumnLetter(index),
                    ["index"] = index + 1,
                    ["header"] = index < headerNames.Count ? headerNames[index] : null
                };
            }
            var distinct = new HashSet<int>(resolved.Values);
            if (distinct.Count != resolved.Count)
                throw new ToolRefusal("baseline.columns resolves two roles to the same column: " +
                                      string.Join(", ", resolved.Select(r => r.Key + "=" + ExcelWriteRows.ColumnLetter(r.Value))) + ".");

            var lines = new JArray();
            for (int r = spec.HeaderRow; r < rows.Count; r++)
            {
                var cells = rows[r] as JArray ?? new JArray();
                var line = new JObject { ["row_index"] = r + 1 };
                foreach (var kv in resolved)
                    line[kv.Key] = kv.Value < cells.Count ? cells[kv.Value] : JValue.CreateNull();
                lines.Add(line);
            }

            source = new JObject
            {
                ["file_path"] = read["file_path"],
                ["sha256"] = read["sha256"],
                ["sheet"] = read["sheet"],
                ["header_row"] = spec.HeaderRow,
                ["columns"] = resolvedJson,
                ["rows_read"] = rows.Count,
                ["rows_truncated"] = read["rows_truncated"],
                ["formulas_without_cached_value"] = read["formulas_without_cached_value"],
                ["merged_ranges"] = read["merged_ranges"],
                ["row_index_means"] = "1-based position among the rows the sheet stores, as horizun_excel_read_rows returns them; " +
                                      "equal to the Excel row number when the sheet stores no empty rows."
            };
            int truncated = read["rows_truncated"] == null ? 0 : (int)read["rows_truncated"];
            if (truncated > 0)
                throw new ToolRefusal("The baseline sheet holds " + truncated + " more row(s) than baseline.max_rows (" + spec.MaxRows +
                                      ") allowed to read. A comparison against a prefix of the budget would call every unread line 'added' " +
                                      "in the model; raise max_rows (at most " + ExcelReadRows.MaxRows + ") or split the sheet.");
            return lines;
        }

        // ------------------------------------------------------------------
        // Outputs.
        // ------------------------------------------------------------------

        private sealed class ExcelSpec { public string FilePath, Sheet = "Comparison", OverwritePolicy = "refuse"; }
        private sealed class PowerBiSpec { public string WorkspaceId, DatasetId, Table; public bool DryRun = true; }
        private sealed class OutputSpec { public ExcelSpec Excel; public PowerBiSpec PowerBi; }

        private static OutputSpec ReadOutputSpec(JToken token)
        {
            var spec = new OutputSpec();
            if (token == null || token.Type == JTokenType.Null) return spec;
            var o = token as JObject;
            if (o == null) throw new ToolRefusal("outputs must be an object {excel?, power_bi?}.");
            RefuseUnknownKeys(o, "outputs.", "excel", "power_bi");

            var excel = o["excel"] as JObject;
            if (o["excel"] != null && o["excel"].Type != JTokenType.Null)
            {
                if (excel == null) throw new ToolRefusal("outputs.excel must be an object {file_path, sheet?, overwrite_policy?}.");
                RefuseUnknownKeys(excel, "outputs.excel.", "file_path", "sheet", "overwrite_policy");
                var e = new ExcelSpec { FilePath = (string)excel["file_path"] };
                if (string.IsNullOrWhiteSpace(e.FilePath)) throw new ToolRefusal("outputs.excel.file_path is required.");
                if (!e.FilePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    throw new ToolRefusal("outputs.excel.file_path must end in .xlsx: the destination is a workbook, and a file named otherwise would be opened as something else.");
                if (excel["sheet"] != null && excel["sheet"].Type != JTokenType.Null) e.Sheet = (string)excel["sheet"];
                string sheetProblem = ExcelWriteRows.SheetNameProblem(e.Sheet);
                if (sheetProblem != null) throw new ToolRefusal("outputs.excel.sheet: " + sheetProblem);
                if (excel["overwrite_policy"] != null && excel["overwrite_policy"].Type != JTokenType.Null)
                {
                    e.OverwritePolicy = (string)excel["overwrite_policy"];
                    if (e.OverwritePolicy != "refuse" && e.OverwritePolicy != "replace")
                        throw new ToolRefusal("outputs.excel.overwrite_policy must be 'refuse' or 'replace'.");
                }
                spec.Excel = e;
            }

            var pbi = o["power_bi"] as JObject;
            if (o["power_bi"] != null && o["power_bi"].Type != JTokenType.Null)
            {
                if (pbi == null) throw new ToolRefusal("outputs.power_bi must be an object {workspace_id?, dataset_id, table, dry_run?}.");
                RefuseUnknownKeys(pbi, "outputs.power_bi.", "workspace_id", "dataset_id", "table", "dry_run");
                var p = new PowerBiSpec
                {
                    WorkspaceId = string.IsNullOrWhiteSpace((string)pbi["workspace_id"]) ? null : (string)pbi["workspace_id"],
                    DatasetId = (string)pbi["dataset_id"],
                    Table = (string)pbi["table"]
                };
                if (string.IsNullOrWhiteSpace(p.DatasetId)) throw new ToolRefusal("outputs.power_bi.dataset_id is required.");
                if (string.IsNullOrWhiteSpace(p.Table)) throw new ToolRefusal("outputs.power_bi.table is required.");
                if (pbi["dry_run"] != null && pbi["dry_run"].Type != JTokenType.Null)
                {
                    if (pbi["dry_run"].Type != JTokenType.Boolean) throw new ToolRefusal("outputs.power_bi.dry_run must be true or false.");
                    p.DryRun = (bool)pbi["dry_run"];
                }
                spec.PowerBi = p;
            }
            return spec;
        }

        /// <summary>
        /// The destinations this call declared, named for the refusal. A caller told only
        /// "outputs needs full_write" has to guess which half of their arguments did it.
        /// </summary>
        private static List<string> DeclaredDestinations(OutputSpec outputs)
        {
            var parts = new List<string>();
            if (outputs.Excel != null) parts.Add("an Excel workbook at " + outputs.Excel.FilePath);
            if (outputs.PowerBi != null)
                parts.Add("a Power BI table '" + outputs.PowerBi.Table + "' in dataset " + outputs.PowerBi.DatasetId +
                          (outputs.PowerBi.DryRun ? " (dry_run)" : ""));
            return parts;
        }

        private static string ScopeOf(OutputSpec outputs)
        {
            var parts = new List<string>();
            if (outputs.Excel != null) parts.Add("xlsx:" + outputs.Excel.FilePath.ToLowerInvariant());
            if (outputs.PowerBi != null) parts.Add("powerbi:" + (outputs.PowerBi.WorkspaceId ?? "myorg") + ":" + outputs.PowerBi.DatasetId + ":" + outputs.PowerBi.Table);
            return string.Join(";", parts);
        }

        private static void RefuseUnknownKeys(JObject o, string prefix, params string[] allowed)
        {
            foreach (JProperty p in o.Properties())
                if (Array.IndexOf(allowed, p.Name) < 0)
                    throw new ToolRefusal(prefix + p.Name + " is not a known key. Known: " + string.Join(", ", allowed) +
                                          ". Refused rather than ignored: an argument nobody reads is an instruction you believe was followed.");
        }
    }
}
