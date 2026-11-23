// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Pro 3.4.0 wiring. The rules behind each improvement are proved in their own
// test classes (LinkedTagRules, GridExtentRules, View3DRequestRules, ...). These prove the
// OTHER half: that the Revit-side command really calls the rule and the Revit API it was
// written for. None of these commands can be constructed without a Revit, so - like
// ApplicationDeclarationWiringTests - they read the SOURCE. It is a coarse instrument and
// deliberately so: it catches a rule that is correct and no longer asked.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class Pro340WiringTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }

        private static string Src(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { RepoRoot(), "src", "Horizun.Revit" }.Concat(parts).ToArray()));

        private static int Count(string text, string needle)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
            return n;
        }

        [Fact]
        public void Annotate_tags_a_link_element_through_a_link_reference_and_re_reads_the_pair()
        {
            string src = Src("Commands", "AnnotateCommand.cs");
            Assert.Contains("LinkedTagRules.Check(", src);
            Assert.Contains("new Reference(p.LinkedElement).CreateLinkReference(p.LinkInstance)", src);
            Assert.Contains("tag.GetTaggedElementIds()", src);
            Assert.Contains("LinkedTagRules.PointsOnlyAt(", src);
        }

        [Fact]
        public void New_grids_span_every_level_and_the_extent_is_read_back()
        {
            string create = Src("Commands", "CreateElementsCommand.cs");
            // SetVerticalExtents, never Maximize3DExtents: Maximize also moves the ends in plan (field cycle, 2026-10-23).
            Assert.Contains("grid.SetVerticalExtents(gridBottom, gridTop)", create);
            Assert.DoesNotContain("grid.Maximize3DExtents()", create);
            Assert.Contains("GridExtentRules.Parse(", create);
            Assert.Contains("GridExtentRules.NeedsFlatten(", create);
            string verify = Src("Commands", "CreateElementsGeometry.cs");
            Assert.Contains("extent_3d_covers_levels", verify);
            Assert.Contains("GridExtentRules.CoversLevels(", verify);
        }

        [Fact]
        public void Existing_grids_are_extended_through_every_stage_of_manage_views()
        {
            string views = Src("Commands", "ManageViewsCommand.cs") + Src("Commands", "ManageViewsCommand.Execution.cs");
            // preview, bind, detail, validate, apply, verify: six places or the operation half-works.
            Assert.True(Count(views, "IsGridExtentOperation(") >= 6, "extend_grids_3d must be wired into all six stages");
            Assert.Contains("\"extend_grids_3d\"", views);
            string ext = Src("Commands", "ManageViewsGridExtent.cs");
            Assert.Contains("g.SetVerticalExtents(bottom, top)", ext);
            Assert.DoesNotContain("g.Maximize3DExtents()", ext);
            Assert.Contains("GridExtentRules.CoversLevels(", ext);
        }

        [Fact]
        public void Create_3d_applies_and_verifies_the_camera_and_style_it_used_to_ignore()
        {
            string views = Src("Commands", "ManageViewsCommand.cs");
            Assert.Contains("Validate3DView(a, true)", views);
            Assert.Contains("Apply3DView(view, a, scale)", views);
            Assert.Contains("Verify3DView(doc, a.Action, e)", views);
            string camera = Src("Commands", "ManageViews3DCamera.cs");
            Assert.Contains("View3DRequestRules.MisnamedCameraField(", camera);
            Assert.Contains("view.SetOrientation(", camera);
            Assert.Contains("view.DisplayStyle = style", camera);
            string perspective = Src("Commands", "ManageViewsPerspective.cs");
            Assert.Contains("Apply3DStyle(view, a, styleSent)", perspective);
            Assert.Contains("display_style_verified", perspective);
        }

        [Fact]
        public void Elevation_depth_is_the_far_clip_offset_and_is_re_read()
        {
            string views = Src("Commands", "ManageViewsCommand.cs");
            Assert.Contains("BuiltInParameter.VIEWER_BOUND_OFFSET_FAR", views);
            Assert.Contains("__elevation_depth_feet", views);
        }

        [Fact]
        public void Captures_report_the_camera_and_accept_style_words()
        {
            string options = Src("Commands", "CaptureViewOptions.cs");
            Assert.Contains("View3DRequestRules.Describe(", options);
            Assert.Contains("View3DRequestRules.ParseStyle(", options);
            Assert.Contains("result[\"display_style\"]", options);
        }

        [Fact]
        public void Surface_overrides_paint_with_a_real_pattern_that_is_visible_and_re_read()
        {
            string graphics = Src("Commands", "ManageViewsGraphics.cs");
            Assert.Contains("ResolveOverridePattern(", graphics);
            Assert.Contains("SetSurfaceForegroundPatternVisible(true)", graphics);
            Assert.Contains("SetCutForegroundPatternVisible(true)", graphics);
            Assert.Contains("OverridePatternRules.PatternHolds(", graphics);
            // ReadOverrides no longer takes "the first solid fill, or none, silently".
            int from = graphics.IndexOf("internal static OverrideGraphicSettings ReadOverrides", StringComparison.Ordinal);
            int to = graphics.IndexOf("private static Color ReadColour", StringComparison.Ordinal);
            Assert.True(from > 0 && to > from);
            Assert.DoesNotContain("SolidFillPattern(doc)", graphics.Substring(from, to - from));
        }

        [Fact]
        public void Detail_2d_says_what_it_did_not_do_and_why_a_re_read_failed()
        {
            string detail = Src("Commands", "Detail2DCommand.cs");
            Assert.Contains("Detail2DOutcomeRules.StampNotApplied(", detail);
            Assert.Contains("Detail2DOutcomeRules.Headline(Detail2DOutcomeRules.FailedChecks(", detail);
            Assert.Contains("Detail2DOutcomeRules.AtRisk(", detail);
            Assert.Contains("Detail2DOutcomeRules.SimplifyLoop(", detail);
            Assert.Contains("Detail2DOutcomeRules.EndpointsAcceptable(", detail);
            Assert.Contains("applied[\"applied\"] = true", detail);
            string query = Src("Commands", "QueryDetail2DCommand.cs");
            Assert.Contains("Detail2DQueryRules.PassesCategoryFilter(", query);
        }

        [Fact]
        public void Railings_take_their_level_from_the_element_that_hosts_them()
        {
            string reader = Src("Commands", "ElementLevelReader.cs");
            Assert.Contains("LevelResolutionRules.RailingHostElement", reader);
            Assert.Contains("railing.HasHost", reader);
            Assert.Contains("railing.HostId", reader);
        }

        [Fact]
        public void Query_model_pages_by_offset_and_the_cursor_hash_ignores_it()
        {
            string query = Src("Commands", "QueryModelCommand.cs");
            Assert.Contains("QueryPagingRules.ParseOffset(", query);
            Assert.Contains("int offset = requestedOffset;", query);
            Assert.Contains("copy.Remove(\"offset\")", query);
        }

        [Fact]
        public void Delete_previews_count_every_cascaded_category()
        {
            string delete = Src("Commands", "DeleteCommand.cs");
            Assert.Contains("CascadeCensusRules.ByCategory(", delete);
            Assert.Contains("CascadeCensusRules.CapNote(", delete);
        }

        [Fact]
        public void A_create_batch_that_rolls_back_names_the_items_Revit_blames()
        {
            string execution = Src("Commands", "CreateElementsCommand.Execution.cs");
            Assert.Contains("failureOptions.SetFailuresPreprocessor(failureCapture)", execution);
            Assert.Contains("FailureAttributionRules.Attribute(failureCapture.Facts", execution);
            Assert.Contains("\"culprit_items\"", execution);
            // The capture must change nothing: it always answers Continue.
            string geometry = Src("Commands", "CreateElementsGeometry.cs");
            int start = geometry.IndexOf("class BatchFailureCapture", StringComparison.Ordinal);
            string body = geometry.Substring(start, Math.Min(1500, geometry.Length - start));
            Assert.Contains("return FailureProcessingResult.Continue;", body);
            Assert.DoesNotContain("ProceedWithRollBack", body);
            Assert.DoesNotContain("DeleteWarning", body);
        }

        [Fact]
        public void Write_params_reports_simple_write_counters()
        {
            string write = Src("Commands", "WriteParamsCommand.cs");
            Assert.Contains("[\"writes_verified\"] = confirmedAgainstCallerValue", write);
            Assert.Contains("[\"writes_requested\"] = rows.Count", write);
            Assert.Contains("[\"writes_failed\"] = failed", write);
        }

        [Fact]
        public void Pdf_overwrite_moves_the_old_file_aside_and_restores_it_on_failure()
        {
            string export = Src("Commands", "ExportCommand.cs");
            string attempt = Src("Commands", "ExportCommand.Attempt.cs");
            Assert.Contains("ExecuteExportAttempt(", export);
            Assert.Contains("PdfOverwriteRules.ToMoveAside(", attempt);
            Assert.Contains("RestoreAsides()", export);
            Assert.Contains("previous_pdfs_restored", attempt);
            Assert.Contains("replaced_existing_pdfs", export);
        }

        [Fact]
        public void Move_verifies_a_location_less_element_by_its_bounding_box()
        {
            string transform = Src("Commands", "TransformElementsCommand.cs");
            Assert.Contains("BoxSampled", transform);
            Assert.Contains("Samples(element, true)", transform);
            // Rotation and mirroring stay refused for such an element: a turned box is not the turned corners.
            Assert.Contains("(op == \"move\" || op == \"move_preserve_pin\") && !(element is Floor)", transform);
        }

        [Fact]
        public void Profiles_are_checked_for_necks_and_spikes_before_anything_is_written()
        {
            string input = Src("Core", "GeometryInput.cs");
            Assert.Contains("LoopHealthRules.Problems(contour", input);
        }

        [Fact]
        public void Query_model_expands_a_star_into_instance_and_type_parameters()
        {
            string query = Src("Commands", "QueryModelCommand.cs");
            Assert.Contains("QueryPagingRules.AllParametersProblem(", query);
            Assert.Contains("specs.Contains(QueryPagingRules.AllParameters)", query);
            Assert.Contains("QueryPagingRules.ParameterKey(owner.Key, name, used)", query);
        }
    }
}
