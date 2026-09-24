// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// WHAT THE TOOLSET (TOOL PACK) SELECTION COSTS AND SAVES, IN THE UNIT A CLIENT PAYS.
//
// Served as horizun://session/toolsets and summarised inside horizun_health. It adds
// nothing to the selection itself - that is ToolPacks.cs, resolved by
// Settings.ActivePackResolution - and it reuses Protocol/DiscoveryCost for the byte
// measurement. What it adds is the unit a context window is billed in: characters
// and an ESTIMATE of tokens, for the current list, for the unrestricted list at the
// same permission posture, and for each toolset selected alone.
//
// Tokens are an estimate and say so: characters / 4, the usual rule of thumb for
// English-heavy JSON. Characters and UTF-8 bytes are exact.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    internal static class ToolsetReport
    {
        public const string ResourceUri = "horizun://session/toolsets";
        public const string TokenMethod = "approx_tokens = ceil(characters / 4); an estimate, not a tokenizer count";

        /// <summary>The serialised size of a tools/list answer, as the client receives it.</summary>
        internal static JObject Size(JArray tools)
        {
            string json = new JObject { ["tools"] = tools }.ToString(Formatting.None);
            return new JObject
            {
                ["tools"] = tools.Count,
                ["characters"] = json.Length,
                ["utf8_bytes"] = Encoding.UTF8.GetByteCount(json),
                ["approx_tokens"] = ApproxTokens(json.Length)
            };
        }

        internal static long ApproxTokens(long characters) => (characters + 3) / 4;

        /// <summary>The full document behind the resource.</summary>
        public static JObject Document(bool advertiseTaskSupport = false)
        {
            var o = new JObject
            {
                ["means"] = "Toolsets are tool packs: the same selection, resolution and refusal. Selected with " +
                            ToolPacks.EnvironmentOverride + " or " + ToolPacks.ToolsetsEnvironmentVariable +
                            " (comma-separated, 'all' for everything), or the \"" + ToolPacks.SettingsKey +
                            "\" / \"" + ToolPacks.ToolsetsSettingsKey + "\" settings key. core is always on; a " +
                            "toolset's dependencies come with it.",
                ["token_estimate"] = TokenMethod,
                ["measured"] = false
            };
            try
            {
                ToolPacks.Resolution sel = Settings.ActivePackResolution();
                o["selection"] = Describe(sel);

                JObject advertised = Size(Tools.List(advertiseTaskSupport));
                JObject baseline = Size(Tools.ListIgnoringPacks(advertiseTaskSupport));
                o["advertised"] = advertised;
                o["without_selection"] = baseline;
                long a = (long)advertised["characters"], b = (long)baseline["characters"];
                o["characters_saved"] = Math.Max(0, b - a);
                o["approx_tokens_saved"] = Math.Max(0, ApproxTokens(b) - ApproxTokens(a));
                o["reduction_percent"] = b <= 0 ? 0.0 : Math.Round(100.0 * Math.Max(0, b - a) / b, 2);

                var rows = new JArray();
                foreach (string pack in ToolPacks.KnownPacks.OrderBy(p => p, StringComparer.Ordinal))
                {
                    rows.Add(new JObject
                    {
                        ["toolset"] = pack,
                        ["description"] = Horizun.Contracts.ToolsetCatalog.Descriptions.TryGetValue(pack, out string d)
                            ? d : null,
                        ["member_tools"] = ToolPacks.MembersOf(pack).Count,
                        ["requires"] = new JArray(ToolPacks.DependenciesOf(pack)),
                        ["active"] = sel.ActivePacks == null || sel.ActivePacks.Contains(pack) || pack == "core",
                        // core + this toolset + its dependencies: what selecting it alone advertises.
                        ["if_selected_alone"] = Size(Tools.ListForPacks(new[] { pack }, advertiseTaskSupport))
                    });
                }
                o["toolsets"] = rows;
                o["aliases"] = JObject.FromObject(Horizun.Contracts.ToolsetCatalog.Aliases);
                o["measured"] = true;
            }
            catch (Exception ex)
            {
                // A measurement that failed must not read as a measurement of zero.
                o["error"] = ex.Message;
            }
            return o;
        }

        /// <summary>
        /// The block horizun_health carries. The add-in reports tool_packs from ITS OWN
        /// process; the selection a client configures (HORIZUN_TOOLSETS in the client's MCP
        /// entry) lives in THIS process's environment, which the add-in cannot see, so the
        /// server says what it is actually advertising.
        /// </summary>
        public static JObject HealthBlock()
        {
            try
            {
                JObject block = Describe(Settings.ActivePackResolution());
                JObject advertised = Size(Tools.List(false));
                block["tools_advertised"] = advertised["tools"];
                block["tools_list_characters"] = advertised["characters"];
                block["tools_list_approx_tokens"] = advertised["approx_tokens"];
                block["details"] = ResourceUri;
                return block;
            }
            catch (Exception ex)
            {
                return new JObject { ["error"] = ex.Message, ["details"] = ResourceUri };
            }
        }

        private static JObject Describe(ToolPacks.Resolution sel)
            => new JObject
            {
                ["source"] = sel.Source.ToString().ToLowerInvariant(),
                ["source_name"] = sel.SourceName == null ? (JToken)JValue.CreateNull() : sel.SourceName,
                ["restricting"] = sel.Restricting,
                ["active"] = sel.ActivePacks == null
                    ? (JToken)"all"
                    : new JArray(new[] { "core" }.Concat(sel.ActivePacks).Distinct()
                                                   .OrderBy(p => p, StringComparer.Ordinal)),
                ["chosen"] = sel.ChosenPacks == null ? (JToken)JValue.CreateNull() : new JArray(sel.ChosenPacks),
                ["added_by_dependency"] = sel.AddedByDependency == null
                    ? (JToken)JValue.CreateNull() : new JArray(sel.AddedByDependency),
                ["problem"] = sel.Problem == null ? (JToken)JValue.CreateNull() : sel.Problem
            };
    }
}
