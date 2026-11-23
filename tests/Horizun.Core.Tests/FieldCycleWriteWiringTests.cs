using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FieldCycleWriteWiringTests
    {
        [Fact]
        public void Capture_accepts_both_view_fields_and_resolves_id_first()
        {
            string source = Read("CaptureViewCommand.cs");
            Assert.DoesNotContain("Specify view_id or view_name, not both", source);
            Assert.True(source.IndexOf("if (viewId != 0)", StringComparison.Ordinal) < source.IndexOf("else if (!string.IsNullOrEmpty(viewName))", StringComparison.Ordinal));
        }
        private static string Read(string file)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Horizun.Revit"))) directory = directory.Parent;
            Assert.NotNull(directory);
            return File.ReadAllText(Path.Combine(directory.FullName, "src", "Horizun.Revit", "Commands", file));
        }

        [Fact]
        public void Double_strings_are_parsed_before_writing_and_never_use_the_parse_readback_path()
        {
            string source = Read("WriteParamsCommand.cs");
            int start = source.IndexOf("case StorageType.Double:", source.IndexOf("private static bool TryApply", StringComparison.Ordinal), StringComparison.Ordinal);
            int end = source.IndexOf("case StorageType.ElementId:", start, StringComparison.Ordinal);
            string doubleSetter = source.Substring(start, end - start);
            Assert.True(doubleSetter.IndexOf("UnitFormatUtils.TryParse", StringComparison.Ordinal) < doubleSetter.IndexOf("p.Set(converted)", StringComparison.Ordinal));
            Assert.Contains("LengthWriteRules.ConvertedExpectation(txt, converted)", doubleSetter);
            Assert.DoesNotContain("ParseExpectation", doubleSetter);
            Assert.DoesNotContain("expectationIsParse = true", doubleSetter);
        }

        [Fact]
        public void Missing_capture_geometry_is_refused_before_the_isolation_transaction()
        {
            string source = Read("CaptureViewOptions.cs");
            int preflight = source.IndexOf("FramePoints(doc, view, request[\"element_ids\"], out _)", StringComparison.Ordinal);
            Assert.True(preflight > 0);
            int group = source.IndexOf("new TransactionGroup", StringComparison.Ordinal);
            Assert.True(preflight < group);
            Assert.Contains("ErrorCodes.PrecondicionNoCumplida", source.Substring(preflight, group - preflight));
            Assert.Contains("var points = FramePoints(doc, view, token", source);
        }
    }
}
