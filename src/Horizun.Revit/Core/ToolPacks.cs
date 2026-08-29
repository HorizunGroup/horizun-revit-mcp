// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// TOOL PACKS: which tools a session advertises, chosen by the user.
//
// Sixty-two tools in one tools/list is a real cost - every client pays for the
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

        // ---- the pack map, closed --------------------------------------------------
        //
        // A tool may live in several packs: capture_view is how documentation gets
        // reviewed AND how an audit collects evidence. Membership is curated by what
        // a session doing that KIND of work actually calls, not by implementation
        // relatives.

        private static readonly Dictionary<string, string[]> Members =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["core"] = new[]
                {
                    "horizun_health", "horizun_target", "horizun_job_status", "horizun_submit_job"
                },
                ["read"] = new[]
                {
                    "get_document_info", "horizun_navigate", "horizun_list_elements", "horizun_query_model",
                    "horizun_model_scan", "horizun_file_info", "horizun_list_schedules",
                    "horizun_get_schedule_data", "horizun_query_dimensions", "horizun_get_dimension_references",
                    "horizun_query_detail_2d", "horizun_query_planimetry", "horizun_quantities",
                    "horizun_capture_view", "horizun_query_cad", "horizun_plan_from_cad",
                    "horizun_audit_cad_model", "horizun_plan_cad_update",
                    "horizun_query_structure", "horizun_plan_reinforcement",
                    "horizun_audit_reinforcement"
                },
                ["model"] = new[]
                {
                    "horizun_create_elements", "horizun_transform_elements", "horizun_write_params_verified",
                    "horizun_delete_verified", "horizun_set_keynote", "horizun_bind_shared_param",
                    "horizun_ungroup_and_mark", "horizun_regroup_by_param", "horizun_execute_plan",
                    "horizun_apply_cad_plan", "horizun_apply_cad_update", "horizun_manage_cad_links"
                },
                ["architecture"] = new[]
                {
                    "horizun_split_floor_loops", "horizun_split_multilayer_walls",
                    "horizun_rectangularize_walls", "horizun_embed_floors_in_toposolid",
                    "horizun_grade_toposolid_around_floors"
                },
                ["structure"] = new[]
                {
                    "horizun_split_multilayer_slabs", "horizun_copy_slab_elevations",
                    "horizun_plan_structure", "horizun_create_elements",
                    "horizun_query_structure", "horizun_plan_reinforcement",
                    "horizun_apply_reinforcement", "horizun_audit_reinforcement"
                },
                ["mep"] = new[]
                {
                    "horizun_manage_system_types", "horizun_plan_mep", "horizun_create_elements",
                    "horizun_query_model"
                },
                ["documentation"] = new[]
                {
                    "horizun_manage_views", "horizun_plan_views", "horizun_annotate",
                    "horizun_edit_dimensions", "horizun_get_dimension_references", "horizun_query_dimensions",
                    "horizun_plan_annotations", "horizun_detail_2d", "horizun_query_detail_2d",
                    "horizun_manage_revisions", "horizun_pack_sheets", "horizun_capture_view",
                    "horizun_delete_verified"
                },
                ["planimetry"] = new[]
                {
                    "horizun_query_planimetry", "horizun_audit_planimetry", "horizun_fix_planimetry",
                    "horizun_pack_sheets", "horizun_plan_annotations", "horizun_manage_revisions",
                    "horizun_capture_view"
                },
                ["audit"] = new[]
                {
                    "horizun_audit_model", "horizun_model_scan", "horizun_clash", "horizun_coordination",
                    "horizun_query_planimetry", "horizun_audit_planimetry", "horizun_capture_view", "horizun_audit_reinforcement"
                },
                ["coordination"] = new[]
                {
                    "horizun_clash", "horizun_coordination", "horizun_acc_upload_status", "horizun_file_info",
                    "horizun_quantities", "horizun_manage_links"
                },
                ["schedules"] = new[]
                {
                    "horizun_create_schedule", "horizun_manage_schedules", "horizun_list_schedules",
                    "horizun_get_schedule_data"
                },
                ["family"] = new[]
                {
                    "horizun_create_family", "horizun_family_apply", "horizun_catalog_lookup",
                    "horizun_set_keynote", "horizun_bind_shared_param"
                },
                ["interoperability"] = new[]
                {
                    "horizun_export", "horizun_excel_write_rows", "horizun_excel_read_rows", "horizun_catalog_lookup",
                    "horizun_manage_links"
                },
                ["powerbi"] = new[]
                {
                    "horizun_power_bi_push", "horizun_excel_write_rows", "horizun_excel_read_rows"
                },
                ["administration"] = new[]
                {
                    "horizun_document_session", "horizun_open_document", "horizun_save_document",
                    "horizun_relinquish_all"
                },
                ["unsafe_code"] = new[]
                {
                    "horizun_execute_python", "horizun_request_python_access"
                }
            };

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
                   ", source: " + resolution.Source.ToString().ToLowerInvariant() + "); " + providerText +
                   ". Add the pack to " + SettingsKey + " in the settings file (or set " + EnvironmentOverride +
                   ") and compatible clients refresh via tools/list_changed; others need one restart.";
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
