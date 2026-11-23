using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Commands;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DocumentationRegressionTests
    {
        [Fact]
        public void Tag_search_reaches_a_narrow_crop_inside_the_approved_radius()
        {
            var tag = PlanBox.FromCorners(0, 0, 1, 1);
            var crop = PlanBox.FromCorners(2.1, 0, 3.2, 1.1);
            var obstacle = PlanBox.FromCorners(0, 0, 2, 1);
            var offsets = DeliveryLayoutRules.TagOffsets(tag, .1, 2.15, crop).ToList();
            Assert.Contains(offsets, p => DeliveryLayoutRules.Clear(
                DeliveryLayoutRules.Shift(tag, p.X, p.Y), new[] { obstacle }, .1, crop));
            Assert.All(offsets, p => Assert.True(p.X * p.X + p.Y * p.Y <= 2.15 * 2.15 + 1e-6));
            Assert.Equal(offsets.Count, offsets.Distinct().Count());
            var chosen = DeliveryLayoutRules.FindTagOffset(tag, new[] { obstacle }, .1, 2.15, crop,
                new AnnotationBudget(), (x, y) => DeliveryLayoutRules.Clear(
                    DeliveryLayoutRules.Shift(tag, x, y), new[] { obstacle }, .1, crop));
            Assert.NotNull(chosen);
        }

        [Fact]
        public void Tag_search_can_reach_beyond_forty_small_steps()
        {
            var tag = PlanBox.FromCorners(0, 0, .1, .1);
            var crop = PlanBox.FromCorners(5, 0, 5.2, .2);
            Assert.Contains(DeliveryLayoutRules.TagOffsets(tag, .05, 5.1, crop),
                p => DeliveryLayoutRules.Clear(DeliveryLayoutRules.Shift(tag, p.X, p.Y), Array.Empty<PlanBox>(), .05, crop));
            var chosen = DeliveryLayoutRules.FindTagOffset(tag, Array.Empty<PlanBox>(), .05, 5.1, crop,
                new AnnotationBudget(), (_, _) => true);
            Assert.True(chosen.HasValue);
            Assert.InRange(chosen.Value.X, 5, 5.1);
        }

        [Fact]
        public void Blocked_seed_with_many_distant_obstacles_only_reaches_bounded_native_probes()
        {
            var tag = PlanBox.FromCorners(0, 0, .1, .1);
            var distant = Enumerable.Range(100, 50000).Select(x => PlanBox.FromCorners(x, 0, x + 1, 1));
            var obstacles = distant.Append(PlanBox.FromCorners(-.2, -.2, .3, .3)).ToArray();
            int probes = 0;
            var budget = new AnnotationBudget(() => 0, limitMs: 5000);
            var fallback = new DeliveryLayoutRules.TagFallback();
            var chosen = DeliveryLayoutRules.FindTagOffset(tag, obstacles, .05, 5, null, budget, (x, y) =>
            {
                probes++;
                var native = x >= 4 && Math.Abs(y) < .1 ? PlanBox.FromCorners(.25, 0, .35, .1) : tag;
                return fallback.Measure((x, y), native, obstacles, .05, 5, null, budget);
            }, fallback: fallback);

            budget.Check();
            Assert.Equal(DeliveryLayoutRules.MaxTagNativeProbes, probes);
            Assert.True(fallback.Used);
            Assert.Equal((4.375, 0.0), chosen.Value);
        }

        [Fact]
        public void Linked_linear_dimension_uses_its_inactive_view_unless_the_caller_opts_out()
        {
            Assert.Equal(20L, DimensionPlanRules.TemporaryLinkedDimensionView(false, 10, new long[] { 20, 20 }));
            Assert.Null(DimensionPlanRules.TemporaryLinkedDimensionView(true, 10, new long[] { 20 }));
            Assert.Null(DimensionPlanRules.TemporaryLinkedDimensionView(false, 20, new long[] { 20 }));
            Assert.Null(DimensionPlanRules.TemporaryLinkedDimensionView(false, 10, Array.Empty<long>()));
            Assert.Null(DimensionPlanRules.TemporaryLinkedDimensionView(false, 10, new long[] { 20, 30 }));
        }

        [Fact]
        public void Family_load_rejection_names_keep_active_view()
        {
            var result = new AnnotateCommand().Execute(null, "{\"load_tag_family\":\"tag.rfa\",\"keep_active_view\":false}");
            Assert.False(result.Success);
            Assert.Contains("keep_active_view", result.Error);
        }

        [Fact]
        public void A_failed_immediate_view_restore_requests_an_asynchronous_change_without_confirming_it()
        {
            int requests = 0;
            JObject evidence = DimensionPlanRules.RestoreTemporaryView(10, 20, true,
                () => false, () => throw new InvalidOperationException("view change unavailable"), () => requests++);
            Assert.Equal(1, requests);
            Assert.False(evidence.Value<bool>("restored"));
            Assert.True(evidence.Value<bool>("restore_requested"));
            Assert.Equal("view change unavailable", evidence.Value<string>("reason"));
        }

        [Fact]
        public void A_measured_view_restore_does_not_queue_another_change()
        {
            long active = 20;
            JObject evidence = DimensionPlanRules.RestoreTemporaryView(10, 20, true,
                () => active == 10, () => active = 10, () => throw new InvalidOperationException("unexpected queue"));
            Assert.True(evidence.Value<bool>("restored"));
            Assert.Null(evidence["restore_requested"]);
        }

        [Fact]
        public void Annotation_response_reports_unconfirmed_restoration_even_when_annotation_failed()
        {
            var annotation = CommandResult.FailCoded(ErrorCodes.EntradaInvalida, "The dimension failed.",
                extra: new JObject { ["write_started"] = false });
            JObject restoration = DimensionPlanRules.RestoreTemporaryView(10, 20, true,
                () => false, () => throw new InvalidOperationException("view change unavailable"), () => { });

            CommandResult result = AnnotateCommand.WithTemporaryViewRestoration(annotation, restoration);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.VerificacionFallida, result.Detail.Value<string>("error_code"));
            Assert.Equal("The dimension failed.", result.Detail.Value<string>("annotation_error"));
            Assert.Equal(ErrorCodes.EntradaInvalida, result.Detail.Value<string>("annotation_error_code"));
            Assert.False(result.Detail.Value<bool>("write_started"));
            Assert.True(JToken.DeepEquals(restoration, result.Detail["temporary_view"]));
        }

        [Fact]
        public void Annotation_rehearsal_response_withholds_token_when_restoration_is_unconfirmed()
        {
            var annotation = CommandResult.Ok(new JObject
            {
                ["confirmation_token"] = "rehearsal-token",
                ["dry_run"] = true
            });
            JObject restoration = DimensionPlanRules.RestoreTemporaryView(10, 20, true,
                () => false, () => throw new InvalidOperationException("view change unavailable"), () => { });

            CommandResult result = AnnotateCommand.WithTemporaryViewRestoration(annotation, restoration);

            Assert.False(result.Success);
            Assert.Equal(ErrorCodes.VerificacionFallida, result.Detail.Value<string>("error_code"));
            Assert.Null(result.Detail["confirmation_token"]);
            Assert.True(result.Detail.Value<bool>("dry_run"));
            Assert.True(JToken.DeepEquals(restoration, result.Detail["temporary_view"]));
        }

        [Fact]
        public void Restored_tag_fallback_requires_the_measured_head_and_extent()
        {
            var initial = PlanBox.FromCorners(0, 0, 1, 1);
            var best = PlanBox.FromCorners(1, 0, 2, 1);
            var obstacles = new[] { PlanBox.FromCorners(-1, -1, 1.5, 2) };
            var fallback = new DeliveryLayoutRules.TagFallback { RestoreBest = true };
            Assert.False(fallback.Measure((0, 0), initial, obstacles, .1, 1, null, null));
            Assert.False(fallback.Measure((1, 0), best, obstacles, .1, 1, null, null));
            Assert.True(fallback.VerifyRestored((1, 0), best));
            Assert.False(fallback.VerifyRestored((1, .02), best));
            Assert.False(fallback.VerifyRestored((1, 0), initial));
        }

        [Fact]
        public void Complete_solid_miss_reads_linked_strokes_lazily_but_partial_and_solid_hits_do_not()
        {
            var arrow = new CadPoint(6, 6, 0);
            int reads = 0;
            (string Document, long ElementId, long InstanceId)? ReadStrokes()
            {
                reads++;
                var strokes = new PlanStrokeIndex<string>();
                strokes.Add("link", 4, 10, true, new CadPoint(4, 6, 20), new CadPoint(8, 6, 20));
                return strokes.UniqueLinkedStroke(arrow, out _);
            }

            Assert.Null(PlanLeaderRules.WithLinkedStrokeFallback(null, false, false, ReadStrokes));
            Assert.Equal(("solid", 19L, -1L), PlanLeaderRules.WithLinkedStrokeFallback(("solid", 19L, -1L), true, true, ReadStrokes).Value);
            Assert.Equal(0, reads);
            Assert.Equal(("link", 4L, 10L), PlanLeaderRules.WithLinkedStrokeFallback(null, true, false, ReadStrokes).Value);
            Assert.Equal(1, reads);
        }

        [Fact]
        public void A_line_only_target_requires_one_attributable_linked_owner_at_the_arrow()
        {
            var strokes = new PlanStrokeIndex<string>();
            var a = new CadPoint(4, 6, 20);
            var b = new CadPoint(8, 6, 20);
            var arrow = new CadPoint(6, 6, 0);
            strokes.Add("link", 4, 10, true, a, b);
            strokes.Add("link", 4, 11, true, new CadPoint(4, 9, 20), new CadPoint(8, 9, 20));
            Assert.Equal(("link", 4L, 10L), strokes.UniqueLinkedStroke(arrow, out bool ambiguous).Value);
            Assert.False(ambiguous);
            strokes.Add("host", 7, -1, true, a, b);
            Assert.Null(strokes.UniqueLinkedStroke(arrow, out ambiguous));
            Assert.True(ambiguous);
        }

        [Fact]
        public void Tag_search_stops_after_a_native_probe_spends_the_remaining_budget()
        {
            long elapsed = 0;
            var budget = new AnnotationBudget(() => elapsed, 10);
            var tag = PlanBox.FromCorners(0, 0, 1, 1);
            var crop = PlanBox.FromCorners(2.1, 0, 3.2, 1.1);
            int probes = 0;
            var error = Assert.Throws<AnnotationBudgetExceeded>(() =>
                DeliveryLayoutRules.FindTagOffset(tag, Array.Empty<PlanBox>(), .1, 2.15, crop, budget, (_, _) =>
                {
                    probes++;
                    elapsed = 10;
                    return true;
                }));
            var detail = new JObject();
            error.AddDetail(detail);
            Assert.Equal(1, probes);
            Assert.Equal(ErrorCodes.AnnotationBudgetExhausted, detail.Value<string>("error_code"));
        }

        [Fact]
        public void Tag_search_spends_some_native_probes_farther_out_before_exhausting_the_cap()
        {
            var tag = PlanBox.FromCorners(0, 0, .1, .1);
            int probes = 0;
            var chosen = DeliveryLayoutRules.FindTagOffset(tag, new[] { tag }, .05, 5, null,
                new AnnotationBudget(() => 0), (x, y) =>
                {
                    probes++;
                    return x >= 4 && Math.Abs(y) < .1;
                });

            Assert.True(chosen.HasValue);
            Assert.InRange(chosen.Value.X, 4.0, 5.0);
            Assert.InRange(probes, 9, DeliveryLayoutRules.MaxTagNativeProbes);
        }

        [Fact]
        public void Tag_search_tries_wider_offsets_when_fewer_than_eight_local_candidates_fit()
        {
            var tag = PlanBox.FromCorners(0, 0, 1, 1);
            var crop = PlanBox.FromCorners(1.99, 1.99, 3.01, 3.01);
            var probes = new List<(double X, double Y)>();
            var chosen = DeliveryLayoutRules.FindTagOffset(tag, Array.Empty<PlanBox>(), .1, 4.01 * Math.Sqrt(2), crop,
                new AnnotationBudget(() => 0), (x, y) =>
                {
                    probes.Add((x, y));
                    return Math.Abs(x - 2.005) < 1e-7 && Math.Abs(y - 2.005) < 1e-7;
                });

            Assert.True(chosen.HasValue);
            Assert.Equal(2.005, chosen.Value.X, 7);
            Assert.Equal(2.005, chosen.Value.Y, 7);
            Assert.InRange(probes.Count, 2, 8);
            Assert.Equal(chosen.Value, probes.Last());
        }

        [Fact]
        public void A_clear_measured_seed_needs_no_native_move()
        {
            var tag = PlanBox.FromCorners(0, 0, 1, 1);
            var offset = DeliveryLayoutRules.FindTagOffset(tag, Array.Empty<PlanBox>(), .1, 2, null,
                new AnnotationBudget(), (_, _) => throw new InvalidOperationException("Redundant native probe"));
            Assert.Equal((0.0, 0.0), offset.Value);
        }

        [Fact]
        public void A_clear_box_at_a_head_that_moved_still_remeasures_the_requested_seed()
        {
            var tag = PlanBox.FromCorners(0, 0, 1, 1);
            var probes = new List<(double, double)>();
            var offset = DeliveryLayoutRules.FindTagOffset(tag, Array.Empty<PlanBox>(), .1, 2, null,
                new AnnotationBudget(), (x, y) =>
                {
                    probes.Add((x, y));
                    return true;
                }, currentHeadFromSeed: (0, 0)); // The head moved normal to the view.
            Assert.Equal((0.0, 0.0), offset.Value);
            Assert.Equal(offset.Value, Assert.Single(probes));
        }

        [Fact]
        public void Moved_tag_searches_the_requested_seed_frame_for_a_clear_position_inside_the_crop()
        {
            var currentBox = PlanBox.FromCorners(10, -4, 11, -3);
            var seedBox = PlanBox.FromCorners(0, 0, 1, 1);
            var crop = PlanBox.FromCorners(0, 0, 3.2, 1.1);
            var obstacles = new[] { PlanBox.FromCorners(0, 0, 2, 1) };
            var probes = new List<(double X, double Y)>();
            var offset = DeliveryLayoutRules.FindTagOffset(currentBox, obstacles, .1, 2.15, crop,
                new AnnotationBudget(() => 0), (x, y) =>
                {
                    probes.Add((x, y));
                    return DeliveryLayoutRules.Clear(DeliveryLayoutRules.Shift(seedBox, x, y), obstacles, .1, crop);
                }, currentHeadFromSeed: (10, -4));

            var chosen = Assert.Single(probes);
            Assert.Equal(chosen, offset);
            Assert.InRange(chosen.X, 2.1 - 1e-7, 2.15 + 1e-7);
        }

        [Fact]
        public void Element_crop_bounds_the_depth_in_the_rotated_view_frame()
        {
            var frame = new CropFrame { Origin = new[] { 100.0, 20, 30 },
                X = new[] { 0.0, 1, 0 }, Y = new[] { 0.0, 0, 1 }, Z = new[] { 1.0, 0, 0 } };
            var points = new[] { new[] { 102.0, 24, 35 }, new[] { 105.0, 28, 40 } };
            double[] volume = ViewCropGeometry.VolumeBounds(points.Select(frame.ToLocal).ToList(), .5);
            Assert.Equal(new[] { 3.5, 4.5, 1.5, 8.5, 10.5, 5.5 }, volume);
        }

        [Fact]
        public void Element_crop_keeps_the_camera_rectangle_and_bounds_section_depth_in_its_native_frame()
        {
            double d = Math.Sqrt(.5);
            var camera = new CropFrame { Y = new[] { 0.0, d, d }, Z = new[] { 0.0, -d, d } };
            var nativeSection = new CropFrame { Origin = new[] { 10.0, 0, 0 } };
            double[][] modelCorners = (
                from x in new[] { 10.0, 20 }
                from y in new[] { 5.0, 10 }
                from z in new[] { 3.0, 7 }
                select new[] { x, y, z }).ToArray();

            var bounds = ViewCropGeometry.ElementCropBounds(camera, nativeSection, modelCorners, .5);
            double[] expectedCrop = { 9.5, 8 * d - .5, 20.5, 17 * d + .5 };
            Assert.Equal(expectedCrop.Select(x => Math.Round(x, 8)), bounds.Crop.Select(x => Math.Round(x, 8)));
            Assert.Equal(new[] { -.5, 4.5, 2.5, 10.5, 10.5, 7.5 }, bounds.Section);
        }

        [Theory]
        [InlineData("depth")]
        [InlineData("origin")]
        [InlineData("axes")]
        [InlineData("view crop")]
        public void Crop_confirmation_refuses_changed_target_box_with_same_request_and_view(string change)
        {
            ResolvedPlan Plan(CropFrame frame, double height, double cropWidth = 10)
            {
                var view = new PlannedElement { UniqueId = "view", Action = PlannedAction.Modify };
                ViewCropGeometry.BindSectionBoxTarget(view, frame, new[] { 0, 0, 0, 10, 10, height },
                    new[] { 0, 0, cropWidth, 10 });
                return new ResolvedPlan { Command = "crop", DocumentKey = "d", Elements = { view } };
            }

            var old = Plan(new CropFrame(), 8);
            var changedFrame = new CropFrame();
            if (change == "origin") changedFrame.Origin[0] = .001;
            if (change == "axes")
            {
                changedFrame.X = new[] { 0.0, 1.0, 0.0 };
                changedFrame.Y = new[] { -1.0, 0.0, 0.0 };
            }
            var updated = Plan(changedFrame, change == "depth" ? 8.001 : 8, change == "view crop" ? 10.001 : 10);
            var store = new ConfirmationStore();
            var token = store.Issue("crop", "d", "request", null, old.Fingerprint());
            Assert.False(store.Validate(token.Token, "crop", "d", "request", updated.Fingerprint(), null).Ok);
        }

        [Fact]
        public void Effective_crop_is_empty_in_the_corner_of_a_rotated_section_boxes_envelope()
        {
            double d = Math.Sqrt(.5);
            var section = new CropFrame { X = new[] { d, d, 0 }, Y = new[] { -d, d, 0 } };
            double[] box = { -1, -1, -2, 1, 1, 2 };
            Assert.Null(ViewCropGeometry.ClippedSectionBounds(new CropFrame(), section, box, new[] { 1.1, 1.1, 1.4, 1.4 }));
            double[] partial = ViewCropGeometry.ClippedSectionBounds(new CropFrame(), section, box, new[] { 0.0, 0, 1, 1.4 });
            Assert.Equal(new[] { 0.0, 0, -2, 1, 1.4, 2 }, partial.Select(value => Math.Round(value, 8)));
        }

        [Fact]
        public void Effective_crop_depth_uses_only_the_part_of_the_tilted_box_in_the_crop()
        {
            double d = Math.Sqrt(.5);
            var section = new CropFrame { X = new[] { d, 0, d }, Z = new[] { -d, 0, d } };
            double[] box = { -1, -1, -1, 1, 1, 1 };
            double[] clipped = ViewCropGeometry.ClippedSectionBounds(new CropFrame(), section, box, new[] { 1.0, -.5, 2, .5 });
            Assert.Equal(Math.Sqrt(2) - 1, clipped[5], 8);
            Assert.Equal(1 - Math.Sqrt(2), clipped[2], 8);
        }

        [Theory]
        [InlineData(.5, true)]
        [InlineData(10, true)]
        [InlineData(10, false)]
        public void Unread_plan_geometry_blocks_confirmation_only_when_it_could_affect_the_first_hit(double mm, bool overlapsCut)
        {
            double distance = mm / 304.8;
            string[] candidates = { "linked geometry", "door at cut", "floor" };
            var observed = PlanLeaderRules.FirstTarget(candidates,
                element => element == "linked geometry" && !overlapsCut ? distance : 0,
                element => element == "linked geometry" ? (null, false, "linked geometry could not be measured") :
                    element == "door at cut" ? (0, true, null) : (distance, false, null),
                element => element);

            Assert.Equal(overlapsCut ? null : "door at cut", observed.Target);
            if (overlapsCut) Assert.Contains("linked geometry", observed.Problem);
            else Assert.Null(observed.Problem);
        }

        [Fact]
        public void A_measured_empty_ray_still_refuses_a_point_leader()
        {
            Assert.Contains("touches no element", TextNoteLayoutRules.LeaderRayProblem(true, true, "complete", false, "the ray met nothing"));
        }

        [Fact]
        public void Override_explanation_keeps_matching_filters_in_order_with_the_setting_winner()
        {
            JObject trace = JObject.Parse(@"{
                layers: [
                    {source:'element'},
                    {source:'filter',filter_id:3,matches:false},
                    {source:'filter',filter_id:7,matches:true,enabled:false},
                    {source:'filter',filter_id:4,matches:true,enabled:true,name:'Walls tint'},
                    {source:'category'}
                ],
                winners: {surface_color:{from:'filter:4',value:'10,20,30'}}
            }");
            var row = new JObject();
            GraphicsPrecedence.AddFilterExplanation(row, trace);
            Assert.Equal(new[] { 7, 4 }, row["filters"].Select(filter => filter.Value<int>("filter_id")));
            Assert.Equal("Walls tint", row["filters"][1].Value<string>("name"));
            Assert.Equal("filter:4", (string)row["graphics_winners"]["surface_color"]["from"]);
        }

        [Theory]
        [InlineData("OST_Viewers", -12)]
        [InlineData("ost_sections", -13)]
        [InlineData("Unknown", null)]
        public void Query_summary_counts_native_section_marks_even_without_a_managed_category(string category, int? id)
        {
            long BuiltInId(string name) => (long)Enum.Parse<BuiltInCategory>(name, true);
            long? nativeId = SectionMarkCategories.QueryId(category, null, BuiltInId);
            Assert.Equal((long?)id, nativeId);
            if (id.HasValue)
                Assert.Equal((long?)id, SectionMarkCategories.QueryId(category, 999, BuiltInId));

            var requested = new HashSet<long>();
            if (nativeId.HasValue) requested.Add(nativeId.Value);
            var doc = new Document();
            var viewId = new ElementId(10);
            void Add(long elementId, BuiltInCategory nativeCategory, long owner, bool hidden = false)
                => doc.Elements.Add(elementId, new Element { Id = new ElementId(elementId), TestCategory = nativeCategory,
                    TestOwnerView = owner, TestNoManagedCategory = true, TestHiddenInOwnerView = hidden });
            Add(1, BuiltInCategory.OST_Viewers, 10);
            Add(2, BuiltInCategory.OST_Viewers, 10);
            Add(3, BuiltInCategory.OST_Sections, 10);
            Add(4, BuiltInCategory.OST_Walls, 10);
            Add(5, BuiltInCategory.OST_Viewers, 20);
            Add(6, BuiltInCategory.OST_Viewers, 10, hidden: true);
            var candidates = QueryModelCommand.CollectCategoryCandidates(doc, viewId, requested, false, out var markFilters);
            var summary = new QuerySummaryAccumulator();
            foreach (Element element in candidates)
                if (SectionMarkCategories.MatchesQuery(requested, true, () => element.Category?.Id.Value, () => false))
                    summary.Add(element.Category?.Name, null, "host", "model");
            var byId = doc.Elements.Values.Where(e => e.Id.Value <= 4 &&
                SectionMarkCategories.MatchesQuery(requested, false, () => e.Category?.Id.Value,
                    () => markFilters.Any(filter => filter.PassesFilter(doc, e.Id)))).Select(e => e.Id.Value);
            Assert.Equal(id == -12 ? new[] { 1L, 2L } : id == -13 ? new[] { 3L } : Array.Empty<long>(), byId);
            int expectedCount = id == -12 ? 2 : id == -13 ? 1 : 0;
            Assert.Equal(expectedCount, summary.Count);
            if (id.HasValue) Assert.Equal(expectedCount, summary.ToJson()["by_category"].Value<int>("(no category)"));
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        public void Unread_filter_facts_are_reported_and_only_safe_winners_are_returned(bool unreadEnabled, bool disabled)
        {
            var row = new JObject { ["source"] = "filter", ["filter_id"] = 12, ["name"] = "Walls tint" };
            var layers = new List<GraphicsLayer>
            {
                new GraphicsLayer { Source = "category", Label = "Walls", Fields = { ["line_color"] = "#123456" } }
            };
            GraphicsPrecedence.ReadFilter(row, layers,
                () => unreadEnabled ? throw new InvalidOperationException("state unavailable") : !disabled,
                () => unreadEnabled ? true : throw new InvalidOperationException("rule unavailable"),
                () => true, () => new Dictionary<string, string> { ["line_color"] = "#654321" });
            var trace = new JObject { ["layers"] = new JArray(row) };
            GraphicsPrecedence.AddWinners(trace, layers, (JArray)trace["layers"]);
            var explanation = new JObject();
            GraphicsPrecedence.AddFilterExplanation(explanation, trace);
            Assert.Contains("filter 12 (Walls tint)", (string)explanation["filter_warning"]);
            Assert.Equal(JTokenType.Null, explanation["filters"][0][unreadEnabled ? "enabled" : "matches"].Type);
            if (disabled) Assert.Equal("Walls", (string)explanation["graphics_winners"]["line_color"]["from"]);
            else Assert.Equal(JTokenType.Null, explanation["graphics_winners"].Type);
        }
    }
}
