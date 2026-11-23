// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF THE "IDS MAY HAVE CHANGED" NOTICE.
//
// Synchronizing with central can renumber ElementIds (field 2026-10-30: notes 7166946-7166948 became
// 7167882-7167884, a tag 7157896 became 7166659), and an id read BEFORE that point can then name an
// unrelated element. Health therefore says so: a synchronize happened after the last time this session
// read element ids, so ids held from before it must be re-read or validated. It is a notice, not a block.
// Any synchronize counts - the person's own, the Revit UI's, or the bridge's sync_with_central - because
// the document event fires for all of them.
// So does "Reload Latest" (R2U m-4).
//
// WHAT CLEARS IT (R2U M-A). Not any read: a query of the levels says nothing about the three text notes whose ids a person holds. The
// notice clears only when the ids this session HANDED OUT before the synchronize (every {element_id|id, unique_id} pair of the replies of
// the id-returning reads) have each been revalidated afterwards, by horizun_selection_context operation=validate_ids (a row that proves
// something: coincide, cambio or no_existe) or by a read that returns that same id with its unique_id; and at least one such revalidation
// happened. Ids the session never received with a unique_id are not tracked: the guard does not depend on this notice for them (a category
// or type alone is refused in a collaborative model), and the note says so.
//
// This notice no longer decides the identity check (that is the collaborative rule); it is the prompt in health.id_freshness.
//
// R2V. (M-3) The ids a WRITE hands out are tracked exactly like those of a read: three notes created, a synchronize, a query of the levels does
// not clear the notice. An element a write deleted leaves the tracking. (m-a) An id that comes back after a synchronize with ANOTHER unique_id is
// reported in the reply as ids_changed. (m-b) A row of a linked model (link_instance_id, source_kind "link") is never taken for a host id.
// (m-c) The state of a document is forgotten when it closes; at most MaxTrackedIds ids are held per document, and past that the notice says it
// can no longer list what to re-read and is turned off by validate_ids on the ids the caller is going to write.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class IdFreshnessRules
    {
        /// <summary>At most this many ids are tracked per document; beyond it a validate_ids that proves something is what turns the notice off.</summary>
        public const int MaxTrackedIds = 50000;
        /// <summary>The ids past the cap are remembered (without their unique_id) up to this many; beyond it a validate_ids can no longer be told apart from an id the session never handed out, and is accepted.</summary>
        public const int MaxOverflowIds = 1000000;
        private const int MaxNodesWalked = 2000000;
        private const int MaxChangedListed = 20;

        private sealed class State
        {
            public DateTime? SyncedUtc;
            public DateTime? ReadUtc;
            public int SyncsSinceRead;
            public readonly Dictionary<long, string> Held = new Dictionary<long, string>();
            public HashSet<long> Unverified = new HashSet<long>();
            public bool Revalidated;
            public bool Overflow;
            public bool ValidatedByTool;
            /// <summary>R2Y m-6. The ids handed out PAST the tracking cap (just the ids: their unique_id is not kept). A validate_ids row on an id that is neither here nor in Held proves nothing about what was handed out.</summary>
            public readonly HashSet<long> OverflowIds = new HashSet<long>();
            public bool OverflowIdsTruncated;
        }

        private struct Pair
        {
            public long Id;
            public string UniqueId;
            public bool Gone;
            /// <summary>A validate_ids row that said no_existe for this id (R2W m-3).</summary>
            public bool Missing;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, State> Documents = new Dictionary<string, State>(StringComparer.Ordinal);

        // The read tools whose answers carry element ids a caller may hold on to.
        private static readonly HashSet<string> IdReaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "horizun_query_model", "horizun_list_elements", "horizun_query_planimetry", "horizun_query_relations",
            "horizun_selection_context", "horizun_query_structure", "horizun_query_dimensions", "horizun_query_detail_2d",
            "horizun_measure_geometry", "horizun_get_dimension_references", "horizun_model_scan"
        };

        public static bool ReadsIds(string tool) => tool != null && IdReaders.Contains(tool);

        /// <summary>
        /// Does this call, with this operation, hand the caller element ids to hold? validate_ids does not: it
        /// answers about ids the caller already holds (it can revalidate them, see <see cref="NoteReply"/>).
        /// </summary>
        public static bool ReadsIds(string tool, string operation) =>
            ReadsIds(tool) && !IsValidateIds(tool, operation);

        public static bool IsValidateIds(string tool, string operation) =>
            tool == "horizun_selection_context" && string.Equals(operation, "validate_ids", StringComparison.Ordinal);

        public static void NoteSynchronized(string documentKey, DateTime utc)
        {
            if (string.IsNullOrEmpty(documentKey)) return;
            lock (Gate)
            {
                State s = Get(documentKey);
                s.SyncedUtc = utc;
                s.SyncsSinceRead++;
                s.Unverified = new HashSet<long>(s.Held.Keys);   // everything handed out before this point may have been renumbered
                s.Revalidated = false;
                s.ValidatedByTool = false;
            }
        }

        /// <summary>"Reload Latest" brings other people's changes (and renumbering) exactly as a synchronize does.</summary>
        public static void NoteReloaded(string documentKey, DateTime utc) => NoteSynchronized(documentKey, utc);

        /// <summary>The document is now known under another key (the first save, Save As).</summary>
        public static void Rename(string oldKey, string newKey)
        {
            if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey) || oldKey == newKey) return;
            lock (Gate)
            {
                LedgerRenameRules.Move(Documents, oldKey, newKey);   // R2Y m-3: replaces whatever a closed document left under that path, like every other ledger
            }
        }

        /// <summary>The document closed: what was tracked for it is dropped (R2V m-c; the dictionary used to grow with every document ever opened).</summary>
        public static void Forget(string documentKey)
        {
            if (string.IsNullOrEmpty(documentKey)) return;
            lock (Gate) Documents.Remove(documentKey);
        }

        /// <summary>How many documents have tracking state (for tests and diagnostics).</summary>
        public static int TrackedDocuments { get { lock (Gate) return Documents.Count; } }

        /// <summary>
        /// A successful reply of an id-returning read, of a write (isWrite) or of validate_ids. Tracks the ids handed out and, after a synchronize,
        /// revalidates the ones the reply proves; clears the notice when all that were held are back and at least one revalidation happened.
        /// Returns the "ids_changed" block (R2V m-a) when, after a synchronize, an id came back with another unique_id than the one held; the
        /// caller puts it in the reply. A dry-run reply must not be passed (it hands out ids that do not exist yet).
        /// </summary>
        public static JObject NoteReply(string documentKey, DateTime utc, string tool, string operation, JToken reply, bool isWrite = false)
        {
            if (string.IsNullOrEmpty(documentKey) || reply == null) return null;
            bool validate = IsValidateIds(tool, operation);
            if (!validate && !isWrite && !ReadsIds(tool, operation)) return null;
            List<Pair> pairs = validate ? ValidatedPairs(reply) : Harvest(reply);
            JArray changed = null;
            int changedTotal = 0;
            lock (Gate)
            {
                State s = Get(documentKey);
                bool pending = s.SyncsSinceRead > 0;
                int proving = 0, provingHandedOut = 0;
                foreach (Pair p in pairs)
                {
                    // R2W m-3. A no_existe for an id this session never tracked proves nothing about what it handed out (any number
                    // proves "no such element"): it does not count as a revalidation, so it cannot turn the notice off, least of all past the tracking cap.
                    bool heldBefore = s.Held.ContainsKey(p.Id);
                    // R2Y m-6. Past the tracking cap, only a validate_ids on an id THIS SESSION HANDED OUT can turn the notice off ("the ids you are going to write").
                    // A coincide / cambio / no_existe for an id it never handed out (neither held nor remembered as past the cap) is not that proof.
                    bool handedOut = heldBefore || s.OverflowIds.Contains(p.Id) || s.OverflowIdsTruncated;
                    if (!(validate && p.Missing && !heldBefore)) proving++;
                    if (validate && handedOut) provingHandedOut++;
                    if (p.Missing) s.Held.Remove(p.Id);
                    if (p.Gone)
                    {
                        s.Held.Remove(p.Id);
                        s.Unverified.Remove(p.Id);
                        continue;
                    }
                    if (p.UniqueId != null)
                    {
                        if (pending && !validate && s.Held.TryGetValue(p.Id, out string was) && !string.Equals(was, p.UniqueId, StringComparison.Ordinal))
                        {
                            changedTotal++;
                            if (changed == null) changed = new JArray();
                            if (changed.Count < MaxChangedListed) changed.Add(new JObject { ["id"] = p.Id, ["was_unique_id"] = was, ["now_unique_id"] = p.UniqueId });
                        }
                        if (s.Held.Count < MaxTrackedIds || s.Held.ContainsKey(p.Id)) s.Held[p.Id] = p.UniqueId;
                        else if (!validate)      // only an id handed out by a read or a write overflows; a validate_ids on an unknown id hands nothing out
                        {
                            s.Overflow = true;
                            if (s.OverflowIds.Count < MaxOverflowIds) s.OverflowIds.Add(p.Id); else s.OverflowIdsTruncated = true;
                        }
                    }
                    if (pending) s.Unverified.Remove(p.Id);
                }
                if (pending)
                {
                    if (proving > 0 || provingHandedOut > 0) s.Revalidated = true;
                    if (validate && provingHandedOut > 0) s.ValidatedByTool = true;
                    // Past the tracking cap the notice cannot list what to re-read, so it is turned off by a validate_ids that proves something.
                    bool cleared = s.Overflow ? s.Revalidated && s.ValidatedByTool : s.Revalidated && s.Unverified.Count == 0;
                    if (cleared)
                    {
                        s.ReadUtc = utc;
                        s.SyncsSinceRead = 0;
                        s.Revalidated = false;
                        s.ValidatedByTool = false;
                        s.Overflow = false;
                        s.OverflowIds.Clear();
                        s.OverflowIdsTruncated = false;
                    }
                }
            }
            if (changedTotal == 0) return null;
            return new JObject
            {
                ["count"] = changedTotal,
                ["ids"] = changed,
                ["note"] = "After the synchronize these ids name a DIFFERENT element than the one this session handed out before it (was_unique_id -> now_unique_id). " +
                           "Whatever you hold under those ids is not the element they name now; the element you meant, if it still exists, lives under another id " +
                           "(look it up by its unique_id with validate_ids: current_id, null when it no longer exists)."
            };
        }

        // Every {element_id | id} + unique_id pair anywhere in a reply (rows of query_model, list_elements, selection context, a write's created rows...).
        // A row of a LINKED model (link_instance_id, source_kind "link") is skipped, and so are the rows under a node that names a link: its ids are
        // not host ids and must never revalidate one (R2V m-b). A row that says its element is deleted is returned as Gone.
        private static List<Pair> Harvest(JToken reply)
        {
            var found = new List<Pair>();
            int budget = MaxNodesWalked;
            var stack = new Stack<KeyValuePair<JToken, bool>>();
            stack.Push(new KeyValuePair<JToken, bool>(reply, false));
            while (stack.Count > 0 && budget-- > 0)
            {
                KeyValuePair<JToken, bool> top = stack.Pop();
                JToken t = top.Key;
                if (t is JObject o)
                {
                    bool inLink = top.Value || NamesALink(o);
                    JToken idT = o["element_id"] ?? o["id"], uidT = o["unique_id"];
                    if (!inLink && idT != null && idT.Type == JTokenType.Integer && uidT != null && uidT.Type == JTokenType.String && !string.IsNullOrEmpty((string)uidT))
                        found.Add(new Pair { Id = (long)idT, UniqueId = (string)uidT, Gone = SaysDeleted(o) });
                    foreach (JProperty p in o.Properties()) if (p.Value is JObject || p.Value is JArray) stack.Push(new KeyValuePair<JToken, bool>(p.Value, inLink));
                }
                else if (t is JArray a) foreach (JToken c in a) if (c is JObject || c is JArray) stack.Push(new KeyValuePair<JToken, bool>(c, top.Value));
            }
            return found;
        }

        private static bool NamesALink(JObject o)
        {
            JToken link = o["link_instance_id"];
            if (link != null && link.Type != JTokenType.Null && !(link.Type == JTokenType.String && string.IsNullOrEmpty((string)link))) return true;
            JToken kind = o["source_kind"];
            return kind != null && kind.Type == JTokenType.String && string.Equals((string)kind, "link", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SaysDeleted(JObject o)
        {
            JToken verdict = o["verdict"] ?? o["status"];
            return verdict != null && verdict.Type == JTokenType.String && string.Equals((string)verdict, "deleted", StringComparison.Ordinal);
        }

        // The ids a validate_ids reply PROVES something about: a row that says coincide, cambio or no_existe for an id the caller held.
        // The value is the unique_id the model holds under that id now (null when nothing does, or when the check carried no unique_id: then the
        // reply names it holder_unique_id, which identifies nothing).
        private static List<Pair> ValidatedPairs(JToken reply)
        {
            var found = new List<Pair>();
            if (!(reply["results"] is JArray rows)) return found;
            foreach (JToken row in rows)
            {
                string status = row["status"]?.Type == JTokenType.String ? (string)row["status"] : null;
                if (status != "coincide" && status != "cambio" && status != "no_existe") continue;
                JToken id = (row["expected"] as JObject)?["id"];
                if (id == null || id.Type != JTokenType.Integer) continue;
                // R2W B-2. What the model holds under that id NOW: found.unique_id (the element asked for) or found.holder_unique_id (another element
                // that took the id). Both say what lives under expected.id, so Held follows what the model has; but only when the row's own id is
                // the expected one (an id_renumerado row describes the element at its NEW id, which is not what the old id holds).
                // found is a JSON null (a JValue, not a C# null) when nothing holds the id: only an object can be asked for children.
                JObject foundRow = row["found"] as JObject;
                JToken foundId = foundRow?["id"];
                JToken foundUid = foundRow?["unique_id"] ?? foundRow?["holder_unique_id"];
                bool sameId = foundId == null || (foundId.Type == JTokenType.Integer && (long)foundId == (long)id);
                found.Add(new Pair { Id = (long)id, Missing = status == "no_existe",
                                     UniqueId = sameId && foundUid != null && foundUid.Type == JTokenType.String ? (string)foundUid : null });
            }
            return found;
        }

        /// <summary>The notice for health, or null when no synchronize happened since the ids that were held were revalidated.</summary>
        public static JObject Block(string documentKey)
        {
            if (string.IsNullOrEmpty(documentKey)) return null;
            lock (Gate)
            {
                if (!Documents.TryGetValue(documentKey, out State s) || s.SyncsSinceRead == 0 || s.SyncedUtc == null) return null;
                var block = new JObject
                {
                    ["ids_may_have_changed"] = true,
                    ["synchronized_utc"] = s.SyncedUtc.Value.ToString("u"),
                    ["last_id_read_utc"] = s.ReadUtc.HasValue ? (JToken)s.ReadUtc.Value.ToString("u") : null,
                    ["syncs_since_last_read"] = s.SyncsSinceRead,
                    ["ids_to_revalidate"] = s.Unverified.Count,
                    ["note"] = "This document was synchronized with central (or reloaded) after this session handed out element ids. " +
                               "Revit can renumber ElementIds there, so an id held from before it may now name a different element. " +
                               "Re-read what you intend to change so that its ids come back with their unique_id, or check the ids you hold with horizun_selection_context " +
                               "operation=validate_ids. A write that carries only expected_category / expected_type is refused in a collaborative model; send the unique_id. " +
                               "The notice clears when every id handed out before the synchronize (by a read or by a write) has been revalidated by validate_ids or returned again with its unique_id by " +
                               "query_model, list_elements, query_planimetry, query_relations, selection_context, query_structure, query_dimensions, query_detail_2d, " +
                               "measure_geometry, get_dimension_references or model_scan. A read of other elements does not clear it."
                };
                if (s.Overflow)
                {
                    block["tracking_overflow"] = true;
                    block["max_tracked_ids"] = MaxTrackedIds;
                    block["note"] = (string)block["note"] + " This session handed out more than " + MaxTrackedIds + " ids, more than can be tracked: re-reading cannot prove them all. " +
                                    "The notice is turned off by horizun_selection_context operation=validate_ids on the ids you are going to write (at least one row that proves something): " +
                                    "revalidate only the ids you are going to write (a row about an id this session never handed out does not count).";
                }
                return block;
            }
        }

        internal static void ResetForTests() { lock (Gate) Documents.Clear(); }

        private static State Get(string key)
        {
            if (!Documents.TryGetValue(key, out State s)) Documents[key] = s = new State();
            return s;
        }
    }

    /// <summary>
    /// R2Y m-3. What a ledger does with its entries when the document it describes is known under another key (the first save, Save As). One rule for every
    /// ledger (creations, sync, change history, id notice). The destination ends up holding ONLY what the renamed document brings: whatever a CLOSED document
    /// left under that path is not this document's, and merging it would hand that document's blocking writes, history or marks to the wrong model.
    /// Nothing is touched when the old key holds nothing, so that renaming twice (the tool, then the event) is harmless.
    /// </summary>
    public static class LedgerRenameRules
    {
        /// <summary>Moves oldKey to newKey in two dictionaries that describe the same document. Returns whether anything moved.</summary>
        public static bool Move<A, B>(IDictionary<string, A> first, IDictionary<string, B> second, string oldKey, string newKey)
        {
            if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey) || oldKey == newKey) return false;
            bool hasFirst = first.TryGetValue(oldKey, out A a);
            bool hasSecond = second.TryGetValue(oldKey, out B b);
            if (!hasFirst && !hasSecond) return false;
            first.Remove(newKey); second.Remove(newKey);
            if (hasFirst) { first.Remove(oldKey); first[newKey] = a; }
            if (hasSecond) { second.Remove(oldKey); second[newKey] = b; }
            return true;
        }

        /// <summary>
        /// INT-9 M-3. The same rule for a ledger that keeps its facts in any number of dictionaries by document key (SyncLedgerRules keeps four).
        /// If the old key holds something in ANY of them, the destination is cleared in ALL of them and then receives what the old key held; if the
        /// old key holds nothing anywhere, nothing is touched.
        /// </summary>
        public static bool MoveAll(string oldKey, string newKey, params System.Collections.IDictionary[] ledgers)
        {
            if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey) || oldKey == newKey || ledgers == null) return false;
            if (!ledgers.Any(d => d != null && d.Contains(oldKey))) return false;
            foreach (System.Collections.IDictionary d in ledgers)
            {
                if (d == null) continue;
                bool has = d.Contains(oldKey);
                object value = has ? d[oldKey] : null;
                d.Remove(newKey);
                if (has) { d.Remove(oldKey); d[newKey] = value; }
            }
            return true;
        }

        /// <summary>The same, for a ledger kept in a single dictionary.</summary>
        public static bool Move<A>(IDictionary<string, A> only, string oldKey, string newKey) =>
            Move(only, new Dictionary<string, A>(), oldKey, newKey);
    }

    /// <summary>
    /// What each document event does to the session's bookkeeping, Revit-free so that it can be tested (R2V m-f). The Revit handlers
    /// (QueryCacheLifecycle) only translate the event into the document key and call these.
    /// </summary>
    public static class DocumentLifecycleRules
    {
        /// <summary>"Reload Latest": other people's changes, and renumbering, arrive exactly as with a synchronize.</summary>
        public static void Reloaded(string key, DateTime utc) => IdFreshnessRules.NoteReloaded(key, utc);

        /// <summary>A synchronize with central.</summary>
        public static void Synchronized(string key, DateTime utc) => IdFreshnessRules.NoteSynchronized(key, utc);

        /// <summary>The document key of a saved file (ChangeWatch.Key builds it with this, so that the events and the key cannot drift apart).</summary>
        public static string PathKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch (Exception) { full = path; }              // an invalid path (NUL, wildcard...) must never throw out of an event handler
            full = full.Replace('/', '\\');
            int rootLength = 0;
            try { rootLength = (System.IO.Path.GetPathRoot(full) ?? "").Length; } catch (Exception) { }
            // R2Y m-2. A trailing separator names the same file; the root (a drive or a UNC share) keeps its own separator.
            while (full.Length > rootLength && full.Length > 1 && full[full.Length - 1] == '\\') full = full.Substring(0, full.Length - 1);
            return "path:" + full.ToLowerInvariant();
        }

        // R2X (step 18). Pairing DocumentSavingAs with DocumentSavedAs by the identity of the Document wrapper alone lost the pair in the field:
        // after a Save As through document_session the notes the session had created were no longer its own. The pair is found two ways and either is
        // enough: the same Document (Document.Equals, not the identity of the wrapper: Revit hands out wrappers) and the target path the saving event named.
        // R2Y (M-2, m-1). A Save As is one at a time, so only one can be in flight: a new DocumentSavingAs drops whatever older entry is still open (a
        // Save As that failed and never reported), and a SavedAs that did not Succeed discards its entry WITHOUT moving anything. The target path alone
        // never pairs an entry with another document's event: the OriginalPath the event reports has to be the path the entry started from.
        private sealed class SaveAsPending { public WeakReference Document; public string Before, TargetKey; }
        private static readonly object SaveAsGate = new object();
        private static readonly List<SaveAsPending> SaveAsOpen = new List<SaveAsPending>();
        private const int SaveAsPendingCap = 16;

        /// <summary>How two Document objects are told to be the same document. Revit hands out wrappers, so the default is Equals, not identity; tests inject their own.</summary>
        internal static Func<object, object, bool> SameDocument = (a, b) => a != null && b != null && (ReferenceEquals(a, b) || a.Equals(b));

        private static bool IsSame(WeakReference held, object document)
        {
            object target = held?.Target;
            return target != null && document != null && SameDocument(target, document);
        }

        /// <summary>DocumentSavingAs fired (it can still fail or be cancelled): remember what the document was called and where it is going.</summary>
        public static void SavingAsBegan(object document, string beforeKey, string targetPath)
        {
            if (string.IsNullOrEmpty(beforeKey)) return;
            lock (SaveAsGate)
            {
                SaveAsOpen.Clear();                      // only one Save As is in flight: anything still open belongs to one that ended without telling us
                SaveAsOpen.Add(new SaveAsPending { Document = new WeakReference(document), Before = beforeKey, TargetKey = PathKey(targetPath) });
            }
        }

        /// <summary>
        /// DocumentSavedAs fired. Everything the document was known under before follows it to <paramref name="nowKey"/>. Returns how many old keys moved.
        /// <paramref name="originalPath"/> is the event's own OriginalPath (null or empty for a document that had no file yet).
        /// <paramref name="succeeded"/> is the event's Status: a Save As that was cancelled or failed moves NOTHING and discards its pending entry (R2Y M-2).
        /// </summary>
        public static int SavedAsCompleted(object document, string nowKey, string originalPath, Action<string, string> renameChangeLedger, bool succeeded = true)
        {
            if (!succeeded)
            {
                lock (SaveAsGate) SaveAsOpen.RemoveAll(p => IsSame(p.Document, document) || p.Document?.Target == null);
                return 0;
            }
            if (string.IsNullOrEmpty(nowKey)) return 0;
            string original = PathKey(originalPath);
            var before = new List<string>();
            lock (SaveAsGate)
            {
                foreach (SaveAsPending p in SaveAsOpen.ToList())
                {
                    bool sameDocument = IsSame(p.Document, document);
                    // By target path only when the event's own OriginalPath does not contradict where the entry started (another document's Save As
                    // to the same file must not inherit it). A document that had no file reports no OriginalPath: then the target alone decides.
                    bool byTarget = p.TargetKey == nowKey && (original == null || original == p.Before);
                    if (!sameDocument && !byTarget) continue;
                    before.Add(p.Before);
                    SaveAsOpen.Remove(p);
                }
            }
            if (original != null) before.Add(original);
            int moved = 0;
            foreach (string key in before.Where(k => !string.IsNullOrEmpty(k) && k != nowKey).Distinct(StringComparer.Ordinal))
            {
                SavedAs(key, nowKey, renameChangeLedger);
                moved++;
            }
            return moved;
        }

        internal static void ResetSaveAsForTests() { lock (SaveAsGate) SaveAsOpen.Clear(); }

        /// <summary>The first save or Save As changed the document key: everything keyed by it follows (renameChangeLedger is the Revit-bound history).</summary>
        public static void SavedAs(string before, string now, Action<string, string> renameChangeLedger)
        {
            SessionCreationLedger.Rename(before, now);
            IdFreshnessRules.Rename(before, now);
            SyncLedgerRules.Rename(before, now);
            SaveSuggestionRules.Rename(before, now);
            renameChangeLedger?.Invoke(before, now);
        }

        /// <summary>
        /// The document closes: the notice state is dropped. The ledger of creations is NOT: a document closed and opened again in the same Revit
        /// session still holds marks of this session, and without the ledger they would be read as copies (fail closed, but a loss for nothing).
        /// </summary>
        public static void Closing(string key) => IdFreshnessRules.Forget(key);

        // R2W m-2. The key a document had when its closing began, by Revit's document id (DocumentClosed has no Document left to name it).
        private static readonly object ClosingGate = new object();
        private static readonly Dictionary<int, string> ClosingKeys = new Dictionary<int, string>();

        /// <summary>DocumentClosing fired. It can still be cancelled, so nothing is dropped yet: the key is only remembered.</summary>
        public static void ClosingBegan(int documentId, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (ClosingGate) ClosingKeys[documentId] = key;
        }

        /// <summary>
        /// DocumentClosed fired. When it succeeded the notice state of that document is dropped (a document reopened later starts without it: what the session
        /// handed out before is forgotten, documented). When it was cancelled or failed nothing is lost. Returns whether state was dropped.
        /// </summary>
        public static bool Closed(int documentId, bool succeeded)
        {
            string key;
            lock (ClosingGate)
            {
                if (!ClosingKeys.TryGetValue(documentId, out key)) return false;
                ClosingKeys.Remove(documentId);
            }
            if (!succeeded) return false;
            Closing(key);
            return true;
        }

        /// <summary>Documents whose closing began and did not end yet (for tests).</summary>
        internal static int PendingClosings { get { lock (ClosingGate) return ClosingKeys.Count; } }
    }

    /// <summary>
    /// R2W M-c. The note on a row of query_model asked BY ID (element_ids) in a collaborative model. Short on purpose: it is repeated per row and
    /// tools/list is not touched (the schema is unchanged; this is only in the reply).
    /// </summary>
    public static class QueryHolderNote
    {
        public const string Text = "holder_note: this unique_id is the one of the element that holds this id TODAY; after a synchronize with central it does not prove " +
                                   "that it is the element you meant. Take the unique_id from the read that gave you the id.";

        /// <summary>Adds holder_note to a row asked by id in a collaborative model. Returns whether it was added.</summary>
        public static bool Apply(JObject row, bool askedById, bool collaborative)
        {
            if (row == null || !askedById || !collaborative) return false;
            row["holder_note"] = Text;
            return true;
        }
    }

    /// <summary>R2W m-3. What a cascade row of delete_verified carries so that the sync notice can follow it (Revit-free, tested).</summary>
    public static class CascadeRowRules
    {
        /// <summary>
        /// Puts unique_id on the row (when it was read) and, in an APPLY, verdict "deleted" when the cascaded element is confirmed gone: the notice then
        /// stops waiting for it. A rehearsal never says deleted (nothing was deleted), and an unconfirmed (null) or surviving cascade is not marked.
        /// </summary>
        public static void Mark(JObject row, string uniqueId, bool? gone, bool dryRun)
        {
            if (row == null) return;
            row["unique_id"] = uniqueId;
            if (!dryRun && gone == true) row["verdict"] = "deleted";
        }
    }
}
