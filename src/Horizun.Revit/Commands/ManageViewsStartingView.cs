// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// horizun_manage_views operation=set_starting_view: the document's Starting View
// (Manage > Starting View) set to a view or a sheet - ONE action of the batch's
// single transaction.
//
//   * StartingViewSettings is one element per document; the view must pass
//     StartingViewSettings.IsAcceptableStartingView (a template, a schedule placed
//     nowhere or an internal view is refused by Revit, so it is refused here first
//     for an existing view and again by the setter for one made in the batch).
//   * The rehearsal reports the CURRENT starting view (id, name, kind) - the read
//     half; the resolved plan binds it, so a change between rehearsal and apply
//     refuses the token as stale.
//   * After the commit StartingViewSettings.ViewId is re-read and must be the view.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        internal static bool IsStartingViewOperation(string op) => string.Equals(op, "set_starting_view", StringComparison.OrdinalIgnoreCase);

        /// <summary>The document's starting view as it reads now: id -1 means Revit's own "last viewed".</summary>
        internal static JObject StartingViewState(Document doc)
        {
            StartingViewSettings settings = StartingViewSettings.GetStartingViewSettings(doc);
            ElementId id = settings?.ViewId ?? ElementId.InvalidElementId;
            View view = id == ElementId.InvalidElementId ? null : doc.GetElement(id) as View;
            return new JObject
            {
                ["settings_id"] = settings == null ? JValue.CreateNull() : new JValue(Rid.Value(settings.Id)),
                ["view_id"] = Rid.Value(id),
                ["view_name"] = view?.Name,
                ["kind"] = view == null ? "last_viewed" : view is ViewSheet ? "sheet" : view.ViewType.ToString(),
                ["sheet_number"] = (view as ViewSheet)?.SheetNumber
            };
        }

        internal static void ValidateStartingView(Document doc, JObject a, Dictionary<string, Type> known)
        {
            Reference<View>(doc, a, "view_id", "view_key", known);
            if (string.IsNullOrWhiteSpace(a.Value<string>("view_key")))
            {
                View view = Need<View>(doc, a, "view_id");
                StartingViewSettings settings = StartingViewSettings.GetStartingViewSettings(doc);
                if (settings == null) throw new ArgumentException("this document has no StartingViewSettings.");
                if (view.IsTemplate)
                    throw new ArgumentException("view " + Rid.Value(view.Id) + " ('" + view.Name + "') is a view TEMPLATE; a document cannot open on a template.");
                if (!settings.IsAcceptableStartingView(view.Id))
                    throw new ArgumentException("view " + Rid.Value(view.Id) + " ('" + view.Name + "') is not acceptable as a starting view " +
                                                "(StartingViewSettings.IsAcceptableStartingView): a template, an internal view or a view Revit cannot open first.");
            }
        }

        internal static JObject StartingViewPreview(Document doc) => new JObject { ["current"] = StartingViewState(doc) };

        internal static void BindStartingView(Document doc, IDictionary<string, string> before)
        {
            JObject state = StartingViewState(doc);
            before["starting_view.view_id"] = ((long)state["view_id"]).ToString(CultureInfo.InvariantCulture);
            before["starting_view.view_name"] = (string)state["view_name"] ?? "";
        }

        internal static Element ApplyStartingView(Document doc, JObject a, Dictionary<string, ElementId> aliases)
        {
            View view = Resolve<View>(doc, a, "view_id", "view_key", aliases);
            StartingViewSettings settings = StartingViewSettings.GetStartingViewSettings(doc)
                ?? throw new InvalidOperationException("this document has no StartingViewSettings.");
            JObject before = StartingViewState(doc);
            if (view.IsTemplate || !settings.IsAcceptableStartingView(view.Id))
                throw new InvalidOperationException("view '" + view.Name + "' is not acceptable as a starting view.");
            settings.ViewId = view.Id;
            a["__starting_view"] = new JObject { ["view_id"] = Rid.Value(view.Id), ["before"] = before };
            return view;
        }

        internal static bool VerifyStartingView(Document doc, JObject a, Element e)
        {
            if (!(a["__starting_view"] is JObject detail) || !(e is View view)) return false;
            JObject now = StartingViewState(doc);
            bool ok = (long)now["view_id"] == Rid.Value(view.Id);
            detail["reread"] = now;
            detail["verified"] = ok;
            return ok;
        }

        internal static JObject StartingViewDetail(JObject a) => a["__starting_view"] as JObject;
    }
}
