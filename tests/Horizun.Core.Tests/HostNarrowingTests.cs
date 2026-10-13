// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// host_ids ONLY INTERSECTS (backlog P0 #7, 2026-10-10). The contract says "Narrow every
// rule to these hosts. The rules still select; this only intersects." The resolver
// refused every rule with no host inside host_ids as host_not_found ("no element in
// this document matches"), and one required refusal aborts the whole apply - so a
// 40-rule set applied to the beams of one floor wrote nothing, and the audit of that
// floor reported differences on a correct model. A rule whose hosts all lie outside
// host_ids is now SKIPPED, not refused; a rule that matches nothing at all still is.
// -----------------------------------------------------------------------------
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class HostNarrowingTests
    {
        [Fact]
        public void A_rule_whose_hosts_all_lie_outside_host_ids_is_skipped_not_refused()
        {
            Assert.Equal(HostNarrowing.Outcome.SkipOutsideHostIds, HostNarrowing.Decide(matchedInDocument: 12, matchedInHostIds: 0, narrowed: true));
        }

        [Fact]
        public void A_rule_matching_nothing_is_still_refused_with_or_without_host_ids()
        {
            Assert.Equal(HostNarrowing.Outcome.HostNotFound, HostNarrowing.Decide(0, 0, narrowed: true));
            Assert.Equal(HostNarrowing.Outcome.HostNotFound, HostNarrowing.Decide(0, 0, narrowed: false));
        }

        [Fact]
        public void A_rule_with_hosts_inside_builds()
        {
            Assert.Equal(HostNarrowing.Outcome.Build, HostNarrowing.Decide(12, 3, narrowed: true));
            Assert.Equal(HostNarrowing.Outcome.Build, HostNarrowing.Decide(12, 12, narrowed: false));
        }

        [Fact]
        public void Every_host_resolution_in_the_resolver_goes_through_the_decision()
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            string text = File.ReadAllText(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", "ReinforcementResolver.cs"));
            // cover, rebar, stirrup zone and mat rules each resolve hosts; none may refuse a
            // rule only because host_ids left it nothing.
            int decisions = Regex.Matches(text, @"Scope\(doc, rule\.Host, narrowTo, hosts\.Count\)").Count;
            Assert.True(decisions >= 4, "the host_ids scope is checked " + decisions + " time(s); cover, rebar, zone and mat rules all need it");
            Assert.Contains("HostNarrowing.Decide(", text);
            // Section rules (2.3.0) resolve hosts too: the same intersection applies there.
            int section = text.IndexOf("public static List<ResolvedRebarRow> ExpandSectionRule(", System.StringComparison.Ordinal);
            if (section >= 0)
            {
                int end = text.IndexOf("SectionFrameOf(host", section, System.StringComparison.Ordinal);
                Assert.True(end > section, "ExpandSectionRule no longer frames hosts where this test expects");
                Assert.Contains("Scope(doc, rule.Host, narrowTo, hosts.Count)", text.Substring(section, end - section));
            }
        }
    }
}
