// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Sheets and annotations, proved by running the rules. Two sentences this file
// refuses, each with its own test:
//
//   "the sheet is complete because it is not empty"
//   "the view is documented because it has a dimension"
//
// And one Revit fact that makes the first one bite: a schedule placed on a
// sheet is a ScheduleSheetInstance, NOT a viewport, so a sheet full of
// schedules has zero viewports.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class SheetAnnotationTests
    {
        private static SheetRules R(string json) => SheetAnnotationRules.Read(JToken.Parse(json));

        private static SheetStateFact S(string number, string name = "Plan", int titleBlocks = 1,
                                        int viewports = 1, int schedules = 0, int revisions = 0)
        {
            return new SheetStateFact
            {
                ElementId = number == null ? 0 : number.GetHashCode() & 0xffff,
                Number = number,
                Name = name,
                TitleBlockCount = titleBlocks,
                ViewportCount = viewports,
                ScheduleInstanceCount = schedules,
                RevisionCount = revisions
            };
        }

        private static List<string> Codes(List<SheetFinding> f) => f.Select(x => x.Code).ToList();

        // ---------------------------------------------- schedules vs viewports

        [Fact]
        public void A_sheet_of_schedules_has_no_viewports_and_is_not_empty()
        {
            // THE ONE THAT MATTERS. A schedule is placed as a ScheduleSheetInstance,
            // so a check counting only viewports calls this sheet empty.
            SheetStateFact s = S("A-500", viewports: 0, schedules: 3);
            Assert.False(s.IsEmpty);
            Assert.Equal(0, s.ViewportCount);
            Assert.Equal(3, s.ScheduleInstanceCount);
            Assert.Contains("ScheduleSheetInstance and NOT a viewport", SheetAnnotationRules.EmptinessMeans);
        }

        [Fact]
        public void A_sheet_with_nothing_at_all_is_empty()
        {
            Assert.True(S("A-999", viewports: 0, schedules: 0).IsEmpty);
        }

        [Fact]
        public void An_empty_sheet_is_a_finding_only_when_the_caller_forbids_one()
        {
            SheetStateFact s = S("A-999", viewports: 0);
            Assert.Empty(SheetAnnotationRules.Judge(new[] { s }, SheetAnnotationRules.Read(null)));
            Assert.Contains(SheetFindingCodes.SheetEmpty,
                Codes(SheetAnnotationRules.Judge(new[] { s },
                    R(@"{ ""version"": ""v1"", ""forbid_empty_sheets"": true }"))));
        }

        [Fact]
        public void A_sheet_that_is_not_empty_is_never_reported_as_complete()
        {
            // There is no "complete" anywhere: not-empty is a fact whose opposite
            // proves nothing, and the reply says so.
            JObject j = SheetAnnotationRules.ToJson(S("A-101"));
            Assert.False(j.Value<bool>("is_empty"));
            Assert.Null(j["is_complete"]);
            Assert.Contains("OPPOSITE proves nothing", SheetAnnotationRules.EmptinessMeans);
        }

        // ------------------------------------------------------------ numbers

        [Fact]
        public void A_duplicate_sheet_number_is_a_fact_computed_whether_or_not_a_rule_asks()
        {
            // Two sheets with one number is not a matter of opinion. Whether it is
            // a FINDING is.
            var sheets = new[] { S("A-101"), S("A-101"), S("A-102") };
            Assert.Equal(new[] { "A-101" }, SheetAnnotationRules.DuplicateNumbers(sheets).ToArray());
            Assert.Empty(SheetAnnotationRules.Judge(sheets, SheetAnnotationRules.Read(null)));

            List<string> codes = Codes(SheetAnnotationRules.Judge(sheets,
                R(@"{ ""version"": ""v1"", ""forbid_duplicate_numbers"": true }")));
            Assert.Equal(2, codes.Count(c => c == SheetFindingCodes.NumberDuplicate));
        }

        [Fact]
        public void An_unreadable_number_is_not_counted_as_a_duplicate_or_as_empty()
        {
            var s = S(null);
            s.NumberReadable = false;
            Assert.Empty(SheetAnnotationRules.DuplicateNumbers(new[] { s, s }));
            Assert.False(s.NumberEmpty);
        }

        [Fact]
        public void An_empty_number_or_name_is_reported_without_any_rule()
        {
            // A sheet with no number is broken in a way no organisation disagrees
            // about, so it needs no profile to say so.
            SheetStateFact s = S("   ", name: "");
            List<string> codes = Codes(SheetAnnotationRules.Judge(new[] { s }, R(@"{ ""version"": ""v1"" }")));
            Assert.Contains(SheetFindingCodes.NumberEmpty, codes);
            Assert.Contains(SheetFindingCodes.NameEmpty, codes);
        }

        // ------------------------------------------------------- title blocks

        [Fact]
        public void A_missing_title_block_and_several_title_blocks_are_different_findings()
        {
            SheetRules r = R(@"{ ""version"": ""v1"", ""title_block_required"": true,
                                 ""forbid_multiple_title_blocks"": true }");
            Assert.Contains(SheetFindingCodes.TitleBlockMissing,
                Codes(SheetAnnotationRules.Judge(new[] { S("A", titleBlocks: 0) }, r)));
            Assert.Contains(SheetFindingCodes.TitleBlockMultiple,
                Codes(SheetAnnotationRules.Judge(new[] { S("B", titleBlocks: 3) }, r)));
            Assert.Empty(SheetAnnotationRules.Judge(new[] { S("C", titleBlocks: 1) }, r));
        }

        [Fact]
        public void A_title_block_count_that_could_not_be_read_produces_no_finding()
        {
            // Unreadable is not zero. Reporting "no title block" from a failed read
            // sends somebody to a sheet that has one.
            var s = S("A", titleBlocks: 0);
            s.Unreadable.Add("title_blocks");
            Assert.Empty(SheetAnnotationRules.Judge(new[] { s },
                R(@"{ ""version"": ""v1"", ""title_block_required"": true }")));
            Assert.Null(SheetAnnotationRules.ToJson(s)["title_block_count"].Value<int?>());
        }

        // --------------------------------------------------------- viewports

        [Fact]
        public void Viewport_bounds_count_viewports_and_never_the_schedules()
        {
            // A sheet with 4 schedules and no viewport still violates min_viewports
            // if that is what the caller asked for, and the message says why.
            SheetStateFact s = S("A-500", viewports: 0, schedules: 4);
            SheetFinding f = Assert.Single(SheetAnnotationRules.Judge(new[] { s },
                R(@"{ ""version"": ""v1"", ""min_viewports"": 1 }")));
            Assert.Equal(SheetFindingCodes.TooFewViewports, f.Code);
            Assert.Contains("Schedules are counted separately", f.Detail);
        }

        [Fact]
        public void A_minimum_above_the_maximum_is_refused_as_unsatisfiable()
        {
            SheetRules r = R(@"{ ""version"": ""v1"", ""min_viewports"": 5, ""max_viewports"": 2 }");
            Assert.False(r.Ok);
            Assert.Contains("nothing can satisfy", r.Message);
        }

        // -------------------------------------------------------- annotations

        [Fact]
        public void A_view_with_one_dimension_is_not_a_documented_view()
        {
            // No minimum is applied unless the caller declared one, so a single
            // dimension produces no verdict at all.
            var c = new AnnotationCensus { ViewId = 1, ViewType = "Section" };
            c.ByKind[AnnotationKinds.Dimensions] = 1;

            Assert.Empty(SheetAnnotationRules.BelowMinimum(new[] { c }, R(@"{ ""version"": ""v1"" }")));
            Assert.Contains("not a measure of documentation", SheetAnnotationRules.AnnotationMeans);
        }

        [Fact]
        public void A_declared_minimum_is_applied_only_to_the_view_types_it_names()
        {
            var section = new AnnotationCensus { ViewId = 1, ViewType = "Section" };
            var plan = new AnnotationCensus { ViewId = 2, ViewType = "FloorPlan" };
            SheetRules r = R(@"{ ""version"": ""v1"", ""min_annotations_by_view_type"": { ""Section"": 5 } }");

            List<AnnotationCensus> below = SheetAnnotationRules.BelowMinimum(new[] { section, plan }, r);
            Assert.Equal(1, Assert.Single(below).ViewId);
        }

        [Fact]
        public void Annotation_counts_are_kept_per_kind_because_they_are_not_interchangeable()
        {
            var c = new AnnotationCensus { ViewId = 1, ViewType = "FloorPlan" };
            c.ByKind[AnnotationKinds.Dimensions] = 2;
            c.ByKind[AnnotationKinds.Tags] = 3;

            JObject j = SheetAnnotationRules.ToJson(c);
            Assert.Equal(5, j.Value<long>("total"));
            Assert.Equal(2, j["by_kind"].Value<long>(AnnotationKinds.Dimensions));
            Assert.Equal(3, j["by_kind"].Value<long>(AnnotationKinds.Tags));
            // Every kind is present, so a reader never has to guess whether a
            // missing key means zero or means nobody looked.
            foreach (string k in AnnotationKinds.All) Assert.NotNull(j["by_kind"][k]);
        }

        [Fact]
        public void A_view_with_no_annotations_reports_zeros_and_that_is_a_real_zero()
        {
            var c = new AnnotationCensus { ViewId = 1, ViewType = "FloorPlan" };
            JObject j = SheetAnnotationRules.ToJson(c);
            Assert.Equal(0, j.Value<long>("total"));
            Assert.Equal(0, j.Value<long>("unreadable"));
        }

        [Fact]
        public void Unreadable_annotations_are_counted_apart_from_the_kinds()
        {
            var c = new AnnotationCensus { ViewId = 1, ViewType = "FloorPlan", Unreadable = 4 };
            c.ByKind[AnnotationKinds.Text] = 1;
            JObject j = SheetAnnotationRules.ToJson(c);
            Assert.Equal(1, j.Value<long>("total"));
            Assert.Equal(4, j.Value<long>("unreadable"));
        }

        // ----------------------------------------------------------- refusals

        [Fact]
        public void With_no_rules_nothing_is_judged_and_nothing_is_a_pass()
        {
            SheetRules r = SheetAnnotationRules.Read(null);
            Assert.True(r.Absent);
            Assert.Contains("NONE of them is a pass", r.Message);
            Assert.Empty(SheetAnnotationRules.Judge(new[] { S("A", titleBlocks: 0, viewports: 0) }, r));
        }

        [Fact]
        public void Rules_without_a_version_are_refused()
        {
            Assert.Equal(SheetRuleCodes.NoVersion, R(@"{ ""title_block_required"": true }").Code);
        }

        [Fact]
        public void An_unknown_key_refuses_the_whole_rule_set()
        {
            SheetRules r = R(@"{ ""version"": ""v1"", ""title_blocks_required"": true }");
            Assert.Equal(SheetRuleCodes.UnknownKey, r.Code);
            Assert.Contains("title_blocks_required", r.Message);
        }

        [Fact]
        public void A_refused_rule_set_is_not_applied_even_though_it_parsed_earlier_rules()
        {
            SheetRules r = R(@"{ ""version"": ""v1"", ""forbid_empty_sheets"": true, ""bogus"": 1 }");
            Assert.False(r.Ok);
            Assert.True(r.ForbidEmptySheets);            // it really did parse one
            Assert.Empty(SheetAnnotationRules.Judge(new[] { S("A", viewports: 0) }, r));
        }

        [Fact]
        public void An_explicit_exception_is_honoured()
        {
            SheetRules r = R(@"{ ""version"": ""v1"", ""forbid_empty_sheets"": true, ""exceptions"": [""A-999""] }");
            Assert.Empty(SheetAnnotationRules.Judge(new[] { S("A-999", viewports: 0) }, r));
            Assert.NotEmpty(SheetAnnotationRules.Judge(new[] { S("A-998", viewports: 0) }, r));
        }

        // ---- a rule nobody judges is worse than a rule nobody wrote -----------------

        /// <summary>
        /// EVERY RULE FIELD MUST BE READ WHERE THE RULES ARE JUDGED.
        ///
        /// required_schedule_names was declared, parsed, validated as an array - and read
        /// by nothing. Judge never mentioned it, and the sheet facts carry only
        /// ScheduleInstanceCount, so no required name was ever compared against anything.
        /// A caller who asked for those schedules got a scan with no finding about them
        /// and reasonably concluded the sheets carried them. That is a false clean
        /// manufactured by the diagnostic itself, and the parser is what made it look
        /// legitimate: it accepted the key and even type-checked it.
        ///
        /// This walks the SheetRules fields by REFLECTION rather than by a list, so a new
        /// rule is covered the day it is added instead of the day somebody remembers to
        /// add it here. Adding a field and a parse case without a judgement now fails.
        ///
        /// The bookkeeping fields are excluded by name: they describe the parse result
        /// itself, not a rule to evaluate.
        /// </summary>
        [Fact]
        public void Every_sheet_rule_field_is_read_where_the_rules_are_judged()
        {
            var bookkeeping = new HashSet<string>(StringComparer.Ordinal)
            { "Ok", "Absent", "Code", "Message", "Version" };

            string source = File.ReadAllText(Path.Combine(
                RulesRepoRoot(), "src", "Horizun.Revit", "Core", "SheetAnnotationRules.cs"));

            // The judging half of the file: everything from the first method that reaches a
            // verdict onward. Declarations and parsing live above it, which is exactly the
            // distinction that hid the defect.
            int judging = source.IndexOf("public static List<string> DuplicateNumbers",
                                         StringComparison.Ordinal);
            Assert.True(judging > 0, "the judging region of SheetAnnotationRules.cs was not found");
            string region = source.Substring(judging);

            var unjudged = typeof(SheetRules).GetFields()
                .Where(f => !bookkeeping.Contains(f.Name))
                .Where(f => region.IndexOf(f.Name, StringComparison.Ordinal) < 0)
                .Select(f => f.Name)
                .ToList();

            Assert.True(unjudged.Count == 0,
                "these SheetRules fields are parsed and never judged, so a caller who sets " +
                "them gets silence that reads as compliance: " + string.Join(", ", unjudged));
        }

        private static string RulesRepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }
}
}
