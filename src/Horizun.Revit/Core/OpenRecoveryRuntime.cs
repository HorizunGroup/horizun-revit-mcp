using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class OpenRecoveryRuntime
    {
        public static UIDocument Open(UIApplication app, OpenPlan plan, OpenRequest request, out JObject evidence)
        {
            evidence = null;
            if (request.OnOpenDialog != DialogAnswer.ResolveInMemory)
            {
                using (Interference.WithDialogAnswer(request.OnOpenDialog))
                    return app.OpenAndActivateDocument(plan.ModelPath, plan.Options(), false);
            }
            var watcher = Interference.Current;
            if (watcher == null || !watcher.FailuresObserved)
                throw new InvalidOperationException("Failure observation unavailable; nothing was opened.");
            var session = new OpenRecoverySession(app, request.Path);
            UIDocument result;
            using (watcher.WithRecovery(session))
            using (Interference.WithDialogAnswer(DialogAnswer.Cancel))
                result = app.OpenAndActivateDocument(plan.ModelPath, plan.Options(), false);
            if (result?.Document != null) OpenRecoveryDocuments.Mark(result.Document, request.Path);
            var errors = new JArray();
            foreach (var item in session.Resolved)
            {
                var deletions = new JArray();
                if (item.Resolution == "DeleteElements")
                    foreach (long id in item.ElementIds)
                    {
                        string status = OpenRecoveryEffect.CheckDeletedId(id,
                            elementId => result.Document.GetElement(Rid.ToElementId(elementId)) != null);
                        deletions.Add(new JObject { ["element_id"] = id, ["status"] = status });
                    }
                errors.Add(new JObject { ["message"] = item.Message, ["element_ids"] = new JArray(item.ElementIds), ["resolution"] = item.Resolution, ["resolution_status"] = "requested", ["deletion_results"] = deletions });
            }
            evidence = new JObject
            {
                ["resolution_attempts"] = errors,
                ["document_differs_from_file"] = JValue.CreateNull(),
                ["difference_note"] = "Resolutions were requested; deletion results report the post-open presence of each requested element ID.",
                ["protected"] = true,
                ["warning"] = "This document may differ from the source file. Do not save over the original; use an explicitly confirmed save_as to a new path."
            };
            return result;
        }
    }
}
