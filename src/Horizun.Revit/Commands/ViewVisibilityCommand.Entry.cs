using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class ViewVisibilityCommand : ICommand
    {
        public string Name => "horizun_view_visibility";
        public string Description => "Hide/unhide and override elements and categories in the caller's OWN views only, hide foreign markers, and keep own markers out of other views; re-read after commit.";

        public CommandResult Execute(UIApplication app, string paramsJson) =>
            ModelEditRunner.Run(app, paramsJson, Name, "Horizun: view visibility", ViewVisibilityRules.Validate, Plan);

        private static ArchModelEdit Plan(Document doc, JObject r, double scale)
        {
            string op = (r.Value<string>("operation") ?? "").Trim().ToLowerInvariant();
            HashSet<string> session = OwnershipProbe.SessionAdded(doc);
            if (op == "marker_scale") return MarkerScale(doc, r, session);
            if (op == "override_report") return OverrideReport(doc, r);                       // ViewVisibilityPrint.cs (P5)
            if (op == "graphics_report") return GraphicsReport(doc, r);
            View view = ModelEditRunner.Need<View>(doc, r, "view_id");
            if (view.IsTemplate) throw new ArgumentException("view_id " + Rid.Value(view.Id) + " is a view template; this command edits views, not templates.");
            if (op == "read") return Read(doc, view, session);
            if (op == "copy_link_overrides") return CopyLinkOverrides(doc, r, view, session);   // ViewVisibilityLinks.cs
            if (op == "copy_view_graphics") return CopyViewGraphics(doc, r, view, session);
            if (op == "explain_overrides") return ExplainOverrides(doc, r, view);              // 4.2-N4, CT-P2-2e
            // link_datum_cleanup decides itself which view must be ours: the view (written in place), the template and every view that
            // uses it (edit_template), or none (duplicate_view writes only on the copy it makes, which is ours).
            if (op == "link_datum_cleanup") return LinkDatumCleanup(doc, r, view, session);      // ViewVisibilityLinkDatums.cs (§6)
            RequireOwn(doc, view, session);
            if (op == "datum_cleanup") return DatumCleanup(doc, r, view, session);               // ViewVisibilityDatums.cs
            if (op == "set_scale") return SetScale(doc, r, view, session);                       // ViewVisibilityDatums.cs
            if (op == "hide_foreign_markers") return HideForeignMarkers(doc, view, session, r);
            return Edit(doc, r, op, view);
        }

        private static void RequireOwn(Document doc, View view, HashSet<string> session)
        {
            string creator;
            OwnershipVerdict v = OwnershipProbe.Of(doc, view, session, out creator);
            if (!v.Own)
                throw new ArgumentException("view '" + view.Name + "' (" + Rid.Value(view.Id) + ") is not ours (" + v.Evidence +
                    (v.Reason == null ? "" : ": " + v.Reason) + "). This command only changes visibility in the caller's own views; " +
                    "duplicate it (horizun_manage_views duplicate_view) and work on the copy. Nothing was written.");
        }

    }
}
