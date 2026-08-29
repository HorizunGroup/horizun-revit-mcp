using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// THE GATE MUST NAME CHECKS THAT EXIST.
    ///
    /// The pre-delivery gate reads its measurements out of the audit's findings,
    /// keyed by the finding's own `check` string. The two halves spelled those
    /// names independently and one was wrong: `forbid_orphan_group_types` pointed
    /// at "group_types" while the finding emits "orphan_group_types". The lookup
    /// could never hit, the row was permanently `not_measurable`, and a
    /// requirement set containing that key could never return the verdict `pass`
    /// - whatever the model was like.
    ///
    /// It failed in the safe direction, which is exactly why it lasted: a gate
    /// that will not pass reads as a strict gate. The cost was a declared standard
    /// that was never enforced.
    /// </summary>
    public class AuditCheckNameTests
    {
        [Fact]
        public void Every_check_the_gate_maps_onto_is_a_name_the_audit_can_emit()
        {
            var unmeasurable = PreDeliveryGateRules.MappedCheckNames()
                .Where(n => !AuditCheckNames.IsMeasurable(n))
                .ToList();

            Assert.True(unmeasurable.Count == 0,
                "these requirements point at a check no finding carries, so they can never do anything but " +
                "report not_measurable: " + string.Join(", ", unmeasurable));
        }

        [Fact]
        public void Every_requirement_the_gate_advertises_maps_somewhere()
        {
            // KnownRequirements is what the contract text promises a caller may
            // declare. A name advertised and not mapped would refuse the whole
            // gate as unknown, which is a worse failure than not measuring.
            foreach (string requirement in PreDeliveryGateRules.KnownRequirements())
                Assert.False(string.IsNullOrWhiteSpace(requirement));

            Assert.Equal(
                PreDeliveryGateRules.KnownRequirements().Count(),
                PreDeliveryGateRules.MappedCheckNames().Count());
        }

        [Fact]
        public void The_audit_command_emits_exactly_the_names_declared_here()
        {
            // AuditModelCommand needs Revit, so it cannot be referenced from this
            // project - but it can be READ. Every Finding("literal") in it would be
            // a name that bypassed the shared list, which is how the two halves
            // drifted apart in the first place.
            string path = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "AuditModelCommand.cs");
            Assert.True(File.Exists(path), path);
            string source = File.ReadAllText(path);

            var literals = System.Text.RegularExpressions.Regex
                .Matches(source, @"return Finding\(""([a-z_]+)""")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.True(literals.Count == 0,
                "these findings name themselves with a string literal instead of an AuditCheckNames constant, " +
                "which is exactly how the gate's map drifted away from them: " + string.Join(", ", literals));

            foreach (string name in AuditCheckNames.Findings)
                Assert.Contains("AuditCheckNames." + Pascal(name), source);
        }

        private static string Pascal(string snake)
        {
            var parts = snake.Split('_');
            var sb = new System.Text.StringBuilder();
            foreach (string p in parts)
            {
                if (p.Length == 0) continue;
                sb.Append(char.ToUpperInvariant(p[0])).Append(p.Substring(1));
            }
            return sb.ToString();
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
