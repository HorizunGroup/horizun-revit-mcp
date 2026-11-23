// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// ONE declaration of what this bridge offers, shared by both halves.
//
// The server carried the tool table; each command carried its own Name,
// Description and ParametersSchema. The same facts, written twice, by hand. They
// drifted twice in a single afternoon of work here: a parameter added to the
// server schema and not to the command's, and a description updated on one side
// while the other kept promising the old behaviour. Nothing detected either -
// the two copies never meet.
//
// So the contract lives here, once, and is LINKED into the server and the add-in.
// Neither can restate it, because neither owns it.
//
// It also carries a hash of itself. The add-in publishes that hash in its
// discovery file and the server compares it before forwarding anything, so two
// builds that disagree about what a command takes are a refusal with a sentence
// rather than an argument silently ignored at the far end.
//
// No Autodesk types and no server types: it compiles into both.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Contracts
{
    public enum ToolEffect
    {
        ReadOnly,
        Mutating,
        MutatingUnlessDryRun,
        DocumentSession,

        /// <summary>
        /// Writes an ARTEFACT OUTSIDE THE MODEL: a workbook, a PNG. This is what the
        /// permission ladder means by "external", and full_write is the rung that
        /// authorizes it.
        /// </summary>
        ExternalSideEffect,

        /// <summary>
        /// READS BY DEFAULT, AND REACHES OUTSIDE THE MODEL ONLY WHEN THE CALL ASKS IT TO.
        ///
        /// horizun_budget_compare is the case this exists for. Its whole surface is an
        /// arithmetic comparison of a takeoff against a workbook, and with no `outputs` it
        /// creates nothing, sends nothing and asks for no credential. Declared
        /// ExternalSideEffect - which is what it CAN do - it was hidden from read_only and
        /// safe_write entirely, so a machine allowed to read a budget was refused the
        /// reading because the same tool can also write one.
        ///
        /// Downgrading the whole tool would have been the other half of that mistake: the
        /// destinations really are external, and full_write is really the rung that
        /// authorizes them. So the guarantee is split in two, and BOTH halves are required:
        ///
        ///   * ADMISSION is by this effect - every rung admits it, because the call that
        ///     needs no rung is the common one.
        ///   * THE DESTINATION is enforced PER CALL inside the handler, through
        ///     Settings.AllowsExternalSideEffect, which refuses a declared output under a
        ///     profile that does not authorize external writes and names that profile.
        ///
        /// The MCP annotations still describe the WORST case (openWorldHint, and
        /// destructiveHint where the tool declares it): a client deciding whether to ask a
        /// person must be told what the tool can do, not what this call happens to do.
        ///
        /// A tool classified here that does not enforce its own destinations is a hole,
        /// not a shortcut. ExternalDestinationGateTests asserts the enforcement exists for
        /// every contract carrying this effect.
        /// </summary>
        ExternalSideEffectOnRequest,

        /// <summary>
        /// Steers the host without changing anything that outlives the session: which
        /// Revit the bridge talks to, what is selected, which view is active. No model
        /// change, no document session, no file written.
        ///
        /// Split out of ExternalSideEffect, which had come to mean two different things.
        /// horizun_target and horizun_navigate sat in the same bucket as the workbook
        /// writer, so making read_only refuse "external" effects - which is correct, and
        /// is the fix - would also have stopped a read-only machine from choosing WHICH
        /// Revit it was reading from. The classification, not the profile, was the part
        /// that was wrong.
        /// </summary>
        HostState
    }

    /// <summary>One command, exactly as both halves must understand it.</summary>
    public sealed class CommandContract
    {
        /// <summary>The MCP tool name the client sees.</summary>
        public string Name;

        /// <summary>The plugin command it forwards to. Null for a host-resident tool.</summary>
        public string Command;

        public string Description;
        public JObject InputSchema;
        public JObject OutputSchema;

        /// <summary>
        /// What invoking the tool can change. Shared by both halves so admission,
        /// idempotency and the MCP annotations cannot drift into three opinions.
        /// </summary>
        public ToolEffect Effect;

        /// <summary>
        /// MCP's destructiveHint: this tool can remove or overwrite something a caller
        /// would not get back. DECLARED HERE, next to Effect, and not in the server.
        ///
        /// It used to be a hardcoded list of six tool names inside Tools.cs, three files
        /// away from where a tool is defined. A tool added without editing that list got
        /// destructiveHint=false by default - the annotation a client uses to decide
        /// whether to ask a human first. Silence was the dangerous answer.
        /// </summary>
        public bool Destructive;

        /// <summary>
        /// MCP's openWorldHint: this tool touches something outside the model - the
        /// filesystem, a network endpoint, the Revit session itself. Same story as
        /// Destructive: declared with the contract, never inferred three files away.
        /// </summary>
        public bool OpenWorld;

        /// <summary>
        /// The TOOLSETS (tool packs) this tool belongs to - the groups a session selects
        /// with tool_packs / HORIZUN_TOOL_PACKS or their synonyms "toolsets" /
        /// HORIZUN_TOOLSETS, so a client does not pay for every schema in every session.
        /// Declared in ONE table (<see cref="ToolsetCatalog"/>), attached here by Annotate,
        /// and read by ToolPacks as its membership - there is no second list. A test fails
        /// when a new tool is added without a row.
        ///
        /// A toolset decides WHETHER a tool is visible, never what it takes or what it may
        /// do: selecting one cannot widen the permission profile, the allowlist or the
        /// Python grant. It is not part of the contract hash, as pack membership never was:
        /// it does not change what travels on the wire.
        /// </summary>
        public string[] Toolsets = new string[0];

        /// <summary>
        /// Does a reply from this tool carry text that came from OUTSIDE this bridge - a
        /// model's element, parameter, view, sheet or family names, comments and marks, a
        /// workbook's cells, a DWG's layers and block names, an IFC/BCF topic title?
        ///
        /// That text reaches the client's language model, and whoever authored the model
        /// or the file authored it. The server therefore neutralises invisible and
        /// bidirectional control characters in the reply and marks it as untrusted content
        /// (see ContentSafety in the server). Every tool that forwards to the add-in
        /// carries model text by construction; host-resident tools that read a file or a
        /// job result are named in Annotate.
        /// </summary>
        public bool ExternalContent;
    }

    /// <summary>
    /// THE TOOLSET MAP, DECLARED ONCE, IN THE CONTRACT. A toolset is what tools/list
    /// calls a TOOL PACK: a named subset a session selects so a client does not pay for
    /// every schema in every session. The selection, the dependencies between packs,
    /// the welded core, the refusal of a hidden tool and the measurement all live in
    /// ToolPacks.cs (the add-in and the server share it); this table only says WHICH
    /// TOOL BELONGS WHERE, next to the tool's other declarations, and ToolPacks derives
    /// its membership from it. There is no second list.
    ///
    /// A tool may belong to several toolsets: capture_view is how documentation gets
    /// reviewed AND how an audit collects evidence. Membership is curated by what a
    /// session doing that KIND of work calls, never inferred from a name prefix -
    /// horizun_audit_access is an ACCESSIBILITY audit, not access control.
    ///
    /// Three memberships are guarded by tests because they are security-shaped, not
    /// organisational: core is exactly health/target/job_status/submit_job; the Python
    /// surface rides ONLY in unsafe_code; the document-session tools ride ONLY in
    /// administration (save_document also in documentation, for save_local). A selection is visibility, never privilege.
    /// </summary>
    public static class ToolsetCatalog
    {
        public const string Core = "core";

        /// <summary>Every toolset, with the sentence a client reads to choose one.</summary>
        private static readonly Dictionary<string, string> DescriptionMap =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Core] = "Always on, welded to every selection: health, target, job status, submit job.",
                ["read"] = "Read the model: document facts, element listing and queries, model scan, schedules " +
                           "data, dimensions, 2D detail, planimetry, quantities, structure reading, capture.",
                ["model"] = "Typed model writes: create/transform/delete elements, parameters, keynotes, shared " +
                            "parameters, groups, materials, execute_plan.",
                ["architecture"] = "Architectural operations: wall/floor decomposition, rectangularize walls, " +
                                   "toposolid grading.",
                ["structure"] = "Structural modelling: slabs, framing, structural planning, schedule sheets read and " +
                                "structural connections.",
                ["mep"] = "MEP: the model reads and writes MEP work starts from (query, create, split by level, verify). " +
                          "MEP routing, fittings and connection are in the Pro edition.",
                ["cad"] = "DWG to BIM is in the Pro edition; in this edition the name selects only the model reading it needs.",
                ["documentation"] = "Views, dimensions, annotation, 2D detail, revisions, sheet packing, PDF export, local save.",
                ["planimetry"] = "Planimetry audit and fix, sheet packing, annotation planning, revisions.",
                ["audit"] = "Model audits and corrections, model scan, planimetry audit, accessibility audit, code check.",
                ["coordination"] = "Coordination data: model diff, file triage, ACC status, information containers, " +
                                   "exchange manifests, quantities, budget comparison.",
                ["schedules"] = "Create, manage and read schedules.",
                ["family"] = "Family data: keynotes, shared parameters, classification tables, catalog lookup, " +
                             "template kits.",
                ["interoperability"] = "Exchange: export, Excel read/write, IDS validation, catalog lookup, " +
                                       "budget comparison, workflow procedures.",
                ["powerbi"] = "Power BI push and selection exchange, Excel read/write, budget comparison.",
                ["administration"] = "Document session, open/save, relinquish, script promotion, repair memory.",
                ["local"] = "For local or short-context models: reading (document facts, element listing and " +
                            "queries, model scan, quantities, capture, navigation, model diff) and six typed writes (parameters, create, " +
                            "transform, delete, views, undo). Never the Python surface. About a fifth of the full list.",
                ["unsafe_code"] = "The Python surface: execute_python (still gated by the owner's grant) and the " +
                                  "access request."
            };

        /// <summary>
        /// Convenience names a selection may use, each expanding to real toolsets. They
        /// are NOT toolsets themselves - nothing declares membership in an alias - so the
        /// guarded memberships above cannot be widened through one.
        /// </summary>
        private static readonly Dictionary<string, string[]> AliasMap =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["families"] = new[] { "family" },
                ["data"] = new[] { "schedules", "interoperability", "powerbi" },
                ["admin"] = new[] { "administration", "unsafe_code" }
            };

        public static IReadOnlyDictionary<string, string> Descriptions => DescriptionMap;

        public static IReadOnlyCollection<string> Known => DescriptionMap.Keys;

        public static IReadOnlyDictionary<string, string[]> Aliases => AliasMap;

        // One row per tool, in contract order except core, whose four come first in the
        // order the core guard pins.
        private static readonly List<KeyValuePair<string, string[]>> Rows = new List<KeyValuePair<string, string[]>>
        {
            // ---- core: welded to every selection -----------------------------------
            Row("horizun_health", Core),
            Row("horizun_target", Core),
            Row("horizun_job_status", Core),
            Row("horizun_submit_job", Core),

            // ---- reading the model --------------------------------------------------
            Row("get_document_info", "read", "local"),
            Row("horizun_navigate", "read", "local"),
            Row("horizun_list_elements", "read", "local"),
            Row("horizun_query_model", "read", "mep", "local"),
            Row("horizun_measure_geometry", "read", "audit"),
            Row("horizun_query_relations", "read", "audit"),
            Row("horizun_selection_context", "read", "audit"),
            Row("horizun_api_help", "read"),
            Row("horizun_audit_support", "read", "audit", "coordination"),
            Row("horizun_model_scan", "read", "audit", "local"),
            Row("horizun_room_boundaries", "read", "documentation", "planimetry"),
            Row("horizun_file_info", "read", "coordination"),
            Row("horizun_model_diff", "audit", "coordination", "powerbi", "local"),
            Row("horizun_quantities", "read", "coordination", "local"),
            Row("horizun_capture_view", "read", "documentation", "planimetry", "audit", "local"),
            Row("horizun_verify_changes", "read", "coordination", "audit", "mep"),

            // ---- document session and administration -----------------------------------
            Row("horizun_save_document", "administration", "documentation"),
            Row("horizun_open_document", "administration"),
            Row("horizun_relinquish_all", "administration"),
            Row("horizun_document_session", "administration"),
            Row("horizun_promote_script", "administration"),
            Row("horizun_repair_memory", "administration"),

            // ---- the Python surface ---------------------------------------------------
            Row("horizun_request_python_access", "unsafe_code"),
            Row("horizun_execute_python", "unsafe_code"),

            // ---- typed model writes -----------------------------------------------------
            Row("horizun_create_elements", "model", "structure", "mep", "interoperability", "local"),
            Row("horizun_transform_elements", "model", "local"),
            Row("horizun_write_params_verified", "model", "local"),
            Row("horizun_delete_verified", "model", "documentation", "local"),
            Row("horizun_execute_plan", "model"),
            Row("horizun_manage_materials", "model"),
            Row("horizun_manage_styles", "model", "documentation"),
            Row("horizun_manage_units", "model", "coordination"),
            Row("horizun_ungroup_and_mark", "model"),
            Row("horizun_regroup_by_param", "model"),
            Row("horizun_manage_groups", "model"),
            Row("horizun_manage_worksets", "model", "administration"),
            Row("horizun_set_keynote", "model", "family"),
            Row("horizun_manage_classification_table", "model", "family"),
            Row("horizun_bind_shared_param", "model", "family"),
            Row("horizun_manage_parameters", "model", "family"),
            Row("horizun_query_classification", "read", "audit"),
            Row("horizun_manage_phases", "model", "documentation"),
            Row("horizun_manage_assemblies_parts", "model", "structure"),

            // ---- architecture -----------------------------------------------------------
            Row("horizun_split_floor_loops", "architecture"),
            Row("horizun_split_multilayer_walls", "architecture"),
            Row("horizun_split_by_level", "architecture", "structure", "mep"),
            Row("horizun_embed_floors_in_toposolid", "architecture"),
            Row("horizun_grade_toposolid_around_floors", "architecture"),
            Row("horizun_rectangularize_walls", "architecture"),
            Row("horizun_manage_curtain", "architecture"),
            Row("horizun_slab_shape", "architecture", "structure"),
            Row("horizun_create_railing", "architecture"),
            Row("horizun_wall_sweep", "architecture"),
            Row("horizun_join_geometry", "model", "architecture", "structure"),
            Row("horizun_room_finishes", "architecture"),
            Row("horizun_wall_finish", "architecture", "coordination"),
            Row("horizun_view_visibility", "documentation"),
            Row("horizun_framing", "architecture", "structure"),

            // ---- structure --------------------------------------------------------------
            Row("horizun_query_structure", "read", "structure"),
            Row("horizun_structural_connections", "structure"),
            // Reads schedule sheets (PDF, DWG entities) in the server; proposes a requirement set.
            Row("horizun_read_schedule", "structure", "interoperability"),
            Row("horizun_plan_structure", "structure"),
            Row("horizun_split_multilayer_slabs", "structure"),
            Row("horizun_copy_slab_elevations", "structure"),

            // ---- documentation and planimetry -------------------------------------------
            Row("horizun_manage_views", "documentation", "local"),
            Row("horizun_plan_views", "documentation"),
            Row("horizun_planimetry_ops", "documentation", "planimetry"),
            Row("horizun_annotate", "documentation"),
            Row("horizun_edit_dimensions", "documentation"),
            Row("horizun_detail_2d", "documentation"),
            Row("horizun_get_dimension_references", "read", "documentation"),
            Row("horizun_query_dimensions", "read", "documentation"),
            Row("horizun_query_detail_2d", "read", "documentation"),
            Row("horizun_plan_annotations", "documentation", "planimetry"),
            Row("horizun_manage_revisions", "documentation", "planimetry"),
            Row("horizun_pack_sheets", "documentation", "planimetry"),
            Row("horizun_view_crop", "documentation"),
            Row("horizun_sheet_layout", "documentation"),
            Row("horizun_query_planimetry", "read", "planimetry", "audit"),
            Row("horizun_audit_planimetry", "planimetry", "audit"),
            Row("horizun_fix_planimetry", "planimetry"),

            // ---- schedules --------------------------------------------------------------
            Row("horizun_create_schedule", "schedules"),
            Row("horizun_manage_schedules", "schedules"),
            Row("horizun_list_schedules", "read", "schedules"),
            Row("horizun_get_schedule_data", "read", "schedules"),

            // ---- audit and coordination -------------------------------------------------
            Row("horizun_audit_model", "audit"),
            Row("horizun_apply_corrections", "audit"),
            Row("horizun_audit_access", "audit"),
            Row("horizun_code_check", "audit"),
            Row("horizun_link_schedule", "coordination", "powerbi"),
            Row("horizun_undo", "model", "local"),
            Row("horizun_acc_upload_status", "coordination"),
            Row("horizun_budget_compare", "coordination", "interoperability", "powerbi"),

            // ---- families ---------------------------------------------------------------
            Row("horizun_catalog_lookup", "family", "interoperability"),
            Row("horizun_template_kit", "read", "family"),

            // ---- interoperability and Power BI ------------------------------------------
            Row("horizun_export", "interoperability", "documentation"),
            Row("horizun_validate_ids", "interoperability"),
            Row("horizun_run_procedure", "interoperability"),
            Row("horizun_excel_read_rows", "interoperability", "powerbi"),
            // ISO 19650 information management (2026-09-24). project_context is read-side
            // context every coordination task starts from; core stays at its four.
            Row("horizun_project_context", "read", "coordination", "interoperability"),
            Row("horizun_information_container", "coordination", "interoperability"),
            // 3.0.0 interoperability: exchange manifests and the review -> correction cycle.
            Row("horizun_exchange", "coordination", "interoperability"),
            Row("horizun_cde_cloud", "coordination", "interoperability"),
            Row("horizun_excel_write_rows", "interoperability", "powerbi"),
            Row("horizun_power_bi_push", "powerbi"),
            Row("horizun_selection_exchange", "powerbi")
        };

        private static KeyValuePair<string, string[]> Row(string tool, params string[] toolsets)
            => new KeyValuePair<string, string[]>(tool, toolsets);

        private static readonly Dictionary<string, string[]> ByTool = BuildByTool();

        private static Dictionary<string, string[]> BuildByTool()
        {
            var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string[]> row in Rows)
            {
                if (d.ContainsKey(row.Key))
                    throw new InvalidOperationException("ToolsetCatalog declares '" + row.Key + "' twice.");
                d[row.Key] = row.Value;
            }
            return d;
        }

        /// <summary>The toolsets a tool declares, or an empty array when it has no row.</summary>
        public static string[] Of(string toolName)
            => toolName != null && ByTool.TryGetValue(toolName, out string[] sets) ? sets : new string[0];

        /// <summary>The tools declaring a toolset, in declaration order.</summary>
        public static string[] MembersOf(string toolset)
            => Rows.Where(r => r.Value.Contains(toolset, StringComparer.Ordinal) && Edition.Ships(r.Key)).Select(r => r.Key).ToArray();

        /// <summary>
        /// Every problem with the map: a row naming a tool that does not exist (a rename
        /// nobody finished), a row naming an unknown toolset, an alias expanding to an
        /// unknown toolset or shadowing a real one, and - given the contract names -
        /// every tool with no row at all. Annotate throws on all but the last; the tests
        /// fail on all four.
        /// </summary>
        public static List<string> Audit(IEnumerable<string> contractNames)
        {
            var problems = new List<string>();
            var names = new HashSet<string>(contractNames ?? new string[0], StringComparer.Ordinal);
            foreach (KeyValuePair<string, string[]> row in Rows)
            {
                if (!names.Contains(row.Key))
                    problems.Add("toolset row names a tool that does not exist: '" + row.Key + "'");
                if (row.Value == null || row.Value.Length == 0)
                    problems.Add("toolset row for '" + row.Key + "' names no toolset");
                else
                    foreach (string set in row.Value)
                        if (!DescriptionMap.ContainsKey(set))
                            problems.Add("'" + row.Key + "' names unknown toolset '" + set + "'");
            }
            foreach (KeyValuePair<string, string[]> alias in AliasMap)
            {
                if (DescriptionMap.ContainsKey(alias.Key))
                    problems.Add("alias '" + alias.Key + "' shadows a real toolset");
                foreach (string target in alias.Value)
                    if (!DescriptionMap.ContainsKey(target))
                        problems.Add("alias '" + alias.Key + "' expands to unknown toolset '" + target + "'");
            }
            foreach (string name in names)
                if (!ByTool.ContainsKey(name))
                    problems.Add("'" + name + "' declares no toolset: add a row to ToolsetCatalog.Rows");
            return problems;
        }
    }

    /// <summary>
    /// TOOL GROUPS (4.0.0): which tools a SESSION SEES, as opposed to which it may reach.
    ///
    /// The 3.x toolsets above overlap on purpose (capture_view is how documentation is
    /// reviewed AND how an audit collects evidence), so selecting one saved little:
    /// "architecture" alone dragged 80 tools in. Groups are the opposite: EVERY TOOL HAS
    /// EXACTLY ONE GROUP, so a profile is a plain union and its size is the sum of its
    /// groups. One table, tool -> group, below. A tool with no row fails
    /// ToolGroupTests (Audit) - which is what forces a tool added on a parallel branch
    /// into a group the day that branch is merged.
    ///
    /// A GROUP IS VISIBILITY, NEVER PRIVILEGE. Whether a call may run is still decided by
    /// Settings.IsToolAllowed: denied/allowed tools, the permission profile, the Python
    /// grant and the 3.x tool_packs/toolsets selection, which acts as the CEILING. A
    /// group only decides whether a reachable tool is printed in tools/list. A reachable
    /// tool in an inactive group is still callable - directly, or through
    /// horizun_call_tool - under exactly the same gates.
    ///
    /// Group and profile identifiers are the ones the approved 4.0 proposal uses
    /// (Spanish, lower case, ASCII). The 3.x toolset names and a few English words are
    /// accepted as aliases wherever a client names a group.
    /// </summary>
    public static class ToolGroupCatalog
    {
        public const string Core = "nucleo";
        public const string FullProfile = "completo";
        public const string CoreProfile = "nucleo";

        /// <summary>Every group, with the sentence a client reads to choose one. Order is display order.</summary>
        private static readonly List<KeyValuePair<string, string>> GroupList = new List<KeyValuePair<string, string>>
        {
            G(Core, "Always on: health, target, jobs, query/list elements, navigate, capture, verified writes and " +
                    "deletes, verify changes, undo, save/open, Python (still gated by the owner's grant), toolsets."),
            G("lectura", "Read more of the model: document and file facts, model scan, room boundaries, geometry " +
                         "measures, relations, classification, model diff, selection context, project context, API help."),
            G("modelado", "Typed model writes: create and transform elements, execute_plan, styles, materials, units, " +
                          "phases, worksets, groups, assemblies/parts, join geometry."),
            G("arquitectura", "Architecture: railings, wall sweeps, finishes, curtain walls, slab shape, splitting " +
                              "walls/floors, rectangularize walls, toposolid embedding and grading."),
            G("estructura", "Structure: structural query and planning, schedule sheets read, connections, framing, " +
                            "multilayer slabs, slab elevations."),
            G("mep", "MEP tools are in the Pro edition; this group lists none in this edition."),
            G("documentacion", "Documentation: views, sheets and layout, annotation and dimensions, 2D detail, " +
                               "revisions, visibility, crop, sheet packing, PDF/DWG export."),
            G("cad_ifc", "IDS validation. DWG and IFC to BIM are in the Pro edition."),
            G("auditoria", "Audits: model audit and corrections, planimetry query/audit/fix, accessibility, code " +
                           "check, audit support."),
            G("coordinacion", "Coordination: exchange manifests. Clash, links and BCF coordination are in the Pro edition."),
            G("nube_cde", "Cloud CDE: ACC/CDE folders, upload status, ISO 19650 information containers."),
            G("familias", "Family data: parameters, shared parameters, keynotes, classification table, catalog " +
                          "lookup, template kit."),
            G("datos", "Data: quantities, schedules (create, manage, list, read), Excel read/write, budget " +
                       "comparison, Power BI push and selection, link schedule, procedures."),
            G("admin", "Administration: document session, relinquish all, promote script, repair memory.")
        };

        private static KeyValuePair<string, string> G(string id, string text) => new KeyValuePair<string, string>(id, text);

        // ---- THE ONE TABLE: tool -> group --------------------------------------------------
        // As in the proposal's annex A1. Membership follows what a session doing that kind
        // of work calls (measured usage), never a name prefix. EXACTLY ONE ROW PER TOOL.
        private static readonly List<KeyValuePair<string, string>> Rows = new List<KeyValuePair<string, string>>
        {
            // nucleo (16 contract tools; the server's own controls are ServerCoreTools)
            R("horizun_health", Core), R("horizun_target", Core), R("horizun_job_status", Core),
            R("horizun_submit_job", Core), R("horizun_query_model", Core), R("horizun_list_elements", Core),
            R("horizun_navigate", Core), R("horizun_capture_view", Core), R("horizun_write_params_verified", Core),
            R("horizun_delete_verified", Core), R("horizun_verify_changes", Core), R("horizun_undo", Core),
            R("horizun_execute_python", Core), R("horizun_request_python_access", Core),
            R("horizun_save_document", Core), R("horizun_open_document", Core),

            // lectura
            R("horizun_file_info", "lectura"), R("get_document_info", "lectura"), R("horizun_room_boundaries", "lectura"),
            R("horizun_model_scan", "lectura"), R("horizun_measure_geometry", "lectura"),
            R("horizun_query_relations", "lectura"), R("horizun_project_context", "lectura"),
            R("horizun_selection_context", "lectura"), R("horizun_api_help", "lectura"),
            R("horizun_query_classification", "lectura"), R("horizun_model_diff", "lectura"),

            // modelado
            R("horizun_create_elements", "modelado"), R("horizun_transform_elements", "modelado"),
            R("horizun_execute_plan", "modelado"), R("horizun_manage_styles", "modelado"),
            R("horizun_manage_materials", "modelado"),
            R("horizun_manage_units", "modelado"), R("horizun_ungroup_and_mark", "modelado"),
            R("horizun_manage_phases", "modelado"), R("horizun_manage_worksets", "modelado"),
            R("horizun_manage_groups", "modelado"), R("horizun_regroup_by_param", "modelado"),
            R("horizun_manage_assemblies_parts", "modelado"), R("horizun_join_geometry", "modelado"),

            // arquitectura
            R("horizun_create_railing", "arquitectura"), R("horizun_split_multilayer_walls", "arquitectura"),
            R("horizun_wall_sweep", "arquitectura"), R("horizun_split_floor_loops", "arquitectura"),
            R("horizun_split_by_level", "arquitectura"), R("horizun_embed_floors_in_toposolid", "arquitectura"),
            R("horizun_grade_toposolid_around_floors", "arquitectura"), R("horizun_rectangularize_walls", "arquitectura"),
            R("horizun_manage_curtain", "arquitectura"), R("horizun_slab_shape", "arquitectura"),
            R("horizun_room_finishes", "arquitectura"), R("horizun_wall_finish", "arquitectura"),

            // estructura
            R("horizun_query_structure", "estructura"),
            R("horizun_read_schedule", "estructura"), R("horizun_structural_connections", "estructura"),
            R("horizun_plan_structure", "estructura"), R("horizun_split_multilayer_slabs", "estructura"),
            R("horizun_copy_slab_elevations", "estructura"), R("horizun_framing", "estructura"),

            // mep

            // documentacion
            R("horizun_manage_views", "documentacion"), R("horizun_export", "documentacion"),
            R("horizun_annotate", "documentacion"), R("horizun_sheet_layout", "documentacion"),
            R("horizun_get_dimension_references", "documentacion"), R("horizun_query_dimensions", "documentacion"),
            R("horizun_pack_sheets", "documentacion"), R("horizun_view_visibility", "documentacion"),
            R("horizun_edit_dimensions", "documentacion"), R("horizun_view_crop", "documentacion"),
            R("horizun_manage_revisions", "documentacion"), R("horizun_plan_annotations", "documentacion"),
            R("horizun_query_detail_2d", "documentacion"), R("horizun_plan_views", "documentacion"),
            R("horizun_planimetry_ops", "documentacion"),
            R("horizun_detail_2d", "documentacion"),

            // cad_ifc
            R("horizun_validate_ids", "cad_ifc"),

            // auditoria
            R("horizun_query_planimetry", "auditoria"), R("horizun_audit_model", "auditoria"),
            R("horizun_audit_planimetry", "auditoria"), R("horizun_fix_planimetry", "auditoria"),
            R("horizun_apply_corrections", "auditoria"), R("horizun_audit_access", "auditoria"),
            R("horizun_code_check", "auditoria"), R("horizun_audit_support", "auditoria"),

            // coordinacion
            R("horizun_exchange", "coordinacion"),

            // nube_cde
            R("horizun_cde_cloud", "nube_cde"), R("horizun_acc_upload_status", "nube_cde"),
            R("horizun_information_container", "nube_cde"),

            // familias
            R("horizun_manage_parameters", "familias"),
            R("horizun_bind_shared_param", "familias"),
            R("horizun_template_kit", "familias"),
            R("horizun_catalog_lookup", "familias"), R("horizun_set_keynote", "familias"),
            R("horizun_manage_classification_table", "familias"),

            // datos
            R("horizun_quantities", "datos"), R("horizun_manage_schedules", "datos"),
            R("horizun_create_schedule", "datos"), R("horizun_get_schedule_data", "datos"),
            R("horizun_excel_write_rows", "datos"), R("horizun_excel_read_rows", "datos"),
            R("horizun_list_schedules", "datos"), R("horizun_budget_compare", "datos"),
            R("horizun_power_bi_push", "datos"), R("horizun_selection_exchange", "datos"),
            R("horizun_link_schedule", "datos"), R("horizun_run_procedure", "datos"),

            // admin
            R("horizun_document_session", "admin"), R("horizun_relinquish_all", "admin"),
            R("horizun_promote_script", "admin"), R("horizun_repair_memory", "admin")
        };

        private static KeyValuePair<string, string> R(string tool, string group) => new KeyValuePair<string, string>(tool, group);

        /// <summary>
        /// Server-only tools that ride in the core group. They are not contract rows (the
        /// add-in never sees them, Contract.Hash does not move): the session's own controls.
        /// horizun_toolsets is the core's seventeenth tool; describe/call are the two meta tools.
        /// </summary>
        public static readonly string[] ServerCoreTools = { "horizun_toolsets", "horizun_describe_tool", "horizun_call_tool" };

        // ---- profiles: core + groups ---------------------------------------------------------
        private static readonly List<KeyValuePair<string, string[]>> ProfileList = new List<KeyValuePair<string, string[]>>
        {
            P(CoreProfile),
            P("auditor", "lectura", "auditoria", "documentacion"),
            P("modelador_arquitectura", "lectura", "modelado", "arquitectura"),
            P("modelador_estructura", "lectura", "modelado", "estructura"),
            P("modelador_mep", "lectura", "modelado", "mep"),
            P("modelador", "lectura", "modelado", "arquitectura", "estructura", "mep", "cad_ifc", "familias"),
            P("documentador", "lectura", "documentacion", "datos"),
            P("coordinador", "lectura", "coordinacion", "nube_cde", "cad_ifc", "documentacion"),
            P("analista_datos", "lectura", "datos", "familias"),
            P(FullProfile, "lectura", "modelado", "arquitectura", "estructura", "mep", "documentacion", "cad_ifc",
              "auditoria", "coordinacion", "nube_cde", "familias", "datos", "admin")
        };

        private static KeyValuePair<string, string[]> P(string id, params string[] groups) =>
            new KeyValuePair<string, string[]>(id, groups);

        private static readonly Dictionary<string, string> ProfileTextMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CoreProfile] = "Core only; every other group on demand with horizun_toolsets.",
            ["auditor"] = "Model audits, planimetry and documentation review.",
            ["modelador_arquitectura"] = "Architectural modelling.",
            ["modelador_estructura"] = "Structural modelling.",
            ["modelador_mep"] = "The model work MEP starts from (MEP routing and connection are in the Pro edition).",
            ["modelador"] = "Complete modeller: architecture, structure, MEP basics, IDS and family data.",
            ["documentador"] = "Views, sheets, annotation, export and schedules.",
            ["coordinador"] = "Coordination data, CDE, IDS and documentation.",
            ["analista_datos"] = "Quantities, schedules, Excel, Power BI and family data.",
            [FullProfile] = "Every group: the 3.x list, unchanged."
        };

        /// <summary>
        /// Other names a client may use for a group: the 3.x toolset names (curated, not
        /// derived from their overlapping membership) and English words. A name that is
        /// itself a group (mep, admin) always means the group.
        /// </summary>
        private static readonly Dictionary<string, string[]> GroupAliasMap = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["core"] = new[] { Core },
            ["unsafe_code"] = new[] { Core },
            ["read"] = new[] { "lectura" },
            ["reading"] = new[] { "lectura" },
            ["local"] = new[] { "lectura" },
            ["model"] = new[] { "modelado" },
            ["modeling"] = new[] { "modelado" },
            ["architecture"] = new[] { "arquitectura" },
            ["structure"] = new[] { "estructura" },
            ["documentation"] = new[] { "documentacion" },
            ["cad"] = new[] { "cad_ifc" },
            ["ifc"] = new[] { "cad_ifc" },
            ["interoperability"] = new[] { "cad_ifc", "datos" },
            ["audit"] = new[] { "auditoria" },
            ["planimetry"] = new[] { "auditoria", "documentacion" },
            ["coordination"] = new[] { "coordinacion" },
            ["cloud"] = new[] { "nube_cde" },
            ["cde"] = new[] { "nube_cde" },
            ["family"] = new[] { "familias" },
            ["families"] = new[] { "familias" },
            ["schedules"] = new[] { "datos" },
            ["powerbi"] = new[] { "datos" },
            ["data"] = new[] { "datos" },
            ["administration"] = new[] { "admin" }
        };

        private static readonly Dictionary<string, string> ProfileAliasMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["core"] = CoreProfile,
            ["full"] = FullProfile,
            ["all"] = FullProfile,
            ["complete"] = FullProfile,
            ["modeler"] = "modelador",
            ["modelador_completo"] = "modelador",
            ["modelador_arq"] = "modelador_arquitectura",
            ["modelador_est"] = "modelador_estructura",
            ["documenter"] = "documentador",
            ["coordinator"] = "coordinador",
            ["analista"] = "analista_datos",
            ["data_analyst"] = "analista_datos"
        };

        private static readonly Dictionary<string, string> ByTool = BuildByTool();

        private static Dictionary<string, string> BuildByTool()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> row in Rows)
            {
                if (d.ContainsKey(row.Key))
                    throw new InvalidOperationException("ToolGroupCatalog declares '" + row.Key + "' twice.");
                d[row.Key] = row.Value;
            }
            foreach (string t in ServerCoreTools) d[t] = Core;
            return d;
        }

        /// <summary>Group ids in display order, core first.</summary>
        public static IReadOnlyList<string> Groups { get; } = GroupList.Select(g => g.Key).ToList();

        /// <summary>The groups a profile may switch: every group but core.</summary>
        public static IReadOnlyList<string> OptionalGroups { get; } = GroupList.Select(g => g.Key).Where(g => g != Core).ToList();

        public static IReadOnlyList<string> Profiles { get; } = ProfileList.Select(p => p.Key).ToList();

        public static IReadOnlyDictionary<string, string[]> GroupAliases => GroupAliasMap;

        public static IReadOnlyDictionary<string, string> ProfileAliases => ProfileAliasMap;

        public static string DescriptionOf(string group) =>
            GroupList.Where(g => g.Key == group).Select(g => g.Value).FirstOrDefault();

        public static string ProfileDescription(string profile) =>
            profile != null && ProfileTextMap.TryGetValue(profile, out string d) ? d : null;

        /// <summary>The group a tool belongs to, or null when the table has no row for it.</summary>
        public static string GroupOf(string tool) =>
            tool != null && ByTool.TryGetValue(tool, out string g) ? g : null;

        /// <summary>The contract tools of a group, in table order (server tools excluded).</summary>
        public static string[] MembersOf(string group) =>
            Rows.Where(r => r.Value == group && Edition.Ships(r.Key)).Select(r => r.Key).ToArray();

        /// <summary>The optional groups a profile switches on (core is implied), or null for an unknown profile.</summary>
        public static string[] GroupsOfProfile(string profile)
        {
            string id = CanonicalProfile(profile);
            return id == null ? null : ProfileList.First(p => p.Key == id).Value;
        }

        /// <summary>The profile id a name means (id or alias, any case), or null.</summary>
        public static string CanonicalProfile(string name)
        {
            string n = Normal(name);
            if (n.Length == 0) return null;
            if (ProfileList.Any(p => p.Key == n)) return n;
            return ProfileAliasMap.TryGetValue(n, out string id) ? id : null;
        }

        /// <summary>
        /// The groups a name stands for: a group id or a group alias (3.x toolset or English),
        /// or null when it means nothing. Profiles are NOT accepted here; see GroupsOfProfile.
        /// </summary>
        public static string[] ResolveGroupName(string name)
        {
            string n = Normal(name);
            if (n.Length == 0) return null;
            if (Groups.Contains(n)) return new[] { n };
            return GroupAliasMap.TryGetValue(n, out string[] targets) ? targets : null;
        }

        private static string Normal(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            // The proposal writes them with accents; a client may too.
            return n.Replace("\u00f3", "o").Replace("\u00ed", "i").Replace("\u00e1", "a").Replace("\u00e9", "e").Replace("\u00fa", "u").Replace("\u00e3\u00b3", "o").Replace("\u00e3\u00ad", "i").Replace("\u00e3\u00a1", "a").Replace("\u00e3\u00a9", "e").Replace("\u00e3\u00ba", "u");
        }

        /// <summary>
        /// Every problem with the map: a row naming a tool that does not exist, a row naming
        /// an unknown group, a profile or alias naming an unknown group and - given the
        /// contract names - every tool with NO row. Tests fail on all of them.
        /// </summary>
        public static List<string> Audit(IEnumerable<string> contractNames)
        {
            var problems = new List<string>();
            var names = new HashSet<string>(contractNames ?? new string[0], StringComparer.Ordinal);
            var groups = new HashSet<string>(Groups, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> row in Rows)
            {
                if (!names.Contains(row.Key))
                    problems.Add("group row names a tool that does not exist: '" + row.Key + "'");
                if (!groups.Contains(row.Value))
                    problems.Add("'" + row.Key + "' names unknown group '" + row.Value + "'");
            }
            foreach (string name in names)
                if (!ByTool.ContainsKey(name))
                    problems.Add("'" + name + "' has no group: add exactly one row to ToolGroupCatalog.Rows");
            foreach (string t in ServerCoreTools)
                if (names.Contains(t)) problems.Add("server tool '" + t + "' collides with a contract tool");
            foreach (KeyValuePair<string, string[]> p in ProfileList)
            {
                if (!ProfileTextMap.ContainsKey(p.Key)) problems.Add("profile '" + p.Key + "' has no description");
                foreach (string g in p.Value)
                    if (!groups.Contains(g) || g == Core)
                        problems.Add("profile '" + p.Key + "' names unknown or core group '" + g + "'");
            }
            foreach (KeyValuePair<string, string[]> a in GroupAliasMap)
            {
                if (groups.Contains(a.Key)) problems.Add("group alias '" + a.Key + "' shadows a group");
                foreach (string g in a.Value)
                    if (!groups.Contains(g)) problems.Add("group alias '" + a.Key + "' names unknown group '" + g + "'");
            }
            foreach (KeyValuePair<string, string> a in ProfileAliasMap)
                if (!ProfileList.Any(p => p.Key == a.Value))
                    problems.Add("profile alias '" + a.Key + "' names unknown profile '" + a.Value + "'");
            string[] full = GroupsOfProfile(FullProfile);
            foreach (string g in OptionalGroups)
                if (!full.Contains(g)) problems.Add("the full profile leaves out group '" + g + "'");
            return problems;
        }
    }

    public static class Contract
    {
        /// <summary>
        /// The shape of the exchange between server and add-in. Bumped when that shape
        /// changes, not when a tool is added - a new tool is caught by the hash.
        /// </summary>
        // v2 adds the authenticated __horizun_cancel_queued control message and
        // bridge_queue execution metadata. An older server cannot safely assume that
        // cancelling a waiting call removes it before start, so mixed v1/v2 halves are
        // refused by discovery instead of silently reverting to zombie-start semantics.
        // v3 adds the authenticated __horizun_cancel_job control message: tasks/cancel asks
        // the add-in to take a DURABLE job out of AsyncQueue before it starts, and the
        // job record gains the terminal status "cancelled" (audit 2026-10-08).
        // v4: __horizun_cancel_queued carries `intent` ("cancel" | "timeout"). A caller whose WAIT expired no longer cancels a
        // running cooperative job (review M5); an add-in that predates it would, so the halves must match.
        public const int ProtocolVersion = 4;
        public const int CreateElementsBatchLimit = 2000;

        /// <summary>
        /// HOW BIG ANYTHING ON THE WIRE MAY GET. Shared, and part of the hash below, for
        /// the same reason the schemas are: a limit enforced by one half and not the other
        /// is not a limit, it is a place where one process dies and the other cannot say
        /// why. Both ends of every hop check the same number.
        ///
        /// A request is a method and its arguments - the payloads travel as file paths, so
        /// four megabytes is already far past anything MCP sends. A REPLY is different: a
        /// scan of a large model is legitimately tens of megabytes of JSON, and refusing
        /// one because it is big would be refusing the answer the caller asked for. The
        /// reply limit is therefore set where "no answer is this big" becomes true, not
        /// where "this is a lot" does - it exists to stop an unbounded allocation, not to
        /// second-guess a command. Over it, the caller is TOLD, and told how to narrow the
        /// scope; nothing is ever silently cut in half and handed over as if complete.
        /// </summary>
        public const int MaxRequestBytes = 4 * 1024 * 1024;

        public const int MaxReplyBytes = 32 * 1024 * 1024;

        // An externally persisted async payload still has to fit once the server
        // wraps it in an MCP result, structuredContent and task metadata. Keep this
        // wire budget in the shared contract: the add-in writes the artifact and the
        // standalone server reads it, so neither project may depend on the other's
        // implementation assembly merely to agree on the boundary.
        public const int AsyncResultEnvelopeReserveBytes = 1024 * 1024;
        public const int MaxAsyncResultBytes = MaxReplyBytes - AsyncResultEnvelopeReserveBytes;
        public const long MaxTaskTtlMilliseconds = 7L * 24 * 60 * 60 * 1000;

        /// <summary>
        /// How much free-flowing TEXT a command may return - what a script printed, or a
        /// value that could only be rendered as a string. Unlike the reply limit this one
        /// truncates rather than refuses, because these are for a human to read and the
        /// first quarter-megabyte of them answers the question. The truncation is always
        /// declared in the reply, with the full length, so nobody mistakes the part for
        /// the whole.
        /// </summary>
        public const int MaxScriptTextChars = 256 * 1024;

        /// <summary>
        /// THE PREVENTION GATE, as an argument on the operations this bridge owns.
        /// One schema, spliced into horizun_save_document and horizun_export, so the
        /// two cannot drift into accepting different grammars for one decision.
        /// </summary>
        private const string RequireGateSchema = @"{ ""type"": ""object"", ""required"": [""profile""],
      ""description"": ""OPTIONAL PREVENTION GATE. Omit it and this call behaves exactly as before. With it, horizun_audit_model's checks run on the document AS IT STANDS, the profile is evaluated with the audit's own evaluator (the same rows the audit would return for the same requirement_set), and the decision is recorded in the reply as prevention.decision: allowed, blocked, overridden or not_assessable. blocked and not_assessable REFUSE BEFORE THE FILE IS TOUCHED. not_assessable is not a fail: nothing failed, but part of the measurement did not happen (a check that died, an element that could not be read, a closed workset, a requirement over a part nobody can measure) and the reason leads with which. Incomplete coverage may block and may never allow. Every gated reply also carries prevention.not_interceptable: Revit's own Save, Save As, Synchronize with Central and Export menu are NOT intercepted by this add-in, by choice - see docs/evidence/prevention-operation-matrix.md."",
      ""properties"": {
        ""profile"": { ""type"": ""object"", ""required"": [""name"", ""version"", ""requirements""],
          ""description"": ""The standard, as an argument: nothing is compiled in. requirements uses horizun_audit_model's requirement_set grammar; tolerances, readiness_roles, workset_rules and warning_profile configure the checks exactly as they do on the audit."",
          ""properties"": {
            ""name"": { ""type"": ""string"" }, ""version"": { ""type"": ""string"", ""description"": ""An override is signed against this."" },
            ""requirements"": { ""type"": ""object"" }, ""tolerances"": { ""type"": ""object"" },
            ""readiness_roles"": { ""type"": ""array"" }, ""workset_rules"": { ""type"": ""object"" }, ""warning_profile"": { ""type"": ""object"" }
          }, ""additionalProperties"": false },
        ""document_fingerprint"": { ""type"": ""string"", ""description"": ""Optional. The document_fingerprint an audit reported; the gate refuses (not_assessable) if the active document is another one."" },
        ""finding_set_fingerprint"": { ""type"": ""string"", ""description"": ""Optional. The finding_set_fingerprint of an audit taken in THIS session. The checks are re-run at that audit's top; if they no longer reproduce it the model moved since the audit and the gate refuses (not_assessable) naming both fingerprints."" },
        ""now_utc"": { ""type"": ""string"", ""description"": ""OPTIONAL, and NOT the authority on the time. On a gated operation an override's expires_utc is judged against THIS MACHINE'S UTC clock, always - a caller cannot send a convenient now_utc to walk past an expired override. Sending one adds a constraint rather than replacing the clock: it must be a real UTC timestamp, it must agree with the machine clock to within 300 seconds (further apart and the gate refuses not_assessable, naming the skew and the tolerance), and where both are valid the LATER of the two is used, so it can bring an expiry forward and never push one back. The reply reports what was used in prevention.clock. The clock-free comparison, where now_utc IS the reference, remains on horizun_audit_model's prevention_gate - which decides nothing and writes nothing."" },
        ""override"": { ""type"": ""object"", ""description"": ""A SIGNED STATEMENT accepting named failing findings: who, why, when, which operation, which profile version, and exactly what is accepted - by check name, requirement name or finding_id. It covers only what it names; an override that leaves one failing row uncovered blocks."",
          ""properties"": {
            ""identity"": { ""type"": ""string"" }, ""reason"": { ""type"": ""string"" }, ""timestamp_utc"": { ""type"": ""string"" },
            ""operation"": { ""type"": ""string"" }, ""profile_version"": { ""type"": ""string"" }, ""evidence"": { ""type"": ""string"" },
            ""expires_utc"": { ""type"": ""string"" }, ""findings_ignored"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
          }, ""additionalProperties"": false }
      }, ""additionalProperties"": false }";

        /// <summary>The element selector shared by horizun_measure_geometry and horizun_delete_verified mode=query.</summary>
        // The identity fields a declaring writer accepts (ElementIdentityGuard): verified before anything is written.
        // `bare` says, in a few words, which id key a bare unique_id is compared with (the long form is docs/IDENTITY-GUARD.md).
        private const string IdentityMapSchema = @"{ ""type"": ""object"", ""description"": ""{id_key: unique_id|{unique_id,expected_*}}"" }";
        // `map`: false where the tool has a single id key per action, so identity{} would only repeat the bare fields
        // (it is still accepted; the list has no room to declare it everywhere).
        private static string IdentityFields(string bare, bool map = true) =>
            @"""unique_id"": { ""type"": ""string"", ""description"": ""Checks " + bare + @"."" }, ""unique_ids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""expected_category"": { ""type"": ""string"" }, ""expected_type"": { ""type"": ""string"" }, " + (map ? @"""identity"": " + IdentityMapSchema + ", " : "");
        /// <summary>The opt-in cooperative-reading argument, word for word what model_scan, query_model and
        /// list_elements publish (Tools.cs holds one short form for all of them).</summary>
        private const string CooperativeReadSchema = @"{
      ""type"": ""object"",
      ""additionalProperties"": false,
      ""description"": ""OPT-IN cooperative reading. OMIT IT AND NOTHING CHANGES: this reader behaves exactly as it always has, and the reply carries no extra field. Send it when a PARTIAL answer that arrives is worth more to you than a complete one nobody waited for - a scan of a large model holds Revit's UI thread for as long as it runs, and the person whose Revit it is cannot click anything meanwhile. With it, the read asks BETWEEN elements whether to carry on: it stops if you have gone away, and it stops at your budget. A result that stopped early says PARTIAL and never passes itself off as clean. WHAT THIS DOES NOT PROVE: ui_budget_ms bounds how long the command SPENDS, not that Revit's interface stayed responsive - the command holds the UI thread for its whole duration, the reply still travels back through the pipe, and Revit's own event queue decides when the window repaints. Stopping work and releasing the interface are different events, and only a sampler watching from outside can measure the second."",
      ""properties"": {
        ""ui_budget_ms"": {
          ""type"": ""integer"",
          ""minimum"": 1000,
          ""maximum"": 600000,
          ""default"": 20000,
          ""description"": ""How long this read may hold Revit's UI thread before it stops and says so. Twenty seconds by default, chosen against what a person does: below about that a frozen window reads as 'it is working'; past it people start clicking, then killing Revit.""
        },
        ""max_units"": {
          ""type"": ""integer"",
          ""minimum"": 1,
          ""description"": ""A hard bound on elements EXAMINED, independent of the clock. Separate from the budget on purpose: a bound in units is reproducible across machines and a bound in milliseconds is not.""
        },
        ""cursor"": {
          ""type"": ""string"",
          ""description"": ""Continue a previous partial read. Opaque, and REFUSED against a different request: resuming one query at another query's position produces a page of the wrong elements with nothing to show that anything went wrong. Where this command already pages with its own cursor or offset, the reply says so and issues none here - two continuation tokens over different things is worse than either.""
        }
      }
    }";

        private const string ElementSelectorSchema = @"{ ""type"": ""object"", ""additionalProperties"": false, ""description"": ""Narrows (AND); at least one key. Never implies everything."", ""properties"": { ""category"": { ""type"": ""string"" }, ""family"": { ""type"": ""string"" }, ""prefix"": { ""type"": ""string"", ""description"": ""Case-insensitive start of the family, type or element name."" }, ""parameter"": { ""type"": ""object"", ""required"": [""name"", ""value""], ""properties"": { ""name"": { ""type"": ""string"" }, ""value"": { ""type"": ""string"" } } }, ""created_since"": { ""type"": ""string"", ""description"": ""ISO 8601 UTC. Only elements with the HRZ mark can be dated."" } } }";
        public static readonly List<CommandContract> All = Edition.Filter(Annotate(new List<CommandContract>{
            new CommandContract
            {
                Name = "get_document_info",
                Command = "get_document_info",
                Description = "Active document facts and model_identity. operation=context lists levels (id,name,elevation_m) and non-template views (id,name,type,level_id,level_name), paginated with offset/max_rows. Query these IDs before capture, sheets or creation. Read-only.",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["operation"] = new JObject { ["type"] = "string", ["enum"] = new JArray("info", "context"), ["default"] = "info" },
                        ["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["default"] = 0, ["description"] = "context: offset in each population." },
                        ["max_rows"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 500, ["default"] = 100, ["description"] = "context: rows per population; follow next_offset until null." }
                    },
                    ["additionalProperties"] = false
                }
            },
            new CommandContract
            {
                Name = "horizun_health",
                Command = "horizun_health",
                Description =
                    "Is this bridge alive, and WHICH Revit is on the other end. Reports the Revit year and build, " +
                    "the process id, every open document and the one that is ACTIVE right now (an explicit null " +
                    "when none is, never an empty title). Call it before anything that reads or writes a model: " +
                    "with two Revit versions open, the expensive failure is not a dead bridge, it is a healthy " +
                    "one attached to the wrong instance. Always reports modal_dialog_policy, including owner and session controls for automatic notice closure.",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["detail"] = new JObject
                        {
                            ["type"] = "string", ["enum"] = new JArray("compact", "full"), ["default"] = "compact",
                            ["description"] = "compact (default) answers who/where/what is active - version, commit, add-in assembly, " +
                                "Python runtime, controls, active document, open documents, pending sync - and leaves out the heavy " +
                                "diagnostics (data paths, registry, jobs, timings, clients, recent writes, the Hub block); it names what it left out. " +
                                "full returns everything."
                        },
                        ["include_verification_catalog"] = new JObject
                        {
                            ["type"] = "boolean", ["default"] = false,
                            ["description"] = "true adds verification_catalog: per writing tool, its mechanism and residual_gap_count " +
                                "from WriteVerificationCatalog, plus where the full text (mechanisms, evidence fields, known gaps) lives. " +
                                "Default false keeps health small."
                        }
                    },
                    ["additionalProperties"] = false
                }
            },
            new CommandContract
            {
                Name = "horizun_save_document",
                Command = "horizun_save_document",
                Description =
                    "Save the ACTIVE document and prove it landed: the file's timestamp and size are read from " +
                    "disk before and after and both are reported, because Document.Save() returns void and " +
                    "'it did not throw' is not evidence the file changed. Refuses a document that has never been " +
                    "saved (it will not invent a path) and never calls SaveAs. On a workshared model this saves " +
                    "the LOCAL file only - it is NOT a synchronize with central, and the response says so. " +
                    "Optional require_gate: the model is re-audited against a declared profile before the save " +
                    "and a blocked or not-assessable decision refuses with the file untouched; Revit's own Save " +
                    "and Synchronize with Central are not intercepted, and the reply says so. " +
                    "operation: save_local (default); relinquish_own (frees only what this user owns, measured " +
                    "before/after); sync_with_central (full_write/unsafe_code, or safe_write with the owner's sync grant or sync-only option; comment required). " +
                    "Both rehearse by default and apply with the confirmation_token. Sync refused on failed session checks.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""target_document"": { ""type"": ""string"", ""description"": ""REQUIRED. Title or full path of the document to change. It must be the document ACTIVE in Revit; this never switches documents for you. Aliases accepted for compatibility: expected_document, target_document_title."" },
    ""expected_document"": { ""type"": ""string"", ""description"": ""GUARD: refused unless the ACTIVE document's title matches."" },
    ""operation"": { ""type"": ""string"", ""enum"": [""save_local"", ""sync_with_central"", ""relinquish_own""], ""default"": ""save_local"" },
    ""comment"": { ""type"": ""string"", ""description"": ""sync: required, non-empty."" },
    ""relinquish"": { ""type"": ""string"", ""enum"": [""all"", ""keep_borrowed"", ""borrowed_only"", ""none""], ""default"": ""all"" },
    ""compact"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Compact a local save or central sync."" },
    ""accept_findings"": { ""type"": ""boolean"", ""description"": ""sync: accepts unverified_python only."" },
    ""accept_findings_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""sync: accepts failed_check rows; name EVERY element id. Bound to token."" },
    ""dry_run"": { ""type"": ""boolean"", ""description"": ""sync/relinquish_own only; defaults to true for those operations. Omit for save_local."" },
    ""confirmation_token"": { ""type"": ""string"" },
    ""require_gate"": " + RequireGateSchema + @"
  },
  ""allOf"": [{ ""if"": { ""required"": [""operation""], ""properties"": { ""operation"": { ""const"": ""relinquish_own"" } } }, ""then"": { ""not"": { ""required"": [""compact""] } } }],
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_open_document",
                Command = "horizun_open_document",
                Description =
                    "Open a .rvt/.rfa by PATH, or a model in ACC / BIM 360 by GUID, and make it active. " +
                    "It runs THE SAME GUARDS as horizun_document_session's open, from one shared implementation - " +
                    "the two used to have their own copies and each was strict about something the other was not. " +
                    "REFUSES a file saved in a different Revit than the one running - opening it UPGRADES the file " +
                    "permanently, and a batch would do that to every file it touches - unless allow_upgrade=true; " +
                    "the version is read from the file itself (BasicFileInfo), before anything is opened. REFUSES a " +
                    "NEWER file outright, because no flag can downgrade one. REFUSES a workshared CENTRAL model " +
                    "unless detach=true or open_central=true - and a CLOUD MODEL IS A CENTRAL MODEL, so the same " +
                    "flag is required for it. A path ALREADY OPEN in this session is ACTIVATED, not reopened, and " +
                    "needs no allow_upgrade (version_guard='not_applicable_already_open'), unless detach or audit asks " +
                    "for a real open. " +
                    "BY GUID (cloud_project_guid + cloud_model_guid): the upgrade guard CANNOT RUN, because a " +
                    "cloud model has no local file whose version could be read before opening it - the response " +
                    "reports version_guard='not_applicable_cloud' and a null version rather than letting an " +
                    "unchecked open read like a checked one. Before a cloud open the model is written to the log, " +
                    "because opening one can take Revit down with an access violation inside its own loader and a " +
                    "dead process reports nothing: a log line with no result after it names the model that did it. " +
                    "Either way the active document is re-read afterwards and compared to what was asked.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"", ""description"": ""Full path of the .rvt or .rfa to open. Mutually exclusive with the cloud_* GUIDs - naming a model two ways is refused, not guessed. Send a new idempotency_key (UUID) for this open; keep it only when retrying the same call."" },
    ""expected_version"": { ""type"": ""string"", ""description"": ""OPTIONAL here, REQUIRED by horizun_document_session, and the same check either way: the Revit year you believe this bridge is, e.g. '2026'. Checked against the HOST before anything is touched - the file and the host can both be 2025 and you can still be talking to the Revit next door. For a cloud model this is the only version check that CAN run."" },
    ""cloud_project_guid"": { ""type"": ""string"", ""description"": ""ACC / BIM 360 PROJECT GUID as Revit knows it. NOT the project id in the ACC web URL, which is a different identifier for the same project. An all-zero GUID is refused: Revit would build a path that looks valid and resolves to nothing."" },
    ""cloud_model_guid"": { ""type"": ""string"", ""description"": ""ACC / BIM 360 MODEL GUID as Revit knows it. NOT the 'urn:adsk.wipprod:dm.lineage:...' from the web UI - that decodes to a valid-looking GUID from the document manager, and opening it answers 'the central model is missing'. These GUIDs can be read off Revit's own CollaborationCache on disk."" },
    ""cloud_region"": { ""type"": ""string"", ""default"": ""US"", ""description"": ""Data centre region of the ACC hub, e.g. 'US' or 'EMEA'. Wrong region means the model is simply not found there."" },
    ""allow_upgrade"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Permit opening a file saved in an OLDER Revit version, which upgrades it irreversibly. Also required when the file's version cannot be read at all - unknown is not treated as safe. It CANNOT open a file saved in a NEWER Revit: there is no downgrade, so that is refused whatever this says. Applies to 'path' only; there is no equivalent check to waive for a cloud model."" },
    ""detach"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Open a workshared model detached from central (worksets preserved). The safe way to read a central model, on disk or in the cloud. A detached document has no path that exists until you save it - Revit reports a synthetic '<original>_detached.rvt'."" },
    ""open_central"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Permit opening the CENTRAL model directly - working in the file everyone else synchronizes to. REQUIRED for a cloud model unless you pass detach: a model in ACC / BIM 360 is the central, and living in the cloud rather than on a server share does not make it less shared. Prefer detach."" },
    ""open_all_worksets"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Open every workset. Needed when something downstream MEASURES worksets, because a closed workset is indistinguishable from an empty one - a scan over a partly loaded model quietly scores the parts it cannot see. IT CAN ALSO KILL REVIT, and that is measured, not feared: on 2026-07-30, 2 of 24 ACC models took Revit 2025.4 down with an access violation (0xc0000005) inside SelectedPartitionsForEdit/decommitDocument on open, identical signature both times, with 30 GB of RAM free - and both opened and read fine with this left false. So it is off by default, and if a specific model dies on open this is the first thing to drop. The cost of dropping it is that whatever measures worksets is then measuring what got loaded, not what is there."" },
    ""audit"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Run Revit's audit while opening. Slow, and it can modify the model to repair it."" },
    ""on_open_dialog"": { ""type"": ""string"", ""enum"": [""cancel"", ""dismiss"", ""resolve_in_memory""], ""default"": ""cancel"", ""description"": ""How a modal dialog raised WHILE opening is answered when nobody is at the keyboard. 'cancel' (default) presses Cancel; 'dismiss' acknowledges and continues. 'resolve_in_memory' requires detach=true and requests default error resolutions; after opening, each requested deletion ID is reported as verified_deleted, still_present or unverified. The recovered document is protected from overwriting the source. Scoped to the open call only; every other dialog still cancels."" }
  },
  ""required"": [""idempotency_key""],
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_relinquish_all",
                Command = "horizun_relinquish_all",
                Description =
                    "Relinquish every workset and element this user owns in the ACTIVE workshared document, then " +
                    "MEASURE both levels before and after (workset owners, every element's checkout status); " +
                    "unreadable ownership makes the result unknown, never complete, and Revit's returned ids are not " +
                    "the census. Refuses a non-workshared document. Does not synchronize or save.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""target_document"": { ""type"": ""string"", ""description"": ""REQUIRED. Title or full path of the document to change. It must be the document ACTIVE in Revit; this never switches documents for you. Aliases accepted for compatibility: expected_document, target_document_title."" },
    ""expected_document"": { ""type"": ""string"", ""description"": ""GUARD. If given, the relinquish is refused unless the ACTIVE document's title matches it."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Default TRUE: reports what this user owns and returns a confirmation_token; nothing is relinquished."" },
    ""confirmation_token"": { ""type"": ""string"", ""description"": ""The token from the dry run; required with dry_run=false."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_capture_view",
                Command = "horizun_capture_view",
                Description =
                    "Capture a view image: First query view_id with get_document_info operation=context (or query_planimetry mode=views). " +
                    "Exports the view as a PNG and returns the IMAGE. Reports the file Revit really produced " +
                    "(ExportImage appends view type and name to the path given) and the pixel size read from the " +
                    "PNG header. Schedules cannot be raster-exported and are refused.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""view_name"": { ""type"": ""string"", ""description"": ""Name of the view to capture. Omitted together with view_id: the ACTIVE view."" },
    ""view_id"": { ""type"": ""integer"", ""description"": ""Element id of the view. Takes precedence over view_name."" },
    ""pixel_size"": { ""type"": ""integer"", ""default"": 1600, ""description"": ""Requested width in pixels (64-8192). Revit fits to the view's aspect, so the result may differ - the response reports the real dimensions."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_verify_changes",
                Command = "horizun_verify_changes",
                Description =
                    "LOOK AT WHAT WAS JUST MODELLED. Spatial coherence check of the elements a Horizun write " +
                    "added or modified in this document (or the element_ids you name): every model solid they share with " +
                    "another element is measured and judged - a door or window blocked by a column, wall or fixture, a " +
                    "duplicate of the same type, MEP through structure, unjoined overlaps - while hosts, joins, MEP " +
                    "connections and the structural frame are expected. scope=last_write (default) checks only the most " +
                    "recent write; scope=session unions every write since Revit started (or since_utc), capped at 2000 " +
                    "ids. include_annotation=true also checks whether tags/text notes in the relevant view(s) overlap " +
                    "each other or another tag of the same host - a defect the solid check cannot see. Returns the " +
                    "findings AND an image (temporary isometric 3D view, always rolled back: blue changed, red errors, " +
                    "orange warnings). Every typed write already carries a spatial_check; call this after a modelling " +
                    "batch, look at the image, and do not report the work as done while errors remain.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1, ""maxItems"": 5000, ""description"": ""Elements to check. Omitted: resolved from scope instead."" },
    ""scope"": { ""type"": ""string"", ""enum"": [""last_write"", ""session""], ""default"": ""last_write"", ""description"": ""Ignored when element_ids is given. last_write: only the most recent Horizun write. session: every write's added/modified ids since Revit started (or since_utc), unioned and capped at 2000 ids."" },
    ""since_utc"": { ""type"": ""string"", ""description"": ""With scope=session, only writes at or after this ISO-8601 UTC instant."" },
    ""capture"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Return an image of the elements with the findings coloured. False: findings only, no temporary view."" },
    ""orientation"": { ""type"": ""string"", ""enum"": [""isometric"", ""top"", ""front"", ""right""], ""default"": ""isometric"" },
    ""pixel_size"": { ""type"": ""integer"", ""default"": 1400, ""minimum"": 256, ""maximum"": 4096 },
    ""max_findings"": { ""type"": ""integer"", ""default"": 50, ""minimum"": 1, ""maximum"": 500 },
    ""time_budget_seconds"": { ""type"": ""integer"", ""default"": 60, ""minimum"": 5, ""maximum"": 600, ""description"": ""Stops early and reports partial rather than running unbounded."" },
    ""include_annotation"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Also check tag/text-note overlap (view coordinates) in view_ids, or the owning view of any tag/text note in scope, or the active view."" },
    ""view_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1, ""maxItems"": 50, ""description"": ""Views to check with include_annotation=true. Each id must resolve to a view in this document."" },
    ""clearance_rules"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""Equipment clearance zones [{category OST_*, face front|all|top, depth_mm, width_extra_mm?, height_mm?}]; TOOLS-EXTENDED."" },
    ""target_document"": { ""type"": ""string"", ""description"": ""Title or full path of the ACTIVE document; required when capture=true or operation != check."" },
    ""operation"": { ""type"": ""string"", ""enum"": [""check"", ""snapshot"", ""compare_to"", ""what_if""], ""default"": ""check"", ""description"": ""snapshot: save a named baseline image+camera (framed on element_ids or view_id). compare_to: pixel-diff against it. what_if: apply what_if, measure element_ids (default its ids) before and after, ALWAYS roll back and prove it (rolled_back_verified); refused on workshared models."" },
    ""what_if"": { ""type"": ""object"", ""required"": [""action"", ""ids""], ""additionalProperties"": false,
      ""properties"": { ""action"": { ""type"": ""string"", ""enum"": [""delete"", ""join"", ""unjoin"", ""move""] }, ""ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""integer"" } }, ""vector"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""move: [x,y,z] in unit."" } } },
    ""measures"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""what_if: horizun_measure_geometry measures; default bounding_box, solid_volume."" },
    ""unit"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""snapshot_name"": { ""type"": ""string"", ""description"": ""Baseline name for snapshot/compare_to; kept per document."" },
    ""view_id"": { ""type"": ""integer"", ""description"": ""3D view whose camera and crop/section box snapshot reuses when element_ids is omitted."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_create_schedule",
                Command = "horizun_create_schedule",
                Description =
                    "Create one native Revit schedule for one category, optionally including elements from loaded RVT links; " +
                    "also multi-category (category=OST_MultiCategory) and key schedules (key_schedule, key_rows re-read as key elements). " +
                    "Dry-run is the default. Resolves fields by Revit display name or stable token, groups non-itemized schedules by identity fields, commits once, " +
                    "then re-reads the schedule, fields, sort/group, totals, IncludeLinkedFiles flag and body row count. Zero host elements is valid: " +
                    "the linked elements are included by Revit itself when include_links=true.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""category"", ""name"", ""target_document""],
  ""properties"": {
    ""category"": { ""type"": ""string"", ""description"": ""BuiltInCategory token such as OST_Walls, or the Revit category display name."" },
    ""name"": { ""type"": ""string"", ""description"": ""Name of the new schedule. An existing name is refused, never overwritten."" },
    ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Fields to add: localized Revit display names, Count/Family/Type aliases, or BuiltInParameter tokens. Defaults to Count, Family, Type."" },
    ""include_links"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Set Revit's Include elements in links option."" },
    ""itemized"": { ""type"": ""boolean"", ""default"": false, ""description"": ""List every element when true; otherwise group by the identity fields (never a length, area, volume, count or number) and total the quantities."" },
    ""group_by"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Sort/group exactly by these requested fields, in order; [] groups by nothing. Omitted: derived as above."" },
    ""key_schedule"": { ""type"": ""boolean"", ""default"": false },
    ""key_rows"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 500 },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Validate category and scope without opening a transaction."" },
    ""confirmation_token"": { ""type"": ""string"", ""description"": ""Required when dry_run=false; returned by the exact dry run."" },
    ""target_document"": { ""type"": ""string"", ""description"": ""REQUIRED. Title or full path of the ACTIVE document to change."" }
  },
  ""allOf"": [{ ""if"": { ""required"": [""dry_run""], ""properties"": { ""dry_run"": { ""const"": false } } }, ""then"": { ""required"": [""idempotency_key""] } }],
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_navigate",
                Command = "horizun_navigate",
                Description =
                    "Hand query results back to the Revit UI: select host elements, clear the selection, ask Revit " +
                    "to frame elements, or activate a non-template view. Selection and active-view changes are " +
                    "re-read immediately. Framing has no readable camera acknowledgement in the Revit API, so it " +
                    "is reported as request_accepted rather than falsely claimed as visually verified.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [
    ""operation""
  ],
  ""properties"": {
    ""operation"": {
      ""type"": ""string"",
      ""enum"": [
        ""select"",
        ""clear_selection"",
        ""zoom"",
        ""select_and_zoom"",
        ""open_view""
      ]
    },
    ""element_ids"": {
      ""type"": ""array"",
      ""maxItems"": 5000,
      ""items"": {
        ""type"": ""integer""
      },
      ""description"": ""Host-document ElementIds for select/zoom. Linked element ids are document-local and are NOT accepted here: use `selections`, which carries the link instance beside the id. Sending both is refused.""
    },
    ""view_id"": {
      ""type"": ""integer"",
      ""description"": ""Host-document ViewId for open_view.""
    },
    ""selections"": {
      ""type"": ""array"",
      ""maxItems"": 5000,
      ""description"": ""select/zoom over COMPOSITE identities: each entry is an element and the link instance it lives in, if any. This is the path for anything a clash, a federated query or a linked review produced. A bare number is refused: an element id with no document means nothing, because two documents can hold the same number - and selecting the host element that happens to share it is not an error anybody sees, it is a different element highlighted confidently. Linked elements are selected through Reference.CreateLinkReference and read back by reference: a host element by its id, a linked one by its link instance AND the element inside it. A selection that does not read back fails the call (host_elements_verified, link_instances_verified, linked_elements_verified)."",
      ""items"": {
        ""type"": ""object"",
        ""required"": [
          ""element_id""
        ],
        ""additionalProperties"": false,
        ""properties"": {
          ""element_id"": {
            ""type"": ""integer"",
            ""description"": ""The element's id IN ITS OWN DOCUMENT - the host when link_instance_id is absent, the linked document when it is present.""
          },
          ""link_instance_id"": {
            ""type"": ""integer"",
            ""description"": ""The RevitLinkInstance in the HOST document. Omit for a host element. A link whose document is not loaded is reported as a link state, not as a missing element: the element may be perfectly fine and nobody can reach it.""
          }
        }
      }
    }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_create_elements",
                Command = "horizun_create_elements",
                Description =
                    "Create an atomic batch of architectural, structural or MEP elements, including door, window and wall_chain. " +
                    "tabular_source takes CSV family_instance rows; edits invalidate rehearsal. " +
                    "XYZ coordinates use the internal origin; point families require coordinate_mode. " +
                    "Inapplicable fields are refused. Dry run defaults to validation; apply needs confirmation and idempotency. " +
                    "Re-reads requested properties and fitting connections. Same-batch elbows verify nominal junctions; " +
                    "physical_start_feet/physical_end_feet report trims.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document""],
  ""properties"": {
    ""level_span_exception"": { ""type"": ""string"", ""description"": ""Why rows may cross a level; no model parameter."" },
    ""tabular_source"": { ""type"": ""object"", ""description"": ""INSTEAD of elements: a CSV whose rows become family_instance entries. Columns x,y[,z][,rotation] under the DECLARED decimal_separator; explicit z is absolute internal or shared (undoing the active project position). Without a Z column, place on level_id. A missing cell in a declared Z column is refused. The reply carries path/sha256/data_rows and each entry its source_row."", ""properties"": { ""path"": { ""type"": ""string"" }, ""type_id"": { ""type"": ""integer"" }, ""level_id"": { ""type"": ""integer"" }, ""coordinates"": { ""type"": ""string"", ""enum"": [""internal"", ""shared""], ""default"": ""internal"" }, ""decimal_separator"": { ""type"": ""string"", ""enum"": [""."", "",""], ""default"": ""."" }, ""x_column"": { ""type"": ""string"" }, ""y_column"": { ""type"": ""string"" }, ""z_column"": { ""type"": ""string"" }, ""rotation_column"": { ""type"": ""string"" } }, ""required"": [""path"", ""type_id"", ""level_id""] },
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""elements"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": " + CreateElementsBatchLimit + @", ""items"": {
      ""type"": ""object"", ""required"": [""kind""], ""properties"": {
        ""kind"": { ""type"": ""string"", ""enum"": [""level"", ""grid"", ""wall"", ""floor"", ""ceiling"", ""roof"", ""room"", ""family_instance"", ""sprinkler"", ""structural_framing"", ""structural_column"", ""duct"", ""pipe"", ""conduit"", ""cable_tray"", ""flex_pipe"", ""flex_duct"", ""fitting"", ""wall_opening"", ""slab_opening"", ""beam_system"", ""wall_foundation"", ""accessory_inline"", ""mep_system"", ""shaft"", ""room_separator"", ""space"", ""area"", ""area_boundary"", ""reference_plane""] },
        ""name"": { ""type"": ""string"", ""description"": ""Level/grid name where supported. REQUIRED for kind=mep_system: an unnamed system is indistinguishable from the ones Revit invents from connectivity."" },
        ""elevation"": { ""type"": ""number"" },
        ""placement"": { ""type"": ""string"", ""enum"": [""point"", ""all_enclosed""], ""description"": ""room/space: point uses level_id+point; all_enclosed fills empty regions of level_id+phase_id."" },
        ""phase_id"": { ""type"": ""integer"", ""description"": ""Required only with placement=all_enclosed. For level_id+point omit phase_id: Revit assigns the phase; the reply reports it."" }, ""min_area_m2"": { ""type"": ""number"", ""description"": ""all_enclosed: skip smaller regions"" },
        ""closed"": { ""type"": ""boolean"", ""default"": false, ""description"": ""room_separator: close each profile chain with an edge to its first point; at least three corners. Otherwise preserve open runs."" },
        ""number"": { ""type"": ""string"", ""description"": ""kind='room': the room NUMBER, which is separate from its name and is the identity Revit requires to be unique. Set inside the creating transaction and re-read from the model afterwards."" },
        ""base_level_id"": { ""type"": ""integer"", ""description"": ""kind='shaft': the storey the shaft starts at. A shaft cuts every floor, roof and ceiling between its two levels - that is what separates it from a hole in one slab - and a drawing carries neither, so both are required and neither is defaulted."" },
        ""top_level_id"": { ""type"": ""integer"", ""description"": ""kind='shaft': the storey it stops at. Must sit above base_level_id. Columns (structural_column, or a two-level family_instance such as an architectural column): the top level, with top_offset; or give height instead. Set and read back."" },
        ""view_id"": { ""type"": ""integer"", ""description"": ""kind='room_separator': the PLAN VIEW of the storey the separator sits on. Revit takes room boundary lines through a view and does not check it: a view of another storey ENDS THE PROCESS rather than raising, so this is validated in the rehearsal. Omit it and horizun_plan_from_cad (Pro edition) finds a plan of that storey, or refuses."" },
        ""start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""arc"": { ""type"": ""object"", ""required"": [""centre"", ""radius""],
          ""description"": ""kind='wall': make it CURVED. start, end, centre and radius over-determine the arc on purpose - the command checks the declaration against itself before writing and refuses arc_does_not_close when the centre is not equidistant from both ends, naming both measured distances. After the commit the built curve is re-read and its centre and radius compared with what was asked for, because 'e is Wall' proves nothing about curvature. Without this a curved DWG wall becomes one straight wall PER CHORD, and no audit can match those back to the single entity the drawing shows."",
          ""properties"": {
            ""centre"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
            ""radius"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
            ""clockwise"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Which way round the arc runs from start to end. A declaration, not a guess: the two readings are different walls."" }
          }, ""additionalProperties"": false },
        ""end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""profile"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 128, ""items"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 2000, ""items"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } } }, ""description"": ""Contours -> XYZ points -> coordinates. Perimeter first, holes after it. Implicit closure; one repeated closing point is accepted. Horizontal, coplanar, disjoint non-touching holes strictly inside the perimeter. Z is absolute from internal origin. Maximum 4000 points total."" },
        ""level_id"": { ""type"": ""integer"" }, ""type_id"": { ""type"": ""integer"" },
        ""system_type_id"": { ""type"": ""integer"", ""description"": ""Required for duct and pipe. For kind=mep_system it is the PipingSystemType or MechanicalSystemType the new system is created from - the two domains Revit can create a system in."" },
        ""height"": { ""type"": ""number"" }, ""offset"": { ""type"": ""number"", ""description"": ""Vertical offset from level; if supplied must agree with absolute geometry Z. Otherwise derived from geometry, never from a type default."" },
        ""base_offset"": { ""type"": ""number"", ""description"": ""Wall alias for offset; do not send both."" },
        ""top_offset"": { ""type"": ""number"" },
        ""coordinate_mode"": { ""type"": ""string"", ""enum"": [""absolute"", ""level_offset""], ""description"": ""Point families require this; door/window default to level_offset (SUPUESTO). absolute: internal-origin XYZ. level_offset: XY internal, Z offset from level_id."" },
        ""parameters"": { ""type"": ""object"", ""description"": ""Instance parameter map: exact name, BuiltInParameter or GUID. Strings, integers, booleans, ElementIds; double values are Revit internal units, or explicit unit-bearing strings."" },
        ""slope_degrees"": { ""type"": ""number"", ""minimum"": 0, ""exclusiveMaximum"": 90, ""default"": 0, ""description"": ""Uniform slope on all footprint-roof edges."" },
        ""slope_ratio"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""Uniform rise/run, mutually exclusive with slope_degrees and edge_slopes. 8:12 = 0.6666666666666666."" },
        ""slope_arrow_start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Sloped ceiling: low point [x,y,z] of the horizontal slope arrow, on the profile datum plane, in the request units."" },
        ""slope_arrow_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Sloped ceiling: high point [x,y,z] of the horizontal slope arrow. Slope rises from start to end."" },
        ""expected_arrow_elevations"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" }, ""description"": ""Sloped ceiling: optional absolute underside Z at arrow start/end. Checked against the committed planar underside within 5 mm; their rise must agree with the requested slope within 1 mm."" },
        ""edge_slopes"": { ""type"": ""array"", ""description"": ""One entry per edge across ALL contours (perimeter first, then holes). Mutually exclusive with uniform slopes; uniform slopes tilt the perimeter only."", ""items"": { ""type"": ""object"", ""required"": [""defines_slope""], ""properties"": { ""defines_slope"": { ""type"": ""boolean"" }, ""slope_ratio"": { ""type"": ""number"", ""minimum"": 0 }, ""slope_degrees"": { ""type"": ""number"", ""minimum"": 0, ""exclusiveMaximum"": 90 }, ""overhang"": { ""type"": ""number"" }, ""offset"": { ""type"": ""number"" } }, ""additionalProperties"": false } },
        ""flip"": { ""type"": ""boolean"", ""default"": false }, ""structural"": { ""type"": ""boolean"", ""default"": false },
        ""structural_type"": { ""type"": ""string"", ""enum"": [""NonStructural"", ""Beam"", ""Brace"", ""Column"", ""Footing""] },
        ""fitting"": { ""type"": ""string"", ""enum"": [""elbow"", ""union"", ""transition"", ""tee"", ""takeoff"", ""takeoff_or_tee"", ""cross"", ""cap""], ""description"": ""Required for kind=fitting. cap closes one existing pipe/duct end with a user-supplied type_id; connector from connect_mep open_ends; connection and size re-read after commit. takeoff_or_tee: without a Tap rule, split the main and place a tee. A tee lists the two through-run elements first, then the branch. A CROSS lists four: the first through pair, then the second - Revit's own argument order, and a four-way junction built as two tees is a different piece of pipework. A takeoff lists the branch (whose open connector taps in) first, then the MAIN curve - the branch connector must TOUCH the main."" },
        ""host_id"": { ""type"": ""integer"", ""description"": ""kind=wall_opening: the wall the opening is cut into. kind=family_instance: the HOST element for a hosted placement (wall/floor/face-based family) - the created instance's Host is re-read after commit (host_verified)."" },
        ""find_host"": { ""type"": ""object"", ""description"": ""kind=family_instance without host_id: take the nearest host of this category - walls by distance to their location line (walls on level_id preferred; keep the point on the ROOM side of the wall - the wall-based route projects it onto the line and faces the device to the side it was drawn on; snap=true moves it onto the line first and loses that side), ceilings/floors/roofs whose plan box contains the point. The choice is echoed in the plan row as host_found and re-read after commit (host_verified)."",
          ""properties"": { ""category"": { ""type"": ""string"", ""enum"": [""wall"", ""ceiling"", ""floor"", ""roof""] }, ""max_distance_mm"": { ""type"": ""number"", ""default"": 600 }, ""snap"": { ""type"": ""boolean"", ""default"": false } },
          ""required"": [""category""], ""additionalProperties"": false },
        ""corner_1"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""kind=wall_opening: one diagonal corner, world coordinates."" },
        ""corner_2"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""kind=wall_opening: the opposite diagonal corner."" },
        ""allow_structural"": { ""type"": ""boolean"", ""default"": false, ""description"": ""kind=wall_opening/slab_opening: required true to cut a STRUCTURAL host - the record that a person approved the cut."" },
        ""shape"": { ""type"": ""string"", ""enum"": [""rectangular"", ""circular""], ""default"": ""rectangular"", ""description"": ""kind=slab_opening: the cut's shape."" },
        ""center"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""kind=slab_opening: the opening's centre."" },
        ""diameter"": { ""type"": ""number"", ""description"": ""kind=slab_opening, shape=circular."" },
        ""width"": { ""type"": ""number"", ""description"": ""kind=slab_opening, shape=rectangular; kind=duct on a RECTANGULAR duct type, with height; kind=wall_foundation: requested physical solid width in request units, verified from the provisional solid without changing the shared type (requires a straight host wall)."" },
        ""rotation_degrees"": { ""type"": ""number"", ""default"": 0, ""description"": ""kind=family_instance: rotate the placed instance about Z at its point; the rotation is applied inside the same transaction."" },
        ""direction"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" }, ""description"": ""kind=beam_system: the axis the beams run along."" },
        ""spacing"": { ""type"": ""number"", ""description"": ""kind=beam_system: fixed member spacing; omit for the type's default layout."" },
        ""beam_type_id"": { ""type"": ""integer"", ""description"": ""kind=beam_system: the structural-framing FamilySymbol the members use."" },
        ""wall_id"": { ""type"": ""integer"", ""description"": ""kind=wall_foundation: the bearing wall the footing runs under."" },
        ""top_elevation"": { ""type"": ""number"", ""description"": ""kind=wall_foundation: requested absolute top Z from the internal origin, in request units. A wall foundation follows its host wall base; the provisional solid must reach this Z or the row refuses without changing the wall."" },
        ""thickness"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""kind=wall_foundation: requested physical solid thickness in request units. The selected type must produce it; a mismatch refuses without changing a shared type."" },
        ""member_element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""kind=mep_system: the elements that BELONG to the new system. Each must expose a connector in the system type's own domain - piping members for a PipingSystemType, HVAC members for a MechanicalSystemType - or the row refuses by name rather than build a system that is quietly wrong. One connector per member is added (the lowest-id one of that domain), because adding both ends of a curve would add the element twice. Omit for a named EMPTY system. After the commit the system is re-read: its name, the type it was created from, and WHICH member ids it actually carries (never the count of Add calls that did not throw)."" },
        ""pipe_id"": { ""type"": ""integer"", ""description"": ""kind=accessory_inline: the Pipe the accessory sits in. The run is BROKEN at the point, the symbol placed and rotated onto the axis, and BOTH freshly opened ends connected - verified inside the transaction, the whole batch rolling back on any failure. Refuses a point off the axis (measured mm) or within 300 mm of a pipe end."" },
        ""elements"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 4, ""description"": ""kind=fitting: cap takes one existing pipe/duct and type_id of a user-supplied cap; choose connector with connect_mep open_ends. Other fittings join 2–4 open members. Ambiguous ends require connector ids."", ""items"": {
          ""type"": ""object"", ""properties"": {
            ""element_id"": { ""type"": ""integer"" },
            ""batch_index"": { ""type"": ""integer"", ""description"": ""Instead of element_id: an EARLIER entry of this same batch (0-based). Its geometry does not exist at plan time, so the connector pair is selected inside the atomic transaction - a refusal there rolls the whole batch back."" },
            ""connector"": { ""type"": ""integer"" }
          }, ""additionalProperties"": false } }
      }, ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: create elements"" },
    ""response_mode"": { ""type"": ""string"", ""enum"": [""full"", ""summary""], ""default"": ""full"", ""description"": ""summary: rows that verified cleanly collapse to counts by status/kind plus their element ids; failed rows, rows with findings and elements the spatial_check names stay in full. Presentation only; response_omissions names what was left out."" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_list_elements",
                Command = "horizun_list_elements",
                Description =
                    "List elements of one category in the active model and loaded RVT links. Totals are exact, whatever " +
                    "max_rows; rows name source model and link instance. Unloaded links, failures and workset gaps are " +
                    "reported; empty never means complete.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [
    ""category""
  ],
  ""properties"": {
    ""confirm_large_operation"": { ""type"": ""boolean"", ""description"": ""Explicitly confirm execution when the conservative scope estimate exceeds the large-model threshold."" },
    ""category"": {
      ""type"": ""string"",
      ""description"": ""BuiltInCategory (OST_Walls) or display name. Family or ids: query_model.""
    },
    ""target_document"": { ""type"": ""string"", ""description"": ""Optional guard: title of the ACTIVE document this read is about (.rvt suffix optional). A mismatch refuses before collecting any elements. Aliases: expected_document, target_document_title."" },
    ""include_links"": {
      ""type"": ""boolean"",
      ""default"": true
    },
    ""offset"": {
      ""type"": ""integer"",
      ""minimum"": 0,
      ""default"": 0
    },
    ""max_rows"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 1000,
      ""default"": 200
    },
    ""return_parameters"": { ""type"": ""array"", ""maxItems"": 30, ""items"": { ""type"": ""string"" }, ""description"": ""Per row: instance parameter, else type's. No '*'."" },
    ""cooperative"": {
      ""type"": ""object"",
      ""additionalProperties"": false,
      ""description"": ""OPT-IN cooperative reading. OMIT IT AND NOTHING CHANGES: this reader behaves exactly as it always has, and the reply carries no extra field. Send it when a PARTIAL answer that arrives is worth more to you than a complete one nobody waited for - a scan of a large model holds Revit's UI thread for as long as it runs, and the person whose Revit it is cannot click anything meanwhile. With it, the read asks BETWEEN elements whether to carry on: it stops if you have gone away, and it stops at your budget. A result that stopped early says PARTIAL and never passes itself off as clean. WHAT THIS DOES NOT PROVE: ui_budget_ms bounds how long the command SPENDS, not that Revit's interface stayed responsive - the command holds the UI thread for its whole duration, the reply still travels back through the pipe, and Revit's own event queue decides when the window repaints. Stopping work and releasing the interface are different events, and only a sampler watching from outside can measure the second."",
      ""properties"": {
        ""ui_budget_ms"": {
          ""type"": ""integer"",
          ""minimum"": 1000,
          ""maximum"": 600000,
          ""default"": 20000,
          ""description"": ""How long this read may hold Revit's UI thread before it stops and says so. Twenty seconds by default, chosen against what a person does: below about that a frozen window reads as 'it is working'; past it people start clicking, then killing Revit.""
        },
        ""max_units"": {
          ""type"": ""integer"",
          ""minimum"": 1,
          ""description"": ""A hard bound on elements EXAMINED, independent of the clock. Separate from the budget on purpose: a bound in units is reproducible across machines and a bound in milliseconds is not.""
        },
        ""cursor"": {
          ""type"": ""string"",
          ""description"": ""Continue a previous partial read. Opaque, and REFUSED against a different request: resuming one query at another query's position produces a page of the wrong elements with nothing to show that anything went wrong. Where this command already pages with its own cursor or offset, the reply says so and issues none here - two continuation tokens over different things is worse than either.""
        }
      }
    }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_measure_geometry",
                Command = "horizun_measure_geometry",
                Description =
                    "Read-only measures of ids, a selector or selection=true (Revit's current selection): solid_volume (m3), " +
                    "summary (paged, mm/mm2/mm3; linked elements through current selection, coordinates in host), section_profile, bounding_box, end_points, face_normals, faces, edges, location. A value that cannot be read " +
                    "is null and named in unreadable with its reason, never 0; coverage_complete says whether all were read. " +
                    "To rehearse a change and measure it: horizun_verify_changes operation=what_if.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""measures""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""ids"": { ""type"": ""array"", ""maxItems"": 500, ""items"": { ""type"": ""integer"" } },
    ""selector"": " + ElementSelectorSchema + @",
    ""selection"": { ""type"": ""boolean"", ""description"": ""true: measure the current selection. One source: ids, selector or selection."" },
    ""measures"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""summary"", ""solid_volume"", ""section_profile"", ""bounding_box"", ""end_points"", ""face_normals"", ""faces"", ""edges"", ""location""] } },
    ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0, ""description"": ""summary: use page.next_offset; keep the selection and model unchanged between calls."" },
    ""page_size"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100, ""default"": 100, ""description"": ""summary: maximum elements per page."" },
    ""unit"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_query_relations",
                Command = "horizun_query_relations",
                Description =
                    "Read-only relations per element id: joined, cut_by, cutting, host, hosted, neighbors (within radius_mm) " +
                    "and type_info. Each related item carries id, category and relation type. unreadable names what could " +
                    "not be read; an empty list was read and is empty.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""ids"", ""relations""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""integer"" } },
    ""relations"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""joined"", ""cut_by"", ""cutting"", ""host"", ""hosted"", ""neighbors"", ""type_info""] } },
    ""radius_mm"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 5000, ""default"": 100, ""description"": ""neighbors only: box-to-box distance."" },
    ""unit"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_selection_context",
                Command = "horizun_selection_context",
                Description =
                    "Read-only. context: selection (also in links) or ids: type, level, parameters, geometry, faces, joins, host/hosted, " +
                    "neighbours. snapshot: named photo. diff: photo vs model or vs against. validate_ids: ids read earlier, " +
                    "coincide, cambio or no_existe (after a synchronize). " +
                    "view_contents: per category, what a view draws (groups, links); room_id/region: real outline.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""context"", ""snapshot"", ""diff"", ""validate_ids"", ""view_contents""], ""default"": ""context"" },
    ""view_id"": { ""type"": ""integer"" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""max_rows"": { ""type"": ""integer"" },
    ""room_id"": { ""type"": ""integer"" },
    ""region"": { ""type"": ""object"" },
    ""include_links"": { ""type"": ""boolean"" },
    ""checks"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""object"", ""properties"": {
      ""id"": { ""type"": ""integer"" }, ""unique_id"": { ""type"": ""string"", ""minLength"": 1 },
      ""expected_category"": { ""type"": ""string"" }, ""expected_type"": { ""type"": ""string"", ""description"": ""'Family: Type'"" } },
      ""anyOf"": [ { ""required"": [""id""] }, { ""required"": [""unique_id""] } ], ""additionalProperties"": false },
      ""description"": ""validate_ids only."" },
    ""ids"": { ""type"": ""array"", ""maxItems"": 5000, ""items"": { ""type"": ""integer"" } },
    ""selector"": " + ElementSelectorSchema + @",
    ""selection"": { ""type"": ""boolean"" },
    ""include"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""enum"": [""geometry"", ""relations"", ""neighbors"", ""parameters""] } },
    ""radius_mm"": { ""type"": ""number"" },
    ""parameters"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""max_faces"": { ""type"": ""integer"" },
    ""snapshot_name"": { ""type"": ""string"" },
    ""against"": { ""type"": ""string"" },
    ""tolerance_mm"": { ""type"": ""number"" },
    ""unit"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""] }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_api_help",
                Command = "horizun_api_help",
                Description = "Read-only reflection over this Revit's loaded API. describe (default) lists members unchanged. " +
                    "check_signature compares declared CLR argument_types with every overload of the exact member_filter (.ctor for constructors), " +
                    "reporting exact/implicit matches, conversions, rejected overloads and IronPython type suggestions. " +
                    "Never invokes a member, converts values or writes. Type compatibility does not prove that concrete values or IronPython binding will succeed.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""type_name""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""describe"", ""check_signature""], ""default"": ""describe"" },
    ""type_name"": { ""type"": ""string"", ""description"": ""Public type in the already loaded RevitAPI, RevitAPIUI or RevitAPIIFC assemblies. Example: Autodesk.Revit.DB.ElementId."" },
    ""member_filter"": { ""type"": ""string"", ""description"": ""describe: case-insensitive substring, as before. check_signature: required exact method name or .ctor (case-sensitive)."" },
    ""argument_types"": { ""type"": ""array"", ""minItems"": 0, ""maxItems"": 32, ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 512 }, ""description"": ""Required for check_signature, including [] for zero arguments. CLR names only, no expressions, values or implicit null. Example: [System.Int64]; generic example: System.Collections.Generic.List[Autodesk.Revit.DB.ElementId]. Unresolved or ambiguous types keep coverage_complete=false; no assembly is loaded by name."" },
    ""include_inherited"": { ""type"": ""boolean"", ""description"": ""Default true for describe (legacy behavior), false for check_signature. Constructors are never inherited."" },
    ""max_members"": { ""type"": ""integer"", ""description"": ""describe result cap, unchanged. check_signature always examines every overload and does not truncate."" }
  }, ""additionalProperties"": false,
  ""allOf"": [{ ""if"": { ""required"": [""operation""], ""properties"": { ""operation"": { ""const"": ""check_signature"" } } },
    ""then"": { ""required"": [""member_filter"", ""argument_types""], ""properties"": { ""member_filter"": { ""type"": ""string"", ""pattern"": ""^(\\.ctor|[A-Za-z_][A-Za-z0-9_]*)$"" } } } }]
}")
            },
            new CommandContract
            {
                Name = "horizun_room_boundaries",
                Command = "horizun_room_boundaries",
                Description = "Read the effective boundary loops of explicit Rooms, their wall/separation-line ids, closure gaps and Revit's native redundant-room warnings. Unreadable boundaries and warnings leave coverage incomplete; duplicate detection covers only effective boundary segments.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""room_ids""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""room_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""integer"" }, ""description"": ""Explicit Rooms in the active model. Each row lists the effective boundary loops, source ids/kinds, closure, redundant warning state and measured area."" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_query_model",
                Command = "horizun_query_model",
                Description =
                    "Read host and loaded links with category, family, type, name, level, parameter and bounding-box filters. " +
                    "Pages use a stale-detecting cursor. Coverage identifies unreadable elements, links and worksets. " +
                    "group_by counts the whole matched set without rows; sum_parameters reports contributors. " +
                    "summary returns counts without rows; compact uses lean fields and display units. " +
                    "source_models / link_instance_ids restrict sources.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""confirm_large_operation"": { ""type"": ""boolean"", ""description"": ""Required for a non-summary query when the conservative scope exceeds 50000 elements or is unknown, regardless of max_rows. A refusal suggests the same call with confirmation."" },
    ""element_ids"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""integer""
      },
      ""maxItems"": 500,
      ""description"": ""Name EXACTLY the rows you want - the verification read for write paths. Ids resolve directly (never via category collectors); an id resolving to nothing lands in unreadable instead of silently shrinking the answer. The other filters still apply.""
    },
    ""target_document"": { ""type"": ""string"", ""description"": ""Optional guard: the title of the document this read is about. When given and the ACTIVE document is another, the read is refused and nothing is returned."" },
    ""response_mode"": {
      ""type"": ""string"",
      ""enum"": [
        ""full"",
        ""compact"",
        ""summary""
      ],
      ""default"": ""full"",
      ""description"": ""summary returns counts, including group_by, without rows; removed row options appear in normalized. compact presets fields and parameters in display units. Coverage is retained.""
    },
    ""cache_mode"": {
      ""type"": ""string"",
      ""enum"": [
        ""bypass"",
        ""reuse""
      ],
      ""default"": ""bypass"",
      ""description"": ""reuse opts into bounded DTO caching for complete, non-workshared host-only model queries. Other scopes always remeasure. Invalidated on document/view events; maximum age 5 seconds. Use bypass for independent verification reads.""
    },
    ""include_diagnostics"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""Return per-call collection and shaping/cache milliseconds and data bytes, excluding queue and transport. Reports cache hit, miss, bypass or ineligible.""
    },
    ""categories"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""string""
      },
      ""description"": ""BuiltInCategory tokens or localized Revit category names. Omit for all non-type elements.""
    },
    ""family"": {
      ""type"": ""string"",
      ""description"": ""Case-insensitive substring.""
    },
    ""type"": {
      ""type"": ""string"",
      ""description"": ""Case-insensitive substring of the type name.""
    },
    ""name"": {
      ""type"": ""string"",
      ""description"": ""Case-insensitive substring of the element name.""
    },
    ""level"": {
      ""type"": ""string"",
      ""description"": ""Case-insensitive substring of the level name. Matches the level the element is ASSOCIATED with, wherever its category keeps it - walls' base constraint, family instances' base level, MEP curves' reference level - not only the plain Level parameter, so it works for walls without knowing about WALL_BASE_CONSTRAINT.""
    },
    ""parameters"": {
      ""type"": ""array"",
      ""description"": ""All predicates must match. Names may be BuiltInParameter tokens, shared-parameter GUIDs or display names; an ambiguous display name is unreadable, never guessed."",
      ""items"": {
        ""type"": ""object"",
        ""required"": [
          ""name"",
          ""operator""
        ],
        ""properties"": {
          ""name"": {
            ""type"": ""string""
          },
          ""operator"": {
            ""type"": ""string"",
            ""enum"": [
              ""exists"",
              ""not_exists"",
              ""equals"",
              ""not_equals"",
              ""contains"",
              ""starts_with"",
              ""ends_with"",
              ""gt"",
              ""gte"",
              ""lt"",
              ""lte""
            ]
          },
          ""value"": {
            ""description"": ""For numeric comparisons, a JSON number is compared to the raw Revit internal-unit value. Strings compare to the stored/displayed text, case-insensitively.""
          }
        },
        ""additionalProperties"": false
      }
    },
    ""bounding_box"": {
      ""type"": ""object"",
      ""required"": [
        ""min"",
        ""max""
      ],
      ""properties"": {
        ""min"": {
          ""type"": ""array"",
          ""minItems"": 3,
          ""maxItems"": 3,
          ""items"": {
            ""type"": ""number""
          }
        },
        ""max"": {
          ""type"": ""array"",
          ""minItems"": 3,
          ""maxItems"": 3,
          ""items"": {
            ""type"": ""number""
          }
        },
        ""units"": {
          ""type"": ""string"",
          ""enum"": [
            ""mm"",
            ""m"",
            ""feet""
          ],
          ""default"": ""mm""
        }
      },
      ""additionalProperties"": false
    },
    ""scope"": {
      ""type"": ""string"",
      ""enum"": [
        ""model"",
        ""current_view"",
        ""view""
      ],
      ""default"": ""model""
    },
    ""view_id"": {
      ""type"": ""integer"",
      ""description"": ""Required for scope=view. View scope is host-only; combine links with model scope. Example: {scope: 'view', view_id: 123}; use an id read from this document.""
    },
    ""include_links"": {
      ""type"": ""boolean"",
      ""description"": ""Omitted: true for scope=model (the default), false for scope=view/current_view. Explicit true with a view scope is refused: a host ViewId cannot establish visibility in a linked document.""
    },
    ""source_models"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""string"" },
      ""minItems"": 1,
      ""description"": ""Read ONLY these documents: exact titles as rows report source_model (case-insensitive), or \""host\"" for the active document. Other documents are never collected. A name matching no loaded source refuses with the available ones. Union with link_instance_ids.""
    },
    ""link_instance_ids"": {
      ""type"": ""array"",
      ""items"": { ""type"": ""integer"" },
      ""minItems"": 1,
      ""description"": ""Read ONLY these link placements (RevitLinkInstance ids, as rows report link_instance_id) - the way to pick one of two placements of the same file. Union with source_models; the host is read only if source_models names it.""
    },
    ""return_parameters"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""string""
      },
      ""description"": ""Specific parameters to project into each row. '*' lists every instance and type parameter (keys instance:Name / type:Name); needs parameter_format=full, no group_by or sum_parameters. Only the requested page is projected, with a 512 KiB row budget; an oversized single row requires named parameters.""
    },
    ""include_bounding_box"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""Host bounds (coordinate_units). Grid rows use axis bounds or extents; spatial filters prefer the model box. datum gives grid geometry or level elevation.""
    },
    ""include_room"": { ""type"": ""boolean"", ""description"": ""Room/space of each row in phase (required); misses are unassigned. Rules: TOOLS-EXTENDED."" },
    ""phase"": { ""type"": ""string"", ""description"": ""include_room: the phase name (no default)."" },
    ""include_compound_structure"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""Attach the layers of a wall/floor/roof/ceiling type (or of an instance's type) in millimetres, exterior to interior, in the shape horizun_manage_system_types (Pro edition) takes as compound_structure: read it, change the core width, duplicate. A type with no compound structure says so.""
    },
    ""include_orientation"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""Attach a placement block as Revit stores it: for a family instance its point, rotation, facing and hand vectors, total transform (a face-based family looks out of its face along basis_z), reflection flags and the stable reference of its host face; for a wall its location line, width, exterior normal and flip. Nothing is derived - for checks that must not borrow a placing tool's reasoning.""
    },
    ""exclude_nested"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""Drop family instances nested in another (SuperComponent set); unreadable nesting is reported in unreadable.""
    },
    ""include_mep"": {
      ""type"": ""boolean"",
      ""default"": false,
      ""description"": ""Attach per-row connector facts (id, domain, shape/size, open or connected, partners, system membership) for elements with a connector manager, plus a mep_summary over every matched row. Connector ids are the names kind=fitting accepts.""
    },
    ""coordinate_units"": {
      ""type"": ""string"",
      ""enum"": [
        ""mm"",
        ""m"",
        ""feet""
      ],
      ""default"": ""mm""
    },
    ""include_types"": {
      ""type"": ""boolean"",
      ""default"": false
    },
    ""cursor"": {
      ""type"": ""string"",
      ""description"": ""next_cursor from the previous page. It is refused if the query or result set changed.""
    },
    ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Matched rows to skip (a start position). Not with cursor or group_by."" },
    ""max_rows"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 500,
      ""default"": 100
    },
    ""group_by"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""items"": {
        ""type"": ""string"",
        ""pattern"": ""^(category|level|type|family|source_model|source_kind|parameter:.+)$""
      },
      ""description"": ""Aggregate instead of listing: returns groups with counts over the WHOLE matched set in one call, no rows and no cursor. 'how many wall types per floor' is group_by:[type,level]. parameter:<name> groups by that parameter's displayed value.""
    },
    ""parameter_format"": {
      ""type"": ""string"",
      ""enum"": [
        ""full"",
        ""compact""
      ],
      ""default"": ""full"",
      ""description"": ""compact returns each readable parameter as name:value instead of the five-field object (~5x smaller per parameter); a measurable number is converted to the host document's display unit and the reply names each parameter's unit once in parameter_units (converted=false means Revit's raw internal value). Parameters that were absent or unreadable move to a per-row parameter_issues object rather than disappearing - compact is a diet, not an amnesty.""
    },
    ""return_fields"": {
      ""type"": ""array"",
      ""minItems"": 1,
      ""items"": {
        ""type"": ""string"",
        ""enum"": [
          ""element_id"",
          ""source_reference"",
          ""unique_id"",
          ""category"",
          ""built_in_category"",
          ""name"",
          ""family"",
          ""type"",
          ""type_id"",
          ""level"",
          ""is_element_type"",
          ""source_kind"",
          ""source_model"",
          ""link_instance_id"",
          ""host_id"",
          ""host_category"",
          ""is_view_template"",
          ""view_template_id"",
          ""view_type"",
          ""view_range"",
          ""sketch""
        ]
      },
      ""description"": ""Row fields besides element_id (always present). View fields, view_range (plans) and sketch (its loops in mm) are opt-in and null elsewhere. Identity and federation fields repeat down a page; name only what you will read.""
    },
    ""sum_parameters"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""string""
      },
      ""description"": ""With group_by: numeric parameters to sum per group. Each sum reports summed/absent/unreadable/non_numeric counts and a complete flag - a sum over part of a group never reads like a sum over all of it.""
    },
    ""cooperative"": {
      ""type"": ""object"",
      ""additionalProperties"": false,
      ""description"": ""OPT-IN cooperative reading. OMIT IT AND NOTHING CHANGES: this reader behaves exactly as it always has, and the reply carries no extra field. Send it when a PARTIAL answer that arrives is worth more to you than a complete one nobody waited for - a scan of a large model holds Revit's UI thread for as long as it runs, and the person whose Revit it is cannot click anything meanwhile. With it, the read asks BETWEEN elements whether to carry on: it stops if you have gone away, and it stops at your budget. A result that stopped early says PARTIAL and never passes itself off as clean. WHAT THIS DOES NOT PROVE: ui_budget_ms bounds how long the command SPENDS, not that Revit's interface stayed responsive - the command holds the UI thread for its whole duration, the reply still travels back through the pipe, and Revit's own event queue decides when the window repaints. Stopping work and releasing the interface are different events, and only a sampler watching from outside can measure the second."",
      ""properties"": {
        ""ui_budget_ms"": {
          ""type"": ""integer"",
          ""minimum"": 1000,
          ""maximum"": 600000,
          ""default"": 20000,
          ""description"": ""How long this read may hold Revit's UI thread before it stops and says so. Twenty seconds by default, chosen against what a person does: below about that a frozen window reads as 'it is working'; past it people start clicking, then killing Revit.""
        },
        ""max_units"": {
          ""type"": ""integer"",
          ""minimum"": 1,
          ""description"": ""A hard bound on elements EXAMINED, independent of the clock. Separate from the budget on purpose: a bound in units is reproducible across machines and a bound in milliseconds is not.""
        },
        ""cursor"": {
          ""type"": ""string"",
          ""description"": ""Continue a previous partial read. Opaque, and REFUSED against a different request: resuming one query at another query's position produces a page of the wrong elements with nothing to show that anything went wrong. Where this command already pages with its own cursor or offset, the reply says so and issues none here - two continuation tokens over different things is worse than either.""
        }
      }
    }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_transform_elements",
                Command = "horizun_transform_elements",
                Description =
                    "Apply an atomic batch of move, copy, rotate, mirror, pin, unpin, type-change (fixed or a " +
                    "per-instance rule) operations to explicit host ElementIds, attach wall tops/bases to a roof, " +
                    "floor or ceiling, or realign a wall's edited elevation " +
                    "profile onto its current location line. move_preserve_pin moves pinned elements and restores pin state atomically. Dry-run resolves every target and refuses duplicate " +
                    "targets across operations. Move/rotate are accepted only for elements whose Location can be " +
                    "sampled and are verified from fresh post-commit location points; copies, pin state and type ids " +
                    "are likewise re-read. A rotate also verifies that each family instance's axes turned. A move or " +
                    "rotate that would take a hosted instance off its host (or onto another) is rolled back whole with host_changed: re-place it instead. " +
                    "realign_wall_sketch may not be mixed with any other operation in one call and must be sent alone.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""operations""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""tolerance_mm"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""realign_wall_sketch only: how far a wall's location line may already sit from its sketch's plane/footprint before it counts as aligned (default 2mm) - the same tolerance horizun_audit_model's wall_sketch_drift finding uses, so a wall it reports correctable resolves the same way here."" },
    ""operations"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": {
      ""type"": ""object"", ""required"": [""operation""], ""anyOf"": [ { ""required"": [""element_ids""] }, { ""required"": [""unique_ids""] } ], ""properties"": {
        ""operation"": { ""type"": ""string"", ""enum"": [""wall_join"", ""wall_attach_top"", ""wall_attach_base"", ""move"", ""move_preserve_pin"", ""copy"", ""rotate"", ""mirror"", ""pin"", ""unpin"", ""change_type"", ""change_type_by_rule"", ""realign_wall_sketch"", ""set_curve"", ""move_tag_head"", ""set_tag_leader"", ""array_linear"", ""array_radial"", ""rename_level"", ""edit_sketch"", ""column_attach"", ""column_detach""],
          ""description"": ""column_attach/column_detach (alone in the call, really rehearsed) join or release a column end and a beam, brace, floor, roof or ceiling; re-read after commit. wall_attach_top/base attaches straight walls to an approved host and verifies both end elevations; dry_run reports predicted_end_elevations_feet from the target faces. Revit 2023/2024 cannot attach via this API; for a new wall use horizun_create_elements kind=wall_profile with an explicit profile. NOT undoable by horizun_undo. Re-sending for a wall already attached to that target only re-verifies. array_linear/array_radial: count members incl. the original, along vector or about axis_start-axis_end by angle_degrees; each copy re-read at k*step. move_tag_head sets an IndependentTag's head (point: absolute, one tag; or vector: a displacement for every tag listed) and re-reads TagHeadPosition within 1e-5 ft; set_tag_leader edits the leader of an IndependentTag with exactly ONE tagged reference (has_leader, leader_end_condition attached|free, leader_end for a FREE end, leader_elbow, leader_visible), refusing what Revit reports it cannot assign (CanLeaderEndConditionBeAssigned), a free end on an attached leader, a leader edit on a tag without a leader, a pinned tag and a multi-reference tag; every requested property is re-read after commit. Room/space/area tags are NOT covered by these two operations. set_curve replaces ONE element's location line with the line given by start and end - what an incremental DWG update needs when a drawing moves a wall and the element must keep its id, its parameters and everything hosted on it. It is verified by re-reading the curve and checking the endpoints lie ON the line that was set, because Revit trims a wall back to where the centrelines of the walls it meets cross, and demanding the exact endpoints would report every joined corner as a failure. change_type_by_rule resolves EACH instance's own type from `rule`: an ordered [{when:{measure:{op:value}},type_id}] list, first match wins, plus an optional {else:true,type_id} last; measures are short_side_mm/long_side_mm/area_m2, read from the instance's own top face (Floor/RoofBase/Ceiling) or exterior side face (Wall) - an instance with no such face, or that matches no rule and no else, refuses the whole batch naming it. realign_wall_sketch (its own element_ids array, no other fields on the item) translates a wall's edited profile (Wall.SketchId) back onto its CURRENT location line inside a SketchEditScope; dry_run actually rehearses the move and Cancels the scope rather than reading geometry only. Refused per wall when it is already aligned, or when it moved OFF its sketch's own plane (needs redrawing, not translation) - see horizun_audit_model wall_sketch_drift. rename_level (one element_id, the level; must be the sole operation in its batch) renames a Level with name: Revit also SOLELY renames any plan view whose name exactly matched the level's, and may raise Copy/Monitor alerts - the dry run lists both (views_expected_to_rename is a read; the Copy/Monitor alerts come from an actual rename rehearsed in a rolled-back transaction), and the applied reply reports views renamed before/after plus copy_monitor_alerts captured from the real commit. edit_sketch (one Floor/Ceiling/profiled Wall/Opening, sent alone): loop+loop_index replaces a loop, start->end moves a vertex; id kept."" },
        ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" } },
        ""unique_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Aligned with element_ids; mismatch refuses the batch."" },
        ""expected_category"": { ""type"": ""string"", ""description"": ""Category (name or OST_*) when read. Not enough alone: send unique_ids."" },
        ""expected_type"": { ""type"": ""string"", ""description"": ""Type (name or 'Family: Type') when read. Not enough alone: send unique_ids."" },
        ""identity"": " + IdentityMapSchema + @",
        ""loop"": { ""type"": ""array"", ""items"": { ""type"": ""array"" }, ""description"": ""edit_sketch: new loop points [x,y,z] on the sketch plane; a profiled Wall has a vertical sketch plane."" },
        ""loop_index"": { ""type"": ""integer"", ""minimum"": 0 },
        ""rule"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""object"" },
          ""description"": ""change_type_by_rule only: [{when:{measure_name:{lt|lte|gt|gte|eq: number}}, type_id}, ..., {else:true, type_id}]. See operation's own description."" },
        ""name"": { ""type"": ""string"", ""description"": ""rename_level: the level's new name."" },
        ""vector"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""move/move_preserve_pin/copy: the translation. move_tag_head: the head displacement, in units."" },
        ""point"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""move_tag_head: the absolute head position, in units; exactly one element_id."" },
        ""has_leader"": { ""type"": ""boolean"", ""description"": ""set_tag_leader: IndependentTag.HasLeader."" },
        ""leader_end_condition"": { ""type"": ""string"", ""enum"": [""attached"", ""free""], ""description"": ""set_tag_leader: refused when CanLeaderEndConditionBeAssigned is false for the tag."" },
        ""leader_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""set_tag_leader: free leader end, in units; needs a free end (existing or set in the same operation)."" },
        ""leader_elbow"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""set_tag_leader: leader elbow, in units; needs a leader."" },
        ""leader_visible"": { ""type"": ""boolean"", ""description"": ""set_tag_leader: IsLeaderVisible for the tagged reference; needs a leader."" },
        ""axis_start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""axis_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""angle_degrees"": { ""type"": ""number"" }, ""type_id"": { ""type"": ""integer"" },
        ""plane_origin"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" },
          ""description"": ""mirror: a point of the plane reflected about, in units. The element is mirrored IN PLACE (keeps its id); its point is verified against the reflection, its axes are reported beside the reflected ones, and a hosted instance must keep its host and stand on a face of it or nothing is written."" },
        ""plane_normal"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" },
          ""description"": ""mirror: the plane's normal (direction only)."" },
        ""start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" },
          ""description"": ""set_curve: one end of the new location line, in the units this call declares."" },
        ""end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" },
          ""description"": ""set_curve: the other end. A zero-length line is refused."" },
        ""release_end_joins"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Walls: release and recheck joins."" },
        ""join_end"": { ""type"": ""integer"", ""enum"": [0,1], ""description"": ""wall_join: wall end index."" },
        ""attachment_target_id"": { ""type"": ""integer"", ""description"": ""wall_attach_top/base: roof, floor, ceiling, toposolid or wall target accepted by Revit's IsValidTargetAttachment. column_attach: beam, brace, floor, roof or ceiling."" },
        ""column_end"": { ""type"": ""string"", ""enum"": [""base"", ""top""], ""description"": ""column_attach/detach. Attach cuts the column at the target's minimum intersection."" },
        ""expected_end_elevations"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" }, ""description"": ""wall_attach_top/base: absolute world Z at location-line start and end, in request units. Required; measured from the committed wall side profile."" },
        ""elevation_tolerance_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""maximum"": 100, ""default"": 5, ""description"": ""wall_attach_top/base: tolerance for each committed end elevation."" },
        ""count"": { ""type"": ""integer"", ""minimum"": 2, ""maximum"": 200 },
        ""anchor"": { ""type"": ""string"", ""enum"": [""second"", ""last""], ""description"": ""array: vector/angle reaches the 2nd or the last member."" },
        ""group"": { ""type"": ""boolean"", ""default"": false, ""description"": ""array: keep the associated Revit array."" },
        ""allow"": { ""type"": ""boolean"", ""description"": ""wall_join: allow/disallow join at the specified end; verified after commit."" }
      }, ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: transform elements"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_curtain",
                Command = "horizun_manage_curtain",
                Description =
                    "Curtain wall/system grids: read (u/v lines with offsets and segments, mullions, panels), " +
                    "add_grid_line, remove_grid_line (deletes it and merges its bordering cells; refused if a bordering " +
                    "panel is a door unless accept_panel_merge=true), set_mullions (add/remove on a line's segments) " +
                    "and set_panel_type (panel ids or a point; door/window panel types included). One edit per call, " +
                    "re-read from the committed grid; a disagreement rolls back.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""operation"", ""element_id""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""read"", ""add_grid_line"", ""remove_grid_line"", ""set_mullions"", ""set_panel_type""] },
    ""element_id"": { ""type"": ""integer"", ""description"": ""Curtain wall or curtain system."" },
    ""grid_index"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Curtain system grid; 0 for a wall."" },
    ""direction"": { ""type"": ""string"", ""enum"": [""u"", ""v""], ""description"": ""u horizontal, v vertical."" },
    ""offset"": { ""type"": ""number"", ""description"": ""Walls: v = along the wall from its start, u = above its base."" },
    ""point"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
    ""grid_line_id"": { ""type"": ""integer"" },
    ""mode"": { ""type"": ""string"", ""enum"": [""add"", ""remove""] },
    ""mullion_type_id"": { ""type"": ""integer"" },
    ""segment_index"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Omit for every segment."" },
    ""panel_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""integer"" } },
    ""type_id"": { ""type"": ""integer"" },
    ""accept_panel_merge"": { ""type"": ""boolean"", ""default"": false, ""description"": ""remove_grid_line only: proceed even though a bordering panel is a door, which the cell merge would replace or discard."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }, ""transaction_name"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_slab_shape",
                Command = "horizun_slab_shape",
                Description =
                    "Floor/roof shape editing: read (vertices, creases, flat contour), add_point, add_split_line, modify_subelement " +
                    "(vertex points or one crease by start/end) and reset_shape (erases the shape points). Every " +
                    "touched vertex elevation is re-read after commit. Splits verify crease coverage, including edge-to-edge on a flat floor.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""operation"", ""element_id""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""read"", ""add_point"", ""add_split_line"", ""modify_subelement"", ""reset_shape""] },
    ""element_id"": { ""type"": ""integer"" },
    ""points"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
      ""description"": ""[x, y, offset]; offset is from the unmodified top."" },
    ""start"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } },
    ""end"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } },
    ""offset"": { ""type"": ""number"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }, ""transaction_name"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_parameters",
                Command = "horizun_manage_parameters",
                Description =
                    "Project parameter bindings and Global Parameters. list_bindings and global_list read. create_shared " +
                    "writes the definition to an SPF and binds it; rebind changes categories, Instance/Type or group; " +
                    "remove_binding and global_delete report the values lost. global_create/global_set take a value in " +
                    "display units, a formula, or element parameters to associate. create_project is refused (no API). " +
                    "purge_legacy_marks deletes old v2 ownership marks (personal data). Writes rehearse, need the token, are re-read.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""list_bindings"", ""create_shared"", ""create_project"", ""rebind"", ""remove_binding"", ""global_list"", ""global_create"", ""global_set"", ""global_delete"", ""purge_legacy_marks""] },
    ""name"": { ""type"": ""string"" },
    ""guid"": { ""type"": ""string"", ""description"": ""rebind/remove_binding: wins over name."" },
    ""spf_path"": { ""type"": ""string"", ""description"": ""Absolute; created if missing."" },
    ""spf_group"": { ""type"": ""string"", ""default"": ""Horizun"" },
    ""data_type"": { ""type"": ""string"", ""description"": ""SpecTypeId id or path (String.Text, Length) or legacy name (Text, YesNo, Integer)."" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""OST_ tokens or names; rebind: the final set."" },
    ""binding_kind"": { ""type"": ""string"", ""enum"": [""Instance"", ""Type""] },
    ""group"": { ""type"": ""string"", ""description"": ""PG_ name or group id."" },
    ""allow_vary_between_groups"": { ""type"": ""boolean"", ""default"": false, ""description"": ""A DEFINITION change for the whole project: lets the parameter differ between instances of one group. Off by default; a write to a group member that would need it is refused. When it changes, the reply lists it in definition_changes, re-read, and horizun_undo reverts it."" },
    ""value"": { ""type"": [""number"", ""string"", ""boolean""] },
    ""formula"": { ""type"": ""string"" },
    ""associate"": { ""type"": ""array"", ""maxItems"": 500, ""items"": { ""type"": ""object"", ""required"": [""element_id"", ""parameter""], ""properties"": { ""element_id"": { ""type"": ""integer"" }, ""parameter"": { ""type"": ""string"" } }, ""additionalProperties"": false } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_query_classification",
                Command = "horizun_query_classification",
                Description =
                    "Read-only classification audit. keynote_table / assembly_code: source path and status, entries with " +
                    "parent and use counts (types, placed instances). unused_codes: table codes nothing uses. missing_codes: " +
                    "placed types without a code and codes absent from the table. family_lookup_tables: size tables of " +
                    "loaded families (name, columns, rows), read without opening the family.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""keynote_table"", ""assembly_code"", ""family_lookup_tables"", ""unused_codes"", ""missing_codes""] },
    ""table"": { ""type"": ""string"", ""enum"": [""keynote"", ""assembly_code""], ""default"": ""keynote"" },
    ""family_id"": { ""type"": ""integer"" },
    ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 5000, ""default"": 500 }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_framing",
                Command = "horizun_framing",
                Description =
                    "Light-gauge/drywall framing built from a caller spec (prompt framing-from-detail reads a detail image into it). " +
                    "wall: studs, tracks, kings, jacks, headers, sills, cripples, blocking in a Basic wall's layer; openings never crossed. " +
                    "ceiling: mains, cross, perimeter, hangers to the structure above. dry_run -> confirmation_token -> apply, " +
                    "re-read; read/remove by marker. method 'curtain': the caller's Curtain Wall / Sloped Glazing types " +
                    "instead of members; remove restores the carrier. Spec: docs/TOOLS-EXTENDED.md.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""wall"", ""ceiling"", ""read"", ""remove""] },
    ""element_ids"": { ""type"": ""array"", ""maxItems"": 200, ""items"": { ""type"": ""integer"" }, ""description"": ""Source Basic walls or ceilings."" },
    ""view_id"": { ""type"": ""integer"", ""description"": ""wall/ceiling: every source visible in this view, instead of element_ids."" },
    ""spec"": { ""type"": ""object"", ""description"": ""{wall:{layer,stud,track,openings,blocking}} or {ceiling:{main,cross,perimeter,hanger,drop_mm}}; mm, type ids."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_create_railing",
                Command = "horizun_create_railing",
                Description =
                    "Create a railing on a stair/ramp (host_id; one per side Revit places) or along a sketched open " +
                    "path on a level. Type, host, path and base_offset are re-read after commit; height is the type's. infill: new type with balusters.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""type_id""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""type_id"": { ""type"": ""integer"" },
    ""host_id"": { ""type"": ""integer"" },
    ""placement"": { ""type"": ""string"", ""enum"": [""treads"", ""stringer""], ""default"": ""treads"" },
    ""path"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 200, ""items"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } } },
    ""level_id"": { ""type"": ""integer"" },
    ""base_offset"": { ""type"": ""number"" },
    ""infill"": { ""type"": ""object"", ""properties"": {
      ""new_type_name"": { ""type"": ""string"" }, ""baluster_type_id"": { ""type"": ""integer"" },
      ""spacing"": { ""type"": ""number"" }, ""post_type_id"": { ""type"": ""integer"" } } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }, ""transaction_name"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_view_crop",
                Command = "horizun_view_crop",
                Description =
                    "Crop min/max mm (plan model XY; sections view axes), elements/room, paper offsets, switches. " +
                    "3D read: crop and effective_crop. Section box: model XYZ mm; keep_transform keeps frame. " +
                    "Re-read after commit. HRZ or allow_unmarked_view; templates and controlled crops refused.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation"", ""view_id""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""read"", ""set_crop_box"", ""set_crop_shape"", ""reset_crop_shape"", ""set_crop_from_elements"", ""set_crop_from_room"", ""set_annotation_crop_offsets"", ""crop_active"", ""crop_visible"", ""read_section_box"", ""section_box_active"", ""set_section_box""] },
    ""view_id"": { ""type"": ""integer"" },
    ""min"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }, ""max"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""points"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }, ""description"": ""set_crop_shape: 3-200 [right,up] mm vertices from view origin; repeat first point. reset_crop_shape removes sketch."" },
    ""depth"": { ""type"": ""number"" },
    ""keep_transform"": { ""type"": ""boolean"", ""default"": false },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""room_id"": { ""type"": ""integer"" },
    ""margin"": { ""type"": ""number"", ""default"": 0 },
    ""offsets"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 4, ""maxItems"": 4 },
    ""value"": { ""type"": ""boolean"" },
    ""allow_unmarked_view"": { ""type"": ""boolean"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_sheet_layout",
                Command = "horizun_sheet_layout",
                Description =
                    "First query view_id (get_document_info context), sheet IDs (horizun_query_planimetry). Paper mm. " +
                    "get_viewport_box: box/title/crop, label_offset_mm/label_offset, outside check. move_viewport: center/corner by crop, box or ink. Token binds anchor/delta. " +
                    "auto_layout: rows, margins [l,r,t,b], notes_width; refused if they do not fit. repair_outside: only outside viewports, minimum translation or refusal. layout_grid: columns/title_gap. " +
                    "set_view_scale: keep_viewport_center; omitted scale retains current (SUPUESTO). rename: views/sheets, require_title_fit. set_title: viewport_id or viewport_ids, title_gap/label_offset/line_length; warns off sheet or over a datum. " +
                    "HRZ unless allow_unmarked. Token, re-read, rollback.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""get_viewport_box"", ""move_viewport"", ""auto_layout"", ""layout_grid"", ""set_view_scale"", ""rename"", ""set_title""] },
    ""ink_box"": { ""type"": ""array"", ""minItems"": 4, ""maxItems"": 4, ""items"": { ""type"": ""number"" }, ""description"": ""anchor=ink: pdf_qa ink_from_box_center_mm."" },
    ""label_offset"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" }, ""description"": ""set_title: Viewport.LabelOffset, mm."" },
    ""sheet_id"": { ""type"": ""integer"" }, ""viewport_id"": { ""type"": ""integer"" },
    ""viewport_ids"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""integer"" }, ""description"": ""layout_grid: the grid's order (default: the sheet's viewports by id)."" },
    ""columns"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 12, ""description"": ""layout_grid: columns, filled row by row."" },
    ""gap_x"": { ""type"": ""number"", ""minimum"": 0, ""default"": 10 }, ""gap_y"": { ""type"": ""number"", ""minimum"": 0, ""default"": 10 },
    ""title_gap"": { ""type"": ""number"", ""minimum"": 0, ""default"": 5 },
    ""scale"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 24000, ""description"": ""set_view_scale: N of 1:N. Omit to keep current scale (SUPUESTO)."" },
    ""repair_outside"": { ""type"": ""boolean"", ""description"": ""Move outside viewports. Refuse new or worse overlaps. Default false."" },
    ""line_length"": { ""type"": ""number"", ""minimum"": 0 },
    ""keep_viewport_center"": { ""type"": ""boolean"", ""default"": true },
    ""items"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""object"", ""additionalProperties"": false, ""properties"": {
        ""view_id"": { ""type"": ""integer"" }, ""sheet_id"": { ""type"": ""integer"" }, ""name"": { ""type"": ""string"" }, ""sheet_number"": { ""type"": ""string"" } } } },
    ""require_title_fit"": { ""type"": ""boolean"", ""default"": true },
    ""center"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }, ""corner"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""anchor"": { ""type"": ""string"", ""enum"": [""crop"", ""box"", ""ink""], ""default"": ""crop"" },
    ""margins"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 4, ""maxItems"": 4 },
    ""notes_width"": { ""type"": ""number"", ""default"": 0 }, ""gap"": { ""type"": ""number"", ""default"": 10 },
    ""allow_unmarked"": { ""type"": ""boolean"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_view_visibility",
                Command = "horizun_view_visibility",
                Description =
                    "OWN views only (foreign refused). hide/unhide, set/clear_overrides (element_ids, categories, family_names/type_names) " +
                    "in view_id; set_overrides predicts printing. datum_cleanup (host), link_datum_cleanup (linked grids/levels, hidden filter). " +
                    "Reads: read, explain_overrides, override_report, graphics_report (view_ids). copy_view_graphics copies filters/categories/elements. Re-read after commit.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""read"", ""hide"", ""unhide"", ""set_overrides"", ""clear_overrides"", ""hide_foreign_markers"", ""marker_scale"", ""copy_link_overrides"", ""datum_cleanup"", ""set_scale"", ""explain_overrides"", ""override_report"", ""link_datum_cleanup"", ""graphics_report"", ""copy_view_graphics""] },
    ""include"": { ""type"": ""array"", ""minItems"": 1, ""uniqueItems"": true, ""items"": { ""type"": ""string"", ""enum"": [""filters"", ""categories"", ""elements""] }, ""description"": ""copy_view_graphics groups."" },
    ""view_id"": { ""type"": ""integer"" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""OST_ names."" },
    ""family_names"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""string"" }, ""description"": ""With type_names: ONE family."" },
    ""type_names"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""string"" }, ""description"": ""Types in view_id."" },
    ""overrides"": { ""type"": ""object"", ""description"": ""As horizun_manage_views."" },
    ""view_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""coarser_than"": { ""type"": ""integer"" },
    ""datum_kinds"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""grid"", ""level""] }, ""description"": ""Default both."" },
    ""template_mode"": { ""type"": ""string"", ""enum"": [""refuse"", ""duplicate_view"", ""edit_template""], ""default"": ""refuse"", ""description"": ""link_datum_cleanup under a template: copy or own template."" },
    ""name_separator"": { ""type"": ""boolean"", ""description"": ""link_datum_cleanup: also by datum Name."" },
    ""criteria"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""outside_crop"", ""foreign""] }, ""description"": ""datum_cleanup: any of (default outside_crop; foreign = not ours)."" },
    ""keep_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""maxItems"": 2000, ""description"": ""datum_cleanup: never hidden."" },
    ""scale"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 24000, ""description"": ""set_scale: 50 = 1:50."" },
    ""rehide_foreign_markers"": { ""type"": ""boolean"", ""default"": true, ""description"": ""set_scale: re-hide foreign marks."" },
    ""unprinted"": { ""type"": ""string"", ""enum"": [""refuse"", ""skip"", ""write""], ""default"": ""refuse"", ""description"": ""set_overrides: predicted unprinted."" },
    ""clear_category_overrides"": { ""type"": ""boolean"", ""description"": ""WHOLE category row."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_wall_sweep",
                Command = "horizun_wall_sweep",
                Description =
                    "Wall sweeps and reveals: read lists types, profiles and (element_id) a wall's sweeps; create_type duplicates a type with " +
                    "a profile; place puts type_id sweeps on walls at height from base or top. Slab edges, fascias, gutters: read with host_id " +
                    "lists a floor's or roof's edges and faces as stable references and the types; place_edge makes kind of type_id on edges " +
                    "or faces named. Re-read after commit; a disagreement rolls back.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"", ""description"": ""Required for create_type and place."" },
    ""operation"": { ""type"": ""string"", ""enum"": [""read"", ""create_type"", ""place"", ""place_edge""] },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""element_id"": { ""type"": ""integer"", ""description"": ""read: a wall."" },
    ""host_id"": { ""type"": ""integer"", ""description"": ""read/place_edge: the floor or roof."" },
    ""kind"": { ""type"": ""string"", ""enum"": [""slab_edge"", ""fascia"", ""gutter""], ""description"": ""place_edge: floors host slab edges; roofs fascias and gutters."" },
    ""edge_references"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""string"" }, ""description"": ""place_edge: edge_key values from read (contiguous)."" },
    ""face_references"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""string"" }, ""description"": ""place_edge: face_key values; all their edges."" },
    ""source_type_id"": { ""type"": ""integer"", ""description"": ""create_type: sweep/reveal TYPE to duplicate."" },
    ""new_name"": { ""type"": ""string"", ""description"": ""create_type: new type name."" },
    ""profile_id"": { ""type"": ""integer"", ""description"": ""create_type: loaded Profile type."" },
    ""material_id"": { ""type"": ""integer"", ""description"": ""create_type: material."" },
    ""type_id"": { ""type"": ""integer"", ""description"": ""place: sweep/reveal type. place_edge: edge type of the kind."" },
    ""wall_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""integer"" }, ""description"": ""place: one sweep per wall (not curtain or stack main)."" },
    ""height"": { ""type"": ""number"", ""description"": ""place: distance from base (or top), in units."" },
    ""measured_from"": { ""type"": ""string"", ""enum"": [""base"", ""top""], ""default"": ""base"" },
    ""side"": { ""type"": ""string"", ""enum"": [""exterior"", ""interior""], ""description"": ""place: required."" },
    ""wall_offset"": { ""type"": ""number"", ""description"": ""place: offset from the wall face, in units."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }, ""transaction_name"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_join_geometry",
                Command = "horizun_join_geometry",
                Description =
                    "Join geometry with a stated cut priority, so shared concrete is not counted twice: walls, floors, roofs, structural " +
                    "framing, columns and foundations. read: per pair joined, who cuts and the shared volume (m3). join: " +
                    "cut_order first_cuts (pair [cutting, cut]), priority (column > framing > structural wall > structural floor > " +
                    "foundation > wall > floor > roof; a tie keeps Revit's order) or keep_revit. unjoin, switch_order. With element_ids " +
                    "the pairs are those already joined or sharing volume. Re-read after commit: joined, who cuts, shared volume 0; " +
                    "groups, nested and non-touching pairs are refused. corners: open L/T wall corners within tolerance; " +
                    "fix_corners allows the join and moves the end to the crossing, re-read joined. auto_priority: a whole level, volume before/after. " +
                    "excavate/unexcavate: a toposolid cut by an element (Revit 2025+), really rehearsed.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"", ""description"": ""Required except for read."" },
    ""operation"": { ""type"": ""string"", ""enum"": [""read"", ""join"", ""unjoin"", ""switch_order"", ""corners"", ""fix_corners"", ""auto_priority"", ""excavate"", ""unexcavate""] },
    ""level_id"": { ""type"": ""integer"", ""minimum"": 1, ""description"": ""auto_priority: join every overlapping pair of this level by priority."" },
    ""tolerance"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""corners: max gap, in units (50 mm)."" },
    ""pairs"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""integer"" } }, ""description"": ""[[a, b], ...]; with first_cuts a cuts b; excavate: [toposolid, excavator]."" },
    ""element_ids"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 300, ""items"": { ""type"": ""integer"" }, ""description"": ""read/join: find the pairs among these; corners: the walls."" },
    ""cut_order"": { ""type"": ""string"", ""enum"": [""first_cuts"", ""priority"", ""keep_revit""], ""description"": ""join: default first_cuts with pairs, priority with element_ids."" },
    ""priority"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""enum"": [""structural_column"", ""structural_framing"", ""wall_structural"", ""floor_structural"", ""structural_foundation"", ""wall"", ""floor"", ""roof""] }, ""description"": ""Required for auto_priority: highest first; explicitly rank every class on the level. join with cut_order=priority completes omitted classes in default order."" },
    ""verification_mode"": { ""type"": ""string"", ""enum"": [""atomic"", ""per_pair""], ""default"": ""atomic"", ""description"": ""join/auto_priority: per_pair retains verified pairs when another pair cannot be measured; overall result is partial."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }, ""transaction_name"": { ""type"": ""string"" }
  }, ""allOf"": [
    { ""if"": { ""properties"": { ""operation"": { ""enum"": [""read"", ""join""] } }, ""required"": [""operation""] }, ""then"": { ""oneOf"": [{ ""required"": [""pairs""] }, { ""required"": [""element_ids""] }] } },
    { ""if"": { ""properties"": { ""operation"": { ""enum"": [""unjoin"", ""switch_order"", ""excavate"", ""unexcavate""] } }, ""required"": [""operation""] }, ""then"": { ""required"": [""pairs""] } },
    { ""if"": { ""properties"": { ""operation"": { ""enum"": [""auto_priority""] } }, ""required"": [""operation""] }, ""then"": { ""required"": [""level_id"", ""priority""], ""properties"": { ""priority"": { ""minItems"": 1 } } } },
    { ""if"": { ""properties"": { ""operation"": { ""enum"": [""corners"", ""fix_corners""] } }, ""required"": [""operation""] }, ""then"": { ""required"": [""element_ids""] } },
    { ""if"": { ""properties"": { ""operation"": { ""enum"": [""join"", ""unjoin"", ""switch_order"", ""fix_corners"", ""auto_priority"", ""excavate"", ""unexcavate""] } }, ""required"": [""operation""] }, ""then"": { ""required"": [""target_document""] } }
  ], ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_room_finishes",
                Command = "horizun_room_finishes",
                Description =
                    "Finishes by room. floors: one floor per room, contour = the room's boundary at the wall FINISH faces (holes " +
                    "kept), type from the room's use (type_by_use) or default_type_id, copy_parameters room to floor. baseboards: a " +
                    "sweep of type_id on each wall face bounding the room, on the room's side, once per face. Re-read: type, area " +
                    "vs the loops (0.5 %), no volume shared with the walls, host and side of each sweep.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""operation"", ""room_ids""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""floors"", ""baseboards""] },
    ""room_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""integer"" } },
    ""type_by_use"": { ""type"": ""object"", ""properties"": { ""parameter"": { ""type"": ""string"" }, ""map"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""integer"" } } }, ""description"": ""floors: {parameter: room parameter, map: {value: floor_type_id}}."" },
    ""default_type_id"": { ""type"": ""integer"", ""description"": ""floors: for rooms whose use is not in the map."" },
    ""offset_mm"": { ""type"": ""number"", ""description"": ""floors: height offset from the room's level."" },
    ""boundary_gap_tolerance_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 1, ""description"": ""floors: order boundary segments and snap endpoint gaps up to this tolerance. Larger measured gaps reject that room; other rooms continue."" },
    ""copy_parameters"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""string"" }, ""description"": ""floors: {room parameter: floor text parameter}."" },
    ""type_id"": { ""type"": ""integer"", ""description"": ""baseboards: the wall sweep type."" },
    ""height_mm"": { ""type"": ""number"", ""default"": 0, ""description"": ""baseboards: from the wall's base."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }, ""transaction_name"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_wall_finish",
                Command = "horizun_wall_finish",
                Description =
                    "ARQ walls as finish layers on the EST core of link_instance_id, with no overlap. match_core (read-only): the core " +
                    "each ARQ wall stands on (parallel 1 deg, within max_offset_mm, overlap >= min_overlap) or why none. " +
                    "create_finish_layers: finish walls of exterior_type_id / interior_type_id on the core faces over the overlap; " +
                    "original_wall keep | delete_if_unhosted (said); walls hosting doors are skipped. Re-read: type, offset, " +
                    "length, no volume shared with the core. convert_to_finish: change the EXISTING wall to finish_type_id of thickness_mm, " +
                    "on side exterior/interior of the linked core; preserve IDs/hosts, verify sampled aperture clearance and volume; incompatible walls skipped.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"", ""description"": ""Required for writes."" },
    ""operation"": { ""type"": ""string"", ""enum"": [""match_core"", ""create_finish_layers"", ""convert_to_finish"", ""paint_faces""] },
    ""pairs"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""description"": ""paint_faces: stable face references from selection_context; paint/remove re-read after commit."", ""items"": { ""type"": ""object"", ""required"": [""element_id"", ""face_reference"", ""action""], ""properties"": {
      ""element_id"": { ""type"": ""integer"", ""minimum"": 1 }, ""face_reference"": { ""type"": ""string"", ""minLength"": 1 }, ""action"": { ""type"": ""string"", ""enum"": [""paint"", ""remove""] }, ""material_id"": { ""type"": ""integer"", ""minimum"": 1 }
    }, ""additionalProperties"": false } },
    ""wall_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 300, ""items"": { ""type"": ""integer"" }, ""description"": ""ARQ walls of this document."" },
    ""link_instance_id"": { ""type"": ""integer"", ""description"": ""The EST link holding the cores."" },
    ""max_offset_mm"": { ""type"": ""number"", ""default"": 150 },
    ""min_overlap"": { ""type"": ""number"", ""default"": 0.6, ""description"": ""Share of the ARQ wall's length the core must cover."" },
    ""exterior_type_id"": { ""type"": ""integer"" }, ""interior_type_id"": { ""type"": ""integer"" },
    ""finish_type_id"": { ""type"": ""integer"", ""minimum"": 1 },
    ""thickness_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""maximum"": 100, ""description"": ""convert_to_finish: existing single-layer type width; tolerance 0.1 mm."" },
    ""side"": { ""type"": ""string"", ""enum"": [""exterior"", ""interior""] },
    ""original_wall"": { ""type"": ""string"", ""enum"": [""keep"", ""delete_if_unhosted""] },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }, ""transaction_name"": { ""type"": ""string"" }
  }, ""allOf"": [{ ""if"": { ""properties"": { ""operation"": { ""const"": ""paint_faces"" } } }, ""then"": { ""required"": [""target_document"", ""pairs""] }, ""else"": { ""required"": [""wall_ids"", ""link_instance_id""] } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""convert_to_finish"" } } }, ""then"": { ""required"": [""target_document"", ""finish_type_id"", ""thickness_mm"", ""side""] } }], ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_audit_support",
                Command = "horizun_audit_support",
                Description =
                    "Read-only. mode floating: elements of categories with nothing under them (box-level, host and links); " +
                    "loose_beam_ends: framing ends in no column/wall/beam/foundation; coverage_gaps: rooms not covered by a floor " +
                    "top face (sampled); model_offset: same-named grids against a link - offsets, p95, rotation, translation. " +
                    "slab_supports: structural elements with no wall, beam, column or slab below within tolerance. What cannot be read is in unreadable.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""mode""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""mode"": { ""type"": ""string"", ""enum"": [""floating"", ""slab_supports"", ""loose_beam_ends"", ""coverage_gaps"", ""model_offset""] },
    ""categories"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 10, ""items"": { ""type"": ""string"" }, ""description"": ""floating: OST_ names."" },
    ""level_id"": { ""type"": ""integer"" }, ""room_ids"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""integer"" } },
    ""tolerance_mm"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 5000 }, ""above_tolerance_mm"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 5000, ""description"": ""floating: count support hanging above within this."" },
    ""grid_mm"": { ""type"": ""number"", ""minimum"": 100, ""maximum"": 5000, ""default"": 500 }, ""include_links"": { ""type"": ""boolean"", ""default"": true },
    ""link_instance_id"": { ""type"": ""integer"", ""description"": ""model_offset."" }
  }, ""allOf"": [
    { ""if"": { ""properties"": { ""mode"": { ""enum"": [""floating""] } }, ""required"": [""mode""] }, ""then"": { ""required"": [""categories""] } },
    { ""if"": { ""properties"": { ""mode"": { ""enum"": [""coverage_gaps""] } }, ""required"": [""mode""] }, ""then"": { ""anyOf"": [{ ""required"": [""level_id""] }, { ""required"": [""room_ids""] }] } },
    { ""if"": { ""properties"": { ""mode"": { ""enum"": [""model_offset""] } }, ""required"": [""mode""] }, ""then"": { ""required"": [""link_instance_id""] } }
  ], ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_planimetry_ops",
                Command = "horizun_planimetry_ops",
                Description =
                    "Edit datum extents, viewports, overlaps, grid bubbles, legends and notes. " +
                    "Bubbles use declared circular envelopes; oversized viewports are refused. " +
                    "Notes and leaders: see docs/NOTES-LEADERS.md. Re-read after commit.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""datum_extents"", ""fit_viewport"", ""resolve_overlaps"", ""legend_from_families"", ""stagger_grid_bubbles"", ""move_note"", ""note_from_model"", ""clone_tag"", ""arrange_column"", ""set_leader_end"", ""reseat_leaders"", ""required_notes"", ""replace_text"", ""sync_sheet_references""] },
    ""leader_index"": { ""type"": ""integer"" }, ""end_mm"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }, ""max_shift_mm"": { ""type"": ""number"" },
    ""old"": { ""type"": ""string"" }, ""new"": { ""type"": ""string"" }, ""pattern"": { ""type"": ""string"" }, ""map"": { ""type"": ""object"" },
    ""note_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""integer"" } },
    ""delta_mm"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } },
    ""frame"": { ""type"": ""string"", ""enum"": [""view"", ""sheet""] },
    ""keep_leader_ends"": { ""type"": ""boolean"" },
    ""elbow"": { ""type"": ""string"", ""enum"": [""keep"", ""horizontal"", ""L""] },
    ""side"": { ""type"": ""string"", ""enum"": [""keep"", ""auto"", ""left"", ""right""] },
    ""width_mm"": { ""type"": ""number"" },
    ""text"": { ""type"": ""string"" },
    ""fit_crop_margin_mm"": { ""type"": ""number"" },
    ""notes"": { ""type"": ""array"", ""maxItems"": 200, ""items"": { ""type"": ""object"", ""required"": [""element_id"", ""end_mm"", ""note_mm""], ""properties"": { ""element_id"": { ""type"": ""integer"" }, ""link_instance_id"": { ""type"": ""integer"" }, ""end_mm"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }, ""note_mm"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } } }, ""additionalProperties"": false } },
    ""source"": { ""type"": ""string"", ""enum"": [""layers"", ""material"", ""type_name"", ""schedule""] },
    ""schedule_id"": { ""type"": ""integer"", ""description"": ""Native schedule whose complete body supplies the note text."" },
    ""text_note_type_id"": { ""type"": ""integer"" },
    ""model_tag_id"": { ""type"": ""integer"" },
    ""targets"": { ""type"": ""array"", ""maxItems"": 200, ""items"": { ""type"": ""object"", ""required"": [""element_id"", ""head_mm""], ""properties"": { ""element_id"": { ""type"": ""integer"" }, ""head_mm"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } } }, ""additionalProperties"": false } },
    ""sheet_horizontal"": { ""type"": ""boolean"" },
    ""grid_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""integer"" } },
    ""ends"": { ""type"": ""string"", ""enum"": [""end0"", ""end1"", ""both""], ""default"": ""both"" },
    ""bubble_diameter_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""maximum"": 200, ""default"": 8, ""description"": ""Declared printed diameter; circular envelope measurement, not rendered glyph size."" },
    ""max_offset_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""maximum"": 200, ""default"": 200 },
    ""view_ids"": { ""type"": ""array"", ""maxItems"": 50, ""items"": { ""type"": ""integer"" } }, ""datum_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""bubbles"": { ""type"": ""string"", ""enum"": [""end0"", ""end1"", ""both"", ""none""] },
    ""viewport_ids"": { ""type"": ""array"", ""maxItems"": 50, ""items"": { ""type"": ""integer"" } }, ""view_id"": { ""type"": ""integer"" },
    ""margin_mm"": { ""type"": ""number"" }, ""gap_mm"": { ""type"": ""number"" }, ""spacing_mm"": { ""type"": ""number"" },
    ""source_legend_id"": { ""type"": ""integer"" }, ""name"": { ""type"": ""string"" }, ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""label"": { ""type"": ""string"", ""enum"": [""family_and_type"", ""type_name"", ""type_mark""] },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }, ""transaction_name"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_list_schedules",
                Command = "horizun_list_schedules",
                Description = "List native schedules with fields, linked-file setting, itemization, dimensions and host/link coverage. Read-only. Titleblock revision schedules are labelled and excludable; their count is always reported.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""default"": 200 },
    ""include_revision_schedules"": { ""type"": ""boolean"", ""default"": true, ""description"": ""false hides titleblock revision schedules from rows; revision_schedules_in_document still reports how many exist, so the count never silently shrinks."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_views",
                Command = "horizun_manage_views",
                Description =
                    "Create and manage views, sheets, filters and graphics in a verified batch. First query view_id and level_id with get_document_info operation=context. Template-controlled graphics are refused.",
                InputSchema = JObject.Parse(@"{
  ""description"": ""Full details. Create sheets and place views: First query view_id and level_id with get_document_info operation=context; never invent IDs. Create and compose documentation in one atomic batch: floor/ceiling/structural/area plans, sections, elevations, callouts, drafting and isometric 3D views, view duplicates (including dependents via AsDependent), template assignment, phases, scope boxes, view ranges, rectangular and annotation crops, sheets, placeholder sheets and their conversion, sheet duplication with or without content, viewports and schedule instances, viewport type changes and cross-sheet viewport alignment against a still anchor. ALSO GRAPHIC CONTROL: create and apply view filters with typed rules, override lines/surfaces/transparency/halftone, colour every element by a parameter value with a deterministic palette and a returned legend, hide or isolate elements (temporary view mode by default, and the reply says which), reset the temporary mode, category/subcategory visibility and overrides; edit filter rules, reorder and enable filters, explain which override wins for an element, create templates and set what they govern; list/create/update/delete named PrintManager sheet sets (view_ids replaces membership on update), restoring the print manager's own current selection afterward. A view whose TEMPLATE governs V/G is REFUSED with the template named rather than accepted and silently ignored, which is what Revit does. Sheet numbers are checked unique against the document AND the batch before anything runs. Actions may assign a key and later actions can reference that created object in the same transaction. Dry-run validates the dependency graph; apply re-reads every created or changed object after commit - view ranges, crops, phases and alignments are verified by re-reading the exact values requested. up: create_perspective/create_3d: eye=start, target=end (in units); up defaults to Z; fan=N (perspective only) views turned 360/N about Z. create_3d refuses eye/target/perspective spellings; camera and style are re-read. display: set_graphic_display: style, shadows, ambient_shadows, smooth_edges and background (type sky|gradient|none; gradient colors #RRGGBB). Unavailable options are refused in the rehearsal. move_filter_ids: order_filters, instead of filter_ids: only these filters, in this order, rearranged among the slots they already hold; every other filter on the view (e.g. ones a duplicated view inherited) keeps its place. hide_marker_coarser_than: create_section: Hide at scales coarser than; default 1 in a workshared model (mark kept out of others' views), 0 = Revit default. A callout's mark shows only in its parent. Reply detail_view: crop, marker_ids. set_view_properties: view_scale, detail_level, discipline, display_style, phase_id of existing views (view_id, view_key or view_ids); a template that controls one refuses naming it; allow_unmarked_view lifts the ownership gate. create_selection_filter: name + element_ids, optionally on view_id with overrides/visible. Both rehearsed in Revit."",
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" }, ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": {
      ""type"": ""object"", ""required"": [""operation""], ""properties"": {
        " + IdentityFields("first of view_id, sheet_id, viewport_id") + @"""operation"": { ""type"": ""string"", ""enum"": [""create_floor_plan"", ""create_ceiling_plan"", ""create_structural_plan"", ""create_area_plan"", ""create_3d"", ""create_drafting"", ""create_section"", ""create_elevation"", ""create_callout"", ""duplicate_view"", ""apply_template"", ""set_phase"", ""assign_scope_box"", ""set_view_range"", ""set_crop"", ""set_annotation_crop"", ""create_sheet"", ""create_placeholder_sheet"", ""convert_placeholder_sheet"", ""duplicate_sheet"", ""place_view"", ""place_schedule"", ""set_viewport_type"", ""align_viewports"", ""create_filter"", ""apply_filter"", ""color_by_value"", ""set_element_overrides"", ""hide_elements"", ""isolate_elements"", ""reset_temporary"", ""set_category_visibility"", ""create_legend"", ""place_legend_component"", ""edit_filter"", ""order_filters"", ""explain_graphics"", ""create_template"", ""set_template_controls"", ""sheet_set_list"", ""sheet_set_create"", ""sheet_set_update"", ""sheet_set_delete"", ""renumber_sheets"", ""create_perspective"", ""set_sun_study"", ""set_graphic_display"", ""hide_outside_crop"", ""set_starting_view"", ""extend_grids_3d"", ""create_room_isometric"", ""set_view_properties"", ""create_selection_filter""],
          ""description"": ""sheet_set_list reads every PrintManager.ViewSheetSetting sheet set (id, name, is_automatic, member views/sheets); sheet_set_create needs name and view_ids; sheet_set_update needs sheet_set_id and name and/or view_ids (view_ids REPLACES membership); sheet_set_delete needs sheet_set_id. The print manager's own current selection is saved and restored around each call."" },
        ""key"": { ""type"": ""string"", ""description"": ""Unique alias for an object this action creates."" },
        ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""GRAPHIC CONTROL. Categories a filter applies to. Prefer the BuiltInCategory name (OST_Walls): a display name depends on the Revit language and would break on another machine."" },
        ""rules"": { ""type"": ""array"", ""maxItems"": 50, ""items"": { ""type"": ""object"", ""required"": [""parameter""], ""properties"": {
          ""parameter"": { ""type"": ""string"", ""description"": ""BuiltInParameter name or parameter label. It must be FILTERABLE for these categories; the refusal lists the ones that are."" },
          ""operator"": { ""type"": ""string"", ""enum"": [""equals"", ""not_equals"", ""contains"", ""not_contains"", ""begins_with"", ""ends_with"", ""greater"", ""greater_or_equal"", ""less"", ""less_or_equal"", ""has_value"", ""has_no_value""], ""default"": ""equals"" },
          ""value_type"": { ""type"": ""string"", ""enum"": [""string"", ""number"", ""integer""], ""default"": ""string"", ""description"": ""Match the parameter: a number against a string matches nothing, with no error."" },
          ""value"": {}, ""tolerance"": { ""type"": ""number"", ""description"": ""number comparisons only. Default: one millimetre in internal feet."" }
        } } },
        ""match"": { ""type"": ""string"", ""enum"": [""all"", ""any""], ""default"": ""all"", ""description"": ""How several rules combine."" },
        ""filter_id"": { ""type"": ""integer"" }, ""filter_key"": { ""type"": ""string"" }, ""filter_name"": { ""type"": ""string"" },
        ""filter_prefix"": { ""type"": ""string"", ""description"": ""color_by_value: prefix of the generated filter names."" },
        ""parameter"": { ""type"": ""string"", ""description"": ""color_by_value: parameter whose values become colours."" },
        ""max_values"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 60, ""default"": 24, ""description"": ""color_by_value: how many values get a colour."" },
        ""empty_color"": { ""type"": ""string"", ""description"": ""color_by_value: no-value #RRGGBB, default #AAAAAA."" },
        ""overrides"": { ""type"": ""object"", ""properties"": {
          ""line_color"": { ""type"": ""string"", ""description"": ""#RRGGBB. Sets both projection and cut line colour."" },
          ""projection_line_color"": { ""type"": ""string"", ""description"": ""#RRGGBB. Sets projection line colour."" },
          ""cut_line_color"": { ""type"": ""string"" }, ""surface_color"": { ""type"": ""string"" }, ""cut_color"": { ""type"": ""string"" },
          ""surface_pattern"": { ""type"": ""string"", ""description"": ""Fill pattern of surface_color: solid (default), exact name or id. No solid drafting fill in the document: refused, not a colour on nothing. Pattern and visibility re-read."" },
          ""cut_pattern"": { ""type"": ""string"", ""description"": ""Same for cut_color."" },
          ""transparency"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 100 },
          ""halftone"": { ""type"": ""boolean"" },
          ""line_weight"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 16 },
          ""line_pattern"": { ""type"": ""string"", ""description"": ""Line pattern name, or Solid."" }
        }, ""description"": ""Only the fields you set are written, and only those are re-read afterwards."" },
        ""visible"": { ""type"": ""boolean"", ""description"": ""apply_filter/create_selection_filter: show or hide the matching elements."" },
        ""enabled"": { ""type"": ""boolean"", ""description"": ""apply_filter: the filter's Enable Filter flag."" },
        ""filter_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""order_filters: ALL the view's filters, top first."" },
        ""move_filter_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""order_filters: reorder these filters within their existing slots."" },
        ""subcategory"": { ""type"": ""string"" },
        ""parameters"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""set_template_controls: BuiltInParameter names or labels."" },
        ""controlled"": { ""type"": ""boolean"" },
        ""element_ids"": { ""type"": ""array"", ""maxItems"": 5000, ""items"": { ""type"": ""integer"" } },
        ""permanent"": { ""type"": ""boolean"", ""default"": false, ""description"": ""hide_elements: false is the TEMPORARY view mode, which does not survive closing the document and is not what a sheet prints. true stores the hide on the view."" },
        ""category"": { ""type"": ""string"", ""description"": ""set_category_visibility: BuiltInCategory name."" },
        ""component_type_id"": { ""type"": ""integer"", ""description"": ""place_legend_component: the TYPE the component draws (a wall type, a door type). A legend component draws a type, never an instance."" },
        ""detail_level"": { ""type"": ""string"", ""enum"": [""coarse"", ""medium"", ""fine""], ""description"": ""place_legend_component: how the component is drawn; set_view_properties: the views' detail level."" },
        ""hidden"": { ""type"": ""boolean"", ""description"": ""set_category_visibility: true hides it."" },
        ""name"": { ""type"": ""string"" }, ""number"": { ""type"": ""string"" },
        ""renumber"": { ""type"": ""object"", ""description"": ""renumber_sheets: old sheet number -> new. Swaps allowed; any collision refused before writing."" },
        ""up"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""create_perspective/create_3d: eye=start, target=end; up defaults to Z."" },
        ""room_id"": { ""type"": ""integer"", ""description"": ""create_room_isometric: a placed, enclosed Room. The 3D view is named name (default: ISO number name), its section box is the room plus margin, and it is re-read after the commit."" },
        ""direction"": { ""type"": ""string"", ""enum"": [""ne"", ""nw"", ""se"", ""sw""], ""default"": ""se"", ""description"": ""create_room_isometric: the corner the viewer stands in."" },
        ""display_style"": { ""type"": ""string"", ""description"": ""create_3d/create_perspective/set_view_properties: shaded, shaded_with_edges, hlr, wireframe, consistent_colors, realistic. Re-read; a template-controlled view refuses."" },
        ""fan"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 36 },
        ""display"": { ""type"": ""object"", ""description"": ""set_graphic_display: style, shadows, edges and background."" },
        ""discipline"": { ""type"": ""string"", ""enum"": [""architectural"", ""structural"", ""mechanical"", ""electrical"", ""plumbing"", ""coordination""] },
        ""allow_unmarked_view"": { ""type"": ""boolean"" },
        ""sun"": { ""type"": ""object"", ""description"": ""set_sun_study: {type:still|single_day|multi_day, start, end: ISO-8601+offset, lat, lon: project site, degrees}"" },
        ""sheet_set_id"": { ""type"": ""integer"", ""description"": ""sheet_set_update/sheet_set_delete: an existing saved sheet set."" },
        ""grid_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""integer"" }, ""description"": ""extend_grids_3d: grids to make span every level (Maximize 3D Extents); omitted: all grids. Extent re-read against the levels."" },
        ""view_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""integer"" }, ""description"": ""sheet_set_create/sheet_set_update: the views/sheets the set holds. On update this REPLACES the whole membership. set_view_properties: the views to change."" },
        ""level_id"": { ""type"": ""integer"" }, ""view_family_type_id"": { ""type"": ""integer"" }, ""plan_view_id"": { ""type"": ""integer"" },
        ""source_view_id"": { ""type"": ""integer"" }, ""source_view_key"": { ""type"": ""string"" },
        ""duplicate_option"": { ""type"": ""string"", ""enum"": [""Duplicate"", ""WithDetailing"", ""AsDependent""], ""default"": ""Duplicate"" },
        ""view_id"": { ""type"": ""integer"", ""description"": ""set_starting_view: the view or sheet the document opens on (Manage > Starting View); the rehearsal reads the current one, apply re-reads it."" }, ""view_key"": { ""type"": ""string"" },
        ""template_view_id"": { ""type"": ""integer"", ""description"": ""-1 removes the template; also applied by create_callout/create_section."" }, ""title_block_type_id"": { ""type"": ""integer"", ""description"": ""create_sheet: omit for no title block. Rehearsed and re-read."" },
        ""hide_marker_coarser_than"": { ""type"": ""integer"", ""description"": ""create_section: hide marker at coarser scales; see full contract."" },
        ""sheet_id"": { ""type"": ""integer"" }, ""sheet_key"": { ""type"": ""string"" },
        ""schedule_id"": { ""type"": ""integer"" }, ""schedule_key"": { ""type"": ""string"" },
        ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""segment_index"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""place_schedule: segment of a split schedule."" },
        ""detail_number"": { ""type"": ""string"", ""description"": ""place_view: refused if used on the sheet."" },
        ""start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""area_scheme_id"": { ""type"": ""integer"", ""description"": ""create_area_plan: the AreaScheme the plan belongs to."" },
        ""area_scheme_name"": { ""type"": ""string"", ""description"": ""create_area_plan: scheme by name; a refusal lists them."" },
        ""parent_view_id"": { ""type"": ""integer"", ""description"": ""create_callout: the view the callout is drawn in."" },
        ""parent_view_key"": { ""type"": ""string"" },
        ""phase_id"": { ""type"": ""integer"", ""description"": ""set_phase/set_view_properties: the Phase the view shows."" },
        ""scope_box_id"": { ""type"": ""integer"", ""description"": ""assign_scope_box: a Volume of Interest element; -1 clears it."" },
        ""margin"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""hide_outside_crop: permanently hide markers, base points and scope boxes lying entirely outside the crop grown by this, in units."" },
        ""cut_level_id"": { ""type"": ""integer"" }, ""cut_offset"": { ""type"": ""number"" },
        ""top_level_id"": { ""type"": ""integer"" }, ""top_offset"": { ""type"": ""number"" },
        ""bottom_level_id"": { ""type"": ""integer"" }, ""bottom_offset"": { ""type"": ""number"" },
        ""view_depth_level_id"": { ""type"": ""integer"" }, ""view_depth_offset"": { ""type"": ""number"" },
        ""box"": { ""type"": ""array"", ""minItems"": 4, ""maxItems"": 4, ""items"": { ""type"": ""number"" }, ""description"": ""set_crop: [min_x, min_y, max_x, max_y] in the view's own plane, in units."" },
        ""active"": { ""type"": ""boolean"", ""description"": ""set_annotation_crop: on or off."" },
        ""annotation_offset"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""set_annotation_crop: uniform offset applied to all four annotation crop edges, in units MEASURED ON PAPER (Revit's convention): 5 mm is 5 mm on the sheet at any scale, not 5 mm of model."" },
        ""source_sheet_id"": { ""type"": ""integer"", ""description"": ""duplicate_sheet: the sheet to copy."" },
        ""with_content"": { ""type"": ""boolean"", ""default"": false, ""description"": ""duplicate_sheet: also duplicate every placed view WithDetailing and place each copy at the same paper position with the same viewport type. Refused when the source carries placed schedules - one schedule cannot live on two sheets."" },
        ""viewport_id"": { ""type"": ""integer"", ""description"": ""set_viewport_type: the viewport to retype."" },
        ""viewport_type_id"": { ""type"": ""integer"", ""description"": ""set_viewport_type: must be in the viewport's own GetValidTypes."" },
        ""viewport_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""integer"" }, ""description"": ""align_viewports: the viewports that MOVE."" },
        ""anchor_viewport_id"": { ""type"": ""integer"", ""description"": ""align_viewports: the viewport that holds still; must not appear in viewport_ids."" },
        ""mode"": { ""type"": ""string"", ""enum"": [""center"", ""center_x"", ""center_y"", ""left"", ""right"", ""top"", ""bottom""], ""description"": ""align_viewports: which edge or center aligns to the anchor."" },
        ""bottom_offset"": { ""type"": ""number"", ""default"": -1000 }, ""top_offset"": { ""type"": ""number"", ""default"": 3000 },
        ""depth"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 5000, ""description"": ""create_section box depth; create_elevation: Far Clip Offset, re-read (omitted: Revit's 3048 mm)."" },
        ""elevation_index"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 3, ""default"": 0 },
        ""rotation"": { ""type"": ""number"", ""exclusiveMinimum"": -360, ""exclusiveMaximum"": 360, ""description"": ""create_elevation: rotate the elevation marker about the vertical axis through its point, in degrees CCW - how a room elevation faces its principal wall. Verified by re-reading the view direction after commit."" },
        ""marker_scale"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 24000, ""default"": 100 },
        ""view_scale"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 24000, ""description"": ""Explicit scale for every operation whose result has a drawing scale: create_floor_plan, create_ceiling_plan, create_structural_plan, create_area_plan, create_drafting, create_3d (isometric), create_callout, create_section, create_elevation, duplicate_view, apply_template and set_view_properties. The value is checked with View.IsValidViewScale before any transaction opens. A view whose template controls View Scale refuses the batch by name instead of letting the template silently win; a sheet, schedule or perspective refuses because it has no scale. The scale is re-read after commit and reported per row as view_scale / view_scale_verified; on any other operation the argument is refused rather than ignored."" }
      }, ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: manage views and sheets"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_get_schedule_data",
                Command = "horizun_get_schedule_data",
                Description =
                    "Read the displayed cells of one native schedule, including rows produced from RVT links. Returns bounded " +
                    "header and body matrices plus exact dimensions and federated coverage; truncation is explicit. Read-only.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""schedule_id""],
  ""properties"": {
    ""schedule_id"": { ""type"": ""integer"" },
    ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""default"": 200 },
    ""max_columns"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100, ""default"": 50 }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_export",
                Command = "horizun_export",
                Description =
                    "Export verified deliverables from the active document: combined PDF, DWG/DGN/DWFX view sets, gbXML, family .rfa, IFC, " +
                    "Navisworks NWC, FBX of 3D views, one-view image, a schedule as CSV, or a COBie 2.4 workbook (gaps as findings). " +
                    "Dry-run validates paths, exporters, views and overwrite policy; apply needs confirmation and idempotency, then " +
                    "re-reads the files actually produced. pdf_qa reads a PDF back: fonts, ink per viewport, printed gray, lines per note, before/after. " +
                    "DWG evidence lists xref and raster companions with header checks; it does not prove dimensions, annotations or layouts inside a DWG. " +
                    "read_only, documentation and safe_write export pdf and one-view image only. mesh_json exports model triangles.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""format"", ""output_path""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""format"": { ""type"": ""string"", ""enum"": [""pdf"", ""pdf_qa"", ""dwg"", ""dgn"", ""dwfx"", ""ifc"", ""nwc"", ""fbx"", ""image"", ""schedule_csv"", ""dwg_layers"", ""ifc_setup"", ""gbxml"", ""rfa"", ""cobie"", ""mesh_json""] },
    ""categories"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""uniqueItems"": true, ""items"": { ""type"": ""string"" }, ""description"": ""mesh_json categories."" },
    ""skip_directshape"": { ""type"": ""boolean"", ""default"": true },
    ""include_links"": { ""type"": ""boolean"", ""default"": false, ""description"": ""mesh_json links."" },
    ""output_path"": { ""type"": ""string"", ""description"": ""Absolute target file with an extension matching format (.pdf/.dwg/.dgn/.dwfx/.ifc/.nwc/.fbx/.xml; an image extension; .csv/.txt; cobie .xlsx); rfa: a folder; pdf with pdf_combine=false: a folder (files take its name as stem unless pdf_name_pattern; see require_existing_folder); pdf_qa: an existing .pdf, read and never written. Image export may create a family of names, all of which are reported."" },
    ""require_existing_folder"": { ""type"": ""boolean"", ""default"": false, ""description"": ""PDF/image/DWG/IFC/FBX only. Confirmed apply creates missing local parent directories unless true."" },
    ""view_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""PDF, DWG/DGN/DWFX (a file each): one or more printable views/sheets. Image: exactly one. FBX: one or more 3D views. NWC view scope: exactly one."" },
    ""schedule_id"": { ""type"": ""integer"" },
    ""image_pixels"": { ""type"": ""integer"", ""minimum"": 128, ""maximum"": 8192, ""default"": 2048 },
    ""pdf_combine"": { ""type"": ""boolean"", ""default"": true, ""description"": ""PDF: true produces one combined file; false produces deterministic stem-ordinal-viewId.pdf files. All PDFs are reopened and page counts checked."" },
    ""pdf_name_pattern"": { ""type"": ""string"", ""description"": ""PDF: name files from {sheet_number} {sheet_name} {view_name} {date} {version} {index}; folder from output_path."" },
    ""dialog_policy"": { ""type"": ""string"", ""enum"": [""cancel"", ""continue_known""], ""default"": ""cancel"", ""description"": ""How the export answers modal dialogs Revit raises while it runs, single-file formats only. cancel (default): nobody is at the keyboard, so the dialog is cancelled - and now NAMED in the reply and in the failure. continue_known: dialogs in a CLOSED catalog (today: Update Resources, e.g. an out-of-date cloud keynote file) are acknowledged and the export continues WITHOUT reloading; any other dialog, and anything that offers a reload, is still cancelled. Reload is never automatic. The reply carries dialog_policy.decisions (dialog id, message, action, result code, reason, whether the button was verified in Revit)."" },
    ""pdf_version"": { ""anyOf"": [{ ""type"": ""integer"", ""minimum"": 0 }, { ""type"": ""string"", ""enum"": [""auto""] }], ""default"": 1, ""description"": ""{version} of pdf_name_pattern. \""auto\"" = one more than the highest version of the pattern already in the folder."" },
    ""pdf_parts"": { ""type"": ""integer"", ""minimum"": 2, ""maximum"": 50, ""description"": ""PDF, combined only: split the set into N contiguous parts (stem-parte-i-de-N.pdf), each re-read for its page count."" },
    ""pdf_also_per_sheet"": { ""type"": ""boolean"", ""default"": false, ""description"": ""PDF, combined only: ALSO write one file per sheet; the combined set is then stem-set.pdf."" },
    ""pdf_archive_previous"": { ""type"": ""boolean"", ""default"": false, ""description"": ""PDF: after the new version is verified, MOVE (never delete) the older versions of pdf_name_pattern into pdf_archive_folder and re-read both ends. The pattern needs {version}."" },
    ""pdf_archive_folder"": { ""type"": ""string"", ""maxLength"": 60, ""default"": ""_anteriores"", ""description"": ""A plain folder name inside the output folder."" },
    ""pdf_qa"": { ""type"": ""object"", ""additionalProperties"": false, ""description"": ""PDF (and format=pdf_qa): read every produced file back for the fonts used (a missing one installed for this user only is advised to be installed for all users), blank pages, text over text, text outside the frame and, on request, the ink of each viewport against its reported box, the printed gray of a box, the printed lines of each note and a comparison with an earlier PDF. Findings are reported, not fatal; no picture is rendered (gray is a coarse raster of the vector drawing). Detail: docs/PDF-QA.md."", ""properties"": {
        ""expected_fonts"": { ""type"": ""array"", ""maxItems"": 20, ""items"": { ""type"": ""string"" }, ""description"": ""Font families the set must use (\""Arial Narrow\""); a missing one with another in its place is reported as substituted."" },
        ""frame_margin_mm"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 100, ""default"": 0, ""description"": ""Text must stay this far inside the page edge."" },
        ""regions"": { ""type"": ""array"", ""maxItems"": 50, ""items"": { ""type"": ""object"", ""required"": [""name""], ""properties"": { ""name"": { ""type"": ""string"" }, ""rect_mm"": { ""type"": ""array"", ""minItems"": 4, ""maxItems"": 4, ""items"": { ""type"": ""number"" } }, ""page"": { ""type"": ""integer"" }, ""viewport_id"": { ""type"": ""integer"" }, ""element_id"": { ""type"": ""integer"" } } }, ""description"": ""Boxes: rect_mm (page mm) or viewport_id (+element_id); ink box and printed gray. Detail: docs/PDF-QA.md."" },
        ""viewports"": { ""type"": ""boolean"", ""description"": ""Each viewport's printed ink vs its box (view_ids=sheets)."" },
        ""notes"": { ""type"": ""array"", ""maxItems"": 50, ""items"": { ""type"": ""object"", ""properties"": { ""text"": { ""type"": ""string"" }, ""note_id"": { ""type"": ""integer"" }, ""page"": { ""type"": ""integer"" }, ""expected_lines"": { ""type"": ""integer"" } } } },
        ""compare_to"": { ""type"": ""string"" } } },
    ""emit_manifest"": { ""type"": ""boolean"", ""default"": false, ""description"": ""PDF only: write output_path.manifest.json with SHA256, page counts, source views and revision IDs. Same overwrite policy as PDFs. Visual approval is separate."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"", ""description"": ""Units of pdf_print.origin_offset_x/y. Nothing else in this tool carries a length."" },
    ""delivery_id"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 120, ""description"": ""PDF only: publish as the publish stage of a ledgered delivery (horizun_plan_views delivery_open). Before any file is touched, every recorded scope is re-read and the publish gate must be open - every stage completed, the audit without blocking findings, every sheet approval current; otherwise the export refuses naming the reasons. On success the publish stage is recorded with the produced files and hashes."" },
    ""pdf_print"": { ""type"": ""object"", ""additionalProperties"": false, ""description"": ""PDF only: the PRINT POLICY, a closed field set over the options Revit's PDFExportOptions exposes - the same 21 in 2023-2027, measured on the installed API of each year. Absent fields take the policy defaults and are reported as 'defaulted', never as requested. Every option is read back from the option object after it is set ('applied'); paper_format and orientation are additionally PROVED from the produced page geometry ('verified' or 'verified_mismatch', which fails the export); everything else is 'requested_unverifiable' - passed to the exporter, not provable from the file. An unknown field, an option that would be silently ignored (zoom_percentage without zoom='zoom', offsets without placement='lower_left'), or export_in_background on a Revit older than 2025 refuses the whole call by name."", ""properties"": {
        ""paper_format"": { ""type"": ""string"", ""enum"": [""Default"", ""ANSI_A"", ""ANSI_B"", ""ANSI_C"", ""ANSI_D"", ""ANSI_E"", ""ISO_A4"", ""ISO_A3"", ""ISO_A2"", ""ISO_A1"", ""ISO_A0"", ""ISO_B4"", ""ISO_B3"", ""ISO_B2"", ""ISO_B1"", ""ARCH_A"", ""ARCH_B"", ""ARCH_C"", ""ARCH_D"", ""ARCH_E"", ""ARCH_E1"", ""ARCH_E2"", ""ARCH_E3""], ""default"": ""Default"", ""description"": ""Default prints each sheet at its own titleblock size, verified against ViewSheet.Outline; a named format is verified against its nominal size, within 2 pt."" },
        ""orientation"": { ""type"": ""string"", ""enum"": [""portrait"", ""landscape"", ""auto""], ""default"": ""auto"", ""description"": ""portrait/landscape are proved from the produced page; auto is reported, not judged."" },
        ""placement"": { ""type"": ""string"", ""enum"": [""center"", ""lower_left""], ""default"": ""center"", ""description"": ""Revit's Margins placement is the same enum value as LowerLeft (measured on 2026: both read back LowerLeft), so it is not offered as a third choice; offsets apply to lower_left."" },
        ""origin_offset_x"": { ""type"": ""number"", ""description"": ""placement='lower_left' AND zoom='zoom' only, in units, together with origin_offset_y; refused otherwise (with fit_to_page Revit fits the sheet to the whole paper and the offset clips it - measured on 2026). Applied and read back; not provable from the page."" },
        ""origin_offset_y"": { ""type"": ""number"", ""description"": ""placement='lower_left' AND zoom='zoom' only, in units, together with origin_offset_x; refused otherwise."" },
        ""zoom"": { ""type"": ""string"", ""enum"": [""fit_to_page"", ""zoom""], ""default"": ""fit_to_page"" },
        ""zoom_percentage"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""description"": ""zoom='zoom' only; refused otherwise."" },
        ""color_depth"": { ""type"": ""string"", ""enum"": [""black_line"", ""grayscale"", ""color""], ""default"": ""color"" },
        ""raster_quality"": { ""type"": ""string"", ""enum"": [""low"", ""medium"", ""high"", ""presentation""], ""default"": ""high"" },
        ""export_quality_dpi"": { ""type"": ""integer"", ""enum"": [72, 144, 300, 600, 1200, 2400, 3600, 4000], ""default"": 600 },
        ""always_use_raster"": { ""type"": ""boolean"", ""default"": false },
        ""hide_crop_boundaries"": { ""type"": ""boolean"", ""default"": true },
        ""hide_scope_boxes"": { ""type"": ""boolean"", ""default"": true },
        ""hide_reference_planes"": { ""type"": ""boolean"", ""default"": true },
        ""hide_unreferenced_view_tags"": { ""type"": ""boolean"", ""default"": true },
        ""mask_coincident_lines"": { ""type"": ""boolean"", ""default"": false },
        ""replace_halftone_with_thin_lines"": { ""type"": ""boolean"", ""default"": false },
        ""view_links_in_blue"": { ""type"": ""boolean"", ""default"": false },
        ""stop_on_error"": { ""type"": ""boolean"", ""default"": true },
        ""export_in_background"": { ""type"": ""boolean"", ""description"": ""Only false is accepted. Revit 2025+ has PDFExportOptions.SetExportInBackground; measured on 2026, a background export returns before the file exists, and this tool reports only files it re-read - so true is refused by name on every year (and the option is refused outright on 2023/2024, where it does not exist)."" }
    } },
    ""preset"": { ""type"": ""object"", ""description"": ""A NAMED, HASHED option bundle handed in as an argument (organisation-neutral: nothing ships compiled in). Its options override the loose arguments, its sha256 joins the plan hash - an edited preset is a different plan and the token refuses - and after the export each option is either PROVED from the produced file (ifc_version via FILE_SCHEMA, acad_version via the DWG signature, pixel_size via the PNG IHDR, combine by counting files) or reported requested_unverifiable by name. Unknown options and out-of-list values refuse the whole call."", ""properties"": { ""name"": { ""type"": ""string"" }, ""schema_version"": { ""type"": ""integer"", ""default"": 1 }, ""overwrite_policy"": { ""type"": ""string"", ""enum"": [""refuse"", ""replace""], ""default"": ""refuse"" }, ""options"": { ""type"": ""object"" } }, ""required"": [""name""] },
    ""file_naming"": { ""type"": ""string"", ""enum"": [""ordinal"", ""view_name"", ""sheet_number""], ""description"": ""dwg/dgn/dwfx sets: stem-<ordinal-id|view name|sheet number-name>."" },
    ""dwg_xrefs"": { ""type"": ""string"", ""enum"": [""linked"", ""bound""], ""description"": ""dwg: a sheet's views and links as xref files beside it, or bound into it. Export view sets report xref and same-stem raster reference_files separately; the produced DWG header proves format, not annotation contents or layout count."" },
    ""family_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""rfa: loadable Family ids."" },
    ""category"": { ""type"": ""string"", ""description"": ""rfa: every loadable family of this category (OST_ token or name)."" },
    ""acad_version"": { ""type"": ""string"", ""enum"": [""2013"", ""2018""], ""description"": ""dwg: the file version; verified from the produced file's signature."" },
    ""dwg_setup"": { ""type"": ""object"", ""required"": [""name""], ""additionalProperties"": false, ""description"": ""dwg: export with this named setup. dwg_layers (.json output): read its layer table, create it if absent, write layers rows; re-read after commit. A new setup is seeded from source, else layer_standard, else Revit's default, the active setup or a predefined one - whichever REALLY gives the created setup a layer table (rehearsed and rolled back); all empty refuses."", ""properties"": {
      ""name"": { ""type"": ""string"" }, ""source"": { ""type"": ""string"", ""description"": ""An existing setup whose table seeds a NEW one."" },
      ""layer_standard"": { ""type"": ""string"", ""enum"": [""AIA"", ""ISO13567"", ""CP83"", ""BS1192""], ""description"": ""Seed a NEW setup from a layer standard Revit ships."" },
      ""layers"": { ""type"": ""array"", ""maxItems"": 500, ""items"": { ""type"": ""object"", ""required"": [""category""], ""additionalProperties"": false, ""properties"": {
        ""category"": { ""type"": ""string"", ""description"": ""BuiltInCategory token (OST_Walls, language-independent) or the name Revit shows (follows Revit's language)."" },
        ""special"": { ""type"": ""string"", ""description"": ""Default, ExteriorWall, InteriorWall, FoundationWall, RetainingWall. Omitted: the Default row."" },
        ""subcategory"": { ""type"": ""string"" }, ""layer"": { ""type"": ""string"" },
        ""color"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 }, ""cut_layer"": { ""type"": ""string"" },
        ""cut_color"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 } } } } } },
    ""ifc_version"": { ""type"": ""string"", ""enum"": [""Default"", ""IFC2x2"", ""IFC2x3"", ""IFC2x3CV2"", ""IFC2x3BFM"", ""IFC2x3FM"", ""IFCBCA"", ""IFCCOBIE"", ""IFC4"", ""IFC4DTV"", ""IFC4RV""], ""default"": ""Default"" },
    ""ifc_filter_view_id"": { ""type"": ""integer"", ""description"": ""Optional non-template view whose visibility filters the IFC export."" },
    ""ifc_setup"": { ""type"": ""object"", ""required"": [""name""], ""additionalProperties"": false, ""description"": ""format ifc_setup (.json output): save a NAMED IFC export setup in the document where Export IFC lists it (ifc_version required; the ifc_* options apply), write IfcExportAs/IfcExportType per type, re-read both; exporter_readback = the installed exporter UI loading it back."", ""properties"": {
      ""name"": { ""type"": ""string"" },
      ""options"": { ""type"": ""object"", ""description"": ""Exporter keys, e.g. ExportIFCCommonPropertySets, ExportInternalRevitPropertySets, ExportSchedulesAsPsets, ExportUserDefinedPsets(+FileName), StoreIFCGUID. An unknown key is refused with the list."" },
      ""classification"": { ""type"": ""object"", ""description"": ""ClassificationName, ClassificationEdition, ClassificationSource, ClassificationLocation, ClassificationFieldName."" },
      ""type_mappings"": { ""type"": ""array"", ""maxItems"": 2000, ""items"": { ""type"": ""object"", ""required"": [""type_id"", ""export_as""], ""additionalProperties"": false, ""properties"": {
        ""type_id"": { ""type"": ""integer"" }, ""export_as"": { ""type"": ""string"", ""description"": ""e.g. IfcFooting"" }, ""predefined_type"": { ""type"": ""string"", ""description"": ""e.g. PAD_FOOTING"" } } } } } },
    ""ifc_export_base_quantities"": { ""type"": ""boolean"", ""default"": false },
    ""ifc_split_walls_and_columns"": { ""type"": ""boolean"", ""default"": false },
    ""ifc_space_boundary_level"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 2, ""default"": 1 },
    ""nwc_scope"": { ""type"": ""string"", ""enum"": [""model"", ""view""], ""default"": ""model"" },
    ""nwc_coordinates"": { ""type"": ""string"", ""enum"": [""Shared"", ""Internal""], ""default"": ""Shared"" },
    ""nwc_parameters"": { ""type"": ""string"", ""enum"": [""All"", ""Elements"", ""None""], ""default"": ""All"" },
    ""nwc_export_links"": { ""type"": ""boolean"", ""default"": false },
    ""nwc_export_element_ids"": { ""type"": ""boolean"", ""default"": true },
    ""nwc_export_room_geometry"": { ""type"": ""boolean"", ""default"": true },
    ""nwc_export_parts"": { ""type"": ""boolean"", ""default"": false },
    ""fbx_without_boundary_edges"": { ""type"": ""boolean"", ""default"": false },
    ""fbx_use_lod"": { ""type"": ""boolean"", ""default"": false },
    ""fbx_lod"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 15, ""default"": 8 },
    ""fbx_stop_on_error"": { ""type"": ""boolean"", ""default"": true },
    ""overwrite"": { ""type"": ""boolean"", ""default"": false },
    ""information_container"": { ""type"": ""object"", ""description"": ""Optional ISO 19650 container (same object as horizun_information_container: fields, field_order, separator, field_patterns, status, revision, title, status_codes, revision_patterns, file_name). Validated BEFORE anything is exported - an invalid one refuses with every problem named. The produced file takes the container's name (directory and extension from output_path) and, after the export is verified, '<file>.container.json' is written beside it with its SHA-256 and read back. status and revision are required. Not accepted for image, nor for PDF with pdf_combine=false (one container is one file); an existing sidecar is never overwritten."" },
    ""cobie"": { ""type"": ""object"", ""additionalProperties"": false, ""required"": [""created_by"", ""facility"", ""phase"", ""component_categories""], ""description"": ""format cobie only: the caller's mapping for a COBie 2.4 workbook (Facility, Floor, Space, Zone, Type, Component, System). Nothing is defaulted from this machine or an organisation: a required cell with no source stays empty and is a finding (never 'n/a'), duplicate names and broken references are findings, and deliverable_ready is false while one remains - the workbook is still written, then re-read cell by cell. Fields, columns and rules: docs/TOOLS-EXTENDED.md."", ""properties"": {
      ""created_by"": { ""type"": ""string"", ""description"": ""COBie CreatedBy contact e-mail, written on every row."" },
      ""created_on"": { ""type"": ""string"", ""description"": ""ISO-8601; default: the export time, UTC."" },
      ""facility"": { ""type"": ""object"", ""additionalProperties"": false, ""required"": [""name""], ""properties"": {
        ""name"": { ""type"": ""string"" }, ""category"": { ""type"": ""string"" }, ""project_name"": { ""type"": ""string"" }, ""site_name"": { ""type"": ""string"" },
        ""phase"": { ""type"": ""string"", ""description"": ""COBie Facility.Phase (the project stage), not a Revit phase."" }, ""description"": { ""type"": ""string"" },
        ""project_description"": { ""type"": ""string"" }, ""site_description"": { ""type"": ""string"" }, ""currency_unit"": { ""type"": ""string"" }, ""area_measurement"": { ""type"": ""string"" } } },
      ""phase"": { ""type"": ""string"", ""description"": ""The Revit phase: its rooms or spaces are the Space rows and components are placed in them. No default."" },
      ""component_categories"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"" }, ""description"": ""OST_ tokens whose instances are Components; their types are the Type rows. No default list."" },
      ""space_source"": { ""type"": ""string"", ""enum"": [""rooms"", ""spaces""], ""default"": ""rooms"" },
      ""category_parameter"": { ""type"": ""string"", ""description"": ""Parameter holding the classification written to Category (Floor, Space, Type, System): on the element, else its type."" },
      ""zone_parameter"": { ""type"": ""string"", ""description"": ""Room/space parameter whose value names its Zone."" }, ""zone_category"": { ""type"": ""string"" },
      ""component_name_parameter"": { ""type"": ""string"", ""description"": ""e.g. Mark. A missing, empty or shared value falls back to <Type name>-<element id>, reported."" },
      ""type_fields"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""string"" }, ""description"": ""{Type column: parameter}, e.g. Manufacturer, ModelNumber."" },
      ""component_fields"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""string"" }, ""description"": ""{Component column: parameter}, e.g. SerialNumber, TagNumber."" },
      ""project_context_path"": { ""type"": ""string"", ""description"": ""A project-context.json (horizun_project_context): fills project name, site, stage and category_parameter when not given."" },
      ""max_findings"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 5000, ""default"": 500 } } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" },
    ""require_gate"": " + RequireGateSchema + @"
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_get_dimension_references",
                Command = "horizun_get_dimension_references",
                Description = "Discover stable dimension references in one view for explicit elements or a filter, with semantic selection, compatibility, ambiguity, geometry fingerprint and coverage. Read-only.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""view_id""],
  ""properties"": {
    ""view_id"": { ""type"": ""integer"", ""description"": ""REQUIRED. The view the dimension will live in; reference compatibility is view-dependent, so candidates are evaluated against THIS view."" },
    ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""integer"" }, ""description"": ""Host-document elements to inspect. Exactly one of element_ids or filter; either may be combined with linked_targets."" },
    ""linked_targets"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""description"": ""Elements INSIDE loaded RVT links. Additive to element_ids/filter, and usable alone. One entry per link INSTANCE - naming the same instance twice is refused, because the two entries could not be told apart afterwards."", ""items"": {
      ""type"": ""object"", ""required"": [""link_instance_id"", ""linked_element_ids""], ""properties"": {
        ""link_instance_id"": { ""type"": ""integer"", ""description"": ""HOST-document id of a placed RevitLinkInstance. Not the link type, and not an element inside the link."" },
        ""linked_element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""integer"" }, ""description"": ""Ids inside the LINKED document. A host-document id copied here will usually resolve to nothing, or to something else entirely."" }
      }, ""additionalProperties"": false
    }},
    ""filter"": { ""type"": ""object"", ""description"": ""Reproducible alternative to explicit ids. Same semantics as horizun_query_model's core filters; matched elements are inspected in ascending element-id order. Over 200 matches is refused, not sampled."", ""properties"": {
      ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
      ""family"": { ""type"": ""string"" }, ""type"": { ""type"": ""string"" },
      ""name"": { ""type"": ""string"" }, ""level"": { ""type"": ""string"" }
    }, ""additionalProperties"": false },
    ""selectors"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""centerline"", ""grid"", ""level"", ""reference_plane"", ""exterior_face"", ""interior_face"", ""top_face"", ""bottom_face"", ""nearest_face"", ""farthest_face"", ""edge"", ""endpoint"", ""opening_left"", ""opening_right"", ""opening_center"", ""opening_head"", ""opening_sill"", ""opening_front_back_center"", ""family_named_reference"", ""core_exterior_face"", ""core_interior_face"", ""layer_face""] }, ""description"": ""Semantic selection. Opening selectors use FamilyInstance.GetReferences. family_named_reference uses GetReferenceByName and requires reference_name. Family reference positions are estimates until dimension rehearsal."" },
    ""reference_name"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""Required for family_named_reference; exact family reference-plane name."" },
    ""probe_point"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""REQUIRED when selectors include nearest_face/farthest_face: nearest to WHAT is not a guessable fact. In 'units'."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""max_results"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 100 },
    ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 },
    ""include_incompatible"": { ""type"": ""boolean"", ""default"": true, ""description"": ""false hides candidates whose compatible_with_dimension is false; totals still count them and the reply says how many were excluded."" },
    ""explain_references"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""string"" }, ""description"": ""EXPLAIN MODE: stable-reference strings to diagnose in view_id. Replaces the candidate search (do not combine with element_ids, filter, linked_targets, selectors). Per reference: usable_in_dimension plus reasons[] with code, cause and next_step; per consecutive pair: parallelism. A linked grid or level answers linked_datum_rejected_by_dimension_api."" },
    ""dimension_id"": { ""type"": ""integer"", ""description"": ""EXPLAIN MODE: an existing dimension whose own references to diagnose in view_id (why a dimension is broken or cannot be recreated). Not with explain_references."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_query_dimensions",
                Command = "horizun_query_dimensions",
                Description =
                    "Read existing dimensions completely: linear, angular, radial, diameter, arc-length and spot " +
                    "elevation/coordinate/slope. Per dimension: owner view, dimension type and style, the dimension " +
                    "curve (an unbound line is reported as origin+direction rather than invented endpoints), EVERY " +
                    "reference as a stable representation with the referenced element's identity and whether it " +
                    "still exists, AreReferencesAvailable and a broken-reference count. A reference into an RVT " +
                    "link is RESOLVED THROUGH the link when it is loaded - link instance, link type, linked " +
                    "document, linked element and the instance's current transform fingerprint, as separate ids - " +
                    "and reported as unknown with a link_state when it is not; it is never counted broken, because " +
                    "'we could not look inside the link' is not 'the element is gone'. Each row carries " +
                    "linked_references, unloaded_link_references, unreadable_link_references and a " +
                    "reference_coverage verdict of host_only/complete/incomplete. Per-segment values " +
                    "with prefix/suffix/above/below/override/lock, EQ state, and values in internal feet AND the " +
                    "requested display units. Every row also names its category and view-specificity, because model " +
                    "CONSTRAINTS - locked alignments, sketch EQ - wear the Dimension class too, and a reader must be " +
                    "able to tell annotation from constraint without decoding a null view. Read-only, deterministic " +
                    "element-id order, exact totals, paginated; a field Revit would not surrender becomes a named " +
                    "warning on that row rather than a silent omission.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""view_id"": { ""type"": ""integer"", ""description"": ""Only dimensions OWNED by this view."" },
    ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" }, ""description"": ""Specific dimension ids; ids that matched nothing are listed back."" },
    ""dimension_type_id"": { ""type"": ""integer"" },
    ""references_to_import"": { ""type"": ""boolean"", ""description"": ""Require a confirmed CAD reference. Rows give import_reference_indices; undecidable matches count as unreadable."" },
    ""shapes"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""linear"", ""angular"", ""radial"", ""diameter"", ""arc_length"", ""spot_elevation"", ""spot_coordinate"", ""spot_slope""] } },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 100 },
    ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_edit_dimensions",
                Command = "horizun_edit_dimensions",
                Description =
                    "Edit existing dimensions atomically and prove every edit from the model: change the dimension " +
                    "type (same style only - changing a dimension's SHAPE is refused), move the dimension line by a " +
                    "vector, set or clear prefix/suffix/above/below/value override on single-segment dimensions or " +
                    "per segment on chains, EQ and lock, reset the text position. Dry-run is the default; the " +
                    "confirmation token binds the RESOLVED state of every dimension it rehearsed - a dimension " +
                    "somebody else edited in between refuses as stale_plan rather than overwriting their change. " +
                    "Apply is one transaction verified in a still-reversible state: any postcondition that does not " +
                    "read back rolls the WHOLE batch back and reports Revit's own transaction status; the closed " +
                    "outcome set is verified_applied, rolled_back, refused, stale_plan and uncertain. Replacing a " +
                    "dimension's references is refused by name: the Revit API exposes no reference setter in any " +
                    "supported year, and Python cannot reach one either - delete and recreate through " +
                    "horizun_annotate instead. Model CONSTRAINTS (non-view-specific dimensions: locked alignments, " +
                    "sketch EQ) are refused by name: unlocking one would change what holds the geometry in place, " +
                    "not what a sheet says.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"", ""description"": ""Units of move_by, text_position, text_offset and leader_end."" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": {
      ""type"": ""object"", ""required"": [""element_id""], ""properties"": {
        " + IdentityFields("element_id", false) + @"""element_id"": { ""type"": ""integer"", ""description"": ""A Dimension in the active document. The same id twice in one batch is refused."" },
        ""set_type_id"": { ""type"": ""integer"", ""description"": ""A DimensionType of the SAME DimensionStyleType as the dimension's current type."" },
        ""move_by_sheet"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } },
        ""move_by"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Translation vector in 'units'. Verified against re-read geometry under a declared tolerance; a component Revit rederives away (along the dimension's own line) fails verification and rolls the batch back."" },
        ""prefix"": { ""type"": ""string"" }, ""suffix"": { ""type"": ""string"" },
        ""above"": { ""type"": ""string"" }, ""below"": { ""type"": ""string"" },
        ""value_override"": { ""type"": ""string"", ""description"": ""Empty string CLEARS the override; the read-back proves it. Single-segment dimensions only - chains take these per segment."" },
        ""eq"": { ""type"": ""boolean"", ""description"": ""AreSegmentsEqual. Multi-segment dimensions only."" },
        ""lock"": { ""type"": ""boolean"", ""description"": ""IsLocked. Single-segment dimensions only."" },
        ""segments"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 64, ""items"": {
          ""type"": ""object"", ""required"": [""index""], ""properties"": {
            ""index"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""0-based, validated against the dimension's real segment count."" },
            ""prefix"": { ""type"": ""string"" }, ""suffix"": { ""type"": ""string"" },
            ""above"": { ""type"": ""string"" }, ""below"": { ""type"": ""string"" },
            ""value_override"": { ""type"": ""string"" }, ""lock"": { ""type"": ""boolean"" },
            ""text_position"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Absolute text position of THIS segment, in units. Refused when the segment reports IsTextPositionAdjustable()=false; re-read within 1e-5 ft after commit."" },
            ""text_offset"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" }, ""description"": ""[dx, dy] from the segment's current text position along the owner view's right/up axes, in units; scaled by the view scale when the action's distance_space is paper. One of text_position / text_offset."" },
            ""reset_text_position"": { ""type"": ""boolean"", ""description"": ""Only explicit true. Reported as invocation_completed with before/after positions."" },
            ""leader_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""This segment's text leader end, in units; needs the dimension to have a leader (existing or leader=true in the same action)."" }
          }, ""additionalProperties"": false } },
        ""text_position"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Absolute text position, in units. Single-segment dimensions only (chains: segments[].text_position). Refused when IsTextPositionAdjustable() is false, so a request Revit would ignore is never accepted; re-read within 1e-5 ft after commit and reported requested/read/match."" },
        ""text_offset"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" }, ""description"": ""[dx, dy] from the current text position along the owner view's right/up axes, in units. distance_space=paper multiplies by the view scale (so 5 mm on paper at 1:100 is 500 mm in the model). One of text_position / text_offset; single-segment only."" },
        ""distance_space"": { ""type"": ""string"", ""enum"": [""model"", ""paper""], ""default"": ""model"", ""description"": ""How text_offset (element or segment) is measured."" },
        ""leader"": { ""type"": ""boolean"", ""description"": ""Dimension.HasLeader for the whole dimension. Set before any leader_end in the same action."" },
        ""leader_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Text leader end position, in units. Single-segment only; needs a leader (existing or leader=true). Re-read within 1e-5 ft."" },
        ""reset_text_position"": { ""type"": ""boolean"", ""description"": ""Only explicit true is accepted. Reported as invocation_completed with the text position before and after - Revit publishes no 'is at default' predicate, and the row says so instead of claiming verified."" }
      }, ""additionalProperties"": false } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: edit dimensions"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_query_detail_2d",
                Command = "horizun_query_detail_2d",
                Description =
                    "Read the 2D-detail surface of ONE view. mode=resources answers what can be drawn WITH: line " +
                    "styles (from a real curve's own valid set when one exists, otherwise the Lines subcategories), " +
                    "filled-region types with IsMasking read from each type, and the placeable view-based family " +
                    "symbols (detail components and generic annotations) with their activation state - all by id " +
                    "and UniqueId, deterministically ordered, paginated per list. mode=elements reads the view's " +
                    "existing detail curves, filled regions and view-based instances: class, line style, normalised " +
                    "geometry and a deterministic geometry signature on a 0.1 mm grid, loop counts, IsMasking, " +
                    "pinned and group membership, with exact totals and named unreadables. This command NEVER " +
                    "resolves resources by name - ambiguity is avoided by design: every answer is ids, and the " +
                    "caller chooses. Coordinates are view-plane: x along the view's RightDirection, y along its " +
                    "UpDirection, from the view origin, in the requested units.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""view_id""],
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""enum"": [""resources"", ""elements""], ""default"": ""resources"" },
    ""view_id"": { ""type"": ""integer"", ""description"": ""REQUIRED. Every answer this command gives is about ONE view. Not a template."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" }, ""description"": ""elements mode: specific ids; ids that matched nothing are listed back."" },
    ""categories"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"" }, ""description"": ""elements mode: lines, detail_components, generic_annotations, filled_regions - or the OST_* token."" },
    ""type_ids"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""integer"" } },
    ""bounding_box"": { ""type"": ""object"", ""required"": [""min"", ""max""], ""properties"": {
      ""min"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } },
      ""max"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } }
    }, ""additionalProperties"": false, ""description"": ""elements mode: view-plane coordinates in 'units'."" },
    ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 100 },
    ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_query_structure",
                Command = "horizun_query_structure",
                Description =
                    "Read STRUCTURE from the model: steel members, reinforcement hosts, cover, rebar, " +
                    "reinforcement systems and steel connections, in one surface with one pagination scheme and " +
                    "one coverage vocabulary. mode=members gives structural framing and columns with the two " +
                    "things that are constantly confused - the LOCATION LINE and the CROSS-SECTION ROTATION, " +
                    "which are different numbers - plus level, offsets, justification, material, volume, the " +
                    "join state at each end measured through the API rather than read off a parameter, and " +
                    "whether Revit accepts the member as a reinforcement host at all. mode=hosts lists every " +
                    "candidate host with its eligibility, its COMMON cover and its cover PER FACE - a slab with " +
                    "different top and bottom cover reports null for the common one, and a report that stopped " +
                    "there would say it has no cover - and the ids of every rebar, area, path and fabric system " +
                    "inside it. mode=covers lists the cover TYPES defined in the document by measured distance, " +
                    "which is not the same question as which face carries which. mode=rebar gives bar type with " +
                    "BOTH diameters named (nominal and model are different numbers and neither one is 'the " +
                    "diameter'), shape, style, layout rule, array length, normal, distribution path, " +
                    "terminations per end with hook type and orientation, couplers, the centreline curves Revit " +
                    "actually draws, every bar position transform, and total length, volume and quantity READ " +
                    "OFF THE ELEMENT rather than recomputed from a layout. It publishes bar POSITIONS and bar " +
                    "COUNT separately because they differ whenever an end bar is suppressed. " +
                    "mode=reinforcement_systems covers area and path systems with their layers, directions and " +
                    "member bars, and lists fabric without pretending to read it. mode=connections gives " +
                    "connected members, origin, and whether the connection is DETAILED - carrying real plates " +
                    "and bolts - or a generic placeholder. mode=coverage answers what this bridge can and cannot " +
                    "do in THIS Revit, including which generation of the rebar creation API it was compiled " +
                    "against. mode=quantities is the same bars grouped the way somebody orders steel - by " +
                    "mark, by diameter, by host, by rule - and it reads EVERY bar in scope rather than a page " +
                    "of them, because a total over the first fifty rows is not a total; it, reconcile and bending_schedule " +
                    "include the bars of area and path systems (RebarInSystem, rows marked system; a system id in " +
                    "element_ids scopes to its bars) and fabric sheets (system.kind fabric: the wires as placed, their steel " +
                    "weighed with your density, Revit's own sheet mass beside it; a fabric area id scopes to its sheets). A value the model " +
                    "would not report never becomes a zero in a sum: the total is null, the _counted number " +
                    "beside it says what could be read, and the group says how many it could not. Weight needs " +
                    "a density AND its source, both declared by you. mode=reconcile compares that takeoff with a detail schedule you pass, mark by mark - count exactly, total length and weight within the tolerances you declare - and names every mark only one side has. mode=bending_schedule is the cartilla derived from the model: one line per mark with bar type, nominal diameter, shape, the legs read off the bar, bars, one bar's length, total length and kg, totals per diameter and per host; a mark over different bars is split and named, never averaged; the density and its source are required. mode=parse_notation reads schedule bar notation (lines) into bars, lengths, spacings and stirrup zones in mm, refusing what it cannot read without guessing; it reads no model. mode=compare_bars compares two sets of bars BAR BY BAR (element_ids_a or host_id_a against element_ids_b or host_id_b): each bar of A is identical, near or unmatched with its nearest same-diameter bar of B by mean centreline distance and length difference, one-to-one (greedy, identical pairs first), under bar_tolerances you declare; B's leftover bars are listed. mode=steel_review judges EVERY bar in scope (sets and area/path system bars; element_ids, host_id or the whole model) against every other with NO requirement set: duplicate_bar (one bar built twice on one centreline, same diameter) and overlapping_bar (a bar through another, or closer than the optional clearances), tolerance_mm required; counts in total and by host category, pairs worst first up to max_rows, verdict clean only when nothing was found and everything was read. mode=analytical: analytical members/panels, physical " +
                    "association, end releases, node gaps, physical members with no analytical one. mode=loads: point, " +
                    "line and area loads with case, nature, host, kN units. Read-only: no transaction is opened. Every count carries complete, partial, " +
                    "unavailable, unreadable or not_applicable, and only complete means the number is a total - " +
                    "a zero under any other word means something was not looked at.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""enum"": [""members"", ""hosts"", ""covers"", ""rebar"", ""reinforcement_systems"", ""connections"", ""coverage"", ""quantities"", ""analytical"", ""loads"", ""reconcile"", ""bending_schedule"", ""parse_notation"", ""compare_bars"", ""steel_review""], ""description"": ""Which population to read. Required: there is no honest default across these different questions."" },
    ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" }, ""description"": ""Narrow to these elements instead of collecting the whole model."" },
    ""categories"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 40, ""items"": { ""type"": ""string"" }, ""description"": ""mode=members: BuiltInCategory names. Defaults to OST_StructuralFraming and OST_StructuralColumns."" },
    ""host_id"": { ""type"": ""integer"", ""description"": ""mode=rebar, quantities, reconcile, bending_schedule and steel_review: only bars whose host is this element."" },
    ""group_by"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 10, ""items"": { ""type"": ""string"", ""enum"": [""mark"", ""bar_type"", ""nominal_diameter_mm"", ""model_diameter_mm"", ""shape"", ""host"", ""host_category"", ""style"", ""layout"", ""rule""] }, ""description"": ""mode=quantities: how to group. Defaults to mark. `rule` is the provenance rule id, which for a stirrup zone is parent#zone - so grouping by rule groups BY ZONE."" },
    ""density_kg_per_m3"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""mode=quantities, reconcile and bending_schedule (REQUIRED there): the density to weigh the steel with. This bridge carries none of its own. Requires density_source."" },
    ""density_source"": { ""type"": ""string"", ""maxLength"": 400, ""description"": ""mode=quantities, reconcile and bending_schedule: where that density came from - the standard, the supplier, the project document. Required whenever density_kg_per_m3 is given, because a weight nobody can trace is a weight nobody should order from."" },
    ""schedule"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 5000, ""items"": { ""type"": ""object"", ""properties"": { ""mark"": { ""type"": ""string"" }, ""bar_type"": { ""type"": ""string"" }, ""diameter_mm"": { ""type"": ""number"" }, ""count"": { ""type"": ""integer"", ""minimum"": 1 }, ""length_mm"": { ""type"": ""number"" }, ""weight_kg"": { ""type"": ""number"" } }, ""required"": [""mark"", ""count"", ""length_mm""], ""additionalProperties"": false }, ""description"": ""mode=reconcile: the detail schedule, one row per line - mark, bar_type or diameter_mm, count of bars, length_mm of EACH bar, and optionally the line's weight_kg. Rows of one mark add up."" },
    ""tolerances"": { ""type"": ""object"", ""properties"": { ""length_pct"": { ""type"": ""number"", ""minimum"": 0 }, ""weight_pct"": { ""type"": ""number"", ""minimum"": 0 } }, ""required"": [""length_pct""], ""additionalProperties"": false, ""description"": ""mode=reconcile: how far the model's total length (and weight) per mark may sit from the schedule's, in percent. Required: whether a difference matters is a decision. Counts are compared exactly."" },
    ""length_tolerance_mm"": { ""type"": ""number"", ""minimum"": 0, ""default"": 0, ""description"": ""mode=bending_schedule: how far one bar's length may differ between sets sharing a mark and still be one line. 0 compares exactly; bar type and shape are always exact."" },
    ""lines"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 5000, ""items"": { ""type"": ""string"" }, ""description"": ""mode=parse_notation: schedule cells as written (4#5 L=6.20, 2#4+1#5, 4Ø16, E#3 c/0.10, E#3 1@0.05 R@0.20); each is read into mm or refused with its code."" },
    ""element_ids_a"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" }, ""description"": ""mode=compare_bars: side A, rebar set ids (every id must be a set). Or host_id_a."" },
    ""element_ids_b"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" }, ""description"": ""mode=compare_bars: side B, rebar set ids. Or host_id_b."" },
    ""host_id_a"": { ""type"": ""integer"", ""description"": ""mode=compare_bars: side A is every set hosted by this element."" },
    ""host_id_b"": { ""type"": ""integer"", ""description"": ""mode=compare_bars: side B is every set hosted by this element."" },
    ""bar_tolerances"": { ""type"": ""object"", ""properties"": { ""identical_mm"": { ""type"": ""number"", ""minimum"": 0 }, ""near_mm"": { ""type"": ""number"", ""minimum"": 0 }, ""length_mm"": { ""type"": ""number"", ""minimum"": 0 } }, ""required"": [""identical_mm"", ""near_mm"", ""length_mm""], ""additionalProperties"": false, ""description"": ""mode=compare_bars, REQUIRED, no defaults: identical = mean distance <= identical_mm and length difference <= length_mm; near = mean distance <= near_mm."" },
    ""tolerance_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""mode=analytical: a member end farther than this from every other analytical curve is a gap. Default: Revit vertex tolerance. mode=steel_review (REQUIRED there): how far two centrelines may stand apart and still be one bar, and how deep surfaces must interpenetrate to be an intersection."" },
    ""clearances"": { ""type"": ""object"", ""properties"": { ""between_rules_mm"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 500 }, ""crossing_mm"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 500 } }, ""additionalProperties"": false, ""description"": ""mode=steel_review, optional: the clear distance bars of different sets keep running alongside (between_rules_mm) or crossing (crossing_mm). Absent: only intersections and duplicates are judged."" },
    ""include_bar_positions"": { ""type"": ""boolean"", ""default"": true, ""description"": ""mode=rebar: the transform origin of every bar position. This is what proves a set sits inside its host, so it is on by default and turning it off costs you that proof."" },
    ""include_constraints"": { ""type"": ""boolean"", ""default"": false, ""description"": ""mode=rebar: per handle, the constraint Revit holds it with now and the preferred one - type, target element, face, distance - and the handles pinned to another element than the host."" },
    ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 50 },
    ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 }
  },
  ""required"": [""mode""],
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_query_planimetry",
                Command = "horizun_query_planimetry",
                Description =
                    "Read documentation from the active model: inventory, sheets, views, placements, annotations and references. " +
                    "Placement boxes use sheet coordinates; unknown relations and totals are reported. " +
                    "Read-only; cursor binds arguments and results. Cooperative annotations and sheets pages use cooperative.cursor; " +
                    "matched totals and unmatched IDs are unknown, and API calls and indexing are indivisible.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""mode"": { ""type"": ""string"", ""enum"": [""inventory"", ""sheets"", ""views"", ""unplaced_views"", ""placements"", ""annotations"", ""references"", ""annotation_overlaps"", ""leader_targets"", ""annotation_staleness"", ""sheet_conflicts"", ""free_spot"", ""view_ray"", ""section_mark_diagnosis"", ""iso_inventory"", ""rasters""], ""default"": ""inventory"", ""description"": ""unplaced_views lists printable graphical views without a viewport. placements includes view, scale and boxes in sheet coordinates. Other modes return paginated populations; see docs/NOTES-LEADERS.md."" },
    ""note_ids"": { ""type"": ""array"", ""maxItems"": 500, ""items"": { ""type"": ""integer"" } }, ""points_mm"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } } },
    ""changed_unique_ids"": { ""type"": ""array"", ""maxItems"": 20000, ""items"": { ""type"": ""string"" } },
    ""deleted_unique_ids"": { ""type"": ""array"", ""maxItems"": 20000, ""items"": { ""type"": ""string"" } },
    ""sheet_id"": { ""type"": ""integer"" }, ""view_id"": { ""type"": ""integer"" }, ""section_view_id"": { ""type"": ""integer"" },
    ""size_mm"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }, ""near_mm"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""gap_mm"": { ""type"": ""number"" }, ""step_mm"": { ""type"": ""number"" }, ""max_radius_mm"": { ""type"": ""number"" }, ""include_linked_symbols"": { ""type"": ""boolean"" },
    ""sheet_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" }, ""description"": ""Narrow to these sheets. Ids that matched nothing come back in unmatched_ids."" },
    ""view_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" } },
    ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" }, ""description"": ""annotations/references modes: specific ids."" },
    ""categories"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""dimensions"", ""tags"", ""text_notes"", ""detail_curves"", ""filled_regions"", ""detail_components"", ""generic_annotations"", ""revision_clouds""] }, ""description"": ""mode=annotations ONLY. Refused in any other mode rather than accepted and ignored."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""cooperative"": " + CooperativeReadSchema + @",
    ""time_budget_s"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 120, ""description"": ""Planimetry time budget. Omitted: 20 s (SUPUESTO), or cooperative.ui_budget_ms."" },
    ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 100 },
    ""cursor"": { ""type"": ""string"", ""description"": ""From a previous reply's next_cursor. Bound to the query arguments AND to the result set: a cursor used with other arguments, or after the model moved, is refused rather than paging a different list."" },
    ""include_parameters"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Project the named parameters onto sheets and views. Requires parameter_names."" },
    ""parameter_names"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""string"" }, ""description"": ""Which parameters to project. Required with include_parameters, and refused without it."" }
  },
  ""allOf"": [
    { ""if"": { ""properties"": { ""include_parameters"": { ""const"": true } }, ""required"": [""include_parameters""] }, ""then"": { ""required"": [""parameter_names""] } },
    { ""if"": { ""required"": [""parameter_names""] }, ""then"": { ""required"": [""include_parameters""] } },
    { ""if"": { ""required"": [""categories""] }, ""then"": { ""properties"": { ""mode"": { ""const"": ""annotations"" } }, ""required"": [""mode""] } }
  ],
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_audit_planimetry",
                Command = "horizun_audit_planimetry",
                Description =
                    "Audit the documentation surface DIRECTLY FROM THE MODEL and return findings, not prose. It " +
                    "reads through the same single collector horizun_query_planimetry renders, so the two can " +
                    "never disagree about what is on a sheet. Read-only: no transaction is opened. Two sets of " +
                    "rules, and the boundary is the design: the UNIVERSAL set is what is true without a company " +
                    "standard - a sheet with zero or several title blocks, viewports/schedules that overlap " +
                    "beyond an explicit tolerance, a placement wholly or partly off the sheet, a schedule summing a non-additive field, a viewport or schedule " +
                    "placement whose target is gone, a view held by more than one viewport, a broken parent, a " +
                    "dimension with AreReferencesAvailable=false or a genuinely broken reference (references into " +
                    "LINKS are never counted broken), an orphaned tag, a duplicate tag over the same target set, " +
                    "an empty text note, a degenerate detail curve, an annotation demonstrably outside an ACTIVE " +
                    "crop, a reference whose target view is gone. Everything with a NUMBER or a NAME in it - " +
                    "margins, minimum gaps, allowed scales/templates/types, sheet and view naming, required sheet " +
                    "parameters, forbidden numeric overrides, which categories must be tagged - arrives as an " +
                    "INLINE requirement_set; there is no file path on this surface. Severities are blocking, " +
                    "advisory and unknown; an unreadable fact is ALWAYS unknown and never a pass, and a check with " +
                    "unknowns is reported as unknown rather than passed. There is no 0-100 score. Deterministic " +
                    "order (severity, rule id, sheet number, view id, element id), cursor bound to the arguments " +
                    "and the finding set, exact totals, and a not_covered block naming the judgements this phase " +
                    "deliberately does not make.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""scope"": { ""type"": ""string"", ""enum"": [""model"", ""sheets"", ""views""], ""default"": ""model"", ""description"": ""model examines every population. sheets examines sheets and placements. views examines views, annotations and references. A check with no population reports not_applicable, never passed."" },
    ""sheet_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" } },
    ""view_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" } },
    ""checks"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"" }, ""description"": ""Universal check ids to run. Omit for all of them; the full catalog is published in the reply. A name that is not in the catalog is REFUSED - running nothing under a misspelt id would report a clean model."" },
    ""requirement_set"": { ""type"": ""object"", ""required"": [""requirement_set"", ""rules""], ""properties"": {
      ""requirement_set"": { ""type"": ""object"", ""required"": [""id"", ""version""], ""properties"": {
        ""id"": { ""type"": ""string"", ""minLength"": 1 },
        ""version"": { ""type"": ""string"", ""minLength"": 1 },
        ""title"": { ""type"": ""string"" },
        ""scope"": { ""type"": ""object"" }
      }, ""additionalProperties"": false },
      ""rules"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": {
        ""type"": ""object"", ""required"": [""id"", ""entity"", ""selector"", ""assertion""], ""properties"": {
          ""id"": { ""type"": ""string"", ""minLength"": 1 },
          ""entity"": { ""type"": ""string"", ""enum"": [""sheet"", ""view"", ""viewport"", ""schedule_placement"", ""dimension"", ""tag"", ""text_note"", ""detail_2d"", ""view_reference""] },
          ""severity"": { ""type"": ""string"", ""enum"": [""blocking"", ""advisory""], ""default"": ""advisory"" },
          ""message"": { ""type"": ""string"" },
          ""selector"": { ""type"": ""object"", ""minProperties"": 1, ""description"": ""field, field_matches (regex) or field_in (list) for a field of the entity; applies_to for explicit ids; applies_to_all: true to mean EVERY one deliberately. An empty selector is refused - a rule that matches everything by accident is indistinguishable from one that meant to."" },
          ""assertion"": { ""type"": ""object"", ""required"": [""operator""], ""properties"": {
            ""field"": { ""type"": ""string"", ""description"": ""Required for the comparing operators, refused for the whole-entity ones. parameter:<name> is accepted on sheet and view."" },
            ""operator"": { ""type"": ""string"", ""enum"": [""matches"", ""not_matches"", ""equals"", ""not_equals"", ""in_list"", ""not_in_list"", ""required"", ""not_empty"", ""greater_than"", ""less_than"", ""between"", ""minimum_gap"", ""inside_extent"", ""allowed_type"", ""allowed_template"", ""allowed_scale"", ""required_parameter"", ""forbid_numeric_override"", ""requires_tag"", ""fits_titleblock_cell""] },
            ""value"": { ""description"": ""Shape follows the operator: a regex string, a scalar, a list, [min,max] for between, a length in the call's units for minimum_gap/inside_extent, category names (optionally objects with exclude_types/exclude_families/exclude_type_matches/exclude_when_parameter_set) for requires_tag, or {field: sheet_number|name, cell_width, text_height, char_width_factor?} for fits_titleblock_cell - the caller's titleblock cell geometry in the call's units; the fit is an ESTIMATE (characters x text_height x char_width_factor, default 0.6) and an overflowing value is a finding, never trimmed or renamed."" }
          }, ""additionalProperties"": false }
        }, ""additionalProperties"": false } }
    }, ""additionalProperties"": false, ""description"": ""INLINE only. This command takes no file path: a read-only auditor that opens arbitrary paths is a file reader wearing an auditor's name. Malformed sets are REFUSED whole - a half-loaded set that then passes is the lie this refusal exists to stop."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"", ""description"": ""Units of every reported length AND of minimum_gap/inside_extent values in the requirement set."" },
    ""max_findings"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 100 },
    ""cursor"": { ""type"": ""string"" },
    ""include_advisory"": { ""type"": ""boolean"", ""default"": true },
    ""include_passed_checks"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Add a status=passed finding for each check that examined a non-empty population and found nothing. A check that examined nothing is never passed."" }
  },
  ""allOf"": [
    { ""if"": { ""properties"": { ""scope"": { ""const"": ""sheets"" } }, ""required"": [""scope""] }, ""then"": { ""not"": { ""required"": [""view_ids""] } } },
    { ""if"": { ""properties"": { ""scope"": { ""const"": ""views"" } }, ""required"": [""scope""] }, ""then"": { ""not"": { ""required"": [""sheet_ids""] } } }
  ],
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_fix_planimetry",
                Command = "horizun_fix_planimetry",
                Description =
                    "Turn findings from horizun_audit_planimetry into TYPED, rehearsed, confirmed, atomic and " +
                    "re-read corrections. Ten operations, closed: set_view_template (explicit template " +
                    "ElementId, validated as a compatible ViewTemplate, ViewTemplateId re-read), set_view_scale " +
                    "(explicit 1..24000, refused for views that take no scale), set_view_display (detail level/discipline, " +
                    "refused when the view's template controls them), rename_view and rename_sheet " +
                    "(explicit final name/number, duplicates refused before the transaction, both re-read), " +
                    "place_title_block (explicit sheet and title-block FamilySymbol, placeholders and wrong " +
                    "categories refused, symbol activated safely, instance/family/type/sheet re-read, never a " +
                    "second title block), move_viewport and move_schedule (explicit final point in SHEET " +
                    "coordinates, GetBoxCenter/Point re-read within a declared tolerance), " +
                    "clear_element_override (explicit view and element, ONLY that element's override cleared, " +
                    "OverrideGraphicSettings re-read as defaults, category and template overrides proven " +
                    "untouched), set_crop (crop.min/max for a rectangle OR crop.loop for a polygon, view-plane " +
                    "coordinates with declared units, crop active/visible/geometry re-read vertex by vertex; " +
                    "refused by name on a view whose CanHaveShape is false). EVERY " +
                    "action cites the finding it corrects - rule id, requirement set and version, element ids, " +
                    "sheet/view, and the OBSERVED state - and is refused when the finding no longer exists " +
                    "(stale finding), the observed state moved (stale observation), or the finding is unknown/" +
                    "not covered: an unmeasured fact is never corrected. Findings from an inline requirement " +
                    "set require that set inline, and its canonical SHA-256 must equal the one the finding " +
                    "cites - a modified set is refused whole. The dry run materialises the whole batch " +
                    "provisionally inside a transaction and rolls it back; the confirmation token binds the " +
                    "request AND the resolved elements' before-state, so a model that moves refuses as " +
                    "stale_plan. Apply commits ONE TransactionGroup - any action or postcondition that fails while " +
                    "the group is still open rolls the entire batch back - then re-reads every promised " +
                    "property from the committed model. A re-read that contradicts the reversible-state " +
                    "check AFTER assimilation is reported as uncertain, never as partly applied: there is " +
                    "nothing left to roll back and two measurements in contradiction are the absence of " +
                    "knowledge. Finally it RE-RUNS the audit rules: the reply separates findings resolved (the rule " +
                    "stopped producing them), persistent, and NEW, with coverage before and after, because " +
                    "resolving one finding must not hide that another appeared. No PDF or export is read or " +
                    "written anywhere on this path, and no Python is involved: an invalid, ambiguous, stale or " +
                    "failed action never grants the fallback; only a whole-batch true capability absence does, " +
                    "with nothing written. Automatic packing, auto-tagging, intent dimensioning and revision generation are delegated " +
                    "to their dedicated typed surfaces: horizun_pack_sheets, horizun_plan_annotations plus " +
                    "horizun_annotate, and horizun_manage_revisions. Visual judgement runs from direct " +
                    "horizun_capture_view images through the planimetry-review MCP prompt. This finding-driven " +
                    "fixer still refuses every implicit choice of type, position, name or standard.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""source_audit"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"", ""description"": ""REQUIRED. Title or full path of the document to change. It must be the document ACTIVE in Revit."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"", ""description"": ""Units of every point, crop box and tolerance in this call."" },
    ""tolerance"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Geometric postcondition tolerance in the call's units. Default 0.1 mm: a committed point or crop edge must land within this distance of the request."" },
    ""source_audit"": { ""type"": ""object"", ""required"": [""finding_set_fingerprint""], ""properties"": {
      ""finding_set_fingerprint"": { ""type"": ""string"", ""minLength"": 8, ""maxLength"": 64, ""description"": ""The finding_set_fingerprint of the horizun_audit_planimetry reply these findings were copied from. Provenance: it is echoed back and compared against this call's own recomputation, and the comparison is reported - the per-action staleness gates are what refuse a moved model."" },
      ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"", ""description"": ""The units the source audit ran with. The observed evidence you copied is in these units, and the staleness comparison recomputes findings in them."" }
    }, ""additionalProperties"": false },
    ""requirement_set"": { ""type"": ""object"", ""description"": ""INLINE, same schema as horizun_audit_planimetry. REQUIRED when any action's finding cites a set other than horizun-universal-planimetry; its canonical SHA-256 must equal the requirement_set_sha256 each such finding cites, or the whole call is refused - a fix judged by different rules than the audit is not a fix."" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": {
      ""type"": ""object"", ""required"": [""operation"", ""finding""],
      ""properties"": {
        " + IdentityFields("first of element_id, viewport_id, sheet_id, view_id") + @"""operation"": { ""type"": ""string"", ""enum"": [""set_view_template"", ""set_view_scale"", ""rename_view"", ""rename_sheet"", ""place_title_block"", ""move_viewport"", ""move_schedule"", ""clear_element_override"", ""set_crop"", ""set_view_display""] },
        ""finding"": { ""type"": ""object"", ""required"": [""rule_id"", ""requirement_set"", ""requirement_set_version"", ""element_ids"", ""observed""], ""properties"": {
          ""rule_id"": { ""type"": ""string"", ""minLength"": 1 },
          ""requirement_set"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""The set id the finding cites: horizun-universal-planimetry or the inline set's id."" },
          ""requirement_set_version"": { ""type"": ""string"", ""minLength"": 1 },
          ""requirement_set_sha256"": { ""type"": ""string"", ""description"": ""Required for requirement-set findings; copied from the finding."" },
          ""entity_kind"": { ""type"": ""string"", ""description"": ""Copied from the finding. Required for requirement-set findings, where it is what judges operation compatibility."" },
          ""sheet_id"": { ""type"": [""integer"", ""null""] },
          ""view_id"": { ""type"": [""integer"", ""null""] },
          ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""integer"" } },
          ""observed"": { ""type"": ""object"", ""description"": ""The finding's observed block, VERBATIM. The fix recomputes the finding and refuses as a stale observation when the model no longer shows this state."" }
        }, ""additionalProperties"": false },
        ""view_id"": { ""type"": ""integer"", ""description"": ""set_view_template / set_view_scale / set_view_display / rename_view / set_crop: the view to change. clear_element_override: the view whose element override is cleared."" },
        ""template_id"": { ""type"": ""integer"", ""description"": ""set_view_template: the ViewTemplate's ElementId. Never resolved from a name."" },
        ""scale"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 24000 },
        ""detail_level"": { ""type"": ""string"", ""enum"": [""Coarse"", ""Medium"", ""Fine""] },
        ""discipline"": { ""type"": ""string"", ""description"": ""ViewDiscipline name, e.g. Architectural."" },
        ""new_name"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""rename_view / rename_sheet: the explicit final name."" },
        ""new_number"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""rename_sheet: the explicit final sheet number."" },
        ""sheet_id"": { ""type"": ""integer"", ""description"": ""rename_sheet / place_title_block: the sheet."" },
        ""title_block_type_id"": { ""type"": ""integer"", ""description"": ""place_title_block: the title-block FamilySymbol's ElementId."" },
        ""viewport_id"": { ""type"": ""integer"" },
        ""schedule_instance_id"": { ""type"": ""integer"" },
        ""element_id"": { ""type"": ""integer"", ""description"": ""clear_element_override: the element whose per-view override is cleared."" },
        ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" }, ""description"": ""move_viewport / move_schedule: the final box centre / placement point in SHEET coordinates, in the call's units."" },
        ""crop"": { ""type"": ""object"", ""properties"": {
          ""min"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } },
          ""max"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } },
          ""loop"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 200, ""items"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } },
            ""description"": ""A non-rectangular crop: a closed polygon, at least 3 [x, y] view-plane points. Refused on a view whose ViewCropRegionShapeManager.CanHaveShape is false."" }
        }, ""additionalProperties"": false, ""description"": ""set_crop: EITHER min+max (a rectangle) OR loop (a polygon), in VIEW-PLANE coordinates (x along RightDirection, y along UpDirection from the view origin), in the call's units."" }
      }, ""additionalProperties"": false } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true, ""description"": ""True (default): validate, materialise the whole batch provisionally, verify, roll back, and return the plan with a confirmation token. Nothing persists."" },
    ""confirmation_token"": { ""type"": ""string"", ""description"": ""From the dry run. Single use; bound to this document, this request and the resolved elements' before-state."" },
    ""transaction_name"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_pack_sheets",
                Command = "horizun_pack_sheets",
                Description =
                    "Automatically pack an ORDERED set of unplaced views/schedules and existing viewport/schedule " +
                    "placements onto one explicit sheet. The caller supplies sheet, priority order, margin and gap; " +
                    "the deterministic upper-left packer chooses every coordinate, preserves order, treats every " +
                    "unselected placement as a fixed obstacle and refuses the whole plan when one item cannot fit. " +
                    "A dry run materialises the complete arrangement in Revit, verifies actual viewport+label and " +
                    "schedule extents, containment and clearance, then rolls it back. Confirmation binds the sheet, " +
                    "source identities, paper outlines, fixed obstacles and geometry. Apply verifies while one " +
                    "TransactionGroup is reversible and rolls the whole arrangement back on any mismatch.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""items""],
  ""oneOf"": [{ ""required"": [""sheet_id""] }, { ""required"": [""sheets""] }],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""sheet_id"": { ""type"": ""integer"" },
    ""sheets"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 30, ""items"": { ""type"": ""object"", ""required"": [""sheet_id""], ""description"": ""Ordered existing sheet candidates; optional usable_rect, reserved_zones, margin, gap, tolerance. Global items must be unplaced views/schedules. First-fit at unchanged scales; refuses if the complete set cannot fit. The resulting placements apply as one atomic plan."" } },
    ""usable_rect"": { ""type"": ""array"", ""minItems"": 4, ""maxItems"": 4, ""items"": { ""type"": ""number"" }, ""description"": ""Paper [minX,minY,maxX,maxY] in units, inside the sheet. Margin applies inside this rectangle."" },
    ""reserved_zones"": { ""type"": ""array"", ""maxItems"": 100, ""items"": { ""type"": ""array"", ""minItems"": 4, ""maxItems"": 4, ""items"": { ""type"": ""number"" } }, ""description"": ""Paper rectangles reserved for titleblock bands, legends and other fixed graphics; never inferred from the titleblock's whole-sheet box."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""margin"": { ""type"": ""number"", ""minimum"": 0, ""default"": 10, ""description"": ""Clear paper margin on all four sheet edges."" },
    ""gap"": { ""type"": ""number"", ""minimum"": 0, ""default"": 10, ""description"": ""Minimum clearance between actual placement extents, including viewport labels."" },
    ""tolerance"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 0.1 },
    ""items"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": {
      ""type"": ""object"", ""required"": [""key""], ""properties"": {
        ""key"": { ""type"": ""string"", ""minLength"": 1 },
        ""view_id"": { ""type"": ""integer"", ""description"": ""Unplaced graphical view."" },
        ""schedule_id"": { ""type"": ""integer"", ""description"": ""Unplaced non-revision schedule."" },
        ""viewport_id"": { ""type"": ""integer"", ""description"": ""Existing viewport on this sheet to repack."" },
        ""schedule_instance_id"": { ""type"": ""integer"", ""description"": ""Existing schedule placement on this sheet to repack."" }
      }, ""oneOf"": [
        { ""required"": [""view_id""] }, { ""required"": [""schedule_id""] },
        { ""required"": [""viewport_id""] }, { ""required"": [""schedule_instance_id""] }
      ], ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_plan_annotations",
                Command = "horizun_plan_annotations",
                Description =
                    "Read-only production planner. auto_tags takes explicit elements and chooses deterministic " +
                    "collision-aware tag-head points in one view, skips targets already tagged by default, names " +
                    "incomplete bounding-box coverage and returns a complete horizun_annotate dry-run request. " +
                    "intent_dimension obtains semantic stable references from horizun_get_dimension_references, " +
                    "requires exactly one compatible unambiguous reference per target, resolves axis and signed " +
                    "offset deterministically, orders the chain and returns a complete horizun_annotate dry-run " +
                    "request. auto_dimension_grids / auto_dimension_levels / auto_dimension_curtain_walls / " +
                    "auto_dimension_openings plan WHOLE chains: candidates are collected semantically (grid and " +
                    "level datums, curtain U/V grid lines, opening CenterLeftRight references) from the HOST " +
                    "(a link instance refuses with the measured datum-rejection reason), grouped into parallel " +
                    "families with a stated 0.5-degree " +
                    "tolerance, ordered positionally, deduplicated against dimensions already in the view by " +
                    "unordered reference-set identity, and stacked on the chosen side. Every reference left out " +
                    "carries a structured code; coverage answers complete/partial/none/nothing_found and is never " +
                    "optimistic. This command NEVER writes: horizun_annotate remains the single " +
                    "provisional-create, confirmation and host-verification path.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation"", ""view_id""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""auto_tags"", ""model_notes"", ""intent_dimension"", ""dimension_set"", ""auto_dimension_grids"", ""auto_dimension_levels"", ""auto_dimension_curtain_walls"", ""auto_dimension_openings"", ""auto_dimension_heights"", ""auto_dimension_room"", ""dimension_mode""] },
    ""mode"": { ""type"": ""string"", ""enum"": [""wall_thickness"", ""opening_width"", ""opening_height"", ""room_clear_width"", ""grid_to_grid"", ""chain_along_line"", ""level_heights"", ""spot_elevation""] },
    ""line_start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
    ""line_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
    ""room_id"": { ""type"": ""integer"", ""description"": ""auto_dimension_room: a closed rectangular Room with four wall sides aligned to the requested plan view."" },
    ""interior_offset"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
    ""exterior_offset"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
    ""thickness_offset"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
    ""layout_margin"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""auto_dimension_room: chains may move only inside the room box (interior) or the room box grown by this; one that does not fit is dropped and reported."" },
    ""annotation_clearance"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 10, ""description"": ""auto_dimension_room: reserved half-width around each proposed dimension line while avoiding existing annotation boxes."" },
    ""expected_values"": { ""type"": ""object"", ""description"": ""auto_dimension_room: measured postconditions for the two interior and four exterior total chains, in units."", ""properties"": { ""interior_x"": { ""type"": ""number"" }, ""interior_y"": { ""type"": ""number"" }, ""exterior_x_top"": { ""type"": ""number"" }, ""exterior_x_bottom"": { ""type"": ""number"" }, ""exterior_y_left"": { ""type"": ""number"" }, ""exterior_y_right"": { ""type"": ""number"" } }, ""additionalProperties"": false },
    ""level_id"": { ""type"": ""integer"", ""description"": ""auto_dimension_heights: one datum level, including an auxiliary level previously created with horizun_create_elements."" },
    ""height_selector"": { ""type"": ""string"", ""enum"": [""top_face"", ""bottom_face"", ""opening_head"", ""opening_sill""], ""default"": ""top_face"" },
    ""expected_value"": { ""type"": ""number"", ""description"": ""auto_dimension_heights with one target: forwarded as the postcondition on the measured height."" },
    ""expected_tolerance"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
    ""distance_space"": { ""type"": ""string"", ""enum"": [""model"", ""paper""], ""default"": ""model"", ""description"": ""Applies scale to offset, chain_separation, clearance and max_displacement only; coordinates/probe points remain model-space units."" },
    ""max_displacement"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""auto_tags: approved maximum displacement from the proposed seed during native measured layout. Defaults to 1200 mm in model distance_space or 30 mm in paper distance_space, converted to units (every layout default is millimetres: offset 15, chain_separation 15, clearance 10)."" },
    ""reference_targets"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 32, ""items"": { ""type"": ""object"", ""required"": [""element_id"", ""selector""], ""additionalProperties"": false, ""properties"": {
      ""element_id"": { ""type"": ""integer"" }, ""selector"": { ""type"": ""string"" }, ""reference_name"": { ""type"": ""string"" }, ""probe_point"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } }
    } }, ""description"": ""intent_dimension: per-reference selectors; can name exterior/interior faces of the same wall to measure thickness. Mutually exclusive with element_ids."" },
    ""sets"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 30, ""items"": { ""type"": ""object"", ""required"": [""role"", ""operation"", ""offset"", ""side"", ""dimension_type_id""], ""description"": ""Named general/partial/thickness/opening set with intent_dimension or an auto_dimension operation and its normal arguments. Explicit reference selectors and targets define the criterion; duplicate reference sets or partial coverage refuse the whole set."" } },
    ""view_id"": { ""type"": ""integer"" },
    ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""integer"" }, ""description"": ""auto_tags: up to 500; omit to sweep the view. intent_dimension: 2..32; duplicates are refused. auto_dimension_*: OPTIONAL explicit subset - omit to sweep the view (host) or the linked document (with link_instance_id); with link_instance_id these are ids INSIDE the linked document."" },
    ""link_instance_id"": { ""type"": ""integer"", ""description"": ""auto_dimension_*: REFUSED with the measured reason. Revit's dimension API rejects datum references lifted through a link (measured live 2026-08-26: 'Invalid number of references', while linked wall FACES construct), and curtain/opening references are datum-backed. Dimension linked geometry via horizun_get_dimension_references linked_targets + horizun_annotate instead."" },
    ""chain_separation"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""auto_dimension_*: distance between successive stacked chains on the same side, in units. Defaults to offset."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""tag_type_id"": { ""type"": ""integer"", ""description"": ""auto_tags: explicit type forwarded to horizun_annotate, which proves validity and verifies the committed type."" },
    ""tag_mode"": { ""type"": ""string"", ""enum"": [""by_category"", ""multi_category"", ""material""], ""default"": ""by_category"" },
    ""orientation"": { ""type"": ""string"", ""enum"": [""horizontal"", ""vertical""], ""default"": ""horizontal"" },
    ""add_leader"": { ""type"": ""boolean"", ""default"": true },
    ""skip_existing"": { ""type"": ""boolean"", ""default"": true },
    ""categories"": { ""type"": ""array"", ""maxItems"": 100, ""items"": { ""type"": ""string"" } },
    ""include_links"": { ""type"": ""boolean"" },
    ""accept_unmeasurable"": { ""type"": ""array"", ""maxItems"": 200, ""items"": { ""type"": ""integer"" }, ""description"": ""auto_tags: element ids of annotations this plan accepts as unmeasurable. An annotation whose extent cannot be read in the view AND which Revit's own view-scoped visible-element collector still lists as visible blocks planning, with its ids in annotation_coverage.blocking - not measuring something never makes it absent. Naming those exact ids here records the acceptance, reports clearance_scope 'partial' instead of 'complete', and forwards the same list into the returned horizun_annotate request."" },
    ""clearance"": { ""type"": ""number"", ""minimum"": 0, ""default"": 10, ""description"": ""auto_tags: search step and annotation clearance in units."" },
    ""selector"": { ""type"": ""string"", ""enum"": [""centerline"", ""grid"", ""level"", ""reference_plane"", ""endpoint"", ""exterior_face"", ""interior_face"", ""top_face"", ""bottom_face"", ""nearest_face"", ""farthest_face"", ""opening_left"", ""opening_right"", ""opening_center"", ""opening_head"", ""opening_sill"", ""opening_front_back_center"", ""family_named_reference""] , ""default"": ""centerline"" },
    ""reference_name"": { ""type"": ""string"", ""description"": ""intent_dimension with family_named_reference: exact family reference-plane name."" },
    ""probe_point"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Required by nearest_face/farthest_face; forwarded to reference discovery."" },
    ""axis"": { ""type"": ""string"", ""enum"": [""auto"", ""horizontal"", ""vertical""], ""default"": ""auto"" },
    ""side"": { ""type"": ""string"", ""enum"": [""positive"", ""negative""], ""default"": ""positive"" },
    ""offset"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 15, ""description"": ""intent_dimension: perpendicular distance from the outermost selected reference, in units."" },
    ""dimension_type_id"": { ""type"": ""integer"" },
    ""text_type_id"": { ""type"": ""integer"" },
    ""note_source"": { ""type"": ""string"", ""enum"": [""layers"", ""params""], ""default"": ""layers"" },
    ""omit_source_id"": { ""type"": ""boolean"", ""default"": false }
  },
  ""oneOf"": [
    { ""properties"": { ""operation"": { ""const"": ""auto_tags"" } }, ""description"": ""Plan collision-aware tags for element_ids, or sweep every taggable element in the view."" },
    { ""properties"": { ""operation"": { ""const"": ""model_notes"" } }, ""required"": [""element_ids"", ""text_type_id""], ""description"": ""Text notes from layers or params, with source id."" },
    { ""properties"": { ""operation"": { ""const"": ""intent_dimension"" } }, ""description"": ""Plan a dimension from element_ids or explicit reference_targets; choose opening_left/opening_right for an opening width."" },
    { ""properties"": { ""operation"": { ""const"": ""dimension_set"" } }, ""required"": [""sets""], ""description"": ""Plan a named collection of complete dimension roles."" },
    { ""properties"": { ""operation"": { ""const"": ""auto_dimension_grids"" } }, ""description"": ""Plan visible host grid chains."" },
    { ""properties"": { ""operation"": { ""const"": ""auto_dimension_levels"" } }, ""description"": ""Plan visible host level chains; use auto_dimension_heights for one level and one face."" },
    { ""properties"": { ""operation"": { ""const"": ""auto_dimension_curtain_walls"" } }, ""description"": ""Plan visible curtain U/V grid chains."" },
    { ""properties"": { ""operation"": { ""const"": ""auto_dimension_openings"" } }, ""description"": ""Plan each opening width and its host wall opening/pier chain; inspect omitted before applying."" },
    { ""properties"": { ""operation"": { ""const"": ""auto_dimension_heights"" } }, ""required"": [""level_id"", ""element_ids""], ""description"": ""Plan level-to-face and level-to-opening heights."" },
    { ""properties"": { ""operation"": { ""const"": ""auto_dimension_room"" } }, ""required"": [""room_id""], ""description"": ""Plan a rectangular room's interior, exterior, thickness and opening chains."" },
    { ""properties"": { ""operation"": { ""const"": ""dimension_mode"" } }, ""required"": [""mode""], ""description"": ""wall_thickness/chain_along_line/spot planned here, others delegate; expected_value from geometry."" }
  ],
  ""allOf"": [
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""auto_tags"" } } }, ""then"": { ""properties"": { ""element_ids"": { ""maxItems"": 500 } } } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""auto_dimension_heights"" } } }, ""then"": { ""required"": [""level_id"", ""element_ids""] } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""auto_dimension_room"" } } }, ""then"": { ""required"": [""room_id""] } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""intent_dimension"" } } }, ""then"": { ""properties"": { ""element_ids"": { ""minItems"": 2, ""maxItems"": 32 } }, ""oneOf"": [{ ""required"": [""element_ids""] }, { ""required"": [""reference_targets""] }] } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""dimension_set"" } } }, ""then"": { ""required"": [""sets""] } },
    { ""if"": { ""properties"": { ""selector"": { ""enum"": [""nearest_face"", ""farthest_face""] } }, ""required"": [""selector""] }, ""then"": { ""required"": [""probe_point""] } }
  ],
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_schedules",
                Command = "horizun_manage_schedules",
                Description =
                    "Create material takeoffs, sheet/view lists, revision and keynote schedules; edit any schedule in one " +
                    "atomic verified batch: fields, totals, filters, sorting, options, rename, duplicate, appearance " +
                    "(text types, fonts, colours, borders, grid, title), split/unsplit. Filters and sorting declare the " +
                    "whole list. The token binds the definition: a colleague's edit refuses as stale_plan. Revit has no " +
                    "API for conditional formatting. Plain schedules: horizun_create_schedule; placement: horizun_manage_views.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": {
      ""type"": ""object"", ""required"": [""operation""], ""properties"": {
        ""operation"": { ""type"": ""string"", ""enum"": [""create"", ""duplicate"", ""rename"", ""add_fields"", ""remove_fields"", ""set_field"", ""set_filters"", ""set_sorting"", ""set_options"", ""set_appearance"", ""split"", ""unsplit""] },
        ""key"": { ""type"": ""string"", ""description"": ""Alias for a schedule created here; target it with schedule_key."" },
        ""kind"": { ""type"": ""string"", ""enum"": [""material_takeoff"", ""sheet_list"", ""view_list"", ""revision_schedule"", ""keynote_legend""], ""description"": ""create only."" },
        ""category"": { ""type"": ""string"", ""description"": ""create material_takeoff: OST_ token or display name."" },
        ""name"": { ""type"": ""string"" },
        ""schedule_id"": { ""type"": ""integer"" }, ""schedule_key"": { ""type"": ""string"" },
        ""fields"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""object"", ""properties"": {
          ""parameter_id"": { ""type"": ""integer"" }, ""name"": { ""type"": ""string"" }, ""field_type"": { ""type"": ""string"", ""description"": ""ScheduleFieldType, e.g. ProjectInfo."" }, ""field_index"": { ""type"": ""integer"", ""minimum"": 0 }
        }, ""additionalProperties"": false } },
        ""parameter_id"": { ""type"": ""integer"", ""description"": ""set_field: the field's stable identity."" },
        ""field_index"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""set_field: position, if ids collide."" },
        ""heading"": { ""type"": ""string"" }, ""hidden"": { ""type"": ""boolean"" },
        ""totals"": { ""type"": ""boolean"", ""description"": ""set_field: does this column fill the total rows."" },
        ""width"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""maximum"": 10000, ""description"": ""set_field: sheet column width, mm."" },
        ""alignment"": { ""type"": ""string"", ""enum"": [""left"", ""center"", ""right""], ""description"": ""set_field: horizontal alignment of the column."" },
        ""format"": { ""type"": ""object"", ""additionalProperties"": false, ""properties"": {
          ""unit_type_id"": { ""type"": ""string"", ""description"": ""e.g. autodesk.unit.unit:millimeters"" },
          ""accuracy"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Rounding step, above zero."" },
          ""suppress_trailing_zeros"": { ""type"": ""boolean"" },
          ""rounding"": { ""type"": ""string"", ""enum"": [""Nearest"", ""Up"", ""Down""] }
        }, ""description"": ""set_field: number format of the column."" },
        ""filters"": { ""type"": ""array"", ""maxItems"": 30, ""items"": { ""type"": ""object"", ""required"": [""operator""], ""properties"": {
          ""parameter_id"": { ""type"": ""integer"" }, ""field"": { ""type"": ""string"" },
          ""operator"": { ""type"": ""string"", ""enum"": [""equal"", ""not_equal"", ""greater_than"", ""greater_than_or_equal"", ""less_than"", ""less_than_or_equal"", ""contains"", ""not_contains"", ""begins_with"", ""not_begins_with"", ""ends_with"", ""not_ends_with"", ""has_value"", ""has_no_value""] },
          ""value"": { ""type"": [""string"", ""integer""], ""description"": ""Text; for ElementId fields a name or id."" }, ""number_value"": { ""type"": ""number"", ""description"": ""Id, or mm / m2 / m3 / unitless."" }
        }, ""additionalProperties"": false } },
        ""sorting"": { ""type"": ""array"", ""maxItems"": 20, ""items"": { ""type"": ""object"", ""properties"": {
          ""parameter_id"": { ""type"": ""integer"" }, ""field"": { ""type"": ""string"" },
          ""direction"": { ""type"": ""string"", ""enum"": [""ascending"", ""descending""], ""default"": ""ascending"" },
          ""header"": { ""type"": ""boolean"" }, ""footer"": { ""type"": ""boolean"" }, ""blank_line"": { ""type"": ""boolean"" }
        }, ""additionalProperties"": false } },
        ""itemized"": { ""type"": ""boolean"" }, ""grand_total"": { ""type"": ""boolean"" }, ""headers"": { ""type"": ""boolean"" },
        ""include_links"": { ""type"": ""boolean"", ""description"": ""set_options: include elements in links."" },
        ""scope"": { ""type"": ""string"", ""enum"": [""title"", ""headers"", ""body"", ""field""], ""description"": ""set_appearance: region; field = one column."" },
        ""text_type"": { ""type"": [""string"", ""integer""], ""description"": ""set_appearance: text type of the region."" },
        ""style"": { ""type"": ""object"", ""additionalProperties"": false, ""properties"": {
          ""font"": { ""type"": ""string"" }, ""size_pt"": { ""type"": ""number"" },
          ""bold"": { ""type"": ""boolean"" }, ""italic"": { ""type"": ""boolean"" }, ""underline"": { ""type"": ""boolean"" },
          ""color"": { ""type"": ""string"" }, ""fill"": { ""type"": ""string"" },
          ""h_align"": { ""type"": ""string"", ""enum"": [""left"", ""center"", ""right""] },
          ""v_align"": { ""type"": ""string"", ""enum"": [""top"", ""middle"", ""bottom""] },
          ""border"": { ""type"": ""object"", ""additionalProperties"": false, ""properties"": { ""top"": { ""type"": [""string"", ""integer""] }, ""bottom"": { ""type"": [""string"", ""integer""] }, ""left"": { ""type"": [""string"", ""integer""] }, ""right"": { ""type"": [""string"", ""integer""] } } }
        }, ""description"": ""set_appearance: only named attributes; #RRGGBB."" },
        ""show_grid"": { ""type"": ""boolean"" }, ""show_title"": { ""type"": ""boolean"" },
        ""parts"": { ""type"": ""integer"", ""description"": ""split: equal segments."" },
        ""heights_mm"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""description"": ""split: height of each segment but the last."" }
      }, ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_plan_views",
                Command = "horizun_plan_views",
                Description =
                    "Read-only planner for per-room deliverable views. room_views measures the placed, enclosed " +
                    "rooms of one plan view's level (or an explicit room/level selection), derives each room's " +
                    "principal wall direction from its longest boundary segment, and plans interior elevations " +
                    "(1-4, markers at the room centre, rotated by the smallest turn the marker's 90-degree " +
                    "symmetry allows), two crossing sections with extents computed from the room's exact bounding " +
                    "box plus a margin, and a cropped duplicate of the plan - all named from a caller-supplied " +
                    "token pattern ({room_name}, {room_number}, {level}, {kind}, {index}) where an unknown token " +
                    "refuses rather than passing through. Rooms not placed, not enclosed/redundant, without a " +
                    "readable boundary or box, or whose planned names collide with existing views are excluded " +
                    "WHOLE with a structured code - half a room's views is not a deliverable. Returns a complete " +
                    "horizun_manage_views dry-run request plus per-room rows, exclusions and a " +
                    "complete/partial/none coverage verdict. deliverable_set instead emits a staged client workflow " +
                    "for dimensions, tags, packing, audit, visual approval and PDF publication over existing IDs; " +
                    "each write needs fresh rehearsal and confirmation. This command NEVER writes.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""allOf"": [
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""room_views"" } } }, ""then"": { ""required"": [""plan_view_id""] } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""deliverable_set"" } } }, ""then"": { ""required"": [""delivery_profile""] } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""delivery_open"" } } }, ""then"": { ""required"": [""delivery_profile""] } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""delivery_status"" } } }, ""then"": { ""required"": [""delivery_id""] } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""delivery_record"" } } }, ""then"": { ""required"": [""delivery_id"", ""stage_key"", ""status""] } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""delivery_approve"" } } }, ""then"": { ""required"": [""delivery_id"", ""stage_key"", ""identity"", ""decision""] } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""delivery_invalidate"" } } }, ""then"": { ""required"": [""delivery_id"", ""stage_key"", ""reason""] } }
  ],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""room_views"", ""deliverable_set"", ""delivery_open"", ""delivery_status"", ""delivery_record"", ""delivery_approve"", ""delivery_invalidate""], ""description"": ""room_views and deliverable_set plan. The delivery_* operations keep THE DELIVERY LEDGER - one append-only event file per delivery under the data root - and never write the model: delivery_open runs the full preflight and opens the ledger from the plan (refused if any stage is known-invalid, or if the delivery already exists); delivery_status replays the ledger, re-reads every recorded scope (Element.VersionGuid) and invalidates what changed, then reports next_stage, completed writes (never replayed), needs_attention and the publish gate; delivery_record moves one stage (in_progress/completed/failed/blocked/awaiting_approval/pending) - a completed write must name its idempotency_key and element_ids, which the host reads back into a scope; delivery_approve binds a named identity's approved/rejected decision to a sheet's current scope; delivery_invalidate names a reason and cascades to everything built on the stage. horizun_export with delivery_id is the publish stage and refuses while the gate is closed."" },
    ""delivery_id"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 120, ""description"": ""delivery_*: the ledger's name. Optional on delivery_open (derived from the profile hash and document fingerprint); required otherwise."" },
    ""stage_key"": { ""type"": ""string"", ""description"": ""delivery_record / delivery_approve / delivery_invalidate: the plan stage key (view_<id>, dimensions_<id>, tags_<id>, capture_view_<id>, pack, audit, capture_sheet_<id>, publish)."" },
    ""status"": { ""type"": ""string"", ""enum"": [""in_progress"", ""completed"", ""failed"", ""blocked"", ""awaiting_approval"", ""pending""], ""description"": ""delivery_record: the state to move the stage to. Transitions are enforced by kind and by dependency order; approved/rejected/invalidated go through their own operations."" },
    ""facts"": { ""type"": ""object"", ""description"": ""delivery_record: what the stage's own reply carried - idempotency_key and element_ids for a completed write (read back into a VersionGuid scope by the host, refused if any element is missing), files for a completed capture/publish (hashed by the host, refused if missing), no_blocking_findings (boolean) and finding_set_fingerprint for a completed audit, reason for failed/blocked."" },
    ""identity"": { ""type"": ""string"", ""description"": ""delivery_approve: who approved or rejected. Required; an anonymous approval is not one."" },
    ""decision"": { ""type"": ""string"", ""enum"": [""approved"", ""rejected""], ""description"": ""delivery_approve."" },
    ""note"": { ""type"": ""string"", ""description"": ""delivery_approve: free text recorded with the decision."" },
    ""reason"": { ""type"": ""string"", ""description"": ""delivery_invalidate: why; recorded on every cascaded stage."" },
    ""delivery_profile"": { ""type"": ""object"", ""required"": [""id"", ""version"", ""units"", ""views"", ""packing"", ""publication"", ""requirement_set""], ""description"": ""Staged client workflow, not an executor. views:[{view_id,dimension_sets?,tags?}], packing: pack_sheets arguments, publication: PDF export arguments with sheet view_ids, requirement_set: inline planimetry requirements. Existing IDs only: create room views first and resolve actual IDs. Emits ordered activation, annotation, packing, audit, visual review and publication stages with fresh rehearsal barriers."" },
    ""plan_view_id"": { ""type"": ""integer"", ""description"": ""The plan view elevation markers anchor in and the cropped plans duplicate. Its generating level is the default room selection."" },
    ""room_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""integer"" }, ""description"": ""Explicit rooms. Exactly one of room_ids or level_id, or neither for the plan view's own level."" },
    ""level_id"": { ""type"": ""integer"", ""description"": ""Every room of this level."" },
    ""kinds"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""elevations"", ""sections"", ""plan""] }, ""description"": ""What to produce per room. Omit for all three."" },
    ""elevation_count"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 4, ""default"": 4 },
    ""marker_clearance"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 200, ""description"": ""Half-width of each proposed marker's clear square, in units. The native marker glyph still needs dry-run and visual verification."" },
    ""orient_to_walls"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true rotates each room's markers/sections to its principal wall (a room with no readable boundary is then excluded, coded); false keeps everything cardinal."" },
    ""name_pattern"": { ""type"": ""string"", ""default"": ""{short_kind} - {room_number}"", ""description"": ""Tokens: room_name, room_number, level, kind, short_kind, index. short_kind renders Corte A-A, Alzado 1 or Planta in Spanish."" },
    ""margin"": { ""type"": ""number"", ""minimum"": 0, ""default"": 500, ""description"": ""Clearance added around the room box for crops, section runs and depths, in units."" },
    ""scale"": { ""type"": ""number"", ""minimum"": 1, ""maximum"": 24000, ""default"": 50, ""description"": ""Marker scale for created elevations."" },
    ""template_view_id"": { ""type"": ""integer"", ""description"": ""A view template applied to every planned view via apply_template actions."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_groups",
                Command = "horizun_manage_groups",
                Description =
                    "Find group/family duplicates; edit groups, change level/type, place patterns; report " +
                    "annotations. Dry run shows tags/dimensions by view; apply verifies. " +
                    "Regen needs consent. edit_type_members=Edit Group, scope=type, MEP links kept, other copies get new ids; big: submit_job. " +
                    "this_instance needs name: NEW type, floor split.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    " + IdentityFields("type_id") + @"""operation"": { ""type"": ""string"", ""enum"": [""list"", ""find_duplicates"", ""annotation_report"", ""create"", ""add_members"", ""remove_members"", ""rename_type"", ""duplicate_type"", ""swap_type"", ""change_group_type"", ""set_group_level"", ""place_group"", ""ungroup"", ""edit_type_members"", ""convert_to_link"", ""copy_to_levels"", ""check_variation""] },
    ""level_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""uniqueItems"": true, ""items"": { ""type"": ""integer"", ""minimum"": 1 }, ""description"": ""copy_to_levels: one source group, preserve offset; re-read every member height."" },
    ""tolerance_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 1, ""description"": ""check_variation bounds tolerance. Excludes parameters."" },
    ""relative_rotations_degrees"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""number"", ""minimum"": -360, ""maximum"": 360 }, ""description"": ""Known CCW angles from the reference for same-handed copies. check_variation: group_ids order after the first. add_members propagating to copies: required in ascending copy group id order."" },
    ""group_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""edits"": { ""type"": ""array"", ""description"": ""edit_type_members, mm: [{operation: move|set_curve|set_parameter, element_ids, vector | start+end | parameter+value}]"" },
    ""reconnect_stretch_mm"": { ""type"": ""number"" },
    ""accept_lost_connections"": { ""type"": ""boolean"" },
    ""type_id"": { ""type"": ""integer"" },
    ""level_id"": { ""type"": ""integer"" },
    ""keep_position"": { ""type"": ""boolean"", ""default"": true },
    ""pattern_group_id"": { ""type"": ""integer"" },
    ""point_mm"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""name"": { ""type"": ""string"" },
    ""scope"": { ""enum"": [""all_instances"", ""this_instance"", ""type""], ""description"": ""For add_members, type is an alias for all_instances. With other instances, this_instance requires a new type name."" },
    ""accept_member_regeneration"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Accept loss of Mark/Comments and changes to unowned groups, types or members in the add_members regeneration scope. Other deletions require verified ownership."" },
    ""max_rows"": { ""type"": ""integer"" },
    ""position_tolerance_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 1 },
    ""level_tolerance_mm"": { ""type"": ""number"", ""minimum"": 0, ""default"": 300 },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_worksets",
                Command = "horizun_manage_worksets",
                Description =
                    "User worksets on a workshared model (otherwise refused, code not_workshared, except enable): list (open, editable, " +
                    "owner, element count), status (read-only, element_ids/view_ids: workset, checkout, borrowed_by, creator, " +
                    "requested_by - nothing borrowed), loans (read-only: what I hold before a sync), create, rename, move_elements (element_ids or category; borrowed elements are " +
                    "reported, never forced), set_default (active workset), visibility per view. Dry run rehearses; apply " +
                    "needs the token; WorksetId and the table are re-read. create/rename/move_elements report " +
                    "ownership_effect (elements and the workset newly owned by the caller - renaming can silently take " +
                    "ownership of thousands) and accept relinquish_after to give everything the caller owns back and " +
                    "re-measure. enable (the only op on a non-shared model): EnableWorksharing + SaveAs a NEW central at " +
                    "central_path; create_local: a new local of central_path at local_path; both re-read the file. borrow (element_ids, at most 500): check out exactly those elements from the central, never forcing one somebody else holds, and re-read every status; the loan goes back with horizun_save_document operation=sync_with_central relinquish=borrowed_only (or all) or relinquish_own.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""list"", ""status"", ""loans"", ""create"", ""rename"", ""move_elements"", ""set_default"", ""visibility"", ""enable"", ""create_local"", ""borrow""] },
    ""central_path"": { ""type"": ""string"" }, ""local_path"": { ""type"": ""string"" },
    ""levels_grids_name"": { ""type"": ""string"", ""description"": ""enable; name is the other workset."" },
    ""workset_id"": { ""type"": ""integer"" },
    ""name"": { ""type"": ""string"" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""category"": { ""type"": ""string"" },
    ""view_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""visibility"": { ""type"": ""string"", ""enum"": [""visible"", ""hidden"", ""use_global""] },
    ""relinquish_after"": { ""type"": ""boolean"", ""default"": false, ""description"": ""create/rename/move_elements only: after a verified apply, relinquish EVERYTHING the caller owns in the document (the same call horizun_relinquish_all makes) and re-measure this operation's own targets to report what remains owned."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_revisions",
                Command = "horizun_manage_revisions",
                Description =
                    "Create or update revisions, add them to or WITHDRAW them from explicit sheets, and create revision clouds in explicit " +
                    "views. Each action uses ids, never names; cloud vertices are view-plane coordinates. The dry " +
                    "run creates the complete batch provisionally, regenerates, verifies revision fields, sheet " +
                    "assignment and each cloud's revision/view, then reports Revit's rollback status. Apply spends " +
                    "a single-use confirmation and verifies the whole batch while one TransactionGroup remains " +
                    "reversible; a failed postcondition rolls everything back.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": {
      ""type"": ""object"", ""required"": [""key"", ""operation""], ""properties"": {
        ""key"": { ""type"": ""string"", ""minLength"": 1 },
        ""operation"": { ""type"": ""string"", ""enum"": [""create_revision"", ""update_revision""] },
        ""revision_id"": { ""type"": ""integer"", ""description"": ""Required by update_revision."" },
        ""description"": { ""type"": ""string"", ""description"": ""Required by create_revision."" },
        ""revision_date"": { ""type"": ""string"" }, ""issued_by"": { ""type"": ""string"" },
        ""issued_to"": { ""type"": ""string"" }, ""issued"": { ""type"": ""boolean"" },
        ""sheet_ids"": { ""type"": ""array"", ""maxItems"": 500, ""items"": { ""type"": ""integer"" }, ""description"": ""Add this revision to each sheet's additional revisions; existing assignments are preserved."" },
        ""remove_sheet_ids"": { ""type"": ""array"", ""maxItems"": 500, ""items"": { ""type"": ""integer"" }, ""description"": ""update_revision only: withdraw this revision from each sheet's ADDITIONAL revisions. A revision reaching a sheet through a CLOUD is named as such and refused - clouds are removed by deleting the cloud."" },
        ""clouds"": { ""type"": ""array"", ""maxItems"": 100, ""items"": {
          ""type"": ""object"", ""required"": [""view_id"", ""loops""], ""properties"": {
            ""view_id"": { ""type"": ""integer"" },
            ""loops"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 200, ""items"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } } } }
          }, ""additionalProperties"": false }
        }
      },
      ""allOf"": [
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""create_revision"" } } }, ""then"": { ""required"": [""description""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""update_revision"" } } }, ""then"": { ""required"": [""revision_id""] } }
      ], ""additionalProperties"": false }
    },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }, ""transaction_name"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_phases",
                Command = "horizun_manage_phases",
                Description =
                    "Phases, phase filters, design options. Reads: list (phases in order, filters with new/existing/demolished/" +
                    "temporary presentation, option sets/options/primary/active with members); element_status (created/demolished " +
                    "phase, ElementOnPhaseStatus in phase_id - default last - and design option). Writes, rehearsed then applied " +
                    "with the token and re-read, rolled back on any mismatch: set_element_phases (demolition never before " +
                    "creation; -1 clears it), create_phase_filter, edit_phase_filter, rename_phase. Revit's API cannot create or " +
                    "reorder phases nor move elements between design options: create_phase and assign_design_option refuse, saying why.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""list"", ""element_status"", ""set_element_phases"", ""create_phase_filter"", ""edit_phase_filter"", ""rename_phase"", ""create_phase"", ""assign_design_option""] },
    ""element_ids"": { ""type"": ""array"", ""maxItems"": 500, ""items"": { ""type"": ""integer"" } },
    ""phase_id"": { ""type"": ""integer"" }, ""created_phase_id"": { ""type"": ""integer"" },
    ""demolished_phase_id"": { ""type"": ""integer"", ""description"": ""-1 = not demolished."" },
    ""filter_id"": { ""type"": ""integer"" }, ""name"": { ""type"": ""string"" },
    ""presentation"": { ""type"": ""object"", ""description"": ""Keys new, existing, demolished, temporary."", ""additionalProperties"": { ""enum"": [""by_category"", ""overridden"", ""hidden""] } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_assemblies_parts",
                Command = "horizun_manage_assemblies_parts",
                Description =
                    "Parts and assemblies. list reads parts, assemblies and, for element_ids, their parts/assembly. Writes, " +
                    "rehearsed then applied with the token and re-read, rolled back on any mismatch: create_parts (elements valid " +
                    "for parts), divide_parts (parts cut by reference_ids: levels, grids, reference planes), exclude_parts, " +
                    "restore_parts, dissolve_parts (removes the parts of element_ids; originals stay), create_assembly (members, " +
                    "naming_category_id defaulting to the first member's, optional name), assembly_views (assembly views and " +
                    "part list), disassemble (removes the assembly; members stay).",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""list"", ""create_parts"", ""divide_parts"", ""exclude_parts"", ""restore_parts"", ""dissolve_parts"", ""create_assembly"", ""assembly_views"", ""disassemble""] },
    ""element_ids"": { ""type"": ""array"", ""maxItems"": 500, ""items"": { ""type"": ""integer"" } },
    ""reference_ids"": { ""type"": ""array"", ""maxItems"": 50, ""items"": { ""type"": ""integer"" } },
    ""assembly_id"": { ""type"": ""integer"" }, ""naming_category_id"": { ""type"": ""integer"" }, ""name"": { ""type"": ""string"" },
    ""views"": { ""type"": ""array"", ""items"": { ""enum"": [""3d"", ""plan"", ""section_a"", ""section_b"", ""elevation_front"", ""part_list""] } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_detail_2d",
                Command = "horizun_detail_2d",
                Description =
                    "Atomically create verified detail lines, arcs, polylines, filled/masking regions, detail " +
                    "components and annotation symbols; set line styles; draw_order restacks existing detail. Dry run rehearses in a rolled-back " +
                    "transaction and answers applied=false. Apply verifies every element after commit or rolls back the batch. Coordinates " +
                    "are in the view plane; out-of-plane geometry is refused. Delete with horizun_delete_verified.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": {
      ""type"": ""object"", ""required"": [""operation""],
      ""properties"": {
        " + IdentityFields("element_id, else view_id") + @"""operation"": { ""type"": ""string"", ""enum"": [""create_detail_line"", ""create_detail_arc"", ""create_detail_polyline"", ""create_filled_region"", ""create_masking_region"", ""place_detail_component"", ""place_symbol"", ""set_line_style"", ""draw_order""] },
        ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 1000, ""items"": { ""type"": ""integer"" }, ""description"": ""draw_order: existing detail elements to restack in view_id."" },
        ""order"": { ""type"": ""string"", ""enum"": [""front"", ""back"", ""forward"", ""backward""], ""description"": ""draw_order: Bring to Front, Send to Back, Bring Forward, Send Backward."" },
        ""view_id"": { ""type"": ""integer"", ""description"": ""Required for every creation/placement; optional for set_line_style (and must then match the element's owner view). Drafting, plan, section, elevation and detail views; templates, schedules, sheets and 3D are refused by name."" },
        ""key"": { ""type"": ""string"", ""description"": ""Alias for this action's created element(s); set_line_style can target it via element_key."" },
        ""start"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""end"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""point_on_arc"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""center"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""radius"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
        ""start_angle_degrees"": { ""type"": ""number"" }, ""end_angle_degrees"": { ""type"": ""number"" },
        ""points"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 200, ""items"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } } },
        ""closed"": { ""type"": ""boolean"", ""default"": false },
        ""line_style_id"": { ""type"": ""integer"", ""description"": ""Must be in the element's own valid set (CurveElement.GetLineStyleIds); omitted, the style Revit assigns is READ and reported, and it is bound into the plan."" },
        ""filled_region_type_id"": { ""type"": ""integer"" },
        ""masking_region_type_id"": { ""type"": ""integer"" },
        ""allow_masking_type_as_filled"": { ""type"": ""boolean"", ""default"": false },
        ""join_tolerance_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""maximum"": 5, ""description"": ""Lines/polylines: accept ends Revit moved by joining or merging, up to this, ONLY for a line at risk (under 3 mm, or collinear and overlapping another). The measured move is reported."" },
        ""simplify_tolerance_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""maximum"": 50, ""description"": ""Regions: drop vertices closer than this to the previous one (edges under Revit's short-curve tolerance are refused). The dropped count is reported; the simplified loop is what is drawn and re-read."" },
        ""loops"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 32, ""items"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 200, ""items"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } } } },
        ""family_symbol_id"": { ""type"": ""integer"", ""description"": ""A ViewBased detail-component or generic-annotation symbol; anything model/level/face based is refused."" },
        ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""rotation_degrees"": { ""type"": ""number"" },
        ""element_id"": { ""type"": ""integer"" },
        ""element_key"": { ""type"": ""string"", ""description"": ""A key of an EARLIER curve-creating action in this batch."" }
      },
      ""allOf"": [
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""create_detail_line"" } } }, ""then"": { ""required"": [""view_id"", ""start"", ""end""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""create_detail_polyline"" } } }, ""then"": { ""required"": [""view_id"", ""points""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""create_filled_region"" } } }, ""then"": { ""required"": [""view_id"", ""filled_region_type_id"", ""loops""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""create_masking_region"" } } }, ""then"": { ""required"": [""view_id"", ""masking_region_type_id"", ""loops""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""place_detail_component"" } } }, ""then"": { ""required"": [""view_id"", ""family_symbol_id"", ""point""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""place_symbol"" } } }, ""then"": { ""required"": [""view_id"", ""family_symbol_id"", ""point""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""set_line_style"" } } }, ""then"": { ""required"": [""line_style_id""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""draw_order"" } } }, ""then"": { ""required"": [""view_id"", ""element_ids"", ""order""] } }
      ],
      ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: 2D detail"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_annotate",
                Command = "horizun_annotate",
                Description =
                    "Atomically create text notes, tags and linear/angular/radial/diameter/arc-length/spot " +
                    "dimensions. Text: height, justification, rotation, paper width, printed lines, ray-aimed leader. Dimensions use " +
                    "stable references from horizun_get_dimension_references. Dry run provisionally creates then " +
                    "rolls back; apply binds the plan and verifies every committed annotation or rolls back all. " +
                    "Radial, diameter and arc-length need Revit 2025+; spot slope has no creation API.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" }, ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"", ""description"": ""Units of every coordinate and of expected_value/expected_tolerance - except that for angular_dimension expected_value/expected_tolerance are DEGREES."" },
    ""on_failure"": { ""type"": ""string"", ""enum"": [""atomic"", ""best_effort""], ""default"": ""atomic"", ""description"": ""best_effort: unbuildable rows are skipped, reported per row."" },
    ""keep_active_view"": { ""type"": ""boolean"", ""description"": ""Skip temporary activation for linked linear dimensions."" },
    ""load_tag_family"": { ""type"": ""string"", ""description"": ""Instead of actions: load a user-approved tag .rfa."" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 1000, ""items"": {
      ""type"": ""object"", ""required"": [""operation"", ""view_id""], ""properties"": {
        " + IdentityFields("per action: tag element_id, else view_id") + @"""operation"": { ""type"": ""string"", ""enum"": [""text"", ""tag"", ""dimension"", ""angular_dimension"", ""radial_dimension"", ""diameter_dimension"", ""arc_length_dimension"", ""spot_elevation"", ""spot_coordinate"", ""spot_slope""], ""description"": ""spot_slope is always refused: Revit exposes no creation API for it in 2023-2027. radial/diameter/arc_length require Revit 2025+."" },
        ""view_id"": { ""type"": ""integer"", ""description"": ""Target view may be inactive. Excludes templates, schedules and sheets. Dimensions require a model view (locked if 3D). If Revit cannot verify a dimension after commit, the batch rolls back. If the view has never been opened, opening it and rehearsing again may help."" },
        ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""text/tag: placement. spot_elevation/spot_coordinate: the origin ON the reference."" },
          ""text"": { ""type"": ""string"" }, ""text_type_id"": { ""type"": ""integer"" },
          ""expected_height_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0,
            ""description"": ""Text only: TextNoteType height must match within 0.1 mm."" },
          ""horizontal_alignment"": { ""type"": ""string"", ""enum"": [""left"", ""center"", ""right""],
            ""description"": ""Text justification; verified."" },
          ""vertical_alignment"": { ""type"": ""string"", ""enum"": [""top"", ""middle"", ""bottom""],
            ""description"": ""Text justification; verified."" },
          ""rotation_degrees"": { ""type"": ""number"", ""description"": ""Text only: rotation in the view plane; re-read from the note's BaseDirection."" },
          ""width_mm"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Text: box width in PAPER mm."" },
          ""expected_lines"": { ""type"": ""integer"", ""minimum"": 1, ""description"": ""Text: lines that must print."" },
          ""leader_target"": { ""type"": ""object"", ""description"": ""Text leader: end point and/or element the ray must hit."", ""properties"": {
            ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } }, ""element_id"": { ""type"": ""integer"" },
            ""link_instance_id"": { ""type"": ""integer"" }, ""linked_element_id"": { ""type"": ""integer"" } }, ""additionalProperties"": false },
          ""leader_side"": { ""type"": ""string"", ""enum"": [""auto"", ""left"", ""right""] },
          ""leader_attachment"": { ""type"": ""string"", ""enum"": [""top"", ""middle"", ""bottom""] },
        ""element_id"": { ""type"": ""integer"" }, ""add_leader"": { ""type"": ""boolean"", ""default"": false },
        ""link_instance_id"": { ""type"": ""integer"", ""description"": ""tag an element of a Revit link: the link instance in this document, with linked_element_id; instead of element_id."" },
        ""linked_element_id"": { ""type"": ""integer"", ""description"": ""tag: the element id inside the linked model (needs link_instance_id)."" },
        ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""integer"" }, ""description"": ""tag: more elements on one tag."" },
        ""leader_end"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""tag: free leader end; units, host model coordinates."" },
        ""leader_elbow"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""tag: leader elbow; units, host model coordinates."" },
        ""keynote_key"": { ""type"": ""string"" },
        ""tag_type_id"": { ""type"": ""integer"", ""description"": ""Optional explicit tag type. For a Room target this must be a RoomTag type; otherwise the dry run proves it is valid for the created IndependentTag. Apply verifies the committed type."" },
        ""tag_family_path"": { ""type"": ""string"", ""description"": ""Room target only: absolute path to an existing RoomTag .rfa. If no RoomTag type is loaded and this is omitted, the single default M_Room Tag.rfa or Room Tag.rfa in the installed Revit library is used. The file SHA-256 is bound to confirmation. A missing or ambiguous library refuses with an actionable path request."" },
        ""tag_type_name"": { ""type"": ""string"", ""description"": ""Room target with tag_family_path: exact type name in the RFA. Required when the family has multiple types and tag_type_id is omitted."" },
        ""avoid_collisions"": { ""type"": ""boolean"", ""default"": false, ""description"": ""tag: measure real native bounding boxes in the reversible rehearsal, search within the approved displacement, verify clearance after commit. Conservative boxes include leaders; not visual approval."" },
        ""layout_clearance"": { ""type"": ""number"", ""minimum"": 0, ""default"": 10 },
        ""layout_max_displacement"": { ""type"": ""number"", ""minimum"": 0, ""default"": 1200 },
        ""require_tag_text"": { ""type"": ""boolean"", ""default"": false, ""description"": ""tag: refuse empty text or a lone question mark."" },
        ""layout_accept_unmeasurable"": { ""type"": ""array"", ""maxItems"": 200, ""items"": { ""type"": ""integer"" }, ""description"": ""tag + avoid_collisions: element ids of annotations this call accepts as unmeasurable. Every annotation whose extent CANNOT be read and which Revit's own view-scoped visible-element collector still lists as visible blocks the layout, naming its ids - an unmeasured annotation is never treated as absent. Naming those exact ids here records the acceptance and downgrades the reported clearance_scope to 'partial'; the placement is then never described as collision-free. There is no blanket switch, and an id that was measurable is not silently consumed."" },
        ""tag_mode"": { ""type"": ""string"", ""enum"": [""by_category"", ""multi_category"", ""material"", ""keynote_element"", ""keynote_material"", ""keynote_user""], ""default"": ""by_category"" },
        ""orientation"": { ""type"": ""string"", ""enum"": [""horizontal"", ""vertical""], ""default"": ""horizontal"" },
        ""line_start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""line_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""references"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 32, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Stable references, 'level:<id>' or '<face>/?'. No duplicates. dimension: 2..32. angular_dimension: exactly 2. arc_length_dimension: the 2 endpoint references. 'level:<id>' is the host level's plane; '<face>/?' the tile joint nearest pattern_points."" },
        ""pattern_points"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } }, ""description"": ""One point per '<face>/?' reference: picks the nearest tile joint."" },
        ""require_printable"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Refuse a dimension not confirmed to print: hidden, outside the crop, or undecided (unknown facts)."" },
        ""dimension_type_id"": { ""type"": ""integer"", ""description"": ""dimension and angular_dimension only - the other shapes' creation APIs take no type. Omitted: the document's default type for the shape is resolved, validated and bound into the plan."" },
        ""arc_center"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""angular_dimension/arc_length_dimension: centre of the dimension arc, in the view plane."" },
        ""arc_radius"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
        ""arc_reference"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""arc_length_dimension: the stable reference of the arc edge being measured."" },
        ""reference"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""radial_dimension/diameter_dimension: the arc edge. spot_elevation/spot_coordinate: the referenced face or edge, or 'level:<id>'."" },
        ""leader"": { ""type"": ""boolean"", ""default"": false, ""description"": ""spot_elevation/spot_coordinate only; Revit's own hasLeader creation argument."" },
        ""bend"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""spot leader bend point. Defaulted from the view plane when omitted."" },
        ""end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""spot leader end point. Defaulted when omitted."" },
        ""prefix"": { ""type"": ""string"" }, ""suffix"": { ""type"": ""string"" },
        ""above"": { ""type"": ""string"" }, ""below"": { ""type"": ""string"" },
        ""value_override"": { ""type"": ""string"", ""description"": ""Overrides and lock apply to two-reference dimensions only; chains take them per segment through horizun_edit_dimensions."" },
        ""allow_text_override"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Required for value_override."" },
        ""allow_outside_crop"": { ""type"": ""boolean"", ""default"": false },
        ""eq"": { ""type"": ""boolean"", ""description"": ""EQ constraint; chains of 3+ references only."" },
        ""lock"": { ""type"": ""boolean"" },
        ""expected_value"": { ""type"": ""number"", ""description"": ""POSTCONDITION on the measured value, checked at apply in the still-reversible state and again after commit; a miss rolls the WHOLE batch back. In 'units'; DEGREES for angular_dimension; not accepted on spots (they have no Value)."" },
        ""expected_tolerance"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Tolerance for expected_value. Defaults: 0.1 mm for lengths, 0.01 degrees for angular."" }
      },
      ""allOf"": [
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""text"" } } }, ""then"": { ""required"": [""view_id"", ""point"", ""text"", ""text_type_id""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""tag"" } } }, ""then"": { ""required"": [""view_id"", ""point""], ""anyOf"": [{ ""required"": [""element_id""] }, { ""required"": [""link_instance_id"", ""linked_element_id""] }] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""dimension"" } } }, ""then"": { ""required"": [""view_id"", ""line_start"", ""line_end"", ""references""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""angular_dimension"" } } }, ""then"": { ""required"": [""view_id"", ""arc_center"", ""arc_radius"", ""references""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""radial_dimension"" } } }, ""then"": { ""required"": [""view_id"", ""reference""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""diameter_dimension"" } } }, ""then"": { ""required"": [""view_id"", ""reference""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""arc_length_dimension"" } } }, ""then"": { ""required"": [""view_id"", ""arc_center"", ""arc_radius"", ""arc_reference"", ""references""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""spot_elevation"" } } }, ""then"": { ""required"": [""view_id"", ""reference"", ""point""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""spot_coordinate"" } } }, ""then"": { ""required"": [""view_id"", ""reference"", ""point""] } }
      ],
      ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: annotate"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_submit_job",
                Command = "horizun_submit_job",
                Description =
                    "Submit any installed Revit-side tool except execute_python, request_python_access or " +
                    "submit_job itself to the bounded " +
                    "asynchronous queue and return a persistent job_id immediately. The submission is durably " +
                    "idempotent, queued work alternates fairly with interactive calls, permissions are checked " +
                    "again when execution begins, and horizun_job_status exposes queued/running/result/failure or " +
                    "process-death state without waiting for Revit. " +
                    "OR AN ORDERED SEQUENCE: pass 'sequence' ({key, tool, arguments} entries) or 'models' (a " +
                    "read-only sweep, expanded into open/audit/close per model) instead of tool + arguments, and " +
                    "the whole run becomes ONE job with one id to poll. A sequence may only name " +
                    "horizun_open_document, horizun_model_scan, horizun_audit_model, horizun_quantities and " +
                    "horizun_document_session with operation 'close' - nothing that writes to a model - so a " +
                    "submission naming any other tool is refused WHOLE with nothing queued. Execution stops at " +
                    "the first failed step; every later step is reported not_run, never omitted and never " +
                    "succeeded, and the closes of a stopped sweep still run so no document is left open. " +
                    "Exactly one of tool+arguments, sequence or models: two shapes in one submission is refused " +
                    "rather than resolved by precedence.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""tool"": { ""type"": ""string"", ""description"": ""An installed Revit-side MCP tool. Host-only tools, horizun_execute_python, horizun_request_python_access and horizun_submit_job are refused. Mutually exclusive with sequence and models."" },
    ""resume_from_job_id"": { ""type"": ""string"", ""description"": ""Resume only a proven never-started job: identical tool, semantic arguments and document; source must be not_started or its owner process dead without a running event. Requires idempotency_key=resume:<source job id>. Fresh confirmation_token is permitted after a new rehearsal. Running, partial, corrupt and legacy records are refused; this never replays an uncertain mutation."" },
    ""arguments"": { ""type"": ""object"", ""description"": ""The exact typed arguments, including target_document, dry_run/confirmation_token where that tool requires them."" },
    ""sequence"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""description"": ""An ordered read-only sequence run as ONE job. Only horizun_open_document, horizun_model_scan, horizun_audit_model, horizun_quantities and horizun_document_session (operation 'close') are admissible; anything that writes to a model refuses the whole submission with nothing queued, naming the index."", ""items"": {
      ""type"": ""object"", ""required"": [""key"", ""tool"", ""arguments""], ""properties"": {
        ""key"": { ""type"": ""string"", ""description"": ""Unique within the sequence. Steps are reported by key, so two steps sharing one cannot be told apart."" },
        ""tool"": { ""type"": ""string"", ""enum"": [""horizun_open_document"", ""horizun_model_scan"", ""horizun_audit_model"", ""horizun_quantities"", ""horizun_document_session""] },
        ""arguments"": { ""type"": ""object"" }
      }, ""additionalProperties"": false } },
    ""models"": { ""type"": ""array"", ""minItems"": 1, ""description"": ""A read-only sweep, expanded into open/audit/close per model in listed order. One document at a time; nothing is saved or synchronised; a model that was never opened is reported not_assessed, never clean."", ""items"": {
      ""type"": ""object"", ""required"": [""id"", ""expected_title""], ""properties"": {
        ""id"": { ""type"": ""string"", ""description"": ""Stable identifier for this model in the report. Duplicates are refused: a result keyed on a repeated id cannot say which model it came from."" },
        ""origin"": { ""type"": ""string"", ""enum"": [""local"", ""cloud""], ""default"": ""local"" },
        ""path"": { ""type"": ""string"", ""description"": ""local only. A downloaded copy of a cloud model is a LOCAL model that resembles it, and offering one as the other is refused."" },
        ""cloud_project_guid"": { ""type"": ""string"" },
        ""cloud_model_guid"": { ""type"": ""string"" },
        ""cloud_region"": { ""type"": ""string"" },
        ""expected_title"": { ""type"": ""string"", ""description"": ""REQUIRED: what the opened document must be called. Both the audit and the close name their target; a sweep that cannot name it acts on whatever document is in front."" },
        ""expected_version"": { ""type"": ""string"" },
        ""profile_version"": { ""type"": ""string"", ""description"": ""Per-model profile, so one sweep can judge models by different rules. Falls back to batch.profile_version."" }
      }, ""additionalProperties"": false } },
    ""batch"": { ""type"": ""object"", ""description"": ""Run-wide options for a models sweep."", ""properties"": {
      ""profile_version"": { ""type"": ""string"" }
    }, ""additionalProperties"": false },
    ""retain_until_utc"": { ""type"": ""string"", ""format"": ""date-time"", ""description"": ""Optional durable-retention lease for MCP Tasks. Must be a future UTC instant no more than seven days away; it protects this job record from configured retention but does not extend execution."" }
  },
  ""oneOf"": [
    { ""required"": [""tool"", ""arguments""] },
    { ""required"": [""sequence""] },
    { ""required"": [""models""] }
  ],
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_execute_plan",
                Command = "horizun_execute_plan",
                Description =
                    "Compose up to 100 typed Revit write commands into one ordered, atomic plan. Alternatively " +
                    "supply a named workflow (pin_elements, apply_view_template, prepare_sheet_set) with explicit targets; " +
                    "it compiles to the same rehearsed, confirmed and verified typed actions. Supply actions OR workflow. Exact result " +
                    "references such as ${scan.rows.0.element_id} feed rehearsed values into later actions without " +
                    "string coercion. Confirmation is issued only when every action and reference resolves during " +
                    "the dry run; each resolved reference is bound to its exact canonical value. References to " +
                    "values that exist only after creation are currently refused rather than authorised unseen. " +
                    "Apply uses an outer TransactionGroup, so a failure in any action rolls every action back. Session changes, " +
                    "exports and arbitrary Python are intentionally excluded because they are not transaction-reversible.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""workflow"": { ""type"": ""object"", ""required"": [""name""], ""additionalProperties"": false, ""properties"": {
      ""name"": { ""type"": ""string"", ""enum"": [""pin_elements"", ""apply_view_template"", ""prepare_sheet_set"", ""document_rooms""] },
      ""room_plan"": { ""type"": ""object"", ""additionalProperties"": false,
        ""required"": [""room_ids"", ""plan_view_id"", ""kinds"", ""units"", ""scale"", ""margin"", ""name_pattern"", ""orient_to_walls""],
        ""properties"": {
          ""room_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""uniqueItems"": true, ""items"": { ""type"": ""integer"", ""minimum"": 1 } },
          ""plan_view_id"": { ""type"": ""integer"", ""minimum"": 1 }, ""template_view_id"": { ""type"": ""integer"", ""minimum"": 1 },
          ""kinds"": { ""type"": ""array"", ""minItems"": 1, ""uniqueItems"": true, ""items"": { ""type"": ""string"", ""enum"": [""plan"", ""sections"", ""elevations""] } },
          ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""] }, ""scale"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 24000 },
          ""margin"": { ""type"": ""number"", ""minimum"": 0 }, ""name_pattern"": { ""type"": ""string"", ""minLength"": 1 },
          ""orient_to_walls"": { ""type"": ""boolean"" }, ""elevation_count"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 4 }
        }, ""description"": ""document_rooms uses the existing room_views planner. Explicit elevation_count for elevations. Partial coverage refuses the entire workflow."" },
      ""view_types"": { ""type"": ""object"", ""additionalProperties"": false, ""properties"": { ""section"": { ""type"": ""integer"", ""minimum"": 1 }, ""elevation"": { ""type"": ""integer"", ""minimum"": 1 } }, ""description"": ""Explicit ViewFamilyType IDs for the requested sections/elevations."" },
      ""view_templates"": { ""type"": ""object"", ""additionalProperties"": false, ""properties"": { ""plan"": { ""type"": ""integer"", ""minimum"": 1 }, ""section"": { ""type"": ""integer"", ""minimum"": 1 }, ""elevation"": { ""type"": ""integer"", ""minimum"": 1 } }, ""description"": ""Required template ID per requested kind. Alternative: room_plan.template_view_id for a single kind only. Never mix both forms."" },
      ""room_sheets"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""object"", ""additionalProperties"": false,
        ""required"": [""room_id"", ""number"", ""name"", ""title_block_type_id"", ""placements""], ""properties"": {
          ""room_id"": { ""type"": ""integer"", ""minimum"": 1 }, ""title_block_type_id"": { ""type"": ""integer"", ""minimum"": 1 },
          ""number"": { ""type"": ""string"", ""minLength"": 1 }, ""name"": { ""type"": ""string"", ""minLength"": 1 },
          ""placements"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""object"", ""additionalProperties"": false, ""required"": [""kind"", ""index"", ""point""],
            ""properties"": { ""kind"": { ""type"": ""string"", ""enum"": [""plan"", ""section"", ""elevation""] }, ""index"": { ""type"": ""integer"", ""minimum"": 1 },
              ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 2, ""items"": { ""type"": ""number"" } } } } }
        }, ""description"": ""Explicit placements using room_plan.units and 1-based planned view indices. Optional; omission creates views only."" } },
      ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""uniqueItems"": true, ""items"": { ""type"": ""integer"", ""minimum"": 1 } },
      ""view_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""uniqueItems"": true, ""items"": { ""type"": ""integer"", ""minimum"": 1 } },
      ""template_view_id"": { ""type"": ""integer"", ""minimum"": 1 },
      ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
      ""sheets"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": {
        ""type"": ""object"", ""additionalProperties"": false, ""required"": [""view_id"", ""number"", ""name"", ""title_block_type_id"", ""point""], ""properties"": {
          ""view_id"": { ""type"": ""integer"", ""minimum"": 1 }, ""template_view_id"": { ""type"": ""integer"", ""minimum"": 1 },
          ""number"": { ""type"": ""string"", ""minLength"": 1 }, ""name"": { ""type"": ""string"", ""minLength"": 1 },
          ""title_block_type_id"": { ""type"": ""integer"", ""minimum"": 1 },
          ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } }
        }
      } }
    } },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": {
      ""type"": ""object"", ""required"": [""key"", ""tool"", ""arguments""], ""properties"": {
        ""key"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""Unique action name used by later ${key.path} references."" },
        ""tool"": { ""type"": ""string"", ""enum"": [
          ""horizun_write_params_verified"", ""horizun_delete_verified"", ""horizun_create_schedule"",
          ""horizun_set_keynote"", ""horizun_bind_shared_param"",
          ""horizun_create_elements"", ""horizun_transform_elements"", ""horizun_manage_views"", ""horizun_annotate"",
          ""horizun_split_floor_loops"", ""horizun_split_multilayer_walls"", ""horizun_split_multilayer_slabs"",
          ""horizun_ungroup_and_mark"", ""horizun_regroup_by_param"", ""horizun_copy_slab_elevations"",
          ""horizun_embed_floors_in_toposolid"", ""horizun_grade_toposolid_around_floors"", ""horizun_rectangularize_walls"",
          ""horizun_pack_sheets"", ""horizun_view_visibility""
        ] },
        ""arguments"": { ""type"": ""object"", ""description"": ""Tool arguments. target_document, dry_run, confirmation_token, idempotency_key are set by the plan. Identity fields: checked plan-wide first."" }
      }, ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: atomic plan"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_request_python_access",
                Command = "horizun_request_python_access",
                Description =
                    "REQUEST, NEVER SELF-GRANT, persistent arbitrary-Python permission. request opens a non-modal consent " +
                    "window in Revit and returns at once (pending_approval, request_id); status reads the owner's decision " +
                    "(also in horizun_health python_runtime). Approval is indefinite until that user turns Python OFF; the " +
                    "caller cannot press the button. Not for unattended use.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""request"", ""status""], ""default"": ""request"" },
    ""request_id"": { ""type"": ""string"", ""description"": ""status: omitted reads the latest."" },
    ""reason"": { ""type"": ""string"", ""maxLength"": 500,
      ""description"": ""Optional reason shown as UNVERIFIED caller text in the consent window. It never grants permission."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_execute_python",
                Command = "horizun_execute_python",
                Description =
                    "Run Python directly against the Revit API on the UI thread - THE EXECUTION FALLBACK for " +
                    "everything the typed commands do not cover. Disabled by default; the machine owner must " +
                    "explicitly approve the persistent Revit UI grant, or configure unsafe_code plus " +
                    "enable_execute_python administratively. It remains enabled until that Windows user revokes " +
                    "it; the MCP client cannot grant itself permission. " +
                    "To create a project from a template use horizun_document_session operation=new_project (rehearses by default). " +
                    "idempotency_key: a fresh UUID per deliberate write; reuse it only to retry the identical request. " +
                    "For family parameters and their values by type use horizun_query_family_internals (Pro edition) operation=parameter_census. " +
                    "POLICY: prefer a typed command when it fully covers the " +
                    "operation. When none exists, or a failed typed call returns fallback.allowed=true - its " +
                    "machine-readable signal that no typed capability covers the request AND nothing was " +
                    "written - generate minimal Python and run it here instead of answering 'not supported'. " +
                    "Decide on that block, never on the wording of an error: no block, or allowed=false, means " +
                    "DO NOT fall back. NEVER fall back here after a typed write FAILED mid-operation - it may " +
                    "have partially written, and a Python retry is a second write; report the real state " +
                    "instead. " +
                    "SOURCE: 'code' inline OR 'code_path' (a .py on the machine running Revit) - exactly one. " +
                    "code_path is read as UTF-8, honouring a BOM or a '# -*- coding: -*-' line, with CRLF " +
                    "normalised; it is measured, hashed and bound to the idempotency key exactly like inline " +
                    "code, and it makes tracebacks name the real file and line instead of <string>. " +
                    "INJECTED: doc, uidoc, uiapp AND __revit__ (both the UIApplication - __revit__ is pyRevit's " +
                    "name for it), app (the Application), checkpoint(), revit_raised(), dialog_answer(). " +
                    "ELEMENT IDS: ElementId.IntegerValue does not exist in Revit 2026+ (it is .Value, 64-bit, " +
                    "since 2024) - use horizun.id_value(some_id), which reads either on 2023-2027, instead of " +
                    "either property. " +
                    "COORDINATES AND LEADERS: horizun.model_to_view / view_to_model / view_to_sheet / sheet_to_view / " +
                    "model_to_sheet / sheet_to_model(viewport, ...) convert between model, view and sheet (feet; " +
                    "horizun.calibrate_viewport returns the offset to a position you measured), horizun.view_ray(view, " +
                    "[(u, v)], depth_ft, categories) casts rays from the view plane into host and links, and " +
                    "horizun.leaders_capture(note) / leaders_restore(note, captured) save and put back text-note leaders " +
                    "(restore re-reads and reports verified). " +
                    "WHAT REVIT RAISED comes back as 'dialogs' and 'failures' beside __output__ - read them " +
                    "when an open fails, because the bridge CANCELS modal dialogs and all the script sees is " +
                    "'Opening was canceled'. Each carries 'while', the script's own last checkpoint() label. " +
                    "revit_raised(since) reads the same records from INSIDE the script, so a batch can attribute " +
                    "a dialog to the model it was raised on; `with dialog_answer('dismiss'): ...` lets ONE call " +
                    "continue past its dialog. " +
                    "RETURN EVIDENCE: assign __output__ the structured shape {status: " +
                    "verified|completed_unverified|partial|failed, summary, created_ids, modified_ids, " +
                    "deleted_ids, verification:{checked, evidence:[]}, warnings:[]} and RE-READ what you wrote " +
                    "before claiming verified. WHAT COMES BACK IS SELF-REPORTED, NOT HOST-VERIFIED: the bridge " +
                    "does not re-read the model after arbitrary code, so evidence_status is one of " +
                    "self_reported_verified|completed_unverified|partial|failed - there is no 'verified' on " +
                    "this path, host_verified is always false, and a verified claim without evidence is " +
                    "downgraded to completed_unverified. script_reported_status carries what your script " +
                    "declared. print() remains as compatibility output. " +
                    "preflight=true validates permission, document, size, script hash and basic syntax WITHOUT " +
                    "executing, and returns advisory warnings; it cannot prove what arbitrary code will do. " +
                    "When the objective is unambiguous and preflight passes, continue to execution in the same " +
                    "task. Scripts that only duplicate a typed command get an advisory naming it, not a " +
                    "refusal. The standard library is available (json, re, csv, datetime, math). " +
                    "TRANSACTIONS ARE YOURS TO CLOSE, and this is the one thing to read before using it. NOTHING " +
                    "here rolls back a transaction your script opened - the Revit API offers no handle on a " +
                    "transaction opened by other code, so no amount of error handling on this side can reach it. " +
                    "An earlier version of this description promised a rollback and there was never any code that " +
                    "could deliver it. What is enforced: Document.IsModifiable is re-read after your script, and a " +
                    "document left modifiable makes the command FAIL. What happens to the orphan, measured on " +
                    "Revit 2026 rather than assumed: Revit ends it when this handler returns and its status " +
                    "becomes RolledBack, so the DOCUMENT recovers and your script's writes DO NOT - everything " +
                    "inside that transaction is discarded. That is a host behaviour observed, not a guarantee " +
                    "offered. Commit or RollBack in a finally - see transaction_policy on every response. " +
                    "Element ids are 64-bit longs in 2024+; wrap ElementId.Value in int() before json.dumps. " +
                    "target_document is REQUIRED for a script that may write (matched against the ACTIVE document), and optional with read_only=true or rehearsal=true, which read the active document and are always rolled back; when named it is checked the same way. This command will " +
                    "not switch documents for you, and a script that needs no document cannot run here. " +
                    "run_async=true returns a job_id immediately; a run still going after 45 s answers state=running with its job_id - follow it with horizun_job_status, never resend. " +
                    "A missing idempotency_key for a write is generated and returned; reuse it only for the identical retry. A preflight, read_only and rehearsal need none. " +
                    "STILL A PRIVILEGED BYPASS: it has no dry run, no plan and no confirmation token, so unlike " +
                    "the typed write commands nothing rehearses what it will do. Accepted risk, not a satisfied " +
                    "policy - see docs/security-model.md.",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["code"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] =
                                "Python source to execute, inline. Assign __output__ for the return value; " +
                                "print() is captured. Exactly one of code / code_path is required."
                        },
                        ["code_path"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] =
                                "A .py file ON THE MACHINE RUNNING REVIT, read instead of 'code'. Exactly one " +
                                "of the two. For scripts too long or too awkward to send inline: it is read " +
                                "here, in UTF-8 - honouring a byte-order mark or a '# -*- coding: -*-' line - " +
                                "with CRLF normalised to LF, and a file that does not decode is REFUSED naming " +
                                "the byte and the offset rather than run with replacement characters. It evades " +
                                "nothing: the size limit and submitted_source_sha256 measure the bytes read " +
                                "here. IDEMPOTENCY IS BOUND TO THE REQUEST, AND THE REQUEST NAMES THE PATH, NOT " +
                                "THE BYTES - so re-sending the same idempotency_key after editing the file " +
                                "replays the original answer and runs nothing. A changed script is new work: " +
                                "give it a new key. Tracebacks name this file and the real line instead of " +
                                "<string>. run_async reads the file ONCE, at submit, so a deferred run executes " +
                                "the script its fingerprint was taken of."
                        },
                        ["arguments"] = new JObject
                        {
                            ["type"] = "object",
                            ["description"] =
                                "Arguments for the script, bound into it as ONE variable: HORIZUN_ARGS_JSON, " +
                                "a JSON STRING the script parses itself with json.loads. Two decisions, both " +
                                "deliberate. ONE NAME, because a caller whose argument happens to be called " +
                                "'doc' would otherwise shadow the name every Revit script expects and fail " +
                                "somewhere far from the cause. TEXT, because binding a JSON object as typed " +
                                "Python globals needs a conversion with opinions about integers, nulls and " +
                                "nested objects, and every one of those opinions is a place where what you " +
                                "sent is not what the script reads. It is ALWAYS bound, as '{}' when omitted, " +
                                "so a script that reads it never has to ask whether it exists. This is what " +
                                "lets a PROMOTED script be called with something instead of being regenerated " +
                                "per call - regenerating it defeats the hash that made the generation " +
                                "attributable."
                        },
                        ["target_document"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] =
                                "Title or full path of the document this script acts on. REQUIRED, and matched " +
                                "against the ACTIVE document: 'the active document' is whatever window was in " +
                                "front when the call arrived, and with two Revit instances open that is not a " +
                                "decision anybody made. Refused if it names a document that is open but not " +
                                "active - this command will not switch for you."
                        },
                        ["idempotency_key"] = new JObject
                        {
                            ["type"] = "string",
                            ["description"] =
                                "Optional for a write: a missing key is generated and returned for retries of this same operation. " +
                                "Not claimed for preflight, read_only or rehearsal. Claimed durably before Python runs: the same key " +
                                "with the identical operation replays its recorded answer without executing, a " +
                                "different operation under that key is refused, and a claimed operation with no " +
                                "terminal record after a crash is reported in_doubt instead of repeated."
                        },
                        ["purpose"] = new JObject
                        {
                            ["type"] = "string",
                            ["maxLength"] = 200,
                            ["description"] =
                                "One plain sentence saying what this script does to the model (e.g. 'renames 12 " +
                                "levels to the project standard'). Shown to the person in Revit's operations pane " +
                                "instead of the bare tool name. Not executed, not hashed."
                        },
                        ["preflight"] = new JObject
                        {
                            ["type"] = "boolean",
                            ["default"] = false,
                            ["description"] =
                                "Validate WITHOUT executing: permission, target document, size, script SHA-256 " +
                                "and basic syntax, plus advisory warnings (typed-command overlaps, missing " +
                                "transaction hygiene, missing __output__). It cannot prove the safety or effect " +
                                "of arbitrary code. Not combinable with run_async. When the objective is already " +
                                "unambiguous and the preflight passes, continue to execution in the same task - " +
                                "this is a check, not an approval step."
                        },
                        ["run_async"] = new JObject
                        {
                            ["type"] = "boolean",
                            ["default"] = false,
                            ["description"] =
                                "Return a job_id immediately instead of waiting. For work longer than the request " +
                                "timeout, where the synchronous path answers with a timeout while the script keeps " +
                                "running unseen. AT MOST ONCE: the script is claimed from the queue destructively " +
                                "and runs exactly once or not at all - there is no retry, because re-running a " +
                                "script that already wrote to a model is a second write, not a recovery. Do not " +
                                "re-send it because a poll looks slow. Cancelling the MCP request stops YOU " +
                                "WAITING and nothing else: the Revit API cannot interrupt work on its UI thread. " +
                                "Poll horizun_job_status with the job_id; the result and any error are kept there, " +
                                "because an async caller never sees a reply. A missing idempotency_key is generated and returned."
                        }
                    },
                    // target_document only. The SOURCE requirement is "exactly one of code /
                    // code_path", which draft-7 can express and this schema deliberately does
                    // not - the same call this file already makes for idempotency_key. A
                    // oneOf here would be enforced inconsistently across MCP clients, and the
                    // handler is the gate that says precisely which of the two mistakes was
                    // made (neither sent, or both) before anything runs.
                    ["required"] = new JArray { "target_document" },
                    ["additionalProperties"] = false
                }
            },
            new CommandContract
            {
                Name = "horizun_model_scan",
                Command = "horizun_model_scan",
                Description =
                    "One deep native pass over the active model: cleanliness (CAD imported vs linked, IMPORT-* patterns, " +
                    "unused templates/filters/group types/types, stray lines, in-place families), naming inputs (RAW view/" +
                    "sheet/level/grid names â€” never judged here; validate them host-side with a regex), documentation " +
                    "(views without template WITH ids, views not on a sheet, sheets missing a titleblock), project info " +
                    "(raw values, placeholders are the caller's call), health (warnings grouped with failing element ids, " +
                    "rooms/areas), links, worksets, categories, design options and the element-type universe. Every section " +
                    "reports status ok|failed(reason) â€” a section that threw returns no buckets, so it can never read as " +
                    "clean. Every bucket reports total (exact) vs returned vs truncated. Unreadable elements get their own " +
                    "bucket: 'I could not look' is never spelled 'there is nothing there'.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [
    ""target_document_title""
  ],
  ""properties"": {
    ""confirm_large_operation"": { ""type"": ""boolean"", ""description"": ""Explicitly confirm execution when the conservative scope estimate exceeds the large-model threshold."" },
    ""target_document_title"": {
      ""type"": ""string"",
      ""description"": ""Title of the document you believe is active. The scan ABORTS if the active document differs. Required because two Revit hosts run side by side (2025 on one port, 2026 on another) and a scan of the wrong model is a clean bill of health for a file nobody looked at. '.rvt' is optional.""
    },
    ""response_mode"": {
      ""type"": ""string"",
      ""enum"": [
        ""full"",
        ""summary""
      ],
      ""default"": ""full"",
      ""description"": ""summary samples three items per inventory bucket without changing measured coverage. Reduces response size, not scan work. Requery full before following cursors.""
    },
    ""top"": {
      ""type"": ""integer"",
      ""default"": 50,
      ""minimum"": 1,
      ""description"": ""Max items returned per bucket. Totals are always exact and independent of this; a shortened list always says truncated=true.""
    },
    ""sections"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""string"",
        ""enum"": [
          ""document"",
          ""categories"",
          ""cleanliness"",
          ""naming"",
          ""documentation"",
          ""project_info"",
          ""health"",
          ""links"",
          ""worksets"",
          ""design_options"",
          ""lines"",
          ""types"",
          ""coordinates"",
          ""datums"",
          ""level_association"",
          ""worksharing"",
          ""families"",
          ""views"",
          ""sheets"",
          ""annotations"",
          ""parameters"",
          ""spatial"",
          ""groups"",
          ""design_options_census"",
          ""phases"",
          ""mep"",
          ""structure"",
          ""federation"",
          ""external_content"",
          ""documentary_context"",
          ""delivery_readiness"",
          ""weight""
        ]
      },
      ""description"": ""Which sections to run. Default: all of them. A section you did not ask for is reported as 'not_requested', never as empty. A partial response supplies resume.sections in the order of sections_not_measured; call again with those sections and the same pagination cursor, if used. Weight requires all its source sections in the same call.""
    },
    ""target_parameter"": {
      ""type"": ""string"",
      ""description"": ""Optional parameter name read off every element type in the 'types' section (e.g. 'Keynote', 'MyOrg_Code'). Reported as absent / empty / value, which are three different things. Accepted only when 'types' is among the sections: elsewhere it would read nothing and the reply would look clean.""
    },
    ""section_limits"": {
      ""type"": ""object"",
      ""description"": ""A budget per section, so one big population does not consume another's. Either a whole number ({\""cleanliness\"": 500}) or an object with 'limit' and 'buckets' ({\""cleanliness\"": {\""limit\"": 20, \""buckets\"": {\""warnings\"": 400}}}). An unknown section name is REFUSED, with the real list.""
    },
    ""weight_profile"": {
      ""type"": ""object"",
      ""description"": ""Ranks the candidates in the 'weight' section. {version, weights:{in_place_families:10, ...}}. REQUIRED for a ranking: there is no built-in default, because that would be one organisation's opinion about what makes a model heavy compiled into a neutral bridge. Without it the candidates are still reported, UNRANKED, with the reason. Nothing in that section is ever a size in bytes - Revit publishes no per-category size.""
    },
    ""cursor"": {
      ""type"": ""string"",
      ""description"": ""Resume one bucket where a previous reply stopped. Take it from that bucket's 'next_cursor'. It is checked against the document, the section, the bucket and the contract version, and refused if any differ - it is never read as 'start again', which a caller could not tell from page one.""
    },
    ""warning_profile"": {
      ""type"": ""object"",
      ""description"": ""Optional. Your triage for Revit warnings, keyed by FailureDefinitionId GUID ONLY - a profile keyed on the description text stops matching the day Revit is upgraded or the session language changes, and it stops matching silently. Needs a version; each entry takes severity and optionally label. Revit's OWN severity is always reported beside yours, never replaced by it. Without a profile no warning is triaged, which is NOT a pass."",
      ""properties"": {
        ""version"": {
          ""type"": ""string""
        }
      },
      ""required"": [
        ""version""
      ]
    },
    ""naming_profile"": {
      ""type"": ""object"",
      ""description"": ""Optional. Your naming grammar, per class of object - levels, grids, views, view_templates, sheets, families, types, worksets, links, groups, rooms, spaces, systems, filters. Needs a version. Rules per class: regex, prefix, suffix, separator, segments, min_length, max_length, allowed, forbidden, case, unique, default_words, exceptions. Nothing is compiled in: with no profile every class reports not_requested, NEVER ok. Nothing is ever renamed - you get the name, the rule that failed and a suggestion."",
      ""properties"": {
        ""version"": {
          ""type"": ""string""
        }
      },
      ""required"": [
        ""version""
      ]
    },
    ""family_profile"": {
      ""type"": ""object"",
      ""description"": ""Optional. YOUR limits for families. Needs a version. Keys: max_types, max_unused_types, max_instances, in_place_allowed_by_category, expected_shared_families, allowed_categories, exceptions. Without one the families section returns FACTS and ranked CANDIDATES and nothing is a violation. Note what this can never tell you: a family loaded into a model reports no file size, and this section does not open .rfa documents, so many types or many instances are INDICATORS and never a weight."",
      ""properties"": {
        ""version"": {
          ""type"": ""string""
        }
      },
      ""required"": [
        ""version""
      ]
    },
    ""family_budget"": {
      ""type"": ""integer"",
      ""minimum"": 0,
      ""description"": ""How many families the candidate triage returns (default 20). The reply always states how many were ranked and how many were passed over, so a budget never reads as a complete list.""
    },
    ""view_profile"": {
      ""type"": ""object"",
      ""description"": ""Optional. Rules PER VIEW TYPE - the keys are Revit ViewType names and an unknown one is refused, because a rule filed under a misspelt type never runs and reports every view as acceptable. Needs a version. Per type: template_required, allowed_templates, allowed_scales, expected_detail_level, expected_discipline, expected_phase, expected_phase_filter, crop_required, scope_box_required, required_filters, forbidden_filters, on_sheet_required, exceptions. Properties a view type does not HAVE come back not_applicable - a legend has no level - which is neither a pass nor a failure, and different again from not_readable."",
      ""properties"": {
        ""version"": {
          ""type"": ""string""
        }
      },
      ""required"": [
        ""version""
      ]
    },
    ""sheet_rules"": {
      ""type"": ""object"",
      ""description"": ""Optional. Rules for sheets and for how much annotation a view type needs. Needs a version. Keys: title_block_required, forbid_multiple_title_blocks, forbid_empty_sheets, forbid_duplicate_numbers, revisions_required, min_viewports, max_viewports, min_annotations_by_view_type, exceptions. required_schedule_names is NOT among them and is REFUSED if sent: it was accepted and never evaluated, and a rule that reports nothing reads as a rule that passed. Note two things this will never say: a sheet that is not empty has NOT been called complete, and a view with one dimension has NOT been called documented. A schedule on a sheet is a ScheduleSheetInstance and not a viewport, so the two counts stay apart."",
      ""properties"": {
        ""version"": {
          ""type"": ""string""
        }
      },
      ""required"": [
        ""version""
      ]
    },
    ""parameter_profile"": {
      ""type"": ""object"",
      ""description"": ""Optional. A versioned list of parameter rules, each with an id and an identity - name, guid or built_in_parameter. A rule keyed by GUID is NEVER satisfied by a name match: two parameters of one name are two parameters. Per rule: scope, categories, element_classes, required, allow_empty, placeholders, storage_type, specification, expected_binding, regex, allowed_values, forbidden_values, minimum, maximum, unit, exceptions, severity, explanation. PROFILES ARE DATA: no expression, script or code from a profile is executed, and the only caller-supplied thing that runs is the regex, with a timeout. Type parameters are judged once per type with the affected instances counted beside the finding."",
      ""properties"": {
        ""version"": {
          ""type"": ""string""
        },
        ""rules"": {
          ""type"": ""array""
        }
      },
      ""required"": [
        ""version"",
        ""rules""
      ]
    },
    ""spatial_rules"": {
      ""type"": ""object"",
      ""description"": ""Optional. redundant_warning_guids: the FailureDefinitionId guid(s) YOUR Revit uses for a redundant room or space. Revit exposes no IsRedundant - a redundant element reports zero area and no boundary exactly as an unenclosed one does - and no guid is compiled into this bridge, so without this list is_redundant stays null and nothing is called redundant. It is never guessed from the area."",
      ""properties"": {
        ""redundant_warning_guids"": {
          ""type"": ""array"",
          ""items"": {
            ""type"": ""string""
          }
        }
      }
    },
    ""documentary_profile"": {
      ""type"": ""object"",
      ""description"": ""Optional. Which documentary fields YOUR projects must carry - project name and number, client, status, author, organisation, address, issue date, units, phases, location, templates, sheets, revisions, links, and any shared or project parameter on Project Information. Same shape as parameter_profile and read by the same parser, so wrong_guid and placeholder mean one thing in this bridge rather than two. NOTHING IS COMPILED IN: with no profile every field is not_requested, which is not a pass. A field that does not EXIST and a field that exists and is BLANK are reported apart."",
      ""properties"": {
        ""version"": {
          ""type"": ""string""
        },
        ""rules"": {
          ""type"": ""array""
        }
      },
      ""required"": [
        ""version"",
        ""rules""
      ]
    },
    ""fourd_profile"": {
      ""type"": ""object"",
      ""description"": ""Optional. Which parameter carries each 4D role - activity_id, activity_name, WBS, package, zone, front, sequence, start_date, finish_date and any role you name - on WHICH CATEGORIES, on the instance or the type. Same shape as parameter_profile. Roles are measured per LEAF CATEGORY, never as one model-wide average, because the average hides the discipline that has nothing. With no profile every role is not_required, which is NOT a verdict: nothing here says a model is not ready for 4D. Nothing reads a programme file, and a text that looks like an activity id is not proof that it is one."",
      ""properties"": {
        ""version"": {
          ""type"": ""string""
        },
        ""rules"": {
          ""type"": ""array""
        }
      },
      ""required"": [
        ""version"",
        ""rules""
      ]
    },
    ""fived_profile"": {
      ""type"": ""object"",
      ""description"": ""Optional. The same for 5D roles - cost_code, classification_code, item_reference, unit, quantity_source, cost_package, cost_center, resource, assembly and your own. A role whose specification is 'classification_code' is checked against classification_catalogue. A cost parameter carrying a value is NOT a connection to a budget: it is evidence somebody typed a code."",
      ""properties"": {
        ""version"": {
          ""type"": ""string""
        },
        ""rules"": {
          ""type"": ""array""
        }
      },
      ""required"": [
        ""version"",
        ""rules""
      ]
    },
    ""classification_catalogue"": {
      ""type"": ""object"",
      ""description"": ""Optional. YOUR taxonomy: { version, name, codes: { code: is_leaf } }. Leafness is DECLARED, never inferred from a code's shape, because prefix inference guesses a taxonomy's structure and guesses wrong on every standard that reuses its separators. Nothing is compiled in - OmniClass, UniFormat, MasterFormat and every house standard belong to somebody and not to everybody. A GROUP code is reported apart from a leaf: it is real, it passes any regex, and nobody prices a group."",
      ""properties"": {
        ""version"": {
          ""type"": ""string""
        },
        ""codes"": {
          ""type"": ""object""
        }
      },
      ""required"": [
        ""version"",
        ""codes""
      ]
    },
    ""cooperative"": {
      ""type"": ""object"",
      ""additionalProperties"": false,
      ""description"": ""When cooperative is omitted, model_scan applies a 120000 ms budget by default. An explicit cooperative object is respected; ui_budget_ms 0 means no time limit. Send it when a PARTIAL answer that arrives is worth more to you than a complete one nobody waited for - a scan of a large model holds Revit's UI thread for as long as it runs, and the person whose Revit it is cannot click anything meanwhile. With it, the read asks BETWEEN elements whether to carry on: it stops if you have gone away, and it stops at your budget. A result that stopped early says PARTIAL and never passes itself off as clean. WHAT THIS DOES NOT PROVE: ui_budget_ms bounds how long the command SPENDS, not that Revit's interface stayed responsive - the command holds the UI thread for its whole duration, the reply still travels back through the pipe, and Revit's own event queue decides when the window repaints. Stopping work and releasing the interface are different events, and only a sampler watching from outside can measure the second."",
      ""properties"": {
        ""ui_budget_ms"": {
          ""type"": ""integer"",
          ""minimum"": 0,
          ""maximum"": 600000,
          ""default"": 20000,
          ""description"": ""How long this read may hold Revit's UI thread before it stops and says so. Twenty seconds by default, chosen against what a person does: below about that a frozen window reads as 'it is working'; past it people start clicking, then killing Revit.""
        },
        ""max_units"": {
          ""type"": ""integer"",
          ""minimum"": 1,
          ""description"": ""A hard bound on elements EXAMINED, independent of the clock. Separate from the budget on purpose: a bound in units is reproducible across machines and a bound in milliseconds is not.""
        },
        ""cursor"": {
          ""type"": ""string"",
          ""description"": ""Continue a previous partial read. Opaque, and REFUSED against a different request: resuming one query at another query's position produces a page of the wrong elements with nothing to show that anything went wrong. Where this command already pages with its own cursor or offset, the reply says so and issues none here - two continuation tokens over different things is worse than either.""
        }
      }
    }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_write_params_verified",
                Command = "horizun_write_params_verified",
                Description =
                    "Apply an explicit batch of parameter writes to elements, types or Project Information in ONE " +
                    "named transaction (one undo step), then RE-READ every parameter from the model after the commit " +
                    "and report value_written vs value_read_back â€” a difference is an explicit failure, not a warning. " +
                    "Targets resolve by BuiltInParameter name, shared-parameter GUID, or parameter name (ambiguity is " +
                    "an error). Parameter.Set() returning false is reported as a refused write, never counted. Writing " +
                    "to a type re-codes every instance of it: the blast radius is measured and reported. on_failure=" +
                    "'atomic' (default) rolls the whole batch back; 'best_effort' commits what worked. The terminal " +
                    "transaction state is a first-class field. Unit strings on Double storage are parsed in the document's units BEFORE " +
                    "Set(double) and verified against the converted request. Integer strings use SetValueString, so those rows " +
                    "can only be verified against a re-read of themselves: they are counted separately under " +
                    "writes_confirmed_by_parse_read_back_only and never claimed as verified against your value. selector + set " +
                    "writes the same value(s) to every element a selector matches (only_if_empty skips filled cells). Use " +
                    "dry_run=true to resolve every target and see what would be written without opening a transaction.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""tabular_source"": {
      ""type"": ""object"", ""required"": [""path"", ""key_column"", ""value_columns"", ""category""],
      ""description"": ""The writes come from a CSV instead of the writes array (give one or the other). The file is diffed against the model NOW: ops are generated only for cells whose reading differs (exact string comparison of the displayed value by default - conservative; declare decimal_separator and numeric cells against Double/Integer parameters compare NUMERICALLY instead, unit-aware), row problems (empty key, unmatched, ambiguous) are named per row in the reply's tabular block, duplicate keys in the file refuse the whole call, and the expanded ops ride the normal rehearsal - a file edited between rehearsal and apply refuses as a stale plan. CSV only, by design: this bridge carries no spreadsheet library."",
      ""properties"": {
        ""path"": { ""type"": ""string"", ""description"": ""Absolute path to the .csv (UTF-8, BOM tolerated)."" },
        ""key_column"": { ""type"": ""string"", ""description"": ""Header column whose value finds the element. Exact, case-sensitive."" },
        ""key_parameter"": { ""type"": ""string"", ""description"": ""Parameter the key is matched against. Default: same name as key_column."" },
        ""value_columns"": { ""type"": ""object"", ""description"": ""{column: parameter} - each mapped column writes that parameter."", ""additionalProperties"": { ""type"": ""string"" } },
        ""category"": { ""type"": ""string"", ""description"": ""OST_ BuiltInCategory bounding the element sweep. Required: an unbounded sweep is not a cost to impose silently."" },
        ""skip_unchanged"": { ""type"": ""boolean"", ""default"": true },
        ""decimal_separator"": { ""type"": ""string"", ""enum"": [""."", "",""],
          ""description"": ""DECLARED, never guessed from the file. When present, a cell that parses as a number under this separator and lands on a Double parameter is compared NUMERICALLY against the model's value converted to that parameter's display unit (Integer storage compares the integer), so '300' no longer rewrites a parameter displaying '300.00 mm'. Equal means within 1e-6 relative. Such a cell is also WRITTEN as that number (converted from the display unit to internal for Double, the integer for Integer), never as text re-parsed with the document's decimal symbol. A cell that does not parse (a unit suffix, the other separator) falls back to the exact display-string compare, which writes - harmlessly. Absent: every cell uses the exact display-string compare, as before."" }
      }
    },
    ""sequence"": { ""type"": ""object"", ""description"": ""Generates:{parameter,order_by[level|x|y|room],element_ids|category,prefix,start,step,pad,restart_per_level,phase_id}"" },
    ""selector"": " + ElementSelectorSchema + @",
    ""set"": { ""type"": ""object"", ""maxProperties"": 10, ""description"": ""With selector: {parameter: value} written to EVERY matched element (up to 10,000; more is refused, never partial). Each row is re-read."" },
    ""only_if_empty"": { ""type"": ""boolean"", ""default"": false, ""description"": ""With selector: skip cells that already hold a value (counted in selector.skipped_not_empty)."" },
    ""writes"": {
      ""type"": ""array"", ""minItems"": 1,
      ""description"": ""The batch. Each entry names ONE parameter on ONE target."",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""parameter"", ""value""],
        ""properties"": {
          ""target_id"": { ""type"": ""integer"", ""description"": ""Element id OR type id. Omit (or set target='project_info') to write a Project Information field. Writing a type id affects every instance of that type."" },
          ""unique_id"": { ""type"": ""string"", ""description"": ""Stable Revit UniqueId. Alone resolves the current id; alongside target_id it must identify the same element."" },
          ""expected_category"": { ""type"": ""string"", ""description"": ""Prior-reading category; mismatch refuses the batch. Not enough alone: send unique_id."" },
          ""expected_type"": { ""type"": ""string"", ""description"": ""Prior-reading type; mismatch refuses the batch. Not enough alone: send unique_id."" },
          ""target"": { ""type"": ""string"", ""enum"": [""project_info""], ""description"": ""Write to the document's Project Information element instead of an id."" },
          ""parameter"": { ""type"": ""string"", ""description"": ""A BuiltInParameter name (e.g. KEYNOTE_PARAM), a shared/project parameter GUID, or a parameter name as it reads in the UI. A name matching more than one parameter is an error, not a guess."" },
          ""value"": { ""description"": ""String | number | boolean | null. Double strings (e.g. '3000 mm') are parsed in document units before Set and verified against the conversion. Integer strings use SetValueString with parse-only confirmation. Numbers use raw Revit internal units (feet for length)."" }
        }
      }
    },
    ""accept_raw_feet"": { ""type"": ""boolean"", ""default"": false,
                         ""description"": ""A bare number on a Length parameter is Revit's internal FEET. Without this, a number outside 0.1 mm-30 m is refused; true writes every bare number as feet (a real 32 m elevation is 104.99) and the row stays verified against your number. The rehearsal shows each bare number's reading in number_read_as."" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: write params"",
                            ""description"": ""The label of the single undo step this batch becomes."" },
    ""on_failure"": { ""type"": ""string"", ""enum"": [""atomic"", ""best_effort""], ""default"": ""atomic"",
                      ""description"": ""atomic: if ANY write fails, roll the whole batch back â€” nothing partial. best_effort: commit what worked and report the rest."" },
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the document to write to. It must be the document that is ACTIVE in Revit; this command will not switch documents for you. A write aimed at whatever window happens to be in front is a write aimed at whatever turns up."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token returned by the dry run of this exact request. Single-use, expires, and bound to this document and this request - if either changed, execution is refused and nothing is written."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
                   ""description"": ""Resolve every target and parameter and report what WOULD be written. Opens no transaction."" },
    ""allow_vary_between_groups"": { ""type"": ""boolean"", ""default"": true,
                                     ""description"": ""Call SetAllowVaryBetweenGroups(true) on project/shared parameters that do not vary yet. Without it Revit throws a modal at write time in any model with groups, which hangs the bridge. Reported as measured off the InternalDefinition, not assumed."" },
    ""target_document_title"": { ""type"": ""string"",
                                 ""description"": ""If given, the write aborts unless the active document's title matches. Writing to whichever model happened to be in front is how a batch lands in the wrong file."" },
    ""max_rows"": { ""type"": ""integer"", ""default"": 500, ""minimum"": 1,
                    ""description"": ""How many rows to include in the response. Totals are always exact regardless of this; truncation is reported."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_delete_verified",
                Command = "horizun_delete_verified",
                Description =
                    "Delete an explicit list of ElementIds, the elements a selector matches (mode=query), or purge unused elements to a real fixed point, and report " +
                    "only what the model confirms. Every id comes back with EXACTLY ONE verdict out of deleted | not_found | " +
                    "failed | skipped_still_in_use | skipped_protected | unexamined_unreadable_id | attempted_fate_unknown; " +
                    "the totals are disjoint and sum to requested_total. The first five are decided by re-resolving that id " +
                    "against the document AFTER the commit â€” never from the return of Delete(); the last two are the cases " +
                    "where we could not look, and they are never folded into a failure or a survival. Elements Revit cascaded " +
                    "away that you did not name are reported explicitly, attributed to the id that took them. A rolled-back " +
                    "transaction is an error, not a count. dry_run defaults to TRUE in both modes; this is destructive and it " +
                    "is a client's model.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""mode""],
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""enum"": [""ids"", ""purge_unused"", ""query""],
                ""description"": ""REQUIRED. ids: delete exactly the ids given. purge_unused: ask Revit for unused elements and delete them, repeating until a pass finds none. query: delete what `selector` matches; the dry run lists ids, the counted cascade and suggested protect_ids, the apply (same arguments + token) re-reads the selector and must find zero. Omission is refused; it never selects the broader purge operation."" },
    ""selector"": " + ElementSelectorSchema + @",
    ""ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
               ""description"": ""Required for mode='ids' (or unique_ids). Ids that do not resolve are reported as not_found, never dropped."" },
    ""unique_ids"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Aligned with the ids; mismatch refuses the call."" },
    ""expected_category"": { ""type"": ""string"", ""description"": ""Category (name or OST_*) when read. Not enough alone: send unique_ids."" },
    ""expected_type"": { ""type"": ""string"", ""description"": ""Type (name or 'Family: Type') when read. Not enough alone: send unique_ids."" },
    ""protect_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
                       ""description"": ""Never delete these, even if a purge pass calls them unused. Use for view templates you are about to assign rather than delete."" },
    ""allow_foreign_dependents"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Workshared, mode=ids: a cascade that takes elements not ours (no mark, not this session, other creator) is refused unless true."" },
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the document to delete from. It must be the document that is ACTIVE in Revit; this command will not switch documents for you. A delete aimed at whatever window happens to be in front is a delete aimed at whatever turns up."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token returned by the dry run of this exact request. Single-use, expires, and bound to this document and this request - if either changed, execution is refused and nothing is deleted."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
                   ""description"": ""Default TRUE in BOTH modes. Opens a transaction, asks Revit what would die (the real dependent closure, cascades included), then ROLLS BACK on purpose and returns a confirmation_token only for a fully resolved plan."" },
    ""max_passes"": { ""type"": ""integer"", ""default"": 8, ""minimum"": 1,
                      ""description"": ""Safety stop for purge. Hitting it is reported as converged:false â€” a stop, not a finish."" },
    ""transaction_name"": { ""type"": ""string"", ""description"": ""Name of the undo step."" },
    ""id_cap"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1,
                  ""description"": ""How many rows to list. Totals are exact regardless; every list states total vs shown vs truncated."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_document_session",
                Command = "horizun_document_session",
                Description =
                    "Open / save / save_as / close a Revit document, guarded against the irreversible. Before opening it " +
                    "reads the file's own Revit version off disk (BasicFileInfo, WITHOUT opening it) and the host's " +
                    "version, and refuses unless both match the REQUIRED expected_version - because opening a 2025 file " +
                    "on a 2026 host upgrades it and there is no downgrade. It runs THE SAME open guards as " +
                    "horizun_open_document, from one shared implementation, which means it now also refuses a " +
                    "workshared CENTRAL model unless detach=true or open_central=true (it used to open one without a " +
                    "word) and can open CLOUD models by GUID (it used to have no route to them at all). " +
                    "CLOSING a document with unsaved changes is refused unless you say discard_unsaved=true AND spend " +
                    "a confirmation_token from a dry_run: Close() discards the work, returns true, and leaves no trace " +
                    "afterwards, so a lost hour and an untouched document produce identical replies. Every close " +
                    "reports the IsModified it measured before closing. The API cannot close the ACTIVE document; " +
                    "activate_other=true makes this command activate another open document first (or its own empty " +
                     "anchor project when nothing else is open) and report which one, instead of refusing. " +
                    "A requested open_all_worksets / close_workset_names plan is re-read from the opened Document: " +
                    "workset_configuration_applied is true only when the observed IsOpen state proves the exact plan. " +
                    "If the file was already open, close_workset_names is refused; open_all_worksets succeeds only " +
                    "when measurement proves every user workset was already open, with applied=false and satisfied=true. " +
                    "A mismatch is a structured post-open failure carrying opened=true plus the document identity so " +
                    "an unattended caller can close what Revit actually opened. " +
                    "Saving reports bytes/mtime/format re-read from " +
                    "the filesystem after the write, never 'it did not throw'. Audit is an OPEN option in the Revit API, " +
                    "so audit_ran only ever describes the open. sync_with_central is OFF until the machine owner enables it " +
                    "in Revit (Advanced options); an omitted dry_run is an ESTIMATE whose token the apply needs. " +
                    "new_project creates a blank project from template_path (or Revit's DefaultProjectTemplate) at a .rvt " +
                    "save_as_path that must NOT exist - never overwrites; rehearses by default, applies with the token, " +
                    "re-reads file and document, and reports whether it was activated. activate targets an open document, " +
                    "previews by default; cloud activation uses its ModelPath. Unsaved or detached projects require a new " +
                    "local save_as_path and a token before SaveAs and activation. Desktop Connector roots are checked against its Windows registrations; an unregistered custom root cannot be detected. Revit may override local cloud changes " +
                    "when opening a restored, relinquished or out-of-date cloud model. " +
                    "batch: closed-file reads/actions/save/discard; token covers passed rows. exit_application: task-owned exit.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""open"", ""save"", ""save_as"", ""close"", ""inspect"", ""sync_with_central"", ""new_project"", ""activate""],
                     ""description"": ""inspect reads a file's version off disk unopened. sync_with_central is owner-gated; preview is an estimate. new_project creates a blank project from a template."" },
    ""file_path"": { ""type"": ""string"",
                     ""description"": ""open/inspect: the file to read. For save/save_as/close it is an ALIAS of target_document, kept for compatibility - it no longer defaults to the active document."" },
    ""target_document"": { ""type"": ""string"",
                     ""description"": ""REQUIRED for save, save_as and close: the full path or the exact title of the OPEN document to act on. There is no default. A save that does not name its target is a save aimed at whatever window happens to be in front, and a close discards whatever is unsaved in it. Unlike the other mutating commands this one does NOT require the document to be ACTIVE - saving a document open behind another is a legitimate request - but it does require you to say which."" },
    ""cloud_project_guid"": { ""type"": ""string"",
                     ""description"": ""open: ACC / BIM 360 PROJECT GUID as Revit knows it, instead of file_path. NOT the project id in the ACC web URL, which is a different identifier for the same project."" },
    ""cloud_model_guid"": { ""type"": ""string"",
                     ""description"": ""open: ACC / BIM 360 MODEL GUID as Revit knows it. NOT the 'urn:adsk.wipprod:dm.lineage:...' from the web UI - that decodes to a valid-looking GUID from the document manager, and opening it answers 'the central model is missing'. These GUIDs can be read off Revit's own CollaborationCache on disk."" },
    ""cloud_region"": { ""type"": ""string"", ""default"": ""US"",
                     ""description"": ""open: data centre region of the ACC hub, e.g. 'US' or 'EMEA'. Wrong region means the model is simply not found there."" },
    ""expected_version"": { ""type"": ""string"",
                            ""description"": ""REQUIRED for open. The Revit year you believe this is, e.g. '2026'. Checked against BOTH the file on disk and the host. Any disagreement aborts before the file is touched. For a CLOUD model only the host check can run - there is no local file whose version could be read first - and the response says so."" },
    ""allow_upgrade"": { ""type"": ""boolean"", ""default"": false,
                         ""description"": ""Opt in to opening a file OLDER than the host. This upgrades it and CANNOT be undone. Nothing else in this tool will do it for you, and nothing can open a file NEWER than the host: there is no downgrade."" },
    ""audit"": { ""type"": ""boolean"", ""default"": false, ""description"": ""open only: open with Audit. This is the ONLY place the Revit API accepts an audit flag."" },
    ""detach"": { ""type"": ""boolean"", ""default"": false, ""description"": ""open only: detach from central, preserving worksets. The safe way to read a central model, on disk or in the cloud."" },
    ""open_central"": { ""type"": ""boolean"", ""default"": false,
                     ""description"": ""open only: permit opening the CENTRAL model directly - working in the file everyone else synchronizes to. REQUIRED for a cloud model unless you pass detach, because a model in ACC / BIM 360 IS the central. Prefer detach."" },
    ""open_all_worksets"": { ""type"": ""boolean"", ""default"": false,
                      ""description"": ""open only: open every workset. If the document is already open this cannot be applied retroactively: the call succeeds only when post-read evidence proves every user workset is already open, and reports satisfied=true with applied=false. Needed when something downstream MEASURES worksets, because a closed workset is indistinguishable from an empty one. IT CAN ALSO KILL REVIT - measured: 2 of 24 ACC models took Revit 2025.4 down with an access violation inside SelectedPartitionsForEdit on open, and both opened fine with this left false. Off by default, and the first thing to drop when a specific model dies on open."" },
    ""close_workset_names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""maxItems"": 128,
                      ""description"": ""open only: exact user-workset names to keep CLOSED while every other user workset opens. Resolved from the unopened file before opening; a missing or ambiguous name refuses. After opening, every requested name and every other user workset are re-read from the Document; workset_configuration_applied is true only when that observed state proves the exact plan. Mutually exclusive with open_all_worksets=true. This is for honest partial-load audits: the reply can measure which content was unavailable instead of accidentally opening all and testing the wrong condition."" },
    ""on_open_dialog"": { ""type"": ""string"", ""enum"": [""cancel"", ""dismiss"", ""resolve_in_memory""], ""default"": ""cancel"",
                     ""description"": ""open only: 'cancel' (default) presses Cancel; 'dismiss' acknowledges and continues. 'resolve_in_memory' requires detach=true and requests default error resolutions; after opening, each requested deletion ID is reported as verified_deleted, still_present or unverified. The recovered document is protected from overwriting the source. Scoped to the open call; every other dialog still cancels."" },
    ""save_as_path"": { ""type"": ""string"", ""description"": ""save_as: absolute destination. new_project: new .rvt. activate: new local .rvt or .rfa for unsaved or detached documents, never overwritten."" },
    ""template_path"": { ""type"": ""string"", ""description"": ""new_project: absolute path of the .rte to create from. Omitted: this Revit's DefaultProjectTemplate (Options > File Locations), named in the reply as template_source; none configured is a refusal, never a template-less project."" },
    ""compact"": { ""type"": ""boolean"", ""default"": false, ""description"": ""save/save_as: pass Compact to the API. The response reports the byte delta it actually produced."" },
    ""comment"": { ""type"": ""string"", ""description"": ""sync_with_central: stored in central; at most 30000 chars."" },
    ""relinquish"": { ""type"": ""string"", ""enum"": [""all"", ""keep_borrowed"", ""borrowed_only"", ""none""], ""default"": ""all"", ""description"": ""sync_with_central: ownership to give back. borrowed_only returns only the elements borrowed one by one and keeps owned worksets."" },
    ""overwrite"": { ""type"": ""boolean"", ""default"": false, ""description"": ""save_as: allow overwriting an existing destination file."" },
    ""max_backups"": { ""type"": ""integer"", ""minimum"": 1, ""description"": ""save_as: cap the .000N backup pile Revit leaves behind."" },
    ""force_workshared"": { ""type"": ""boolean"", ""default"": false,
                            ""description"": ""Required to save/save_as a workshared document or close one with save_on_close, also when that state is unreadable. Save/close never sync; saving a central writes it."" },
    ""save_on_close"": { ""type"": ""boolean"", ""default"": false, ""description"": ""close: save before closing. Off by default - closing should not be a write you did not ask for."" },
    ""discard_unsaved"": { ""type"": ""boolean"", ""default"": false,
                     ""description"": ""close: REQUIRED to close a document that has unsaved changes without saving them. Close() discards them, returns true, and leaves nothing behind to detect it - the file on disk is untouched and IsModified cannot be asked of a closed document, so an hour of lost edits and an untouched model produce identical responses. Not enough on its own: a dry_run token is required too. Unknown counts as modified."" },
    ""dry_run"": { ""type"": ""boolean"",
                     ""description"": ""Defaults depend on operation: new_project and sync_with_central rehearse by default; save, save_as and close default to false. save/save_as: validate target and options without calling Save or touching disk. close: rehearse without closing or activating; may issue a discard confirmation. inspect is read-only. open with dry_run=true is explicitly refused."" },
    ""activate_other"": { ""type"": ""boolean"", ""default"": false,
                     ""description"": ""close: Revit's API cannot close the ACTIVE document, so closing the last document of a batch used to need a decoy opened by hand (and a relaunched batch SKIPPED the model that stayed open). With this true, the command activates another open document first - or opens the bridge's own empty anchor project when nothing else qualifies - then closes the target, and REPORTS which document it activated. Off by default because activation changes what the user is looking at; it must be asked for, never a side effect."" },
    ""confirmation_token"": { ""type"": ""string"",
                     ""description"": ""new_project: the token from its rehearsal. close: the token from a dry_run, required alongside discard_unsaved=true. Single use, expires, and bound to THIS document and THIS request - if either changes it is refused and nothing is closed."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_model_diff",
                Command = "horizun_model_diff",
                Description =
                    "What changed between two model deliveries, the model explained, and quality over time. snapshot: " +
                    "record the active model, or a .rvt opened detached and closed unsaved (file_path + expected_version), " +
                    "to %USERPROFILE%\\.horizun\\snapshots\\<id>.json.gz: per element UniqueId, category, family/type, " +
                    "level, workset, phases, bbox, location, volume, instance/type parameters (internal units), geometry hash. " +
                    "list: stored snapshots. compare before/after (snapshot id or 'active'): added, deleted, modified " +
                    "(parameter before/after, moves, type changes) by OST_ category (language-independent, with changed_fields)/discipline/level, paged, " +
                    "CSV+JSON exported. Identity is UniqueId; a re-created model is flagged and heuristic_match pairs by " +
                    "category/type/location, marked inferred. compare and colorize report identity_proven: true only when " +
                    "both sides carry the same document creation GUID (Revit 2024+); with only Project Information " +
                    "(Revit 2023, which every project of one template shares) it is false, project_identity_kind is " +
                    "project_info and warnings carries identity_unprovable_2023. " +
                    "colorize: overrides added/modified in a new copy of view_id " +
                    "(the only write; dry_run token, re-read). explain: facts-only summary; ISO 19650 gaps from " +
                    "project_context_path. record_quality: runs model_scan/audit_model, appends to quality-history\\" +
                    "<project>.jsonl; quality_trend: rows for horizun_power_bi_push + CSV.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""snapshot"", ""list"", ""compare"", ""colorize"", ""explain"", ""record_quality"", ""quality_trend""] },
    ""target_document"": { ""type"": ""string"", ""description"": ""Title of the active document; required by colorize, checked by reads."" },
    ""file_path"": { ""type"": ""string"" },
    ""expected_version"": { ""type"": ""string"" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""OST_ names or category names."" },
    ""max_elements"": { ""type"": ""integer"", ""default"": 50000, ""minimum"": 1 },
    ""before"": { ""type"": ""string"" },
    ""after"": { ""type"": ""string"", ""default"": ""active"" },
    ""move_tolerance_mm"": { ""type"": ""number"", ""default"": 1 },
    ""heuristic_match"": { ""type"": ""boolean"", ""default"": false },
    ""offset"": { ""type"": ""integer"", ""default"": 0 },
    ""limit"": { ""type"": ""integer"", ""default"": 100 },
    ""view_id"": { ""type"": ""integer"" },
    ""source"": { ""type"": ""string"", ""enum"": [""model_scan"", ""audit_model""] },
    ""project"": { ""type"": ""string"" },
    ""metrics"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""project_context_path"": { ""type"": ""string"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_file_info",
                Command = "horizun_file_info",
                Description =
                    "Read Revit files' headers off disk - format/saved version, is_workshared, is_central, is_local, " +
                    "central path - WITHOUT opening any of them and WITHOUT an active document. The folder triage " +
                    "every batch starts with, which used to be hand-written in execute_python and needed a blank " +
                    "document open just to satisfy the active-document check. Pass 'paths' (a list) or 'folder' " +
                    "(swept for 'pattern', default *.rvt, optionally recursive). Nothing is opened, so nothing is " +
                    "upgraded. Each file names its own read_error when unreadable; the summary counts " +
                    "readable/unreadable/missing/not_revit_files. " +
                    "WHEN THE HEADER CANNOT BE READ, the reply also carries the file's first 8 bytes as " +
                    "'signature', a plain-language 'signature_means' and the boolean 'is_revit_container' - and " +
                    "you must read those before repeating the read_error. Revit's own message for that case names " +
                    "two causes and BOTH are about Revit files ('a newer format file... or saved in a very old " +
                    "version'), so a ZIP renamed .rvt is reported as a version problem: measured, that cost two " +
                    "false diagnoses on the same two files, and a sweep of 3,252 files found 1,193 of them. " +
                    "d0cf11e0a1b11ae1 = the OLE container a genuine .rvt/.rfa uses, so the version story is worth " +
                    "believing. 504b0304 = ZIP, i.e. NOT a model (a renamed package). Anything else is returned as " +
                    "hex and deliberately not interpreted. Read-only.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""paths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" },
                 ""description"": ""Explicit file paths to read, in order. Combine with 'folder' or use alone. At least one of paths/folder is required."" },
    ""folder"": { ""type"": ""string"",
                  ""description"": ""A directory to sweep for 'pattern'. Its matches follow any explicit 'paths', de-duplicated. At least one of paths/folder is required."" },
    ""pattern"": { ""type"": ""string"", ""default"": ""*.rvt"",
                   ""description"": ""Glob for the folder sweep. Default *.rvt. Use *.rfa for families."" },
    ""recursive"": { ""type"": ""boolean"", ""default"": false,
                     ""description"": ""Sweep subfolders too. Off by default."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_acc_upload_status",
                Command = "horizun_acc_upload_status",
                Description =
                    "Has each of these files been assigned an ACC cloud folder yet, or is its upload still " +
                    "pending? Copying into the Desktop Connector folder and hashing proves the LOCAL CACHE, not " +
                    "the cloud - the upload is a later async step that fails under throttling (measured: 3 of 8 " +
                    "files silently unuploaded while every local hash checked out). This reads the connector's " +
                    "OWN log on this machine: the Name-beside-ParentFolderUrn record it writes when an upload " +
                    "completes. Pass 'names' and/or 'paths' (basenames are used); optional 'project_id' turns " +
                    "each hit into an ACC Docs URL. has_folder_urn=true is the connector's testimony, not a " +
                    "cloud API check; false is absence of evidence, never proof of absence - pending, failed, " +
                    "or synced under another name look identical from here, and the reply says so. A log file " +
                    "that cannot be read is reported per file and makes the counts declared lower bounds. " +
                    "Read-only, touches no model, needs no document open.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" },
                 ""description"": ""File names to look up, with or without extension. At least one of names/paths is required."" },
    ""paths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" },
                 ""description"": ""Paths whose BASENAMES are looked up - pass the same paths you copied into the Desktop Connector folder. At least one of names/paths is required."" },
    ""project_id"": { ""type"": ""string"",
                      ""description"": ""Optional ACC project id; each recorded folder is also returned as an acc.autodesk.com Docs URL."" },
    ""wal_root"": { ""type"": ""string"",
                    ""description"": ""Optional override of the Desktop Connector data folder, for nonstandard installs. Default: %LOCALAPPDATA%\\Autodesk\\Desktop Connector\\Data\\Autodesk.DataSourceType.BIMDocs."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_audit_model",
                Command = "horizun_audit_model",
                Description = @"Read-only audit of the named model: warnings, groups, families, CAD, views, rooms, links, design options, weight, coordinates, datums, wall drift, 4D/5D and optional template comparison. Lists report totals; unreadable checks fail explicitly. Templates open detached and close without saving.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document""],
  ""allOf"": [{
    ""if"": { ""anyOf"": [{ ""required"": [""level_id""] }, { ""required"": [""wall_ids""] }, { ""required"": [""tolerance_mm""] }] },
    ""then"": { ""required"": [""operation""], ""properties"": { ""operation"": { ""const"": ""wall_slab"" } } }
  }],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""warnings"", ""wall_slab""], ""description"": ""warnings: read warning occurrences grouped by description and severity; paged by individual failing element, with unique_id and suggested typed calls requiring input. wall_slab: sample five stations per supported host wall; level status counts cover the requested page. Omit for the existing audit."" },
    ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""warnings or wall_slab: record offset; model changes can invalidate pages."" },
    ""page_size"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""default"": 100 },
    ""level_id"": { ""type"": ""integer"", ""minimum"": 1, ""description"": ""wall_slab: base constraint level."" },
    ""wall_ids"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""integer"", ""minimum"": 1 }, ""description"": ""wall_slab: host wall ids."" },
    ""tolerance_mm"": { ""type"": ""number"", ""minimum"": 0, ""default"": 5, ""description"": ""wall_slab: contact tolerance; omitted means SUPUESTO."" },
    ""byte_budget"": { ""type"": ""integer"", ""minimum"": 1024, ""maximum"": 524288, ""default"": 65536, ""description"": ""Maximum UTF-8 bytes of the warnings result body."" },
    ""confirm_large_operation"": { ""type"": ""boolean"", ""description"": ""Explicitly confirm execution when the conservative scope estimate exceeds the large-model threshold."" },
    ""target_document"": { ""type"": ""string"",
               ""description"": ""REQUIRED: the exact title of the document to audit. This command acts on the ACTIVE document and will NOT switch for you - naming a different one refuses. An audit is a claim about a named model, and a report naming the wrong model is worse than no report."" },
    ""cooperative"": {
      ""type"": ""object"",
      ""additionalProperties"": false,
      ""description"": ""OPT-IN cooperative reading. OMIT IT AND NOTHING CHANGES: this reader behaves exactly as it always has, and the reply carries no extra field. Send it when a PARTIAL answer that arrives is worth more to you than a complete one nobody waited for - a scan of a large model holds Revit's UI thread for as long as it runs, and the person whose Revit it is cannot click anything meanwhile. With it, the read asks BETWEEN elements whether to carry on: it stops if you have gone away, and it stops at your budget. A result that stopped early says PARTIAL and never passes itself off as clean. WHAT THIS DOES NOT PROVE: ui_budget_ms bounds how long the command SPENDS, not that Revit's interface stayed responsive - the command holds the UI thread for its whole duration, the reply still travels back through the pipe, and Revit's own event queue decides when the window repaints. Stopping work and releasing the interface are different events, and only a sampler watching from outside can measure the second."",
      ""properties"": {
        ""ui_budget_ms"": {
          ""type"": ""integer"",
          ""minimum"": 1000,
          ""maximum"": 600000,
          ""default"": 20000,
          ""description"": ""How long this read may hold Revit's UI thread before it stops and says so. Twenty seconds by default, chosen against what a person does: below about that a frozen window reads as 'it is working'; past it people start clicking, then killing Revit.""
        },
        ""max_units"": {
          ""type"": ""integer"",
          ""minimum"": 1,
          ""description"": ""A hard bound on elements EXAMINED, independent of the clock. Separate from the budget on purpose: a bound in units is reproducible across machines and a bound in milliseconds is not.""
        },
        ""cursor"": {
          ""type"": ""string"",
          ""description"": ""Continue a previous partial read. Opaque, and REFUSED against a different request: resuming one query at another query's position produces a page of the wrong elements with nothing to show that anything went wrong. Where this command already pages with its own cursor or offset, the reply says so and issues none here - two continuation tokens over different things is worse than either.""
        }
      }
    },
    ""top"": { ""type"": ""integer"", ""default"": 20, ""minimum"": 1,
               ""description"": ""How many items to list per finding. Totals are always exact regardless of this."" },
    ""requirement_set"": { ""type"": ""object"",
               ""description"": ""A declarative pre-delivery gate over what the audit measured - the standard arrives here, nothing is compiled in. NUMBERS: max_warnings, max_in_place_families, max_views_off_sheets, max_file_mb, max_open_mep_connectors, max_unpinned_links, max_views_without_template, max_elements_far_from_origin, max_links_reflected, max_links_rotated, max_links_not_sharing_position, max_duplicate_level_names, max_coincident_levels, max_levels_without_views, max_levels_without_elements, max_duplicate_grid_names, max_coincident_grids, max_grids_off_axis. BOOLEANS: forbid_orphan_group_types, forbid_imported_cad, forbid_room_problems (true enforces, false waives AND records the waiver). LISTS, which produce one row PER ITEM so a failure names the item: require_coordinate_facts (internal_origin, project_base_point, survey_point, project_location, true_north, length_units), require_4d_roles and require_5d_roles (role ids you declared in readiness_roles). The reply gains a gate block with per-requirement rows and a verdict: pass, fail, or not_assessable - a check with incomplete coverage can FAIL a limit (the count is at least that) but can never PASS one, and an unknown requirement refuses the whole gate rather than reading like one that passed. A TOLERANCE passed here is refused and told where it belongs."" },
    ""tolerances"": { ""type"": ""object"",
               ""description"": ""Configuration for the checks, NOT assertions - a tolerance cannot pass or fail, so it lives here rather than in requirement_set, and passing one there is refused. origin_distance_mm (how far from the INTERNAL ORIGIN counts as far, default 1000000), link_origin_offset_mm, level_coincidence_mm (default 1), grid_coincidence_mm (default 1), grid_axis_tolerance_degrees (default 0.5). An unknown tolerance refuses the call rather than being ignored, because a misspelled one silently dropped leaves the check running on its default while you believe otherwise."" },
    ""readiness_roles"": { ""type"": ""array"", ""maxItems"": 64,
               ""description"": ""What 4D/5D readiness MEANS for this project. No parameter name is compiled in, so with nothing declared readiness reports not_assessable rather than 'not ready'. Each entry: { id, dimension (4d | 5d | traceability | classification | quantity), parameter_names: [the names this organisation uses, tried in order], blank_is_absent (default true) }. A parameter that EXISTS and is blank is reported differently from one that does not exist - the first is a model set up and not filled in, the second is a model not set up, and collapsing them destroys the only thing worth knowing."",
               ""items"": { ""type"": ""object"", ""required"": [""id"", ""dimension"", ""parameter_names""],
                 ""properties"": {
                   ""id"": { ""type"": ""string"" },
                   ""dimension"": { ""type"": ""string"", ""enum"": [""4d"", ""5d"", ""traceability"", ""classification"", ""quantity""] },
                   ""parameter_names"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1 },
                   ""blank_is_absent"": { ""type"": ""boolean"", ""default"": true }
                 } } },
    ""warning_profile"": { ""type"": ""object"",
      ""description"": ""Optional. Your triage for Revit warnings, keyed by FailureDefinitionId GUID ONLY - a profile keyed on the description text stops matching the day Revit is upgraded or the session language changes, and it stops matching silently. Needs a version; each entry takes severity and optionally label. Revit's OWN severity is always reported beside yours, never replaced by it. Without a profile no warning is triaged, which is NOT a pass."",
      ""properties"": { ""version"": { ""type"": ""string"" } }, ""required"": [""version""] },
    ""propose_corrections"": { ""type"": ""boolean"", ""default"": false,
               ""description"": ""Opt-in. Adds a 'corrections' block of TYPED PROPOSALS beside the findings - proposal_id, the typed tool, its arguments, preconditions, expected outcome, risk, reversibility, ambiguities and dry_run. NOTHING IS EXECUTED and executed:false says so. A proposal may name only a tool in the built-in correction registry (published in the same block), with arguments built from typed fields and the registry's own typed constants - never from a finding's text. horizun_execute_python is not in it. States: actionable, requires_input, unsupported, unsafe, already_resolved, not_applicable. A finding whose element list was TRUNCATED yields requires_input rather than a correction over an unknown scope."" },
    ""store_snapshot"": { ""type"": ""boolean"", ""default"": false,
               ""description"": ""Opt-in. Stores this read-only audit measurement under the local Horizun data root, never beside the RVT, and compares it with the previous verified snapshot of the same model. The file is hash-verified, atomically replaced and sanitised before writing. This writes no Revit model data."" },
    ""health_profile"": { ""type"": ""object"",
               ""description"": ""Opt-in, versioned weights for a transparent health index. No weights are compiled in. Fields: id, version, context, weights [{dimension (an audit check name), weight (>0), critical}]. Each dimension is binary finding presence and the reply includes every deduction, coverage, unassessed dimensions and plausible range; a critical unmeasured dimension suppresses the headline score."",
               ""required"": [""id"", ""version"", ""weights""],
               ""properties"": {
                 ""id"": { ""type"": ""string"" }, ""version"": { ""type"": ""string"" },
                 ""context"": { ""type"": ""string"", ""enum"": [""project"", ""template"", ""family"", ""coordination""] },
                 ""weights"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""object"",
                   ""required"": [""dimension"", ""weight""], ""properties"": {
                     ""dimension"": { ""type"": ""string"" }, ""weight"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
                     ""critical"": { ""type"": ""boolean"", ""default"": false }
                   }, ""additionalProperties"": false } }
               }, ""additionalProperties"": false },
    ""prevention_gate"": { ""type"": ""object"",
               ""description"": ""Opt-in. Asks whether an operation may proceed GIVEN THIS AUDIT. Answers allow, block, requires_override or not_assessable, and it DECIDES rather than enforces - nothing here cancels a save, and the matrix in docs/evidence/prevention-operation-matrix.md keeps 'gate possible' and 'gate implemented' apart. THE ASYMMETRY: incomplete coverage may BLOCK and may never ALLOW, because a defect found in the part that was examined is real while 'nothing wrong here' is a claim about a whole model that was half looked at. Coverage comes from this run, not from the caller."",
      ""properties"": {
        ""operation"": { ""type"": ""string"", ""enum"": [""save"", ""save_as"", ""sync_with_central"", ""export"", ""publish"", ""close_with_save"", ""batch_open_close""] },
        ""profile_version"": { ""type"": ""string"" },
        ""now_utc"": { ""type"": ""string"", ""description"": ""Optional in general, but REQUIRED whenever the override carries an expiry: nothing here reads a clock, so this is the only time the gate has. An override with an expiry and no now_utc is REFUSED rather than honoured - an expiry nobody can evaluate is not a pass. Keeping the clock out of the rules is what makes an expiry exact in a test."" },
        ""override"": { ""type"": ""object"", ""description"": ""A SIGNED STATEMENT, not a flag: who, when, which operation, which profile, and exactly which findings are accepted. It covers only the findings it NAMES, is refused for another operation or profile version, and expires by comparison."",
          ""properties"": {
            ""identity"": { ""type"": ""string"" }, ""reason"": { ""type"": ""string"" },
            ""timestamp_utc"": { ""type"": ""string"" }, ""operation"": { ""type"": ""string"" },
            ""profile_version"": { ""type"": ""string"" }, ""evidence"": { ""type"": ""string"" },
            ""expires_utc"": { ""type"": ""string"" },
            ""findings_ignored"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
          }, ""additionalProperties"": false }
      }, ""required"": [""operation""], ""additionalProperties"": false },
    ""workset_rules"": { ""type"": ""object"",
      ""description"": ""Optional. Which workset each category belongs on, plus the names YOUR session calls an un-renamed default - Revit's own default workset name is localized, so none is compiled in. Needs a version. Keys: by_category, default_workset_names, max_elements_in_wrong_workset. WITH A CLOSED WORKSET the check may FAIL but can never PASS: a closed workset's elements are not in the document, so 'nothing found' would be a claim about elements nobody loaded."",
      ""properties"": { ""version"": { ""type"": ""string"" } }, ""required"": [""version""] },
    ""template_path"": { ""type"": ""string"",
      ""description"": ""Opt-in, enables the template_comparison finding. An absolute path to a project template (.rte/.rvt), opened DETACHED in the background and closed without saving. Compares project parameters, view filters, line patterns, fill patterns, object styles and view types by identity (shared parameters by guid, everything else by name)."" },
    ""spf_path"": { ""type"": ""string"",
      ""description"": ""Opt-in, enables the template_comparison finding. An absolute path to a shared parameter file, read directly from disk (never opened as the session's own SPF). Compared against the model's shared-bound project parameters by guid; a name shared with a different guid is reported as a name_collision - two parameters wearing one label, not one that drifted."" },
    ""wall_sketch_drift_tolerance_mm"": { ""type"": ""number"", ""minimum"": 0, ""default"": 2,
      ""description"": ""How far a wall's sketch may sit from its current location line before wall_sketch_drift reports it stranded. Shares its default with horizun_transform_elements realign_wall_sketch's own tolerance_mm."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_apply_corrections",
                Command = "horizun_apply_corrections",
                Description = @"THE CORRECTION CYCLE. correction_plan: typed rehearsal, token, per-step apply, final re-read of ALL targets; PDF re-review pending. Otherwise choose current audit findings by finding_id and fingerprint; registry rehearses and applies typed corrections, then re-audits targets (corrected, persistent, failed, not_verifiable). Unnamed findings are skipped; empty selection refused. Scope only narrows. Checks before rehearsal and apply refuse stale_plan without writing. Missing input returns requires_input and no token. A DESTRUCTIVE action must LIST element_ids: omission is requires_input rather than read as every element the finding named. ROLLBACK SCOPE IS PER ACTION; use horizun_execute_plan for an atomic group. horizun_execute_python is not reachable from here.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document""],
  ""oneOf"": [{""required"": [""finding_set_fingerprint"", ""actions""], ""not"": {""required"": [""correction_plan""]}}, {""required"": [""correction_plan""], ""not"": {""anyOf"": [{""required"": [""actions""]}, {""required"": [""finding_set_fingerprint""]}]}}],
  ""properties"": {
    ""correction_plan"": { ""type"": ""object"", ""description"": ""Resolved review plan: pasos[{id, herramienta, argumentos{target_document, dry_run:true, writes or actions}}], manual[]. No Python, placeholders, CSV, sequence or revision clouds. Rehearse/apply here with the outer token; re-read after all steps."" },
    ""target_document"": { ""type"": ""string"", ""description"": ""REQUIRED. The document the audit was taken on; it must be ACTIVE, and its fingerprint must match the audit's."" },
    ""finding_set_fingerprint"": { ""type"": ""string"", ""description"": ""REQUIRED. From the horizun_audit_model reply. Names one run at one top in THIS session; finding ids belong to it and do not survive a restart."" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100,
      ""description"": ""The findings to act on. One entry per finding_id; an empty array is refused rather than read as all."",
      ""items"": { ""type"": ""object"", ""required"": [""finding_id""], ""properties"": {
        ""finding_id"": { ""type"": ""string"", ""description"": ""A finding_id from the audit's findings[]."" },
        ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""integer"" }, ""description"": ""Optional narrowing to some of the elements the finding listed. An id the finding never named refuses the action as scope_widened."" },
        ""inputs"": { ""type"": ""object"", ""description"": ""Answers to the recipe's required inputs and nothing else - e.g. {\""template_view_id\"": 123} for views_without_template. An input the recipe did not ask for is refused."" }
      }, ""additionalProperties"": false } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true: rehearse every action through its typed tool, write nothing, and return a confirmation_token if all rehearsed cleanly. false: apply under the token."" },
    ""confirmation_token"": { ""type"": ""string"", ""description"": ""REQUIRED when dry_run=false: the token this exact request's dry run returned. Single use; bound to the document, the audit, the action set and each typed tool's resolved plan."" },
    ""idempotency_key"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200,
      ""description"": ""The shared rule, and it is true here: a retry with the SAME key returns the recorded reply and runs nothing. Measured 2026-09-03. What also prevents a second application, and is stronger, is that confirmation_token is single use and the cited checks are re-run before the apply - so the same actions under a NEW key are refused as a spent token or as a stale plan, with nothing written."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_quantities",
                Command = "horizun_quantities",
                Description = "Volume mode compares parameter, geometry and material takeoff in m3, with measured coverage and agreement only over common elements. takeoff measures caller-defined quantities per element and code, optionally including links with placement provenance; units must match. room_finishes measures gross faces and openings; carbon uses caller factors. Missing and failed readings are distinguished from zero. Read-only.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""confirm_large_operation"": { ""type"": ""boolean"", ""description"": ""Explicitly confirm execution when the conservative scope estimate exceeds the large-model threshold."" },
    ""target_document_title"": { ""type"": ""string"",
      ""description"": ""Title of the document you believe is active. When present, the takeoff ABORTS if the active document differs - the same guard model_scan has, and it matters MORE here because this number gets billed: a takeoff of the wrong model is a priced bill of quantities for a file nobody looked at. '.rvt' is optional. Optional for compatibility; pass it whenever more than one document is open."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
                       ""description"": ""Elements to measure. Omit and pass 'category' instead to sweep a whole category."" },
    ""category"": { ""type"": ""string"",
                    ""description"": ""BuiltInCategory name, e.g. OST_StructuralFraming, OST_Walls, OST_Floors. Used when element_ids is omitted. In mode takeoff, 'categories' sweeps several at once."" },
    ""detail_level"": { ""type"": ""string"", ""enum"": [""Coarse"", ""Medium"", ""Fine""], ""default"": ""Fine"",
                        ""description"": ""Geometry detail level. Fine is the default here on purpose: Coarse geometry under-reports, and this number gets billed."" },
    ""tolerance_pct"": { ""type"": ""number"", ""default"": 1.0,
                         ""description"": ""Relative disagreement above which sources are flagged as not agreeing."" },
    ""cooperative"": " + CooperativeReadSchema + @",
    ""top"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1,
                ""description"": ""Max element rows returned. Totals and coverage are EXACT and independent of this; a shortened list sets truncated=true and rows_matching says how many there were."" },
    ""code_parameter"": { ""type"": ""string"", ""description"": ""Parameter carrying each element's budget/classification code (instance first, then type). Supplied per call - no organisation's parameter is compiled in. Adds 'code' per row and a by_code rollup whose sums state how many elements they cover."" },
    ""only_disagreements"": { ""type"": ""boolean"", ""default"": false,
                              ""description"": ""List only the elements whose sources disagree. Totals still cover everything."" },
    ""phase"": { ""type"": ""string"", ""description"": ""room_finishes, group_by=room (required): the phase name. No default: rooms and door sides are per phase."" },
    ""group_by"": { ""enum"": [""room""], ""description"": ""takeoff: add a by_room rollup in phase; misses are (unassigned)."" },
    ""level"": { ""type"": ""string"", ""description"": ""room_finishes: only rooms/spaces on this level (name)."" },
    ""carbon_factors"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""object"", ""required"": [""factor"", ""per""],
      ""properties"": { ""material"": { ""type"": ""string"" }, ""material_class"": { ""type"": ""string"" }, ""factor"": { ""type"": ""number"" }, ""per"": { ""enum"": [""m3"", ""kg""] } } },
      ""description"": ""carbon (required): kgCO2e per m3 or per kg, keyed by material name or class. None compiled in."" },
    ""factor_source"": { ""type"": ""string"", ""description"": ""carbon (required): where the factors came from (EPD list, EC3 export)."" },
    ""mode"": { ""type"": ""string"", ""enum"": [""volume"", ""takeoff"", ""room_finishes"", ""carbon""], ""default"": ""volume"",
      ""description"": ""volume: 3-source m3 check (default). takeoff: caller quantities by code. room_finishes: gross room faces, openings apart. carbon: materials x caller factors."" },
    ""quantities"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50,
      ""description"": ""takeoff only. Each: {name, source, parameter?, unit}. source parameter reads a named parameter (instance first, then type; Length/Area/Volume specs come back in m/m2/m3 and 'unit' must say so; other specs raw in your unit; text is invalid, never parsed). geometry_volume (m3) and geometry_area (m2: the total face area of the solids) read the geometry at detail_level; length (m) reads the location curve; count is 1 per element. 'unit' is yours and is written on every reading - nothing is compiled in."",
      ""items"": { ""type"": ""object"", ""required"": [""name"", ""source"", ""unit""], ""additionalProperties"": false,
        ""properties"": {
          ""name"": { ""type"": ""string"", ""minLength"": 1 },
          ""source"": { ""type"": ""string"", ""enum"": [""parameter"", ""geometry_volume"", ""geometry_area"", ""length"", ""count""] },
          ""parameter"": { ""type"": ""string"", ""description"": ""Required when source is parameter; refused otherwise."" },
          ""unit"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""The unit this quantity is billed in, e.g. m3, m2, m, kg, un. Must be m3 / m2 / m for geometry_volume / geometry_area / length."" }
        } } },
    ""categories"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 50, ""items"": { ""type"": ""string"" },
      ""description"": ""takeoff only. Several BuiltInCategory names measured as ONE takeoff (e.g. [\""OST_StructuralFraming\"", \""OST_StructuralColumns\"", \""OST_Floors\""]), instead of one call per category whose replies would have to be joined by hand. Exclusive with 'category' and with element_ids; include_links sweeps every listed category in every loaded link."" },
    ""rows_file"": { ""type"": ""boolean"", ""default"": false,
      ""description"": ""takeoff only. Also write the COMPLETE reply - every row, ignoring 'top' - as JSON to the bridge's own folder (<data root>/takeoffs), and report it in rows_file {written, path, rows, bytes, sha256}. Pass rows_file.path as horizun_budget_compare model_rows_path so a takeoff of hundreds or thousands of rows never travels through the conversation; the inline reply stays capped by 'top'. The location is not caller-chosen, so this needs no permission beyond reading the model. A write that fails is reported as written=false with the error; the measurement itself is still returned."" },
    ""classification_parameter"": { ""type"": ""string"",
      ""description"": ""takeoff only, required. The parameter (instance first, then type) carrying each element's budget code. Each row's classification_code is the value, or '(no such parameter)', '(empty)', '(unreadable)' - three non-values kept apart."" },
    ""include_links"": { ""type"": ""boolean"", ""default"": false,
      ""description"": ""takeoff only. Also sweep every LOADED RevitLinkInstance for 'category' (needs category, not element_ids: an id is only unique inside one document). Each row carries element_id, document, document_path, link_instance_id and the placement number, and the documents block adds the link's transform for provenance; links that are not loaded are listed in links_not_loaded and make coverage_complete false. TWO INSTANCES OF ONE LINKED FILE are two placements: the same element id comes back once per placement, told apart by link_instance_id, counted once per placement (which is what the model says), and every repeated file is named in repeated_link_documents."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_code_check",
                Command = "horizun_code_check",
                Description =
                    "Evaluate a declarative requirement set over the active model: parameter assertions and geometric measures " +
                    "(doors, ramps, stairs, 2R+T, space illuminance, exits per level, travel_distance_m). Examples: standards/co-*.json. " +
                    "operation=travel_distance routes egress per room with Revit's path of travel; create_paths: dry run, token, re-read. energy_readiness: read-only energy-model gaps, WWR by orientation. headroom: clear height by vertical rays.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""check"", ""travel_distance"", ""energy_readiness"", ""headroom""], ""default"": ""check"" },
    ""target_document"": { ""type"": ""string"" },
    ""requirement_set"": { ""type"": ""object"", ""description"": ""Inline set, or give requirement_set_path."" },
    ""requirement_set_path"": { ""type"": ""string"" },
    ""max_findings"": { ""type"": ""integer"" },
    ""include_passes"": { ""type"": ""boolean"" },
    ""travel"": { ""type"": ""object"", ""description"": ""{view_ids, exits:{parameter,value?}|{mark_prefix}|{element_ids}, room_ids?, max_m?, create_paths?}"" },
    ""headroom"": { ""type"": ""object"", ""description"": ""{view_id, element_ids|categories, min_mm, direction?, spacing_mm?, targets?}"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_link_schedule",
                Command = "horizun_link_schedule",
                Description =
                    "4D XML/CSV/XER: import, match, write, colour. Rehearse, confirm, reread.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation"", ""schedule_path""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""import"", ""match"", ""write"", ""status_view""] },
    ""target_document"": { ""type"": ""string"" },
    ""schedule_path"": { ""type"": ""string"" },
    ""match"": { ""type"": ""object"", ""description"": ""{parameter, key:id|wbs} or {rules:[{activity,category,level?,parameter?,value?}]}"" },
    ""write"": { ""type"": ""object"", ""properties"": {
      ""activity_parameter"": { ""type"": ""string"" },
      ""activity_field"": { ""enum"": [""name"",""id"",""wbs""], ""default"": ""name"" },
      ""allow_unmarked"": { ""type"": ""boolean"", ""default"": false, ""description"": ""HRZ_* outputs only."" },
      ""start_parameter"": { ""type"": ""string"" },
      ""finish_parameter"": { ""type"": ""string"" }
    } },
    ""view_id"": { ""type"": ""integer"" },
    ""as_of"": { ""type"": ""string"", ""description"": ""yyyy-MM-dd"" },
    ""include_as_of_day"": { ""type"": ""boolean"", ""default"": true },
    ""max_rows"": { ""type"": ""integer"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_audit_access",
                Command = "horizun_audit_access",
                Description =
                    "Measure doors, ramps, stairs and straight-line distance to exits against a rule profile " +
                    "THE CALLER SUPPLIES. READ-ONLY. Horizun carries no accessibility or egress thresholds of " +
                    "its own and never will: they depend on a jurisdiction, an edition and a building use that " +
                    "nothing in a model can know, and a number compiled in here would be read as authority and " +
                    "be wrong somewhere. THE SPLIT IS STRUCTURAL, not a disclaimer. A MEASUREMENT is what the " +
                    "model says; a FINDING is that measurement on the wrong side of a threshold the supplied " +
                    "profile declares, echoed back with the profile's own name and source. The strongest word " +
                    "any row uses is 'within_profile': nothing here says compliant, approved or passes. Every " +
                    "row also states how good its own number is - a door width is NOMINAL, a ramp slope is the " +
                    "TYPE'S DECLARED MAXIMUM because Revit exposes no as-built slope, and a distance to exit " +
                    "is a STRAIGHT LINE through walls, never a travel distance. Which doors are exits is the " +
                    "caller's to state; with no exit_rule the egress half is reported NOT COVERED rather than " +
                    "guessed, because a guessed exit set produces distances that are confidently wrong.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [
    ""profile""
  ],
  ""properties"": {
    ""target_document"": {
      ""type"": ""string"",
      ""description"": ""OPTIONAL guard. When given, the read is REFUSED unless the ACTIVE document matches it. Two Revit hosts run side by side on machines like this one, and a clean report about a model nobody looked at is worse than no report. The reply names the document it actually read either way.""
    },
    ""profile"": {
      ""type"": ""object"",
      ""required"": [
        ""name"",
        ""source"",
        ""thresholds""
      ],
      ""description"": ""The rule profile. It needs a name AND a source: a finding that cannot say whose rule produced it is a finding nobody can check."",
      ""properties"": {
        ""name"": {
          ""type"": ""string""
        },
        ""source"": {
          ""type"": ""string""
        },
        ""jurisdiction"": {
          ""type"": ""string""
        },
        ""thresholds"": {
          ""type"": ""object"",
          ""description"": ""Keyed by threshold name. Applied: minimum_clear_width_mm, minimum_corridor_width_mm, maximum_ramp_slope_percent, maximum_stair_riser_mm, minimum_stair_tread_mm, minimum_stair_width_mm, maximum_straight_line_to_exit_mm, minimum_door_approach_mm. Any other key is echoed back as UNAPPLIED rather than dropped."",
          ""additionalProperties"": {
            ""type"": ""object"",
            ""required"": [
              ""value"",
              ""comparison""
            ],
            ""properties"": {
              ""value"": {
                ""type"": ""number""
              },
              ""comparison"": {
                ""type"": ""string"",
                ""enum"": [
                  ""min"",
                  ""max""
                ],
                ""description"": ""Which side of the number is the problem. Required; without it nothing knows.""
              },
              ""units"": {
                ""type"": ""string""
              },
              ""note"": {
                ""type"": ""string""
              }
            }
          }
        }
      }
    },
    ""checks"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""string"",
        ""enum"": [
          ""doors"",
          ""ramps"",
          ""stairs"",
          ""egress""
        ]
      },
      ""description"": ""Omit for all four.""
    },
    ""exit_rule"": {
      ""type"": ""object"",
      ""description"": ""Which doors are exits. One of element_ids, mark_prefix, or parameter (+ optional value). Required for the egress check."",
      ""properties"": {
        ""element_ids"": {
          ""type"": ""array"",
          ""items"": {
            ""type"": ""integer""
          }
        },
        ""mark_prefix"": {
          ""type"": ""string""
        },
        ""parameter"": {
          ""type"": ""string""
        },
        ""value"": {
          ""type"": ""string""
        }
      }
    },
    ""route_view_id"": {
      ""type"": ""integer"",
      ""description"": ""OPTIONAL, and the difference between a screening number and a measurement. Name a FLOOR PLAN view and the egress check computes a REAL travel distance per room with Revit's own path-of-travel service - around walls, around furniture, around everything that plan shows - beside the straight line it already reported. Both travel: the straight line is fast and always shorter than the truth, and dropping it would lose the one reliable thing about it. THE VIEW IS YOUR CHOICE BECAUSE THE OBSTACLES ARE: a plan with furniture hidden measures a building with no furniture in it, and the number looks identical. The calculation is two-dimensional and per level, destinations' Z is replaced by the view's level elevation, and a room with NO route reports that as a finding rather than as a large number. Every routed row is evaluated against maximum_travel_distance_to_exit_mm - its own threshold, never the straight-line one - and the reply carries coverage: how many rooms were measured and why each of the others was not.""
    }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_validate_ids",
                Command = "horizun_validate_ids",
                Description =
                    "Evaluate a buildingSMART IDS, implemented against the published schema (ids.xsd 1.0.0) rather than against one vendor's export. TWO OPERATIONS THAT ANSWER DIFFERENT QUESTIONS and are never summed: operation=precheck reads the LIVE REVIT MODEL and says on every property finding that PROPERTY SET MEMBERSHIP WAS NOT ESTABLISHED - which Pset a parameter lands in is decided by the export mapping at export time, so a pre-check that claimed otherwise would hand you a passing report about a file that does not exist yet. operation=validate reads an EXPORTED IFC and answers what IDS is written against. WHAT IS EVALUATED: all six facets (entity, attribute, classification, property, material, partOf); the four restriction kinds (enumeration; pattern with XML Schema's ANCHORED whole-value semantics, not a substring search; bounds; length); required / optional / prohibited per facet - including prohibited, where PRESENCE is the failure; and the applicability's own minOccurs/maxOccurs, where (0,0) means no element may match AND the requirements are deliberately not evaluated. Property sets are read from the occurrence AND from the type, because an occurrence inherits its type's sets and that is where most exporters put them. partOf traverses the five relations RECURSIVELY, so a column in a storey in a building is part of the building. WHAT IT IS NOT: a certificate. Constructions this build does not evaluate are NAMED per specification and counted as not_decidable - never as a pass. READ-ONLY: corrections are PROPOSED as ready requests against tools that rehearse and re-read their own work, and nothing here writes to a model.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [
    ""path""
  ],
  ""additionalProperties"": false,
  ""properties"": {
    ""operation"": {
      ""type"": ""string"",
      ""enum"": [
        ""precheck"",
        ""validate""
      ],
      ""default"": ""precheck"",
      ""description"": ""TWO DIFFERENT QUESTIONS, and their results are never added together. 'precheck' reads the LIVE REVIT MODEL: fast, available before anybody exports, and structurally unable to answer part of what IDS asks - a Revit parameter named FireRating is not evidence that the export will put it in Pset_WallCommon, because the export mapping decides that at export time, and every property finding says so. 'validate' reads an EXPORTED IFC and answers what IDS is written against: there the property set is an IfcPropertySet attached by an IfcRelDefinesByProperties, and it is either there or it is not. Use the pre-check to catch the expensive things early; use the validation to settle them.""
    },
    ""path"": {
      ""type"": ""string"",
      ""description"": ""Full path of the .ids file. Read against buildingSMART's published schema (namespace http://standards.buildingsmart.org/IDS). A file in another namespace is REFUSED rather than guessed at: the namespace is the version handshake, and reading one grammar with another's rules produces confident findings nobody agreed to.""
    },
    ""ifc_path"": {
      ""type"": ""string"",
      ""description"": ""REQUIRED for operation='validate': the exported .ifc to evaluate. There is no fallback to the Revit model - that would answer a different question under this operation's name. The specification's ifcVersion is checked against the file's FILE_SCHEMA and a mismatch is reported, not evaluated.""
    },
    ""target_document"": {
      ""type"": ""string"",
      ""description"": ""OPTIONAL guard for operation='precheck'. When given, the read is REFUSED unless the ACTIVE document matches it. Two Revit hosts run side by side on machines like this one, and a clean report about a model nobody looked at is worse than no report.""
    },
    ""max_findings"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 5000,
      ""default"": 100,
      ""description"": ""How many per-element findings each specification returns. The TOTAL is always reported beside the shown count, so a truncated list can never be read as the whole of it.""
    },
    ""cooperative"": {
      ""type"": ""object"",
      ""additionalProperties"": false,
      ""description"": ""OPT-IN cooperative reading for operation='precheck'. Omit it and nothing changes. A specification applicable to 80,000 elements holds Revit's UI thread for as long as it runs; with this the read asks between elements whether to carry on, and a result that stopped early says PARTIAL."",
      ""properties"": {
        ""ui_budget_ms"": {
          ""type"": ""integer"",
          ""minimum"": 1000,
          ""maximum"": 600000,
          ""default"": 20000
        },
        ""max_units"": {
          ""type"": ""integer"",
          ""minimum"": 1
        },
        ""cursor"": {
          ""type"": ""string""
        }
      }
    }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_materials",
                Command = "horizun_manage_materials",
                Description =
                    "Create, duplicate and edit materials in a verified batch. set_appearance edits an exclusive asset or replaces it with Generic, in its own batch.",
                InputSchema = JObject.Parse(@"{
  ""description"": ""Full details. Create, duplicate and edit MATERIALS in one verified batch: graphics (colour, surface and cut pattern and their colours, transparency, shininess, smoothness), identity (class, category) and the appearance asset. Names are checked unique against the document and against the batch before anything runs, and every value is re-read from the material after the commit. ASSIGNING AN APPEARANCE ASSET DUPLICATES IT BY DEFAULT: several materials commonly share one asset, so pointing this material at yours without copying would mean a later edit changing every material that shares it. Pass share_appearance_asset to share deliberately, and the dry run lists how many materials already use that asset. The set_appearance edits a supported schema colour and its texture on an exclusive appearance asset; replace_with_generic creates a new Generic asset. Transparency requires generic_transparency. set_appearance must be the only action in its batch. share_appearance_asset: Point at the asset instead of a copy of it. Off by default: sharing means a later edit of that asset changes every material using it. structural: Physical properties. When the material has NO structural asset, one is CREATED - but only if this object carries a 'class' (Concrete, Metal, Wood, Plastic, Generic, Gas, Liquid), because the class decides which fields exist at all and choosing it for you would produce a material whose properties nobody picked; without it the call is refused with that sentence. Optional 'name' names the new property set. Fields, written and RE-READ per field: density, minimum_yield_stress, minimum_tensile_strength, concrete_compression, young_modulus, poisson_ratio, shear_modulus, thermal_expansion_coefficient. Values are in REVIT'S INTERNAL UNITS and are not converted - density is mass per cubic foot, moduli are force per square foot - because a conversion invented here produces a number nobody can trace back to the model. A name outside that list is reported as unknown_field and NOT written. The three vector properties take one number and are written to all three axes (isotropic); writing one axis would leave a material isotropic in name and orthotropic in its numbers. duplicate_shared_assets: A structural or thermal property set is an ELEMENT, and several materials commonly point at the same one - Autodesk's own templates ship concrete assets shared by a dozen. Editing it changes every one of them. With this false (the default) an edit that would reach another material is REFUSED and the other materials are named; with it true, this material gets its own copy first and the others are untouched. Nothing here silently edits a material you did not name. thermal: Thermal properties. When the material has NO thermal asset, one is CREATED if this object carries a 'material_type' (Solid, Liquid, Gas); without it the call is refused rather than guessing. Fields, written and re-read per field: thermal_conductivity, specific_heat, density, emissivity, permeability, porosity, reflectivity. Same unit rule, same unknown_field rule. material_id: Identifies the material for duplicate, update or set_appearance; material_name is an alternative."",
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" },
    ""idempotency_key"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": {
      ""type"": ""object"", ""required"": [""key"", ""operation""], ""properties"": {
        ""key"": { ""type"": ""string"", ""minLength"": 1 },
        ""operation"": { ""type"": ""string"", ""enum"": [""create"", ""duplicate"", ""update"", ""set_appearance""] },
        ""name"": { ""type"": ""string"", ""description"": ""Required by create and duplicate; optional rename for update."" },
        ""material_id"": { ""type"": ""integer"", ""description"": ""Material ID; material_name is an alternative."" },
        ""material_name"": { ""type"": ""string"", ""description"": ""The same choice by name, when the id is not to hand."" },
        ""color"": { ""type"": ""string"", ""description"": ""#RRGGBB, the shaded colour."" },
        ""surface_pattern"": { ""type"": ""string"", ""description"": ""Fill pattern name, or 'solid'."" },
        ""surface_pattern_color"": { ""type"": ""string"" },
        ""cut_pattern"": { ""type"": ""string"" }, ""cut_pattern_color"": { ""type"": ""string"" },
        ""generic_diffuse"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 255 } },
        ""match_shading_color"": { ""type"": ""boolean"" },
        ""replace_with_generic"": { ""type"": ""boolean"" },
        ""remove_diffuse_texture"": { ""type"": ""boolean"" },
        ""transparency"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 100, ""description"": ""For set_appearance 0..1; other operations use integer 0..100 for shading."" },
        ""shininess"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 128 },
        ""smoothness"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 100 },
        ""material_class"": { ""type"": ""string"" }, ""material_category"": { ""type"": ""string"" },
        ""appearance_asset_id"": { ""type"": ""integer"" },
        ""share_appearance_asset"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Share the appearance asset instead of copying it."" },
        ""structural"": { ""type"": ""object"", ""description"": ""Physical properties; see full contract for fields and units."" },
        ""thermal"": { ""type"": ""object"", ""description"": ""Thermal properties; see full contract for fields and units."" },
        ""duplicate_shared_assets"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Copy shared structural or thermal assets before editing."" }
      }, ""additionalProperties"": false } }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_styles",
                Command = "horizun_manage_styles",
                Description =
                    "Object styles, subcategories, line styles, line and fill patterns, and colour schemes of rooms, " +
                    "areas and spaces (list, create as a copy, edit, apply to a view, legend). list_* read; the rest " +
                    "rehearse, then re-read after commit. Deleting: horizun_delete_verified.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""list_object_styles"", ""set_object_style"", ""create_subcategory"", ""list_line_styles"", ""create_line_style"", ""list_line_patterns"", ""create_line_pattern"", ""list_fill_patterns"", ""create_fill_pattern"", ""list_color_schemes"", ""create_color_scheme"", ""edit_color_scheme"", ""apply_color_scheme"", ""place_color_legend""] },
    ""target_document"": { ""type"": ""string"" },
    ""category"": { ""type"": ""string"", ""description"": ""OST_ name, id or name. Parent for create_subcategory."" },
    ""subcategory"": { ""type"": ""string"" },
    ""name"": { ""type"": ""string"" },
    ""projection_weight"": { ""type"": ""integer"" }, ""cut_weight"": { ""type"": ""integer"" },
    ""color"": { ""type"": ""string"", ""description"": ""#RRGGBB"" },
    ""line_pattern"": { ""type"": ""string"", ""description"": ""Name, or Solid."" },
    ""material"": { ""type"": ""string"" },
    ""segments"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""[{type: dash|space|dot, length}]"" },
    ""target"": { ""type"": ""string"", ""enum"": [""drafting"", ""model""] },
    ""fill"": { ""type"": ""string"", ""enum"": [""solid"", ""hatch"", ""crosshatch""] },
    ""angle"": { ""type"": ""number"", ""description"": ""Degrees."" }, ""spacing"": { ""type"": ""number"" }, ""spacing2"": { ""type"": ""number"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""scheme_id"": { ""type"": ""integer"", ""description"": ""Colour scheme to use."" },
    ""parameter"": { ""type"": ""string"", ""description"": ""Scheme parameter, name or id."" },
    ""by_range"": { ""type"": ""boolean"" }, ""include_links"": { ""type"": ""boolean"" }, ""title"": { ""type"": ""string"" },
    ""entries"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""required"": [""value"", ""color""], ""additionalProperties"": false, ""properties"": {
      ""value"": {}, ""color"": { ""type"": ""string"" }, ""caption"": { ""type"": ""string"" }, ""visible"": { ""type"": ""boolean"" }, ""pattern"": { ""type"": [""string"", ""integer""] }
    } }, ""description"": ""Scheme entries."" },
    ""value_units"": { ""type"": ""string"", ""enum"": [""internal"", ""mm"", ""m"", ""feet"", ""m2"", ""ft2""], ""description"": ""Required for a numeric (length/area) scheme."" },
    ""view_id"": { ""type"": ""integer"" }, ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_units",
                Command = "horizun_manage_units",
                Description =
                    "Project units and project data. read: FormatOptions per spec (unit, accuracy, symbol, zero " +
                    "suppression) and decimal/grouping symbols; set writes them. " +
                    "project_information: without values reads every parameter; with values writes text/integer ones. " +
                    "base_points: reads both points and the angle to true north; project_position RE-SPECIFIES SHARED " +
                    "COORDINATES (links and coordinate exports follow) and also needs confirm_shared_coordinates=true. " +
                    "Writes rehearse and re-read after commit.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""read"", ""set"", ""project_information"", ""base_points""] },
    ""target_document"": { ""type"": ""string"" },
    ""specs"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } }, ""all"": { ""type"": ""boolean"" },
    ""spec"": { ""type"": ""string"", ""description"": ""length, area, slope... or a full spec id."" },
    ""unit"": { ""type"": ""string"" }, ""accuracy"": { ""type"": ""number"" }, ""symbol"": { ""type"": ""string"" },
    ""suppress_trailing_zeros"": { ""type"": ""boolean"" }, ""suppress_leading_zeros"": { ""type"": ""boolean"" },
    ""suppress_spaces"": { ""type"": ""boolean"" }, ""use_digit_grouping"": { ""type"": ""boolean"" },
    ""decimal_symbol"": { ""type"": ""string"" }, ""digit_grouping_symbol"": { ""type"": ""string"" },
    ""values"": { ""type"": ""object"", ""description"": ""name, number, client, address, status, issue_date, author... or any parameter name."" },
    ""project_position"": { ""type"": ""object"", ""description"": ""east_west, north_south, elevation (units), angle_to_true_north (deg)."" },
    ""confirm_shared_coordinates"": { ""type"": ""boolean"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_structural_connections",
                Command = "horizun_structural_connections",
                Description =
                    "Create structural connections between named steel members. Every member is checked BEFORE " +
                    "the transaction - it must be in a category a connection can join (framing, columns, " +
                    "foundations, stiffeners, trusses, bracing) and must report geometry - because Revit " +
                    "accepts a connection over members that cannot carry one and produces an element that " +
                    "connects nothing. Omit connection_type for the generic connection Revit places when no " +
                    "family is chosen; a named type must be loaded, and the refusal lists the types that ARE. " +
                    "The dry run creates each connection provisionally, regenerates, asks it which elements it " +
                    "actually connects, and rolls back. What a DETAILED connection then generates - plates, " +
                    "bolts, welds - belongs to the Steel Connections add-in: this command does not make it and " +
                    "does not report it as though it had.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": {
      ""type"": ""object"", ""required"": [""key"", ""member_ids""], ""properties"": {
        ""key"": { ""type"": ""string"", ""minLength"": 1 },
        ""member_ids"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 50, ""items"": { ""type"": ""integer"" }, ""description"": ""The members the connection joins. Two is the minimum: one member is not a connection."" },
        ""connection_type"": { ""type"": ""string"", ""description"": ""Name of a loaded StructuralConnectionHandlerType. Omit for the generic connection."" },
        ""connection_type_id"": { ""type"": ""integer"", ""description"": ""The same choice by id, which is unambiguous when two types share a name."" }
      }, ""additionalProperties"": false } }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_plan_structure",
                Command = "horizun_plan_structure",
                Description = @"Read-only plan: columns at straight grid crossings or beams in free parts of bays. Same-level beam boxes in the datum band (-1000/+10 mm) exclude grid overlaps greater than 150 mm with 0.01 mm tolerance. See omitted_existing_beams for beam identities and crossing_grid when a crossing triggers a trim. Where model boxes or reference levels are unavailable, the planner checks framing placement using nominal sections or measured footprints and withholds coverage_unmeasured_spans without proven longitudinal clearance. Returns next_arguments or ordered next_arguments_batches for create_elements. Rehearse, confirm and verify each batch separately.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation"", ""level_id"", ""type_id""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""columns_on_grid_intersections"", ""beams_along_grids""] },
    ""level_id"": { ""type"": ""integer"", ""description"": ""The Level placements land on."" },
    ""type_id"": { ""type"": ""integer"", ""description"": ""A FamilySymbol in OST_StructuralColumns (columns) or OST_StructuralFraming (beams)."" },
    ""grid_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""Restrict to these grids; omitted = every straight grid in the document."" },
    ""min_span_mm"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 20000, ""description"": ""beams_along_grids: spans shorter than an explicit minimum are reported as omitted. When absent, the 150.01 mm minimum applies only to free segments next to columns."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_undo",
                Command = "horizun_undo",
                Description = @"Undo the last Horizun batch (Revit has no API Undo). transform_elements, write_params_verified, create_elements and resolve_clash record an inverse; undo_last applies it verified, refusing if those elements changed since or the document was saved/synced. list shows batches.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""list"", ""undo_last""], ""default"": ""list"" },
    ""target_document"": { ""type"": ""string"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_classification_table",
                Command = "horizun_manage_classification_table",
                Description = "Set an absolute or project-relative keynote/Assembly Code table file, or reload its current resource. Re-read path, load state and entry count after commit. A keynote file is diagnosed first (unreadable, duplicated keys, missing parents, cycles) and refused with errors; operation=diagnose returns that report and whether the file is the loaded one, writing nothing.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document""], ""additionalProperties"": false,
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""table"": { ""type"": ""string"", ""enum"": [""keynote"", ""assembly_code""], ""default"": ""keynote"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""set_path"", ""reload"", ""diagnose""], ""default"": ""set_path"" },
    ""path"": { ""type"": ""string"", ""description"": ""Existing local table file; absolute or relative to the saved RVT. Required for set_path, omitted for reload, optional for diagnose (default: current file)."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_set_keynote",
                Command = "horizun_set_keynote",
                Description = @"Set the Keynote code on elements, reporting exactly what it touched. In Revit the Keynote parameter normally lives on the TYPE, so writing it re-codes every instance of that type: this tool resolves the target first, tells you the blast radius (including elements you did not name), writes each type once, and VERIFIES AFTER THE COMMIT: every target is re-resolved from the committed document and its value read fresh, because a value read inside an open transaction can still disappear with it. elements_now_carrying_this_keynote is counted by asking the model again afterwards, never by summing what the plan expected. The counts are kept apart because they answer different questions: requested_ids (every id sent, INCLUDING entries that were not integers), parsed_ids, targets_resolved, writes_accepted_in_transaction (not evidence), writes_verified_after_commit (evidence) and writes_failed. Use scope='instance' to refuse any write that would spill onto siblings, or dry_run=true to see the impact first.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""anyOf"": [ { ""required"": [""keynote"", ""element_ids""] }, { ""required"": [""keynote"", ""unique_ids""] }, { ""required"": [""assignments""] } ],
  ""properties"": {
    ""assignments"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 5000,
      ""description"": ""Batch: a different code per element/type id in one rehearsal, token and transaction. Replaces element_ids+keynote. Two codes resolving to one type refuse the call."",
      ""items"": { ""type"": ""object"", ""required"": [""element_id"", ""keynote""], ""properties"": {
        ""element_id"": { ""type"": ""integer"" }, ""keynote"": { ""type"": ""string"" },
        ""unique_id"": { ""type"": ""string"" }, ""expected_category"": { ""type"": ""string"", ""description"": ""Not enough alone: send unique_id."" }, ""expected_type"": { ""type"": ""string"", ""description"": ""Not enough alone: send unique_id."" } } } },
    ""validate_against_table"": { ""type"": ""boolean"", ""description"": ""Refuse codes absent from the keynote table loaded in the project (default true with assignments, false otherwise)."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1,
                       ""description"": ""Elements to code (or send unique_ids). If the Keynote lives on their type, the type is what gets written."" },
    ""unique_ids"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Aligned with the ids; mismatch refuses the call."" },
    ""expected_category"": { ""type"": ""string"", ""description"": ""Category (name or OST_*) when read. Not enough alone: send unique_ids."" },
    ""expected_type"": { ""type"": ""string"", ""description"": ""Type (name or 'Family: Type') when read. Not enough alone: send unique_ids."" },
    ""keynote"": { ""type"": ""string"", ""description"": ""The keynote code for element_ids. Empty string clears it."" },
    ""scope"": { ""type"": ""string"", ""enum"": [""auto"", ""instance"", ""type""], ""default"": ""auto"",
                 ""description"": ""auto: write wherever the parameter lives (type if that is the only place). instance: only write an instance-level Keynote; fail rather than spill onto siblings. type: always write the type."" },
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the document to write to. It must be the document that is ACTIVE in Revit; this command will not switch documents for you. A write aimed at whatever window happens to be in front is a write aimed at whatever turns up."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token returned by the dry run of this exact request. Single-use, expires, and bound to this document and this request - if either changed, execution is refused and nothing is written."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Resolve targets and report the blast radius without writing."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_bind_shared_param",
                Command = "horizun_bind_shared_param",
                Description = @"Bind a shared parameter from an SPF (by GUID, or by name) to a set of categories as an Instance or Type binding in a parameter group, then RE-READ the binding from ParameterBindings after the commit and report what is actually there. merge_existing_categories (default true) UNIONs the already-bound categories with the new ones: ReInsert with only the new ones drops the rest AND their values, and a response that echoed the request could not show it â€” so the category list returned here is always the one read back from the model, and categories_dropped is measured, not assumed. allow_vary_between_groups (default true) calls SetAllowVaryBetweenGroups on the real InternalDefinition, found through an element's parameter after Regenerate, because the iterator's Key is the ExternalDefinition and has no such flag; without the flag Revit throws the DESAGRUPAR modal on the first differing write inside a Model Group and hangs the bridge. If NO element carries the parameter the flag cannot be set and that is REPORTED, not swallowed. The binding kind, the category list and VariesAcrossGroups are three separate measurements: if any of them could not be taken, the outcome is 'unknown' and never 'confirmed'. A ReInsert that dropped previously-bound categories under merge_existing_categories=true reports outcome 'categories_dropped', never 'confirmed' â€” with merge you asked for the UNION, so a binding missing part of it is not what you asked for and the values in the dropped categories are gone. The document's SharedParametersFilename is restored afterwards.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""spf_path"", ""categories""],
  ""properties"": {
    ""confirmation_token"": { ""type"": ""string"", ""description"": ""REQUIRED when dry_run=false. The token the dry run of this exact request returned. Single-use, expiring, bound to this document and this request - if either changed, execution is refused and nothing is written."" },
    ""target_document"": { ""type"": ""string"", ""description"": ""REQUIRED. Title or full path of the document to change. It must be the document ACTIVE in Revit; this never switches documents for you. Aliases accepted for compatibility: expected_document, target_document_title."" },
    ""spf_path"": { ""type"": ""string"", ""description"": ""Absolute path to the shared parameter file. It is loaded via app.SharedParametersFilename + OpenSharedParameterFile(), and the previous filename is restored afterwards â€” silently repointing the user's SPF is its own bug."" },
    ""param_guid"": { ""type"": ""string"", ""description"": ""GUID of the shared parameter in the SPF. Preferred: a GUID identifies a shared parameter, a name does not."" },
    ""param_name"": { ""type"": ""string"", ""description"": ""Name of the shared parameter in the SPF. Used only if param_guid is absent. A name matching more than one definition is an error, not a guess."" },
    ""categories"": {
      ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"" },
      ""description"": ""BuiltInCategory tokens (OST_PipeFitting) or category display names. A category that does not resolve is an error: nothing is bound.""
    },
    ""binding_kind"": { ""type"": ""string"", ""enum"": [""Instance"", ""Type""], ""default"": ""Instance"",
                        ""description"": ""Instance | Type. What is reported is read off the resulting Binding object's own type, not from this field."" },
    ""group"": { ""type"": ""string"", ""default"": ""PG_IDENTITY_DATA"",
                 ""description"": ""Parameter group: a PG_ name (PG_IDENTITY_DATA, PG_DATA, PG_TEXT...) or a group schema id (autodesk.parameter.group:identityData)."" },
    ""merge_existing_categories"": { ""type"": ""boolean"", ""default"": true,
                                     ""description"": ""true: UNION the categories already bound with the requested ones. false: bind ONLY the requested ones â€” ReInsert then DROPS every other bound category and LOSES the values stored in them. Leave it true unless you mean exactly that."" },
    ""allow_vary_between_groups"": { ""type"": ""boolean"", ""default"": true,
                                     ""description"": ""Call SetAllowVaryBetweenGroups(true) on the InternalDefinition. Without it, writing different values to instances inside Model Groups raises the DESAGRUPAR modal, which hangs the bridge. Reported as read back from the InternalDefinition, never as assumed. Instance bindings only: a Type binding has one value per type and cannot vary between groups, so for binding_kind=Type the flag is reported as not_applicable, never set and never counted against the outcome."" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: bind shared parameter"" },
    ""target_document_title"": { ""type"": ""string"",
                                 ""description"": ""If given, the bind aborts unless the active document's title matches. Binding into whichever model happened to be in front is how a batch lands in the wrong file."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_split_floor_loops",
                Command = "horizun_split_floor_loops",
                Description = @"Split each multi-loop floor into ONE FLOOR PER LOOP. A slab sketched with several closed loops is a single element, so every schedule, area takeoff and keynote downstream treats those separate slabs as one row, and nothing downstream can fix it. Scope it with element_ids (exactly those), view_id (everything eligible visible there), or neither (the whole model, rarely what you meant). A floor with one loop is REPORTED AS SKIPPED with the reason, never silently ignored, and an id that resolves to nothing or to something that is not a floor comes back in scope.missing_ids / scope.wrong_type_ids. Every loop becomes a floor INCLUDING inner loops, which in a slab with openings are the holes - the plan reports the loop count per floor so you can look first. The original is deleted only once at least one replacement exists, so a loop Revit refused does not take the geometry with it. Unlike the button this was ported from, the height offset from the level is carried onto each new floor and reported. VERIFIED AFTER THE COMMIT: created_present counts new elements re-read from the model and confirmed to be floors, deleted_gone counts originals confirmed absent - never the calls that did not throw. dry_run defaults to TRUE and opens no transaction.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the model to change. It must be the document ACTIVE in Revit; this never switches documents for you. A write aimed at whatever window is in front is a write aimed at whatever turns up."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
      ""description"": ""Exactly these floors. An id that does not exist is reported in scope.missing_ids and one that is not a floor in scope.wrong_type_ids; neither is dropped in silence. Omit to use view_id."" },
    ""view_id"": { ""type"": ""integer"",
      ""description"": ""Every eligible floor VISIBLE IN THIS VIEW. Used only when element_ids is omitted. Omit both and the whole model is in scope."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it returns the plan (which floors, how many loops each) and a single-use confirmation_token."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token this exact request's dry run returned. Single-use, expiring, bound to this document and this scope - if either changed, nothing is written."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_split_by_level",
                Command = "horizun_split_by_level",
                Description = "Split one basic wall, architectural/structural column or vertical pipe, duct, conduit or cable tray at every crossed level. Recreates doors and windows on the corresponding wall piece, reconnects MEP pieces and external connectors, and re-reads all resulting pieces. split_at=openings splits one straight wall at the jambs of its doors/windows into side and opening pieces (the original keeps its id; other inserts' new ids are mapped). An unsupported dependency or failed verification rolls the transaction back. Dry run is the default.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""additionalProperties"": false,
  ""required"": [""target_document"", ""element_id""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"", ""description"": ""The active document to modify."" },
    ""element_id"": { ""type"": ""integer"", ""description"": ""Exactly one wall, column or vertical MEP run."" },
    ""split_at"": { ""type"": ""string"", ""enum"": [""levels"", ""openings""], ""default"": ""levels"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" },
    ""idempotency_key"": { ""type"": ""string"" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_split_multilayer_walls",
                Command = "horizun_split_multilayer_walls",
                Description = @"Split compound walls into ONE SINGLE-LAYER WALL PER MATERIAL LAYER, each at the physical position its layer occupied inside the assembly. THE ORIGINAL WALL IS NOT DELETED: it is converted into the single-layer wall of the CORE, so it keeps its ElementId and UniqueId, and every door, window, hosted family, opening, sweep, reveal and embedded curtain wall stays hosted in it - with ITS own ElementId and UniqueId, its parameters, its sill and head heights, its phase, its workset and its nested subcomponents. Only the other layers are created. N layers WITH VOLUME produce exactly N walls; a zero-width membrane keeps its layer number, is reported as not materialised, and does not renumber the layers behind it. THE CORE IS NOT 'the first Structure layer': the core range comes from the compound structure's own core boundaries, and the carrier is the structural layer inside it, or the thickest one, or the thickest core layer when none is structural - ties broken by lowest original index, and the reason reported. A wall with no valid core is REFUSED, never silently hosted on layer 0. Offsets are computed from the layer widths AND the wall's actual location line - all six of WallCenterline, CoreCenterline, FinishFaceExterior, FinishFaceInterior, CoreExterior and CoreInterior - so a wall drawn on its exterior face is not displaced by half its thickness. The exterior direction is MEASURED off the wall's exterior shell face rather than deduced. STRAIGHT AND CIRCULAR-ARC walls are supported; an arc keeps its centre, angles and sense and only its radius changes. Splines, ellipses and degenerate curves are refused by name, never straightened. Refused with an explicit code and nothing written: stacked walls, curtain walls, slanted or tapered walls, walls with an edited profile, attached walls, walls in a group or a design option, walls owned by another user, and any dependency whose equivalence cannot be guaranteed. EACH WALL IS ITS OWN ATOM: it runs in its own SubTransaction and is committed only after the model is re-read - each layer's position measured against its planned offset within 0.5 mm, each resulting type re-read and confirmed single-layer and correctly named, every insert checked by ElementId, UniqueId, host, symbol, placement, flips, level, phase, subcomponents and parameters, and every secondary layer ray-cast at each insert to prove the opening passes through. Anything that fails rolls that wall back WHOLE, leaving it exactly as it was, while the rest of the batch keeps its verified conversions. Type names follow [ORIGINAL TYPE] - [MATERIAL] - [NN] with NN the original layer position counted from the exterior, and a name already taken by a different composition gets a deterministic variant rather than being overwritten; no existing type is ever modified. Provenance is stamped in Extensible Storage, so a second identical call answers already_split instead of duplicating. The confirmation token binds EACH WALL individually - type, compound structure, location line, curve, flip, constraints and dependencies - so a model that moved refuses as stale. Only Revit's walls-overlap warning is suppressed, matched by FailureDefinitionId and not by localised text; any other warning comes back and takes all_verified down with it. originals_deleted is 0 by design. dry_run defaults to TRUE.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the model to change. It must be the document ACTIVE in Revit; this never switches documents for you."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
      ""description"": ""Exactly these walls. An id that resolves to nothing comes back in scope.missing_ids and one that resolves to a non-wall in scope.wrong_type_ids; neither is dropped. OMIT to use view_id. An EMPTY array is REFUSED rather than widened: it is what a caller sends when its own filter matched nothing, and reading that as the whole model would convert a document on the strength of an empty selection."" },
    ""view_id"": { ""type"": ""integer"",
      ""description"": ""Every wall VISIBLE IN THIS VIEW. Used only when element_ids is omitted. Omit both and the whole model is in scope."" },
    ""origin_group_param"": { ""type"": ""string"",
      ""description"": ""OPTIONAL, and never assumed: the name of a text INSTANCE parameter whose value is carried from the original wall onto every layer wall created from it. Omit it and nothing is copied under that name."" },
    ""core_carrier_policy"": { ""type"": ""string"", ""enum"": [""structural_in_core_then_thickest""], ""default"": ""structural_in_core_then_thickest"",
      ""description"": ""How the layer that keeps the original element is chosen: the structural layer INSIDE THE CORE, else the thickest structural one, else the thickest core layer - ties broken by lowest original index. It is an argument rather than an assumption so that a future policy is a contract change instead of a silent behaviour change."" },
    ""parameter_copy_policy"": { ""type"": ""string"", ""enum"": [""safe_compatible""], ""default"": ""safe_compatible"",
      ""description"": ""Which parameters are copied onto the newly created layer walls, by stable identifier (BuiltInParameter, then shared GUID) and never by translated name. Read-only, computed and type-driven parameters are reported as skipped rather than silently omitted."" },
    ""allow_arc_walls"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""Whether circular-arc walls are eligible. Set false to restrict this run to straight walls; arcs then come back refused with unsupported_curve instead of being converted."" },
    ""failure_policy"": { ""type"": ""string"", ""enum"": [""rollback_wall""], ""default"": ""rollback_wall"",
      ""description"": ""What happens when a wall cannot be converted with every dependency intact: it is rolled back whole and the rest of the batch continues. There is deliberately NO mode that accepts the loss of a hosted object."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it returns, per wall, the layer plan with each layer's expected offset and type name, the core range, the chosen carrier and why, the full dependency ledger, the refusals with their codes, and a single-use confirmation_token."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token this exact request's dry run returned. Single-use, expiring, and bound to each wall individually - a wall that moved, was re-typed or gained a door since the dry run refuses as stale_plan and nothing is written."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_split_multilayer_slabs",
                Command = "horizun_split_multilayer_slabs",
                Description = @"Split compound FLOORS AND CEILINGS into one element per material layer. Each layer keeps the ORIGINAL PROFILE - cloned from the slab's sketch, or read off its face when Revit will not hand over a sketch - and is then moved in Z, so curved edges survive intact (unlike the wall splitter, nothing here is rebuilt from endpoints). Hosted families are re-placed on the layer that can take them, trying the outer face, then the structural layer, then the far face. A slab whose hosted families CANNOT be put back rolls back ALONE, in its own SubTransaction, and is reported by id with the reason - layer slabs without the families they hosted is silent data loss, so it is refused per slab rather than accepted, and the rest of the batch still applies. Originals are unpinned before deletion. A single-layer slab, and one whose profile Revit will not surrender, are both reported as skipped with the reason, never silently ignored. Revit's overlap warning is suppressed because layer slabs share a footprint BY CONSTRUCTION; every other warning reaches you. VERIFIED AFTER THE COMMIT: created_present re-reads each new id and confirms it is a floor or ceiling, deleted_gone confirms each original is absent. dry_run defaults to TRUE.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the model to change. It must be the document ACTIVE in Revit; this never switches documents for you."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
      ""description"": ""Exactly these floors or ceilings. An id that resolves to neither comes back in scope.wrong_type_ids. Omit to use view_id."" },
    ""view_id"": { ""type"": ""integer"",
      ""description"": ""Every eligible floor and ceiling VISIBLE IN THIS VIEW. Used only when element_ids is omitted. Omit both and the whole model is in scope."" },
    ""origin_group_param"": { ""type"": ""string"",
      ""description"": ""OPTIONAL, and never assumed: the name of a text INSTANCE parameter carried from each original slab onto every layer it produces (the button this was ported from hard-coded one organisation's '_GrupoOrigen'). Omit it and nothing is copied - reported as null, meaning NOT TRACKED, never as 0."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it returns which slabs are eligible, their layer counts, what was skipped and why, plus a single-use confirmation_token."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token this exact request's dry run returned. Single-use, expiring, bound to this document and this scope."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_ungroup_and_mark",
                Command = "horizun_ungroup_and_mark",
                Description = @"Ungroup model groups AND record where every element came from, so the operation is reversible. Ungrouping destroys the only record of which elements belonged together; before the members scatter, each is stamped with the group's name in the text parameter you name, and horizun_regroup_by_param reads that stamp to rebuild the group later. THE STAMP IS CHECKED BEFORE ANYTHING IS UNGROUPED: the button this was ported from ungrouped first and discovered per element that the parameter did not exist, leaving the model ungrouped AND unmarked - unrecoverable, because the membership was already gone. A group where NOT ONE member can carry the parameter is now refused outright with its reason; a group where SOME can is listed with a per-reason 'blockers' count, and those members are ungrouped without a stamp and will not come back. Bind the parameter first with horizun_bind_shared_param if that count is not zero. Optionally draws the group's origin marker (a circle plus rotated X/Y axes) with marker_view_id - the original drew into whatever view was active, which is not a decision a tool called by an agent should make, so it is drawn only into the view you name and skipped entirely when you name none. VERIFIED AFTER THE COMMIT: groups_gone confirms each group is really absent, elements_carrying_the_stamp re-reads the parameter on each element and counts the ones that really hold a value. dry_run defaults to TRUE.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document"", ""origin_group_param""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the model to change. It must be the document ACTIVE in Revit; this never switches documents for you."" },
    ""origin_group_param"": { ""type"": ""string"",
      ""description"": ""REQUIRED, and never assumed: the TEXT INSTANCE parameter that will hold each element's origin group name. The button this was ported from hard-coded one organisation's '_GrupoOrigen'; Horizun compiles no such convention in. It must be bound to the members' categories - use horizun_bind_shared_param first if it is not."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
      ""description"": ""Exactly these model groups. An id that is not a model group comes back in scope.wrong_type_ids. Omit to use view_id."" },
    ""view_id"": { ""type"": ""integer"",
      ""description"": ""Every model group VISIBLE IN THIS VIEW. Used only when element_ids is omitted. Omit both and every model group in the model is in scope."" },
    ""marker_view_id"": { ""type"": ""integer"",
      ""description"": ""OPTIONAL. Draw each group's origin marker as detail lines in THIS view. Omit and no marker is drawn at all. The view must accept detail curves - a 3D view does not, and the failure is reported per group rather than taking the run down."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it returns each group, how many members can carry the stamp, which cannot and why, plus a single-use confirmation_token."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token this exact request's dry run returned. Single-use, expiring, bound to this document and this scope."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_regroup_by_param",
                Command = "horizun_regroup_by_param",
                Description = @"Rebuild model groups from the stamp horizun_ungroup_and_mark left behind: collect every LOOSE element in the model carrying a value in the named text parameter, group the ones sharing a value, name the group, and clear the stamp so the pair is idempotent. TWO DEFECTS OF THE ORIGINAL BUTTON ARE FIXED HERE. First, it handed EVERY element carrying the parameter to Revit, including annotation - a model group cannot contain a view-specific element, and Revit refuses the WHOLE call with one ArgumentException naming nothing, so a single stray tag made the button fail entirely. View-specific elements, elements with no category, and elements already inside a group are now excluded up front and listed per candidate in 'excluded', so the rest still groups. Second, it cleared the parameter AFTER creating the group; writing a parameter on an element that is already a group member is precisely what raises Revit's group modal, and an unanswered modal holds Revit's UI thread until the caller times out. The stamp is now cleared BEFORE the group is created - same end state, no modal, and a failed grouping rolls the clearing back with it. Group names get a numeric suffix rather than colliding, and both the parameter and the name prefix are arguments, never compiled in. VERIFIED AFTER THE COMMIT: groups_present re-reads each new group, members_confirmed counts the members the model says each one holds, and elements_still_stamped reports any element whose stamp survived. dry_run defaults to TRUE.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document"", ""origin_group_param""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the model to change. It must be the document ACTIVE in Revit; this never switches documents for you."" },
    ""origin_group_param"": { ""type"": ""string"",
      ""description"": ""REQUIRED, and never assumed: the TEXT INSTANCE parameter holding each element's origin group name - the same one passed to horizun_ungroup_and_mark."" },
    ""origin_value"": { ""type"": ""string"",
      ""description"": ""OPTIONAL. Regroup ONLY the elements whose stamp equals this value. Omit and every distinct value found becomes its own group - run a dry run first to see which values exist and how many elements each holds."" },
    ""group_name_prefix"": { ""type"": ""string"", ""default"": """",
      ""description"": ""Prepended to the stamp value to form the group name (the original button compiled in 'MOD_'). Defaults to empty: the group is named after the value alone."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it returns every stamp value found, how many elements each would group, which were excluded and why, the exact group name that would be used, plus a single-use confirmation_token."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token this exact request's dry run returned. Single-use, expiring, bound to this document and this scope."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_copy_slab_elevations",
                Command = "horizun_copy_slab_elevations",
                Description = @"Copy the SHAPE of one warped floor onto other floors. Reads the source's triangulated top face and, for each destination, creates slab shape points at three places: the destination's own boundary vertices, wherever the source's split lines cross that boundary, and every source vertex falling inside it - all sampled off the source surface, so the destination lands ON it rather than near it. DESTRUCTIVE, AND SAID SO BEFORE IT HAPPENS: a destination that already carries shape edits has them WIPED (ResetSlabShape) before the new points go on, because two warps cannot be merged. The dry run lists exactly which floors will lose an existing shape, in destinations_whose_shape_will_be_reset - the button this came from did it without a word. THREE DEFECTS OF THAT BUTTON ARE FIXED. It refused any source with four or fewer vertices as 'not warped', which rejects a rectangular slab with one corner raised - the commonest warped slab there is; the source is now judged by whether its shape actually varies (more vertices than its boundary, OR differing vertex elevations, OR non-boundary split lines). It reduced every edge curve to its START POINT, so a curved slab's boundary polygon cut straight across the bulge and points were tested against a shape that is not the slab; curved edges are now tessellated and the affected destinations are named in curved_boundary_note. And one bad destination took the whole batch down; each now runs in its own SubTransaction and rolls back alone. VERIFIED AFTER THE COMMIT: floors_now_warped re-reads each destination's SlabShapeEditor and counts the ones whose vertices really vary in elevation or that really carry split lines - never the DrawPoint calls that did not throw. dry_run defaults to TRUE.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document"", ""source_floor_id""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the model to change. It must be the document ACTIVE in Revit; this never switches documents for you."" },
    ""source_floor_id"": { ""type"": ""integer"",
      ""description"": ""REQUIRED. The floor whose shape is copied. It must actually be warped - the run is refused with its vertex count if the slab is flat, unedited and has no split lines. It is never itself a destination, even if you also name it in element_ids."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
      ""description"": ""The destination floors - exactly these. An id that is not a floor comes back in scope.wrong_type_ids. Omit to use view_id."" },
    ""view_id"": { ""type"": ""integer"",
      ""description"": ""Every floor VISIBLE IN THIS VIEW becomes a destination. Used only when element_ids is omitted. Omit both and every floor in the model is a destination, which given this tool RESETS existing shapes is rarely what you meant."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it returns how many points each destination would get, which ones would LOSE an existing shape, which were skipped and why, plus a single-use confirmation_token."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token this exact request's dry run returned. Single-use, expiring, bound to this document and this scope."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_embed_floors_in_toposolid",
                Command = "horizun_embed_floors_in_toposolid",
                Description = @"Embed floors INTO a toposolid: the slab's top face ends flush with the terrain and its body goes into the ground. Around each slab it writes three rings of shape points - the boundary and an outer ring at the top-face elevation, and an inner ring one centimetre in at the slab's UNDERSIDE, which is what pulls the terrain down around it - plus split lines along the outer and inner rings. Slabs that TOUCH and sit at the SAME elevation are merged into ONE outline by 2D edge cancellation, so no split line is drawn along a false seam; slabs that touch with a REAL STEP between them are deliberately NOT merged, because that step is a design feature and smoothing it away would be wrong. No solid booleans are used: Revit's kernel fails on slabs meeting edge to edge, which is the case this exists to handle. Arcs are tessellated so rings follow curves, corners are mitred so rectangular slabs stay sharp, and a SLOPED top face is sampled per point off its plane equation so ramps work. Existing toposolid points within 60cm of each outline are DELETED first - without that the triangulation bands. THE TOPOSOLID MUST BE UNAMBIGUOUS: pass toposolid_id, or omit it only when the document holds exactly one (the choice is then reported in toposolid_resolved_by); several and no id is REFUSED with the candidates listed, because reshaping the wrong terrain is not a thing to resolve by guessing. It never iterates SlabShapeCreases - that read crashes Revit on large toposolids - and neither does the verification. VERIFIED AFTER THE COMMIT by RECOMPUTING every ring position independently and asking the model whether a vertex is really there: points_present against points_expected, both deduplicated with the same tolerance. dry_run defaults to TRUE.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the model to change. It must be the document ACTIVE in Revit; this never switches documents for you."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
      ""description"": ""The floors to embed - exactly these. They are grouped automatically: touching AND level means one merged outline, a real step means separate outlines. Omit to use view_id."" },
    ""view_id"": { ""type"": ""integer"",
      ""description"": ""Every floor VISIBLE IN THIS VIEW is embedded. Used only when element_ids is omitted. Omit both and every floor in the model is in scope."" },
    ""toposolid_id"": { ""type"": ""integer"",
      ""description"": ""The Toposolid to reshape. May be omitted ONLY when the document holds exactly one, which is then used and reported. With several present and no id, the run is REFUSED and the candidates are listed."" },
    ""offset_cm"": { ""type"": ""number"", ""default"": 5,
      ""description"": ""How far OUTSIDE the slab edge the outer ring sits, in centimetres, at the top-face elevation. Must be greater than zero."" },
    ""spacing_cm"": { ""type"": ""number"", ""default"": 100,
      ""description"": ""Maximum distance between points along the outline, in centimetres. Corners are always kept exactly; this only subdivides the long edges. Smaller means more points and a closer-following terrain. Must be greater than zero."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it returns how the slabs grouped, each outline's vertex count, how many points and split lines would be added, plus a single-use confirmation_token."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token this exact request's dry run returned. Single-use, expiring, bound to this document and this scope."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_grade_toposolid_around_floors",
                Command = "horizun_grade_toposolid_around_floors",
                Description = @"Grade a toposolid around one or more path slabs, Civil 3D style: a CONSTANT SIDE SLOPE run outward from the path until it meets the existing terrain. Writes the whole thing into the toposolid as shape points and split lines - points along the slab edge at the slab's own top elevation, an inner ring just inside at the slab underside, an outer offset ring with breaklines along it, the DAYLIGHT line where the side slope finally meets existing ground, and intermediate slope points with split lines between the two so the terrain between path and daylight is actually modelled rather than interpolated. Existing toposolid points inside the graded footprint are DELETED first. THE STATIONS THAT NEVER DAYLIGHT ARE REPORTED, NOT FAKED: the search walks outward until the slope's elevation crosses the sampled terrain and gives up at max_search_cm; where it never crosses there is no daylight point and no slope path, and daylight_missing counts exactly those - it is the number worth reading before you accept the result. Per-point failures Revit refused (a point it would not create, a split line it would not take) come back in recipe_reported rather than being swallowed, and points_skipped / split_lines_skipped count them. THE TOPOSOLID MUST BE UNAMBIGUOUS: pass toposolid_id, or omit it only when the document holds exactly one (the choice is reported); several and no id is REFUSED with the candidates listed. VERIFIED AFTER THE COMMIT by RECOMPUTING every position this grading should have produced and asking the model whether a vertex is really there - points_present against points_expected, both deduplicated at the same tolerance. It reads SlabShapeVertices only, never SlabShapeCreases, which crashes Revit on large toposolids. dry_run defaults to TRUE.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the model to change. It must be the document ACTIVE in Revit; this never switches documents for you."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
      ""description"": ""The path slabs to grade around - exactly these. A slab whose geometry cannot be read is reported in floors_failed rather than taking the run down. Omit to use view_id."" },
    ""view_id"": { ""type"": ""integer"",
      ""description"": ""Every floor VISIBLE IN THIS VIEW is graded around. Used only when element_ids is omitted. Omit both and every floor in the model is in scope."" },
    ""toposolid_id"": { ""type"": ""integer"",
      ""description"": ""The Toposolid to grade. May be omitted ONLY when the document holds exactly one, which is then used and reported. With several present and no id, the run is REFUSED and the candidates are listed."" },
    ""offset_cm"": { ""type"": ""number"", ""default"": 5,
      ""description"": ""How far OUTSIDE the slab edge the offset ring sits, in centimetres, at the top-face elevation. The side slope starts here. Must be greater than zero."" },
    ""edge_spacing_cm"": { ""type"": ""number"", ""default"": 100,
      ""description"": ""Maximum distance between sampled points along the slab edge, in centimetres. Must be greater than zero."" },
    ""slope"": { ""type"": ""string"", ""default"": ""2:1"",
      ""description"": ""The side slope, in any of the forms the original button took: 'H:V' like '2:1' (two horizontal to one vertical), a percentage like '50%', or a bare horizontal-to-vertical ratio like '2'. Must be greater than zero."" },
    ""max_search_cm"": { ""type"": ""number"", ""default"": 1000,
      ""description"": ""How far out, in centimetres, to look for daylight before giving up on a station. Stations that never meet the terrain within this distance are counted in daylight_missing and get NO slope - raise this, or flatten the slope, if that count is not what you want."" },
    ""slope_spacing_cm"": { ""type"": ""number"", ""default"": 100,
      ""description"": ""Maximum distance between intermediate points along the side slope, in centimetres. Smaller means the slope face is modelled more finely. Must be greater than zero."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it returns per slab how many edge, inner and offset points would be created, how many stations daylight and how many do NOT, plus a single-use confirmation_token."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token this exact request's dry run returned. Single-use, expiring, bound to this document and this scope."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_rectangularize_walls",
                Command = "horizun_rectangularize_walls",
                Description = @"Rebuild walls whose elevation profile has been edited into irregular steps as simple RECTANGULAR fragments, read from the wall's real solid geometry. The profile is partitioned into a grid around the openings, each cell becomes its own straight wall, and the doors and windows the original carried are re-hosted onto the fragments that contain them. IT REFUSES RATHER THAN APPROXIMATES, and this is the point of the tool: it works only on straight Basic Walls, and a curved wall, a non-rectangular opening, or any profile it cannot rebuild stably is reported BY NAME with its reason in 'refused' - nothing is guessed at. A wall that is already rectangular is listed in 'already_rectangular' and left alone; that is a correct outcome, not a failure, and it is kept separate from the refusals so the two are never confused. Each wall is rebuilt inside its OWN SubTransaction, so one that defeats the rebuild rolls back alone, is reported in 'errors', and the rest of the batch still applies. Revit's overlap, join and identical-instance warnings are suppressed because rebuilding a wall as fragments raises them by construction; every other warning reaches you. Fragments below a minimum dimension or area are dropped and counted in fragments_skipped_tiny rather than created as slivers. VERIFIED AFTER THE COMMIT: fragments_present re-reads every new id and confirms it is a Wall - a SubTransaction that committed still dies with the outer one, so counting what was built inside it is not evidence. dry_run defaults to TRUE.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the model to change. It must be the document ACTIVE in Revit; this never switches documents for you."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
      ""description"": ""Exactly these walls. An id that is not a wall comes back in scope.wrong_type_ids. Omit to use view_id."" },
    ""view_id"": { ""type"": ""integer"",
      ""description"": ""Every eligible wall VISIBLE IN THIS VIEW. Used only when element_ids is omitted. Omit both and the whole model is in scope."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it runs the full analysis and returns which walls would be replaced and by how many fragments, which are already rectangular, and which are refused and why - plus a single-use confirmation_token."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token this exact request's dry run returned. Single-use, expiring, bound to this document and this scope."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_job_status",
                Command = null,           // host-resident: reads the durable job records, never forwarded to Revit
                Description =
                    "How a long run is going - answered WITHOUT touching Revit. While a long command executes, " +
                    "Revit's UI thread is inside it and the pipe is waiting for it to end, so asking the plugin " +
                    "for progress is asking the thing that is busy. The running script writes checkpoints to a " +
                    "file and this reads that file, so the answer comes back even mid-transaction, and survives a " +
                    "crash: the record is append-only and flushed line by line. Scripts call " +
                    "checkpoint(\"label\", done, total) - no import needed. A job with no finish record is " +
                    "reported as exactly that, never guessed to be 'stalled': a log cannot tell a slow step from " +
                    "a hang. A DEAD process is knowable, though: the record carries the pid of the Revit that " +
                    "claimed the job, and process_alive says whether that process still exists - checked against " +
                    "the OS, touching nothing. process_alive false means the job will never finish (or, if it " +
                    "was still queued, will never run) and that is reported as a fact, not left for the caller " +
                    "to discover with a process monitor.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""job_id"": { ""type"": ""string"", ""description"": ""A specific job (the id horizun_execute_python returns). Omitted: the most recent ones."" },
    ""wait_s"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 60, ""description"": ""Wait for a job state change. Requires job_id; status only."" },
    ""limit"": { ""type"": ""integer"", ""default"": 5, ""description"": ""How many recent jobs to describe."" },
    ""checkpoints"": { ""type"": ""integer"", ""default"": 10, ""description"": ""How many of the LAST checkpoints of each job to include."" },
    ""output_page"": { ""type"": ""integer"", ""minimum"": 1, ""description"": ""With job_id: page N of a Python output over 20000 chars."" },
    ""cancel"": { ""type"": ""boolean"", ""default"": false, ""description"": ""With job_id: ask Revit to stop that job. Queued: removed, never runs. A running Python script is asked to stop at its next checkpoint, safe_point or should_stop; its open transactions are rolled back and the job finishes as cancelled_running. A request, not a stop - read the job's own state afterwards."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_catalog_lookup",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                // The bSDD operations (2026-09-24) live here rather than in a tool of their own:
                // both look classification up, and tools/list has a 512 KiB budget. Detail in
                // docs/INFORMATION-MANAGEMENT.md (bSDD lookup) and BsddLookup.cs.
                Description =
                    "operation=bsdd_search|bsdd_search_dictionary|bsdd_class|bsdd_property|bsdd_dictionaries: " +
                    "read-only buildingSMART Data Dictionary lookup (cached, capped; bsdd_class adds loin_property " +
                    "suggestions). operation=leaf (default): resolve whether a hierarchical code is a LEAF of a catalog you pass at call time. Generic: " +
                    "the catalog is a file (catalog_path), no codes are baked in. A code is a LEAF iff it EXISTS in " +
                    "the catalog AND no OTHER code is its strict descendant (no other code begins with code + the " +
                    "hierarchy separator). HONESTY: a code that is NOT in the catalog returns is_leaf=null (unknown) " +
                    "— never false; 'exists' is a separate field, so 'absent' and 'not a leaf' are never conflated. " +
                    "operation=search: find codes whose description best matches 'query' (accent- and case-insensitive " +
                    "whole-token overlap), returning code/description/is_leaf/score ranked highest first. operation=match: " +
                    "pair each of 'queries' with a code and EXPLAIN it: candidates with confidence %, matched and missing " +
                    "features, number conflicts, and decision accept/review with its reason (below threshold, ambiguous, " +
                    "no candidate); feed accepted pairs to horizun_set_keynote assignments. UTF-16 keynote files are read. Both leaf and " +
                    "search share the same COLUMN parsing: delimiter is auto-detected among tab/;/,/| by counting each " +
                    "one consistently across the first sampled lines (a genuine tie between two delimiters REFUSES and " +
                    "asks for 'delimiter' explicitly, rather than guessing); pass 'delimiter' to pin it. code_column " +
                    "(0-based index, default 0) and description_column (search only, default 1) select columns; either " +
                    "may be a header-name string when has_header=true. A quoted cell (\"...\") may contain the delimiter. " +
                    "If 'separator' is given, a descendant is other=code+separator+…; if it is omitted the code is " +
                    "opaque and any of - . _ / or space counts as the hierarchy separator (unrelated to the column " +
                    "delimiter). PROVENANCE: the response carries a sha256 of the catalog bytes, delimiter_used/" +
                    "delimiter_mode, columns (when has_header), row_count and distinct_code_count, so the verdict is " +
                    "auditable. Read-only; touches no Revit model.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""category"": { ""type"": ""string"", ""description"": ""products: BuiltInCategory filter; catalog_path names the user kit JSON."" },
    ""operation"": { ""type"": ""string"", ""enum"": [""leaf"", ""search"", ""match"", ""products"", ""bsdd_search"", ""bsdd_search_dictionary"", ""bsdd_class"", ""bsdd_property"", ""bsdd_dictionaries""] },
    ""text"": { ""type"": ""string"" },
    ""uri"": { ""type"": ""string"", ""description"": ""bSDD class, property or dictionary URI."" },
    ""dictionary_uris"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""related_ifc_entity"": { ""type"": ""string"" },
    ""language_code"": { ""type"": ""string"" },
    ""offset"": { ""type"": ""integer"" },
    ""limit"": { ""type"": ""integer"" },
    ""refresh"": { ""type"": ""boolean"" },
    ""max_age_hours"": { ""type"": ""integer"" },
    ""catalog_path"": { ""type"": ""string"",
      ""description"": ""Absolute path to the catalog file (leaf and search). A missing or unreadable file is an ERROR, not an empty catalog — the tool never answers off a file it could not read."" },
    ""code"": { ""type"": ""string"",
      ""description"": ""leaf: the code to test. If it is not present in the catalog the answer is exists=false, is_leaf=null (unknown) — never is_leaf=false."" },
    ""query"": { ""type"": ""string"",
      ""description"": ""search: free text to match against each row's description, normalized (lowercase, accents stripped) and compared by whole token."" },
    ""separator"": { ""type"": ""string"",
      ""description"": ""The HIERARCHY segment separator inside a code, e.g. '-' or '.' (not the column delimiter). A code X is a parent of Y when Y begins with X+separator. If omitted, any of - . _ / or space is accepted."" },
    ""delimiter"": { ""type"": ""string"",
      ""description"": ""The COLUMN delimiter that splits each catalog line into cells (e.g. tab, ';', ',', '|'). Omitted: auto-detected deterministically from the first sampled lines; a genuine tie between two delimiters is an error asking for this explicitly."" },
    ""has_header"": { ""type"": ""boolean"", ""default"": false,
      ""description"": ""True when the catalog's first non-blank line is a header row (its cells become the 'columns' in the response and let code_column/description_column be header names)."" },
    ""code_column"": { ""type"": [""integer"", ""string""],
      ""description"": ""Which column holds the code: a 0-based index, or a header-name string when has_header=true. Default: column 0."" },
    ""description_column"": { ""type"": [""integer"", ""string""],
      ""description"": ""search only: which column holds the description: a 0-based index, or a header-name string when has_header=true. Default: column 1."" },
    ""max_results"": { ""type"": ""integer"", ""default"": 10,
      ""description"": ""search: ranked matches (cap 200); match: candidates per query (default 3, cap 20)."" },
    ""queries"": { ""type"": ""array"", ""maxItems"": 500, ""items"": { ""type"": ""object"", ""required"": [""text""],
      ""properties"": { ""id"": { ""type"": [""string"", ""integer""] }, ""text"": { ""type"": ""string"" } } },
      ""description"": ""match: items to pair with a code, e.g. {id: type id, text: family + type + material}. Or send query for one."" },
    ""threshold"": { ""type"": ""number"", ""default"": 80, ""description"": ""match: minimum confidence (0-100) to accept a candidate."" },
    ""leaf_only"": { ""type"": ""boolean"", ""default"": true, ""description"": ""match: only leaf codes are candidates."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_template_kit",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "AI-ready templates: read a Horizun template kit (.hzkit.json, schema horizun.template-kit/1) that the MCP " +
                    "generated FROM a live template, and brief yourself before modelling. operation=list: installed kits. load (default; by path, kit_name or model_path): template, " +
                    "conventions, catalog summary, playbook tasks, rule topics, quality checks, limitations and whether the kit " +
                    "matches this server's contract. find: types by intent (text, accent-insensitive), category, code prefix, host " +
                    "(none|wall|ceiling|floor|roof|face) and dims ranges (name: [min,max] mm); each hit carries code, unit, " +
                    "nominal and envelope dims in mm (search nominal when present), measured footprint, placement (host, route) and the exact " +
                    "horizun_create_elements recipe plus the horizun_query_model call that resolves its type_id. type: one entry " +
                    "by key. rules: selection rules (topic filter). playbook: ordered steps of a task with this server's tool names " +
                    "and verifications (task filter). checks: quality gates. validate: kit_valid, recipes_valid against tool schemas, contract_match and template_hash_match. load reports unavailable_tools with their groups. audit (Revit, read-only): model checks and optional external drawing declarations (declared_evidence_path); declared evidence never becomes a measured pass. evidence_schema: the external declaration JSON Schema, without Revit or a kit. Read-only.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""product_only"": { ""type"": ""boolean"", ""default"": false, ""description"": ""find: only product families supplied in the kit; load recipe retains rehearsal and verification."" },
    ""policy"": { ""type"": ""string"", ""enum"": [""existing_first"", ""existing_only"", ""allow_create""], ""default"": ""existing_first"", ""description"": ""find: existing_only returns no_match without resize advice when no existing size matches within 0.5 mm per dimension."" },
    ""include_unusable"": { ""type"": ""boolean"", ""default"": false, ""description"": ""find: include entries marked usable=false, retaining their reason."" },
    ""operation"": { ""type"": ""string"", ""enum"": [""list"", ""load"", ""find"", ""type"", ""rules"", ""playbook"", ""checks"", ""validate"", ""audit"", ""evidence_schema""], ""default"": ""load"" },
    ""declared_evidence_path"": { ""type"": ""string"", ""description"": ""audit: absolute JSON manifest, schema horizun.template-check-evidence/1 (operation=evidence_schema). Validates source hash and per-check drawing/model references; executed_with_declared_evidence retains passed=null, declared_passed and host_verified=false."" },
    ""target_document"": { ""type"": ""string"", ""description"": ""audit: the ACTIVE model to judge against the kit."" },
    ""top"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200, ""default"": 20, ""description"": ""audit: sample size per finding."" },
    ""path"": { ""type"": ""string"", ""description"": ""Absolute path of the .hzkit.json."" },
    ""kit_name"": { ""type"": ""string"", ""description"": ""An installed kit (<data root>/kits/<kit_name>.hzkit.json); a model made from a kit template names it in Project Information HZ_AI_Kit."" },
    ""model_path"": { ""type"": ""string"", ""description"": ""Instead of path: a model/template file or folder; the kit is <stem>.hzkit.json beside it, or the only *.hzkit.json there."" },
    ""text"": { ""type"": ""string"" },
    ""category"": { ""type"": ""string"" },
    ""code"": { ""type"": ""string"", ""description"": ""Classification code prefix."" },
    ""host"": { ""type"": ""string"" },
    ""dims"": { ""type"": ""object"", ""additionalProperties"": true, ""description"": ""Parameter name to [min,max] (mm; null = open) or an exact value."" },
    ""near"": { ""type"": ""object"", ""additionalProperties"": true, ""description"": ""find: the dimensions you NEED (name: mm). Ranks by closeness and, when no type is exact, returns size_missing with the new_size recipe instead of letting you use a wrong size."" },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 50, ""default"": 10 },
    ""key"": { ""type"": ""string"", ""description"": ""type: family|type key from find."" },
    ""topic"": { ""type"": ""string"" },
    ""task"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_project_context",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "ISO 19650 project context and intake, answered WITHOUT Revit. operation=schema returns the JSON " +
                    "Schema of project-context.json (schema_version 1; also resource horizun://schemas/project-context/v1). " +
                    "validate reads a file and keeps three verdicts apart: invalid (breaks the schema, errors by JSON " +
                    "pointer), inconsistent (e.g. a deliverable name that does not follow naming.fields, a status code " +
                    "naming.status_codes does not declare) and incomplete (ISO 19650 questions unanswered, listed in order). questions " +
                    "returns the ordered intake questions still open, each with its target pointer, type, options and " +
                    "why it matters, in Spanish and English. draft applies {pointer: value} answers onto the existing " +
                    "file or an empty context and validates it; dry_run defaults to true, dry_run=false writes and then " +
                    "re-reads the file, never replaces one without overwrite=true, and never writes an invalid context " +
                    "or a credential. elicit asks them via MCP elicitation forms (else code elicitation_unsupported: " +
                    "ask in chat). Nothing is inferred to fill a gap. ids_from_loin: loin (ISO 7817-1) to a validated IDS 1.0. " +
                    "integrations / register_model / resolve_model: the model registry project-integrations.json beside it; a " +
                    "model_ref binds strong evidence (get_document_info model_identity), never Project Information alone; " +
                    "Revit 2023 readings are identity_proven:false.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""schema"", ""validate"", ""questions"", ""draft"", ""elicit"", ""ids_from_loin"", ""integrations"", ""register_model"", ""resolve_model""],
      ""description"": ""schema: the JSON Schema. validate: check a file (path required). questions: the ordered intake questions still open (path optional; without it, all of them). draft: build a context from answers (path optional; required with dry_run=false). elicit: ask them via the client's forms, apply like draft."" },
    ""path"": { ""type"": ""string"",
      ""description"": ""Absolute path of the project-context.json. draft builds ON TOP of an existing file, so a second intake round fills gaps instead of erasing the first."" },
    ""answers"": { ""type"": ""object"", ""additionalProperties"": true,
      ""description"": ""draft (elicit: earlier answers): {\""<JSON pointer>\"": value}, e.g. {\""/project/code\"": \""P01\"", \""/cde/states/wip\"": \""01_WIP\""}. Pointers come from operation=questions. A pointer that cannot be placed refuses the whole call. intake.missing is derived from what is still unanswered unless you set it."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""draft/elicit: true (default) returns the drafted document and its validation without writing. false writes it (full_write or unsafe_code profile) and re-reads it before reporting it written."" },
    ""overwrite"": { ""type"": ""boolean"", ""default"": false,
      ""description"": ""draft/elicit with dry_run=false: required to replace an existing file. Without it an existing file is never touched."" },
    ""language"": { ""type"": ""string"", ""enum"": [""es"", ""en""], ""default"": ""en"" },
    ""timeout_seconds"": { ""type"": ""integer"", ""minimum"": 10, ""maximum"": 540, ""default"": 300 },
    ""include_answered"": { ""type"": ""boolean"", ""default"": false,
      ""description"": ""questions: also list the answered and not-applicable questions, with their current values."" },
    ""output_path"": { ""type"": ""string"" },
    ""requirement_ids"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""milestone"": { ""type"": ""string"" },
    ""info"": { ""type"": ""object"" },
    ""integrations_path"": { ""type"": ""string"", ""description"": ""Absolute project-integrations.json. Default: beside path."" },
    ""project_code"": { ""type"": ""string"", ""description"": ""register_model: the code a NEW registry is created with."" },
    ""model"": { ""type"": ""object"", ""description"": ""register_model: {name, discipline?, evidence, model_ref?}. dry_run defaults to true."" },
    ""evidence"": { ""type"": ""object"", ""description"": ""resolve_model: get_document_info's model_identity."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_exchange",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "Cross-app exchange/0. emit hashes payload and writes manifest last (dry_run true; at most " +
                    "self_reported_verified). verify checks delivery, hashes, state and model identity. " +
                    "correction_plan_check/reverify inspect steps and relayed replies; apply through " +
                    "horizun_apply_corrections and re-review PDF.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""emit"", ""verify"", ""correction_plan_check"", ""correction_plan_reverify""] },
    ""kind"": { ""type"": ""string"", ""enum"": [""coordination.ledger"", ""coordination.status"", ""quantities.rows"", ""schedule.element_4d"", ""model.element_lineage"", ""receipt""], ""description"": ""emit: the hop's kind. verify: the kind this hop expects."" },
    ""files"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""required"": [""path"", ""format""], ""properties"": { ""path"": { ""type"": ""string"" }, ""format"": { ""type"": ""string"" }, ""rows"": { ""type"": ""integer"", ""minimum"": 0 }, ""name"": { ""type"": ""string"" } } }, ""description"": ""emit: absolute payload paths and the wrapped format, e.g. horizun.coordination-ledger.csv/2."" },
    ""verification_state"": { ""type"": ""string"", ""enum"": [""verified"", ""self_reported_verified"", ""partial"", ""failed""], ""description"": ""emit: what the producing reply reported."" },
    ""verification_method"": { ""type"": ""string"" },
    ""producer"": { ""type"": ""object"", ""description"": ""emit: {app, version, commit} of the payload's producer. Default: this bridge."" },
    ""model_ref"": { ""type"": ""string"", ""description"": ""emit: the source model's registry id; its evidence is copied from the registry."" },
    ""project_code"": { ""type"": ""string"" },
    ""exchange_root"": { ""type"": ""string"", ""description"": ""Absolute. Default: the registry's, else the local default."" },
    ""integrations_path"": { ""type"": ""string"", ""description"": ""Absolute project-integrations.json (or path: the project-context.json beside it)."" },
    ""path"": { ""type"": ""string"" },
    ""spatial_ref"": { ""type"": ""object"" },
    ""caveats"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""What the payload does NOT prove."" },
    ""correlation_id"": { ""type"": ""string"" },
    ""manifest_path"": { ""type"": ""string"", ""description"": ""verify: exchange.hzx.json or its folder."" },
    ""plan"": { ""type"": ""object"", ""description"": ""The plan_correcciones_revit object (pasos, manual)."" },
    ""applied"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""required"": [""step_id""], ""properties"": { ""step_id"": { ""type"": ""string"" }, ""arguments"": { ""type"": ""object"" }, ""reply"": { ""type"": ""object"" } } }, ""description"": ""reverify: per applied step, the arguments sent with dry_run=false and the typed tool's reply."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_information_container",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "ISO 19650 information containers, CDE states, transmittals and approvals over LOCAL or SYNCED " +
                    "folders - never a cloud API. name: compose and validate a container name (ISO 19650-2 defaults " +
                    "unless rules are passed, reported as defaults). stamp: write '<file>.container.json' (name, status, " +
                    "revision, bytes, SHA-256), read back. verify: does the file still match its sidecar. inspect: " +
                    "paginated audit of the wip/shared/published/archived folders and the MIDP deliverables. transition: " +
                    "COPY a sealed container to the next state, stamp, verify, log; shared->published needs approved_by. " +
                    "transmittal: issue <project>-TR-0001 (json + md + csv) for sealed containers in one state, each " +
                    "SHA-256 re-measured against its sidecar; the number is safe under concurrency. record_review: " +
                    "append accepted / accepted_with_comments / rejected to reviews.jsonl; it changes no state. " +
                    "register: approval history per container (transitions, transmittals, reviews) with incoherences. " +
                    "Writes rehearse by default (dry_run=true) and need full_write; nothing is moved, deleted or " +
                    "overwritten. Every concrete code, rule and folder is an argument. inspect names non-compliant names, " +
                    "missing and orphan sidecars, hash mismatches, one revision with two contents and published " +
                    "revisions below shared ones; register names transmittals whose file changed after issue, reviews " +
                    "of unknown containers or transmittals and publications logged without approved_by.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""name"", ""stamp"", ""verify"", ""inspect"", ""transition"", ""transmittal"", ""record_review"", ""register""] },
    ""information_container"": { ""type"": ""object"", ""description"": ""name/stamp: { fields: {field: code}, field_order (optional only for the 7 ISO fields), separator, field_patterns: {field: regex} (merged over ISO), status, revision, title, status_codes: {code: text} (ordered, replaces ISO), revision_patterns: {kind: regex}, file_name: name | name_status_revision }. Unknown keys are refused."" },
    ""naming"": { ""type"": ""object"", ""description"": ""The rules only (information_container without fields/status/revision/title). Default: the project context's naming, else ISO 19650-2."" },
    ""file_path"": { ""type"": ""string"", ""description"": ""stamp/verify: the container file. transition: the sealed source inside from_state."" },
    ""root"": { ""type"": ""string"", ""description"": ""Absolute CDE root: relative states resolve against it; its .horizun/ holds the transition log, transmittals and reviews."" },
    ""states"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""string"" }, ""description"": ""{ wip, shared, published, archived }: folders, absolute or relative to root. Never created."" },
    ""project_context_path"": { ""type"": ""string"", ""description"": ""Absolute project-context.json (schema_version 1): cde, naming, deliverables, project.code. Explicit arguments win."" },
    ""deliverables"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""required"": [""container""] }, ""description"": ""inspect: MIDP rows {container, title, task_team, due, required_status, format, milestone}."" },
    ""as_of"": { ""type"": ""string"", ""description"": ""inspect: YYYY-MM-DD that judges overdue. Default today (UTC)."" },
    ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""default"": 200 },
    ""max_files"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 200000, ""default"": 20000 },
    ""from_state"": { ""type"": ""string"", ""enum"": [""wip"", ""shared"", ""published""] },
    ""to_state"": { ""type"": ""string"", ""enum"": [""shared"", ""published"", ""archived""] },
    ""status"": { ""type"": ""string"", ""description"": ""transition: status in to_state (default the source's)."" },
    ""revision"": { ""type"": ""string"", ""description"": ""transition: destination revision. record_review: the revision reviewed."" },
    ""approved_by"": { ""type"": ""string"", ""description"": ""Required for shared->published, and for a published transmittal unless the sidecars carry it."" },
    ""note"": { ""type"": ""string"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""source_document"": { ""type"": ""string"" },
    ""revit_year"": { ""type"": ""string"" },
    ""project"": { ""type"": ""string"", ""description"": ""transmittal: number prefix (default project.code)."" },
    ""state"": { ""type"": ""string"", ""enum"": [""wip"", ""shared"", ""published"", ""archived""] },
    ""file_paths"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""sender"": { ""type"": ""object"", ""description"": ""{name, organization, role}"" },
    ""recipients"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } },
    ""purpose"": { ""type"": ""string"", ""description"": ""A status code, e.g. S3."" },
    ""transmittal_id"": { ""type"": ""string"" },
    ""container"": { ""type"": ""string"" },
    ""outcome"": { ""type"": ""string"", ""enum"": [""accepted"", ""accepted_with_comments"", ""rejected""] },
    ""comments"": { ""type"": ""string"" },
    ""reviewed_by"": { ""type"": ""string"" },
    ""reviewer_organization"": { ""type"": ""string"" },
    ""reviewed_on"": { ""type"": ""string"" },
    ""since"": { ""type"": ""string"" },
    ""until"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_cde_cloud",
                Command = null,           // host-resident: fixed provider endpoints, never forwarded to Revit
                // Deliberately terse: tools/list has a byte budget. The detail is in
                // docs/INFORMATION-MANAGEMENT.md, "Cloud CDE reader".
                Description =
                    "Cloud CDE, acc (APS) or opencde. Reads: list_projects, list_states (ISO 19650 folders), inspect (files, naming, MIDP), versions, " +
                    "issues_list. ACC issue_create/issue_update: dry_run->confirmation_token, 3-legged data:write, keyed, read back. Unread: coverage_complete=false.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation"", ""provider""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""list_projects"", ""list_states"", ""inspect"", ""versions"", ""issues_list"", ""issue_create"", ""issue_update""] },
    ""provider"": { ""type"": ""string"", ""enum"": [""acc"", ""opencde""] },
    ""project_context_path"": { ""type"": ""string"" },
    ""hub_id"": { ""type"": ""string"" },
    ""project_id"": { ""type"": ""string"" },
    ""states"": { ""type"": ""object"" },
    ""naming"": { ""type"": ""object"" },
    ""deliverables"": { ""type"": ""array"" },
    ""as_of"": { ""type"": ""string"" },
    ""offset"": { ""type"": ""integer"" },
    ""limit"": { ""type"": ""integer"" },
    ""max_calls"": { ""type"": ""integer"" },
    ""item_id"": { ""type"": ""string"" },
    ""server_url"": { ""type"": ""string"" },
    ""document_ids"": { ""type"": ""array"" },
    ""document_id"": { ""type"": ""string"" },
    ""issue_id"": { ""type"": ""string"" },
    ""issue"": { ""type"": ""object"", ""description"": ""title, description, issue_type_id (subtype), status, assigned_to(_type), due_date/start_date, location_id, root_cause_id"" },
    ""finding"": { ""type"": ""object"", ""description"": ""A coordination ledger row (CSV columns/JSON keys) mapped to title/description/key; issue overrides it."" },
    ""external_key"": { ""type"": ""string"", ""description"": ""Idempotency key kept in the issue description; a retry finds the issue instead of duplicating it."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_excel_read_rows",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "Read rows from an .xlsx worksheet through the same minimal OPC reader the writer uses - no " +
                    "new dependency, no Revit. Types are PRESERVED, never guessed: numbers as numbers, booleans as " +
                    "booleans, shared/inline strings as strings; Excel DATES are numbers with a format and no date " +
                    "is invented from them. Formula cells return their CACHED value with formula=true (a formula " +
                    "with no cache is reported, not zeroed), merged ranges are DECLARED (their value lives at the " +
                    "anchor; covered cells read null), the file's sha256 comes back so a read->decide->write chain " +
                    "can prove it acted on what it read, and a file that is not a valid workbook REFUSES whole.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""file_path""],
  ""properties"": {
    ""file_path"": { ""type"": ""string"", ""description"": ""Absolute path to the .xlsx."" },
    ""sheet"": { ""type"": ""string"", ""description"": ""Worksheet name (case-insensitive). Omitted: the first sheet."" },
    ""max_rows"": { ""type"": ""integer"", ""default"": 1000, ""minimum"": 1, ""maximum"": 10000 }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_read_schedule",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "Read reinforcement SCHEDULE SHEETS - beam cards (framed card, title = the member mark, axis labels, bars as lines " +
                    "with a callout above such as 2#6 L=6.40, 90-degree hook ticks, stirrup zones as dimension lines with 60e#3 over " +
                    "c/.30, 11@.08 or 14#3@.07, SON n stirrups, Son n Vigas members), wall cards (vertical bars beside rotated callouts, " +
                    "level lines, n#k over c/.20 or n#k L= written across them) and bending tables (columns the CALLER names) - from a " +
                    "vector PDF (file_path), from DWG entities that carry text (cad_entities + cad_text_anchor) or from primitives. Every callout goes through " +
                    "the F3 notation reader. The scale and the tolerances are ARGUMENTS, never inferred. What cannot be read with " +
                    "certainty is REFUSED with a code (callout_without_line, callout_two_lines_equally_close, line_claimed_by_two_callouts, " +
                    "callout_notation_refused, hook_ambiguous, zone_spacing_missing, card_frame_not_found, sheet_has_no_text...), never " +
                    "guessed. Self-checks are named results (pass/fail/not_run): bar drawn length vs its L at the scale, zone count vs its " +
                    "length, zones vs the stated stirrup total, zones vs the span between axes, levels vs the scale, table count x length vs " +
                    "total and rows vs the totals row, cards vs a sheet's stated count. With requirement_set (bar_type_names...) the beam " +
                    "cards are PROPOSED as section_rules keyed by mark - all or nothing, every reason listed. Writes nothing; plan the " +
                    "proposal with horizun_plan_reinforcement (Pro edition).",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""file_path"": { ""type"": ""string"", ""description"": ""Absolute path to a vector PDF. Exactly one of file_path, cad_entities, primitives."" },
    ""pages"": { ""type"": ""array"", ""items"": { ""type"": ""integer"", ""minimum"": 1 }, ""description"": ""1-based PDF pages. Omitted: every page (at most 50)."" },
    ""cad_entities"": { ""type"": ""array"", ""description"": ""CAD IR entities in mm WITH text (the accoreconsole reading); a Revit-link reading has none."" },
    ""cad_text_anchor"": { ""type"": ""string"", ""enum"": [""start"", ""middle""], ""description"": ""REQUIRED with cad_entities whose texts carry no text_type: is a text's point the start or the middle of its baseline."" },
    ""primitives"": { ""description"": ""{ segments: [{a:[x,y], b:[x,y], color?, layer?}], polylines: [{points, closed?, color?, layer?}], texts: [{text, at:[x,y], height, direction?:[dx,dy], width?}] } in drawing mm, y up; or a list of them, one per sheet."" },
    ""scale"": { ""type"": ""number"", ""description"": ""REQUIRED: the drawing scale's denominator (50 for 1:50). Without it nothing is read (scale_not_declared)."" },
    ""tolerances"": { ""type"": ""object"", ""properties"": { ""length_mm"": { ""type"": ""number"" }, ""zone_span_mm"": { ""type"": ""number"" }, ""support_gap_mm"": { ""type"": ""number"" } }, ""additionalProperties"": false,
      ""description"": ""REQUIRED, both > 0, no defaults: length_mm judges drawn bars, zones and table totals; zone_span_mm the zones against the span between axes. support_gap_mm (optional): zones drawn between support faces, the widest gap a support leaves."" },
    ""stirrup_bar_label"": { ""type"": ""string"", ""description"": ""Regex with a named group bar for the card text stating its stirrup bar, for zones that state none."" },
    ""kinds"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""enum"": [""beam_cards"", ""wall_cards"", ""tables""] }, ""description"": ""What to read. Omitted: all three."" },
    ""title_pattern"": { ""type"": [""string"", ""array""], ""items"": { ""type"": ""string"" }, ""description"": ""Regex (or list) a card title matches. Default ^[A-Z]{1,3}-?\\d{1,4}[A-Z]?$."" },
    ""bar_colors"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Only lines of these colours (#rrggbb; aci:n for a DWG) can be bars."" },
    ""bar_layers"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Only lines on these layers can be bars."" },
    ""bar_linetypes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Only lines of these linetypes can be bars (DWG)."" },
    ""table"": { ""type"": ""object"", ""properties"": { ""columns"": { ""type"": ""object"", ""description"": ""Header text per column as the sheet writes it: mark, bar, count, length (required); total, shape, notation."" },
      ""bar_prefix"": { ""type"": ""string"", ""enum"": [""#"", ""Ø""] }, ""total_label"": { ""type"": ""string"" } }, ""additionalProperties"": false,
      ""description"": ""Without it no table is read: no header dictionary is compiled in."" },
    ""requirement_set"": { ""type"": ""object"", ""properties"": { ""set_id"": { ""type"": ""string"" },
      ""bar_type_names"": { ""type"": ""object"", ""description"": ""Bar as written (#5, Ø16) -> RebarBarType name in the model."" },
      ""hook_type_names"": { ""type"": ""object"", ""description"": ""Key 90 -> the hook type name, for hooked bars."" },
      ""cover"": { ""type"": ""object"", ""description"": ""A section-rule cover. Omitted: { source: host }."" },
      ""stirrup_hook_type_name"": { ""type"": ""string"" },
      ""extents"": { ""type"": ""string"", ""enum"": [""axes"", ""host""], ""description"": ""axes (default): stations from the card's named axes, placed on the model's grids by the plan. host: from the host's start face."" },
      ""card_left_is_host_start"": { ""type"": ""boolean"", ""description"": ""extents=host only: map asymmetric zones or one-end hooks to the host's start."" },
      ""face_slots"": { ""type"": ""object"", ""properties"": { ""laps"": { ""type"": ""string"", ""enum"": [""inside"", ""outside""] }, ""additional"": { ""type"": ""string"", ""enum"": [""between""] } },
        ""additionalProperties"": false, ""minProperties"": 1, ""description"": ""How the drawing set lays two groups on one face, which a card does not draw. laps: two groups of one bar and one count overlapping at unhooked ends are one lapped layer, the second piece toward inside/outside. additional=between: a group within one continuous group, one bar fewer, fills its gaps. Anything else stays face_bars_overlap."" } },
      ""additionalProperties"": false, ""description"": ""Ask for the beam cards to be proposed as a horizun.structural-requirements/1 set."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_run_procedure",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "Follow one of the procedures horizun_workflows publishes, step by step, with a record that " +
                    "survives the conversation. IT CALLS NOTHING. It hands you the next call to make - the tool, " +
                    "what that step takes from the ones before it, what must already be true, and what to read " +
                    "back - you make that call yourself, and you send the reply back with operation=record. That " +
                    "boundary is the point: every write keeps its own dry_run, confirmation token and " +
                    "target_document, because this layer never touches them, and no procedure can run by " +
                    "accident because there is no execute. A recorded step is JUDGED rather than believed: the " +
                    "reply's own 'verified', application outcome or coverage statement is read, and a reply " +
                    "carrying none of them is recorded as NOT EVALUATED - never as ok, because the caller's word " +
                    "is not evidence. A partial application and an incomplete coverage are also not_evaluated. " +
                    "The run is resumable by id (a conversation that dies at step 5 of 8 is resumed, not " +
                    "restarted) and records are swept after 30 days. Finishing every step is not the same as " +
                    "meeting the acceptance criterion, which is about the MODEL and is a reading somebody still " +
                    "has to do; the summary says so rather than declaring success.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": {
      ""type"": ""string"",
      ""enum"": [""start"", ""advance"", ""decide"", ""record"", ""reconcile"", ""status"", ""abandon""],
      ""default"": ""start"",
      ""description"": ""start: open a run. advance: RUN the next step (only for a procedure horizun_workflows marks `executable`) - it dispatches through the same entry point tools/call uses, so a step that writes carries its own dry_run and confirmation because it IS that call. decide: supply what a step is waiting for; a step that needs a choice HOLDS the run rather than choosing. record: report what a step produced when you ran it yourself - the only route for a procedure whose steps have no argument templates. reconcile: a step dispatched whose reply never arrived is asked of the tool itself - a read again, or a write again with the SAME idempotency key (the bridge replays what landed and never runs it twice); a write sent without a key is refused. status: where a run is. abandon: stop it, with a reason.""
    },
    ""procedure"": { ""type"": ""string"", ""description"": ""start: the procedure id from horizun_workflows. One whose `detail` is tool_list_only has no route and is refused."" },
    ""run_id"": { ""type"": ""string"", ""description"": ""record/status/abandon: the id start returned."" },
    ""step"": { ""type"": ""integer"", ""description"": ""record: which step this result is for. A step is recorded ONCE - recording it twice would overwrite the evidence of what happened."" },
    ""outcome"": { ""type"": ""string"", ""enum"": [""ok"", ""failed"", ""skipped""], ""description"": ""record: what you observed. It is the starting point, not the verdict: the result you send is read and may downgrade an 'ok' to not_evaluated or failed."" },
    ""result"": { ""description"": ""record: the reply that step produced, verbatim. Send the whole thing - the fields that decide the verdict are 'verified', 'host_verified', 'coverage_complete' and the application outcome."" },
    ""note"": { ""type"": ""string"", ""description"": ""record: anything a later reader needs that the reply does not carry."" },
    ""reason"": { ""type"": ""string"", ""description"": ""abandon: REQUIRED. Without it nobody can tell an unfinished procedure from one deliberately stopped."" },
    ""values"": { ""type"": ""object"", ""description"": ""decide: what the step asked for, by the names its decision_needed states. Recorded against THAT step; a later step reuses it only if its own template asks for it by name, so a decision is never silently applied twice."" },
    ""decided_by"": { ""type"": ""string"", ""description"": ""decide: who decided. Kept with the run."" },
    ""decision_version"": { ""type"": ""string"", ""description"": ""decide: REQUIRED - the identity of this decision (for example the proposal version it answers). Recorded with the run; a key the step does not read is refused."" },
    ""inputs"": { ""type"": ""object"", ""description"": ""start: what the procedure needs. The catalogue lists its inputs in prose; what can be matched is checked and what cannot is reported as not supplied rather than assumed."" },
    ""target_document"": { ""type"": ""string"", ""description"": ""start: the document this run is about. It is carried into every step's next-call so a run cannot drift onto another document because a window changed."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_excel_write_rows",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "Append rows to a worksheet of an existing .xlsx, preserving the rest of the workbook (every other " +
                    "sheet, all styles, tables and formatting); create_if_missing=true starts a new .xlsx when the path holds " +
                    "nothing, never replacing a file. HONESTY: an existing original is BACKED UP first (to backup_path " +
                    "in the Horizun state folder, not beside the file); a file that is not a valid .xlsx (a zip carrying xl/workbook.xml) is REFUSED, never " +
                    "written into corruption; and after the new workbook is built it is RE-OPENED and every appended cell " +
                    "is read back and compared to what you asked before it replaces the original â€” rows_written is what the " +
                    "file holds on re-read, not a count of calls. Text is written as inline strings, numbers as numbers. v1 " +
                    "APPENDS after the last used row and does NOT expand an Excel Table's range: sheet_has_table reports " +
                    "whether the target sheet carries a table, so rows landing below it are never silently assumed to be " +
                    "inside it. Writes to disk; touches no Revit model. REQUIRES an idempotency_key: appending is not " +
                    "undone by repeating it, so a lost reply resent without a key lands the rows twice - with a key, an " +
                    "identical retry replays the recorded answer and the same key aimed at different rows is refused.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""file_path"", ""rows"", ""idempotency_key""],
  ""properties"": {
    ""format"": { ""type"": ""string"", ""enum"": [""xlsx"", ""csv""], ""default"": ""xlsx"", ""description"": ""csv appends RFC-4180 rows to a .csv (created when absent - an xlsx never is) under the same at-most-once ledger, lock, backup and temp-and-replace, re-reading the bytes as its evidence. Only an absolute local .csv in a folder that already exists, never under Startup, Windows, Program Files or .ssh, never UNC. Appends keep the file encoding (UTF-8, UTF-8 BOM, Windows-1252), delimiter (a ; file gets ; and decimal commas) and line ending; text starting with = + - @ is prefixed with an apostrophe."" },
    ""file_path"": { ""type"": ""string"",
      ""description"": ""Absolute path to an existing .xlsx (or a new one with create_if_missing). An existing file is backed up to the Horizun state folder (reply: backup_path) before any write. A file that is not a valid .xlsx package is an ERROR, never overwritten."" },
    ""create_if_missing"": { ""type"": ""boolean"", ""default"": false,
      ""description"": ""true: when file_path does not exist, create a new .xlsx whose first worksheet is 'sheet' (default Sheet1) and append the rows to it; reply created=true, no backup. An existing file is appended to, never replaced."" },
    ""sheet"": { ""type"": ""string"",
      ""description"": ""Worksheet name to append to (case-insensitive). Omit to use the FIRST sheet in workbook order. A name matching no sheet is an error; the response lists the sheets that do exist."" },
    ""rows"": {
      ""type"": ""array"", ""minItems"": 1,
      ""description"": ""Rows to append. Each element is an array of cell values, filled left-to-right from column A. A cell may be a string, number, boolean or null (null leaves the cell blank â€” a blank is not a zero)."",
      ""items"": { ""type"": ""array"", ""items"": { ""type"": [""string"", ""number"", ""boolean"", ""null""] } }
    },
    ""idempotency_key"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200,
      ""description"": ""REQUIRED. A retry with the same key and identical arguments returns the recorded answer without appending again; the same key with different rows or a different workbook is refused. Generate a new UUID for each deliberate append and keep it unchanged only for retries."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_budget_compare",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "Compare a horizun_quantities TAKEOFF (mode 'takeoff' rows, inline or from a JSON file) against a " +
                    "budget baseline read from an .xlsx through the same OPC reader as horizun_excel_read_rows, and " +
                    "report per code: added | removed | modified | unchanged | not_comparable, with SEPARATE deltas for " +
                    "quantity (abs and pct, inside a declared tolerance or not), classification (not in baseline, and " +
                    "group/unknown against a catalogue you pass) and price (model quantity at the BASELINE unit price - " +
                    "no price is ever invented; a line without one reports not_available). HONESTY: no unit is converted " +
                    "without a declared {from, to, factor} (unit_incompatible otherwise), an incomplete read is a lower " +
                    "bound and is not compared, a fragment (elements without the quantity) is refused unless you opt in, " +
                    "and every line keeps its element ids, documents and baseline row indices. The structured result " +
                    "ALWAYS comes back; each declared destination is then written and reported ON ITS OWN - " +
                    "written | replayed | skipped | failed | in_doubt with evidence - there is no global transaction, so a " +
                    "failed Excel write does not undo a Power BI push and the reply says so. Excel: a NEW workbook with a " +
                    "header row and one row per code, written through horizun_excel_write_rows's lock/verify/backup " +
                    "path; an existing file is refused unless overwrite_policy=replace, and then backed up first. Power " +
                    "BI: through horizun_power_bi_push with dry_run defaulting to true and its ledger; a lost answer is " +
                    "reported in_doubt with the ledger key and is never re-sent automatically. PERMISSION: the " +
                    "comparison ALONE - no 'outputs' - reads a workbook and writes nothing, so it runs at ANY " +
                    "permission_profile. DECLARING a destination, or operation=export_bc3 (a FIEBDC-3 .bc3 from the takeoff and your apu " +
                    "prices, all or nothing, never overwritten), needs full_write or unsafe_code, and under a " +
                    "lower profile the call is refused by name before anything is read.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""compare"", ""export_bc3""], ""default"": ""compare"" },
    ""apu"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""export_bc3: [{code, unit, unit_price, description?}]"" },
    ""bc3_path"": { ""type"": ""string"", ""description"": ""export_bc3: absolute path of a NEW .bc3"" },
    ""model_rows"": { ""type"": [""array"", ""object""],
      ""description"": ""The takeoff: either the per-element rows array or the whole horizun_quantities mode='takeoff' reply (its 'rows' are used; a TRUNCATED reply is refused - re-run with top >= rows_matching). Exactly one of model_rows / model_rows_path."" },
    ""model_rows_path"": { ""type"": ""string"", ""description"": ""Absolute path to a JSON file holding the same thing (a takeoff reply or a rows array). The simplest source is horizun_quantities mode='takeoff' with rows_file=true: pass its rows_file.path here, which holds every row, so a large takeoff never travels through the conversation."" },
    ""baseline"": { ""type"": ""object"", ""required"": [""file_path"", ""columns""], ""additionalProperties"": false,
      ""description"": ""The budget: an .xlsx read through horizun_excel_read_rows. Every row below header_row with a non-blank code is a line; blank-code rows are skipped and counted."",
      ""properties"": {
        ""file_path"": { ""type"": ""string"" },
        ""sheet"": { ""type"": ""string"", ""description"": ""Worksheet name (case-insensitive). Omitted: the first sheet."" },
        ""header_row"": { ""type"": ""integer"", ""minimum"": 1, ""default"": 1, ""description"": ""1-based position of the header row among the sheet's stored rows."" },
        ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 10000, ""default"": 10000 },
        ""columns"": { ""type"": ""object"", ""required"": [""code"", ""unit"", ""quantity""], ""additionalProperties"": false,
          ""description"": ""Which column carries what: a header NAME (case-insensitive) or a 1-based column INDEX."",
          ""properties"": {
            ""code"": { ""type"": [""string"", ""integer""] }, ""description"": { ""type"": [""string"", ""integer""] },
            ""unit"": { ""type"": [""string"", ""integer""] }, ""quantity"": { ""type"": [""string"", ""integer""] },
            ""unit_price"": { ""type"": [""string"", ""integer""] }, ""currency"": { ""type"": [""string"", ""integer""] }
          } }
      } },
    ""mapping"": { ""type"": ""object"", ""additionalProperties"": false,
      ""properties"": {
        ""code_field"": { ""type"": ""string"", ""default"": ""classification_code"", ""description"": ""The model row field carrying the code."" },
        ""quantity_field"": { ""type"": ""string"", ""description"": ""Pin which takeoff quantity feeds the comparison. Omitted: chosen by UNIT (the quantity whose declared unit equals the baseline's, or has a declared conversion to it); two candidates are refused as ambiguous."" },
        ""unit_conversions"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""required"": [""from"", ""to"", ""factor""], ""additionalProperties"": false,
          ""properties"": { ""from"": { ""type"": ""string"" }, ""to"": { ""type"": ""string"" }, ""factor"": { ""type"": ""number"", ""exclusiveMinimum"": 0 } } },
          ""description"": ""EXPLICIT ONLY, in the declared direction only: model value in 'from' times factor = value in 'to'. An undeclared pair is unit_incompatible, never converted."" },
        ""tolerances"": { ""type"": ""object"", ""additionalProperties"": false,
          ""properties"": { ""quantity_pct"": { ""type"": ""number"", ""minimum"": 0 }, ""quantity_abs"": { ""type"": ""number"", ""minimum"": 0 } },
          ""description"": ""A line is unchanged when |delta| <= quantity_abs OR |delta|/baseline*100 <= quantity_pct. Neither: exact match."" },
        ""rules"": { ""type"": ""object"", ""additionalProperties"": false,
          ""properties"": { ""compare_partial_coverage"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Compare a code even when some of its elements do not carry the quantity. Off, such a code is not_comparable (partial_coverage)."" } } },
        ""catalogue"": { ""type"": ""object"", ""description"": ""Optional {version, name?, codes: {code: is_leaf}} - the same shape horizun_audit_model's delivery readiness takes. Adds classification.catalogue_status per line (leaf / group_not_terminal / not_in_catalogue). Absent: catalogue_not_supplied, is_leaf null - never guessed."" }
      } },
    ""outputs"": { ""type"": ""object"", ""additionalProperties"": false,
      ""description"": ""Destinations, each written and reported separately. Omit for the comparison alone - no idempotency_key needed, nothing written, and no permission_profile required. Declaring EITHER destination (a dry-run Power BI push included, because its reply reports this machine's credential state) needs permission_profile=full_write or unsafe_code; under read_only or safe_write the whole call is refused before the baseline is read, naming the profile."",
      ""properties"": {
        ""excel"": { ""type"": ""object"", ""required"": [""file_path""], ""additionalProperties"": false,
          ""properties"": {
            ""file_path"": { ""type"": ""string"", ""description"": ""Absolute path of the .xlsx to CREATE: a new workbook with one sheet, a header row and one row per code."" },
            ""sheet"": { ""type"": ""string"", ""default"": ""Comparison"" },
            ""overwrite_policy"": { ""type"": ""string"", ""enum"": [""refuse"", ""replace""], ""default"": ""refuse"",
              ""description"": ""refuse: an existing file makes this destination 'skipped', nothing touched. replace: the existing file is backed up to the Horizun state folder (replaced_backup_path), then replaced under the workbook lock via temp-and-replace. A destination whose tool is in denied_tools is skipped (skip_reason denied_by_owner)."" }
          } },
        ""power_bi"": { ""type"": ""object"", ""required"": [""dataset_id"", ""table""], ""additionalProperties"": false,
          ""properties"": {
            ""workspace_id"": { ""type"": ""string"" }, ""dataset_id"": { ""type"": ""string"" },
            ""table"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 512 },
            ""dry_run"": { ""type"": ""boolean"", ""default"": true, ""description"": ""true validates the rows and the destination and sends nothing (status 'skipped', reason dry_run). false pushes one row per code through horizun_power_bi_push's ledger."" }
          } }
      } },
    ""idempotency_key"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 200,
      ""description"": ""export_bc3 requires it. REQUIRED when outputs.excel is declared or outputs.power_bi.dry_run is false. An identical retry replays the recorded reply (every destination included) without writing again; the same key with different arguments is refused. The destinations use derived keys <key>/excel and <key>/powerbi in their own ledgers."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_power_bi_push",
                Command = null,           // host-resident: fixed Microsoft endpoints, never forwarded to Revit
                Description =
                    "Push a bounded batch of primitive rows directly into a Power BI push semantic-model table, " +
                    "optionally inside a workspace. Dry-run validates the destination and Microsoft service limits " +
                    "without requesting a token or sending data. Apply accepts credentials ONLY from fixed server " +
                    "environment variables (short-lived access token or Entra service principal), sends only to " +
                    "api.powerbi.com, and uses a durable idempotency ledger: an identical retry replays the recorded " +
                    "answer, while a lost HTTP response becomes in_doubt and is never sent twice automatically. " +
                    "Requires permission_profile=full_write or unsafe_code.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""dataset_id"", ""table"", ""rows""],
  ""properties"": {
    ""workspace_id"": { ""type"": ""string"", ""description"": ""Optional Power BI workspace GUID. Omit for My workspace."" },
    ""dataset_id"": { ""type"": ""string"", ""description"": ""Push semantic-model/dataset GUID."" },
    ""table"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 512 },
    ""rows"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 10000,
      ""description"": ""Rows whose values are string, number, boolean or null. At most 75 distinct columns and 4000 characters per string."",
      ""items"": { ""type"": ""object"", ""minProperties"": 1, ""maxProperties"": 75,
        ""additionalProperties"": { ""type"": [""string"", ""number"", ""boolean"", ""null""] }
      }
    },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_selection_exchange",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "A SELECTION shared between Revit and a Power BI report, as a durable versioned document " +
                    "on a named channel. operation=capabilities is the honest inventory of the integration and " +
                    "is worth reading first: publishing from Revit and reading back into Revit are implemented; " +
                    "the LIVE push from a report visual is NOT, and it is not simulated - Power BI offers no " +
                    "public way for a visual to reach a process on the same machine, so closing that needs " +
                    "either a custom visual with a local listener or the REST API with a tenant, workspace and " +
                    "app registration. Those are credentials and product decisions, and they are named as " +
                    "external dependencies rather than hidden behind a screen. THE IDENTITY RULE IS THE POINT: " +
                    "a selection carries the document's title AND fingerprint, and reading it while a DIFFERENT " +
                    "document is in play returns NO KEYS - a Revit ElementId means something only inside one " +
                    "document, and a report joining on raw ids lights up element N of whichever model happens " +
                    "to be open. The selection therefore travels as KEYS: the report's key column and the Revit " +
                    "parameter it corresponds to are a project decision and arrive as arguments, because " +
                    "guessing them produces a selection that is confidently wrong.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""publish"", ""read"", ""clear"", ""capabilities""], ""default"": ""read"" },
    ""channel"": { ""type"": ""string"", ""maxLength"": 64, ""default"": ""default"", ""description"": ""Letters, digits, '-' and '_'. It becomes a file name, and a name silently cleaned up is a channel one side writes and the other never finds."" },
    ""document_title"": { ""type"": ""string"" },
    ""document_fingerprint"": { ""type"": ""string"", ""description"": ""From horizun_health. Publishing requires it; reading with a different one returns no keys."" },
    ""key_column"": { ""type"": ""string"", ""description"": ""publish: the column in the report the keys belong to."" },
    ""key_parameter"": { ""type"": ""string"", ""description"": ""publish: the Revit parameter that carries the same key. Never a raw ElementId."" },
    ""keys"": { ""type"": ""array"", ""maxItems"": 20000, ""items"": { ""type"": ""string"" }, ""description"": ""publish: the selected keys. An EMPTY selection is published with operation=clear, so that 'nothing is selected' and 'somebody published an empty list by accident' are different documents."" },
    ""ttl_ms"": { ""type"": ""integer"", ""minimum"": 1000, ""default"": 21600000, ""description"": ""How long the selection stays live. A selection is a moment, not a state: a click from yesterday must not quietly drive today's model."" },
    ""source"": { ""type"": ""string"", ""description"": ""Who published it, for the record."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_promote_script",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "Promote a Python script to a NAMED, VERSIONED, REVIEWED artefact - and nothing more than " +
                    "that. IT GRANTS NO PERMISSION. A promoted script is still Python: it comes back as source " +
                    "and you run it through horizun_execute_python, under the machine owner's persistent Python " +
                    "grant, with the same refusals and the same self-reported evidence as any other script. " +
                    "There is no execution path in this tool at all, which is the structural way of keeping " +
                    "that true. THE STATES ARE proposed -> reviewed -> approved, then activate. Nothing is born " +
                    "approved, including a correction to something that was; a generation must record evidence " +
                    "of having been tested before it can be reviewed; the AUTHOR MAY NOT REVIEW THEIR OWN WORK, " +
                    "because a review by the person who wrote it is the step happening on paper and not in " +
                    "fact; and approval is a separate act from review, by name. ACTIVATION IS REVERSIBLE BY " +
                    "DESIGN: the pointer moves atomically, the previous generation's bytes are never deleted, " +
                    "and a source edited after approval fails to activate - it no longer hashes to what was " +
                    "reviewed - leaving the old generation live. Compiled commands are NOT reloadable and this " +
                    "does not pretend otherwise: a .NET assembly loaded into Revit cannot be unloaded, so what " +
                    "can be swapped is what is read from disk per call, which is Python.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": {
      ""type"": ""string"",
      ""enum"": [
        ""list"",
        ""show"",
        ""propose"",
        ""review"",
        ""approve"",
        ""activate"",
        ""deactivate"",
        ""source"",
        ""resolve"",
        ""invocation""
      ],
      ""default"": ""list"",
      ""description"": ""list/show/source read the registry. propose/review/approve/activate move a generation along the ladder, and nothing is born approved. deactivate WITHDRAWS the script: it stops serving any generation, keeps every one of them on disk, and records who pulled it and why - so the answer to 'this is doing damage' is not 'publish a different version'. resolve says WHICH generation is live and what it takes. invocation returns a ready horizun_execute_python request for it - a REQUEST, which you then send: this tool executes nothing, ever, and that is structural rather than careful.""
    },
    ""id"": {
      ""type"": ""string"",
      ""maxLength"": 64,
      ""description"": ""Letters, digits, '-' and '_'. Anything else is REFUSED rather than cleaned up: an id that silently became another id is a promotion nobody can find again.""
    },
    ""title"": {
      ""type"": ""string""
    },
    ""description"": {
      ""type"": ""string""
    },
    ""source"": {
      ""type"": ""string"",
      ""description"": ""propose: the Python. Identical bytes to an existing generation are refused - one thing with two review histories is worse than either.""
    },
    ""author"": {
      ""type"": ""string"",
      ""description"": ""propose: who wrote it. Required; nothing promotes itself.""
    },
    ""evidence"": {
      ""type"": ""string"",
      ""description"": ""propose: what this was tested against. Required before it can be reviewed - promotion after review means after review OF SOMETHING.""
    },
    ""generation"": {
      ""type"": ""integer"",
      ""description"": ""review/approve/activate: which generation.""
    },
    ""reviewer"": {
      ""type"": ""string""
    },
    ""note"": {
      ""type"": ""string""
    },
    ""approver"": {
      ""type"": ""string""
    },
    ""by"": {
      ""type"": ""string"",
      ""description"": ""deactivate: who is withdrawing this. Required.""
    },
    ""reason"": {
      ""type"": ""string"",
      ""description"": ""deactivate: why. REQUIRED, because a withdrawal nobody explained cannot be safely undone - the next person either republishes something that was pulled for a cause they cannot see, or leaves a working script withdrawn forever.""
    },
    ""contract"": {
      ""type"": ""object"",
      ""description"": ""propose: REQUIRED. What this generation takes, what it returns, and the minimum permission a caller needs. Without it a generation can reach 'approved' - two people having signed it off - and still be uncallable, because nobody knows what to send. Declared PER GENERATION, not per script: generation 3 taking an argument generation 2 did not is exactly the change a version exists to record."",
      ""required"": [
        ""inputs"",
        ""output"",
        ""minimum_permission""
      ],
      ""additionalProperties"": false,
      ""properties"": {
        ""inputs"": {
          ""type"": ""object"",
          ""description"": ""A JSON Schema object for the arguments. It must declare type=object and a properties map; a schema that describes nothing accepts everything, which is the same as having none. Set additionalProperties:false to have an undeclared argument REFUSED - an argument nothing reads is a default nobody chose, and a typo in an argument name is the commonest way that happens.""
        },
        ""output"": {
          ""type"": ""object"",
          ""description"": ""A JSON Schema object for the structured __output__ the script assigns. It must declare at least one property: a script whose output shape is unstated is a script whose result nobody can check, and the whole reason this path labels results self-reported is that somebody has to.""
        },
        ""minimum_permission"": {
          ""type"": ""string"",
          ""enum"": [
            ""read_only"",
            ""safe_write"",
            ""unsafe_code""
          ],
          ""description"": ""Must be 'unsafe_code'. A promoted script is Python and runs through horizun_execute_python, which needs the machine owner's grant. Declaring less would tell a reader that a lesser session can call this, and it cannot. Promotion buys provenance and review; it buys no rights.""
        },
        ""workflow_id"": {
          ""type"": ""string"",
          ""description"": ""The workflow-catalogue entry this script belongs to, when it belongs to one. Recorded so a procedure and the script that carries it out can be found from each other.""
        }
      }
    },
    ""arguments"": {
      ""type"": ""object"",
      ""description"": ""invocation: the arguments for the script, checked against the active generation's declared inputs BEFORE anything is built. They arrive inside the script as one variable, HORIZUN_ARGS_JSON, holding JSON text the script parses itself.""
    },
    ""target_document"": {
      ""type"": ""string"",
      ""description"": ""invocation: REQUIRED. horizun_execute_python matches it against the ACTIVE document, because 'the active document' is whatever window was in front when the call arrived - and on a machine with two Revit hosts open that is not a question a promoted script should answer by accident.""
    }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_repair_memory",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "The durable memory of FAILURE SHAPES and what a person found fixed them, plus an explicit, " +
                    "reversible quarantine. Host-resident: it answers without Revit, which is the state Revit is " +
                    "usually in when somebody most wants to ask. WHAT IS STORED IS A SHAPE, NEVER A MESSAGE: " +
                    "every path, quoted name, identifier and number is replaced before anything is written, and " +
                    "a message the redactor could not clean is REFUSED rather than stored - nothing about any " +
                    "model can be reconstructed from this file. NOTHING IS LEARNED WITHOUT A PERSON and nothing " +
                    "is ever applied automatically: advice is advice. A remedy becomes validated only when " +
                    "somebody records the same text a second time. A quarantine needs a reason and an author " +
                    "and marks only that exact shape as do-not-retry. The bridge cannot refuse by shape alone - a " +
                    "shape is the failure's own message, known only once a call has failed - so pass the failing " +
                    "call's arguments as call_arguments to observe or quarantine: only a hash of their normalised " +
                    "form is kept, and while the shape is quarantined that same call (same tool and arguments, " +
                    "idempotency_key and confirmation_token aside) is REFUSED before it reaches Revit with code " +
                    "call_quarantined, the reason and the way to lift it. advice on a quarantined shape answers " +
                    "QUARANTINED with the reason, who set it and how to lift it, and a quarantine never expires silently.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""list"", ""advice"", ""observe"", ""remedy"", ""quarantine"", ""release""], ""default"": ""list"" },
    ""tool"": { ""type"": ""string"", ""description"": ""observe/advice: which tool failed. Part of the shape."" },
    ""failure_class"": { ""type"": ""string"", ""description"": ""observe/advice: a short class such as refusal, rollback, transport. Part of the shape."" },
    ""message"": { ""type"": ""string"", ""description"": ""observe/advice: the failure text. It is normalised here and the original is never written anywhere."" },
    ""shape"": { ""type"": ""string"", ""description"": ""remedy/quarantine/release: the shape id from list or advice."" },
    ""call_arguments"": { ""type"": ""object"", ""description"": ""observe/quarantine: the failing call's arguments, exactly as sent. Only a hash of their normalised form is stored, never a value. While the shape is quarantined, the same call is refused with code call_quarantined."" },
    ""remedy"": { ""type"": ""string"", ""description"": ""remedy: what actually worked, in GENERAL terms. Text carrying a path, a quoted name or a long number is refused."" },
    ""reason"": { ""type"": ""string"", ""description"": ""quarantine: why. Required - a refusal nobody can argue with later is worse than the failure it was meant to stop."" },
    ""author"": { ""type"": ""string"", ""description"": ""remedy/quarantine: who decided. Required; nothing here decides by itself."" },
    ""state"": { ""type"": ""string"", ""enum"": [""observed"", ""remedy_proposed"", ""remedy_validated"", ""quarantined""], ""description"": ""list: filter."" },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 50 }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_target",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "Which Revit these tools talk to, answered WITHOUT touching Revit. No arguments: every Revit that " +
                    "published a bridge (year, pid, alive, add-in version) and which is selected and why. 'year' or 'pid' " +
                    "sends every later call to that Revit ('auto' clears it); with two instances of one year use pid, or 'document' " +
                    "(the model's name) to follow the model across Revit restarts. A " +
                    "year with no bridge is REFUSED and the target kept. operation=focus_window brings pid's window to the " +
                    "front. HORIZUN_SESSION_KEY remembers the choice; HORIZUN_REVIT_PID pins one.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"",
      ""description"": ""Process id of a SPECIFIC Revit instance to direct calls to. Use this when two instances of the same year are running - a year no longer names one process. Wins over year."" },
    ""year"": { ""type"": ""string"",
      ""description"": ""Four-digit Revit year to direct all later calls to, e.g. '2026'. Pass 'auto' to clear the choice and go back to the most recently started running Revit. Omit to only report, changing nothing."" },
    ""document"": { ""type"": ""string"",
      ""description"": ""Name of the MODEL (as in Revit's title bar, extension optional) to direct calls to. The server finds the one running Revit whose active document it is and keeps following the model if that instance restarts; none or two showing it is refused and the target kept. Not with pid or year."" },
    ""operation"": { ""enum"": [""report"", ""focus_window""] }
  },
  ""additionalProperties"": false
}")
            }
        }));

        /// <summary>
        /// Attach behavioural metadata and the argument every mutating execution shares.
        /// Keeping this here means a newly-added mutating command cannot forget to tell
        /// either the server or the add-in about idempotency.
        /// </summary>
        // Conditions found by scripts/audit-conditional-requirements.py. Keep the
        // runtime requirement visible to kit validation, rather than only prose.
        private static void AddConditionalRequirements(List<CommandContract> all)
        {
            JObject Schema(string tool) => all.First(c => c.Name == tool).InputSchema;
            JObject Has(string field) => new JObject { ["required"] = new JArray(field) };
            JObject Is(string field, params string[] values) => new JObject
            { ["required"] = new JArray(field), ["properties"] = new JObject { [field] = new JObject { ["enum"] = new JArray(values) } } };
            void Append(JObject schema, JObject condition, string requirements)
            {
                var conditions = schema["allOf"] as JArray;
                if (conditions == null) schema["allOf"] = conditions = new JArray();
                conditions.Add(new JObject { ["if"] = condition, ["then"] = JObject.Parse(requirements) });
            }
            void Add(string tool, JObject condition, string requirements) => Append(Schema(tool), condition, requirements);
            void On(string tool, string field, string values, string requirements) => Add(tool, Is(field, values.Split('|')), requirements);

            foreach (string name in new[] { "get_document_info", "horizun_navigate", "horizun_get_dimension_references", "horizun_plan_annotations", "horizun_plan_views", "horizun_model_scan", "horizun_quantities", "horizun_plan_structure" })
                Schema(name)["properties"]["target_document"] = new JObject { ["type"] = "string" };
            Schema("horizun_query_structure")["properties"]["target_document"] = new JObject { ["type"] = "string" };
            Schema("horizun_query_dimensions")["properties"]["target_document"] = new JObject { ["type"] = "string" };
            foreach (string name in new[] { "horizun_list_schedules", "horizun_get_schedule_data", "horizun_query_detail_2d", "horizun_audit_planimetry" })
                Schema(name)["properties"]["target_document"] = new JObject { ["type"] = "string" };
            On("horizun_query_structure", "mode", "steel_review", "{'required':['tolerance_mm']}");
            On("horizun_code_check", "operation", "headroom", "{'required':['headroom']}");
            On("horizun_code_check", "operation", "travel_distance", "{'required':['travel']}");
            On("horizun_framing", "operation", "remove", "{'required':['element_ids'],'properties':{'element_ids':{'minItems':1}}}");

            On("horizun_manage_groups", "operation", "create|add_members|remove_members", "{'required':['element_ids'],'properties':{'element_ids':{'minItems':1}}}");
            On("horizun_manage_groups", "operation", "rename_type|duplicate_type|swap_type|change_group_type", "{'required':['type_id']}");
            On("horizun_manage_groups", "operation", "swap_type|change_group_type|ungroup", "{'required':['group_ids'],'properties':{'group_ids':{'minItems':1}}}");
            On("horizun_manage_groups", "operation", "change_group_type|set_group_level|add_members|remove_members|edit_type_members", "{'required':['group_ids'],'properties':{'group_ids':{'minItems':1,'maxItems':1}}}");
            On("horizun_manage_groups", "operation", "set_group_level", "{'required':['level_id']}");
            On("horizun_manage_groups", "operation", "edit_type_members", "{'required':['scope'],'properties':{'scope':{'const':'type'}}}");
            Add("horizun_manage_groups", Has("relative_rotations_degrees"), "{'properties':{'operation':{'enum':['check_variation','add_members']}}}");
            On("horizun_manage_groups", "operation", "check_variation", "{'properties':{'relative_rotations_degrees':{'maxItems':49}}}");
            On("horizun_manage_phases", "operation", "set_element_phases", "{'anyOf':[{'required':['created_phase_id']},{'required':['demolished_phase_id']}]}");
            On("horizun_manage_worksets", "operation", "rename|set_default|visibility|move_elements", "{'required':['workset_id']}");
            On("horizun_manage_worksets", "operation", "visibility", "{'required':['view_ids','visibility'],'properties':{'view_ids':{'minItems':1}}}");
            On("horizun_manage_worksets", "operation", "move_elements", "{'oneOf':[{'required':['element_ids'],'properties':{'element_ids':{'minItems':1}}},{'required':['category']}]}");

            On("horizun_quantities", "mode", "carbon", "{'required':['factor_source']}");
            On("horizun_quantities", "mode", "takeoff", "{'required':['classification_parameter','quantities'],'properties':{'quantities':{'minItems':1}}}");
            Add("horizun_quantities", JObject.Parse("{'required':['include_links'],'properties':{'include_links':{'const':true}}}"), "{'required':['category'],'not':{'required':['element_ids']}}");
            Add("horizun_query_model", JObject.Parse("{'required':['sum_parameters'],'properties':{'sum_parameters':{'minItems':1}},'not':{'required':['response_mode'],'properties':{'response_mode':{'const':'summary'}}}}"), "{'required':['group_by'],'properties':{'group_by':{'minItems':1}}}");

            On("horizun_manage_curtain", "operation", "set_mullions", "{'required':['mode']}");
            Add("horizun_manage_curtain", JObject.Parse("{'required':['operation','mode'],'properties':{'operation':{'const':'set_mullions'},'mode':{'const':'add'}}}"), "{'required':['mullion_type_id']}");
            On("horizun_slab_shape", "operation", "add_point", "{'required':['points'],'properties':{'points':{'minItems':1}}}");
            On("horizun_slab_shape", "operation", "add_split_line", "{'required':['start','end']}");
            Add("horizun_create_railing", Has("path"), "{'required':['level_id'],'properties':{'path':{'minItems':2}}}");
            On("horizun_join_geometry", "cut_order", "first_cuts", "{'required':['pairs']}");
            On("horizun_view_visibility", "operation", "marker_scale", "{'required':['view_ids','coarser_than'],'properties':{'view_ids':{'minItems':1},'coarser_than':{'minimum':1,'maximum':24000}}}");
            On("horizun_view_visibility", "operation", "copy_link_overrides", "{'required':['view_id','view_ids'],'properties':{'view_ids':{'minItems':1,'maxItems':200}}}");
            On("horizun_view_visibility", "operation", "read|hide|unhide|set_overrides|clear_overrides|hide_foreign_markers|datum_cleanup|explain_overrides|link_datum_cleanup", "{'required':['view_id']}");
            On("horizun_view_visibility", "operation", "set_scale", "{'required':['view_id','scale']}");
            On("horizun_view_visibility", "operation", "override_report|graphics_report", "{'required':['view_ids']}");
            On("horizun_view_visibility", "operation", "copy_view_graphics", "{'required':['view_id','view_ids']}");
            On("horizun_view_visibility", "operation", "hide|unhide|set_overrides|clear_overrides|explain_overrides", "{'anyOf':[{'required':['element_ids'],'properties':{'element_ids':{'minItems':1}}},{'required':['categories'],'properties':{'categories':{'minItems':1}}},{'required':['family_names']},{'required':['type_names']}]}");
            On("horizun_view_visibility", "operation", "set_overrides", "{'required':['overrides'],'properties':{'overrides':{'minProperties':1}}}");
            On("horizun_verify_changes", "operation", "what_if", "{'required':['what_if']}");
            var whatIf = (JObject)Schema("horizun_verify_changes")["properties"]["what_if"];
            whatIf["allOf"] = JArray.Parse("[{'if':{'required':['action'],'properties':{'action':{'const':'move'}}},'then':{'required':['vector']}}]");
            // These messages describe nested contracts; keep the requirement at
            // its own level, rather than accidentally requiring a top-level key.
            var match = (JObject)Schema("horizun_link_schedule")["properties"]["match"];
            match["oneOf"] = JArray.Parse("[{'required':['parameter']},{'required':['rules']}]");
            match["properties"] = JObject.Parse("{'rules':{'type':'array','minItems':1,'items':{'type':'object','required':['activity','category']}}}");

            Add("horizun_capture_view", Has("margin_ratio"), "{'required':['element_ids']}");
            Add("horizun_capture_view", JObject.Parse("{'required':['calibrate_world_to_pixel'],'properties':{'calibrate_world_to_pixel':{'const':true}}}"), "{'required':['hide_annotations'],'properties':{'hide_annotations':{'const':true},'pixel_size':{'maximum':4096}}}");
            On("horizun_delete_verified", "mode", "ids", "{'required':['ids'],'properties':{'ids':{'minItems':1}}}");
            Add("horizun_delete_verified", JObject.Parse("{'required':['dry_run'],'properties':{'dry_run':{'const':false}}}"), "{'properties':{'max_passes':{'const':1}}}");
            On("horizun_export", "format", "pdf|fbx|pdf_set|dwg_set", "{'required':['view_ids'],'properties':{'view_ids':{'minItems':1}}}");
            On("horizun_export", "format", "dwg|image", "{'required':['view_ids'],'properties':{'view_ids':{'minItems':1,'maxItems':1}}}");
            Add("horizun_export", JObject.Parse("{'required':['format','nwc_scope'],'properties':{'format':{'const':'nwc'},'nwc_scope':{'const':'view'}}}"), "{'required':['view_ids'],'properties':{'view_ids':{'minItems':1,'maxItems':1}}}");
            On("horizun_export", "format", "cobie", "{'required':['cobie']}");
            On("horizun_export", "format", "ifc_setup", "{'required':['ifc_version','ifc_setup'],'properties':{'ifc_setup':{'required':['name']}}}");
            On("horizun_export", "format", "dwg_layers", "{'required':['dwg_setup'],'properties':{'dwg_setup':{'required':['name']}}}");
            On("horizun_save_document", "operation", "sync_with_central", "{'required':['comment'],'properties':{'comment':{'minLength':1,'pattern':'\\\\S'}}}");
            On("horizun_plan_annotations", "mode", "wall_thickness|opening_height|spot_elevation", "{'required':['element_ids'],'properties':{'element_ids':{'minItems':1,'maxItems':50}}}");
            On("horizun_plan_annotations", "mode", "chain_along_line", "{'required':['line_start','line_end','element_ids'],'properties':{'element_ids':{'minItems':1,'maxItems':200}}}");
            On("horizun_plan_annotations", "mode", "room_clear_width", "{'required':['room_id']}");

            var sequence = (JObject)Schema("horizun_write_params_verified")["properties"]["sequence"];
            sequence["oneOf"] = JArray.Parse("[{'required':['element_ids']},{'required':['category']}]");
            Append(sequence, Has("category"), "{'required':['phase_id']}");
            Append(sequence, JObject.Parse("{'required':['order_by'],'properties':{'order_by':{'const':'room'}}}"), "{'required':['phase_id']}");
            Append(sequence, JObject.Parse("{'required':['order_by'],'properties':{'order_by':{'type':'array','not':{'items':{'not':{'const':'room'}}}}}}"), "{'required':['phase_id']}");
            Add("horizun_query_model", JObject.Parse("{'required':['return_parameters'],'properties':{'return_parameters':{'type':'array','not':{'items':{'not':{'const':'*'}}}}},'not':{'required':['response_mode'],'properties':{'response_mode':{'const':'summary'}}}}"), "{'required':['parameter_format'],'properties':{'parameter_format':{'const':'full'}},'not':{'anyOf':[{'required':['group_by'],'properties':{'group_by':{'minItems':1}}},{'required':['sum_parameters'],'properties':{'sum_parameters':{'minItems':1}}}]}}");
            On("horizun_selection_context", "operation", "snapshot|diff", "{'required':['snapshot_name'],'properties':{'snapshot_name':{'minLength':1}}}");
            On("horizun_selection_context", "operation", "validate_ids", "{'required':['checks']}");
            On("horizun_selection_context", "operation", "snapshot", "{'oneOf':[{'required':['ids'],'properties':{'ids':{'minItems':1}}},{'required':['selector']},{'required':['selection'],'properties':{'selection':{'const':true}}}]}");
            foreach (var ruleLocation in new[] { new[] { "horizun_audit_model", "workset_rules" }, new[] { "horizun_model_scan", "sheet_rules" } })
            {
                var rules = Schema(ruleLocation[0])["properties"][ruleLocation[1]] as JObject;
                if (rules != null) Append(rules, JObject.Parse("{'type':'object'}"), "{'required':['version']}");
            }
            foreach (JObject creation in Schema("horizun_create_elements")["properties"]["elements"]["items"]["oneOf"])
            {
                var props = (JObject)creation["properties"];
                if (props["top_offset"] != null) Append(creation, Has("top_offset"), "{'required':['top_level_id']}");
                if (props["host_face"] != null) Append(creation, Is("host_face", "end"), "{'required':['host_id']}");
            }
        }

        private static List<CommandContract> Annotate(List<CommandContract> all)
        {
            var always = new HashSet<string>(StringComparer.Ordinal)
            {
                "horizun_open_document", "horizun_save_document", "horizun_relinquish_all",
                "horizun_execute_python", "horizun_submit_job"
            };
            var dryRun = new HashSet<string>(StringComparer.Ordinal)
            {
                "horizun_create_schedule", "horizun_write_params_verified", "horizun_delete_verified",
                "horizun_create_elements",
                "horizun_transform_elements",
                "horizun_undo",
                "horizun_manage_views",
                "horizun_manage_schedules",
                "horizun_export",
                "horizun_link_schedule",
                // Only operation=travel_distance with travel.create_paths writes (PathOfTravel elements).
                "horizun_code_check",
                "horizun_power_bi_push",
                "horizun_annotate",
                "horizun_edit_dimensions",
                "horizun_detail_2d",
                "horizun_fix_planimetry",
                "horizun_pack_sheets",
                "horizun_manage_revisions",
                "horizun_manage_phases", "horizun_manage_assemblies_parts",
                "horizun_manage_groups", "horizun_manage_worksets",
                "horizun_structural_connections",
                "horizun_manage_materials",
                "horizun_manage_styles", "horizun_manage_units",
                "horizun_execute_plan",
                "horizun_apply_corrections",
                "horizun_set_keynote", "horizun_manage_classification_table", "horizun_bind_shared_param", "horizun_manage_parameters",
                "horizun_set_keynote", "horizun_bind_shared_param",
                "horizun_split_floor_loops", "horizun_split_multilayer_walls", "horizun_split_by_level",
                "horizun_split_multilayer_slabs", "horizun_ungroup_and_mark",
                "horizun_regroup_by_param", "horizun_copy_slab_elevations",
                "horizun_embed_floors_in_toposolid", "horizun_grade_toposolid_around_floors",
                "horizun_rectangularize_walls",
                "horizun_manage_curtain", "horizun_slab_shape", "horizun_create_railing", "horizun_wall_sweep", "horizun_join_geometry", "horizun_room_finishes", "horizun_wall_finish", "horizun_planimetry_ops",
                "horizun_view_crop", "horizun_sheet_layout",
                "horizun_view_visibility",
                "horizun_framing",
                // Only operation=colorize writes (a duplicated view with overrides); every
                // other operation reads the model and writes only under the data root.
                "horizun_model_diff",
            };
            // Writes something outside the model that is still there after the call: a PNG,
            // a workbook. full_write is the rung that authorizes these.
            var external = new HashSet<string>(StringComparer.Ordinal)
            {
                "horizun_capture_view", "horizun_excel_write_rows", "horizun_verify_changes"
            };

            // Reaches outside the model ONLY when the call declares a destination, and
            // refuses that destination itself under a profile that does not authorize it.
            //
            // horizun_budget_compare used to sit in `external` above, with a comment
            // explaining that a comparison with no outputs writes nothing but the profile
            // decides on the enum anyway. That was true and it was the defect: reading a
            // budget and writing one are two different acts, and the tool was hidden from
            // read_only and safe_write for a capability the caller had not asked for. See
            // ToolEffect.ExternalSideEffectOnRequest for the two halves of the fix.
            var externalOnRequest = new HashSet<string>(StringComparer.Ordinal)
            {
                "horizun_budget_compare",
                // Reads and validates by default; writes project-context.json only on
                // draft with dry_run=false, and asks Settings.AllowsExternalSideEffect first.
                "horizun_project_context",
                // name/verify/inspect only read; stamp and transition write a sidecar, a copy
                // and a log line - and only with dry_run=false, which the handler gates
                // through Settings.AllowsExternalSideEffect. Nothing is ever overwritten.
                "horizun_information_container",
                // verify and the plan operations only read; emit with dry_run=false copies payload and
                // writes a manifest, gated through Settings.AllowsExternalSideEffect. Nothing is overwritten.
                "horizun_exchange",
                // Reads by default; issue_create / issue_update with dry_run=false write ACC
                // issues and ask Settings.AllowsExternalSideEffect first (CdeCloudIssues.cs).
                "horizun_cde_cloud"
            };

            // Steers the host and leaves no artefact: which Revit answers, what is
            // selected, which view is active. These used to be in `external`, which is why
            // making the restrictive profiles honour "external" needed this split first -
            // read_only has to keep horizun_target or it cannot choose the Revit it reads.
            var hostState = new HashSet<string>(StringComparer.Ordinal)
            {
                "horizun_navigate", "horizun_target", "horizun_request_python_access"
            };

            // MCP's destructiveHint, where every other classification already lives.
            // Beyond what Effect implies: a command can be MutatingUnlessDryRun and still
            // destroy something a caller cannot get back - an export overwrites a file, a
            // family rebuild replaces geometry, a push replaces a dataset.
            var destructive = new HashSet<string>(StringComparer.Ordinal)
            {
                "horizun_delete_verified", "horizun_execute_plan", "horizun_execute_python", "horizun_document_session",
                "horizun_export", "horizun_power_bi_push",
                // overwrite_policy=replace replaces a workbook (backed up first) and a
                // non-dry-run push lands rows in a dataset. Same reasons as export and
                // power_bi_push above.
                "horizun_budget_compare",
                // The correction cycle can drive horizun_delete_verified (orphan group
                // types, unplaced rooms). A composing surface that can delete is
                // destructive, whatever else it can do.
                "horizun_apply_corrections",
                // THREE TOOLS THAT DELETE, AND SAID SO IN THEIR OWN DESCRIPTION WHILE
                // TELLING EVERY CLIENT THEY WERE SAFE.
                //
                // destructiveHint is the annotation an MCP client reads to decide
                // whether to ask a person first. These three publish text that
                // convicts them - split_multilayer_slabs: "Originals are unpinned
                // before deletion"; split_floor_loops: "The original is deleted only
                // once at least one replacement exists"; ungroup_and_mark:
                // "Ungrouping destroys the only record of which elements belonged
                // together" - and all three were absent from this set, while
                // create_family sat in it for replacing family geometry.
                //
                // horizun_split_multilayer_walls is deliberately NOT here: it deletes
                // nothing. The original becomes the core wall and keeps its
                // ElementId, and its reply says originals_deleted is 0 by design.
                "horizun_split_multilayer_slabs", "horizun_split_floor_loops", "horizun_ungroup_and_mark",
                // original_wall=delete_if_unhosted deletes the ARQ walls the finish layers replace.
                "horizun_wall_finish",
                // Redefining a type deletes the old type and, with scope=all_instances, the
                // removed members of every other instance; ungroup destroys the grouping.
                "horizun_manage_groups",
                // AND TWO MORE THAT NO REVIEWER NAMED. Both erase toposolid vertices -
                // "existing toposolid points within 60cm of each outline are deleted first",
                // "existing toposolid points inside the graded footprint are deleted first" -
                // and both call DeletePoint and report points_deleted. Surveyed topography
                // is not recoverable by running the tool again.
                //
                // They were found by A_tool_whose_own_description_admits_deleting_declares_
                // destructive, which reads the descriptions instead of a second hand-kept
                // list. The hand-kept list had missed them for the same reason it missed the
                // other three: it records what somebody remembered, not what the tools do.
                "horizun_embed_floors_in_toposolid", "horizun_grade_toposolid_around_floors",
                // dissolve_parts removes parts and every edit made to them; disassemble removes the assembly.
                "horizun_manage_assemblies_parts",
                // remove_grid_line and set_mullions remove delete grid elements; reset_shape erases shape points.
                "horizun_manage_curtain", "horizun_slab_shape",
                // remove_binding and rebind (Instance<->Type) discard stored values; global_delete
                // deletes an element. Values do not come back by running the tool again.
                "horizun_manage_parameters"
            };

            // MCP's openWorldHint. Effect already covers ExternalSideEffect and
            // DocumentSession; these are the ones that reach outside the model while being
            // classified by Effect as ordinary model writes.
            var openWorld = new HashSet<string>(StringComparer.Ordinal)
            {
                "horizun_open_document", "horizun_export",
                "horizun_power_bi_push", "horizun_execute_python", "horizun_catalog_lookup",
                // Reads a cloud CDE over HTTPS and, on request, writes ACC issues: open-world by nature.
                "horizun_cde_cloud"
            };

            // A name in one of those sets that matches no contract is a rename nobody
            // finished, and it fails LOUDLY here rather than handing a client a wrong hint
            // for the tool that was renamed. This is the rot the sets are prone to.
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (CommandContract c in all) known.Add(c.Name);
            foreach (string n in destructive)
                if (!known.Contains(n))
                    throw new InvalidOperationException(
                        "destructive names a tool that does not exist: '" + n + "'. Renamed or removed? " +
                        "Fix the set - a stale entry means the tool it replaced now reports destructiveHint=false.");
            foreach (string n in openWorld)
                if (!known.Contains(n))
                    throw new InvalidOperationException(
                        "openWorld names a tool that does not exist: '" + n + "'.");

            // The EFFECT sets get the same guard, and they need it more. A stale name in
            // `destructive` costs a wrong hint; a stale name in one of these falls through
            // every branch to `else c.Effect = ToolEffect.ReadOnly` - so a mistyped entry
            // does not merely lose a classification, it hands the tool the ONE effect that
            // read_only admits. Silent, and in the permissive direction.
            foreach (var set in new[]
                     {
                         new KeyValuePair<string, HashSet<string>>("always (Mutating)", always),
                         new KeyValuePair<string, HashSet<string>>("dryRun (MutatingUnlessDryRun)", dryRun),
                         new KeyValuePair<string, HashSet<string>>("external (ExternalSideEffect)", external),
                         new KeyValuePair<string, HashSet<string>>(
                             "externalOnRequest (ExternalSideEffectOnRequest)", externalOnRequest),
                         new KeyValuePair<string, HashSet<string>>("hostState (HostState)", hostState)
                     })
                foreach (string n in set.Value)
                    if (!known.Contains(n))
                        throw new InvalidOperationException(
                            "The effect set " + set.Key + " names a tool that does not exist: '" + n +
                            "'. Renamed or removed? Fix the set - an entry that matches nothing falls " +
                            "through to ToolEffect.ReadOnly, which is the one effect permission_profile=" +
                            "read_only allows.");

            // And the other direction: the sets must not overlap, or the if/else chain
            // silently picks whichever branch comes first.
            foreach (string n in external)
                if (hostState.Contains(n) || externalOnRequest.Contains(n))
                    throw new InvalidOperationException(
                        "'" + n + "' is in external AND in one of hostState / externalOnRequest. One of them " +
                        "decides its effect and the other is a lie about what it does; pick one deliberately.");
            foreach (string n in externalOnRequest)
                if (hostState.Contains(n))
                    throw new InvalidOperationException(
                        "'" + n + "' is in BOTH externalOnRequest and hostState. One of them decides its effect " +
                        "and the other is a lie about what it does; pick one deliberately.");

            foreach (CommandContract c in all)
            {
                c.OutputSchema = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = true
                };
                if (always.Contains(c.Name)) c.Effect = ToolEffect.Mutating;
                else if (dryRun.Contains(c.Name)) c.Effect = ToolEffect.MutatingUnlessDryRun;
                else if (c.Name == "horizun_document_session") c.Effect = ToolEffect.DocumentSession;
                else if (external.Contains(c.Name)) c.Effect = ToolEffect.ExternalSideEffect;
                else if (externalOnRequest.Contains(c.Name)) c.Effect = ToolEffect.ExternalSideEffectOnRequest;
                else if (hostState.Contains(c.Name)) c.Effect = ToolEffect.HostState;
                else c.Effect = ToolEffect.ReadOnly;

                c.Destructive = destructive.Contains(c.Name);
                // HostState is listed here so the MCP annotations do not change when the
                // classification split: navigate and target reported openWorldHint=true
                // under their old effect and still describe something outside this process.
                // The WORST case, deliberately. A tool that can reach outside the model
                // when asked is open-world whether or not this particular call asks:
                // openWorldHint tells a client what the tool is, and the per-call
                // enforcement is what decides what a given call may do.
                c.OpenWorld = openWorld.Contains(c.Name) ||
                              c.Effect == ToolEffect.ExternalSideEffect ||
                              c.Effect == ToolEffect.ExternalSideEffectOnRequest ||
                              c.Effect == ToolEffect.HostState ||
                              c.Effect == ToolEffect.DocumentSession;

                if (c.Effect == ToolEffect.Mutating ||
                    c.Effect == ToolEffect.MutatingUnlessDryRun ||
                    c.Effect == ToolEffect.DocumentSession)
                {
                    JObject properties = c.InputSchema?["properties"] as JObject;
                    if (properties == null)
                    {
                        properties = new JObject();
                        c.InputSchema["properties"] = properties;
                    }
                    if (properties["idempotency_key"] == null)
                        properties["idempotency_key"] = new JObject
                        {
                            ["type"] = "string",
                            ["minLength"] = 1,
                            ["maxLength"] = 200,
                            ["description"] =
                                "REQUIRED whenever this call will mutate or change the Revit session. A retry " +
                                "with the same key and identical operation returns the recorded result without " +
                                "executing twice. Reusing it for different arguments is refused. Generate a new " +
                                "UUID for each deliberate operation; keep it unchanged only for retries."
                        };
                }
            }
            BatchSessionRules.AddSchema(all.First(c => c.Name == "horizun_document_session").InputSchema,
                all.First(c => c.Name == "horizun_execute_plan").InputSchema);
            SessionExitRules.AddSchema(all.First(c => c.Name == "horizun_document_session").InputSchema);
            ToolInputRules.AddSessionVariants(all.First(c => c.Name == "horizun_document_session").InputSchema);
            ToolInputRules.AddCreationVariants(all.First(c => c.Name == "horizun_create_elements").InputSchema);
            AddConditionalRequirements(all);
            all.First(c => c.Name == "horizun_create_elements").InputSchema["properties"]["validation_mode"] = new JObject
            { ["type"] = "string", ["enum"] = new JArray("arguments", "revit_rollback"), ["default"] = "arguments",
              ["description"] = "dry_run validation depth. arguments opens no transaction. revit_rollback creates provisionally, commits internally, verifies geometry, then rolls back the whole group and verifies IDs absent. Never saves a file." };
            all.First(c => c.Name == "horizun_create_elements").InputSchema["properties"]["validation_mode"]["description"] =
                "Oblique pipe tees rehearse in Revit and bind the built wye type. revit_rollback also builds the rest provisionally, verifies after an inner commit and proves the rollback. Never saves a file.";
            all.First(c => c.Name == "horizun_execute_python").InputSchema["properties"]["scripts_root"] = new JObject
            {
                ["type"] = "string", ["description"] = "Optional absolute script directory. code_path must then be relative and stay inside it. Source is frozen and hashed before durable idempotency admission; changed content under the same key is refused."
            };
            var pythonProps = (JObject)all.First(c => c.Name == "horizun_execute_python").InputSchema["properties"];
            // The add-in has read it since field 6.x; undeclared, it was reachable only by a client
            // that ignored additionalProperties:false, and the server now refuses undeclared arguments.
            pythonProps["read_only"] = new JObject { ["type"] = "boolean", ["default"] = false,
                ["description"] = "Run inside a TransactionGroup that is always rolled back, and prove the model did not move. Save/SaveAs/Close/Open calls are refused before the run by a textual scan, which is not a sandbox." };
            pythonProps["documentation_only"] = new JObject { ["type"] = "boolean", ["default"] = false,
                ["description"] = "Run inside a TransactionGroup that is KEPT only if Revit's account of the run (created, modified, deleted) touches views, sheets, annotation and elements a view owns - the set permission_profile=documentation allows the typed tools. Anything else (a wall moved, a type, family, material, parameter or Object Style edited, a deletion outside the pre-run documentation snapshot, a change in another document) rolls the whole run back, re-reads it, and fails with error_code documentacion_violada and documentation_only_check.offending_ids. Uncertainty refuses. Not combinable with read_only, rehearsal or run_async; Save/SaveAs/Close/Open calls are refused before the run by a textual scan, which is not a sandbox." };
            pythonProps["code"]["description"] = ((string)pythonProps["code"]["description"] ?? "") + " To read family parameters and values by type use horizun_query_family_internals (Pro edition) operation=parameter_census.";
            pythonProps["rehearsal"] = new JObject { ["type"] = "boolean", ["default"] = false,
                ["description"] = "read_only plus Revit's record of what changed, by category; rolled back and re-read." };
            pythonProps["includes"] = new JObject { ["type"] = "array", ["maxItems"] = 16, ["items"] = new JObject { ["type"] = "string" },
                ["description"] = "Local Python paths executed in order before the main script, in the same scope. Main plus includes share the source size limit and durable snapshot. Relative to scripts_root when supplied. Injected horizun v1 provides transaction(), report() and out_reference(type)." };
            pythonProps["response_mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("compact", "full"), ["default"] = "compact",
                ["description"] = "compact removes repeated contract prose, preserving errors, evidence and observation coverage. Python remains self-reported; host ID observations do not verify geometry or script authorship." };
            pythonProps["response"] = new JObject { ["type"] = "string", ["enum"] = new JArray("normal", "output_only"), ["default"] = "normal",
                ["description"] = "output_only returns only the script's __output__ in an output field on success. Oversize output retains truncation details; errors retain their diagnostics. Python output remains self-reported." };
            pythonProps["timeout_s"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["maximum"] = 86400,
                ["description"] = "Cooperative seconds from script start; requires a checkpoint to stop." };
            pythonProps["deadline_s"] = new JObject { ["type"] = "number", ["minimum"] = 1, ["maximum"] = 86400,
                ["description"] = "Cooperative seconds from script start; must not exceed timeout_s." };
            pythonProps["max_output_chars"] = new JObject { ["type"]=new JArray("integer","null"),["minimum"]=1024,["maximum"]=MaxScriptTextChars,
                ["description"]="Page size and limit for the serialized __output__ (omitted: 20000). Longer output is saved whole: page 1 comes back as TEXT with output_paging; the rest via horizun_job_status job_id=<id> output_page=N." };
            var captureProps=(JObject)all.First(c=>c.Name=="horizun_capture_view").InputSchema["properties"];
            captureProps["target_document"]=new JObject { ["type"]="string",["description"]="Required when using temporary view options; must identify the active document." };
            captureProps["element_ids"]=new JObject { ["type"]="array",["minItems"]=1,["maxItems"]=2000,["items"]=new JObject { ["type"]="integer" },["description"]="Frame these model elements, using crop/section box temporarily. Restores and verifies the view after export, including on failure." };
            captureProps["margin_ratio"]=new JObject { ["type"]="number",["minimum"]=0,["maximum"]=1,["default"]=0.08 };
            captureProps["hide_category_ids"]=new JObject { ["type"]="array",["maxItems"]=256,["items"]=new JObject { ["type"]="integer" } };
            captureProps["hide_annotations"]=new JObject { ["type"]="boolean",["default"]=false };
            captureProps["calibrate_world_to_pixel"]=new JObject { ["type"]="boolean",["default"]=false,["description"]="Plans/sections with rectangular unsplit active crop only, requires hide_annotations=true; max 4096 pixels per axis. Creates temporary colored anchors in a separate export, fits from three anchors and checks three independent anchors within 1.5 pixels, verifies background stability, then rolls back. Returns the clean PNG and measured affine map; fails explicitly when calibration cannot be proven." };
            captureProps["display_style"]=new JObject { ["type"]="string",["description"]="shaded, shaded_with_edges, hlr, wireframe, consistent_colors, realistic (Revit DisplayStyle names also work). The reply says if it was applied and the camera and style the image shows." };
            captureProps["orientation"]=new JObject { ["type"]="string",["enum"]=new JArray("top","front","right","isometric"),["description"]="Temporary orientation, orthographic 3D only. Spatial output contains measured model crop and axes; exact world-to-pixel mapping remains explicitly uncalibrated." };


            // TOOLSETS. A row that names a tool that does not exist, or a toolset that does
            // not exist, is a rename nobody finished and fails here, loudly, like every other
            // set in this method. A tool with NO row is left with an empty list - visible only
            // when every toolset is selected - and ToolsetTests fails the build for it, so a
            // tool added in a parallel branch cannot crash a server at startup.
            foreach (string problem in ToolsetCatalog.Audit(all.Select(c => c.Name)))
                if (!problem.Contains("declares no toolset"))
                    throw new InvalidOperationException("Toolset map: " + problem + ".");

            // Host-resident tools whose reply carries text read from a file or from a job
            // the add-in ran. Plugin-forwarded tools carry model text by construction.
            var hostExternalContent = new HashSet<string>(StringComparer.Ordinal)
            {
                // Read project-context.json and CDE folder/sidecar contents written by people.
                "horizun_project_context", "horizun_information_container",
                // A template kit manifest: generated, then possibly edited by people.
                "horizun_template_kit",
                // Manifests, registries and review plans written by other apps and people.
                "horizun_exchange",
                "horizun_job_status", "horizun_excel_read_rows", "horizun_budget_compare",
                // Reads the text of a PDF or of a DWG's entities: drawing text written by people.
                "horizun_read_schedule",
                "horizun_catalog_lookup", "horizun_selection_exchange", "horizun_power_bi_push",
                "horizun_run_procedure", "horizun_excel_write_rows",
                // File, folder and document names read from a cloud CDE, written by people.
                "horizun_cde_cloud"
            };
            foreach (string n in hostExternalContent)
                if (!known.Contains(n))
                    throw new InvalidOperationException(
                        "hostExternalContent names a tool that does not exist: '" + n + "'.");

            foreach (CommandContract c in all)
            {
                c.Toolsets = ToolsetCatalog.Of(c.Name);
                c.ExternalContent = !string.IsNullOrEmpty(c.Command) || hostExternalContent.Contains(c.Name);
            }
            return all;
        }

        /// <summary>
        /// A fingerprint of every contract above: names, forwarding targets, descriptions
        /// and schemas. Two builds with the same hash agree about what every command takes.
        /// Two with different hashes do not, and that is worth refusing over rather than
        /// discovering when an argument is silently ignored.
        /// </summary>
        public static string Hash { get; } = ComputeHash();

        private static string ComputeHash()
        {
            var sb = new StringBuilder();
            sb.Append("protocol=").Append(ProtocolVersion).Append((char)30);
            // The wire limits are part of the agreement. Two halves that disagree about
            // how big a reply may be will one day meet a reply that one of them will not
            // send and the other was waiting for.
            sb.Append("limits=").Append(MaxRequestBytes).Append(',').Append(MaxReplyBytes)
              .Append(',').Append(MaxScriptTextChars).Append((char)30);
            foreach (CommandContract c in All.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                sb.Append(c.Name).Append((char)31);
                sb.Append(c.Command ?? "-").Append((char)31);
                sb.Append(c.Description ?? "").Append((char)31);
                sb.Append(c.Effect.ToString()).Append((char)31);
                // Canonical form, so whitespace in a schema literal is not a contract change.
                sb.Append(c.InputSchema == null ? "-" : c.InputSchema.ToString(Newtonsoft.Json.Formatting.None)).Append((char)31);
                sb.Append(c.OutputSchema == null ? "-" : c.OutputSchema.ToString(Newtonsoft.Json.Formatting.None));
                sb.Append((char)30);
            }
            // The identity guard's coverage is part of the agreement (R2S m-5).
            sb.Append(IdentityCoverage.Summary());
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())), 0, 12)
                                   .Replace("-", "").ToLowerInvariant();
        }

        /// <summary>The contract for a tool name, or null.</summary>
        public static CommandContract Find(string name)
        {
            foreach (CommandContract c in All)
                if (string.Equals(c.Name, name, StringComparison.Ordinal)) return c;
            return null;
        }

        /// <summary>Every plugin command a contract names. What the add-in must register.</summary>
        public static IEnumerable<string> PluginCommands =>
            All.Where(c => !string.IsNullOrEmpty(c.Command)).Select(c => c.Command);
    }
    /// <summary>
    /// THE IDENTITY-GUARD COVERAGE as DATA, kept in the contract assembly so that it joins <see cref="Contract.Hash"/>: a server and an
    /// add-in that disagree about which tools verify an identity, or which operations write nothing, are different builds
    /// and must not be mixed (review of R2R, m-5). ElementIdentityGuard reads these tables; it owns no copy.
    /// </summary>
    public static class IdentityCoverage
    {
        /// <summary>Tools whose identity fields are verified against the active document.</summary>
        private static readonly string[] VerifiedArray =
        {
            "horizun_write_params_verified", "horizun_delete_verified", "horizun_set_keynote",
            "horizun_transform_elements", "horizun_execute_plan",
            "horizun_annotate", "horizun_manage_views", "horizun_fix_planimetry", "horizun_edit_dimensions",
            "horizun_detail_2d", "horizun_manage_groups",
            "horizun_create_elements", "horizun_manage_curtain", "horizun_slab_shape", "horizun_manage_parameters",
            "horizun_framing", "horizun_create_railing", "horizun_view_crop", "horizun_view_visibility",
            "horizun_wall_sweep", "horizun_join_geometry", "horizun_wall_finish",
            "horizun_manage_worksets", "horizun_manage_phases",
            "horizun_manage_assemblies_parts", "horizun_apply_corrections",
            "horizun_split_floor_loops", "horizun_split_by_level", "horizun_split_multilayer_walls",
            "horizun_split_multilayer_slabs", "horizun_ungroup_and_mark", "horizun_copy_slab_elevations",
            "horizun_embed_floors_in_toposolid", "horizun_grade_toposolid_around_floors",
            "horizun_rectangularize_walls",
            "horizun_sheet_layout", "horizun_planimetry_ops", "horizun_room_finishes",
            "horizun_pack_sheets", "horizun_manage_revisions", "horizun_manage_schedules",
            "horizun_structural_connections",
            "horizun_manage_materials", "horizun_link_schedule",
            "horizun_capture_view", "horizun_export",
            "horizun_model_diff", "horizun_manage_styles"
        };

        /// <summary>Operations of a covered tool that read and write nothing: an identity there protects nothing and is refused.</summary>
        private static readonly KeyValuePair<string, string[]>[] ReadArray =
        {
            new KeyValuePair<string, string[]>("horizun_sheet_layout", new[] { "get_viewport_box" }),
            new KeyValuePair<string, string[]>("horizun_wall_finish", new[] { "match_core" }),
            new KeyValuePair<string, string[]>("horizun_join_geometry", new[] { "read" }),
            new KeyValuePair<string, string[]>("horizun_manage_curtain", new[] { "read" }),
            new KeyValuePair<string, string[]>("horizun_slab_shape", new[] { "read" }),
            new KeyValuePair<string, string[]>("horizun_view_crop", new[] { "read", "read_section_box" }),
            new KeyValuePair<string, string[]>("horizun_view_visibility", new[] { "read", "explain_overrides", "override_report", "graphics_report" }),
            new KeyValuePair<string, string[]>("horizun_wall_sweep", new[] { "read" }),
            new KeyValuePair<string, string[]>("horizun_manage_groups", new[] { "list", "find_duplicates", "annotation_report", "check_variation" }),
            new KeyValuePair<string, string[]>("horizun_manage_styles", new[] { "list_object_styles", "list_line_styles", "list_line_patterns", "list_fill_patterns", "list_color_schemes" })
        };

        /// <summary>horizun_capture_view writes (a temporary isolation, then undone) only when one of these options is sent.</summary>
        private static readonly string[] CaptureArray =
            { "element_ids", "margin_ratio", "hide_category_ids", "hide_annotations", "display_style", "orientation", "calibrate_world_to_pixel" };

        /// <summary>Covered tools every call of which deletes (or replaces by deleting) the elements it is given: a category-only identity is refused for them.</summary>
        private static readonly string[] AlwaysDeletingArray =
        {
            "horizun_delete_verified", "horizun_split_floor_loops", "horizun_split_multilayer_slabs", "horizun_split_by_level",
            "horizun_rectangularize_walls", "horizun_ungroup_and_mark"
        };

        /// <summary>
        /// A node of a covered tool whose operation / action / mode / decision / original_wall is (or contains, between underscores) one
        /// of these words deletes: a category-only identity is refused there. Deliberately wide: refusing a weak identity on an
        /// operation that merely detaches or removes a member is cheap; letting one through on a deletion is not.
        /// </summary>
        private static readonly string[] DeleteWordsArray = { "delete", "remove", "purge", "erase", "ungroup" };

        /// <summary>The keys of a node whose string value names the operation (checked against <see cref="DeleteWords"/>).</summary>
        private static readonly string[] OperationKeysArray = { "operation", "action", "mode", "decision", "original_wall" };

        /// <summary>
        /// Operations that DELETE (or replace by deleting) an element the caller named, and whose name carries none of <see cref="DeleteWords"/>
        /// (R2T M-1). Matched whole, lower-case, against the operation keys of the node. Swept from every doc.Delete / d.Delete of the commands
        /// of the covered tools; a test classifies every deleting command file.
        /// </summary>
        private static readonly KeyValuePair<string, string[]>[] DeletingOperationsArray =
        {
            // level_steps deletes the step fitting it levels; replace_junctions deletes the box junction it rebuilds.
            // dissolve_parts deletes the PartMaker (and its parts) of the named element; disassemble deletes the named assembly instance.
            new KeyValuePair<string, string[]>("horizun_manage_assemblies_parts", new[] { "dissolve_parts", "disassemble" }),
            // add_members / edit_type_members regroup: the named group instance is ungrouped and the group TYPE the caller named is deleted and redefined.
            new KeyValuePair<string, string[]>("horizun_manage_groups", new[] { "add_members", "edit_type_members" })
        };

        /// <summary>
        /// A node of the tool that has this dotted path with this text deletes what it is given, whatever its operation is called ("path=value").
        /// framing with spec.wall.method=curtain replaces the carrier wall by a curtain wall and deletes it when the curtain covers it whole.
        /// </summary>
        private static readonly KeyValuePair<string, string[]>[] DeletingConditionsArray =
        {
            new KeyValuePair<string, string[]>("horizun_framing", new[] { "spec.wall.method=curtain" })
        };

        /// <summary>
        /// Operations whose NAME carries a deletion word but which do not delete the element they name (R2U m-6): they detach, un-bind or drop a
        /// definition. Matched whole, lower-case, like <see cref="DeletingOperationsArray"/>; they do not make a node, or its children, a deletion.
        /// manage_schedules remove_fields drops schedule columns; manage_parameters remove_binding un-binds a parameter (the element and the
        /// parameter stay); electrical remove_from_circuit takes a member out of a circuit; mep_routing remove_sizes drops sizes from a routing preference.
        /// </summary>
        private static readonly KeyValuePair<string, string[]>[] NotDeletingArray =
        {
            new KeyValuePair<string, string[]>("horizun_manage_schedules", new[] { "remove_fields" }),
            new KeyValuePair<string, string[]>("horizun_manage_parameters", new[] { "remove_binding" })
        };

        /// <summary>
        /// For a deleting operation of <see cref="DeletingOperationsArray"/>, the id keys it really deletes ("operation=key,key"); the rest of the ids of
        /// the node are not deleted (R2U m-6). manage_groups add_members ungroups the group instance (group_ids) and deletes and redefines its type (type_id);
        /// the elements it adds (element_ids) stay. An operation of that table that is not listed here deletes every id it names.
        /// </summary>
        private static readonly KeyValuePair<string, string[]>[] DeletingKeysArray =
        {
            new KeyValuePair<string, string[]>("horizun_manage_groups", new[] { "add_members=group_ids,type_id" })
        };

        /// <summary>Tools whose identity fields are refused because they take no ids of the active document.</summary>
        private static readonly string[] NoSubjectArray = {  };
        // Tools whose request is a batch of actions[]: an identity at the top level would be consumed without protecting any action.
        private static readonly string[] PerActionArray = { "horizun_annotate", "horizun_edit_dimensions" };

        /// <summary>Misspellings of an identity field (lower-case, no separators) and the name each should have been.</summary>
        private static readonly KeyValuePair<string, string>[] NearNamesArray =
        {
            new KeyValuePair<string, string>("identity", "identity"), new KeyValuePair<string, string>("uniqueid", "unique_id"),
            new KeyValuePair<string, string>("uniqueids", "unique_ids"), new KeyValuePair<string, string>("expectedcategory", "expected_category"),
            new KeyValuePair<string, string>("expectedtype", "expected_type")
        };

        /// <summary>The EXACT paths where a property called identity is text the tool defines itself (who approved, who signed), not an identity map.</summary>
        private static readonly KeyValuePair<string, string[]>[] OwnIdentityTextArray =
        {
            new KeyValuePair<string, string[]>("horizun_plan_views", new[] { "identity" }),
            new KeyValuePair<string, string[]>("horizun_audit_model", new[] { "prevention_gate.override.identity" }),
            new KeyValuePair<string, string[]>("horizun_save_document", new[] { "require_gate.override.identity" }),
            new KeyValuePair<string, string[]>("horizun_export", new[] { "require_gate.override.identity" })
        };

        /// <summary>
        /// The id key a bare unique_id (s:) / unique_ids (l:) is FILLED INTO when the node carries no ids, for each covered tool that has one (a tool
        /// not listed has none). Like <see cref="DocumentedIdKeysArray"/> it is derived from the guard's own code by a test (R2U m-2).
        /// </summary>
        private static readonly KeyValuePair<string, string>[] DocumentedDefaultKeysArray =
        {
            new KeyValuePair<string, string>("horizun_write_params_verified", "s:target_id;l:element_ids"),
            new KeyValuePair<string, string>("horizun_set_keynote", "s:element_id;l:element_ids"),
            new KeyValuePair<string, string>("horizun_transform_elements", "s:element_id;l:element_ids"),
            new KeyValuePair<string, string>("horizun_delete_verified", "s:element_id;l:ids"),
            new KeyValuePair<string, string>("horizun_annotate", "s:element_id,view_id;l:"),
            new KeyValuePair<string, string>("horizun_edit_dimensions", "s:element_id;l:"),
            new KeyValuePair<string, string>("horizun_manage_schedules", "s:schedule_id;l:"),
            new KeyValuePair<string, string>("horizun_manage_styles", "s:scheme_id;l:")
        };

        /// <summary>The names an identity can arrive under. A covered tool accepts them even where its schema does not declare them.</summary>
        private static readonly string[] AcceptedNamesArray = { "identity", "unique_id", "unique_ids", "expected_category", "expected_type" };

        /// <summary>
        /// For each covered tool, the id keys a bare unique_id (s:) and unique_ids (l:) are compared with, first present first.
        /// The guard (ElementIdentityGuard.SpecFor) resolves with its own code; a test proves the two are the same, so a change in one moves
        /// the contract hash through this table (R2T m-1).
        /// </summary>
        private static readonly KeyValuePair<string, string>[] DocumentedIdKeysArray =
        {
            new KeyValuePair<string, string>("horizun_write_params_verified", "s:target_id,element_id,host_id;l:ids,element_ids,host_ids"),
            new KeyValuePair<string, string>("horizun_delete_verified", "s:target_id,element_id,host_id;l:ids,element_ids,host_ids"),
            new KeyValuePair<string, string>("horizun_set_keynote", "s:target_id,element_id,host_id;l:ids,element_ids,host_ids"),
            new KeyValuePair<string, string>("horizun_transform_elements", "s:target_id,element_id,host_id;l:ids,element_ids,host_ids"),
            new KeyValuePair<string, string>("horizun_execute_plan", "s:;l:"),
            new KeyValuePair<string, string>("horizun_annotate", "s:element_id,link_instance_id,view_id;l:element_ids"),
            new KeyValuePair<string, string>("horizun_manage_views", "s:view_id,sheet_id,viewport_id;l:element_ids,view_ids,viewport_ids"),
            new KeyValuePair<string, string>("horizun_fix_planimetry", "s:element_id,viewport_id,schedule_instance_id,sheet_id,view_id;l:element_ids"),
            new KeyValuePair<string, string>("horizun_edit_dimensions", "s:element_id;l:"),
            new KeyValuePair<string, string>("horizun_detail_2d", "s:element_id,view_id;l:element_ids"),
            new KeyValuePair<string, string>("horizun_manage_groups", "s:type_id;l:element_ids,group_ids"),
            new KeyValuePair<string, string>("horizun_create_elements", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_manage_curtain", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_slab_shape", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_manage_parameters", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_framing", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_create_railing", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_view_crop", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_view_visibility", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids,view_ids"),
            new KeyValuePair<string, string>("horizun_wall_sweep", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_join_geometry", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_wall_finish", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_manage_worksets", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids,view_ids"),
            new KeyValuePair<string, string>("horizun_manage_phases", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_manage_assemblies_parts", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_apply_corrections", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_split_floor_loops", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_split_by_level", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_split_multilayer_walls", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_split_multilayer_slabs", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_ungroup_and_mark", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_copy_slab_elevations", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_embed_floors_in_toposolid", "s:toposolid_id;l:element_ids"),
            new KeyValuePair<string, string>("horizun_grade_toposolid_around_floors", "s:toposolid_id;l:element_ids"),
            new KeyValuePair<string, string>("horizun_rectangularize_walls", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_sheet_layout", "s:viewport_id,sheet_id,view_id;l:viewport_ids"),
            new KeyValuePair<string, string>("horizun_planimetry_ops", "s:view_id,source_legend_id,model_tag_id,link_instance_id,element_id,text_note_type_id,schedule_id;l:note_ids,grid_ids,datum_ids,view_ids,viewport_ids"),
            new KeyValuePair<string, string>("horizun_room_finishes", "s:;l:room_ids"),
            new KeyValuePair<string, string>("horizun_pack_sheets", "s:sheet_id,viewport_id,schedule_instance_id,view_id,schedule_id;l:"),
            new KeyValuePair<string, string>("horizun_manage_revisions", "s:revision_id,view_id;l:sheet_ids,remove_sheet_ids"),
            new KeyValuePair<string, string>("horizun_manage_styles", "s:scheme_id,view_id;l:"),
            new KeyValuePair<string, string>("horizun_manage_schedules", "s:schedule_id;l:"),
            new KeyValuePair<string, string>("horizun_structural_connections", "s:;l:member_ids"),
            new KeyValuePair<string, string>("horizun_manage_materials", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_link_schedule", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_capture_view", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids"),
            new KeyValuePair<string, string>("horizun_export", "s:schedule_id,ifc_filter_view_id;l:view_ids,family_ids,nwc_export_element_ids"),
            new KeyValuePair<string, string>("horizun_model_diff", "s:target_id,element_id,host_id,instance_id,view_id,sheet_id;l:ids,element_ids,host_ids,wall_ids,member_ids,room_ids")
        };

        public static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> DeletingOperations { get; } = Frozen(DeletingOperationsArray);
        public static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> DeletingConditions { get; } = Frozen(DeletingConditionsArray);
        public static IReadOnlyList<string> AcceptedNames { get; } = Array.AsReadOnly(AcceptedNamesArray);
        public static IReadOnlyList<KeyValuePair<string, string>> DocumentedIdKeys { get; } = Array.AsReadOnly(DocumentedIdKeysArray);
        public static IReadOnlyList<KeyValuePair<string, string>> DocumentedDefaultKeys { get; } = Array.AsReadOnly(DocumentedDefaultKeysArray);
        public static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> NotDeletingOperations { get; } = Frozen(NotDeletingArray);
        public static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> DeletingKeys { get; } = Frozen(DeletingKeysArray);
        public static IReadOnlyList<string> NoSubjectTools { get; } = Array.AsReadOnly(NoSubjectArray);
        public static IReadOnlyList<string> PerActionTools { get; } = Array.AsReadOnly(PerActionArray);
        public static IReadOnlyList<KeyValuePair<string, string>> NearNames { get; } = Array.AsReadOnly(NearNamesArray);
        public static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> OwnIdentityTextPaths { get; } = Frozen(OwnIdentityTextArray);

        // ---- the tables as READ-ONLY views (R2T m-1): a caller cannot reach the array behind them -------------------------------
        public static IReadOnlyList<string> VerifiedTools { get; } = Array.AsReadOnly(VerifiedArray);
        public static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> ReadOperations { get; } = Frozen(ReadArray);
        public static IReadOnlyList<string> CaptureTemporaryFields { get; } = Array.AsReadOnly(CaptureArray);
        public static IReadOnlyList<string> AlwaysDeletingTools { get; } = Array.AsReadOnly(AlwaysDeletingArray);
        public static IReadOnlyList<string> DeleteWords { get; } = Array.AsReadOnly(DeleteWordsArray);
        public static IReadOnlyList<string> OperationKeys { get; } = Array.AsReadOnly(OperationKeysArray);

        private static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Frozen(KeyValuePair<string, string[]>[] rows) =>
            Array.AsReadOnly(rows.Select(r => new KeyValuePair<string, IReadOnlyList<string>>(r.Key, Array.AsReadOnly(r.Value))).ToArray());

        /// <summary>
        /// A node of a covered tool that carries one of these keys detaches or removes what it names whatever its operation is called
        /// (manage_revisions update_revision remove_sheet_ids takes sheets out of a revision): a category-only identity is refused there.
        /// </summary>
        private static readonly string[] DeleteKeysArray = { "remove_sheet_ids" };

        /// <summary>Read-only view of <c>DeleteKeysArray</c> (R2T m-1: no table is a modifiable array).</summary>
        public static IReadOnlyList<string> DeleteKeys { get; } = Array.AsReadOnly(DeleteKeysArray);

        /// <summary>Canonical text of the whole coverage, for <see cref="Contract.Hash"/>.</summary>
        public static string Summary()
        {
            var sb = new StringBuilder();
            sb.Append("verified=").Append(string.Join(",", VerifiedTools.OrderBy(t => t, StringComparer.Ordinal))).Append((char)30);
            sb.Append("writes_nothing=");
            foreach (KeyValuePair<string, IReadOnlyList<string>> kv in ReadOperations.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append(':').Append(string.Join("|", kv.Value.OrderBy(o => o, StringComparer.Ordinal))).Append(';');
            sb.Append((char)30).Append("capture=").Append(string.Join("|", CaptureTemporaryFields.OrderBy(o => o, StringComparer.Ordinal))).Append((char)30);
            sb.Append("deleting=").Append(string.Join(",", AlwaysDeletingTools.OrderBy(t => t, StringComparer.Ordinal)))
              .Append(';').Append(string.Join(",", DeleteWords)).Append(';').Append(string.Join(",", OperationKeys))
              .Append(';').Append(string.Join(",", DeleteKeys.OrderBy(k => k, StringComparer.Ordinal))).Append((char)30);
            sb.Append("deleting_operations=");
            foreach (KeyValuePair<string, IReadOnlyList<string>> kv in DeletingOperations.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append(':').Append(string.Join("|", kv.Value.OrderBy(o => o, StringComparer.Ordinal))).Append(';');
            sb.Append((char)30).Append("deleting_conditions=");
            foreach (KeyValuePair<string, IReadOnlyList<string>> kv in DeletingConditions.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append(':').Append(string.Join("|", kv.Value.OrderBy(o => o, StringComparer.Ordinal))).Append(';');
            sb.Append((char)30).Append("not_deleting=");
            foreach (KeyValuePair<string, IReadOnlyList<string>> kv in NotDeletingOperations.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append(':').Append(string.Join("|", kv.Value.OrderBy(o => o, StringComparer.Ordinal))).Append(';');
            sb.Append((char)30).Append("deleting_keys=");
            foreach (KeyValuePair<string, IReadOnlyList<string>> kv in DeletingKeys.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append(':').Append(string.Join("|", kv.Value.OrderBy(o => o, StringComparer.Ordinal))).Append(';');
            sb.Append((char)30).Append("no_subject=").Append(string.Join(",", NoSubjectTools.OrderBy(t => t, StringComparer.Ordinal)));
            sb.Append((char)30).Append("per_action=").Append(string.Join(",", PerActionTools.OrderBy(t => t, StringComparer.Ordinal)));
            sb.Append((char)30).Append("near_names=");
            foreach (KeyValuePair<string, string> kv in NearNames.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('>').Append(kv.Value).Append(';');
            sb.Append((char)30).Append("own_identity_text=");
            foreach (KeyValuePair<string, IReadOnlyList<string>> kv in OwnIdentityTextPaths.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append(':').Append(string.Join("|", kv.Value.OrderBy(o => o, StringComparer.Ordinal))).Append(';');
            sb.Append((char)30).Append("default_keys=");
            foreach (KeyValuePair<string, string> kv in DocumentedDefaultKeys.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('/');
            sb.Append((char)30).Append("accepted=").Append(string.Join(",", AcceptedNames)).Append((char)30);
            sb.Append("keys=");
            foreach (KeyValuePair<string, string> kv in DocumentedIdKeys.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('/');
            sb.Append((char)30);
            return sb.ToString();
        }
    }

    /// <summary>Operation-specific input rules shared by schema and handler.</summary>
    public static class ToolInputRules
    {
        /// <summary>Only these save operations rehearse without changing the document.</summary>
        public static bool IsSaveDocumentRehearsal(string tool, JObject request)
        {
            if (tool != "horizun_save_document" || request == null) return false;
            string operation = request.Value<string>("operation");
            if (operation != "relinquish_own" && operation != "sync_with_central") return false;
            return request["dry_run"] == null || request.Value<bool?>("dry_run") == true;
        }

        public static readonly Dictionary<string, string[]> CreationFields = new Dictionary<string, string[]>
        {
            ["door"] = new[] { "point", "host_id", "level_id", "type_id", "type_name", "coordinate_mode", "rotation_degrees", "flip" },
            ["window"] = new[] { "point", "host_id", "level_id", "type_id", "type_name", "coordinate_mode", "rotation_degrees", "flip" },
            ["wall_chain"] = new[] { "points", "closed", "level_id", "type_id", "type_name", "height", "top_level_id", "top_offset", "offset", "base_offset", "flip", "structural", "join_rule" },
            ["fitting"] = new[] { "fitting", "elements", "type_id" },
            ["slab_opening"] = new[] { "host_id", "allow_structural", "shape", "center", "diameter", "width", "height" },
            ["beam_system"] = new[] { "level_id", "profile", "beam_type_id", "direction", "spacing" },
            ["wall_foundation"] = new[] { "wall_id", "type_id", "top_elevation", "width", "thickness" },
            ["accessory_inline"] = new[] { "pipe_id", "point", "type_id" },
            ["mep_system"] = new[] { "name", "system_type_id", "member_element_ids" },
            ["shaft"] = new[] { "base_level_id", "top_level_id", "profile", "allow_structural" },
            ["room_separator"] = new[] { "level_id", "view_id", "profile", "closed" },
            ["level"] = new[] { "elevation", "name" }, ["grid"] = new[] { "start", "end", "name", "extent_3d" },
            ["wall_opening"] = new[] { "start", "end", "corner_1", "corner_2", "host_id", "allow_structural" },
            ["wall_profile"] = new[] { "profile", "level_id", "type_id", "structural" },
            ["displacement"] = new[] { "view_id", "element_ids", "displacement" },
            ["stairs"] = new[] { "level_id", "top_level_id", "top_offset", "type_id", "desired_risers", "tread_depth", "runs", "landings" },
            // join_rule: none disallows BOTH ends, so Revit does not trim the wall
            // back to whatever it meets. Without it a drawing's wall arrives with
            // its ends moved - MEASURED at 1.6 mm - and the postcondition that
            // checks where it runs fails on a wall that is exactly right.
            ["wall"] = new[] { "start", "end", "level_id", "type_id", "type_name", "height", "offset", "base_offset", "top_level_id", "top_offset", "flip", "structural", "arc", "join_rule" },
            ["floor"] = new[] { "profile", "level_id", "type_id", "type_name", "offset", "structural", "slope_degrees", "slope_ratio", "slope_arrow_start", "slope_arrow_end" },
            // A named plane drawn in a plan, elevation or section view; the view's own direction is its cut vector.
            ["reference_plane"] = new[] { "start", "end", "name", "view_id" },
            ["ceiling"] = new[] { "profile", "level_id", "type_id", "type_name", "offset", "slope_degrees", "slope_ratio", "slope_arrow_start", "slope_arrow_end", "expected_arrow_elevations" },
            ["roof"] = new[] { "profile", "level_id", "type_id", "type_name", "offset", "slope_degrees", "slope_ratio", "edge_slopes" },
            ["room"] = new[] { "point", "level_id", "name", "number", "placement", "phase_id", "min_area_m2" },
            // flip: a MIRRORED symbol. No rotation reproduces a reflection, and a
            // drawing distinguishes a left-handed fixture from a right-handed one
            // that way. Applied with flipHand() and verified by re-reading
            // HandFlipped; a family that cannot be flipped refuses the row.
            ["family_instance"] = new[] { "point", "type_id", "level_id", "coordinate_mode", "structural_type", "host_id", "rotation_degrees", "flip", "face_allowance_mm", "facing_degrees", "side_dead_band_mm", "host_face", "top_level_id", "top_offset", "height", "find_host" },
            // A sprinkler is a family_instance restricted to OST_Sprinklers, same routes (level,
            // hosted or face - most sprinklers are ceiling/pipe hosted, so host_id stays). The
            // wall-side fields (facing_degrees/side_dead_band_mm/host_face) are dropped, and
            // host_id's description is overridden below to something terse: the 512 KiB tools/list
            // budget (GeometryProductionCompatibilityTests requires every published kind to have
            // its OWN variant, so this cannot be folded into family_instance's).
            ["sprinkler"] = new[] { "point", "type_id", "level_id", "coordinate_mode", "host_id" },
            ["structural_column"] = new[] { "point", "type_id", "level_id", "coordinate_mode", "rotation_degrees", "top_level_id", "top_offset", "height" },
            ["structural_framing"] = new[] { "start", "end", "type_id", "level_id", "structural_type", "join_rule" },
            ["duct"] = new[] { "start", "end", "type_id", "level_id", "system_type_id", "diameter", "width", "height" },
            ["pipe"] = new[] { "start", "end", "type_id", "level_id", "system_type_id", "diameter" },
            ["conduit"] = new[] { "start", "end", "type_id", "level_id", "diameter" },
            ["cable_tray"] = new[] { "start", "end", "type_id", "level_id" },
            // FlexPipe.Create/FlexDuct.Create take a PATH (points), not a start/end pair.
            ["flex_pipe"] = new[] { "points", "type_id", "level_id", "system_type_id", "diameter" },
            ["flex_duct"] = new[] { "points", "type_id", "level_id", "system_type_id", "diameter", "width", "height" },
            // Space: a 2D point on a level, like room. Area/area_boundary: a POINT/PROFILE
            // in an area plan VIEW, not a level - Revit finds the boundary through the view.
            ["space"] = new[] { "point", "level_id", "placement", "phase_id", "min_area_m2" },
            ["area"] = new[] { "point", "view_id" },
            ["area_boundary"] = new[] { "profile", "view_id" },
            // Toposolid.Create(doc, points, typeId, levelId): the points ARE the top surface (Revit 2024+).
            ["toposolid"] = new[] { "points", "landxml_path", "type_id", "level_id" }
        };
        public static string ValidateCreation(JObject item, string kind) => ValidateCreation(item, kind, out _);

        public static string ValidateCreation(JObject item, string kind, out Tuple<string, string> foreign)
        {
            foreign = ForeignCreationField(item, kind);
            if (foreign != null) return CreationFieldMessage(kind, foreign.Item1, foreign.Item2);
            // Suggest corrections to misspelled fields before reporting a missing coordinate mode.
            if ((kind == "family_instance" || kind == "sprinkler" || kind == "structural_column") && item["coordinate_mode"] == null)
                return "coordinate_mode is required: absolute or level_offset. Legacy ambiguous Z placement is refused.";
            if (!CreationFields.ContainsKey(kind)) return null; // handler owns typed fallback
            if (item["parameters"] != null && !(item["parameters"] is JObject)) return "parameters must be an object.";
            return null;
        }

        public static Tuple<string, string> ForeignCreationField(JObject item, string kind, Func<string, bool> handledElsewhere = null)
        {
            if (!CreationFields.TryGetValue(kind, out var fields)) return null;
            var allowed = new HashSet<string>(fields, StringComparer.Ordinal) { "kind", "parameters", "source_reference", "source_row" };
            var foreign = item.Properties().FirstOrDefault(field => !allowed.Contains(field.Name) &&
                handledElsewhere?.Invoke(field.Name) != true);
            if (foreign == null) return null;
            string name = foreign.Name;
            string suggestion = allowed.Where(f => name.StartsWith(f + "_", StringComparison.OrdinalIgnoreCase) ||
                    f.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Math.Abs(f.Length - name.Length)).ThenBy(f => f, StringComparer.Ordinal).FirstOrDefault();
            if (suggestion == null)
            {
                var near = allowed.Select(f => new { Field = f, Distance = FieldDistance(name, f) })
                    .Where(x => x.Distance <= 2).OrderBy(x => x.Distance).Take(2).ToArray();
                if (near.Length == 1 || (near.Length == 2 && near[0].Distance < near[1].Distance)) suggestion = near[0].Field;
            }
            return Tuple.Create(name, suggestion);
        }

        public static string CreationFieldMessage(string kind, string field, string suggestion) =>
            "'" + field + "' is not applicable to kind '" + kind + "'." +
            (suggestion == null ? "" : " Did you mean '" + suggestion + "'?");

        private static int FieldDistance(string given, string declared)
        {
            if (Math.Abs(given.Length - declared.Length) > 2) return 3;
            var row = Enumerable.Range(0, declared.Length + 1).ToArray();
            for (int i = 0; i < given.Length; i++)
            {
                int diagonal = row[0];
                row[0] = i + 1;
                for (int j = 0; j < declared.Length; j++)
                {
                    int above = row[j + 1];
                    int substitution = char.ToLowerInvariant(given[i]) == char.ToLowerInvariant(declared[j]) ? 0 : 1;
                    row[j + 1] = Math.Min(diagonal + substitution, Math.Min(above, row[j]) + 1);
                    diagonal = above;
                }
            }
            return row[declared.Length];
        }
        internal static void AddCreationVariants(JObject schema)
        {
            var item = (JObject)schema["properties"]["elements"]["items"];
            var props = (JObject)item["properties"]; var variants = new JArray();

            ((JArray)props["kind"]["enum"]).Add("wall_profile");
            ((JArray)props["kind"]["enum"]).Add("displacement");
            ((JArray)props["kind"]["enum"]).Add("stairs");
            ((JArray)props["kind"]["enum"]).Add("toposolid");
            foreach (string architecturalKind in new[] { "door", "window", "wall_chain" })
                ((JArray)props["kind"]["enum"]).Add(architecturalKind);
            props["type_name"] = new JObject { ["type"] = "string", ["minLength"] = 1,
                ["description"] = "Closest loaded architectural type if missing: SUPUESTO. type_id wins." };
            // room_separator already declares closed; wall_chain adds its own sentence instead of replacing it.
            string closedText = props["closed"]?.Value<string>("description");
            props["closed"] = new JObject { ["type"] = "boolean", ["default"] = false,
                ["description"] = (closedText == null ? "" : closedText + " ") + "wall_chain: add a segment from the last point back to the first." };
            props["source_row"] = new JObject { ["type"]="integer", ["minimum"]=1 };
            // JOIN RULE. Declared here because every field named in CreationFields
            // is cloned from these properties: a field in that table with no
            // property here dereferences null and takes the whole contract down at
            // startup - measured, as a Revit that loaded no add-in at all.
            props["extent_3d"] = new JObject
            {
                ["type"] = "string", ["enum"] = new JArray("maximize", "none"), ["default"] = "maximize",
                ["description"] = "kind='grid': maximize (default) makes the grid span every level so it shows in all plans (Maximize 3D Extents); a Z on start/end is ignored. Read back against the levels."
            };
            props["face_allowance_mm"] = new JObject
            {
                ["type"] = "number",
                ["exclusiveMinimum"] = 0,
                ["description"] = "Max distance a work-plane family may sit from its face: a search distance, not a position tolerance."
            };
            props["host_face"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray("side", "end"),
                ["description"] = "family_instance with host_id on a WALL: 'side' (default) = the face the point is in front of; " +
                                  "'end' = the wall's terminal face nearer the point. Joined ends, points outside it and wall-based families are refused."
            };
            props["facing_degrees"] = new JObject
            {
                ["type"] = "number",
                ["description"] = "family_instance on a wall face: the direction the device faces in plan, degrees from +X; " +
                                  "decides the face only when the point is inside the host's thickness. Send with side_dead_band_mm."
            };
            props["side_dead_band_mm"] = new JObject
            {
                ["type"] = "number",
                ["minimum"] = 0,
                ["description"] = "family_instance on a wall face: when present the rotation is not read as the side. A point inside the thickness " +
                                  "takes the face facing_degrees points out of, else its centreline side if further than this; otherwise refused."
            };
            props["join_rule"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray("none", "auto", "butt"),
                ["description"] = "wall or structural_framing: none disallows joins at BOTH ends so Revit keeps the " +
                                  "requested location line; auto retains Revit's normal joins. butt applies to walls only."
            };
            props["view_id"] = new JObject { ["type"]="integer" };
            props["element_ids"] = new JObject { ["type"]="array",["minItems"]=1,["maxItems"]=2000,["items"]=new JObject { ["type"]="integer" } };
            props["displacement"] = props["start"].DeepClone();
            props["points"] = new JObject
            {
                ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 100,
                ["items"] = new JObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = new JObject { ["type"] = "number" } },
                ["description"] = "The flex path; ends included."
            };
            props["landxml_path"] = new JObject { ["type"] = "string" };
            props["desired_risers"]=new JObject { ["type"]="integer",["minimum"]=1,["maximum"]=1000 };
            props["tread_depth"]=new JObject { ["type"]="number",["exclusiveMinimum"]=0 };
            props["runs"]=JObject.Parse(@"{'type':'array','minItems':1,'maxItems':50,'items':{'type':'object','required':['start','end','width','expected_risers'],'properties':{'start':{'type':'array','minItems':3,'maxItems':3,'items':{'type':'number'}},'end':{'type':'array','minItems':3,'maxItems':3,'items':{'type':'number'}},'width':{'type':'number','exclusiveMinimum':0},'expected_risers':{'type':'integer','minimum':1}},'additionalProperties':false}}");
            props["runs"]["description"]="Straight center-justified run paths in absolute XYZ; each horizontal path begins at its run's base elevation. Supply exact expected_risers; Revit path rounding is checked, never silently accepted. Stairs must be the sole element in a batch.";
            props["landings"]=new JObject { ["type"]="array",["maxItems"]=49,["items"]=new JObject { ["type"]="object",["required"]=new JArray("profile"),["properties"]=new JObject { ["profile"]=props["profile"].DeepClone() },["additionalProperties"]=false },["description"]="One explicit horizontal landing contour between runs, no holes. Profile Z gives the absolute landing elevation. No automatic placement guess." };
            props["source_reference"]=JObject.Parse(@"{ 'type':'object', 'required':['document_id','document_sha256','page','method','measurements'],
                'properties': { 'document_id':{'type':'string'}, 'document_sha256':{'type':'string','pattern':'^[a-fA-F0-9]{64}$'}, 'revision':{'type':'string'}, 'page':{'type':'integer','minimum':1},
                'region':{'type':'array','minItems':4,'maxItems':4,'items':{'type':'number'}}, 'method':{'type':'string','enum':['dimension','scaled_measurement','assumption']}, 'assumption':{'type':'string'}, 'confidence':{'type':'number','minimum':0,'maximum':1},
                'measurements':{'type':'array','minItems':1,'maxItems':100,'items':{'type':'object','required':['property','value','unit','tolerance','reference'],'properties':{'property':{'type':'string'},'value':{'type':'number'},'unit':{'type':'string','enum':['mm','m','feet','ratio','degrees','radians']},'tolerance':{'type':'number','minimum':0},'reference':{'type':'string'}},'additionalProperties':false}} }, 'additionalProperties':false }");
            props["source_reference"]["description"]="Optional PDF/source trace stored as ExtensibleStorage on the element (not Comments). Page is one-based; region uses PDF points from top-left. Measurement property names refer to numeric postconditions, e.g. reference_face_elevation or height. Source dimensions are compared with independently measured model values; a mismatch refuses successful application.";
            foreach (var pair in CreationFields)
            {
                // tools/list is budgeted at 512 KiB (McpPrimitiveTests): source_reference alone
                // clones to ~1.2 KB PER VARIANT, so the newest, least PDF-traced kinds skip it
                // (and source_row, which only means anything alongside it) rather than push the
                // whole tool over budget. ValidateCreation still allows both fields by name for
                // every kind - this only affects what the compact, advertised schema documents.
                bool leanKind = pair.Key == "flex_pipe" || pair.Key == "flex_duct" || pair.Key == "sprinkler" ||
                                pair.Key == "space" || pair.Key == "area" || pair.Key == "area_boundary" || pair.Key == "toposolid" || pair.Key == "reference_plane";
                var specific = new JObject { ["kind"] = new JObject { ["const"] = pair.Key } };
                if (!leanKind)
                {
                    specific["parameters"] = props["parameters"].DeepClone();
                    specific["source_reference"] = props["source_reference"].DeepClone();
                    specific["source_row"] = props["source_row"].DeepClone();
                }
                foreach (string field in pair.Value) specific[field] = props[field].DeepClone();
                if (pair.Key == "beam_system") specific["profile"] = JObject.Parse(@"{'type':'array','minItems':3,'maxItems':12,'items':{'type':'array','minItems':2,'maxItems':2,'items':{'type':'number'}}}");
                if (pair.Key == "room_separator" || pair.Key == "area_boundary") specific["profile"]["items"]["minItems"] = 2;
                if(pair.Key=="wall_profile") specific["profile"]["description"]="One simple contour in a vertical plane, absolute internal XYZ. No holes. Revit base normalization is checked against the resulting world-space side-face silhouette.";
                if (pair.Key == "area_boundary") specific["profile"]["description"] = "One open chain; one line per curve.";
                if (pair.Key == "reference_plane")
                {
                    specific["name"] = new JObject { ["type"] = "string" };
                    specific["view_id"] = new JObject { ["type"] = "integer", ["description"] = "Plan, elevation or section view." };
                }
                if (pair.Key == "floor")
                {
                    foreach (string slopeField in new[] { "slope_degrees", "slope_ratio" }) ((JObject)specific[slopeField]).Remove("description");
                    specific["slope_arrow_start"]["description"] = "Sloped floor: low point of the horizontal arrow; needs end and one slope.";
                    ((JObject)specific["slope_arrow_end"]).Remove("description");
                }
                if (pair.Key == "toposolid") specific["landxml_path"]["description"] = "Instead of points: LandXML TIN, shared coords; path#name = surface";
                if (pair.Key == "toposolid") specific["points"]["description"] = "Top surface; absolute internal coords.";
                if (pair.Key == "sprinkler")
                {
                    specific["host_id"]["description"] = "Host for a hosted/face sprinkler.";
                    specific["coordinate_mode"]["description"] = "absolute or level_offset.";
                }
                if (pair.Key == "flex_duct") specific["width"]["description"] = "Rectangular flex duct, with height.";
                if (pair.Key == "flex_pipe" || pair.Key == "flex_duct")
                {
                    specific["system_type_id"]["description"] = "The PipingSystemType/MechanicalSystemType.";
                    ((JObject)specific["diameter"]).Remove("description");
                }
                var required = new JArray("kind");
                string[] requiredFields;
                switch (pair.Key)
                {
                    case "level": requiredFields = new[] { "elevation" }; break;
                    case "grid": requiredFields = new[] { "start", "end" }; break;
                    case "wall": requiredFields = new[] { "start", "end", "level_id" }; break;
                    case "wall_chain": requiredFields = new[] { "points", "level_id" }; break;
                    case "door": case "window": requiredFields = new[] { "point", "host_id" }; break;
                    case "floor": case "ceiling": case "roof": requiredFields = new[] { "profile", "level_id" }; break;
                    case "wall_profile": requiredFields = new[] { "profile", "level_id", "type_id" }; break;
                    case "wall_opening": requiredFields = new[] { "host_id" }; break;
                    case "room": requiredFields = new[] { "level_id" }; break; // a point, or placement all_enclosed
                    case "family_instance": case "sprinkler": requiredFields = new[] { "point", "type_id", "coordinate_mode" }; break;
                    case "structural_column": requiredFields = new[] { "point", "type_id", "level_id", "coordinate_mode" }; break;
                    case "stairs": requiredFields = new[] { "level_id", "top_level_id", "type_id", "desired_risers", "tread_depth", "runs" }; break;
                    case "displacement": requiredFields = new[] { "view_id", "element_ids", "displacement" }; break;
                    case "duct": case "pipe": requiredFields = new[] { "start", "end", "type_id", "level_id", "system_type_id" }; break;
                    case "flex_pipe": case "flex_duct": requiredFields = new[] { "points", "type_id", "level_id", "system_type_id" }; break;
                    case "space": requiredFields = new[] { "level_id" }; break;
                    case "toposolid": requiredFields = new[] { "type_id", "level_id" }; break; // points or landxml_path: the add-in wants exactly one
                    case "area": requiredFields = new[] { "point", "view_id" }; break;
                    case "area_boundary": requiredFields = new[] { "profile", "view_id" }; break;
                    case "cable_tray": requiredFields = new[] { "start", "end", "level_id" }; break;
                    case "fitting": requiredFields = new[] { "fitting", "elements" }; break;
                    case "slab_opening": requiredFields = new[] { "host_id", "center" }; break;
                    case "beam_system": requiredFields = new[] { "level_id", "profile", "beam_type_id", "direction" }; break;
                    case "wall_foundation": requiredFields = new[] { "wall_id", "type_id" }; break;
                    case "accessory_inline": requiredFields = new[] { "pipe_id", "point", "type_id" }; break;
                    case "mep_system": requiredFields = new[] { "name", "system_type_id" }; break;
                    case "shaft": requiredFields = new[] { "base_level_id", "top_level_id", "profile" }; break;
                    case "room_separator": requiredFields = new[] { "level_id", "view_id", "profile" }; break;
                    case "reference_plane": requiredFields = new[] { "start", "end", "name", "view_id" }; break;
                    default: requiredFields = new[] { "start", "end", "type_id", "level_id" }; break;
                }
                foreach (string field in requiredFields) required.Add(field);
                if (specific["point"] != null)
                {
                    bool point2D = pair.Key == "room" || pair.Key == "space" || pair.Key == "area";
                    specific["point"]["minItems"] = point2D ? 2 : 3;
                    specific["point"]["maxItems"] = point2D ? 2 : 3;
                }
                if (pair.Key == "wall_profile") specific["profile"]["maxItems"] = 1;
                var variant = new JObject { ["type"] = "object", ["properties"] = specific, ["required"] = required, ["additionalProperties"] = false };
                if (pair.Key == "room" || pair.Key == "space")
                    variant["allOf"] = JArray.Parse(@"[{'if':{'required':['placement'],'properties':{'placement':{'const':'all_enclosed'}}},'then':{'required':['phase_id'],'not':{'required':['point']}},'else':{'required':['point'],'not':{'anyOf':[{'required':['phase_id']},{'required':['min_area_m2']}]}}}]");
                if (pair.Key == "room_separator")
                    variant["allOf"] = JArray.Parse(@"[{'if':{'properties':{'closed':{'const':true}},'required':['closed']},'then':{'properties':{'profile':{'items':{'minItems':3}}}}}]");
                variants.Add(variant);
            }
            props["profile"] = new JObject { ["anyOf"] = new JArray(props["profile"].DeepClone(),
                JObject.Parse(@"{'type':'array','minItems':3,'items':{'type':'array','minItems':2,'maxItems':2,'items':{'type':'number'}}}")) };
            // Room separators are open chains; their specific schema permits two points.
            ((JObject)props["profile"]["anyOf"][0])["items"]["minItems"] = 2;
            item["oneOf"] = variants;
        }
        // What a sync caller needs that the base descriptions (written for save/close) do not say.
        private static readonly Dictionary<string, string> SyncNotes = new Dictionary<string, string>
        {
            ["confirmation_token"] = "From the estimate, bound to it; single use.",
            ["compact"] = "Compacts central."
        };
        private static readonly Dictionary<string, string[]> SessionFields = new Dictionary<string, string[]>
        {
            ["inspect"] = new[] { "file_path" },
            ["open"] = new[] { "file_path", "cloud_project_guid", "cloud_model_guid", "cloud_region", "expected_version", "allow_upgrade", "audit", "detach", "open_central", "open_all_worksets", "close_workset_names", "on_open_dialog" },
            ["save"] = new[] { "target_document", "file_path", "compact", "force_workshared" },
            ["save_as"] = new[] { "target_document", "file_path", "compact", "force_workshared", "save_as_path", "overwrite", "max_backups" },
            ["close"] = new[] { "target_document", "file_path", "save_on_close", "discard_unsaved", "activate_other", "force_workshared", "confirmation_token" },
            ["sync_with_central"] = new[] { "target_document", "comment", "relinquish", "compact", "confirmation_token" },
            ["new_project"] = new[] { "save_as_path", "template_path", "confirmation_token" },
            ["activate"] = new[] { "target_document", "save_as_path", "confirmation_token" },
            ["batch"] = new[] { "models", "confirmation_token" },
            ["exit_application"] = new[] { "expected_pid", "expected_started_utc", "owner_session", "discard_documents", "confirmation_token" }
        };
        private static HashSet<string> AllowedSession(string operation)
        {
            string[] fields;
            if (!SessionFields.TryGetValue(operation, out fields)) return null;
            var allowed = new HashSet<string>(fields, StringComparer.Ordinal);
            allowed.UnionWith(new[] { "operation", "dry_run", "idempotency_key" });
            return allowed;
        }
        public static string ValidateSession(JObject request, string operation)
        {
            var allowed = AllowedSession(operation);
            if (allowed == null) return "operation must be inspect, open, save, save_as, close, sync_with_central, new_project, activate, batch or exit_application.";
            foreach (var p in request.Properties())
                if (!allowed.Contains(p.Name)) return p.Name + " is not applicable to operation '" + operation + "'. Nothing ran.";
            JToken dry = request["dry_run"];
            if (dry != null && dry.Type != JTokenType.Boolean) return "dry_run must be a boolean.";
            if (operation == "open" && (bool?)dry == true)
                return "open does not support dry_run=true. Use inspect to read local file information without opening.";
            if (request["max_backups"] != null && (request["max_backups"].Type != JTokenType.Integer || (long)request["max_backups"] < 1 || (long)request["max_backups"] > int.MaxValue))
                return "max_backups must be an integer from 1 to 2147483647.";
            if (operation == "batch") return BatchSessionRules.Validate(request);
            if (operation == "exit_application") return SessionExitRules.Validate(request);
            return null;
        }
        internal static void AddSessionVariants(JObject schema)
        {
            var variants = new JArray();
            var properties = (JObject)schema["properties"];
            foreach (var operation in SessionFields.Keys)
            {
                var props = new JObject();
                // sync_with_central's variant carries types without the descriptions the
                // base properties already publish: tools/list is a byte budget.
                bool terse = operation == "sync_with_central";
                foreach (string field in AllowedSession(operation))
                    if (properties[field] != null)
                    {
                        props[field] = properties[field].DeepClone();
                        if (terse) ((JObject)props[field]).Remove("description");
                        if (terse && SyncNotes.TryGetValue(field, out string note)) props[field]["description"] = note;
                    }
                props["operation"] = new JObject { ["const"] = operation };
                if (operation == "open") props["dry_run"] = new JObject { ["const"] = false };
                // A sync previews when dry_run is omitted (DocumentSessionSync.cs), unlike the shared default.
                if (operation == "sync_with_central") props["dry_run"] = new JObject { ["type"] = "boolean", ["default"] = true };
                // new_project rehearses when dry_run is omitted, like a sync (DocumentSessionNewProject.cs).
                if (operation == "new_project") props["dry_run"] = new JObject { ["type"] = "boolean", ["default"] = true };
                if (operation == "activate") props["dry_run"] = new JObject { ["type"] = "boolean", ["default"] = true };
                if (operation == "batch") props["dry_run"] = new JObject { ["type"] = "boolean", ["default"] = true };
                if (operation == "exit_application") props["dry_run"] = new JObject { ["type"] = "boolean", ["default"] = true };
                var required = new JArray("operation");
                if (operation == "save_as" || operation == "new_project") required.Add("save_as_path");
                if (operation == "open") required.Add("expected_version");
                if (operation == "inspect") required.Add("file_path");
                if (operation == "sync_with_central") required.Add("target_document");
                if (operation == "activate") required.Add("target_document");
                if (operation == "batch") required.Add("models");
                if (operation == "exit_application") foreach (string field in new[] { "expected_pid", "expected_started_utc", "owner_session" }) required.Add(field);
                var variant = new JObject { ["type"] = "object", ["properties"] = props, ["required"] = required, ["additionalProperties"] = false };
                if (operation == "save" || operation == "save_as" || operation == "close")
                    variant["anyOf"] = new JArray(new JObject { ["required"] = new JArray("target_document") }, new JObject { ["required"] = new JArray("file_path") });
                variants.Add(variant);
            }
            schema["oneOf"] = variants;
            schema["additionalProperties"] = false;
        }
    }
}
namespace Horizun.Contracts {
    public static class BatchSessionRules
    {
        public static string Validate(JObject request)
        {
            if (!(request["models"] is JArray models) || models.Count < 1 || models.Count > 100)
                return "models must contain 1..100 model objects.";
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in models)
            {
                if (!(token is JObject model)) return "Each model must be an object.";
                foreach (var p in model.Properties())
                    if (!new[] { "file_path", "expected_version", "detach", "audit", "close", "save_as_path", "actions", "reads" }.Contains(p.Name))
                        return "Unknown batch model field: " + p.Name;
                foreach (string field in new[] { "file_path", "expected_version", "close", "save_as_path" })
                    if (model[field] != null && (model[field].Type != JTokenType.String || string.IsNullOrWhiteSpace((string)model[field])))
                        return field + " must be a non-empty string.";
                string path = model.Value<string>("file_path");
                if (!LocalModelPath(path)) return "file_path must be an absolute local .rvt or .rfa path.";
                if (!paths.Add(Normalize(path))) return "Duplicate model source: " + path;
                foreach (string field in new[] { "detach", "audit" })
                    if (model[field] != null && model[field].Type != JTokenType.Boolean) return field + " must be boolean.";
                string close = model.Value<string>("close") ?? "discard";
                if (close != "discard" && close != "save") return "close must be discard or save.";
                string destination = model.Value<string>("save_as_path");
                if (close == "save" && destination != null)
                {
                    if (!LocalModelPath(destination)) return "close=save requires an absolute local save_as_path (.rvt or .rfa).";
                    if (!destinations.Add(Normalize(destination))) return "Duplicate save_as_path.";
                    if (!string.Equals(System.IO.Path.GetExtension(path), System.IO.Path.GetExtension(destination), StringComparison.OrdinalIgnoreCase))
                        return "Source and destination file extensions must match.";
                }
                else if (close != "save" && destination != null) return "save_as_path applies only to close=save.";
                if (model["actions"] != null && (!(model["actions"] is JArray actions) || actions.Count > 100 || actions.Any(a => !(a is JObject))))
                    return "actions must be an array of at most 100 execute_plan actions.";
                if (model["reads"] != null)
                {
                    if (!(model["reads"] is JArray reads) || reads.Count > 100) return "reads must contain at most 100 typed read objects.";
                    foreach (var read in reads)
                    {
                        if (!(read is JObject r) || r["tool"]?.Type != JTokenType.String || !(r["arguments"] is JObject) || r.Properties().Any(p => p.Name != "tool" && p.Name != "arguments"))
                            return "Each read requires only tool and arguments.";
                        var contract = Contract.Find(r.Value<string>("tool"));
                        if (!AdmitsRead(contract, (JObject)r["arguments"])) return "reads admits only typed read operations.";
                    }
                }
            }
            if (paths.Overlaps(destinations)) return "A batch destination cannot overwrite any source.";
            return null;
        }

        public static bool AdmitsRead(CommandContract contract, JObject args) => contract != null &&
            (contract.Effect == ToolEffect.ReadOnly || (contract.Effect == ToolEffect.MutatingUnlessDryRun &&
            IdentityCoverage.ReadOperations.Any(kv => kv.Key == contract.Name && kv.Value.Contains(args?.Value<string>("operation")))));

        public static string DetachDecision(bool workshared, bool? requested, out bool detach)
        {
            detach = requested ?? workshared;
            return workshared && !detach ? "Batch refuses detach=false for central or local workshared files; no connected model was opened." : null;
        }

        public static string SaveDecision(bool workshared, bool detached) => workshared && !detached
            ? "Batch refuses saving a connected workshared model." : null;

        private static string Normalize(string path) => System.IO.Path.GetFullPath(path).TrimEnd('\\').Replace('/', '\\');
        private static bool LocalModelPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 4 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/')) return false;
            try
            {
                string extension = System.IO.Path.GetExtension(path);
                System.IO.Path.GetFullPath(path);
                return extension.Equals(".rvt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".rfa", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void AddSchema(JObject session, JObject plan)
        {
            ((JArray)session["properties"]["operation"]["enum"]).Add("batch");
            session["properties"]["models"] = JObject.Parse(@"{
                'type':'array','minItems':1,'maxItems':100,'items':{
                'type':'object','required':['file_path'],'additionalProperties':false,'properties':{
                'file_path':{'type':'string'},'expected_version':{'type':'string'},
                'detach':{'type':'boolean'},'audit':{'type':'boolean','default':false},
                'close':{'enum':['discard','save'],'default':'discard'},'save_as_path':{'type':'string'},
                'actions':{'type':'array','maxItems':100,'items':{'type':'object'}},
                'reads':{'type':'array','maxItems':100,'items':{'type':'object','additionalProperties':false,'required':['tool','arguments'],
                'properties':{'tool':{'type':'string'},'arguments':{'type':'object'}}}}}}}");
            session["properties"]["models"]["description"] = "batch models; see DOCUMENT-BATCH.md.";
            // A compact action envelope; execute_plan validates each child and its arguments.
            session["properties"]["models"]["items"]["properties"]["actions"]["items"] = JObject.Parse(@"{
                'type':'object','required':['key','tool','arguments'],'additionalProperties':false,
                'properties':{'key':{'type':'string'},'tool':{'type':'string'},'arguments':{'type':'object'}}}");
        }
    }
}

namespace Horizun.Contracts
{
    public static class SessionExitRules
    {
        public static string Validate(JObject r)
        {
            if (r["expected_pid"]?.Type != JTokenType.Integer || (long)r["expected_pid"] < 1 || (long)r["expected_pid"] > int.MaxValue)
                return "expected_pid must name the exact process started by this task.";
            if (r["expected_started_utc"]?.Type != JTokenType.String || !DateTimeOffset.TryParse((string)r["expected_started_utc"], out _))
                return "expected_started_utc must be the process start timestamp.";
            if (r["owner_session"]?.Type != JTokenType.String || !Guid.TryParse((string)r["owner_session"], out _))
                return "owner_session must be the GUID supplied in HORIZUN_OWNER_SESSION when this task launched Revit.";
            if (r["discard_documents"] != null && (!(r["discard_documents"] is JArray discards) || discards.Count > 1000 || discards.Any(t => t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t))))
                return "discard_documents must be an array of document fingerprints from this operation's rehearsal.";
            return null;
        }

        public static string OwnershipError(JObject r, int pid, string startedUtc, string launchOwner)
        {
            string error = Validate(r);
            if (error != null) return error;
            if (string.IsNullOrWhiteSpace(launchOwner) || !string.Equals(launchOwner, r.Value<string>("owner_session"), StringComparison.Ordinal))
                return "This process was not launched with this task's HORIZUN_OWNER_SESSION marker. A running shared Revit cannot be claimed by this command.";
            if (pid != r.Value<int>("expected_pid") || Math.Abs((DateTimeOffset.Parse(startedUtc) - DateTimeOffset.Parse(r.Value<string>("expected_started_utc"))).TotalSeconds) > 1)
                return "Process identity differs from the task's launch record (pid and start timestamp are both required). Nothing was closed.";
            return null;
        }

        public static bool DiscardRequired(bool? modified, JToken pending) => modified != false || pending != null;
        public static bool Ready(JArray states) => states.OfType<JObject>().All(d => !d.Value<bool>("discard_required") || d.Value<bool>("discard_authorized"));
        public static string ForeignDiscard(JArray states, IEnumerable<string> discards) =>
            discards.Any(f => !states.OfType<JObject>().Any(d => d.Value<string>("fingerprint") == f))
            ? "discard_documents names a document not in this process." : null;

        public static void AddSchema(JObject session)
        {
            ((JArray)session["properties"]["operation"]["enum"]).Add("exit_application");
            var props = (JObject)session["properties"];
            props["expected_pid"] = JObject.Parse("{'type':'integer','minimum':1}");
            props["expected_started_utc"] = JObject.Parse("{'type':'string'}");
            props["owner_session"] = JObject.Parse("{'type':'string'}");
            props["discard_documents"] = JObject.Parse("{'type':'array','maxItems':1000,'items':{'type':'string'}}");
            session["description"] = "Owned exit; token; verify termination.";
        }
    }
}
