// -----------------------------------------------------------------------------
// Horizun Server tests - both MCP Apps keep the MCP Apps 2026-01-26 contract.
// Original Horizun code.
//
// Three points, each checked for BOTH apps (ui://horizun/clash-viewer and
// ui://horizun/impact-preview) against ext-apps specification/2026-01-26/apps.mdx:
//   1. the CSP is declared at `_meta.ui.csp`, with the spec's field names
//      (connectDomains, resourceDomains, frameDomains, baseUriDomains), on the
//      resources/list entry and on the resources/read content item;
//   2. tool calls are enabled by `hostCapabilities.serverTools` of the ui/initialize
//      result - not by a `capabilities.tools` a host happens to send;
//   3. the View sends `ui/notifications/initialized` before anything else after the
//      host answers ui/initialize ("Host MUST NOT send any request or notification
//      to the View before it receives an initialized notification").
// Points 2 and 3 are behaviour, so they run the SHIPPED pages under node against a
// scripted host (McpApps/app-handshake.test.js).
// -----------------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Server.Tests
{
    public class McpAppSpecTests
    {
        private readonly ITestOutputHelper _out;
        public McpAppSpecTests(ITestOutputHelper output) { _out = output; }

        private static readonly string[] SpecCspFields = { "connectDomains", "resourceDomains", "frameDomains", "baseUriDomains" };

        public static TheoryData<string> AppUris => new TheoryData<string> { McpAppResources.ClashViewerUri, ImpactPreviewApp.Uri };

        private static string HtmlOf(string uri) =>
            uri == McpAppResources.ClashViewerUri ? McpAppResources.Html() : ImpactPreviewApp.Html();

        [Theory]
        [MemberData(nameof(AppUris))]
        public void The_csp_is_declared_at_meta_ui_csp_with_the_spec_field_names(string uri)
        {
            JObject listed = ((JArray)McpResources.List(null)["resources"]).OfType<JObject>().Single(r => (string)r["uri"] == uri);
            JObject content = (JObject)Assert.Single((JArray)McpResources.Read(new JObject { ["uri"] = uri })["contents"]);

            foreach (JObject holder in new[] { listed, content })
            {
                JObject csp = holder["_meta"]?["ui"]?["csp"] as JObject;
                Assert.True(csp != null, uri + ": no _meta.ui.csp on " + (holder == listed ? "the listing" : "the content item"));
                foreach (string field in SpecCspFields)
                {
                    Assert.True(csp[field] is JArray, uri + ": _meta.ui.csp." + field + " is missing");
                    Assert.Empty((JArray)csp[field]);   // the apps reach nothing
                }
                // CSP-directive spellings are not spec fields and must not stand in for them.
                foreach (JProperty p in csp.Properties())
                    Assert.Contains(p.Name, SpecCspFields);
            }
        }

        [Fact]
        public void The_clash_viewer_keeps_its_pre_spec_csp_block_closed()
        {
            // Kept for hosts built against the draft; harmless only while it allows nothing.
            JObject legacy = McpAppResources.ResourceMeta()["io.modelcontextprotocol/ui"]?["csp"] as JObject;
            Assert.NotNull(legacy);
            Assert.All(legacy.Properties(), p => Assert.Empty((JArray)p.Value));
        }

        [Theory]
        [MemberData(nameof(AppUris))]
        public void The_page_reads_host_capabilities_and_sends_initialized(string uri)
        {
            string html = HtmlOf(uri);
            Assert.Contains("hostCapabilities", html);
            Assert.Contains("serverTools", html);
            Assert.Contains("'ui/notifications/initialized'", html);
            Assert.DoesNotContain("'ui/context-update'", html);   // not a 2026-01-26 method
        }

        [Fact]
        public async System.Threading.Tasks.Task Both_apps_pass_the_handshake_suite_under_node()
        {
            string node = FindOnPath(Environment.OSVersion.Platform == PlatformID.Win32NT ? "node.exe" : "node");
            if (node == null)
            {
                // Same rule as the adapter suite: on CI a suite that did not run is a failure.
                Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")),
                            "node is not on PATH, so the MCP App handshake suite did not run.");
                _out.WriteLine("NOT RUN (outside CI): node is not on PATH; the handshake suite did not run.");
                return;
            }
            string dir = Path.Combine(Path.GetTempPath(), "hz-app-handshake-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            // The pages are the ones the server serves, written out as-is.
            string clash = Path.Combine(dir, "clash-viewer.html");
            string impact = Path.Combine(dir, "impact-preview.html");
            File.WriteAllText(clash, McpAppResources.Html(), new UTF8Encoding(false));
            File.WriteAllText(impact, ImpactPreviewApp.Html(), new UTF8Encoding(false));
            try
            {
                var psi = new ProcessStartInfo(node)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                };
                psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "McpApps", "app-handshake.test.js"));
                psi.ArgumentList.Add(clash);
                psi.ArgumentList.Add(impact);
                psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ImpactPreview", "fixtures", "write-params.json"));
                using (Process p = Process.Start(psi))
                {
                    var stderrTask = p.StandardError.ReadToEndAsync();
                    string stdout = p.StandardOutput.ReadToEnd();
                    Assert.True(p.WaitForExit(60000), "node did not finish");
                    string stderr = await stderrTask;
                    _out.WriteLine(stdout);
                    Assert.True(p.ExitCode == 0, "handshake suite failed:\n" + stdout + stderr);
                    Assert.Contains(" 0 failed", stdout);
                    Assert.Contains("PASS clash-viewer:", stdout);
                    Assert.Contains("PASS impact-preview:", stdout);
                }
            }
            finally
            {
                File.Delete(clash);
                File.Delete(impact);
                Directory.Delete(dir);
            }
        }

        private static string FindOnPath(string exe)
        {
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(d)) continue;
                try { string full = Path.Combine(d.Trim().Trim('"'), exe); if (File.Exists(full)) return full; }
                catch (ArgumentException) { }
            }
            return null;
        }
    }
}
