// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// MEP, proved by running the rules. The load-bearing property is a NEGATIVE
// one: this area must never claim calculation from connectivity. Two pipes
// that touch say the model joins them and nothing about flow, size or
// pressure - and a tool that slides from "connected" to "correct" is why
// engineers stop trusting model audits.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MepCensusTests
    {
        private static MepElementFact E(int? systems = 1, long total = 2, long connected = 2,
                                        long open = 0, long unreadable = 0)
        {
            return new MepElementFact
            {
                ElementId = 1,
                Category = "Ducts",
                SystemCount = systems,
                ConnectorsTotal = total,
                ConnectorsConnected = connected,
                ConnectorsOpen = open,
                ConnectorsUnreadable = unreadable
            };
        }

        private static MepSystemFact S(string name, string classification = "Supply Air",
                                       bool readable = true)
        {
            return new MepSystemFact
            {
                ElementId = 1,
                Name = name,
                Classification = classification,
                ClassificationReadable = readable
            };
        }

        // ------------------------------------------------- what is never claimed

        [Fact]
        public void The_section_says_connectivity_is_not_calculation()
        {
            JObject t = MepCensusRules.Tally(new[] { E() }, new[] { S("SA-1") });
            string means = t.Value<string>("connectivity_means");
            Assert.Contains("NOT calculation", means);
            Assert.Contains("whether anything flows", means);

            // And no field claims any of it.
            foreach (string forbidden in new[] { "balanced", "sized", "pressure_drop", "flow_rate" })
                Assert.Null(t[forbidden]);
        }

        [Fact]
        public void An_open_connector_is_a_fact_and_never_a_defect()
        {
            JObject t = MepCensusRules.Tally(new[] { E(total: 2, connected: 1, open: 1) }, null);
            Assert.Equal(1, t.Value<long>("connectors_open"));
            Assert.Contains("not a defect", MepCensusRules.OpenConnectorMeans);
            // Not called an issue anywhere.
            Assert.Null(t["open_connector_issues"]);
        }

        // ------------------------------------------------------ system states

        [Fact]
        public void An_element_in_no_system_is_distinct_from_one_in_several()
        {
            Assert.Equal(MepSystemState.NoSystem, E(systems: 0).State);
            Assert.Equal(MepSystemState.InSystem, E(systems: 1).State);
            Assert.Equal(MepSystemState.MultipleSystems, E(systems: 3).State);
        }

        [Fact]
        public void An_element_whose_system_could_not_be_read_is_not_an_element_without_one()
        {
            // Folding it into no_system reports a modelling gap that may not exist.
            Assert.Equal(MepSystemState.Unreadable, E(systems: null).State);

            JObject t = MepCensusRules.Tally(new[] { E(systems: null), E(systems: 0) }, null);
            Assert.Equal(1, t.Value<long>(MepSystemState.Unreadable));
            Assert.Equal(1, t.Value<long>(MepSystemState.NoSystem));
            Assert.False(t.Value<bool>("counts_are_exact"));
        }

        [Fact]
        public void Multiple_systems_is_reported_rather_than_merged_into_having_one()
        {
            // Normal for some families, a mistake in others - and merging it hides
            // it from the people who would know which.
            JObject t = MepCensusRules.Tally(new[] { E(systems: 2) }, null);
            Assert.Equal(1, t.Value<long>(MepSystemState.MultipleSystems));
            Assert.Equal(0, t.Value<long>(MepSystemState.InSystem));
        }

        // ------------------------------------------------------- connectors

        [Fact]
        public void The_connector_counts_are_published_as_balancing_or_not()
        {
            JObject ok = MepCensusRules.Tally(new[] { E(total: 4, connected: 2, open: 1, unreadable: 1) }, null);
            Assert.True(ok.Value<bool>("connectors_balance"));

            JObject bad = MepCensusRules.Tally(new[] { E(total: 9, connected: 2, open: 1, unreadable: 1) }, null);
            Assert.False(bad.Value<bool>("connectors_balance"));
        }

        [Fact]
        public void An_unreadable_connector_makes_the_counts_inexact()
        {
            JObject t = MepCensusRules.Tally(new[] { E(total: 2, connected: 1, unreadable: 1) }, null);
            Assert.False(t.Value<bool>("counts_are_exact"));
        }

        // ---------------------------------------------------- classification

        [Fact]
        public void A_system_with_no_classification_is_apart_from_one_that_could_not_be_read()
        {
            var none = S("A", classification: null);
            var unreadable = S("B", classification: null, readable: false);

            Assert.Single(MepCensusRules.WithoutClassification(new[] { none, unreadable }));

            JObject t = MepCensusRules.Tally(null, new[] { none, unreadable });
            Assert.Equal(1, t.Value<int>("systems_without_classification"));
            Assert.Equal(1, t.Value<int>("systems_classification_unreadable"));
            Assert.Contains("could not read", MepCensusRules.ClassificationMeans);
        }

        [Fact]
        public void A_classified_system_is_not_counted_as_unclassified()
        {
            Assert.Empty(MepCensusRules.WithoutClassification(new[] { S("A", "Return Air") }));
        }

        [Fact]
        public void An_empty_model_reports_zeros_and_exact_counts()
        {
            JObject t = MepCensusRules.Tally(null, null);
            Assert.Equal(0, t.Value<int>("systems"));
            Assert.Equal(0, t.Value<int>("elements_examined"));
            Assert.True(t.Value<bool>("counts_are_exact"));
            Assert.True(t.Value<bool>("connectors_balance"));
        }

        [Fact]
        public void Every_system_state_appears_so_a_missing_key_never_has_to_be_guessed()
        {
            JObject t = MepCensusRules.Tally(new MepElementFact[0], new MepSystemFact[0]);
            foreach (string s in MepSystemState.All) Assert.NotNull(t[s]);
        }
    }
}
