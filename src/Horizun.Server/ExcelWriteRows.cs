// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// A HOST-RESIDENT tool: it answers inside this process and never touches Revit.
// It appends rows to a worksheet in an existing .xlsx, preserving the rest of the
// workbook (every other sheet, all styles, tables and formatting), and does it
// under the honesty contract:
//
//   * BACKUP first. The original is copied aside before a single byte is rewritten,
//     so a failure can never leave the caller with less than they started with.
//   * REFUSE what is not an .xlsx. A file that is not a valid OPC package (a zip
//     carrying xl/workbook.xml) is rejected — never "written" into corruption.
//   * VERIFY by RE-READING. After the new workbook is produced it is re-opened and
//     the appended cells are read back and compared to what was asked; the original
//     is only replaced once that check passes. rows_written is what the file holds
//     on re-read, not the count of rows we handed to the writer.
//
// Dependency-light on purpose: System.IO.Compression + System.Xml.Linq, no Excel,
// no COM, no third-party package. That is also what makes it unit-testable without
// Excel installed — the pure XML transform (AppendRowsToSheetXml) takes a string and
// returns a string, and the sheet resolution reads the same bytes a caller would.
//
// Scope of v1 (stated, not hidden): it APPENDS rows after the last used row and
// writes text as inline strings and numbers as numbers. It does NOT expand an Excel
// Table's range, so rows appended below a table are not absorbed into it — the
// response reports whether the target sheet carries a table so the caller knows.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ExcelWriteRows
    {
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace Rel  = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace Pkg  = "http://schemas.openxmlformats.org/package/2006/relationships";

        // ---------------------------------------------------------------------
        // Pure helpers — no file I/O, unit-testable.
        // ---------------------------------------------------------------------

        /// <summary>Zero-based column index to its spreadsheet letter: 0->A, 25->Z, 26->AA.</summary>
        internal static string ColumnLetter(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            var sb = new StringBuilder();
            index += 1;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }

        /// <summary>The highest existing row number in a sheetData element, or 0 if there are none.</summary>
        private static int MaxRowNumber(XElement sheetData)
        {
            int max = 0;
            foreach (XElement row in sheetData.Elements(Main + "row"))
            {
                var r = row.Attribute("r");
                int n;
                if (r != null && int.TryParse(r.Value, out n)) { if (n > max) max = n; }
                else max++; // a row without an explicit index still occupies the next slot
            }
            return max;
        }

        /// <summary>Build one &lt;c&gt; cell element for a value at a given row/column, typed honestly.</summary>
        private static XElement BuildCell(int rowNumber, int colIndex, object value)
        {
            string reference = ColumnLetter(colIndex) + rowNumber;
            var c = new XElement(Main + "c", new XAttribute("r", reference));

            if (value == null) return c; // empty cell — a blank is a blank, not a zero

            // JSON numbers arrive as long/double via Newtonsoft; booleans as bool.
            if (value is bool b)
            {
                c.Add(new XAttribute("t", "b"));
                c.Add(new XElement(Main + "v", b ? "1" : "0"));
                return c;
            }
            if (value is long || value is int || value is double || value is float || value is decimal)
            {
                c.Add(new XElement(Main + "v", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
                return c;
            }

            // Everything else is text, written as an inline string so sharedStrings.xml
            // is never touched. xml:space=preserve keeps leading/trailing spaces.
            string s = value.ToString();
            c.Add(new XAttribute("t", "inlineStr"));
            c.Add(new XElement(Main + "is",
                new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), s)));
            return c;
        }

        /// <summary>
        /// THE PURE TRANSFORM. Given a worksheet XML string and rows of cell values, append
        /// the rows after the last used row and return the new XML. Row numbers continue from
        /// the existing maximum; columns are filled left-to-right from A. Returns the number of
        /// rows appended and the first/last new row numbers via <paramref name="report"/>.
        /// </summary>
        internal static string AppendRowsToSheetXml(string sheetXml, IList<IList<object>> rows, out AppendReport report)
        {
            XDocument doc = XDocument.Parse(sheetXml, LoadOptions.PreserveWhitespace);
            XElement root = doc.Root;
            if (root == null || root.Name != Main + "worksheet")
                throw new InvalidDataException("Not a worksheet part: root element is not <worksheet> in the spreadsheetml namespace.");

            XElement sheetData = root.Element(Main + "sheetData");
            if (sheetData == null)
            {
                sheetData = new XElement(Main + "sheetData");
                root.Add(sheetData);
            }

            int firstNew = MaxRowNumber(sheetData) + 1;
            int rowNumber = firstNew;
            int appended = 0;
            int widestColumn = 0;
            foreach (IList<object> row in rows)
            {
                var rowEl = new XElement(Main + "row", new XAttribute("r", rowNumber));
                for (int col = 0; col < row.Count; col++)
                    rowEl.Add(BuildCell(rowNumber, col, row[col]));
                if (row.Count > widestColumn) widestColumn = row.Count;
                sheetData.Add(rowEl);
                rowNumber++;
                appended++;
            }

            // The <dimension> element must grow with the data. A reader is entitled to
            // trust it: openpyxl in read_only mode (which is what pandas.read_excel uses)
            // sizes the sheet from this ref and never sees rows beyond it. Leaving it stale
            // means the rows are in the file yet invisible to the very tools this exists to
            // feed — written, verified, and gone. Re-reading with our own parser would not
            // catch that, because our parser ignores dimension.
            if (appended > 0)
                ExpandDimension(root, rowNumber - 1, widestColumn);

            report = new AppendReport
            {
                RowsAppended = appended,
                FirstNewRow = appended > 0 ? firstNew : 0,
                LastNewRow = appended > 0 ? rowNumber - 1 : 0
            };
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        internal struct AppendReport
        {
            public int RowsAppended;
            public int FirstNewRow;
            public int LastNewRow;
        }

        /// <summary>Spreadsheet column letters to a zero-based index: A-&gt;0, Z-&gt;25, AA-&gt;26.</summary>
        internal static int ColumnIndex(string letters)
        {
            int n = 0;
            foreach (char ch in letters)
            {
                char up = char.ToUpperInvariant(ch);
                if (up < 'A' || up > 'Z') break;
                n = n * 26 + (up - 'A' + 1);
            }
            return n - 1;
        }

        /// <summary>
        /// Grow the worksheet's &lt;dimension ref&gt; so it covers the appended rows. Only ever
        /// grows, never shrinks: the existing ref may legitimately cover columns no appended
        /// row uses. Absent dimension is left absent — a reader then measures the rows
        /// themselves, which is already correct.
        /// </summary>
        internal static void ExpandDimension(XElement worksheet, int lastRow, int columnCount)
        {
            XElement dim = worksheet.Element(Main + "dimension");
            if (dim == null) return;

            string reference = (string)dim.Attribute("ref");
            if (string.IsNullOrEmpty(reference)) return;

            string end = reference.Contains(":") ? reference.Substring(reference.IndexOf(':') + 1) : reference;
            string start = reference.Contains(":") ? reference.Substring(0, reference.IndexOf(':')) : reference;

            string endLetters = new string(end.TakeWhile(char.IsLetter).ToArray());
            string endDigits = new string(end.SkipWhile(char.IsLetter).ToArray());

            int endRow;
            if (!int.TryParse(endDigits, out endRow)) return;   // unparseable: leave it alone rather than write a wrong one
            int endCol = endLetters.Length > 0 ? ColumnIndex(endLetters) : 0;

            int newEndRow = Math.Max(endRow, lastRow);
            int newEndCol = Math.Max(endCol, columnCount - 1);

            dim.SetAttributeValue("ref", start + ":" + ColumnLetter(newEndCol) + newEndRow);
        }

        /// <summary>Lowercase hex SHA-256 — the provenance stamp of the produced file.</summary>
        internal static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder(64);
                foreach (byte x in sha.ComputeHash(bytes)) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        // ---------------------------------------------------------------------
        // Package reading — resolve which worksheet part a sheet name maps to.
        // ---------------------------------------------------------------------

        /// <summary>Read every zip entry into an ordered name->bytes map (order preserved for a faithful rewrite).</summary>
        internal static List<KeyValuePair<string, byte[]>> ReadEntries(byte[] xlsxBytes)
        {
            var entries = new List<KeyValuePair<string, byte[]>>();
            using (var ms = new MemoryStream(xlsxBytes, false))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    using (var s = e.Open())
                    using (var buf = new MemoryStream())
                    {
                        s.CopyTo(buf);
                        entries.Add(new KeyValuePair<string, byte[]>(e.FullName, buf.ToArray()));
                    }
                }
            }
            return entries;
        }

        private static string Utf8(byte[] b) => new UTF8Encoding(false).GetString(StripBom(b));
        private static byte[] StripBom(byte[] b)
            => (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) ? b.Skip(3).ToArray() : b;

        /// <summary>
        /// Resolve the worksheet part path for a sheet by name (case-insensitive). When name is
        /// null/empty, return the FIRST sheet in workbook order. Returns null if it cannot be resolved.
        /// </summary>
        internal static ResolvedSheet ResolveSheet(List<KeyValuePair<string, byte[]>> entries, string requestedName)
        {
            byte[] wbBytes = entries.FirstOrDefault(kv => kv.Key == "xl/workbook.xml").Value;
            byte[] relBytes = entries.FirstOrDefault(kv => kv.Key == "xl/_rels/workbook.xml.rels").Value;
            if (wbBytes == null || relBytes == null) return null;

            XDocument wb = XDocument.Parse(Utf8(wbBytes));
            XDocument rels = XDocument.Parse(Utf8(relBytes));

            var sheetEls = wb.Root.Element(Main + "sheets")?.Elements(Main + "sheet").ToList();
            if (sheetEls == null || sheetEls.Count == 0) return null;

            XElement chosen = null;
            if (string.IsNullOrEmpty(requestedName))
                chosen = sheetEls[0];
            else
                chosen = sheetEls.FirstOrDefault(s =>
                    string.Equals((string)s.Attribute("name"), requestedName, StringComparison.OrdinalIgnoreCase));
            if (chosen == null) return null;

            string rid = (string)chosen.Attribute(Rel + "id");
            XElement relEl = rels.Root.Elements(Pkg + "Relationship")
                .FirstOrDefault(r => (string)r.Attribute("Id") == rid);
            if (relEl == null) return null;

            string target = (string)relEl.Attribute("Target"); // e.g. "worksheets/sheet1.xml"
            string path = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;

            return new ResolvedSheet
            {
                Name = (string)chosen.Attribute("name"),
                PartPath = path,
                SheetNames = sheetEls.Select(s => (string)s.Attribute("name")).ToList()
            };
        }

        internal sealed class ResolvedSheet
        {
            public string Name;
            public string PartPath;
            public List<string> SheetNames;
        }

        /// <summary>Does any Table part reference this worksheet? (Reported so the caller knows a table won't auto-expand.)</summary>
        private static bool SheetHasTable(List<KeyValuePair<string, byte[]>> entries, string sheetPartPath)
        {
            // A table is wired via xl/worksheets/_rels/sheetN.xml.rels -> ../tables/tableM.xml
            string relsPath = Path.GetDirectoryName(sheetPartPath).Replace('\\', '/') + "/_rels/" + Path.GetFileName(sheetPartPath) + ".rels";
            byte[] rb = entries.FirstOrDefault(kv => kv.Key == relsPath).Value;
            if (rb == null) return false;
            try
            {
                XDocument rels = XDocument.Parse(Utf8(rb));
                return rels.Root.Elements(Pkg + "Relationship")
                    .Any(r => ((string)r.Attribute("Type") ?? "").EndsWith("/table"));
            }
            catch (System.Xml.XmlException) { return false; }  // malformed rels: report "no table", never guess one
        }

        /// <summary>Re-serialize the entries to a new .xlsx byte array, substituting one part's bytes.</summary>
        private static byte[] RewriteZip(List<KeyValuePair<string, byte[]>> entries, string replacePath, byte[] replaceBytes)
        {
            using (var outMs = new MemoryStream())
            {
                using (var zip = new ZipArchive(outMs, ZipArchiveMode.Create, true))
                {
                    foreach (var kv in entries)
                    {
                        byte[] data = kv.Key == replacePath ? replaceBytes : kv.Value;
                        ZipArchiveEntry e = zip.CreateEntry(kv.Key, CompressionLevel.Optimal);
                        using (var s = e.Open()) s.Write(data, 0, data.Length);
                    }
                }
                return outMs.ToArray();
            }
        }

        // ---------------------------------------------------------------------
        // The host handler.
        // ---------------------------------------------------------------------

        /// <summary>The tool name, as the ledger records it.</summary>
        internal const string ToolName = "horizun_excel_write_rows";

        /// <summary>
        /// What the durable key is scoped to. The workbook, so the same key aimed at a
        /// different file is a conflict rather than a second append somewhere else.
        /// </summary>
        internal static string LedgerScopeOf(JObject args)
            => "xlsx:" + ((string)args?["file_path"] ?? "(none)");

        internal static JObject Handle(JObject args) =>
            Handle(args, new DurableCommandLedger(retentionLog: message => Log.Info(message)));

        internal static JObject Handle(JObject args, DurableCommandLedger ledger)
        {
            string filePath = (string)args?["file_path"];
            string sheetName = (string)args?["sheet"];
            JToken rowsTok = args?["rows"];

            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("file_path is required.");
            if (!(rowsTok is JArray rowsArr) || rowsArr.Count == 0)
                throw new ArgumentException("rows is required and must be a non-empty array of arrays.");

            // AT-MOST-ONCE, because an append is not idempotent and a lost reply is not
            // rare. Every typed Revit mutation gets this from the dispatcher; this tool is
            // answered in the server and so was never on that path. The failure it removes
            // needs no concurrency at all: one client, one timeout, one retry, two copies
            // of the rows - and the second answer honestly reporting rows_written: 1.
            string idempotencyKey = (string)args["idempotency_key"];
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                throw new ToolRefusal(
                    "idempotency_key is required. Appending rows cannot be undone by repeating it: if this " +
                    "reply is lost and you send the call again without a key, the rows land twice and the " +
                    "second answer reports rows_written honestly, because it did write them. Generate a new " +
                    "UUID for each deliberate append and keep it unchanged only for retries.");

            // Parse the caller's rows into CLR values (numbers/bools/strings/null).
            // This depends on the ARGUMENTS and never on the workbook, so it stays
            // outside the lock: a malformed request is rejected without making a
            // legitimate writer wait behind it.
            var rows = new List<IList<object>>();
            foreach (JToken rt in rowsArr)
            {
                if (!(rt is JArray cells))
                    throw new ArgumentException("Each entry of rows must itself be an array of cell values.");
                var one = new List<object>();
                foreach (JToken ct in cells) one.Add(JsonValueToClr(ct));
                rows.Add(one);
            }

            if (!File.Exists(filePath))
                throw new FileNotFoundException("Workbook not found: " + filePath);

            // ---- Exclusive, uniquely named, and verified against DISK. ----
            //
            // THE LOCK IS TAKEN BEFORE THE FILE IS READ, and held until the replace has
            // been verified. That ordering is the whole point, and it was wrong.
            //
            // Reading and rewriting the package used to happen BEFORE the lock. Two
            // writers therefore never met at the lock at all: both read the same bytes,
            // both computed the same first_new_row, and whichever finished its transform
            // second took a lock that was already free and wrote a package built from a
            // snapshot that no longer described the file. The first writer's rows were
            // gone -- and it had already answered rows_written and verified: true.
            // Measured on an 8000-row workbook: a one-row append finished at 244 ms, a
            // 60000-row append at 889 ms, both reporting success, and only the second
            // one's rows were in the file.
            //
            // The unique temp and backup names stay: two writers still cannot land on one
            // name if a lock file is ever deleted by hand while a write is running.
            string stamp = System.Diagnostics.Process.GetCurrentProcess().Id + "-" +
                           Guid.NewGuid().ToString("N").Substring(0, 8);
            string lockPath = filePath + ".horizunlock";
            string backupPath = filePath + "." + stamp + ".horizunbak";
            string tmp = filePath + "." + stamp + ".horizuntmp";

            FileStream lockHandle = AcquireWorkbookLock(lockPath);

            byte[] onDisk;
            byte[] original;
            ResolvedSheet sheet;
            AppendReport rep;
            bool hasTable;
            string replaceNote;
            DurableCommandDecision decision = null;
            bool writeStarted = false;
            try
            {
                // THE KEY IS CLAIMED AFTER THE LOCK, deliberately. A caller refused the
                // lock has been told "somebody else is writing, nothing was read, nothing
                // was written" - and a claim written before that refusal would leave the
                // key in doubt forever, so the one thing a caller should obviously do
                // (wait and retry with the same key) would be permanently refused.
                string fingerprint = RequestFingerprint.OfOperation(
                    ToolName, LedgerScopeOf(args), args, "idempotency_key");
                decision = ledger.Claim(idempotencyKey, ToolName, fingerprint);

                if (decision.Outcome == DurableCommandOutcome.Replay) return Replay(decision.ReplayResult);
                if (!decision.IsFresh) throw new ToolRefusal(decision.Message);

                original = File.ReadAllBytes(filePath);
                if (original.Length < 4 || original[0] != 0x50 || original[1] != 0x4B) // "PK"
                    throw new InvalidDataException("Not an .xlsx (OPC/zip) file — refusing to write. First bytes are not a zip signature.");

                List<KeyValuePair<string, byte[]>> entries;
                try { entries = ReadEntries(original); }
                catch (Exception ex) { throw new InvalidDataException("File is not a readable .xlsx package: " + ex.Message); }

                if (!entries.Any(kv => kv.Key == "xl/workbook.xml"))
                    throw new InvalidDataException("Not an .xlsx workbook — xl/workbook.xml is absent. Refusing to write.");

                sheet = ResolveSheet(entries, sheetName);
                if (sheet == null)
                    throw new InvalidDataException(string.IsNullOrEmpty(sheetName)
                        ? "Could not resolve the first worksheet from xl/workbook.xml."
                        : "No worksheet named '" + sheetName + "'. Present sheets are read back from the workbook.");

                byte[] sheetBytes = entries.FirstOrDefault(kv => kv.Key == sheet.PartPath).Value;
                if (sheetBytes == null)
                    throw new InvalidDataException("Worksheet part '" + sheet.PartPath + "' referenced by the workbook is missing from the package.");

                string newSheetXml = AppendRowsToSheetXml(Utf8(sheetBytes), rows, out rep);
                byte[] newSheetBytes = new UTF8Encoding(false).GetBytes(newSheetXml);
                byte[] produced = RewriteZip(entries, sheet.PartPath, newSheetBytes);

                // RE-READ the produced bytes and confirm the appended cells are actually there,
                // with the values we intended, before we let it replace the original.
                VerifyReadBack(produced, sheet.PartPath, rows, rep);

                hasTable = SheetHasTable(entries, sheet.PartPath);

                File.Copy(filePath, backupPath, true);
                File.WriteAllBytes(tmp, produced);

                // From here the original may already have been replaced. Everything above
                // is a read or a write to a file nobody else is going to open, so a failure
                // there is safely terminal; a failure from here on is not knowable, and the
                // ledger must be left in doubt rather than recording an outcome.
                writeStarted = true;
                replaceNote = ReplaceFile(tmp, filePath);

                // THE FILE, not the bytes we hoped we wrote. VerifyReadBack above proved
                // the produced package was correct IN MEMORY; it says nothing about what
                // survived File.Replace or the Copy fallback. Claiming verified=true off
                // the in-memory check was claiming a property of the wrong artefact.
                onDisk = File.ReadAllBytes(filePath);
                if (Sha256Hex(onDisk) != Sha256Hex(produced))
                    throw new IOException(
                        "The workbook on disk is NOT the package that was verified: its SHA-256 differs from the " +
                        "bytes this call produced." + RestoreFromBackup(backupPath, filePath) + " Nothing about the " +
                        "rows can be claimed - the check passed against the package in memory, and something else " +
                        "ended up in the file.");

                // And re-run the read-back over the DISK bytes, so 'verified' names the
                // file the caller is going to open.
                try
                {
                    VerifyReadBack(onDisk, sheet.PartPath, rows, rep);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        "The workbook on disk did not read back as intended: " + ex.Message +
                        RestoreFromBackup(backupPath, filePath), ex);
                }
            }
            catch (Exception ex) when (!writeStarted && decision != null && decision.IsFresh)
            {
                // A refusal that never reached the replace: the workbook is exactly as it
                // was. Recording it as a terminal failure is what lets an identical retry
                // be told the same thing instead of finding the key in doubt - in doubt is
                // for outcomes nobody can know, and this one is known.
                ledger.Complete(decision, CommandResult.Fail(ex.Message));
                throw;
            }
            finally
            {
                ReleaseWorkbookLock(lockHandle, lockPath, tmp);
            }

            var response = new JObject
            {
                ["file_path"] = filePath,
                ["sheet"] = sheet.Name,
                ["available_sheets"] = new JArray(sheet.SheetNames.Cast<object>().ToArray()),
                ["rows_written"] = rep.RowsAppended,
                ["first_new_row"] = rep.FirstNewRow,
                ["last_new_row"] = rep.LastNewRow,
                ["verified"] = true,
                ["verified_means"] = "Twice: the produced package was re-opened in memory and every appended cell read back and matched the value requested, and then THE FILE ON DISK was re-read after the replace, its SHA-256 compared against the package that was verified, and every appended cell read back again from those bytes. The second check is the one that speaks about the file you are going to open.",
                ["backup_path"] = backupPath,
                ["replace_method"] = replaceNote,
                ["bytes_before"] = original.Length,
                ["bytes_after"] = onDisk.Length,
                ["sha256_after"] = Sha256Hex(onDisk),
                ["sheet_has_table"] = hasTable,
                ["table_note"] = hasTable
                    ? "This sheet carries an Excel Table. Rows were appended to the sheet but the table's range was NOT expanded — the new rows are below the table, not inside it."
                    : "No Excel Table detected on this sheet.",
                ["mode"] = "append_inline_strings",
                ["idempotency_key"] = idempotencyKey,
                ["replayed"] = false
            };

            // Recorded AFTER the lock is released, and that ordering is safe: the rows are
            // in the file and verified there. A failure to record now costs a retry the
            // replay - it becomes in_doubt, which refuses rather than appending twice.
            ledger.Complete(decision, CommandResult.Ok(response));
            return response;
        }

        /// <summary>
        /// Hand back the recorded answer, marked as a replay so a caller can tell "your
        /// rows are already in the file" from "I just appended them". Every other field is
        /// the first answer's, including first_new_row and sha256_after - the point is
        /// that it describes the write that actually happened, not this call.
        /// </summary>
        private static JObject Replay(CommandResult result)
        {
            if (result == null) throw new ToolRefusal("The durable replay record had no result.");
            if (!result.Success)
                throw new ToolRefusal((result.Error ?? "The recorded workbook append failed.") +
                                      " This is the recorded answer for that idempotency_key; nothing was " +
                                      "written now. Use a NEW key only if you deliberately decide the append " +
                                      "must be attempted again.");

            if (!(result.Data is JObject recorded))
                throw new ToolRefusal("The durable replay record for this key is not a workbook result.");

            var clone = (JObject)recorded.DeepClone();
            clone["replayed"] = true;
            clone["replay_note"] =
                "These rows were appended by an EARLIER call with this same idempotency_key. The workbook was " +
                "not opened or written now; every count and hash above describes that first write.";
            return clone;
        }

        /// <summary>
        /// Take the workbook's exclusive lock, or refuse. Nothing has been read yet when
        /// this runs, which is what makes the refusal safe: a caller that is told "no"
        /// has lost nothing, whereas a caller that read first and queued here would go on
        /// to write a package built from bytes that changed while it waited.
        /// </summary>
        private static FileStream AcquireWorkbookLock(string lockPath)
        {
            try
            {
                var handle = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using (var lw = new StreamWriter(handle, new UTF8Encoding(false), 512, true))
                    lw.WriteLine("horizun pid " + System.Diagnostics.Process.GetCurrentProcess().Id + " " +
                                 DateTime.UtcNow.ToString("u"));
                handle.Flush();
                return handle;
            }
            catch (IOException)
            {
                string held;
                try { held = File.ReadAllText(lockPath).Trim(); }
                catch (IOException ex) { held = "the lock file exists but could not be read: " + ex.Message; }
                catch (UnauthorizedAccessException ex) { held = "the lock file exists but is not readable: " + ex.Message; }
                throw new IOException(
                    "Another write to this workbook is in progress and holds " + lockPath + " (" + held + "). " +
                    "Nothing was read and nothing was written. Two appenders interleaving would each rewrite the " +
                    "whole package from its own copy, and the second to finish would silently discard the first's " +
                    "rows. If no write is really running, delete that lock file.");
            }
        }

        /// <summary>
        /// Release the lock and clear the temp. Failures here are reported through the
        /// server's error channel, never swallowed: a lock file left behind makes the
        /// workbook unwritable for every later call, and finding out why by guessing is
        /// exactly the situation this bridge exists to avoid. It does not throw, because
        /// that would replace the real failure with the cleanup's.
        /// </summary>
        private static void ReleaseWorkbookLock(FileStream lockHandle, string lockPath, string tmp)
        {
            var problems = new List<string>();

            try { lockHandle.Dispose(); }
            catch (IOException ex) { problems.Add("could not close the lock handle: " + ex.Message); }

            try { if (File.Exists(lockPath)) File.Delete(lockPath); }
            catch (IOException ex) { problems.Add("could not delete " + lockPath + ": " + ex.Message); }
            catch (UnauthorizedAccessException ex) { problems.Add("not allowed to delete " + lockPath + ": " + ex.Message); }

            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (IOException ex) { problems.Add("could not delete the temp file " + tmp + ": " + ex.Message); }
            catch (UnauthorizedAccessException ex) { problems.Add("not allowed to delete the temp file " + tmp + ": " + ex.Message); }

            if (problems.Count > 0)
                Console.Error.WriteLine("horizun_excel_write_rows cleanup: " + string.Join("; ", problems) +
                                        ". A lock file left behind blocks every later write to this workbook.");
        }

        /// <summary>
        /// Atomic replace where the platform supports it, with a stated fallback. The
        /// reason for the fallback is returned rather than discarded: "we used the
        /// non-atomic path" is exactly what someone reading an incident needs.
        /// </summary>
        private static string ReplaceFile(string tmp, string destination)
        {
            try
            {
                File.Replace(tmp, destination, null);
                return "File.Replace (atomic where the filesystem supports it)";
            }
            catch (IOException ex)
            {
                File.Copy(tmp, destination, true);
                return "File.Copy fallback, because File.Replace failed: " + ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                File.Copy(tmp, destination, true);
                return "File.Copy fallback, because File.Replace was not permitted: " + ex.Message;
            }
        }

        /// <summary>
        /// Put the original back after a failed write, and SAY whether it worked. A
        /// message that names a backup nobody restored is not a recovery.
        /// </summary>
        private static string RestoreFromBackup(string backupPath, string filePath)
        {
            try
            {
                File.Copy(backupPath, filePath, true);
                return " The original was RESTORED from " + backupPath + ", which is still there.";
            }
            catch (IOException ex)
            {
                return " The original could NOT be restored automatically (" + ex.Message + "); it is at " +
                       backupPath + " and must be put back by hand.";
            }
            catch (UnauthorizedAccessException ex)
            {
                return " The original could NOT be restored automatically (" + ex.Message + "); it is at " +
                       backupPath + " and must be put back by hand.";
            }
        }

        private static object JsonValueToClr(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Null: return null;
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Integer: return (long)t;
                case JTokenType.Float: return (double)t;
                default: return (string)t; // string, date, etc. -> text
            }
        }

        /// <summary>Re-open the produced bytes and assert every appended cell reads back as intended.</summary>
        private static void VerifyReadBack(byte[] produced, string sheetPartPath, List<IList<object>> rows, AppendReport rep)
        {
            List<KeyValuePair<string, byte[]>> re = ReadEntries(produced);
            byte[] sb = re.FirstOrDefault(kv => kv.Key == sheetPartPath).Value;
            if (sb == null) throw new IOException("Verification failed: worksheet part vanished from the produced workbook.");

            XDocument doc = XDocument.Parse(Utf8(sb));
            XElement sheetData = doc.Root.Element(Main + "sheetData");
            if (sheetData == null) throw new IOException("Verification failed: sheetData missing after write.");

            var byRow = sheetData.Elements(Main + "row")
                .Where(r => { int n; return int.TryParse((string)r.Attribute("r"), out n) && n >= rep.FirstNewRow && n <= rep.LastNewRow; })
                .ToDictionary(r => int.Parse((string)r.Attribute("r")), r => r);

            for (int i = 0; i < rows.Count; i++)
            {
                int rn = rep.FirstNewRow + i;
                if (!byRow.TryGetValue(rn, out XElement rowEl))
                    throw new IOException("Verification failed: appended row " + rn + " is not present on re-read.");
                IList<object> intended = rows[i];
                for (int col = 0; col < intended.Count; col++)
                {
                    if (intended[col] == null) continue; // blank stays blank
                    string reference = ColumnLetter(col) + rn;
                    XElement cell = rowEl.Elements(Main + "c").FirstOrDefault(c => (string)c.Attribute("r") == reference);
                    if (cell == null) throw new IOException("Verification failed: cell " + reference + " is missing on re-read.");
                    string readBack = ReadCellText(cell);
                    string want = Convert.ToString(intended[col], System.Globalization.CultureInfo.InvariantCulture);
                    if (intended[col] is bool bb) want = bb ? "1" : "0";
                    if (readBack != want)
                        throw new IOException("Verification failed at " + reference + ": wrote '" + want + "' but re-read '" + readBack + "'.");
                }
            }
        }

        private static string ReadCellText(XElement cell)
        {
            string t = (string)cell.Attribute("t");
            if (t == "inlineStr")
                return cell.Element(Main + "is")?.Element(Main + "t")?.Value ?? "";
            return cell.Element(Main + "v")?.Value ?? "";
        }
    }
}
