using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Horizun.Revit.Core
{
    // Lifetime is one serialized OpenAndActivateDocument call.
    internal sealed class OpenRecoverySession
    {
        private readonly UIApplication _app;
        private readonly string _source;
        private readonly List<Document> _alreadyOpen = new List<Document>();
        private readonly OpenRecoveryProcessor _processor = new OpenRecoveryProcessor();
        private readonly HashSet<string> _attempted = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyList<OpenRecoveryRecord> Resolved => _processor.Resolved;

        public OpenRecoverySession(UIApplication app, string source)
        {
            _app = app;
            _source = source;
            foreach (Document document in app.Application.Documents) _alreadyOpen.Add(document);
        }

        public bool TryResolve(FailuresAccessor accessor, FailureMessageAccessor failure)
        {
            Document doc = accessor.GetDocument();
            // Opens are serialized: a document absent before this call belongs to it.
            if (doc == null || _alreadyOpen.Contains(doc))
                return false;
            if (failure.GetSeverity() != FailureSeverity.Error) return false;
            var adapter = new OpenRecoveryFailureAdapter(_app, accessor, failure);
            if (!adapter.Permitted) return false;
            string key = failure.GetFailureDefinitionId().Guid.ToString() + ":" + string.Join(",", adapter.ElementIds);
            if (!_attempted.Add(key)) return false;
            // Mark BEFORE any attempted resolution, including a call that throws.
            OpenRecoveryDocuments.Mark(doc, _source);
            return _processor.TryResolve(adapter);
        }
    }
}
