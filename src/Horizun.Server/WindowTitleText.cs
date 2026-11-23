// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// E10: which model does each running Revit show? Revit's main window title reads
// "Autodesk Revit 2024 - [Model.rvt - Floor Plan: Level 1]" (Spanish: "Planta de
// planta: Nivel 1"), or "... - [Home]"/"[Inicio]" with no model. Parsing it is
// cheap, needs nothing from the add-in, and is what the user sees on screen. It is
// reported as the window's ACTIVE document and view, sourced from the title.
// -----------------------------------------------------------------------------
using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    public static class WindowTitleText
    {
        public static string Of(int pid)
        {
            try { using (Process p = Process.GetProcessById(pid)) return p.MainWindowTitle; }
            catch { return null; }
        }

        /// <summary>{document, view, window_title, source} or null when the title says nothing.</summary>
        public static JToken Describe(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return JValue.CreateNull();
            string document = null, view = null;
            int open = title.IndexOf('[');
            int close = title.LastIndexOf(']');
            if (open >= 0 && close > open)
            {
                string inner = title.Substring(open + 1, close - open - 1).Trim();
                // "Model.rvt - Floor Plan: Level 1": the view part starts at the LAST " - "
                // that follows a file extension, so a model named "A - B.rvt" stays whole.
                int ext = IndexOfExtension(inner);
                if (ext >= 0)
                {
                    document = inner.Substring(0, ext).Trim();
                    string rest = inner.Substring(ext).TrimStart();
                    if (rest.StartsWith("-", StringComparison.Ordinal)) rest = rest.Substring(1).Trim();
                    view = rest.Length > 0 ? rest : null;
                }
                else if (!IsHome(inner))
                {
                    // No file extension in the title (Revit shows "Torre A - Floor Plan: Level 1"): the view is still there, and a
                    // view is not part of the document - changing it must never change which document a pinned target follows
                    // (INT-7 m-h). A file name cannot hold ':' (Windows), so the first ':' belongs to the view; the view starts at
                    // the last " - " before it. A title with no ':' is the model's name alone.
                    SplitViewSuffix(inner, out document, out view);
                }
            }
            return new JObject
            {
                ["document"] = document == null ? JValue.CreateNull() : (JToken)document,
                ["view"] = view == null ? JValue.CreateNull() : (JToken)view,
                ["window_title"] = title,
                ["source"] = "main_window_title"
            };
        }

        /// <summary>"Model - View type: view name" -> ("Model", "View type: view name"); no ':' -> (the whole text, null).</summary>
        internal static void SplitViewSuffix(string inner, out string document, out string view)
        {
            document = inner; view = null;
            int colon = inner.IndexOf(':');
            if (colon < 0) return;
            int dash = inner.LastIndexOf(" - ", colon, StringComparison.Ordinal);
            if (dash <= 0) return;            // "Floor Plan: Level 1" alone: nothing says where the model's name ends
            document = inner.Substring(0, dash).Trim();
            view = inner.Substring(dash + 3).Trim();
            if (document.Length == 0) { document = inner; view = null; }
        }

        private static bool IsHome(string inner) =>
            string.Equals(inner, "Home", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(inner, "Inicio", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(inner, "Start", StringComparison.OrdinalIgnoreCase);

        /// <summary>Index just past ".rvt"/".rfa"/".rte"/".rft" (case-insensitive), or -1.</summary>
        private static int IndexOfExtension(string s)
        {
            int best = -1;
            foreach (string e in new[] { ".rvt", ".rfa", ".rte", ".rft" })
            {
                int i = s.IndexOf(e, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (best < 0 || i + e.Length < best)) best = i + e.Length;
            }
            return best;
        }
    }
}
