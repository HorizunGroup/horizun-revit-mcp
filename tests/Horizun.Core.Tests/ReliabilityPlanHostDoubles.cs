using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public partial class Document
    {
        internal Action<string> TestTransactionStarted;
        internal long ScheduleParameterId = 21;
        internal bool UnrelatedScheduleFieldAdded;

        public ICollection<ElementId> Delete(ElementId id)
        {
            if (!Elements.Remove(id.Value)) throw new InvalidOperationException("element missing");
            TestAfterDelete?.Invoke();
            return new[] { id };
        }
    }

    public partial class Category
    {
        public CategoryType CategoryType { get; set; } = CategoryType.Model;
        public IEnumerable<Category> SubCategories => Array.Empty<Category>();
        public static Category GetCategory(Document doc, ElementId id) =>
            doc.Settings.Categories.SingleOrDefault(c => c.Id.Value == id.Value);
    }

    public enum CategoryType { Model, Annotation, AnalyticalModel, Internal, Invalid }
    public sealed class Categories : List<Category> { }
    public enum ScheduleFieldType { Instance, Count }
    public enum ScheduleFieldDisplayType { Standard, Totals }
    public enum SectionType { Body }

    public sealed class ViewSchedule : View
    {
        public ScheduleDefinition Definition { get; private set; }
        public static IList<ElementId> GetValidCategoriesForSchedule() => throw new NotSupportedException();
        public static bool IsValidCategoryForKeySchedule(ElementId id) => throw new NotSupportedException();
        public static ViewSchedule CreateKeySchedule(Document doc, ElementId id) => throw new NotSupportedException();
        public static ViewSchedule CreateSchedule(Document doc, ElementId categoryId)
        {
            var schedule = new ViewSchedule
            {
                Id = new ElementId(doc.NextElementId++),
                Definition = new ScheduleDefinition(categoryId, doc.ScheduleParameterId, doc.UnrelatedScheduleFieldAdded)
            };
            doc.Elements.Add(schedule.Id.Value, schedule);
            return schedule;
        }
        public TableData GetTableData() => new TableData();
    }

    public sealed class ScheduleDefinition
    {
        private readonly List<ScheduleField> fields = new List<ScheduleField>();
        private readonly List<ScheduleSortGroupField> sortFields = new List<ScheduleSortGroupField>();
        private readonly List<SchedulableField> offered = new List<SchedulableField>();

        internal ScheduleDefinition(ElementId categoryId, long parameterId, bool unrelatedFieldAdded)
        {
            CategoryId = categoryId;
            offered.Add(new SchedulableField { ParameterId = new ElementId(parameterId) });
            if (unrelatedFieldAdded)
                offered.Add(new SchedulableField { ParameterId = new ElementId(30), Name = "Unrequested field" });
        }
        public ElementId CategoryId { get; }
        public bool IsKeySchedule => false;
        public bool IncludeLinkedFiles { get; set; }
        public bool IsItemized { get; set; }
        public IList<ScheduleFieldId> GetFieldOrder() => fields.Select(f => f.FieldId).ToList();
        public ScheduleField GetField(ScheduleFieldId id) => fields[id.IntegerValue];
        public IList<SchedulableField> GetSchedulableFields() => offered;
        public ScheduleField AddField(SchedulableField field)
        {
            var added = new ScheduleField(new ScheduleFieldId(fields.Count), field);
            fields.Add(added);
            return added;
        }
        public void AddSortGroupField(ScheduleSortGroupField field) => sortFields.Add(field);
        public int GetSortGroupFieldCount() => sortFields.Count;
        public ScheduleSortGroupField GetSortGroupField(int i) => sortFields[i];
    }

    public sealed class SchedulableField
    {
        public ScheduleFieldType FieldType => ScheduleFieldType.Instance;
        public ElementId ParameterId { get; set; }
        public string Name { get; set; } = "Review field";
        public string GetName(Document doc) => Name;
    }
    public sealed class ScheduleFieldId
    {
        public ScheduleFieldId(int value) { IntegerValue = value; }
        public int IntegerValue { get; }
    }
    public sealed class ScheduleField
    {
        private readonly SchedulableField schedulable;
        internal ScheduleField(ScheduleFieldId id, SchedulableField schedulable) { FieldId = id; this.schedulable = schedulable; }
        public ScheduleFieldId FieldId { get; }
        public ScheduleFieldType FieldType => schedulable.FieldType;
        public ElementId ParameterId => schedulable.ParameterId;
        public ScheduleFieldDisplayType DisplayType { get; set; }
        public string GetName() => schedulable.GetName(null);
        public SchedulableField GetSchedulableField() => schedulable;
        public ForgeTypeId GetSpecTypeId() => null;
        public bool CanTotal() => false;
    }
    public sealed class ScheduleSortGroupField
    {
        public ScheduleSortGroupField(ScheduleFieldId id) { FieldId = id; }
        public ScheduleFieldId FieldId { get; }
    }
    public sealed class TableData
    {
        public TableSectionData GetSectionData(SectionType section) => new TableSectionData();
    }
    public sealed class TableSectionData
    {
        public int NumberOfRows => 0;
        public int FirstRowNumber => throw new NotSupportedException();
        public bool CanInsertRow(int row) => throw new NotSupportedException();
        public void InsertRow(int row) => throw new NotSupportedException();
    }
    public static partial class UnitUtils
    {
        public static bool IsMeasurableSpec(ForgeTypeId spec) => throw new NotSupportedException();
    }

    public partial class View
    {
        internal readonly HashSet<long> HiddenIds = new HashSet<long>();
        internal bool TestInheritLinkTypeHidden;
        internal int HideWrites;
        internal int UnhideWrites;
        internal int OverrideReads;
        internal int OverrideWrites;
        internal int ElementTransparency;
        public readonly Dictionary<long, OverrideGraphicSettings> TestOverrides = new Dictionary<long, OverrideGraphicSettings>();
        public virtual ViewType ViewType => ViewType.DraftingView;
        public ElementId GetPrimaryViewId() => ElementId.InvalidElementId;
        public ICollection<ElementId> GetDependentViewIds() => Array.Empty<ElementId>();
        public ICollection<ElementId> GetTemplateParameterIds() => Array.Empty<ElementId>();
        public View CreateViewTemplate() => throw new NotSupportedException();
        public void SetNonControlledTemplateParameterIds(ICollection<ElementId> ids) => throw new NotSupportedException();
        public void ApplyViewTemplateParameters(View template) => throw new NotSupportedException();
        public IList<ElementId> GetOrderedFilters() => throw new NotSupportedException();
        public bool GetFilterVisibility(ElementId id) => throw new NotSupportedException();
        public bool GetIsFilterEnabled(ElementId id) => throw new NotSupportedException();
        public OverrideGraphicSettings GetFilterOverrides(ElementId id) => throw new NotSupportedException();
        public bool CanCategoryBeHidden(ElementId id) => throw new NotSupportedException();
        public bool GetCategoryHidden(ElementId id) => throw new NotSupportedException();
        public bool IsCategoryOverridable(ElementId id) => throw new NotSupportedException();
        public OverrideGraphicSettings GetCategoryOverrides(ElementId id) => throw new NotSupportedException();
        public OverrideGraphicSettings GetElementOverrides(ElementId id)
        {
            if (TestScheduleOverrideReadError != null) throw TestScheduleOverrideReadError;
            OverrideReads++;
            return TestOverrides.TryGetValue(id.Value, out OverrideGraphicSettings overrides)
                ? overrides : new OverrideGraphicSettings { Transparency = ElementTransparency };
        }
        public void SetElementOverrides(ElementId id, OverrideGraphicSettings overrides)
        {
            OverrideWrites++;
            ElementTransparency = overrides.Transparency;
            TestOverrides[id.Value] = overrides;
        }
        public void HideElements(ICollection<ElementId> ids)
        {
            if (TestInheritLinkTypeHidden && ids.Any(id => Document.GetElement(id).IsHidden(this)))
                throw new ArgumentException("Every element must be visible before it can be hidden.");
            HideWrites++;
            HiddenIds.UnionWith(ids.Select(id => id.Value));
        }
        public void UnhideElements(ICollection<ElementId> ids)
        {
            if (TestInheritLinkTypeHidden && ids.Any(id => !HiddenIds.Contains(id.Value)))
                throw new ArgumentException("An inherited hide cannot be removed from the instance.");
            UnhideWrites++;
            HiddenIds.ExceptWith(ids.Select(id => id.Value));
        }
        public void SetCategoryHidden(ElementId id, bool hidden) => throw new NotSupportedException();
        public void SetCategoryOverrides(ElementId id, OverrideGraphicSettings overrides) => throw new NotSupportedException();
        public bool AreGraphicsOverridesAllowed() => throw new NotSupportedException();
        public DisplayStyle DisplayStyle { get; set; }
    }

    public class ViewSection : View { }
    public class ElementType : Element { public string FamilyName { get; set; } }
    public partial class Element
    {
        public bool CanBeHidden(View view) => true;
        public bool IsHidden(View view) => view.HiddenIds.Contains(Id.Value) ||
            (view.TestInheritLinkTypeHidden && this is RevitLinkInstance link && view.HiddenIds.Contains(link.GetTypeId().Value));
    }

    public sealed class Color
    {
        public Color(byte red, byte green, byte blue) { Red = red; Green = green; Blue = blue; IsValid = true; }
        public bool IsValid { get; }
        public byte Red { get; }
        public byte Green { get; }
        public byte Blue { get; }
    }
    public sealed class OverrideGraphicSettings
    {
        public const int InvalidPenNumber = -1;
        public int Transparency { get; set; }
        public Color ProjectionLineColor { get; private set; }
        public Color CutLineColor { get; }
        public int ProjectionLineWeight { get; }
        public int CutLineWeight { get; }
        public ElementId ProjectionLinePatternId { get; }
        public ElementId CutLinePatternId { get; }
        public Color SurfaceForegroundPatternColor { get; private set; }
        public ElementId SurfaceForegroundPatternId { get; private set; }
        public void SetSurfaceForegroundPatternId(ElementId id) => SurfaceForegroundPatternId = id;
        public void SetSurfaceForegroundPatternColor(Color color) => SurfaceForegroundPatternColor = color;
        public void SetProjectionLineColor(Color color) => ProjectionLineColor = color;
        public Color SurfaceBackgroundPatternColor { get; }
        public ElementId SurfaceBackgroundPatternId { get; }
        public Color CutForegroundPatternColor { get; private set; }
        public ElementId CutForegroundPatternId { get; private set; }
        public void SetCutForegroundPatternId(ElementId id) => CutForegroundPatternId = id;
        public void SetCutForegroundPatternColor(Color color) => CutForegroundPatternColor = color;
        public Color CutBackgroundPatternColor { get; }
        public ElementId CutBackgroundPatternId { get; }
        public bool IsSurfaceForegroundPatternVisible { get; }
        public bool IsSurfaceBackgroundPatternVisible { get; }
        public bool IsCutForegroundPatternVisible { get; }
        public bool IsCutBackgroundPatternVisible { get; }
        public bool Halftone { get; }
        public ViewDetailLevel DetailLevel { get; }
    }
}

namespace Horizun.Revit.Core
{

    internal static partial class DocumentGate
    {
        [ThreadStatic] private static bool confirmedPlan;
        internal static bool InConfirmedAtomicPlan { get => confirmedPlan; set => confirmedPlan = value; }
        public static DocIdentity IdentityOf(Document doc, string year) => new DocIdentity
        {
            Title = doc.Title, Path = doc.PathName, RevitYear = year, IsWorkshared = doc.IsWorkshared
        };
        internal static IDisposable EnterConfirmedAtomicPlan()
        {
            confirmedPlan = true;
            return new ConfirmedPlan();
        }
        private sealed class ConfirmedPlan : IDisposable
        {
            public void Dispose() => confirmedPlan = false;
        }
    }
}

namespace Horizun.Revit.Commands
{
    internal static class OwnershipProbe
    {
        public static HashSet<string> SessionAdded(Document doc) => new HashSet<string>();
        public static OwnershipVerdict Of(Document doc, View view, HashSet<string> session, out string creator)
        {
            creator = null;
            return OwnershipRules.Decide(doc.IsWorkshared, null, false, null);
        }
        public static JObject Row(Document doc, Element element, HashSet<string> session) => throw new NotSupportedException();
    }

    internal static class ViewMarkers
    {
        public static object Filter() => throw new NotSupportedException();
        public static int? ReadCoarser(View view) => throw new NotSupportedException();
        public static void SetCoarser(View view, int scale) => throw new NotSupportedException();
    }

    public sealed partial class ManageViewsCommand
    {
        internal static bool TemplateGoverns(View template, BuiltInParameter parameter) =>
            template == null ? false : throw new NotSupportedException();
        internal static ElementId ResolveCategory(Document doc, string name) => throw new NotSupportedException();
        internal static OverrideGraphicSettings ReadOverrides(Document doc, JObject request, OverrideGraphicSettings before = null)
            => throw new NotSupportedException();
        internal static void RequireOverridesAllowed(View view, string action) => throw new NotSupportedException();
        internal static void RequireCategoryHideable(Document doc, View view, ElementId id, string name) => throw new NotSupportedException();
        internal static bool OverridesMatch(OverrideGraphicSettings graphics, JObject request) => throw new NotSupportedException();
    }

    // Core links the production visibility edits. Printing and other view operations
    // are outside this fixture.
    public sealed partial class ViewVisibilityCommand
    {
        private static ArchModelEdit OverrideReport(Document d, JObject r) => throw new NotSupportedException();
        private static ArchModelEdit GraphicsReport(Document d, JObject r) => throw new NotSupportedException();
        private static ArchModelEdit CopyLinkOverrides(Document d, JObject r, View v, HashSet<string> s) => throw new NotSupportedException();
        private static ArchModelEdit LinkDatumCleanup(Document d, JObject r, View v, HashSet<string> s) => throw new NotSupportedException();
        private static ArchModelEdit DatumCleanup(Document d, JObject r, View v, HashSet<string> s) => throw new NotSupportedException();
        private static ArchModelEdit SetScale(Document d, JObject r, View v, HashSet<string> s) => throw new NotSupportedException();
        private static List<Element> ForeignVisibleMarkers(Document d, View v, HashSet<string> s, JArray kept) => throw new NotSupportedException();
        private sealed class PrintContext { }
        private static JObject PrintCheck(Document d, View v, List<Element> elements, JObject wanted, List<Element> blocked,
            Dictionary<long, PrintContext> contexts) => throw new NotSupportedException();
        private static Dictionary<ElementId, JObject> CategoriesBehind(Document d, View v, List<Element> elements,
            Dictionary<long, PrintContext> contexts) => throw new NotSupportedException();
    }
}
