using System;
using Autodesk.Revit.DB;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// The language-neutral key of a category. Category.Name follows Revit's language
    /// ("Walls" in English, "Muros" in Spanish); the BuiltInCategory name ("OST_Walls") does
    /// not. Rows that report a category carry both, so a reader can address the same
    /// category on any installation. Null for a category that is not built in (a user
    /// subcategory) or when the element or its category cannot be read; it never throws.
    /// </summary>
    internal static class CategoryKey
    {
        internal static string Of(Element element)
        {
            try
            {
                // A text note type has no Category in the API; its name is resolved the same way
                // (ElementIdentityGuard.CategoryNameFor), so its key must be too.
                if (element is TextNoteType && element.Category == null) return nameof(BuiltInCategory.OST_TextNotes);
                return Of(element?.Category);
            }
            catch { return null; }
        }

        internal static string Of(Category category)
        {
            try
            {
                if (category == null) return null;
                long id = Rid.Value(category.Id);
                if (id >= 0 || id < int.MinValue) return null;
                var bic = (BuiltInCategory)(int)id;
                return Enum.IsDefined(typeof(BuiltInCategory), bic) ? Enum.GetName(typeof(BuiltInCategory), bic) : null;
            }
            catch { return null; }
        }
    }
}
