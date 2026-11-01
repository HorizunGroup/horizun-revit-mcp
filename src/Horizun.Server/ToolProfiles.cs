// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// TOOL PROFILES: which reachable tools tools/list SHOWS (product owner, 2026-10-31).
//
// A client without deferred tool loading pays for the whole tools/list before the
// first question: ~105k tokens for the 113 tools of the default profile. A profile
// names a ROLE, and the list shows that role's toolsets plus four server tools:
//
//   horizun_list_tools     - every tool this session can call, searchable (ES/EN);
//   horizun_describe_tool  - the exact schema of one tool;
//   horizun_call_tool      - call any reachable tool by name;
//   horizun_activate_toolset - show one more toolset for this session.
//
// VISIBILITY, NEVER PERMISSION. Two questions are kept apart:
//   * may this call run?  - Settings.IsToolAllowed, unchanged: the permission profile,
//     denied/allowed tools, the Python grant and HORIZUN_TOOLSETS / tool_packs. A tool
//     refused there is refused on every path, and a profile can never list it;
//   * should it be in the list? - this file. A tool a profile does not list is still
//     called by name or through horizun_call_tool, under the very same gates.
//
// WHERE THE PROFILE COMES FROM, first match wins:
//   1. HORIZUN_PROFILE in the client's own MCP server entry;
//   2. "tool_profile" in settings.json - a NEW install writes "esencial";
//   3. nothing: "completo", every tool listed, the list as it always was. An upgrade
//      has no tool_profile, so nothing changes for anyone already using the bridge.
// A value is a profile name or a comma-separated list of toolsets (Spanish aliases
// accepted). An unknown value lists everything - visibility must never lose a
// capability - and horizun_health.tool_profile.problem says why.
//
// SESSION ACTIVATIONS (horizun_activate_toolset) live in this process's memory only:
// they never write settings.json and end with the process.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    internal static class ToolProfiles
    {
        public const string EnvironmentVariable = "HORIZUN_PROFILE";
        public const string SettingsKey = "tool_profile";
        public const string Full = "completo";
        public const string Essential = "esencial";

        /// <summary>Every profile but completo, in display order, with the toolsets it lists.</summary>
        private static readonly List<KeyValuePair<string, string[]>> Roles = new List<KeyValuePair<string, string[]>>
        {
            P(Essential, "local"),
            P("consulta", "local", "read"),
            P("modelado", "local", "model", "architecture", "structure", "mep"),
            P("documentacion", "local", "documentation", "planimetry", "schedules"),
            P("auditoria", "local", "read", "audit", "coordination"),
        };

        private static readonly Dictionary<string, string> RoleSentence = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Essential] = "The local tools: health, document facts, list and query, model scan, capture, typed writes, undo.",
            ["consulta"] = "Essential plus reading: quantities, schedules, rooms, relations, model diff.",
            ["modelado"] = "Essential plus model writes, architecture, structure and MEP.",
            ["documentacion"] = "Essential plus views, sheets, annotation, planimetry and schedules.",
            ["auditoria"] = "Essential plus reading, audits, clash and coordination.",
            [Full] = "Every tool: the list as it always was."
        };

        // English (and other) spellings of the profile names; normalised like toolset names.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["essential"] = Essential, ["basico"] = Essential, ["basic"] = Essential,
            ["query"] = "consulta", ["reading"] = "consulta",
            ["modeling"] = "modelado", ["modelling"] = "modelado", ["modelador"] = "modelado",
            ["documentation"] = "documentacion", ["documentador"] = "documentacion",
            ["audit"] = "auditoria", ["auditor"] = "auditoria",
            ["full"] = Full, ["complete"] = Full, ["all"] = Full, ["todo"] = Full
        };

        private static KeyValuePair<string, string[]> P(string name, params string[] toolsets) =>
            new KeyValuePair<string, string[]>(name, toolsets);

        /// <summary>The role profiles, completo excluded.</summary>
        public static IReadOnlyList<string> RoleProfiles { get; } = Roles.Select(r => r.Key).ToList();

        private static readonly object Gate = new object();
        private static readonly SortedSet<string> Shown = new SortedSet<string>(StringComparer.Ordinal);

        /// <summary>What this session lists.</summary>
        internal sealed class Resolution
        {
            public string Name;                 // profile name, "custom" for a toolset list, or completo
            public string Source;               // HORIZUN_PROFILE, tool_profile or default
            public string Requested;            // the value as written
            public string Problem;              // why an unknown value lists everything
            public string[] Toolsets;           // null: everything
            public string[] ShownThisSession = new string[0];
            public bool Restricting => Toolsets != null;
        }

        internal static void ResetSessionForTests() { lock (Gate) Shown.Clear(); }

        public static Resolution Current()
        {
            string raw = null, source = "default";
            try { raw = Environment.GetEnvironmentVariable(EnvironmentVariable); } catch { raw = null; }
            if (!string.IsNullOrWhiteSpace(raw)) source = EnvironmentVariable;
            else
            {
                raw = Settings.RawValue(SettingsKey);
                if (!string.IsNullOrWhiteSpace(raw)) source = SettingsKey;
            }
            Resolution r = Resolve(raw);
            r.Source = string.IsNullOrWhiteSpace(raw) ? "default" : source;
            if (r.Restricting)
                lock (Gate)
                {
                    r.ShownThisSession = Shown.ToArray();
                    r.Toolsets = r.Toolsets.Union(Shown, StringComparer.Ordinal).ToArray();
                }
            return r;
        }

        internal static Resolution Resolve(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new Resolution { Name = Full };
            string n = ToolsetCatalog.Normalize(raw);
            if (Aliases.TryGetValue(n, out string alias)) n = alias;
            if (n == Full) return new Resolution { Name = Full, Requested = raw };
            foreach (KeyValuePair<string, string[]> role in Roles)
                if (role.Key == n) return new Resolution { Name = role.Key, Requested = raw, Toolsets = role.Value };

            // A comma-separated list of toolsets, Spanish aliases accepted.
            var sets = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string part in raw.Split(','))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                string[] real = ToolsetCatalog.ExpandName(part);
                if (real == null)
                    return new Resolution
                    {
                        Name = Full, Requested = raw,
                        Problem = "'" + raw.Trim() + "' is neither a tool profile (" + string.Join(", ", RoleProfiles) + ", " +
                                  Full + ") nor a list of toolsets ('" + part.Trim() + "' is unknown), so every tool is " +
                                  "listed. Fix " + SettingsKey + " in settings.json or " + EnvironmentVariable + " in the client."
                    };
                foreach (string s in real) sets.Add(s);
            }
            if (sets.Count == 0) return new Resolution { Name = Full, Requested = raw };
            return new Resolution { Name = "custom", Requested = raw, Toolsets = sets.ToArray() };
        }

        /// <summary>Show more toolsets for this process. Returns the toolsets that were not shown yet.</summary>
        internal static string[] ShowForSession(IEnumerable<string> toolsets)
        {
            lock (Gate) return toolsets.Where(t => Shown.Add(t)).ToArray();
        }

        private static HashSet<string> ListedNames(Resolution r)
        {
            var names = new HashSet<string>(StringComparer.Ordinal) { "horizun_health" };
            foreach (string set in r.Toolsets) foreach (string tool in ToolPacks.MembersOf(set)) names.Add(tool);
            return names;
        }

        /// <summary>
        /// The tools/list entries this session shows: <paramref name="list"/> (already filtered by
        /// reachability) narrowed to the profile, plus the four server tools. Unrestricted, the
        /// list is returned untouched.
        /// </summary>
        public static JArray Apply(JArray list, bool advertiseTaskSupport, bool compact)
        {
            Resolution r = Current();
            if (!r.Restricting) return list;
            HashSet<string> keep = ListedNames(r);
            var arr = new JArray();
            foreach (JObject t in list.OfType<JObject>())
            {
                string name = (string)t["name"];
                if (keep.Contains(name)) arr.Add(t);
            }
            foreach (JObject def in MetaDefinitions(compact))
            {
                if (advertiseTaskSupport)
                    def["execution"] = new JObject
                    {
                        ["taskSupport"] = (string)def["name"] == SchemaFacade.CallToolName ? "optional" : "forbidden"
                    };
                arr.Add(def);
            }
            return arr;
        }

        /// <summary>The server tools a profile lists, with descriptions that fit compact (≤ 200).</summary>
        internal static IEnumerable<JObject> MetaDefinitions(bool compact)
        {
            JObject list = SchemaFacade.ListToolsDef();
            list["description"] =
                "Every tool this session can call, not only the listed ones. Pass query (what you want to do, Spanish or " +
                "English) for the best matches, then call one by name or with horizun_call_tool.";
            JObject describe = SchemaFacade.DescribeToolDef();
            describe["description"] =
                "The exact schema of one tool, including tools this profile does not list. Read it before calling an " +
                "unlisted tool with horizun_call_tool.";
            JObject call = SchemaFacade.CallToolDef();
            call["description"] =
                "Call any tool this session can call by name, listed or not. Same validation, permissions, dry run and " +
                "reply as calling the tool directly.";
            JObject activate = ToolsetActivation.Definition();
            activate["description"] = ToolsetActivation.CompactDescription;
            return new[] { list, describe, call, activate };
        }

        public static bool IsMetaName(string name) =>
            name == SchemaFacade.ListToolsName || name == SchemaFacade.DescribeToolName || name == SchemaFacade.CallToolName;

        public static JObject HealthBlock()
        {
            Resolution r = Current();
            List<ToolDef> callable = Tools.Advertisable();
            int listed = r.Restricting ? callable.Count(t => ListedNames(r).Contains(t.Name)) : callable.Count;
            var profiles = new JObject();
            foreach (string p in RoleProfiles.Concat(new[] { Full })) profiles[p] = RoleSentence[p];
            return new JObject
            {
                ["name"] = r.Name,
                ["source"] = r.Source,
                ["requested"] = r.Requested,
                ["toolsets"] = r.Restricting ? new JArray(r.Toolsets) : null,
                ["shown_this_session"] = new JArray(r.ShownThisSession),
                ["listed"] = listed,
                ["callable"] = callable.Count,
                ["problem"] = r.Problem,
                ["profiles"] = new JArray(RoleProfiles.Concat(new[] { Full })),
                ["profile_descriptions"] = profiles,
                ["note"] = r.Restricting
                    ? "tools/list shows " + listed + " of the " + callable.Count + " tools this session can call. Every " +
                      "other one is still callable: find it with horizun_list_tools(query), then call it by name or with " +
                      "horizun_call_tool, or show its toolset with horizun_activate_toolset. Change the profile with " +
                      SettingsKey + " in settings.json or " + EnvironmentVariable + " in the client."
                    : "Every callable tool is listed. A role profile lists fewer (" + string.Join(", ", RoleProfiles) +
                      "): set " + SettingsKey + " in settings.json or " + EnvironmentVariable + " in the client."
            };
        }
    }
}
