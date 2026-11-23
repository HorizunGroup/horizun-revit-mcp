// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Structure, proved by running the rules. As with MEP, the load-bearing
// property is a NEGATIVE one: this area must never assess safety, capacity or
// compliance. A beam's presence is not a statement that it carries its load,
// and a report that drifts from "modelled" to "sound" is worse than no report,
// because somebody will act on it.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class StructureCensusTests
    {
        private static StructuralPopulationFact P(string name, long total = 10, long unreadable = 0)
        {
            var f = new StructuralPopulationFact { Population = name, Total = total, Unreadable = unreadable };
            f.ByMaterial["Concrete"] = total;
            return f;
        }

        private static RebarFact R(bool? host = true, bool coverReadable = true)
        {
            return new RebarFact
            {
                ElementId = 1,
                TypeName = "16 mm",
                HasHost = host,
                HostId = host == true ? 99 : (long?)null,
                HostCategory = host == true ? "Structural Foundations" : null,
                CoverMm = coverReadable ? 40 : (double?)null,
                CoverReadable = coverReadable
            };
        }

        // -------------------------------------------- what is never asserted

        [Fact]
        public void The_section_says_it_assesses_no_safety_or_capacity()
        {
            JObject s = StructureCensusRules.Summary(new[] { P(StructuralPopulations.Columns) }, null);
            string means = s.Value<string>("scope_means");
            Assert.Contains("Nothing here assesses safety", means);
            Assert.Contains("needs an engineer, not a scan", means);

            foreach (string forbidden in new[] { "capacity", "utilisation", "compliant", "adequate", "safe" })
                Assert.Null(s[forbidden]);
        }

        // ---------------------------------------------------------- rebar

        [Fact]
        public void Rebar_with_no_host_is_reported_as_a_fact_with_its_ids()
        {
            List<RebarFact> hostless = StructureCensusRules.WithoutHost(new[] { R(host: true), R(host: false) });
            Assert.Single(hostless);
            Assert.Contains("nearly always is not always", StructureCensusRules.RebarHostMeans);
        }

        [Fact]
        public void Rebar_whose_host_could_not_be_read_is_never_counted_as_hostless()
        {
            // Null is a third state. Counting it as hostless invents a modelling
            // problem out of a read that failed.
            Assert.Empty(StructureCensusRules.WithoutHost(new[] { R(host: null) }));
            Assert.Equal(1, StructureCensusRules.HostUnreadable(new[] { R(host: null) }));

            JObject s = StructureCensusRules.Summary(null, new[] { R(host: null), R(host: false) });
            Assert.Equal(1, s.Value<long>("rebar_without_host"));
            Assert.Equal(1, s.Value<long>("rebar_host_unreadable"));
        }

        [Fact]
        public void A_system_bar_whose_own_host_is_the_slab_has_a_host()
        {
            // Field case (Revit 2026): the 90 bars of an area reinforcement are RebarInSystem, not
            // Rebar. The census only asked Rebar, so they all came out hostless although
            // GetHostId() was the slab.
            Assert.True(StructureCensusRules.HostState(RebarHostSource.SystemBar, false, 4321, null));
            Assert.Equal(4321, StructureCensusRules.ResolvedHostId(RebarHostSource.SystemBar, 4321, null));
        }

        [Fact]
        public void A_system_bar_with_no_host_of_its_own_takes_the_host_of_its_system()
        {
            Assert.True(StructureCensusRules.HostState(RebarHostSource.SystemBar, false, null, 777));
            Assert.Equal(777, StructureCensusRules.ResolvedHostId(RebarHostSource.SystemBar, -1, 777));
            // A set never borrows from a system it does not have.
            Assert.False(StructureCensusRules.HostState(RebarHostSource.RebarSet, false, null, 777));
        }

        [Fact]
        public void An_element_nobody_could_ask_for_its_host_is_unknown_not_hostless()
        {
            Assert.Null(StructureCensusRules.HostState(RebarHostSource.NotAsked, false, null, null));
            Assert.Null(StructureCensusRules.HostState(RebarHostSource.SystemBar, true, null, null));
            Assert.False(StructureCensusRules.HostState(RebarHostSource.SystemBar, false, null, null));
        }

        [Fact]
        public void System_bars_with_a_host_do_not_show_up_in_the_hostless_count()
        {
            var bars = new List<RebarFact>();
            for (int i = 0; i < 90; i++)
                bars.Add(new RebarFact
                {
                    ElementId = 1000 + i,
                    SystemId = 55,
                    HasHost = StructureCensusRules.HostState(RebarHostSource.SystemBar, false, 4321, null),
                    HostId = 4321
                });
            JObject s = StructureCensusRules.Summary(null, bars);
            Assert.Equal(0, s.Value<long>("rebar_without_host"));
            Assert.Equal(90, s.Value<long>("rebar_total"));
        }

        [Fact]
        public void A_cover_that_could_not_be_read_is_null_and_not_zero()
        {
            // Zero cover is a specification. Unreadable is an absence of one, and
            // the two must not print the same number.
            JObject j = StructureCensusRules.ToJson(R(coverReadable: false));
            Assert.Null(j["cover_mm"].Value<double?>());
            Assert.False(j.Value<bool>("cover_readable"));

            JObject ok = StructureCensusRules.ToJson(R(coverReadable: true));
            Assert.Equal(40, ok.Value<double>("cover_mm"));
        }

        // ----------------------------------------------------- populations

        [Fact]
        public void Every_population_appears_even_when_it_was_not_collected()
        {
            // A missing key would have to be read as either zero or "not looked at",
            // and those are different answers.
            JObject s = StructureCensusRules.Summary(new[] { P(StructuralPopulations.Columns) }, null);
            JObject pops = (JObject)s["populations"];
            foreach (string name in StructuralPopulations.All) Assert.NotNull(pops[name]);

            Assert.Equal("not_collected", pops[StructuralPopulations.Rebar].Value<string>("status"));
            Assert.Equal(10, pops[StructuralPopulations.Columns].Value<long>("total"));
        }

        [Fact]
        public void An_unreadable_element_makes_its_population_inexact_and_the_summary_too()
        {
            StructuralPopulationFact bad = P(StructuralPopulations.Framing, total: 5, unreadable: 2);
            Assert.False(bad.CountsAreExact);

            JObject s = StructureCensusRules.Summary(new[] { P(StructuralPopulations.Columns), bad }, null);
            Assert.False(s.Value<bool>("counts_are_exact"));
        }

        [Fact]
        public void A_clean_census_reports_exact_counts()
        {
            JObject s = StructureCensusRules.Summary(
                new[] { P(StructuralPopulations.Columns), P(StructuralPopulations.Walls) },
                new[] { R() });
            Assert.True(s.Value<bool>("counts_are_exact"));
        }

        [Fact]
        public void Structural_material_is_broken_down_and_ranked()
        {
            var f = new StructuralPopulationFact { Population = StructuralPopulations.Framing, Total = 9 };
            f.ByMaterial["Steel"] = 6;
            f.ByMaterial["Concrete"] = 3;
            JArray mats = (JArray)StructureCensusRules.ToJson(f)["by_structural_material"];
            Assert.Equal("Steel", mats[0].Value<string>("material"));
        }

        [Fact]
        public void An_empty_model_reports_every_population_as_not_collected()
        {
            JObject s = StructureCensusRules.Summary(null, null);
            JObject pops = (JObject)s["populations"];
            foreach (string name in StructuralPopulations.All)
                Assert.Equal("not_collected", pops[name].Value<string>("status"));
            Assert.Equal(0, s.Value<long>("rebar_total"));
        }
    }
}
