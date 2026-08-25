// -----------------------------------------------------------------------------
// Horizun Revit MCP - THE read of the documentation surface. One collector.
//
// horizun_query_planimetry renders what this returns; horizun_audit_planimetry
// reasons over it. Neither has a collector of its own, and that is deliberate:
// two readers of the same model WILL drift, and the day they do, the query says
// a sheet has two viewports while the auditor says three, and neither is
// obviously wrong. So the read happens here, once, and the two tools become a
// renderer and a rule engine over the same object.
//
// It opens NO transaction and holds NO document state. Everything below is
// FilteredElementCollector plus property reads, each one guarded so a property
// Revit will not surrender becomes a named field note on that row rather than a
// null the reader cannot tell from "there is nothing there".
//
// COORDINATES. Two frames, never mixed, each declared on the row that uses it:
//   * SHEET - what Viewport.GetBoxOutline and ScheduleSheetInstance report. A
//     ViewSheet is a 2D view whose plane is XY, so paper coordinates are simply
//     X and Y in internal feet.
//   * VIEW PLANE - x along View.RightDirection, y along View.UpDirection, from
//     View.Origin. The same convention horizun_query_detail_2d publishes, so a
//     caller that already reads 2D detail does not learn a second one. A model
//     bounding box is projected by all EIGHT corners, because projecting two
//     corners of an axis-aligned model box into a rotated view frame gives a
//     rectangle that is not the element's extent.
//
// API NOTE, measured rather than assumed: every member used below exists,
// identically, in the RevitAPI.dll of 2023, 2024, 2025, 2026 and 2027 - checked
// by metadata over all five installed assemblies before this file was written.
// The one exception is the GuideGrid CLASS, which exists in NONE of them; the
// guide grid is therefore read through BuiltInParameter.SHEET_GUIDE_GRID and
// resolved as an ordinary element.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What to collect, and how much of it.</summary>
    public sealed class PlanimetryScope
    {
        public HashSet<long> SheetIds;      // null = every sheet
        public HashSet<long> ViewIds;       // null = every view
        public HashSet<long> ElementIds;    // null = every annotation/reference
        public List<string> Categories;     // annotation category filter, null = all

        public bool NeedSheets = true;
        public bool NeedViews = true;
        public bool NeedPlacements = true;
        public bool NeedAnnotations = true;
        public bool NeedReferences = true;

        public bool IncludeParameters;
        public List<string> ParameterNames = new List<string>();

        public List<string> TagCoverageCategories = new List<string>();
        public List<string> TagCoverageExcludeParameters = new List<string>();

        /// <summary>Ids the caller named that matched nothing. Filled in by Collect.</summary>
        public List<long> UnmatchedIds = new List<long>();

        public bool Narrowed
        {
            get { return SheetIds != null || ViewIds != null || ElementIds != null || Categories != null; }
        }
    }

    public static class PlanimetryInventory
    {
        /// <summary>
        /// Read the documentation surface of ONE document. Read-only by construction: no
        /// Transaction is opened anywhere below, and nothing here writes.
        /// </summary>
        public static PlanimetrySnapshot Collect(Document doc, PlanimetryScope scope, int revitYear)
        {
            var snap = new PlanimetrySnapshot
            {
                RevitYear = revitYear,
                DocumentTitle = Try(() => doc.Title, null),
                Scoped = scope.Narrowed
            };

            // Coverage first, so an answer that is about half a model says so even if
            // everything below succeeds.
            try
            {
                DocumentVisibilityCoverage visibility = DocumentVisibility.Measure(doc);
                snap.VisibilityCoverage = visibility.ToJson();
                snap.VisibilityCoverageComplete = visibility.CoverageComplete;
            }
            catch (Exception ex)
            {
                snap.VisibilityCoverageComplete = false;
                snap.VisibilityCoverage = new JObject { ["error"] = ex.Message };
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = "visibility_coverage", Error = ex.Message });
            }
            try
            {
                JObject federated = FederatedVisibility.Measure(doc, true);
                snap.LinkCoverage = federated;
                snap.LinkCoverageComplete = federated.Value<bool>("coverage_complete");
            }
            catch (Exception ex)
            {
                snap.LinkCoverageComplete = false;
                snap.LinkCoverage = new JObject { ["error"] = ex.Message };
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = "link_coverage", Error = ex.Message });
            }

            Totals(doc, snap);

            // Views before sheets: a placement names a view, and a sheet names its
            // placements, so the view index has to exist before either is described.
            var allViews = new Dictionary<long, View>();
            var viewNames = new Dictionary<long, string>();
            try
            {
                foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                {
                    long id = Rid.Value(v.Id);
                    allViews[id] = v;
                    viewNames[id] = Try(() => v.Name, null);
                }
            }
            catch (Exception ex)
            {
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = "views_index", Error = ex.Message });
            }

            // Where every view is placed, computed ONCE from the viewport collector rather
            // than from GetAllPlacedViews: a schedule is placed on a sheet WITHOUT a
            // viewport, and a viewport whose sheet cannot be read must not make its view
            // look unplaced.
            var viewportsByView = new Dictionary<long, List<Viewport>>();
            var allViewports = new List<Viewport>();
            try
            {
                foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                {
                    allViewports.Add(vp);
                    long? viewId = TryId(() => vp.ViewId);
                    if (!viewId.HasValue) continue;
                    List<Viewport> list;
                    if (!viewportsByView.TryGetValue(viewId.Value, out list))
                        viewportsByView[viewId.Value] = list = new List<Viewport>();
                    list.Add(vp);
                }
            }
            catch (Exception ex)
            {
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = "viewport_index", Error = ex.Message });
            }

            var schedulePlacements = new List<ScheduleSheetInstance>();
            try
            {
                schedulePlacements.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>());
            }
            catch (Exception ex)
            {
                snap.ChecksFailed.Add(new PlanimetryCheckFailure
                { Check = "schedule_placement_index", Error = ex.Message });
            }

            if (scope.NeedViews || scope.NeedAnnotations || scope.NeedReferences)
                Views(doc, snap, scope, allViews, viewportsByView);
            if (scope.NeedSheets)
                Sheets(doc, snap, scope, schedulePlacements);
            if (scope.NeedPlacements)
                Placements(doc, snap, scope, allViews, viewNames, allViewports, schedulePlacements);
            if (scope.NeedAnnotations)
                Annotations(doc, snap, scope, allViews, viewNames, viewportsByView);
            if (scope.NeedReferences)
                References(doc, snap, scope, allViews, viewNames, viewportsByView);
            if (scope.TagCoverageCategories.Count > 0)
                TagCoverage(doc, snap, scope);

            ResolveUnmatched(snap, scope);
            return snap;
        }

        // =====================================================================
        // TOTALS - always for the WHOLE document, whatever the scope narrowed to
        // =====================================================================
        private static void Totals(Document doc, PlanimetrySnapshot snap)
        {
            Count(doc, snap, "sheets_total", d => new FilteredElementCollector(d).OfClass(typeof(ViewSheet)).GetElementCount());
            Count(doc, snap, "viewports_total", d => new FilteredElementCollector(d).OfClass(typeof(Viewport)).GetElementCount());
            Count(doc, snap, "schedule_placements_total",
                  d => new FilteredElementCollector(d).OfClass(typeof(ScheduleSheetInstance)).GetElementCount());
            Count(doc, snap, "titleblocks_total", d => new FilteredElementCollector(d)
                  .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().GetElementCount());
            Count(doc, snap, "dimensions_total", d => new FilteredElementCollector(d).OfClass(typeof(Dimension)).GetElementCount());
            Count(doc, snap, "tags_total", d => new FilteredElementCollector(d).OfClass(typeof(IndependentTag)).GetElementCount());
            Count(doc, snap, "text_notes_total", d => new FilteredElementCollector(d).OfClass(typeof(TextNote)).GetElementCount());
            Count(doc, snap, "filled_regions_total", d => new FilteredElementCollector(d).OfClass(typeof(FilledRegion)).GetElementCount());
            Count(doc, snap, "revision_clouds_total", d => new FilteredElementCollector(d).OfClass(typeof(RevisionCloud)).GetElementCount());
            Count(doc, snap, "detail_components_total", d => new FilteredElementCollector(d)
                  .OfCategory(BuiltInCategory.OST_DetailComponents).WhereElementIsNotElementType().GetElementCount());
            Count(doc, snap, "generic_annotations_total", d => new FilteredElementCollector(d)
                  .OfCategory(BuiltInCategory.OST_GenericAnnotation).WhereElementIsNotElementType().GetElementCount());
            Count(doc, snap, "detail_curves_total", d => new FilteredElementCollector(d)
                  .OfClass(typeof(CurveElement)).Cast<CurveElement>().Count(c => c is DetailCurve));

            Count(doc, snap, "views_total", d => Views(d).Count(v => !IsTemplateSafe(v) && !(v is ViewSheet)));
            Count(doc, snap, "templates_total", d => Views(d).Count(IsTemplateSafe));
            Count(doc, snap, "sections_total", d => CountType(d, ViewType.Section));
            Count(doc, snap, "elevations_total", d => CountType(d, ViewType.Elevation));
            Count(doc, snap, "drafting_views_total", d => CountType(d, ViewType.DraftingView));
            Count(doc, snap, "legends_total", d => CountType(d, ViewType.Legend));
            Count(doc, snap, "schedules_total", d => new FilteredElementCollector(d)
                  .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Count(s => !IsTemplateSafe(s)));
        }

        private static IEnumerable<View> Views(Document d)
        {
            return new FilteredElementCollector(d).OfClass(typeof(View)).Cast<View>();
        }

        private static int CountType(Document d, ViewType type)
        {
            return Views(d).Count(v => !IsTemplateSafe(v) && SafeViewType(v) == type);
        }

        private static bool IsTemplateSafe(View v)
        {
            try { return v.IsTemplate; } catch { return false; }
        }

        private static ViewType? SafeViewType(View v)
        {
            try { return v.ViewType; } catch { return null; }
        }

        /// <summary>
        /// One total. A total that could not be computed is NOT written as zero: it is
        /// absent from Totals and named in checks_failed, so an inventory that failed to
        /// count sheets never reports a document with no sheets.
        /// </summary>
        private static void Count(Document doc, PlanimetrySnapshot snap, string key, Func<Document, int> f)
        {
            try { snap.Totals[key] = f(doc); }
            catch (Exception ex)
            {
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = key, Error = ex.Message });
            }
        }

        // =====================================================================
        // SHEETS
        // =====================================================================
        private static void Sheets(Document doc, PlanimetrySnapshot snap, PlanimetryScope scope,
                                   List<ScheduleSheetInstance> schedulePlacements)
        {
            List<ViewSheet> sheets;
            try
            {
                sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => scope.SheetIds == null || scope.SheetIds.Contains(Rid.Value(s.Id)))
                    .OrderBy(s => Rid.Value(s.Id)).ToList();
            }
            catch (Exception ex)
            {
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = "sheets", Error = ex.Message });
                return;
            }

            var schedulesBySheet = new Dictionary<long, List<ScheduleSheetInstance>>();
            foreach (ScheduleSheetInstance si in schedulePlacements)
            {
                long? owner = TryId(() => si.OwnerViewId);
                if (!owner.HasValue) continue;
                List<ScheduleSheetInstance> list;
                if (!schedulesBySheet.TryGetValue(owner.Value, out list))
                    schedulesBySheet[owner.Value] = list = new List<ScheduleSheetInstance>();
                list.Add(si);
            }

            foreach (ViewSheet s in sheets)
            {
                var fact = new SheetFact { Id = Rid.Value(s.Id) };
                try
                {
                    fact.UniqueId = Guard(fact, "unique_id", () => s.UniqueId);
                    fact.SheetNumber = Guard(fact, "sheet_number", () => s.SheetNumber);
                    fact.Name = Guard(fact, "name", () => s.Name);
                    fact.IsPlaceholder = GuardBool(fact, "placeholder", () => s.IsPlaceholder);

                    // Title blocks: instances OF this sheet, from a view-scoped collector.
                    try
                    {
                        var titleblocks = new FilteredElementCollector(doc, s.Id)
                            .OfCategory(BuiltInCategory.OST_TitleBlocks)
                            .WhereElementIsNotElementType().ToElements()
                            .OrderBy(e => Rid.Value(e.Id)).ToList();
                        foreach (Element tb in titleblocks) fact.TitleblockIds.Add(Rid.Value(tb.Id));
                        if (titleblocks.Count > 0)
                        {
                            Element first = titleblocks[0];
                            fact.TitleblockTypeId = TryId(() => first.GetTypeId());
                            var symbol = doc.GetElement(first.GetTypeId()) as FamilySymbol;
                            if (symbol != null)
                            {
                                fact.TitleblockTypeName = Guard(fact, "titleblock_type", () => symbol.Name);
                                fact.TitleblockFamilyName = Guard(fact, "titleblock_family",
                                    () => symbol.Family == null ? null : symbol.Family.Name);
                            }
                            fact.TitleblockExtent = SheetBox(first, s);
                        }
                    }
                    catch (Exception ex)
                    {
                        fact.TitleblocksReadable = false;
                        fact.Note("titleblocks", ex.Message);
                    }

                    try
                    {
                        BoundingBoxUV outline = s.Outline;
                        if (outline != null)
                            fact.SheetOutline = PlanBox.FromCorners(outline.Min.U, outline.Min.V,
                                                                    outline.Max.U, outline.Max.V);
                    }
                    catch (Exception ex) { fact.Note("sheet_outline", ex.Message); }

                    fact.ExtentSource = fact.TitleblockExtent.Valid ? "titleblock"
                                      : fact.SheetOutline.Valid ? "sheet_outline" : null;

                    try
                    {
                        foreach (ElementId vp in s.GetAllViewports().OrderBy(Rid.Value))
                            fact.ViewportIds.Add(Rid.Value(vp));
                    }
                    catch (Exception ex) { fact.Note("viewports", ex.Message); }

                    try
                    {
                        foreach (ElementId v in s.GetAllPlacedViews().Select(x => x).OrderBy(Rid.Value))
                            fact.PlacedViewIds.Add(Rid.Value(v));
                    }
                    catch (Exception ex) { fact.Note("placed_views", ex.Message); }

                    List<ScheduleSheetInstance> onSheet;
                    if (schedulesBySheet.TryGetValue(fact.Id, out onSheet))
                        foreach (ScheduleSheetInstance si in onSheet.OrderBy(x => Rid.Value(x.Id)))
                            fact.SchedulePlacementIds.Add(Rid.Value(si.Id));

                    try
                    {
                        foreach (ElementId rev in s.GetAllRevisionIds().OrderBy(Rid.Value))
                            fact.RevisionIds.Add(Rid.Value(rev));
                    }
                    catch (Exception ex) { fact.Note("revisions", ex.Message); }

                    GuideGrid(doc, s, fact);
                    if (scope.IncludeParameters) Parameters(s, scope.ParameterNames, fact.Parameters, fact);
                }
                catch (Exception ex)
                {
                    fact.Readable = false;
                    fact.Note("sheet", ex.Message);
                }
                snap.Sheets.Add(fact);
            }
        }

        /// <summary>
        /// The sheet's guide grid, WITHOUT the GuideGrid class - which exists in none of
        /// the five supported Revit assemblies. The parameter is looked up by its
        /// BuiltInParameter token, so no localized name is involved, and the value is
        /// resolved as an ordinary element for its name.
        /// </summary>
        private static void GuideGrid(Document doc, ViewSheet s, SheetFact fact)
        {
            try
            {
                Parameter p = s.get_Parameter(BuiltInParameter.SHEET_GUIDE_GRID);
                if (p == null || p.StorageType != StorageType.ElementId) return;
                ElementId id = p.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return;
                fact.GuideGridId = Rid.Value(id);
                Element grid = doc.GetElement(id);
                if (grid != null) fact.GuideGridName = Try(() => grid.Name, null);
            }
            catch (Exception ex) { fact.Note("guide_grid", ex.Message); }
        }

        // =====================================================================
        // VIEWS
        // =====================================================================
        private static void Views(Document doc, PlanimetrySnapshot snap, PlanimetryScope scope,
                                  Dictionary<long, View> allViews, Dictionary<long, List<Viewport>> viewportsByView)
        {
            IEnumerable<KeyValuePair<long, View>> selected = allViews
                .Where(kv => !(kv.Value is ViewSheet))
                .Where(kv => scope.ViewIds == null || scope.ViewIds.Contains(kv.Key))
                .OrderBy(kv => kv.Key);

            foreach (KeyValuePair<long, View> kv in selected)
            {
                View v = kv.Value;
                var fact = new ViewFact { Id = kv.Key };
                try
                {
                    fact.UniqueId = Guard(fact, "unique_id", () => v.UniqueId);
                    fact.Name = Guard(fact, "name", () => v.Name);
                    fact.ViewType = Guard(fact, "view_type", () => v.ViewType.ToString());
                    fact.IsTemplate = GuardBool(fact, "is_template", () => v.IsTemplate);

                    try
                    {
                        ElementId t = v.ViewTemplateId;
                        if (t != null && t != ElementId.InvalidElementId)
                        {
                            fact.TemplateId = Rid.Value(t);
                            View template = doc.GetElement(t) as View;
                            fact.TemplateName = template == null ? null : Try(() => template.Name, null);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                    { fact.NoteNotApplicable("template", ex.Message); }
                    catch (Exception ex) { fact.TemplateReadable = false; fact.Note("template", ex.Message); }

                    fact.Scale = GuardInt(fact, "scale", () => v.Scale);
                    fact.Discipline = Guard(fact, "discipline", () => v.Discipline.ToString());
                    fact.DetailLevel = Guard(fact, "detail_level", () => v.DetailLevel.ToString());
                    fact.SubDiscipline = ParameterText(v, "Sub-Discipline");
                    fact.Phase = ParameterElementName(doc, v, BuiltInParameter.VIEW_PHASE);
                    fact.PhaseFilter = ParameterElementName(doc, v, BuiltInParameter.VIEW_PHASE_FILTER);

                    try
                    {
                        Level level = v.GenLevel;
                        if (level != null) { fact.LevelId = Rid.Value(level.Id); fact.LevelName = Try(() => level.Name, null); }
                    }
                    catch (Exception ex) { fact.Note("level", ex.Message); }

                    fact.CanBePrinted = GuardBool(fact, "printable", () => v.CanBePrinted);
                    fact.IsGraphical = GuardBool(fact, "graphical", () => v.CanBePrinted && !(v is ViewSchedule));
                    fact.IsCallout = GuardBool(fact, "is_callout", () => v.IsCallout);

                    try
                    {
                        ElementId primary = v.GetPrimaryViewId();
                        if (primary != null && primary != ElementId.InvalidElementId)
                            fact.PrimaryViewId = Rid.Value(primary);
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                    { fact.NoteNotApplicable("parent_view", ex.Message); }
                    catch (Exception ex) { fact.Note("parent_view", ex.Message); }

                    try
                    {
                        foreach (ElementId d in v.GetDependentViewIds().OrderBy(Rid.Value))
                            fact.DependentViewIds.Add(Rid.Value(d));
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                    { fact.NoteNotApplicable("dependent_views", ex.Message); }
                    catch (Exception ex) { fact.Note("dependent_views", ex.Message); }

                    try
                    {
                        foreach (ElementId f in v.GetFilters().OrderBy(Rid.Value))
                        {
                            fact.FilterIds.Add(Rid.Value(f));
                            Element filter = doc.GetElement(f);
                            fact.FilterNames.Add(filter == null ? null : Try(() => filter.Name, null));
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                    {
                        // A view kind that has no V/G filters at all (a schedule) is not a
                        // view whose filters could not be read.
                        fact.NoteNotApplicable("filters", ex.Message);
                    }
                    catch (Exception ex) { fact.FiltersReadable = false; fact.Note("filters", ex.Message); }

                    ViewPlane(v, fact);
                    Crop(v, fact);
                    ScopeBox(doc, v, fact);
                    ViewRange(doc, v, fact);

                    List<Viewport> placements;
                    if (viewportsByView.TryGetValue(kv.Key, out placements))
                        foreach (Viewport vp in placements.OrderBy(x => Rid.Value(x.Id)))
                        {
                            fact.ViewportIds.Add(Rid.Value(vp.Id));
                            long? sheetId = TryId(() => vp.SheetId);
                            if (sheetId.HasValue && !fact.SheetIds.Contains(sheetId.Value))
                                fact.SheetIds.Add(sheetId.Value);
                        }

                    if (scope.IncludeParameters) Parameters(v, scope.ParameterNames, fact.Parameters, fact);
                }
                catch (Exception ex)
                {
                    fact.Readable = false;
                    fact.Note("view", ex.Message);
                }
                snap.Views.Add(fact);
            }
        }

        private static void ViewPlane(View v, ViewFact fact)
        {
            try
            {
                XYZ o = v.Origin, r = v.RightDirection, u = v.UpDirection;
                if (o == null || r == null || u == null) return;
                fact.Origin = new[] { o.X, o.Y, o.Z };
                fact.RightDirection = new[] { r.X, r.Y, r.Z };
                fact.UpDirection = new[] { u.X, u.Y, u.Z };
            }
            catch
            {
                // Schedules and legends have no plane. That is NOT a read failure, so it
                // does not become a field note - the absent view_plane says it.
            }
        }

        private static void Crop(View v, ViewFact fact)
        {
            fact.CropBoxActive = GuardBool(fact, "crop_box_active", () => v.CropBoxActive);
            fact.CropBoxVisible = GuardBool(fact, "crop_box_visible", () => v.CropBoxVisible);
            if (fact.CropBoxActive != true) return;

            try
            {
                ViewCropRegionShapeManager manager = v.GetCropRegionShapeManager();
                fact.AnnotationCropAvailable = manager.CanHaveAnnotationCrop;

                // The MODEL crop, from the shape manager when the crop is non-rectangular,
                // otherwise from CropBox with its own transform applied.
                var points = new List<XYZ>();
                try
                {
                    foreach (CurveLoop loop in manager.GetCropShape())
                        foreach (Curve c in loop)
                            points.AddRange(c.Tessellate());
                }
                catch { points.Clear(); }

                if (points.Count == 0)
                {
                    BoundingBoxXYZ box = v.CropBox;
                    if (box != null) points.AddRange(Corners(box));
                }
                fact.CropBox = points.Count == 0 ? PlanBox.Unreadable : Project(v, points);
                if (!fact.CropBox.Valid) { fact.CropGeometryReadable = false; fact.Note("crop_box", "the crop geometry could not be read"); }

                if (manager.CanHaveAnnotationCrop)
                {
                    fact.AnnotationCropActive = AnnotationCropActive(v);
                    if (fact.AnnotationCropActive == true)
                    {
                        try
                        {
                            CurveLoop annotation = manager.GetAnnotationCropShape();
                            var annotationPoints = new List<XYZ>();
                            if (annotation != null)
                                foreach (Curve c in annotation) annotationPoints.AddRange(c.Tessellate());
                            fact.AnnotationCrop = annotationPoints.Count == 0
                                ? PlanBox.Unreadable : Project(v, annotationPoints);
                            if (!fact.AnnotationCrop.Valid)
                                fact.Note("annotation_crop", "the annotation crop shape could not be read");
                        }
                        catch (Exception ex) { fact.Note("annotation_crop", ex.Message); }
                    }
                }
                else fact.AnnotationCropActive = false;
            }
            catch (Exception ex)
            {
                fact.CropGeometryReadable = false;
                fact.Note("crop_box", ex.Message);
            }
        }

        /// <summary>
        /// Whether the ANNOTATION crop is on. Read through the BuiltInParameter token, not
        /// a localized name, and reported as null when Revit will not answer - a false
        /// here would silence every "outside the annotation crop" finding in the view.
        /// </summary>
        private static bool? AnnotationCropActive(View v)
        {
            try
            {
                Parameter p = v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (p == null || p.StorageType != StorageType.Integer) return null;
                return p.AsInteger() == 1;
            }
            catch { return null; }
        }

        private static void ScopeBox(Document doc, View v, ViewFact fact)
        {
            try
            {
                Parameter p = v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                if (p == null || p.StorageType != StorageType.ElementId) return;
                ElementId id = p.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return;
                fact.ScopeBoxId = Rid.Value(id);
                Element box = doc.GetElement(id);
                if (box != null) fact.ScopeBoxName = Try(() => box.Name, null);
            }
            catch (Exception ex) { fact.Note("scope_box", ex.Message); }
        }

        private static void ViewRange(Document doc, View v, ViewFact fact)
        {
            var plan = v as ViewPlan;
            if (plan == null) { fact.ViewRangeState = Read.NotApplicable; return; }
            try
            {
                PlanViewRange range = plan.GetViewRange();
                var o = new JObject();
                foreach (PlanViewPlane plane in new[]
                {
                    PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane,
                    PlanViewPlane.BottomClipPlane, PlanViewPlane.UnderlayBottom
                })
                {
                    ElementId levelId = range.GetLevelId(plane);
                    Element level = levelId == null || levelId == ElementId.InvalidElementId
                        ? null : doc.GetElement(levelId);
                    o[plane.ToString()] = new JObject
                    {
                        ["level_id"] = levelId == null ? (JToken)JValue.CreateNull() : Rid.Value(levelId),
                        ["level"] = level == null ? null : Try(() => level.Name, null),
                        ["offset_feet"] = range.GetOffset(plane)
                    };
                }
                fact.ViewRange = o;
                fact.ViewRangeState = Read.Value;

                ElementId underlay = plan.GetUnderlayBaseLevel();
                if (underlay != null && underlay != ElementId.InvalidElementId)
                    fact.UnderlayLevelId = Rid.Value(underlay);
                fact.UnderlayOrientation = plan.GetUnderlayOrientation().ToString();
            }
            catch (Exception ex)
            {
                fact.ViewRangeState = Read.Unreadable;
                fact.Note("view_range", ex.Message);
            }
        }

        // =====================================================================
        // PLACEMENTS
        // =====================================================================
        private static void Placements(Document doc, PlanimetrySnapshot snap, PlanimetryScope scope,
                                       Dictionary<long, View> allViews, Dictionary<long, string> viewNames,
                                       List<Viewport> viewports, List<ScheduleSheetInstance> schedules)
        {
            var sheetNumbers = new Dictionary<long, string>();
            foreach (SheetFact s in snap.Sheets) sheetNumbers[s.Id] = s.SheetNumber;

            foreach (Viewport vp in viewports.OrderBy(x => Rid.Value(x.Id)))
            {
                long? sheetId = TryId(() => vp.SheetId);
                if (scope.SheetIds != null && (!sheetId.HasValue || !scope.SheetIds.Contains(sheetId.Value))) continue;
                long? viewId = TryId(() => vp.ViewId);
                if (scope.ViewIds != null && (!viewId.HasValue || !scope.ViewIds.Contains(viewId.Value))) continue;

                var fact = new PlacementFact
                {
                    Id = Rid.Value(vp.Id),
                    Class = "viewport",
                    SheetId = sheetId ?? -1,
                    ViewId = viewId
                };
                fact.SheetNumber = sheetId.HasValue && sheetNumbers.ContainsKey(sheetId.Value)
                    ? sheetNumbers[sheetId.Value] : SheetNumberOf(doc, sheetId);
                try
                {
                    fact.UniqueId = Guard(fact, "unique_id", () => vp.UniqueId);
                    if (viewId.HasValue)
                    {
                        fact.TargetExists = allViews.ContainsKey(viewId.Value);
                        fact.TargetName = viewNames.ContainsKey(viewId.Value) ? viewNames[viewId.Value] : null;
                    }
                    else { fact.Note("view_id", "the viewport would not name its view"); }

                    try
                    {
                        Outline box = vp.GetBoxOutline();
                        if (box != null)
                            fact.Box = PlanBox.FromCorners(box.MinimumPoint.X, box.MinimumPoint.Y,
                                                            box.MaximumPoint.X, box.MaximumPoint.Y);
                    }
                    catch (Exception ex) { fact.BoundsReadable = false; fact.Note("box_outline", ex.Message); }
                    if (!fact.Box.Valid) fact.BoundsReadable = false;

                    try
                    {
                        // The LABEL is the part a neighbour collides with that the view box
                        // does not contain. Reported separately AND unioned into the extent,
                        // so a caller can audit either.
                        Outline label = vp.GetLabelOutline();
                        if (label != null)
                            fact.LabelBox = PlanBox.FromCorners(label.MinimumPoint.X, label.MinimumPoint.Y,
                                                                label.MaximumPoint.X, label.MaximumPoint.Y);
                    }
                    catch (Exception ex) { fact.Note("label_outline", ex.Message); }

                    try
                    {
                        XYZ centre = vp.GetBoxCenter();
                        if (centre != null) fact.BoxCenter = new[] { centre.X, centre.Y };
                    }
                    catch (Exception ex) { fact.Note("box_center", ex.Message); }

                    fact.Rotation = Guard(fact, "rotation", () => vp.Rotation.ToString());
                    fact.TypeId = TryId(() => vp.GetTypeId());
                    if (fact.TypeId.HasValue)
                    {
                        Element type = doc.GetElement(vp.GetTypeId());
                        if (type != null) fact.TypeName = Guard(fact, "viewport_type", () => type.Name);
                    }
                    fact.Pinned = GuardBool(fact, "pinned", () => vp.Pinned);
                    fact.Title = ParameterTextByToken(vp, BuiltInParameter.VIEWPORT_VIEW_NAME);
                    fact.DetailNumber = ParameterTextByToken(vp, BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
                }
                catch (Exception ex) { fact.Readable = false; fact.Note("viewport", ex.Message); }
                snap.Placements.Add(fact);
            }

            foreach (ScheduleSheetInstance si in schedules.OrderBy(x => Rid.Value(x.Id)))
            {
                long? sheetId = TryId(() => si.OwnerViewId);
                if (scope.SheetIds != null && (!sheetId.HasValue || !scope.SheetIds.Contains(sheetId.Value))) continue;
                if (scope.ViewIds != null) continue;   // a schedule placement is not a view placement

                var fact = new PlacementFact
                {
                    Id = Rid.Value(si.Id),
                    Class = "schedule_placement",
                    SheetId = sheetId ?? -1
                };
                fact.SheetNumber = sheetId.HasValue && sheetNumbers.ContainsKey(sheetId.Value)
                    ? sheetNumbers[sheetId.Value] : SheetNumberOf(doc, sheetId);
                try
                {
                    fact.UniqueId = Guard(fact, "unique_id", () => si.UniqueId);
                    long? scheduleId = TryId(() => si.ScheduleId);
                    fact.ScheduleId = scheduleId;
                    if (scheduleId.HasValue)
                    {
                        Element schedule = doc.GetElement(si.ScheduleId);
                        fact.TargetExists = schedule != null;
                        fact.TargetName = schedule == null ? null : Try(() => schedule.Name, null);
                    }
                    else fact.Note("schedule_id", "the placement would not name its schedule");

                    // A ScheduleSheetInstance publishes a point, not an outline. The extent
                    // comes from its bounding box IN THE SHEET, which is the only reading
                    // that can collide with a viewport's outline.
                    ViewSheet sheet = sheetId.HasValue ? doc.GetElement(si.OwnerViewId) as ViewSheet : null;
                    if (sheet != null) fact.Box = SheetBox(si, sheet);
                    if (!fact.Box.Valid)
                    {
                        fact.BoundsReadable = false;
                        fact.Note("box_outline", "the schedule placement's bounding box on the sheet could not be read");
                    }
                    try
                    {
                        XYZ p = si.Point;
                        if (p != null) fact.BoxCenter = new[] { p.X, p.Y };
                    }
                    catch (Exception ex) { fact.Note("box_center", ex.Message); }
                    fact.Pinned = GuardBool(fact, "pinned", () => si.Pinned);
                }
                catch (Exception ex) { fact.Readable = false; fact.Note("schedule_placement", ex.Message); }
                snap.Placements.Add(fact);
            }
        }

        private static string SheetNumberOf(Document doc, long? sheetId)
        {
            if (!sheetId.HasValue) return null;
            try
            {
                var sheet = doc.GetElement(Rid.Make(sheetId.Value)) as ViewSheet;
                return sheet == null ? null : sheet.SheetNumber;
            }
            catch { return null; }
        }

        // =====================================================================
        // ANNOTATIONS
        // =====================================================================
        private static void Annotations(Document doc, PlanimetrySnapshot snap, PlanimetryScope scope,
                                        Dictionary<long, View> allViews, Dictionary<long, string> viewNames,
                                        Dictionary<long, List<Viewport>> viewportsByView)
        {
            bool Wanted(string category) =>
                scope.Categories == null ||
                scope.Categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));

            if (Wanted("dimensions"))
                Collect<Dimension>(doc, snap, scope, "dimension", d => Dimension(doc, d, allViews, viewNames, viewportsByView, scope));
            if (Wanted("tags"))
                Collect<IndependentTag>(doc, snap, scope, "tag", t => Tag(doc, t, allViews, viewNames, viewportsByView, scope));
            if (Wanted("text_notes"))
                Collect<TextNote>(doc, snap, scope, "text_note", t => Text(doc, t, allViews, viewNames, viewportsByView, scope));
            if (Wanted("detail_curves"))
                CollectFiltered<CurveElement>(doc, snap, scope, "detail_curve", c => c is DetailCurve,
                    c => Curve(doc, c, allViews, viewNames, viewportsByView, scope));
            if (Wanted("filled_regions"))
                Collect<FilledRegion>(doc, snap, scope, "filled_region", f => Region(doc, f, allViews, viewNames, viewportsByView, scope));
            if (Wanted("revision_clouds"))
                Collect<RevisionCloud>(doc, snap, scope, "revision_cloud", c => Cloud(doc, c, allViews, viewNames, viewportsByView, scope));
            if (Wanted("detail_components"))
                CollectCategory(doc, snap, scope, BuiltInCategory.OST_DetailComponents, "detail_component",
                    allViews, viewNames, viewportsByView);
            if (Wanted("generic_annotations"))
                CollectCategory(doc, snap, scope, BuiltInCategory.OST_GenericAnnotation, "generic_annotation",
                    allViews, viewNames, viewportsByView);
        }

        private static void Collect<T>(Document doc, PlanimetrySnapshot snap, PlanimetryScope scope,
                                       string population, Func<T, AnnotationFact> build) where T : Element
        {
            CollectFiltered(doc, snap, scope, population, x => true, build);
        }

        private static void CollectFiltered<T>(Document doc, PlanimetrySnapshot snap, PlanimetryScope scope,
                                               string population, Func<T, bool> keep,
                                               Func<T, AnnotationFact> build) where T : Element
        {
            try
            {
                foreach (T e in new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
                             .Where(keep).OrderBy(x => Rid.Value(x.Id)))
                {
                    long id = Rid.Value(e.Id);
                    if (scope.ElementIds != null && !scope.ElementIds.Contains(id)) continue;
                    if (!InViewScope(e, scope)) continue;
                    try { snap.Annotations.Add(build(e)); }
                    catch (Exception ex)
                    {
                        snap.Unreadable.Add(new PlanimetryUnreadable
                        { Population = population, ElementId = id, Reason = ex.Message });
                    }
                }
            }
            catch (Exception ex)
            {
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = population, Error = ex.Message });
            }
        }

        private static void CollectCategory(Document doc, PlanimetrySnapshot snap, PlanimetryScope scope,
                                            BuiltInCategory category, string kind,
                                            Dictionary<long, View> allViews, Dictionary<long, string> viewNames,
                                            Dictionary<long, List<Viewport>> viewportsByView)
        {
            try
            {
                foreach (Element e in new FilteredElementCollector(doc).OfCategory(category)
                             .WhereElementIsNotElementType().ToElements().OrderBy(x => Rid.Value(x.Id)))
                {
                    long id = Rid.Value(e.Id);
                    if (scope.ElementIds != null && !scope.ElementIds.Contains(id)) continue;
                    if (!InViewScope(e, scope)) continue;
                    try
                    {
                        AnnotationFact fact = Common(doc, e, kind, allViews, viewNames, viewportsByView);
                        fact.GeometryReadable = fact.Box.Valid;
                        snap.Annotations.Add(fact);
                    }
                    catch (Exception ex)
                    {
                        snap.Unreadable.Add(new PlanimetryUnreadable
                        { Population = kind, ElementId = id, Reason = ex.Message });
                    }
                }
            }
            catch (Exception ex)
            {
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = kind, Error = ex.Message });
            }
        }

        private static bool InViewScope(Element e, PlanimetryScope scope)
        {
            if (scope.ViewIds == null) return true;
            long? owner = TryId(() => e.OwnerViewId);
            return owner.HasValue && scope.ViewIds.Contains(owner.Value);
        }

        /// <summary>Identity, owner view, type and bounding box - the part every annotation
        /// row shares, read once so no two kinds disagree about what "the box" means.</summary>
        private static AnnotationFact Common(Document doc, Element e, string kind,
                                             Dictionary<long, View> allViews, Dictionary<long, string> viewNames,
                                             Dictionary<long, List<Viewport>> viewportsByView)
        {
            var fact = new AnnotationFact { Id = Rid.Value(e.Id), Kind = kind };
            fact.UniqueId = Guard(fact, "unique_id", () => e.UniqueId);
            fact.Category = Guard(fact, "category", () => e.Category == null ? null : e.Category.Name);
            fact.Class = e.GetType().Name;
            fact.Pinned = GuardBool(fact, "pinned", () => e.Pinned);
            long? group = TryId(() => e.GroupId);
            if (group.HasValue && group.Value != -1) fact.GroupId = group;

            long? owner = TryId(() => e.OwnerViewId);
            if (owner.HasValue)
            {
                fact.OwnerViewId = owner;
                fact.OwnerViewExists = allViews.ContainsKey(owner.Value);
                fact.OwnerViewName = viewNames.ContainsKey(owner.Value) ? viewNames[owner.Value] : null;
                List<Viewport> placements;
                if (viewportsByView.TryGetValue(owner.Value, out placements))
                    foreach (Viewport vp in placements.OrderBy(x => Rid.Value(x.Id)))
                        fact.SheetPlacementIds.Add(Rid.Value(vp.Id));
            }

            fact.TypeId = TryId(() => e.GetTypeId());
            if (fact.TypeId.HasValue && fact.TypeId.Value != -1)
            {
                Element type = doc.GetElement(e.GetTypeId());
                if (type != null)
                {
                    fact.TypeName = Guard(fact, "type", () => type.Name);
                    var symbol = type as FamilySymbol;
                    if (symbol != null)
                        fact.FamilyName = Try(() => symbol.Family == null ? null : symbol.Family.Name, null);
                }
            }

            View ownerView = owner.HasValue && allViews.ContainsKey(owner.Value) ? allViews[owner.Value] : null;
            if (ownerView != null)
            {
                try
                {
                    BoundingBoxXYZ box = e.get_BoundingBox(ownerView);
                    if (box != null) fact.Box = Project(ownerView, Corners(box));
                }
                catch (Exception ex) { fact.Note("bounding_box", ex.Message); }

                // Whether the element carries a per-element graphic override in its owner
                // view. Read so a requirement set can assert over it and so the fix
                // command's clear_element_override cites a finding the auditor produced -
                // the two must share one interpretation of "overridden".
                try
                {
                    fact.HasViewOverrides = OverridesDifferFromDefaults(ownerView.GetElementOverrides(e.Id));
                }
                catch (Exception ex) { fact.Note("has_view_overrides", ex.Message); }
            }
            else
            {
                fact.NoteNotApplicable("has_view_overrides",
                    "the element has no owner view, so no per-view element override applies");
            }
            if (!fact.Box.Valid)
            {
                fact.BoundsReadable = false;
                if (!fact.Notes.Any(n => n.Field == "bounding_box"))
                    fact.Note("bounding_box", ownerView == null
                        ? "the element has no owner view to be measured in"
                        : "Revit returned no bounding box for this element in its owner view");
            }
            return fact;
        }

        private static AnnotationFact Dimension(Document doc, Dimension d, Dictionary<long, View> allViews,
                                                Dictionary<long, string> viewNames,
                                                Dictionary<long, List<Viewport>> viewportsByView,
                                                PlanimetryScope scope)
        {
            AnnotationFact fact = Common(doc, d, "dimension", allViews, viewNames, viewportsByView);
            fact.IsViewSpecific = GuardBool(fact, "view_specific", () => d.ViewSpecific);
            fact.AreReferencesAvailable = GuardBool(fact, "references_available", () => d.AreReferencesAvailable);
            fact.SegmentCount = GuardInt(fact, "segment_count", () => d.NumberOfSegments);

            // The SAME reference reading horizun_query_dimensions publishes: a reference into
            // a LINK is labelled linked and is never counted broken, because "not inspected"
            // is not "gone". The two commands must not disagree about a broken count.
            try
            {
                ReferenceArray refs = d.References;
                int total = 0, broken = 0, linked = 0, unreadable = 0;
                if (refs != null)
                    foreach (Reference r in refs)
                    {
                        total++;
                        try
                        {
                            bool isLinked = r.LinkedElementId != null && r.LinkedElementId != ElementId.InvalidElementId;
                            if (isLinked) { linked++; continue; }
                            Element target = r.ElementId == ElementId.InvalidElementId ? null : doc.GetElement(r.ElementId);
                            if (target == null) broken++;
                        }
                        catch { unreadable++; }
                    }
                fact.ReferenceCount = total;
                fact.BrokenReferenceCount = broken;
                fact.LinkedReferenceCount = linked;
                fact.UnreadableReferenceCount = unreadable;
            }
            catch (Exception ex) { fact.Note("references", ex.Message); }

            try
            {
                var overrides = new List<string>();
                int segments = fact.SegmentCount ?? 0;
                if (segments <= 1)
                {
                    string over = d.ValueOverride;
                    if (!string.IsNullOrEmpty(over)) overrides.Add(over);
                }
                else
                {
                    int index = 0;
                    foreach (DimensionSegment s in d.Segments)
                    {
                        string over = s.ValueOverride;
                        if (!string.IsNullOrEmpty(over))
                            overrides.Add("segment " + index + ": " + over);
                        index++;
                    }
                }
                fact.ValueOverrides = overrides;
                fact.HasValueOverride = overrides.Count > 0;
            }
            catch (Exception ex) { fact.Note("value_override", ex.Message); }

            return fact;
        }

        private static AnnotationFact Tag(Document doc, IndependentTag t, Dictionary<long, View> allViews,
                                          Dictionary<long, string> viewNames,
                                          Dictionary<long, List<Viewport>> viewportsByView, PlanimetryScope scope)
        {
            AnnotationFact fact = Common(doc, t, "tag", allViews, viewNames, viewportsByView);
            fact.IsOrphaned = GuardBool(fact, "orphaned", () => t.IsOrphaned);
            fact.HasLeader = GuardBool(fact, "has_leader", () => t.HasLeader);
            try
            {
                XYZ head = t.TagHeadPosition;
                if (head != null && fact.OwnerViewId.HasValue && allViews.ContainsKey(fact.OwnerViewId.Value))
                {
                    double[] p = ViewFrame(allViews[fact.OwnerViewId.Value], head);
                    fact.TagHeadPoint = new[] { p[0], p[1] };
                }
            }
            catch (Exception ex) { fact.Note("tag_head_point", ex.Message); }

            try
            {
                // Host targets and LINKED targets are counted separately and never merged:
                // a tag on a linked element is not this model's to fix, and calling it
                // broken would be a finding against another team's file.
                var local = t.GetTaggedLocalElementIds();
                foreach (ElementId id in local.OrderBy(Rid.Value))
                {
                    fact.TaggedElementIds.Add(Rid.Value(id));
                    Element target = doc.GetElement(id);
                    string category = target == null ? null : Try(() => target.Category == null ? null : target.Category.Name, null);
                    if (category != null && !fact.TargetCategories.Contains(category))
                        fact.TargetCategories.Add(category);
                }
                fact.TargetCategories.Sort(StringComparer.Ordinal);

                int all = 0;
                try { all = t.GetTaggedElementIds().Count; } catch { all = local.Count; }
                fact.TargetCount = all;
                fact.TargetsLinked = all > local.Count;
                fact.TargetsReadable = true;
            }
            catch (Exception ex)
            {
                fact.TargetsReadable = false;
                fact.Note("tagged_elements", ex.Message);
            }
            return fact;
        }

        private static AnnotationFact Text(Document doc, TextNote t, Dictionary<long, View> allViews,
                                           Dictionary<long, string> viewNames,
                                           Dictionary<long, List<Viewport>> viewportsByView, PlanimetryScope scope)
        {
            AnnotationFact fact = Common(doc, t, "text_note", allViews, viewNames, viewportsByView);
            try
            {
                fact.Text = t.Text;
                fact.TextIsEmptyOrWhitespace = string.IsNullOrWhiteSpace(fact.Text);
            }
            catch (Exception ex) { fact.Note("text", ex.Message); }
            fact.Width = GuardDouble(fact, "width", () => t.Width);
            fact.Alignment = Guard(fact, "alignment", () => t.HorizontalAlignment.ToString());
            try
            {
                XYZ c = t.Coord;
                if (c != null && fact.OwnerViewId.HasValue && allViews.ContainsKey(fact.OwnerViewId.Value))
                {
                    double[] p = ViewFrame(allViews[fact.OwnerViewId.Value], c);
                    fact.Position = new[] { p[0], p[1] };
                }
            }
            catch (Exception ex) { fact.Note("position", ex.Message); }
            return fact;
        }

        private static AnnotationFact Curve(Document doc, CurveElement c, Dictionary<long, View> allViews,
                                            Dictionary<long, string> viewNames,
                                            Dictionary<long, List<Viewport>> viewportsByView, PlanimetryScope scope)
        {
            AnnotationFact fact = Common(doc, c, "detail_curve", allViews, viewNames, viewportsByView);
            try
            {
                Curve geometry = c.GeometryCurve;
                if (geometry == null)
                {
                    fact.GeometryReadable = false;
                    fact.Note("geometry", "the curve element carries no geometry curve");
                }
                else
                {
                    fact.GeometryReadable = true;
                    double length = geometry.ApproximateLength;
                    fact.CurveLength = length;
                    // Revit's own short-curve tolerance. Below it the curve draws nothing.
                    fact.Degenerate = length <= doc.Application.ShortCurveTolerance;
                }
            }
            catch (Exception ex)
            {
                fact.GeometryReadable = false;
                fact.Note("geometry", ex.Message);
            }
            try
            {
                Element style = c.LineStyle;
                if (style != null) fact.TypeName = Try(() => style.Name, fact.TypeName);
            }
            catch (Exception ex) { fact.Note("line_style", ex.Message); }
            return fact;
        }

        private static AnnotationFact Region(Document doc, FilledRegion f, Dictionary<long, View> allViews,
                                             Dictionary<long, string> viewNames,
                                             Dictionary<long, List<Viewport>> viewportsByView, PlanimetryScope scope)
        {
            AnnotationFact fact = Common(doc, f, "filled_region", allViews, viewNames, viewportsByView);
            try
            {
                IList<CurveLoop> loops = f.GetBoundaries();
                fact.LoopCount = loops == null ? (int?)null : loops.Count;
                fact.GeometryReadable = loops != null;
            }
            catch (Exception ex)
            {
                fact.GeometryReadable = false;
                fact.Note("loops", ex.Message);
            }
            try
            {
                var type = doc.GetElement(f.GetTypeId()) as FilledRegionType;
                if (type != null) fact.IsMasking = type.IsMasking;
            }
            catch (Exception ex) { fact.Note("is_masking", ex.Message); }
            return fact;
        }

        private static AnnotationFact Cloud(Document doc, RevisionCloud c, Dictionary<long, View> allViews,
                                            Dictionary<long, string> viewNames,
                                            Dictionary<long, List<Viewport>> viewportsByView, PlanimetryScope scope)
        {
            AnnotationFact fact = Common(doc, c, "revision_cloud", allViews, viewNames, viewportsByView);
            fact.GeometryReadable = fact.Box.Valid;
            try
            {
                ElementId revision = c.RevisionId;
                if (revision != null && revision != ElementId.InvalidElementId)
                {
                    Element r = doc.GetElement(revision);
                    if (r != null) fact.TypeName = Try(() => r.Name, fact.TypeName);
                }
            }
            catch (Exception ex) { fact.Note("revision", ex.Message); }
            return fact;
        }

        // =====================================================================
        // REFERENCES BETWEEN VIEWS
        // =====================================================================
        private static void References(Document doc, PlanimetrySnapshot snap, PlanimetryScope scope,
                                       Dictionary<long, View> allViews, Dictionary<long, string> viewNames,
                                       Dictionary<long, List<Viewport>> viewportsByView)
        {
            var seen = new HashSet<long>();

            // 1. ELEVATION MARKERS publish their views outright. This is the one relation
            //    the API states rather than implies, so it is read first and never guessed.
            try
            {
                foreach (ElevationMarker marker in new FilteredElementCollector(doc)
                             .OfClass(typeof(ElevationMarker)).Cast<ElevationMarker>()
                             .OrderBy(m => Rid.Value(m.Id)))
                {
                    long id = Rid.Value(marker.Id);
                    if (scope.ElementIds != null && !scope.ElementIds.Contains(id)) continue;
                    long? owner = TryId(() => marker.OwnerViewId);
                    if (scope.ViewIds != null && (!owner.HasValue || !scope.ViewIds.Contains(owner.Value))) continue;
                    seen.Add(id);

                    int max = 0;
                    try { max = marker.MaximumViewCount; } catch { max = 0; }
                    bool any = false;
                    for (int i = 0; i < max; i++)
                    {
                        ElementId targetId;
                        try
                        {
                            if (marker.IsAvailableIndex(i)) continue;   // empty slot, not a reference
                            targetId = marker.GetViewId(i);
                        }
                        catch { continue; }
                        if (targetId == null || targetId == ElementId.InvalidElementId) continue;
                        any = true;
                        snap.References.Add(Reference(doc, marker, "elevation_marker", owner, viewNames,
                                                      allViews, viewportsByView, Rid.Value(targetId), null));
                    }
                    if (!any)
                        snap.References.Add(Reference(doc, marker, "elevation_marker", owner, viewNames,
                            allViews, viewportsByView, null,
                            "the marker holds no elevation view at any available index"));
                }
            }
            catch (Exception ex)
            {
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = "elevation_markers", Error = ex.Message });
            }

            // 2. REFERENCE CALLOUTS, from the view that owns them. Revit publishes the
            //    relation view -> reference elements; the target comes off the element's
            //    own REFERENCE_VIEWER_TARGET_VIEW parameter, by token, never by name.
            try
            {
                foreach (KeyValuePair<long, View> kv in allViews.OrderBy(x => x.Key))
                {
                    if (scope.ViewIds != null && !scope.ViewIds.Contains(kv.Key)) continue;
                    ICollection<ElementId> callouts;
                    try { callouts = kv.Value.GetReferenceCallouts(); }
                    catch { continue; }
                    if (callouts == null) continue;
                    foreach (ElementId id in callouts.OrderBy(Rid.Value))
                    {
                        long raw = Rid.Value(id);
                        if (!seen.Add(raw)) continue;
                        if (scope.ElementIds != null && !scope.ElementIds.Contains(raw)) continue;
                        Element marker = doc.GetElement(id);
                        if (marker == null) continue;
                        string reason;
                        long? target = TargetView(doc, marker, out reason);
                        snap.References.Add(Reference(doc, marker, KindOf(marker), kv.Key, viewNames,
                                                      allViews, viewportsByView, target, reason));
                    }
                }
            }
            catch (Exception ex)
            {
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = "reference_callouts", Error = ex.Message });
            }

            // 3. Anything left in the reference-viewer category. These are the ones whose
            //    target may simply not be discoverable; they are reported as `unknown` with
            //    the reason, never inferred from a name.
            try
            {
                foreach (Element marker in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_ReferenceViewer)
                             .WhereElementIsNotElementType().ToElements()
                             .OrderBy(e => Rid.Value(e.Id)))
                {
                    long raw = Rid.Value(marker.Id);
                    if (!seen.Add(raw)) continue;
                    if (scope.ElementIds != null && !scope.ElementIds.Contains(raw)) continue;
                    long? owner = TryId(() => marker.OwnerViewId);
                    if (scope.ViewIds != null && (!owner.HasValue || !scope.ViewIds.Contains(owner.Value))) continue;
                    string reason;
                    long? target = TargetView(doc, marker, out reason);
                    snap.References.Add(Reference(doc, marker, KindOf(marker), owner, viewNames,
                                                  allViews, viewportsByView, target, reason));
                }
            }
            catch (Exception ex)
            {
                snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = "reference_viewers", Error = ex.Message });
            }

            snap.References = snap.References
                .OrderBy(r => r.Id)
                .ThenBy(r => r.TargetViewId ?? long.MaxValue)
                .ToList();
            snap.Totals["view_references_total"] = snap.References.Count;
        }

        private static string KindOf(Element marker)
        {
            BuiltInCategory category;
            try
            {
                if (marker.Category == null) return "view_reference";
                category = (BuiltInCategory)Rid.Value(marker.Category.Id);
            }
            catch { return "view_reference"; }
            switch (category)
            {
                case BuiltInCategory.OST_Callouts: return "callout";
                case BuiltInCategory.OST_Elev: return "elevation_marker";
                case BuiltInCategory.OST_Viewers: return "section_head";
                case BuiltInCategory.OST_ReferenceViewer: return "reference_viewer";
                default: return "view_reference";
            }
        }

        /// <summary>
        /// The view a reference marker points at, by BuiltInParameter token. When that one
        /// does not answer, exactly ONE ElementId-valued parameter resolving to a View is
        /// accepted; two would be a guess, and a guess here is a fabricated relation.
        /// </summary>
        private static long? TargetView(Document doc, Element marker, out string reason)
        {
            reason = null;
            try
            {
                Parameter p = marker.get_Parameter(BuiltInParameter.REFERENCE_VIEWER_TARGET_VIEW);
                if (p != null && p.StorageType == StorageType.ElementId)
                {
                    ElementId id = p.AsElementId();
                    if (id != null && id != ElementId.InvalidElementId) return Rid.Value(id);
                }
            }
            catch (Exception ex) { reason = "REFERENCE_VIEWER_TARGET_VIEW: " + ex.Message; return null; }

            var candidates = new List<long>();
            try
            {
                foreach (Parameter p in marker.Parameters)
                {
                    if (p == null || p.StorageType != StorageType.ElementId) continue;
                    ElementId id;
                    try { id = p.AsElementId(); } catch { continue; }
                    if (id == null || id == ElementId.InvalidElementId) continue;
                    if (doc.GetElement(id) is View && !(doc.GetElement(id) is ViewSheet))
                        candidates.Add(Rid.Value(id));
                }
            }
            catch (Exception ex) { reason = "parameter scan: " + ex.Message; return null; }

            var distinct = candidates.Distinct().ToList();
            if (distinct.Count == 1) return distinct[0];
            reason = distinct.Count == 0
                ? "REFERENCE_VIEWER_TARGET_VIEW is empty and no parameter on this marker resolves to a view; " +
                  "the API exposes no other relation, so the target is unknown rather than guessed"
                : "REFERENCE_VIEWER_TARGET_VIEW is empty and " + distinct.Count + " parameters resolve to a " +
                  "view, so the target is ambiguous; choosing one silently would be a fabricated relation";
            return null;
        }

        private static ReferenceFact Reference(Document doc, Element marker, string kind, long? ownerViewId,
                                               Dictionary<long, string> viewNames, Dictionary<long, View> allViews,
                                               Dictionary<long, List<Viewport>> viewportsByView,
                                               long? targetViewId, string reason)
        {
            var fact = new ReferenceFact { Id = Rid.Value(marker.Id), Kind = kind, OwnerViewId = ownerViewId };
            fact.UniqueId = Guard(fact, "unique_id", () => marker.UniqueId);
            fact.Category = Guard(fact, "category", () => marker.Category == null ? null : marker.Category.Name);
            if (ownerViewId.HasValue && viewNames.ContainsKey(ownerViewId.Value))
                fact.OwnerViewName = viewNames[ownerViewId.Value];

            if (!targetViewId.HasValue)
            {
                fact.TargetState = "unknown";
                fact.TargetStateReason = reason ?? "no target view could be established from the API";
                return fact;
            }

            fact.TargetViewId = targetViewId;
            if (!allViews.ContainsKey(targetViewId.Value))
            {
                fact.TargetState = "missing";
                fact.TargetStateReason = "the target view id does not resolve to a view in this document";
                return fact;
            }
            fact.TargetState = "resolved";
            fact.TargetViewName = viewNames.ContainsKey(targetViewId.Value) ? viewNames[targetViewId.Value] : null;
            List<Viewport> placements;
            if (viewportsByView.TryGetValue(targetViewId.Value, out placements))
                foreach (Viewport vp in placements.OrderBy(x => Rid.Value(x.Id)))
                {
                    long? sheetId = TryId(() => vp.SheetId);
                    if (sheetId.HasValue && !fact.TargetSheetIds.Contains(sheetId.Value))
                        fact.TargetSheetIds.Add(sheetId.Value);
                }
            fact.TargetPlaced = fact.TargetSheetIds.Count > 0;
            return fact;
        }

        // =====================================================================
        // TAG COVERAGE - only for the categories a requires_tag rule asked about
        // =====================================================================
        private static void TagCoverage(Document doc, PlanimetrySnapshot snap, PlanimetryScope scope)
        {
            // What is tagged, per view, from the tags already read. Host targets only:
            // a linked element is not this model's to tag.
            var taggedByView = new Dictionary<long, HashSet<long>>();
            foreach (AnnotationFact t in snap.Annotations.Where(a => a.Kind == "tag" && a.OwnerViewId.HasValue))
            {
                HashSet<long> set;
                if (!taggedByView.TryGetValue(t.OwnerViewId.Value, out set))
                    taggedByView[t.OwnerViewId.Value] = set = new HashSet<long>();
                foreach (long id in t.TaggedElementIds) set.Add(id);
            }

            foreach (ViewFact v in snap.Views)
            {
                if (v.IsTemplate == true) continue;
                v.TagCoverage = new List<TagCoverageFact>();
                View view = doc.GetElement(Rid.Make(v.Id)) as View;
                if (view == null) continue;

                foreach (string categoryName in scope.TagCoverageCategories)
                {
                    var cov = new TagCoverageFact { Category = categoryName };
                    BuiltInCategory category;
                    if (!TryCategory(doc, categoryName, out category))
                    {
                        cov.Complete = false;
                        cov.IncompleteReason = "'" + categoryName + "' does not name a category in this document; " +
                                               "the portable form is the OST_* token";
                        v.TagCoverage.Add(cov);
                        continue;
                    }

                    HashSet<long> tagged;
                    if (!taggedByView.TryGetValue(v.Id, out tagged)) tagged = new HashSet<long>();

                    try
                    {
                        // MEASURED (Revit 2023, 2026-08-24, twice): the view-scoped
                        // FilteredElementCollector omits elements that ARE in the view -
                        // present, not hidden, inside the active crop, answering a bounding
                        // box in that very view - when the view's graphics have not been
                        // (re)generated since they were created or its crop moved. A tag rule
                        // built on it fabricates "nothing to tag" over a view full of
                        // untagged work. So visibility is decided by SUBSTANCE instead:
                        // the element is in the document's category, is not hidden in the
                        // view, yields a bounding box in it, and - when the crop is active
                        // and was read - that box intersects the crop. The same convention
                        // the annotation rows already use, so the two can never disagree.
                        PlanBox cropBox = v.CropBoxActive == true ? v.CropBox : PlanBox.Unreadable;
                        if (v.CropBoxActive == true && !cropBox.Valid)
                        {
                            cov.Complete = false;
                            cov.IncompleteReason = "the view's crop is active but its geometry could not be read, " +
                                                   "so which elements fall inside it is unknown";
                            v.TagCoverage.Add(cov);
                            continue;
                        }
                        var visible = new List<Element>();
                        foreach (Element e in new FilteredElementCollector(doc)
                                     .OfCategory(category).WhereElementIsNotElementType().ToElements()
                                     .OrderBy(x => Rid.Value(x.Id)))
                        {
                            bool hidden;
                            try { hidden = e.IsHidden(view); } catch { hidden = false; }
                            if (hidden) continue;
                            BoundingBoxXYZ box;
                            try { box = e.get_BoundingBox(view); } catch { box = null; }
                            if (box == null) continue;
                            if (cropBox.Valid)
                            {
                                PlanBox projected = Project(view, Corners(box));
                                if (!projected.Valid) continue;
                                if (PlanimetryGeometry.Disjoint(cropBox, projected,
                                                               PlanimetryGeometry.TouchToleranceFeet)) continue;
                            }
                            visible.Add(e);
                        }
                        cov.VisibleTotal = visible.Count;
                        foreach (Element e in visible)
                        {
                            long id = Rid.Value(e.Id);
                            if (tagged.Contains(id)) { cov.TaggedTotal++; continue; }
                            cov.UntaggedTotal++;
                            if (cov.Untagged.Count >= TagCoverageFact.MaxEnumerated)
                            {
                                cov.Complete = false;
                                cov.IncompleteReason = "more than " + TagCoverageFact.MaxEnumerated +
                                    " untagged elements of this category are visible in this view; the list is a " +
                                    "LOWER BOUND and the remainder is unknown, not tagged";
                                continue;
                            }
                            var row = new UntaggedElement { Id = id, Category = categoryName };
                            try
                            {
                                Element type = doc.GetElement(e.GetTypeId());
                                if (type != null)
                                {
                                    row.TypeName = Try(() => type.Name, null);
                                    var symbol = type as FamilySymbol;
                                    if (symbol != null)
                                        row.FamilyName = Try(() => symbol.Family == null ? null : symbol.Family.Name, null);
                                }
                            }
                            catch { }
                            foreach (string parameterName in scope.TagCoverageExcludeParameters)
                            {
                                string value = ParameterText(e, parameterName);
                                if (value != null) row.ExclusionParameters[parameterName] = value;
                            }
                            cov.Untagged.Add(row);
                        }

                        // Elements visible FROM A LINK. Counted, never mixed into the host's
                        // untagged list, so a rule can say so instead of blaming this model.
                        cov.LinkedVisibleTotal = LinkedVisible(doc, view, category);
                    }
                    catch (Exception ex)
                    {
                        cov.Complete = false;
                        cov.IncompleteReason = "the visible set could not be enumerated: " + ex.Message;
                    }
                    v.TagCoverage.Add(cov);
                }
            }
        }

        private static int LinkedVisible(Document doc, View view, BuiltInCategory category)
        {
            int total = 0;
            try
            {
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc)
                             .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    Document linked;
                    try { linked = link.GetLinkDocument(); } catch { continue; }
                    if (linked == null) continue;
                    try
                    {
                        total += new FilteredElementCollector(linked)
                            .OfCategory(category).WhereElementIsNotElementType().GetElementCount();
                    }
                    catch { }
                }
            }
            catch { }
            return total;
        }

        /// <summary>A category by OST_* token or by its name IN THIS DOCUMENT. The token is
        /// the portable form and is tried first; a localized name only ever resolves against
        /// the document's own category table, never against a hardcoded English list.</summary>
        private static bool TryCategory(Document doc, string name, out BuiltInCategory category)
        {
            category = BuiltInCategory.INVALID;
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
            {
                BuiltInCategory parsed;
                if (Enum.TryParse(name, true, out parsed) && Enum.IsDefined(typeof(BuiltInCategory), parsed))
                { category = parsed; return true; }
                return false;
            }
            try
            {
                foreach (Category c in doc.Settings.Categories)
                {
                    if (!string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    category = (BuiltInCategory)Rid.Value(c.Id);
                    return Enum.IsDefined(typeof(BuiltInCategory), category);
                }
            }
            catch { }
            return false;
        }

        // =====================================================================
        // Unmatched ids: an id the caller named that produced no row
        // =====================================================================
        private static void ResolveUnmatched(PlanimetrySnapshot snap, PlanimetryScope scope)
        {
            var present = new HashSet<long>();
            foreach (SheetFact s in snap.Sheets) present.Add(s.Id);
            foreach (ViewFact v in snap.Views) present.Add(v.Id);
            foreach (PlacementFact p in snap.Placements) present.Add(p.Id);
            foreach (AnnotationFact a in snap.Annotations) present.Add(a.Id);
            foreach (ReferenceFact r in snap.References) present.Add(r.Id);

            foreach (HashSet<long> asked in new[] { scope.SheetIds, scope.ViewIds, scope.ElementIds })
            {
                if (asked == null) continue;
                foreach (long id in asked.OrderBy(x => x))
                    if (!present.Contains(id) && !scope.UnmatchedIds.Contains(id))
                        scope.UnmatchedIds.Add(id);
            }
        }

        // =====================================================================
        // Geometry and guarded reads
        // =====================================================================

        /// <summary>
        /// Does this OverrideGraphicSettings differ from a default-constructed one?
        /// Compared property by property against a fresh instance rather than against
        /// remembered literals, so a Revit that changes a default cannot silently turn
        /// every element into "overridden". Only per-ELEMENT view overrides are judged
        /// here - category and template overrides are different facts and different
        /// fields.
        /// </summary>
        internal static bool OverridesDifferFromDefaults(OverrideGraphicSettings o)
        {
            if (o == null) return false;
            return !string.Equals(OverrideSignature(o), OverrideSignature(new OverrideGraphicSettings()),
                                  StringComparison.Ordinal);
        }

        /// <summary>
        /// A canonical rendering of every fact an OverrideGraphicSettings carries, so
        /// "did this override change" and "is this override the default" are ONE
        /// comparison instead of two lists that can drift. Shared with
        /// horizun_fix_planimetry, whose clear_element_override must prove it cleared
        /// exactly the element override and left the category override alone.
        /// </summary>
        internal static string OverrideSignature(OverrideGraphicSettings o)
        {
            if (o == null) return "(null)";
            var sb = new System.Text.StringBuilder();
            sb.Append("halftone=").Append(o.Halftone ? "1" : "0");
            sb.Append(";transparency=").Append(o.Transparency.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(";detail=").Append(o.DetailLevel.ToString());
            sb.Append(";plw=").Append(o.ProjectionLineWeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(";clw=").Append(o.CutLineWeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(";plp=").Append(Rid.Value(o.ProjectionLinePatternId));
            sb.Append(";clp=").Append(Rid.Value(o.CutLinePatternId));
            sb.Append(";sfp=").Append(Rid.Value(o.SurfaceForegroundPatternId));
            sb.Append(";sbp=").Append(Rid.Value(o.SurfaceBackgroundPatternId));
            sb.Append(";cfp=").Append(Rid.Value(o.CutForegroundPatternId));
            sb.Append(";cbp=").Append(Rid.Value(o.CutBackgroundPatternId));
            sb.Append(";sfv=").Append(o.IsSurfaceForegroundPatternVisible ? "1" : "0");
            sb.Append(";sbv=").Append(o.IsSurfaceBackgroundPatternVisible ? "1" : "0");
            sb.Append(";cfv=").Append(o.IsCutForegroundPatternVisible ? "1" : "0");
            sb.Append(";cbv=").Append(o.IsCutBackgroundPatternVisible ? "1" : "0");
            sb.Append(";plc=").Append(ColorToken(o.ProjectionLineColor));
            sb.Append(";clc=").Append(ColorToken(o.CutLineColor));
            sb.Append(";sfc=").Append(ColorToken(o.SurfaceForegroundPatternColor));
            sb.Append(";sbc=").Append(ColorToken(o.SurfaceBackgroundPatternColor));
            sb.Append(";cfc=").Append(ColorToken(o.CutForegroundPatternColor));
            sb.Append(";cbc=").Append(ColorToken(o.CutBackgroundPatternColor));
            return sb.ToString();
        }

        private static string ColorToken(Color c)
        {
            if (c == null || !c.IsValid) return "-";
            return c.Red + "," + c.Green + "," + c.Blue;
        }

        /// <summary>Model point into the view-plane frame, internal feet: [x, y, out-of-plane].
        /// The same convention horizun_query_detail_2d publishes.</summary>
        private static double[] ViewFrame(View view, XYZ p)
        {
            XYZ d = p.Subtract(view.Origin);
            return new[]
            {
                d.DotProduct(view.RightDirection),
                d.DotProduct(view.UpDirection),
                d.DotProduct(view.ViewDirection)
            };
        }

        /// <summary>
        /// A set of model points as a view-plane rectangle. ALL points are projected: an
        /// axis-aligned model box projected by two corners into a rotated view frame is not
        /// the element's extent, and the resulting rectangle would be wrong in exactly the
        /// cases a rotated section makes interesting.
        /// </summary>
        private static PlanBox Project(View view, IEnumerable<XYZ> points)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool any = false;
            try
            {
                foreach (XYZ p in points)
                {
                    if (p == null) continue;
                    double[] v = ViewFrame(view, p);
                    if (double.IsNaN(v[0]) || double.IsNaN(v[1])) continue;
                    any = true;
                    if (v[0] < minX) minX = v[0];
                    if (v[1] < minY) minY = v[1];
                    if (v[0] > maxX) maxX = v[0];
                    if (v[1] > maxY) maxY = v[1];
                }
            }
            catch { return PlanBox.Unreadable; }
            return any ? PlanBox.FromCorners(minX, minY, maxX, maxY) : PlanBox.Unreadable;
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
        {
            XYZ min = box.Min, max = box.Max;
            Transform t = box.Transform ?? Transform.Identity;
            for (int i = 0; i < 8; i++)
            {
                var raw = new XYZ((i & 1) == 0 ? min.X : max.X,
                                  (i & 2) == 0 ? min.Y : max.Y,
                                  (i & 4) == 0 ? min.Z : max.Z);
                yield return t.OfPoint(raw);
            }
        }

        /// <summary>An element's extent IN SHEET COORDINATES. A ViewSheet's plane is XY, so
        /// the bounding box read in the sheet already is paper geometry.</summary>
        private static PlanBox SheetBox(Element e, ViewSheet sheet)
        {
            try
            {
                BoundingBoxXYZ box = e.get_BoundingBox(sheet);
                if (box == null) return PlanBox.Unreadable;
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (XYZ p in Corners(box))
                {
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }
                return PlanBox.FromCorners(minX, minY, maxX, maxY);
            }
            catch { return PlanBox.Unreadable; }
        }

        private static void Parameters(Element e, List<string> names, Dictionary<string, JToken> into,
                                       PlanimetryRow row)
        {
            foreach (string name in names)
            {
                try
                {
                    string value = ParameterText(e, name);
                    into[name] = value == null ? JValue.CreateNull() : (JToken)value;
                }
                catch (Exception ex) { row.Note("parameter:" + name, ex.Message); }
            }
        }

        private static string ParameterText(Element e, string name)
        {
            try
            {
                Parameter p = e.LookupParameter(name);
                if (p == null || !p.HasValue) return null;
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString();
                    case StorageType.Integer: return p.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case StorageType.Double: return p.AsValueString() ?? p.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case StorageType.ElementId: return p.AsValueString();
                    default: return null;
                }
            }
            catch { return null; }
        }

        private static string ParameterTextByToken(Element e, BuiltInParameter token)
        {
            try
            {
                Parameter p = e.get_Parameter(token);
                if (p == null || !p.HasValue) return null;
                return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            }
            catch { return null; }
        }

        private static string ParameterElementName(Document doc, Element e, BuiltInParameter token)
        {
            try
            {
                Parameter p = e.get_Parameter(token);
                if (p == null || p.StorageType != StorageType.ElementId) return null;
                ElementId id = p.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return null;
                Element target = doc.GetElement(id);
                return target == null ? null : Try(() => target.Name, null);
            }
            catch { return null; }
        }

        private static T Try<T>(Func<T> f, T fallback)
        {
            try { return f(); } catch { return fallback; }
        }

        private static long? TryId(Func<ElementId> f)
        {
            try
            {
                ElementId id = f();
                return id == null ? (long?)null : Rid.Value(id);
            }
            catch { return null; }
        }

        private static string Guard(PlanimetryRow row, string field, Func<string> f)
        {
            try { return f(); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            { row.NoteNotApplicable(field, ex.Message); return null; }
            catch (Exception ex) { row.Note(field, ex.Message); return null; }
        }

        private static bool? GuardBool(PlanimetryRow row, string field, Func<bool> f)
        {
            try { return f(); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            { row.NoteNotApplicable(field, ex.Message); return null; }
            catch (Exception ex) { row.Note(field, ex.Message); return null; }
        }

        private static int? GuardInt(PlanimetryRow row, string field, Func<int> f)
        {
            try { return f(); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            { row.NoteNotApplicable(field, ex.Message); return null; }
            catch (Exception ex) { row.Note(field, ex.Message); return null; }
        }

        private static double? GuardDouble(PlanimetryRow row, string field, Func<double> f)
        {
            try { return f(); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            { row.NoteNotApplicable(field, ex.Message); return null; }
            catch (Exception ex) { row.Note(field, ex.Message); return null; }
        }
    }
}
