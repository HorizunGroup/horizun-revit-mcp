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
        public Func<JObject, JObject> Host;
    }

    internal static class Tools
    {
        // The tool table is BUILT from the shared contract, never declared here. It used
        // to be declared in this file and restated in every command, and the two copies
        // drifted twice in one afternoon - a parameter added on one side only, a
        // description updated on one side only. Neither drift was detectable, because
        // the copies never met. Now there is one copy and this binds the server-only
        // half to it: which host function answers a tool that never reaches Revit.
        private static readonly Dictionary<string, Func<JObject, JObject>> Hosts =
            new Dictionary<string, Func<JObject, JObject>>(StringComparer.Ordinal)
            {
                { "horizun_job_status",       JobStatus.Handle },
                { "horizun_catalog_lookup",   CatalogLookup.Handle },
                { "horizun_excel_write_rows", ExcelWriteRows.Handle },
                { "horizun_power_bi_push",    PowerBiPush.Handle },
                { "horizun_target",           Targets.Handle }
            };

        private static readonly List<ToolDef> All = Build();

        private static List<ToolDef> Build()
        {
            var list = new List<ToolDef>();
            foreach (Horizun.Contracts.CommandContract c in Horizun.Contracts.Contract.All)
            {
                Func<JObject, JObject> host;
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

        public static JArray List()
        {
            var arr = new JArray();
            foreach (var t in All)
            {
                if (!IsEnabled(t)) continue;
                arr.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["title"] = Title(t.Name),
                    ["description"] = t.Description,
                    ["inputSchema"] = t.InputSchema,
                    ["outputSchema"] = t.OutputSchema,
                    ["annotations"] = Annotations(t),
                    // MCP's execution hint: may this tool be run as a long-lived task
                    // rather than a blocking call. DERIVED, like every other hint, from
                    // what the contract already knows - never a second list to maintain.
                    //
                    // A tool that forwards to Revit can be queued through
                    // horizun_submit_job, so a client may treat it as a task. A
                    // host-resident tool answers inside the server in milliseconds and
                    // submit_job refuses it by its own documented rule, so offering it as
                    // a task would advertise something that cannot happen.
                    ["execution"] = new JObject { ["taskSupport"] = TaskSupport(t) }
                });
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

        /// <summary>
        /// "optional" for anything that forwards to Revit - those are exactly the tools
        /// horizun_submit_job accepts, and a model scan or a batch open genuinely outlives
        /// a request. "forbidden" for host-resident tools and for the two submit_job
        /// itself refuses by name, because advertising a task a caller cannot create is
        /// worse than advertising nothing.
        /// </summary>
        private static string TaskSupport(ToolDef t)
        {
            bool hostResident = string.IsNullOrEmpty(t.Command);
            bool refusedByJobs = t.Name == "horizun_execute_python" ||
                                 t.Name == "horizun_submit_job";
            return (hostResident || refusedByJobs) ? "forbidden" : "optional";
        }

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
