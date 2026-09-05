// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The naming ROUTE, proved by running it. NamingProfileTests already prove the
// rules; these prove the thing between the rules and the model, where the
// interesting failures live:
//
//   a class silently dropped from the answer
//   a class judged against another class's population
//   "absent" reported as "empty"
//   any of the three no-finding states quietly rendered as a pass
//
// None of those throws. Every one of them reads as a clean model.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class NamingRouteTests
    {
        private static NamingProfile P(string json) => NamingProfileRules.Read(JToken.Parse(json));

        private static List<NamedThing> Things(params string[] names) =>
            names.Select((n, i) => new NamedThing { Id = "e" + i, Name = n }).ToList();

        private static Dictionary<string, List<NamedThing>> All(params (string, List<NamedThing>)[] some)
        {
            // Every class collected, so a test that cares about ONE class is not
            // quietly also testing not_collected.
            var d = new Dictionary<string, List<NamedThing>>();
            foreach (string c in NamingClasses.All) d[c] = new List<NamedThing>();
            foreach ((string cls, List<NamedThing> things) in some) d[cls] = things;
            return d;
        }

        // ------------------------------------------------------- completeness

        [Fact]
        public void Every_class_the_profile_can_mention_appears_in_the_answer()
        {
            // A class missing from the reply is read as "nothing to say about it",
            // and the one thing it must never be read as is a pass.
            JObject j = NamingFromScan.Judge(All(), null, NamingProfileRules.Read(null));
            var classes = (JObject)j["classes"];
            foreach (string c in NamingClasses.All)
                Assert.True(classes[c] != null, "class missing from the reply: " + c);
            Assert.Equal(NamingClasses.All.Length, classes.Properties().Count());
        }

        [Fact]
        public void A_class_nobody_collected_is_reported_as_a_defect_in_this_tool()
        {
            // THE WIRING FAILURE. Not "ok", not omitted: named, with whose fault it is.
            var populations = All();
            populations.Remove("grids");

            JObject j = NamingFromScan.Judge(populations, null,
                P(@"{ ""version"": ""v1"", ""grids"": { ""prefix"": ""G-"" } }"));
            JObject grids = (JObject)j["classes"]["grids"];

            Assert.Equal(NamingStatus.NotCollected, grids.Value<string>("status"));
            Assert.Contains("defect in this tool", grids.Value<string>("reason"));
            Assert.Equal(1, j.Value<int>("classes_not_collected"));
        }

        [Fact]
        public void A_null_population_is_not_collected_rather_than_empty()
        {
            var populations = All();
            populations["links"] = null;
            JObject j = NamingFromScan.Judge(populations, null, NamingProfileRules.Read(null));
            Assert.Equal(NamingStatus.NotCollected, j["classes"]["links"].Value<string>("status"));
        }

        // ------------------------------------------------------ not_applicable

        [Fact]
        public void A_class_that_cannot_exist_here_is_not_applicable_not_an_empty_pass()
        {
            // A document that was never workshared has no worksets. Reporting "ok"
            // over that emptiness is the non-workshared-document-as-clean-result
            // confusion, in one field.
            JObject j = NamingFromScan.Judge(
                All(),
                new[] { new NamingNotApplicable { Class = "worksets", Reason = "this document is not workshared." } },
                P(@"{ ""version"": ""v1"", ""worksets"": { ""prefix"": ""WS-"" } }"));

            JObject ws = (JObject)j["classes"]["worksets"];
            Assert.Equal(NamingStatus.NotApplicable, ws.Value<string>("status"));
            Assert.Contains("not workshared", ws.Value<string>("reason"));
        }

        [Fact]
        public void Not_applicable_wins_over_a_population_that_was_also_supplied()
        {
            // If the command says the class cannot exist, an empty list it also
            // passed must not turn into a pass.
            JObject j = NamingFromScan.Judge(
                All(("worksets", new List<NamedThing>())),
                new[] { new NamingNotApplicable { Class = "worksets", Reason = "not workshared." } },
                P(@"{ ""version"": ""v1"", ""worksets"": { ""prefix"": ""WS-"" } }"));
            Assert.Equal(NamingStatus.NotApplicable, j["classes"]["worksets"].Value<string>("status"));
        }

        // -------------------------------------------------------- the verdicts

        [Fact]
        public void Each_class_is_judged_against_its_own_population()
        {
            // The classic wiring bug is handing `views` to the sheets check. Here
            // the two rules are mutually exclusive, so a swap cannot pass.
            JObject j = NamingFromScan.Judge(
                All(("views", Things("V-01")), ("sheets", Things("A-100"))),
                null,
                P(@"{ ""version"": ""v1"",
                      ""views"": { ""prefix"": ""V-"" },
                      ""sheets"": { ""prefix"": ""A-"" } }"));

            Assert.Equal(NamingStatus.Ok, j["classes"]["views"].Value<string>("status"));
            Assert.Equal(NamingStatus.Ok, j["classes"]["sheets"].Value<string>("status"));
        }

        [Fact]
        public void A_failing_class_is_counted_and_carries_its_findings()
        {
            JObject j = NamingFromScan.Judge(
                All(("levels", Things("L-01", "Ground"))), null,
                P(@"{ ""version"": ""v1"", ""levels"": { ""prefix"": ""L-"" } }"));

            JObject lv = (JObject)j["classes"]["levels"];
            Assert.Equal(NamingStatus.Failed, lv.Value<string>("status"));
            Assert.Equal(2, lv.Value<int>("examined_count"));
            Assert.Equal(1, lv.Value<int>("matched_count"));
            Assert.Equal(1, j.Value<int>("classes_failed"));
            Assert.Equal(1, j.Value<int>("classes_assessed"));
        }

        [Fact]
        public void A_class_the_profile_is_silent_about_is_not_counted_as_assessed()
        {
            // not_requested is not a check that passed, so it must not inflate the
            // number of classes this run actually assessed.
            JObject j = NamingFromScan.Judge(
                All(("levels", Things("L-01")), ("sheets", Things("whatever"))), null,
                P(@"{ ""version"": ""v1"", ""levels"": { ""prefix"": ""L-"" } }"));

            Assert.Equal(NamingStatus.NotRequested, j["classes"]["sheets"].Value<string>("status"));
            Assert.Equal(1, j.Value<int>("classes_assessed"));
        }

        [Fact]
        public void With_no_profile_nothing_is_assessed_and_nothing_is_declared_clean()
        {
            JObject j = NamingFromScan.Judge(
                All(("levels", Things("anything at all"))), null, NamingProfileRules.Read(null));

            Assert.Equal(0, j.Value<int>("classes_assessed"));
            Assert.Equal(NamingStatus.NotRequested, j["profile"].Value<string>("status"));
            foreach (string c in NamingClasses.All)
                Assert.NotEqual(NamingStatus.Ok, j["classes"][c].Value<string>("status"));
        }

        [Fact]
        public void A_refused_profile_is_reported_as_refused_and_not_as_absent()
        {
            // Refused and absent both produce no findings. Only one of them is the
            // caller's mistake, and they must not look alike.
            JObject j = NamingFromScan.Judge(All(), null,
                P(@"{ ""version"": ""v1"", ""views"": { ""regex"": ""[unclosed"" } }"));

            JObject prof = (JObject)j["profile"];
            Assert.Equal("refused", prof.Value<string>("status"));
            Assert.Equal(NamingCodes.BadRegex, prof.Value<string>("code"));
        }

        // ----------------------------------------------------- what it means

        [Fact]
        public void Every_class_publishes_what_its_population_actually_was()
        {
            // Two people counting "types" get different numbers until somebody
            // writes down whether a system family type is one.
            JObject j = NamingFromScan.Judge(All(), null, NamingProfileRules.Read(null));
            foreach (string c in NamingClasses.All)
                Assert.False(string.IsNullOrWhiteSpace(j["classes"][c].Value<string>("population")),
                             "no population description for " + c);

            Assert.Contains("System families are not Family elements",
                            j["classes"]["families"].Value<string>("population"));
        }

        [Fact]
        public void The_reply_says_that_none_of_the_three_empty_states_is_a_pass()
        {
            string means = NamingFromScan.Judge(All(), null, NamingProfileRules.Read(null)).Value<string>("means");
            Assert.Contains("NONE of them is a pass", means);
            Assert.Contains("not_collected", means);
        }
    }
}
