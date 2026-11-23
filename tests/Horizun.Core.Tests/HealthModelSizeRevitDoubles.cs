using System;

namespace Autodesk.Revit.DB
{
    public enum BuiltInCategory { OST_Walls, OST_Doors, OST_PlumbingFixtures, OST_Windows, OST_TitleBlocks, OST_StructuralColumns, OST_StructuralFraming, OST_StairsRailingBaluster = -2000000, OST_Grids, OST_TextNotes, OST_Mass, OST_PlanRegion, OST_Viewers = -12, OST_Sections = -13 }
    public sealed class RevitLinkInstance : Element
    {
        public Document Linked;
        public Transform TestTotalTransform = Transform.Identity;
        internal long TestTypeId;
        public Document GetLinkDocument() => Linked;
        public Transform GetTransform() => TestTotalTransform;
        public Transform GetTotalTransform() => TestTotalTransform;
        public override ElementId GetTypeId() => new ElementId(TestTypeId);
    }

    public partial class Document { internal bool TestRequireNativeCollectorFilter; }

    internal sealed class FilteredElementCollector : System.Collections.Generic.IEnumerable<Element>, IDisposable
    {
        private readonly Document document;
        private bool links;
        private bool types;
        private bool hasTypeFilter;
        private Type elementClass;
        private BuiltInCategory? category;
        private ElementId ownerView;
        private readonly bool visibleOnly;
        private readonly System.Collections.Generic.List<ElementFilter> filters = new();
        public FilteredElementCollector(Document document) { this.document = document; }
        public FilteredElementCollector(Document document, ElementId view) { this.document = document; ownerView=view; visibleOnly = true; }
        public FilteredElementCollector OwnedByView(ElementId view) { ownerView = view; return this; }
        public FilteredElementCollector WherePasses(object filter) => throw new NotSupportedException();
        public FilteredElementCollector WhereElementIsNotElementType() { types = false; hasTypeFilter = true; return this; }
        public FilteredElementCollector WhereElementIsElementType() { types = true; hasTypeFilter = true; return this; }
        public FilteredElementCollector OfCategory(BuiltInCategory category) { this.category=category; return this; }
        public FilteredElementCollector WherePasses(ElementFilter filter) { filters.Add(filter); return this; }
        public FilteredElementCollector OfCategoryId(ElementId id) => OfCategory((BuiltInCategory)id.Value);
        public System.Collections.Generic.IEnumerator<Element> GetEnumerator()
        {
            if (links) { foreach (var link in document.TestLinkDocuments) yield return link; yield break; }
            if (category == BuiltInCategory.OST_TitleBlocks && document.TestTitleBlockReadError is Exception error) throw error;
            if (document.TestRequireNativeCollectorFilter && !hasTypeFilter && filters.Count == 0)
                throw new InvalidOperationException("The collector requires a native filter before iteration.");
            foreach (Element e in document.Elements.Values)
                if ((elementClass == null || elementClass.IsInstanceOfType(e)) &&
                    (!category.HasValue || e.TestCategory == category) &&
                    (ownerView == null || e.TestOwnerView == ownerView.Value) &&
                    (!visibleOnly || !e.TestHiddenInOwnerView) &&
                    (!document.TestRequireNativeCollectorFilter || !hasTypeFilter || (e is ElementType) == types) &&
                    filters.TrueForAll(filter => filter.Passes(e))) yield return e;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() { }
        public FilteredElementCollector OfClass(Type type) { elementClass=type; links = type == typeof(RevitLinkInstance); return this; }
        public System.Collections.Generic.ICollection<ElementId> ToElementIds() => document.TestLinkDocuments.ConvertAll(link => link.Id);
        public ElementId FirstElementId()
        {
            using var elements = GetEnumerator();
            return elements.MoveNext() ? elements.Current.Id : ElementId.InvalidElementId;
        }
        public int GetElementCount()
        {
            document.TestNativeCounts++;
            if (document.TestCountThrows && !links) throw new InvalidOperationException("count unavailable");
            return links ? document.TestLinks : types ? document.TestTypes : document.TestElements;
        }
    }

    internal sealed class FilteredWorksetCollector
    {
        private readonly Document document;
        public FilteredWorksetCollector(Document document) { this.document = document; }
        public WorksetIterator GetWorksetIterator() => new WorksetIterator(document.TestWorksets);
    }

    internal sealed class WorksetIterator
    {
        private int remaining;
        public WorksetIterator(int count) { remaining = count; }
        public bool MoveNext() => remaining-- > 0;
    }
}
