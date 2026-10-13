// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The durable records named by the 2026-10-08 audit, held to AtomicFile: none of
// them may write straight over its live file, and none may use a FIXED temporary
// name ("<file>.tmp") that two writers would share. These stores run inside Revit
// or the server, where a crash or a second process is not hypothetical; a source
// check is what keeps a later edit from quietly bringing the old write back.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AtomicStoreWiringTests
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

        private static string Source(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        [Theory]
        [InlineData("src/Horizun.Revit/Core/RepairMemory.cs")]
        [InlineData("src/Horizun.Revit/Core/CadUpdateOperations.cs")]
        [InlineData("src/Horizun.Revit/Core/CadLinkLoads.cs")]
        [InlineData("src/Horizun.Server/PowerBiSelection.cs")]
        [InlineData("src/Horizun.Server/SharedRecordFile.cs")]
        public void The_store_never_writes_over_its_live_file_or_through_a_fixed_temporary(string file)
        {
            string text = Source(file);
            Assert.DoesNotMatch(new Regex(@"(?<!Atomic)File\.WriteAllText\("), text);
            Assert.DoesNotMatch(new Regex(@"\+\s*""\.tmp""\s*;"), text);
        }
    }
}
