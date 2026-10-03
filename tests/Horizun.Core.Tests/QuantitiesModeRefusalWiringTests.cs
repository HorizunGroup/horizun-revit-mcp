// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The volume-mode refusal names, for each key it refuses, the modes that DO read
// it - looked up in QuantitiesModeArguments.ModesReadingKey. It used to say
// "takeoff, room_finishes, carbon" for every key, which sent a caller who passed
// rows_file (takeoff only) to two modes that refuse it too (course rehearsal
// follow-up, 2026-10-03). A key added to the refusal list without an entry in
// the map would throw KeyNotFoundException at runtime instead of refusing: this
// reads both lists out of the source and holds them together.
// -----------------------------------------------------------------------------
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class QuantitiesModeRefusalWiringTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new DirectoryNotFoundException("repository root not found above " + System.AppContext.BaseDirectory);
        }

        private static string Source(string file) =>
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", file));

        [Fact]
        public void Every_key_volume_mode_refuses_has_the_modes_that_read_it()
        {
            string command = Source("QuantitiesCommand.cs");
            Match list = Regex.Match(command, @"foreach \(string takeoffOnly in new\[\] \{([^}]*)\}\)");
            Assert.True(list.Success, "the volume-mode refusal list was not found in QuantitiesCommand.cs");
            string[] refused = Regex.Matches(list.Groups[1].Value, "\"([a-z_]+)\"").Cast<Match>()
                                    .Select(m => m.Groups[1].Value).ToArray();
            Assert.Contains("rows_file", refused);
            Assert.Contains("categories", refused);

            string modes = Source("QuantitiesModeArguments.cs");
            int start = modes.IndexOf("ModesReadingKey = new");
            Assert.True(start >= 0, "ModesReadingKey was not found in QuantitiesModeArguments.cs");
            string map = modes.Substring(start, modes.IndexOf("};", start) - start);
            foreach (string key in refused)
                Assert.True(map.Contains("[\"" + key + "\"] ="), "ModesReadingKey has no entry for '" + key + "'");

            Assert.Contains("ModesReadingKey[takeoffOnly]", command);
            Assert.Matches("\\[\"rows_file\"\\] = \"takeoff\",", map);
        }
    }
}
