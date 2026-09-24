// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// 4D SCHEDULE IMPORT - the Revit-free half of horizun_link_schedule.
//
// Three formats, no dependency: MS Project XML (MSPDI, the "Save as XML" of
// Project), a generic CSV (id, name, start, finish, wbs - plus optional
// percent_complete / actual_start / actual_finish), and Primavera P6 XER, which is
// tab-separated text (%T table, %F fields, %R rows) and needs no paid library.
//
// A ROW THAT CANNOT BE READ IS REJECTED BY LINE, NEVER GUESSED. A date that is not
// ISO (yyyy-MM-dd...) in a CSV is refused rather than read as dd/MM or MM/dd -
// 03/04 is two different days depending on who exported it, and a 4D colour on
// the wrong month looks exactly like a right one.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class ScheduleActivity
    {
        public string Id;
        public string Name;
        public string Wbs;
        public DateTime? Start;
        public DateTime? Finish;
        public double? PercentComplete;
        public DateTime? ActualStart;
        public DateTime? ActualFinish;
        public int Line;

        public bool HasProgress => PercentComplete != null || ActualStart != null || ActualFinish != null;

        public JObject ToJson() => new JObject
        {
            ["id"] = Id, ["name"] = Name, ["wbs"] = Wbs,
            ["start"] = ScheduleImport.Day(Start), ["finish"] = ScheduleImport.Day(Finish),
            ["percent_complete"] = PercentComplete,
            ["actual_start"] = ScheduleImport.Day(ActualStart), ["actual_finish"] = ScheduleImport.Day(ActualFinish)
        };
    }

    public sealed class ScheduleImportResult
    {
        public string Format;
        public List<ScheduleActivity> Activities = new List<ScheduleActivity>();
        public List<JObject> Rejected = new List<JObject>();
        public int SummariesSkipped;

        internal void Reject(int line, string reason) => Rejected.Add(new JObject { ["line"] = line, ["reason"] = reason });
    }

    public static partial class ScheduleImport
    {
        private static readonly string[] IsoFormats =
        {
            "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
            "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.fffK"
        };

        public static string Day(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public static bool TryDate(string text, out DateTime? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text)) return true;           // absent is not malformed
            if (DateTime.TryParseExact(text.Trim(), IsoFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime d))
            { value = d.Date; return true; }
            return false;
        }

        /// <summary>Format from the extension: .xml MSPDI, .csv/.txt CSV, .xer Primavera.</summary>
        public static ScheduleImportResult Parse(string path, string text)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            ScheduleImportResult r;
            if (ext == ".xml") r = ParseMspdi(text);
            else if (ext == ".xer") r = ParseXer(text);
            else if (ext == ".csv" || ext == ".txt") r = ParseCsv(text);
            else throw new ScheduleImportException("Unsupported schedule extension '" + ext + "'. Use .xml (MS Project MSPDI), .csv or .xer.");
            Validate(r);
            return r;
        }

        /// <summary>Shared checks: an id is required and unique; finish is not before start.</summary>
        private static void Validate(ScheduleImportResult r)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<ScheduleActivity>();
            foreach (ScheduleActivity a in r.Activities)
            {
                if (string.IsNullOrWhiteSpace(a.Id)) { r.Reject(a.Line, "no activity id."); continue; }
                if (!seen.Add(a.Id)) { r.Reject(a.Line, "duplicate activity id '" + a.Id + "'."); continue; }
                if (a.Start != null && a.Finish != null && a.Finish < a.Start) { r.Reject(a.Line, "finish before start for '" + a.Id + "'."); continue; }
                kept.Add(a);
            }
            r.Activities = kept;
        }

        // ---- MS Project XML (MSPDI) --------------------------------------------------

        public static ScheduleImportResult ParseMspdi(string text)
        {
            var r = new ScheduleImportResult { Format = "mspdi" };
            XDocument doc;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try
            {
                using (var sr = new StringReader(text))
                using (XmlReader xr = XmlReader.Create(sr, settings))
                    doc = XDocument.Load(xr, LoadOptions.SetLineInfo);
            }
            catch (Exception ex) { throw new ScheduleImportException("Not a readable MS Project XML: " + ex.Message); }
            if (doc.Root == null || doc.Root.Name.LocalName != "Project")
                throw new ScheduleImportException("Not an MSPDI file: the root element is not <Project>.");

            foreach (XElement task in doc.Root.Elements().Where(e => e.Name.LocalName == "Tasks").SelectMany(t => t.Elements()))
            {
                if (task.Name.LocalName != "Task") continue;
                int line = ((IXmlLineInfo)task).LineNumber;
                string V(string name) => task.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
                // UID 0 is the project summary row; Summary=1 rows are WBS parents, not work.
                if (V("UID") == "0" || V("Summary") == "1") { r.SummariesSkipped++; continue; }
                var a = new ScheduleActivity { Id = V("UID") ?? V("ID"), Name = V("Name"), Wbs = V("WBS"), Line = line };
                if (!Dates(r, line, a, V("Start"), V("Finish"), V("ActualStart"), V("ActualFinish"))) continue;
                if (double.TryParse(V("PercentComplete"), NumberStyles.Float, CultureInfo.InvariantCulture, out double pc)) a.PercentComplete = pc;
                r.Activities.Add(a);
            }
            return r;
        }

        private static bool Dates(ScheduleImportResult r, int line, ScheduleActivity a, string s, string f, string asT, string afT)
        {
            if (!TryDate(s, out a.Start)) { r.Reject(line, "start '" + s + "' is not an ISO date."); return false; }
            if (!TryDate(f, out a.Finish)) { r.Reject(line, "finish '" + f + "' is not an ISO date."); return false; }
            if (!TryDate(asT, out a.ActualStart)) { r.Reject(line, "actual_start '" + asT + "' is not an ISO date."); return false; }
            if (!TryDate(afT, out a.ActualFinish)) { r.Reject(line, "actual_finish '" + afT + "' is not an ISO date."); return false; }
            return true;
        }

        // ---- CSV -------------------------------------------------------------------------

        private static readonly Dictionary<string, string> CsvAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "id", "id" }, { "activity_id", "id" }, { "task_id", "id" }, { "codigo", "id" }, { "código", "id" },
            { "name", "name" }, { "activity_name", "name" }, { "task_name", "name" }, { "nombre", "name" },
            { "start", "start" }, { "start_date", "start" }, { "inicio", "start" },
            { "finish", "finish" }, { "end", "finish" }, { "finish_date", "finish" }, { "fin", "finish" },
            { "wbs", "wbs" }, { "edt", "wbs" },
            { "percent_complete", "percent_complete" }, { "avance", "percent_complete" },
            { "actual_start", "actual_start" }, { "inicio_real", "actual_start" },
            { "actual_finish", "actual_finish" }, { "fin_real", "actual_finish" }
        };

        public static ScheduleImportResult ParseCsv(string text)
        {
            var r = new ScheduleImportResult { Format = "csv" };
            string[] lines = (text ?? "").TrimStart('﻿').Replace("\r\n", "\n").Split('\n');
            int headerIndex = Array.FindIndex(lines, l => !string.IsNullOrWhiteSpace(l));
            if (headerIndex < 0) throw new ScheduleImportException("The CSV is empty.");
            string header = lines[headerIndex];
            char sep = header.Contains("\t") ? '\t' : header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';
            List<string> cols = SplitCsv(header, sep).Select(h => CsvAliases.TryGetValue(h.Trim(), out string k) ? k : null).ToList();
            foreach (string required in new[] { "id", "name", "start", "finish" })
                if (!cols.Contains(required))
                    throw new ScheduleImportException("The CSV header lacks '" + required + "'. Expected columns id,name,start,finish[,wbs,percent_complete,actual_start,actual_finish].");

            for (int i = headerIndex + 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                List<string> cells = SplitCsv(lines[i], sep);
                string C(string key) { int k = cols.IndexOf(key); return k >= 0 && k < cells.Count ? cells[k].Trim() : null; }
                var a = new ScheduleActivity { Id = C("id"), Name = C("name"), Wbs = C("wbs"), Line = i + 1 };
                if (!Dates(r, i + 1, a, C("start"), C("finish"), C("actual_start"), C("actual_finish"))) continue;
                string pc = C("percent_complete");
                if (!string.IsNullOrWhiteSpace(pc))
                {
                    if (!double.TryParse(pc.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    { r.Reject(i + 1, "percent_complete '" + pc + "' is not a number."); continue; }
                    a.PercentComplete = v;
                }
                r.Activities.Add(a);
            }
            return r;
        }

        /// <summary>One CSV line; double quotes enclose separators and "" escapes a quote.</summary>
        public static List<string> SplitCsv(string line, char sep)
        {
            var cells = new List<string>();
            var cur = new System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cur.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == sep) { cells.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            cells.Add(cur.ToString());
            return cells;
        }
    }

    public sealed class ScheduleImportException : Exception
    {
        public ScheduleImportException(string message) : base(message) { }
    }
}
