using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Horizun.Revit.Core
{
    // Invalidate conservatively on changes to ANY document, including linked models.
    // Cache use still excludes federation and view scope: visibility can change
    // without a document transaction. Caching is an explicit read optimisation.
    internal static class QueryCacheLifecycle
    {
        internal static readonly BoundedReadCache Cache = new BoundedReadCache();
        internal static bool Ready { get; private set; }
        // Counts only what can change the GEOMETRY or the federation a long read is walking. A save
        // (the content is the same) or a view change (clash reads no view) bumps the cache epoch but
        // not this one, so neither kills a running cooperative clash (R2-N M1).
        private static long _modelEpoch;
        internal static long ModelEpoch => System.Threading.Interlocked.Read(ref _modelEpoch);
        internal static event Action<Document> DocumentEditObserved;
        private static void Model() { System.Threading.Interlocked.Increment(ref _modelEpoch); Cache.Invalidate(); }
        private static void DocumentEdited(Func<Document> readDocument)
        {
            Model();
            var observer = DocumentEditObserved;
            if (observer == null) return;
            Document document = null;
            try { document = readDocument(); } catch { }
            observer(document);
        }
        internal static void Attach(UIControlledApplication app)
        {
            Ready = false;
            app.ControlledApplication.DocumentChanged += Changed;
            app.ControlledApplication.DocumentOpened += Opened;
            app.ControlledApplication.DocumentClosing += Closing;
            app.ControlledApplication.DocumentClosed += Closed;
            app.ControlledApplication.DocumentSaved += Saved;
            app.ControlledApplication.DocumentSavingAs += SavingAs;
            app.ControlledApplication.DocumentSavedAs += SavedAs;
            app.ControlledApplication.DocumentReloadedLatest += ReloadedLatest;
            app.ControlledApplication.DocumentSynchronizedWithCentral += Synchronized;
            app.ControlledApplication.DocumentSynchronizingWithCentral += Synchronizing;
            app.ControlledApplication.DocumentReloadingLatest += ReloadingLatest;
            app.ControlledApplication.LinkedResourceOpened += LinkedOpened;
            app.ControlledApplication.LinkedResourceOpening += LinkedOpening;
            app.ViewActivated += ViewActivated;
            Cache.Invalidate(); Ready = true;
        }
        internal static void Detach(UIControlledApplication app)
        {
            Ready = false; Cache.Invalidate();
            app.ControlledApplication.DocumentChanged -= Changed;
            app.ControlledApplication.DocumentOpened -= Opened;
            app.ControlledApplication.DocumentClosing -= Closing;
            app.ControlledApplication.DocumentClosed -= Closed;
            app.ControlledApplication.DocumentSaved -= Saved;
            app.ControlledApplication.DocumentSavingAs -= SavingAs;
            app.ControlledApplication.DocumentSavedAs -= SavedAs;
            app.ControlledApplication.DocumentReloadedLatest -= ReloadedLatest;
            app.ControlledApplication.DocumentSynchronizedWithCentral -= Synchronized;
            app.ControlledApplication.DocumentSynchronizingWithCentral -= Synchronizing;
            app.ControlledApplication.DocumentReloadingLatest -= ReloadingLatest;
            app.ControlledApplication.LinkedResourceOpened -= LinkedOpened;
            app.ControlledApplication.LinkedResourceOpening -= LinkedOpening;
            app.ViewActivated -= ViewActivated;
        }
        static void Changed(object s, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e) => DocumentEdited(e.GetDocument);
        static void Opened(object s, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e) => Model();
        // R2W m-2. DocumentClosing can still be CANCELLED (the person answers "Cancel" to the save prompt): the state is only dropped in DocumentClosed,
        // and only when it succeeded. Closing just remembers which key the document id had (the closed event no longer has a Document to name it).
        static void Closing(object s, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e)
        {
            using var probe = CloseProbe.Enter("DocumentClosing");
            Model();
            try { DocumentLifecycleRules.ClosingBegan(e.DocumentId, ChangeWatch.Key(e.Document)); } catch { }
        }
        static void Closed(object s, Autodesk.Revit.DB.Events.DocumentClosedEventArgs e)
        {
            Model();
            try { DocumentLifecycleRules.Closed(e.DocumentId, e.Status == Autodesk.Revit.DB.Events.RevitAPIEventStatus.Succeeded); } catch { }
        }
        static void Saved(object s, Autodesk.Revit.DB.Events.DocumentSavedEventArgs e) => Cache.Invalidate();
        // The document key is its path (or title#hash while unsaved). The first save and Save As change it, and everything keyed by it (the ledger of
        // creations, the sync notice, the history of writes) must follow the document, or what is ours becomes somebody else's (R2U M-A).
        static void SavingAs(object s, Autodesk.Revit.DB.Events.DocumentSavingAsEventArgs e)
        {
            try { DocumentLifecycleRules.SavingAsBegan(e.Document, ChangeWatch.Key(e.Document), e.PathName); } catch { }
        }
        static void SavedAs(object s, Autodesk.Revit.DB.Events.DocumentSavedAsEventArgs e)
        {
            Cache.Invalidate();
            try { DocumentLifecycleRules.SavedAsCompleted(e.Document, ChangeWatch.Key(e.Document), e.OriginalPath, ChangeLedger.Rename, e.Status == Autodesk.Revit.DB.Events.RevitAPIEventStatus.Succeeded); }
            catch { }
        }
        // "Reload Latest" brings other people's changes, and renumbering, exactly as a synchronize does (R2U m-4).
        static void ReloadedLatest(object s, Autodesk.Revit.DB.Events.DocumentReloadedLatestEventArgs e)
        {
            DocumentEdited(() => e.Document);
            try { DocumentLifecycleRules.Reloaded(ChangeWatch.Key(e.Document), DateTime.UtcNow); } catch { }
        }
        static void Synchronized(object s, Autodesk.Revit.DB.Events.DocumentSynchronizedWithCentralEventArgs e)
        {
            DocumentEdited(() => e.Document);
            // Ids read before a synchronize may name other elements after it: health says so (IdFreshnessRules).
            try { DocumentLifecycleRules.Synchronized(ChangeWatch.Key(e.Document), DateTime.UtcNow); } catch { }
            try
            {
                if (e.Document != null)
                {
                    bool? hasAll = null, modified = null;
                    try { hasAll = e.Document.HasAllChangesFromCentral(); } catch { }
                    try { modified = e.Document.IsModified; } catch { }
                    SyncLedgerRules.EndSynchronization(ChangeWatch.Key(e.Document), e.Status == Autodesk.Revit.DB.Events.RevitAPIEventStatus.Succeeded, DateTime.UtcNow, hasAll, modified);
                }
            }
            catch (Exception ex) { Log.Error("Could not reconcile synchronization event", ex); }
        }
        static void Synchronizing(object s, Autodesk.Revit.DB.Events.DocumentSynchronizingWithCentralEventArgs e)
        {
            Model();
            try { if (e.Document != null) SyncLedgerRules.BeginSynchronization(ChangeWatch.Key(e.Document), DateTime.UtcNow); }
            catch (Exception ex) { Log.Error("Could not record synchronization start", ex); }
        }
        static void ReloadingLatest(object s, Autodesk.Revit.DB.Events.DocumentReloadingLatestEventArgs e) => Model();
        static void LinkedOpened(object s, Autodesk.Revit.DB.Events.LinkedResourceOpenedEventArgs e) => Model();
        static void LinkedOpening(object s, Autodesk.Revit.DB.Events.LinkedResourceOpeningEventArgs e) => Model();
        static void ViewActivated(object s, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e) => Cache.Invalidate();
    }
}
