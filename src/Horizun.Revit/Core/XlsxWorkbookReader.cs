// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE READ SIDE OF XlsxWorkbookWriter: what a written workbook holds, re-read from
// the bytes on disk, and the comparison that decides whether that is what was
// planned.
//
// Small on purpose. It reads what the writer produces - inline strings, numbers,
// the workbook's sheet order through its relationships - plus the three things a
// workbook touched by Excel may carry instead (shared strings, formula text, a
// boolean), so a file that was opened and saved again still compares. Rows are
// read one at a time (XmlReader), because a COBie Component sheet can hold tens of
// thousands of rows and this runs inside Revit.
//
// The comparison is by CANONICAL CELL: kind + exact text (a number is the text of
// its <v>), so nothing is equal by rounding. CellsDigest hashes every cell of every
// sheet in order; a digest can leave one column out by its header text, which is
// how a plan whose CreatedOn is "the export time" can be approved before that time
// exists.
//
// Revit-free: bytes in, sheets out. Tested in Horizun.Core.Tests.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Planned versus re-read: equal or not, and the first differences named.</summary>
    public sealed class XlsxComparison
    {
        public bool Matches;
        public readonly List<string> Differences = new List<string>();
        public string PlannedDigest, ReadDigest;
    }

    public static class XlsxWorkbookReader
    {
        /// <summary>The worksheet limits (Excel's and the OOXML spec's): rows 1..1 048 576, columns A..XFD.</summary>
        public const int MaxRows = 1048576, MaxColumns = 16384;

        private static readonly XNamespace Main = XlsxWorkbookWriter.MainNamespace;
        private static readonly XNamespace Rel = XlsxWorkbookWriter.RelationshipsNamespace;
        private static readonly XNamespace Pkg = XlsxWorkbookWriter.PackageRelationshipsNamespace;

        /// <summary>No part of a workbook this tool writes comes near this; a larger one is not read.</summary>
        public const long MaxPartBytes = 512L * 1024 * 1024;
        private const int MaxDifferencesNamed = 10;

        public static List<XlsxSheet> Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return Read(stream);
        }

        public static List<XlsxSheet> Read(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes, false))
                return Read(ms);
        }

        /// <summary>Every sheet in workbook order. Throws InvalidDataException for what is not a workbook.</summary>
        public static List<XlsxSheet> Read(Stream stream)
        {
            var sheets = new List<XlsxSheet>();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
            {
                XDocument workbook = LoadPart(zip, "xl/workbook.xml")
                    ?? throw new InvalidDataException("xl/workbook.xml is absent: this is not a workbook.");
                XDocument relationships = LoadPart(zip, "xl/_rels/workbook.xml.rels")
                    ?? throw new InvalidDataException("xl/_rels/workbook.xml.rels is absent: the sheets cannot be resolved.");
                var targets = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (XElement r in relationships.Root.Elements(Pkg + "Relationship"))
                {
                    string id = (string)r.Attribute("Id");
                    if (id != null) targets[id] = (string)r.Attribute("Target");
                }
                List<string> shared = SharedStrings(zip);
                XElement sheetList = workbook.Root.Element(Main + "sheets")
                    ?? throw new InvalidDataException("xl/workbook.xml lists no sheets.");
                foreach (XElement s in sheetList.Elements(Main + "sheet"))
                {
                    string name = (string)s.Attribute("name");
                    string id = (string)s.Attribute(Rel + "id");
                    string target;
                    if (id == null || !targets.TryGetValue(id, out target) || string.IsNullOrEmpty(target))
                        throw new InvalidDataException("sheet '" + name + "' names relationship '" + id + "', which xl/_rels/workbook.xml.rels does not hold.");
                    string partName = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target;
                    ZipArchiveEntry entry = zip.GetEntry(partName)
                        ?? throw new InvalidDataException("sheet '" + name + "' points at " + partName + ", which the package does not hold.");
                    var sheet = new XlsxSheet(name);
                    using (Stream part = OpenBounded(entry)) ReadRows(part, shared, sheet);
                    sheets.Add(sheet);
                }
            }
            return sheets;
        }

        private static Stream OpenBounded(ZipArchiveEntry entry)
        {
            if (entry.Length > MaxPartBytes)
                throw new InvalidDataException(entry.FullName + " is " + entry.Length + " bytes uncompressed; parts over " + MaxPartBytes + " are not read.");
            return entry.Open();
        }

        private static XmlReaderSettings Settings() => new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };

        private static XDocument LoadPart(ZipArchive zip, string name)
        {
            ZipArchiveEntry entry = zip.GetEntry(name);
            if (entry == null) return null;
            using (Stream s = OpenBounded(entry))
            using (XmlReader xr = XmlReader.Create(s, Settings()))
                return XDocument.Load(xr);
        }

        private static List<string> SharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            XDocument sst = LoadPart(zip, "xl/sharedStrings.xml");
            if (sst?.Root == null) return list;
            foreach (XElement si in sst.Root.Elements(Main + "si")) list.Add(Decode(RunText(si)));
            return list;
        }

        /// <summary>The text of an &lt;is&gt; or &lt;si&gt;: its &lt;t&gt;, or its runs' &lt;t&gt;; phonetic runs are not text.</summary>
        private static string RunText(XElement container)
        {
            var sb = new StringBuilder();
            foreach (XElement child in container.Elements())
            {
                if (child.Name == Main + "t") sb.Append(child.Value);
                else if (child.Name == Main + "r")
                    foreach (XElement t in child.Elements(Main + "t")) sb.Append(t.Value);
            }
            return sb.ToString();
        }

        private static void ReadRows(Stream part, List<string> shared, XlsxSheet sheet)
        {
            using (XmlReader xr = XmlReader.Create(part, Settings()))
            {
                int lastRow = 0;
                xr.MoveToContent();
                while (!xr.EOF)
                {
                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "row" && xr.NamespaceURI == XlsxWorkbookWriter.MainNamespace)
                    {
                        // ReadFrom leaves the reader on the node AFTER the row: no Read() here, or
                        // the next row would be skipped.
                        var row = (XElement)XNode.ReadFrom(xr);
                        int number = ParseInt((string)row.Attribute("r")) ?? lastRow + 1;
                        // A SHEET'S OWN LIMIT (backlog #47): the gap up to `r` is padded with
                        // empty rows, so an unbounded r="2000000000" exhausted memory.
                        if (number < 1 || number > MaxRows)
                            throw new InvalidDataException("sheet '" + sheet.Name + "' names row " + number + "; a worksheet has rows 1 to " + MaxRows + ".");
                        if (number <= lastRow)
                            throw new InvalidDataException("sheet '" + sheet.Name + "' row " + number + " comes after row " + lastRow + ".");
                        while (sheet.Rows.Count < number - 1) sheet.Rows.Add(new XlsxCell[0]);
                        sheet.Rows.Add(ReadCells(row, shared, sheet.Name, number));
                        lastRow = number;
                    }
                    else xr.Read();
                }
            }
        }

        private static XlsxCell[] ReadCells(XElement row, List<string> shared, string sheetName, int rowNumber)
        {
            var cells = new List<XlsxCell>();
            foreach (XElement c in row.Elements(Main + "c"))
            {
                int column = ColumnIndex((string)c.Attribute("r")) ?? cells.Count;
                if (column < 0 || column >= MaxColumns)
                    throw new InvalidDataException("sheet '" + sheetName + "' row " + rowNumber + ": cell " + (string)c.Attribute("r") +
                                                   " is past the last column a worksheet has (XFD, " + MaxColumns + " columns).");
                if (column < cells.Count)
                    throw new InvalidDataException("sheet '" + sheetName + "' row " + rowNumber + ": cell " + (string)c.Attribute("r") + " is out of order.");
                while (cells.Count < column) cells.Add(XlsxCell.Empty);
                cells.Add(CellOf(c, shared));
            }
            return cells.ToArray();
        }

        private static XlsxCell CellOf(XElement c, List<string> shared)
        {
            string type = (string)c.Attribute("t") ?? "n";
            string raw = (string)c.Element(Main + "v");
            switch (type)
            {
                case "inlineStr":
                {
                    XElement inline = c.Element(Main + "is");
                    return inline == null ? XlsxCell.Empty : XlsxCell.Text(Decode(RunText(inline)));
                }
                case "s":
                {
                    int? index = ParseInt(raw);
                    if (index == null || index.Value < 0 || index.Value >= shared.Count)
                        throw new InvalidDataException("a shared-string cell names index '" + raw + "', which the workbook does not hold.");
                    return XlsxCell.Text(shared[index.Value]);
                }
                case "str":
                case "b":
                case "e":
                    return XlsxCell.Text(raw);
                default:
                    return XlsxCell.RawNumber(raw == null ? null : raw.Trim());
            }
        }

        /// <summary>Undo Excel's ST_Xstring escape: _xHHHH_ is the character HHHH.</summary>
        public static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf("_x", StringComparison.Ordinal) < 0) return value;
            var sb = new StringBuilder(value.Length);
            int i = 0;
            while (i < value.Length)
            {
                if (XlsxWorkbookWriter.LooksLikeEscape(value, i))
                {
                    sb.Append((char)int.Parse(value.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 7;
                }
                else sb.Append(value[i++]);
            }
            return sb.ToString();
        }

        /// <summary>"AB12" -> 27 (0-based column); null when there is no reference.</summary>
        public static int? ColumnIndex(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            int index = 0, letters = 0;
            foreach (char ch in reference)
            {
                char c = char.ToUpperInvariant(ch);
                if (c < 'A' || c > 'Z') break;
                // Clamped past the last column, so a long run of letters cannot wrap an int
                // round to a small, plausible column.
                index = Math.Min(index * 26 + (c - 'A' + 1), MaxColumns + 1);
                letters++;
            }
            return letters == 0 ? (int?)null : index - 1;
        }

        private static int? ParseInt(string raw)
        {
            int value;
            return raw != null && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : (int?)null;
        }

        // ---- comparison ----------------------------------------------------------------

        /// <summary>
        /// SHA-256 (hex) over every sheet's name and every cell in canonical form, rows padded
        /// to the sheet's widest row. A column whose HEADER (row 0) equals
        /// <paramref name="excludedHeader"/> is left out on every row, header included.
        /// </summary>
        public static string CellsDigest(IList<XlsxSheet> sheets, string excludedHeader = null)
        {
            var sb = new StringBuilder();
            foreach (XlsxSheet sheet in sheets ?? new List<XlsxSheet>())
            {
                sb.Append('S').Append('\u001f').Append(sheet.Name).Append('\u001e');
                int width = Width(sheet);
                var excluded = new HashSet<int>();
                if (excludedHeader != null && sheet.Rows.Count > 0)
                    for (int c = 0; c < width; c++)
                    {
                        XlsxCell h = sheet.Cell(0, c);
                        if (h.Kind == XlsxCellKind.Text && h.Value == excludedHeader) excluded.Add(c);
                    }
                for (int r = 0; r < sheet.Rows.Count; r++)
                {
                    sb.Append('R');
                    for (int c = 0; c < width; c++)
                        sb.Append('\u001f').Append(excluded.Contains(c) ? "x" : sheet.Cell(r, c).Canonical);
                    sb.Append('\u001e');
                }
            }
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static int Width(XlsxSheet sheet)
        {
            int width = 0;
            foreach (XlsxCell[] row in sheet.Rows)
            {
                if (row == null) continue;
                for (int c = row.Length - 1; c >= 0; c--)
                    if (row[c].Kind != XlsxCellKind.Empty) { if (c + 1 > width) width = c + 1; break; }
            }
            return width;
        }

        /// <summary>
        /// Sheet names and order, row counts per sheet, and every cell: all equal, or the first
        /// differences named by sheet and cell reference.
        /// </summary>
        public static XlsxComparison Compare(IList<XlsxSheet> planned, IList<XlsxSheet> read)
        {
            var result = new XlsxComparison { PlannedDigest = CellsDigest(planned), ReadDigest = CellsDigest(read) };
            planned = planned ?? new List<XlsxSheet>();
            read = read ?? new List<XlsxSheet>();
            string plannedNames = string.Join(", ", planned.Select(s => s.Name));
            string readNames = string.Join(", ", read.Select(s => s.Name));
            if (plannedNames != readNames)
                result.Differences.Add("sheets: planned [" + plannedNames + "], read [" + readNames + "]");
            for (int i = 0; i < Math.Min(planned.Count, read.Count) && result.Differences.Count < MaxDifferencesNamed; i++)
            {
                XlsxSheet p = planned[i], q = read[i];
                if (p.Rows.Count != q.Rows.Count)
                    result.Differences.Add(p.Name + ": planned " + p.Rows.Count + " rows, read " + q.Rows.Count);
                int width = Math.Max(Width(p), Width(q));
                for (int r = 0; r < Math.Max(p.Rows.Count, q.Rows.Count) && result.Differences.Count < MaxDifferencesNamed; r++)
                    for (int c = 0; c < width && result.Differences.Count < MaxDifferencesNamed; c++)
                    {
                        string a = p.Cell(r, c).Canonical, b = q.Cell(r, c).Canonical;
                        if (a != b)
                            result.Differences.Add(p.Name + "!" + XlsxWorkbookWriter.ColumnName(c) + (r + 1).ToString(CultureInfo.InvariantCulture) +
                                                   ": planned " + Show(a) + ", read " + Show(b));
                    }
            }
            result.Matches = result.Differences.Count == 0 && result.PlannedDigest == result.ReadDigest;
            if (!result.Matches && result.Differences.Count == 0)
                result.Differences.Add("the cell digests differ (" + result.PlannedDigest + " planned, " + result.ReadDigest + " read)");
            return result;
        }

        private static string Show(string canonical)
        {
            if (string.IsNullOrEmpty(canonical)) return "(empty)";
            string kind = canonical[0] == 'n' ? "number " : "text ";
            string value = canonical.Substring(1);
            if (value.Length > 60) value = value.Substring(0, 60) + "...";
            return kind + "'" + value + "'";
        }
    }
}
