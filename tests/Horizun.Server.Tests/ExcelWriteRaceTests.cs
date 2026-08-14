using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>
    /// The race the lock did NOT close.
    ///
    /// ExcelConcurrencyTests proves the lock refuses a writer that arrives while the
    /// lock file exists. It creates that file by hand, so it only ever exercised the
    /// case where the collision happens inside the short window the lock is held.
    ///
    /// The real order of operations was: read the workbook, parse the package, append
    /// the rows, rewrite the whole zip IN MEMORY -- and only THEN take the lock. Two
    /// writers therefore never met at the lock at all. They both read the same
    /// snapshot, queued politely on the lock, and the second one wrote a package built
    /// from bytes that no longer described the file. The first writer's rows were
    /// gone, and it had already answered rows_written: 1 and verified: true.
    ///
    /// The invariant asserted here is exact, not statistical: every call that RETURNED
    /// SUCCESS must have its row in the workbook. A refused call is fine -- being told
    /// "no" loses nothing. Silently reporting success for a row that is not in the file
    /// is the whole defect.
    /// </summary>
    public class ExcelWriteRaceTests
    {
        /// <summary>
        /// A workbook with enough existing rows that reading and rewriting the package
        /// takes real time.
        ///
        /// This is not padding to make a flaky test pass. It is the condition under
        /// which the defect exists at all: the unlocked phase has to still be running
        /// when the second writer starts, and on an empty workbook it is over in about
        /// a millisecond. It is also the realistic case -- nobody races two appends on
        /// an empty sheet; they race them on the register everyone is writing to.
        /// </summary>
        internal static string MakeBook(int existingRows = 8000)
        {
            string path = Path.Combine(Path.GetTempPath(), "hz_race_" + Guid.NewGuid().ToString("N") + ".xlsx");
            var sb = new StringBuilder(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<dimension ref=\"A1:B" + Math.Max(existingRows, 1) + "\"/><sheetData>");
            for (int i = 1; i <= existingRows; i++)
                sb.Append("<row r=\"").Append(i).Append("\"><c r=\"A").Append(i)
                  .Append("\" t=\"inlineStr\"><is><t>fila ").Append(i).Append("</t></is></c></row>");
            sb.Append("</sheetData></worksheet>");
            string sheetXml = sb.ToString();

            var parts = new (string, string)[]
            {
                ("[Content_Types].xml",
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                 "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                 "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                 "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                 "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>"),
                ("_rels/.rels",
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                 "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
                ("xl/workbook.xml",
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                 "<sheets><sheet name=\"Hoja\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
                ("xl/_rels/workbook.xml.rels",
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                 "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
                ("xl/worksheets/sheet1.xml", sheetXml),
            };
            using (var fs = new FileStream(path, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                foreach (var (name, xml) in parts)
                {
                    var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(xml);
                }
            return path;
        }

        private static JObject Args(string path, string cell) => new JObject
        {
            ["file_path"] = path,
            ["rows"] = new JArray { new JArray { cell } },
            // Distinct work needs a distinct key: these tests race two DIFFERENT
            // appends, and sharing a key would make the second a replay of the first.
            ["idempotency_key"] = Guid.NewGuid().ToString("N")
        };

        /// <summary>Many rows, so this caller's transform outlasts the other's whole call.</summary>
        private static JObject BulkArgs(string path, string tag, int rowCount)
        {
            var rows = new JArray();
            for (int i = 0; i < rowCount; i++) rows.Add(new JArray { tag + i });
            return new JObject
            {
                ["file_path"] = path,
                ["rows"] = rows,
                ["idempotency_key"] = Guid.NewGuid().ToString("N")
            };
        }

        internal static void Sweep(string path)
        {
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            try
            {
                foreach (string p in Directory.GetFiles(dir, name + ".*"))
                {
                    try { File.Delete(p); }
                    catch (IOException) { }
                }
            }
            catch (DirectoryNotFoundException) { }
        }

        /// <summary>Every text cell present in the first worksheet of the workbook on disk.</summary>
        internal static List<string> CellsOnDisk(string path)
        {
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var found = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                ZipArchiveEntry sheet = zip.GetEntry("xl/worksheets/sheet1.xml");
                Assert.NotNull(sheet);
                using (var s = sheet.Open())
                {
                    XDocument doc = XDocument.Load(s);
                    foreach (XElement t in doc.Descendants(main + "t"))
                        found.Add(t.Value);
                }
            }
            return found;
        }

        [Fact]
        public async Task Two_concurrent_writers_never_lose_a_row_that_was_reported_written()
        {
            // The interleaving is forced by construction, not hoped for.
            //
            // Both writers are released from one barrier, so both read the same bytes --
            // neither can have written yet. The QUICK one appends a single row and is
            // done; the SLOW one appends thousands, so its transform is still running
            // long after the quick one took the lock, wrote, and released it. The slow
            // writer then takes a free lock and rewrites the whole package from the
            // snapshot it read before any of that happened.
            //
            // Two symmetric writers do NOT show this: they arrive at the lock together,
            // the second is refused, and being refused loses nothing. That is why the
            // existing suite stayed green over the defect.
            string path = MakeBook();
            try
            {
                var succeeded = new ConcurrentBag<string>();
                using (var barrier = new Barrier(2))
                {
                    Task quick = Task.Run(() =>
                    {
                        barrier.SignalAndWait();
                        try
                        {
                            JObject r = ExcelWriteRows.Handle(Args(path, "QUICK"), ExcelTestLedger.New());
                            if ((int)r["rows_written"] == 1) succeeded.Add("QUICK");
                        }
                        catch (IOException) { /* refused: told, so nothing is lost */ }
                    });

                    Task slow = Task.Run(() =>
                    {
                        barrier.SignalAndWait();
                        try
                        {
                            JObject r = ExcelWriteRows.Handle(BulkArgs(path, "SLOW", 60000), ExcelTestLedger.New());
                            if ((int)r["rows_written"] == 60000) succeeded.Add("SLOW0");
                        }
                        catch (IOException) { /* refused: told, so nothing is lost */ }
                    });

                    await Task.WhenAll(quick, slow);
                }

                List<string> onDisk = CellsOnDisk(path);
                var lost = succeeded.Where(cell => !onDisk.Contains(cell)).ToList();

                Assert.True(lost.Count == 0,
                    "These calls returned success and their rows are NOT in the workbook: " +
                    string.Join(", ", lost) + ". A writer that reports rows_written and verified: true " +
                    "must not have its rows discarded by a concurrent writer that started from the same " +
                    "stale snapshot. Rows on disk: " + onDisk.Count + ".");
            }
            finally { Sweep(path); }
        }

        [Fact]
        public void A_writer_refused_by_the_lock_did_not_read_or_alter_the_workbook()
        {
            string path = MakeBook(existingRows: 0);
            string lockPath = path + ".horizunlock";
            try
            {
                byte[] before = File.ReadAllBytes(path);

                using (new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var ex = Assert.ThrowsAny<Exception>(() => ExcelWriteRows.Handle(Args(path, "refused"), ExcelTestLedger.New()));
                    Assert.Contains("in progress", ex.Message, StringComparison.OrdinalIgnoreCase);
                }

                Assert.Equal(before, File.ReadAllBytes(path));
                Assert.Empty(CellsOnDisk(path));
            }
            finally { Sweep(path); }
        }
    }
}
