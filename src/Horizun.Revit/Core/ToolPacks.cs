// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// TOOL PACKS: which tools a session advertises, chosen by the user.
//
// Every tool in one tools/list is a real cost - every client pays for the
// schemas in context whether or not the session will ever pack a sheet - and a
// modeller, an auditor and a Power BI pipeline are three different sessions
// with three different needs. A pack is a named subset; the user selects packs
// and the union (plus core) is what tools/list shows and what a call may reach.
//
// The rules, and why each one is load-bearing:
//
//   * CORE IS NOT OPTIONAL. horizun_health answers "which Revit, which
//     document, what state"; horizun_target picks the Revit; horizun_job_status
//     reads work already queued; horizun_submit_job is how anything long runs.
//     A configuration that hid these would not be a smaller bridge, it would be
//     a broken one - so they are welded on, and a pack list that names garbage
//     still shows them.
//
//   * A PACK'S DEPENDENCIES COME WITH IT, VISIBLY. plan_annotations returns
//     horizun_annotate requests; fix_planimetry consumes audit_planimetry
//     findings. Selecting a pack whose tools hand you requests for hidden
//     tools would advertise workflows that dead-end, so dependencies resolve
//     transitively and the health report names which packs arrived by
//     dependency rather than by choice.
//
//   * HIDDEN MEANS UNREACHABLE. The same IsToolAllowed that hides a tool from
//     tools/list refuses its dispatch - including the async path and submit -
//     and execute_plan validates its children against it, because a plan step
//     reaching a hidden tool would make the pack a decoration.
//
//   * MALFORMED FALLS CLOSED, LOUDLY. A tool_packs value that is not a list of
//     known pack names reads as core-only, and the refusal for every hidden
//     tool says the configuration is malformed - the safe state that cannot be
//     mistaken for the intended one.
//
//   * SCHEMAS NEVER CHANGE. A pack decides WHETHER a tool appears, never what
//     it looks like; toggling packs cannot alter any input schema.
//
// The selection persists per user in the same settings.json as every other
// owner choice. Administrators override with HORIZUN_TOOL_PACKS (comma-
// separated; "all" restores everything); the environment wins over the file
// and the health report says which source decided.
//
// "Toolsets" is the same thing under the name MCP clients use: HORIZUN_TOOLSETS and
// a "toolsets" settings key are accepted synonyms, and the three convenience names
// families / data / admin expand to real packs. Which tool belongs to which pack is
// declared in the shared contract (ToolsetCatalog), not here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public static class ToolPacks
    {
        public const string SettingsKey = "tool_packs";
        public const string EnvironmentOverride = "HORIZUN_TOOL_PACKS";
        public const string AllToken = "all";

        // TOOLSETS ARE TOOL PACKS. The names below are accepted as synonyms of the two
        // selectors above, so a client configuration can say HORIZUN_TOOLSETS=mep,cad
        // and a settings file can say "toolsets": [...]. When both spellings are present
        // the pack spelling wins and the health report says which one decided - one
        // selection, one resolution, one refusal, one measurement.
        public const string ToolsetsSettingsKey = "toolsets";
        public const string ToolsetsEnvironmentVariable = "HORIZUN_TOOLSETS";

        // ---- the pack map, declared in the contract --------------------------------
        //
        // Membership used to be a literal dictionary in this file. It is now declared
        // once, beside each tool, in Horizun.Contracts.ToolsetCatalog, and derived here:
        // a tool added without a row fails a test instead of being reachable only when
        // nothing is restricted. The sixteen packs that lived here moved there with
        // their membership unchanged (the golden lists in ToolPackTests pin that); "cad"
        // is new.
        private static readonly Dictionary<string, string[]> Members = BuildMembers();

        private static Dictionary<string, string[]> BuildMembers()
        {
            var members = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (string pack in Horizun.Contracts.ToolsetCatalog.Known)
                members[pack] = Horizun.Contracts.ToolsetCatalog.MembersOf(pack);
            return members;
        }

        // ---- dependencies, explicit ------------------------------------------------
        //
        // "This pack's tools hand you requests for THAT pack's tools." Resolved
        // transitively; the resolution is reported, never silent.

        private static readonly Dictionary<string, string[]> Requires =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["model"] = new[] { "read" },
                ["architecture"] = new[] { "model" },
                ["structure"] = new[] { "model" },
                ["mep"] = new[] { "model" },
                ["documentation"] = new[] { "read" },
                ["planimetry"] = new[] { "documentation" },
                ["audit"] = new[] { "read" },
                ["coordination"] = new[] { "read" },
                ["schedules"] = new[] { "read" },
                ["family"] = new[] { "read" },
                ["interoperability"] = new[] { "read" },
                ["powerbi"] = new[] { "interoperability" },
                // The CAD pack plans, applies and reviews on its own; what it needs besides
                // is to read the model it is converting into.
                ["cad"] = new[] { "read" },
                ["administration"] = new string[0],
                ["unsafe_code"] = new string[0],
                ["read"] = new string[0],
                ["core"] = new string[0]
            };

        public static IReadOnlyCollection<string> KnownPacks => Members.Keys;

        public static IReadOnlyList<string> MembersOf(string pack)
            => Members.TryGetValue(pack ?? "", out string[] tools) ? tools : new string[0];

        public static IReadOnlyList<string> DependenciesOf(string pack)
            => Requires.TryGetValue(pack ?? "", out string[] deps) ? deps : new string[0];

        // ---- selection resolution ---------------------------------------------------

        public enum SelectionSource { Default, Settings, Environment, Malformed }

        /// <summary>The whole resolved state, computed once per question and cheap enough to be.</summary>
        public sealed class Resolution
        {
            public SelectionSource Source;
            /// <summary>Null means every pack (the default). Includes dependency-added packs.</summary>
            public List<string> ActivePacks;
            /// <summary>The packs the user actually named, before dependency resolution.</summary>
            public List<string> ChosenPacks;
            /// <summary>Packs that arrived through Requires rather than by choice.</summary>
            public List<string> AddedByDependency;
            /// <summary>Non-null when the configuration could not be read as a pack list.</summary>
            public string Problem;
            /// <summary>
            /// Which spelling decided: HORIZUN_TOOL_PACKS, HORIZUN_TOOLSETS, tool_packs or
            /// toolsets. Null for the default. Set by Settings.ActivePackResolution.
            /// </summary>
            public string SourceName;

            public bool Restricting => ActivePacks != null;

            public HashSet<string> Tools()
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (string tool in Members["core"]) result.Add(tool);
                if (ActivePacks == null)
                {
                    foreach (string[] tools in Members.Values)
                        foreach (string tool in tools) result.Add(tool);
                    return result;
                }
                foreach (string pack in ActivePacks)
                    foreach (string tool in MembersOf(pack)) result.Add(tool);
                return result;
            }
        }

        /// <summary>
        /// Resolve the active selection: environment first (the administrator's word),
        /// then the settings file (the user's), then the default (everything). The two
        /// raw values arrive as arguments so this stays provable without a filesystem.
        /// </summary>
        public static Resolution Resolve(string environmentValue, IEnumerable<string> settingsValue,
                                         bool settingsValueMalformed)
        {
            if (!string.IsNullOrWhiteSpace(environmentValue))
            {
                if (string.Equals(environmentValue.Trim(), AllToken, StringComparison.OrdinalIgnoreCase))
                    return new Resolution { Source = SelectionSource.Environment, ActivePacks = null };
                List<string> requested = environmentValue.Split(',')
                    .Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).ToList();
                return Build(SelectionSource.Environment, requested);
            }
            if (settingsValueMalformed)
                return new Resolution
                {
                    Source = SelectionSource.Malformed,
                    ActivePacks = new List<string>(),
                    ChosenPacks = new List<string>(),
                    AddedByDependency = new List<string>(),
                    Problem = "the " + SettingsKey + " setting is not an array of pack-name strings. Until it " +
                              "is fixed, only the core tools are offered - a malformed restriction must not " +
                              "read as no restriction."
                };
            if (settingsValue == null) return new Resolution { Source = SelectionSource.Default, ActivePacks = null };
            List<string> fromSettings = settingsValue
                .Select(p => (p ?? "").Trim().ToLowerInvariant()).Where(p => p.Length > 0).ToList();
            if (fromSettings.Count == 1 && fromSettings[0] == AllToken)
                return new Resolution { Source = SelectionSource.Settings, ActivePacks = null };
            return Build(SelectionSource.Settings, fromSettings);
        }

        private static Resolution Build(SelectionSource source, List<string> requested)
        {
            // Convenience names (families, data, admin) expand to the packs they stand
            // for BEFORE validation, so an alias can never name something a pack does not.
            requested = requested
                .SelectMany(p => Horizun.Contracts.ToolsetCatalog.Aliases.TryGetValue(p, out string[] targets)
                    ? (IEnumerable<string>)targets : new[] { p })
                .ToList();
            var unknown = requested.Where(p => !Members.ContainsKey(p)).Distinct().ToList();
            if (unknown.Count > 0)
                return new Resolution
                {
                    Source = SelectionSource.Malformed,
                    ActivePacks = new List<string>(),
                    ChosenPacks = new List<string>(),
                    AddedByDependency = new List<string>(),
                    Problem = "unknown pack name(s): " + string.Join(", ", unknown) + ". Known packs: " +
                              string.Join(", ", Members.Keys.OrderBy(k => k, StringComparer.Ordinal)) +
                              " (aliases: " + string.Join(", ", Horizun.Contracts.ToolsetCatalog.Aliases.Keys
                                  .OrderBy(k => k, StringComparer.Ordinal)) + ")" +
                              ". Until this is fixed, only the core tools are offered."
                };

            var chosen = requested.Distinct().ToList();
            var active = new List<string>();
            var queue = new Queue<string>(chosen);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (queue.Count > 0)
            {
                string pack = queue.Dequeue();
                if (!seen.Add(pack)) continue;
                active.Add(pack);
                foreach (string dependency in DependenciesOf(pack)) queue.Enqueue(dependency);
            }
            return new Resolution
            {
                Source = source,
                ChosenPacks = chosen,
                ActivePacks = active.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                AddedByDependency = active.Where(p => !chosen.Contains(p))
                                          .OrderBy(p => p, StringComparer.Ordinal).ToList()
            };
        }

        /// <summary>The refusal a hidden tool answers with: which packs would surface it.</summary>
        public static string HiddenReason(string toolName, Resolution resolution)
        {
            List<string> providers = Members
                .Where(kv => kv.Value.Contains(toolName))
                .Select(kv => kv.Key)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            string providerText = providers.Count == 0
                ? "no pack provides it, which is a bridge defect worth reporting"
                : "it belongs to pack(s): " + string.Join(", ", providers);
            string prefix = resolution.Problem != null
                ? "the tool-pack configuration is malformed (" + resolution.Problem + ") and "
                : "";
            return toolName + " is hidden by the active tool packs (" + prefix +
                   "active: " + (resolution.ActivePacks == null || resolution.ActivePacks.Count == 0
                       ? "core only" : string.Join(", ", resolution.ActivePacks)) +
                   ", source: " + resolution.Source.ToString().ToLowerInvariant() +
                   (resolution.SourceName == null ? "" : " (" + resolution.SourceName + ")") + "); " + providerText +
                   ". Add the pack to " + SettingsKey + " in the settings file (or set " + EnvironmentOverride +
                   " / " + ToolsetsEnvironmentVariable + " in the MCP client configuration) and compatible " +
                   "clients refresh via tools/list_changed; others need one restart. Nothing was run.";
        }

        /// <summary>
        /// Every tool named by every pack must exist in the shared contract, and every
        /// dependency must name a real pack. Called by tests, not at runtime - a pack
        /// that names a renamed tool must fail a build, not surface at a customer.
        /// </summary>
        public static List<string> Audit(Func<string, bool> toolExists)
        {
            var problems = new List<string>();
            foreach (KeyValuePair<string, string[]> pack in Members)
            {
                foreach (string tool in pack.Value)
                    if (!toolExists(tool))
                        problems.Add("pack '" + pack.Key + "' names unknown tool '" + tool + "'");
                if (!Requires.ContainsKey(pack.Key))
                    problems.Add("pack '" + pack.Key + "' has no dependency declaration (empty is fine, absent is not)");
            }
            foreach (KeyValuePair<string, string[]> dependency in Requires)
            {
                if (!Members.ContainsKey(dependency.Key))
                    problems.Add("dependency table names unknown pack '" + dependency.Key + "'");
                foreach (string target in dependency.Value)
                    if (!Members.ContainsKey(target))
                        problems.Add("pack '" + dependency.Key + "' depends on unknown pack '" + target + "'");
            }
            return problems;
        }
    }
}
