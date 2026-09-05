// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Naming profiles, proved by running them. The property that matters most is
// what happens when a class has NO rule: it is not_requested, never ok. A clean
// report about a rule nobody wrote is the misreading this surface exists to
// prevent.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class NamingProfileTests
    {
        private static NamingProfile P(string json) => NamingProfileRules.Read(JToken.Parse(json));

        private static List<NamedThing> Things(params string[] names) =>
            names.Select((n, i) => new NamedThing { Id = "e" + i, Name = n }).ToList();

        // ------------------------------------------------------------- profile

        [Fact]
        public void No_profile_means_nothing_is_judged_and_nothing_is_declared_clean()
        {
            NamingProfile p = NamingProfileRules.Read(null);
            Assert.False(p.Ok);

            NamingVerdict v = NamingProfileRules.Check("levels", Things("whatever", "L1"), p);
            Assert.Equal("not_requested", v.Status);
            Assert.Empty(v.Findings);
            Assert.Contains("NOT a pass", v.Limitation);
        }

        [Fact]
        public void A_class_the_profile_is_silent_about_is_not_requested_not_ok()
        {
            // THE ONE THAT MATTERS. Rules for levels say nothing about sheets.
            NamingProfile p = P(@"{ ""version"": ""v1"", ""levels"": { ""prefix"": ""L-"" } }");
            Assert.True(p.Ok);

            Assert.Equal("ok", NamingProfileRules.Check("levels", Things("L-01"), p).Status);
            Assert.Equal("not_requested", NamingProfileRules.Check("sheets", Things("anything at all"), p).Status);
        }

        [Fact]
        public void A_profile_without_a_version_is_refused()
        {
            Assert.Equal(NamingCodes.NoVersion, P(@"{ ""levels"": { ""prefix"": ""L"" } }").Code);
        }

        [Fact]
        public void A_class_that_does_not_exist_is_refused_with_the_real_list()
        {
            NamingProfile p = P(@"{ ""version"": ""v1"", ""levelz"": { ""prefix"": ""L"" } }");
            Assert.False(p.Ok);
            Assert.Equal(NamingCodes.UnknownClass, p.Code);
            Assert.Contains("levels", p.Message);
        }

        [Fact]
        public void An_unknown_rule_key_is_refused()
        {
            Assert.Equal(NamingCodes.UnknownRuleKey,
                P(@"{ ""version"": ""v1"", ""levels"": { ""prefixx"": ""L"" } }").Code);
        }

        [Fact]
        public void An_invalid_regex_is_refused_rather_than_skipped()
        {
            // A rule that silently does not run reports every name as acceptable.
            NamingProfile p = P(@"{ ""version"": ""v1"", ""views"": { ""regex"": ""[unclosed"" } }");
            Assert.False(p.Ok);
            Assert.Equal(NamingCodes.BadRegex, p.Code);
            Assert.Contains("silently does not run", p.Message);
        }

        [Fact]
        public void Segments_without_a_separator_is_refused_as_uncountable()
        {
            NamingProfile p = P(@"{ ""version"": ""v1"", ""sheets"": { ""segments"": 3 } }");
            Assert.False(p.Ok);
            Assert.Contains("separator", p.Message);
        }

        [Fact]
        public void A_minimum_above_the_maximum_is_refused_as_unsatisfiable()
        {
            NamingProfile p = P(@"{ ""version"": ""v1"", ""views"": { ""min_length"": 10, ""max_length"": 3 } }");
            Assert.False(p.Ok);
            Assert.Contains("nothing can satisfy", p.Message);
        }

        [Fact]
        public void A_case_that_is_neither_upper_nor_lower_is_refused()
        {
            Assert.Equal(NamingCodes.BadRule,
                P(@"{ ""version"": ""v1"", ""views"": { ""case"": ""Title"" } }").Code);
        }

        // --------------------------------------------------------------- rules

        [Fact]
        public void Each_rule_names_itself_when_it_fails_and_offers_a_suggestion()
        {
            NamingProfile p = P(@"{ ""version"": ""v1"", ""levels"": { ""prefix"": ""L-"" } }");
            NamingVerdict v = NamingProfileRules.Check("levels", Things("L-01", "Ground"), p);

            Assert.Equal("failed", v.Status);
            Assert.Equal(2, v.Examined);
            Assert.Equal(1, v.Matched);
            NamingFinding f = Assert.Single(v.Findings);
            Assert.Equal("Ground", f.Name);
            Assert.Equal(NamingCodes.PrefixFailed, f.Rule);
            Assert.Equal("L-Ground", f.Suggestion);
        }

        [Theory]
        [InlineData(@"{ ""suffix"": ""_A"" }", "Plan_B", NamingCodes.SuffixFailed)]
        [InlineData(@"{ ""min_length"": 5 }", "ab", NamingCodes.TooShort)]
        [InlineData(@"{ ""max_length"": 3 }", "abcdef", NamingCodes.TooLong)]
        [InlineData(@"{ ""case"": ""upper"" }", "Mixed", NamingCodes.CaseFailed)]
        [InlineData(@"{ ""forbidden"": [""TEMP""] }", "A TEMP B", NamingCodes.Forbidden)]
        [InlineData(@"{ ""default_words"": [""Unnamed""] }", "Unnamed 3", NamingCodes.DefaultWord)]
        [InlineData(@"{ ""allowed"": [""A"",""B""] }", "C", NamingCodes.NotAllowed)]
        [InlineData(@"{ ""regex"": ""^[0-9]+$"" }", "12a", NamingCodes.RegexFailed)]
        [InlineData(@"{ ""separator"": ""-"", ""segments"": 3 }", "A-B", NamingCodes.SegmentsFailed)]
        public void Every_rule_kind_can_fail_and_says_which_one_did(string rule, string name, string expected)
        {
            NamingProfile p = P(@"{ ""version"": ""v1"", ""views"": " + rule + " }");
            Assert.True(p.Ok, p.Message);
            NamingVerdict v = NamingProfileRules.Check("views", Things(name), p);
            Assert.Equal(expected, Assert.Single(v.Findings).Rule);
        }

        [Fact]
        public void Uniqueness_is_judged_over_the_population_not_one_name_at_a_time()
        {
            NamingProfile p = P(@"{ ""version"": ""v1"", ""views"": { ""unique"": true } }");
            NamingVerdict v = NamingProfileRules.Check("views", Things("Plan", "Plan", "Section"), p);

            NamingFinding f = Assert.Single(v.Findings);
            Assert.Equal(NamingCodes.NotUnique, f.Rule);
            Assert.Equal("Plan", f.Name);
            Assert.Contains("2 share", f.Detail);
            Assert.Equal(1, v.Matched);       // only "Section" survives
        }

        [Fact]
        public void An_explicit_exception_is_honoured_and_counted_as_matched()
        {
            NamingProfile p = P(@"{ ""version"": ""v1"",
                ""views"": { ""prefix"": ""V-"", ""exceptions"": [""Legacy Plan""] } }");
            NamingVerdict v = NamingProfileRules.Check("views", Things("V-1", "Legacy Plan", "Other"), p);
            Assert.Single(v.Findings);
            Assert.Equal("Other", v.Findings[0].Name);
            Assert.Equal(2, v.Matched);
        }

        [Fact]
        public void An_unreadable_name_is_counted_apart_and_makes_the_matches_a_lower_bound()
        {
            var things = new List<NamedThing>
            {
                new NamedThing { Id = "1", Name = "V-1" },
                new NamedThing { Id = "2", Name = null, Readable = false },
            };
            NamingProfile p = P(@"{ ""version"": ""v1"", ""views"": { ""prefix"": ""V-"" } }");
            NamingVerdict v = NamingProfileRules.Check("views", things, p);

            Assert.Equal(1, v.Examined);      // the unreadable one was not examined
            Assert.Equal(1, v.Unreadable);
            Assert.Contains("lower bound", v.Limitation);
        }

        [Fact]
        public void Nothing_is_ever_renamed_only_suggested()
        {
            // The suggestion is a string in the report; the input is untouched.
            var things = Things("bad");
            NamingProfile p = P(@"{ ""version"": ""v1"", ""views"": { ""prefix"": ""V-"" } }");
            NamingProfileRules.Check("views", things, p);
            Assert.Equal("bad", things[0].Name);
        }

        [Fact]
        public void An_empty_population_is_ok_rather_than_failed()
        {
            NamingProfile p = P(@"{ ""version"": ""v1"", ""views"": { ""prefix"": ""V-"" } }");
            NamingVerdict v = NamingProfileRules.Check("views", new List<NamedThing>(), p);
            Assert.Equal("ok", v.Status);
            Assert.Equal(0, v.Examined);
        }

        [Fact]
        public void Different_classes_can_carry_genuinely_different_rules()
        {
            // One regex cannot serve a project: a level is named nothing like a sheet.
            NamingProfile p = P(@"{ ""version"": ""v2"",
                ""levels"": { ""regex"": ""^L[0-9]{2}$"" },
                ""sheets"": { ""separator"": ""-"", ""segments"": 2, ""case"": ""upper"" } }");
            Assert.True(p.Ok);

            Assert.Equal("ok", NamingProfileRules.Check("levels", Things("L01"), p).Status);
            Assert.Equal("failed", NamingProfileRules.Check("levels", Things("A-100"), p).Status);
            Assert.Equal("ok", NamingProfileRules.Check("sheets", Things("A-100"), p).Status);
            Assert.Equal("failed", NamingProfileRules.Check("sheets", Things("L01"), p).Status);
        }

        [Fact]
        public void The_report_carries_the_counts_a_reader_needs()
        {
            NamingProfile p = P(@"{ ""version"": ""v1"", ""views"": { ""prefix"": ""V-"" } }");
            JObject j = NamingProfileRules.Check("views", Things("V-1", "x", "y"), p).ToJson();
            Assert.Equal(3, j.Value<int>("examined_count"));
            Assert.Equal(1, j.Value<int>("matched_count"));
            Assert.Equal(0, j.Value<int>("unreadable_count"));
            Assert.Equal(2, j["findings"].Count());
        }
    }
}
