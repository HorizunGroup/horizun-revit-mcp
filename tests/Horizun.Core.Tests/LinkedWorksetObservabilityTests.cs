// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A WORKSET CLOSED ON A LINK IS NOT OBSERVABLE, AND THE REPLY HAS TO SAY SO.
//
// MEASURED, Revit 2026, 2026-09-04, artifact q42-probe2: a link created with
// RevitLinkOptions(false, WorksetConfiguration) closing HZ_WS_CLOSED by id gives,
// in the LINKED document:
//
//     workset            IsOpen   elements
//     Workset1           true       4767
//     Shared Levels...   true         26
//     HZ_WS_CLOSED       true        392      <- asked to be closed
//     total                         9800
//
// and reloading the SAME type with OpenAllWorksets gives the identical census.
// The witness elements are there either way, so the request is not observable -
// not through IsOpen, and not through the elements.
//
// These tests exist so nobody reads that flag as evidence again. They cannot run
// Revit; what they pin is the SENTENCE the product publishes and the wiring that
// attaches it to every linked document, which is the whole of what the fix is.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LinkedWorksetObservabilityTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "AGENTS.md")) &&
                    Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
        }

        [Fact]
        public void The_limit_is_stated_in_the_product_not_only_in_a_report()
        {
            string means = DocumentVisibilityCoverage.LinkedDocumentMeans;
            Assert.Contains("LINKED", means);
            // The three things a reader must not conclude.
            Assert.Contains("no way to read back the WorksetConfiguration", means);
            Assert.Contains("NOT evidence of a closed workset", means);
            Assert.Contains("still hands over that workset's elements", means);
        }

        [Fact]
        public void Every_linked_document_row_of_a_takeoff_carries_it()
        {
            string src = File.ReadAllText(Path.Combine(
                RepoRoot(), "src", "Horizun.Revit", "Commands", "QuantitiesCommand.cs"));

            // Attached on the LINK rows, and only there: the host's own worksets are
            // read from the document the command is actually in, and that number
            // means what it says.
            Assert.Contains("if (link != null)", src);
            Assert.Contains("[\"linked_document_means\"] =\n                        DocumentVisibilityCoverage.LinkedDocumentMeans;", src);

            // And the headline stops blaming a link's worksets for an absence.
            Assert.Contains("the configuration a link was loaded with is not readable", src);
        }

        [Fact]
        public void A_host_document_still_reports_its_worksets_as_the_measurement_they_are()
        {
            // The limitation is about LINKS. Closing a workset in the document you
            // are IN really does remove its elements, and that coverage is evidence.
            DocumentVisibilityCoverage host = DocumentVisibilityCoverage.From(3, 2);
            Assert.False(host.CoverageComplete);
            Assert.Equal(1, host.WorksetsClosed);
            Assert.True(host.IsWorkshared);
            Assert.DoesNotContain("linked", host.ToJson().Properties().Select(p => p.Name));
        }
    }
}
