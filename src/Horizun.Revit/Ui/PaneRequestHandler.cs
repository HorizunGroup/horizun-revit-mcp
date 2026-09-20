// -----------------------------------------------------------------------------
// Horizun Revit MCP - the pane's only route to the Revit API. Original Horizun code.
//
// A DockablePane is WPF. Its click handlers run on the UI thread but OUTSIDE a
// Revit API context, and calling the API from there is not merely discouraged: it
// throws InvalidOperationException, and on the paths where it does not, it works
// often enough that nobody notices until a customer's session dies mid-model.
//
// ExternalEvent is Revit's own answer. The pane records what it wants, raises the
// event, and returns; Revit raises the handler on its thread when it is ready to
// be interrupted. Everything below runs there and nowhere else.
//
// TWO REQUESTS, AND THEY ARE NOT THE SAME KIND OF THING:
//
//   A DOCUMENT READING is a poll. The pane asks on every refresh, the answer is
//   stamped with when it was taken, and a stale one is shown WITH its timestamp
//   rather than as the current state. A title from four minutes ago presented as
//   the document in front of somebody is worse than no title.
//
//   A SELECTION is a user action. It happens once, it reports what it did, and it
//   NEVER silently narrows: asked for six elements of which two are gone, it
//   selects four and says four - because a selection of four presented as the
//   row's six is the pane quietly disagreeing with its own history.
//
// IT SELECTS. IT DOES NOT MODIFY. There is no transaction anywhere in this file,
// so nothing it does can change a model - which is the property that lets it run
// without any of the confirmation machinery a write would need.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;

namespace Horizun.Revit.Ui
{
    /// <summary>What the pane asked Revit to do, next time Revit is free.</summary>
    internal sealed class PaneRequestHandler : IExternalEventHandler
    {
        private readonly object _gate = new object();

        private List<long> _select;

        /// <summary>
        /// Elements that live inside a LINK, as (link instance, element) pairs.
        ///
        /// THREE PARTS OF ONE IDENTITY, and none of them is optional: the host document
        /// the pane is looking at, the link INSTANCE (two instances of the same file hold
        /// the same element ids and sit in different places), and the element inside the
        /// linked document. A pair is the smallest thing that names one physical element.
        /// </summary>
        private List<Tuple<long, long>> _selectLinked;
        private Action<string> _report;
        private bool _wantsDocument;

        /// <summary>The title read on Revit's thread, and WHEN. Never shown without the when.</summary>
        public string LastDocumentTitle { get; private set; }

        public DateTime LastDocumentReadUtc { get; private set; } = DateTime.MinValue;

        /// <summary>The Revit version this add-in is loaded into, read from the application.</summary>
        public string LastRevitVersion { get; private set; }

        public string GetName() => "Horizun operations pane";

        /// <summary>Ask for a selection. Replaces any selection request not yet served.</summary>
        public void Request(List<long> ids, Action<string> report)
            => Request(ids, null, report);

        /// <summary>Ask for a selection that may include elements inside links.</summary>
        public void Request(List<long> ids, List<Tuple<long, long>> linked, Action<string> report)
        {
            lock (_gate)
            {
                // REPLACES rather than queues. Two clicks a second apart mean the person
                // changed their mind, and serving the first one after the second would
                // select the row they navigated away from.
                _select = ids;
                _selectLinked = linked;
                _report = report;
            }
        }

        /// <summary>Ask for a fresh document reading on the next raise.</summary>
        public void RequestDocument()
        {
            lock (_gate) _wantsDocument = true;
        }

        public void Execute(UIApplication app)
        {
            List<long> ids;
            List<Tuple<long, long>> linked;
            Action<string> report;
            bool wantsDocument;
            lock (_gate)
            {
                ids = _select; _select = null;
                linked = _selectLinked; _selectLinked = null;
                report = _report; _report = null;
                wantsDocument = _wantsDocument; _wantsDocument = false;
            }

            if (wantsDocument) ReadDocument(app);
            if (ids != null || linked != null) Select(app, ids, linked, report);
        }

        // =====================================================================

        private void ReadDocument(UIApplication app)
        {
            try
            {
                LastRevitVersion = app?.Application?.VersionNumber;
                Document doc = app?.ActiveUIDocument?.Document;
                LastDocumentTitle = doc == null ? "no document open" : SafeTitle(doc);
                LastDocumentReadUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // A pane that throws takes Revit's UI with it. This one reports.
                LastDocumentTitle = "could not be read (" + ex.GetType().Name + ")";
                LastDocumentReadUtc = DateTime.UtcNow;
            }
        }

        private static void Select(UIApplication app, List<long> ids, List<Tuple<long, long>> linked,
                                   Action<string> report)
        {
            ids = ids ?? new List<long>();
            linked = linked ?? new List<Tuple<long, long>>();
            try
            {
                UIDocument ui = app?.ActiveUIDocument;
                Document doc = ui?.Document;
                if (ui == null || doc == null)
                {
                    Say(report, "There is no open document to select in.");
                    return;
                }

                var found = new List<ElementId>();
                var missing = new List<long>();
                foreach (long id in ids)
                {
                    ElementId elementId = Rid.Make(id);
                    Element element = null;
                    try { element = doc.GetElement(elementId); } catch { }
                    if (element == null) missing.Add(id);
                    else found.Add(elementId);
                }

                if (found.Count == 0)
                {
                    // WHERE ARE THEY, THEN? An id means nothing outside the document that
                    // issued it, and the most common reason a row does not resolve here is
                    // that it belongs to a LINK. Looking is cheap and turns "none of these
                    // is here" into something a person can act on.
                    string elsewhere = WhereElse(doc, ids);
                    Say(report,
                        "None of the " + ids.Count + " element(s) this row names is in " + SafeTitle(doc) +
                        ". Either they were deleted, or this row belongs to a DIFFERENT document - a receipt " +
                        "records which one, and ids are only meaningful inside the document that issued them." +
                        (elsewhere == null ? "" : " " + elsewhere));
                    return;
                }

                // ---- elements inside links ------------------------------------------
                // A linked element is selected through a REFERENCE built from the element in
                // the linked document and the instance it appears through - not by id, which
                // means nothing outside the document that issued it.
                var references = new List<Reference>();
                var linkProblems = new List<string>();
                foreach (Tuple<long, long> pair in linked)
                {
                    var instance = doc.GetElement(Rid.Make(pair.Item1)) as RevitLinkInstance;
                    if (instance == null)
                    { linkProblems.Add("link instance " + pair.Item1 + " is not in this document"); continue; }

                    Document inside = null;
                    try { inside = instance.GetLinkDocument(); } catch { }
                    if (inside == null)
                    {
                        // UNLOADED IS NOT MISSING. Revit cannot make a reference into a
                        // document it has not loaded, and saying "not found" would send
                        // somebody looking for a deleted element that is merely unloaded.
                        linkProblems.Add("the link '" + SafeName(instance) + "' is UNLOADED, so nothing " +
                                         "inside it can be selected until it is loaded");
                        continue;
                    }

                    Element target = null;
                    try { target = inside.GetElement(Rid.Make(pair.Item2)); } catch { }
                    if (target == null)
                    { linkProblems.Add("element " + pair.Item2 + " is not in '" + SafeTitle(inside) + "'"); continue; }

                    try { references.Add(new Reference(target).CreateLinkReference(instance)); }
                    catch (Exception ex)
                    { linkProblems.Add("element " + pair.Item2 + " could not be referenced: " + ex.Message); }
                }

                if (references.Count > 0 && found.Count == 0)
                {
                    ui.Selection.SetReferences(references);
                    Say(report, Narrate(references.Count, linked.Count, "linked element", SafeTitle(doc),
                                        linkProblems));
                    return;
                }

                ui.Selection.SetElementIds(found);
                try { ui.ShowElements(found); } catch { /* a view that cannot show them is not a failure */ }

                if (linked.Count > 0)
                    // BOTH KINDS AT ONCE IS NOT SELECTABLE. Revit's selection takes element
                    // ids or references, and mixing them would silently drop one set. The
                    // host elements are selected and the linked ones are NAMED rather than
                    // quietly left out.
                    linkProblems.Insert(0,
                        references.Count + " linked element(s) were resolved and NOT selected: a selection " +
                        "holds host element ids or linked references, not both, and this row named host " +
                        "elements too. Ask for the linked ones on their own to select them.");

                // NEVER SILENTLY NARROWED. Selecting four of six and saying nothing is the
                // pane disagreeing with the history it just displayed.
                Say(report, missing.Count == 0
                    ? "Selected " + found.Count + " element(s) in " + SafeTitle(doc) + "."
                    : "Selected " + found.Count + " of " + ids.Count + " element(s) in " + SafeTitle(doc) +
                      ". " + missing.Count + " are NOT in this document and were not selected: " +
                      string.Join(", ", missing.Take(20).Select(i => i.ToString(CultureInfo.InvariantCulture))) +
                      (missing.Count > 20 ? ", …" : "") +
                      ". They were deleted, or this row belongs to another document.");
            }
            catch (Exception ex)
            {
                Say(report, "Revit refused the selection: " + ex.Message);
            }
        }

        /// <summary>
        /// Whether these ids resolve inside a loaded link, and in which one.
        ///
        /// IT REPORTS AND DOES NOT SELECT. Selecting an element inside a link needs a
        /// linked reference built from the link instance, and the row does not say which
        /// instance its ids belong to - two instances of the same link hold the same
        /// element ids. Guessing an instance would select a different physical element in
        /// the right shape, which is worse than not selecting.
        /// </summary>
        private static string WhereElse(Document host, List<long> ids)
        {
            try
            {
                var hits = new List<string>();
                foreach (RevitLinkInstance instance in new FilteredElementCollector(host)
                             .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document linked = null;
                    try { linked = instance.GetLinkDocument(); } catch { }
                    if (linked == null) continue;          // unloaded: nothing to look in

                    int present = ids.Count(id =>
                    {
                        try { return linked.GetElement(Rid.Make(id)) != null; } catch { return false; }
                    });
                    if (present > 0)
                        hits.Add(present + " of them in the link '" + SafeTitle(linked) + "'");
                }

                if (hits.Count == 0) return null;
                return "They are not missing: " + string.Join("; ", hits) + ". This pane does not select " +
                       "inside a link, because the row does not say WHICH link instance its ids belong to - " +
                       "two instances of the same link hold the same element ids, and guessing one would " +
                       "select a different physical element that looks right.";
            }
            catch { return null; }
        }

        /// <summary>One sentence that never overstates what was selected.</summary>
        private static string Narrate(int selected, int asked, string what, string where,
                                      List<string> problems)
        {
            string head = "Selected " + selected + " of " + asked + " " + what + "(s) through " + where + ".";
            if (problems.Count == 0) return head;
            return head + " " + problems.Count + " could not be: " + string.Join("; ", problems.Take(10)) +
                   (problems.Count > 10 ? "; …" : "") + ".";
        }

        private static string SafeName(Element element)
        {
            try { return element.Name; } catch { return "(unnamed link)"; }
        }

        private static void Say(Action<string> report, string message)
        {
            try { report?.Invoke(message); } catch { /* the pane may be gone; that is not an error here */ }
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc.Title; } catch { return "(untitled)"; }
        }
    }
}
