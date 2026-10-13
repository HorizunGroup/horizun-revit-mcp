// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_read_schedule - HOST-RESIDENT (acero 3.0, L10). Reads reinforcement
// schedule sheets - beam cards, wall cards, bending tables - into rows, bar
// notation and self-checks, and proposes the beam cards as a
// horizun.structural-requirements/1 set keyed by mark. It writes nothing and it
// never touches Revit: the caller plans and applies the proposal with
// horizun_plan_reinforcement / horizun_apply_reinforcement, which resolve marks to
// hosts and refuse what does not fit.
//
// One reader, three sources, exactly one per call:
//   file_path     - a vector PDF, read here with PdfPig (SchedulePdf.cs);
//   cad_entities  - CAD IR entities that carry TEXT - the bridge's accoreconsole
//                   reading of a DWG (horizun_cad_extract over a Revit link carries
//                   no text: Revit's import drops it) - with cad_text_anchor declared,
//                   because a DWG text has no extent;
//   primitives    - segments, polylines and texts any other extractor produced.
// The reading itself is ScheduleCards (Core), the same pure code for all three.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ReadScheduleTool
    {
        internal const string ToolName = "horizun_read_schedule";
        public const int MaxPages = 50;
        public const long MaxFileBytes = 256L * 1024 * 1024;

        internal static JObject Handle(JObject args, CancellationToken ct)
        {
            args = args ?? new JObject();
            int sources = new[] { "file_path", "primitives", "cad_entities" }.Count(k => args[k] != null && args[k].Type != JTokenType.Null);
            if (sources != 1)
                throw Refusal("source_not_declared",
                    "give exactly one source: file_path (a vector PDF), cad_entities (CAD IR entities that carry text, with cad_text_anchor) " +
                    "or primitives (segments, polylines, texts in drawing millimetres). " + (sources == 0 ? "None was given." : sources + " were given.") +
                    " Nothing was read.");

            ScheduleCards.Options o = ScheduleCards.ReadOptions(args, out string code, out string error);
            if (o == null) throw Refusal(code, error + " Nothing was read.");

            var sheets = new List<ScheduleCards.Sheet>();
            JObject file = null;
            if (args["file_path"] != null && args["file_path"].Type != JTokenType.Null)
            {
                string path = args["file_path"].Type == JTokenType.String ? (string)args["file_path"] : null;
                if (string.IsNullOrWhiteSpace(path))
                    throw Refusal("file_not_usable", "file_path must be an absolute path to a PDF. Nothing was read.");
                string notLocal = LocalPath.Problem(path, "file_path");
                if (notLocal != null) throw Refusal("file_not_usable", notLocal + " Nothing was read.");
                if (!File.Exists(path)) throw Refusal("file_not_found", "No file at " + path + ". Nothing was read.");
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes)
                    throw Refusal("file_too_large", "The file is " + info.Length + " bytes; this reads at most " + MaxFileBytes + ". Nothing was read.");
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 5 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P' || bytes[2] != (byte)'D' || bytes[3] != (byte)'F')
                    throw Refusal("pdf_not_readable", "The file does not start with a PDF header (%PDF). Nothing was read.");
                List<int> pages = null;
                if (args["pages"] != null)
                {
                    var a = args["pages"] as JArray;
                    if (a == null || a.Count == 0 || a.Any(t => t.Type != JTokenType.Integer) || a.Select(t => (int)t).Distinct().Count() != a.Count)
                        throw Refusal("pages_not_usable", "pages must be a list of distinct 1-based page numbers. Nothing was read.");
                    pages = a.Select(t => (int)t).ToList();
                }
                SchedulePdf.Result read;
                try { read = SchedulePdf.Read(bytes, Path.GetFileName(path), pages, MaxPages, ct); }
                catch (ToolRefusal) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw Refusal("pdf_not_readable", "The PDF could not be opened: " + ex.Message + " Nothing was read.");
                }
                sheets.AddRange(read.Sheets);
                string sha;
                using (SHA256 h = SHA256.Create()) sha = BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                file = new JObject
                {
                    ["file_path"] = path, ["sha256"] = sha, ["pages_in_file"] = read.PageCount,
                    ["pages_read"] = new JArray(read.Sheets.Select(s => (JToken)s.Page)),
                    ["reader"] = "PdfPig (UglyToad.PdfPig) - stroked lines and the text layer; no OCR, no curves, no fills"
                };
            }
            else if (args["cad_entities"] != null && args["cad_entities"].Type != JTokenType.Null)
            {
                string anchor = args["cad_text_anchor"]?.Type == JTokenType.String ? (string)args["cad_text_anchor"] : null;
                ScheduleCards.Sheet s = ScheduleCards.SheetFromCadEntities(args["cad_entities"] as JArray, anchor, out code, out error);
                if (s == null) throw Refusal(code, error + " Nothing was read.");
                sheets.Add(s);
            }
            else
            {
                JToken p = args["primitives"];
                IEnumerable<JToken> list = p is JArray arr ? arr : new[] { p };
                int i = 0;
                foreach (JToken one in list)
                {
                    ScheduleCards.Sheet s = ScheduleCards.SheetFromPrimitives(one as JObject, out code, out error);
                    if (s == null) throw Refusal(code, (p is JArray ? "primitives[" + i + "]: " : "") + error + " Nothing was read.");
                    if (one["page"] == null) s.Page = i + 1;
                    sheets.Add(s);
                    i++;
                }
                if (sheets.Count == 0) throw Refusal(ScheduleCards.CodePrimitivesNotUsable, "primitives is an empty list. Nothing was read.");
            }

            ct.ThrowIfCancellationRequested();
            JObject reply = ScheduleCards.Read(sheets, o);
            if (file != null) reply.AddFirst(new JProperty("file", file));
            return reply;
        }

        private static ToolRefusal Refusal(string code, string message) =>
            new ToolRefusal(message, new JObject { ["code"] = code });
    }
}
