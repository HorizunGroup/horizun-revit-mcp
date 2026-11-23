// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free.
//
// WHAT A WRITE OF THIS SESSION CREATED, BY UNIQUEID (R2U M-A).
//
// The identity guard calls an element "ours" when it carries the Horizun mark AND the mark is believable (HrzOwnershipRules.TrustMark):
// a mark that claims to be from THIS session must correspond to an element a write of this session added. The previous ledger remembered
// ElementIds, which a synchronize with central renumbers and which Revit can hand to another element, so an old id could point at somebody
// else's marked copy and be believed. This one remembers UniqueIds, which survive a synchronize, a save, a Save As and a reload.
//
// Keyed by the document key (ChangeWatch.Key: the path, or title#hash while unsaved). When the key changes (the first save gives an unsaved
// document a path; Save As changes it) QueryCacheLifecycle calls Rename from the DocumentSavingAs / DocumentSavedAs pair, so what is ours
// stays ours. The last WriteCap writes of each document are kept; an older creation is no longer remembered and the element is refused
// (fail closed: the caller sends the unique_id).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class SessionCreationLedger
    {
        /// <summary>At most this many writes are remembered per document; the oldest is dropped first.</summary>
        public const int WriteCap = 500;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, List<HashSet<string>>> Writes = new Dictionary<string, List<HashSet<string>>>(StringComparer.Ordinal);

        /// <summary>At most this many duplicated elements are remembered per document (INT-9 m-3).</summary>
        public const int DuplicatedCap = 20000;

        // INT-9 m-3. Elements a write DUPLICATED from somebody else's view (a legend duplicated with its detailing carries the source's annotations): the
        // write did not create them, so they are neither stamped nor believed. Same belief as copy_between_documents, element by element.
        private static readonly Dictionary<string, HashSet<string>> Duplicated = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>One committed write that ADDED these elements (named by UniqueId). Nothing is recorded for an empty list.</summary>
        public static void Record(string documentKey, IEnumerable<string> addedUniqueIds)
        {
            if (string.IsNullOrEmpty(documentKey) || addedUniqueIds == null) return;
            var uids = new HashSet<string>(addedUniqueIds.Where(u => !string.IsNullOrEmpty(u)), StringComparer.Ordinal);
            if (uids.Count == 0) return;
            lock (Gate)
            {
                if (Duplicated.TryGetValue(documentKey, out HashSet<string> copies)) uids.ExceptWith(copies);   // INT-9 m-3: a duplicate is not a creation
                if (uids.Count == 0) return;
                if (!Writes.TryGetValue(documentKey, out List<HashSet<string>> list)) Writes[documentKey] = list = new List<HashSet<string>>();
                list.Add(uids);
                while (list.Count > WriteCap) list.RemoveAt(0);
            }
        }

        /// <summary>
        /// INT-9 m-3. These elements were DUPLICATED by the write that is running (not created by it): they will not be stamped and will not enter the
        /// ledger of creations. Call it from inside the write, before the dispatcher's stamp. Bounded per document by <see cref="DuplicatedCap"/>.
        /// </summary>
        public static void NoteDuplicated(string documentKey, IEnumerable<string> uniqueIds)
        {
            if (string.IsNullOrEmpty(documentKey) || uniqueIds == null) return;
            lock (Gate)
            {
                if (!Duplicated.TryGetValue(documentKey, out HashSet<string> set)) Duplicated[documentKey] = set = new HashSet<string>(StringComparer.Ordinal);
                foreach (string u in uniqueIds)
                {
                    if (set.Count >= DuplicatedCap) break;
                    if (!string.IsNullOrEmpty(u)) set.Add(u);
                }
            }
        }

        /// <summary>The element was duplicated by a write of this session (see <see cref="NoteDuplicated"/>): it is not ours to stamp or to believe.</summary>
        public static bool IsDuplicated(string documentKey, string uniqueId)
        {
            if (string.IsNullOrEmpty(documentKey) || string.IsNullOrEmpty(uniqueId)) return false;
            lock (Gate) return Duplicated.TryGetValue(documentKey, out HashSet<string> set) && set.Contains(uniqueId);
        }

        /// <summary>A write of this session that this ledger still remembers added the element with this UniqueId.</summary>
        public static bool Created(string documentKey, string uniqueId)
        {
            if (string.IsNullOrEmpty(documentKey) || string.IsNullOrEmpty(uniqueId)) return false;
            lock (Gate) return Writes.TryGetValue(documentKey, out List<HashSet<string>> list) && list.Any(w => w.Contains(uniqueId));
        }

        /// <summary>The document is now known under another key (the first save, Save As): what it remembers follows it.</summary>
        public static void Rename(string oldKey, string newKey)
        {
            if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey) || oldKey == newKey) return;
            lock (Gate)
            {
                // R2Y m-3. The destination holds only what the renamed document created: what a closed document left under that path is not its own.
                LedgerRenameRules.MoveAll(oldKey, newKey, Writes, Duplicated);
            }
        }

        /// <summary>
        /// <see cref="Record"/> for the write of a tool, if that tool's creations are ours at all (R2V M-1): what a script or a copy between
        /// documents adds is NOT remembered here, so a mark of this session on it is read as a copy. It stays in the change ledger.
        /// </summary>
        public static void RecordFor(string tool, string documentKey, IEnumerable<string> addedUniqueIds)
        {
            if (HrzOwnershipRules.CreationsAreOurs(tool)) Record(documentKey, addedUniqueIds);
        }

        /// <summary>Every UniqueId this ledger still remembers for the document (a copy).</summary>
        public static IReadOnlyCollection<string> All(string documentKey)
        {
            if (string.IsNullOrEmpty(documentKey)) return new string[0];
            lock (Gate) return Writes.TryGetValue(documentKey, out List<HashSet<string>> list) ? list.SelectMany(w => w).Distinct().ToArray() : new string[0];
        }

        internal static void ResetForTests() { lock (Gate) { Writes.Clear(); Duplicated.Clear(); } }
    }
}
