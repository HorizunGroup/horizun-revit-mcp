// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_export decides which files on disk it may report as PRODUCED by
// comparing a snapshot taken before Revit's exporter ran against one taken
// after. This is the Revit-free half of that decision, so it is testable
// without a Revit in the room.
//
// THE DEFECT THIS EXISTS TO FIX, found in review: the old snapshot dropped a
// path from its dictionary entirely when FileInfo threw (locked, a permission
// blip) - so a file that already existed but was merely unreadable AT THAT
// INSTANT came back MISSING from the 'before' map, and the diff then read that
// absence as "this file did not exist before" and reported it as newly
// PRODUCED. A pre-existing file that Revit never touched could pass as new
// evidence of the export.
//
// The fix separates two different facts a snapshot can know about a path:
//   EXISTED   - Directory.GetFiles returned it, whether or not it could be read.
//   READABLE  - size, mtime AND a content hash were all measured.
// A path that existed but was not readable BEFORE the call is reported
// UNMEASURED, never PRODUCED - "we could not look" must never add up to "it
// is new", the same rule PostconditionCheck.Unreadable enforces for a single
// property.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One path's measurement at a snapshot instant.</summary>
    public sealed class ExportFileStamp
    {
        /// <summary>The path was listed by the directory scan, whether or not it could then be read.</summary>
        public bool Existed;
        /// <summary>Size, Mtime AND Hash were all measured; only then are they trustworthy for comparison.</summary>
        public bool Readable;
        public long Size;
        public long Mtime;
        public string Hash;
    }

    public static class ExportFileDiff
    {
        /// <summary>
        /// Sorts every path present in 'after' into PRODUCED (new, or measured
        /// changed against 'before') and UNMEASURED (existed before this call and
        /// could not be proven either way - unreadable now, or unreadable before).
        /// A path in neither list existed before, was measured both times, and is
        /// byte-identical: this call did not touch it.
        /// </summary>
        /// <summary>
        /// Whether a produced file's stem belongs to the requested output stem: equal, or the
        /// stem followed by '-', '_' or " - ". MEASURED (Revit 2024, format=image): ExportImage
        /// names its file "&lt;stem&gt; - Floor Plan - &lt;view name&gt;.png"; without the " - "
        /// form the export was reported as producing nothing although the file was there.
        /// </summary>
        public static bool StemMatches(string wantedStem, string actualStem)
        {
            if (string.IsNullOrEmpty(wantedStem) || actualStem == null) return false;
            return actualStem.Equals(wantedStem, System.StringComparison.OrdinalIgnoreCase) ||
                   actualStem.StartsWith(wantedStem + "-", System.StringComparison.OrdinalIgnoreCase) ||
                   actualStem.StartsWith(wantedStem + "_", System.StringComparison.OrdinalIgnoreCase) ||
                   actualStem.StartsWith(wantedStem + " - ", System.StringComparison.OrdinalIgnoreCase);
        }

        public static (List<string> Produced, List<string> Unmeasured) Diff(
            IReadOnlyDictionary<string, ExportFileStamp> before,
            IReadOnlyDictionary<string, ExportFileStamp> after)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));
            var produced = new List<string>();
            var unmeasured = new List<string>();
            foreach (KeyValuePair<string, ExportFileStamp> kv in after)
            {
                string path = kv.Key;
                ExportFileStamp cur = kv.Value;
                if (cur == null || !cur.Readable || cur.Size <= 0) { unmeasured.Add(path); continue; }
                if (!before.TryGetValue(path, out ExportFileStamp old) || old == null || !old.Existed)
                { produced.Add(path); continue; }   // genuinely new: nothing at this path before.
                if (!old.Readable) { unmeasured.Add(path); continue; }   // existed, unreadable before: NOT a match either way.
                if (old.Size != cur.Size || old.Mtime != cur.Mtime ||
                    !string.Equals(old.Hash, cur.Hash, StringComparison.Ordinal))
                    produced.Add(path);
                // else: measured both times, byte-identical - untouched by this call.
            }
            produced.Sort(StringComparer.OrdinalIgnoreCase);
            unmeasured.Sort(StringComparer.OrdinalIgnoreCase);
            return (produced, unmeasured);
        }

        /// <summary>
        /// Non-PDF formats in horizun_export always expect EXACTLY ONE output path
        /// per call (dwg/image/nwc-view take one view_id; ifc/nwc-model/schedule_csv
        /// take none; fbx combines every 3D view_id into one .fbx) - so, unlike
        /// PDF's per-view expected set, this is a single-path comparison. Reports
        /// what is missing and what is unexpectedly extra by name, rather than a
        /// bare count mismatch.
        /// </summary>
        /// <summary>
        /// format=image: Revit's ExportImage names the file "&lt;stem&gt; - &lt;view type&gt; - &lt;view name&gt;.png"
        /// instead of the requested path (MEASURED, Revit 2024 and 2027), so a correct export was
        /// reported as "missing". When the requested path was NOT produced and exactly one produced
        /// file follows its stem (and extension), that file is the export: the command moves it to
        /// the requested path. Null in every other case, which keeps the strict check.
        /// </summary>
        public static string ImageToRename(IReadOnlyList<string> produced, string expectedPath)
        {
            if (produced == null || string.IsNullOrEmpty(expectedPath) || produced.Count != 1) return null;
            string only = produced[0];
            if (string.Equals(only, expectedPath, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(System.IO.Path.GetDirectoryName(only), System.IO.Path.GetDirectoryName(expectedPath), StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(System.IO.Path.GetExtension(only), System.IO.Path.GetExtension(expectedPath), StringComparison.OrdinalIgnoreCase)) return null;
            return StemMatches(System.IO.Path.GetFileNameWithoutExtension(expectedPath), System.IO.Path.GetFileNameWithoutExtension(only)) ? only : null;
        }

        public static (List<string> Missing, List<string> Extra) AgainstExpectedSingleFile(
            IReadOnlyList<string> produced, string expectedPath)
        {
            if (produced == null) throw new ArgumentNullException(nameof(produced));
            if (string.IsNullOrEmpty(expectedPath)) throw new ArgumentException("expectedPath is required.", nameof(expectedPath));
            bool found = produced.Any(p => string.Equals(p, expectedPath, StringComparison.OrdinalIgnoreCase));
            var missing = found ? new List<string>() : new List<string> { expectedPath };
            var extra = produced.Where(p => !string.Equals(p, expectedPath, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            return (missing, extra);
        }
    }
}
