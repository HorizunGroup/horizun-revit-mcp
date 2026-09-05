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
                { "horizun_excel_read_rows",  (a, ct) => { ct.ThrowIfCancellationRequested(); return ExcelReadRows.Handle(a); } },
                { "horizun_power_bi_push",    (a, ct) => PowerBiPush.Handle(a, ct) },
                { "horizun_budget_compare",   (a, ct) => BudgetCompare.Handle(a, ct) },
                { "horizun_target",           (a, ct) => { ct.ThrowIfCancellationRequested(); return Targets.Handle(a); } }
            };

        /// <summary>
        /// Every tool this server publishes. Internal rather than private so the tests
        /// can hold the published surface against the contract directly, instead of
        /// re-deriving it and comparing two derivations.
        /// </summary>
        internal static readonly List<ToolDef> All = Build();

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

        /// <summary>
        /// The add-in this server would route to right now, or null when none is
        /// discovered or the choice is ambiguous. Program installs it at startup; a test
        /// substitutes its own. It must never throw: a tool list that fails because Revit
        /// is busy is worse than one that lists everything.
        /// </summary>
        internal static Func<Discovered> LiveBridge;

        private static Discovered Live()
        {
            Func<Discovered> f = LiveBridge;
            if (f == null) return null;
            try { return f(); } catch { return null; }
        }

        /// <summary>
        /// Why this tool is NOT advertised to a client, or null when it is.
        ///
        /// A TOOL A CLIENT CAN SEE IS A TOOL A CLIENT WILL CALL. The per-call guard
        /// already refuses a command the loaded add-in does not register, and refuses a
        /// server and add-in built from different contracts - but the client had already
        /// been told the tool was there, so the refusal arrives as a surprise in the
        /// middle of somebody's work instead of as an absence they could plan around.
        /// The list now answers the same question the call does.
        ///
        /// THE THREE THINGS THIS IS CAREFUL NOT TO DO:
        ///   - it never withholds a HOST-RESIDENT tool: those are answered in this
        ///     process and need no Revit at all;
        ///   - it never treats UNKNOWN as absent: an add-in that published no command
        ///     list (before schema 3) says nothing about what it has, and a client
        ///     that started before Revit did has no bridge to ask;
        ///   - it keeps ONE source. The set of plugin commands comes from the contract
        ///     and the registration list comes from the add-in's own discovery file;
        ///     there is no third list here to go stale.
        /// </summary>
        internal static string WithheldReason(ToolDef t, Discovered live)
        {
            if (t == null) return null;
            if (t.Host != null) return null;                       // answered here; Revit is not involved
            if (string.IsNullOrEmpty(t.Command)) return null;      // host-resident by contract
            if (live == null) return null;                         // no bridge discovered: unknown, not absent

            // Two builds that disagree about the contract cannot exchange arguments
            // safely, so EVERY plugin tool is unusable until they are redeployed
            // together. Advertising them all and refusing them all one at a time is
            // the surprise this exists to remove.
            if (live.ContractHash != null && live.ContractHash != Horizun.Contracts.Contract.Hash)
                return "the Horizun add-in loaded in Revit " + live.Year + " (version " +
                       (live.AddinVersion ?? "unknown") + ", pid " + live.Pid + ") was built from a DIFFERENT " +
                       "command contract - server " + Horizun.Contracts.Contract.Hash + ", add-in " +
                       live.ContractHash + ". Close Revit and run install.ps1 so both halves move together.";

            bool? supports = live.Supports(t.Command);
            if (supports != false) return null;                    // registered, or the add-in published no list
            return "the Horizun add-in loaded in Revit " + live.Year + " (version " +
                   (live.AddinVersion ?? "unknown") + ", pid " + live.Pid + ") does not register '" + t.Command +
                   "', which is the command this tool needs. The two halves were not built from one tree: close " +
                   "Revit and run install.ps1.";
        }

        /// <summary>Every tool withheld right now, with the reason - the diagnostic half.</summary>
        internal static JArray Withheld()
        {
            Discovered live = Live();
            var arr = new JArray();
            foreach (var t in All)
            {
                string why = WithheldReason(t, live);
                if (why == null) continue;
                arr.Add(new JObject { ["name"] = t.Name, ["command"] = t.Command, ["reason"] = why });
            }
            return arr;
        }

        public static JArray List(bool advertiseTaskSupport = false)
        {
            var arr = new JArray();
            Discovered live = Live();
            foreach (var t in All)
            {
                if (!IsEnabled(t)) continue;
                if (WithheldReason(t, live) != null) continue;
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
