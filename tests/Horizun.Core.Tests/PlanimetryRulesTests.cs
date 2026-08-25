// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// What the planimetry auditor CONCLUDES, over synthetic snapshots. The snapshot
// is plain data by design, so every case below - including the ones a live Revit
// will not produce on demand, like a viewport whose outline will not read - is
// an ordinary test rather than a model somebody has to build first.
//
// The properties these pin, in order of how expensive getting them wrong is:
//
//   1. AN UNREADABLE FACT IS NEVER A PASS. A placement whose bounds would not
//      read is `unknown`, and the check that examined it reports unknown rather
//      than passed. This is the substitution the whole repository exists to
//      refuse, and it is the one an audit makes most easily: the element simply
//      is not in the overlap list.
//   2. TOUCHING IS NOT OVERLAPPING, and the finding carries the measurement, so
//      a reader can see WHY.
//   3. SEVERITY IS EARNED. A view without a template is advisory - a working
//      view legitimately has none. A dimension override is advisory until a
//      requirement set forbids it. Only things that are broken on any sheet in
//      any office are blocking.
//   4. THE ORDER IS THE PUBLISHED ORDER, and two runs over one snapshot produce
//      the same list, or a cursor means nothing.
//   5. THERE IS NO SCORE. Nothing in the output is a number out of 100.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanimetryRulesTests
    {
        // ---------------------------------------------------------------------
        // Builders. Everything defaults to READABLE and VALID, so each test only
        // says what it is about.
        // ---------------------------------------------------------------------
        private static PlanimetryRuleOptions Options(bool advisory = true, bool passed = false)
        {
            return new PlanimetryRuleOptions
            {
                Units = "mm",
                ScaleFromFeet = 304.8,
                ToleranceFeet = PlanimetryGeometry.TouchToleranceFeet,
                IncludeAdvisory = advisory,
                IncludePassedChecks = passed
            };
        }

        private static double Mm(double mm) { return mm / 304.8; }

        private static PlanBox Box(double x1, double y1, double x2, double y2)
        {
            return PlanBox.FromCorners(Mm(x1), Mm(y1), Mm(x2), Mm(y2));
        }

        private static SheetFact Sheet(long id, string number = "A-201", int titleblocks = 1)
        {
            var s = new SheetFact
            {
                Id = id,
                UniqueId = "sheet-" + id,
                SheetNumber = number,
                Name = "Plan " + number,
                IsPlaceholder = false,
                SheetOutline = Box(0, 0, 841, 594),
                ExtentSource = "sheet_outline"
            };
            for (int i = 0; i < titleblocks; i++) s.TitleblockIds.Add(id * 1000 + i);
            if (titleblocks > 0)
            {
                s.TitleblockTypeId = 900;
                s.TitleblockTypeName = "A1 metric";
                s.TitleblockFamilyName = "Horizun titleblock";
                s.TitleblockExtent = Box(0, 0, 841, 594);
                s.ExtentSource = "titleblock";
            }
            return s;
        }

        private static ViewFact View(long id, string name = "Level 1", string type = "FloorPlan")
        {
            return new ViewFact
            {
                Id = id,
                UniqueId = "view-" + id,
                Name = name,
                ViewType = type,
                IsTemplate = false,
                TemplateId = 700,
                TemplateName = "ARQ-PLANTA",
                Scale = 50,
                Discipline = "Architectural",
                DetailLevel = "Fine",
                CropBoxActive = false,
                CanBePrinted = true,
                IsGraphical = true
            };
        }

        private static PlacementFact Viewport(long id, long sheetId, long viewId,
                                              double x1, double y1, double x2, double y2)
        {
            return new PlacementFact
            {
                Id = id,
                UniqueId = "vp-" + id,
                Class = "viewport",
                SheetId = sheetId,
                SheetNumber = "A-201",
                ViewId = viewId,
                TargetExists = true,
                TargetName = "Level 1",
                Box = Box(x1, y1, x2, y2),
                TypeId = 800,
                TypeName = "Title w Line",
                Rotation = "None",
                Pinned = false
            };
        }

        private static PlacementFact Schedule(long id, long sheetId, long scheduleId,
                                              double x1, double y1, double x2, double y2)
        {
            return new PlacementFact
            {
                Id = id,
                UniqueId = "si-" + id,
                Class = "schedule_placement",
                SheetId = sheetId,
                SheetNumber = "A-201",
                ScheduleId = scheduleId,
                TargetExists = true,
                TargetName = "Door schedule",
                Box = Box(x1, y1, x2, y2),
                Pinned = false
            };
        }

        private static AnnotationFact Dimension(long id, long viewId)
        {
            return new AnnotationFact
            {
                Id = id,
                UniqueId = "dim-" + id,
                Kind = "dimension",
                Category = "Dimensions",
                Class = "Dimension",
                OwnerViewId = viewId,
                OwnerViewExists = true,
                OwnerViewName = "Level 1",
                TypeId = 600,
                TypeName = "Linear - 2.5mm",
                IsViewSpecific = true,
                AreReferencesAvailable = true,
                ReferenceCount = 2,
                BrokenReferenceCount = 0,
                LinkedReferenceCount = 0,
                UnreadableReferenceCount = 0,
                HasValueOverride = false,
                SegmentCount = 1,
                Box = Box(10, 10, 100, 20)
            };
        }

        private static AnnotationFact Tag(long id, long viewId, long typeId, params long[] targets)
        {
            var t = new AnnotationFact
            {
                Id = id,
                UniqueId = "tag-" + id,
                Kind = "tag",
                Category = "Door Tags",
                Class = "IndependentTag",
                OwnerViewId = viewId,
                OwnerViewExists = true,
                OwnerViewName = "Level 1",
                TypeId = typeId,
                TypeName = "Door tag",
                IsOrphaned = false,
                TargetsReadable = true,
                TargetsLinked = false,
                TargetCount = targets.Length,
                HasLeader = false,
                Box = Box(10, 10, 30, 20)
            };
            t.TaggedElementIds.AddRange(targets);
            return t;
        }

        private static AnnotationFact Text(long id, long viewId, string text = "NOTA")
        {
            return new AnnotationFact
            {
                Id = id,
                UniqueId = "txt-" + id,
                Kind = "text_note",
                Category = "Text Notes",
                Class = "TextNote",
                OwnerViewId = viewId,
                OwnerViewExists = true,
                TypeId = 500,
                TypeName = "3mm Arial",
                Text = text,
                TextIsEmptyOrWhitespace = string.IsNullOrWhiteSpace(text),
                Box = Box(200, 200, 260, 210)
            };
        }

        private static AnnotationFact Curve(long id, long viewId, double lengthMm = 1000)
        {
            return new AnnotationFact
            {
                Id = id,
                UniqueId = "crv-" + id,
                Kind = "detail_curve",
                Category = "Lines",
                Class = "DetailLine",
                OwnerViewId = viewId,
                OwnerViewExists = true,
                GeometryReadable = true,
                Degenerate = false,
                CurveLength = Mm(lengthMm),
                Box = Box(0, 0, lengthMm, 1)
            };
        }

        private static ReferenceFact Reference(long id, long ownerView, string state = "resolved",
                                               long? target = 42, bool placed = true)
        {
            return new ReferenceFact
            {
                Id = id,
                UniqueId = "ref-" + id,
                Kind = "callout",
                Category = "Callouts",
                OwnerViewId = ownerView,
                OwnerViewName = "Level 1",
                TargetViewId = target,
                TargetViewName = "Detail 1",
                TargetState = state,
                TargetPlaced = state == "resolved" ? (bool?)placed : null
            };
        }

        private static PlanimetrySnapshot Snapshot(params object[] facts)
        {
            var snap = new PlanimetrySnapshot { DocumentTitle = "HZ_TEST", RevitYear = 2026 };
            foreach (object f in facts)
            {
                if (f is SheetFact s) snap.Sheets.Add(s);
                else if (f is ViewFact v) snap.Views.Add(v);
                else if (f is PlacementFact p) snap.Placements.Add(p);
                else if (f is AnnotationFact a) snap.Annotations.Add(a);
                else if (f is ReferenceFact r) snap.References.Add(r);
                else throw new ArgumentException("unknown fact type " + f.GetType().Name);
            }
            return snap;
        }

        private static List<PlanimetryFinding> Failed(PlanimetryAuditResult r, string ruleId)
        {
            return r.Findings.Where(f => f.RuleId == ruleId && f.Status == "failed").ToList();
        }

        private static List<PlanimetryFinding> Unknowns(PlanimetryAuditResult r, string ruleId)
        {
            return r.Findings.Where(f => f.RuleId == ruleId && f.Status == "unknown").ToList();
        }

        private static PlanimetryCheckRun Run(PlanimetryAuditResult r, string ruleId)
        {
            return r.Checks.Single(c => c.RuleId == ruleId);
        }

        // =====================================================================
        // SHEETS
        // =====================================================================

        [Fact]
        public void A_sheet_with_no_titleblock_is_blocking_and_names_the_sheet()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10, "A-201", titleblocks: 0)), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "sheet.no-titleblock"));
            Assert.Equal("blocking", f.Severity);
            Assert.Equal("sheet", f.EntityKind);
            Assert.Equal(10, f.SheetId);
            Assert.Equal("A-201", f.SheetNumber);
            Assert.Equal(new long[] { 10 }, f.ElementIds.ToArray());
            Assert.Equal(0, (int)f.Observed["titleblock_count"]);
            Assert.Equal(1, (int)f.Expected["titleblock_count"]);
            Assert.False(f.Fixable);
        }

        [Fact]
        public void A_placeholder_sheet_is_exempt_because_it_cannot_hold_a_titleblock()
        {
            SheetFact s = Sheet(10, "A-900", titleblocks: 0);
            s.IsPlaceholder = true;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(s), Options());
            Assert.Empty(Failed(r, "sheet.no-titleblock"));
        }

        [Fact]
        public void A_sheet_with_two_titleblocks_is_blocking_and_lists_them_all()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10, "A-201", titleblocks: 2)), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "sheet.multiple-titleblocks"));
            Assert.Equal(2, (int)f.Observed["titleblock_count"]);
            Assert.Equal(3, f.ElementIds.Count);          // the sheet plus both blocks
            Assert.Contains(10000L, f.ElementIds);
            Assert.Contains(10001L, f.ElementIds);
        }

        [Fact]
        public void A_sheet_whose_titleblocks_would_not_enumerate_is_unknown_not_a_missing_titleblock()
        {
            SheetFact s = Sheet(10, "A-201", titleblocks: 0);
            s.TitleblocksReadable = false;
            s.Note("titleblocks", "the collector threw");
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(s), Options());
            Assert.Empty(Failed(r, "sheet.no-titleblock"));
            Assert.Single(Unknowns(r, "sheet.unreadable"));
            Assert.Equal("unknown", Run(r, "sheet.unreadable").Status);
            Assert.DoesNotContain(r.Findings, f => f.RuleId == "sheet.no-titleblock" && f.Status == "passed");
        }

        // =====================================================================
        // LAYOUT
        // =====================================================================

        [Fact]
        public void Two_overlapping_viewports_are_blocking_and_carry_the_measurement()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(
                Sheet(10), View(20), View(21),
                Viewport(30, 10, 20, 0, 0, 100, 100),
                Viewport(31, 10, 21, 90, 80, 200, 200)), Options());

            PlanimetryFinding f = Assert.Single(Failed(r, "sheet.viewport-overlap"));
            Assert.Equal("blocking", f.Severity);
            Assert.Equal(new long[] { 30, 31 }, f.ElementIds.ToArray());
            Assert.Equal(10.0, (double)f.Observed["overlap_x"], 3);
            Assert.Equal(20.0, (double)f.Observed["overlap_y"], 3);
            Assert.Equal("mm", (string)f.Observed["units"]);
            Assert.Equal("sheet", f.CoordinateSystem);
            Assert.NotNull(f.Point);
        }

        [Fact]
        public void Placements_that_only_touch_are_not_reported_as_overlapping()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(
                Sheet(10), View(20), View(21),
                Viewport(30, 10, 20, 0, 0, 100, 100),
                Viewport(31, 10, 21, 100, 0, 200, 100)), Options());
            Assert.Empty(Failed(r, "sheet.viewport-overlap"));
            Assert.Equal("passed", Run(r, "sheet.viewport-overlap").Status);
        }

        [Fact]
        public void A_viewport_and_a_schedule_that_overlap_get_their_own_rule_id()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(
                Sheet(10), View(20),
                Viewport(30, 10, 20, 0, 0, 100, 100),
                Schedule(40, 10, 50, 50, 50, 200, 200)), Options());
            Assert.Empty(Failed(r, "sheet.viewport-overlap"));
            PlanimetryFinding f = Assert.Single(Failed(r, "sheet.viewport-schedule-overlap"));
            Assert.Equal(new long[] { 30, 40 }, f.ElementIds.ToArray());
        }

        [Fact]
        public void Two_schedules_that_overlap_get_theirs()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(
                Sheet(10),
                Schedule(40, 10, 50, 0, 0, 100, 100),
                Schedule(41, 10, 51, 50, 50, 200, 200)), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "sheet.schedule-overlap"));
            Assert.Equal("schedule_placement", f.EntityKind);
        }

        [Fact]
        public void The_label_outline_is_part_of_what_collides()
        {
            PlacementFact a = Viewport(30, 10, 20, 0, 0, 100, 100);
            a.LabelBox = Box(0, -30, 60, -5);
            PlacementFact b = Viewport(31, 10, 21, 20, -40, 80, -10);

            PlanimetryAuditResult withLabel = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10), View(20), View(21), a, b), Options());
            Assert.Single(Failed(withLabel, "sheet.viewport-overlap"));

            a.LabelBox = PlanBox.Unreadable;   // no label read at all
            PlanimetryAuditResult without = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10), View(20), View(21), a, b), Options());
            Assert.Empty(Failed(without, "sheet.viewport-overlap"));
        }

        [Fact]
        public void A_placement_whose_bounds_would_not_read_is_unknown_and_in_no_overlap_answer()
        {
            PlacementFact broken = Viewport(31, 10, 21, 0, 0, 1, 1);
            broken.Box = PlanBox.Unreadable;
            broken.BoundsReadable = false;
            broken.Note("box_outline", "GetBoxOutline threw");

            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(
                Sheet(10), View(20), View(21),
                Viewport(30, 10, 20, 0, 0, 100, 100), broken), Options());

            PlanimetryFinding f = Assert.Single(Unknowns(r, "placement.bounds-unreadable"));
            Assert.Equal("unknown", f.Severity);
            Assert.Equal(31, f.ElementIds.Single());
            Assert.Empty(Failed(r, "sheet.viewport-overlap"));
            // AND the overlap check must not read as passed, because one placement was
            // never in it.
            Assert.Equal("unknown", Run(r, "placement.bounds-unreadable").Status);
        }

        [Fact]
        public void A_placement_entirely_off_the_sheet_is_blocking_and_a_grazing_one_is_not()
        {
            PlanimetryAuditResult off = PlanimetryRules.EvaluateUniversal(Snapshot(
                Sheet(10), View(20), Viewport(30, 10, 20, 2000, 2000, 2100, 2100)), Options());
            PlanimetryFinding f = Assert.Single(Failed(off, "sheet.placement-outside-extent"));
            Assert.Equal("titleblock", (string)f.Observed["sheet_extent_source"]);

            PlanimetryAuditResult grazing = PlanimetryRules.EvaluateUniversal(Snapshot(
                Sheet(10), View(20), Viewport(30, 10, 20, 841, 0, 1000, 100)), Options());
            Assert.Empty(Failed(grazing, "sheet.placement-outside-extent"));
        }

        [Fact]
        public void A_viewport_whose_view_is_gone_is_blocking()
        {
            PlacementFact vp = Viewport(30, 10, 999, 0, 0, 100, 100);
            vp.TargetExists = false;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10), vp), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "viewport.view-missing"));
            Assert.Equal("horizun_manage_views", f.RecommendedTool);
            Assert.False(f.Fixable);
        }

        [Fact]
        public void A_schedule_placement_whose_schedule_is_gone_is_blocking()
        {
            PlacementFact si = Schedule(40, 10, 999, 0, 0, 100, 100);
            si.TargetExists = false;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10), si), Options());
            Assert.Single(Failed(r, "schedule-placement.target-missing"));
        }

        [Fact]
        public void A_sheet_whose_extent_would_not_read_is_unknown_when_it_holds_placements()
        {
            SheetFact s = Sheet(10);
            s.TitleblockExtent = PlanBox.Unreadable;
            s.SheetOutline = PlanBox.Unreadable;
            s.ExtentSource = null;
            s.ViewportIds.Add(30);
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(s, View(20), Viewport(30, 10, 20, 0, 0, 100, 100)), Options());
            Assert.Single(Unknowns(r, "sheet.extent-unreadable"));
            Assert.Empty(Failed(r, "sheet.placement-outside-extent"));
        }

        // =====================================================================
        // VIEWS
        // =====================================================================

        [Fact]
        public void A_view_held_by_two_viewports_is_blocking()
        {
            ViewFact v = View(20);
            v.ViewportIds.AddRange(new long[] { 30, 31 });
            v.SheetIds.AddRange(new long[] { 10, 11 });
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(v), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "view.placed-on-multiple-sheets"));
            Assert.Equal(2, (int)f.Observed["viewport_count"]);
            Assert.Equal(new long[] { 20, 30, 31 }, f.ElementIds.ToArray());
        }

        [Fact]
        public void A_view_without_a_template_is_ADVISORY_not_blocking()
        {
            ViewFact v = View(20);
            v.TemplateId = null;
            v.TemplateName = null;
            v.SheetIds.Add(10);
            v.ViewportIds.Add(30);
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(v), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "view.no-template"));
            Assert.Equal("advisory", f.Severity);
        }

        [Fact]
        public void A_view_that_is_on_no_sheet_is_ADVISORY_not_blocking()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20)), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "view.not-placed"));
            Assert.Equal("advisory", f.Severity);
        }

        [Fact]
        public void A_schedule_or_non_printable_view_is_not_reported_as_unplaced()
        {
            ViewFact v = View(20, "Door schedule", "Schedule");
            v.IsGraphical = false;
            v.CanBePrinted = true;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(v), Options());
            Assert.Empty(Failed(r, "view.not-placed"));
        }

        [Fact]
        public void A_template_owns_no_placement_and_is_examined_by_no_view_rule()
        {
            ViewFact t = View(20, "ARQ-PLANTA", "FloorPlan");
            t.IsTemplate = true;
            t.TemplateId = null;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(t), Options());
            Assert.Empty(Failed(r, "view.no-template"));
            Assert.Empty(Failed(r, "view.not-placed"));
        }

        [Fact]
        public void A_view_whose_template_would_not_read_is_unknown_not_template_less()
        {
            ViewFact v = View(20);
            v.TemplateReadable = false;
            v.TemplateId = null;
            v.SheetIds.Add(10);
            v.ViewportIds.Add(30);
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(v), Options());
            Assert.Empty(Failed(r, "view.no-template"));
            Assert.Single(Unknowns(r, "view.template-unreadable"));
        }

        [Fact]
        public void A_view_whose_type_would_not_read_is_unclassifiable_and_examined_by_nothing_else()
        {
            ViewFact v = View(20);
            v.ViewType = null;
            v.Note("view_type", "ViewType threw");
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(v), Options());
            Assert.Single(Unknowns(r, "view.unclassifiable"));
            Assert.Empty(Failed(r, "view.not-placed"));
            Assert.Empty(Failed(r, "view.no-template"));
        }

        [Fact]
        public void A_crop_that_is_active_but_unreadable_is_unknown()
        {
            ViewFact v = View(20);
            v.CropBoxActive = true;
            v.CropGeometryReadable = false;
            v.SheetIds.Add(10);
            v.ViewportIds.Add(30);
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(v), Options());
            Assert.Single(Unknowns(r, "view.crop-geometry-unreadable"));
        }

        // =====================================================================
        // DIMENSIONS
        // =====================================================================

        [Fact]
        public void A_dimension_whose_references_are_unavailable_is_blocking()
        {
            AnnotationFact d = Dimension(50, 20);
            d.AreReferencesAvailable = false;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20), d), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "dimension.references-unavailable"));
            Assert.Equal("blocking", f.Severity);
            Assert.Equal(20, f.ViewId);
        }

        [Fact]
        public void A_broken_reference_is_blocking_and_a_LINKED_one_is_not()
        {
            AnnotationFact broken = Dimension(50, 20);
            broken.BrokenReferenceCount = 1;
            AnnotationFact linked = Dimension(51, 20);
            linked.LinkedReferenceCount = 2;
            linked.BrokenReferenceCount = 0;

            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), broken, linked), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "dimension.broken-reference"));
            Assert.Equal(50, f.ElementIds.Single());
            Assert.Contains("never counted broken", (string)f.Expected["note"]);
        }

        [Fact]
        public void An_unreadable_reference_is_unknown_not_broken()
        {
            AnnotationFact d = Dimension(50, 20);
            d.UnreadableReferenceCount = 1;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20), d), Options());
            Assert.Single(Unknowns(r, "dimension.reference-unreadable"));
            Assert.Empty(Failed(r, "dimension.broken-reference"));
        }

        [Fact]
        public void A_numeric_override_is_ADVISORY_by_default()
        {
            AnnotationFact d = Dimension(50, 20);
            d.HasValueOverride = true;
            d.ValueOverrides.Add("VARIES");
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20), d), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "dimension.value-override"));
            Assert.Equal("advisory", f.Severity);
            Assert.Equal("horizun_edit_dimensions", f.RecommendedTool);
            Assert.Contains("VARIES", f.Observed["value_overrides"].Select(t => (string)t));
        }

        [Fact]
        public void A_non_view_specific_dimension_is_reported_as_a_constraint_not_as_a_defect()
        {
            AnnotationFact d = Dimension(50, 20);
            d.IsViewSpecific = false;
            d.OwnerViewId = null;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20), d), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "dimension.no-owner-view"));
            Assert.Equal("advisory", f.Severity);
            Assert.Contains("model constraint", (string)f.Expected["note"]);
        }

        // =====================================================================
        // TAGS
        // =====================================================================

        [Fact]
        public void An_orphaned_tag_is_blocking()
        {
            AnnotationFact t = Tag(60, 20, 100, 1);
            t.IsOrphaned = true;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20), t), Options());
            Assert.Equal("blocking", Assert.Single(Failed(r, "tag.orphaned")).Severity);
        }

        [Fact]
        public void A_linked_target_is_unknown_and_never_orphaned()
        {
            AnnotationFact t = Tag(60, 20, 100, 1);
            t.TargetsLinked = true;
            t.TargetCount = 2;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20), t), Options());
            Assert.Empty(Failed(r, "tag.orphaned"));
            PlanimetryFinding f = Assert.Single(Unknowns(r, "tag.linked-target-not-inspected"));
            Assert.Contains("Not inspected is not broken", (string)f.Observed["note"]);
        }

        [Fact]
        public void A_tag_whose_targets_would_not_read_is_unknown_not_orphaned()
        {
            AnnotationFact t = Tag(60, 20, 100);
            t.TargetsReadable = false;
            t.Note("tagged_elements", "GetTaggedLocalElementIds threw");
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20), t), Options());
            Assert.Single(Unknowns(r, "tag.target-unreadable"));
            Assert.Empty(Failed(r, "tag.orphaned"));
        }

        [Fact]
        public void Two_identical_tags_over_the_same_target_in_the_same_view_are_advisory_duplicates()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), Tag(60, 20, 100, 7), Tag(61, 20, 100, 7)), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "tag.duplicate"));
            Assert.Equal("advisory", f.Severity);
            Assert.Equal(new long[] { 60, 61 }, f.ElementIds.ToArray());
            Assert.Equal(2, (int)f.Observed["duplicate_count"]);
        }

        [Fact]
        public void Tags_of_different_types_or_in_different_views_are_not_duplicates()
        {
            PlanimetryAuditResult otherType = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), Tag(60, 20, 100, 7), Tag(61, 20, 101, 7)), Options());
            Assert.Empty(Failed(otherType, "tag.duplicate"));

            PlanimetryAuditResult otherView = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), View(21), Tag(60, 20, 100, 7), Tag(61, 21, 100, 7)), Options());
            Assert.Empty(Failed(otherView, "tag.duplicate"));
        }

        [Fact]
        public void A_multi_reference_tag_is_keyed_by_its_WHOLE_target_set()
        {
            // A tag over {7, 8} is not a duplicate of a tag over {7}: collapsing a
            // multi-reference tag onto one target would fabricate the finding.
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), Tag(60, 20, 100, 7, 8), Tag(61, 20, 100, 7)), Options());
            Assert.Empty(Failed(r, "tag.duplicate"));

            // Two tags over the same PAIR, in either id order, are.
            PlanimetryAuditResult pair = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), Tag(60, 20, 100, 7, 8), Tag(61, 20, 100, 8, 7)), Options());
            Assert.Single(Failed(pair, "tag.duplicate"));
        }

        // =====================================================================
        // TEXT AND 2D DETAIL
        // =====================================================================

        [Fact]
        public void An_empty_or_whitespace_text_note_is_blocking()
        {
            PlanimetryAuditResult empty = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), Text(70, 20, "")), Options());
            Assert.Equal("blocking", Assert.Single(Failed(empty, "text.empty")).Severity);

            PlanimetryAuditResult whitespace = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), Text(70, 20, "   ")), Options());
            Assert.Single(Failed(whitespace, "text.empty"));

            PlanimetryAuditResult real = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), Text(70, 20, "NOTA 1")), Options());
            Assert.Empty(Failed(real, "text.empty"));
            Assert.Equal("passed", Run(real, "text.empty").Status);
        }

        [Fact]
        public void A_degenerate_detail_curve_is_blocking_and_a_real_one_is_not()
        {
            AnnotationFact zero = Curve(80, 20, 0.0001);
            zero.Degenerate = true;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), zero, Curve(81, 20)), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "detail_2d.degenerate-curve"));
            Assert.Equal(80, f.ElementIds.Single());
        }

        [Fact]
        public void A_detail_element_whose_owner_view_is_gone_is_blocking()
        {
            AnnotationFact c = Curve(80, 999);
            c.OwnerViewExists = false;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20), c), Options());
            Assert.Single(Failed(r, "detail_2d.owner-view-missing"));
        }

        [Fact]
        public void Detail_geometry_that_would_not_read_is_unknown_and_examined_by_nothing_else()
        {
            AnnotationFact c = Curve(80, 20);
            c.GeometryReadable = false;
            c.Degenerate = null;
            c.Note("geometry", "GeometryCurve threw");
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20), c), Options());
            Assert.Single(Unknowns(r, "detail_2d.geometry-unreadable"));
            Assert.Empty(Failed(r, "detail_2d.degenerate-curve"));
        }

        // =====================================================================
        // CROP - only when demonstrable
        // =====================================================================

        [Fact]
        public void An_annotation_outside_an_ACTIVE_annotation_crop_is_blocking()
        {
            ViewFact v = View(20);
            v.CropBoxActive = true;
            v.AnnotationCropAvailable = true;
            v.AnnotationCropActive = true;
            v.AnnotationCrop = Box(0, 0, 100, 100);

            AnnotationFact t = Text(70, 20);
            t.Box = Box(500, 500, 560, 510);

            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(v, t), Options());
            PlanimetryFinding f = Assert.Single(Failed(r, "text.outside-annotation-crop"));
            Assert.Equal("view_plane", f.CoordinateSystem);
            Assert.Contains("stays silent rather than guessing", (string)f.Expected["note"]);
        }

        [Fact]
        public void Nothing_is_reported_when_the_annotation_crop_is_OFF()
        {
            ViewFact v = View(20);
            v.AnnotationCropActive = false;
            AnnotationFact t = Text(70, 20);
            t.Box = Box(500, 500, 560, 510);
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(v, t), Options());
            Assert.Empty(Failed(r, "text.outside-annotation-crop"));
        }

        [Fact]
        public void Nothing_is_reported_when_the_crop_shape_or_the_element_box_was_not_read()
        {
            ViewFact v = View(20);
            v.CropBoxActive = true;
            v.AnnotationCropActive = true;
            v.AnnotationCrop = PlanBox.Unreadable;
            AnnotationFact t = Text(70, 20);
            t.Box = Box(500, 500, 560, 510);
            Assert.Empty(Failed(PlanimetryRules.EvaluateUniversal(Snapshot(v, t), Options()),
                                "text.outside-annotation-crop"));

            v.AnnotationCrop = Box(0, 0, 100, 100);
            t.Box = PlanBox.Unreadable;
            t.BoundsReadable = false;
            t.Note("bounding_box", "no box");
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(v, t), Options());
            Assert.Empty(Failed(r, "text.outside-annotation-crop"));
            Assert.Single(Unknowns(r, "text.bounds-unreadable"));
        }

        [Fact]
        public void Detail_uses_the_MODEL_crop_and_annotations_use_the_ANNOTATION_crop()
        {
            ViewFact v = View(20);
            v.CropBoxActive = true;
            v.CropBox = Box(0, 0, 100, 100);
            v.AnnotationCropAvailable = true;
            v.AnnotationCropActive = false;

            AnnotationFact detail = Curve(80, 20);
            detail.Box = Box(500, 500, 600, 501);
            AnnotationFact text = Text(70, 20);
            text.Box = Box(500, 500, 560, 510);

            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(v, detail, text), Options());
            Assert.Single(Failed(r, "detail_2d.outside-crop"));
            Assert.Empty(Failed(r, "text.outside-annotation-crop"));
        }

        // =====================================================================
        // REFERENCES
        // =====================================================================

        [Fact]
        public void A_reference_whose_target_view_is_gone_is_blocking()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), Reference(90, 20, "missing", 999)), Options());
            Assert.Equal("blocking", Assert.Single(Failed(r, "reference.target-missing")).Severity);
        }

        [Fact]
        public void A_reference_the_API_cannot_resolve_is_unknown_and_never_guessed()
        {
            ReferenceFact f = Reference(90, 20, "unknown", null);
            f.TargetStateReason = "no parameter on this marker resolves to a view";
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(View(20), f), Options());
            PlanimetryFinding finding = Assert.Single(Unknowns(r, "reference.target-unidentifiable"));
            Assert.Contains("Never inferred from a name", (string)finding.Observed["note"]);
            Assert.Equal("unknown", Run(r, "reference.target-unidentifiable").Status);
        }

        [Fact]
        public void A_referenced_view_that_is_on_no_sheet_is_ADVISORY()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), Reference(90, 20, "resolved", 42, placed: false)), Options());
            Assert.Equal("advisory", Assert.Single(Failed(r, "reference.target-not-placed")).Severity);
        }

        [Fact]
        public void A_resolved_and_placed_reference_produces_no_finding()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(View(20), Reference(90, 20)), Options());
            Assert.DoesNotContain(r.Findings, f => f.RuleId.StartsWith("reference."));
            Assert.Equal("passed", Run(r, "reference.target-missing").Status);
        }

        // =====================================================================
        // CHECK STATUS, ADVISORY FILTER, PASSED CHECKS
        // =====================================================================

        [Fact]
        public void A_check_with_no_population_is_not_applicable_and_never_passed()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(), Options());
            foreach (PlanimetryCheckRun c in r.Checks)
            {
                Assert.Equal(0, c.Population);
                Assert.Equal("not_applicable", c.Status);
            }
            Assert.Empty(r.Findings);
        }

        [Fact]
        public void Every_catalog_entry_is_reported_as_a_check_exactly_once()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(Snapshot(Sheet(10)), Options());
            Assert.Equal(PlanimetryRules.Catalog.Length, r.Checks.Count);
            Assert.Equal(PlanimetryRules.Catalog.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal),
                         r.Checks.Select(c => c.RuleId));
        }

        [Fact]
        public void Include_advisory_false_removes_advisories_from_findings_and_from_the_tally()
        {
            PlanimetrySnapshot snap = Snapshot(View(20));   // an unplaced, templated view
            PlanimetryAuditResult with = PlanimetryRules.EvaluateUniversal(snap, Options(advisory: true));
            Assert.Single(Failed(with, "view.not-placed"));

            PlanimetryAuditResult without = PlanimetryRules.EvaluateUniversal(snap, Options(advisory: false));
            Assert.Empty(Failed(without, "view.not-placed"));
            Assert.Equal("passed", Run(without, "view.not-placed").Status);
            Assert.DoesNotContain(without.Findings, f => f.Severity == "advisory");
        }

        [Fact]
        public void Include_passed_checks_adds_one_entry_per_passing_check_and_none_for_empty_ones()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10)), Options(passed: true));
            List<PlanimetryFinding> passed = r.Findings.Where(f => f.Status == "passed").ToList();
            Assert.NotEmpty(passed);
            Assert.All(passed, f => Assert.Equal(PlanimetryRules.UniversalId, f.RequirementSetId));
            foreach (PlanimetryFinding f in passed)
                Assert.Equal("passed", Run(r, f.RuleId).Status);
            // No passed entry for a check that examined nothing.
            Assert.DoesNotContain(passed, f => Run(r, f.RuleId).Population == 0);
        }

        [Fact]
        public void A_check_that_produced_unknowns_is_reported_unknown_and_never_passed()
        {
            PlacementFact broken = Viewport(31, 10, 21, 0, 0, 1, 1);
            broken.Box = PlanBox.Unreadable;
            broken.BoundsReadable = false;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10), View(21), broken), Options(passed: true));
            Assert.Equal("unknown", Run(r, "placement.bounds-unreadable").Status);
            Assert.DoesNotContain(r.Findings,
                f => f.RuleId == "placement.bounds-unreadable" && f.Status == "passed");
        }

        // =====================================================================
        // ORDER AND DETERMINISM
        // =====================================================================

        [Fact]
        public void Findings_are_ordered_by_severity_then_rule_then_sheet_then_view_then_element()
        {
            PlanimetrySnapshot snap = Snapshot(
                Sheet(10, "A-201", titleblocks: 0),
                Sheet(11, "A-101", titleblocks: 0),
                View(20), View(21),
                Viewport(30, 10, 20, 0, 0, 100, 100),
                Viewport(31, 10, 21, 50, 50, 200, 200));
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(snap, Options());

            List<string> severities = r.Findings.Select(f => f.Severity).ToList();
            Assert.Equal(severities.OrderBy(SeverityRank).ToList(), severities);

            List<PlanimetryFinding> noTitleblock =
                r.Findings.Where(f => f.RuleId == "sheet.no-titleblock").ToList();
            Assert.Equal(2, noTitleblock.Count);
            Assert.Equal("A-101", noTitleblock[0].SheetNumber);
            Assert.Equal("A-201", noTitleblock[1].SheetNumber);
        }

        private static int SeverityRank(string s)
        {
            return s == "blocking" ? 0 : s == "advisory" ? 1 : 2;
        }

        [Fact]
        public void Two_evaluations_of_one_snapshot_produce_the_same_list_and_the_same_signatures()
        {
            PlanimetrySnapshot snap = Snapshot(
                Sheet(10, "A-201", titleblocks: 0), View(20), View(21),
                Viewport(30, 10, 20, 0, 0, 100, 100),
                Viewport(31, 10, 21, 50, 50, 200, 200),
                Dimension(50, 20), Tag(60, 20, 100, 7), Tag(61, 20, 100, 7),
                Text(70, 20, ""), Curve(80, 20), Reference(90, 20));

            PlanimetryAuditResult a = PlanimetryRules.EvaluateUniversal(snap, Options());
            PlanimetryAuditResult b = PlanimetryRules.EvaluateUniversal(snap, Options());

            Assert.Equal(a.Findings.Count, b.Findings.Count);
            Assert.Equal(a.Findings.Select(f => f.Signature()), b.Findings.Select(f => f.Signature()));
            Assert.Equal(a.Findings.Select(f => f.ToJson().ToString(Newtonsoft.Json.Formatting.None)),
                         b.Findings.Select(f => f.ToJson().ToString(Newtonsoft.Json.Formatting.None)));
        }

        [Fact]
        public void The_pairwise_overlap_order_does_not_depend_on_the_order_the_placements_arrived()
        {
            PlacementFact a = Viewport(30, 10, 20, 0, 0, 100, 100);
            PlacementFact b = Viewport(31, 10, 21, 50, 50, 200, 200);
            PlanimetryAuditResult forward = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10), View(20), View(21), a, b), Options());
            PlanimetryAuditResult backward = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10), View(21), View(20), b, a), Options());
            Assert.Equal(Failed(forward, "sheet.viewport-overlap").Single().ElementIds,
                         Failed(backward, "sheet.viewport-overlap").Single().ElementIds);
        }

        [Fact]
        public void No_finding_and_no_check_carries_a_score()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10, "A-201", titleblocks: 0), View(20)), Options(passed: true));
            string json = new JArray(r.Findings.Select(f => (JToken)f.ToJson()))
                .ToString(Newtonsoft.Json.Formatting.None) +
                new JArray(r.Checks.Select(c => (JToken)c.ToJson())).ToString(Newtonsoft.Json.Formatting.None);
            foreach (string forbidden in new[] { "\"score\"", "\"health\"", "\"grade\"", "\"percent\"", "out of 100" })
                Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Every_finding_cites_the_set_and_version_that_produced_it()
        {
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(
                Snapshot(Sheet(10, "A-201", titleblocks: 0)), Options());
            Assert.All(r.Findings, f =>
            {
                Assert.Equal(PlanimetryRules.UniversalId, f.RequirementSetId);
                Assert.Equal(PlanimetryRules.UniversalVersion, f.RequirementSetVersion);
            });
        }

        // =====================================================================
        // COVERAGE
        // =====================================================================

        [Fact]
        public void A_snapshot_with_a_failed_pass_or_a_closed_workset_is_not_complete_coverage()
        {
            var snap = Snapshot(Sheet(10));
            Assert.True(snap.CoverageComplete);
            Assert.Null(snap.CoverageNote());

            snap.ChecksFailed.Add(new PlanimetryCheckFailure { Check = "tags", Error = "collector threw" });
            Assert.False(snap.CoverageComplete);
            Assert.Contains("not a pass", snap.CoverageNote());

            var closed = Snapshot(Sheet(10));
            closed.VisibilityCoverageComplete = false;
            Assert.False(closed.CoverageComplete);
            Assert.Contains("closed worksets", closed.CoverageNote());

            var unloaded = Snapshot(Sheet(10));
            unloaded.LinkCoverageComplete = false;
            Assert.False(unloaded.CoverageComplete);
            Assert.Contains("link", unloaded.CoverageNote());
        }

        [Fact]
        public void An_unreadable_field_on_a_row_counts_towards_the_unreadable_total()
        {
            SheetFact s = Sheet(10);
            s.Note("name", "Name threw");
            var snap = Snapshot(s);
            Assert.Equal(1, snap.UnreadableTotal);
            Assert.False(snap.CoverageComplete);
        }

        [Fact]
        public void A_not_applicable_field_is_recorded_but_never_degrades_coverage()
        {
            // Discipline on a schedule, CanBePrinted on a template: "this kind of view
            // does not have the property" is a fact about the view, not a failed read.
            // Folding it into unreadable would make coverage_complete false on every
            // model that contains a schedule - a lie in the cautious direction, which
            // is still a lie.
            ViewFact v = View(20, "Door schedule", "Schedule");
            v.NoteNotApplicable("discipline", "this view type does not support Discipline");
            v.NoteNotApplicable("crop_box_active", "schedules have no crop");
            var snap = Snapshot(v);
            Assert.Equal(0, snap.UnreadableTotal);
            Assert.True(snap.CoverageComplete);
            Assert.False(v.HasUnreadableField);
            // The notes are still VISIBLE - recorded with their own state, not dropped.
            Assert.Equal(2, v.Notes.Count);
            Assert.All(v.Notes, n => Assert.Equal(Read.NotApplicable, n.State));
            Assert.Contains("not_applicable", v.ToJson(304.8)["unreadable_fields"].ToString());
        }

        [Fact]
        public void Findings_report_the_runs_coverage_so_a_reader_can_see_it_on_every_row()
        {
            var snap = Snapshot(Sheet(10, "A-201", titleblocks: 0));
            snap.VisibilityCoverageComplete = false;
            PlanimetryAuditResult r = PlanimetryRules.EvaluateUniversal(snap, Options());
            Assert.All(r.Findings, f => Assert.False(f.CoverageComplete));
        }

        // =====================================================================
        // THE CONFIGURABLE PASS
        // =====================================================================

        private static PlanimetryRequirementSet Set(string rules)
        {
            return PlanimetryRequirementSet.Load(JObject.Parse(
                "{\"requirement_set\":{\"id\":\"acme\",\"version\":\"2.0.0\"},\"rules\":" + rules + "}"));
        }

        [Fact]
        public void A_naming_rule_finds_the_sheet_whose_number_is_wrong()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"sheet-number\",\"entity\":\"sheet\",\"severity\":\"blocking\"," +
                "\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"field\":\"sheet_number\",\"operator\":\"matches\",\"value\":\"^A-[0-9]{3}$\"}}]");

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(Sheet(10, "A-201"), Sheet(11, "PLANO 1")), set, Options());
            PlanimetryRules.Attribute(r, set);

            PlanimetryFinding f = Assert.Single(r.Findings, x => x.Status == "failed");
            Assert.Equal(11, f.ElementIds.Single());
            Assert.Equal("blocking", f.Severity);
            Assert.Equal("acme", f.RequirementSetId);
            Assert.Equal("2.0.0", f.RequirementSetVersion);
            Assert.Equal(set.Sha256, f.RequirementSetSha256);
            Assert.Equal("PLANO 1", (string)f.Observed["value"]);
            Assert.Equal("matches", (string)f.Expected["operator"]);
        }

        [Fact]
        public void A_selector_narrows_which_elements_the_rule_examines()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"a-sheets\",\"entity\":\"sheet\",\"selector\":{\"sheet_number_matches\":\"^A-\"}," +
                "\"assertion\":{\"field\":\"name\",\"operator\":\"matches\",\"value\":\"^PLANTA\"}}]");
            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(Sheet(10, "A-201"), Sheet(11, "E-101")), set, Options());
            Assert.Equal(1, r.Checks.Single().Population);
            Assert.Equal(10, r.Findings.Single(f => f.Status == "failed").ElementIds.Single());
        }

        [Fact]
        public void An_allowed_template_rule_makes_a_missing_template_blocking()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"templates\",\"entity\":\"view\",\"severity\":\"blocking\"," +
                "\"selector\":{\"view_type\":\"FloorPlan\"}," +
                "\"assertion\":{\"operator\":\"allowed_template\",\"value\":[\"ARQ-PLANTA\"]}}]");

            ViewFact good = View(20);
            ViewFact wrong = View(21);
            wrong.TemplateName = "ESTRUCTURA";
            ViewFact none = View(22);
            none.TemplateId = null;
            none.TemplateName = null;

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(good, wrong, none), set, Options());
            List<PlanimetryFinding> failed = r.Findings.Where(f => f.Status == "failed").ToList();
            Assert.Equal(2, failed.Count);
            Assert.Equal(new long[] { 21, 22 }, failed.Select(f => f.ElementIds.Single()).OrderBy(x => x).ToArray());
            Assert.All(failed, f => Assert.Equal("blocking", f.Severity));
            Assert.Contains("null is not in the list",
                (string)failed.First(f => f.ElementIds.Single() == 22).Expected["note"]);
        }

        [Fact]
        public void An_allowed_scale_rule_measures_the_scale_and_an_unreadable_one_is_unknown()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"scales\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"allowed_scale\",\"value\":[50,100]}}]");

            ViewFact ok = View(20);
            ViewFact wrong = View(21); wrong.Scale = 75;
            ViewFact unreadable = View(22); unreadable.Scale = null; unreadable.Note("scale", "Scale threw");

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(ok, wrong, unreadable), set, Options());
            Assert.Equal(21, r.Findings.Single(f => f.Status == "failed").ElementIds.Single());
            Assert.Equal(22, r.Findings.Single(f => f.Status == "unknown").ElementIds.Single());
            Assert.Equal("failed", r.Checks.Single().Status);
        }

        [Fact]
        public void Forbid_numeric_override_turns_the_advisory_into_a_blocking_finding()
        {
            AnnotationFact d = Dimension(50, 20);
            d.HasValueOverride = true;
            d.ValueOverrides.Add("2.40");

            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"no-overrides\",\"entity\":\"dimension\",\"severity\":\"blocking\"," +
                "\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"forbid_numeric_override\"}}]");

            PlanimetrySnapshot snap = Snapshot(View(20), d);
            Assert.Equal("advisory",
                Failed(PlanimetryRules.EvaluateUniversal(snap, Options()), "dimension.value-override")
                    .Single().Severity);
            Assert.Equal("blocking",
                PlanimetryRules.EvaluateRequirementSet(snap, set, Options())
                    .Findings.Single(f => f.Status == "failed").Severity);
        }

        [Fact]
        public void A_minimum_gap_rule_reports_each_pair_once_and_names_the_measured_gap()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"gap\",\"entity\":\"viewport\",\"severity\":\"blocking\"," +
                "\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"minimum_gap\",\"value\":20}}]");

            PlanimetryAuditResult tooClose = PlanimetryRules.EvaluateRequirementSet(Snapshot(
                Sheet(10), View(20), View(21),
                Viewport(30, 10, 20, 0, 0, 100, 100),
                Viewport(31, 10, 21, 110, 0, 200, 100)), set, Options());
            PlanimetryFinding f = Assert.Single(tooClose.Findings, x => x.Status == "failed");
            Assert.Equal(new long[] { 30, 31 }, f.ElementIds.ToArray());
            Assert.Equal(10.0, (double)f.Observed["gap"], 3);
            Assert.Equal(20, (int)f.Expected["minimum_gap"]);

            PlanimetryAuditResult clear = PlanimetryRules.EvaluateRequirementSet(Snapshot(
                Sheet(10), View(20), View(21),
                Viewport(30, 10, 20, 0, 0, 100, 100),
                Viewport(31, 10, 21, 140, 0, 200, 100)), set, Options());
            Assert.DoesNotContain(clear.Findings, x => x.Status == "failed");
        }

        [Fact]
        public void An_inside_extent_rule_shrinks_the_sheet_by_the_margin()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"margin\",\"entity\":\"viewport\",\"severity\":\"blocking\"," +
                "\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"inside_extent\",\"value\":10}}]");

            PlanimetryAuditResult tight = PlanimetryRules.EvaluateRequirementSet(Snapshot(
                Sheet(10), View(20), Viewport(30, 10, 20, 5, 5, 100, 100)), set, Options());
            Assert.Single(tight.Findings, f => f.Status == "failed");

            PlanimetryAuditResult ok = PlanimetryRules.EvaluateRequirementSet(Snapshot(
                Sheet(10), View(20), Viewport(30, 10, 20, 20, 20, 100, 100)), set, Options());
            Assert.DoesNotContain(ok.Findings, f => f.Status == "failed");
        }

        [Fact]
        public void A_required_parameter_rule_reports_each_missing_parameter_by_name()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"sheet-data\",\"entity\":\"sheet\",\"severity\":\"blocking\"," +
                "\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"required_parameter\",\"value\":[\"Drawn By\",\"Checked By\"]}}]");

            SheetFact s = Sheet(10);
            s.Parameters["Drawn By"] = "PZ";
            s.Parameters["Checked By"] = "   ";

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(Snapshot(s), set, Options());
            PlanimetryFinding f = Assert.Single(r.Findings, x => x.Status == "failed");
            Assert.Equal("Checked By", (string)f.Observed["parameter"]);
            Assert.True((bool)f.Observed["present"]);
        }

        [Fact]
        public void A_parameter_selector_and_assertion_read_the_projected_parameters()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"phase\",\"entity\":\"sheet\",\"selector\":{\"parameter:Estado\":\"EMITIDO\"}," +
                "\"assertion\":{\"field\":\"parameter:Revision\",\"operator\":\"not_empty\"}}]");

            SheetFact issued = Sheet(10, "A-201");
            issued.Parameters["Estado"] = "EMITIDO";
            issued.Parameters["Revision"] = JValue.CreateNull();
            SheetFact draft = Sheet(11, "A-202");
            draft.Parameters["Estado"] = "BORRADOR";
            draft.Parameters["Revision"] = JValue.CreateNull();

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(issued, draft), set, Options());
            Assert.Equal(1, r.Checks.Single().Population);
            Assert.Equal(10, r.Findings.Single(f => f.Status == "failed").ElementIds.Single());
        }

        [Fact]
        public void A_field_that_could_not_be_read_makes_the_element_unknown_rather_than_excluding_it()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"titles\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"field\":\"name\",\"operator\":\"not_empty\"}}]");

            SheetFact unreadable = Sheet(10);
            unreadable.Name = null;
            unreadable.Note("name", "Name threw");

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(unreadable), set, Options());
            PlanimetryFinding f = Assert.Single(r.Findings, x => x.Status == "unknown");
            Assert.Equal("unknown", f.Severity);
            Assert.Equal("unknown", r.Checks.Single().Status);
            Assert.Contains("Unknown is not a pass", (string)f.Expected["note"]);
        }

        [Fact]
        public void An_unreadable_SELECTOR_field_is_unknown_and_the_element_is_not_asserted_over()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"typed\",\"entity\":\"view\",\"selector\":{\"scale\":50}," +
                "\"assertion\":{\"field\":\"name\",\"operator\":\"matches\",\"value\":\"^NOTHING$\"}}]");

            ViewFact unreadable = View(20);
            unreadable.Scale = null;
            unreadable.Note("scale", "Scale threw");

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(unreadable), set, Options());
            Assert.Single(r.Findings, f => f.Status == "unknown");
            Assert.DoesNotContain(r.Findings, f => f.Status == "failed");
            Assert.Equal(0, r.Checks.Single().Population);
            Assert.Equal("unknown", r.Checks.Single().Status);
        }

        // ---- requires_tag --------------------------------------------------------

        private static ViewFact ViewWithCoverage(long id, TagCoverageFact coverage)
        {
            ViewFact v = View(id);
            v.SheetIds.Add(10);
            v.ViewportIds.Add(30);
            v.TagCoverage = new List<TagCoverageFact> { coverage };
            return v;
        }

        [Fact]
        public void A_requires_tag_rule_names_the_exact_untagged_element()
        {
            var coverage = new TagCoverageFact
            { Category = "OST_Doors", VisibleTotal = 3, TaggedTotal = 2, UntaggedTotal = 1 };
            coverage.Untagged.Add(new UntaggedElement
            { Id = 555, Category = "OST_Doors", TypeName = "P-02", FamilyName = "Puerta" });

            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"door-tags\",\"entity\":\"view\",\"severity\":\"blocking\"," +
                "\"selector\":{\"view_type\":\"FloorPlan\"}," +
                "\"assertion\":{\"operator\":\"requires_tag\",\"value\":[\"OST_Doors\"]}}]");

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(ViewWithCoverage(20, coverage)), set, Options());
            PlanimetryFinding f = Assert.Single(r.Findings, x => x.Status == "failed");
            Assert.Equal(555, f.ElementIds.Single());
            Assert.Equal(20, f.ViewId);
            Assert.Equal("P-02", (string)f.Observed["type"]);
            Assert.Equal(3, (int)f.Observed["visible_total"]);
        }

        [Fact]
        public void Exclusions_remove_elements_from_the_untagged_list()
        {
            var coverage = new TagCoverageFact
            { Category = "OST_Doors", VisibleTotal = 4, TaggedTotal = 0, UntaggedTotal = 4 };
            coverage.Untagged.Add(new UntaggedElement { Id = 1, Category = "OST_Doors", TypeName = "P-01" });
            coverage.Untagged.Add(new UntaggedElement { Id = 2, Category = "OST_Doors", TypeName = "TMP-A" });
            coverage.Untagged.Add(new UntaggedElement { Id = 3, Category = "OST_Doors", TypeName = "P-09", FamilyName = "Hueco" });
            var excludedByParameter = new UntaggedElement { Id = 4, Category = "OST_Doors", TypeName = "P-10" };
            excludedByParameter.ExclusionParameters["NO_TAG"] = "1";
            coverage.Untagged.Add(excludedByParameter);
            coverage.Untagged.Add(new UntaggedElement { Id = 5, Category = "OST_Doors", TypeName = "P-11" });

            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"door-tags\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"requires_tag\",\"value\":[{\"category\":\"OST_Doors\"," +
                "\"exclude_types\":[\"P-01\"],\"exclude_type_matches\":\"^TMP-\"," +
                "\"exclude_families\":[\"Hueco\"],\"exclude_when_parameter_set\":\"NO_TAG\"}]}}]");

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(ViewWithCoverage(20, coverage)), set, Options());
            List<long> reported = r.Findings.Where(f => f.Status == "failed")
                                            .Select(f => f.ElementIds.Single()).ToList();
            Assert.Equal(new long[] { 5 }, reported.ToArray());
        }

        [Fact]
        public void Incomplete_tag_coverage_adds_an_unknown_and_the_findings_are_a_lower_bound()
        {
            var coverage = new TagCoverageFact
            {
                Category = "OST_Doors",
                VisibleTotal = 5000,
                TaggedTotal = 0,
                UntaggedTotal = 5000,
                Complete = false,
                IncompleteReason = "more than 2000 untagged elements are visible"
            };
            coverage.Untagged.Add(new UntaggedElement { Id = 1, Category = "OST_Doors" });

            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"door-tags\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"requires_tag\",\"value\":[\"OST_Doors\"]}}]");

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(ViewWithCoverage(20, coverage)), set, Options());
            Assert.Single(r.Findings, f => f.Status == "failed");
            PlanimetryFinding unknown = Assert.Single(r.Findings, f => f.Status == "unknown");
            Assert.Contains("LOWER BOUND", (string)unknown.Observed["note"]);
            Assert.Equal("failed", r.Checks.Single().Status);
        }

        [Fact]
        public void A_view_whose_visible_set_was_never_gathered_is_unknown_and_never_clean()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"door-tags\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"requires_tag\",\"value\":[\"OST_Doors\"]}}]");

            ViewFact v = View(20);
            v.SheetIds.Add(10);
            v.TagCoverage = null;   // nobody asked, nobody looked

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(Snapshot(v), set, Options());
            PlanimetryFinding f = Assert.Single(r.Findings, x => x.Status == "unknown");
            Assert.Contains("was not gathered", (string)f.Observed["reason"]);
            Assert.Equal("unknown", r.Checks.Single().Status);
        }

        [Fact]
        public void Linked_visible_elements_are_reported_as_unknown_and_never_blamed_on_this_model()
        {
            var coverage = new TagCoverageFact
            {
                Category = "OST_Doors",
                VisibleTotal = 0,
                TaggedTotal = 0,
                UntaggedTotal = 0,
                LinkedVisibleTotal = 12
            };
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"door-tags\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"requires_tag\",\"value\":[\"OST_Doors\"]}}]");

            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(ViewWithCoverage(20, coverage)), set, Options());
            Assert.DoesNotContain(r.Findings, f => f.Status == "failed");
            PlanimetryFinding f = Assert.Single(r.Findings, x => x.Status == "unknown");
            Assert.Equal(12, (int)f.Observed["linked_element_total"]);
            Assert.Contains("not this model's to tag", (string)f.Observed["reason"]);
        }

        [Fact]
        public void Configurable_findings_are_deterministic_across_runs()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"sheet-number\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"field\":\"sheet_number\",\"operator\":\"matches\",\"value\":\"^A-\"}}," +
                "{\"id\":\"gap\",\"entity\":\"viewport\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"minimum_gap\",\"value\":50}}]");
            PlanimetrySnapshot snap = Snapshot(
                Sheet(10, "E-101"), Sheet(11, "A-201"), View(20), View(21),
                Viewport(30, 10, 20, 0, 0, 100, 100),
                Viewport(31, 10, 21, 120, 0, 200, 100));

            PlanimetryAuditResult a = PlanimetryRules.EvaluateRequirementSet(snap, set, Options());
            PlanimetryAuditResult b = PlanimetryRules.EvaluateRequirementSet(snap, set, Options());
            Assert.Equal(a.Findings.Select(f => f.Signature()), b.Findings.Select(f => f.Signature()));
        }
    }
}
