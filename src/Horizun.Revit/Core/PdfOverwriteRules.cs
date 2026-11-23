// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// overwrite=true THAT OVERWROTE NOTHING (field cycle, 2026-10-21).
//
// MEASURED: horizun_export format=pdf with overwrite=true on a path that already held a PDF
// failed with Revit's "Following files already exist" - Revit's PDF exporter never replaces a
// file. The caller had to move the old PDF away by hand first.
//
// The tool does that step itself, and safely: with overwrite=true each existing PDF is MOVED
// ASIDE (never deleted up front) under a name that cannot collide, the export runs, and then
//   * success  -> the moved-aside copies are deleted (the caller asked for replacement);
//   * failure  -> they are moved back, replacing any partial file the failed export left.
// So a failed export leaves the old PDF exactly where it was.
//
// Revit-free: the names and the decision of what to do with the aside copies.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class AsideFile
    {
        public string Original;
        public string Aside;
    }

    public static class PdfOverwriteRules
    {
        /// <summary>The name an existing PDF is moved to: same folder, stamped, never the original and never a PDF Revit lists.</summary>
        public static string AsidePath(string original, DateTime utc)
        {
            return original + ".replaced-" + utc.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        }

        /// <summary>The files to move aside: the planned PDF paths that exist, when overwrite was asked.</summary>
        public static List<string> ToMoveAside(IEnumerable<string> plannedPaths, bool overwrite, Func<string, bool> exists)
        {
            if (!overwrite || plannedPaths == null) return new List<string>();
            return plannedPaths.Where(p => !string.IsNullOrWhiteSpace(p) && exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Record each completed move immediately so the caller can recover after a later failure.</summary>
        public static void MoveAside(IEnumerable<string> paths, DateTime utc, List<AsideFile> moved, Action<string, string> move)
        {
            foreach (string original in paths)
            {
                string aside = AsidePath(original, utc);
                move(original, aside);
                moved.Add(new AsideFile { Original = original, Aside = aside });
            }
        }

        /// <summary>Restore every recorded original independently; retain any failed backup on disk.</summary>
        public static List<RestoreResult> RestoreAsides(List<AsideFile> moved)
        {
            var results = new List<RestoreResult>();
            foreach (var file in moved)
            {
                try
                {
                    // Never remove the current file unless the old copy is available.
                    // A missing backup must be reported without destroying the only file left.
                    if (!File.Exists(file.Aside))
                        throw new FileNotFoundException("The backup PDF is missing; the current file was preserved.", file.Aside);
                    if (File.Exists(file.Original)) File.Delete(file.Original);
                    File.Move(file.Aside, file.Original);
                    results.Add(new RestoreResult { Path = file.Original });
                }
                catch (Exception ex)
                {
                    results.Add(new RestoreResult { Path = file.Original, Error = ex.Message, OldCopy = file.Aside });
                }
            }
            moved.Clear();
            return results;
        }

        /// <summary>What to do with the aside copies once the export is judged.</summary>
        public static string Decide(bool exportSucceeded) => exportSucceeded ? "delete_aside" : "restore_aside";
    }

    public sealed class RestoreResult
    {
        public string Path;
        public string Error;
        public string OldCopy;
    }
}
