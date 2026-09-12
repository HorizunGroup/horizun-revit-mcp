using System;
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
        internal static void Attach(UIControlledApplication app)
        {
            Ready = false;
            app.ControlledApplication.DocumentChanged += Changed;
            app.ControlledApplication.DocumentOpened += Opened;
            app.ControlledApplication.DocumentClosing += Closing;
            app.ControlledApplication.DocumentSaved += Saved;
            app.ControlledApplication.DocumentSavedAs += SavedAs;
            app.ControlledApplication.DocumentSynchronizedWithCentral += Synchronized;
            app.ViewActivated += ViewActivated;
            Cache.Invalidate(); Ready = true;
        }
        internal static void Detach(UIControlledApplication app)
        {
            Ready = false; Cache.Invalidate();
            app.ControlledApplication.DocumentChanged -= Changed;
            app.ControlledApplication.DocumentOpened -= Opened;
            app.ControlledApplication.DocumentClosing -= Closing;
            app.ControlledApplication.DocumentSaved -= Saved;
            app.ControlledApplication.DocumentSavedAs -= SavedAs;
            app.ControlledApplication.DocumentSynchronizedWithCentral -= Synchronized;
            app.ViewActivated -= ViewActivated;
        }
        static void Changed(object s, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e) => Cache.Invalidate();
        static void Opened(object s, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e) => Cache.Invalidate();
        static void Closing(object s, Autodesk.Revit.DB.Events.DocumentClosingEventArgs e) => Cache.Invalidate();
        static void Saved(object s, Autodesk.Revit.DB.Events.DocumentSavedEventArgs e) => Cache.Invalidate();
        static void SavedAs(object s, Autodesk.Revit.DB.Events.DocumentSavedAsEventArgs e) => Cache.Invalidate();
        static void Synchronized(object s, Autodesk.Revit.DB.Events.DocumentSynchronizedWithCentralEventArgs e) => Cache.Invalidate();
        static void ViewActivated(object s, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e) => Cache.Invalidate();
    }
}
