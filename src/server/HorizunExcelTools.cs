// -----------------------------------------------------------------------------
// Horizun — NEW FILE. Apache-2.0 (see LICENSE); original Horizun contribution.
//
// A HOST-SIDE Horizun tool. Unlike every other horizun_* tool this one does NOT
// forward to the Revit plugin — it does its work IN the server process (file I/O,
// hashing, and driving a real Microsoft Excel via COM). There is no
// ToolGateway.SendToRevit here, on purpose.
//
// Why real Excel and not a spreadsheet library: the user's master workbook carries
// an Excel Table (ListObject "Tabla512"), data validations and structured
// references. A library that writes cells but does not understand Tables strips the
// Table on save, and Excel then greets the user with a "we found a problem, want us
// to recover?" repair dialog. Real Excel is the ONLY engine that definitionally
// preserves that structure — so we drive it.
//
// Two real destructions shaped every guard in here:
//   (1) editing the master with a Table-stripping library forced Excel to "repair"
//       it — the Table, and its structured formulas, were gone;
//   (2) a mid-write OneDrive sync TRUNCATED the file to 2 rows AFTER a successful
//       save. The save reported success. The file on disk was ruined.
// So: never write the OneDrive/accented original directly — stage to a short ASCII
// scratch, verify by REOPENING from disk, and only then copy back with a byte-hash
// check that catches a truncated write-back. Take a timestamped backup first and
// return its path always, so a bad run is a recoverable run.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Server
{
    // COM automation of Office is Windows-only. This box (the Revit box) is always
    // Windows; the platform guard silences CA1416 for the whole class rather than
    // sprinkling #pragma warning disable around every dynamic call.
    [SupportedOSPlatform("windows")]
    public class HorizunExcelTools
    {
        [McpServerTool(Name = "horizun_excel_write_rows"),
         System.ComponentModel.Description(
            "Write/update cells in existing rows of an .xlsx by DRIVING REAL EXCEL (COM), so Excel Tables (ListObjects " +
            "like 'Tabla512'), validations and structured references survive the edit. A cell-writing library strips the " +
            "Table on save and Excel then throws a 'repair' dialog — this does not, because Excel itself does the write. " +
            "IT NEVER TOUCHES THE ORIGINAL IN PLACE. The file is copied to a short ASCII scratch path (Workbooks.Open " +
            "chokes on accents and OneDrive), edited there, and copied back ONLY after a reopen-from-disk verify passes; " +
            "a timestamped backup is taken first and its path is ALWAYS returned. " +
            "Columns resolve BY HEADER NAME, never by index (an off-by-one that shifts every value one column right never " +
            "throws and looks like a clean success — the read-back is what catches it). " +
            "ROW GUARD: each row carries an expected key (key_column/key_value that must ALREADY be in the target row); " +
            "a row whose key cell does not match is refused — writing the right value into the wrong row is the quiet " +
            "catastrophe this exists to prevent. " +
            "POST-VERIFY: after save it REOPENS the file and re-reads — it reports {saved_ok, tables_present, " +
            "row_count_before/after, cells_written:[{row, column_name, value_written, value_read_back}]}. A read_back that " +
            "differs from what was written is a FAILURE; a collapsed row_count catches the OneDrive truncation; either way " +
            "it RESTORES from the backup and reports, never leaving a corrupt file. " +
            "PROVENANCE on every read: absolute path, last-write-time, sha256, sheet, header_row and rows parsed — 'I read " +
            "THIS file, this hash' not 'I read the catalog'. " +
            "rows: JSON array of {\"key_column\":\"...\",\"key_value\":\"...\",\"cells\":{\"Header A\":value,...}}. " +
            "header_row is 1-based (default 1). Use dry_run=true first: it opens read-only on a staged copy, resolves " +
            "columns, checks the guards and reports what WOULD be written — no backup, no write.")]
        public static async Task<string> ExcelWriteRows(
            string workbook_path,
            string sheet,
            string rows,
            int header_row = 1,
            bool backup = true,
            bool dry_run = false)
        {
            try
            {
                // Office COM wants an STA thread; run the whole job on a dedicated one so the
                // apartment is deterministic and we never leak the MCP threadpool thread's state.
                return await Task.Run(() => RunSta(() =>
                    Execute(workbook_path, sheet, rows, header_row, backup, dry_run)));
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        // ---------------------------------------------------------------------
        // Core
        // ---------------------------------------------------------------------

        private static string Execute(string workbookPath, string sheet, string rowsJson,
            int headerRow, bool backup, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(workbookPath))
                throw new InvalidOperationException("workbook_path is required.");
            string original = Path.GetFullPath(workbookPath);
            if (!File.Exists(original))
                throw new InvalidOperationException($"Workbook not found on disk: {original}");
            if (string.IsNullOrWhiteSpace(sheet))
                throw new InvalidOperationException("sheet is required.");
            if (headerRow < 1)
                throw new InvalidOperationException($"header_row must be >= 1 (got {headerRow}).");

            var specs = ParseRows(rowsJson);
            var provBefore = Provenance(original);

            string ext = Path.GetExtension(original);
            if (string.IsNullOrEmpty(ext)) ext = ".xlsx";
            string scratch = Path.Combine(Path.GetTempPath(),
                $"hz_excel_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
            File.Copy(original, scratch, true);

            string backupPath = null;
            try
            {
                if (dryRun)
                {
                    Plan plan = null;
                    WithWorkbook(scratch, /*readOnly*/ true, wb =>
                    {
                        dynamic ws = GetWorksheet(wb, sheet);
                        try { plan = ResolvePlan(ws, headerRow, specs); }
                        finally { Release(ws); }
                        return 0;
                    });
                    return DryReport(original, sheet, headerRow, specs, plan, provBefore).ToString(Formatting.Indented);
                }

                // Real write. Guards have already passed inside ResolvePlan below before any
                // byte is written; the backup is taken now so the copy-back / restore path is
                // always covered.
                if (backup)
                {
                    backupPath = BackupPath(original);
                    File.Copy(original, backupPath, true);
                }

                // --- WRITE PASS (read-write on the scratch copy) ---
                Plan wplan = null;
                WithWorkbook(scratch, /*readOnly*/ false, wb =>
                {
                    dynamic ws = GetWorksheet(wb, sheet);
                    try
                    {
                        wplan = ResolvePlan(ws, headerRow, specs);
                        foreach (var pc in wplan.Cells)
                        {
                            // Re-assert the ROW GUARD at the moment of writing.
                            string keyNow = NormalizeStr(GetCellValue(ws, pc.Row, pc.KeyColumnIndex));
                            if (keyNow != NormalizeStr(pc.KeyValue))
                                throw new InvalidOperationException(
                                    $"ROW GUARD: row {pc.Row} key changed (expected '{pc.KeyValue}', found '{keyNow}') — refusing to write.");
                            SetCellValue(ws, pc.Row, pc.Column, pc.ExpectedValue);
                        }
                        wb.Save();
                    }
                    finally { Release(ws); }
                    return 0;
                });

                // --- VERIFY PASS (reopen read-only, re-read from disk) ---
                Plan vplan = null;
                var readbacks = new List<KeyValuePair<PlannedCell, string>>();
                WithWorkbook(scratch, /*readOnly*/ true, wb =>
                {
                    dynamic ws = GetWorksheet(wb, sheet);
                    try
                    {
                        vplan = ResolvePlan(ws, headerRow, specs);
                        foreach (var pc in wplan.Cells)
                        {
                            string got = NormalizeStr(GetCellValue(ws, pc.Row, pc.Column));
                            readbacks.Add(new KeyValuePair<PlannedCell, string>(pc, got));
                        }
                    }
                    finally { Release(ws); }
                    return 0;
                });

                var (cellsWritten, mismatches) = BuildReadbackReport(readbacks);

                int rcBefore = wplan.DataRowCount;
                int rcScratchAfter = vplan.DataRowCount;
                bool countCollapsed = rcScratchAfter < rcBefore;

                // TABLE-SURVIVAL GUARD. wplan.Tables is the set of ListObjects present in the
                // sheet BEFORE the save; vplan.Tables is what came back after the save+reopen.
                // The one destruction this tool exists to prevent is a dropped Table ("Tabla512"
                // and its structured references), which read-back only REPORTED before — it never
                // GATED on it. A table that was present before and is gone after is a hard failure,
                // not a green result.
                var tablesLost = TablesMissing(wplan.Tables, vplan.Tables);

                if (mismatches.Count > 0 || countCollapsed || tablesLost.Count > 0)
                {
                    // The original was never written (we only edited the scratch copy), so it is
                    // still pristine. Restore from backup anyway if we have one — defensive, and
                    // it makes "never leave a corrupt file" unconditional.
                    bool restored = false;
                    if (backupPath != null) { File.Copy(backupPath, original, true); restored = true; }
                    string reason;
                    if (tablesLost.Count > 0)
                        reason = $"Excel Table(s) [{string.Join(", ", tablesLost)}] present before the write are GONE after " +
                                 "save+reopen (structured references destroyed) — refusing to keep this write";
                    else if (countCollapsed)
                        reason = $"row_count collapsed {rcBefore} -> {rcScratchAfter} after save (possible truncation) — refusing to keep this write";
                    else
                        reason = $"{mismatches.Count} cell(s) read back different from what was written";
                    return new JObject
                    {
                        ["status"] = "VERIFY_FAILED_ORIGINAL_UNTOUCHED",
                        ["saved_ok"] = false,
                        ["dry_run"] = false,
                        ["reason"] = reason,
                        ["workbook_path"] = original,
                        ["scratch_path"] = scratch,
                        ["backup_path"] = backupPath,
                        ["restored_from_backup"] = restored,
                        ["sheet"] = sheet,
                        ["header_row"] = headerRow,
                        ["row_count_before"] = rcBefore,
                        ["row_count_after"] = rcScratchAfter,
                        ["tables_present_before"] = JArray.FromObject(wplan.Tables),
                        ["tables_present"] = JArray.FromObject(vplan.Tables),
                        ["tables_lost"] = JArray.FromObject(tablesLost),
                        ["mismatches"] = mismatches,
                        ["provenance_original"] = Provenance(original)
                    }.ToString(Formatting.Indented);
                }

                // Verify passed on the scratch. Copy back over the original, then hash-compare
                // the two files: this is the exact boundary where a OneDrive sync truncated a
                // file after a "successful" save. If the bytes on the original do not match the
                // scratch we just verified, the write-back was corrupted — restore and fail.
                File.Copy(scratch, original, true);
                string shaScratch = Sha256(scratch);
                string shaOriginal = Sha256(original);
                if (shaScratch != shaOriginal)
                {
                    bool restored = false;
                    if (backupPath != null) { File.Copy(backupPath, original, true); restored = true; }
                    return new JObject
                    {
                        ["status"] = "COPY_BACK_CORRUPT_RESTORED",
                        ["saved_ok"] = false,
                        ["dry_run"] = false,
                        ["reason"] = "copy-back hash mismatch: the file written to the original path does not match the " +
                                     "verified scratch (truncated/altered during write-back). Restored from backup.",
                        ["workbook_path"] = original,
                        ["scratch_path"] = scratch,
                        ["backup_path"] = backupPath,
                        ["restored_from_backup"] = restored,
                        ["sha256_scratch"] = shaScratch,
                        ["sha256_original_after_copy"] = shaOriginal
                    }.ToString(Formatting.Indented);
                }

                // DELIVERED-STATE VERIFY. Everything above (row_count_after, every value_read_back,
                // tables_present) was measured on the %TEMP% scratch — a DIFFERENT file that never
                // lived on OneDrive. Re-open the ORIGINAL we just wrote and re-derive the delivered
                // row count, table set and cell values from IT, then gate on those. This is what
                // lets us claim WRITTEN_VERIFIED about the file the user actually gets rather than
                // about a temp copy. (A post-RETURN async OneDrive truncation is inherently
                // uncatchable in-process; row_count is therefore NOT advertised as a guard against
                // that — it is a delivered-state assertion at the moment of return.)
                Plan oplan = null;
                var oReadbacks = new List<KeyValuePair<PlannedCell, string>>();
                try
                {
                    WithWorkbook(original, /*readOnly*/ true, wb =>
                    {
                        dynamic ws = GetWorksheet(wb, sheet);
                        try
                        {
                            oplan = ResolvePlan(ws, headerRow, specs);
                            foreach (var pc in wplan.Cells)
                            {
                                string got = NormalizeStr(GetCellValue(ws, pc.Row, pc.Column));
                                oReadbacks.Add(new KeyValuePair<PlannedCell, string>(pc, got));
                            }
                        }
                        finally { Release(ws); }
                        return 0;
                    });
                }
                catch (Exception ex)
                {
                    bool restored = false;
                    if (backupPath != null) { File.Copy(backupPath, original, true); restored = true; }
                    return new JObject
                    {
                        ["status"] = "DELIVERED_VERIFY_FAILED_RESTORED",
                        ["saved_ok"] = false,
                        ["dry_run"] = false,
                        ["reason"] = $"could not re-open and re-read the delivered original to confirm the write: {ex.Message}. Restored from backup.",
                        ["workbook_path"] = original,
                        ["scratch_path"] = scratch,
                        ["backup_path"] = backupPath,
                        ["restored_from_backup"] = restored
                    }.ToString(Formatting.Indented);
                }

                var (deliveredCells, deliveredMismatches) = BuildReadbackReport(oReadbacks);
                int rcAfter = oplan.DataRowCount;
                bool deliveredCountCollapsed = rcAfter < rcBefore;
                var deliveredTablesLost = TablesMissing(wplan.Tables, oplan.Tables);

                if (deliveredMismatches.Count > 0 || deliveredCountCollapsed || deliveredTablesLost.Count > 0)
                {
                    bool restored = false;
                    if (backupPath != null) { File.Copy(backupPath, original, true); restored = true; }
                    string reason;
                    if (deliveredTablesLost.Count > 0)
                        reason = $"delivered original is missing Excel Table(s) [{string.Join(", ", deliveredTablesLost)}] — refusing to report success";
                    else if (deliveredCountCollapsed)
                        reason = $"delivered original row_count collapsed {rcBefore} -> {rcAfter} — refusing to report success";
                    else
                        reason = $"{deliveredMismatches.Count} cell(s) in the delivered original differ from what was written";
                    return new JObject
                    {
                        ["status"] = "DELIVERED_VERIFY_FAILED_RESTORED",
                        ["saved_ok"] = false,
                        ["dry_run"] = false,
                        ["reason"] = reason,
                        ["workbook_path"] = original,
                        ["scratch_path"] = scratch,
                        ["backup_path"] = backupPath,
                        ["restored_from_backup"] = restored,
                        ["sheet"] = sheet,
                        ["header_row"] = headerRow,
                        ["row_count_before"] = rcBefore,
                        ["row_count_after"] = rcAfter,
                        ["tables_present"] = JArray.FromObject(oplan.Tables),
                        ["tables_lost"] = JArray.FromObject(deliveredTablesLost),
                        ["mismatches"] = deliveredMismatches,
                        ["provenance_original"] = Provenance(original)
                    }.ToString(Formatting.Indented);
                }

                var provAfter = Provenance(original);
                return new JObject
                {
                    ["status"] = "WRITTEN_VERIFIED",
                    ["saved_ok"] = true,
                    ["dry_run"] = false,
                    ["engine"] = "Microsoft Excel (COM, late-bound)",
                    ["workbook_path"] = original,
                    ["scratch_path"] = scratch,
                    ["backup_path"] = backupPath,
                    ["sheet"] = sheet,
                    ["header_row"] = headerRow,
                    ["rows_parsed"] = specs.Count,
                    // Delivered-state (re-read from the ORIGINAL after copy-back), not the scratch.
                    ["tables_present"] = JArray.FromObject(oplan.Tables),
                    ["row_count_before"] = rcBefore,
                    ["row_count_after"] = rcAfter,
                    ["cells_written"] = deliveredCells,
                    // Scratch-verification kept separately so the two files are never conflated.
                    ["scratch_verify_row_count_after"] = rcScratchAfter,
                    ["scratch_verify_tables_present"] = JArray.FromObject(vplan.Tables),
                    ["provenance_before"] = provBefore,
                    ["provenance_after"] = provAfter
                }.ToString(Formatting.Indented);
            }
            finally
            {
                try { if (File.Exists(scratch)) File.Delete(scratch); } catch { }
            }
        }

        // Build the cells_written / mismatches pair from a set of (plannedCell -> read-back)
        // observations. Used for BOTH the scratch verify and the delivered-original verify so the
        // exact same comparison logic guards every read-back site.
        private static (JArray cellsWritten, JArray mismatches) BuildReadbackReport(
            List<KeyValuePair<PlannedCell, string>> readbacks)
        {
            var cellsWritten = new JArray();
            var mismatches = new JArray();
            foreach (var rb in readbacks)
            {
                string exp = NormalizeStr(rb.Key.ExpectedValue);
                bool ok = exp == rb.Value;
                var cw = new JObject
                {
                    ["row"] = rb.Key.Row,
                    ["column_name"] = rb.Key.ColumnName,
                    ["value_written"] = exp,
                    ["value_read_back"] = rb.Value,
                    ["ok"] = ok
                };
                cellsWritten.Add(cw);
                if (!ok) mismatches.Add(cw);
            }
            return (cellsWritten, mismatches);
        }

        // Names of Tables (ListObjects) that were present in `before` but are absent from `after`.
        // A dropped Table means its structured references are gone — a corruption, not a warning.
        // Comparison is ordinal (Excel Table names are case-sensitively unique per workbook).
        private static List<string> TablesMissing(List<string> before, List<string> after)
        {
            var afterSet = new HashSet<string>(after ?? new List<string>(), StringComparer.Ordinal);
            var lost = new List<string>();
            foreach (var t in before ?? new List<string>())
                if (!afterSet.Contains(t)) lost.Add(t);
            return lost;
        }

        private static JObject DryReport(string original, string sheet, int headerRow,
            List<RowSpec> specs, Plan plan, JObject provBefore)
        {
            var would = new JArray();
            foreach (var pc in plan.Cells)
            {
                would.Add(new JObject
                {
                    ["row"] = pc.Row,
                    ["column_name"] = pc.ColumnName,
                    ["key_column"] = pc.KeyColumn,
                    ["key_value"] = pc.KeyValue,
                    ["value"] = NormalizeStr(pc.ExpectedValue)
                });
            }
            return new JObject
            {
                ["status"] = "DRY_RUN",
                ["saved_ok"] = false,
                ["dry_run"] = true,
                ["engine"] = "Microsoft Excel (COM, late-bound)",
                ["workbook_path"] = original,
                ["sheet"] = sheet,
                ["header_row"] = headerRow,
                ["rows_parsed"] = specs.Count,
                ["tables_present"] = JArray.FromObject(plan.Tables),
                ["row_count"] = plan.DataRowCount,
                ["would_write"] = would,
                ["provenance"] = provBefore,
                ["note"] = "Read-only rehearsal on a staged copy. No backup taken, the original was never opened for write."
            };
        }

        // ---------------------------------------------------------------------
        // Excel driving (COM late-binding)
        // ---------------------------------------------------------------------

        // Opens the workbook, runs body, and ALWAYS closes the book + quits the app +
        // releases every COM object in finally — otherwise every call leaks an invisible
        // EXCEL.EXE. body's return value is ignored by callers (they use closures).
        private static T WithWorkbook<T>(string path, bool readOnly, Func<dynamic, T> body)
        {
            // Retry the WHOLE create-app-and-open, not just the Open. Excel automation is
            // effectively single-instance: after one call's app.Quit(), the EXCEL.EXE lingers
            // a beat while it dies, and the NEXT call's CreateInstance can attach to that dying
            // instance — whose Workbooks.Open then fails ("file not found", a rejected call,
            // RPC_E_CALL_REJECTED). Observed live: a real write immediately after a dry-run on
            // the same file. Re-opening on the same bad app cannot help; only tearing it down,
            // waiting, and creating a FRESH app does. The first attempt has no delay, so the
            // common case (no prior Excel) pays nothing.
            const int maxAttempts = 4;
            Exception last = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (attempt > 1) System.Threading.Thread.Sleep(1200 * (attempt - 1));
                try { return OpenAndRun(path, readOnly, body); }
                catch (Exception ex)
                {
                    // Never retry past a body() that already touched the workbook — that is
                    // this call's own logic failing, not Excel being flaky, and retrying could
                    // double-apply a write. Only transient OPEN failures are retried.
                    if (ex is HorizunBodyException) throw ((HorizunBodyException)ex).Inner;
                    last = ex;
                }
            }
            throw new InvalidOperationException(
                $"Excel could not open '{path}' after {maxAttempts} attempts: {last?.Message}. " +
                "The original workbook was not modified.", last);
        }

        // Marks an exception as coming from body() (this call's real logic) rather than from
        // opening Excel, so the retry loop above knows not to re-run it.
        private sealed class HorizunBodyException : Exception
        {
            public Exception Inner { get; }
            public HorizunBodyException(Exception inner) { Inner = inner; }
        }

        private static T OpenAndRun<T>(string path, bool readOnly, Func<dynamic, T> body)
        {
            dynamic app = null, workbooks = null, wb = null;
            try
            {
                Type t = Type.GetTypeFromProgID("Excel.Application");
                if (t == null)
                    throw new InvalidOperationException(
                        "Excel COM (ProgID 'Excel.Application') is not registered on this machine — cannot preserve Tables without real Excel.");
                app = Activator.CreateInstance(t);
                try { app.Visible = false; } catch { }
                try { app.DisplayAlerts = false; } catch { }
                try { app.ScreenUpdating = false; } catch { }
                try { app.AskToUpdateLinks = false; } catch { }
                try { app.AutomationSecurity = 3; /* msoAutomationSecurityForceDisable */ } catch { }

                workbooks = app.Workbooks;
                // (Filename, UpdateLinks=0 don't-update, ReadOnly)
                wb = workbooks.Open(path, 0, readOnly);

                // Anything the body throws is OUR logic (guards, verify), not a flaky open —
                // wrap it so the retry loop re-throws instead of re-running the write.
                try { return body(wb); }
                catch (Exception bodyEx) { throw new HorizunBodyException(bodyEx); }
            }
            finally
            {
                try { if (wb != null) wb.Close(false); } catch { }
                Release(wb);
                Release(workbooks);
                try { if (app != null) app.Quit(); } catch { }
                Release(app);
                // COM RCWs are freed on GC; force it so the EXCEL.EXE actually exits now.
                GC.Collect(); GC.WaitForPendingFinalizers();
                GC.Collect(); GC.WaitForPendingFinalizers();
            }
        }

        private static dynamic GetWorksheet(dynamic wb, string sheet)
        {
            dynamic sheets = wb.Worksheets;
            try
            {
                int count = (int)sheets.Count;
                dynamic exact = null, ci = null;
                var names = new List<string>();
                for (int i = 1; i <= count; i++)
                {
                    dynamic s = sheets[i];
                    string nm = (string)s.Name;
                    names.Add(nm);
                    if (exact == null && string.Equals(nm, sheet, StringComparison.Ordinal)) { exact = s; continue; }
                    if (ci == null && string.Equals(nm, sheet, StringComparison.OrdinalIgnoreCase)) { ci = s; continue; }
                    Release(s);
                }
                dynamic ws = exact ?? ci;
                if (exact != null && ci != null) Release(ci); // exact wins; ci was a different sheet
                if (ws == null)
                    throw new InvalidOperationException(
                        $"Sheet '{sheet}' not found. Available sheets: [{string.Join(", ", names)}]");
                return ws;
            }
            finally { Release(sheets); }
        }

        private static Plan ResolvePlan(dynamic ws, int headerRow, List<RowSpec> specs)
        {
            int firstRow, firstCol, lastRow, lastCol;
            dynamic used = ws.UsedRange;
            try
            {
                firstRow = (int)used.Row;
                firstCol = (int)used.Column;
                dynamic urRows = used.Rows;
                dynamic urCols = used.Columns;
                int nRows = (int)urRows.Count;
                int nCols = (int)urCols.Count;
                Release(urRows); Release(urCols);
                lastRow = firstRow + nRows - 1;
                lastCol = firstCol + nCols - 1;
            }
            finally { Release(used); }

            if (headerRow > lastRow)
                throw new InvalidOperationException(
                    $"header_row {headerRow} is past the last used row {lastRow} — the sheet looks empty or header_row is wrong.");

            // Column resolution BY NAME. Duplicate header names are tracked; a requested
            // column that is duplicated is refused rather than guessed.
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var dupes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var headers = new List<string>();
            for (int c = firstCol; c <= lastCol; c++)
            {
                string nm = NormalizeStr(GetCellValue(ws, headerRow, c));
                if (nm.Length == 0) continue;
                headers.Add(nm);
                if (map.ContainsKey(nm)) dupes.Add(nm);
                else map[nm] = c;
            }
            if (map.Count == 0)
                throw new InvalidOperationException(
                    $"No header names found in row {headerRow} (columns {firstCol}..{lastCol} were all blank) — wrong header_row?");

            var tables = new List<string>();
            dynamic los = ws.ListObjects;
            try
            {
                int ln = (int)los.Count;
                for (int i = 1; i <= ln; i++)
                {
                    dynamic lo = los[i];
                    try { tables.Add((string)lo.Name); }
                    finally { Release(lo); }
                }
            }
            finally { Release(los); }

            int dataRowCount = Math.Max(0, lastRow - headerRow);

            var planned = new List<PlannedCell>();
            foreach (var rs in specs)
            {
                if (dupes.Contains(rs.KeyColumn))
                    throw new InvalidOperationException(
                        $"key_column '{rs.KeyColumn}' is ambiguous — it appears more than once in header row {headerRow}.");
                if (!map.TryGetValue(rs.KeyColumn, out int keyCol))
                    throw new InvalidOperationException(
                        $"key_column '{rs.KeyColumn}' not found in header row {headerRow}. Headers: [{string.Join(", ", headers)}]");

                var matches = new List<int>();
                string wantKey = NormalizeStr(rs.KeyValue);
                for (int r = headerRow + 1; r <= lastRow; r++)
                {
                    string cell = NormalizeStr(GetCellValue(ws, r, keyCol));
                    if (cell == wantKey) matches.Add(r);
                }
                if (matches.Count == 0)
                    throw new InvalidOperationException(
                        $"ROW GUARD: key_value '{rs.KeyValue}' not found in column '{rs.KeyColumn}' (scanned rows {headerRow + 1}..{lastRow}) — refusing to write into a row that does not already carry this key.");
                if (matches.Count > 1)
                    throw new InvalidOperationException(
                        $"ROW GUARD: key_value '{rs.KeyValue}' matches {matches.Count} rows ({string.Join(", ", matches)}) in column '{rs.KeyColumn}' — ambiguous, refusing.");

                int row = matches[0];
                foreach (var kv in rs.Cells)
                {
                    string colName = kv.Key;
                    if (dupes.Contains(colName))
                        throw new InvalidOperationException(
                            $"column '{colName}' is ambiguous — it appears more than once in header row {headerRow}.");
                    if (!map.TryGetValue(colName, out int col))
                        throw new InvalidOperationException(
                            $"column '{colName}' not found in header row {headerRow}. Headers: [{string.Join(", ", headers)}]");
                    planned.Add(new PlannedCell
                    {
                        Row = row,
                        ColumnName = colName,
                        Column = col,
                        ExpectedValue = JTokenToValue(kv.Value),
                        KeyColumn = rs.KeyColumn,
                        KeyColumnIndex = keyCol,
                        KeyValue = rs.KeyValue
                    });
                }
            }
            if (planned.Count == 0)
                throw new InvalidOperationException(
                    "Resolved 0 cells to write — refusing (an empty write is never reported as success).");

            return new Plan
            {
                Cells = planned,
                Tables = tables,
                DataRowCount = dataRowCount,
                UsedLastRow = lastRow,
                Headers = headers
            };
        }

        private static object GetCellValue(dynamic ws, int r, int c)
        {
            dynamic cell = ws.Cells[r, c];
            try { return cell.Value2; }
            finally { Release(cell); }
        }

        private static void SetCellValue(dynamic ws, int r, int c, object v)
        {
            dynamic cell = ws.Cells[r, c];
            try { cell.Value2 = v; }
            finally { Release(cell); }
        }

        // ---------------------------------------------------------------------
        // Plain-CLR helpers
        // ---------------------------------------------------------------------

        private static List<RowSpec> ParseRows(string rowsJson)
        {
            if (string.IsNullOrWhiteSpace(rowsJson))
                throw new InvalidOperationException("rows is required (JSON array of {key_column, key_value, cells}).");
            JArray arr;
            try { arr = JArray.Parse(rowsJson); }
            catch (Exception ex) { throw new InvalidOperationException($"rows is not a valid JSON array: {ex.Message}"); }
            if (arr.Count == 0)
                throw new InvalidOperationException(
                    "rows array is empty — nothing to write (refusing rather than reporting a no-op success).");

            var specs = new List<RowSpec>();
            for (int i = 0; i < arr.Count; i++)
            {
                if (!(arr[i] is JObject o))
                    throw new InvalidOperationException($"rows[{i}] is not an object.");
                string keyColumn = o.Value<string>("key_column");
                if (string.IsNullOrWhiteSpace(keyColumn))
                    throw new InvalidOperationException($"rows[{i}] is missing key_column.");
                if (o["key_value"] == null)
                    throw new InvalidOperationException($"rows[{i}] (key_column '{keyColumn}') is missing key_value.");
                string keyValue = o["key_value"].Type == JTokenType.Null ? "" : o["key_value"].ToString();
                if (!(o["cells"] is JObject cells) || cells.Count == 0)
                    throw new InvalidOperationException(
                        $"rows[{i}] (key_value '{keyValue}') has no cells — nothing to write for this row.");
                specs.Add(new RowSpec { KeyColumn = keyColumn, KeyValue = keyValue, Cells = cells });
            }
            return specs;
        }

        // Coerce a JSON value into the CLR type Excel's Value2 should receive. Numbers go
        // in as doubles (so the read-back normalizes identically), booleans as bool,
        // null/empty as "".
        private static object JTokenToValue(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Integer: return (double)(long)t;
                case JTokenType.Float: return (double)t;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Null:
                case JTokenType.Undefined: return "";
                case JTokenType.Date: return ((DateTime)t).ToString("o", CultureInfo.InvariantCulture);
                case JTokenType.String: return (string)t;
                default: return t.ToString();
            }
        }

        // One canonical string form for both what-we-wrote and what-we-read-back, so the
        // comparison is exact and culture-independent. Excel returns numbers as double via
        // Value2, so 512 (written) and 512.0 (read) both normalize to "512".
        private static string NormalizeStr(object v)
        {
            if (v == null) return "";
            if (v is bool b) return b ? "TRUE" : "FALSE";
            if (v is double d) return d.ToString("0.############", CultureInfo.InvariantCulture);
            if (v is float f) return ((double)f).ToString("0.############", CultureInfo.InvariantCulture);
            if (v is DateTime dt) return dt.ToString("o", CultureInfo.InvariantCulture);
            if (v is int || v is long || v is short || v is decimal)
                return Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("0.############", CultureInfo.InvariantCulture);
            return v.ToString().Trim();
        }

        private static JObject Provenance(string path)
        {
            var fi = new FileInfo(path);
            return new JObject
            {
                ["path"] = fi.FullName,
                ["exists"] = fi.Exists,
                ["size_bytes"] = fi.Exists ? fi.Length : 0L,
                ["last_write_utc"] = fi.Exists ? fi.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture) : null,
                ["sha256"] = fi.Exists ? Sha256(path) : null
            };
        }

        private static string BackupPath(string original)
        {
            string dir = Path.GetDirectoryName(original) ?? ".";
            string name = Path.GetFileNameWithoutExtension(original);
            string ext = Path.GetExtension(original);
            return Path.Combine(dir, $"{name}.{DateTime.Now:yyyyMMdd_HHmmss}.hzbak{ext}");
        }

        private static string Sha256(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            byte[] hash = sha.ComputeHash(fs);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte x in hash) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
            catch { }
        }

        // Run func on a dedicated STA thread and marshal any exception back to the caller.
        private static T RunSta<T>(Func<T> func)
        {
            T result = default;
            Exception captured = null;
            var t = new Thread(() =>
            {
                try { result = func(); }
                catch (Exception ex) { captured = ex; }
            })
            { IsBackground = true, Name = "HorizunExcel.STA" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (captured != null) ExceptionDispatchInfo.Capture(captured).Throw();
            return result;
        }

        // ---------------------------------------------------------------------
        // POCOs
        // ---------------------------------------------------------------------

        private sealed class RowSpec
        {
            public string KeyColumn;
            public string KeyValue;
            public JObject Cells;
        }

        private sealed class PlannedCell
        {
            public int Row;
            public string ColumnName;
            public int Column;
            public object ExpectedValue;
            public string KeyColumn;
            public int KeyColumnIndex;
            public string KeyValue;
        }

        private sealed class Plan
        {
            public List<PlannedCell> Cells;
            public List<string> Tables;
            public int DataRowCount;
            public int UsedLastRow;
            public List<string> Headers;
        }
    }
}
