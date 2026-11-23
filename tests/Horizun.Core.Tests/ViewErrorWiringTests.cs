using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ViewErrorWiringTests
    {
        [Fact]
        public void No_active_view_uses_its_specific_error_code_and_missing_named_views_keep_their_own_code()
        {
            DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "CaptureViewCommand.cs"));
            Assert.Contains("if (view == null) return CommandResult.FailCoded(ErrorCodes.SinVista, \"There is no active view to capture.\")", source);
            Assert.Contains("DocumentContext.MissingView(doc, viewName)", source);
        }
    }
}
