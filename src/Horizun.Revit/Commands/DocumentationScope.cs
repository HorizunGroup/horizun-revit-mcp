// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// The Revit half of permission_profile=documentation for the two generic writers it
// admits (horizun_delete_verified, horizun_write_params_verified): WHAT each target
// element is. The decision - every target documentation, or the whole batch refused
// naming the ids - is OperationPermission.DocumentationTargetRefusal, pure and tested.
//
// Documentation, here: a View (sheets included), a Viewport, a schedule placed on a
// sheet, anything owned by a view (OwnerViewId valid: dimensions, tags, text, detail
// lines, filled regions, ...) and anything in an annotation category. A type is NOT:
// editing a type changes every instance in the project, which is what the excluded
// manage_styles is excluded for.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class DocumentationScope
    {
        /// <summary>True when this call runs under permission_profile=documentation.</summary>
        internal static bool Active =>
            Horizun.Revit.Core.Settings.PermissionProfile == OperationPermission.DocumentationProfile;

        internal static DocumentationTarget Classify(Element e, long? id)
        {
            if (e == null) return new DocumentationTarget { Id = id, Kind = DocumentationTarget.MissingKind };
            string category = null;
            try { category = e.Category?.Name; } catch { }
            var t = new DocumentationTarget { Id = id ?? SafeId(e), Category = category };
            try
            {
                if (e is ElementType || e is ProjectInfo) { t.Kind = DocumentationTarget.ModelKind; return t; }
                if (e is View || e is Viewport || e is ScheduleSheetInstance) { t.Kind = DocumentationTarget.ViewKind; return t; }
                ElementId owner = e.OwnerViewId;
                if (owner != null && owner != ElementId.InvalidElementId) { t.Kind = DocumentationTarget.ViewOwnedKind; return t; }
                if (e.Category != null && e.Category.CategoryType == CategoryType.Annotation)
                { t.Kind = DocumentationTarget.AnnotationKind; return t; }
            }
            catch { /* unreadable: falls through to model, which refuses */ }
            t.Kind = DocumentationTarget.ModelKind;
            return t;
        }

        /// <summary>Null when the ids may be written under this profile; otherwise the refusal.</summary>
        internal static string Refusal(Document doc, IEnumerable<long> ids, string tool)
        {
            if (!Active) return null;
            var targets = new List<DocumentationTarget>();
            foreach (long id in ids ?? Enumerable.Empty<long>())
            {
                Element e = null;
                try { e = Rid.CanRepresentElementId(id) ? doc.GetElement(Rid.ToElementId(id)) : null; } catch { }
                targets.Add(Classify(e, id));
            }
            return OperationPermission.DocumentationTargetRefusal(OperationPermission.DocumentationProfile, targets, tool);
        }

        private static long? SafeId(Element e)
        {
            try { return Rid.GetIdOrNull(e.Id); } catch { return null; }
        }
    }
}
