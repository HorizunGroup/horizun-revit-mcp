// -----------------------------------------------------------------------------
// Horizun MCP server - which MCP extensions this build actually implements.
// Original Horizun code.
//
// An advertised capability is a promise the client PLANS with. A client that sees
// io.modelcontextprotocol/ui in our capabilities will render a UI resource and
// show the user a blank panel when there is nothing behind it - and the user will
// read that as their client being broken, not as this server over-promising.
//
// So an extension appears here with a flag, and the flag is the only thing that
// decides whether it is announced. A half-built extension keeps its row and stays
// false: the row is where the next person looks to find out what is left.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Server.Protocol
{
    internal sealed class McpExtension
    {
        public string Id;

        /// <summary>
        /// Implemented END TO END on this build. Not "the types exist", not "the method
        /// is routed": a client that declares it and uses it gets the behaviour the
        /// extension specifies, or this is false.
        /// </summary>
        public bool Implemented;

        /// <summary>Per-extension settings this server advertises. Empty means "supported, no settings".</summary>
        public Func<JObject> Settings;

        /// <summary>What is missing, when Implemented is false. Read by nobody but a human.</summary>
        public string Pending;
    }

    internal static class ExtensionRegistry
    {
        public const string Tasks = "io.modelcontextprotocol/tasks";
        public const string Ui = "io.modelcontextprotocol/ui";

        private static readonly List<McpExtension> Known = new List<McpExtension>
        {
            new McpExtension
            {
                Id = Tasks,
                Implemented = true,
                Settings = () => new JObject(),
                Pending = null
            },

            // G05. The clash viewer is served at ui://horizun/clash-viewer and renders
            // ONLY what the tool result already contains - it fetches nothing - so the
            // textual reply and the app cannot disagree. McpAppResources.Text() renders
            // the same payload from the same fields in the same order, which is what
            // makes "same result in both modes" a property a test can assert rather than
            // a sentence in a README.
            new McpExtension
            {
                Id = Ui,
                Implemented = true,
                Settings = () => new JObject
                {
                    ["mimeTypes"] = new JArray(McpAppResources.AppMimeType)
                },
                Pending = null
            }
        };

        /// <summary>The `extensions` map for server capabilities: only what is real.</summary>
        public static JObject Advertised()
        {
            var map = new JObject();
            foreach (McpExtension e in Known)
                if (e.Implemented) map[e.Id] = e.Settings != null ? e.Settings() : new JObject();
            return map;
        }

        public static bool IsAdvertised(string id)
        {
            foreach (McpExtension e in Known)
                if (e.Id == id) return e.Implemented;
            return false;
        }

        /// <summary>Every row, advertised or not. For the build-identity resource and for tests.</summary>
        public static IReadOnlyList<McpExtension> All => Known;
    }
}
