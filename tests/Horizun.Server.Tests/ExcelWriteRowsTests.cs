// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// Proves horizun_excel_write_rows without Excel installed: the pure XML transform,
// the column math, sheet resolution, and the full Handle round-trip against a
// minimal .xlsx built by hand here. The honesty-critical assertions: a non-.xlsx
// is REFUSED (not corrupted), a backup exists after a write, and every appended
// cell reads back with the value asked — the same re-read the tool does before it
// replaces the original.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class ExcelWriteRowsPureTests
    {
        [Theory]
        [InlineData(0, "A")]
        [InlineData(25, "Z")]
        [InlineData(26, "AA")]
        [InlineData(27, "AB")]
        [InlineData(51, "AZ")]
        [InlineData(701, "ZZ")]
        [InlineData(702, "AAA")]
        public void ColumnLetter_MapsIndexToSpreadsheetColumn(int index, string expected)
            => Assert.Equal(expected, ExcelWriteRows.ColumnLetter(index));

        [Fact]
        public void AppendRows_ContinuesRowNumbering_AfterExistingRows()
        {
            string sheet = SheetXml("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Head</t></is></c></row>" +
                                    "<row r=\"2\"><c r=\"A2\"><v>10</v></c></row>");
            var rows = new List<IList<object>> { new List<object> { "x", 5L } };
            string outXml = ExcelWriteRows.AppendRowsToSheetXml(sheet, rows, out var rep);

            Assert.Equal(1, rep.RowsAppended);
            Assert.Equal(3, rep.FirstNewRow);      // continues after row 2
            Assert.Equal(3, rep.LastNewRow);
            XDocument doc = XDocument.Parse(outXml);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XElement r3 = doc.Descendants(ns + "row").Single(r => (string)r.Attribute("r") == "3");
            Assert.Equal("x", r3.Elements(ns + "c").First().Element(ns + "is").Element(ns + "t").Value);
            Assert.Equal("5", r3.Elements(ns + "c").Last().Element(ns + "v").Value);
        }

        [Fact]
        public void AppendRows_EmptySheetData_StartsAtRow1()
        {
            string sheet = SheetXml("");   // <sheetData/> effectively
            var rows = new List<IList<object>> { new List<object> { "first" } };
            ExcelWriteRows.AppendRowsToSheetXml(sheet, rows, out var rep);
            Assert.Equal(1, rep.FirstNewRow);
        }

        [Fact]
        public void AppendRows_EscapesXmlSpecialCharactersInText()
        {
            string sheet = SheetXml("");
            var rows = new List<IList<object>> { new List<object> { "a & b < c > \"d\"" } };
            string outXml = ExcelWriteRows.AppendRowsToSheetXml(sheet, rows, out _);
            // Round-trips through the parser without breaking, value intact.
            XDocument doc = XDocument.Parse(outXml);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Assert.Equal("a & b < c > \"d\"", doc.Descendants(ns + "t").First().Value);
        }

        [Fact]
        public void AppendRows_NullCell_IsBlank_NotZero()
        {
            string sheet = SheetXml("");
            var rows = new List<IList<object>> { new List<object> { null, 0L } };
            string outXml = ExcelWriteRows.AppendRowsToSheetXml(sheet, rows, out _);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XElement row = XDocument.Parse(outXml).Descendants(ns + "row").First();
            XElement a1 = row.Elements(ns + "c").First(c => (string)c.Attribute("r") == "A1");
            Assert.Empty(a1.Elements());            // blank cell: no <v>, no <is>
            XElement b1 = row.Elements(ns + "c").First(c => (string)c.Attribute("r") == "B1");
            Assert.Equal("0", b1.Element(ns + "v").Value);  // an explicit 0 IS written
        }

        // ----- <dimension>: the rows must be visible to readers that trust it ------
        //
        // Excel writes <dimension ref="..."> and a reader is entitled to believe it.
        // openpyxl in read_only mode — what pandas.read_excel uses — sizes the sheet from
        // this ref and never yields a row beyond it. A stale ref means the appended rows
        // are in the file and invisible to the tools this feeds. Re-reading with our own
        // parser cannot catch it: our parser ignores dimension. Hence these tests.

        [Fact]
        public void AppendRows_ExpandsStaleDimension_ToCoverTheNewRows()
        {
            string xml = SheetXmlWithDimension("A1:B3",
                "<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>" +
                "<row r=\"2\"><c r=\"A2\"><v>2</v></c></row>" +
                "<row r=\"3\"><c r=\"A3\"><v>3</v></c></row>");

            ExcelWriteRows.AppendReport rep;
            string outXml = ExcelWriteRows.AppendRowsToSheetXml(
                xml, new List<IList<object>> { new object[] { 4L, "d", "x" }, new object[] { 5L, "e", "y" } }, out rep);

            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            string reference = (string)XDocument.Parse(outXml).Root.Element(ns + "dimension").Attribute("ref");
            Assert.Equal("A1:C5", reference);   // 3 columns wide now, and down to row 5
        }

        [Fact]
        public void AppendRows_DimensionOnlyGrows_NeverShrinks()
        {
            // The existing ref covers columns the appended rows do not use (up to E).
            // Narrowing it would hide real data that is already in the sheet.
            string xml = SheetXmlWithDimension("A1:E2", "<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>");

            ExcelWriteRows.AppendReport rep;
            string outXml = ExcelWriteRows.AppendRowsToSheetXml(
                xml, new List<IList<object>> { new object[] { "solo A" } }, out rep);

            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            string reference = (string)XDocument.Parse(outXml).Root.Element(ns + "dimension").Attribute("ref");
            Assert.Equal("A1:E2", reference);   // row 2 was already covered, width kept at E
        }

        [Fact]
        public void AppendRows_NoDimensionElement_StaysAbsent()
        {
            // Absent is already honest: a reader then measures the rows themselves.
            // Inventing one here could only be wrong.
            ExcelWriteRows.AppendReport rep;
            string outXml = ExcelWriteRows.AppendRowsToSheetXml(
                SheetXml(""), new List<IList<object>> { new object[] { 1L } }, out rep);

            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Assert.Null(XDocument.Parse(outXml).Root.Element(ns + "dimension"));
        }

        [Theory]
        [InlineData("A", 0)]
        [InlineData("Z", 25)]
        [InlineData("AA", 26)]
        [InlineData("AB", 27)]
        public void ColumnIndex_IsTheInverseOfColumnLetter(string letters, int index)
        {
            Assert.Equal(index, ExcelWriteRows.ColumnIndex(letters));
            Assert.Equal(letters, ExcelWriteRows.ColumnLetter(index));
        }

        private static string SheetXml(string sheetDataInner) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<sheetData>" + sheetDataInner + "</sheetData></worksheet>";

        private static string SheetXmlWithDimension(string dimensionRef, string sheetDataInner) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<dimension ref=\"" + dimensionRef + "\"/>" +
            "<sheetData>" + sheetDataInner + "</sheetData></worksheet>";
    }

    public sealed class ExcelWriteRowsHandleTests
    {
        [Fact]
        public void Handle_AppendsRows_BacksUp_AndVerifiesReadBack()
        {
            string path = MakeMinimalXlsx("Datos", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Header</t></is></c></row>");
            try
            {
                var args = new JObject
                {
                    ["file_path"] = path,
                    ["rows"] = new JArray {
                        new JArray { "código", 12L, true },
                        new JArray { "otro", 3.5 }
                    },
                    ["idempotency_key"] = Guid.NewGuid().ToString("N")
                };
                JObject r = ExcelWriteRows.Handle(args, ExcelTestLedger.New());

                Assert.Equal(2, (int)r["rows_written"]);
                Assert.Equal(2, (int)r["first_new_row"]);   // header was row 1
                Assert.Equal(3, (int)r["last_new_row"]);
                Assert.True((bool)r["verified"]);
                Assert.Equal("Datos", (string)r["sheet"]);
                Assert.True(File.Exists((string)r["backup_path"]));   // honesty: original preserved aside

                // Independently re-open and confirm the cells landed.
                var entries = ExcelWriteRows.ReadEntries(File.ReadAllBytes(path));
                var sheet = ExcelWriteRows.ResolveSheet(entries, "Datos");
                string xml = new UTF8Encoding(false).GetString(entries.First(kv => kv.Key == sheet.PartPath).Value);
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                XElement r2 = XDocument.Parse(xml).Descendants(ns + "row").Single(x => (string)x.Attribute("r") == "2");
                Assert.Equal("código", r2.Elements(ns + "c").First().Element(ns + "is").Element(ns + "t").Value);
            }
            finally { Cleanup(path); }
        }

        [Fact]
        public void Handle_RefusesAFileThatIsNotXlsx_NeverCorruptsIt()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz_notxlsx_" + Guid.NewGuid().ToString("N") + ".xlsx");
            File.WriteAllText(path, "this is plain text, not a zip");
            byte[] before = File.ReadAllBytes(path);
            try
            {
                Assert.ThrowsAny<Exception>(() => ExcelWriteRows.Handle(new JObject
                {
                    ["file_path"] = path,
                    ["rows"] = new JArray { new JArray { "x" } },
                    ["idempotency_key"] = Guid.NewGuid().ToString("N")
                }, ExcelTestLedger.New()));
                Assert.Equal(before, File.ReadAllBytes(path));   // untouched
            }
            finally { Cleanup(path); }
        }

        [Fact]
        public void Handle_UnknownSheetName_IsAnError()
        {
            string path = MakeMinimalXlsx("Datos", "");
            try
            {
                Assert.ThrowsAny<Exception>(() => ExcelWriteRows.Handle(new JObject
                {
                    ["file_path"] = path,
                    ["sheet"] = "NoExiste",
                    ["rows"] = new JArray { new JArray { "x" } },
                    ["idempotency_key"] = Guid.NewGuid().ToString("N")
                }, ExcelTestLedger.New()));
            }
            finally { Cleanup(path); }
        }

        [Fact]
        public void Handle_MissingFile_Throws()
        {
            string path = Path.Combine(Path.GetTempPath(), "hz_absent_" + Guid.NewGuid().ToString("N") + ".xlsx");
            Assert.Throws<FileNotFoundException>(() => ExcelWriteRows.Handle(new JObject
            {
                ["file_path"] = path,
                ["rows"] = new JArray { new JArray { "x" } },
                ["idempotency_key"] = Guid.NewGuid().ToString("N")
            }, ExcelTestLedger.New()));
        }

        // --- a hand-built minimal but valid .xlsx -----------------------------
        private static string MakeMinimalXlsx(string sheetName, string sheetDataInner)
        {
            string path = Path.Combine(Path.GetTempPath(), "hz_xlsx_" + Guid.NewGuid().ToString("N") + ".xlsx");
            var parts = new Dictionary<string, string>
            {
                ["[Content_Types].xml"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "</Types>",
                ["_rels/.rels"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>",
                ["xl/workbook.xml"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"" + sheetName + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
                ["xl/_rels/workbook.xml.rels"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "</Relationships>",
                ["xl/worksheets/sheet1.xml"] =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<sheetData>" + sheetDataInner + "</sheetData></worksheet>",
            };
            using (var fs = new FileStream(path, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                foreach (var kv in parts)
                {
                    var e = zip.CreateEntry(kv.Key, CompressionLevel.Optimal);
                    using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(kv.Value);
                }
            return path;
        }

        private static void Cleanup(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".horizunlock")) File.Delete(path + ".horizunlock"); } catch { }
            // The temp and backup names now carry a pid and a guid, so they cannot be
            // named here - which is the point of them.
            try
            {
                string dir = Path.GetDirectoryName(path);
                string prefix = Path.GetFileName(path) + ".";
                foreach (string p in Directory.GetFiles(dir, prefix + "*"))
                    if (p.EndsWith(".horizunbak", StringComparison.Ordinal) ||
                        p.EndsWith(".horizuntmp", StringComparison.Ordinal))
                        try { File.Delete(p); } catch { }
            }
            catch { }
        }
    }
}
