// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A rounded number concatenated into a message follows the THREAD's culture. On an
// es-CO machine Revit's thread formats 241.4 as "241,4" - measured live 2026-10-09
// in horizun_cad_connect's transition sentence - so a reply said one number in its
// fields and another in its words. A source check, because the Revit half cannot
// run here: a literal followed by + Math.Round(x, digits) must format the result
// with an explicit culture. Math.Round(x) with no digits is an integer and has no
// decimal separator to get wrong.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class MessageNumbersInvariantTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        // "..." + Math.Round(<args>, <digits>) NOT followed by .ToString(...)
        private static readonly Regex Concatenated = new Regex(
            "\"\\s*\\+\\s*Math\\.Round\\((?:[^()]|\\([^()]*\\))*,\\s*\\d+\\)(?!\\s*\\.)", RegexOptions.Compiled);

        // Fixed, with its own test under es-CO, on fix/verificacion-comandos-t4 (CadTransitionWords).
        private static readonly string[] FixedElsewhere = { "CadConnectCommand.cs" };

        [Fact]
        public void No_command_message_concatenates_a_rounded_decimal_in_the_thread_culture()
        {
            string dir = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands");
            var offenders = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !FixedElsewhere.Contains(Path.GetFileName(f)))
                .SelectMany(f => Concatenated.Matches(File.ReadAllText(f)).Cast<Match>()
                    .Select(m => Path.GetFileName(f) + ": " + m.Value.Replace("\r", " ").Replace("\n", " ")))
                .ToList();
            Assert.True(offenders.Count == 0, "culture-dependent numbers in messages:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void The_check_sees_the_pattern_it_names_and_not_the_safe_forms()
        {
            Assert.Matches(Concatenated, "\"moved \" + Math.Round(dev, 1) + \" mm\"");
            Assert.Matches(Concatenated, "\"planned \" + Math.Round(want * 2, 2);");
            Assert.DoesNotMatch(Concatenated, "\"moved \" + Math.Round(dev, 1).ToString(CultureInfo.InvariantCulture)");
            Assert.DoesNotMatch(Concatenated, "\"inside 0..\" + Math.Round(height * 304.8) + \" mm\"");
        }
    }
}
