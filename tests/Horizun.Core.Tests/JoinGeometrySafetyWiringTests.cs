using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// Guards the production Revit wiring as well as the pure state-machine tests:
    /// passing the latter is insufficient if a command bypasses that state machine.
    /// A baseline source path can be supplied without replacing the working tree.
    /// </summary>
    public class JoinGeometrySafetyWiringTests
    {
        private static string Source()
        {
            string baseline = Environment.GetEnvironmentVariable("HORIZUN_TEST_JOIN_SOURCE");
            if (!string.IsNullOrWhiteSpace(baseline)) return File.ReadAllText(baseline);
            for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "JoinGeometryCommand.cs");
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            throw new FileNotFoundException("JoinGeometryCommand.cs was not found above the test binary.");
        }

        [Fact]
        public void NativePairMutationUsesTheTestedCurrentStateGuard()
        {
            string source = Source();
            int start = source.IndexOf("private static void ApplyPair(", StringComparison.Ordinal);
            int end = source.IndexOf("private static JToken SumMemberVolumes(", start, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start);
            string apply = source.Substring(start, end - start);
            Assert.Contains("JoinGeometryRules.ApplyCurrentPair(", apply);
            Assert.Contains("CurrentPair(d, p, progress)", apply);
            Assert.Contains("CurrentMemberError(d, a)", apply);
            Assert.Contains("d.Regenerate()", apply);
        }

        [Fact]
        public void PostMutationReadsResolveStableNumericIdsInsteadOfInvalidatableWrappers()
        {
            string source = Source();
            Assert.DoesNotContain("p.A.Element.Id", source);
            Assert.DoesNotContain("p.B.Element.Id", source);
            Assert.DoesNotContain("m.Element.Id", source);
        }
    }
}
