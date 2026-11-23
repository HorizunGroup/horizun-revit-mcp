// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of Core/ExportIsolationRules.cs: a TransactionGroup opened around an
// exporter that commits to the document, a ChangeWatch that counts what it committed,
// and a rollback once the file is on disk. The counts are taken twice - before the
// rollback (what the exporter did) and after it, settled against the model (what is
// left) - so the reply never has to take the rollback's word for it.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class ExportIsolation
    {
        private const int SampleSize = 12;

        private Document _doc;
        private TransactionGroup _group;
        private ChangeWatch _watch;
        private readonly ExportIsolationRules.Facts _facts = new ExportIsolationRules.Facts();

        public static ExportIsolation Begin(UIApplication app, Document doc, string format)
        {
            var iso = new ExportIsolation { _doc = doc };
            // The witness: Revit's own modified flag, before anything runs.
            try { iso._facts.ModifiedBefore = doc.IsModified; } catch { iso._facts.ModifiedBefore = null; }
            try { iso._watch = new ChangeWatch(app?.Application); } catch { iso._watch = null; }
            try
            {
                if (doc.IsReadOnly) iso._facts.UnavailableReason = "the document is read-only";
                else if (doc.IsModifiable) iso._facts.UnavailableReason = "a transaction is already open on the document";
                else
                {
                    iso._group = new TransactionGroup(doc, "Horizun: export " + format + " (rolled back)");
                    if (iso._group.Start() == TransactionStatus.Started) iso._facts.GroupStarted = true;
                    else
                    {
                        iso._facts.UnavailableReason = "the transaction group did not start";
                        iso._group.Dispose(); iso._group = null;
                    }
                }
            }
            catch (Exception ex)
            {
                iso._facts.UnavailableReason = "the transaction group could not be opened: " + ex.Message;
                try { iso._group?.Dispose(); } catch { }
                iso._group = null;
            }
            return iso;
        }

        /// <summary>Count, roll back, count again. Never throws.</summary>
        public ExportIsolationRules.Facts End()
        {
            ChangeWatch.DocChanges mine = Mine();
            if (mine != null)
            {
                _facts.Added = mine.Added.Count; _facts.Modified = mine.Modified.Count; _facts.Deleted = mine.Deleted.Count;
                _facts.Transactions.AddRange(mine.Transactions);
                foreach (long id in mine.Added.Take(SampleSize)) _facts.Sample.Add(Describe(id, "added"));
                foreach (long id in mine.Modified.Take(SampleSize - _facts.Sample.Count)) _facts.Sample.Add(Describe(id, "modified"));
            }
            if (_group != null)
            {
                try
                {
                    // An exporter that threw can leave its own transaction open; the group
                    // cannot roll back over it, and Revit says so rather than guessing.
                    _facts.RollbackStatus = _group.GetStatus() == TransactionStatus.Started
                        ? _group.RollBack().ToString()
                        : _group.GetStatus().ToString();
                }
                catch (Exception ex) { _facts.RollbackError = ex.Message; }
                finally { try { _group.Dispose(); } catch { } _group = null; }
            }
            try
            {
                _watch?.Settle();
                mine = Mine();
                if (mine != null)
                {
                    _facts.ResidualAdded = mine.Added.Count; _facts.ResidualModified = mine.Modified.Count;
                    _facts.ResidualDeleted = mine.Deleted.Count;
                    foreach (long id in mine.Added.Take(SampleSize)) _facts.ResidualSample.Add(Describe(id, "added"));
                    foreach (long id in mine.Modified.Take(SampleSize - _facts.ResidualSample.Count)) _facts.ResidualSample.Add(Describe(id, "modified"));
                }
            }
            catch { }
            finally { try { _watch?.Dispose(); } catch { } }
            try { _facts.ModifiedAfter = _doc.IsModified; } catch { _facts.ModifiedAfter = null; }
            return _facts;
        }

        private ChangeWatch.DocChanges Mine()
        {
            if (_watch == null) return null;
            string key = ChangeWatch.Key(_doc);
            return _watch.Documents.FirstOrDefault(d => d.Document != null && ChangeWatch.Key(d.Document) == key);
        }

        private JObject Describe(long id, string change)
        {
            var row = new JObject { ["id"] = id, ["change"] = change };
            try
            {
                Element e = Rid.CanRepresent(id) ? _doc.GetElement(Rid.Make(id)) : null;
                if (e != null) { row["category"] = e.Category?.Name; row["built_in_category"] = CategoryKey.Of(e); row["class"] = e.GetType().Name; row["name"] = e.Name; }
            }
            catch { }
            return row;
        }
    }
}
