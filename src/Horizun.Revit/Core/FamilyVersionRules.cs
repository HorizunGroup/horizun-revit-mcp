// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE VERSION OF A FAMILY FILE, checked BEFORE Revit is asked to open or load it.
//
// field cycle ELE and HS (2026-10-24, T1/L3/H1): the HUB's double outlet was saved in
// Revit 2025 and the five plumbing families in 2024; the model was 2023. Revit
// answered each load with its own format error, after the agent had planned around
// the family, and the agents retried (38 create_family and 8 file_info calls in HS).
// A newer .rfa can NEVER be loaded by an older Revit, and the API has no downgrade:
// saying so up front, with the years and what to do instead, is the whole fix.
//
// The year is read with BasicFileInfo (no open) by the Revit half; this half decides
// and words it. An UNREADABLE version is not refused here - an older family loads
// fine and Revit is the judge of a header it cannot report - but it is never called
// "checked". Alternates are files of the same family name (year tokens and
// punctuation ignored) whose header reads this Revit's year or older.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Horizun.Revit.Core
{
    public enum FamilyVersionVerdict { Same, Older, Newer, Unknown }

    public sealed class FamilyAlternate
    {
        public string Path;
        public string SavedIn;
    }

    public static class FamilyVersionRules
    {
        public const string Reason = "family_saved_in_newer_version";
        public const int MaxAlternatesExamined = 400;

        public static FamilyVersionVerdict Judge(string fileVersion, string hostVersion)
        {
            if (!OpenDecision.TryYear(fileVersion, out int file) || !OpenDecision.TryYear(hostVersion, out int host))
                return FamilyVersionVerdict.Unknown;
            return file == host ? FamilyVersionVerdict.Same : file < host ? FamilyVersionVerdict.Older : FamilyVersionVerdict.Newer;
        }

        /// <summary>The refusal text for a newer family, naming both years, the alternates found and what else works.</summary>
        public static string NewerMessage(string fileName, string fileVersion, string hostVersion, IList<FamilyAlternate> alternates)
        {
            var sb = new StringBuilder();
            sb.Append(Reason).Append(": '").Append(fileName).Append("' was saved in a NEWER Revit (").Append(fileVersion)
              .Append(") than this one (").Append(hostVersion).Append("). Revit cannot load a family from a newer version and ")
              .Append("the API has no way to downgrade it, so nothing was opened or loaded. ");
            if (alternates != null && alternates.Count > 0)
                sb.Append("Same family saved in ").Append(hostVersion).Append(" or older, next to it: ")
                  .Append(string.Join("; ", alternates.Take(5).Select(a => a.Path + " (" + a.SavedIn + ")")))
                  .Append(". Load one of those instead. ");
            else
                sb.Append("No copy of it saved in ").Append(hostVersion).Append(" or older was found next to it. ");
            sb.Append("Otherwise author an equivalent here with horizun_create_family (Pro edition), or ask for the family saved in Revit ")
              .Append(hostVersion).Append(" (re-saving it in a newer Revit does not help).");
            return sb.ToString();
        }

        private static readonly Regex YearToken = new Regex(@"(?<![0-9])(19|20)[0-9]{2}(?![0-9])|\b[Rr][Vv][Tt]?[0-9]{2}\b|\b[Rr][0-9]{2}\b|\b[Vv][0-9]+\b", RegexOptions.Compiled);
        private static readonly Regex NotAlnum = new Regex(@"[^0-9a-z]+", RegexOptions.Compiled);

        /// <summary>The family name with year/version tokens, case, accents and punctuation removed.</summary>
        public static string NormalizeStem(string stem)
        {
            if (string.IsNullOrWhiteSpace(stem)) return "";
            string s = YearToken.Replace(stem, " ");
            s = RemoveDiacritics(s).ToLowerInvariant();
            return NotAlnum.Replace(s, "");
        }

        public static bool SameFamilyName(string stemA, string stemB)
        {
            string a = NormalizeStem(stemA), b = NormalizeStem(stemB);
            return a.Length > 0 && a == b;
        }

        /// <summary>A candidate is an alternate when it is the same family name and its year is this Revit's or older.</summary>
        public static bool IsAlternate(string requestedStem, string candidateStem, string candidateVersion, string hostVersion)
        {
            if (!SameFamilyName(requestedStem, candidateStem)) return false;
            FamilyVersionVerdict v = Judge(candidateVersion, hostVersion);
            return v == FamilyVersionVerdict.Same || v == FamilyVersionVerdict.Older;
        }

        private static string RemoveDiacritics(string s)
        {
            string formD = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (char c in formD)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
