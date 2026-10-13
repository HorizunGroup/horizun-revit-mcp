// -----------------------------------------------------------------------------
// Horizun MCP server — standard MCP Resources.
//
// These are intentionally virtual horizun:// resources, not file:// paths. An
// installed server cannot assume that its source checkout exists, and publishing a
// builder/user path would leak local state. Every byte is derived from the running
// binary and the shared contract, so it cannot drift from what tools/list advertises.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpResources
    {
        private const string GuidanceUri = "horizun://guidance/typed-first";
        internal const string ContractUri = "horizun://contract/tools";
        // One tool, or one variant of one tool, of the same contract. Served here, not
        // contract rows, so Contract.Hash does not move when they change.
        internal const string ContractToolPrefix = ContractUri + "/";
        internal const string ToolTemplate = ContractUri + "/{tool}";
        internal const string VariantTemplate = ContractUri + "/{tool}/{variant}";
        private const string SecurityUri = "horizun://security/current-profile";
        private const string BuildUri = "horizun://build/identity";
        private const string WorkflowsUri = "horizun://workflows/bim-production";
        private const string ProjectContextSchemaUri = ProjectContext.SchemaResourceUri;

        public static JObject List(JObject prms)
        {
            RejectCursor(prms);
            return new JObject
            {
                ["resources"] = new JArray
                {
                    Def(GuidanceUri, "typed-first-guidance", "Verified Revit workflow",
                        "The operating rules for health-first targeting, typed writes, dry-runs and Python fallback.",
                        "text/markdown", Encoding.UTF8.GetByteCount(ServerInstructions.TextFor(SchemaMode.Current))),
                    Def(ContractUri, "tool-contract", "Installed tool contract",
                        "The exact names, effects and JSON schemas compiled into this server.",
                        "application/json", Encoding.UTF8.GetByteCount(ContractText())),
                    Def(SecurityUri, "security-profile", "Current permission profile",
                        "The effective local capability posture, including whether temporary Python consent is active.",
                        "application/json", Encoding.UTF8.GetByteCount(SecurityText())),
                    Def(BuildUri, "build-identity", "Build identity",
                        "Version-independent contract and protocol identity of the running server.",
                        "application/json", Encoding.UTF8.GetByteCount(BuildText())),
                    Def(WorkflowsUri, "bim-production-workflows", "BIM Production Workflows",
                        "Task-oriented workflow catalog over the installed typed tool surface.",
                        "application/json", Encoding.UTF8.GetByteCount(WorkflowText())),
                    Def(ProjectContextSchemaUri, "project-context-schema", "Project context schema (ISO 19650)",
                        "JSON Schema (draft 2020-12) of project-context.json, schema_version 1: appointment, EIR/BEP/MIDP/TIDP, CDE states, container naming, classification, georeference and delivery. horizun_project_context validates against it.",
                        "application/schema+json", Encoding.UTF8.GetByteCount(ProjectContext.SchemaText)),
                    Def(ToolsetReport.ResourceUri, "session-toolsets", "Active toolsets",
                        "Which toolsets (tool packs) this session advertises, and the measured size of tools/list " +
                        "with and without the selection, in characters and estimated tokens.",
                        "application/json", Encoding.UTF8.GetByteCount(ToolsetText())),
                    // The MCP App. It is listed like any other resource because it IS
                    // one; what makes it an app is its mime type and the tool that
                    // names it, not a separate listing mechanism.
                    McpAppResources.Definition(),
                    ImpactPreviewApp.Definition()
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
                case GuidanceUri: mime = "text/markdown"; text = ServerInstructions.TextFor(SchemaMode.Current); break;
                case ContractUri: mime = "application/json"; text = ContractText(); break;
                case SecurityUri: mime = "application/json"; text = SecurityText(); break;
                case BuildUri: mime = "application/json"; text = BuildText(); break;
                case WorkflowsUri: mime = "application/json"; text = WorkflowText(); break;
                case ProjectContextSchemaUri: mime = "application/schema+json"; text = ProjectContext.SchemaText; break;
                case ToolsetReport.ResourceUri: mime = "application/json"; text = ToolsetText(); break;
                case McpAppResources.ClashViewerUri:
                    return AppContent(uri, McpAppResources.Html(), McpAppResources.ResourceMeta());
                case ImpactPreviewApp.Uri:
                    return AppContent(uri, ImpactPreviewApp.Html(), ImpactPreviewApp.ResourceMeta());
                default:
                    // The exact contract URI matched above; only its children reach here.
                    if (uri.StartsWith(ContractToolPrefix, StringComparison.Ordinal))
                    {
                        mime = "application/json";
                        text = ContractPartText(uri);
                        break;
                    }
                    throw new McpError(-32602, "Unknown Horizun resource URI: '" + uri + "'.");
            }
            return new JObject
            {
                ["contents"] = new JArray
                {
                    new JObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text }
                }
            };
        }

        /// <summary>
        /// An MCP App's resources/read reply. The CSP travels on the content item too:
        /// the 2026-01-26 spec reads `_meta.ui` there as well as on the listing.
        /// </summary>
        private static JObject AppContent(string uri, string html, JObject meta) => new JObject
        {
            ["contents"] = new JArray
            {
                new JObject
                {
                    ["uri"] = uri, ["mimeType"] = McpAppResources.AppMimeType,
                    ["text"] = html, ["_meta"] = meta
                }
            }
        };

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

        private static string ToolsetText() => ToolsetReport.Document().ToString(Formatting.Indented);

        /// <summary>
        /// The resource templates: one tool's contract row, and one variant of a tool whose
        /// schema is a discriminated union. They exist because tools/list advertises an
        /// abridged copy (see Tools.CompactSchema); these serve the contract's own objects.
        /// </summary>
        internal static JArray Templates()
        {
            string sites = string.Join(", ", ContractVariants.All.Select(v => v.Tool + " (" + v.Discriminator + ")"));
            return new JArray
            {
                new JObject
                {
                    ["uriTemplate"] = ToolTemplate,
                    ["name"] = "tool-contract-row",
                    ["title"] = "One tool's contract",
                    ["description"] = "The exact contract row of one tool, as " + ContractUri + " holds it: the full " +
                        "input_schema with every description untruncated. Read it when a tools/list schema is abridged.",
                    ["mimeType"] = "application/json"
                },
                new JObject
                {
                    ["uriTemplate"] = VariantTemplate,
                    ["name"] = "tool-contract-variant",
                    ["title"] = "One variant of a tool's contract",
                    ["description"] = "The exact schema of one discriminated variant, verbatim from the contract, " +
                        "with the fields it requires. {variant} is the discriminator value. Variants exist for: " + sites + ".",
                    ["mimeType"] = "application/json"
                }
            };
        }

        /// <summary>horizun://contract/tools/{tool} and .../{tool}/{variant}. Names are
        /// [a-z0-9_]; anything else, and any unknown value, is -32602 naming what is valid.</summary>
        private static string ContractPartText(string uri)
        {
            string[] parts = uri.Substring(ContractToolPrefix.Length).Split('/');
            if (parts.Length > 2 || parts.Any(p => p.Length == 0 || p.Any(ch => !(ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' || ch == '_'))))
                throw new McpError(-32602, "Invalid contract URI '" + uri + "': expected " + ToolTemplate + " or " +
                    VariantTemplate + ", each name of [a-z0-9_].");
            Horizun.Contracts.CommandContract c = Horizun.Contracts.Contract.Find(parts[0]);
            if (c == null)
                throw new McpError(-32602, "Unknown tool '" + parts[0] + "' in '" + uri + "'. Valid tools: " +
                    string.Join(", ", Horizun.Contracts.Contract.All.Select(x => x.Name)) + ".");
            if (parts.Length == 1) return Row(c).ToString(Formatting.Indented);

            var sites = ContractVariants.For(c.Name).ToList();
            if (sites.Count == 0)
                throw new McpError(-32602, "'" + c.Name + "' has no discriminated variants; its whole schema is at " +
                    ContractToolPrefix + c.Name + ".");
            // More than one site in one tool resolves to the first that knows the value;
            // ContractTemplateTests pins today's sites (one per tool) so a second is deliberate.
            VariantSite site = sites.FirstOrDefault(v => v.BranchIndex.ContainsKey(parts[1]));
            if (site == null)
                throw new McpError(-32602, "Unknown " + string.Join("/", sites.Select(v => v.Discriminator).Distinct()) +
                    " '" + parts[1] + "' for " + c.Name + ". Valid values: " +
                    string.Join(", ", sites.SelectMany(v => v.Values)) + ".");
            return site.Describe(parts[1]).ToString(Formatting.Indented);
        }

        /// <summary>One contract row: the whole-contract document and the per-tool read
        /// share it, so the two can never disagree about a tool.</summary>
        internal static JObject Row(Horizun.Contracts.CommandContract c) => new JObject
        {
            ["name"] = c.Name,
            ["command"] = c.Command,
            ["description"] = c.Description,
            ["effect"] = c.Effect.ToString(),
            ["destructive"] = c.Destructive,
            ["open_world"] = c.OpenWorld,
            ["toolsets"] = new JArray(c.Toolsets ?? new string[0]),
            ["external_content"] = c.ExternalContent,
            ["input_schema"] = c.InputSchema?.DeepClone(),
            ["output_schema"] = c.OutputSchema?.DeepClone()
        };

        private static string ContractText()
        {
            var rows = new JArray();
            foreach (Horizun.Contracts.CommandContract c in Horizun.Contracts.Contract.All)
                rows.Add(Row(c));
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
                ["mcp_paused"] = Horizun.Revit.Core.Settings.McpPaused,
                ["mcp_paused_means"] = "When true, only horizun_health is available until the local Revit owner resumes MCP.",
                ["force_read_only_on_workshared"] = Horizun.Revit.Core.Settings.ForceReadOnlyOnWorkshared,
                ["sync_with_central_owner_granted"] = Horizun.Revit.Core.Settings.SyncWithCentralOwnerEnabled,
                ["sync_with_central_owner_granted_means"] = "The machine owner's grant for synchronize with central (Advanced options). The typed operation also needs permission_profile=full_write; force_read_only_on_workshared wins over it.",
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
                // The facade's identity beside the contract's: the two hashes are independent
                // (SchemaFacade.cs), and the mode says which surface tools/list carries.
                ["facade_hash"] = SchemaFacade.Hash,
                ["schema_mode"] = SchemaMode.Name(SchemaMode.Current),
                ["bridge_protocol_version"] = Horizun.Contracts.Contract.ProtocolVersion,
                // EVERY revision, not only the ones reachable through initialize.
                // ProtocolNegotiation.Supported is the legacy half by design; a reader
                // asking what this server speaks must be told about 2026-07-28 too.
                ["supported_mcp_protocols"] = new JArray(Protocol.McpRevision.All),
                ["legacy_initialize_protocols"] = new JArray(ProtocolNegotiation.Supported),
                ["mcp_extensions"] = ExtensionRows(),
                ["registry"] = registry
            }.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Every extension this build knows about, advertised or not, and what is missing
        /// from the ones that are not. Withholding without explaining is its own failure:
        /// a client that wondered why io.modelcontextprotocol/ui is absent reads it here
        /// instead of filing a bug about a capability that was never claimed.
        /// </summary>
        private static JArray ExtensionRows()
        {
            var arr = new JArray();
            foreach (Protocol.McpExtension e in Protocol.ExtensionRegistry.All)
                arr.Add(new JObject
                {
                    ["id"] = e.Id,
                    ["advertised"] = e.Implemented,
                    ["pending"] = e.Pending == null ? (JToken)JValue.CreateNull() : e.Pending
                });
            return arr;
        }

        private static string WorkflowText() => McpWorkflowCatalog.Document().ToString(Formatting.Indented);

        private static void RejectCursor(JObject prms)
        {
            JToken cursor = prms?["cursor"];
            if (cursor != null && cursor.Type != JTokenType.Null)
                throw new McpError(-32602,
                    "resources/list has one bounded page and does not accept a cursor. Omit params.cursor.");
        }
    }
}
