// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_activate_toolset: a server-only meta-tool (like the schema facade's
// list / describe / call - NOT a contract row, so Contract.Hash cannot move and the
// add-in never sees it) that lets a session whose toolsets were restricted by its
// client configuration (HORIZUN_TOOLSETS=local, say) open one more toolset when the
// work calls for it, without a restart.
//
// WHAT IT DOES: adds the toolset - and the toolsets it depends on - to this server
// process's selection (ToolPacks.ActivateForSession). That is all.
// WHAT IT NEVER DOES:
//   * write settings.json or an environment variable: it is gone on restart;
//   * open anything the permission profile, denied_tools or allowed_tools switch off:
//     the very IsToolAllowed that hides a tool still hides it after the toolset opens,
//     and the answer names each such tool and the reason;
//   * widen a selection the settings FILE decided: the Revit add-in enforces that one
//     too and cannot be told, so it is refused with the reason (ToolPacks.CanActivate).
//
// It is advertised only while a restriction can be widened, so an unrestricted session
// (the default) lists exactly what it always listed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    internal static class ToolsetActivation
    {
        public const string Name = "horizun_activate_toolset";

        /// <summary>
        /// Called after a successful activation so the server can announce
        /// notifications/tools/list_changed (Program installs ToolListMonitor.CheckNow).
        /// </summary>
        internal static Action AfterActivation;

        /// <summary>Is the tool offered right now: a restriction exists and a session may widen it.</summary>
        public static bool Offered() => ToolPacks.CanActivate(Settings.ActivePackResolution(), out _);

        public static JObject Definition() => new JObject
        {
            ["name"] = Name,
            ["title"] = "Activate Toolset",
            ["description"] =
                "Turn on one more toolset for THIS session only (nothing is saved; a restart forgets it), e.g. " +
                "documentacion for views and sheets, modelado for more writes, auditoria. Accepts a toolset name " +
                "or its Spanish alias. It never opens a tool the permission profile or the owner's allow/deny " +
                "lists switch off: the answer lists what became available and what stays blocked, and why.",
            ["inputSchema"] = JObject.Parse(@"{""type"":""object"",""properties"":{""toolset"":{""type"":""string"",
""description"":""Toolset to activate, e.g. documentation, model, read, audit, or an alias such as documentacion. horizun_list_tools shows which toolsets are not active.""}},
""required"":[""toolset""],""additionalProperties"":false}"),
            ["outputSchema"] = new JObject { ["type"] = "object", ["additionalProperties"] = true },
            ["annotations"] = new JObject
            {
                ["title"] = "Activate Toolset",
                ["readOnlyHint"] = false,
                ["destructiveHint"] = false,
                ["idempotentHint"] = true,
                ["openWorldHint"] = false
            }
        };

        /// <summary>
        /// The compact-mode text: the same tool, said within SchemaCompact.DescriptionMax like every
        /// other compact description (the long one measured 372 characters).
        /// </summary>
        internal const string CompactDescription =
            "Turn on one more toolset for this session only (e.g. documentacion, modelado). Never opens what the " +
            "permission profile blocks. Use it when horizun_list_tools names a toolset to enable.";

        /// <summary>Adds the definition to a tools/list answer when it is offered.</summary>
        public static JArray AppendIfOffered(JArray list, bool advertiseTaskSupport, bool compact = false)
        {
            if (!Offered()) return list;
            JObject def = Definition();
            if (compact) def["description"] = CompactDescription;
            if (advertiseTaskSupport)
                def["execution"] = new JObject { ["taskSupport"] = "forbidden" };
            list.Add(def);
            return list;
        }

        /// <summary>The answer. Throws <see cref="ToolRefusal"/> for arguments or a state it cannot act on.</summary>
        public static JObject Answer(JObject args)
        {
            args = args ?? new JObject();
            string extra = args.Properties().Select(p => p.Name).FirstOrDefault(k => k != "toolset");
            if (extra != null)
                throw new ToolRefusal(Name + " takes only 'toolset'; '" + extra + "' is not one of its arguments. " +
                                      "Nothing was changed.");
            JToken t = args["toolset"];
            if (t == null || t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t))
                throw new ToolRefusal(Name + " needs 'toolset', the toolset to activate, as a string (e.g. " +
                                      "documentation or documentacion). Nothing was changed.");
            string asked = (string)t;
            string[] real = Horizun.Contracts.ToolsetCatalog.ExpandName(asked);
            if (real == null)
            {
                ToolPacks.ActivateForSession(asked, out string unknown);   // builds the sentence; adds nothing
                throw new ToolRefusal(unknown);
            }

            ToolPacks.Resolution selection = Settings.ActivePackResolution();
            if (!selection.Restricting)
                return new JObject
                {
                    ["restricting"] = false,
                    ["toolset"] = real.Length == 1 ? real[0] : Horizun.Contracts.ToolsetCatalog.Normalize(asked),
                    ["activated"] = new JArray(),
                    ["newly_available"] = new JArray(),
                    ["still_blocked"] = new JArray(),
                    ["note"] = "No toolset restriction is active, so every toolset is already visible. Nothing was changed."
                };
            if (!ToolPacks.CanActivate(selection, out string why))
                throw new ToolRefusal("Cannot activate '" + asked + "': " + why + ". Nothing was changed.");

            var before = new HashSet<string>(Tools.Advertisable().Select(d => d.Name), StringComparer.Ordinal);
            bool wasLocalOnly = selection.LocalOnly;

            ToolPacks.ActivateForSession(asked, out _);

            // The toolsets this opens: the ones asked for plus their dependencies, transitively.
            var opened = new SortedSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(real);
            while (queue.Count > 0)
            {
                string pack = queue.Dequeue();
                if (!opened.Add(pack)) continue;
                foreach (string dependency in ToolPacks.DependenciesOf(pack)) queue.Enqueue(dependency);
            }

            List<ToolDef> reachable = Tools.Advertisable();
            var after = new HashSet<string>(reachable.Select(d => d.Name), StringComparer.Ordinal);
            var newly = reachable.Where(d => !before.Contains(d.Name)).Select(d => d.Name).ToList();

            // What the toolsets declare but this session still cannot call, with the reason the
            // gate itself gives: a tool the profile or the lists turn off keeps its own sentence.
            var candidates = new HashSet<string>(opened.SelectMany(ToolPacks.MembersOf), StringComparer.Ordinal);
            if (wasLocalOnly) foreach (string core in ToolPacks.MembersOf("core")) candidates.Add(core);
            var blocked = new JArray();
            foreach (ToolDef def in Tools.All)
            {
                if (!candidates.Contains(def.Name) || after.Contains(def.Name)) continue;
                blocked.Add(new JObject
                {
                    ["tool"] = def.Name,
                    ["reason"] = Tools.DisabledReason(def.Name) ?? Tools.WithheldNow(def) ?? "not reachable right now"
                });
            }

            // Announce BEFORE answering: a client that supports the notification re-reads tools/list.
            try { AfterActivation?.Invoke(); } catch (Exception ex) { Log.Warn("tools/list_changed after activation: " + ex.Message); }

            ToolPacks.Resolution now = Settings.ActivePackResolution();
            return new JObject
            {
                ["restricting"] = true,
                ["toolset"] = real.Length == 1 ? real[0] : Horizun.Contracts.ToolsetCatalog.Normalize(asked),
                ["requested"] = asked,
                ["activated"] = new JArray(opened),
                ["already_active"] = newly.Count == 0 && blocked.Count == 0,
                ["newly_available"] = new JArray(newly),
                ["still_blocked"] = blocked,
                ["active_toolsets"] = new JArray(ToolPacks.ActiveNames(now).OrderBy(p => p, StringComparer.Ordinal)),
                ["activated_this_session"] = new JArray(now.ActivatedThisSession),
                ["scope"] = "this server process only: nothing was written to settings.json or the environment, " +
                            "and a restart forgets it",
                ["tools_list_changed"] = "announced to clients that support notifications/tools/list_changed; others " +
                                         "read tools/list again, or call the tools by name",
                ["next"] = "Call a newly available tool by name (horizun_describe_tool {name} shows its schema in " +
                           "facade mode). still_blocked tools stay refused for the reason given; only the machine's " +
                           "owner changes that."
            };
        }
    }
}
