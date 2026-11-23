// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// CT-P2-1a: CHOOSE THE REVIT BY THE MODEL IT HOLDS, NOT BY A PID THAT CHANGES.
//
// A process id names one run of Revit. The model a person works on outlives it: Revit
// restarts after a crash or an update, a file opens in a new instance, and the pid the
// agent pinned now belongs to nothing (or to a different model). horizun_target
// document="<name>" pins the document instead, and the server keeps finding the instance
// that holds it:
//   * the match is on the ACTIVE document each live Revit shows in its main window title
//     (the same evidence horizun_target already publishes as active_document): by exact
//     name - with or without the extension, or the full path of the file, whose name is the
//     one compared. Choosing a document with horizun_target may also use a fragment, but only
//     a UNIQUE one; FOLLOWING a pinned document never does (INT-6 m-10): "Torre A" is not
//     "Torre A - Estructura", and a read that moved to the second would be the right
//     answer from the wrong model, with nothing to say so;
//   * two instances showing it, or none, is a refusal that lists the candidates. Nothing
//     is guessed: a write to the wrong copy of a model is a correct edit to the wrong file;
//   * a pinned document whose instance is gone moves to the one instance that shows it by EXACT
//     name - and only when exactly one does. A pinned instance that is alive and shows ANOTHER
//     document is not routed to: the target is unresolved and every call is refused with
//     documento_objetivo_ambiguo, naming the candidates.
//
// The matching is pure (Match); reading window titles is the thin edge (Refresh).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Server
{
    internal static class DocumentTarget
    {
        internal sealed class Live
        {
            public int Pid;
            public string Year;
            /// <summary>The active document named in the instance's main window title; null when unreadable.</summary>
            public string ActiveDocument;
        }

        internal sealed class Outcome
        {
            public Live Chosen;
            public string Refusal;
            public List<Live> Candidates = new List<Live>();
            /// <summary>True when the choice was made by a fragment of the name, not by the name.</summary>
            public bool ByFragment;
        }

        /// <summary>A full path names its file: the title bar shows the name, so the name of the file is what is compared.</summary>
        internal static string NameOf(string asked)
        {
            string s = (asked ?? "").Trim();
            if (s.IndexOfAny(new[] { (char)92, (char)47 }) < 0) return s;
            int cut = Math.Max(s.LastIndexOf((char)92), s.LastIndexOf((char)47));
            return s.Substring(cut + 1);
        }

        /// <summary>Name without a Revit file extension, trimmed.</summary>
        internal static string Normalize(string name)
        {
            string s = (name ?? "").Trim();
            foreach (string ext in new[] { ".rvt", ".rfa", ".rte", ".rft" })
                if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(0, s.Length - ext.Length).TrimEnd(); break; }
            return s;
        }

        /// <summary>
        /// Exactly one live instance whose active document is <paramref name="asked"/>: exact (extension-insensitive,
        /// case-insensitive) first, then - only when allowFragment - a unique fragment. Otherwise a refusal naming what was found.
        /// </summary>
        internal static Outcome Match(string asked, IEnumerable<Live> live, bool allowFragment = true)
        {
            var o = new Outcome();
            string want = Normalize(NameOf(asked));
            if (want.Length == 0) { o.Refusal = "document needs a model name (the one in Revit's title bar)."; return o; }
            List<Live> all = (live ?? Enumerable.Empty<Live>()).ToList();
            List<Live> exact = all.Where(l => l.ActiveDocument != null &&
                string.Equals(Normalize(l.ActiveDocument), want, StringComparison.OrdinalIgnoreCase)).ToList();
            bool byFragment = exact.Count == 0 && allowFragment;
            List<Live> hits = !byFragment ? exact
                : all.Where(l => l.ActiveDocument != null &&
                    Normalize(l.ActiveDocument).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            o.Candidates = hits;
            if (hits.Count == 1) { o.Chosen = hits[0]; o.ByFragment = byFragment; return o; }
            string shown = string.Join(", ", all.Select(l => "Revit " + l.Year + " pid " + l.Pid + " [" + (l.ActiveDocument ?? "active document unreadable") + "]"));
            o.Refusal = hits.Count == 0
                ? "No running Revit shows a document named '" + asked + "' (" + (all.Count == 0 ? "none is running" : shown) +
                  "). Only the ACTIVE document of each instance is visible; open or activate the model, or pass pid."
                : hits.Count + " running Revit instances show '" + asked + "' (" + string.Join(", ", hits.Select(l => "pid " + l.Pid)) +
                  "): a document open twice is not a target. Pass pid to choose one.";
            return o;
        }

        /// <summary>The live instances with the active document each shows; the only part that reads the machine.</summary>
        internal static List<Live> ReadLive()
        {
            var list = new List<Live>();
            foreach (Discovered d in PipeClient.ListAll().Where(x => x.ProcessAlive).OrderBy(x => x.Year).ThenBy(x => x.Pid))
            {
                string doc = null;
                try
                {
                    var described = WindowTitleText.Describe(WindowTitleText.Of(d.Pid)) as Newtonsoft.Json.Linq.JObject;
                    doc = (string)described?["document"];
                }
                catch { doc = null; }
                list.Add(new Live { Pid = d.Pid, Year = d.Year, ActiveDocument = doc });
            }
            return list;
        }

        /// <summary>
        /// Does the session keep <paramref name="moved"/> in place of <paramref name="t"/>? Only an answer that moved the pin to a
        /// resolved instance: an unresolved answer is for THIS call only, the pin stays and the next call looks again.
        /// </summary>
        internal static bool ShouldStore(TargetSelection t, TargetSelection moved) =>
            moved != null && !ReferenceEquals(moved, t) && moved.Unresolved == null;

        /// <summary>
        /// If <paramref name="t"/> pins a document and its instance is gone or no longer shows it, the selection
        /// that follows the document to the one instance that shows it now; otherwise <paramref name="t"/> itself.
        /// An unreadable title keeps the pin (unknown is not "moved").
        /// </summary>
        internal static TargetSelection Refresh(TargetSelection t, Func<List<Live>> read = null)
        {
            if (t == null || t.Document == null) return t;
            List<Live> live = (read ?? ReadLive)();
            Live pinned = live.FirstOrDefault(l => l.Pid == t.Pid);
            string name = Normalize(NameOf(t.Document));
            if (pinned != null && (pinned.ActiveDocument == null ||
                string.Equals(Normalize(pinned.ActiveDocument), name, StringComparison.OrdinalIgnoreCase)))
                return t;
            // Following a document is by its EXACT name, never by a fragment of it (INT-6 m-10).
            Outcome o = Match(t.Document, live.Where(l => l.Pid != t.Pid), allowFragment: false);
            if (o.Chosen != null) return TargetSelection.ByDocument(o.Chosen.Pid, t.Document, t.Origin);
            // The pinned process is gone: the call will say there is no bridge for it - never a different model.
            if (pinned == null) return t;
            // The pinned process is alive and shows ANOTHER document, and no other instance shows this one by name (or two do).
            // Routing the call there would answer a read about the wrong model without saying so.
            return t.AsUnresolved("The document '" + t.Document + "' was pinned to Revit process " + t.Pid + ", which now shows '" +
                pinned.ActiveDocument + "'" + (o.Candidates.Count > 1
                    ? ", and " + o.Candidates.Count + " other instances show it (" + string.Join(", ", o.Candidates.Select(c => "pid " + c.Pid)) + ")"
                    : ", and no other running Revit shows it") + ". Nothing was sent. Open or activate the model, or call horizun_target with a pid or the exact document name.");
        }
    }
}
