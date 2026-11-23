using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public enum DisplayStyle { Wireframe, HLR, FlatColors, Realistic, ShadingWithEdges }

    public partial class Document { }

    public partial class View3D
    {
        public ViewDetailLevel DetailLevel { get; set; }
        public static View3D CreateIsometric(Document doc, ElementId type)
        {
            var view = new View3D { Id = new ElementId(doc.NextElementId++), Document = doc };
            doc.Elements.Add(view.Id.Value, view);
            
            return view;
        }
        public void AddFilter(ElementId id) => throw new NotSupportedException();
        public void SetFilterVisibility(ElementId id, bool visible) => throw new NotSupportedException();
        public void SetFilterOverrides(ElementId id, OverrideGraphicSettings settings) => throw new NotSupportedException();
        public ICollection<ElementId> GetFilters() => throw new NotSupportedException();
    }

    public sealed class FillPatternElement : Element
    {
        public FillPattern GetFillPattern() => new FillPattern();
    }
    public sealed class FillPattern { public bool IsSolidFill => true; }

    public class ElementParameterFilter { public ElementParameterFilter(FilterRule rule) => throw new NotSupportedException(); }
    public class FilterRule { }
    public sealed class ParameterFilterElement : Element
    {
        public static ParameterFilterElement Create(Document doc, string name, ICollection<ElementId> categories, ElementParameterFilter filter) =>
            throw new NotSupportedException();
    }
    public static class ParameterFilterUtilities
    {
        public static ICollection<ElementId> GetFilterableParametersInCommon(Document doc, ICollection<ElementId> categories) =>
            throw new NotSupportedException();
    }
    public static class ParameterFilterRuleFactory
    {
        public static FilterRule CreateEqualsRule(ElementId parameter, string value) => throw new NotSupportedException();
        public static FilterRule CreateContainsRule(ElementId parameter, string value) => throw new NotSupportedException();
    }
}

namespace Horizun.Revit.Commands
{
    public sealed partial class CoordinationViewCommand
    {
        private static ArchModelEdit PlanAudit(Document doc, JObject request, double scale) => throw new NotSupportedException();
    }
}
