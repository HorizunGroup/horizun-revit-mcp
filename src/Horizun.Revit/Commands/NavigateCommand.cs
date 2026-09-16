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

            // THE COMPOSITE PATH, when the caller has an element that may live in a link.
            //
            // Separate from element_ids rather than merged with it: two ways of saying what to
            // select, disagreeing quietly, is worse than either. Sending both is refused.
            JArray selectionsToken = request["selections"] as JArray;
            if (selectionsToken != null && request["element_ids"] != null)
                return CommandResult.Fail(
                    "send element_ids OR selections, not both. element_ids means host-document ids; " +
                    "selections means (element_id, link_instance_id) pairs. Two lists of what to " +
                    "select can disagree, and nothing here would know which one you meant.");
            if (selectionsToken != null)
                return SelectComposite(uidoc, doc, request, operation, selectionsToken);

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

        /// <summary>
        /// select / zoom over (element_id, link_instance_id) pairs.
        ///
        /// WHY THIS EXISTS. The clash viewer used to take side A's id, call Number() on it and
        /// send it as a host id. For a clash between the host model and a link, that selects
        /// whichever HOST element happens to carry the same number - not nothing, not an
        /// error: a different element, highlighted confidently. An element id means nothing
        /// without the document it belongs to, and this is the path that carries both.
        /// </summary>
        private static CommandResult SelectComposite(UIDocument uidoc, Document doc, JObject request,
                                                     string operation, JArray selectionsToken)
        {
            string refusal;
            List<NavigationTarget> targets = NavigateLinked.Read(selectionsToken, out refusal);
            if (targets == null) return CommandResult.Fail(refusal + " Nothing in the UI was changed.");

            JArray problems;
            List<Reference> references = NavigateLinked.Resolve(doc, targets, out problems);
            if (problems.Count > 0)
                // ALL OR NOTHING. A partial selection of a clash pair lights up one side and
                // reads as though the other side is fine.
                return CommandResult.FailWithDetail(
                    problems.Count + " of " + targets.Count + " selection(s) could not be resolved, so " +
                    "NOTHING was selected: a half-selected clash shows one side lit up and reads as " +
                    "though the other side is fine. " + problems.ToString(Formatting.None),
                    new JObject { ["operation"] = operation, ["problems"] = problems });

            JObject verification = null;
            if (operation == "select" || operation == "select_and_zoom")
                verification = NavigateLinked.SelectAndVerify(uidoc, targets, references);

            bool framed = false;
            string framingProblem = null;
            if (operation == "zoom" || operation == "select_and_zoom")
            {
                try { uidoc.ShowElements(references.Select(r => r.ElementId).ToList()); framed = true; }
                catch (Exception ex) { framingProblem = ex.Message; }
            }

            var payload = new JObject
            {
                ["operation"] = operation,
                ["targets"] = targets.Count,
                ["in_links"] = targets.Count(t => t.InLink),
                ["framed"] = framed,
                ["framing_problem"] = framingProblem,
                ["framing_note"] =
                    "ShowElements frames by HOST id, so a linked target frames its LINK INSTANCE " +
                    "rather than the element inside it. The selection is precise; the camera is as " +
                    "precise as Revit lets it be, and the difference is said rather than glossed."
            };
            if (verification != null) foreach (JProperty p in verification.Properties()) payload[p.Name] = p.Value;
            return CommandResult.Ok(payload);
        }

    }
}
