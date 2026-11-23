// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// AREA TAGS AND MEP SPACE TAGS (horizun_annotate operation=tag).
//
// Rooms had their own tag path; areas and MEP spaces had none, and IndependentTag.Create
// refuses both (they take a spatial tag: NewAreaTag / NewSpaceTag). This is the Revit-free
// half: which element kinds take which tag, what each view must be, what the options mean
// for a spatial tag - and the refusals that name the cause instead of Revit's generic one.
//
//   area  -> AreaTag  : only in an AREA PLAN of the area's own scheme and level
//   space -> SpaceTag : in a floor, ceiling or structural plan at the space's level
//
// Neither accepts tag_mode other than by_category, avoid_collisions or a family path: the
// type is chosen with tag_type_id from a tag family already loaded in the document.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class SpatialTagRules
    {
        public const string Area = "area";
        public const string Space = "space";

        public static string TagCategory(string kind) => kind == Area ? "OST_AreaTags" : "OST_MEPSpaceTags";
        public static string NativeName(string kind) => kind == Area ? "AreaTag" : "SpaceTag";

        /// <summary>The view types a spatial tag can live in (Revit's ViewType names).</summary>
        public static string ViewRefusal(string kind, string viewTypeName, bool sameScheme, bool sameLevel)
        {
            if (kind == Area)
            {
                if (viewTypeName != "AreaPlan")
                    return "an Area is tagged in an AREA PLAN; view_id is a " + viewTypeName + ". Use the area plan of the area's scheme (manage_views create_area_plan).";
                if (!sameScheme) return "the area plan belongs to another area scheme than the Area, so the Area is not in that view. Tag it in a plan of its own scheme.";
                if (!sameLevel) return "the area plan is at another level than the Area. Tag it in the plan of its own level.";
                return null;
            }
            if (viewTypeName != "FloorPlan" && viewTypeName != "CeilingPlan" && viewTypeName != "EngineeringPlan")
                return "a Space is tagged in a floor, ceiling or structural plan; view_id is a " + viewTypeName + ".";
            if (!sameLevel) return "the plan is at another level than the Space. Tag it in the plan of its own level.";
            return null;
        }

        /// <summary>What a spatial tag does not take. Each is refused so the intent is not dropped silently.</summary>
        public static string OptionRefusal(string kind, string tagMode, bool avoidCollisions, bool hasFamilyPath, bool hasTypeName)
        {
            string tag = NativeName(kind);
            if (avoidCollisions) return tag + " collision placement is not measured by this operation; omit avoid_collisions.";
            if (tagMode != null && !string.Equals(tagMode, "by_category", StringComparison.Ordinal))
                return tag + " only accepts tag_mode=by_category.";
            if (hasFamilyPath || hasTypeName)
                return "tag_family_path and tag_type_name load room tag families only; for an " + (kind == Area ? "Area" : "MEP Space") +
                       " pass tag_type_id of a " + tag + " type already loaded in the document.";
            return null;
        }

        /// <summary>A tag_type_id of the wrong tag category is refused with the category it needed.</summary>
        public static string TypeCategoryRefusal(string kind, string typeCategoryName)
        {
            string need = TagCategory(kind);
            return string.Equals(typeCategoryName, need, StringComparison.Ordinal)
                ? null
                : "tag_type_id must be a type of category " + need + " for " + (kind == Area ? "an Area" : "an MEP Space") + " target.";
        }

        /// <summary>The types a document offers for the kind; exactly one is the default, none or several need a choice.</summary>
        public static string TypeChoiceError(string kind, int available)
        {
            if (available == 1) return null;
            return available == 0
                ? "No " + NativeName(kind) + " type is loaded in the document. Load a " + (kind == Area ? "Area" : "Space") + " Tag family (annotate load_tag_family) or pass tag_type_id."
                : NativeName(kind) + " needs one unambiguous type; " + available + " are loaded. Pass tag_type_id.";
        }
    }
}
