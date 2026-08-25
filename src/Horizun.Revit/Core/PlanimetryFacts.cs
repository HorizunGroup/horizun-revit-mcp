// -----------------------------------------------------------------------------
// Horizun Revit MCP - WHAT was read off the documentation surface, with no Revit
// in it.
//
// The inventory reads the model once (Core/PlanimetryInventory.cs, which needs
// Revit) and fills these. horizun_query_planimetry renders them; the auditor
// reasons over them. That is the whole reason they exist as plain objects: two
// collectors that can disagree is the defect this design removes, and rules that
// need a Revit to test are rules nobody tests.
//
// THE ONE INVARIANT THAT RUNS THROUGH EVERY TYPE HERE: a field that could not be
// read is not null and not false. It is absent from its value slot AND named in
// Unreadable, so the auditor can turn it into `unknown` instead of a pass. A
// null that means "Revit would not answer" and a null that means "this does not
// apply" are two different facts, and collapsing them is how a broken sheet
// reports clean - so they are Read.Unreadable and Read.NotApplicable, and they
// print differently.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// Why a slot has no value. `Value` is the only state that carries one; the other
    /// three are the reasons, told apart because they mean different things to a reader
    /// and to a rule.
    /// </summary>
    public enum Read
    {
        Value,
        NotApplicable,
        Unreadable,
        UnsupportedInRevitYear
    }

    /// <summary>One field on one row that Revit would not surrender, and why.</summary>
    public sealed class FieldNote
    {
        public string Field;
        public string Reason;
        public Read State = Read.Unreadable;

        public static FieldNote Unreadable(string field, string reason)
        {
            return new FieldNote { Field = field, Reason = reason, State = Read.Unreadable };
        }

        public static FieldNote NotApplicable(string field, string reason)
        {
            return new FieldNote { Field = field, Reason = reason, State = Read.NotApplicable };
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["field"] = Field,
                ["state"] = StateToken(State),
                ["reason"] = Reason
            };
        }

        /// <summary>The published snake_case token for a state - the enum's own
        /// lowercased name would print "notapplicable", which matches nothing a
        /// schema or a reader expects.</summary>
        public static string StateToken(Read state)
        {
            switch (state)
            {
                case Read.NotApplicable: return "not_applicable";
                case Read.Unreadable: return "unreadable";
                case Read.UnsupportedInRevitYear: return "unsupported_in_revit_year";
                default: return "value";
            }
        }
    }

    /// <summary>Shared row plumbing: identity plus the per-row unreadable list.</summary>
    public abstract class PlanimetryRow
    {
        public long Id;
        public string UniqueId;

        /// <summary>False when the row's own element could not be interrogated at all.</summary>
        public bool Readable = true;

        public List<FieldNote> Notes = new List<FieldNote>();

        public void Note(string field, string reason)
        {
            Notes.Add(FieldNote.Unreadable(field, reason));
        }

        /// <summary>
        /// A property this KIND of view/element simply does not have - Discipline on a
        /// schedule, CanBePrinted on a template. Recorded so the reader sees why the
        /// slot is empty, but NOT counted as unreadable: "does not apply" is a fact
        /// about the entity, not a failure to read it, and folding the two together
        /// would make coverage_complete false on every model that contains a schedule.
        /// </summary>
        public void NoteNotApplicable(string field, string reason)
        {
            Notes.Add(FieldNote.NotApplicable(field, reason));
        }

        /// <summary>True when this row has at least one field Revit would not answer.</summary>
        public bool HasUnreadableField { get { return Notes.Any(n => n.State == Read.Unreadable); } }

        protected JArray NotesJson()
        {
            return new JArray(Notes.Select(n => (JToken)n.ToJson()));
        }
    }

    // -------------------------------------------------------------------------
    // SHEETS
    // -------------------------------------------------------------------------
    public sealed class SheetFact : PlanimetryRow
    {
        public string SheetNumber;
        public string Name;
        public bool? IsPlaceholder;

        public List<long> TitleblockIds = new List<long>();
        public long? TitleblockTypeId;
        public string TitleblockTypeName;
        public string TitleblockFamilyName;
        public bool TitleblocksReadable = true;

        /// <summary>The titleblock's own extent on paper. NOT "the printable area": margins
        /// and allowed zones are a standard, and a standard arrives as an argument.</summary>
        public PlanBox TitleblockExtent = PlanBox.Unreadable;

        /// <summary>The sheet's own outline, which exists whether or not a titleblock does.</summary>
        public PlanBox SheetOutline = PlanBox.Unreadable;

        /// <summary>Which of the two above the auditor used as "the sheet", and why.</summary>
        public string ExtentSource;

        public List<long> ViewportIds = new List<long>();
        public List<long> SchedulePlacementIds = new List<long>();
        public List<long> PlacedViewIds = new List<long>();
        public List<long> RevisionIds = new List<long>();

        public long? GuideGridId;
        public string GuideGridName;

        public Dictionary<string, JToken> Parameters = new Dictionary<string, JToken>(StringComparer.Ordinal);

        /// <summary>The extent a placement is measured against: the titleblock when there is
        /// one, otherwise the sheet outline. Unreadable when neither could be read.</summary>
        public PlanBox Extent
        {
            get { return TitleblockExtent.Valid ? TitleblockExtent : SheetOutline; }
        }

        public JObject ToJson(double scale, bool includeParameters)
        {
            var o = new JObject
            {
                ["entity_kind"] = "sheet",
                ["sheet_id"] = Id,
                ["unique_id"] = UniqueId,
                ["sheet_number"] = SheetNumber,
                ["name"] = Name,
                ["placeholder"] = IsPlaceholder.HasValue ? (JToken)IsPlaceholder.Value : JValue.CreateNull(),
                ["readable"] = Readable,
                ["titleblock_instance_ids"] = new JArray(TitleblockIds.Select(i => (JToken)i)),
                ["titleblock_count"] = TitleblocksReadable ? (JToken)TitleblockIds.Count : JValue.CreateNull(),
                ["titleblock_count_readable"] = TitleblocksReadable,
                ["titleblock_type_id"] = TitleblockTypeId.HasValue ? (JToken)TitleblockTypeId.Value : JValue.CreateNull(),
                ["titleblock_type"] = TitleblockTypeName,
                ["titleblock_family"] = TitleblockFamilyName,
                ["titleblock_extent"] = PlanimetryGeometry.ToDisplayArray(TitleblockExtent, scale) is double[] tb
                    ? new JArray(tb.Select(v => (JToken)v)) : (JToken)JValue.CreateNull(),
                ["sheet_outline"] = PlanimetryGeometry.ToDisplayArray(SheetOutline, scale) is double[] so
                    ? new JArray(so.Select(v => (JToken)v)) : (JToken)JValue.CreateNull(),
                ["extent_source"] = ExtentSource,
                ["viewport_ids"] = new JArray(ViewportIds.Select(i => (JToken)i)),
                ["schedule_placement_ids"] = new JArray(SchedulePlacementIds.Select(i => (JToken)i)),
                ["placed_view_ids"] = new JArray(PlacedViewIds.Select(i => (JToken)i)),
                ["revision_ids"] = new JArray(RevisionIds.Select(i => (JToken)i)),
                ["guide_grid_id"] = GuideGridId.HasValue ? (JToken)GuideGridId.Value : JValue.CreateNull(),
                ["guide_grid_name"] = GuideGridName,
                ["populations"] = new JObject
                {
                    ["viewports"] = ViewportIds.Count,
                    ["schedule_placements"] = SchedulePlacementIds.Count,
                    ["placed_views"] = PlacedViewIds.Count,
                    ["revisions"] = RevisionIds.Count
                },
                ["unreadable_fields"] = NotesJson()
            };
            if (includeParameters)
            {
                var p = new JObject();
                foreach (var pair in Parameters.OrderBy(x => x.Key, StringComparer.Ordinal))
                    p[pair.Key] = pair.Value;
                o["parameters"] = p;
            }
            return o;
        }
    }

    // -------------------------------------------------------------------------
    // VIEWS
    // -------------------------------------------------------------------------
    public sealed class ViewFact : PlanimetryRow
    {
        public string Name;
        public string ViewType;
        public bool? IsTemplate;
        public long? TemplateId;
        public string TemplateName;
        public bool TemplateReadable = true;

        public int? Scale;
        public string Discipline;
        public string SubDiscipline;
        public string DetailLevel;
        public string Phase;
        public string PhaseFilter;
        public long? LevelId;
        public string LevelName;

        public bool? CropBoxActive;
        public bool? CropBoxVisible;
        public PlanBox CropBox = PlanBox.Unreadable;
        public bool CropGeometryReadable = true;

        public bool? AnnotationCropAvailable;
        public bool? AnnotationCropActive;
        public PlanBox AnnotationCrop = PlanBox.Unreadable;

        public long? ScopeBoxId;
        public string ScopeBoxName;

        public JObject ViewRange;
        public Read ViewRangeState = Read.NotApplicable;

        public long? UnderlayLevelId;
        public string UnderlayOrientation;

        public long? PrimaryViewId;
        public bool? IsCallout;
        public List<long> DependentViewIds = new List<long>();

        public List<long> FilterIds = new List<long>();
        public List<string> FilterNames = new List<string>();
        public bool FiltersReadable = true;

        /// <summary>Every sheet this view is placed on, via a Viewport. Revit permits one;
        /// more than one is a finding, and this is where it becomes visible.</summary>
        public List<long> SheetIds = new List<long>();
        public List<long> ViewportIds = new List<long>();

        public bool? CanBePrinted;
        public bool? IsGraphical;

        /// <summary>The view plane, so a caller can turn view coordinates into model ones.
        /// Absent for views that have no plane (schedules).</summary>
        public double[] Origin;
        public double[] RightDirection;
        public double[] UpDirection;

        /// <summary>Per-category tagging coverage, present only for the categories a
        /// requires_tag rule named. Null means nobody asked - never "nothing untagged".</summary>
        public List<TagCoverageFact> TagCoverage;

        /// <summary>Parameters read off the view, for parameter:&lt;name&gt; selectors.</summary>
        public Dictionary<string, JToken> Parameters = new Dictionary<string, JToken>(StringComparer.Ordinal);

        public JObject ToJson(double scale)
        {
            return new JObject
            {
                ["entity_kind"] = "view",
                ["view_id"] = Id,
                ["unique_id"] = UniqueId,
                ["name"] = Name,
                ["view_type"] = ViewType,
                ["is_template"] = IsTemplate.HasValue ? (JToken)IsTemplate.Value : JValue.CreateNull(),
                ["readable"] = Readable,
                ["template_id"] = TemplateId.HasValue ? (JToken)TemplateId.Value : JValue.CreateNull(),
                ["template_name"] = TemplateName,
                ["template_readable"] = TemplateReadable,
                ["scale"] = Scale.HasValue ? (JToken)Scale.Value : JValue.CreateNull(),
                ["discipline"] = Discipline,
                ["subdiscipline"] = SubDiscipline,
                ["detail_level"] = DetailLevel,
                ["phase"] = Phase,
                ["phase_filter"] = PhaseFilter,
                ["level_id"] = LevelId.HasValue ? (JToken)LevelId.Value : JValue.CreateNull(),
                ["level"] = LevelName,
                ["crop_box_active"] = CropBoxActive.HasValue ? (JToken)CropBoxActive.Value : JValue.CreateNull(),
                ["crop_box_visible"] = CropBoxVisible.HasValue ? (JToken)CropBoxVisible.Value : JValue.CreateNull(),
                ["crop_box"] = Box(CropBox, scale),
                ["crop_geometry_readable"] = CropGeometryReadable,
                ["annotation_crop_available"] = AnnotationCropAvailable.HasValue
                    ? (JToken)AnnotationCropAvailable.Value : JValue.CreateNull(),
                ["annotation_crop_active"] = AnnotationCropActive.HasValue
                    ? (JToken)AnnotationCropActive.Value : JValue.CreateNull(),
                ["annotation_crop"] = Box(AnnotationCrop, scale),
                ["scope_box_id"] = ScopeBoxId.HasValue ? (JToken)ScopeBoxId.Value : JValue.CreateNull(),
                ["scope_box"] = ScopeBoxName,
                ["view_range"] = ViewRangeState == Read.Value ? (JToken)ViewRange : JValue.CreateNull(),
                ["view_range_state"] = FieldNote.StateToken(ViewRangeState),
                ["underlay_level_id"] = UnderlayLevelId.HasValue ? (JToken)UnderlayLevelId.Value : JValue.CreateNull(),
                ["underlay_orientation"] = UnderlayOrientation,
                ["parent_view_id"] = PrimaryViewId.HasValue ? (JToken)PrimaryViewId.Value : JValue.CreateNull(),
                ["is_callout"] = IsCallout.HasValue ? (JToken)IsCallout.Value : JValue.CreateNull(),
                ["dependent_view_ids"] = new JArray(DependentViewIds.Select(i => (JToken)i)),
                ["filter_ids"] = new JArray(FilterIds.Select(i => (JToken)i)),
                ["filter_names"] = new JArray(FilterNames.Select(n => (JToken)n)),
                ["filters_readable"] = FiltersReadable,
                ["sheet_ids"] = new JArray(SheetIds.Select(i => (JToken)i)),
                ["viewport_ids"] = new JArray(ViewportIds.Select(i => (JToken)i)),
                ["placed_on_sheet"] = SheetIds.Count > 0,
                ["printable"] = CanBePrinted.HasValue ? (JToken)CanBePrinted.Value : JValue.CreateNull(),
                ["graphical"] = IsGraphical.HasValue ? (JToken)IsGraphical.Value : JValue.CreateNull(),
                ["view_plane"] = Origin == null ? (JToken)JValue.CreateNull() : new JObject
                {
                    ["origin"] = new JArray(Origin.Select(v => (JToken)v)),
                    ["right_direction"] = new JArray(RightDirection.Select(v => (JToken)v)),
                    ["up_direction"] = new JArray(UpDirection.Select(v => (JToken)v))
                },
                ["tag_coverage"] = TagCoverage == null
                    ? (JToken)JValue.CreateNull()
                    : new JArray(TagCoverage.Select(t => (JToken)t.ToJson())),
                ["unreadable_fields"] = NotesJson()
            };
        }

        internal static JToken Box(PlanBox b, double scale)
        {
            double[] a = PlanimetryGeometry.ToDisplayArray(b, scale);
            return a == null ? (JToken)JValue.CreateNull() : new JArray(a.Select(v => (JToken)v));
        }
    }

    /// <summary>
    /// One untagged element a tag-coverage rule may report. Type and family travel with
    /// it so a rule's EXCLUSIONS are applied purely, over data already gathered, instead
    /// of the inventory having to understand the rule.
    /// </summary>
    public sealed class UntaggedElement
    {
        public long Id;
        public string Category;
        public string TypeName;
        public string FamilyName;

        /// <summary>Values of the parameters an exclusion asked about, and nothing else.</summary>
        public Dictionary<string, string> ExclusionParameters =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public JObject ToJson()
        {
            return new JObject
            {
                ["element_id"] = Id,
                ["category"] = Category,
                ["type"] = TypeName,
                ["family"] = FamilyName
            };
        }
    }

    /// <summary>
    /// What one view's tagging looks like for ONE category. Gathered only when a
    /// requires_tag rule asked for that category - the pass is expensive, and a coverage
    /// number nobody asked for is not worth a second walk of every view.
    /// </summary>
    public sealed class TagCoverageFact
    {
        public string Category;

        /// <summary>False when the visible set could not be enumerated, or was larger than
        /// the enumeration bound. Either way the untagged list is a LOWER BOUND and the rule
        /// must report the remainder as unknown rather than as clean.</summary>
        public bool Complete = true;
        public string IncompleteReason;

        public int VisibleTotal;
        public int TaggedTotal;
        public int UntaggedTotal;

        /// <summary>Host-document elements only; a linked element is not the host model's to
        /// tag, and counting it here would fabricate findings against another team's file.</summary>
        public List<UntaggedElement> Untagged = new List<UntaggedElement>();
        public int LinkedVisibleTotal;

        /// <summary>The bound at which enumeration stops. Exceeding it does not truncate a
        /// count - it makes the check say so.</summary>
        public const int MaxEnumerated = 2000;

        public JObject ToJson()
        {
            return new JObject
            {
                ["category"] = Category,
                ["complete"] = Complete,
                ["incomplete_reason"] = IncompleteReason,
                ["visible_total"] = VisibleTotal,
                ["tagged_total"] = TaggedTotal,
                ["untagged_total"] = UntaggedTotal,
                ["untagged_listed"] = Untagged.Count,
                ["linked_visible_total"] = LinkedVisibleTotal,
                ["untagged"] = new JArray(Untagged.Select(u => (JToken)u.ToJson()))
            };
        }
    }

    // -------------------------------------------------------------------------
    // PLACEMENTS - viewports and schedule instances, in SHEET coordinates
    // -------------------------------------------------------------------------
    public sealed class PlacementFact : PlanimetryRow
    {
        /// <summary>"viewport" or "schedule_placement". Never inferred from a shape.</summary>
        public string Class;

        public long SheetId;
        public string SheetNumber;

        public long? ViewId;
        public long? ScheduleId;

        /// <summary>Did the placement's target resolve to an element in this document?</summary>
        public bool? TargetExists;
        public string TargetName;

        public PlanBox Box = PlanBox.Unreadable;
        public PlanBox LabelBox = PlanBox.Unreadable;
        public double[] BoxCenter;

        public string Rotation;
        public long? TypeId;
        public string TypeName;
        public bool? Pinned;
        public string Title;
        public string DetailNumber;

        public bool BoundsReadable = true;

        /// <summary>What a neighbour actually collides with: the box, plus the label when the
        /// label has its own outline. A schedule has no label and keeps its own box.</summary>
        public PlanBox Extent
        {
            get { return PlanimetryGeometry.UnionOptional(Box, LabelBox); }
        }

        public JObject ToJson(double scale)
        {
            return new JObject
            {
                ["entity_kind"] = "placement",
                ["placement_id"] = Id,
                ["unique_id"] = UniqueId,
                ["class"] = Class,
                ["sheet_id"] = SheetId,
                ["sheet_number"] = SheetNumber,
                ["view_id"] = ViewId.HasValue ? (JToken)ViewId.Value : JValue.CreateNull(),
                ["schedule_id"] = ScheduleId.HasValue ? (JToken)ScheduleId.Value : JValue.CreateNull(),
                ["target_exists"] = TargetExists.HasValue ? (JToken)TargetExists.Value : JValue.CreateNull(),
                ["target_name"] = TargetName,
                ["coordinate_system"] = "sheet",
                ["box_outline"] = ViewFact.Box(Box, scale),
                ["label_outline"] = ViewFact.Box(LabelBox, scale),
                ["extent"] = ViewFact.Box(Extent, scale),
                ["box_center"] = BoxCenter == null
                    ? (JToken)JValue.CreateNull()
                    : new JArray(BoxCenter.Select(v => (JToken)PlanimetryGeometry.Display(v, scale))),
                ["bounds_readable"] = BoundsReadable,
                ["rotation"] = Rotation,
                ["viewport_type_id"] = TypeId.HasValue ? (JToken)TypeId.Value : JValue.CreateNull(),
                ["viewport_type"] = TypeName,
                ["pinned"] = Pinned.HasValue ? (JToken)Pinned.Value : JValue.CreateNull(),
                ["title"] = Title,
                ["detail_number"] = DetailNumber,
                ["readable"] = Readable,
                ["unreadable_fields"] = NotesJson()
            };
        }
    }

    // -------------------------------------------------------------------------
    // ANNOTATIONS - dimensions, tags, text, detail 2D, generic annotation
    // -------------------------------------------------------------------------
    public sealed class AnnotationFact : PlanimetryRow
    {
        /// <summary>The discriminator every row carries so one list never mixes silently:
        /// dimension, tag, text_note, detail_curve, filled_region, detail_component,
        /// generic_annotation, revision_cloud, revision_tag.</summary>
        public string Kind;

        public string Category;
        public string Class;

        public long? OwnerViewId;
        public string OwnerViewName;
        public bool? OwnerViewExists;

        public long? TypeId;
        public string TypeName;
        public string FamilyName;

        public PlanBox Box = PlanBox.Unreadable;
        public bool BoundsReadable = true;

        public bool? Pinned;
        public long? GroupId;

        /// <summary>Whether the element carries a per-element graphic override in its
        /// OWNER view (View.GetElementOverrides differs from the defaults). Null when it
        /// could not be read; absent-by-nature (no owner view) is a NotApplicable note.
        /// Category and template overrides are deliberately not folded in: this field
        /// answers for the one override horizun_fix_planimetry can clear.</summary>
        public bool? HasViewOverrides;

        public List<long> SheetPlacementIds = new List<long>();

        // ---- dimension ----
        public bool? AreReferencesAvailable;
        public int? ReferenceCount;
        public int? BrokenReferenceCount;
        public int? LinkedReferenceCount;
        public int? UnreadableReferenceCount;
        public bool? HasValueOverride;
        public List<string> ValueOverrides = new List<string>();
        public bool? IsViewSpecific;
        public int? SegmentCount;

        // ---- tag ----
        public bool? IsOrphaned;
        public List<long> TaggedElementIds = new List<long>();
        public List<string> TargetCategories = new List<string>();
        public int? TargetCount;
        public bool? TargetsLinked;
        public bool? TargetsReadable;
        public bool? HasLeader;
        public double[] TagHeadPoint;

        // ---- text ----
        public string Text;
        public bool? TextIsEmptyOrWhitespace;
        public double? Width;
        public string Alignment;
        public double[] Position;

        // ---- detail 2D ----
        public bool? GeometryReadable;
        public bool? Degenerate;
        public int? LoopCount;
        public bool? IsMasking;
        public double? CurveLength;

        public JObject ToJson(double scale)
        {
            var o = new JObject
            {
                ["entity_kind"] = "annotation",
                ["kind"] = Kind,
                ["element_id"] = Id,
                ["unique_id"] = UniqueId,
                ["category"] = Category,
                ["class"] = Class,
                ["owner_view_id"] = OwnerViewId.HasValue ? (JToken)OwnerViewId.Value : JValue.CreateNull(),
                ["owner_view_name"] = OwnerViewName,
                ["owner_view_exists"] = OwnerViewExists.HasValue ? (JToken)OwnerViewExists.Value : JValue.CreateNull(),
                ["type_id"] = TypeId.HasValue ? (JToken)TypeId.Value : JValue.CreateNull(),
                ["type"] = TypeName,
                ["family"] = FamilyName,
                ["coordinate_system"] = "view_plane",
                ["bounding_box"] = ViewFact.Box(Box, scale),
                ["bounds_readable"] = BoundsReadable,
                ["pinned"] = Pinned.HasValue ? (JToken)Pinned.Value : JValue.CreateNull(),
                ["group_id"] = GroupId.HasValue ? (JToken)GroupId.Value : JValue.CreateNull(),
                ["has_view_overrides"] = HasViewOverrides.HasValue
                    ? (JToken)HasViewOverrides.Value : JValue.CreateNull(),
                ["sheet_placement_ids"] = new JArray(SheetPlacementIds.Select(i => (JToken)i)),
                ["readable"] = Readable,
                ["unreadable_fields"] = NotesJson()
            };
            if (Kind == "dimension")
            {
                o["references_available"] = AreReferencesAvailable.HasValue
                    ? (JToken)AreReferencesAvailable.Value : JValue.CreateNull();
                o["reference_count"] = ReferenceCount.HasValue ? (JToken)ReferenceCount.Value : JValue.CreateNull();
                o["broken_reference_count"] = BrokenReferenceCount.HasValue
                    ? (JToken)BrokenReferenceCount.Value : JValue.CreateNull();
                o["linked_reference_count"] = LinkedReferenceCount.HasValue
                    ? (JToken)LinkedReferenceCount.Value : JValue.CreateNull();
                o["unreadable_reference_count"] = UnreadableReferenceCount.HasValue
                    ? (JToken)UnreadableReferenceCount.Value : JValue.CreateNull();
                o["has_value_override"] = HasValueOverride.HasValue ? (JToken)HasValueOverride.Value : JValue.CreateNull();
                o["value_overrides"] = new JArray(ValueOverrides.Select(v => (JToken)v));
                o["view_specific"] = IsViewSpecific.HasValue ? (JToken)IsViewSpecific.Value : JValue.CreateNull();
                o["segment_count"] = SegmentCount.HasValue ? (JToken)SegmentCount.Value : JValue.CreateNull();
            }
            else if (Kind == "tag" || Kind == "revision_tag")
            {
                o["orphaned"] = IsOrphaned.HasValue ? (JToken)IsOrphaned.Value : JValue.CreateNull();
                o["tagged_element_ids"] = new JArray(TaggedElementIds.Select(i => (JToken)i));
                o["target_categories"] = new JArray(TargetCategories.Select(c => (JToken)c));
                o["target_count"] = TargetCount.HasValue ? (JToken)TargetCount.Value : JValue.CreateNull();
                o["targets_linked"] = TargetsLinked.HasValue ? (JToken)TargetsLinked.Value : JValue.CreateNull();
                o["targets_readable"] = TargetsReadable.HasValue ? (JToken)TargetsReadable.Value : JValue.CreateNull();
                o["has_leader"] = HasLeader.HasValue ? (JToken)HasLeader.Value : JValue.CreateNull();
                o["tag_head_point"] = TagHeadPoint == null
                    ? (JToken)JValue.CreateNull()
                    : new JArray(TagHeadPoint.Select(v => (JToken)PlanimetryGeometry.Display(v, scale)));
            }
            else if (Kind == "text_note")
            {
                o["text"] = Text;
                o["empty_or_whitespace"] = TextIsEmptyOrWhitespace.HasValue
                    ? (JToken)TextIsEmptyOrWhitespace.Value : JValue.CreateNull();
                o["width"] = Width.HasValue ? (JToken)PlanimetryGeometry.Display(Width.Value, scale) : JValue.CreateNull();
                o["alignment"] = Alignment;
                o["position"] = Position == null
                    ? (JToken)JValue.CreateNull()
                    : new JArray(Position.Select(v => (JToken)PlanimetryGeometry.Display(v, scale)));
            }
            else
            {
                o["geometry_readable"] = GeometryReadable.HasValue ? (JToken)GeometryReadable.Value : JValue.CreateNull();
                o["degenerate"] = Degenerate.HasValue ? (JToken)Degenerate.Value : JValue.CreateNull();
                o["loop_count"] = LoopCount.HasValue ? (JToken)LoopCount.Value : JValue.CreateNull();
                o["is_masking"] = IsMasking.HasValue ? (JToken)IsMasking.Value : JValue.CreateNull();
                o["curve_length"] = CurveLength.HasValue
                    ? (JToken)PlanimetryGeometry.Display(CurveLength.Value, scale) : JValue.CreateNull();
            }
            return o;
        }
    }

    // -------------------------------------------------------------------------
    // REFERENCES BETWEEN VIEWS
    // -------------------------------------------------------------------------
    public sealed class ReferenceFact : PlanimetryRow
    {
        /// <summary>section_head, elevation_marker, callout, reference_viewer, view_reference.</summary>
        public string Kind;
        public string Category;

        public long? OwnerViewId;
        public string OwnerViewName;

        public long? TargetViewId;
        public string TargetViewName;

        /// <summary>resolved | missing | unknown. `unknown` is the honest answer when the API
        /// exposes no relation - it is never guessed from a name.</summary>
        public string TargetState = "unknown";
        public string TargetStateReason;

        public bool? TargetPlaced;
        public List<long> TargetSheetIds = new List<long>();

        public JObject ToJson()
        {
            return new JObject
            {
                ["entity_kind"] = "view_reference",
                ["element_id"] = Id,
                ["unique_id"] = UniqueId,
                ["kind"] = Kind,
                ["category"] = Category,
                ["owner_view_id"] = OwnerViewId.HasValue ? (JToken)OwnerViewId.Value : JValue.CreateNull(),
                ["owner_view_name"] = OwnerViewName,
                ["target_view_id"] = TargetViewId.HasValue ? (JToken)TargetViewId.Value : JValue.CreateNull(),
                ["target_view_name"] = TargetViewName,
                ["target_state"] = TargetState,
                ["target_state_reason"] = TargetStateReason,
                ["target_placed"] = TargetPlaced.HasValue ? (JToken)TargetPlaced.Value : JValue.CreateNull(),
                ["target_sheet_ids"] = new JArray(TargetSheetIds.Select(i => (JToken)i)),
                ["readable"] = Readable,
                ["unreadable_fields"] = NotesJson()
            };
        }
    }

    /// <summary>One element the inventory could not interrogate at all.</summary>
    public sealed class PlanimetryUnreadable
    {
        public string Population;
        public long? ElementId;
        public string Reason;

        public JObject ToJson()
        {
            return new JObject
            {
                ["population"] = Population,
                ["element_id"] = ElementId.HasValue ? (JToken)ElementId.Value : JValue.CreateNull(),
                ["reason"] = Reason
            };
        }
    }

    /// <summary>One collection pass that DIED. Distinct from an unreadable element: this is
    /// a population nobody looked at, and its absence must never read as emptiness.</summary>
    public sealed class PlanimetryCheckFailure
    {
        public string Check;
        public string Error;

        public JObject ToJson()
        {
            return new JObject
            {
                ["check"] = Check,
                ["error"] = Error,
                ["consequence"] = "'" + Check + "' was NOT collected. Its contents are unknown, not empty."
            };
        }
    }

    /// <summary>
    /// One read of the documentation surface. Every rule and every query answer in this
    /// phase is a function of exactly this object, which is why the two tools cannot
    /// disagree about what the model contains.
    /// </summary>
    public sealed class PlanimetrySnapshot
    {
        public string DocumentTitle;
        public int RevitYear;

        public List<SheetFact> Sheets = new List<SheetFact>();
        public List<ViewFact> Views = new List<ViewFact>();
        public List<PlacementFact> Placements = new List<PlacementFact>();
        public List<AnnotationFact> Annotations = new List<AnnotationFact>();
        public List<ReferenceFact> References = new List<ReferenceFact>();

        public List<PlanimetryCheckFailure> ChecksFailed = new List<PlanimetryCheckFailure>();
        public List<PlanimetryUnreadable> Unreadable = new List<PlanimetryUnreadable>();

        /// <summary>Totals for the whole document, independent of any scope narrowing.</summary>
        public Dictionary<string, int> Totals = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Closed worksets: elements that are not in the document at all, so every
        /// pass above ran perfectly over a model with holes in it.</summary>
        public bool VisibilityCoverageComplete = true;
        public JObject VisibilityCoverage;

        public bool LinkCoverageComplete = true;
        public JObject LinkCoverage;

        /// <summary>Whether the scope narrowed what was collected. When it did, Totals still
        /// describe the WHOLE document and the populations describe the scope.</summary>
        public bool Scoped;

        public int UnreadableTotal
        {
            get
            {
                return Unreadable.Count +
                       Sheets.Count(s => s.HasUnreadableField) +
                       Views.Count(v => v.HasUnreadableField) +
                       Placements.Count(p => p.HasUnreadableField) +
                       Annotations.Count(a => a.HasUnreadableField) +
                       References.Count(r => r.HasUnreadableField);
            }
        }

        /// <summary>
        /// THE field a reader looks at before believing an empty finding list. False when a
        /// pass died, when any element or field could not be read, or when part of the model
        /// was not in the document to be read.
        /// </summary>
        public bool CoverageComplete
        {
            get
            {
                return ChecksFailed.Count == 0 && UnreadableTotal == 0 &&
                       VisibilityCoverageComplete && LinkCoverageComplete;
            }
        }

        public string CoverageNote()
        {
            if (CoverageComplete) return null;
            var bits = new List<string>();
            if (ChecksFailed.Count > 0)
                bits.Add(ChecksFailed.Count + " collection pass(es) did not run at all");
            if (UnreadableTotal > 0)
                bits.Add(UnreadableTotal + " element(s) or field(s) could not be read");
            if (!VisibilityCoverageComplete)
                bits.Add("part of the model is not loaded in this document (closed worksets)");
            if (!LinkCoverageComplete)
                bits.Add("at least one Revit link is not loaded");
            return string.Join("; ", bits) +
                   ". Coverage is INCOMPLETE: the absence of a finding here is not a pass, because " +
                   "what was not read is unknown rather than clean.";
        }

        public JObject CoverageJson()
        {
            return new JObject
            {
                ["coverage_complete"] = CoverageComplete,
                ["checks_failed"] = new JArray(ChecksFailed.Select(c => (JToken)c.ToJson())),
                ["unreadable_total"] = UnreadableTotal,
                ["visibility_coverage"] = VisibilityCoverage,
                ["link_coverage"] = LinkCoverage,
                ["note"] = CoverageNote()
            };
        }

        public int Total(string key)
        {
            int v;
            return Totals.TryGetValue(key, out v) ? v : 0;
        }

        public JObject TotalsJson()
        {
            var o = new JObject();
            foreach (var pair in Totals.OrderBy(x => x.Key, StringComparer.Ordinal))
                o[pair.Key] = pair.Value;
            return o;
        }

        /// <summary>Sheet number for an id, for findings that must name the sheet.</summary>
        public string SheetNumberOf(long? sheetId)
        {
            if (!sheetId.HasValue) return null;
            SheetFact s = Sheets.FirstOrDefault(x => x.Id == sheetId.Value);
            return s == null ? null : s.SheetNumber;
        }

        public ViewFact ViewById(long id) { return Views.FirstOrDefault(v => v.Id == id); }
        public SheetFact SheetById(long id) { return Sheets.FirstOrDefault(s => s.Id == id); }

        public static string Number(double v)
        {
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
