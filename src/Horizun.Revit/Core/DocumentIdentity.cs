// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHICH document is this, and is it the one we mean.
//
// Reference equality does not answer that question. Revit's Application.Documents
// hands out managed wrappers, and enumerating the set does not guarantee the same
// instance you got from ActiveUIDocument.Document. horizun_health compared them
// with ReferenceEquals and therefore reported, on a real model, an active document
// by name while marking all three open documents is_active=false. The reply
// contradicted itself in the same payload.
//
// Identity here is built from what actually distinguishes a document: its path
// (normalized), its title, and whether it is workshared. Titles are NOT unique -
// two files in different folders share one routinely, and a workshared local and
// its central can too - so the matcher has three outcomes, not two:
//
//     matched  -> exactly one candidate is this document
//     none     -> no candidate is
//     ambiguous-> more than one candidate fits, and we CANNOT tell which
//
// Ambiguous is reported as unknown (null), never resolved by picking the first.
// A mutation that guesses which document it is about is the most expensive bug
// this codebase can ship, so the guess is not available.
//
// No `using Autodesk.*`: the rule is provable without Revit, and it has to be.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    /// <summary>What we know about one document, independent of any Revit object.</summary>
    public sealed class DocIdentity
    {
        public string Title { get; set; }

        /// <summary>Full path as Revit reported it. Empty for a document never saved.</summary>
        public string Path { get; set; }

        public bool IsWorkshared { get; set; }

        /// <summary>
        /// Cloud/worksharing identity when available (model GUID). The only field that is
        /// genuinely unique, which is why it wins when both sides have it.
        /// </summary>
        public string ModelGuid { get; set; }

        public string PathNormalized => NormalizePath(Path);

        /// <summary>
        /// Case- and separator-insensitive path comparison. Revit reports cloud paths with
        /// forward slashes and disk paths with backslashes, and Windows paths are not
        /// case sensitive, so a raw string compare produces false negatives.
        /// </summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string p = path.Trim().Replace('\\', '/');
            while (p.EndsWith("/", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 1);
            return p.ToLowerInvariant();
        }

        /// <summary>Which Revit this document is open in. Part of the fingerprint.</summary>
        public string RevitYear { get; set; }

        /// <summary>A short human description for messages. Never throws.</summary>
        public string Describe()
        {
            string t = string.IsNullOrEmpty(Title) ? "(untitled)" : Title;
            return string.IsNullOrEmpty(Path) ? t + " (never saved)" : t + " [" + Path + "]";
        }

        /// <summary>
        /// A stable key for "this exact document in this exact Revit". Used to bind a
        /// confirmation token, so an approval for one model cannot execute against another.
        ///
        /// Deliberately includes the Revit year: the same file open in 2025 and in 2026 is
        /// two different documents for this purpose, and approving work in one must not
        /// authorise it in the other.
        ///
        /// A document with no path and no GUID falls back to its title, which is weak - so
        /// callers that need certainty must also check DocumentMatcher for ambiguity rather
        /// than trusting this key alone.
        /// </summary>
        public string Fingerprint()
        {
            char us = (char)31;
            return string.Join(us.ToString(), new[]
            {
                "rvt:" + (RevitYear ?? "?"),
                "guid:" + (string.IsNullOrWhiteSpace(ModelGuid) ? "-" : ModelGuid.ToLowerInvariant()),
                "path:" + (PathNormalized ?? "-"),
                "title:" + (string.IsNullOrEmpty(Title) ? "-" : Title.ToLowerInvariant())
            });
        }

        /// <summary>
        /// The fingerprint rendered for a reply: a short digest instead of the raw key.
        ///
        /// The raw one joins its parts with a control character, which is right for
        /// comparison and wrong for a payload - it arrived in a real response as
        /// "rvt:2026guid:...path:...", escape sequences and all. The parts it
        /// encodes (title, path, guid) are already published as their own fields, so the
        /// digest costs the reader nothing and stays comparable between two replies.
        /// </summary>
        public string FingerprintDigest()
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Fingerprint()));
                return BitConverter.ToString(h, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    public enum DocMatchOutcome
    {
        None,
        Matched,
        Ambiguous
    }

    public sealed class DocMatch
    {
        public DocMatchOutcome Outcome { get; internal set; }

        /// <summary>Index into the candidate list when Outcome is Matched; otherwise -1.</summary>
        public int Index { get; internal set; } = -1;

        /// <summary>What settled it: "model guid", "path" or "title". Null unless Matched.</summary>
        public string Basis { get; internal set; }

        /// <summary>Indices that fit when Outcome is Ambiguous. Empty otherwise.</summary>
        public List<int> Candidates { get; internal set; } = new List<int>();

        /// <summary>
        /// true / false per candidate index, or NULL when the match was ambiguous - the
        /// honest answer to "is this one it?" when we cannot tell.
        /// </summary>
        public bool? IsMatch(int index)
        {
            if (Outcome == DocMatchOutcome.Ambiguous && Candidates.Contains(index)) return null;
            return Outcome == DocMatchOutcome.Matched && Index == index;
        }

        public string Explain()
        {
            switch (Outcome)
            {
                case DocMatchOutcome.Matched:
                    return "Identified by " + Basis + ".";
                case DocMatchOutcome.Ambiguous:
                    return Candidates.Count + " open documents fit this identity and nothing available " +
                           "distinguishes them, so which one it is cannot be determined. Reported as unknown " +
                           "rather than guessed.";
                default:
                    return "No open document matches this identity.";
            }
        }
    }

    public static class DocumentMatcher
    {
        /// <summary>
        /// Strong equality for an operation that will immediately target one document.
        /// Titles are deliberately absent: two open files may have the same title, and an
        /// unsaved/detached document with no path is not made identifiable by that fact.
        /// Native Revit identity is checked by the caller before reaching this pure rule;
        /// here only cloud GUID pairs or normalized local paths can prove equality.
        /// </summary>
        public static bool SameStableIdentity(DocIdentity a, DocIdentity b,
                                              string projectGuidA = null, string projectGuidB = null)
        {
            if (a == null || b == null) return false;

            bool eitherCloud = !string.IsNullOrWhiteSpace(a.ModelGuid) ||
                               !string.IsNullOrWhiteSpace(b.ModelGuid) ||
                               !string.IsNullOrWhiteSpace(projectGuidA) ||
                               !string.IsNullOrWhiteSpace(projectGuidB);
            if (eitherCloud)
                return !string.IsNullOrWhiteSpace(a.ModelGuid) &&
                       !string.IsNullOrWhiteSpace(b.ModelGuid) &&
                       !string.IsNullOrWhiteSpace(projectGuidA) &&
                       !string.IsNullOrWhiteSpace(projectGuidB) &&
                       string.Equals(a.ModelGuid, b.ModelGuid, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(projectGuidA, projectGuidB, StringComparison.OrdinalIgnoreCase);

            return a.PathNormalized != null && b.PathNormalized != null &&
                   string.Equals(a.PathNormalized, b.PathNormalized, StringComparison.Ordinal);
        }

        /// <summary>
        /// Find <paramref name="target"/> among <paramref name="candidates"/>, strongest
        /// evidence first: model GUID, then normalized path, then title. A tier that
        /// produces more than one hit is AMBIGUOUS and stops there - falling through to a
        /// weaker tier could only make the answer less certain, not more.
        /// </summary>
        public static DocMatch Find(IList<DocIdentity> candidates, DocIdentity target)
        {
            var result = new DocMatch { Outcome = DocMatchOutcome.None };
            if (candidates == null || candidates.Count == 0 || target == null) return result;

            // Tier 1: model GUID. Unique when present on both sides.
            var hits = IndicesWhere(candidates, c =>
                !string.IsNullOrWhiteSpace(target.ModelGuid) &&
                string.Equals(c.ModelGuid, target.ModelGuid, StringComparison.OrdinalIgnoreCase));
            if (hits.Count > 0) return Settle(result, hits, "model guid");

            // Tier 2: normalized path. Unique on disk; empty for an unsaved document, which
            // is why it cannot be the only tier.
            hits = IndicesWhere(candidates, c =>
                target.PathNormalized != null && c.PathNormalized == target.PathNormalized);
            if (hits.Count > 0) return Settle(result, hits, "path");

            // Tier 3: title. Routinely NOT unique - that is the whole reason Ambiguous exists.
            hits = IndicesWhere(candidates, c =>
                !string.IsNullOrEmpty(target.Title) &&
                string.Equals(c.Title, target.Title, StringComparison.OrdinalIgnoreCase));
            if (hits.Count > 0) return Settle(result, hits, "title");

            return result;
        }

        private static DocMatch Settle(DocMatch result, List<int> hits, string basis)
        {
            if (hits.Count == 1)
            {
                result.Outcome = DocMatchOutcome.Matched;
                result.Index = hits[0];
                result.Basis = basis;
            }
            else
            {
                result.Outcome = DocMatchOutcome.Ambiguous;
                result.Candidates = hits;
            }
            return result;
        }

        private static List<int> IndicesWhere(IList<DocIdentity> items, Func<DocIdentity, bool> predicate)
        {
            var found = new List<int>();
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && predicate(items[i])) found.Add(i);
            return found;
        }
    }
}
