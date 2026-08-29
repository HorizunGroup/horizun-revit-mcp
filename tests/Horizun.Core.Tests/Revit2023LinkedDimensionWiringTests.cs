// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The Revit-specific commands cannot run in the offline test host. These source
// checks pin the version guard whose behaviour the live 2023 release gate proves.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class Revit2023LinkedDimensionWiringTests
    {
        private static string Command(string name)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                string path = Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", name);
                if (File.Exists(path)) return File.ReadAllText(path);
                d = d.Parent;
            }
            throw new InvalidOperationException("repository root not found");
        }

        [Fact]
        public void Discovery_keeps_the_reference_but_marks_it_incompatible_only_in_2023()
        {
            string src = Command("DimensionReferencesCommand.cs");

            Assert.Contains("#if REVIT2023", src);
            Assert.Contains("candidate.Link != null && candidate.Compatible", src);
            Assert.Contains("LinkedGeometryRejectedByRevit2023", src);
        }

        [Fact]
        public void Annotation_refuses_the_2023_link_before_reaching_NewDimension()
        {
            string src = Command("AnnotateCommand.cs");
            int guard = src.IndexOf("LinkedGeometryRejectedByRevit2023", StringComparison.Ordinal);
            int create = src.IndexOf("doc.Create.NewDimension", StringComparison.Ordinal);

            Assert.True(guard >= 0 && create > guard);
            Assert.Contains("revit2023Limit.Code", src);
        }
    }
}
