// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE APPLY CARRIES EVERY VERIFICATION THE CREATE EMITS.
//
// horizun_create_elements re-reads each element after the commit and says what
// it found. horizun_apply_cad_plan used to summarise all of that to a count,
// and a door came back "created" with no way to see whether Revit had put it in
// a wall. The rows now travel, trimmed to the verification fields.
//
// A hand-written list of fields is a list that will be forgotten - and it was,
// immediately: structural_verified was added to the create command and not to
// the list, so the apply reported a load-bearing wall as merely created, which
// is the exact distinction the field exists to draw.
//
// So the list is checked against the source of the thing that fills it.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadApplyVerificationTests
    {
        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit")))
                dir = dir.Parent;
            Assert.True(dir != null, "the repository root must be findable from the test binary");
            string path = Path.Combine(new[] { dir.FullName, "src", "Horizun.Revit" }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), path + " must exist");
            return File.ReadAllText(path);
        }

        /// <summary>Every verifyRow["..."] key horizun_create_elements can emit.</summary>
        private static HashSet<string> KeysCreateEmits()
        {
            string create = Source("Commands", "CreateElementsCommand.cs");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(create, @"verifyRow\[""(?<k>[a-z_]+)""\]\s*="))
                keys.Add(m.Groups["k"].Value);
            // The row is also built with an object initialiser; take those too.
            Match block = Regex.Match(create, @"var verifyRow = new JObject\s*\{(?<body>[^}]*)\}",
                                      RegexOptions.Singleline);
            Assert.True(block.Success, "the verification row must still be built as a JObject initialiser");
            foreach (Match m in Regex.Matches(block.Groups["body"].Value, @"\[""(?<k>[a-z_]+)""\]"))
                keys.Add(m.Groups["k"].Value);
            return keys;
        }

        [Fact]
        public void The_apply_carries_every_verification_key_the_create_can_emit()
        {
            string apply = Source("Commands", "ApplyCadPlanCommand.cs");
            Match list = Regex.Match(apply,
                @"VerificationFields\s*=\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
            Assert.True(list.Success, "ApplyCadPlanCommand must declare VerificationFields");

            var carried = new HashSet<string>(
                Regex.Matches(list.Groups["body"].Value, @"""(?<k>[a-z_]+)""")
                     .Cast<Match>().Select(m => m.Groups["k"].Value), StringComparer.Ordinal);

            List<string> missing = KeysCreateEmits().Where(k => !carried.Contains(k)).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0,
                "horizun_apply_cad_plan drops " + string.Join(", ", missing) + " from the rows it reports. " +
                "A field create_elements emits as evidence of what it RE-READ must reach the caller, or the " +
                "apply claims created where the create claimed verified.");
        }

        [Fact]
        public void The_parameter_writer_is_REHEARSED_before_it_is_spent()
        {
            // horizun_write_params_verified will not write without a confirmation
            // token from its own rehearsal. The apply called it straight through
            // with dry_run=false and got "No such confirmation token" back - every
            // time, for every requirement set that declared a parameter. The
            // capability existed on paper from the day it was written: the plan
            // carried the values, the apply handed them over, the writer refused
            // the handover, and nothing above it said so until a model was asked
            // what it actually held.
            string source = Source("Commands", "ApplyCadPlanCommand.cs");
            int at = source.IndexOf("private static JObject ApplyParameters(", StringComparison.Ordinal);
            Assert.True(at >= 0, "the apply must still delegate parameters to the one verified writer");
            string body = source.Substring(at);

            Assert.Contains("confirmation_token", body);
            Assert.Contains("[\"dry_run\"] = true", body);
            Assert.Contains("[\"dry_run\"] = false", body);
        }

        [Fact]
        public void required_false_decides_whether_a_missing_value_STOPS_the_conversion()
        {
            // `required` is parsed by the loader, carried on the plan row and acted
            // on by the audit - and the apply read it nowhere, so a nice-to-have
            // that could not be written abandoned every remaining stage. That is
            // the opposite of what the key means and of what this bridge's own
            // documentation promises.
            string source = Source("Commands", "ApplyCadPlanCommand.cs");
            int at = source.IndexOf("private static JObject ApplyParameters(", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string body = source.Substring(at);

            Assert.Contains("required_missing", body);
            Assert.Contains("declared", body);
            // and the stop decision keys on the required ones, not on every value
            Assert.Contains("required_missing", source.Substring(0, at));
        }

        [Fact]
        public void The_distinctions_that_matter_most_are_named_explicitly()
        {
            // A regex can drift with the source it reads. These three are the ones
            // whose absence produced a real false report, so they are pinned by
            // name as well.
            string apply = Source("Commands", "ApplyCadPlanCommand.cs");
            foreach (string key in new[] { "host_verified", "structural_verified", "curve_verified" })
                Assert.Contains("\"" + key + "\"", apply);
        }
    }
}
