// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_excel_read_rows - the READ side of the workbook contract, built on
// the same minimal OPC reader the writer uses; no new dependency. What it
// holds:
//
//   * TYPES ARE PRESERVED, NOT GUESSED. A number cell comes back as a number,
//     a boolean as a boolean, shared and inline strings as strings. A date is
//     NOT invented: Excel stores dates as numbers with a format, and format
//     archaeology is guesswork - the number comes back with a note, never a
//     fabricated timestamp.
//   * FORMULAS ARE DECLARED. A formula cell returns its CACHED value with
//     formula=true; a formula with no cached value is reported, not zeroed.
//   * MERGED CELLS ARE DECLARED, because the value lives in the anchor and
//     the covered cells read empty - a reader who does not know that will
//     mis-join columns.
//   * THE FILE IS HASHED, so a caller chaining read -> decide -> write can
//     prove the file it decided over is the file it acted on.
//   * A file that is not a valid workbook REFUSES; it is never half-parsed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ExcelReadRows
    {
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        public const int MaxRows = 10000, MaxColumns = 256;

        internal static JObject Handle(JObject args)
        {
            string filePath = (string)args?["file_path"];
            if (string.IsNullOrEmpty(filePath)) throw new ArgumentException("file_path is required.");
            if (!File.Exists(filePath)) throw new FileNotFoundException("Workbook not found: " + filePath);
            int maxRows = (int?)args?["max_rows"] ?? 1000;
            if (maxRows < 1 || maxRows > MaxRows)
                throw new ArgumentException("max_rows must be 1.." + MaxRows + ".");

            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
                throw new InvalidDataException("Not an .xlsx (OPC/zip) file - refusing to half-parse. " +
                    "First bytes are not a zip signature.");
            string sha;
            using (var hasher = SHA256.Create())
                sha = BitConverter.ToString(hasher.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();

            List<KeyValuePair<string, byte[]>> entries;
            try { entries = ExcelWriteRows.ReadEntries(bytes); }
            catch (Exception ex) { throw new InvalidDataException("File is not a readable .xlsx package: " + ex.Message); }
            if (!entries.Any(kv => kv.Key == "xl/workbook.xml"))
                throw new InvalidDataException("Not an .xlsx workbook - xl/workbook.xml is absent.");

            ExcelWriteRows.ResolvedSheet sheet = ExcelWriteRows.ResolveSheet(entries, (string)args?["sheet"]);

            // Shared strings, when the workbook carries them.
            var shared = new List<string>();
            KeyValuePair<string, byte[]> sharedPart = entries.FirstOrDefault(kv => kv.Key == "xl/sharedStrings.xml");
            if (sharedPart.Value != null)
            {
                XDocument sharedXml = XDocument.Load(new MemoryStream(sharedPart.Value));
                foreach (XElement si in sharedXml.Root.Elements(Main + "si"))
                    shared.Add(string.Concat(si.Descendants(Main + "t").Select(t => (string)t)));
            }

            byte[] sheetBytes = entries.First(kv => kv.Key == sheet.PartPath).Value;
            XDocument sheetXml = XDocument.Load(new MemoryStream(sheetBytes));
            XElement sheetData = sheetXml.Root.Element(Main + "sheetData")
                ?? throw new InvalidDataException("worksheet part has no sheetData element.");

            var merged = new JArray();
            XElement mergeCells = sheetXml.Root.Element(Main + "mergeCells");
            if (mergeCells != null)
                foreach (XElement merge in mergeCells.Elements(Main + "mergeCell"))
                    merged.Add((string)merge.Attribute("ref"));

            var rows = new JArray();
            int formulasWithoutCache = 0, truncatedRows = 0, widestRow = 0;
            foreach (XElement rowElement in sheetData.Elements(Main + "row"))
            {
                if (rows.Count >= maxRows) { truncatedRows++; continue; }
                var row = new JArray();
                foreach (XElement cell in rowElement.Elements(Main + "c"))
                {
                    int columnIndex = ColumnIndexOf((string)cell.Attribute("r"));
                    if (columnIndex >= MaxColumns) continue;
                    while (row.Count < columnIndex) row.Add(JValue.CreateNull());
                    row.Add(CellValue(cell, shared, ref formulasWithoutCache));
                }
                if (row.Count > widestRow) widestRow = row.Count;
                rows.Add(row);
            }

            return new JObject
            {
                ["file_path"] = filePath,
                ["sha256"] = sha,
                ["sheet"] = sheet.Name,
                ["sheets"] = new JArray(sheet.SheetNames),
                ["rows"] = rows,
                ["row_count"] = rows.Count,
                ["widest_row"] = widestRow,
                ["rows_truncated"] = truncatedRows,
                ["merged_ranges"] = merged,
                ["formulas_without_cached_value"] = formulasWithoutCache,
                ["notes"] = new JArray(new[]
                {
                    "numbers are the stored doubles; Excel DATES are numbers with a display format, and no " +
                    "date is invented from them - interpret date columns yourself, knowingly.",
                    merged.Count == 0 ? null : "merged ranges hold their value at the ANCHOR cell; covered " +
                        "cells read null.",
                    formulasWithoutCache == 0 ? null : formulasWithoutCache + " formula cell(s) carry no cached " +
                        "value and were reported null with formula=true."
                }.Where(n => n != null))
            };
        }

        private static JToken CellValue(XElement cell, List<string> shared, ref int formulasWithoutCache)
        {
            string type = (string)cell.Attribute("t") ?? "n";
            XElement valueElement = cell.Element(Main + "v");
            bool isFormula = cell.Element(Main + "f") != null;
            string raw = (string)valueElement;
            JToken value;
            switch (type)
            {
                case "s":
                {
                    int index;
                    value = raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) &&
                            index >= 0 && index < shared.Count
                        ? (JToken)shared[index]
                        : JValue.CreateNull();
                    break;
                }
                case "inlineStr":
                    value = string.Concat(cell.Descendants(Main + "t").Select(t => (string)t));
                    break;
                case "b":
                    value = raw == "1";
                    break;
                case "str":   // formula whose cached value is text
                    value = (JToken)raw ?? JValue.CreateNull();
                    break;
                default:      // "n" and unmarked
                {
                    double number;
                    value = raw != null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                        ? (JToken)number
                        : JValue.CreateNull();
                    break;
                }
            }
            if (isFormula)
            {
                if (valueElement == null) formulasWithoutCache++;
                return new JObject { ["value"] = value, ["formula"] = true };
            }
            return value;
        }

        private static int ColumnIndexOf(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return 0;
            int index = 0;
            foreach (char c in reference)
            {
                if (c < 'A' || c > 'Z') break;
                index = index * 26 + (c - 'A' + 1);
            }
            return Math.Max(0, index - 1);
        }
    }
}
