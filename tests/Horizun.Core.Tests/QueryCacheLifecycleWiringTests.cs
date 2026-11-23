using System;
using System.IO;
using Xunit;

namespace Horizun.Core.Tests
{
    public class QueryCacheLifecycleWiringTests
    {
        [Fact]
        public void Document_and_link_observers_are_attached_and_detached()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src", "Horizun.Revit"))) root = root.Parent;
            Assert.NotNull(root);
            string source = File.ReadAllText(Path.Combine(root.FullName, "src", "Horizun.Revit", "Core", "QueryCacheLifecycle.cs"));
            foreach (string name in new[] { "DocumentChanged", "DocumentOpened", "DocumentClosing", "DocumentClosed",
                "LinkedResourceOpening", "LinkedResourceOpened", "DocumentReloadingLatest", "DocumentReloadedLatest" })
            {
                Assert.Contains("." + name + " +=", source);
                Assert.Contains("." + name + " -=", source);
            }
        }
    }
}
