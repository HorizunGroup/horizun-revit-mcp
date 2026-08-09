// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Has this file been assigned a cloud folder yet, or is its upload still
// pending? - read out of the Desktop Connector's own write-ahead log (5.15).
//
// Copying into the Desktop Connector folder and hashing the copy proves the
// LOCAL CACHE, not the cloud: the upload is a later async step that fails
// under throttling ("Too many people or processes appear to be accessing this
// service", an ~11-minute circuit breaker) - measured in the field: 3 of 8
// families silently unuploaded, caught only by a human screenshot. The one
// local record that answers the question is the connector's WAL
// (*.properties-log.db): when an upload completes, the file's Name appears
// beside a ParentFolderUrn; while it is pending or failed, it does not. An
// external script (extract_wal_links.py) proved the read; this makes it a
// rule the bridge can apply.
//
// TWO HONESTY LINES, drawn here and repeated in the reply:
//   * A hit is the CONNECTOR'S TESTIMONY read off this machine - the record
//     that appears when its upload completes - not a cloud API check.
//   * A miss is absence of EVIDENCE, never proof of absence: pending, failed,
//     or synced under another name all look identical from here. And a WAL
//     that cannot be found or read is UNKNOWN, not "not uploaded".
//
// ORG-NEUTRAL, unlike the script it generalises: the script matched PRD-*
// because those were one client's families. Which names to look for is the
// caller's argument; nothing here knows any organisation's prefix.
//
// Revit-free: the bytes come from the command's IO; everything decided about
// them - the decode, the pair extraction, the name matching - is provable here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    /// <summary>One Name-beside-ParentFolderUrn pair found in a WAL file.</summary>
    public sealed class WalHit
    {
        public string Name;        // as recorded, e.g. "PRD-CAMARA.rfa"
        public string FolderUrn;   // the co.XXX tail of urn:adsk.wipprod:fs.folder:co.XXX
        public string SourceFile;  // which WAL file it was read from
    }

    /// <summary>The verdict for one requested name.</summary>
    public sealed class AccNameStatus
    {
        public string Requested;
        public bool HasFolderUrn;
        public List<string> MatchedNames = new List<string>();
        public List<string> FolderUrns = new List<string>();
        public List<string> SourceFiles = new List<string>();
        /// <summary>Set when the name itself cannot participate in matching.</summary>
        public string Note;
    }

    public static class AccUploadWal
    {
        /// <summary>
        /// The WAL is JSON escaped inside JSON, so the quotes around each value are
        /// backslash-escaped in the raw bytes. The pair this matches is literally
        /// ParentFolderUrn\":\"urn:adsk.wipprod:fs.folder:co.X\",\"Name\":\"file\" -
        /// the same shape the field-proven script matched, with the client-specific
        /// name prefix generalised away.
        /// </summary>
        private static readonly Regex Pair = new Regex(
            @"ParentFolderUrn\\"":\\""urn:adsk\.wipprod:fs\.folder:(co\.[A-Za-z0-9_\-]+)\\"",\\""Name\\"":\\""([^""\\]+?)\\""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The WAL is a binary log: NUL bytes interleave the text. Strip them and
        /// read the rest as Latin-1 - every byte is a character, nothing throws,
        /// and the ASCII the pattern needs comes through untouched.
        /// </summary>
        public static string Decode(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return "";
            var kept = new byte[raw.Length];
            int n = 0;
            foreach (byte b in raw)
                if (b != 0) kept[n++] = b;
            return Encoding.GetEncoding("ISO-8859-1").GetString(kept, 0, n);
        }

        public static List<WalHit> Scan(string sourceFile, string decoded)
        {
            var hits = new List<WalHit>();
            if (string.IsNullOrEmpty(decoded)) return hits;
            foreach (Match m in Pair.Matches(decoded))
                hits.Add(new WalHit { FolderUrn = m.Groups[1].Value, Name = m.Groups[2].Value, SourceFile = sourceFile });
            return hits;
        }

        /// <summary>
        /// Matching is canonical - letters and digits only, case-insensitive - the
        /// same normalisation the field script proved against real WAL content,
        /// where the recorded name and the name a human types differ in spacing,
        /// dashes and case. A requested name may come with or without its
        /// extension; stems are compared alongside full names, and every matched
        /// display name is reported so a cross-extension match is visible rather
        /// than silent.
        /// </summary>
        public static List<AccNameStatus> Match(IList<string> requested, IList<WalHit> hits)
        {
            var results = new List<AccNameStatus>();
            if (requested == null) return results;
            hits = hits ?? new List<WalHit>();

            var prepared = hits.Select(h => new
            {
                Hit = h,
                CanonFull = Canon(h.Name),
                CanonStem = Canon(Stem(h.Name))
            }).ToList();

            foreach (string r in requested)
            {
                var s = new AccNameStatus { Requested = r };
                results.Add(s);

                string rc = Canon(r), rcStem = Canon(Stem(r));
                if (rcStem.Length == 0)
                {
                    s.Note = "the name contains no letters or digits to match on, so it cannot be looked up.";
                    continue;
                }

                foreach (var p in prepared)
                {
                    bool match = string.Equals(rc, p.CanonFull, StringComparison.Ordinal) ||
                                 string.Equals(rcStem, p.CanonStem, StringComparison.Ordinal);
                    if (!match) continue;
                    s.HasFolderUrn = true;
                    if (!s.MatchedNames.Contains(p.Hit.Name)) s.MatchedNames.Add(p.Hit.Name);
                    if (!s.FolderUrns.Contains(p.Hit.FolderUrn)) s.FolderUrns.Add(p.Hit.FolderUrn);
                    if (!s.SourceFiles.Contains(p.Hit.SourceFile)) s.SourceFiles.Add(p.Hit.SourceFile);
                }
            }
            return results;
        }

        /// <summary>letters and digits, lower-cased; everything else dropped.</summary>
        public static string Canon(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>
        /// The name without a trailing file extension - a final dot followed by 1-4
        /// LETTERS ("rfa", "rvt", "dwg"). Letters only, on purpose: a size like
        /// "Caja 1.5x2.5" ends in ".5", and a rule that accepted digits would
        /// truncate the size instead of stripping an extension.
        /// </summary>
        public static string Stem(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1) return name;
            string ext = name.Substring(dot + 1);
            if (ext.Length > 4) return name;
            foreach (char c in ext)
                if (!char.IsLetter(c)) return name;
            return name.Substring(0, dot);
        }

        /// <summary>The ACC Docs URL for a folder, exactly as the field script built it.</summary>
        public static string BuildUrl(string projectId, string coUrn)
        {
            if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(coUrn)) return null;
            return "https://acc.autodesk.com/docs/files/projects/" + projectId +
                   "?folderUrn=" + Uri.EscapeDataString("urn:adsk.wipprod:fs.folder:" + coUrn) +
                   "&viewModel=detail&moduleId=folders";
        }
    }
}
