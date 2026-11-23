// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// copy_link_overrides evidence (4.0.0 battery, live 2026-10-28): counting the link
// elements visible in a host view - FilteredElementCollector(host, view, link) - on a
// view never drawn in the session TERMINATED Revit 2025.4 (no exception, the journal just
// ends; reproduced twice, once through the typed tool and once from a bare script). The
// collector is not testable without Revit, so this pins the guard in the source: only a
// view open in the UI is counted, and the guard comes before the collector.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LinkEvidenceGuardTests
    {
        private static string Source(params string[] parts)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "src"))) root = root.Parent;
            Assert.NotNull(root);
            return File.ReadAllText(Path.Combine(new[] { root.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void Link_elements_are_counted_only_in_views_open_in_the_ui()
        {
            string src = Source("src", "Horizun.Revit", "Commands", "ViewVisibilityLinks.cs");
            int method = src.IndexOf("private static JToken VisibleByCategory(", StringComparison.Ordinal);
            Assert.True(method > 0);
            int guard = src.IndexOf("view_not_open_in_ui", method, StringComparison.Ordinal);
            int collector = src.IndexOf("new FilteredElementCollector(doc, view.Id, link.Id)", method, StringComparison.Ordinal);
            Assert.True(guard > 0 && collector > 0 && guard < collector, "the open-view guard must run before the link-in-view collector");
            Assert.Contains("GetOpenUIViews()", src);
            // An unreadable open-view list is not permission to count.
            Assert.Contains("open == null || !open.Contains(Rid.Value(view.Id))", src);
        }
    }
}
