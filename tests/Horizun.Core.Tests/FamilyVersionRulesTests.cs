// The family version check, without Revit. field cycle ELE/HS (2026-10-24, T1): families saved in
// Revit 2024/2025 handed to a Revit 2023 model failed inside Revit, after the agents had planned
// around them, and were retried.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FamilyVersionRulesTests
    {
        [Theory]
        [InlineData("2025", "2023", FamilyVersionVerdict.Newer)]
        [InlineData("2023", "2023", FamilyVersionVerdict.Same)]
        [InlineData("2021", "2023", FamilyVersionVerdict.Older)]
        [InlineData("Autodesk Revit 2024", "2023", FamilyVersionVerdict.Newer)]
        [InlineData(null, "2023", FamilyVersionVerdict.Unknown)]
        [InlineData("garbage", "2023", FamilyVersionVerdict.Unknown)]
        [InlineData("2025", null, FamilyVersionVerdict.Unknown)]
        public void The_verdict_compares_years_and_an_unreadable_one_is_unknown(string file, string host, FamilyVersionVerdict expected)
            => Assert.Equal(expected, FamilyVersionRules.Judge(file, host));

        [Fact]
        public void The_refusal_names_both_years_the_reason_and_what_to_do()
        {
            string m = FamilyVersionRules.NewerMessage("Toma doble.rfa", "2025", "2023", new List<FamilyAlternate>());
            Assert.StartsWith(FamilyVersionRules.Reason + ":", m);
            Assert.Contains("2025", m);
            Assert.Contains("2023", m);
            Assert.Contains("no way to downgrade", m);
            Assert.Contains("nothing was opened or loaded", m);
            Assert.Contains("No copy", m);
        }

        [Fact]
        public void The_refusal_lists_the_alternates_found()
        {
            var alts = new List<FamilyAlternate> { new FamilyAlternate { Path = @"C:\lib\2023\Toma doble.rfa", SavedIn = "2023" } };
            string m = FamilyVersionRules.NewerMessage("Toma doble.rfa", "2025", "2023", alts);
            Assert.Contains(@"C:\lib\2023\Toma doble.rfa (2023)", m);
            Assert.Contains("Load one of those instead", m);
        }

        [Fact]
        public void The_refusal_is_classified_as_its_own_stable_code()
        {
            string m = FamilyVersionRules.NewerMessage("x.rfa", "2025", "2023", null);
            Assert.Equal(ErrorCodes.FamiliaVersionMasNueva, ErrorCodes.Classify(m, null).Code);
        }

        [Theory]
        [InlineData("Toma doble_2025", "Toma doble", true)]
        [InlineData("HZ_SAN-Sifon-Piso R24", "HZ_SAN Sifon Piso", true)]
        [InlineData("Sifón piso v2", "Sifon_Piso", true)]
        [InlineData("Toma doble", "Toma sencilla", false)]
        [InlineData("2025", "2023", false)]
        public void Same_family_name_ignores_years_versions_case_accents_and_punctuation(string a, string b, bool same)
            => Assert.Equal(same, FamilyVersionRules.SameFamilyName(a, b));

        [Fact]
        public void An_alternate_is_the_same_name_saved_in_this_year_or_older()
        {
            Assert.True(FamilyVersionRules.IsAlternate("Toma doble", "Toma doble_2023", "2023", "2023"));
            Assert.True(FamilyVersionRules.IsAlternate("Toma doble", "Toma doble 2020", "2020", "2023"));
            Assert.False(FamilyVersionRules.IsAlternate("Toma doble", "Toma doble_2024", "2024", "2023"));
            Assert.False(FamilyVersionRules.IsAlternate("Toma doble", "Toma doble", null, "2023"));
            Assert.False(FamilyVersionRules.IsAlternate("Toma doble", "Interruptor", "2023", "2023"));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("repository root (global.json) not found");
        }
    }
}
