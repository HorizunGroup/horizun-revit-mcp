// -----------------------------------------------------------------------------
// Horizun MCP server — standard MCP Resources.
//
// These are intentionally virtual horizun:// resources, not file:// paths. An
// installed server cannot assume that its source checkout exists, and publishing a
// builder/user path would leak local state. Every byte is derived from the running
// binary and the shared contract, so it cannot drift from what tools/list advertises.
// -----------------------------------------------------------------------------
using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpResources
    {
        private const string GuidanceUri = "horizun://guidance/typed-first";
        private const string ContractUri = "horizun://contract/tools";
        private const string SecurityUri = "horizun://security/current-profile";
        private const string BuildUri = "horizun://build/identity";

        public static JObject List(JObject prms)
        {
            RejectCursor(prms);
            return new JObject
            {
                ["resources"] = new JArray
                {
                    Def(GuidanceUri, "typed-first-guidance", "Verified Revit workflow",
                        "The operating rules for health-first targeting, typed writes, dry-runs and Python fallback.",
                        "text/markdown", Encoding.UTF8.GetByteCount(ServerInstructions.Text)),
                    Def(ContractUri, "tool-contract", "Installed tool contract",
                        "The exact names, effects and JSON schemas compiled into this server.",
                        "application/json", Encoding.UTF8.GetByteCount(ContractText())),
                    Def(SecurityUri, "security-profile", "Current permission profile",
                        "The effective local capability posture, including whether temporary Python consent is active.",
                        "application/json", Encoding.UTF8.GetByteCount(SecurityText())),
                    Def(BuildUri, "build-identity", "Build identity",
                        "Version-independent contract and protocol identity of the running server.",
                        "application/json", Encoding.UTF8.GetByteCount(BuildText()))
                }
            };
        }

        public static JObject Read(JObject prms)
        {
            JToken token = prms?["uri"];
            if (token == null || token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                throw new McpError(-32602, "Invalid params: resources/read requires a non-empty string 'uri'.");
            string uri = (string)token;
            string mime;
            string text;
            switch (uri)
            {
                case GuidanceUri: mime = "text/markdown"; text = ServerInstructions.Text; break;
                case ContractUri: mime = "application/json"; text = ContractText(); break;
                case SecurityUri: mime = "application/json"; text = SecurityText(); break;
                case BuildUri: mime = "application/json"; text = BuildText(); break;
                default: throw new McpError(-32602, "Unknown Horizun resource URI: '" + uri + "'.");
            }
            return new JObject
            {
                ["contents"] = new JArray
                {
                    new JObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text }
                }
            };
        }

        private static JObject Def(string uri, string name, string title, string description, string mime, int size)
            => new JObject
            {
                ["uri"] = uri,
                ["name"] = name,
                ["title"] = title,
                ["description"] = description,
                ["mimeType"] = mime,
                ["size"] = size,
                ["annotations"] = new JObject
                {
                    ["audience"] = new JArray("assistant", "user"),
                    ["priority"] = uri == GuidanceUri ? 1.0 : 0.7
                }
            };

        private static string ContractText()
        {
            var rows = new JArray();
            foreach (Horizun.Contracts.CommandContract c in Horizun.Contracts.Contract.All)
                rows.Add(new JObject
                {
                    ["name"] = c.Name,
                    ["command"] = c.Command,
                    ["description"] = c.Description,
                    ["effect"] = c.Effect.ToString(),
                    ["destructive"] = c.Destructive,
                    ["open_world"] = c.OpenWorld,
                    ["input_schema"] = c.InputSchema?.DeepClone(),
                    ["output_schema"] = c.OutputSchema?.DeepClone()
                });
            return new JObject
            {
                ["protocol_version"] = Horizun.Contracts.Contract.ProtocolVersion,
                ["contract_hash"] = Horizun.Contracts.Contract.Hash,
                ["tools"] = rows
            }.ToString(Formatting.Indented);
        }

        private static string SecurityText()
        {
            DateTimeOffset? until = Horizun.Revit.Core.Settings.ExecutePythonTemporaryGrantUntilUtc;
            bool python = Horizun.Revit.Core.Settings.IsToolAllowed(
                Horizun.Contracts.Contract.Find("horizun_execute_python"), out _);
            return new JObject
            {
                ["permission_profile"] = Horizun.Revit.Core.Settings.PermissionProfile,
                ["execute_python_allowed"] = python,
                ["execute_python_temporary_grant_until_utc"] = until == null
                    ? JValue.CreateNull() : JToken.FromObject(until.Value.ToString("O")),
                // Refusal internals may contain the local settings path. A public MCP
                // resource reports effective policy, never the operator's home path.
                ["execute_python_refusal"] = python ? JValue.CreateNull() :
                    JToken.FromObject("Disabled by the effective local permission policy."),
                ["execute_python_refusal_code"] = python ? JValue.CreateNull() :
                    JToken.FromObject("effective_local_policy"),
                ["safe_default"] = "safe_write",
                ["settings_are_re_read_per_call"] = true
            }.ToString(Formatting.Indented);
        }

        private static string BuildText()
        {
            // WITHHOLDING WITHOUT EXPLAINING IS ITS OWN FAILURE. tools/list no longer
            // advertises a plugin tool the loaded add-in does not register, which is
            // right - and it leaves somebody looking for a tool that used to be there
            // with nothing to read. This is where they read it.
            JArray withheld;
            try { withheld = Tools.Withheld(); } catch { withheld = null; }
            var registry = new JObject
            {
                ["withheld_count"] = withheld == null ? (JToken)null : withheld.Count,
                ["withheld"] = withheld,
                ["means"] = withheld == null
                    ? "the withheld list could not be computed in this process."
                    : (withheld.Count == 0
                        ? "every tool this server publishes is answerable: host-resident, or a plugin command the " +
                          "loaded add-in registers. When no Revit has published a bridge nothing is withheld, " +
                          "because unknown is not absent."
                        : "these tools are NOT in tools/list. Each names the plugin command its answer needs and " +
                          "why the loaded add-in cannot give it. Rebuild and redeploy both halves together.")
            };
            return new JObject
            {
                ["server_name"] = "horizun-mcp",
                ["contract_hash"] = Horizun.Contracts.Contract.Hash,
                ["bridge_protocol_version"] = Horizun.Contracts.Contract.ProtocolVersion,
                ["supported_mcp_protocols"] = new JArray(ProtocolNegotiation.Supported),
                ["registry"] = registry
            }.ToString(Formatting.Indented);
        }

        private static void RejectCursor(JObject prms)
        {
            JToken cursor = prms?["cursor"];
            if (cursor != null && cursor.Type != JTokenType.Null)
                throw new McpError(-32602,
                    "resources/list has one bounded page and does not accept a cursor. Omit params.cursor.");
        }
    }
}
