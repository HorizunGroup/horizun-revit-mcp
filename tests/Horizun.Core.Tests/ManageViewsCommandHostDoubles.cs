using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Autodesk.Revit.DB
{
    public enum ViewDetailLevel { Coarse, Medium, Fine }
    public enum ViewType { DraftingView }
    public enum ViewFamily { Drafting, ThreeDimensional }
    public class ViewFamilyType : Element { public ViewFamily ViewFamily { get; set; } }

    public class ViewDrafting : View
    {
        public override ViewType ViewType => ViewType.DraftingView;
        public ViewDetailLevel DetailLevel { get; set; }

        public static ViewDrafting Create(Document doc, ElementId type)
        {
            var view = new ViewDrafting { Id = new ElementId(doc.NextElementId++), Document = doc, DetailLevel = doc.TestInitialDetailLevel };
            doc.Elements.Add(view.Id.Value, view);
            return view;
        }
    }

    public partial class Document
    {
        public ViewDetailLevel TestInitialDetailLevel;
        public Exception TestCommittedTransactionDisposeError;
    }
}

namespace Horizun.Revit.Commands
{
    // Core links the production Execute, RunBatch, create and verify paths.
    // Other view operations, including their API-specific preflight, are outside this host.
    public sealed partial class ManageViewsCommand : ICommand
    {
        public string Name => "horizun_manage_views";
        public string Description => Name;

        private static JObject ParseVerbatim(string json) => JObject.Parse(json);
        private static bool Scale(string units, out double scale)
        {
            scale = SheetLayoutRules.FeetPerMm;
            return units == "mm";
        }

        private static readonly HashSet<string> NamedOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "create_sheet", "create_drafting" };
        private static readonly HashSet<string> ModifyingOperations = new HashSet<string>();
        private static readonly string[] RefIdFields = { "title_block_type_id", "view_family_type_id" };

        private static string Validate(Document doc, JObject a, Dictionary<string, Type> known, out string reason)
        {
            reason = null;
            string op = a.Value<string>("operation");
            return NamedOps.Contains(op) || op == "place_view" ? null : throw OutsideFixture();
        }

        private static ViewFamilyType ResolveVft(Document doc, JObject action, ViewFamily family)
            => doc.Elements.Values.OfType<ViewFamilyType>().Single();
        private static T Need<T>(Document doc, JObject action, string key) where T : Element =>
            (T)doc.GetElement(Rid.Make(action.Value<long>(key)));
        private static string SafePlanRef(Document doc, long id) => doc.GetElement(Rid.Make(id)).UniqueId;
        private static string TemplateScaleConflict(Document doc, JObject a, Dictionary<string, KeyValuePair<int, int>> known) => null;
        private static IEnumerable<string> PerspectiveViewNames(JObject action) => throw OutsideFixture();
        private static void CenterPlacedViewports(Document doc, List<Applied> applied) { }
        private static void ReportProgress(string label, int done, int total) { }
        private static ElementId TargetId(Document doc, JObject a, Dictionary<string, ElementId> aliases) => null;

        private static NotSupportedException OutsideFixture() => new NotSupportedException("Only sheet creation, drafting-view creation and placement are exercised by this test host.");
        private static bool IsRenumberOperation(string op) => false;
        private static bool IsPerspectiveOperation(string op) => false;
        private static bool IsSunStudyOperation(string op) => false;
        private static bool IsGraphicDisplayOperation(string op) => false;
        private static bool IsViewPropertiesOperation(string op) => false;
        private static bool IsSelectionFilterOperation(string op) => false;
        private static bool IsStartingViewOperation(string op) => false;
        private static bool IsGridExtentOperation(string op) => false;
        private static bool IsRoomIsometricOperation(string op) => false;
        private static JObject GraphicsDetail(Document doc, JObject a, string op, Element e) => null;
        private static JObject ControlDetail(JObject a, string op) => null;

        private static Element ApplyOther(Document doc, JObject a, Dictionary<string, ElementId> aliases, double scale, List<Applied> applied, string op)
        {
            if (op != "place_view") throw OutsideFixture();
            var viewport = new Viewport
            {
                Id = new ElementId(doc.NextElementId++), Document = doc,
                SheetId = Resolve<ViewSheet>(doc, a, "sheet_id", "sheet_key", aliases).Id,
                ViewId = Resolve<View>(doc, a, "view_id", "view_key", aliases).Id
            };
            viewport.SetBoxCenter(Point(a["point"]) * scale);
            doc.Elements.Add(viewport.Id.Value, viewport);
            return viewport;
        }

        private static bool VerifyOther(Document doc, Applied a, Element e, string op)
        {
            if (op != "place_view") throw OutsideFixture();
            return e is Viewport viewport && viewport.SheetId == a.SheetId && viewport.ViewId == a.ViewId &&
                SheetDistance(viewport.GetBoxCenter(), Point(a.Action["point"]) * a.Scale) < 1e-6;
        }
        private static string ExpandHideOutsideCrop(Document doc, JObject a, double scale, out JArray found) => throw OutsideFixture();
        private static Type ResultType(string op) => op == "create_sheet" ? typeof(ViewSheet) : throw OutsideFixture();
        internal static JArray ExplainGraphics(Document doc, View view, IList<ElementId> ids) => throw OutsideFixture();
        private static IList<ElementId> ReadElementIds(Document doc, JObject a, string field) => throw OutsideFixture();
        private static JToken SheetSetCensus(Document doc) => throw OutsideFixture();
        private static JObject RenumberPreview(Document doc, JObject a) => throw OutsideFixture();
        private static JObject PerspectivePreview(JObject a, double scale) => throw OutsideFixture();
        private static JObject SunStudyPreview(Document doc, JObject a) => throw OutsideFixture();
        private static JObject GraphicDisplayPreview(Document doc, JObject a) => throw OutsideFixture();
        private static JObject ViewPropertiesPreview(Document doc, JObject a) => throw OutsideFixture();
        private static JObject SelectionFilterPreview(JObject a) => throw OutsideFixture();
        private static JObject StartingViewPreview(Document doc) => throw OutsideFixture();
        private static JObject GridExtentPreview(Document doc, JObject a) => throw OutsideFixture();
        private static void BindRenumber(Document doc, IDictionary<string, string> values) => throw OutsideFixture();
        private static void BindSunStudy(Document doc, JObject a, IDictionary<string, string> values) => throw OutsideFixture();
        private static void BindGraphicDisplay(Document doc, JObject a, IDictionary<string, string> values) => throw OutsideFixture();
        private static void BindStartingView(Document doc, IDictionary<string, string> values) => throw OutsideFixture();
        private static void BindGridExtent(Document doc, JObject a, IDictionary<string, string> values) => throw OutsideFixture();
        private static string PlanListBinding(Document doc, JArray ids) => throw OutsideFixture();
        private static double SheetDistance(XYZ a, XYZ b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        private static XYZ Point(JToken p) => new XYZ((double)p[0], (double)p[1], 0);
        private static string DetailNumberOf(Viewport vp) => "1";
        private static JObject NewDetailViewFacts(Document doc, View detailView, JObject a) => throw OutsideFixture();
        private static JObject CropDetail(Document doc, Applied a, View view) => throw OutsideFixture();
        private static JObject RenumberDetail(JObject a) => throw OutsideFixture();
        private static JObject PerspectiveDetail(JObject a) => throw OutsideFixture();
        private static JObject SunStudyDetail(JObject a) => throw OutsideFixture();
        private static JObject GraphicDisplayDetail(JObject a) => throw OutsideFixture();
        private static JObject ViewPropertiesDetail(JObject a) => throw OutsideFixture();
        private static JObject SelectionFilterDetail(JObject a) => throw OutsideFixture();
        private static JObject StartingViewDetail(JObject a) => throw OutsideFixture();
        private static JObject GridExtentDetail(JObject a) => throw OutsideFixture();
        private static JObject View3DDetail(JObject a) => throw OutsideFixture();
        private static void ApplyViewScale(Document doc, View view, int scale, string op) => throw OutsideFixture();
        private static T Resolve<T>(Document doc, JObject a, string id, string key, Dictionary<string, ElementId> aliases) where T : Element =>
            a[key] == null ? Need<T>(doc, a, id) : (T)doc.GetElement(aliases[a.Value<string>(key)]);
    }
}
