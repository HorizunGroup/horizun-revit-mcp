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
            if (t.Name == "horizun_execute_python") return Horizun.Revit.Core.Settings.ExecutePythonEnabled;
            return true;
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
                    ["description"] = t.Description,
                    ["inputSchema"] = t.InputSchema
                });
            }
            return arr;
        }

        /// <summary>Why a known tool is not being offered right now, or null if it is.</summary>
        public static string DisabledReason(string toolName)
        {
            if (toolName == "horizun_execute_python" && !Horizun.Revit.Core.Settings.ExecutePythonEnabled)
                return Horizun.Revit.Core.Settings.ExecutePythonRefusal();
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
