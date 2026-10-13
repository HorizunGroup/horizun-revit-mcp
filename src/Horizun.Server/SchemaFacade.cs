// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// THE SCHEMA FACADE (3.0.0). tools/list used to carry every tool with its whole
// (abridged) schema: 417,545 B in the default safe_write profile, 483,087 B in the
// worst case, measured 2026-10-12 - 104k-121k estimated tokens that a client without
// deferred loading pays before the first question. The facade advertises four tools
// instead:
//
//   horizun_health          - unchanged, because every session starts with it;
//   horizun_list_tools      - what this session can call, grouped by toolset, one line each;
//   horizun_describe_tool   - the EXACT contract of one tool, or of one variant;
//   horizun_call_tool       - call one by name. The call is UNWRAPPED at the door
//                             (Program.DispatchToolCall) into the ordinary tools/call it
//                             stands for, so validation, schema_help, the permission and
//                             toolset gates, task augmentation, progress and cancellation
//                             are the SAME code a direct call runs. There is no second
//                             implementation to drift and no second gate to forget.
//
// WHAT THE FACADE DOES NOT CHANGE:
//   - the contract and Contract.Hash. The three meta-tools are server-only and are NOT
//     contract rows, so the add-in never sees them and a server/add-in pair keeps
//     agreeing. Their own identity is SchemaFacade.Hash, reported beside the contract
//     hash in horizun_health.schema_mode and horizun://build/identity;
//   - what can be called. Reachability is Tools.Advertisable(), the very filter the full
//     tools/list applies, and a direct tools/call of any tool still works;
//   - the exact schemas: horizun://contract/tools/{tool}[/{variant}] and schema_help.
//
// HORIZUN_SCHEMA_MODE=full serves the complete contract (held against the
// committed ledger by SchemaFacadeTests). The default lives in SchemaMode.Default;
// facade stays opt-in until an agent-quality battery can be measured.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    /// <summary>What tools/list advertises.</summary>
    internal enum SchemaSurface
    {
        /// <summary>Every reachable tool with its abridged schema - the 2.x surface, byte for byte.</summary>
        Full,
        /// <summary>horizun_health plus list / describe / call; every other tool on demand.</summary>
        Facade
    }

    internal static class SchemaMode
    {
        public const string EnvironmentVariable = "HORIZUN_SCHEMA_MODE";

        /// <summary>
        /// THE ONE PLACE THE DEFAULT LIVES. The isolated Codex client had no credentials
        /// and Claude had no quota for the 3.0.0 battery, so retain the full surface until
        /// a measured comparison supports changing it. Facade is opt-in.
        /// </summary>
        public const SchemaSurface Default = SchemaSurface.Full;

        /// <summary>The mode this process serves, re-read from its environment per call.</summary>
        public static SchemaSurface Current => Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable), out _, out _);

        /// <summary>
        /// The mode a value of <see cref="EnvironmentVariable"/> selects. Unset or blank is the
        /// default; an unknown value is ALSO the default, and says so in <paramref name="problem"/>
        /// rather than silently picking a surface for a typo.
        /// </summary>
        public static SchemaSurface Resolve(string raw, out string source, out string problem)
        {
            problem = null;
            string v = raw?.Trim();
            if (string.IsNullOrEmpty(v)) { source = "default"; return Default; }
            source = "environment";
            if (string.Equals(v, "full", StringComparison.OrdinalIgnoreCase)) return SchemaSurface.Full;
            if (string.Equals(v, "facade", StringComparison.OrdinalIgnoreCase)) return SchemaSurface.Facade;
            source = "default";
            problem = EnvironmentVariable + "='" + (v.Length > 40 ? v.Substring(0, 40) + "..." : v) + "' is not a " +
                      "schema mode; expected 'full' or 'facade'. Serving the default (" + Name(Default) + ").";
            return Default;
        }

        public static string Name(SchemaSurface s) => s == SchemaSurface.Full ? "full" : "facade";
    }

    internal static class SchemaFacade
    {
        public const string ListToolsName = "horizun_list_tools";
        public const string DescribeToolName = "horizun_describe_tool";
        public const string CallToolName = "horizun_call_tool";

        /// <summary>One summary line per tool in horizun_list_tools.</summary>
        internal const int SummaryChars = 160;

        // ---- the three meta-tools ---------------------------------------------------------

        private static JObject ListToolsDef() => new JObject
        {
            ["name"] = ListToolsName,
            ["title"] = "List Tools",
            ["description"] =
                "The Horizun tools this session can call, grouped by toolset, one line each. tools/list carries " +
                "only horizun_health and this facade; every other tool is reached through it: read a tool's exact " +
                "schema with " + DescribeToolName + ", then call it with " + CallToolName + ". A tool that is not " +
                "listed is switched off by the permission profile or a disabled toolset (toolsets_not_active); ask " +
                "the user to enable it.",
            ["inputSchema"] = JObject.Parse(@"{""type"":""object"",""properties"":{""toolset"":{""type"":""string"",
""description"":""Only this toolset, e.g. read, model, documentation. Omit for every callable tool.""}},
""additionalProperties"":false}"),
            ["outputSchema"] = OutputSchema(),
            ["annotations"] = Hints("List Tools", readOnly: true)
        };

        private static JObject DescribeToolDef() => new JObject
        {
            ["name"] = DescribeToolName,
            ["title"] = "Describe Tool",
            ["description"] =
                "The EXACT contract of one tool: every argument, type, enum, default and description, unabridged, " +
                "with its effect and toolsets - the schema " + CallToolName + " validates against. Read it before a " +
                "tool's first call. A discriminated tool (e.g. horizun_create_elements) is large whole and small per " +
                "variant: the reply lists its variants; pass variant=<discriminator value> for one. Same content as " +
                "the resource horizun://contract/tools/{tool}[/{variant}].",
            ["inputSchema"] = JObject.Parse(@"{""type"":""object"",""properties"":{""name"":{""type"":""string"",
""description"":""Tool name, as horizun_list_tools gives it.""},""variant"":{""type"":""string"",
""description"":""One discriminator value of a discriminated tool (listed under variants). Omit for the whole tool.""}},
""required"":[""name""],""additionalProperties"":false}"),
            ["outputSchema"] = OutputSchema(),
            ["annotations"] = Hints("Describe Tool", readOnly: true)
        };

        private static JObject CallToolDef() => new JObject
        {
            ["name"] = CallToolName,
            ["title"] = "Call Tool",
            ["description"] =
                "Call one Horizun tool by name. It IS that tool's tools/call: arguments are validated against its " +
                "exact schema (" + DescribeToolName + "), the same permission-profile, toolset, active-document and " +
                "dry-run rules apply, and the reply is that tool's own reply - including schema_help when the " +
                "arguments break the contract. Writes rehearse first unless their schema says otherwise.",
            ["inputSchema"] = JObject.Parse(@"{""type"":""object"",""properties"":{""name"":{""type"":""string"",
""description"":""Tool name, as horizun_list_tools gives it.""},""arguments"":{""type"":""object"",
""description"":""That tool's arguments, exactly as its schema lists them.""}},
""required"":[""name""],""additionalProperties"":false}"),
            ["outputSchema"] = OutputSchema(),
            ["annotations"] = Hints("Call Tool", readOnly: false)
        };

        // The shape every contract row declares (Contract.Annotate): an object reply. The call
        // tool's reply IS the called tool's, which declares the same.
        private static JObject OutputSchema() => new JObject
        {
            ["type"] = "object",
            ["additionalProperties"] = true
        };

        private static JObject Hints(string title, bool readOnly) => new JObject
        {
            ["title"] = title,
            ["readOnlyHint"] = readOnly,
            // The call tool can stand for any tool, so it carries the strongest hints any
            // tool behind it has; a client that gates on them must not be told less.
            ["destructiveHint"] = !readOnly,
            ["idempotentHint"] = readOnly,
            ["openWorldHint"] = !readOnly
        };

        private static readonly Lazy<string> _hash = new Lazy<string>(() =>
        {
            string text = new JArray(ListToolsDef(), DescribeToolDef(), CallToolDef()).ToString(Formatting.None);
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 12)
                                   .Replace("-", "").ToLowerInvariant();
        });

        /// <summary>
        /// The facade's own identity: a hash of the three meta-tool definitions, in the format
        /// of Contract.Hash. It is NOT folded into Contract.Hash - the add-in neither knows
        /// nor needs these tools, and folding it in would refuse every call to an add-in built
        /// from the same contract.
        /// </summary>
        public static string Hash => _hash.Value;

        public static bool IsMetaTool(string name, SchemaSurface mode) =>
            mode == SchemaSurface.Facade &&
            (name == ListToolsName || name == DescribeToolName || name == CallToolName);

        // ---- tools/list ---------------------------------------------------------------------

        /// <summary>What tools/list answers in <paramref name="mode"/>.</summary>
        public static JArray List(SchemaSurface mode, bool advertiseTaskSupport)
        {
            if (mode == SchemaSurface.Full) return Tools.List(advertiseTaskSupport);

            var arr = new JArray();
            ToolDef health = Tools.Advertisable().FirstOrDefault(t => t.Name == "horizun_health");
            if (health != null) arr.Add(Tools.Publish(health, advertiseTaskSupport));
            foreach (JObject def in new[] { ListToolsDef(), DescribeToolDef(), CallToolDef() })
            {
                if (advertiseTaskSupport)
                    // Only the call can run as a task: it becomes the task of the tool it calls,
                    // which then decides (McpTasks.Supports) exactly as a direct call would.
                    def["execution"] = new JObject
                    {
                        ["taskSupport"] = (string)def["name"] == CallToolName ? "optional" : "forbidden"
                    };
                arr.Add(def);
            }
            return arr;
        }

        // ---- tools/call ---------------------------------------------------------------------

        /// <summary>
        /// A horizun_call_tool request rewritten as the tools/call it stands for: the inner
        /// name and arguments, every other request field (_meta, task, inputResponses,
        /// requestState) carried over. Anything else - another tool, full mode, or a call
        /// whose own arguments are invalid - is returned unchanged; an invalid call then
        /// reaches <see cref="Answer"/>, which refuses it with the reason.
        /// </summary>
        public static JObject UnwrapCall(JObject prms, SchemaSurface mode)
        {
            if (mode != SchemaSurface.Facade || prms == null) return prms;
            JToken n = prms["name"];
            if (n == null || n.Type != JTokenType.String || (string)n != CallToolName) return prms;
            if (!TryInner(prms["arguments"], out string inner, out JToken innerArgs, out _)) return prms;

            var rewritten = new JObject();
            foreach (JProperty p in prms.Properties())
                if (p.Name != "name" && p.Name != "arguments") rewritten[p.Name] = p.Value.DeepClone();
            rewritten["name"] = inner;
            if (innerArgs != null) rewritten["arguments"] = innerArgs.DeepClone();
            return rewritten;
        }

        private static bool TryInner(JToken args, out string name, out JToken arguments, out string why)
        {
            name = null; arguments = null; why = null;
            const string Shape = " Expected {\"name\": \"<tool>\", \"arguments\": {...}}. Nothing was run.";
            if (!(args is JObject a)) { why = CallToolName + " needs its own arguments object." + Shape; return false; }
            string extra = a.Properties().Select(p => p.Name).FirstOrDefault(k => k != "name" && k != "arguments");
            if (extra != null)
            {
                why = CallToolName + " takes only 'name' and 'arguments'; '" + extra + "' belongs inside 'arguments'." + Shape;
                return false;
            }
            JToken n = a["name"];
            if (n == null || n.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)n))
            {
                why = CallToolName + " needs 'name', the tool to call, as a string." + Shape;
                return false;
            }
            name = (string)n;
            if (name == CallToolName)
            {
                why = CallToolName + " cannot call itself; name the tool directly." + Shape;
                return false;
            }
            if (Tools.Find(name) == null && name != ListToolsName && name != DescribeToolName)
            {
                why = "Unknown tool '" + name + "'. " + ListToolsName + " names every tool this session can call. " +
                      "Nothing was run.";
                return false;
            }
            JToken given = a["arguments"];
            if (given != null && given.Type != JTokenType.Object)
            {
                why = "'arguments' must be an object, not " + given.Type + ". It is refused rather than treated as " +
                      "no arguments at all, which would answer a different question. Nothing was run.";
                return false;
            }
            arguments = given;
            return true;
        }

        /// <summary>
        /// The answer to a meta-tool call. Throws <see cref="ToolRefusal"/> for arguments it
        /// cannot act on; the caller turns that into an isError result.
        /// </summary>
        public static JObject Answer(string name, JObject args)
        {
            switch (name)
            {
                case ListToolsName: return ListTools(args);
                case DescribeToolName: return Describe(args);
                case CallToolName:
                    // Reached only when UnwrapCall refused to unwrap, so this names why.
                    if (!TryInner(args, out _, out _, out string why)) throw new ToolRefusal(why);
                    throw new InvalidOperationException(CallToolName + " reached Answer with valid arguments; " +
                                                        "it must have been unwrapped at dispatch.");
                default:
                    throw new ToolRefusal("'" + name + "' is not a facade tool.");
            }
        }

        // ---- horizun_list_tools -----------------------------------------------------------------

        public static JObject ListTools(JObject args)
        {
            args = args ?? new JObject();
            RejectUnknown(args, ListToolsName, "toolset");
            string filter = null;
            JToken f = args["toolset"];
            if (f != null)
            {
                if (f.Type != JTokenType.String) throw new ToolRefusal("'toolset' must be a string.");
                filter = ((string)f).Trim().ToLowerInvariant();
                if (!ToolPacks.KnownPacks.Contains(filter))
                    throw new ToolRefusal("Unknown toolset '" + filter + "'. Known: " +
                        string.Join(", ", ToolPacks.KnownPacks.OrderBy(k => k, StringComparer.Ordinal)) + ".");
            }

            List<ToolDef> reachable = Tools.Advertisable();
            ToolPacks.Resolution sel = Settings.ActivePackResolution();
            var active = new HashSet<string>(new[] { "core" }.Concat(sel.ActivePacks ?? ToolPacks.KnownPacks),
                                             StringComparer.Ordinal);

            var groups = new JObject();
            var tools = new JObject();
            foreach (string pack in ToolPacks.KnownPacks.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!active.Contains(pack) || (filter != null && pack != filter)) continue;
                var names = new JArray(reachable.Where(t => t.Toolsets.Contains(pack)).Select(t => t.Name));
                if (names.Count > 0) groups[pack] = names;
            }
            foreach (ToolDef t in reachable)
            {
                if (filter != null && !t.Toolsets.Contains(filter)) continue;
                tools[t.Name] = Summary(t.Description);
            }

            return new JObject
            {
                ["mode"] = "facade",
                ["toolset"] = filter == null ? (JToken)JValue.CreateNull() : filter,
                ["tools"] = tools,
                ["toolsets"] = groups,
                ["toolsets_not_active"] = new JArray(ToolPacks.KnownPacks.Where(p => !active.Contains(p))
                                                              .OrderBy(p => p, StringComparer.Ordinal)),
                ["next"] = DescribeToolName + " {name} for the exact schema, then " + CallToolName +
                           " {name, arguments}. horizun_health first: commands act on the ACTIVE document.",
                ["contract_hash"] = Horizun.Contracts.Contract.Hash,
                ["facade_hash"] = Hash
            };
        }

        /// <summary>The first sentence, or the first <see cref="SummaryChars"/> characters cut at a word.</summary>
        internal static string Summary(string description)
        {
            string d = (description ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            for (int i = 20; i < d.Length - 1 && i < SummaryChars; i++)
                if (d[i] == '.' && d[i + 1] == ' ') return d.Substring(0, i + 1);
            if (d.Length <= SummaryChars) return d;
            int cut = d.LastIndexOf(' ', SummaryChars - 3);
            return d.Substring(0, cut > 40 ? cut : SummaryChars - 3).TrimEnd(',', ';', ':', ' ') + "...";
        }

        // ---- horizun_describe_tool ----------------------------------------------------------------

        public static JObject Describe(JObject args)
        {
            args = args ?? new JObject();
            RejectUnknown(args, DescribeToolName, "name", "variant");
            JToken n = args["name"];
            if (n == null || n.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)n))
                throw new ToolRefusal(DescribeToolName + " needs 'name', the tool to describe, as a string.");
            string name = (string)n;
            if (name == ListToolsName || name == DescribeToolName || name == CallToolName)
                throw new ToolRefusal("'" + name + "' is a facade tool; its whole schema is in tools/list.");
            Horizun.Contracts.CommandContract c = Horizun.Contracts.Contract.Find(name);
            ToolDef def = Tools.Find(name);
            if (c == null || def == null)
                throw new ToolRefusal("Unknown tool '" + name + "'. " + ListToolsName + " names every tool this " +
                                      "session can call.");

            // The SAME question the call asks: the profile/toolset gate, then the live bridge.
            string refusal = Tools.DisabledReason(name) ?? Tools.WithheldNow(def);
            var sites = ContractVariants.For(name).ToList();

            JToken v = args["variant"];
            if (v != null)
            {
                if (v.Type != JTokenType.String) throw new ToolRefusal("'variant' must be a string.");
                string value = (string)v;
                if (sites.Count == 0)
                    throw new ToolRefusal("'" + name + "' has no discriminated variants; describe it without 'variant'.");
                VariantSite site = sites.FirstOrDefault(s => s.BranchIndex.ContainsKey(value));
                if (site == null)
                    throw new ToolRefusal("Unknown " + string.Join("/", sites.Select(s => s.Discriminator).Distinct()) +
                        " '" + value + "' for " + name + ". Valid values: " +
                        string.Join(", ", sites.SelectMany(s => s.Values)) + ".");
                return new JObject
                {
                    ["name"] = name,
                    ["variant"] = value,
                    ["discriminator"] = site.Discriminator,
                    ["uri"] = site.Uri(value),
                    ["schema"] = site.Describe(value),
                    ["callable"] = refusal == null,
                    ["refusal"] = refusal == null ? (JToken)JValue.CreateNull() : refusal
                };
            }

            JObject row = McpResources.Row(c);
            row["contract_uri"] = McpResources.ContractToolPrefix + name;
            if (sites.Count > 0)
                row["variants"] = new JArray(sites.Select(s => new JObject
                {
                    ["discriminator"] = s.Discriminator,
                    ["pointer"] = s.Pointer,
                    ["values"] = new JArray(s.Values),
                    ["uri_template"] = McpResources.ContractToolPrefix + name + "/{" + s.Discriminator + "}"
                }));
            row["callable"] = refusal == null;
            row["refusal"] = refusal == null ? (JToken)JValue.CreateNull() : refusal;
            row["contract_hash"] = Horizun.Contracts.Contract.Hash;
            return row;
        }

        private static void RejectUnknown(JObject args, string tool, params string[] allowed)
        {
            string extra = args.Properties().Select(p => p.Name).FirstOrDefault(k => !allowed.Contains(k));
            if (extra != null)
                throw new ToolRefusal(tool + " takes " + string.Join(", ", allowed.Select(a => "'" + a + "'")) +
                                      "; '" + extra + "' is not one of them. Nothing was done.");
        }

        // ---- identity -------------------------------------------------------------------------------

        /// <summary>horizun_health.schema_mode: which surface tools/list carries, and what it costs.</summary>
        public static JObject HealthBlock()
        {
            try
            {
                SchemaSurface mode = SchemaMode.Resolve(Environment.GetEnvironmentVariable(SchemaMode.EnvironmentVariable),
                                                        out string source, out string problem);
                JArray list = List(mode, false);
                string json = list.ToString(Formatting.None);
                return new JObject
                {
                    ["mode"] = SchemaMode.Name(mode),
                    ["default"] = SchemaMode.Name(SchemaMode.Default),
                    ["source"] = source,
                    ["variable"] = SchemaMode.EnvironmentVariable,
                    ["problem"] = problem == null ? (JToken)JValue.CreateNull() : problem,
                    ["contract_hash"] = Horizun.Contracts.Contract.Hash,
                    ["facade_hash"] = Hash,
                    ["tools_list"] = new JObject
                    {
                        ["tools"] = list.Count,
                        ["utf8_bytes"] = Encoding.UTF8.GetByteCount(json),
                        ["approx_tokens"] = ToolsetReport.ApproxTokens(json.Length)
                    },
                    ["reachable_tools"] = Tools.Advertisable().Count,
                    ["means"] = mode == SchemaSurface.Full
                        ? "tools/list carries every reachable tool with its abridged schema (the 2.x surface)."
                        : "tools/list carries horizun_health and " + ListToolsName + " / " + DescribeToolName + " / " +
                          CallToolName + "; the reachable tools are called through them, under the same gates. " +
                          SchemaMode.EnvironmentVariable + "=full restores the whole list."
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["error"] = ex.Message };
            }
        }
    }
}
