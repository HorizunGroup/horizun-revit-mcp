using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

// The linked-grid regression runs QueryModelCommand itself. API branches outside
// the fixture throw so they cannot provide a substitute result for the query.
namespace Autodesk.Revit.DB
{
    public class ViewPlan : View
    {
        public Level GenLevel => throw new NotSupportedException();
        public PlanViewRange GetViewRange() => throw new NotSupportedException();
    }

    public class PlanViewRange
    {
        public static ElementId Unlimited, LevelAbove, LevelBelow, Current;
        public ElementId GetLevelId(PlanViewPlane plane) => throw new NotSupportedException();
        public double GetOffset(PlanViewPlane plane) => throw new NotSupportedException();
    }

    public enum PlanViewPlane { TopClipPlane, CutPlane, BottomClipPlane, ViewDepthPlane }
    public class TextNoteType : ElementType { }
    public class Sketch : Element
    {
        public IEnumerable<CurveArray> Profile => throw new NotSupportedException();
    }
    public class CurveArray : List<Curve> { }
    public class ElementFilter
    {
        public bool PassesFilter(Element element) => Passes(element);
        public bool PassesFilter(Document doc, ElementId id) => Passes(doc.GetElement(id));
        public virtual bool Passes(Element element) => throw new NotSupportedException();
    }
    public class ElementClassFilter : ElementFilter
    {
        public ElementClassFilter(Type type) { }
    }
    public class ElementCategoryFilter : ElementFilter
    {
        private readonly ElementId id;
        public ElementCategoryFilter(ElementId id) { this.id = id; }
        public override bool Passes(Element element) => (long?)element.TestCategory == id.Value;
    }
    public class ElementMulticategoryFilter : ElementFilter
    {
        private readonly HashSet<long> categories;
        public ElementMulticategoryFilter(ICollection<ElementId> ids) => categories = ids.Select(id => id.Value).ToHashSet();
        public override bool Passes(Element element) => element.TestCategory is BuiltInCategory category && categories.Contains((long)category);
    }
    public class ElementIsElementTypeFilter : ElementFilter
    {
        private readonly bool inverted;
        public ElementIsElementTypeFilter(bool inverted) => this.inverted = inverted;
        public override bool Passes(Element element) => (element is ElementType) != inverted;
    }
    public class LogicalOrFilter : ElementFilter
    {
        private readonly IReadOnlyList<ElementFilter> filters;
        public LogicalOrFilter(IReadOnlyList<ElementFilter> filters) { this.filters = filters; }
        public LogicalOrFilter(ElementFilter first, ElementFilter second) : this(new[] { first, second }) { }
        public override bool Passes(Element element) => filters.Any(filter => filter.Passes(element));
    }
    public class Units
    {
        public FormatOptions GetFormatOptions(ForgeTypeId spec) => throw new NotSupportedException();
    }
    public class FormatOptions
    {
        public ForgeTypeId GetUnitTypeId() => throw new NotSupportedException();
    }
    public static partial class UnitUtils
    {
        public static double ConvertFromInternalUnits(double value, ForgeTypeId unit) => throw new NotSupportedException();
    }
    public static class LabelUtils
    {
        public static string GetLabelForUnit(ForgeTypeId unit) => throw new NotSupportedException();
    }

    public partial class Document
    {
        public Units GetUnits() => throw new NotSupportedException();
        public Element GetElement(Reference reference) => throw new NotSupportedException();
    }
    public partial class Element
    {
        public IEnumerable<ElementId> GetDependentElements(ElementFilter filter) => throw new NotSupportedException();
        public virtual object GetGeometryObjectFromReference(Reference reference) => throw new NotSupportedException();
    }
    public partial class Parameter
    {
        public string AsValueString() => throw new NotSupportedException();
    }
    public partial class FamilyInstance
    {
        public Element SuperComponent => throw new NotSupportedException();
        public Element Host => throw new NotSupportedException();
        public XYZ FacingOrientation => throw new NotSupportedException();
        public XYZ HandOrientation => throw new NotSupportedException();
        public Transform GetTotalTransform() => throw new NotSupportedException();
        public bool Mirrored => throw new NotSupportedException();
        public bool HandFlipped => throw new NotSupportedException();
        public bool FacingFlipped => throw new NotSupportedException();
        public Reference HostFace => throw new NotSupportedException();
    }
    public partial class Transform
    {
        public XYZ OfVector(XYZ vector) => throw new NotSupportedException();
        public XYZ Origin => throw new NotSupportedException();
        public XYZ BasisZ => throw new NotSupportedException();
    }
}

namespace Horizun.Revit.Commands
{
    internal class RoomMembershipReader
    {
        internal static RoomMembershipReader Create(Document doc, string phase, out string problem) => throw new NotSupportedException();
        internal object Locate(Element element, Transform transform) => throw new NotSupportedException();
        internal JObject ToJson(object location, double scale) => throw new NotSupportedException();
        internal JObject Summary() => throw new NotSupportedException();
    }

    internal static class ParameterResolver
    {
        internal static Parameter Resolve(Element element, Element type, string spec, out string scope, out string error) => throw new NotSupportedException();
    }
    internal static class CreateElementsPlacement
    {
        internal static string FaceKind(Document doc, Element host, Reference face) => throw new NotSupportedException();
    }
    public sealed partial class QueryModelCommand
    {
        private static JObject CompoundJson(Element element) => throw new NotSupportedException();
    }
}

namespace Horizun.Revit.Core
{
    internal static class FederatedVisibility
    {
        internal static JObject Measure(Document doc, bool includeLinks) => includeLinks
            ? new JObject { ["coverage_complete"] = true } : new JObject();
    }
    internal sealed class ElementLevelReader
    {
        internal ElementLevelReader(Document doc) { }
        internal LevelResolution Resolve(Element element) => LevelResolutionRules.Resolve(_ => LevelProbe.Absent());
    }
    internal static partial class SourceTraceStorage
    {
        internal static JObject Read(Element element) => throw new NotSupportedException();
    }
    internal static class FamilyPlacementInfo
    {
        internal static JObject Describe(FamilySymbol element) => throw new NotSupportedException();
    }

}
