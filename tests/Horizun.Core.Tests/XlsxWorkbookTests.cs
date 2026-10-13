// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The dependency-free .xlsx writer and its reader: the OPC package has exactly the
// ECMA-376 parts, content types and relationships Excel needs; text that XML cannot
// carry as-is (markup characters, CR, control characters, a literal _xHHHH_) comes
// back exactly; numbers come back as the text that was written; and the comparison
// the export's verification relies on names the cell that differs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class XlsxWorkbookTests
    {
        private static readonly XNamespace Main = XlsxWorkbookWriter.MainNamespace;

        private static XlsxSheet Sheet(string name, params XlsxCell[][] rows)
        {
            var s = new XlsxSheet(name);
            s.Rows.AddRange(rows);
            return s;
        }

        private static XlsxCell[] Row(params object[] values) =>
            values.Select(v => v == null ? XlsxCell.Empty : v is double d ? XlsxCell.Number(d) : v is int i ? XlsxCell.Number(i) : XlsxCell.Text((string)v)).ToArray();

        private static readonly string[] Awkward =
        {
            "a & b < c > d \"quoted\" 'single'",
            "line one\r\nline two\rline three\nend",
            "\ttabbed\t",
            "  leading and trailing  ",
            "control \u0001 \u0008 \u000B \u001F here",
            "literal _x0041_ must stay literal",
            "already escaped _x005F_ too",
            "__x0041_ double underscore",
            "_x12_ short, _xZZZZ_ not hex, _x0041 unterminated",
            "unicode ñ ü 中文 and an emoji \U0001F600",
            "lone high \uD800 and lone low \uDC00 surrogates",
            "non-characters ￾ ￿"
        };

        [Fact]
        public void Text_that_xml_cannot_carry_as_is_reads_back_exactly()
        {
            var sheet = Sheet("Text", Row("Value"));
            foreach (string s in Awkward) sheet.Rows.Add(Row(s));
            List<XlsxSheet> back = XlsxWorkbookReader.Read(XlsxWorkbookWriter.ToBytes(new[] { sheet }));
            Assert.Single(back);
            for (int i = 0; i < Awkward.Length; i++)
            {
                XlsxCell cell = back[0].Cell(i + 1, 0);
                Assert.Equal(XlsxCellKind.Text, cell.Kind);
                Assert.Equal(Awkward[i], cell.Value);
            }
        }

        [Fact]
        public void Escaping_uses_excel_own_forms_for_what_xml_cannot_hold()
        {
            Assert.Equal("_x0001_", XlsxWorkbookWriter.EscapeText("\u0001"));
            Assert.Equal("_x005F_x0041_", XlsxWorkbookWriter.EscapeText("_x0041_"));
            Assert.Equal("a &amp; b &lt; c &gt;", XlsxWorkbookWriter.EscapeText("a & b < c >"));
            Assert.Equal("x&#xD;\ny", XlsxWorkbookWriter.EscapeText("x\r\ny"));
            Assert.Equal("_xD800_", XlsxWorkbookWriter.EscapeText("\uD800"));
            Assert.Equal("\U0001F600", XlsxWorkbookWriter.EscapeText("\U0001F600"));
            Assert.Equal("A", XlsxWorkbookReader.Decode("_x0041_"));
            Assert.Equal("_x0041_", XlsxWorkbookReader.Decode("_x005F_x0041_"));
            Assert.Equal("_x004_", XlsxWorkbookReader.Decode("_x004_"));
        }

        [Fact]
        public void Numbers_and_empty_cells_keep_their_places()
        {
            var sheet = Sheet("Numbers",
                Row("A", "B", "C", "D", "E"),
                Row(1.0, null, -2.5, "text", null),
                Row(null, 0.0001, 123456789.125, null, 1e21),
                Row(-0.0, null, null, null, "last"));
            List<XlsxSheet> back = XlsxWorkbookReader.Read(XlsxWorkbookWriter.ToBytes(new[] { sheet }));
            XlsxSheet s = back[0];
            Assert.Equal(4, s.Rows.Count);
            Assert.Equal("1", s.Cell(1, 0).Value);
            Assert.Equal(XlsxCellKind.Empty, s.Cell(1, 1).Kind);
            Assert.Equal("-2.5", s.Cell(1, 2).Value);
            Assert.Equal(XlsxCellKind.Number, s.Cell(1, 2).Kind);
            Assert.Equal("text", s.Cell(1, 3).Value);
            Assert.Equal(XlsxCellKind.Empty, s.Cell(1, 4).Kind);
            Assert.Equal(XlsxCellKind.Empty, s.Cell(2, 0).Kind);
            Assert.Equal("0.0001", s.Cell(2, 1).Value);
            Assert.Equal("123456789.125", s.Cell(2, 2).Value);
            Assert.Equal(XlsxWorkbookWriter.FormatNumber(1e21), s.Cell(2, 4).Value);
            Assert.Equal("0", s.Cell(3, 0).Value);       // negative zero is written as 0
            Assert.Equal("last", s.Cell(3, 4).Value);
            Assert.True(XlsxWorkbookReader.Compare(new[] { sheet }, back).Matches);
        }

        [Fact]
        public void A_number_must_be_finite()
        {
            Assert.Throws<ArgumentException>(() => XlsxCell.Number(double.NaN));
            Assert.Throws<ArgumentException>(() => XlsxCell.Number(double.PositiveInfinity));
            Assert.Equal("0", XlsxWorkbookWriter.FormatNumber(-0.0));
        }

        [Fact]
        public void The_package_has_the_ecma_376_parts_types_and_relationships()
        {
            var sheets = new[] { Sheet("First", Row("H1", "H2"), Row("a", 1.0)), Sheet("Second", Row("H")) };
            byte[] bytes = XlsxWorkbookWriter.ToBytes(sheets);
            using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            {
                var names = zip.Entries.Select(e => e.FullName).ToList();
                Assert.Equal(new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml",
                                     "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml" }, names);
                foreach (ZipArchiveEntry e in zip.Entries)
                    using (Stream s = e.Open()) XDocument.Load(s); // every part is well-formed XML

                XDocument types = Load(zip, "[Content_Types].xml");
                XNamespace ct = XlsxWorkbookWriter.ContentTypesNamespace;
                Assert.Contains(types.Root.Elements(ct + "Default"), d => (string)d.Attribute("Extension") == "rels" &&
                    (string)d.Attribute("ContentType") == "application/vnd.openxmlformats-package.relationships+xml");
                Assert.Contains(types.Root.Elements(ct + "Default"), d => (string)d.Attribute("Extension") == "xml" &&
                    (string)d.Attribute("ContentType") == "application/xml");
                var overrides = types.Root.Elements(ct + "Override").ToDictionary(o => (string)o.Attribute("PartName"), o => (string)o.Attribute("ContentType"));
                Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml", overrides["/xl/workbook.xml"]);
                Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml", overrides["/xl/styles.xml"]);
                Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml", overrides["/xl/worksheets/sheet1.xml"]);
                Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml", overrides["/xl/worksheets/sheet2.xml"]);

                XNamespace pr = XlsxWorkbookWriter.PackageRelationshipsNamespace;
                XElement root = Load(zip, "_rels/.rels").Root.Elements(pr + "Relationship").Single();
                Assert.Equal("http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", (string)root.Attribute("Type"));
                Assert.Equal("xl/workbook.xml", (string)root.Attribute("Target"));

                XDocument workbook = Load(zip, "xl/workbook.xml");
                XNamespace r = XlsxWorkbookWriter.RelationshipsNamespace;
                var sheetElements = workbook.Root.Element(Main + "sheets").Elements(Main + "sheet").ToList();
                Assert.Equal(new[] { "First", "Second" }, sheetElements.Select(s => (string)s.Attribute("name")));
                Assert.Equal(new[] { "1", "2" }, sheetElements.Select(s => (string)s.Attribute("sheetId")));
                var rels = Load(zip, "xl/_rels/workbook.xml.rels").Root.Elements(pr + "Relationship")
                    .ToDictionary(x => (string)x.Attribute("Id"), x => x);
                foreach (XElement s in sheetElements)
                {
                    XElement rel = rels[(string)s.Attribute(r + "id")];
                    Assert.Equal("http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", (string)rel.Attribute("Type"));
                }
                Assert.Contains(rels.Values, x => (string)x.Attribute("Type") == "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" &&
                                                  (string)x.Attribute("Target") == "styles.xml");

                // Excel insists on the two default fills; cell format 1 is the bold header.
                XDocument styles = Load(zip, "xl/styles.xml");
                var fills = styles.Root.Element(Main + "fills").Elements(Main + "fill").Select(f => (string)f.Element(Main + "patternFill").Attribute("patternType")).ToList();
                Assert.Equal(new[] { "none", "gray125" }, fills);
                Assert.Equal("1", (string)styles.Root.Element(Main + "cellXfs").Elements(Main + "xf").ElementAt(1).Attribute("fontId"));

                XDocument sheet1 = Load(zip, "xl/worksheets/sheet1.xml");
                Assert.Equal("A1:B2", (string)sheet1.Root.Element(Main + "dimension").Attribute("ref"));
                XElement header = sheet1.Root.Element(Main + "sheetData").Elements(Main + "row").First().Elements(Main + "c").First();
                Assert.Equal("1", (string)header.Attribute("s"));
                Assert.Equal("inlineStr", (string)header.Attribute("t"));
                Assert.Equal("frozen", (string)sheet1.Root.Element(Main + "sheetViews").Element(Main + "sheetView").Element(Main + "pane").Attribute("state"));
                XElement number = sheet1.Root.Element(Main + "sheetData").Elements(Main + "row").ElementAt(1).Elements(Main + "c").ElementAt(1);
                Assert.Null(number.Attribute("t"));
                Assert.Equal("1", (string)number.Element(Main + "v"));
            }
        }

        private static XDocument Load(ZipArchive zip, string name)
        {
            using (Stream s = zip.GetEntry(name).Open()) return XDocument.Load(s);
        }

        [Fact]
        public void The_same_sheets_give_the_same_bytes()
        {
            var sheets = new[] { Sheet("S", Row("a", 1.5), Row("b", 2.0)) };
            Assert.Equal(XlsxWorkbookWriter.ToBytes(sheets), XlsxWorkbookWriter.ToBytes(sheets));
        }

        [Fact]
        public void Validation_names_what_excel_would_refuse_or_alter()
        {
            Assert.Contains("at least one sheet", string.Join("|", XlsxWorkbookWriter.Validate(new List<XlsxSheet>())));
            List<string> problems = XlsxWorkbookWriter.Validate(new[]
            {
                Sheet(new string('x', 32)), Sheet("a/b"), Sheet("Same"), Sheet("same"), Sheet("'quoted'"),
                Sheet("Long", Row(new string('y', XlsxWorkbookWriter.MaxCellCharacters + 1)))
            });
            string all = string.Join("|", problems);
            Assert.Contains("longer than 31", all);
            Assert.Contains("'a/b' contains", all);
            Assert.Contains("'same' is used twice", all);
            Assert.Contains("apostrophe", all);
            Assert.Contains("cell A1 holds 32768 characters", all);
            Assert.Throws<ArgumentException>(() => XlsxWorkbookWriter.ToBytes(new[] { Sheet("a/b") }));
        }

        [Fact]
        public void Column_names_follow_excel()
        {
            Assert.Equal("A", XlsxWorkbookWriter.ColumnName(0));
            Assert.Equal("Z", XlsxWorkbookWriter.ColumnName(25));
            Assert.Equal("AA", XlsxWorkbookWriter.ColumnName(26));
            Assert.Equal("ZZ", XlsxWorkbookWriter.ColumnName(701));
            Assert.Equal("AAA", XlsxWorkbookWriter.ColumnName(702));
            Assert.Equal(27, XlsxWorkbookReader.ColumnIndex("AB12"));
            Assert.Null(XlsxWorkbookReader.ColumnIndex("12"));
        }

        [Fact]
        public void The_comparison_names_the_cell_that_differs_and_the_digest_can_skip_a_column()
        {
            var planned = new[] { Sheet("Space", Row("Name", "CreatedOn", "Area"), Row("101", "2026-09-27T10:00:00", 12.5)) };
            var same = new[] { Sheet("Space", Row("Name", "CreatedOn", "Area"), Row("101", "2026-09-27T10:00:00", 12.5)) };
            var other = new[] { Sheet("Space", Row("Name", "CreatedOn", "Area"), Row("10l", "2026-09-27T10:00:00", 12.5)) };
            var later = new[] { Sheet("Space", Row("Name", "CreatedOn", "Area"), Row("101", "2026-09-28T09:00:00", 12.5)) };
            var textArea = new[] { Sheet("Space", Row("Name", "CreatedOn", "Area"), Row("101", "2026-09-27T10:00:00", "12.5")) };

            Assert.True(XlsxWorkbookReader.Compare(planned, same).Matches);
            XlsxComparison diff = XlsxWorkbookReader.Compare(planned, other);
            Assert.False(diff.Matches);
            Assert.Contains("Space!A2: planned text '101', read text '10l'", diff.Differences);
            // A number and the same digits as text are different cells.
            Assert.Contains(XlsxWorkbookReader.Compare(planned, textArea).Differences, d => d.StartsWith("Space!C2", StringComparison.Ordinal));
            // Excluding CreatedOn by its header: the export time can move, nothing else.
            Assert.Equal(XlsxWorkbookReader.CellsDigest(planned, "CreatedOn"), XlsxWorkbookReader.CellsDigest(later, "CreatedOn"));
            Assert.NotEqual(XlsxWorkbookReader.CellsDigest(planned), XlsxWorkbookReader.CellsDigest(later));
            Assert.NotEqual(XlsxWorkbookReader.CellsDigest(planned, "CreatedOn"), XlsxWorkbookReader.CellsDigest(other, "CreatedOn"));

            var renamed = new[] { Sheet("Spaces", Row("Name", "CreatedOn", "Area"), Row("101", "2026-09-27T10:00:00", 12.5)) };
            Assert.Contains(XlsxWorkbookReader.Compare(planned, renamed).Differences, d => d.StartsWith("sheets:", StringComparison.Ordinal));
            var shorter = new[] { Sheet("Space", Row("Name", "CreatedOn", "Area")) };
            Assert.Contains("Space: planned 2 rows, read 1", XlsxWorkbookReader.Compare(planned, shorter).Differences);
        }

        [Fact]
        public void The_reader_takes_shared_strings_and_rich_text_runs_from_a_workbook_excel_saved()
        {
            // A package shaped the way Excel saves one: shared strings, a run-split string,
            // an absolute relationship target and a cell without a reference.
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    Put(zip, "xl/workbook.xml", "<workbook xmlns=\"" + Main.NamespaceName + "\" xmlns:r=\"" + XlsxWorkbookWriter.RelationshipsNamespace +
                        "\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId7\"/></sheets></workbook>");
                    Put(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"" + XlsxWorkbookWriter.PackageRelationshipsNamespace +
                        "\"><Relationship Id=\"rId7\" Type=\"" + XlsxWorkbookWriter.WorksheetRelationship + "\" Target=\"/xl/worksheets/data.xml\"/></Relationships>");
                    Put(zip, "xl/sharedStrings.xml", "<sst xmlns=\"" + Main.NamespaceName + "\"><si><t>shared</t></si>" +
                        "<si><r><t>rich </t></r><r><t>text</t></r><rPh><t>phonetic</t></rPh></si></sst>");
                    Put(zip, "xl/worksheets/data.xml", "<worksheet xmlns=\"" + Main.NamespaceName + "\"><sheetData>" +
                        "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c t=\"s\"><v>1</v></c></row>" +
                        "<row r=\"3\"><c r=\"C3\"><v>42</v></c></row></sheetData></worksheet>");
                }
                bytes = ms.ToArray();
            }
            XlsxSheet s = XlsxWorkbookReader.Read(bytes).Single();
            Assert.Equal("S", s.Name);
            Assert.Equal(3, s.Rows.Count);
            Assert.Equal("shared", s.Cell(0, 0).Value);
            Assert.Equal("rich text", s.Cell(0, 1).Value);
            Assert.Empty(s.Rows[1]);
            Assert.Equal("42", s.Cell(2, 2).Value);
            Assert.Equal(XlsxCellKind.Number, s.Cell(2, 2).Kind);
        }

        [Fact]
        public void What_is_not_a_workbook_is_refused_whole()
        {
            Assert.ThrowsAny<InvalidDataException>(() => XlsxWorkbookReader.Read(Encoding.UTF8.GetBytes("not a zip at all")));
            byte[] noWorkbook;
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true)) Put(zip, "hello.txt", "<x/>");
                noWorkbook = ms.ToArray();
            }
            var ex = Assert.Throws<InvalidDataException>(() => XlsxWorkbookReader.Read(noWorkbook));
            Assert.Contains("xl/workbook.xml is absent", ex.Message);
        }

        // Backlog #47: the row attribute padded Rows without a bound, so r="2000000000"
        // exhausted memory. Rows and columns past the sheet's own limits (1 048 576 rows,
        // 16 384 columns, XFD) are refused instead. Just past the limit keeps the red cheap.
        [Theory]
        [InlineData("<row r=\"1048577\"><c r=\"A1048577\"><v>1</v></c></row>", "row")]
        [InlineData("<row r=\"1\"><c r=\"XFE1\"><v>1</v></c></row>", "column")]
        public void A_row_or_column_past_the_sheet_limits_is_refused(string rowXml, string what)
        {
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    Put(zip, "xl/workbook.xml", "<workbook xmlns=\"" + Main.NamespaceName + "\" xmlns:r=\"" + XlsxWorkbookWriter.RelationshipsNamespace +
                        "\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                    Put(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"" + XlsxWorkbookWriter.PackageRelationshipsNamespace +
                        "\"><Relationship Id=\"rId1\" Type=\"" + XlsxWorkbookWriter.WorksheetRelationship + "\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                    Put(zip, "xl/worksheets/sheet1.xml", "<worksheet xmlns=\"" + Main.NamespaceName + "\"><sheetData>" + rowXml + "</sheetData></worksheet>");
                }
                bytes = ms.ToArray();
            }
            var ex = Assert.Throws<InvalidDataException>(() => XlsxWorkbookReader.Read(bytes));
            Assert.Contains(what, ex.Message);
        }

        private static void Put(ZipArchive zip, string name, string xml)
        {
            using (var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) w.Write(xml);
        }

        [Fact]
        public void A_file_is_written_new_and_never_over_an_existing_one()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-xlsx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "book.xlsx");
                var sheets = new[] { Sheet("S", Row("H"), Row("v")) };
                XlsxWorkbookWriter.WriteFile(path, sheets);
                Assert.True(XlsxWorkbookReader.Compare(sheets, XlsxWorkbookReader.Read(path)).Matches);
                Assert.Throws<IOException>(() => XlsxWorkbookWriter.WriteFile(path, sheets));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
