// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The Revit half of horizun_execute_python documentation_only=true (4.2-a). The rules
// (Core/DocumentationOnlyRules.cs) are pure; this class gathers their inputs:
//
//   * a SNAPSHOT, taken before the script, of the ids that are documentation - views,
//     viewports, schedules on sheets, annotation-category elements, detail lines and filled
//     regions. A deleted element no longer resolves, so this is the only way to say what it
//     was. A deletion outside the snapshot is refused as unclassified;
//   * the net created / modified / deleted sets of DocumentChanged for the target document;
//   * a flag when ANY other document reported a change (the group cannot roll that back).
//
// The caller judges while the TransactionGroup is still open, then assimilates or rolls back.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class DocumentationOnlyTracker
    {
        private readonly Autodesk.Revit.ApplicationServices.Application app;
        private readonly Document doc;
        private readonly RehearsalChanges changes = new RehearsalChanges();
        private readonly Dictionary<long, string> snapshot = new Dictionary<long, string>();
        private bool listening, otherDocument, snapshotComplete = true;
        private string listenError;

        private DocumentationOnlyTracker(Autodesk.Revit.ApplicationServices.Application app, Document doc) { this.app = app; this.doc = doc; }

        public bool SnapshotComplete => snapshotComplete;
        public int SnapshotSize => snapshot.Count;
        public RehearsalChanges Changes => changes;

        public static DocumentationOnlyTracker Start(Autodesk.Revit.ApplicationServices.Application app, Document doc)
        {
            var t = new DocumentationOnlyTracker(app, doc);
            t.TakeSnapshot();
            try { app.DocumentChanged += t.OnChanged; t.listening = true; }
            catch (Exception ex) { t.listenError = ex.Message; }
            return t;
        }

        private void TakeSnapshot()
        {
            try
            {
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(View))) snapshot[Rid.Value(e.Id)] = DocumentationTarget.ViewKind;
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(Viewport))) snapshot[Rid.Value(e.Id)] = DocumentationTarget.ViewKind;
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance))) snapshot[Rid.Value(e.Id)] = DocumentationTarget.ViewKind;
                var annotation = new List<ElementId>();
                foreach (Category c in doc.Settings.Categories)
                    if (c != null && c.CategoryType == CategoryType.Annotation) annotation.Add(c.Id);
                if (annotation.Count > 0)
                    foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType()
                                 .WherePasses(new ElementMulticategoryFilter(annotation)))
                        snapshot[Rid.Value(e.Id)] = DocumentationTarget.AnnotationKind;
                // Detail lines and filled regions are model-category in Revit but live in one view.
                foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Concat(
                                      new FilteredElementCollector(doc).OfClass(typeof(FilledRegion))))
                {
                    ElementId owner = e.OwnerViewId;
                    if (owner != null && owner != ElementId.InvalidElementId) snapshot[Rid.Value(e.Id)] = DocumentationTarget.ViewOwnedKind;
                }
            }
            catch { snapshotComplete = false; }
        }

        private void OnChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                Document d = e.GetDocument();
                if (d == null) return;
                if (!d.Equals(doc))
                {
                    if (!d.IsLinked && (e.GetAddedElementIds().Count + e.GetModifiedElementIds().Count + e.GetDeletedElementIds().Count) > 0)
                        otherDocument = true;
                    return;
                }
                changes.Record(e.GetAddedElementIds().Select(Rid.Value), e.GetDeletedElementIds().Select(Rid.Value),
                               e.GetModifiedElementIds().Select(Rid.Value), e.GetTransactionNames());
            }
            catch (Exception ex) { listenError = "a DocumentChanged event could not be read: " + ex.Message; }
        }

        /// <summary>Stop listening and judge, while the group is still open.</summary>
        public DocumentationOnlyVerdict Judge()
        {
            if (listening) { try { app.DocumentChanged -= OnChanged; } catch { } listening = false; }
            // Revit's own bookkeeping element (no category, no name) is reported modified by
            // every call; it says nothing about the script (same filter as ChangeWatch).
            foreach (long id in changes.Modified.ToList()) if (Bookkeeping(id)) changes.Modified.Remove(id);
            return DocumentationOnlyRules.Judge(changes, KindNow,
                id => snapshot.TryGetValue(id, out string k) ? k : null, otherDocument,
                snapshotComplete ? listenError : (listenError ?? "the documentation snapshot is incomplete"));
        }

        private DocumentationTarget KindNow(long id)
        {
            try
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                return DocumentationScope.Classify(e, id);
            }
            catch { return null; }
        }

        private bool Bookkeeping(long id)
        {
            try
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                return e != null && e.GetType() == typeof(Element) && e.Category == null && string.IsNullOrEmpty(e.Name);
            }
            catch { return false; }
        }

        /// <summary>After a rollback: every created id gone and every deleted id back.</summary>
        public bool? RollbackProven(bool groupRolledBack)
        {
            if (listenError != null || otherDocument || !snapshotComplete) return false;
            if (!groupRolledBack) return false;
            RehearsalVerdict v = RehearsalRules.Judge(true, changes, id =>
            {
                try { return Rid.CanRepresent(id) && doc.GetElement(Rid.Make(id)) != null; }
                catch { return null; }
            });
            return v.RolledBackVerified;
        }
    }
}
