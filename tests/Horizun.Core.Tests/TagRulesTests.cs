// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Tags of every kind (horizun_annotate operation=tag, horizun_plan_annotations auto_tags):
// the category map, the modes and what each refuses before anything is created, keynotes,
// multi-reference tags, leader options, linked targets Revit cannot tag, and the per-category
// report of a view sweep.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class TagRulesTests
    {
        [Theory]
        [InlineData("OST_Doors", "OST_DoorTags")]
        [InlineData("OST_Windows", "OST_WindowTags")]
        [InlineData("OST_PipeCurves", "OST_PipeTags")]
        [InlineData("OST_DuctTerminal", "OST_DuctTerminalTags")]
        [InlineData("OST_IOSModelGroups", "OST_ModelGroupTags")]
        [InlineData("OST_Rooms", "OST_RoomTags")]
        [InlineData("OST_MEPSpaces", "OST_MEPSpaceTags")]
        [InlineData("OST_Areas", "OST_AreaTags")]
        public void Each_model_category_names_its_tag_category(string tagged, string tag)
        {
            Assert.Equal(tag, TagRules.TagCategoryFor(tagged));
            Assert.Equal(tag, LinkedTagRules.TagCategoryName(tagged)); // one map for every path
        }

        [Fact]
        public void Every_mapped_name_is_a_category_followed_by_its_tag_category()
        {
            foreach (string c in TagRules.MappedCategories)
            {
                Assert.StartsWith("OST_", c);
                string t = TagRules.TagCategoryFor(c);
                Assert.StartsWith("OST_", t);
                Assert.EndsWith("Tags", t);
                Assert.True(AnnotationNoteRules.IsTagCategoryName(t), t + " must load through load_tag_family");
            }
            Assert.Null(TagRules.TagCategoryFor("OST_Levels"));
            Assert.Null(TagRules.TagCategoryFor(null));
        }

        [Fact]
        public void The_modes_include_the_three_keynotes()
        {
            Assert.Equal(new[] { "by_category", "multi_category", "material", "keynote_element", "keynote_material", "keynote_user" }, TagRules.Modes);
            Assert.True(TagRules.IsKeynote("keynote_user"));
            Assert.False(TagRules.IsKeynote("material"));
            Assert.True(TagRules.IsMaterialMode("keynote_material"));
            Assert.Equal("OST_KeynoteTags", TagRules.RequiredTypeCategory("keynote_element"));
            Assert.Equal("OST_MaterialTags", TagRules.RequiredTypeCategory("material"));
            Assert.Equal("OST_MultiCategoryTags", TagRules.RequiredTypeCategory("multi_category"));
            Assert.Null(TagRules.RequiredTypeCategory("by_category"));
        }

        [Fact]
        public void An_unknown_mode_and_a_misplaced_keynote_key_are_refused()
        {
            Assert.Contains("tag_mode must be one of", TagRules.OptionRefusal("keynote", false, false, 0, false, false, false));
            Assert.Contains("needs tag_mode=keynote_user", TagRules.OptionRefusal("by_category", false, true, 0, false, false, false));
            Assert.Contains("needs keynote_key", TagRules.OptionRefusal("keynote_user", false, false, 0, false, false, false));
            Assert.Null(TagRules.OptionRefusal("keynote_user", false, true, 0, false, false, false));
            Assert.Null(TagRules.OptionRefusal("keynote_element", true, false, 0, false, false, false));
        }

        [Theory]
        [InlineData("material")]
        [InlineData("keynote_material")]
        public void A_material_tag_over_a_link_is_refused_with_the_measured_reason(string mode)
            => Assert.Contains("InternalException", TagRules.OptionRefusal(mode, true, false, 0, false, false, false));

        [Fact]
        public void More_references_need_a_host_category_or_multi_category_tag()
        {
            Assert.Null(TagRules.OptionRefusal("by_category", false, false, 3, false, false, false));
            Assert.Null(TagRules.OptionRefusal("multi_category", false, false, 3, false, false, false));
            Assert.Contains("over a link", TagRules.OptionRefusal("by_category", true, false, 1, false, false, false));
            Assert.Contains("labels one element", TagRules.OptionRefusal("keynote_element", false, false, 1, false, false, false));
            Assert.Contains("at most 50", TagRules.OptionRefusal("by_category", false, false, 51, false, false, false));
        }

        [Fact]
        public void A_leader_shape_needs_a_leader()
        {
            Assert.Contains("add_leader=true", TagRules.OptionRefusal("by_category", false, false, 0, false, true, false));
            Assert.Contains("add_leader=true", TagRules.OptionRefusal("by_category", false, false, 0, false, false, true));
            Assert.Null(TagRules.OptionRefusal("by_category", false, false, 0, true, true, true));
            Assert.Contains("add_leader=true", TagRules.SpatialOptionRefusal("RoomTag", 0, false, false, true, false));
            Assert.Null(TagRules.SpatialOptionRefusal("RoomTag", 0, false, true, true, true));
        }

        [Fact]
        public void A_spatial_tag_takes_no_extra_references_or_keynote_key()
        {
            Assert.Contains("one room, area or space", TagRules.SpatialOptionRefusal("AreaTag", 2, false, false, false, false));
            Assert.Contains("keynote_user", TagRules.SpatialOptionRefusal("SpaceTag", 0, true, false, false, false));
        }

        [Fact]
        public void The_type_is_checked_against_mode_and_category_before_creation()
        {
            Assert.Null(TagRules.TypeCategoryRefusal("by_category", "OST_Doors", "OST_DoorTags"));
            Assert.Contains("OST_DoorTags", TagRules.TypeCategoryRefusal("by_category", "OST_Doors", "OST_WindowTags"));
            Assert.Contains("multi_category", TagRules.TypeCategoryRefusal("by_category", "OST_Doors", "OST_MultiCategoryTags"));
            Assert.Contains("keynote_element", TagRules.TypeCategoryRefusal("by_category", "OST_Doors", "OST_KeynoteTags"));
            Assert.Contains("OST_KeynoteTags", TagRules.TypeCategoryRefusal("keynote_user", "OST_Doors", "OST_DoorTags"));
            Assert.Null(TagRules.TypeCategoryRefusal("keynote_material", "OST_Walls", "OST_KeynoteTags"));
            Assert.Null(TagRules.TypeCategoryRefusal("multi_category", "OST_Doors", "OST_MultiCategoryTags"));
            // A category this map does not know is left to Revit's own GetValidTypes in the rehearsal.
            Assert.Null(TagRules.TypeCategoryRefusal("by_category", "OST_Unknown", "OST_DoorTags"));
        }

        [Fact]
        public void A_category_without_a_loaded_tag_type_names_load_tag_family()
        {
            Assert.Null(TagRules.NoLoadedTypeRefusal("by_category", "OST_Doors", 2));
            string none = TagRules.NoLoadedTypeRefusal("by_category", "OST_Doors", 0);
            Assert.Contains("OST_DoorTags", none);
            Assert.Contains("load_tag_family", none);
            Assert.Contains("OST_MaterialTags", TagRules.NoLoadedTypeRefusal("material", "OST_Walls", 0));
            Assert.Null(TagRules.NoLoadedTypeRefusal("by_category", "OST_Unknown", 0));
        }

        [Fact]
        public void Keynote_types_keys_and_element_keynotes_are_checked_before_creation()
        {
            Assert.Null(TagRules.KeynoteTypeChoiceError(1));
            Assert.Contains("load_tag_family", TagRules.KeynoteTypeChoiceError(0));
            Assert.Contains("Pass tag_type_id", TagRules.KeynoteTypeChoiceError(3));
            Assert.Null(TagRules.ElementKeynoteRefusal("09 91 00", null, 7));
            Assert.Null(TagRules.ElementKeynoteRefusal(null, "09 91 00", 7));
            Assert.Contains("horizun_set_keynote", TagRules.ElementKeynoteRefusal(" ", null, 7));
            var keys = new[] { "A1", "09 91 00.A1" };
            Assert.Null(TagRules.KeynoteKeyRefusal("A1", keys, null));
            Assert.Contains("not in the project's keynote table", TagRules.KeynoteKeyRefusal("B2", keys, null));
            Assert.Contains("cannot be validated", TagRules.KeynoteKeyRefusal("A1", null, "no table."));
        }

        [Fact]
        public void Extra_references_are_distinct_and_of_the_category_for_a_category_tag()
        {
            Assert.Null(TagRules.ExtraReferencesRefusal("by_category", 1, "OST_Doors", new long[] { 2, 3 }, new[] { "OST_Doors", "OST_Doors" }));
            Assert.Contains("repeats element_id", TagRules.ExtraReferencesRefusal("by_category", 1, "OST_Doors", new long[] { 1 }, new[] { "OST_Doors" }));
            Assert.Contains("twice", TagRules.ExtraReferencesRefusal("by_category", 1, "OST_Doors", new long[] { 2, 2 }, new[] { "OST_Doors", "OST_Doors" }));
            Assert.Contains("multi_category", TagRules.ExtraReferencesRefusal("by_category", 1, "OST_Doors", new long[] { 2 }, new[] { "OST_Windows" }));
            Assert.Null(TagRules.ExtraReferencesRefusal("multi_category", 1, "OST_Doors", new long[] { 2 }, new[] { "OST_Windows" }));
            Assert.Contains("own tag", TagRules.ExtraReferencesRefusal("multi_category", 1, "OST_Doors", new long[] { 2 }, new[] { "OST_Rooms" }));
        }

        [Fact]
        public void The_committed_set_must_be_exactly_the_requested_one()
        {
            Assert.True(TagRules.SameTaggedSet(new long[] { 1, 2, 3 }, new long[] { 3, 1, 2 }));
            Assert.False(TagRules.SameTaggedSet(new long[] { 1, 2, 3 }, new long[] { 1, 2 }));
            Assert.False(TagRules.SameTaggedSet(new long[] { 1, 2 }, new long[] { 1, 2, 4 }));
            Assert.False(TagRules.SameTaggedSet(null, new long[] { 1 }));
        }

        [Theory]
        [InlineData("area", "NewAreaTag")]
        [InlineData("space", "NewSpaceTag")]
        [InlineData("nested_link", "one level deep")]
        public void Linked_targets_revit_cannot_tag_are_refused_with_the_api_reason(string kind, string reason)
            => Assert.Contains(reason, TagRules.LinkedTargetRefusal(kind));

        [Fact]
        public void A_linked_room_and_an_ordinary_element_are_taggable()
        {
            Assert.Null(TagRules.LinkedTargetRefusal("room"));
            Assert.Null(TagRules.LinkedTargetRefusal(null));
        }

        [Fact]
        public void The_leader_is_re_read_in_the_view_plane()
        {
            double[] right = { 1, 0, 0 }, up = { 0, 1, 0 };
            // A depth difference (z in a plan) is not a placement error.
            Assert.Equal(0, TagRules.PlanarDistance(new double[] { 1, 2, 0 }, new double[] { 1, 2, 9 }, right, up), 9);
            Assert.Equal(5, TagRules.PlanarDistance(new double[] { 3, 4, 0 }, new double[] { 0, 0, 0 }, right, up), 9);
            Assert.True(TagRules.LeaderToleranceFeet * 304.8 < 0.51);
        }

        [Fact]
        public void A_sweep_report_accounts_for_every_element_per_category()
        {
            var r = new AutoTagReport();
            r.Add("OST_Doors", AutoTagReport.Planned, "10", false, false);
            r.Add("OST_Doors", AutoTagReport.AlreadyTagged, "11", true, false);
            r.Add("OST_Doors", AutoTagReport.NoFreePoint, "12", false, false);
            r.Add("OST_Walls", AutoTagReport.NoTagTypeLoaded, "5:20", false, true);
            JObject j = r.ToJson();
            Assert.Equal(3, (int)j["OST_Doors"]["visible"]);
            Assert.Equal(1, (int)j["OST_Doors"]["planned"]);
            Assert.Equal(1, (int)j["OST_Doors"]["already_tagged"]);
            Assert.Equal(1, (int)j["OST_Doors"]["not_taggable"]);
            Assert.Equal(1, (int)j["OST_Doors"]["in_groups"]);
            Assert.Equal(1, (int)j["OST_Walls"]["from_links"]);
            Assert.Equal("5:20", (string)j["OST_Walls"]["not_taggable_by_reason"]["no_tag_type_loaded"]["sample"][0]);
            Assert.Contains("load_tag_family", (string)j["OST_Walls"]["not_taggable_by_reason"]["no_tag_type_loaded"]["why"]);
            Assert.Equal(1, r.PlannedCount);
            Assert.Equal(2, r.NotTaggableCount);
            Assert.False(r.Complete);
        }

        [Fact]
        public void Already_tagged_does_not_make_a_sweep_incomplete()
        {
            var r = new AutoTagReport();
            r.Add("OST_Doors", AutoTagReport.AlreadyTagged, "1", false, false);
            r.Add("OST_Doors", AutoTagReport.Planned, "2", false, false);
            Assert.True(r.Complete);
        }

        [Fact]
        public void An_unnamed_tag_type_takes_the_single_one_or_the_document_default()
        {
            Assert.Equal(7L, TagRules.DefaultTagTypeId(new long[] { 7 }, null));
            Assert.Equal(9L, TagRules.DefaultTagTypeId(new long[] { 7, 9 }, 9));
            Assert.Null(TagRules.DefaultTagTypeId(new long[] { 7, 9 }, null));
            Assert.Null(TagRules.DefaultTagTypeId(new long[] { 7, 9 }, 4)); // a default that is not loaded is no answer
            Assert.Null(TagRules.DefaultTagTypeId(new long[0], 4));
            string refusal = TagRules.AmbiguousTagTypeRefusal("RoomTag", new[] { "7 Room Tag: A", "9 Room Tag: B" });
            Assert.Contains("7 Room Tag: A", refusal);
            Assert.Contains("tag_type_id", refusal);
        }

        [Fact]
        public void A_tag_that_reads_nothing_is_flagged_but_an_unreadable_one_is_not()
        {
            Assert.Contains("print blank", TagRules.EmptyTagTextWarning("", true, false));
            Assert.Contains("require_tag_text=true", TagRules.EmptyTagTextWarning(" ", true, false));
            Assert.Contains("refuses it", TagRules.EmptyTagTextWarning("", true, true));
            Assert.Null(TagRules.EmptyTagTextWarning(null, false, false));
            Assert.Null(TagRules.EmptyTagTextWarning("101", true, false));
        }

        [Fact]
        public void A_tag_extent_must_hold_its_head_or_is_named_as_the_scale_trap()
        {
            double[] box = { -14.3, 8.0, -13.5, 8.6 };
            Assert.Equal("contains_head", TagRules.ExtentHeadCheck(box, new[] { -14.0, 8.4 }, 16, 0.01));
            double[] scaled = box.Select(x => x * 16).ToArray(); // scaled about the view origin, the frame both are in
            Assert.Equal("contains_head_divided_by_view_scale", TagRules.ExtentHeadCheck(scaled, new[] { -14.0, 8.4 }, 16, 0.01));
            Assert.Equal("head_outside", TagRules.ExtentHeadCheck(box, new[] { 0.0, 0.0 }, 16, 0.01));
            Assert.Equal("unchecked", TagRules.ExtentHeadCheck(null, new[] { 0.0, 0.0 }, 16, 0.01));
        }

        [Fact]
        public void The_field_findings_are_wired()
        {
            Assert.Contains("DefaultTagType(doc, BuiltInCategory.OST_RoomTags", Src("AnnotateCommand.cs"));
            Assert.Contains("DefaultTagType(doc, BuiltInCategory.OST_MultiCategoryTags", Src("AnnotateTagKinds.cs"));
            Assert.Contains("DefaultTagType(doc, tagCat", Src("AnnotateSpatialTags.cs"));
            Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(
                Src("AnnotateCommand.cs") + Src("AnnotateCommand.Execution.cs"), @"MarkEmptyTagText\(").Count);
            Assert.Contains("BuiltInCategory.OST_RoomTags", Src("PlanAnnotationsCommand.AutoTags.cs"));
        }

        [Fact]
        public void Every_keynote_mode_reads_back_its_source()
        {
            Assert.Null(TagRules.KeynoteVerificationFailure("keynote_element", 0, null, null, "09 91 00", null));
            Assert.Contains("expects key source 0", TagRules.KeynoteVerificationFailure("keynote_element", 1, null, null, "x", null));
            Assert.Null(TagRules.KeynoteVerificationFailure("keynote_material", 1, "x", "x", "x", null));
            Assert.Contains("unreadable key source", TagRules.KeynoteVerificationFailure("keynote_material", null, null, null, "x", null));
            Assert.Null(TagRules.KeynoteVerificationFailure("by_category", null, null, null, null, null));
        }

        [Fact]
        public void A_user_keynote_is_proven_by_its_own_text_not_by_the_source_it_was_given()
        {
            Assert.Null(TagRules.KeynoteVerificationFailure("keynote_user", 2, "A1", "A1", "A1", "Paint"));
            Assert.Null(TagRules.KeynoteVerificationFailure("keynote_user", 2, "A1", "A1", "Paint", "Paint"));
            Assert.Null(TagRules.KeynoteVerificationFailure("keynote_user", 2, "A1", "A1", "A1 Paint", "Paint"));
            // The source and key read back, yet the tag shows something else: not proven.
            Assert.Contains("neither its key", TagRules.KeynoteVerificationFailure("keynote_user", 2, "A1", "A1", "", "Paint"));
            Assert.Contains("neither its key", TagRules.KeynoteVerificationFailure("keynote_user", 2, "A1", "A1", "B2", "Paint"));
            // Unreadable text fails closed.
            Assert.Contains("fail closed", TagRules.KeynoteVerificationFailure("keynote_user", 2, "A1", "A1", null, "Paint"));
            Assert.Contains("reads key 'B2'", TagRules.KeynoteVerificationFailure("keynote_user", 2, "B2", "A1", "A1", "Paint"));
            Assert.Contains("expects key source 2", TagRules.KeynoteVerificationFailure("keynote_user", 0, "A1", "A1", "A1", "Paint"));
        }

        [Fact]
        public void Tag_mode_is_case_insensitive_and_absent_is_by_category()
        {
            Assert.Equal("multi_category", TagRules.NormalizeMode(" Multi_Category "));
            Assert.Equal("by_category", TagRules.NormalizeMode(null));
            Assert.True(TagRules.IsKnownMode(TagRules.NormalizeMode("KEYNOTE_USER")));
        }

        [Fact]
        public void A_linked_material_is_not_taggable_with_its_reason_in_a_sweep()
        {
            var r = new AutoTagReport();
            r.Add("OST_Walls", AutoTagReport.LinkedMaterial, "5:20", false, true);
            Assert.Contains("InternalException", (string)r.ToJson()["OST_Walls"]["not_taggable_by_reason"]["linked_material"]["why"]);
            Assert.Contains("AutoTagReport.LinkedMaterial", Src("PlanAnnotationsCommand.AutoTags.cs"));
            Assert.Contains("TagRules.DefaultTagTypeId(loaded, def)", Src("PlanAnnotationsCommand.AutoTags.cs"));
            Assert.Contains("TagRules.KeynoteVerificationFailure(", Src("AnnotateTagKinds.cs"));
            Assert.Contains("DefaultTagType(doc, BuiltInCategory.OST_KeynoteTags", Src("AnnotateTagKinds.cs"));
        }

        private static string Src(string file)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            return File.ReadAllText(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", file));
        }

        [Fact]
        public void Annotate_wires_every_tag_kind_and_re_reads_it()
        {
            string kinds = Src("AnnotateTagKinds.cs");
            Assert.Contains("tag.AddReferences(", kinds);
            Assert.Contains("TagRules.SameTaggedSet(", kinds);
            Assert.Contains("tag.LeaderEndCondition = LeaderEndCondition.Free", kinds);
            Assert.Contains("tag.SetLeaderEnd(main, p.LeaderEnd)", kinds);
            Assert.Contains("tag.SetLeaderElbow(main, p.LeaderElbow)", kinds);
            Assert.Contains("BuiltInParameter.KEY_SOURCE_PARAM", kinds);
            Assert.Contains("BuiltInParameter.KEY_VALUE", kinds);
            Assert.Contains("TagRules.TypeCategoryRefusal(", kinds);
            Assert.Contains("TagRules.LinkedTargetRefusal", Src("AnnotateCommand.cs"));
            string annotate = Src("AnnotateCommand.cs");
            Assert.Contains("new LinkElementId(p.LinkInstance.Id, room.Id)", annotate);
            Assert.Contains("VerifyTagKind(p, tag, out reason)", annotate);
            Assert.Contains("VerifyTagLeader(p, tag, out reason)", annotate);
            Assert.Contains("RoomTagPointsAt(native, p, room)", annotate);
        }

        [Fact]
        public void Auto_tags_sweeps_the_view_links_and_groups_and_reports_per_category()
        {
            string s = Src("PlanAnnotationsCommand.AutoTags.cs");
            Assert.Contains("new FilteredElementCollector(doc, view.Id, link.Id)", s);
            Assert.Contains("#if REVIT2023", s);
            Assert.Contains("private static readonly bool LinkViewCollector", s);
            Assert.Contains("AutoTagReport.LinksNeedRevit2024", s);
            Assert.Contains("[\"by_category\"] = report.ToJson()", s);
            Assert.Contains("e.GroupId", s);
            Assert.Contains("next[\"on_failure\"] = \"best_effort\"", s);
            Assert.DoesNotContain("new Transaction(", s);
        }
    }
}
