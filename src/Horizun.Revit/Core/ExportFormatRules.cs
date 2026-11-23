// -----------------------------------------------------------------------------
// Horizun Revit MCP - Revit-free rules for the multi-file and non-drawing exports:
// DWG/DGN/DWFX view sets (one file per view or sheet), gbXML and family .rfa files.
// File names are decided HERE, before anything is exported, so a naming rule that
// would make two views write one file refuses by name instead of letting the
// second export silently replace the first. Headers are judged here too, so the
// "is this really a DWG" question is unit-tested without a Revit in the room.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace Horizun.Revit.Core
{
    /// <summary>What the naming rule needs to know about one exported view or sheet.</summary>
    public sealed class ExportViewFacts
    {
        public long Id;
        public string Name;
        public bool IsSheet;
        public string SheetNumber;
    }

    /// <summary>Element counts read back from a written gbXML file.</summary>
    public sealed class GbXmlCounts
    {
        public string Root;
        public int Campus, Building, Space, Zone, Surface, Opening;

        /// <summary>Construction definitions (gbXML/Construction): counted, never judged - a file
        /// exported below tier Final carries none, and whether it must is the caller's rule.</summary>
        public int Construction;
    }

    public static class ExportFormatRules
    {
        public const string NamingOrdinal = "ordinal";
        public const string NamingViewName = "view_name";
        public const string NamingSheetNumber = "sheet_number";
        public const string XrefsLinked = "linked";
        public const string XrefsBound = "bound";

        /// <summary>
        /// Where a DWG's native dimensions and annotations end up, by xref mode. MEASURED
        /// 2026-10-17 on Revit 2026 sheet exports (Scan validation 4): every native
        /// dimension arrived as an AutoCAD DIMENSION entity, but in the MODEL SPACE OF
        /// EACH VIEW'S companion file, which the sheet file only references (an xref
        /// block, flags 36). A top-level selection of the sheet file - AutoLISP
        /// (ssget "_X" '((0 . "DIMENSION"))) - counts zero and reads as "the exporter
        /// lost the dimensions". The audit must walk the view files or the xref blocks.
        /// </summary>
        public static string DwgAnnotationLocation(string xrefs)
            => xrefs == XrefsBound
                ? "Native dimensions and annotations of each view are exported as DIMENSION/MTEXT entities inside the block " +
                  "definition that represents the view in the sheet file, not at the top level of paper or model space; " +
                  "count them by walking block definitions."
                : "Native dimensions and annotations of each view are exported as DIMENSION/MTEXT entities in the model space " +
                  "of that view's companion file (<stem>-<view>.dwg); the sheet file only references it as an xref, so a " +
                  "top-level selection of the sheet file counts zero. Count them in the companion files or the xref blocks.";

        /// <summary>
        /// Above this many views the dry run advises running the apply through
        /// horizun_submit_job: each view is its own exporter call on Revit's UI thread,
        /// and a long set outlives a client's tool timeout while still working.
        /// </summary>
        public const int LongSetViews = 20;

        // '.' is replaced too: Revit's exporters treat the text after the last dot of
        // the name they are handed as an extension, so "Level 1.5" would not be the
        // file that was planned.
        private static readonly char[] InvalidStemChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '.' };

        /// <summary>A file stem safe on Windows, or null when nothing printable is left.</summary>
        public static string SanitizeStem(string raw)
        {
            if (raw == null) return null;
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw) sb.Append(ch < 32 || Array.IndexOf(InvalidStemChars, ch) >= 0 ? '_' : ch);
            string stem = sb.ToString().Trim().TrimEnd(' ', '_');
            return stem.Length == 0 ? null : stem;
        }

        /// <summary>
        /// One file stem per view, in the order given. ordinal: stem-001-id (the same
        /// shape PDF uses with pdf_combine=false); view_name: stem-view name;
        /// sheet_number: stem-number-name, sheets only. Null with a refusal naming the
        /// views when the rule cannot give every view its own file.
        /// </summary>
        public static List<string> SetStems(string stem, string naming, IList<ExportViewFacts> views, out string refusal)
        {
            refusal = null;
            if (views == null || views.Count == 0) { refusal = "at least one view_id is required."; return null; }
            if (views.Select(v => v.Id).Distinct().Count() != views.Count)
            { refusal = "view_ids repeats a view; each view or sheet is exported once."; return null; }
            string baseStem = SanitizeStem(stem);
            if (baseStem == null) { refusal = "output_path has no usable file name."; return null; }
            var stems = new List<string>();
            var seen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < views.Count; i++)
            {
                ExportViewFacts view = views[i];
                string part;
                if (naming == NamingOrdinal) part = (i + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + "-" + view.Id;
                else if (naming == NamingViewName) part = view.Name;
                else if (naming == NamingSheetNumber)
                {
                    if (!view.IsSheet)
                    { refusal = "file_naming=sheet_number names sheets only; view " + view.Id + " ('" + view.Name + "') is not a sheet."; return null; }
                    part = view.SheetNumber + "-" + view.Name;
                }
                else { refusal = "file_naming must be ordinal, view_name or sheet_number."; return null; }
                string candidate = SanitizeStem(baseStem + "-" + part);
                if (candidate == null) { refusal = "view " + view.Id + " yields no usable file name."; return null; }
                long other;
                if (seen.TryGetValue(candidate, out other))
                {
                    refusal = "file_naming=" + naming + " gives views " + other + " and " + view.Id + " the same file '" + candidate +
                              "'; the second export would replace the first. Use ordinal, or rename one of them.";
                    return null;
                }
                seen[candidate] = view.Id;
                stems.Add(candidate);
            }
            return stems;
        }

        /// <summary>
        /// One .rfa stem per family name. Two names that sanitize to one file (or
        /// differ only by case, which Windows treats as one file) refuse by name.
        /// </summary>
        public static List<string> FamilyStems(IList<string> familyNames, out string refusal)
        {
            refusal = null;
            var stems = new List<string>();
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in familyNames)
            {
                string stem = SanitizeStem(name);
                if (stem == null) { refusal = "family '" + name + "' yields no usable file name."; return null; }
                string other;
                if (seen.TryGetValue(stem, out other))
                {
                    refusal = "families '" + other + "' and '" + name + "' would both be written as '" + stem + ".rfa'.";
                    return null;
                }
                seen[stem] = name;
                stems.Add(stem);
            }
            return stems;
        }

        /// <summary>
        /// What the first bytes of a produced file say it is, or null when they do not
        /// say what the format promises. dwg: "AC10xx" (the release signature); dgn: a
        /// V8 file is an OLE structured-storage container, a V7 file starts with its
        /// type-9 header element; dwfx: an Open Packaging (zip) container.
        /// </summary>
        public static string HeaderKindOf(string format, byte[] head)
        {
            if (head == null) return null;
            switch (format)
            {
                case "dwg":
                    if (head.Length >= 6 && head[0] == 'A' && head[1] == 'C' && head[2] == '1' && head[3] == '0' &&
                        IsDigit(head[4]) && IsDigit(head[5]))
                        return Encoding.ASCII.GetString(head, 0, 6);
                    return null;
                case "dgn":
                    if (head.Length >= 8 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0 &&
                        head[4] == 0xA1 && head[5] == 0xB1 && head[6] == 0x1A && head[7] == 0xE1)
                        return "dgn_v8_structured_storage";
                    if (head.Length >= 4 && (head[0] == 0x08 || head[0] == 0xC8) && head[1] == 0x09 && head[2] == 0xFE && head[3] == 0x02)
                        return "dgn_v7";
                    return null;
                case "dwfx":
                    if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04)
                        return "zip_package";
                    return null;
                default:
                    return null;
            }
        }

        private static bool IsDigit(byte b) { return b >= '0' && b <= '9'; }

        /// <summary>
        /// Count the gbXML elements that say whether the file describes a building:
        /// Campus, Building, Space, Zone, Surface and Opening, by local name in any
        /// namespace. DTDs are refused - a gbXML written by Revit carries none.
        /// </summary>
        public static GbXmlCounts CountGbXml(Stream stream)
        {
            var counts = new GbXmlCounts();
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (counts.Root == null) counts.Root = reader.LocalName;
                    switch (reader.LocalName)
                    {
                        case "Campus": counts.Campus++; break;
                        case "Building": counts.Building++; break;
                        case "Space": counts.Space++; break;
                        case "Zone": counts.Zone++; break;
                        case "Surface": counts.Surface++; break;
                        case "Opening": counts.Opening++; break;
                        case "Construction": counts.Construction++; break;
                    }
                }
            }
            return counts;
        }

        /// <summary>
        /// The verdict on a written gbXML: a file whose root is not gbXML, or that holds
        /// no Space, is not the export that was asked for - an empty campus is refused,
        /// never reported as a deliverable. Null when the file holds up.
        /// </summary>
        public static string GbXmlProblem(GbXmlCounts counts)
        {
            if (counts == null || !string.Equals(counts.Root, "gbXML", StringComparison.Ordinal))
                return "the file's root element is '" + (counts?.Root ?? "<none>") + "', not gbXML";
            if (counts.Campus == 0) return "the file holds no Campus";
            if (counts.Space == 0) return "no spaces: the file holds an empty campus (0 Space elements)";
            return null;
        }
    }
}
