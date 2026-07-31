using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>
    /// Two writers, one workbook.
    ///
    /// The temp and backup names used to be fixed - "<file>.horizuntmp" and
    /// "<file>.horizunbak" - and nothing stopped two appends running at once. Both
    /// wrote the same temp file; one Replace won; and the loser's backup had already
    /// been overwritten by the winner's copy of a file that was itself mid-write. Each
    /// appender rewrites the WHOLE package from its own snapshot, so the second to
    /// finish silently discards the first's rows and reports rows_written for them.
    ///
    /// Now: unique names, and an exclusive lock that REFUSES the second writer instead
    /// of interleaving with it.
    /// </summary>
    public class ExcelConcurrencyTests
    {
        private static string MakeBook()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz_conc_" + Guid.NewGuid().ToString("N") + ".xlsx");
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
                ("xl/worksheets/sheet1.xml",
                 "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData/></worksheet>"),
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
            ["rows"] = new JArray { new JArray { cell } }
        };

        private static void Sweep(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".horizunlock")) File.Delete(path + ".horizunlock"); } catch { }
            try
            {
                foreach (string p in Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*"))
                    try { File.Delete(p); } catch { }
            }
            catch { }
        }

        [Fact]
        public void A_held_lock_refuses_the_second_writer_instead_of_interleaving()
        {
            string path = MakeBook();
            string lockPath = path + ".horizunlock";
            try
            {
                // Stand in for a write already in progress.
                using (new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var ex = Assert.ThrowsAny<Exception>(() => ExcelWriteRows.Handle(Args(path, "B")));
                    Assert.Contains("in progress", ex.Message, StringComparison.OrdinalIgnoreCase);
                }

                // And nothing was written: the workbook is untouched.
                Assert.False(Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*")
                                      .Any(p => p.EndsWith(".horizunbak", StringComparison.Ordinal)),
                             "A refused write must not leave a backup behind - it never started.");
            }
            finally { Sweep(path); }
        }

        [Fact]
        public void The_lock_is_released_so_the_next_write_is_not_blocked_forever()
        {
            string path = MakeBook();
            try
            {
                ExcelWriteRows.Handle(Args(path, "one"));
                Assert.False(File.Exists(path + ".horizunlock"),
                             "The lock must be released when the write finishes, or the workbook is dead to every later call.");

                // Proven by doing it again rather than by inspecting the flag.
                JObject second = ExcelWriteRows.Handle(Args(path, "two"));
                Assert.Equal(1, (int)second["rows_written"]);
            }
            finally { Sweep(path); }
        }

        [Fact]
        public void Backup_and_temp_names_are_unique_per_call()
        {
            string path = MakeBook();
            try
            {
                JObject a = ExcelWriteRows.Handle(Args(path, "one"));
                JObject b = ExcelWriteRows.Handle(Args(path, "two"));

                string backupA = (string)a["backup_path"];
                string backupB = (string)b["backup_path"];

                Assert.NotEqual(backupA, backupB);
                // The old fixed name would have had the second call overwrite the first
                // call's backup - the copy you reach for when the second write went wrong.
                Assert.NotEqual(path + ".horizunbak", backupA);
                Assert.True(File.Exists(backupA), "The first call's backup must survive the second call.");
                Assert.True(File.Exists(backupB));
            }
            finally { Sweep(path); }
        }

        [Fact]
        public void No_temp_file_is_left_behind()
        {
            string path = MakeBook();
            try
            {
                ExcelWriteRows.Handle(Args(path, "one"));
                Assert.False(Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".*")
                                      .Any(p => p.EndsWith(".horizuntmp", StringComparison.Ordinal)),
                             "The temp file must be cleaned up whichever branch of the replace ran.");
            }
            finally { Sweep(path); }
        }
    }
}
