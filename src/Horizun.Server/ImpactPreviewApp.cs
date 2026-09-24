// -----------------------------------------------------------------------------
// Horizun MCP - the impact preview as an MCP App. Original Horizun code.
//
// The one improvement of EXPERIENCE rather than coverage: before a bulk write, see
// which elements it will touch and take some of them out.
//
// WHAT IT RENDERS. The rehearsal (dry_run) reply the five bulk-write tools already
// return - change_preview, plan_resolved, confirmation_token and each tool's own rows.
// Like the clash viewer it fetches nothing and computes no fact the reply did not
// state; a host without MCP Apps loses nothing, because the reply is the result.
//
// WHAT IT CAN SEND, AND THE CONTRACT IT KEEPS. A confirmation token approves ONE
// request: all five tools fold the narrowed field into their plan hash (see
// ImpactPreviewAppTests.The_narrowed_field_is_part_of_every_plan_hash). So excluding a
// row can never be "apply the old token to fewer elements". The app asks for a NEW
// rehearsal of the reduced request, shows it, and only then offers the apply - of
// that rehearsal, with that rehearsal's token. Nothing that was not rehearsed is
// applied from here.
//
// The HTML, the adapter and the view are three embedded files so the adapter can be
// run under node by the tests (tests/Horizun.Server.Tests/ImpactPreview).
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ImpactPreviewApp
    {
        public const string Uri = "ui://horizun/impact-preview";

        /// <summary>
        /// The tools whose rehearsal payload the app KNOWS how to render and narrow. The
        /// same rule as the clash viewer: an app attached to a payload it cannot read is a
        /// blank panel the user blames their client for.
        /// </summary>
        public static readonly string[] Tools =
        {
            "horizun_write_params_verified",
            "horizun_set_keynote",
            "horizun_delete_verified",
            "horizun_transform_elements",
            "horizun_create_elements"
        };

        public static bool Renders(string toolName) => Array.IndexOf(Tools, toolName) >= 0;

        internal const string HtmlResource = "Horizun.Apps.impact-preview.html";
        internal const string AdapterResource = "Horizun.Apps.impact-preview-adapter.js";
        internal const string ViewResource = "Horizun.Apps.impact-preview-view.js";

        private static readonly Lazy<string> _html = new Lazy<string>(Compose);

        public static string Html() => _html.Value;

        public static string AdapterScript() => Read(AdapterResource);

        private static string Compose()
        {
            string template = Read(HtmlResource);
            // Inlined, not linked: the app loads nothing, so there is nothing a CSP has to allow.
            // A "</script" inside a script would end the element early; neither file has one,
            // and the test that reads the composed page proves it.
            return template
                .Replace("/*@ADAPTER@*/", Read(AdapterResource))
                .Replace("/*@VIEW@*/", Read(ViewResource));
        }

        private static string Read(string name)
        {
            Assembly assembly = typeof(ImpactPreviewApp).Assembly;
            using (Stream s = assembly.GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("Embedded resource missing: " + name);
                using (var reader = new StreamReader(s, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// `_meta.ui` for the resource, in the 2026-01-26 spelling: no connect, resource or
        /// frame origins at all. The app has no reason to reach anything.
        /// </summary>
        public static JObject ResourceMeta() => new JObject
        {
            ["ui"] = new JObject
            {
                ["csp"] = new JObject
                {
                    ["connectDomains"] = new JArray(),
                    ["resourceDomains"] = new JArray(),
                    ["frameDomains"] = new JArray(),
                    ["baseUriDomains"] = new JArray()
                },
                ["prefersBorder"] = true
            }
        };

        public static JObject Definition() => new JObject
        {
            ["uri"] = Uri,
            ["name"] = "impact-preview",
            ["title"] = "Impact preview",
            ["description"] =
                "Interactive view of a bulk-write rehearsal (dry_run): what it touches, before -> after, warnings, " +
                "and rows to exclude. Excluding asks for a NEW rehearsal; Apply only spends the token of the " +
                "rehearsal on screen. Fetches nothing.",
            ["mimeType"] = McpAppResources.AppMimeType,
            ["size"] = Encoding.UTF8.GetByteCount(Html()),
            ["annotations"] = new JObject
            {
                ["audience"] = new JArray("user"),
                ["priority"] = 0.6
            },
            ["_meta"] = ResourceMeta()
        };

        /// <summary>The `_meta` block a declaring tool carries. Deliberately minimal: tools/list bytes.</summary>
        public static JObject ToolUiMeta() => new JObject
        {
            ["ui"] = new JObject { ["resourceUri"] = Uri }
        };

        /// <summary>Tools that declare the app, for the docs and the tests.</summary>
        public static string ToolList() => string.Join(", ", Tools.OrderBy(t => t, StringComparer.Ordinal));
    }
}
