// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// What did this command actually change? Every write reports what it MEANT to do and
// re-reads that. The spatial coherence check needs the other half: every element the
// command's transactions really added or modified, including the ones it did not
// think about (a join, a wall a door cut, a hosted element that moved with its host).
//
// Revit tells us through Application.DocumentChanged, which fires for each committed
// transaction AND for each rollback or undo. A rehearsal that commits and then rolls
// its group back produces both, so the sets subtract what a rollback took away; what
// is left when the command returns is what it left in the model.
//
// ChangeLedger keeps the last write of each document so horizun_verify_changes can
// check "what the previous call did" without the caller copying ids around.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace Horizun.Revit.Core
{
    internal sealed class ChangeWatch : IDisposable
    {
        // Touched only on the Revit thread. A finalized transaction's commit event,
        // if raised, has been delivered by the time Revit starts the next command.
        private static readonly HashSet<ChangeWatch> FinalizedWatches = new HashSet<ChangeWatch>();
        private readonly Autodesk.Revit.ApplicationServices.Application _app;
        private readonly Dictionary<string, DocChanges> _docs = new Dictionary<string, DocChanges>(StringComparer.Ordinal);
        private readonly Document _commitDocument;
        private readonly string _commitName;
        private readonly Action _commitObserved;
        private bool _attached;
        public bool ObservationComplete { get; private set; } = true;

        internal sealed class DocChanges
        {
            public Document Document;
            public readonly HashSet<long> Added = new HashSet<long>();
            public readonly HashSet<long> Modified = new HashSet<long>();
            public readonly HashSet<long> Deleted = new HashSet<long>();
            /// <summary>Ids in <see cref="Deleted"/> that this same watch saw ADDED first: elements made and removed inside
            /// the call (a group rebuild does this), which existed neither before nor after it.</summary>
            public readonly HashSet<long> Transient = new HashSet<long>();
            /// <summary>Revit's own browser panes it refreshed during the call (RevitInternalTouchRules): reported, never counted as a change.</summary>
            public readonly HashSet<long> InternalTouches = new HashSet<long>();
            /// <summary>Revit raised a rollback or undo for this document during the call.</summary>
            public bool SawRollback;
            public readonly List<string> Transactions = new List<string>();
        }

        public ChangeWatch(Autodesk.Revit.ApplicationServices.Application app, Document commitDocument = null,
            string commitName = null, Action commitObserved = null)
        {
            if (commitDocument == null)
                foreach (ChangeWatch finalized in FinalizedWatches.ToArray()) finalized.Dispose();
            _app = app;
            _commitDocument = commitDocument;
            _commitName = commitName;
            _commitObserved = commitObserved;
            if (_app == null) { ObservationComplete = false; return; }
            try { _app.DocumentChanged += OnChanged; _attached = true; } catch { _attached = false; ObservationComplete = false; }
        }

        public IEnumerable<DocChanges> Documents => _docs.Values;

        public void ReleaseAtNextCommand()
        {
            if (_attached) FinalizedWatches.Add(this);
        }

        private void OnChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                Document doc = e.GetDocument();
                if (doc == null || doc.IsLinked) return;
                string key = Key(doc);
                if (_commitDocument != null && (key != Key(_commitDocument) ||
                    e.Operation != UndoOperation.TransactionCommitted || !e.GetTransactionNames().Contains(_commitName))) return;
                if (!_docs.TryGetValue(key, out DocChanges d)) _docs[key] = d = new DocChanges { Document = doc };
                ICollection<ElementId> added = e.GetAddedElementIds(), modified = e.GetModifiedElementIds(), deleted = e.GetDeletedElementIds();
                bool reverted = e.Operation == UndoOperation.TransactionUndone || e.Operation == UndoOperation.TransactionGroupRolledBack ||
                                e.Operation == UndoOperation.TransactionRolledBack;
                if (reverted)
                {
                    d.SawRollback = true;
                    // An undo or rollback reports what it touched while taking the change back.
                    foreach (ElementId id in added.Concat(modified).Concat(deleted))
                    {
                        long v = Rid.Value(id);
                        d.Added.Remove(v); d.Modified.Remove(v); d.Deleted.Remove(v); d.Transient.Remove(v);
                    }
                    return;
                }
                foreach (ElementId id in added) d.Added.Add(Rid.Value(id));
                foreach (ElementId id in modified) { long v = Rid.Value(id); if (!d.Added.Contains(v)) d.Modified.Add(v); }
                foreach (ElementId id in deleted) { long v = Rid.Value(id); d.Deleted.Add(v); if (d.Added.Remove(v)) d.Transient.Add(v); d.Modified.Remove(v); }
                foreach (string t in e.GetTransactionNames()) if (!d.Transactions.Contains(t)) d.Transactions.Add(t);
            }
            catch { ObservationComplete = false; /* never let an observer break the command it observes */ }
            finally
            {
                // A deferred transaction has exactly one matching commit event.
                if (_commitDocument != null && _docs.Count != 0)
                {
                    Dispose();
                    try { _commitObserved?.Invoke(); }
                    catch (Exception ex) { Log.Error("Could not record deferred transaction changes", ex); }
                }
            }
        }

        /// <summary>
        /// Reconcile the event tally with the model as the call leaves it. The events alone
        /// over-count: measured 2026-09-26, horizun_verify_changes rolled its temporary view
        /// back (status RolledBack) and still reported 11 elements added. An id counted as
        /// added or modified that no longer resolves is gone; an id counted as deleted that
        /// still resolves came back. What the model answers wins over what the events said.
        /// </summary>
        public void Settle()
        {
            foreach (DocChanges d in _docs.Values)
            {
                try
                {
                    if (d.Document == null || !d.Document.IsValidObject) { ObservationComplete = false; continue; }
                    Document doc = d.Document;
                    RevitInternalTouchRules.Reconcile(d.Added, d.Modified, d.Deleted, d.InternalTouches, d.SawRollback,
                        v => Exists(doc, v), v => Bookkeeping(doc, v), v => BrowserPane(doc, v));
                }
                catch { ObservationComplete = false; /* retain the tally, but do not claim complete observation */ }
            }
        }

        /// <summary>
        /// Revit's own bookkeeping: no category, no name, the bare Element class. Measured
        /// 2026-09-26: one such element (id 23741 in a metric template) is reported modified
        /// by every call, a read-only one whose group rolled back included - so it says
        /// nothing about what the call did. Only MODIFIED is filtered; an added element exists.
        /// </summary>
        private static bool Bookkeeping(Document doc, long id)
        {
            try
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                return e != null && e.GetType() == typeof(Element) && e.Category == null && string.IsNullOrEmpty(e.Name);
            }
            catch { return false; }
        }

        /// <summary>The System Browser / Project Browser view (measured 2026-11-17: modified by an apply_cad_plan rehearsal that rolled back). Moved only per RevitInternalTouchRules.SetAside.</summary>
        private static bool BrowserPane(Document doc, long id)
        {
            try
            {
                View v = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as View : null;
                return v != null && RevitInternalTouchRules.IsBrowserPane(true, v.ViewType.ToString());
            }
            catch { return false; }
        }

        private static bool Exists(Document doc, long id)
        {
            if (!Rid.CanRepresent(id)) return true;
            try { return doc.GetElement(Rid.Make(id)) != null; } catch { return true; }
        }

        public void Dispose()
        {
            FinalizedWatches.Remove(this);
            if (_attached) try { _app.DocumentChanged -= OnChanged; } catch { }
            _attached = false;
        }

        public static string Key(Document doc)
        {
            try
            {
                string path = doc.PathName;
                // Two unsaved "Project1" documents share a title; the runtime hash keeps them apart.
                return string.IsNullOrEmpty(path) ? "title:" + doc.Title + "#" + doc.GetHashCode() : DocumentLifecycleRules.PathKey(path);
            }
            catch { return "unknown"; }
        }
    }

    /// <summary>
    /// The Horizun writes for each document, for horizun_verify_changes. `For` keeps the
    /// EXACT scope=last_write behaviour it always had (one entry, replaced each write).
    /// `HistoryFor` is new: a bounded list of every write recorded for that document since
    /// the add-in started, oldest first, for scope=session (SessionScopeRules.cs unions
    /// their added/modified ids). Capped at HistoryCap writes per document so a long Revit
    /// session does not grow this without limit; the oldest write is dropped first, which
    /// only narrows how far back scope=session can see, never corrupts what it returns.
    /// </summary>
    internal static class ChangeLedger
    {
        internal sealed class Entry
        {
            public string Tool, DocumentTitle;
            public DateTime AtUtc;
            public long[] Added, Modified;
            /// <summary>The UniqueIds of <see cref="Added"/>, read when the write was recorded (R2U M-A): an ElementId is renumbered by a synchronize, a UniqueId is not.</summary>
            public string[] AddedUniqueIds = new string[0];
            public string[] Transactions;
            public int Deleted;
        }

        internal const int HistoryCap = 500;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Last = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<Entry>> History = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);

        public static void Record(string tool, ChangeWatch.DocChanges d)
        {
            // Only what added or reshaped something is a "last write" worth checking: a
            // delete-only or failed call must not replace the previous write and leave
            // horizun_verify_changes with nothing to look at (review 2026-09-26).
            if (d == null || (d.Added.Count == 0 && d.Modified.Count == 0)) return;
            var entry = new Entry
            {
                Tool = tool, AtUtc = DateTime.UtcNow,
                Added = d.Added.ToArray(), Modified = d.Modified.ToArray(), Deleted = d.Deleted.Count,
                Transactions = d.Transactions.ToArray()
            };
            try { entry.DocumentTitle = d.Document.Title; } catch { }
            string key = ChangeWatch.Key(d.Document);
            // R2U M-A: what this write CREATED is remembered by UniqueId (an ElementId is renumbered by a synchronize and can be handed to
            // another element). The ledger of creations is what the identity guard asks whether a mark of this session is genuine.
            entry.AddedUniqueIds = UniqueIdsOf(d.Document, d.Added).ToArray();
            // R2V M-1: what a script or a copy between documents added stays in this change ledger (verify_changes reads it) but is NOT a creation of ours.
            SessionCreationLedger.RecordFor(tool, key, entry.AddedUniqueIds);
            lock (Gate)
            {
                Last[key] = entry;
                if (!History.TryGetValue(key, out List<Entry> list)) History[key] = list = new List<Entry>();
                list.Add(entry);
                while (list.Count > HistoryCap) list.RemoveAt(0);
            }
        }

        private static IEnumerable<string> UniqueIdsOf(Document doc, IEnumerable<long> ids)
        {
            var uids = new List<string>();
            foreach (long id in ids)
            {
                try
                {
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (e != null && e.IsValidObject && !string.IsNullOrEmpty(e.UniqueId)) uids.Add(e.UniqueId);
                }
                catch { /* an element that cannot be read is simply not remembered: fail closed */ }
            }
            return uids;
        }

        /// <summary>The document is now known under another key (the first save, Save As): its history follows it.</summary>
        public static void Rename(string oldKey, string newKey)
        {
            if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey) || oldKey == newKey) return;
            lock (Gate)
            {
                LedgerRenameRules.Move(Last, History, oldKey, newKey);       // R2Y m-3: the destination keeps only the renamed document's content
            }
        }

        public static Entry For(Document doc)
        {
            lock (Gate) return Last.TryGetValue(ChangeWatch.Key(doc), out Entry e) ? e : null;
        }

        /// <summary>Every write recorded for this document, oldest first, bounded to HistoryCap.
        /// Empty (never null) when nothing has been recorded yet.</summary>
        public static IReadOnlyList<Entry> HistoryFor(Document doc)
        {
            lock (Gate) return History.TryGetValue(ChangeWatch.Key(doc), out List<Entry> list) ? list.ToArray() : Array.Empty<Entry>();
        }
    }
}
