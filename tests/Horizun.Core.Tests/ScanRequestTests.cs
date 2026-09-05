// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The scan request, and the four ways it used to succeed while answering a
// question nobody asked. Every case below was MEASURED against v1.1.6 before it
// was fixed - they are not hypotheticals.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScanRequestTests
    {
        private static readonly string[] Sections =
        {
            "document", "categories", "cleanliness", "naming", "documentation",
            "project_info", "health", "links", "worksets", "design_options", "lines", "types"
        };

        private static ScanRequestVerdict Check(string json) =>
            ScanRequestRules.Check(JObject.Parse(json), Sections);

        [Fact]
        public void An_ordinary_request_is_accepted()
        {
            Assert.True(Check(@"{ ""target_document_title"": ""M"", ""top"": 20,
                                  ""sections"": [""cleanliness""] }").Ok);
            Assert.True(Check(@"{ ""target_document_title"": ""M"" }").Ok);
        }

        [Fact]
        public void A_misspelt_option_is_refused_and_the_real_ones_are_named()
        {
            // v1.1.6: accepted silently. The schema has no additionalProperties
            // and nothing validates against the schema anyway, so `sectons`
            // produced a full, successful, clean-looking scan of everything.
            ScanRequestVerdict v = Check(@"{ ""target_document_title"": ""M"", ""sectons"": [""links""] }");
            Assert.False(v.Ok);
            Assert.Equal(ScanRequestCodes.UnknownKey, v.Code);
            Assert.Contains("sectons", v.Message);
            Assert.Contains("sections", v.Message);   // the real list is offered
        }

        [Fact]
        public void Every_unknown_option_is_named_not_just_the_first()
        {
            ScanRequestVerdict v = Check(@"{ ""target_document_title"": ""M"", ""aaa"": 1, ""zzz"": 2 }");
            Assert.False(v.Ok);
            Assert.Contains("aaa", v.Message);
            Assert.Contains("zzz", v.Message);
        }

        [Fact]
        public void The_new_options_are_known()
        {
            Assert.True(Check(@"{ ""target_document_title"": ""M"",
                                  ""section_limits"": { ""links"": 5 }, ""cursor"": ""abc"" }").Ok);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void A_top_below_one_is_refused_rather_than_clamped(int n)
        {
            // v1.1.6: Math.Max(1, ...) - silently became 1.
            ScanRequestVerdict v = Check(@"{ ""target_document_title"": ""M"", ""top"": " + n + " }");
            Assert.False(v.Ok);
            Assert.Equal(ScanRequestCodes.BadTop, v.Code);
        }

        [Fact]
        public void A_top_above_the_ceiling_is_refused_and_offered_an_alternative()
        {
            // v1.1.6: no ceiling at all, so one call could return every element.
            ScanRequestVerdict v = Check(@"{ ""target_document_title"": ""M"", ""top"": 1000000 }");
            Assert.False(v.Ok);
            Assert.Equal(ScanRequestCodes.BadTop, v.Code);
            Assert.Contains("cursor", v.Message);
            Assert.Contains("section_limits", v.Message);
        }

        [Theory]
        [InlineData(@"""fifty""")]
        [InlineData("1.5")]
        [InlineData("true")]
        public void A_top_of_the_wrong_type_is_refused_by_name(string raw)
        {
            // v1.1.6: Value<int>() threw, and the caller saw an exception type
            // with no mention of 'top'.
            ScanRequestVerdict v = Check(@"{ ""target_document_title"": ""M"", ""top"": " + raw + " }");
            Assert.False(v.Ok);
            Assert.Equal(ScanRequestCodes.BadTop, v.Code);
            Assert.Contains("top", v.Message);
        }

        [Fact]
        public void A_null_top_falls_back_to_the_default_instead_of_throwing()
        {
            // The old guard tested token PRESENCE, and a JSON null is present.
            Assert.True(Check(@"{ ""target_document_title"": ""M"", ""top"": null }").Ok);
        }

        [Fact]
        public void Sections_given_as_a_bare_string_is_refused_not_read_as_all_of_them()
        {
            // v1.1.6: `as JArray` yielded null, and null meant every section. The
            // most plausible client mistake ran the most expensive call there is.
            ScanRequestVerdict v = Check(@"{ ""target_document_title"": ""M"", ""sections"": ""health"" }");
            Assert.False(v.Ok);
            Assert.Equal(ScanRequestCodes.BadSections, v.Code);
            Assert.Contains("every section", v.Message);
        }

        [Fact]
        public void Sections_given_as_an_object_is_refused_too()
        {
            Assert.Equal(ScanRequestCodes.BadSections,
                Check(@"{ ""target_document_title"": ""M"", ""sections"": { ""a"": 1 } }").Code);
        }

        [Fact]
        public void An_empty_sections_array_is_refused_because_it_used_to_mean_all_twelve()
        {
            ScanRequestVerdict v = Check(@"{ ""target_document_title"": ""M"", ""sections"": [] }");
            Assert.False(v.Ok);
            Assert.Equal(ScanRequestCodes.EmptySections, v.Code);
            Assert.Contains("Omit", v.Message);       // it says how to get all of them
            Assert.Contains("cleanliness", v.Message); // and lists the real ones
        }

        [Fact]
        public void A_non_string_inside_sections_is_refused()
        {
            Assert.Equal(ScanRequestCodes.BadSections,
                Check(@"{ ""target_document_title"": ""M"", ""sections"": [""links"", 7] }").Code);
        }

        [Fact]
        public void A_null_sections_means_all_of_them_as_before()
        {
            Assert.True(Check(@"{ ""target_document_title"": ""M"", ""sections"": null }").Ok);
        }

        [Fact]
        public void A_target_parameter_with_no_types_section_is_reported_as_doing_nothing()
        {
            // It is read by the 'types' section and by nothing else, so accepting
            // it alongside `sections:["cleanliness"]` returns a clean-looking reply
            // that never looked at a single parameter.
            JObject r = JObject.Parse(@"{ ""target_document_title"": ""M"", ""target_parameter"": ""Keynote"" }");
            Assert.True(ScanRequestRules.TargetParameterWouldBeIgnored(r, new List<string> { "cleanliness" }));
            Assert.False(ScanRequestRules.TargetParameterWouldBeIgnored(r, new List<string> { "types" }));
            Assert.False(ScanRequestRules.TargetParameterWouldBeIgnored(r, new List<string> { "TYPES" }));

            JObject none = JObject.Parse(@"{ ""target_document_title"": ""M"" }");
            Assert.False(ScanRequestRules.TargetParameterWouldBeIgnored(none, new List<string> { "cleanliness" }));

            JObject blank = JObject.Parse(@"{ ""target_document_title"": ""M"", ""target_parameter"": ""  "" }");
            Assert.False(ScanRequestRules.TargetParameterWouldBeIgnored(blank, new List<string> { "cleanliness" }));
        }

        [Fact]
        public void The_scan_command_checks_the_request_shape_before_reading_anything()
        {
            // A SOURCE-LEVEL test, and deliberately labelled as one. ModelScanCommand
            // cannot be constructed without a Revit UIApplication, so the WIRING - as
            // opposed to the rule, which is exercised behaviourally by every other
            // test in this file - has no other way to be held. What it pins is that
            // the check runs BEFORE the document is read, and that `top` is taken as
            // given rather than repaired.
            string src = System.IO.File.ReadAllText(Source("src/Horizun.Revit/Commands/ModelScanCommand.cs"));
            string code = string.Join("\n", src.Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", System.StringComparison.Ordinal)));

            Assert.Contains("ScanRequestRules.Check(request, AllSections)", code);
            Assert.Contains("if (!shape.Ok) return CommandResult.Fail(", code);

            int check = code.IndexOf("ScanRequestRules.Check(", System.StringComparison.Ordinal);
            int read = code.IndexOf("request.Value<string>(\"target_document_title\")", System.StringComparison.Ordinal);
            Assert.True(check > 0 && check < read, "the shape is checked after the request is acted on");

            // The clamp is gone: a limit nobody can honour is refused, not repaired.
            Assert.DoesNotContain("Math.Max(1, request.Value<int>(\"top\"))", code);
        }

        private static string Source(string relative)
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(dir.FullName, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        [Fact]
        public void A_null_request_is_not_a_refusal()
        {
            Assert.True(ScanRequestRules.Check(null, Sections).Ok);
        }
    }
}
