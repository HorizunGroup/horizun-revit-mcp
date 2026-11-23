using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
namespace Horizun.Revit.Commands
{
    public partial class AuditModelCommand
    {
        private CommandResult WarningDetails(Document doc, JObject request)
        {
            int offset, size, budget;
            string parseError = WarningDetailRules.ParseRequest(request, doc.Title, out offset, out size, out budget);
            if (parseError != null) return CommandResult.Fail(parseError);
            string target = (string)request["target_document"];
            var facts = new List<JObject>(); int index = 0;
            foreach (FailureMessage warning in doc.GetWarnings())
            {
                string description, severity;
                try { description = warning.GetDescriptionText(); } catch { description = null; }
                try { severity = warning.GetSeverity().ToString(); } catch { severity = null; }
                var elements = new JArray(); bool complete = true; string error = null;
                try
                {
                    foreach (ElementId id in warning.GetFailingElements())
                    {
                        string uid = null; string identityError = null;
                        try { Element element = doc.GetElement(id); if (element != null) uid = element.UniqueId; else identityError = "element not found"; }
                        catch (Exception ex) { identityError = ex.Message; }
                        elements.Add(new JObject { ["id"] = Rid.Value(id), ["unique_id"] = uid, ["identity_error"] = identityError });
                    }
                }
                catch (Exception ex) { complete = false; error = ex.Message; }
                facts.Add(new JObject { ["description"] = description, ["severity"] = severity,
                    ["warning_index"] = index++, ["elements"] = elements, ["ids_complete"] = complete,
                    ["ids_error"] = error, ["suggested_call"] = WarningDetailRules.Suggestion(description, target) });
            }
            try { return CommandResult.Ok(WarningDetailRules.Page(WarningDetailRules.Group(facts), offset, size, budget)); }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message); }
        }
    }
}
