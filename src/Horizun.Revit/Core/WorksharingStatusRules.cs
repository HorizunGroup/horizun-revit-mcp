// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WHO HOLDS THIS ELEMENT, ASKED WITHOUT TAKING IT (FIELD FEEDBACK 2.9).
//
// horizun_manage_worksets operation=status reads, per element: its workset, its
// checkout state (GetCheckoutStatus) and the names Revit's worksharing tooltip
// carries (owner, creator, last changed by, requesters). Nothing is borrowed to
// answer. The arithmetic that turns those readings into a row lives here so the
// rules are provable at a desk:
//   * borrowed_by is the owner ONLY when another user holds it; "me" is owned_by_me.
//   * an unreadable status is unknown, never "not owned" (OwnershipCensus rule).
//   * created_by_me compares Revit user names case-insensitively, trimmed; an
//     empty creator is unknown (null), not "someone else".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class WorksharingStatusRules
    {
        public const int MaxIds = 500;

        /// <summary>Revit's CheckoutStatus enum name to the census state; anything else is unreadable.</summary>
        public static string State(string checkoutStatus)
        {
            switch (checkoutStatus)
            {
                case "OwnedByCurrentUser": return CheckoutState.Me;
                case "OwnedByOtherUser": return CheckoutState.Others;
                case "NotOwned": return CheckoutState.NoOne;
                default: return CheckoutState.Unreadable;
            }
        }

        /// <summary>The other user holding the element, or null when nobody else does (or it is unknown).</summary>
        public static string BorrowedBy(string state, string owner) =>
            state == CheckoutState.Others ? (string.IsNullOrWhiteSpace(owner) ? "(owner name unreadable)" : owner.Trim()) : null;

        /// <summary>True/false when both names read; null when either is empty (unknown is not "someone else").</summary>
        public static bool? SameUser(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return null;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Null when the request is acceptable, else why not.</summary>
        public static string Validate(IList<long> ids)
        {
            if (ids == null || ids.Count == 0) return null;
            if (ids.Count > MaxIds) return "status reads at most " + MaxIds + " ids per call (" + ids.Count + " given); page them.";
            if (ids.Any(i => i <= 0)) return "element ids must be positive.";
            return null;
        }

        /// <summary>Where a NEW element of this kind goes, stated rather than implied.</summary>
        public const string PlacementNote =
            "New model elements and datums go to the ACTIVE workset (set_default changes it and re-reads it). " +
            "View-specific elements - dimensions, tags, text, detail lines - always go to their view's own workset, " +
            "whatever the active workset is.";
    }
}

namespace Horizun.Revit.Core
{
    /// <summary>
    /// horizun_manage_worksets enable / create_local, the Revit-free half. Enabling
    /// worksharing cannot be undone or rehearsed, and the central it writes is the file
    /// a team synchronizes to, so the request is checked here before Revit is touched:
    /// an absolute .rvt path that does not exist yet, in a folder that does, and two
    /// distinct, legal workset names. Nothing is ever overwritten.
    /// </summary>
    public static class WorksharingEnableRules
    {
        public const string DefaultLevelsGrids = "Shared Levels and Grids";
        public const string DefaultWorkset = "Workset1";

        /// <summary>Why <paramref name="path"/> cannot receive a NEW model file, or null.</summary>
        public static string NewFileProblem(string field, string path, bool exists, bool folderExists)
        {
            if (string.IsNullOrWhiteSpace(path)) return field + " is required.";
            if (!IsAbsolute(path)) return field + " must be an absolute path.";
            if (!path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) return field + " must end in .rvt.";
            if (exists) return field + " '" + path + "' already exists; nothing is overwritten.";
            if (!folderExists) return "the folder of " + field + " does not exist.";
            return null;
        }

        /// <summary>enable: null when the central path and the two names are acceptable.</summary>
        public static string EnableProblem(string centralPath, bool exists, bool folderExists, string levelsGrids, string workset)
        {
            string p = NewFileProblem("central_path", centralPath, exists, folderExists);
            if (p != null) return p;
            p = WorksetEditRules.NameProblem(levelsGrids, null);
            if (p != null) return "levels_grids_name: " + p;
            p = WorksetEditRules.NameProblem(workset, null);
            if (p != null) return "name: " + p;
            if (string.Equals(levelsGrids, workset, StringComparison.OrdinalIgnoreCase)) return "levels_grids_name and name must differ.";
            return null;
        }

        /// <summary>create_local: the central must be a workshared central on disk and the local a new, different file.</summary>
        public static string LocalProblem(string centralPath, bool? centralIsWorkshared, string localPath, bool localExists, bool folderExists)
        {
            if (string.IsNullOrWhiteSpace(centralPath) || !IsAbsolute(centralPath)) return "central_path must be the absolute path of the central model.";
            if (centralIsWorkshared != true) return "central_path does not read as a workshared model on disk.";
            string p = NewFileProblem("local_path", localPath, localExists, folderExists);
            if (p != null) return p;
            if (SamePath(centralPath, localPath)) return "local_path must differ from central_path.";
            return null;
        }

        /// <summary>Postcondition problems after enable; empty when every fact re-read as planned.</summary>
        public static List<string> EnableVerdict(bool isWorkshared, IEnumerable<string> userWorksets, string levelsGrids, string workset,
                                                 string documentPath, string centralPath, bool? fileWorkshared, bool? fileCentral)
        {
            var problems = new List<string>();
            var names = new HashSet<string>(userWorksets ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (!isWorkshared) problems.Add("the document does not read as workshared");
            if (!names.Contains(levelsGrids)) problems.Add("workset '" + levelsGrids + "' is missing");
            if (!names.Contains(workset)) problems.Add("workset '" + workset + "' is missing");
            if (!SamePath(documentPath, centralPath)) problems.Add("the document path is '" + documentPath + "', not the central path");
            if (fileWorkshared != true) problems.Add("the file on disk does not read as workshared");
            if (fileCentral != true) problems.Add("the file on disk does not read as a central");
            return problems;
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }

        private const char Back = (char)92;

        private static string Norm(string p) => p.Trim().Replace('/', Back).TrimEnd(Back);

        private static bool IsAbsolute(string p) =>
            p.Length > 2 && ((char.IsLetter(p[0]) && p[1] == ':' && (p[2] == Back || p[2] == '/')) || (p[0] == Back && p[1] == Back));
    }
}
