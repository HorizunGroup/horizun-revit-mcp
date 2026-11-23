using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GeometryRegenerationFailureTests
    {
        private static string Source(string name)
        {
            for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", name);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            throw new FileNotFoundException(name);
        }

        private static int AbortCatches(string source) => Regex.Matches(source,
            @"catch\s*\(\s*Autodesk\.Revit\.Exceptions\.RegenerationFailedException\s*\)\s*\{\s*throw;\s*\}").Count;

        [Fact]
        public void Join_pair_regeneration_corruption_aborts_instead_of_continuing_the_batch()
        {
            string source = Source("JoinGeometryCommand.cs");
            int apply = source.IndexOf("edit.Apply = d =>", StringComparison.Ordinal);
            int verify = source.IndexOf("edit.Verify = d =>", apply, StringComparison.Ordinal);
            string write = source.Substring(apply, verify - apply);
            Assert.True(AbortCatches(write) >= 1,
                "RegenerationFailedException must escape the per-pair catch before rollback-and-continue can read another pair.");
            int fatal = write.IndexOf("catch (Autodesk.Revit.Exceptions.RegenerationFailedException)", StringComparison.Ordinal);
            int ordinary = write.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            Assert.True(fatal >= 0 && ordinary > fatal);
        }

        [Fact]
        public void Wall_conversion_preserves_regeneration_corruption_through_both_catch_layers()
        {
            string source = Source("WallFinishCommand.Convert.cs");
            int apply = source.IndexOf("edit.ApplyWithTransactions = d =>", StringComparison.Ordinal);
            int verify = source.IndexOf("edit.Verify = d =>", apply, StringComparison.Ordinal);
            string write = source.Substring(apply, verify - apply);
            Assert.True(AbortCatches(write) >= 2,
                "Both native-commit wrapping and per-wall rollback/readback must propagate RegenerationFailedException without reading the corrupted document.");
            int fatal = write.IndexOf("catch (Autodesk.Revit.Exceptions.RegenerationFailedException)", StringComparison.Ordinal);
            int ordinary = write.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
            Assert.True(fatal >= 0 && ordinary > fatal);
        }
    }
}
