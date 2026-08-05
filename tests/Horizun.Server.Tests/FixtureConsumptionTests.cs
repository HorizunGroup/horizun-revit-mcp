// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// NO DECORATIVE FIXTURES.
//
// VoidFamilyDocument was a parameter, an entry in fixtures_present and a line in
// the example JSON that NO live probe ever consumed - its only consumer,
// family_mirror_void, had been retired. A fixture nobody reads is worse than
// absent: it advertises coverage that does not exist, and a machine that has to
// hand-craft one (a family with a void that cuts a solid) spends real effort on a
// slot that changes nothing.
//
// So this holds the line: every fixture the run REPORTS as present in
// fixtures_present must be CONSUMED somewhere - referenced as $Name outside its
// own declaration and outside the fixtures_present bookkeeping block. A future
// decorative fixture fails here instead of shipping as fake coverage.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Server.Tests
{
    public class FixtureConsumptionTests
    {
        private static string ScriptPath()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "scripts", "verify-live.ps1")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate scripts/verify-live.ps1");
            return Path.Combine(d.FullName, "scripts", "verify-live.ps1");
        }

        private static string Script() => File.ReadAllText(ScriptPath());

        /// <summary>The names the run reports in its fixtures_present block.</summary>
        private static List<string> DeclaredFixtures(string text)
        {
            const string marker = "fixtures_present";
            int start = text.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, "the fixtures_present block is gone");
            int open = text.IndexOf("@{", start, StringComparison.Ordinal);
            int end = text.IndexOf("}", open, StringComparison.Ordinal);
            string block = text.Substring(open, end - open);

            return Regex.Matches(block, @"(\w+)\s*=\s*-not \[string\]::IsNullOrWhiteSpace")
                        .Select(m => m.Groups[1].Value)
                        .ToList();
        }

        /// <summary>The [start,end) character span of the fixtures_present block, so a
        /// reference INSIDE it does not count as consumption.</summary>
        private static (int start, int end) FixturesBlockSpan(string text)
        {
            int marker = text.IndexOf("fixtures_present", StringComparison.Ordinal);
            int open = text.IndexOf("@{", marker, StringComparison.Ordinal);
            int end = text.IndexOf("}", open, StringComparison.Ordinal);
            return (marker, end);
        }

        [Fact]
        public void Every_reported_fixture_is_consumed_by_something_other_than_its_bookkeeping()
        {
            string text = Script();
            List<string> fixtures = DeclaredFixtures(text);
            Assert.NotEmpty(fixtures);

            (int blockStart, int blockEnd) = FixturesBlockSpan(text);

            foreach (string name in fixtures)
            {
                // Every place this fixture's variable is used.
                var uses = Regex.Matches(text, @"\$" + Regex.Escape(name) + @"\b")
                                .Cast<Match>()
                                .Select(m => m.Index)
                                .ToList();

                // A use that is a real consumption: not the '[string]$Name' declaration and
                // not inside the fixtures_present block. The declaration is matched by the
                // '[string]$' immediately before it.
                bool consumed = uses.Any(i =>
                {
                    bool insideBlock = i >= blockStart && i <= blockEnd;
                    bool isDeclaration = i >= 8 && text.Substring(i - 8, 8) == "[string]";
                    return !insideBlock && !isDeclaration;
                });

                Assert.True(consumed,
                    $"Fixture '{name}' is declared and reported in fixtures_present but never consumed by a " +
                    "probe. Either wire a probe that uses it or remove it - a fixture nobody reads advertises " +
                    "coverage that does not exist (this is exactly how VoidFamilyDocument rotted).");
            }
        }

        [Fact]
        public void The_retired_VoidFamilyDocument_fixture_is_fully_gone()
        {
            // It fed only the retired family_mirror_void probe. Its history lives in the
            // RETIRED registry; the parameter, the fixtures_present entry and any example
            // JSON line must not linger and re-advertise a capability with no current probe.
            Assert.DoesNotContain("VoidFamilyDocument", Script(), StringComparison.Ordinal);
        }
    }
}
