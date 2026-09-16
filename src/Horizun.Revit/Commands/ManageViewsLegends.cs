// -----------------------------------------------------------------------------
// Horizun Revit MCP - legends, and the two things Revit will not let anyone do.
// Original Horizun code.
//
// G10 of the 2026-09-14 competitive inventory asked for "leyendas y documentación
// repetitiva". This is the legend half, and it is mostly an exercise in saying no
// accurately, because the Revit API cannot create either of the two things a
// legend is made of:
//
//   THERE IS NO ViewLegend.Create. ViewFamilyType exists for ViewFamily.Legend,
//   and View.Create has no overload that takes it. The only way to obtain a
//   legend view through the API is to DUPLICATE one that already exists - which
//   means a document with no legend at all cannot be given its first one from
//   here, and no amount of trying different overloads changes that.
//
//   THERE IS NO LegendComponent.Create EITHER. A legend component is an
//   annotation whose LEGEND_COMPONENT parameter names the type it draws. It can
//   be COPIED with ElementTransformUtils and then re-pointed at another type,
//   which is what this does; it cannot be conjured where none exists.
//
// Both limits are reported as refusals naming the exact reason and the exact
// manual step, rather than as a generic failure. That distinction is the whole
// point: "Revit's API does not offer this" and "your arguments were wrong" send
// a caller to two completely different places, and a bridge that blurs them
// costs somebody an afternoon.
//
// The alternative - doing it through horizun_execute_python - is not better: the
// API is the same API from there. What Python would buy is nothing, and it would
// cost the typed verification.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        internal static readonly string[] LegendOperations = { "create_legend", "place_legend_component" };

        internal static bool IsLegendOperation(string op)
            => Array.IndexOf(LegendOperations, op ?? "") >= 0;

        // =====================================================================
        // VALIDATION
        // =====================================================================

        internal static void ValidateLegend(Document doc, JObject a, string op,
                                            Dictionary<string, Type> known)
        {
            switch (op)
            {
                case "create_legend":
                {
                    if (string.IsNullOrWhiteSpace(a.Value<string>("name")))
                        throw new ArgumentException(
                            "name is required: an unnamed duplicate arrives as 'Copy of ...' and nobody chose that.");

                    View source = SourceLegend(doc, a);
                    if (source == null)
                        throw new ArgumentException(
                            "this document contains no legend view to duplicate, and the Revit API cannot create " +
                            "the first one: there is no ViewLegend.Create, and View.Create has no overload that " +
                            "takes a ViewFamily.Legend type. Nothing was written. Create one legend by hand " +
                            "(View tab > Legends > Legend); every legend after that can be made from here.");

                    ViewDuplicateOption option = LegendDuplicateOption(a);
                    if (!source.CanViewBeDuplicated(option))
                        throw new ArgumentException(
                            "legend view '" + source.Name + "' reports CanViewBeDuplicated false for " + option +
                            ". Nothing was written.");
                    RequireUnusedViewName(doc, a.Value<string>("name"));
                    break;
                }

                case "place_legend_component":
                {
                    View legend = LegendTarget(doc, a, known);
                    if (legend != null && legend.ViewType != ViewType.Legend)
                        throw new ArgumentException(
                            "view_id must identify a LEGEND view; '" + legend.Name + "' is a " + legend.ViewType +
                            ". A legend component exists only in a legend.");

                    Element type = Need<Element>(doc, a, "component_type_id");
                    if (!(type is ElementType))
                        throw new ArgumentException(
                            "component_type_id must identify a TYPE (a wall type, a door type, a family symbol). " +
                            "A legend component draws a type, not an instance.");

                    if (TemplateComponent(doc) == null)
                        throw new ArgumentException(
                            "this document contains no legend component to copy, and the Revit API cannot create " +
                            "one: there is no LegendComponent.Create, and a legend component is only obtainable by " +
                            "copying an existing one and re-pointing its LEGEND_COMPONENT parameter. Nothing was " +
                            "written. Drag one type into any legend by hand; every component after that can be " +
                            "made from here.");

                    Point(a["point"]);   // throws with the exact bad coordinate
                    break;
                }
            }
        }

        // =====================================================================
        // APPLY
        // =====================================================================

        internal static Element ApplyLegend(Document doc, JObject a, string op,
                                            Dictionary<string, ElementId> aliases, double scale)
        {
            if (op == "create_legend")
            {
                View source = SourceLegend(doc, a);
                View copy = doc.GetElement(source.Duplicate(LegendDuplicateOption(a))) as View;
                if (copy == null) throw new InvalidOperationException("the legend duplicate could not be read back");
                copy.Name = a.Value<string>("name");
                a["__source_legend_id"] = Rid.Value(source.Id);
                return copy;
            }

            if (op == "place_legend_component")
            {
                View legend = Resolve<View>(doc, a, "view_id", "view_key", aliases);
                Element template = TemplateComponent(doc);
                XYZ point = Point(a["point"]) * scale;

                // COPIED, then re-pointed. ElementTransformUtils.CopyElements across
                // views is the only route: the copy lands in the target legend and its
                // LEGEND_COMPONENT parameter is then set to the type the caller asked
                // for. The translation is applied by the copy itself so the component
                // never exists at the wrong place even momentarily.
                View templateView = doc.GetElement(template.OwnerViewId) as View;
                if (templateView == null)
                    throw new InvalidOperationException(
                        "the legend component being copied has no owning view, so it cannot be copied between views");

                ICollection<ElementId> copied = ElementTransformUtils.CopyElements(
                    templateView, new List<ElementId> { template.Id }, legend, Transform.Identity, new CopyPasteOptions());
                if (copied == null || copied.Count != 1)
                    throw new InvalidOperationException(
                        "copying the legend component produced " + (copied == null ? 0 : copied.Count) +
                        " elements; exactly one was expected");

                ElementId newId = copied.First();
                Element component = doc.GetElement(newId);
                Parameter p = component.get_Parameter(BuiltInParameter.LEGEND_COMPONENT);
                if (p == null || p.IsReadOnly)
                    throw new InvalidOperationException(
                        "the copied element has no writable LEGEND_COMPONENT parameter, so it is not a legend " +
                        "component and cannot be re-pointed at another type");
                p.Set(Need<Element>(doc, a, "component_type_id").Id);

                string detailLevel = a.Value<string>("detail_level");
                if (!string.IsNullOrWhiteSpace(detailLevel))
                {
                    Parameter dl = component.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_DETAIL_LEVEL);
                    if (dl != null && !dl.IsReadOnly) dl.Set(DetailLevelValue(detailLevel));
                }

                // Move it to where the caller asked. The copy arrives at the template's
                // own location, which is somebody else's layout decision.
                LocationPoint location = component.Location as LocationPoint;
                if (location != null)
                {
                    XYZ delta = point - location.Point;
                    if (delta.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, newId, delta);
                }

                a["__component_id"] = Rid.Value(newId);
                a["__template_component_id"] = Rid.Value(template.Id);
                return component;
            }

            throw new ArgumentException("unsupported legend operation '" + op + "'");
        }

        // =====================================================================
        // VERIFY
        // =====================================================================

        internal static bool VerifyLegend(Document doc, JObject action, string op, Element e)
        {
            if (op == "create_legend")
                return e is View legend && legend.ViewType == ViewType.Legend &&
                       string.Equals(legend.Name, action.Value<string>("name"), StringComparison.Ordinal);

            if (op == "place_legend_component")
            {
                if (e == null) return false;
                Parameter p = e.get_Parameter(BuiltInParameter.LEGEND_COMPONENT);
                if (p == null) return false;
                long wanted = action.Value<long?>("component_type_id") ?? -1;
                // Re-READ, not assumed: setting LEGEND_COMPONENT to a type the legend
                // cannot draw is accepted by the parameter and reverted by Revit.
                return Rid.Value(p.AsElementId()) == wanted;
            }
            return false;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static View SourceLegend(Document doc, JObject a)
        {
            long explicitId = a.Value<long?>("source_view_id") ?? -1;
            if (Rid.CanRepresent(explicitId))
            {
                View chosen = doc.GetElement(Rid.Make(explicitId)) as View;
                if (chosen == null || chosen.ViewType != ViewType.Legend)
                    throw new ArgumentException("source_view_id must identify an existing LEGEND view");
                return chosen;
            }

            // Any legend will do as a donor: duplicating one copies its scale and its
            // view properties, and the caller renames it immediately. Ordered by id so
            // two runs on the same document pick the same donor and produce the same
            // result - an arbitrary-but-stable choice beats an arbitrary one.
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.Legend)
                .OrderBy(v => Rid.Value(v.Id))
                .FirstOrDefault();
        }

        private static ViewDuplicateOption LegendDuplicateOption(JObject a)
        {
            string requested = a.Value<string>("duplicate_option") ?? "WithDetailing";
            ViewDuplicateOption option;
            if (!Enum.TryParse(requested, true, out option) ||
                !Enum.IsDefined(typeof(ViewDuplicateOption), option))
                throw new ArgumentException("duplicate_option is invalid");
            if (option == ViewDuplicateOption.AsDependent)
                throw new ArgumentException(
                    "a legend cannot be duplicated AsDependent: dependent views exist for plans with shared crop " +
                    "regions, and a legend has no crop. Use Duplicate or WithDetailing.");
            return option;
        }

        /// <summary>
        /// A legend component to copy. Ordered by id for the same reason the donor
        /// legend is: the same document must yield the same choice on every run.
        /// </summary>
        private static Element TemplateComponent(Document doc)
        {
            try
            {
                return new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_LegendComponents)
                    .WhereElementIsNotElementType()
                    .OrderBy(e => Rid.Value(e.Id))
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static View LegendTarget(Document doc, JObject a, Dictionary<string, Type> known)
        {
            string key = a.Value<string>("view_key");
            if (!string.IsNullOrWhiteSpace(key))
            {
                if (!known.TryGetValue(key, out Type actual))
                    throw new ArgumentException("view_key references unknown/prior key '" + key + "'");
                if (!typeof(View).IsAssignableFrom(actual))
                    throw new ArgumentException("view_key '" + key + "' produces " + actual.Name + ", not a view");
                return null;   // created earlier in this same batch; verified after commit
            }
            return Need<View>(doc, a, "view_id");
        }

        private static int DetailLevelValue(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "coarse": return 1;
                case "medium": return 2;
                case "fine": return 3;
                default:
                    throw new ArgumentException("detail_level must be coarse, medium or fine");
            }
        }

        private static void RequireUnusedViewName(Document doc, string name)
        {
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                string existing;
                try { existing = v.Name; } catch { continue; }
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(
                        "a view named '" + name + "' already exists (id " + Rid.Value(v.Id) +
                        "). Revit view names are unique; the duplicate would be renamed behind your back.");
            }
        }
    }
}
