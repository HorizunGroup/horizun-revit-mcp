// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// AREA TAGS AND MEP SPACE TAGS for horizun_annotate operation=tag (rules: Core/SpatialTagRules.cs).
//
// IndependentTag.Create refuses an Area and an MEP Space: they take a SPATIAL tag
// (Document.Create.NewAreaTag / NewSpaceTag), the way a Room takes a RoomTag. The same
// request - view_id, element_id, point, add_leader, tag_type_id - now works for them:
//
//   * planning refuses, by name, what the target cannot take: a view that is not an area plan of
//     the Area's scheme and level (a plan at the Space's level), collision placement, another
//     tag_mode, a family path, a tag type of another category, no or several loaded tag types;
//   * the tag is created, its type changed to the chosen one, its leader set, then its head
//     moved to the requested point - the order a Room tag needs;
//   * the re-read checks the tag still points at the element and the view, is not orphaned, its
//     head sits on the requested point, its leader state and its type are the requested ones.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed partial class AnnotateCommand
    {
        /// <summary>"area", "space", or null when the element takes an ordinary or room tag.</summary>
        private static string SpatialKind(Element target)
        {
            if (target is Area) return SpatialTagRules.Area;
            if (target is Space) return SpatialTagRules.Space;
            return null;
        }

        private static ElementId SpatialTargetId(SpatialElementTag tag)
        {
            if (tag is AreaTag area) return area.Area?.Id;
            if (tag is SpaceTag space) return space.Space?.Id;
            return null;
        }

        private static string CategoryToken(Element type)
        {
            try
            {
                long id = type?.Category == null ? 0 : Rid.Value(type.Category.Id);
                return id < int.MinValue || id > int.MaxValue ? null : ((BuiltInCategory)(int)id).ToString();
            }
            catch { return null; }
        }

        private static void PlanSpatialTag(Document doc, Plan p, JObject a)
        {
            string kind = SpatialKind(p.Target);
            string option = SpatialTagRules.OptionRefusal(kind, (a["tag_mode"] == null ? null : TagRules.NormalizeMode(a.Value<string>("tag_mode"))), a.Value<bool?>("avoid_collisions") == true,
                a["tag_family_path"] != null, a["tag_type_name"] != null);
            if (option != null) throw new ArgumentException(option);
            if (a["orientation"] != null && !string.Equals(a.Value<string>("orientation"), "horizontal", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("a " + SpatialTagRules.NativeName(kind) + " is always horizontal; omit orientation.");

            var plan = p.View as ViewPlan;
            if (plan == null && kind == SpatialTagRules.Area)
                throw new ArgumentException(SpatialTagRules.ViewRefusal(kind, p.View.ViewType.ToString(), true, true));
            bool sameScheme = true, sameLevel = true;
            if (plan != null)
            {
                ElementId viewLevel = plan.GenLevel?.Id;
                if (p.Target is Area area)
                {
                    sameScheme = area.AreaScheme != null && plan.AreaScheme != null && area.AreaScheme.Id == plan.AreaScheme.Id;
                    sameLevel = area.LevelId == viewLevel;
                }
                else if (p.Target is Space space) sameLevel = space.LevelId == viewLevel;
            }
            string viewRefusal = SpatialTagRules.ViewRefusal(kind, p.View.ViewType.ToString(), sameScheme, sameLevel);
            if (viewRefusal != null) throw new ArgumentException(viewRefusal);

            if (p.Type != null)
            {
                string typeRefusal = SpatialTagRules.TypeCategoryRefusal(kind, CategoryToken(p.Type));
                if (typeRefusal != null) throw new ArgumentException(typeRefusal);
                p.SpatialType = p.Type as ElementType ?? throw new ArgumentException("tag_type_id must identify an element type.");
                return;
            }
            BuiltInCategory tagCat = kind == SpatialTagRules.Area ? BuiltInCategory.OST_AreaTags : BuiltInCategory.OST_MEPSpaceTags;
            string ambiguous;
            ElementType chosen = DefaultTagType(doc, tagCat, kind == SpatialTagRules.Area ? "AreaTag" : "SpaceTag", out ambiguous);
            if (chosen == null) throw new ArgumentException(ambiguous ?? SpatialTagRules.TypeChoiceError(kind, 0));
            p.SpatialType = chosen;
            p.TypeDefaulted = true;
        }

        private static Element CreateSpatialTag(Document doc, Plan p)
        {
            string kind = SpatialKind(p.Target);
            bool leader = p.Input.Value<bool?>("add_leader") == true;
            UV at = new UV(p.Point.X, p.Point.Y);
            SpatialElementTag created = null;
            try { created = NewSpatialTag(doc, p, at); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) when (leader && ElementCentre(p.Target) != null)
            {
                // A head outside the element is only valid with a leader: born at the element, moved afterwards.
                created = NewSpatialTag(doc, p, ElementCentre(p.Target));
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new InvalidOperationException("Revit refused the " + SpatialTagRules.NativeName(kind) + " at that point (" + ex.Message.Split('\n')[0] +
                    "). A head outside the element needs add_leader=true.");
            }
            if (created == null) throw new InvalidOperationException("Revit returned no " + SpatialTagRules.NativeName(kind) + ".");
            if (p.SpatialType != null && created.GetTypeId() != p.SpatialType.Id)
            {
                ElementId changed = created.ChangeTypeId(p.SpatialType.Id);
                if (changed != null && changed != ElementId.InvalidElementId && changed != created.Id)
                    created = doc.GetElement(changed) as SpatialElementTag ?? created;
            }
            if (leader) created.HasLeader = true;
            p.TagPoint = new XYZ(p.Point.X, p.Point.Y, created.TagHeadPosition.Z);
            created.TagHeadPosition = p.TagPoint;
            ApplySpatialLeader(created, p);
            return created;
        }

        private static UV ElementCentre(Element target)
        {
            XYZ at = (target.Location as LocationPoint)?.Point;
            return at == null ? null : new UV(at.X, at.Y);
        }

        private static SpatialElementTag NewSpatialTag(Document doc, Plan p, UV at)
        {
            if (p.Target is Area area) return doc.Create.NewAreaTag((ViewPlan)p.View, area, at);
            return doc.Create.NewSpaceTag((Space)p.Target, at, p.View);
        }

        private static bool VerifySpatialTag(Plan p, Element e, out string reason)
        {
            reason = null;
            string name = SpatialTagRules.NativeName(SpatialKind(p.Target));
            var native = e as SpatialElementTag;
            if (native == null || native.IsOrphaned || native.OwnerViewId != p.View.Id || SpatialTargetId(native) != p.Target.Id)
            { reason = "the committed " + name + " is missing, orphaned, or bound to another element or view"; return false; }
            if (native.TagHeadPosition.DistanceTo(p.TagPoint ?? p.Point) > 1e-6)
            { reason = "the committed " + name + " head moved from the requested point"; return false; }
            if (native.HasLeader != (p.Input.Value<bool?>("add_leader") == true))
            { reason = "the committed " + name + " leader state (" + native.HasLeader + ") differs from add_leader"; return false; }
            if (p.SpatialType != null && native.GetTypeId() != p.SpatialType.Id)
            { reason = "the committed " + name + " type differs from the chosen one"; return false; }
            return VerifySpatialLeader(native, p, out reason);
        }
    }
}
