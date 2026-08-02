// -----------------------------------------------------------------------------
// Horizun Revit MCP - visible handoff from an agent result to the Revit user.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class NavigateCommand : ICommand
    {
        public string Name => "horizun_navigate";
        public string Description => "Select, frame or open host-document elements/views in Revit.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            string operation = (request.Value<string>("operation") ?? "").ToLowerInvariant();

            if (operation == "open_view") return OpenView(uidoc, doc, request);
            if (operation == "clear_selection")
            {
                uidoc.Selection.SetElementIds(new List<ElementId>());
                int after = uidoc.Selection.GetElementIds().Count;
                if (after != 0) return CommandResult.Fail("Revit accepted clear_selection but " + after + " element(s) remain selected.");
                return CommandResult.Ok(new JObject
                {
                    ["operation"] = operation, ["selection_verified"] = true, ["selected_after"] = 0
                });
            }
            if (operation != "select" && operation != "zoom" && operation != "select_and_zoom")
                return CommandResult.Fail("operation must be select, clear_selection, zoom, select_and_zoom or open_view.");

            JArray idsToken = request["element_ids"] as JArray;
            if (idsToken == null || idsToken.Count == 0)
                return CommandResult.Fail("element_ids is required and must not be empty for " + operation + ".");
            if (idsToken.Count > 5000) return CommandResult.Fail("element_ids exceeds the 5000 item limit.");

            var resolved = new List<ElementId>();
            var missing = new JArray();
            var invalid = new JArray();
            foreach (JToken token in idsToken)
            {
                long raw;
                if ((token.Type != JTokenType.Integer && token.Type != JTokenType.Float) ||
                    !long.TryParse(token.ToString(Formatting.None), out raw) || !Rid.CanRepresent(raw))
                { invalid.Add(token.DeepClone()); continue; }
                ElementId id = Rid.Make(raw);
                if (doc.GetElement(id) == null) missing.Add(raw);
                else resolved.Add(id);
            }
            if (invalid.Count > 0 || missing.Count > 0)
                return CommandResult.Fail(
                    "Every element_id must resolve in the ACTIVE HOST document. invalid=" + invalid.ToString(Formatting.None) +
                    ", missing=" + missing.ToString(Formatting.None) + ". Nothing in the UI was changed. " +
                    "Ids from linked models need their link_instance_id and are not accepted by this host-only handoff.");

            bool selectionVerified = false;
            if (operation == "select" || operation == "select_and_zoom")
            {
                uidoc.Selection.SetElementIds(resolved);
                var after = new HashSet<long>(uidoc.Selection.GetElementIds().Select(Rid.Value));
                var wanted = new HashSet<long>(resolved.Select(Rid.Value));
                selectionVerified = after.SetEquals(wanted);
                if (!selectionVerified)
                    return CommandResult.Fail("Revit accepted the selection request but the selection read back differently.");
            }

            bool framingRequested = operation == "zoom" || operation == "select_and_zoom";
            if (framingRequested)
            {
                try { uidoc.ShowElements(resolved); }
                catch (Exception ex)
                {
                    return CommandResult.Fail("Revit refused to frame the resolved elements: " + ex.Message +
                        (selectionVerified ? " The selection WAS changed and verified before framing failed." : ""));
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["operation"] = operation,
                ["elements_resolved"] = resolved.Count,
                ["selection_verified"] = selectionVerified,
                ["framing"] = framingRequested ? "request_accepted_not_measurable" : "not_requested",
                ["active_view_id"] = Rid.Value(uidoc.ActiveView.Id),
                ["note"] = framingRequested
                    ? "ShowElements returned without error, but Revit exposes no camera acknowledgement to re-read; no stronger claim is made."
                    : "Selection was re-read from Revit."
            });
        }

        private static CommandResult OpenView(UIDocument uidoc, Document doc, JObject request)
        {
            long raw = request.Value<long?>("view_id") ?? -1;
            if (!Rid.CanRepresent(raw)) return CommandResult.Fail("view_id is required for open_view.");
            View view = doc.GetElement(Rid.Make(raw)) as View;
            if (view == null) return CommandResult.Fail("view_id does not identify a view in the active document.");
            if (view.IsTemplate) return CommandResult.Fail("A view template cannot become the active UI view.");
            try { uidoc.ActiveView = view; }
            catch (Exception ex) { return CommandResult.Fail("Revit refused to activate view '" + view.Name + "': " + ex.Message); }

            View after = uidoc.ActiveView;
            bool verified = after != null && Rid.Value(after.Id) == Rid.Value(view.Id);
            if (!verified) return CommandResult.Fail("Revit accepted the active-view assignment but another view is active on re-read.");
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "open_view", ["view_id"] = Rid.Value(after.Id), ["view_name"] = after.Name,
                ["active_view_verified"] = true
            });
        }
    }
}
