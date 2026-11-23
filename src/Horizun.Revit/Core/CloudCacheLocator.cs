// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// save_document save_local on a CLOUD model (Autodesk Docs / ACC). Its PathName
// is not a file ("Autodesk Docs://..."), so the save could not be measured and
// came back verification_unknown - although Revit DOES write a local copy:
//
//   %LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <year>\CollaborationCache\
//       <account>\<project GUID>\<model GUID>.rvt
//
// The account folder belongs to the Autodesk user signed in to Revit. Signed in
// as somebody else, Revit keeps a SECOND cache under that user's folder, and the
// file from the earlier session stops being the one that is written - a cause of
// "my local save did not take" that nobody can see from inside Revit.
//
// This half is Revit-free: it lists the candidates, picks the one the save wrote,
// and says whether the account folder is not the current user's. The live check
// against a real cloud model is pending a model the owner authorises; the folder
// structure is exercised by tests.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class CloudCacheCandidate
    {
        public string Path;
        public string AccountFolder;
        public bool CurrentAccount;
        public DateTime? WrittenUtc;
        public long? Bytes;
    }

    public sealed class CloudSaveVerdict
    {
        public string Path;                  // the file judged; null when none exists
        public bool? Changed;                // null: unmeasurable
        public bool AccountMismatch;         // the written/only copy is not under the current account folder
        public bool SeveralAccounts;         // the model is cached under more than one account folder
        public string Warning;
        public List<CloudCacheCandidate> After = new List<CloudCacheCandidate>();
    }

    public static class CloudCacheLocator
    {
        public static string CacheRoot(string localAppData, string revitYear)
            => System.IO.Path.Combine(localAppData ?? "", "Autodesk", "Revit", "Autodesk Revit " + revitYear, "CollaborationCache");

        /// <summary>Every cached copy of this model, under any account folder. Case of GUIDs and account ignored.</summary>
        public static List<CloudCacheCandidate> Find(string root, string account, string projectGuid, string modelGuid)
        {
            var list = new List<CloudCacheCandidate>();
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(projectGuid) || string.IsNullOrWhiteSpace(modelGuid)) return list;
            if (!Directory.Exists(root)) return list;
            string file = modelGuid.Trim() + ".rvt";
            foreach (string accountDir in SafeDirs(root))
            {
                string projectDir = SafeDirs(accountDir).FirstOrDefault(d =>
                    string.Equals(System.IO.Path.GetFileName(d), projectGuid.Trim(), StringComparison.OrdinalIgnoreCase));
                if (projectDir == null) continue;
                string hit = SafeFiles(projectDir).FirstOrDefault(f =>
                    string.Equals(System.IO.Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
                if (hit == null) continue;
                string folder = System.IO.Path.GetFileName(accountDir);
                var c = new CloudCacheCandidate
                {
                    Path = hit, AccountFolder = folder,
                    CurrentAccount = !string.IsNullOrWhiteSpace(account) && string.Equals(folder, account.Trim(), StringComparison.OrdinalIgnoreCase)
                };
                try { var info = new FileInfo(hit); c.WrittenUtc = info.LastWriteTimeUtc; c.Bytes = info.Length; } catch { }
                list.Add(c);
            }
            return list.OrderByDescending(c => c.CurrentAccount).ThenBy(c => c.AccountFolder, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Which copy the save wrote, and whether it changed (time or size). The current account's
        /// copy is judged first; when it did not change but another account's did, that one is the
        /// write and the mismatch is reported.
        /// </summary>
        public static CloudSaveVerdict Judge(IList<CloudCacheCandidate> before, IList<CloudCacheCandidate> after, string account)
        {
            var v = new CloudSaveVerdict { After = after?.ToList() ?? new List<CloudCacheCandidate>() };
            before = before ?? new List<CloudCacheCandidate>();
            v.SeveralAccounts = v.After.Select(c => c.AccountFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            if (v.After.Count == 0)
            {
                v.Warning = "no cached copy of this model was found under any account folder of the collaboration cache.";
                return v;
            }
            CloudCacheCandidate changed = v.After.FirstOrDefault(a => Moved(before, a) == true && a.CurrentAccount)
                                       ?? v.After.FirstOrDefault(a => Moved(before, a) == true);
            CloudCacheCandidate pick = changed ?? v.After.FirstOrDefault(a => a.CurrentAccount) ?? v.After[0];
            v.Path = pick.Path;
            v.Changed = changed != null ? true : Moved(before, pick);
            v.AccountMismatch = !pick.CurrentAccount;
            if (v.AccountMismatch)
                v.Warning = "the cached copy is under account folder '" + pick.AccountFolder + "', not the signed-in user's" +
                    (string.IsNullOrWhiteSpace(account) ? " (the current Autodesk user id could not be read)" : " ('" + account + "')") +
                    ": the Autodesk user changed between sessions, so Revit writes a different cache. Sign in as the user that " +
                    "opened the model, or reopen it from the cloud.";
            else if (v.SeveralAccounts)
                v.Warning = "this model is cached under " + v.After.Count + " account folders (" +
                    string.Join(", ", v.After.Select(c => c.AccountFolder)) + "): another Autodesk user worked on it on this machine; " +
                    "only the current user's copy was judged.";
            return v;
        }

        /// <summary>True when the candidate's time or size differ from the same file before; null when it was not there before.</summary>
        private static bool? Moved(IList<CloudCacheCandidate> before, CloudCacheCandidate after)
        {
            CloudCacheCandidate b = before.FirstOrDefault(x => string.Equals(x.Path, after.Path, StringComparison.OrdinalIgnoreCase));
            if (b == null) return after.WrittenUtc.HasValue ? true : (bool?)null;   // created by the save
            if (!b.WrittenUtc.HasValue || !after.WrittenUtc.HasValue) return null;
            return b.WrittenUtc.Value != after.WrittenUtc.Value || b.Bytes != after.Bytes;
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            try { return Directory.GetDirectories(dir); } catch { return new string[0]; }
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); } catch { return new string[0]; }
        }
    }
}
