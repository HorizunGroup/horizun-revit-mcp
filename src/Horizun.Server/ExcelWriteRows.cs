// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// A HOST-RESIDENT tool: it answers inside this process and never touches Revit.
// It appends rows to a worksheet in an existing .xlsx, preserving the rest of the
// workbook (every other sheet, all styles, tables and formatting), and does it
// under the honesty contract:
//
//   * BACKUP first. The original is copied aside before a single byte is rewritten,
//     so a failure can never leave the caller with less than they started with. The
//     copy goes to the Horizun state folder (%USERPROFILE%\.horizun\backups\excel),
//     never beside the user's file, and the reply names it.
//   * CREATE only when asked. create_if_missing: true starts a new .xlsx from the
//     MinimalWorkbook package when the path holds nothing; an existing file is
//     appended to, never replaced, through that flag. A new workbook needs no backup.
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
using System.Threading;
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

        // ---- the CSV side of the same contract. -----------------------------
        //
        // 2026-10-08 audit: this path used to write ANY extension to ANY folder it
        // could create, with free text - a .bat in the Startup folder was one call
        // away, with no Python permission involved. It now writes only a .csv, only
        // into a folder that already exists and is not a system or autostart
        // location, through the same lock, backup, temp-and-replace and on-disk
        // re-read as the workbook path, in the file's own encoding and delimiter.
        private static JObject HandleCsv(JObject args, string filePath, List<IList<object>> rows,
                                         string idempotencyKey, DurableCommandLedger ledger,
                                         CancellationToken cancellationToken)
        {
            string problem = CsvPathProblem(filePath, ForbiddenCsvRoots());
            if (problem != null) throw new ToolRefusal(problem + " Nothing was written.");
            string fullPath = Path.GetFullPath(filePath);

            string stamp = System.Diagnostics.Process.GetCurrentProcess().Id + "-" +
                           Guid.NewGuid().ToString("N").Substring(0, 8);
            string lockPath = fullPath + ".horizunlock";
            string backupPath = BackupPathFor(fullPath, stamp);
            string tmp = fullPath + "." + stamp + ".horizuntmp";

            cancellationToken.ThrowIfCancellationRequested();
            FileStream lockHandle = AcquireWorkbookLock(lockPath);

            DurableCommandDecision decision = null;
            bool writeStarted = false;
            bool keepTemp = false;
            bool existed;
            byte[] produced, onDisk;
            string encodingName, replaceNote, newline;
            char delimiter;
            try
            {
                // Claimed under the lock, exactly as the workbook path does and for the
                // same reason: a caller refused the lock must be able to retry the key.
                string fingerprint = RequestFingerprint.OfOperation(
                    ToolName, "csv:" + fullPath.ToLowerInvariant(), args, "idempotency_key");
                decision = ledger.Claim(idempotencyKey, ToolName, fingerprint);
                if (decision.Outcome == DurableCommandOutcome.Replay) return Replay(decision.ReplayResult);
                if (!decision.IsFresh) throw new ToolRefusal(decision.Message);

                existed = File.Exists(fullPath);
                byte[] before = existed ? File.ReadAllBytes(fullPath) : new byte[0];
                CsvText.Detected current;
                if (existed)
                {
                    try { current = CsvText.Detect(before); }
                    catch (DecoderFallbackException)
                    {
                        throw new ToolRefusal("The existing file " + fullPath + " is neither UTF-8 nor Windows-1252, so its " +
                                              "encoding is unknown and an append would corrupt it. Nothing was written.");
                    }
                }
                else
                {
                    current = new CsvText.Detected
                    {
                        Name = "utf-8", Encoding = new UTF8Encoding(false, true), Preamble = new byte[0], Text = ""
                    };
                }
                encodingName = current.Name;
                delimiter = existed ? CsvText.DetectDelimiter(current.Text) : ',';
                newline = existed ? CsvText.DetectNewline(current.Text) : "\r\n";

                var appended = new StringBuilder();
                if (current.Text.Length > 0 && !current.Text.EndsWith("\n")) appended.Append(newline);
                foreach (IList<object> row in rows)
                {
                    for (int i = 0; i < row.Count; i++)
                    {
                        if (i > 0) appended.Append(delimiter);
                        appended.Append(CsvText.Field(row[i], delimiter));
                    }
                    appended.Append(newline);
                }

                byte[] tail;
                try { tail = current.Encoding.GetBytes(appended.ToString()); }
                catch (EncoderFallbackException ex)
                {
                    throw new ToolRefusal("The file is " + current.Name + " and cannot hold the character U+" +
                                          ((int)ex.CharUnknown).ToString("X4") + " in these rows; writing it would " +
                                          "turn it into '?'. Nothing was written. Save the file as UTF-8 first, or " +
                                          "send the text without that character.");
                }

                byte[] head = existed ? before : current.Preamble;
                produced = new byte[head.Length + tail.Length];
                Buffer.BlockCopy(head, 0, produced, 0, head.Length);
                Buffer.BlockCopy(tail, 0, produced, head.Length, tail.Length);

                cancellationToken.ThrowIfCancellationRequested();
                if (!existed)
                {
                    backupPath = null;
                    File.WriteAllBytes(tmp, produced);
                    try { File.Move(tmp, fullPath); }
                    catch (IOException ex)
                    {
                        throw new IOException(
                            "A file appeared at " + fullPath + " while the new CSV was being prepared, and it was NOT " +
                            "overwritten (" + ex.Message + "). Nothing was written.", ex);
                    }
                    writeStarted = true;
                    replaceNote = "File.Move of a new file (nothing existed to replace)";
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                    File.Copy(fullPath, backupPath, true);
                    File.WriteAllBytes(tmp, produced);
                    cancellationToken.ThrowIfCancellationRequested();
                    writeStarted = true;
                    try { replaceNote = ReplaceFile(tmp, fullPath, backupPath); }
                    catch (IOException ex)
                    {
                        // Same as the workbook path: the verified new CSV stays where the
                        // message says it is, and an untouched original makes the outcome KNOWN.
                        keepTemp = true;
                        bool unchanged;
                        try { unchanged = Sha256Hex(File.ReadAllBytes(fullPath)) == Sha256Hex(before); }
                        catch (IOException) { unchanged = false; }
                        catch (UnauthorizedAccessException) { unchanged = false; }
                        if (unchanged) ledger.Complete(decision, CommandResult.Fail(ex.Message));
                        throw;
                    }
                }

                // The FILE, not the bytes we meant: what is on disk must be exactly the
                // original followed by the rows, byte for byte.
                onDisk = File.ReadAllBytes(fullPath);
                if (Sha256Hex(onDisk) != Sha256Hex(produced))
                    throw new IOException("The CSV on disk is NOT what this call produced: its SHA-256 differs." +
                                          Undo(!existed, backupPath, fullPath));
            }
            catch (Exception ex) when (!writeStarted && decision != null && decision.IsFresh)
            {
                ledger.Complete(decision, CommandResult.Fail(ex.Message));
                throw;
            }
            finally
            {
                ReleaseWorkbookLock(lockHandle, lockPath, keepTemp ? null : tmp);
            }

            int lines = 0;
            foreach (byte x in onDisk) if (x == (byte)'\n') lines++;
            var result = new JObject
            {
                ["file_path"] = fullPath,
                ["format"] = "csv",
                ["created"] = !existed,
                ["rows_written"] = rows.Count,
                ["total_lines_after"] = lines,
                ["bytes"] = onDisk.Length,
                ["sha256"] = Sha256Hex(onDisk),
                ["encoding"] = encodingName,
                ["delimiter"] = delimiter.ToString(),
                ["newline"] = newline == "\n" ? "lf" : "crlf",
                ["backup_path"] = backupPath == null ? JValue.CreateNull() : (JToken)backupPath,
                ["replace_method"] = replaceNote,
                ["verified_by_reread"] = true,
                ["verified_means"] = "The file was re-read after the replace and is byte-for-byte the original followed by " +
                                     "the appended rows (SHA-256 compared). Text a spreadsheet would run as a formula was " +
                                     "prefixed with an apostrophe."
            };
            ledger.Complete(decision, CommandResult.Ok(result));
            return result;
        }

        /// <summary>
        /// Why format=csv may not write at this path, or null. Absolute local paths only (no
        /// UNC: a share is also where Windows hands out the user's NTLM hash; no device
        /// namespace; no alternate data stream), a .csv extension, a folder that already
        /// exists, and nothing under a system or autostart location.
        /// </summary>
        internal static string CsvPathProblem(string filePath, IEnumerable<string> forbiddenRoots)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return "file_path is required.";
            string local = LocalPath.Problem(filePath, "file_path");
            if (local != null) return "format=csv: " + local;
            string full;
            try { full = Path.GetFullPath(filePath); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            { return "file_path is not a valid path: " + ex.Message; }
            if (full.IndexOf(':', 2) >= 0)
                return "file_path names an alternate data stream ('" + full + "'); format=csv writes plain files only.";
            if (!string.Equals(Path.GetExtension(full), ".csv", StringComparison.OrdinalIgnoreCase))
                return "format=csv writes only files ending in .csv; '" + full + "' does not.";
            foreach (string root in forbiddenRoots ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return "'" + full + "' is under " + r + ", a system or autostart folder this tool never writes into.";
            }
            string dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return "The folder '" + dir + "' does not exist, and format=csv does not create folders.";
            return null;
        }

        /// <summary>Folders a CSV append never writes into: autostart, Windows, Program Files, ssh keys.</summary>
        internal static IEnumerable<string> ForbiddenCsvRoots()
        {
            var roots = new List<string>();
            foreach (Environment.SpecialFolder f in new[]
            {
                Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup,
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86
            })
            {
                string p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p)) roots.Add(p);
            }
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile)) roots.Add(Path.Combine(profile, ".ssh"));
            return roots;
        }

        /// <summary>One comma-delimited CSV cell: RFC-4180 quoting, formula text neutralised.</summary>
        internal static string CsvField(object value) => CsvText.Field(value, ',');

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

        /// <summary>
        /// A valid, EMPTY .xlsx with one worksheet: the smallest package Excel, openpyxl
        /// and this file's own reader all accept. It exists so a host-resident tool can
        /// CREATE a report workbook and then append to it through Handle, inheriting the
        /// lock, the re-read verification and the durable ledger instead of growing a
        /// second writer. The append path itself starts from this package only when the
        /// caller asks for it with create_if_missing: true - never by default.
        /// </summary>
        internal static byte[] MinimalWorkbook(string sheetName)
        {
            string problem = SheetNameProblem(sheetName);
            if (problem != null) throw new ArgumentException(problem);
            string escaped = new XText(sheetName).ToString().Replace("\"", "&quot;");
            var parts = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "</Types>"),
                new KeyValuePair<string, string>("_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>"),
                new KeyValuePair<string, string>("xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"" + escaped + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
                new KeyValuePair<string, string>("xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "</Relationships>"),
                new KeyValuePair<string, string>("xl/worksheets/sheet1.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<dimension ref=\"A1:A1\"/><sheetData/></worksheet>")
            };
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                    foreach (var kv in parts)
                    {
                        ZipArchiveEntry e = zip.CreateEntry(kv.Key, CompressionLevel.Optimal);
                        byte[] data = new UTF8Encoding(false).GetBytes(kv.Value);
                        using (var w = e.Open()) w.Write(data, 0, data.Length);
                    }
                return ms.ToArray();
            }
        }

        /// <summary>Excel's own rules for a sheet name, so the workbook opens rather than repairs.</summary>
        internal static string SheetNameProblem(string sheetName)
        {
            if (string.IsNullOrWhiteSpace(sheetName)) return "sheet name is required.";
            if (sheetName.Length > 31) return "sheet name '" + sheetName + "' is longer than Excel's 31-character limit.";
            if (sheetName.IndexOfAny(new[] { '[', ']', ':', '*', '?', '/', '\\' }) >= 0)
                return "sheet name '" + sheetName + "' contains a character Excel forbids ([ ] : * ? / \\).";
            return null;
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

        /// <summary>Largest single part a workbook may inflate to: 256 MB.</summary>
        internal const long MaxEntryBytes = 256L << 20;
        /// <summary>Largest the whole package may inflate to: 512 MB.</summary>
        internal const long MaxTotalBytes = 512L << 20;
        /// <summary>Inflated/compressed ratio no real worksheet reaches (checked past 1 MB inflated).</summary>
        internal const long MaxRatio = 200;
        private const long RatioFloorBytes = 1L << 20;

        /// <summary>Read every zip entry into an ordered name->bytes map (order preserved for a faithful rewrite).</summary>
        internal static List<KeyValuePair<string, byte[]>> ReadEntries(byte[] xlsxBytes)
            => ReadEntries(xlsxBytes, MaxEntryBytes, MaxRatio, MaxTotalBytes);

        /// <summary>
        /// The bounded reader. 2026-10-08 audit: every entry used to be inflated whole with
        /// CopyTo, so a few KB of crafted zip became gigabytes in memory. Bytes are now counted
        /// while they stream, and an entry is refused the moment it passes the size cap or
        /// inflates past maxRatio times its compressed size - before the rest is read.
        /// </summary>
        internal static List<KeyValuePair<string, byte[]>> ReadEntries(byte[] xlsxBytes, long maxEntryBytes, long maxRatio, long maxTotalBytes)
        {
            var entries = new List<KeyValuePair<string, byte[]>>();
            long total = 0;
            var chunk = new byte[81920];
            using (var ms = new MemoryStream(xlsxBytes, false))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    long compressed = Math.Max(1, Math.Min(e.CompressedLength, xlsxBytes.Length));
                    using (var s = e.Open())
                    using (var buf = new MemoryStream())
                    {
                        long inflated = 0;
                        int n;
                        while ((n = s.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            inflated += n;
                            total += n;
                            if (inflated > maxEntryBytes)
                                throw new InvalidDataException("Package part '" + e.FullName + "' inflates beyond " + maxEntryBytes +
                                                               " bytes; refused as a possible zip bomb. Nothing was read further.");
                            if (inflated > RatioFloorBytes && inflated / compressed > maxRatio)
                                throw new InvalidDataException("Package part '" + e.FullName + "' inflates more than " + maxRatio +
                                                               ":1 (compression ratio) - refused as a possible zip bomb. Nothing was read further.");
                            if (total > maxTotalBytes)
                                throw new InvalidDataException("The package inflates beyond " + maxTotalBytes +
                                                               " bytes in total; refused as a possible zip bomb.");
                            buf.Write(chunk, 0, n);
                        }
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

        internal static JObject Handle(JObject args) => Handle(args, CancellationToken.None);

        internal static JObject Handle(JObject args, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Handle(args, new DurableCommandLedger(retentionLog: message => Log.Info(message)), cancellationToken);
        }

        internal static JObject Handle(JObject args, DurableCommandLedger ledger) =>
            Handle(args, ledger, CancellationToken.None);

        internal static JObject Handle(JObject args, DurableCommandLedger ledger,
                                       CancellationToken cancellationToken)
            => Handle(args, ledger, cancellationToken, startFresh: false, replaceExisting: false);

        /// <summary>
        /// The same lock, ledger, backup, temp-and-replace and two read-backs, but the rows go
        /// into a NEW workbook (sheet = args.sheet) that takes the place of whatever is at the
        /// path: horizun_budget_compare's report. An existing file is replaced only when
        /// replaceExisting is true, and that is decided again UNDER THE LOCK; its bytes need
        /// not be a workbook, because they are backed up and replaced, never parsed.
        /// </summary>
        internal static JObject WriteNewWorkbook(JObject args, DurableCommandLedger ledger,
                                                 CancellationToken cancellationToken, bool replaceExisting)
            => Handle(args, ledger, cancellationToken, startFresh: true, replaceExisting: replaceExisting);

        private static JObject Handle(JObject args, DurableCommandLedger ledger,
                                      CancellationToken cancellationToken, bool startFresh, bool replaceExisting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string filePath = (string)args?["file_path"];
            string sheetName = (string)args?["sheet"];
            JToken rowsTok = args?["rows"];

            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("file_path is required.");
            // Before the ledger and before the first File.Exists: a UNC path is an SMB
            // sign-in, a drive- or root-relative one lands in the server's working folder.
            string notLocal = LocalPath.Problem(filePath, "file_path");
            if (notLocal != null) throw new ArgumentException(notLocal + " Nothing was written.");
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

            // ---- format=csv: the same append contract, plain text. --------------
            // A CSV is created when absent (an xlsx never is - its structure cannot
            // be invented), rows append under RFC-4180 quoting, and the evidence is
            // the re-read file: bytes, sha256 and the line count afterwards.
            string format = ((string)args?["format"] ?? "xlsx").ToLowerInvariant();
            if (format == "csv") return HandleCsv(args, filePath, rows, idempotencyKey, ledger, cancellationToken);
            if (format != "xlsx")
                throw new ArgumentException("format must be xlsx or csv.");

            // create_if_missing is OPT-IN and only ever creates: an existing file is
            // appended to exactly as without it, never replaced. The new workbook is the
            // MinimalWorkbook package, and the rows reach it through the same append,
            // lock, in-memory and on-disk read-back and ledger as any other call.
            bool createIfMissing = args["create_if_missing"] != null &&
                                   args["create_if_missing"].Type == JTokenType.Boolean &&
                                   (bool)args["create_if_missing"];
            if (startFresh) createIfMissing = true;
            string newSheetName = string.IsNullOrEmpty(sheetName) ? DefaultNewSheetName : sheetName;
            if (!File.Exists(filePath))
            {
                if (!createIfMissing)
                    throw new FileNotFoundException(
                        "Workbook not found: " + filePath + ". Nothing was created. To start a new workbook, send " +
                        "create_if_missing: true (with sheet to name its first worksheet; default '" +
                        DefaultNewSheetName + "'). An existing file is never replaced through that flag.");
                string problem = NewWorkbookProblem(filePath, newSheetName);
                if (problem != null) throw new ArgumentException(problem);
                string parent = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            }

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
            string backupPath = BackupPathFor(filePath, stamp);
            string tmp = filePath + "." + stamp + ".horizuntmp";

            cancellationToken.ThrowIfCancellationRequested();
            FileStream lockHandle = AcquireWorkbookLock(lockPath);

            byte[] onDisk;
            byte[] original;
            ResolvedSheet sheet;
            AppendReport rep;
            bool hasTable;
            string replaceNote;
            DurableCommandDecision decision = null;
            bool writeStarted = false;
            bool created = false;
            bool keepTemp = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // THE KEY IS CLAIMED AFTER THE LOCK, deliberately. A caller refused the
                // lock has been told "somebody else is writing, nothing was read, nothing
                // was written" - and a claim written before that refusal would leave the
                // key in doubt forever, so the one thing a caller should obviously do
                // (wait and retry with the same key) would be permanently refused.
                string fingerprint = RequestFingerprint.OfOperation(
                    ToolName, LedgerScopeOf(args) + (startFresh ? "|new-workbook" : ""), args, "idempotency_key");
                decision = ledger.Claim(idempotencyKey, ToolName, fingerprint);

                if (decision.Outcome == DurableCommandOutcome.Replay) return Replay(decision.ReplayResult);
                if (!decision.IsFresh) throw new ToolRefusal(decision.Message);

                // Decided again UNDER THE LOCK: the file may have appeared since the
                // check above, and then this call appends to it rather than creating.
                created = !File.Exists(filePath);
                if (created && !createIfMissing)
                    throw new FileNotFoundException("Workbook not found: " + filePath + ". Nothing was created.");
                if (startFresh && !created && !replaceExisting)
                    throw new ToolRefusal("A file now exists at " + filePath + " and replacing it was not allowed. Nothing was touched.");
                original = created ? MinimalWorkbook(newSheetName) : File.ReadAllBytes(filePath);
                // A fresh workbook is built from the minimal package; the file it replaces is
                // backed up and never parsed, so it need not be a workbook at all.
                byte[] basePackage = startFresh ? MinimalWorkbook(newSheetName) : original;
                if (basePackage.Length < 4 || basePackage[0] != 0x50 || basePackage[1] != 0x4B) // "PK"
                    throw new InvalidDataException("Not an .xlsx (OPC/zip) file — refusing to write. First bytes are not a zip signature.");

                List<KeyValuePair<string, byte[]>> entries;
                try { entries = ReadEntries(basePackage); }
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

                // Last cooperative boundary before any external artefact is written.
                // Once replacement starts the operation must run through verification
                // and durable completion; aborting there would manufacture ambiguity.
                cancellationToken.ThrowIfCancellationRequested();
                if (created)
                {
                    // Nothing to back up: there was no file. The move refuses an existing
                    // destination, so a file that appeared from outside Horizun since the
                    // check under the lock is left exactly as it is.
                    backupPath = null;
                    File.WriteAllBytes(tmp, produced);
                    cancellationToken.ThrowIfCancellationRequested();
                    try { File.Move(tmp, filePath); }
                    catch (IOException ex)
                    {
                        throw new IOException(
                            "A file appeared at " + filePath + " while the new workbook was being prepared, and it " +
                            "was NOT overwritten (" + ex.Message + "). Nothing was written; send the call again " +
                            "with a new idempotency_key to append to that file instead.", ex);
                    }
                    writeStarted = true;
                    replaceNote = "File.Move of a new file (create_if_missing: nothing existed to replace)";
                }
                else
                {
                    // The backup lives in the Horizun state folder, not beside the user's
                    // file: it is Horizun's recovery copy, and keeping it in the user's
                    // folder left a stray .horizunbak next to every workbook appended to.
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                    File.Copy(filePath, backupPath, true);
                    File.WriteAllBytes(tmp, produced);
                    cancellationToken.ThrowIfCancellationRequested();

                    // From here the original may already have been replaced. Everything above
                    // is a read or a write to a file nobody else is going to open, so a failure
                    // there is safely terminal; a failure from here on is not knowable, and the
                    // ledger must be left in doubt rather than recording an outcome.
                    writeStarted = true;
                    try { replaceNote = ReplaceFile(tmp, filePath, backupPath); }
                    catch (IOException ex)
                    {
                        // The verified new workbook stays where the message says it is.
                        keepTemp = true;
                        // File.Replace without a backup name leaves the destination as it
                        // was when it fails; prove that against the bytes read under the
                        // lock, and record the outcome as KNOWN rather than in doubt.
                        bool unchanged;
                        try { unchanged = Sha256Hex(File.ReadAllBytes(filePath)) == Sha256Hex(original); }
                        catch (IOException) { unchanged = false; }
                        catch (UnauthorizedAccessException) { unchanged = false; }
                        if (unchanged) ledger.Complete(decision, CommandResult.Fail(ex.Message));
                        throw;
                    }
                }

                // THE FILE, not the bytes we hoped we wrote. VerifyReadBack above proved
                // the produced package was correct IN MEMORY; it says nothing about what
                // survived File.Replace. Claiming verified=true off
                // the in-memory check was claiming a property of the wrong artefact.
                onDisk = File.ReadAllBytes(filePath);
                if (Sha256Hex(onDisk) != Sha256Hex(produced))
                    throw new IOException(
                        "The workbook on disk is NOT the package that was verified: its SHA-256 differs from the " +
                        "bytes this call produced." + Undo(created, backupPath, filePath) + " Nothing about the " +
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
                        Undo(created, backupPath, filePath), ex);
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
                ReleaseWorkbookLock(lockHandle, lockPath, keepTemp ? null : tmp);
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
                ["created"] = created,
                ["backup_path"] = backupPath == null ? JValue.CreateNull() : (JToken)backupPath,
                ["backup_note"] = created
                    ? "No backup: this call created the workbook, so there was no earlier file to preserve."
                    : startFresh
                        ? "The file that was at file_path was copied to backup_path, in the Horizun state folder, and then " +
                          "replaced by a new workbook. Copy it back over file_path to undo."
                        : "The workbook as it was before this append was copied to backup_path, in the Horizun state " +
                          "folder rather than beside your file. Copy it back over file_path to undo the append.",
                ["replaced_with_new_workbook"] = startFresh && !created,
                ["replace_method"] = replaceNote,
                ["bytes_before"] = created ? 0 : original.Length,
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
        /// Atomic replace, or a failure that says where everything is. There used to be a
        /// File.Copy fallback: a reader holding the workbook without FILE_SHARE_DELETE
        /// (Excel itself, an indexer) makes Replace fail while Copy still succeeds, so the
        /// user's file was rewritten in place, non-atomically, and the call reported
        /// success (audit 2026-10-08). Now nothing is copied over it.
        /// </summary>
        private static string ReplaceFile(string tmp, string destination, string backupPath)
        {
            try
            {
                File.Replace(tmp, destination, null);
                return "File.Replace (atomic where the filesystem supports it)";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException(
                    "The workbook at " + destination + " could not be replaced (" + ex.Message + "), usually because " +
                    "another program holds it open. It was NOT rewritten in place. The verified new workbook is at " +
                    tmp + " and the copy taken before this call is at " + backupPath + ". Close the other program and " +
                    "send the call again with a new idempotency_key, or put the new workbook in place by hand.", ex);
            }
        }

        /// <summary>The first worksheet's name when create_if_missing makes a workbook and no sheet is named.</summary>
        internal const string DefaultNewSheetName = "Sheet1";

        /// <summary>
        /// Why a NEW workbook cannot be created at this path with this sheet name, or null.
        /// Only .xlsx is created: an .xlsm or .xltx holding this package would carry the
        /// wrong content type and open as a repair.
        /// </summary>
        internal static string NewWorkbookProblem(string filePath, string sheetName)
        {
            if (!string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
                return "create_if_missing creates .xlsx workbooks only; '" + filePath + "' does not end in .xlsx. Nothing was created.";
            string sheetProblem = SheetNameProblem(sheetName);
            return sheetProblem == null ? null : sheetProblem + " Nothing was created.";
        }

        /// <summary>
        /// Where the pre-append copy of a workbook goes: backups\excel under the Horizun data
        /// root (%USERPROFILE%\.horizun, or HORIZUN_DATA_ROOT). The name keeps the workbook's
        /// file name for a person looking for it, a hash of its full path so two Book1.xlsx in
        /// different folders never share a name, and the per-call stamp so a later append
        /// never overwrites an earlier call's copy.
        /// </summary>
        internal static string BackupPathFor(string filePath, string stamp)
        {
            string full;
            try { full = Path.GetFullPath(filePath); } catch (Exception) { full = filePath; }
            string pathHash = Sha256Hex(Encoding.UTF8.GetBytes(full.ToLowerInvariant())).Substring(0, 8);
            return Path.Combine(BackupDirectory(), Path.GetFileName(filePath) + "." + pathHash + "." + stamp + ".horizunbak");
        }

        internal static string BackupDirectory() => Path.Combine(HorizunPaths.DataRoot(), "backups", "excel");

        /// <summary>Undo a failed write: restore the backup, or remove the workbook this call created.</summary>
        private static string Undo(bool created, string backupPath, string filePath)
            => created ? RemoveCreated(filePath) : RestoreFromBackup(backupPath, filePath);

        /// <summary>
        /// A workbook this call created and could not verify is removed, which is exactly the
        /// state before the call: no file. Said either way.
        /// </summary>
        private static string RemoveCreated(string filePath)
        {
            try
            {
                File.Delete(filePath);
                return " The workbook this call created was REMOVED again, so the path is as it was: no file.";
            }
            catch (IOException ex)
            {
                return " The workbook this call created could NOT be removed (" + ex.Message + "); delete " + filePath + " by hand.";
            }
            catch (UnauthorizedAccessException ex)
            {
                return " The workbook this call created could NOT be removed (" + ex.Message + "); delete " + filePath + " by hand.";
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
