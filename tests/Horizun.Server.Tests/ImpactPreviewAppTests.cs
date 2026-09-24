// -----------------------------------------------------------------------------
// Horizun Server tests - the impact preview MCP App. Original Horizun code.
//
// Three properties, each asserted against the shipped source rather than a copy:
//   1. it is a resource like any other, served with the MCP Apps mime type and a CSP
//      that allows nothing, and only the five tools whose payload it reads declare it;
//   2. it cannot reach the network - no URL, no fetch, nothing loaded;
//   3. excluding rows can never spend the old token: the field the app narrows is in
//      each command's plan hash, so a narrowed request is a DIFFERENT plan and needs its
//      own rehearsal. The adapter that builds those requests runs under node
//      (ImpactPreview/adapter.test.js) against fixtures shaped like the real replies.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Server.Tests
{
    public class ImpactPreviewAppTests
    {
        private readonly ITestOutputHelper _out;
        public ImpactPreviewAppTests(ITestOutputHelper output) { _out = output; }

        /// <summary>Which request field the app narrows for each tool, and where the plan hash must see it.</summary>
        private static readonly (string Tool, string CommandFile, string[] Narrowed)[] Narrowing =
        {
            ("horizun_write_params_verified", "WriteParamsCommand.cs", new[] { "writes" }),
            ("horizun_set_keynote", "SetKeynoteCommand.cs", new[] { "element_ids" }),
            ("horizun_delete_verified", "DeleteCommand.cs", new[] { "ids", "protect_ids" }),
            ("horizun_transform_elements", "TransformElementsCommand.cs", new[] { "operations" }),
            ("horizun_create_elements", "CreateElementsCommand.cs", new[] { "elements" })
        };

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "src", "Horizun.Server", "ImpactPreviewApp.cs")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate the repository root");
            return d.FullName;
        }

        [Fact]
        public void The_app_is_listed_and_read_with_the_mcp_app_mime_type_and_a_closed_csp()
        {
            JObject listed = ((JArray)McpResources.List(null)["resources"]).OfType<JObject>()
                .Single(r => (string)r["uri"] == ImpactPreviewApp.Uri);
            Assert.Equal("text/html;profile=mcp-app", (string)listed["mimeType"]);
            Assert.Equal(Encoding.UTF8.GetByteCount(ImpactPreviewApp.Html()), (int)listed["size"]);

            JObject read = McpResources.Read(new JObject { ["uri"] = ImpactPreviewApp.Uri });
            JObject content = (JObject)Assert.Single((JArray)read["contents"]);
            Assert.Equal("text/html;profile=mcp-app", (string)content["mimeType"]);
            Assert.Equal(ImpactPreviewApp.Html(), (string)content["text"]);

            // The 2026-01-26 spelling, on the listing AND on the content item, all empty.
            foreach (JObject holder in new[] { listed, content })
            {
                JObject csp = (JObject)holder["_meta"]?["ui"]?["csp"];
                Assert.NotNull(csp);
                foreach (string key in new[] { "connectDomains", "resourceDomains", "frameDomains", "baseUriDomains" })
                    Assert.Empty((JArray)csp[key]);
            }
        }

        [Fact]
        public void Exactly_the_five_bulk_writes_declare_the_app()
        {
            Assert.Equal(Narrowing.Select(n => n.Tool).OrderBy(x => x), ImpactPreviewApp.Tools.OrderBy(x => x));
            var declaring = new List<string>();
            foreach (JObject tool in Tools.ListIgnoringPacks(true).OfType<JObject>())
            {
                string uri = (string)tool["_meta"]?["ui"]?["resourceUri"];
                if (uri == ImpactPreviewApp.Uri) declaring.Add((string)tool["name"]);
                if (ImpactPreviewApp.Renders((string)tool["name"])) Assert.Equal(ImpactPreviewApp.Uri, uri);
            }
            Assert.NotEmpty(declaring);
            Assert.All(declaring, name => Assert.Contains(name, ImpactPreviewApp.Tools));
        }

        [Fact]
        public void The_declaration_costs_well_under_the_tools_list_budget()
        {
            // The whole footprint on tools/list is the five `_meta` blocks. Budget: 2 KB.
            int bytes = ImpactPreviewApp.Tools.Sum(t =>
                Encoding.UTF8.GetByteCount(",\"_meta\":" + ImpactPreviewApp.ToolUiMeta().ToString(Formatting.None)));
            _out.WriteLine("tools/list bytes added by the impact preview: " + bytes);
            Assert.True(bytes <= 2048, "impact preview adds " + bytes + " bytes to tools/list");
        }

        [Fact]
        public void The_page_reaches_nothing_outside_itself()
        {
            string html = ImpactPreviewApp.Html();
            Assert.DoesNotMatch(new Regex(@"https?://", RegexOptions.IgnoreCase), html);
            Assert.DoesNotMatch(new Regex(@"(src|href|action)\s*=\s*[""']?//", RegexOptions.IgnoreCase), html);
            foreach (string forbidden in new[] { "fetch(", "XMLHttpRequest", "WebSocket", "EventSource", "import(",
                                                 "<script src", "<link", "@import", "url(", "<iframe", "<img", "eval(", "new Function" })
                Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_page_is_composed_whole()
        {
            string html = ImpactPreviewApp.Html();
            Assert.DoesNotContain("/*@ADAPTER@*/", html);
            Assert.DoesNotContain("/*@VIEW@*/", html);
            Assert.Contains("ImpactAdapter", html);
            // Two inline scripts, each closed once: an inlined file containing "</script"
            // would end its element early and ship half a program.
            Assert.Equal(2, Regex.Matches(html, "</script", RegexOptions.IgnoreCase).Count);
            Assert.DoesNotContain("</script", ImpactPreviewApp.AdapterScript(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_page_is_accessible_and_themed()
        {
            string html = ImpactPreviewApp.Html();
            Assert.Contains("<html lang=\"en\">", html);
            Assert.Contains("role=\"status\"", html);
            Assert.Contains("aria-live=\"polite\"", html);
            Assert.Contains("scope: 'col'", html);             // column headers
            Assert.Contains("'aria-label': 'Exclude '", html); // every checkbox is named
            Assert.Contains("prefers-color-scheme: dark", html);
            Assert.Contains("data-theme=\"dark\"", html);
            Assert.Contains("hostContext", html);              // the host's theme wins when it sends one
        }

        [Fact]
        public void The_view_speaks_the_2026_01_26_dialect()
        {
            string html = ImpactPreviewApp.Html();
            foreach (string method in new[] { "ui/initialize", "ui/notifications/initialized", "ui/notifications/tool-input",
                                              "ui/notifications/tool-result", "ui/notifications/host-context-changed",
                                              "ui/resource-teardown", "ui/update-model-context", "tools/call" })
                Assert.Contains("'" + method + "'", html);
            Assert.Contains("serverTools", html);  // tool calls only when the host grants them
            Assert.Contains("idempotency_key", html);
        }

        [Fact]
        public void Every_declaring_tool_accepts_what_the_app_sends()
        {
            // The app sends back the tool's OWN arguments with dry_run and confirmation_token -
            // and, when narrowing, the same fields made shorter. Every one must be a field the
            // schema declares, or the host sends a request the server refuses.
            foreach (var n in Narrowing)
            {
                CommandContract c = Contract.Find(n.Tool);
                Assert.NotNull(c);
                var props = (JObject)c.InputSchema["properties"];
                foreach (string field in n.Narrowed.Concat(new[] { "dry_run", "confirmation_token", "target_document" }))
                    Assert.True(props[field] != null, n.Tool + " does not declare '" + field + "'");
            }
            Assert.NotNull(Contract.Find("horizun_transform_elements").InputSchema
                .SelectToken("properties.operations.items.properties.element_ids"));
        }

        [Fact]
        public void The_narrowed_field_is_part_of_every_plan_hash()
        {
            // The reason excluding rows ALWAYS asks for a new rehearsal: a token approves the
            // hash of the request's scope, and the field the app shortens is in that scope.
            // If one of these ever left the hash, a narrowed request could spend the full
            // plan's token - and the app's two-step flow would be the only thing standing
            // between a click and an unrehearsed write.
            string commands = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands");
            foreach (var n in Narrowing)
            {
                string text = File.ReadAllText(Path.Combine(commands, n.CommandFile));
                string hashed;
                Match call = Regex.Match(text, @"PlanHash\(\s*request\s*,(?<args>[^;]*?)\)\s*;", RegexOptions.Singleline);
                if (call.Success) hashed = call.Groups["args"].Value;
                else
                {
                    Match own = Regex.Match(text, @"string PlanHashOf\([^)]*\)\s*\{(?<body>.*?)return", RegexOptions.Singleline);
                    Assert.True(own.Success, n.CommandFile + ": no plan hash found");
                    hashed = own.Groups["body"].Value;
                }
                foreach (string field in n.Narrowed)
                    Assert.True(hashed.Contains("\"" + field + "\""), n.CommandFile + " does not hash '" + field + "'");
            }
        }

        [Fact]
        public void The_fields_the_adapter_reads_are_the_fields_the_bridge_writes()
        {
            // The fixtures are synthetic (no live capture of these five rehearsals existed at
            // 038f882), so the shape they assume is tied to the source that emits it: a renamed
            // field fails here, not in a user's panel.
            string core = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core");
            string preview = File.ReadAllText(Path.Combine(core, "PlanPreview.cs"));
            foreach (string f in new[] { "unique_id", "element_id", "action", "category", "type", "captured_state",
                                         "proposed_values", "total", "shown", "truncated", "expected_cascade", "rows" })
                Assert.Contains("[\"" + f + "\"]", preview);
            string gate = File.ReadAllText(Path.Combine(core, "DocumentGate.cs"));
            foreach (string f in new[] { "change_preview", "plan_resolved", "confirmation_token", "confirmation_expires_utc" })
                Assert.Contains("[\"" + f + "\"]", gate);

            string commands = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands");
            string write = File.ReadAllText(Path.Combine(commands, "WriteParamsCommand.cs"));
            foreach (string f in new[] { "index", "target_id", "target_name", "target_kind", "parameter", "before", "requested", "writes_planned", "on_failure_if_run" })
                Assert.Contains("[\"" + f + "\"]", write);
            string keynote = File.ReadAllText(Path.Combine(commands, "SetKeynoteCommand.cs"));
            foreach (string f in new[] { "target_id", "target_name", "current_keynote", "requested_elements", "collateral_elements", "writes_to" })
                Assert.Contains("[\"" + f + "\"]", keynote);
            string delete = File.ReadAllText(Path.Combine(commands, "DeleteCommand.cs"));
            foreach (string f in new[] { "{ \"role\", \"requested\" }", "{ \"role\", \"cascade\" }", "{ \"raw_id\",", "{ \"parent_raw_id\",", "[\"would_delete_total\"]" })
                Assert.Contains(f, delete);
            string create = File.ReadAllText(Path.Combine(commands, "CreateElementsCommand.cs"));
            Assert.Contains("\"create:\" + planned.Index", create);
            Assert.Contains("{ \"level\", SafePlanName(planned.Level) }", create);
            Assert.Contains("[\"validation_level\"]", create);
            string transform = File.ReadAllText(Path.Combine(commands, "TransformElementsCommand.cs"));
            Assert.Contains("[\"valid_operations\"]", transform);
            Assert.Contains("ElementId = Rid.Value(e.Id)", transform);
        }

        [Fact]
        public void Every_fixture_uses_only_arguments_its_tool_declares()
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "ImpactPreview", "fixtures");
            string[] files = Directory.GetFiles(dir, "*.json");
            Assert.Equal(5, files.Length);
            foreach (string file in files)
            {
                JObject f = JObject.Parse(File.ReadAllText(file));
                CommandContract c = Contract.Find((string)f["tool"]);
                Assert.NotNull(c);
                var props = (JObject)c.InputSchema["properties"];
                foreach (JProperty arg in ((JObject)f["arguments"]).Properties())
                    Assert.True(props[arg.Name] != null, Path.GetFileName(file) + ": '" + arg.Name + "' is not a " + c.Name + " argument");
                Assert.False(string.IsNullOrEmpty((string)f["result"]?["confirmation_token"]));
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task The_adapter_passes_its_node_suite()
        {
            string node = FindOnPath(Environment.OSVersion.Platform == PlatformID.Win32NT ? "node.exe" : "node");
            if (node == null)
            {
                // Hosted CI images carry node; a bare SDK container may not. On CI its absence
                // is a failure - a suite that silently did not run is not a pass.
                Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")),
                            "node is not on PATH, so the impact-preview adapter suite did not run.");
                _out.WriteLine("NOT RUN (outside CI): node is not on PATH; the adapter suite did not run.");
                return;
            }
            string suite = Path.Combine(AppContext.BaseDirectory, "ImpactPreview", "adapter.test.js");
            // The adapter is taken from the EMBEDDED resource: what is proved is what ships.
            string adapter = Path.Combine(Path.GetTempPath(), "hz-impact-adapter-" + Guid.NewGuid().ToString("N") + ".js");
            File.WriteAllText(adapter, ImpactPreviewApp.AdapterScript(), new UTF8Encoding(false));
            try
            {
                var psi = new ProcessStartInfo(node)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                };
                psi.ArgumentList.Add(suite);
                psi.ArgumentList.Add(adapter);
                psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ImpactPreview", "fixtures"));
                using (Process p = Process.Start(psi))
                {
                    var stderrTask = p.StandardError.ReadToEndAsync();
                    string stdout = p.StandardOutput.ReadToEnd();
                    Assert.True(p.WaitForExit(60000), "node did not finish");
                    string stderr = await stderrTask;
                    _out.WriteLine(stdout);
                    Assert.True(p.ExitCode == 0, "adapter suite failed:\n" + stdout + stderr);
                    Assert.Contains(" 0 failed", stdout);
                }
            }
            finally { File.Delete(adapter); }
        }

        private static string FindOnPath(string exe)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try { string full = Path.Combine(dir.Trim().Trim('"'), exe); if (File.Exists(full)) return full; }
                catch (ArgumentException) { }
            }
            return null;
        }
    }
}
