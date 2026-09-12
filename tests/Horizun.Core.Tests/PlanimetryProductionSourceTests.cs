using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class PlanimetryProductionSourceTests
    {
        private static string Source(string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", file));
        }

        [Fact]
        public void Sheet_packing_is_deterministic_rehearsed_and_atomic()
        {
            string s = Source("PackSheetsCommand.cs");
            Assert.Contains("PlanimetryPackingRules.Pack", s, StringComparison.Ordinal);
            Assert.Contains("TransactionGroup", s, StringComparison.Ordinal);
            Assert.Contains("RequireConfirmation", s, StringComparison.Ordinal);
            Assert.Contains("fixedObstacles", s, StringComparison.Ordinal);
            Assert.Contains("RollbackConfirmed", s, StringComparison.Ordinal);
            Assert.Contains("measure unplaced sheet content", s, StringComparison.Ordinal);
            Assert.Contains("GetLabelOutline", s, StringComparison.Ordinal);
            Assert.Contains("AnchorOffsetX", s, StringComparison.Ordinal);

            int confirmation = s.IndexOf("RequireConfirmation(app", StringComparison.Ordinal);
            int applyMeasurement = s.IndexOf("MeasureItems(doc", StringComparison.Ordinal);
            Assert.True(confirmation >= 0 && applyMeasurement > confirmation,
                "apply must spend confirmation before provisional paper-size measurement opens a transaction");
        }

        [Fact]
        public void Print_policy_contradiction_names_rewritten_options_and_the_dimension_editor_admits_the_modifier()
        {
            // c7 (2026-09-08): the export failed with "contradicts the print policy: " and
            // nothing after the colon, because only verified_mismatch rows were listed
            // while an applied_mismatch had closed the gate.
            string export = Source("ExportCommand.cs");
            int msg = export.IndexOf("The produced PDF contradicts the print policy", StringComparison.Ordinal);
            Assert.True(msg >= 0);
            string after = export.Substring(msg, 900);
            Assert.Contains("PdfPrintPolicy.StatusAppliedMismatch", after, StringComparison.Ordinal);
            Assert.Contains("PdfPrintPolicy.StatusVerifiedMismatch", after, StringComparison.Ordinal);
            string dims = Source("EditDimensionsCommand.cs");
            Assert.Contains("ActionFieldClass.Modifier:", dims, StringComparison.Ordinal);
            Assert.Contains("DimensionEditRules.ModifierWithoutTarget(", dims, StringComparison.Ordinal);
        }

        [Fact]
        public void Delivery_open_refuses_an_existing_id_before_it_preflights_the_plan()
        {
            // c8b (2026-09-08): re-opening a delivery whose model had changed since the
            // first open was refused by the preflight, hiding "already exists - resume it".
            string s = Source("PlanViewsCommand.cs");
            int open = s.IndexOf("case \"delivery_open\":", StringComparison.Ordinal);
            Assert.True(open >= 0);
            int exists = s.IndexOf("already exists on this machine", open, StringComparison.Ordinal);
            int preflight = s.IndexOf("DeliveryPreflight.Static(profile, hostYear)", open, StringComparison.Ordinal);
            Assert.True(exists >= 0 && preflight >= 0 && exists < preflight,
                "delivery_open must answer 'already exists' before it runs the preflight");
        }

        [Fact]
        public void Ribbon_speaks_the_language_of_the_host_and_keeps_the_owner_controls_behind_one_menu()
        {
            // Every button label, tooltip and dialog text comes from RibbonText, which
            // decides Spanish/English from Revit's own LanguageType; the four owner-local
            // controls live behind ONE "Advanced options" button that opens the Horizun
            // menu and then runs exactly the command the chosen row stands for.
            string ribbon = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Ribbon.cs"));
            string text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "RibbonText.cs"));
            int buttons = CountOf(ribbon, "new PushButtonData(");
            Assert.Equal(4, buttons);
            Assert.True(CountOf(ribbon, "RibbonText.") >= buttons, "every button reads its text from RibbonText");
            Assert.DoesNotContain("\"Modo\\nBIM\"", ribbon, StringComparison.Ordinal);
            Assert.Contains("typeof(AdvancedOptionsCommand)", ribbon, StringComparison.Ordinal);
            Assert.Contains("RibbonText.IsSpanish(app.ControlledApplication.Language)", ribbon, StringComparison.Ordinal);
            Assert.Contains("\"Opciones\\navanzadas\", \"Advanced\\noptions\"", text, StringComparison.Ordinal);
            Assert.Contains("\"Estado de\\nconexión\", \"Connection\\nstatus\"", text, StringComparison.Ordinal);
            Assert.Contains("ModeName(bool es, string profile)", text, StringComparison.Ordinal);   // profile ids never reach the person raw
            Assert.Contains("\"Producción BIM\", \"BIM Production\"", text, StringComparison.Ordinal);
            foreach (string option in new[] { "OptionMode", "OptionHistory", "OptionPause", "OptionCentral" })
                Assert.Contains("AdvancedOptionsWindow." + option + ": return new", ribbon, StringComparison.Ordinal);
            // The window is owned by Revit's main window and shows the CURRENT state of each control.
            Assert.Contains("new WindowInteropHelper(this).Owner = owner", text, StringComparison.Ordinal);
            Assert.Contains("BridgeSettings.McpPaused", text, StringComparison.Ordinal);
            Assert.Contains("BridgeSettings.ForceReadOnlyOnWorkshared", text, StringComparison.Ordinal);
            Assert.Contains("BridgeSettings.PermissionProfile", text, StringComparison.Ordinal);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static int CountOf(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
            return count;
        }

        [Fact]
        public void A_tag_without_extent_is_explained_by_the_view_before_the_family_is_blamed()
        {
            // 2026-09-08: a labelled tag with its text resolved was refused as "label-less"
            // while the real cause was the view template hiding the Multi-Category Tags
            // category. The refusal asks the view first, then the text, then the family.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Core"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string s = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Core", "AnnotationLayout.cs"));
            int refusal = s.IndexOf("private static string OwnBoxRefusal", StringComparison.Ordinal);
            Assert.True(refusal >= 0);
            string body = s.Substring(refusal, 3600);
            Assert.Contains("host.get_BoundingBox(view) == null", body, StringComparison.Ordinal);
            Assert.Contains("view.GetCategoryHidden(cat.Id)", body, StringComparison.Ordinal);
            Assert.Contains("view.AreAnnotationCategoriesHidden", body, StringComparison.Ordinal);
            Assert.Contains("view.ViewTemplateId", body, StringComparison.Ordinal);
            Assert.Contains("Cause found:", body, StringComparison.Ordinal);
            Assert.True(body.IndexOf("GetCategoryHidden", StringComparison.Ordinal) < body.IndexOf("label-less", StringComparison.Ordinal),
                "the view is asked before the family is blamed");
        }

        [Fact]
        public void Annotation_planner_writes_nothing_and_delegates_the_verified_write()
        {
            string s = Source("PlanAnnotationsCommand.cs");
            Assert.DoesNotContain("new Transaction(", s, StringComparison.Ordinal);
            Assert.Contains("new DimensionReferencesCommand().Execute", s, StringComparison.Ordinal);
            Assert.Contains("exactly one is required", s, StringComparison.Ordinal);
            Assert.Contains("next_tool\"] = \"horizun_annotate\"", s, StringComparison.Ordinal);
            Assert.Contains("coverage_complete", s, StringComparison.Ordinal);
        }

        [Fact]
        public void Explicit_tag_type_and_duplicate_precondition_are_bound_and_verified()
        {
            string s = Source("AnnotateCommand.cs");
            Assert.Contains("tag_type_id", s, StringComparison.Ordinal);
            Assert.Contains("GetValidTypes", s, StringComparison.Ordinal);
            Assert.Contains("tag.ChangeTypeId", s, StringComparison.Ordinal);
            Assert.Contains("ExistingTagCount", s, StringComparison.Ordinal);
            Assert.Contains("p.Type ?? p.EffectiveTagType", s, StringComparison.Ordinal);
            Assert.Contains("tag.OwnerViewId == viewId", s, StringComparison.Ordinal);
            Assert.Contains("if (expected != null && tag.GetTypeId() != expected.Id)", s, StringComparison.Ordinal);
            Assert.Contains("the committed tag type is not the bound type", s, StringComparison.Ordinal);
            Assert.Contains("tag.OwnerViewId != p.View.Id || tag.IsOrphaned", s, StringComparison.Ordinal);
            Assert.Contains("tag.TagHeadPosition.DistanceTo(p.TagPoint ?? p.Point)", s, StringComparison.Ordinal);
            Assert.Contains("JToken.DeepEquals(p.TagRehearsal,legacy[\"tag_evidence\"])", s, StringComparison.Ordinal);
            // Post-commit clearance is re-surveyed from the document in the tag's own
            // view, excluding the tag itself, and a coverage refusal keeps its ids
            // instead of collapsing into a bare false.
            Assert.Contains("AnnotationLayout.Survey(tag.Document, p.View, tag.Id, p.LayoutAccepted)", s, StringComparison.Ordinal);
            Assert.Contains("if (!survey.Complete) { reason = AnnotationVisibility.RefusalMessage(survey.Coverage); return false; }", s, StringComparison.Ordinal);
            Assert.Contains("catch (AnnotationCoverageException ex)", s, StringComparison.Ordinal);
            int verifyStart = s.IndexOf("private static bool Verify(Plan p, Element e, out string reason)", StringComparison.Ordinal);
            int verifyEnd = s.IndexOf("bool okDim = e is Dimension dimension", verifyStart, StringComparison.Ordinal);
            Assert.True(verifyStart >= 0 && verifyEnd > verifyStart, "the reasoned tag verification must exist");
            Assert.DoesNotContain("catch { return false; }", s.Substring(verifyStart, verifyEnd - verifyStart), StringComparison.Ordinal);
        }

        [Fact]
        public void Text_positions_and_leaders_are_explicit_arguments_that_refuse_what_revit_cannot_honour()
        {
            // 8B. The design decision (where the text or leader goes) is the caller's
            // explicit argument; the bridge refuses what the API reports it cannot do and
            // proves what it wrote by re-reading it.
            string dims = Source("EditDimensionsCommand.cs");
            Assert.Contains("IsTextPositionAdjustable()", dims, StringComparison.Ordinal);
            Assert.Contains("text_position and text_offset are two answers to one question", dims, StringComparison.Ordinal);
            Assert.Contains("leader_end needs a leader", dims, StringComparison.Ordinal);
            Assert.Contains("d.TextPosition = p.TextPositionTarget", dims, StringComparison.Ordinal);
            Assert.Contains("s.TextPosition = se.TextPositionTarget", dims, StringComparison.Ordinal);
            Assert.Contains("d.LeaderEndPosition = p.LeaderEnd", dims, StringComparison.Ordinal);
            Assert.Contains("PointCheck(fields, \"text_position\"", dims, StringComparison.Ordinal);
            Assert.Contains("DimensionEditRules.DefaultPositionToleranceFeet", dims, StringComparison.Ordinal);
            Assert.Contains("DeliveryLayoutRules.Distance(Math.Abs(dx), scale, view.Scale, space)", dims, StringComparison.Ordinal);

            string tags = Source("TransformElementsCommand.cs");
            Assert.Contains("CanLeaderEndConditionBeAssigned", tags, StringComparison.Ordinal);
            Assert.Contains("leader_end applies only to a FREE leader end", tags, StringComparison.Ordinal);
            Assert.Contains("set_tag_leader addresses the leader of exactly one tagged reference", tags, StringComparison.Ordinal);
            Assert.Contains("room, space and area tags are not", tags, StringComparison.Ordinal);
            Assert.Contains("tag.SetLeaderEnd(r, p.LeaderEnd)", tags, StringComparison.Ordinal);
            Assert.Contains("tag.GetLeaderEnd(r)", tags, StringComparison.Ordinal);
            Assert.Contains("tag.TagHeadPosition = p.HeadPoint ?? p.HeadBefore", tags, StringComparison.Ordinal);
            Assert.Contains("TagPositionToleranceFeet = 1e-5", tags, StringComparison.Ordinal);
        }

        [Fact]
        public void Pdf_composition_is_judged_against_the_declared_sheet_size_not_the_titleblock_box()
        {
            // Measured live 2026-09-08: a titleblock's bounding box includes its labels,
            // so an overflowing sheet number widened the box exactly as much as the page
            // and the two agreed. The paper the titleblock DECLARES is the reference.
            string s = Source("ExportCommand.cs");
            Assert.Contains("BuiltInParameter.SHEET_WIDTH", s, StringComparison.Ordinal);
            Assert.Contains("BuiltInParameter.SHEET_HEIGHT", s, StringComparison.Ordinal);
            Assert.Contains("TitleblockPoints(doc, source)", s, StringComparison.Ordinal);
            Assert.DoesNotContain("blocks[0].get_BoundingBox(sheet)", s, StringComparison.Ordinal);
            // A composition defect fails the export by name, beside the option mismatches.
            Assert.Contains("pages_exceeding_titleblock", s, StringComparison.Ordinal);
            Assert.Contains("The produced PDF contradicts the print policy", s, StringComparison.Ordinal);
        }

        [Fact]
        public void Revisions_are_rehearsed_confirmed_and_verified_inside_one_group()
        {
            string s = Source("ManageRevisionsCommand.cs");
            int confirmation = s.IndexOf("RequireConfirmation", StringComparison.Ordinal);
            int group = s.IndexOf("new TransactionGroup", StringComparison.Ordinal);
            Assert.True(confirmation >= 0 && group > confirmation, "apply must spend confirmation before its writing group opens");
            Assert.Contains("RevisionCloud.Create", s, StringComparison.Ordinal);
            Assert.Contains("SetAdditionalRevisionIds", s, StringComparison.Ordinal);
            Assert.Contains("plans.All(p => Verify", s, StringComparison.Ordinal);
            Assert.Contains("Guard.RollBack(group)", s, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_production_command_is_registered_in_the_addin()
        {
            string s = Source(Path.Combine("..", "App.cs"));
            Assert.Contains("new PackSheetsCommand()", s, StringComparison.Ordinal);
            Assert.Contains("new PlanAnnotationsCommand()", s, StringComparison.Ordinal);
            Assert.Contains("new ManageRevisionsCommand()", s, StringComparison.Ordinal);
        }
    }
}
