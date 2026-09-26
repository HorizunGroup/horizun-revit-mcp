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
        private readonly Autodesk.Revit.ApplicationServices.Application _app;
        private readonly Dictionary<string, DocChanges> _docs = new Dictionary<string, DocChanges>(StringComparer.Ordinal);
        private bool _attached;

        internal sealed class DocChanges
        {
            public Document Document;
            public readonly HashSet<long> Added = new HashSet<long>();
            public readonly HashSet<long> Modified = new HashSet<long>();
            public readonly HashSet<long> Deleted = new HashSet<long>();
            public readonly List<string> Transactions = new List<string>();
        }

        public ChangeWatch(Autodesk.Revit.ApplicationServices.Application app)
        {
            _app = app;
            if (_app == null) return;
            try { _app.DocumentChanged += OnChanged; _attached = true; } catch { _attached = false; }
        }

        public IEnumerable<DocChanges> Documents => _docs.Values;

        private void OnChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                Document doc = e.GetDocument();
                if (doc == null || doc.IsLinked) return;
                string key = Key(doc);
                if (!_docs.TryGetValue(key, out DocChanges d)) _docs[key] = d = new DocChanges { Document = doc };
                ICollection<ElementId> added = e.GetAddedElementIds(), modified = e.GetModifiedElementIds(), deleted = e.GetDeletedElementIds();
                bool reverted = e.Operation == UndoOperation.TransactionUndone || e.Operation == UndoOperation.TransactionGroupRolledBack ||
                                e.Operation == UndoOperation.TransactionRolledBack;
                if (reverted)
                {
                    // An undo or rollback reports what it touched while taking the change back.
                    foreach (ElementId id in added.Concat(modified).Concat(deleted))
                    {
                        long v = Rid.Value(id);
                        d.Added.Remove(v); d.Modified.Remove(v); d.Deleted.Remove(v);
                    }
                    return;
                }
                foreach (ElementId id in added) d.Added.Add(Rid.Value(id));
                foreach (ElementId id in modified) { long v = Rid.Value(id); if (!d.Added.Contains(v)) d.Modified.Add(v); }
                foreach (ElementId id in deleted) { long v = Rid.Value(id); d.Deleted.Add(v); d.Added.Remove(v); d.Modified.Remove(v); }
                foreach (string t in e.GetTransactionNames()) if (!d.Transactions.Contains(t)) d.Transactions.Add(t);
            }
            catch { /* never let an observer break the command it observes */ }
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
                    if (d.Document == null || !d.Document.IsValidObject) continue;
                    d.Added.RemoveWhere(v => !Exists(d.Document, v));
                    d.Modified.RemoveWhere(v => !Exists(d.Document, v) || Bookkeeping(d.Document, v));
                    d.Deleted.RemoveWhere(v => Exists(d.Document, v));
                }
                catch { /* an unreadable document keeps the event tally, never a guess */ }
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

        private static bool Exists(Document doc, long id)
        {
            if (!Rid.CanRepresent(id)) return true;
            try { return doc.GetElement(Rid.Make(id)) != null; } catch { return true; }
        }

        public void Dispose()
        {
            if (_attached) try { _app.DocumentChanged -= OnChanged; } catch { }
            _attached = false;
        }

        public static string Key(Document doc)
        {
            try
            {
                string path = doc.PathName;
                // Two unsaved "Project1" documents share a title; the runtime hash keeps them apart.
                return string.IsNullOrEmpty(path) ? "title:" + doc.Title + "#" + doc.GetHashCode() : "path:" + path.ToLowerInvariant();
            }
            catch { return "unknown"; }
        }
    }

    /// <summary>The last Horizun write per document, for horizun_verify_changes scope=last_write.</summary>
    internal static class ChangeLedger
    {
        internal sealed class Entry
        {
            public string Tool, DocumentTitle;
            public DateTime AtUtc;
            public long[] Added, Modified;
            public string[] Transactions;
            public int Deleted;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Last = new Dictionary<string, Entry>(StringComparer.Ordinal);

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
            lock (Gate) Last[ChangeWatch.Key(d.Document)] = entry;
        }

        public static Entry For(Document doc)
        {
            lock (Gate) return Last.TryGetValue(ChangeWatch.Key(doc), out Entry e) ? e : null;
        }
    }
}
