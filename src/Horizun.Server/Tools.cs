// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// The tool table: the single place that declares which MCP tools exist, the
// plugin command each forwards to, and the JSON schema the client sees. It lives
// server-side on purpose — an MCP client calls tools/list at startup, often
// before Revit is even running, so the schemas cannot depend on reaching the
// plugin. As the deep tools are ported onto the plugin, they get one row here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal sealed class ToolDef
    {
        public string Name;
        public string Command;      // plugin command to forward to (null for a host-resident tool)
        public string Description;
        public JObject InputSchema;
        public JObject OutputSchema;
        public Horizun.Contracts.ToolEffect Effect;
        public bool Destructive;
        public bool OpenWorld;

        // A host-resident tool answers inside the server and never touches Revit. When Host
        // is non-null the server invokes it locally and does NOT forward to the plugin; when
        // it is null the tool forwards to Command over the pipe, exactly as before.
        public Func<JObject, CancellationToken, JObject> Host;
    }

    internal static class Tools
    {
        // The tool table is BUILT from the shared contract, never declared here. It used
        // to be declared in this file and restated in every command, and the two copies
        // drifted twice in one afternoon - a parameter added on one side only, a
        // description updated on one side only. Neither drift was detectable, because
        // the copies never met. Now there is one copy and this binds the server-only
        // half to it: which host function answers a tool that never reaches Revit.
        private static readonly Dictionary<string, Func<JObject, CancellationToken, JObject>> Hosts =
            new Dictionary<string, Func<JObject, CancellationToken, JObject>>(StringComparer.Ordinal)
            {
                { "horizun_job_status",       (a, ct) => JobStatus.Handle(a, ct) },
                { "horizun_catalog_lookup",   (a, ct) => CatalogLookup.Handle(a, ct) },
                { "horizun_excel_write_rows", (a, ct) => ExcelWriteRows.Handle(a, ct) },
                { "horizun_power_bi_push",    (a, ct) => PowerBiPush.Handle(a, ct) },
                { "horizun_target",           (a, ct) => { ct.ThrowIfCancellationRequested(); return Targets.Handle(a); } }
            };

        private static readonly List<ToolDef> All = Build();

        private static List<ToolDef> Build()
        {
            var list = new List<ToolDef>();
            foreach (Horizun.Contracts.CommandContract c in Horizun.Contracts.Contract.All)
            {
                Func<JObject, CancellationToken, JObject> host;
                Hosts.TryGetValue(c.Name, out host);

                // A contract with no plugin command and no host function would be a tool
                // that exists and can never answer. Better to know at startup.
                if (string.IsNullOrEmpty(c.Command) && host == null)
                    throw new InvalidOperationException(
                        "Tool '" + c.Name + "' names no plugin command and has no host handler, so nothing could " +
                        "answer it. Either give it a Command in the contract or bind it in Hosts.");

                list.Add(new ToolDef
                {
                    Name = c.Name,
                    Command = c.Command,
                    Description = c.Description,
                    InputSchema = c.InputSchema,
                    OutputSchema = c.OutputSchema,
                    Effect = c.Effect,
                    Destructive = c.Destructive,
                    OpenWorld = c.OpenWorld,
                    Host = host
                });
            }
            return list;
        }

        /// <summary>
        /// Tools that are switched off are NOT advertised. A client should not see a tool
        /// it will be refused for calling - and a capability that runs arbitrary code
        /// should not appear in a list somebody skims.
        /// </summary>
        private static bool IsEnabled(ToolDef t)
        {
            Horizun.Contracts.CommandContract contract = Horizun.Contracts.Contract.Find(t.Name);
            string reason;
            return Horizun.Revit.Core.Settings.IsToolAllowed(contract, out reason);
        }

        public static JArray List(bool advertiseTaskSupport = false)
        {
            var arr = new JArray();
            foreach (var t in All)
            {
                if (!IsEnabled(t)) continue;
                var published = new JObject
                {
                    ["name"] = t.Name,
                    ["title"] = Title(t.Name),
                    ["description"] = CompactDescription(t.Description),
                    ["inputSchema"] = t.InputSchema,
                    ["outputSchema"] = t.OutputSchema,
                    ["annotations"] = Annotations(t)

                    // execution/taskSupport is added below only for a negotiated
                    // 2025-11-25 session. Down-level clients never see the field. The
                    // optional/forbidden decision is the same rule the durable submit
                    // queue enforces, through McpTasks.Supports.
                };
                if (advertiseTaskSupport)
                    published["execution"] = new JObject
                    {
                        ["taskSupport"] = McpTasks.Supports(t) ? "optional" : "forbidden"
                    };
                arr.Add(published);
            }
            return arr;
        }

        private static string Title(string name)
        {
            string raw = name.StartsWith("horizun_", StringComparison.Ordinal) ? name.Substring(8) : name;
            string[] words = raw.Split('_');
            for (int i = 0; i < words.Length; i++)
                if (words[i].Length > 0) words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            return string.Join(" ", words);
        }

        internal static string CompactDescription(string description)
        {
            const int max = 900;
            const string suffix = " Full installed contract: horizun://contract/tools";
            if (string.IsNullOrWhiteSpace(description)) return suffix.Trim();
            string normalized = description.Replace("\r", " ").Replace("\n", " ").Trim();
            if (normalized.Length + suffix.Length <= max) return normalized + suffix;
            // THE ELLIPSIS IS PART OF THE BUDGET. It was not, and the cap this function
            // promises could be exceeded by one or two characters: with the sentence
            // boundary falling exactly on `limit` the result came back at 901, and
            // `cut += 1` could reach 902. Nothing detected it until a description landed
            // on the boundary, because every existing one happened to cut earlier - the
            // quiet kind of off-by-one that waits for the next tool. One character is
            // reserved here, and the two branches below can then only shorten.
            int limit = max - suffix.Length - 1;
            int cut = normalized.LastIndexOf(". ", limit, StringComparison.Ordinal);
            if (cut < Math.Min(160, limit / 2)) cut = limit;
            else cut += 1;
            return normalized.Substring(0, cut).TrimEnd() + "…" + suffix;
        }

        // The task-support rule lives in McpTasks.Supports so the advertised hint and
        // actual task admission cannot drift.

        private static JObject Annotations(ToolDef t)
        {
            // Every hint is READ from the contract. The two that used to be hardcoded
            // lists in this file - destructiveHint and openWorldHint - are declared on the
            // contract next to Effect, so a tool added without touching this file gets the
            // hints its own definition asked for instead of the safe-sounding default.
            bool readOnly = t.Effect == Horizun.Contracts.ToolEffect.ReadOnly;
            bool durable = t.Effect == Horizun.Contracts.ToolEffect.Mutating ||
                           t.Effect == Horizun.Contracts.ToolEffect.MutatingUnlessDryRun ||
                           t.Effect == Horizun.Contracts.ToolEffect.DocumentSession;
            return new JObject
            {
                ["title"] = Title(t.Name),
                ["readOnlyHint"] = readOnly,
                ["destructiveHint"] = t.Destructive,
                ["idempotentHint"] = readOnly || durable,
                ["openWorldHint"] = t.OpenWorld
            };
        }

        /// <summary>Why a known tool is not being offered right now, or null if it is.</summary>
        public static string DisabledReason(string toolName)
        {
            Horizun.Contracts.CommandContract contract = Horizun.Contracts.Contract.Find(toolName);
            string reason;
            if (contract != null && !Horizun.Revit.Core.Settings.IsToolAllowed(contract, out reason)) return reason;
            return null;
        }

        /// <summary>The full ToolDef for a name, or null if no such tool — the caller decides host vs. forward.</summary>
        public static ToolDef Find(string toolName)
        {
            foreach (var t in All)
                if (t.Name == toolName) return t;
            return null;
        }
    }
}
