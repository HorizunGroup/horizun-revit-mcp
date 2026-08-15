// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH files horizun_file_info reads, out of the arguments (story 5.20). The
// directory listing is IO and injected; everything else - the refusal when
// nothing was named, the union of explicit paths and a folder sweep, the
// case-insensitive dedup, the cap - is pure and proved without a Revit or a disk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public sealed class FileInfoPlan
    {
        /// <summary>Non-null when the arguments cannot be honoured; nothing was read.</summary>
        public string Error { get; internal set; }

        /// <summary>The files to probe, in order, de-duplicated.</summary>
        public List<string> Files { get; internal set; } = new List<string>();

        /// <summary>How many the folder sweep matched before the cap.</summary>
        public int TotalMatched { get; internal set; }

        /// <summary>True when the cap dropped some: Files is a prefix of what matched.</summary>
        public bool Truncated { get; internal set; }

        public bool Ok => Error == null;
    }

    public static class FileInfoPaths
    {
        /// <summary>The most files one call will probe. A folder sweep past this is truncated, not refused.</summary>
        public const int MaxFiles = 2000;

        /// <summary>
        /// Resolve the file list. <paramref name="listFolder"/>(folder, pattern, recursive)
        /// returns the folder's matches (injected so this is testable without a disk); it
        /// is called only when a folder was given. Explicit paths come first, in the order
        /// given; folder matches follow. Dedup is case-insensitive on the string as given.
        /// </summary>
        public static FileInfoPlan Resolve(IEnumerable<string> paths, string folder, string pattern,
                                           bool recursive, Func<string, string, bool, IEnumerable<string>> listFolder)
        {
            var plan = new FileInfoPlan();

            bool hasFolder = !string.IsNullOrWhiteSpace(folder);
            var ordered = new List<string>();

            if (paths != null)
                foreach (string p in paths)
                    if (!string.IsNullOrWhiteSpace(p)) ordered.Add(p);

            int explicitCount = ordered.Count;

            if (!hasFolder && explicitCount == 0)
            {
                plan.Error = "Pass 'paths' (a list of file paths) or 'folder' (a directory to sweep). " +
                             "Neither was given, so there is nothing to read.";
                return plan;
            }

            if (hasFolder)
            {
                if (listFolder == null)
                {
                    plan.Error = "A folder was given but no way to list it was provided.";
                    return plan;
                }
                string pat = string.IsNullOrWhiteSpace(pattern) ? "*.rvt" : pattern;
                IEnumerable<string> matches;
                try { matches = listFolder(folder, pat, recursive); }
                catch (Exception ex)
                {
                    plan.Error = "Could not list folder '" + folder + "': " + ex.Message;
                    return plan;
                }
                if (matches != null)
                    foreach (string m in matches)
                        if (!string.IsNullOrWhiteSpace(m)) ordered.Add(m);
            }

            // Case-insensitive dedup, first occurrence wins so explicit paths keep priority.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unique = new List<string>();
            foreach (string p in ordered)
                if (seen.Add(p)) unique.Add(p);

            plan.TotalMatched = unique.Count;
            if (unique.Count > MaxFiles)
            {
                plan.Truncated = true;
                plan.Files = unique.GetRange(0, MaxFiles);
            }
            else
            {
                plan.Files = unique;
            }
            return plan;
        }
    }
}
