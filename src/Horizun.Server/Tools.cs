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
                    ["annotations"] = Annotations(t)

                    // NO execution/taskSupport BLOCK, deliberately.
                    //
                    // It used to be emitted here, derived from whether a tool forwards to
                    // Revit. The derivation was sound and the field was still wrong to
                    // send: execution.taskSupport belongs to MCP Tasks (2025-11-25), and
                    // this server implements no tasks/* method and declares no "tasks"
                    // capability - initialize returns capabilities {"tools":{}}. So the
                    // hint invited a client to call tasks/create and get "method not
                    // found" for work it believed it had submitted.
                    //
                    // The long-running path is real, it is just not MCP's:
                    // horizun_submit_job returns a job_id immediately and
                    // horizun_job_status reads the durable record WITHOUT touching Revit,
                    // which is what lets it answer while the UI thread is busy. That is a
                    // Horizun extension and is documented as one. If tasks/* is ever
                    // implemented and proved against the spec, this field comes back
                    // together with the capability and the methods - not before.
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

        // TaskSupport(ToolDef) lived here and derived "optional"/"forbidden" for the MCP
        // execution hint. It is gone with the field it fed - see List(). Deleted rather
        // than left unused: a private helper nobody calls is the seed of the field
        // reappearing without the capability and the methods that would make it true.
        // The rule it encoded (submit_job takes exactly the tools that forward to Revit)
        // is still asserted, against the contract, in TaskSupportTests.

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
