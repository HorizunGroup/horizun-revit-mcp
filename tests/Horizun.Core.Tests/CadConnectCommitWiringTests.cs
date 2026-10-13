// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_cad_connect commits the declared transition types through Guard and
// re-reads the type it set (audit 2026-10-08, 6.16). A source check: the Revit
// half cannot run here, and a bare Commit() is how the status got ignored.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadConnectCommitWiringTests
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

        [Fact]
        public void No_transaction_in_cad_connect_is_committed_without_its_status_being_read()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "CadConnectCommand.cs"));
            Assert.DoesNotMatch(new Regex(@"\btx\.Commit\(\)\s*;"), src);
            Assert.Contains("Guard.Commit(tx, \"Horizun: declared transition type\")", src);
            Assert.Contains("Guard.Commit(tx, \"Horizun: best declared transition type\")", src);
            Assert.Contains("fitting.GetTypeId() != bestSymbol.Id", src);
        }
    }
}
