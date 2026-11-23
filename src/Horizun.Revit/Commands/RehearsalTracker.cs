// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of horizun_execute_python rehearsal=true: listens to DocumentChanged
// for ONE document while the script runs inside the always-rolled-back group, reads the
// categories of what was created BEFORE the rollback (afterwards they no longer exist)
// and of what was deleted or modified AFTER it (by then they are back), and re-reads
// every id to prove the rollback. Rules in Core/RehearsalRules.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class RehearsalTracker
    {
        private readonly Autodesk.Revit.ApplicationServices.Application app;
        private readonly Document doc;
        private readonly RehearsalChanges changes = new RehearsalChanges();
        private readonly Dictionary<long, string> categories = new Dictionary<long, string>();
        private bool listening;
        private string listenError;
        private bool otherDocument;

        private RehearsalTracker(Autodesk.Revit.ApplicationServices.Application app, Document doc) { this.app = app; this.doc = doc; }

        public static RehearsalTracker Start(Autodesk.Revit.ApplicationServices.Application app, Document doc)
        {
            var t = new RehearsalTracker(app, doc);
            try { app.DocumentChanged += t.OnChanged; t.listening = true; }
            catch (Exception ex) { t.listenError = ex.Message; }
            return t;
        }

        private void OnChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                Document d = e.GetDocument();
                if (d == null) { listenError = "DocumentChanged did not identify its document"; return; }
                if (!d.Equals(doc))
                {
                    if (e.GetAddedElementIds().Count + e.GetDeletedElementIds().Count + e.GetModifiedElementIds().Count > 0)
                        otherDocument = true;
                    return;
                }
                changes.Record(e.GetAddedElementIds().Select(Rid.Value), e.GetDeletedElementIds().Select(Rid.Value),
                               e.GetModifiedElementIds().Select(Rid.Value), e.GetTransactionNames());
            }
            catch (Exception ex) { listenError = "a DocumentChanged event could not be read: " + ex.Message; }
        }

        /// <summary>Stop listening, BEFORE the group rolls back, and read what was created while it still exists.</summary>
        public void StopBeforeRollback()
        {
            if (listening) { try { app.DocumentChanged -= OnChanged; } catch { } listening = false; }
            foreach (long id in changes.Created) categories[id] = CategoryOf(id);
        }

        /// <summary>After the group rolled back: categories of what came back, and the rollback proof.</summary>
        public JObject Report(bool groupRolledBack)
        {
            foreach (long id in changes.Deleted.Concat(changes.Modified)) if (!categories.ContainsKey(id)) categories[id] = CategoryOf(id);
            RehearsalVerdict v = RehearsalRules.Judge(groupRolledBack, changes, id =>
            {
                try { return Rid.CanRepresent(id) ? (bool?)(doc.GetElement(Rid.Make(id)) != null) : null; }
                catch { return null; }
            });
            Func<long, string> cat = id => categories.TryGetValue(id, out string c) ? c : null;
            var block = new JObject
            {
                ["created"] = RehearsalRules.Block(changes.Created, cat),
                ["deleted"] = RehearsalRules.Block(changes.Deleted, cat),
                ["modified"] = RehearsalRules.Block(changes.Modified, cat),
                ["transactions"] = new JArray(changes.Transactions),
                ["events"] = changes.Events,
                ["rolled_back_verified"] = v.RolledBackVerified,
                ["group_rolled_back"] = groupRolledBack,
                ["reread_verified"] = v.RolledBackVerified,
                ["created_still_present"] = new JArray(v.CreatedStillPresent.Take(50)),
                ["deleted_still_missing"] = new JArray(v.DeletedStillMissing.Take(50)),
                ["note"] = v.Note,
                ["source"] = "Revit's DocumentChanged events for this document while the script ran (host-observed, not script-reported). " +
                             "Deleted counts include what Revit cascaded. modified includes elements Revit regenerated (views, joins, hosts)."
            };
            if (listenError != null) { block["listen_error"] = listenError; block["rolled_back_verified"] = false; }
            block["other_document_changed"] = otherDocument;
            if (otherDocument) block["rolled_back_verified"] = false;
            return block;
        }

        private string CategoryOf(long id)
        {
            try
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                string name = e?.Category?.Name ?? (e == null ? null : e.GetType().Name);
                // A purged type reads as its category ("Structural Framing deleted") unless it says it is a type.
                return e is ElementType && name != null ? name + " (type)" : name;
            }
            catch { return null; }
        }
    }
}
