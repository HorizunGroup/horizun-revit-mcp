using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Horizun.Revit.Core
{
    // One adapter per failure; the processor is exercised with mocks without Revit.
    internal sealed class OpenRecoveryFailureAdapter : IOpenRecoveryFailure
    {
        private readonly FailuresAccessor _accessor;
        private readonly FailureMessageAccessor _failure;
        private readonly FailureResolutionType _resolution;
        public string Message { get; }
        public IReadOnlyList<long> ElementIds { get; }
        public string DefaultResolution => _resolution.ToString();
        public bool Permitted { get; }

        public OpenRecoveryFailureAdapter(UIApplication app, FailuresAccessor accessor, FailureMessageAccessor failure)
        {
            _accessor = accessor;
            _failure = failure;
            Message = failure.GetDescriptionText();
            var ids = new List<long>();
            foreach (var id in failure.GetFailingElementIds()) ids.Add(Rid.GetId(id));
            ElementIds = ids.AsReadOnly();
            var definition = Autodesk.Revit.ApplicationServices.Application.GetFailureDefinitionRegistry().FindFailureDefinition(failure.GetFailureDefinitionId());
            if (definition == null) throw new InvalidOperationException("Failure definition unavailable; resolution is unknown.");
            _resolution = definition.GetDefaultResolutionType();
            Permitted = accessor.IsFailureResolutionPermitted(failure, _resolution);
        }
        public void Resolve()
        {
            _failure.SetCurrentResolutionType(_resolution);
            _accessor.ResolveFailure(_failure);
        }
    }
}
