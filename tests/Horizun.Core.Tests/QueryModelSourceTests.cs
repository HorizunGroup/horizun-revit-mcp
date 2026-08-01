using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class QueryModelSourceTests
    {
        [Fact]
        public void Collector_has_a_native_filter_before_any_iteration()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "Horizun.Revit", "Commands", "QueryModelCommand.cs"));
            int collector = source.IndexOf("FilteredElementCollector collector", StringComparison.Ordinal);
            int instances = source.IndexOf("WhereElementIsNotElementType()", collector, StringComparison.Ordinal);
            int all = source.IndexOf("collector.WherePasses(", collector, StringComparison.Ordinal);
            int iteration = source.IndexOf("foreach (Element element in candidates)", collector, StringComparison.Ordinal);
            Assert.True(collector >= 0 && instances > collector && all > collector && iteration > instances && iteration > all,
                "Revit throws when a FilteredElementCollector is iterated without a native ElementFilter.");
        }
    }
}
