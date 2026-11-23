// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// TOOL GROUPS AND PROFILES (4.0.0): which reachable tools THIS SESSION LISTS.
//
// Two questions used to be one. "May this call run?" and "should this tool be in
// the list?" were both answered by the 3.x tool packs, so a smaller list meant a
// smaller bridge. 4.0 separates them:
//
//   * REACHABLE (the ceiling) - Settings.IsToolAllowed, unchanged: denied/allowed
//     tools, the permission profile, the Python grant, and the 3.x tool_packs /
//     toolsets selection (settings or HORIZUN_TOOL_PACKS / HORIZUN_TOOLSETS). A tool
//     outside the ceiling is refused on every path, exactly as in 3.x.
//
//   * LISTED (visibility) - this file. A profile names groups (ToolGroupCatalog);
//     core is always on. tools/list prints the reachable tools of the active groups
//     plus three server tools: horizun_toolsets (switch groups for this session),
//     horizun_describe_tool and horizun_call_tool. A reachable tool in an inactive
//     group is NOT refused: it can be called directly or through horizun_call_tool,
//     under the very same gates. Activation is visibility, never privilege.
//
// WHERE THE PROFILE COMES FROM, first match wins:
//   1. --profile <name> on the server command line (the client's own entry);
//   2. HORIZUN_PROFILE in the client's environment ("local" keeps selecting the
//      local schema surface, as in 3.7);
//   3. "tool_profile" in settings.json - what the installer writes;
//   4. nothing: "completo", every group, the 3.x list byte for byte. An upgrade with
//      no tool_profile therefore keeps its behaviour.
// A value is a profile name or a comma-separated list of groups. An unknown value
// lists everything (visibility must never lose a capability) and health says why.
//
// SESSION ACTIVATIONS live in this process's memory only. They never write
// settings.json, never change another client's list, and end with the process.
// Each change that alters the listed set fires Changed, which the ToolListMonitor
// turns into ONE notifications/tools/list_changed.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;

namespace Horizun.Server
{
    internal static class ToolVisibility
    {
        public const string ProfileEnvironmentVariable = "HORIZUN_PROFILE";
        public const string ProfileSettingsKey = "tool_profile";
        public const string ProfileArgument = "--profile";
        public const string ToolsetsToolName = "horizun_toolsets";

        /// <summary>Set once by Program from <c>--profile</c>; tests set it directly.</summary>
        internal static string CommandLineProfile;

        /// <summary>The client's self-declared name, for health. Program installs it.</summary>
        internal static Func<string> ClientName = () => null;

        /// <summary>Raised after a session change that may alter the listed set.</summary>
        internal static event Action Changed;

        private static readonly object Gate = new object();
        private static string _sessionProfile;                       // null: the initial profile
        private static readonly HashSet<string> Activated = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Deactivated = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<JObject> History = new List<JObject>();
        private static int _notificationsSent;

        // ---- the initial profile -----------------------------------------------------------

        internal sealed class Initial
        {
            public string Profile;          // profile id, or "custom" for a list of groups
            public string[] Groups;         // optional groups it switches on
            public string Source;           // command_line | environment | settings | default
            public string SourceName;       // --profile | HORIZUN_PROFILE | tool_profile | null
            public string Problem;
        }

        internal static Initial ResolveInitial()
        {
            string cli = CommandLineProfile;
            if (!string.IsNullOrWhiteSpace(cli)) return Parse(cli, "command_line", ProfileArgument);

            string env = null;
            try { env = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable); } catch { }
            // HORIZUN_PROFILE=local is the 3.7 local SCHEMA SURFACE (SchemaMode), not a group profile.
            if (!string.IsNullOrWhiteSpace(env) && !string.Equals(env.Trim(), "local", StringComparison.OrdinalIgnoreCase))
                return Parse(env, "environment", ProfileEnvironmentVariable);

            string fromSettings = Settings.RawValue(ProfileSettingsKey);
            if (!string.IsNullOrWhiteSpace(fromSettings)) return Parse(fromSettings, "settings", ProfileSettingsKey);

            return new Initial
            {
                Profile = ToolGroupCatalog.FullProfile,
                Groups = ToolGroupCatalog.GroupsOfProfile(ToolGroupCatalog.FullProfile),
                Source = "default"
            };
        }

        /// <summary>A profile name, or a comma-separated list of group names (aliases accepted).</summary>
        internal static Initial Parse(string raw, string source, string sourceName)
        {
            string value = (raw ?? "").Trim();
            string profile = ToolGroupCatalog.CanonicalProfile(value);
            if (profile != null)
                return new Initial
                {
                    Profile = profile, Groups = ToolGroupCatalog.GroupsOfProfile(profile),
                    Source = source, SourceName = sourceName
                };

            var groups = new List<string>();
            var unknown = new List<string>();
            foreach (string part in value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                string[] g = ToolGroupCatalog.ResolveGroupName(part);
                if (g == null) unknown.Add(part);
                else groups.AddRange(g.Where(x => x != ToolGroupCatalog.Core));
            }
            if (unknown.Count == 0 && value.Length > 0)
                return new Initial
                {
                    Profile = "custom", Groups = groups.Distinct().ToArray(),
                    Source = source, SourceName = sourceName
                };

            return new Initial
            {
                Profile = ToolGroupCatalog.FullProfile,
                Groups = ToolGroupCatalog.GroupsOfProfile(ToolGroupCatalog.FullProfile),
                Source = source,
                SourceName = sourceName,
                Problem = sourceName + "='" + Clip(value) + "' names no profile or group (" +
                          string.Join(", ", unknown.Select(Clip)) + "). Listing every group so nothing is lost. " +
                          "Profiles: " + string.Join(", ", ToolGroupCatalog.Profiles) + ". Groups: " +
                          string.Join(", ", ToolGroupCatalog.OptionalGroups) + "."
            };
        }

        private static string Clip(string s) => s == null ? "" : s.Length > 40 ? s.Substring(0, 40) + "..." : s;

        // ---- the active set ----------------------------------------------------------------

        /// <summary>The profile in effect: the session's choice, else the initial one.</summary>
        internal static string ActiveProfile()
        {
            lock (Gate)
            {
                if (_sessionProfile != null) return _sessionProfile;
            }
            return ResolveInitial().Profile;
        }

        /// <summary>Core plus every optional group the profile and the session switched on, in display order.</summary>
        internal static List<string> ActiveGroups() => ActiveGroups(ResolveInitial());

        private static List<string> ActiveGroups(Initial initial)
        {
            string[] baseGroups;
            HashSet<string> on, off;
            lock (Gate)
            {
                baseGroups = _sessionProfile != null ? ToolGroupCatalog.GroupsOfProfile(_sessionProfile) : initial.Groups;
                on = new HashSet<string>(Activated, StringComparer.Ordinal);
                off = new HashSet<string>(Deactivated, StringComparer.Ordinal);
            }
            var set = new HashSet<string>(baseGroups ?? new string[0], StringComparer.Ordinal);
            set.UnionWith(on);
            set.ExceptWith(off);
            set.Add(ToolGroupCatalog.Core);
            return ToolGroupCatalog.Groups.Where(set.Contains).ToList();
        }

        /// <summary>Every optional group is on: tools/list is the 3.x list, unchanged.</summary>
        internal static bool AllVisible() => AllVisible(ActiveGroups());

        private static bool AllVisible(List<string> active) =>
            ToolGroupCatalog.OptionalGroups.All(active.Contains);

        /// <summary>Whether tools/list prints this tool (reachability is a separate question).</summary>
        internal static bool IsListed(string tool, List<string> active)
        {
            string g = ToolGroupCatalog.GroupOf(tool);
            return g == null || active.Contains(g);
        }

        // ---- the grouped tools/list -----------------------------------------------------------

        /// <summary>
        /// tools/list when not every group is on: the reachable tools of the active groups in
        /// contract order, then horizun_toolsets, horizun_describe_tool and horizun_call_tool.
        /// </summary>
        internal static JArray GroupedList(bool advertiseTaskSupport) => ListFor(ActiveGroups(), advertiseTaskSupport);

        /// <summary>
        /// The tools/list a session with exactly this PROFILE would see at the current permission
        /// posture, independent of this session's own state. Measurement only (ledger, budgets,
        /// horizun_toolsets action=list); completo is the 3.x list, unchanged.
        /// </summary>
        internal static JArray ListForProfile(string profile, bool advertiseTaskSupport)
        {
            string[] groups = ToolGroupCatalog.GroupsOfProfile(profile)
                ?? throw new ArgumentException("unknown profile '" + profile + "'");
            var active = new List<string>(groups) { ToolGroupCatalog.Core };
            return AllVisible(active) ? Tools.List(advertiseTaskSupport) : ListFor(active, advertiseTaskSupport);
        }

        private static JArray ListFor(List<string> active, bool advertiseTaskSupport)
        {
            var arr = new JArray();
            foreach (JObject t in Tools.List(advertiseTaskSupport).OfType<JObject>())
                if (IsListed((string)t["name"], active)) arr.Add(t);
            foreach (JObject def in new[] { ToolsetsDef(), SchemaFacade.DescribeToolDef(), SchemaFacade.CallToolDef() })
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

        internal static JObject ToolsetsDef() => new JObject
        {
            ["name"] = ToolsetsToolName,
            ["title"] = "Toolsets",
            ["description"] =
                "Switch tool groups for THIS session. action=list names every group and profile with its size; " +
                "activate/deactivate (groups=[...]) or profile (profile=<name>) change what tools/list shows, at once, " +
                "and the reply carries a one-line catalog of what became visible. Visibility only: permissions never " +
                "change, nothing is written to settings, and a tool of an inactive group is still callable through " +
                SchemaFacade.CallToolName + " (exact schema: " + SchemaFacade.DescribeToolName + ").",
            ["inputSchema"] = JObject.Parse(@"{""type"":""object"",""properties"":{
""action"":{""type"":""string"",""enum"":[""list"",""activate"",""deactivate"",""profile"",""reset""],""description"":""list (default), activate, deactivate, profile or reset (back to the configured profile).""},
""groups"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Groups for activate/deactivate, e.g. documentacion, datos (3.x toolset names accepted).""},
""profile"":{""type"":""string"",""description"":""Profile for action=profile: nucleo, auditor, modelador_arquitectura, modelador_estructura, modelador_mep, modelador, documentador, coordinador, analista_datos, completo.""}},
""additionalProperties"":false}"),
            ["outputSchema"] = new JObject { ["type"] = "object", ["additionalProperties"] = true },
            ["annotations"] = new JObject
            {
                ["title"] = "Toolsets",
                ["readOnlyHint"] = true,          // it changes what is LISTED, never the model or a setting
                ["destructiveHint"] = false,
                ["idempotentHint"] = true,
                ["openWorldHint"] = false
            }
        };

        // ---- horizun_toolsets -----------------------------------------------------------------

        internal static JObject Answer(JObject args)
        {
            args = args ?? new JObject();
            string extra = args.Properties().Select(p => p.Name)
                               .FirstOrDefault(k => k != "action" && k != "groups" && k != "profile");
            if (extra != null)
                throw new ToolRefusal(ToolsetsToolName + " takes 'action', 'groups' and 'profile'; '" + extra +
                                      "' is not one of them. Nothing was changed.");
            JToken a = args["action"];
            if (a != null && a.Type != JTokenType.String) throw new ToolRefusal("'action' must be a string.");
            string action = ((string)a ?? "list").Trim().ToLowerInvariant();

            switch (action)
            {
                case "list": return Report(null, null);
                case "activate":
                case "deactivate":
                {
                    List<string> groups = GroupsArgument(args, action);
                    string before = ListedNames();
                    lock (Gate)
                    {
                        foreach (string g in groups)
                        {
                            if (action == "activate") { Activated.Add(g); Deactivated.Remove(g); }
                            else { Deactivated.Add(g); Activated.Remove(g); }
                        }
                        Record(action, groups, null);
                    }
                    return AfterChange(before, action, groups);
                }
                case "profile":
                {
                    JToken p = args["profile"];
                    if (p == null || p.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)p))
                        throw new ToolRefusal("action=profile needs 'profile': one of " +
                                              string.Join(", ", ToolGroupCatalog.Profiles) + ". Nothing was changed.");
                    string id = ToolGroupCatalog.CanonicalProfile((string)p);
                    if (id == null)
                        throw new ToolRefusal("Unknown profile '" + Clip((string)p) + "'. Profiles: " +
                                              string.Join(", ", ToolGroupCatalog.Profiles) + ". Nothing was changed.");
                    string before = ListedNames();
                    lock (Gate)
                    {
                        _sessionProfile = id;
                        Activated.Clear();
                        Deactivated.Clear();
                        Record("profile", null, id);
                    }
                    return AfterChange(before, "profile", ToolGroupCatalog.GroupsOfProfile(id).ToList());
                }
                case "reset":
                {
                    string before = ListedNames();
                    ResetSession(record: true);
                    return AfterChange(before, "reset", null);
                }
                default:
                    throw new ToolRefusal("Unknown action '" + Clip(action) + "'. Use list, activate, deactivate, " +
                                          "profile or reset. Nothing was changed.");
            }
        }

        private static List<string> GroupsArgument(JObject args, string action)
        {
            JToken g = args["groups"];
            var names = new List<string>();
            if (g is JArray arr)
            {
                foreach (JToken t in arr)
                {
                    if (t.Type != JTokenType.String) throw new ToolRefusal("'groups' must be an array of strings.");
                    names.Add((string)t);
                }
            }
            else if (g != null && g.Type == JTokenType.String) names.AddRange(((string)g).Split(','));
            else if (g != null) throw new ToolRefusal("'groups' must be an array of strings.");
            names = names.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
            if (names.Count == 0)
                throw new ToolRefusal("action=" + action + " needs 'groups', e.g. [\"documentacion\"]. Groups: " +
                                      string.Join(", ", ToolGroupCatalog.OptionalGroups) + ". Nothing was changed.");

            var groups = new List<string>();
            var unknown = new List<string>();
            foreach (string n in names)
            {
                string[] r = ToolGroupCatalog.ResolveGroupName(n);
                if (r == null)
                {
                    // A profile name where a group was expected: say what to do instead of guessing.
                    if (ToolGroupCatalog.CanonicalProfile(n) != null)
                        throw new ToolRefusal("'" + Clip(n) + "' is a profile, not a group: use action=profile, " +
                                              "profile='" + ToolGroupCatalog.CanonicalProfile(n) + "'. Nothing was changed.");
                    unknown.Add(n);
                }
                else groups.AddRange(r);
            }
            if (unknown.Count > 0)
                throw new ToolRefusal("Unknown group(s): " + string.Join(", ", unknown.Select(Clip)) + ". Groups: " +
                                      string.Join(", ", ToolGroupCatalog.OptionalGroups) + " (3.x toolset names are " +
                                      "accepted too). Nothing was changed.");
            if (action == "deactivate" && groups.Contains(ToolGroupCatalog.Core))
                throw new ToolRefusal("The core group (" + ToolGroupCatalog.Core + ") is always on and cannot be " +
                                      "deactivated. Nothing was changed.");
            return groups.Where(x => x != ToolGroupCatalog.Core).Distinct().ToList();
        }

        private static void Record(string action, List<string> groups, string profile)
        {
            var row = new JObject
            {
                ["action"] = action,
                ["at_utc"] = DateTimeOffset.UtcNow.ToString("O")
            };
            if (groups != null) row["groups"] = new JArray(groups);
            if (profile != null) row["profile"] = profile;
            History.Add(row);
            if (History.Count > 50) History.RemoveAt(0);
        }

        /// <summary>Back to the configured profile. Tests call it between cases.</summary>
        internal static void ResetSession(bool record = false)
        {
            lock (Gate)
            {
                _sessionProfile = null;
                Activated.Clear();
                Deactivated.Clear();
                if (record) Record("reset", null, null);
                else History.Clear();
            }
        }

        private static string ListedNames()
        {
            try
            {
                return string.Join("\n", SchemaFacade.List(SchemaMode.Current, false).OfType<JObject>()
                    .Select(t => (string)t["name"]));
            }
            catch { return null; }
        }

        private static JObject AfterChange(string before, string action, List<string> groups)
        {
            string after = ListedNames();
            bool changed = before == null || after == null || !string.Equals(before, after, StringComparison.Ordinal);
            long sentBefore = System.Threading.Volatile.Read(ref _notificationsSent);
            if (changed)
            {
                try { Changed?.Invoke(); }
                catch (Exception ex) { Log.Warn("tool-list change notification failed: " + ex.Message); }
            }
            JObject reply = Report(action, groups);
            bool sent = System.Threading.Volatile.Read(ref _notificationsSent) > sentBefore;
            reply["tools_list_changed"] = changed;
            reply["list_changed_notification_sent"] = changed && sent;
            reply["next"] = changed
                ? "tools/list changed; " + (sent
                    ? "notifications/tools/list_changed was sent, and a client that implements it refreshes the list now. "
                    : "this client neither subscribed to notifications/tools/list_changed nor handshook as a legacy client, " +
                      "so no notification was sent and its list is not refreshed. ") +
                  "If the new tools do not show up, call them through " +
                  SchemaFacade.CallToolName + " {name, arguments}; " + SchemaFacade.DescribeToolName +
                  " gives a tool's exact schema."
                : "Nothing new to list. Every reachable tool stays callable through " + SchemaFacade.CallToolName + ".";
            return reply;
        }

        internal static void CountNotification() => System.Threading.Interlocked.Increment(ref _notificationsSent);

        // ---- reports ------------------------------------------------------------------------------

        /// <summary>The body of action=list and of every change: groups, profiles, sizes.</summary>
        internal static JObject Report(string action, List<string> touched)
        {
            Initial initial = ResolveInitial();
            List<string> active = ActiveGroups(initial);
            List<ToolDef> reachable = Tools.Advertisable();
            var reachableNames = new HashSet<string>(reachable.Select(t => t.Name), StringComparer.Ordinal);
            var published = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (ToolDef t in reachable) published[t.Name] = Tools.Publish(t, false);

            var groups = new JArray();
            foreach (string g in ToolGroupCatalog.Groups)
            {
                string[] members = ToolGroupCatalog.MembersOf(g);
                var listed = new JArray(members.Where(published.ContainsKey).Select(m => published[m]));
                if (g == ToolGroupCatalog.Core)
                    foreach (JObject def in new[] { ToolsetsDef(), SchemaFacade.DescribeToolDef(), SchemaFacade.CallToolDef() })
                        listed.Add(def);
                var row = new JObject
                {
                    ["group"] = g,
                    ["active"] = active.Contains(g),
                    ["description"] = ToolGroupCatalog.DescriptionOf(g),
                    ["tools"] = members.Length + (g == ToolGroupCatalog.Core ? ToolGroupCatalog.ServerCoreTools.Length : 0),
                    ["reachable"] = listed.Count,
                    ["approx_tokens"] = ToolsetReport.Size(listed)["approx_tokens"]
                };
                // The one-line catalog of what an activation made visible, so the model knows what it has
                // without loading the schemas.
                if (touched != null && touched.Contains(g))
                {
                    var catalog = new JObject();
                    foreach (string m in members)
                        if (reachableNames.Contains(m))
                            catalog[m] = SchemaFacade.Summary(Tools.Find(m)?.Description);
                    row["catalog"] = catalog;
                    var outside = new JObject();
                    foreach (string m in members)
                        if (!reachableNames.Contains(m))
                            outside[m] = Tools.DisabledReason(m) ?? Tools.WithheldNow(Tools.Find(m)) ?? "not reachable";
                    if (outside.Count > 0) row["not_reachable"] = outside;
                }
                groups.Add(row);
            }

            var profiles = new JArray();
            foreach (string p in ToolGroupCatalog.Profiles)
            {
                var pg = new HashSet<string>(ToolGroupCatalog.GroupsOfProfile(p)) { ToolGroupCatalog.Core };
                var list = new JArray();
                foreach (ToolDef t in reachable)
                    if (pg.Contains(ToolGroupCatalog.GroupOf(t.Name) ?? ToolGroupCatalog.Core)) list.Add(published[t.Name]);
                if (p != ToolGroupCatalog.FullProfile)
                    foreach (JObject def in new[] { ToolsetsDef(), SchemaFacade.DescribeToolDef(), SchemaFacade.CallToolDef() })
                        list.Add(def);
                JObject size = ToolsetReport.Size(list);
                profiles.Add(new JObject
                {
                    ["profile"] = p,
                    ["description"] = ToolGroupCatalog.ProfileDescription(p),
                    ["groups"] = new JArray(ToolGroupCatalog.GroupsOfProfile(p)),
                    ["tools_listed"] = size["tools"],
                    ["approx_tokens"] = size["approx_tokens"]
                });
            }

            JObject o = State(initial, active);
            if (action != null) o["action"] = action;
            o["groups"] = groups;
            o["profiles"] = profiles;
            o["means"] = "Groups decide what tools/list SHOWS for this session; they never grant a permission. A " +
                         "reachable tool of an inactive group is still callable through " + SchemaFacade.CallToolName +
                         ". Session changes live in memory only and never touch settings.json.";
            return o;
        }

        /// <summary>The state block shared by horizun_toolsets and horizun_health.</summary>
        private static JObject State(Initial initial, List<string> active)
        {
            string sessionProfile;
            JArray history;
            lock (Gate)
            {
                sessionProfile = _sessionProfile;
                history = new JArray(History.Select(h => h.DeepClone()));
            }
            ToolPacks.Resolution ceiling = Settings.ActivePackResolution();
            return new JObject
            {
                ["profile"] = sessionProfile ?? initial.Profile,
                ["profile_source"] = sessionProfile != null ? "session" : initial.Source,
                ["configured_profile"] = initial.Profile,
                ["configured_source"] = initial.Source,
                ["configured_source_name"] = initial.SourceName == null ? (JToken)JValue.CreateNull() : initial.SourceName,
                ["profile_problem"] = initial.Problem == null ? (JToken)JValue.CreateNull() : initial.Problem,
                ["active_groups"] = new JArray(active),
                ["inactive_groups"] = new JArray(ToolGroupCatalog.Groups.Where(g => !active.Contains(g))),
                ["session_activations"] = history,
                ["ceiling"] = new JObject
                {
                    ["means"] = "What may run at all: the permission profile, denied/allowed tools and the 3.x " +
                                "tool_packs/toolsets selection. Groups only choose what is listed inside it.",
                    ["permission_profile"] = Settings.PermissionProfile,
                    ["toolsets_restricting"] = ceiling.Restricting,
                    ["toolsets_source"] = ceiling.SourceName == null ? ceiling.Source.ToString().ToLowerInvariant() : ceiling.SourceName,
                    ["toolsets_active"] = ceiling.ActivePacks == null ? (JToken)"all" : new JArray(ceiling.ActivePacks),
                    ["toolsets_problem"] = ceiling.Problem == null ? (JToken)JValue.CreateNull() : ceiling.Problem
                }
            };
        }

        /// <summary>horizun_health.tool_groups.</summary>
        internal static JObject HealthBlock()
        {
            try
            {
                Initial initial = ResolveInitial();
                List<string> active = ActiveGroups(initial);
                JObject o = State(initial, active);
                SchemaSurface mode = SchemaMode.Current;
                JArray listed = SchemaFacade.List(mode, false);
                o["schema_mode"] = SchemaMode.Name(mode);
                o["tools_list"] = ToolsetReport.Size(listed);
                o["tools_list"]["estimated_tokens_range"] = TokenRange(Encoding.UTF8.GetByteCount(
                    new JObject { ["tools"] = listed }.ToString(Formatting.None)));
                o["reachable_tools"] = Tools.Advertisable().Count;
                o["compact_schemas"] = CompactAnnouncement.Enabled;
                o["client"] = ClientName() ?? (JToken)JValue.CreateNull();
                o["list_changed_notifications_sent"] = System.Threading.Volatile.Read(ref _notificationsSent);
                o["how"] = ToolsetsToolName + " action=list shows every group and profile; activate groups=[...] " +
                           "lists them for this session. A tool you cannot see is still callable through " +
                           SchemaFacade.CallToolName + " when it is reachable.";
                return o;
            }
            catch (Exception ex)
            {
                return new JObject { ["error"] = ex.Message };
            }
        }

        /// <summary>
        /// The token range the 4.0 proposal measured with: bytes / 4.4 (tiktoken cl100k/o200k on
        /// this JSON) as the low bound, bytes / 3.5 as the high one. An estimate, never a count.
        /// </summary>
        internal static JObject TokenRange(long utf8Bytes) => new JObject
        {
            ["low"] = (long)Math.Round(utf8Bytes / 4.4),
            ["high"] = (long)Math.Round(utf8Bytes / 3.5),
            ["method"] = "utf8_bytes/4.4 .. utf8_bytes/3.5 (proposal 4.0 calibration); an estimate"
        };
    }
}
