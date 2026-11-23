// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free.
//
// A script's __output__ read in PAGES instead of all at once.
//
// Field 2026-11: a script returned about 130,000 characters of __output__ and the
// whole reply landed in the client's context. A serialized output above the page size
// (max_output_chars when given, else 20,000) is now kept whole in a file named after the
// job (<jobs>\outputs\<job_id>.<page_chars>.json) and the reply carries only page 1.
// The rest is asked for with `horizun_job_status job_id=<id> output_page=N`, which the
// server answers from disk without Revit.
//
// Both halves (the add-in that writes the file and the server that reads a page)
// use THIS class, so the page boundaries and the file names cannot disagree.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class OutputPages
    {
        /// <summary>Default page size, and the size above which an output is paged when the caller gave no max_output_chars.</summary>
        public const int PageChars = 20000;

        /// <summary>The largest output kept for paging, in characters and in UTF-8 bytes. Above it nothing is saved.</summary>
        public const int MaxPagedChars = 4 * 1024 * 1024;
        public const long MaxPagedBytes = 4L * 1024 * 1024;

        public const string FolderName = "outputs";

        /// <summary>A job id is a bare file name without dots: it can never name a path outside the outputs folder.</summary>
        public static bool ValidJobId(string jobId) =>
            !string.IsNullOrWhiteSpace(jobId) && jobId == Path.GetFileName(jobId) &&
            jobId.IndexOfAny(new[] { '/', '\\', ':', '.' }) < 0;

        public static string Folder(string jobsDir) => Path.Combine(jobsDir, FolderName);

        public static string FileName(string jobId, int pageChars) => jobId + "." + pageChars + ".json";

        /// <summary>An output above what output_page reads: saved whole under the job, never read by pages.</summary>
        public static string FullFileName(string jobId) => jobId + ".full.json";

        public static string FilePath(string jobsDir, string jobId, int pageChars) =>
            Path.Combine(Folder(jobsDir), FileName(jobId, pageChars));

        /// <summary>The job an output file (or its temp) belongs to: the name up to the first dot.</summary>
        public static string JobIdOfFile(string fileName)
        {
            string name = Path.GetFileName(fileName ?? "");
            int dot = name.IndexOf('.');
            return dot <= 0 ? name : name.Substring(0, dot);
        }

        /// <summary>The saved output of a job and the page size it was cut with, or null when there is none.</summary>
        public static string Find(string jobsDir, string jobId, out int pageChars)
        {
            pageChars = 0;
            if (!ValidJobId(jobId)) return null;
            string folder = Folder(jobsDir);
            if (!Directory.Exists(folder)) return null;
            foreach (string path in Directory.GetFiles(folder, jobId + ".*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                string[] parts = Path.GetFileName(path).Split('.');
                if (parts.Length == 3 && parts[0] == jobId && int.TryParse(parts[1], out int size) && size >= 1024)
                {
                    pageChars = size;
                    return path;
                }
            }
            return null;
        }

        public static int PageCount(int totalChars, int pageChars = PageChars) =>
            totalChars <= 0 ? 1 : (totalChars + pageChars - 1) / pageChars;

        /// <summary>First character of page <paramref name="page"/> (1-based). Never splits a surrogate pair.</summary>
        public static int Start(string text, int page, int pageChars = PageChars)
        {
            if (page <= 1) return 0;
            long at = (long)(page - 1) * pageChars;
            if (at >= text.Length) return text.Length;
            int i = (int)at;
            if (char.IsLowSurrogate(text[i]) && i > 0 && char.IsHighSurrogate(text[i - 1])) i--;
            return i;
        }

        /// <summary>The text of page <paramref name="page"/> (1-based); empty when the page is past the end.</summary>
        public static string Slice(string text, int page, int pageChars = PageChars)
        {
            if (string.IsNullOrEmpty(text) || page < 1) return string.Empty;
            int from = Start(text, page, pageChars);
            int to = page >= PageCount(text.Length, pageChars) ? text.Length : Start(text, page + 1, pageChars);
            return to <= from ? string.Empty : text.Substring(from, to - from);
        }

        /// <summary>Whether an output of this size is kept for paging at all.</summary>
        public static bool Keepable(int chars, long utf8Bytes) => chars <= MaxPagedChars && utf8Bytes <= MaxPagedBytes;

        /// <summary>Why a job has no saved output, said differently for the three situations a caller can be in.</summary>
        public static string MissingReason(string jobId, bool recordExists, bool finished)
        {
            if (!recordExists)
                return "There is no record of job " + jobId + ": it expired (jobs are kept for a limited time) or never existed, " +
                       "and its saved output went with it. If the reply that carried the first page named a file, read that file directly.";
            if (!finished)
                return "Job " + jobId + " is still running: its output is saved only when it finishes. Ask for this page again later " +
                       "(horizun_job_status job_id=" + jobId + " shows its checkpoints).";
            return "Job " + jobId + " finished and saved no paged output: its __output__ fitted in one page (the reply carried it whole), " +
                   "it was above the " + (MaxPagedBytes / (1024 * 1024)) + " MB that output_page reads (it is then saved whole as " + FullFileName(jobId) + " in the outputs folder, to be opened as a file), or it could not be written. Nothing was read.";
        }
    }
}
