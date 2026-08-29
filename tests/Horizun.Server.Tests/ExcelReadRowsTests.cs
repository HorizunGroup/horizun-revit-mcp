// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The workbook reader: types preserved, formulas declared, merges named,
// corruption refused whole. The fixtures are real xlsx bytes authored through
// the same writer the product ships, plus hand-built parts for the shapes the
// writer never produces (shared strings, formulas, merges).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ExcelReadRowsTests : IDisposable
    {
        private readonly string _dir;

        public ExcelReadRowsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-xlsxread-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Book(string sheetXml, string sharedXml = null)
        {
            string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".xlsx");
            using (FileStream stream = File.Create(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "</Types>");
                Add(zip, "_rels/.rels",
                    "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Add(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                    "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"Datos\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Add(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                Add(zip, "xl/worksheets/sheet1.xml", sheetXml);
                if (sharedXml != null) Add(zip, "xl/sharedStrings.xml", sharedXml);
            }
            return path;
        }

        private static void Add(ZipArchive zip, string name, string content)
        {
            using (Stream stream = zip.CreateEntry(name).Open())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private const string Ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";

        [Fact]
        public void Types_come_back_as_themselves_and_dates_are_not_invented()
        {
            string book = Book(
                "<?xml version=\"1.0\"?><worksheet " + Ns + "><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\"><v>12.5</v></c>" +
                "<c r=\"C1\" t=\"b\"><v>1</v></c><c r=\"D1\" t=\"inlineStr\"><is><t>Tubería Ø110</t></is></c></row>" +
                "<row r=\"2\"><c r=\"A2\"><v>45930</v></c></row>" +   // an Excel date-as-number
                "</sheetData></worksheet>",
                "<?xml version=\"1.0\"?><sst " + Ns + " count=\"1\"><si><t>hola</t></si></sst>");
            JObject result = ExcelReadRows.Handle(new JObject { ["file_path"] = book });
            var row = (JArray)((JArray)result["rows"])[0];
            Assert.Equal("hola", (string)row[0]);
            Assert.Equal(12.5, (double)row[1]);
            Assert.True((bool)row[2]);
            Assert.Equal("Tubería Ø110", (string)row[3]);
            Assert.Equal(45930.0, (double)((JArray)((JArray)result["rows"])[1])[0]);   // a number, not a date
            Assert.Contains("no date is invented", string.Join(" ", result["notes"].Select(n => (string)n)));
        }

        [Fact]
        public void Formulas_declare_themselves_and_a_missing_cache_is_counted()
        {
            string book = Book(
                "<?xml version=\"1.0\"?><worksheet " + Ns + "><sheetData>" +
                "<row r=\"1\"><c r=\"A1\"><f>1+1</f><v>2</v></c><c r=\"B1\"><f>NOW()</f></c></row>" +
                "</sheetData></worksheet>");
            JObject result = ExcelReadRows.Handle(new JObject { ["file_path"] = book });
            var row = (JArray)((JArray)result["rows"])[0];
            Assert.True((bool)row[0]["formula"]);
            Assert.Equal(2.0, (double)row[0]["value"]);
            Assert.True((bool)row[1]["formula"]);
            Assert.Equal(JTokenType.Null, row[1]["value"].Type);
            Assert.Equal(1, (int)result["formulas_without_cached_value"]);
        }

        [Fact]
        public void Merged_ranges_are_declared_and_gaps_are_null()
        {
            string book = Book(
                "<?xml version=\"1.0\"?><worksheet " + Ns + "><sheetData>" +
                "<row r=\"1\"><c r=\"A1\"><v>1</v></c><c r=\"C1\"><v>3</v></c></row>" +
                "</sheetData><mergeCells count=\"1\"><mergeCell ref=\"A1:B1\"/></mergeCells></worksheet>");
            JObject result = ExcelReadRows.Handle(new JObject { ["file_path"] = book });
            var row = (JArray)((JArray)result["rows"])[0];
            Assert.Equal(JTokenType.Null, row[1].Type);          // the covered cell
            Assert.Equal(3.0, (double)row[2]);                   // C kept its column
            Assert.Equal("A1:B1", (string)((JArray)result["merged_ranges"])[0]);
        }

        [Fact]
        public void Corruption_refuses_whole_and_the_hash_is_of_the_file()
        {
            string garbage = Path.Combine(_dir, "not.xlsx");
            File.WriteAllText(garbage, "this is not a workbook");
            Assert.Throws<InvalidDataException>(() => ExcelReadRows.Handle(new JObject { ["file_path"] = garbage }));

            string book = Book("<?xml version=\"1.0\"?><worksheet " + Ns + "><sheetData/></worksheet>");
            JObject result = ExcelReadRows.Handle(new JObject { ["file_path"] = book });
            Assert.Equal(64, ((string)result["sha256"]).Length);
            Assert.Equal(0, (int)result["row_count"]);
        }

        [Fact]
        public void The_round_trip_reads_what_the_shipping_writer_wrote()
        {
            // author with the REAL writer, read with the REAL reader
            string book = Book("<?xml version=\"1.0\"?><worksheet " + Ns + "><sheetData/></worksheet>");
            var ledger = ExcelTestLedger.New();
            ExcelWriteRows.Handle(new JObject
            {
                ["file_path"] = book,
                ["rows"] = new JArray { new JArray("id", 7, true) },
                ["idempotency_key"] = "read-roundtrip-1"
            }, ledger);
            JObject result = ExcelReadRows.Handle(new JObject { ["file_path"] = book });
            var row = (JArray)((JArray)result["rows"])[0];
            Assert.Equal("id", (string)row[0]);
            Assert.Equal(7.0, (double)row[1]);
            Assert.True((bool)row[2]);
        }
    }
}
