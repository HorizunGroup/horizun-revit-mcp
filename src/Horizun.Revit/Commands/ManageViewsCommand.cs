// -----------------------------------------------------------------------------
// Horizun Revit MCP - views and sheets as one dependency-aware atomic batch.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    // PARTIAL, so the graphic-control operations (view filters, overrides, temporary
    // hide/isolate, colour by value) live in ManageViewsGraphics.cs without either half
    // re-implementing the resolution helpers or the verification contract. They are the
    // same command and the same batch; they are two files because one 1,900-line file is
    // not a place where a reviewer finds anything.
    public sealed partial class ManageViewsCommand : ICommand
    {
        public string Name => "horizun_manage_views";
        public string Description => "Create plans, sections, elevations, drafting/3D views and sheets in one verified transaction.";

        /// <summary>
        /// The operations that CHANGE an existing object rather than minting a new one.
        /// The distinction keeps the modify/create counts a person approves honest.
        /// place_view/place_schedule mint a Viewport/instance but their meaning to the
        /// approver is "this sheet changes", so they classify as modifications.
        /// </summary>
        private static readonly HashSet<string> ModifyingOperations = new HashSet<string>(StringComparer.Ordinal)
        {
            "apply_template", "place_view", "place_schedule",
            "convert_placeholder_sheet", "set_phase", "assign_scope_box", "set_view_range",
            "set_crop", "set_annotation_crop", "set_viewport_type", "align_viewports",
            "edit_filter", "order_filters", "set_template_controls", "explain_graphics",
            "sheet_set_list", "sheet_set_update", "sheet_set_delete", "renumber_sheets", "set_sun_study", "set_graphic_display", "set_starting_view", "extend_grids_3d", "set_view_properties"
        };

        /// <summary>
        /// Every request field that points at an EXISTING element. Kept as one list so the
        /// plan builder and the schema cannot quietly disagree about what is a reference.
        /// </summary>
        private static readonly string[] RefIdFields =
        {
            "level_id", "view_family_type_id", "plan_view_id", "source_view_id", "view_id",
            "template_view_id", "title_block_type_id", "sheet_id", "schedule_id",
            "parent_view_id", "area_scheme_id", "phase_id", "scope_box_id", "viewport_id",
            "source_sheet_id", "viewport_type_id", "anchor_viewport_id",
            "cut_level_id", "top_level_id", "bottom_level_id", "view_depth_level_id"
        };

        /// <summary>
        /// Identity and name of a referenced element, guarded: a plan must never fail
        /// while MEASURING. A dangling id reads as "unresolved" - the validator has its
        /// own, louder opinion about those; the plan just has to be stable about them.
        /// </summary>
        /// <summary>
        /// One regeneration for every viewport this batch placed, then each box re-centred on
        /// its requested point (Revit drifts it on creation). Verification re-reads after this.
        /// </summary>
        private static void CenterPlacedViewports(Document doc, List<Applied> applied)
        {
            var placed = applied.Where(x => !x.Centered &&
                string.Equals(x.Operation, "place_view", StringComparison.OrdinalIgnoreCase)).ToList();
            if (placed.Count == 0) return;
            doc.Regenerate();
            foreach (Applied x in placed)
            {
                if (doc.GetElement(x.Id) is Viewport vp)
                    vp.SetBoxCenter(Point(x.Action["point"]) * x.Scale);
                x.Centered = true;
            }
            doc.Regenerate();
        }

        /// <summary>Best-effort progress for horizun_job_status; never fails the batch.</summary>
        private static void ReportProgress(string label, int done, int total)
        {
            try { Job.Ambient?.Write(label, done, total); } catch { }
        }

        private static string SafePlanRef(Document doc, long id)
        {
            try
            {
                if (!Rid.CanRepresent(id)) return "unresolved";
                Element e = doc.GetElement(Rid.Make(id));
                if (e == null) return "unresolved";
                string uid; try { uid = e.UniqueId ?? ""; } catch { uid = "<unreadable>"; }
                string name; try { name = e.Name ?? ""; } catch { name = "<unreadable>"; }
                return uid + "|" + name;
            }
            catch { return "<unreadable>"; }
        }

        private static string Validate(Document doc, JObject a, Dictionary<string, Type> known,
                                       out string unsupportedReason)
        {
            unsupportedReason = null;
            if (a == null) return "action is not an object";
            // Revit's own static validity check runs before any transaction opens, so a
            // scale it will not accept refuses here instead of rolling a batch back.
            if (a["view_scale"] != null && !View.IsValidViewScale(a.Value<int>("view_scale")))
                return "view_scale " + a.Value<int>("view_scale") + " is not a scale Revit accepts (View.IsValidViewScale)";
            if (a["view_scale"] != null && !ScalableOperations.Contains(a.Value<string>("operation") ?? ""))
                return "view_scale applies to operations that create, duplicate or retemplate a GRAPHICAL view (" +
                       string.Join(", ", ScalableOperations) + "); '" + a.Value<string>("operation") +
                       "' produces nothing with a drawing scale, so the argument would be ignored and is refused";
            string op = (a.Value<string>("operation") ?? "").ToLowerInvariant();
            if (a["detail_number"] != null && op != "place_view") return "detail_number applies to place_view only; '" + op + "' would ignore it";
            string key = a.Value<string>("key");
            if (!string.IsNullOrWhiteSpace(key) && known.ContainsKey(key)) return "key '" + key + "' is duplicated";
            // E2 (field report, 2026-10-18): a plan was created from level_id alone and the
            // start/end the caller sent were dropped without a word, so a "cropped" detail
            // plan came out with the whole tower. A plan has no section box: refuse the
            // arguments instead of ignoring them, and name the tool that does crop.
            string ignored = ViewArgumentRules.PlanIgnoredArgument(op, a.Properties().Select(p => p.Name));
            if (ignored != null)
                return op + " does not take '" + ignored + "' (a plan is cut by its level, not by a section box). " +
                       "Create the plan, then crop it with horizun_view_crop (set_crop_box / set_crop_from_elements / " +
                       "set_crop_from_room). Nothing was created.";
            // Field cycle (2026-10-22): a sheet name with ':' passed the rehearsal and
            // the apply rolled the whole group back in Revit's words. Revit's forbidden
            // characters are known before any transaction opens.
            if (NamedOps.Contains(op) && a["name"] != null && a["name"].Type == JTokenType.String &&
                !string.IsNullOrWhiteSpace(a.Value<string>("name")))
            {
                string nameError = PlanimetryFixRules.NameError("name", a.Value<string>("name"));
                if (nameError != null) return nameError + " Nothing was created.";
            }
            try
            {
                switch (op)
                {
                    case "create_floor_plan": Need<Level>(doc, a, "level_id"); OptionalViewFamilyType(doc, a, ViewFamily.FloorPlan); RefuseScaleUnderDefaultTemplate(doc, a, ViewFamily.FloorPlan); break;
                    case "create_ceiling_plan": Need<Level>(doc, a, "level_id"); OptionalViewFamilyType(doc, a, ViewFamily.CeilingPlan); RefuseScaleUnderDefaultTemplate(doc, a, ViewFamily.CeilingPlan); break;
                    case "create_structural_plan": Need<Level>(doc, a, "level_id"); OptionalViewFamilyType(doc, a, ViewFamily.StructuralPlan); RefuseScaleUnderDefaultTemplate(doc, a, ViewFamily.StructuralPlan); break;
                    case "create_3d": Validate3DView(a, true); OptionalViewFamilyType(doc, a, ViewFamily.ThreeDimensional); break;
                    case "create_drafting": OptionalViewFamilyType(doc, a, ViewFamily.Drafting); break;
                    case "create_section": OptionalViewFamilyType(doc, a, ViewFamily.Section); SectionBox(a, 1); ValidateNewDetailView(doc, a); break;
                    case "create_elevation":
                        OptionalViewFamilyType(doc, a, ViewFamily.Elevation); Need<ViewPlan>(doc, a, "plan_view_id"); Point(a["point"]);
                        if (a["depth"] != null && Finite(a.Value<double>("depth"), "depth") <= 0) throw new ArgumentException("depth must be positive");
                        int index = a.Value<int?>("elevation_index") ?? 0;
                        if (index < 0 || index > 3) throw new ArgumentException("elevation_index must be 0..3");
                        int markerScale = a.Value<int?>("marker_scale") ?? 100;
                        if (markerScale < 1 || markerScale > 24000) throw new ArgumentException("marker_scale must be 1..24000");
                        if (a["rotation"] != null)
                        {
                            double rot = Finite(a.Value<double>("rotation"), "rotation");
                            if (rot <= -360 || rot >= 360) throw new ArgumentException("rotation must be between -360 and 360 degrees exclusive");
                        }
                        break;
                    case "duplicate_view":
                        Reference<View>(doc, a, "source_view_id", "source_view_key", known);
                        if (!Enum.TryParse(a.Value<string>("duplicate_option") ?? "Duplicate", true, out ViewDuplicateOption duplicate) ||
                            !Enum.IsDefined(typeof(ViewDuplicateOption), duplicate))
                            throw new ArgumentException("duplicate_option is invalid");
                        if (string.IsNullOrWhiteSpace(a.Value<string>("source_view_key")) &&
                            !Need<View>(doc, a, "source_view_id").CanViewBeDuplicated(duplicate))
                            throw new ArgumentException("source_view_id cannot be duplicated with " + duplicate);
                        break;
                    case "apply_template":
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        if (a.Value<long?>("template_view_id") == -1) break;   // -1 removes the template
                        View template = Need<View>(doc, a, "template_view_id"); if (!template.IsTemplate) throw new ArgumentException("template_view_id is not a view template"); break;
                    case "create_sheet":
                        if (a["title_block_type_id"] != null)
                        {
                            FamilySymbol title = Need<FamilySymbol>(doc, a, "title_block_type_id");
                            if (!InCategory(title, BuiltInCategory.OST_TitleBlocks))
                                throw new ArgumentException("title_block_type_id must identify a title-block FamilySymbol");
                        }
                        // The number stays OPTIONAL here - Revit auto-numbers a sheet
                        // created without one - but a number the caller DID choose gets
                        // the same document-and-batch uniqueness check the placeholder
                        // and duplicate operations run. Run 2 measured the gap: a
                        // duplicate number sailed through this case's validation and
                        // would have died mid-transaction in Revit's own words.
                        if (!string.IsNullOrWhiteSpace(a.Value<string>("number")))
                            RequireUnusedSheetNumber(doc, a, known);
                        break;
                    case "place_view": Reference<ViewSheet>(doc, a, "sheet_id", "sheet_key", known); ReferenceViewportView(doc, a, known); Point(a["point"]); PlaceViewPreflight(doc, a, known); break;
                    case "place_schedule":
                        Reference<ViewSheet>(doc, a, "sheet_id", "sheet_key", known);
                        Reference<ViewSchedule>(doc, a, "schedule_id", "schedule_key", known);
                        Point(a["point"]);
                        if (a["segment_index"] != null) RequireScheduleSegment(doc, a);
                        break;
                    case "create_area_plan":
                        Need<Level>(doc, a, "level_id");
                        AreaSchemeOf(doc, a);
                        break;
                    case "create_callout":
                        Reference<View>(doc, a, "parent_view_id", "parent_view_key", known);
                        Point(a["start"]); Point(a["end"]);
                        if (a["view_family_type_id"] != null) Need<ViewFamilyType>(doc, a, "view_family_type_id");
                        ValidateNewDetailView(doc, a);
                        break;
                    case "create_placeholder_sheet":
                        RequireUnusedSheetNumber(doc, a, known);
                        break;
                    case "convert_placeholder_sheet":
                    {
                        FamilySymbol convTitle = Need<FamilySymbol>(doc, a, "title_block_type_id");
                        if (!InCategory(convTitle, BuiltInCategory.OST_TitleBlocks))
                            throw new ArgumentException("title_block_type_id must identify a title-block FamilySymbol");
                        // A key may name a placeholder created earlier in THIS batch, whose
                        // IsPlaceholder cannot be asked yet; an id must resolve to one now.
                        string convKey = a.Value<string>("sheet_key");
                        if (string.IsNullOrWhiteSpace(convKey))
                        {
                            ViewSheet placeholder = Need<ViewSheet>(doc, a, "sheet_id");
                            if (!placeholder.IsPlaceholder)
                                throw new ArgumentException("sheet_id " + a.Value<long>("sheet_id") + " is not a " +
                                    "placeholder sheet; converting a real sheet is not an operation");
                        }
                        else Reference<ViewSheet>(doc, a, "sheet_id", "sheet_key", known);
                        break;
                    }
                    case "duplicate_sheet":
                    {
                        ViewSheet dupSource = Need<ViewSheet>(doc, a, "source_sheet_id");
                        if (dupSource.IsPlaceholder)
                            throw new ArgumentException("source_sheet_id is a placeholder; duplicate a real sheet");
                        RequireUnusedSheetNumber(doc, a, known);
                        bool withContent = a.Value<bool?>("with_content") ?? false;
                        if (withContent)
                        {
                            var schedules = new FilteredElementCollector(doc, dupSource.Id)
                                .OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                                .Where(ssi => { try { return !ssi.IsTitleblockRevisionSchedule; } catch { return true; } })
                                .Select(ssi => Rid.Value(ssi.Id)).ToList();
                            if (schedules.Count > 0)
                                throw new ArgumentException("source sheet carries " + schedules.Count + " placed " +
                                    "schedule(s) (" + string.Join(", ", schedules) + "). Revit does not permit one " +
                                    "schedule on two sheets, and duplicating the schedules silently would double " +
                                    "the model's schedule census. Duplicate without content and place schedule " +
                                    "duplicates deliberately, or remove the placements first.");
                            foreach (Viewport vp in dupSource.GetAllViewports()
                                     .Select(id => doc.GetElement(id)).OfType<Viewport>())
                            {
                                View source = doc.GetElement(vp.ViewId) as View;
                                if (source == null || !source.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing))
                                    throw new ArgumentException("view '" + (source == null ? vp.ViewId.ToString() : source.Name) +
                                        "' on the source sheet cannot be duplicated WithDetailing, so the sheet " +
                                        "cannot be duplicated with content. Duplicate without content instead.");
                            }
                        }
                        break;
                    }
                    case "set_phase":
                    {
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        Element phase = Need<Element>(doc, a, "phase_id");
                        if (!(phase is Phase)) throw new ArgumentException("phase_id must identify a Phase");
                        break;
                    }
                    case "assign_scope_box":
                    {
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        if (a.Value<long?>("scope_box_id") == -1) break;   // -1 clears the scope box
                        Element box = Need<Element>(doc, a, "scope_box_id");
                        if (!InCategory(box, BuiltInCategory.OST_VolumeOfInterest))
                            throw new ArgumentException("scope_box_id must identify a scope box (Volume of Interest)");
                        break;
                    }
                    case "set_view_range":
                    {
                        string planKey = a.Value<string>("view_key");
                        if (string.IsNullOrWhiteSpace(planKey)) Need<ViewPlan>(doc, a, "view_id");
                        else
                        {
                            // MEASURED live (2026-08-26): duplicate_view declares its
                            // result as View in the key table, and a duplicated plan IS
                            // a ViewPlan at run time. Requiring the declared type to be
                            // ViewPlan refused the one composition this operation exists
                            // for; a View-typed key is accepted here and Apply's cast
                            // rolls the batch back by name if the runtime object is not
                            // actually a plan.
                            if (!known.TryGetValue(planKey, out Type actualKeyType))
                                throw new ArgumentException("view_key references unknown/prior key '" + planKey + "'");
                            if (!typeof(View).IsAssignableFrom(actualKeyType))
                                throw new ArgumentException("view_key references key '" + planKey +
                                    "' whose result is " + actualKeyType.Name + ", not a View");
                        }
                        bool any = false;
                        foreach (string plane in ViewRangePlanes)
                        {
                            bool hasLevel = a[plane + "_level_id"] != null, hasOffset = a[plane + "_offset"] != null;
                            if (hasLevel) { Need<Level>(doc, a, plane + "_level_id"); any = true; }
                            if (hasOffset) { Finite(a.Value<double>(plane + "_offset"), plane + "_offset"); any = true; }
                        }
                        if (!any)
                            throw new ArgumentException("set_view_range changes nothing: pass at least one of " +
                                "cut/top/bottom/view_depth _level_id or _offset. A no-op action in a verified " +
                                "batch can only be a mistake.");
                        break;
                    }
                    case "set_crop":
                    {
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        JArray cropBox = a["box"] as JArray;
                        if (cropBox == null || cropBox.Count != 4 ||
                            cropBox.Any(t => t.Type != JTokenType.Float && t.Type != JTokenType.Integer))
                            throw new ArgumentException("box must be [min_x, min_y, max_x, max_y] in the view's " +
                                "own right/up plane, in the request's units");
                        if (cropBox[2].Value<double>() <= cropBox[0].Value<double>() ||
                            cropBox[3].Value<double>() <= cropBox[1].Value<double>())
                            throw new ArgumentException("box max must exceed box min on both axes");
                        // A crop owned by a template, a scope box or a sketch is refused in
                        // the rehearsal when the view already exists; a view made earlier in
                        // this batch is checked again by Apply.
                        if (string.IsNullOrWhiteSpace(a.Value<string>("view_key")) &&
                            doc.GetElement(Rid.Make(a.Value<long>("view_id"))) is View preflightView)
                        {
                            string cropRefusal = ViewCropRules.Refusal(CropPreflight(doc, preflightView));
                            if (cropRefusal != null) throw new ArgumentException(cropRefusal);
                        }
                        break;
                    }
                    case "set_annotation_crop":
                    {
                        Reference<View>(doc, a, "view_id", "view_key", known);
                        if (a["active"] == null || a["active"].Type != JTokenType.Boolean)
                            throw new ArgumentException("active (boolean) is required");
                        if (a["annotation_offset"] != null)
                        {
                            double off = Finite(a.Value<double>("annotation_offset"), "annotation_offset");
                            if (off < 0) throw new ArgumentException("annotation_offset must be zero or greater");
                        }
                        break;
                    }
                    case "set_viewport_type":
                    {
                        Viewport typed = Need<Viewport>(doc, a, "viewport_id");
                        ElementType wanted = Need<ElementType>(doc, a, "viewport_type_id");
                        if (!typed.GetValidTypes().Contains(wanted.Id))
                            throw new ArgumentException("viewport_type_id " + Rid.Value(wanted.Id) + " is not a " +
                                "valid type for viewport " + Rid.Value(typed.Id) + " - Revit's own GetValidTypes " +
                                "does not offer it");
                        break;
                    }
                    case "align_viewports":
                    {
                        JArray ids = a["viewport_ids"] as JArray;
                        if (ids == null || ids.Count < 1 || ids.Any(t => t.Type != JTokenType.Integer))
                            throw new ArgumentException("viewport_ids must be a non-empty array of viewport ids");
                        var seenVp = new HashSet<long>();
                        foreach (JToken t in ids)
                        {
                            long vid = t.Value<long>();
                            if (!seenVp.Add(vid)) throw new ArgumentException("viewport_ids repeats " + vid);
                            if (!Rid.CanRepresent(vid) || !(doc.GetElement(Rid.Make(vid)) is Viewport))
                                throw new ArgumentException("viewport_ids entry " + vid + " is not a viewport");
                        }
                        string mode = (a.Value<string>("mode") ?? "").ToLowerInvariant();
                        if (Array.IndexOf(AlignModes, mode) < 0)
                            throw new ArgumentException("mode must be one of: " + string.Join(", ", AlignModes));
                        Viewport anchor = Need<Viewport>(doc, a, "anchor_viewport_id");
                        if (seenVp.Contains(Rid.Value(anchor.Id)))
                            throw new ArgumentException("anchor_viewport_id must not appear in viewport_ids: the " +
                                "anchor holds still and everything else moves to it");
                        break;
                    }
                    default:
                        // The graphic-control half of this command: view filters, overrides,
                        // temporary hide/isolate and colour by value. See ManageViewsGraphics.cs.
                        if (IsGraphicsOperation(op)) { ValidateGraphics(doc, a, op, known); break; }
                        // Legends. Two of Revit's operations here cannot be done at all
                        // through its API; those refuse with the exact manual step rather
                        // than with a generic failure. See ManageViewsLegends.cs.
                        if (IsLegendOperation(op)) { ValidateLegend(doc, a, op, known); break; }
                        // Filter editing/order, the precedence report and templates. See ManageViewsControl.cs.
                        if (IsControlOperation(op)) { ValidateControl(doc, a, op, known); break; }
                        // Register-wide renumbering by map. See ManageViewsRenumber.cs.
                        if (IsRenumberOperation(op)) { ValidateRenumber(doc, a, known); break; }
                        // A camera from eye/target/up, or a fan of them. See ManageViewsPerspective.cs.
                        if (IsPerspectiveOperation(op)) { ValidatePerspective(doc, a); break; }
                        // One room from one corner, the section box fitted to it (4.2-N4). See ManageViewsRoomIsometric.cs.
                        if (IsRoomIsometricOperation(op)) { ValidateRoomIsometric(doc, a); OptionalViewFamilyType(doc, a, ViewFamily.ThreeDimensional); break; }
                        // A view's sun: still, single-day or multi-day. See ManageViewsSunStudy.cs.
                        if (IsSunStudyOperation(op)) { ValidateSunStudy(doc, a, known); break; }
                        if (IsGraphicDisplayOperation(op)) { ValidateGraphicDisplay(doc, a, known); break; }
                        // Scale, detail level, discipline, style and phase of existing views. See ManageViewsProperties.cs.
                        if (IsViewPropertiesOperation(op)) { ValidateViewProperties(doc, a, known); break; }
                        // A filter that holds a fixed set of element ids. See ManageViewsSelectionFilter.cs.
                        if (IsSelectionFilterOperation(op)) { ValidateSelectionFilter(doc, a, known); break; }
                        // The document's Starting View. See ManageViewsStartingView.cs.
                        if (IsStartingViewOperation(op)) { ValidateStartingView(doc, a, known); break; }
                        // Existing grids made to span every level. See ManageViewsGridExtent.cs.
                        if (IsGridExtentOperation(op)) { ValidateGridExtent(doc, a, known); break; }
                        // A capability gap, not a fixable argument: this command implements a
                        // fixed set of documentation operations and this is not one of them.
                        unsupportedReason = FallbackSignal.ReasonUnsupportedOperation;
                        return "unsupported operation '" + op + "' - horizun_manage_views implements a fixed " +
                               "set of view, sheet and viewport operations. Nothing was written.";
                }
                return null;
            }
            catch (Exception ex)
            {
                unsupportedReason = UnsupportedCapability.ReasonOf(ex);
                return ex.Message;
            }
        }

        /// <summary>Every operation whose result carries a drawing scale.</summary>
        private static readonly string[] ScalableOperations =
        {
            "create_floor_plan", "create_ceiling_plan", "create_structural_plan", "create_area_plan",
            "create_drafting", "create_3d", "create_callout", "create_section", "create_elevation",
            "duplicate_view", "apply_template", "set_view_properties"
        };

        /// <summary>
        /// Assign a view scale and PROVE it, or throw with the exact reason. A template
        /// that controls View Scale silently wins over the property setter, so it is
        /// detected first; a perspective, sheet or schedule has no scale at all; and
        /// the value is re-read after assignment because "did not throw" is not
        /// "was kept".
        /// </summary>
        internal static void ApplyViewScale(Document doc, View view, int wanted, string op)
        {
            string refusal = ViewScaleRefusal(doc, view, wanted, op);
            if (refusal != null) throw new InvalidOperationException(refusal);
            if (view.Scale != wanted) view.Scale = wanted;
            int actual = view.Scale;
            if (actual != wanted)
                throw new InvalidOperationException(op + ": view_scale " + wanted + " was assigned but the view reads back " + actual + "; Revit did not keep it.");
        }

        /// <summary>Why <paramref name="view"/> cannot take <paramref name="wanted"/> as its scale, or null. Asked before any transaction by the planners that change a scale.</summary>
        internal static string ViewScaleRefusal(Document doc, View view, int wanted, string op)
        {
            if (view is ViewSheet || view is ViewSchedule)
                return op + ": view_scale cannot apply to a " + view.GetType().Name + "; it has no drawing scale.";
            if (view is View3D three && three.IsPerspective)
                return op + ": view_scale cannot apply to a perspective 3D view.";
            if (!View.IsValidViewScale(wanted))
                return op + ": Revit rejects view_scale " + wanted + " (View.IsValidViewScale).";
            ElementId templateId = view.ViewTemplateId;
            if (templateId != null && templateId != ElementId.InvalidElementId && doc.GetElement(templateId) is View template &&
                ControlsViewScale(template))
                return op + ": view_scale " + wanted + " cannot be applied because view template '" +
                    template.Name + "' (" + Rid.Value(templateId) + ") controls View Scale; the assignment would be overridden. " +
                    "Apply a template that leaves scale uncontrolled, or change the scale on the template itself.";
            return null;
        }

        /// <summary>
        /// 5.0.4 (course, Revit 2024): an apply_template whose template controls View Scale overrides a view_scale asked in
        /// the same batch - on this action, or on the action that created the view (view_key) - and the postcondition then
        /// rolled the whole batch back. Asked here, before any transaction, and said with the template's own scale.
        /// </summary>
        private static string TemplateScaleConflict(Document doc, JObject a, Dictionary<string, KeyValuePair<int, int>> scaleByKey)
        {
            if (a == null || !string.Equals(a.Value<string>("operation"), "apply_template", StringComparison.OrdinalIgnoreCase)) return null;
            long? templateId = a.Value<long?>("template_view_id");
            if (templateId == null || templateId == -1 || !(doc.GetElement(Rid.Make(templateId.Value)) is View template) ||
                !template.IsTemplate || !ControlsViewScale(template)) return null;
            int templateScale = template.Scale;
            string fix = " Send view_scale without that template (or with template_view_id=-1), use a template that leaves View Scale " +
                         "uncontrolled, or change the scale on the template itself. Nothing was written.";
            if (a["view_scale"] != null && a.Value<int>("view_scale") != templateScale)
                return "apply_template: view template '" + template.Name + "' (" + template.Id + ") controls View Scale at 1:" + templateScale +
                       ", so view_scale " + a.Value<int>("view_scale") + " cannot be kept." + fix;
            string viewKey = a.Value<string>("view_key");
            if (!string.IsNullOrWhiteSpace(viewKey) && scaleByKey.TryGetValue(viewKey, out var asked) && asked.Value != templateScale)
                return "apply_template: view template '" + template.Name + "' (" + template.Id + ") controls View Scale at 1:" + templateScale +
                       " and would override view_scale " + asked.Value + " asked by action " + asked.Key + " for view_key '" + viewKey + "'." + fix;
            return null;
        }

        private static bool ControlsViewScale(View template)
        {
            var controlled = new HashSet<ElementId>(template.GetTemplateParameterIds());
            foreach (ElementId id in template.GetNonControlledTemplateParameterIds()) controlled.Remove(id);
            return new[] { BuiltInParameter.VIEW_SCALE, BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC, BuiltInParameter.VIEW_SCALE_PULLDOWN_IMPERIAL }
                .Any(bp => controlled.Contains(new ElementId(bp)));
        }

        /// <summary>
        /// A new plan takes its ViewFamilyType's default template, and ApplyViewScale then
        /// refuses a scale that template controls - mid-transaction, after the rehearsal had
        /// accepted it (field MEP cycle, 2026-10-22, three disciplines, Mechanical Plan). The same
        /// question is asked here, before anything opens.
        /// </summary>
        private static void RefuseScaleUnderDefaultTemplate(Document doc, JObject a, ViewFamily family)
        {
            if (a["view_scale"] == null) return;
            ElementId templateId = ResolveVft(doc, a, family).DefaultTemplateId;
            if (templateId == null || templateId == ElementId.InvalidElementId || !(doc.GetElement(templateId) is View template) ||
                !template.IsTemplate || !ControlsViewScale(template)) return;
            throw new ArgumentException("view_scale " + a.Value<int>("view_scale") + " cannot be kept: a new " + family +
                " takes its view type's default template '" + template.Name + "' (" + Rid.Value(templateId) + "), which controls " +
                "View Scale. Create the view without view_scale, then send apply_template with template_view_id=-1 (or a template " +
                "that leaves scale uncontrolled) and the view_scale you want.");
        }

        private static Element ApplyOther(Document doc, JObject a, Dictionary<string, ElementId> aliases, double scale, List<Applied> applied, string op)
        {
            if (IsGraphicsOperation(op)) return ApplyGraphics(doc, a, op, aliases);
            if (IsLegendOperation(op)) return ApplyLegend(doc, a, op, aliases, scale);
            if (IsControlOperation(op)) return ApplyControl(doc, a, op, aliases);
            if (IsRenumberOperation(op)) return ApplyRenumber(doc, a);
            if (IsPerspectiveOperation(op)) return ApplyPerspective(doc, a, scale);
            if (IsRoomIsometricOperation(op)) return ApplyRoomIsometric(doc, a, ResolveVft(doc, a, ViewFamily.ThreeDimensional));
            if (IsSunStudyOperation(op)) return ApplySunStudy(doc, a, aliases);
            if (IsGraphicDisplayOperation(op)) return ApplyGraphicDisplay(doc, a, aliases);
            if (IsViewPropertiesOperation(op)) return ApplyViewProperties(doc, a, aliases);
            if (IsSelectionFilterOperation(op)) return ApplySelectionFilter(doc, a, aliases);
            if (IsStartingViewOperation(op)) return ApplyStartingView(doc, a, aliases);
            if (IsGridExtentOperation(op)) return ApplyGridExtent(doc, a);
            if (op == "create_floor_plan" || op == "create_ceiling_plan" || op == "create_structural_plan")
            {
                ViewFamily family = op == "create_floor_plan" ? ViewFamily.FloorPlan :
                    op == "create_ceiling_plan" ? ViewFamily.CeilingPlan : ViewFamily.StructuralPlan;
                Level level = Need<Level>(doc, a, "level_id"); ViewFamilyType type = ResolveVft(doc, a, family);
                ViewPlan view = ViewPlan.Create(doc, type.Id, level.Id); SetName(view, a); return view;
            }
            if (op == "create_3d") { View3D view = View3D.CreateIsometric(doc, ResolveVft(doc, a, ViewFamily.ThreeDimensional).Id); SetName(view, a); Apply3DView(view, a, scale); return view; }
            if (op == "create_section")
            {
                ViewSection view = ViewSection.CreateSection(doc, ResolveVft(doc, a, ViewFamily.Section).Id, SectionBox(a, scale));
                SetName(view, a); ApplyNewDetailView(doc, view, a); return view;
            }
            if (op == "create_elevation")
            {
                ViewFamilyType type = ResolveVft(doc, a, ViewFamily.Elevation);
                XYZ elevationPoint = Point(a["point"]) * scale;
                ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, type.Id, elevationPoint, a.Value<int?>("marker_scale") ?? 100);
                ViewSection view = marker.CreateElevation(doc, Need<ViewPlan>(doc, a, "plan_view_id").Id,
                    a.Value<int?>("elevation_index") ?? 0);
                // depth used to be accepted and ignored: Revit's default far clip (3048 mm) left the
                // elevation almost empty. It is the view's Far Clip Offset, set here and re-read.
                if (a["depth"] != null)
                {
                    double depthFeet = Finite(a.Value<double>("depth"), "depth") * scale;
                    Parameter far = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                    if (far == null || far.IsReadOnly)
                        throw new InvalidOperationException("the new elevation exposes no editable far clip offset, so depth cannot be applied. Nothing was kept.");
                    far.Set(depthFeet);
                    a["__elevation_depth_feet"] = depthFeet;
                }
                double? rotation = a.Value<double?>("rotation");
                if (rotation != null && Math.Abs(rotation.Value) > 1e-9)
                {
                    // Orienting to a principal wall: the MARKER rotates about the vertical
                    // axis through its own point, and the elevation's view direction turns
                    // with it. The pre-rotation direction is recorded on the action so the
                    // post-commit check can verify the TURN rather than assume which way
                    // Revit points a fresh index.
                    a["__direction_before_rotation"] = new JArray(view.ViewDirection.X, view.ViewDirection.Y,
                                                                  view.ViewDirection.Z);
                    var axis = Line.CreateBound(elevationPoint, elevationPoint.Add(XYZ.BasisZ));
                    ElementTransformUtils.RotateElement(doc, marker.Id, axis,
                                                        rotation.Value * Math.PI / 180.0);
                }
                SetName(view, a); return view;
            }
            if (op == "duplicate_view")
            {
                View source = Resolve<View>(doc, a, "source_view_id", "source_view_key", aliases);
                ViewDuplicateOption option;
                if (!Enum.TryParse(a.Value<string>("duplicate_option") ?? "Duplicate", true, out option) ||
                    !Enum.IsDefined(typeof(ViewDuplicateOption), option)) throw new ArgumentException("invalid duplicate_option");
                a["__duplicate_source_id"] = Rid.Value(source.Id); a["__duplicate_option"] = option.ToString();
                View copy = doc.GetElement(source.Duplicate(option)) as View; SetName(copy, a); return copy;
            }
            if (op == "apply_template")
            {
                View view = Resolve<View>(doc, a, "view_id", "view_key", aliases);
                view.ViewTemplateId = a.Value<long?>("template_view_id") == -1
                    ? ElementId.InvalidElementId : Need<View>(doc, a, "template_view_id").Id;
                return view;
            }
            if (op == "create_area_plan")
            {
                ViewPlan view = ViewPlan.CreateAreaPlan(doc, AreaSchemeOf(doc, a).Id,
                                                        Need<Level>(doc, a, "level_id").Id);
                SetName(view, a); return view;
            }
            if (op == "create_callout")
            {
                View parent = Resolve<View>(doc, a, "parent_view_id", "parent_view_key", aliases);
                ViewFamilyType vft = a["view_family_type_id"] != null
                    ? Need<ViewFamilyType>(doc, a, "view_family_type_id")
                    : ResolveVft(doc, a, ViewFamily.Detail);
                View callout = ViewSection.CreateCallout(doc, parent.Id, vft.Id,
                                                         Point(a["start"]) * scale, Point(a["end"]) * scale);
                SetName(callout, a); ApplyNewDetailView(doc, callout, a); return callout;
            }
            if (op == "create_placeholder_sheet")
            {
                ViewSheet placeholder = ViewSheet.CreatePlaceholder(doc);
                if (!string.IsNullOrWhiteSpace(a.Value<string>("name"))) placeholder.Name = a.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(a.Value<string>("number"))) placeholder.SheetNumber = a.Value<string>("number");
                return placeholder;
            }
            if (op == "convert_placeholder_sheet")
            {
                ViewSheet placeholder = Resolve<ViewSheet>(doc, a, "sheet_id", "sheet_key", aliases);
                if (!placeholder.IsPlaceholder)
                    throw new InvalidOperationException("sheet '" + placeholder.SheetNumber + "' is not a " +
                        "placeholder (a key may have resolved to a real sheet); the batch is rolling back");
                placeholder.ConvertToRealSheet(Need<FamilySymbol>(doc, a, "title_block_type_id").Id);
                return placeholder;
            }
            if (op == "duplicate_sheet")
            {
                ViewSheet source = Need<ViewSheet>(doc, a, "source_sheet_id");
                // The new sheet takes the SOURCE's title block type: a duplicate that
                // silently swapped title blocks would not be a duplicate.
                ElementId titleType = ElementId.InvalidElementId;
                FamilyInstance titleInstance = new FilteredElementCollector(doc, source.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>().FirstOrDefault();
                if (titleInstance != null) titleType = titleInstance.GetTypeId();
                ViewSheet copy = ViewSheet.Create(doc, titleType);
                copy.SheetNumber = a.Value<string>("number");
                copy.Name = string.IsNullOrWhiteSpace(a.Value<string>("name")) ? source.Name : a.Value<string>("name");
                if (a.Value<bool?>("with_content") ?? false)
                {
                    foreach (Viewport vp in source.GetAllViewports().Select(id => doc.GetElement(id)).OfType<Viewport>()
                             .OrderBy(v => Rid.Value(v.Id)))
                    {
                        View sourceView = (View)doc.GetElement(vp.ViewId);
                        var duplicated = (View)doc.GetElement(sourceView.Duplicate(ViewDuplicateOption.WithDetailing));
                        Viewport placed = Viewport.Create(doc, copy.Id, duplicated.Id, vp.GetBoxCenter());
                        try { if (placed.GetTypeId() != vp.GetTypeId()) placed.ChangeTypeId(vp.GetTypeId()); }
                        catch { /* a viewport type invalid for the copy keeps the default; the box center is verified */ }
                    }
                }
                return copy;
            }
            if (op == "set_phase")
            {
                View phaseView = Resolve<View>(doc, a, "view_id", "view_key", aliases);
                Parameter phaseParam = phaseView.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (phaseParam == null || phaseParam.IsReadOnly)
                    throw new InvalidOperationException("view '" + phaseView.Name + "' does not expose a writable " +
                        "Phase parameter; the batch is rolling back");
                phaseParam.Set(Need<Element>(doc, a, "phase_id").Id);
                return phaseView;
            }
            if (op == "assign_scope_box")
            {
                View scopedView = Resolve<View>(doc, a, "view_id", "view_key", aliases);
                Parameter scopeParam = scopedView.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                if (scopeParam == null || scopeParam.IsReadOnly)
                    throw new InvalidOperationException("view '" + scopedView.Name + "' does not accept a scope " +
                        "box; the batch is rolling back");
                scopeParam.Set(a.Value<long?>("scope_box_id") == -1 ? ElementId.InvalidElementId : Need<Element>(doc, a, "scope_box_id").Id);
                return scopedView;
            }
            if (op == "set_view_range")
            {
                ViewPlan plan = Resolve<ViewPlan>(doc, a, "view_id", "view_key", aliases);
                PlanViewRange range = plan.GetViewRange();
                foreach (string planeName in ViewRangePlanes)
                {
                    PlanViewPlane plane = PlaneOf(planeName);
                    long? levelId = a.Value<long?>(planeName + "_level_id");
                    if (levelId != null) range.SetLevelId(plane, Rid.Make(levelId.Value));
                    double? offset = a.Value<double?>(planeName + "_offset");
                    if (offset != null) range.SetOffset(plane, offset.Value * scale);
                }
                plan.SetViewRange(range);
                return plan;
            }
            if (op == "set_crop")
            {
                View cropView = Resolve<View>(doc, a, "view_id", "view_key", aliases);
                string cropRefusal = ViewCropRules.Refusal(CropPreflight(doc, cropView));
                if (cropRefusal != null)
                    throw new InvalidOperationException(cropRefusal + " The batch is rolling back.");
                JArray b = (JArray)a["box"];
                // The request's rectangle lives in the VIEW's right/up plane, anchored at
                // the view origin - the frame a caller can actually reason about. The
                // CropBox has its OWN transform, whose origin and basis Revit chooses,
                // so the corners are carried view-plane -> model -> crop-local rather
                // than written into Min/Max as if the two frames were the same. They
                // often are; "often" is not a property.
                BoundingBoxXYZ crop = cropView.CropBox;
                XYZ localA = CropLocal(cropView, crop, b[0].Value<double>() * scale, b[1].Value<double>() * scale);
                XYZ localB = CropLocal(cropView, crop, b[2].Value<double>() * scale, b[3].Value<double>() * scale);
                crop.Min = new XYZ(Math.Min(localA.X, localB.X), Math.Min(localA.Y, localB.Y), crop.Min.Z);
                crop.Max = new XYZ(Math.Max(localA.X, localB.X), Math.Max(localA.Y, localB.Y), crop.Max.Z);
                cropView.CropBox = crop;
                cropView.CropBoxActive = true;
                return cropView;
            }
            if (op == "set_annotation_crop")
            {
                View annView = Resolve<View>(doc, a, "view_id", "view_key", aliases);
                Parameter annParam = annView.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (annParam == null || annParam.IsReadOnly)
                    throw new InvalidOperationException("view '" + annView.Name + "' does not expose the annotation " +
                        "crop; the batch is rolling back");
                annParam.Set(a.Value<bool>("active") ? 1 : 0);
                double? annOffset = a.Value<double?>("annotation_offset");
                if (annOffset != null)
                {
                    ViewCropRegionShapeManager manager = annView.GetCropRegionShapeManager();
                    double feet = annOffset.Value * scale;
                    manager.BottomAnnotationCropOffset = feet;
                    manager.TopAnnotationCropOffset = feet;
                    manager.LeftAnnotationCropOffset = feet;
                    manager.RightAnnotationCropOffset = feet;
                }
                return annView;
            }
            if (op == "set_viewport_type")
            {
                Viewport typed = Need<Viewport>(doc, a, "viewport_id");
                typed.ChangeTypeId(Need<ElementType>(doc, a, "viewport_type_id").Id);
                return typed;
            }
            if (op == "align_viewports")
            {
                Viewport anchor = Need<Viewport>(doc, a, "anchor_viewport_id");
                string mode = a.Value<string>("mode").ToLowerInvariant();
                Outline anchorOutline = anchor.GetBoxOutline();
                XYZ anchorCenter = anchor.GetBoxCenter();
                foreach (JToken t in (JArray)a["viewport_ids"])
                {
                    var vp = (Viewport)doc.GetElement(Rid.Make(t.Value<long>()));
                    Outline o = vp.GetBoxOutline();
                    XYZ c = vp.GetBoxCenter();
                    double dx = 0, dy = 0;
                    switch (mode)
                    {
                        case "center_x": dx = anchorCenter.X - c.X; break;
                        case "center_y": dy = anchorCenter.Y - c.Y; break;
                        case "center": dx = anchorCenter.X - c.X; dy = anchorCenter.Y - c.Y; break;
                        case "left": dx = anchorOutline.MinimumPoint.X - o.MinimumPoint.X; break;
                        case "right": dx = anchorOutline.MaximumPoint.X - o.MaximumPoint.X; break;
                        case "top": dy = anchorOutline.MaximumPoint.Y - o.MaximumPoint.Y; break;
                        case "bottom": dy = anchorOutline.MinimumPoint.Y - o.MinimumPoint.Y; break;
                    }
                    vp.SetBoxCenter(new XYZ(c.X + dx, c.Y + dy, c.Z));
                }
                return anchor;
            }
            ViewSheet targetSheet = Resolve<ViewSheet>(doc, a, "sheet_id", "sheet_key", aliases);
            XYZ point = Point(a["point"]) * scale;
            if (op == "place_view")
            {
                // E8: the regeneration that lets SetBoxCenter correct Revit's placement drift
                // used to run once PER viewport (27 viewports, ~9 min on a tower). It now runs
                // once for the whole batch, in CenterPlacedViewports, before verification.
                Viewport created = Viewport.Create(doc, targetSheet.Id, Resolve<View>(doc, a, "view_id", "view_key", aliases).Id, point);
                // 5.2 (P2): the number is set right after the create, in the same transaction, and re-read in Verify.
                if (created != null && a["detail_number"] != null && a["detail_number"].Type == JTokenType.String)
                {
                    string wanted = a.Value<string>("detail_number").Trim();
                    // The preflight found the number free on the sheet; a viewport holding it now was placed earlier in
                    // THIS batch and auto-numbered by Revit. It moves to a free number so the asked number can be set.
                    var others = targetSheet.GetAllViewports().Where(id => id != created.Id).Select(id => doc.GetElement(id) as Viewport).Where(v => v != null).ToList();
                    KeyValuePair<long, string>? holder = SheetPaperRules.Holder(wanted, others.Select(v => new KeyValuePair<long, string>(Rid.Value(v.Id), DetailNumberOf(v))));
                    if (holder.HasValue)
                    {
                        Applied earlier = applied?.FirstOrDefault(x => x.Id != null && Rid.Value(x.Id) == holder.Value.Key);
                        if (earlier == null)
                            throw new InvalidOperationException("viewport " + holder.Value.Key + " holds detail number '" + wanted + "' but was not placed by this batch; it is not renumbered");
                        Parameter moved = (doc.GetElement(Rid.Make(holder.Value.Key)) as Viewport)?.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
                        string free = SheetPaperRules.FreeDetailNumber(others.Select(DetailNumberOf).Concat(new[] { wanted, DetailNumberOf(created) }));
                        if (moved == null || moved.IsReadOnly || !moved.Set(free))
                            throw new InvalidOperationException("viewport " + holder.Value.Key + ", placed earlier in this batch, took detail number '" + wanted + "' and could not be moved to a free one");
                        earlier.Renumbered = PdfQaPaper.DetailNumberMoved(holder.Value.Key, holder.Value.Value, free, wanted);
                    }
                    Parameter number = created.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
                    if (number == null || number.IsReadOnly) throw new InvalidOperationException("the new viewport's Detail Number cannot be written");
                    if (!number.Set(wanted)) throw new InvalidOperationException("Revit refused Detail Number '" + wanted + "' on the new viewport");
                }
                return created;
            }
            if (op == "place_schedule")
            {
                ElementId scheduleId = Resolve<ViewSchedule>(doc, a, "schedule_id", "schedule_key", aliases).Id;
                // A split schedule is placed one SEGMENT at a time: the segment_index names which one.
                return a["segment_index"] != null
                    ? ScheduleSheetInstance.Create(doc, targetSheet.Id, scheduleId, point, a.Value<int>("segment_index"))
                    : ScheduleSheetInstance.Create(doc, targetSheet.Id, scheduleId, point);
            }
            throw new InvalidOperationException("unsupported operation");
        }

        /// <summary>
        /// A point given in the view's right/up plane (anchored at the view origin),
        /// expressed in the crop box's own local frame.
        /// </summary>
        private static XYZ CropLocal(View view, BoundingBoxXYZ crop, double viewX, double viewY)
        {
            XYZ model = view.Origin.Add(view.RightDirection.Multiply(viewX)).Add(view.UpDirection.Multiply(viewY));
            return crop.Transform.Inverse.OfPoint(model);
        }

        /// <summary>
        /// Who owns this view's crop, read before a write. Each read is guarded: a fact
        /// that does not read is left at its "nothing in the way" value only where the
        /// write itself would then fail loudly (template/scope box), never silently.
        /// </summary>
        internal static CropPreflightFacts CropPreflight(Document doc, View view)
        {
            var f = new CropPreflightFacts { ViewId = Rid.Value(view.Id) };
            try { f.ViewName = view.Name; } catch { }
            f.IsTemplate = view.IsTemplate;
            if (f.IsTemplate) return f;
            try
            {
                if (view.ViewTemplateId != null && view.ViewTemplateId != ElementId.InvalidElementId &&
                    doc.GetElement(view.ViewTemplateId) is View template)
                {
                    f.TemplateId = Rid.Value(template.Id);
                    f.TemplateName = template.Name;
                    var cropParams = new[] { BuiltInParameter.VIEWER_CROP_REGION, BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP };
                    var controllable = new HashSet<long>(template.GetTemplateParameterIds().Select(id => Rid.Value(id)));
                    var notControlled = new HashSet<long>(template.GetNonControlledTemplateParameterIds().Select(id => Rid.Value(id)));
                    f.TemplateControlsCrop = cropParams.Any(p => controllable.Contains((long)p) && !notControlled.Contains((long)p));
                }
            }
            catch { }
            try
            {
                ElementId scope = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP)?.AsElementId();
                if (scope != null && scope != ElementId.InvalidElementId)
                {
                    f.ScopeBoxId = Rid.Value(scope);
                    try { f.ScopeBoxName = doc.GetElement(scope)?.Name; } catch { }
                }
            }
            catch { }
            try { f.NonRectangularShape = view.GetCropRegionShapeManager()?.ShapeSet; } catch { }
            return f;
        }

        /// <summary>The request's rectangle, view-plane feet, as [minX, minY, maxX, maxY].</summary>
        private static double[] RequestedCrop(Applied a)
        {
            JArray b = (JArray)a.Action["box"];
            double x0 = b[0].Value<double>() * a.Scale, y0 = b[1].Value<double>() * a.Scale;
            double x1 = b[2].Value<double>() * a.Scale, y1 = b[3].Value<double>() * a.Scale;
            return new[] { Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1) };
        }

        /// <summary>
        /// The crop region Revit DRAWS, projected on the view's right/up plane from its
        /// origin - the same frame the request speaks - as [minX, minY, maxX, maxY] in
        /// feet. Null when there is no shape or it does not read.
        /// </summary>
        private static double[] CropShapeRect(View view)
        {
            try
            {
                ViewCropRegionShapeManager manager = view.GetCropRegionShapeManager();
                if (manager == null) return null;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                int n = 0;
                foreach (CurveLoop loop in manager.GetCropShape())
                    foreach (Curve curve in loop)
                        foreach (XYZ p in curve.Tessellate())
                        {
                            XYZ d = p.Subtract(view.Origin);
                            double x = d.DotProduct(view.RightDirection), y = d.DotProduct(view.UpDirection);
                            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                            n++;
                        }
                return n == 0 ? null : new[] { minX, minY, maxX, maxY };
            }
            catch { return null; }
        }

        // The mm tolerance rather than 1e-6 ft: Revit is free to snap a crop to its own
        // internal grid, and the caller's claim is "this rectangle", not "these doubles".
        private const double CropToleranceFeet = 1.0 / 304.8;

        /// <summary>Every fact the set_crop verdict rests on, read off the view as it is now.</summary>
        private static CropVerdict ReadCrop(View view, Applied a)
        {
            var r = new CropReadback();
            try { r.CropBoxActive = view.CropBoxActive; } catch { }
            try
            {
                Parameter p = view.get_Parameter(BuiltInParameter.VIEWER_CROP_REGION);
                if (p != null && p.HasValue) r.CropParameter = p.AsInteger();
            }
            catch { }
            double[] want = RequestedCrop(a);
            try
            {
                BoundingBoxXYZ crop = view.CropBox;
                if (crop != null)
                {
                    XYZ localA = CropLocal(view, crop, want[0], want[1]);
                    XYZ localB = CropLocal(view, crop, want[2], want[3]);
                    r.BoxMatches = ViewCropRules.RectangleMatches(
                        new[] { Math.Min(localA.X, localB.X), Math.Min(localA.Y, localB.Y), Math.Max(localA.X, localB.X), Math.Max(localA.Y, localB.Y) },
                        new[] { crop.Min.X, crop.Min.Y, crop.Max.X, crop.Max.Y }, CropToleranceFeet);
                }
            }
            catch { }
            double[] shape = CropShapeRect(view);
            if (shape != null) r.ShapeMatches = ViewCropRules.RectangleMatches(want, shape, CropToleranceFeet);
            return ViewCropRules.Evaluate(r);
        }

        /// <summary>
        /// What a set_crop left on the model and on paper: the verdict's facts, where the
        /// crop sits in MODEL coordinates (so a caller can see which part of the model it
        /// frames), and every viewport that shows the view, sized against the crop.
        /// </summary>
        private static JObject CropDetail(Document doc, Applied a, View view)
        {
            CropVerdict verdict = ReadCrop(view, a);
            var o = new JObject
            {
                ["verified"] = verdict.Verified,
                ["failures"] = new JArray(verdict.Failures),
                ["notes"] = new JArray(verdict.Notes)
            };
            try { o["crop_box_active"] = view.CropBoxActive; } catch { o["crop_box_active"] = JValue.CreateNull(); }
            try { o["crop_box_visible"] = view.CropBoxVisible; } catch { o["crop_box_visible"] = JValue.CreateNull(); }
            double[] shape = CropShapeRect(view);
            if (shape != null)
            {
                o["shape_view_plane_internal_feet"] = new JArray(shape);
                try
                {
                    var corners = new JArray();
                    foreach (double[] c in new[] { new[] { shape[0], shape[1] }, new[] { shape[2], shape[3] } })
                    {
                        XYZ m = view.Origin.Add(view.RightDirection.Multiply(c[0])).Add(view.UpDirection.Multiply(c[1]));
                        corners.Add(new JArray(m.X, m.Y, m.Z));
                    }
                    o["shape_model_corners_internal_feet"] = corners;
                }
                catch { }
            }
            int scale = 0;
            try { scale = view.Scale; } catch { }
            bool? annotationCrop = null;
            try
            {
                Parameter ann = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (ann != null && ann.HasValue) annotationCrop = ann.AsInteger() == 1;
            }
            catch { }
            var viewports = new JArray();
            try
            {
                foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                             .Where(v => v.ViewId == view.Id))
                {
                    var row = new JObject { ["viewport_id"] = Rid.Value(vp.Id), ["sheet_id"] = Rid.Value(vp.SheetId) };
                    try
                    {
                        Outline outline = vp.GetBoxOutline();
                        double w = (outline.MaximumPoint.X - outline.MinimumPoint.X) * 304.8;
                        double h = (outline.MaximumPoint.Y - outline.MinimumPoint.Y) * 304.8;
                        row["viewport_mm"] = new JArray(Math.Round(w, 1), Math.Round(h, 1));
                        if (shape != null && scale > 0)
                        {
                            double cw = (shape[2] - shape[0]) * 304.8 / scale, ch = (shape[3] - shape[1]) * 304.8 / scale;
                            row["crop_at_scale_mm"] = new JArray(Math.Round(cw, 1), Math.Round(ch, 1));
                            string finding = ViewCropRules.ViewportFinding(cw, ch, w, h, annotationCrop, 2.0);
                            if (finding != null) row["finding"] = finding;
                        }
                    }
                    catch (Exception ex) { row["unreadable"] = ex.Message; }
                    viewports.Add(row);
                }
            }
            catch { }
            o["annotation_crop_active"] = annotationCrop.HasValue ? (JToken)annotationCrop.Value : JValue.CreateNull();
            o["viewports"] = viewports;
            return o;
        }

        /// <summary>The four planes set_view_range can move, in the request's field-prefix spelling.</summary>
        private static readonly string[] ViewRangePlanes = { "cut", "top", "bottom", "view_depth" };

        private static PlanViewPlane PlaneOf(string prefix)
        {
            switch (prefix)
            {
                case "cut": return PlanViewPlane.CutPlane;
                case "top": return PlanViewPlane.TopClipPlane;
                case "bottom": return PlanViewPlane.BottomClipPlane;
                default: return PlanViewPlane.ViewDepthPlane;
            }
        }

        /// <summary>The alignment vocabulary, closed. The anchor never moves.</summary>
        private static readonly string[] AlignModes =
            { "center", "center_x", "center_y", "left", "right", "top", "bottom" };

        /// <summary>
        /// place_schedule with segment_index: the index must be a non-negative integer and, when the
        /// schedule is named by id (not by a key created in this batch), name a segment it really has.
        /// </summary>
        private static void RequireScheduleSegment(Document doc, JObject a)
        {
            JToken token = a["segment_index"];
            if (token.Type != JTokenType.Integer || token.Value<int>() < 0)
                throw new ArgumentException("segment_index must be a whole number from 0 (the first segment)");
            long id = a.Value<long?>("schedule_id") ?? -1;
            if (!Rid.CanRepresent(id)) return;
            ViewSchedule schedule = doc.GetElement(Rid.Make(id)) as ViewSchedule;
            if (schedule == null) return;
            int count = schedule.GetSegmentCount();
            if (!schedule.IsSplit())
                throw new ArgumentException("segment_index names a segment of a split schedule, and schedule " + id +
                    " is not split (horizun_manage_schedules operation=split splits it)");
            if (token.Value<int>() >= count)
                throw new ArgumentException("segment_index " + token.Value<int>() + " is out of range: schedule " + id +
                    " has " + count + " segments (0.." + (count - 1) + ")");
        }

        /// <summary>
        /// A sheet number must be unique across the DOCUMENT and across the BATCH.
        /// Revit would refuse the second copy anyway - but mid-transaction, taking the
        /// whole batch down with an exception whose wording nobody chose. Checked here
        /// so the refusal happens before anything runs and names the collision.
        /// </summary>
        private static string DetailNumberOf(Viewport vp)
        {
            try { return vp.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString(); } catch { return null; }
        }

        /// <summary>
        /// 5.2 (P2, field report): before Viewport.Create. A view Revit will not add to the sheet
        /// (Viewport.CanAddViewToSheet) and a detail_number already used on the sheet - or twice in the
        /// batch - refuse here, by name, instead of a viewport that silently ends up with another number.
        /// </summary>
        private static void PlaceViewPreflight(Document doc, JObject a, Dictionary<string, Type> known)
        {
            string numberError = SheetPaperRules.ValidateDetailNumber(a["detail_number"]);
            if (numberError != null) throw new ArgumentException(numberError);
            ViewSheet sheet = string.IsNullOrWhiteSpace(a.Value<string>("sheet_key")) ? Need<ViewSheet>(doc, a, "sheet_id") : null;
            View view = string.IsNullOrWhiteSpace(a.Value<string>("view_key")) ? Need<View>(doc, a, "view_id") : null;
            if (sheet != null && view != null && !Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                throw new ArgumentException("view " + Rid.Value(view.Id) + " ('" + view.Name + "') cannot be placed on sheet " + sheet.SheetNumber +
                    " (Viewport.CanAddViewToSheet is false: it is already on a sheet, or it is not a placeable view). Nothing was written.");
            if (a["detail_number"] == null) return;
            string wanted = a.Value<string>("detail_number").Trim();
            if (sheet != null)
            {
                var numbers = sheet.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).Where(v => v != null)
                    .Select(v => new KeyValuePair<long, string>(Rid.Value(v.Id), DetailNumberOf(v)));
                KeyValuePair<long, string>? holder = SheetPaperRules.Holder(wanted, numbers);
                if (holder.HasValue)
                {
                    string holderView = null;
                    try { holderView = (doc.GetElement((doc.GetElement(Rid.Make(holder.Value.Key)) as Viewport)?.ViewId ?? ElementId.InvalidElementId) as View)?.Name; } catch { }
                    throw new ArgumentException(SheetPaperRules.DetailNumberRefusal(wanted, sheet.SheetNumber, holder.Value.Key, holderView));
                }
            }
            string sheetRef = sheet != null ? Rid.Value(sheet.Id).ToString(System.Globalization.CultureInfo.InvariantCulture) : "key:" + a.Value<string>("sheet_key");
            string reserved = "detail-number:" + sheetRef + ":" + wanted.ToUpperInvariant();
            if (known.ContainsKey(reserved)) throw new ArgumentException("detail_number '" + wanted + "' appears twice for the same sheet in this batch. Nothing was written.");
            known.Add(reserved, typeof(Viewport));
        }

        private static void RequireUnusedSheetNumber(Document doc, JObject a, Dictionary<string, Type> known)
        {
            string number = a.Value<string>("number");
            if (string.IsNullOrWhiteSpace(number))
                throw new ArgumentException("number is required: a sheet without a number cannot enter a drawing " +
                                            "register, and Revit would invent one nobody chose.");
            foreach (ViewSheet existing in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                string existingNumber;
                try { existingNumber = existing.SheetNumber; } catch { continue; }
                if (string.Equals(existingNumber, number, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("sheet number '" + number + "' is already used by sheet " +
                        Rid.Value(existing.Id) + "; sheet numbers are unique in a Revit document.");
            }
            // The batch's own numbers: recorded in `known` under a reserved prefix so
            // two creates in one batch cannot collide either.
            string reserved = "sheet-number:" + number.ToLowerInvariant();
            if (known.ContainsKey(reserved))
                throw new ArgumentException("sheet number '" + number + "' appears twice in this batch.");
            known.Add(reserved, typeof(ViewSheet));
        }

        /// <summary>
        /// Operations whose 'name' field this command actually WRITES on the created
        /// element (SetName on a View, .Name on a ViewSheet). Any other action's
        /// 'name' - if it even carries one - is not this command's promise to keep.
        /// </summary>
        private static readonly HashSet<string> NamedOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "create_floor_plan", "create_ceiling_plan", "create_structural_plan", "create_3d", "create_drafting",
            "create_section", "create_elevation", "duplicate_view", "create_sheet", "create_area_plan",
            "create_callout", "create_placeholder_sheet", "duplicate_sheet", "create_room_isometric",
        };

        /// <summary>
        /// The duplicate is checked against its source (review 2026-11-20, M8), not only for being a View:
        /// same view type, same view family type, same level, and a dependent's primary view is the source.
        /// </summary>
        private static bool DuplicateHolds(Document doc, View copy, JObject action)
        {
            long? sourceId = action?.Value<long?>("__duplicate_source_id");
            if (sourceId == null) return false;
            if (!(doc.GetElement(Rid.Make(sourceId.Value)) is View source)) return false;
            try
            {
                if (copy.Id == source.Id || copy.ViewType != source.ViewType || copy.GetTypeId() != source.GetTypeId()) return false;
                if ((copy.GenLevel?.Id ?? ElementId.InvalidElementId) != (source.GenLevel?.Id ?? ElementId.InvalidElementId)) return false;
                if (string.Equals(action.Value<string>("__duplicate_option"), nameof(ViewDuplicateOption.AsDependent), StringComparison.Ordinal) &&
                    copy.GetPrimaryViewId() != source.Id) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool VerifyOther(Document doc, Applied a, Element e, string op)
        {
            switch (op)
            {
                case "create_floor_plan": return e is ViewPlan floor && floor.ViewType == ViewType.FloorPlan;
                case "create_ceiling_plan": return e is ViewPlan ceiling && ceiling.ViewType == ViewType.CeilingPlan;
                case "create_structural_plan": return e is ViewPlan structural && structural.ViewType == ViewType.EngineeringPlan;
                case "create_3d": return e is View3D threeD && threeD.ViewType == ViewType.ThreeD && Verify3DView(doc, a.Action, e);
                case "create_section": return e is ViewSection section && section.ViewType == ViewType.Section && NewDetailViewHolds(doc, section, a.Action);
                case "create_elevation":
                {
                    if (!(e is ViewSection elevation) || elevation.ViewType != ViewType.Elevation) return false;
                    if (a.Action["__elevation_depth_feet"] != null)
                    {
                        Parameter farNow = elevation.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR);
                        if (farNow == null || Math.Abs(farNow.AsDouble() - a.Action.Value<double>("__elevation_depth_feet")) > 1e-6) return false;
                    }
                    JArray before = a.Action["__direction_before_rotation"] as JArray;
                    double? rotation = a.Action.Value<double?>("rotation");
                    if (before == null || rotation == null) return true;
                    double angle = rotation.Value * Math.PI / 180.0;
                    double cos = Math.Cos(angle), sin = Math.Sin(angle);
                    double bx = before[0].Value<double>(), by = before[1].Value<double>();
                    var want = new XYZ(bx * cos - by * sin, bx * sin + by * cos, before[2].Value<double>());
                    XYZ got;
                    try { got = elevation.ViewDirection; } catch { return false; }
                    return got.DistanceTo(want) <= 1e-6;
                }
                case "duplicate_view": return e is View duplicated && DuplicateHolds(doc, duplicated, a.Action);
                case "apply_template": return e is View && a.TargetId != null && ((View)e).ViewTemplateId == a.TargetId;
                case "place_view": return e is Viewport viewport && viewport.SheetId == a.SheetId &&
                    viewport.ViewId == a.ViewId && SheetDistance(viewport.GetBoxCenter(), Point(a.Action["point"]) * a.Scale) < 1e-6 &&
                    (a.Action["detail_number"] == null || string.Equals(DetailNumberOf(viewport), a.Action.Value<string>("detail_number").Trim(), StringComparison.Ordinal));
                case "place_schedule":
                    return e is ScheduleSheetInstance schedule && schedule.OwnerViewId == a.SheetId &&
                           (a.Action["segment_index"] == null || schedule.SegmentIndex == a.Action.Value<int>("segment_index"));
                case "create_area_plan": return e is ViewPlan area && area.ViewType == ViewType.AreaPlan;
                case "create_callout":
                    // A callout of a plan is a Detail view; of a section, a Section or
                    // Detail. What is being verified is that a real graphical view came
                    // back, not which of Revit's spellings it wears.
                    return e is View calloutView && !(e is ViewSheet) && !(e is ViewSchedule) &&
                           !calloutView.IsTemplate && NewDetailViewHolds(doc, calloutView, a.Action);
                case "create_placeholder_sheet":
                {
                    if (!(e is ViewSheet ph) || !NumberMatches(ph, a.Action)) return false;
                    if (ph.IsPlaceholder) return true;
                    // MEASURED live (2026-08-26): a convert_placeholder_sheet LATER in
                    // the same batch flips IsPlaceholder before this verifier runs, and
                    // the first version of this check took the whole committed batch
                    // down for it. A conversion this batch itself asked for is the
                    // promised state, not a drift - so it is accepted, and ONLY it.
                    string myKey = a.Action.Value<string>("key");
                    if (a.Batch == null) return false;
                    for (int later = a.Index + 1; later < a.Batch.Count; later++)
                    {
                        var other = a.Batch[later] as JObject;
                        if (other == null) continue;
                        if (!string.Equals(other.Value<string>("operation"), "convert_placeholder_sheet",
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrWhiteSpace(myKey) &&
                            string.Equals(other.Value<string>("sheet_key"), myKey, StringComparison.Ordinal))
                            return true;
                        long? sheetId = other.Value<long?>("sheet_id");
                        if (sheetId != null && Rid.Value(e.Id) == sheetId.Value) return true;
                    }
                    return false;
                }
                case "convert_placeholder_sheet":
                    if (!(e is ViewSheet converted) || converted.IsPlaceholder) return false;
                    // The conversion's whole promise is the title block: re-read it.
                    return new FilteredElementCollector(doc, converted.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .Any(t => Rid.Value(t.GetTypeId()) == (a.Action.Value<long?>("title_block_type_id") ?? -1));
                case "duplicate_sheet":
                {
                    if (!(e is ViewSheet copy) || copy.IsPlaceholder) return false;
                    if (!NumberMatches(copy, a.Action)) return false;
                    if (!(a.Action.Value<bool?>("with_content") ?? false)) return true;
                    ViewSheet dupSource = doc.GetElement(Rid.Make(a.Action.Value<long>("source_sheet_id"))) as ViewSheet;
                    if (dupSource == null) return false;
                    var sourcePorts = dupSource.GetAllViewports().Select(id => doc.GetElement(id))
                        .OfType<Viewport>().OrderBy(v => Rid.Value(v.Id)).ToList();
                    var copyPorts = copy.GetAllViewports().Select(id => doc.GetElement(id))
                        .OfType<Viewport>().ToList();
                    if (copyPorts.Count != sourcePorts.Count) return false;
                    // Every source center must be matched by a copy center within the
                    // paper tolerance - the promise was "the same arrangement".
                    const double tolerance = 1e-6;
                    foreach (Viewport sp in sourcePorts)
                    {
                        XYZ want = sp.GetBoxCenter();
                        if (!copyPorts.Any(cp =>
                        {
                            XYZ got = cp.GetBoxCenter();
                            return Math.Abs(got.X - want.X) <= tolerance && Math.Abs(got.Y - want.Y) <= tolerance;
                        })) return false;
                    }
                    return true;
                }
                case "set_phase":
                {
                    Parameter phase = (e as View)?.get_Parameter(BuiltInParameter.VIEW_PHASE);
                    return phase != null && Rid.Value(phase.AsElementId()) == (a.Action.Value<long?>("phase_id") ?? -1);
                }
                case "assign_scope_box":
                {
                    Parameter scope = (e as View)?.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    long wantBox = a.Action.Value<long?>("scope_box_id") ?? -1;
                    return scope != null && (wantBox == -1 ? scope.AsElementId() == ElementId.InvalidElementId
                                                           : Rid.Value(scope.AsElementId()) == wantBox);
                }
                case "set_view_range":
                {
                    if (!(e is ViewPlan plan)) return false;
                    PlanViewRange range;
                    try { range = plan.GetViewRange(); } catch { return false; }
                    foreach (string planeName in ViewRangePlanes)
                    {
                        PlanViewPlane plane = PlaneOf(planeName);
                        long? levelId = a.Action.Value<long?>(planeName + "_level_id");
                        if (levelId != null && Rid.Value(range.GetLevelId(plane)) != levelId.Value) return false;
                        double? offset = a.Action.Value<double?>(planeName + "_offset");
                        if (offset != null && Math.Abs(range.GetOffset(plane) - offset.Value * a.Scale) > 1e-6)
                            return false;
                    }
                    return true;
                }
                case "set_crop":
                    // Active flag, "Crop View" parameter, CropBox AND the drawn crop
                    // shape: a box stored on a crop that does not govern the view is not
                    // a cropped view (ViewCropRules).
                    return e is View cropped && ReadCrop(cropped, a).Verified;
                case "set_annotation_crop":
                {
                    Parameter ann = (e as View)?.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                    if (ann == null || (ann.AsInteger() == 1) != a.Action.Value<bool>("active")) return false;
                    double? offset = a.Action.Value<double?>("annotation_offset");
                    if (offset == null) return true;
                    try
                    {
                        ViewCropRegionShapeManager manager = ((View)e).GetCropRegionShapeManager();
                        double want = offset.Value * a.Scale;
                        return Math.Abs(manager.BottomAnnotationCropOffset - want) <= 1e-6 &&
                               Math.Abs(manager.TopAnnotationCropOffset - want) <= 1e-6 &&
                               Math.Abs(manager.LeftAnnotationCropOffset - want) <= 1e-6 &&
                               Math.Abs(manager.RightAnnotationCropOffset - want) <= 1e-6;
                    }
                    catch { return false; }
                }
                case "set_viewport_type":
                    return e is Viewport typed &&
                           Rid.Value(typed.GetTypeId()) == (a.Action.Value<long?>("viewport_type_id") ?? -1);
                case "align_viewports":
                {
                    if (!(e is Viewport anchor)) return false;
                    Outline anchorOutline; XYZ anchorCenter;
                    try { anchorOutline = anchor.GetBoxOutline(); anchorCenter = anchor.GetBoxCenter(); }
                    catch { return false; }
                    string mode = a.Action.Value<string>("mode").ToLowerInvariant();
                    const double tolerance = 1e-6;
                    foreach (JToken t in (JArray)a.Action["viewport_ids"])
                    {
                        if (!(doc.GetElement(Rid.Make(t.Value<long>())) is Viewport vp)) return false;
                        Outline o; XYZ c;
                        try { o = vp.GetBoxOutline(); c = vp.GetBoxCenter(); } catch { return false; }
                        bool ok;
                        switch (mode)
                        {
                            case "center_x": ok = Math.Abs(c.X - anchorCenter.X) <= tolerance; break;
                            case "center_y": ok = Math.Abs(c.Y - anchorCenter.Y) <= tolerance; break;
                            case "center": ok = Math.Abs(c.X - anchorCenter.X) <= tolerance &&
                                                Math.Abs(c.Y - anchorCenter.Y) <= tolerance; break;
                            case "left": ok = Math.Abs(o.MinimumPoint.X - anchorOutline.MinimumPoint.X) <= tolerance; break;
                            case "right": ok = Math.Abs(o.MaximumPoint.X - anchorOutline.MaximumPoint.X) <= tolerance; break;
                            case "top": ok = Math.Abs(o.MaximumPoint.Y - anchorOutline.MaximumPoint.Y) <= tolerance; break;
                            case "bottom": ok = Math.Abs(o.MinimumPoint.Y - anchorOutline.MinimumPoint.Y) <= tolerance; break;
                            default: ok = false; break;
                        }
                        if (!ok) return false;
                    }
                    return true;
                }
                default:
                {
                    string graphicsOp = a.Operation.ToLowerInvariant();
                    if (IsGraphicsOperation(graphicsOp)) return VerifyGraphics(doc, a.Action, graphicsOp, e);
                    if (IsLegendOperation(graphicsOp)) return VerifyLegend(doc, a.Action, graphicsOp, e, a.Scale);
                    if (IsControlOperation(graphicsOp)) return VerifyControl(doc, a.Action, graphicsOp, e);
                    if (IsRenumberOperation(graphicsOp)) return VerifyRenumber(doc, a.Action);
                    if (IsPerspectiveOperation(graphicsOp)) return VerifyPerspective(doc, a.Action, e);
                    if (IsRoomIsometricOperation(graphicsOp)) return VerifyRoomIsometric(doc, a.Action, e);
                    if (IsSunStudyOperation(graphicsOp)) return VerifySunStudy(doc, a.Action, e);
                    if (IsGraphicDisplayOperation(graphicsOp)) return VerifyGraphicDisplay(doc, a.Action, e);
                    if (IsViewPropertiesOperation(graphicsOp)) return VerifyViewProperties(doc, a.Action, e);
                    if (IsSelectionFilterOperation(graphicsOp)) return VerifySelectionFilter(doc, a.Action, e);
                    if (IsStartingViewOperation(graphicsOp)) return VerifyStartingView(doc, a.Action, e);
                    if (IsGridExtentOperation(graphicsOp)) return VerifyGridExtent(doc, a.Action, e);
                    return false;
                }
            }
        }

        private static double SheetDistance(XYZ actual, XYZ expected) =>
            new XYZ(actual.X, actual.Y, 0).DistanceTo(new XYZ(expected.X, expected.Y, 0));

        private static ElementId TargetId(Document d, JObject a, Dictionary<string, ElementId> aliases)
        {
            try
            {
                if (!string.Equals(a.Value<string>("operation"), "apply_template", StringComparison.OrdinalIgnoreCase)) return null;
                return a.Value<long?>("template_view_id") == -1 ? ElementId.InvalidElementId : Need<View>(d, a, "template_view_id").Id;
            }
            catch { return null; }
        }
        // AN AREA SCHEME HAS NO CATEGORY A QUERY CAN NAME. MEASURED 2026-09-26: query_model
        // with OST_AreaSchemes matches nothing even though every project carries at least
        // one scheme, so an id the caller cannot discover is not a usable argument on its
        // own. area_scheme_name resolves by name, and any refusal lists the schemes the
        // document really holds - the discovery path is the refusal itself.
        private static AreaScheme AreaSchemeOf(Document doc, JObject a)
        {
            List<AreaScheme> schemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
            string available = schemes.Count == 0 ? "none"
                : string.Join(", ", schemes.Select(s => "'" + s.Name + "' (id " + Rid.Value(s.Id) + ")"));
            if (a["area_scheme_id"] != null)
            {
                long raw = a.Value<long?>("area_scheme_id") ?? -1;
                AreaScheme byId = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) as AreaScheme : null;
                if (byId == null)
                    throw new ArgumentException("area_scheme_id " + raw + " is not an AreaScheme in this document. Schemes: " + available + ".");
                return byId;
            }
            string name = a.Value<string>("area_scheme_name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                AreaScheme byName = schemes.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (byName == null)
                    throw new ArgumentException("no AreaScheme is named '" + name + "'. Schemes: " + available + ".");
                return byName;
            }
            throw new ArgumentException("create_area_plan needs area_scheme_id or area_scheme_name. Schemes: " + available + ".");
        }

        private static BoundingBoxXYZ SectionBox(JObject a, double scale)
        {
            XYZ start = Point(a["start"]) * scale;
            XYZ end = Point(a["end"]) * scale;
            XYZ horizontal = new XYZ(end.X - start.X, end.Y - start.Y, 0);
            if (horizontal.GetLength() < 1e-9) throw new ArgumentException("section start/end must define a non-zero horizontal line");
            if (Math.Abs(end.Z - start.Z) > 1e-6) throw new ArgumentException("section start/end must have the same Z elevation");
            double bottom = Finite(a.Value<double?>("bottom_offset") ?? -1000, "bottom_offset") * scale;
            double top = Finite(a.Value<double?>("top_offset") ?? 3000, "top_offset") * scale;
            double depth = Finite(a.Value<double?>("depth") ?? 5000, "depth") * scale;
            if (top <= bottom) throw new ArgumentException("top_offset must be greater than bottom_offset");
            if (depth <= 0) throw new ArgumentException("depth must be positive");
            XYZ x = horizontal.Normalize();
            XYZ y = XYZ.BasisZ;
            XYZ z = x.CrossProduct(y).Normalize();
            double half = horizontal.GetLength() / 2.0;
            var box = new BoundingBoxXYZ
            {
                Transform = new Transform(Transform.Identity)
                {
                    Origin = (start + end) / 2.0,
                    BasisX = x,
                    BasisY = y,
                    BasisZ = z
                },
                Min = new XYZ(-half, bottom, 0),
                Max = new XYZ(half, top, depth)
            };
            return box;
        }
        private static ViewFamilyType ResolveVft(Document doc, JObject a, ViewFamily family) => a["view_family_type_id"] != null
            ? Need<ViewFamilyType>(doc, a, "view_family_type_id")
            : new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == family)
              ?? throw new ArgumentException("no ViewFamilyType for " + family);
        private static void OptionalViewFamilyType(Document doc, JObject a, ViewFamily family) { ViewFamilyType t = ResolveVft(doc, a, family); if (t.ViewFamily != family) throw new ArgumentException("view_family_type_id is " + t.ViewFamily + ", not " + family); }
        private static T Need<T>(Document doc, JObject a, string field) where T : Element
        { long id = a.Value<long?>(field) ?? -1; if (!Rid.CanRepresent(id) || !(doc.GetElement(Rid.Make(id)) is T value)) throw new ArgumentException(field + " must identify a " + typeof(T).Name + DocumentContext.Hint<T>(doc, id.ToString())); return value; }
        private static void Reference<T>(Document doc, JObject a, string idField, string keyField, Dictionary<string, Type> known) where T : Element
        {
            string key = a.Value<string>(keyField);
            if (!string.IsNullOrWhiteSpace(key))
            {
                if (!known.TryGetValue(key, out Type actual)) throw new ArgumentException(keyField + " references unknown/prior key '" + key + "'");
                if (!typeof(T).IsAssignableFrom(actual))
                    throw new ArgumentException(keyField + " references key '" + key + "' whose result is " + actual.Name + ", not " + typeof(T).Name);
                return;
            }
            Need<T>(doc, a, idField);
        }
        private static void ReferenceViewportView(Document doc, JObject a, Dictionary<string, Type> known)
        {
            string key = a.Value<string>("view_key");
            if (!string.IsNullOrWhiteSpace(key))
            {
                if (!known.TryGetValue(key, out Type actual)) throw new ArgumentException("view_key references unknown/prior key '" + key + "'");
                if (!typeof(View).IsAssignableFrom(actual) || typeof(ViewSchedule).IsAssignableFrom(actual) || typeof(ViewSheet).IsAssignableFrom(actual))
                    throw new ArgumentException("view_key must resolve to a non-schedule, non-sheet View suitable for a Viewport");
                return;
            }
            View view = Need<View>(doc, a, "view_id");
            if (view is ViewSchedule) throw new ArgumentException("view_id is a schedule; use place_schedule");
            if (view is ViewSheet) throw new ArgumentException("view_id is a sheet and cannot be placed in a Viewport");
        }
        private static T Resolve<T>(Document doc, JObject a, string idField, string keyField, Dictionary<string, ElementId> aliases) where T : Element
        { string key = a.Value<string>(keyField); if (!string.IsNullOrWhiteSpace(key)) { if (!aliases.TryGetValue(key, out ElementId id) || !(doc.GetElement(id) is T byKey)) throw new ArgumentException(keyField + " did not resolve to " + typeof(T).Name); return byKey; } return Need<T>(doc, a, idField); }
        private static XYZ Point(JToken token)
        {
            JArray p = token as JArray; if (p == null || p.Count < 2 || p.Count > 3) throw new ArgumentException("point needs XY or XYZ");
            return new XYZ(Finite(p[0].Value<double>(), "X"), Finite(p[1].Value<double>(), "Y"),
                Finite(p.Count > 2 ? p[2].Value<double>() : 0, "Z"));
        }
        private static bool Scale(string u, out double s) { if (u == "feet") { s = 1; return true; } if (u == "m") { s = 1 / 0.3048; return true; } if (u == "mm") { s = 1 / 304.8; return true; } s = 0; return false; }
        private static double Finite(double value, string field)
        { if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException(field + " must be finite"); return value; }
        private static Type ResultType(string operation)
        {
            switch ((operation ?? "").ToLowerInvariant())
            {
                case "create_floor_plan": case "create_ceiling_plan": case "create_structural_plan": return typeof(ViewPlan);
                case "create_3d": case "create_perspective": case "create_room_isometric": return typeof(View3D);
                case "create_drafting": return typeof(ViewDrafting);
                case "create_section": case "create_elevation": return typeof(ViewSection);
                case "duplicate_view": case "apply_template": return typeof(View);
                case "create_sheet": return typeof(ViewSheet);
                case "place_view": return typeof(Viewport);
                case "place_schedule": return typeof(ScheduleSheetInstance);
                case "create_area_plan": return typeof(ViewPlan);
                case "create_callout": return typeof(View);
                case "create_placeholder_sheet": case "convert_placeholder_sheet": case "duplicate_sheet":
                    return typeof(ViewSheet);
                case "set_phase": case "assign_scope_box": case "set_crop": case "set_annotation_crop":
                    return typeof(View);
                case "set_view_range": return typeof(ViewPlan);
                case "set_sun_study": case "set_graphic_display": case "set_starting_view": case "set_view_properties": return typeof(View);
                case "create_selection_filter": return typeof(SelectionFilterElement);
                case "extend_grids_3d": return typeof(Grid);
                case "set_viewport_type": case "align_viewports": return typeof(Viewport);
                // Graphic control. create_filter yields the filter itself, so a later
                // action in the same batch can reference it by key; everything else
                // yields the view it changed.
                case "create_filter": return typeof(ParameterFilterElement);
                case "apply_filter": case "color_by_value": case "set_element_overrides":
                case "hide_elements": case "isolate_elements": case "reset_temporary":
                case "set_category_visibility": return typeof(View);
                case "create_legend": return typeof(View);
                case "edit_filter": return typeof(ParameterFilterElement);
                case "order_filters": case "explain_graphics": case "create_template": case "set_template_controls":
                    return typeof(View);
                case "sheet_set_create": return typeof(ViewSheetSet);
                case "sheet_set_list": case "sheet_set_update": case "sheet_set_delete": return typeof(Element);
                case "place_legend_component": return typeof(Element);
                default: return typeof(Element);
            }
        }
        private static bool InCategory(Element element, BuiltInCategory category)
        { return element?.Category != null && Rid.Value(element.Category.Id) == (long)category; }
    }
}
