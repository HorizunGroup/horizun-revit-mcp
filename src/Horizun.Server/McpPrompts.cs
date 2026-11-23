// -----------------------------------------------------------------------------
// Horizun MCP server — standard MCP Prompts.
//
// Prompts contain product operating policy, never an organisation's standards.
// Company catalogues and delivery rules remain arguments/resources supplied by the
// caller, preserving the bridge's organisation-neutral contract.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpPrompts
    {
        public static JObject List(JObject prms)
        {
            RejectCursor(prms);
            return new JObject
            {
                ["prompts"] = new JArray
                {
                    Prompt("deliverable-production", "Produce a verified drawing package",
                        "Plan and execute delivery stages with fresh rehearsal boundaries and explicit visual approval.", Arg("profile", "Project delivery profile with existing view IDs, dimension/tag criteria, paper zones, publication sheets and planimetry requirements.", true)),
                    Prompt("room-documentation", "Rehearse room documentation",
                        "Compose room views and explicit sheet placements without applying them.", Arg("specification", "Explicit rooms, view types, templates, scale, names, margins and optional sheet placements.", true)),
                    Prompt("review-correct-verify", "Rehearse corrections from audit findings",
                        "Preserve finding identity and re-audit evidence.", Arg("selection", "Audit finding IDs, fingerprint and any explicit corrective inputs.", true)),
                    Prompt("health-first", "Start safely in Revit",
                        "Identify the reachable Revit and active document before any operation."),
                    Prompt("verified-change", "Plan a verified model change",
                        "Turn an objective, target set and acceptance criterion into a dry-run-first typed operation.",
                        Arg("objective", "What outcome is required in the model.", true),
                        Arg("applies_to", "Which elements/documents the change applies to.", true),
                        Arg("correct_when", "How the result will be recognised as correct.", true)),
                    Prompt("read-only-audit", "Audit the active model without writes",
                        "Run a coverage-honest audit of the active model and report uncertainty explicitly.",
                        Arg("focus", "Optional audit focus such as health, links, quantities or naming.", false)),
                    Prompt("model-health-audit", "Run a model health audit",
                        "Measure model health, hygiene, links and readiness without changing the model.",
                        Arg("scope", "Optional health focus or model area; omit for the whole active model.", false)),
                    Prompt("sheet-qaqc", "Run sheet QA/QC",
                        "Audit sheets, views, placements and annotations without changing them.",
                        Arg("scope", "Optional sheet numbers, ids or discipline; omit for every non-placeholder sheet.", false)),
                    Prompt("sheet-issue", "Issue a sheet set",
                        "Audit, save locally and export a versioned PDF set with a manifest and a PDF QA.",
                        Arg("scope", "The sheets to issue, by number or id.", false)),
                    Prompt("family-qaqc", "Run family QA/QC",
                        "Inspect family health, in-place content and measurable hygiene without writes.",
                        Arg("scope", "Optional category or explicit family scope; omit for the whole active model.", false)),
                    Prompt("parameter-compliance", "Audit parameter compliance",
                        "Measure supplied parameter requirements and propose—never apply—typed corrections.",
                        Arg("standard", "The approved requirement set or its project reference; required because Horizun never invents standards.", true)),
                    Prompt("room-area-audit", "Audit rooms and areas",
                        "Inspect rooms and areas for placement, enclosure and data completeness without writes.",
                        Arg("scope", "Optional level, phase or department scope; omit for all rooms and areas.", false)),
                    Prompt("quantity-export-pack", "Prepare a quantity and Power BI export pack",
                        "Make a read-only takeoff first; any file or Power BI destination remains a separate approved step.",
                        Arg("scope", "Categories, parameters and grouping required for the takeoff.", true)),
                    Prompt("safe-batch-parameter-update", "Rehearse a safe batch parameter update",
                        "Resolve explicit targets and rehearse a verified parameter change without applying it.",
                        Arg("updates", "Explicit element ids, parameter names and requested values; required.", true)),
                    Prompt("qaqc-report-export", "Export a QA/QC report",
                        "Append only measured QA/QC findings to an approved local workbook; the writer backs up and re-reads every appended cell.",
                        Arg("workbook_path", "Absolute path of the approved .xlsx workbook to receive the report rows.", true),
                        Arg("evidence_source", "The completed audit reply or durable receipt that supplies the measured findings; required to prevent invented report rows.", true)),
                    Prompt("planimetry-review", "Review planimetry directly from Revit",
                        "Audit sheets and documentation from the model, capture the actual sheets and judge visual quality without exporting a PDF.",
                        Arg("scope", "Optional sheet numbers, sheet ids or discipline; omit for every non-placeholder sheet.", false)),
                    // The three procedures added with the 2026-09-15 catalogue. A
                    // procedure whose `prompt` names nothing is a catalogue entry a
                    // client cannot act on, so the two lists move together.
                    Prompt("graphic-review-pack", "Build a coloured review view",
                        "Duplicate a view and colour it by one parameter's values, with a returned legend and a capture.",
                        Arg("source_view_id", "The view to duplicate. The original is never modified.", true),
                        Arg("parameter", "The parameter whose distinct values become colours; it must be filterable for the chosen categories.", true),
                        Arg("categories", "The BuiltInCategory names the colouring applies to.", true)),
                    // The two DWG procedures the catalogue carried with no prompt behind them.
                    Prompt("model-with-template-kit", "Model with an AI-ready Horizun template",
                        "Modelar con una plantilla AI-ready: brief yourself from the template kit, pick every type from its catalog, follow its playbook and close with its checks.",
                        Arg("kit_name", "Installed kit name (Project Information HZ_AI_Kit of the model names it). Optional: without it the kit is found from the active model or listed.", false),
                        Arg("task", "What to model (e.g. 'the ground floor from sheet A-101'). Optional: without it the person is asked.", false)),
                    Prompt("project-intake", "Start a BIM project the ISO 19650 way",
                        "Arrancar un proyecto BIM según ISO 19650: ask only what the project context is missing, with options, and write project-context.json from the answers.",
                        Arg("path", "Absolute path of the project's project-context.json. Optional: without it the intake starts from nothing and the draft is only rehearsed until a path is agreed.", false)),
                    Prompt("framing-from-detail", "Read a wall or ceiling detail into a framing spec",
                        "Turn a wall-type or ceiling detail (an image or a 2-D detail) into a horizun_framing spec, confirm it with the person, then build and verify it.",
                        Arg("kind", "wall or ceiling: which spec the detail fills.", true),
                        Arg("element_ids", "The walls or ceilings the detail applies to, comma-separated. Optional: without them the person is asked.", false)),
                    Prompt("cross-app-coordination", "Coordinate a task across Horizun apps",
                        "The agent is the bus between Revit, Navisworks, MS Project, Power BI and the CDE: peers preflighted, one intake, approval per stage, owner order, three-layer verification, one closing row per hop.",
                        Arg("objective", "WHAT outcome is wanted across the apps, in the person's words.", true),
                        Arg("integrations_path", "Absolute path of the project's project-integrations.json (or of the project-context.json beside it). Optional: without it models are matched by nothing and every hop says so.", false)),
                    Prompt("material-standardisation", "Bring materials to a declared standard",
                        "Create, duplicate and edit materials to match an approved standard, re-reading every value and touching nothing else.",
                        Arg("material_standard", "The approved standard - names, classes, colours, patterns. Required because Horizun carries no organisation's catalogue.", true))
                }
            };
        }

        public static JObject Get(JObject prms)
        {
            string name = RequiredString(prms, "name");
            JObject args = prms?["arguments"] as JObject ?? new JObject();
            if (prms?["arguments"] != null && prms["arguments"].Type != JTokenType.Object)
                throw new McpError(-32602, "Invalid params: prompts/get 'arguments' must be an object of strings.");

            string description;
            string body;
            switch (name)
            {
                case "deliverable-production":
                    description = "Produce drawings through measured, staged delivery.";
                    body = "Call horizun_health first. Profile: " + Argument(args,"profile",true) +
                        ". Use horizun_plan_views operation=deliverable_set with delivery_profile. Create room views first if needed and resolve actual IDs, never guess them. " +
                        "Execute stages in order; each write uses dry_run=true and its own confirmation. Activate each dimension view before rehearsal. " +
                        "Use paper distances for graphical spacing, reference_targets for explicit opposite wall faces, and dimension_set roles for general/partial/opening criteria. " +
                        "Replan tags after dimensions and remeasure packing after annotations. Record receipts and do not blindly replay completed writes. " +
                        "Stop for incomplete coverage, failed rollback, unresolved references, blocking audit findings or missing visual approval. " +
                        "Inspect captures before publication. PDF page counts and hashes verify files, not drawing semantics or visual quality. " +
                        "Never promise transaction rollback for exports or for the complete multi-stage package.";
                    break;
                case "room-documentation":
                    description = "Rehearse atomic room documentation.";
                    body = "Call horizun_health first. Specification: " + Argument(args, "specification", true) +
                        ". Resolve IDs and confirm the intended result before any write. Use horizun_plan_views operation=room_views, then " +
                        "horizun_execute_plan workflow=document_rooms with dry_run=true. Supply room_plan, view_types, view_templates " +
                        "(a compatible template per kind) and optional room_sheets with explicit placements. Reject partial room coverage. " +
                        "Return the rehearsal and confirmation request; do not apply yet. After approved application, reread view scales, templates and placements and inspect sheet captures.";
                    break;
                case "review-correct-verify":
                    description = "Rehearse selected corrections, preserving audit identity.";
                    body = "Call horizun_health first. Selection: " + Argument(args, "selection", true) +
                        ". Resolve the cited horizun_audit_model finding_set_fingerprint and explicit finding IDs. Use horizun_apply_corrections " +
                        "dry_run=true with only the selected targets and required inputs. Destructive corrections require explicit element IDs. " +
                        "Do not apply until confirmed. After application distinguish corrected, persistent, failed and not_verifiable from the re-audit. " +
                        "Corrections are atomic per action, not across the whole selection; do not claim batch-wide rollback.";
                    break;
                case "health-first":
                    description = "Start every Revit task against a measured target.";
                    body =
                        "Call horizun_health first. Confirm the reachable Revit year, process and active document. " +
                        "If more than one Revit is reachable, use horizun_target instead of guessing. Do not write " +
                        "until the objective, target elements and acceptance evidence are unambiguous.";
                    break;
                case "verified-change":
                    string objective = Argument(args, "objective", true);
                    string applies = Argument(args, "applies_to", true);
                    string correct = Argument(args, "correct_when", true);
                    description = "Prepare a typed, dry-run-first and postcondition-verified model change.";
                    body =
                        "Objective: " + objective + "\nApplies to: " + applies + "\nCorrect when: " + correct +
                        "\n\nCall horizun_health first. Choose the narrowest typed Horizun tool that covers the " +
                        "whole objective. Run its default dry-run, inspect the resolved plan and warnings, then use " +
                        "the returned single-use confirmation token without changing the request. After execution, " +
                        "require measured postconditions. Do not retry an uncertain/partial write. Use Python only " +
                        "when fallback.allowed=true and the machine owner has temporarily enabled it in Revit.";
                    break;
                case "read-only-audit":
                    string focus = Argument(args, "focus", false);
                    description = "Audit without changing the model or hiding incomplete coverage.";
                    body =
                        "Call horizun_health, then use read-only typed tools such as horizun_model_scan, " +
                        "horizun_audit_model and horizun_quantities. " +
                        (string.IsNullOrWhiteSpace(focus) ? "Cover the whole model." : "Focus: " + focus + ".") +
                        " Report closed worksets, unloaded links, truncation and unreadable sections as uncertainty; " +
                        "never interpret absence under incomplete coverage as proof that a problem does not exist.";
                    break;
                case "model-health-audit":
                    string healthScope = Argument(args, "scope", false);
                    description = "Measure health and readiness without changing the active model.";
                    body =
                        "Call horizun_health first and confirm the exact active document. Run horizun_model_scan " +
                        "and horizun_audit_model in read-only mode " +
                        (string.IsNullOrWhiteSpace(healthScope) ? "over the whole model." : "for this stated focus: " + healthScope + ".") +
                        " Report warnings, links, CAD, in-place families, groups, worksets, datums, views, rooms " +
                        "and readiness only where they were measured. Keep findings, incomplete coverage and " +
                        "proposed typed corrections separate. Do not write, save, export or fabricate a score.";
                    break;
                case "sheet-qaqc":
                    string sheetScope = Argument(args, "scope", false);
                    description = "Audit sheets and documentation without changing them.";
                    body =
                        "Call horizun_health first. Use horizun_query_planimetry and horizun_audit_planimetry " +
                        "for " + (string.IsNullOrWhiteSpace(sheetScope) ? "every non-placeholder sheet" : "this scope: " + sheetScope) +
                        ". State every required inline requirement_set explicitly; without one, report only the " +
                        "universal checks. Return blocking, advisory and unknown findings with sheet/view ids, " +
                        "and do not apply corrections until a human approves their dry run.";
                    break;
                case "sheet-issue":
                    string issueScope = Argument(args, "scope", false);
                    description = "Issue a sheet set as a versioned PDF.";
                    body =
                        "Call horizun_health first. Audit " + (string.IsNullOrWhiteSpace(issueScope) ? "the sheets to issue" : "these sheets: " + issueScope) +
                        " with horizun_audit_planimetry, save locally, then rehearse horizun_export format=pdf with pdf_version=auto, " +
                        "pdf_archive_previous, emit_manifest and pdf_qa, and apply only with the token of that rehearsal. " +
                        "Before saving, read annotation_staleness on the sheets' views (horizun_query_planimetry) and the own-changes diff (horizun_model_diff compare, origin). " +
                        "Report the audit, the staleness, the diff, the save, the version, the files moved to the archive and the QA findings separately; " +
                        "a QA finding is reported, not hidden.";
                    break;
                case "family-qaqc":
                    string familyScope = Argument(args, "scope", false);
                    description = "Inspect measurable family hygiene without writes.";
                    body =
                        "Call horizun_health, then inspect the active model with horizun_model_scan and " +
                        "horizun_query_model " + (string.IsNullOrWhiteSpace(familyScope) ? "across all relevant categories." : "for this scope: " + familyScope + ".") +
                        " Report in-place families, naming/data facts and every coverage limit. Do not call family " +
                        "creation or modification tools; propose a typed next step only where its target is explicit.";
                    break;
                case "parameter-compliance":
                    string standard = Argument(args, "standard", true);
                    description = "Measure a declared parameter standard; do not invent one or apply changes.";
                    body =
                        "Call horizun_health first. Audit parameter compliance against this approved standard: " + standard +
                        ". Resolve the standard into explicit categories, parameter names and acceptable values " +
                        "before querying the model. Report absent, blank, unreadable and non-compliant values " +
                        "separately. Propose only verified typed parameter updates; do not apply them.";
                    break;
                case "room-area-audit":
                    string roomScope = Argument(args, "scope", false);
                    description = "Inspect room and area placement, enclosure and data completeness without writes.";
                    body =
                        "Call horizun_health first. Use read-only typed model queries and audit tools for " +
                        (string.IsNullOrWhiteSpace(roomScope) ? "all rooms and areas in the active document." : "this scope: " + roomScope + ".") +
                        " Distinguish unplaced, unenclosed, zero-area, blank, duplicate and unreadable conditions. " +
                        "State phases, design options and links that were not measured; do not create or edit rooms.";
                    break;
                case "quantity-export-pack":
                    string quantityScope = Argument(args, "scope", true);
                    description = "Produce a provenance-preserving takeoff before any export or Power BI push.";
                    body =
                        "Call horizun_health, then run horizun_quantities for this required scope: " + quantityScope +
                        ". Keep units, classification, quantity and price separate. Return a read-only takeoff " +
                        "with coverage and provenance first. Do not write a workbook, export a file or push to " +
                        "Power BI until a human supplies and approves the destination under full_write.";
                    break;
                case "safe-batch-parameter-update":
                    string updates = Argument(args, "updates", true);
                    description = "Rehearse a narrowly scoped parameter change; do not apply it.";
                    body =
                        "Call horizun_health first. Resolve and rehearse only these explicit updates: " + updates +
                        ". Use horizun_write_params_verified with dry_run=true. Report each resolved target, " +
                        "missing or read-only parameter, proposed value and confirmation token. Do not apply, widen " +
                        "the target set or retry a partial result.";
                    break;
                case "graphic-review-pack":
                    string sourceViewId = Argument(args, "source_view_id", true);
                    string colourParameter = Argument(args, "parameter", true);
                    string colourCategories = Argument(args, "categories", true);
                    description = "Duplicate a view and colour it by a parameter, with a legend and a capture.";
                    body =
                        "Call horizun_health first. With horizun_manage_views, DUPLICATE view " + sourceViewId +
                        " - never colour the original - and on the duplicate run color_by_value over categories " +
                        colourCategories + " using parameter " + colourParameter + ". If the duplicate carries a view " +
                        "template that governs V/G the command will refuse and name it; duplicate without the template " +
                        "rather than editing somebody's template. Read the returned legend: when palette_wrapped is " +
                        "true, two different values share a colour and the legend is the only way to tell them apart. " +
                        "Finish with horizun_capture_view and report the legend beside the image.";
                    break;
                case "material-standardisation":
                    string materialStandard = Argument(args, "material_standard", true);
                    description = "Bring materials to an approved standard, verified value by value.";
                    body =
                        "Call horizun_health first. Use this approved standard: " + materialStandard +
                        ". Read the existing materials with horizun_query_model before proposing anything. Use " +
                        "horizun_manage_materials as a dry run first, and read its shared_appearance_warning: an " +
                        "appearance asset shared by several materials means a later edit changes all of them, which " +
                        "is why assigning one duplicates it unless sharing is asked for. Do not edit elements that " +
                        "reference these materials, and do not invent a name, class or colour the standard does not " +
                        "state - report what the standard does not cover instead.";
                    break;
                case "qaqc-report-export":
                    string workbookPath = Argument(args, "workbook_path", true);
                    string evidenceSource = Argument(args, "evidence_source", true);
                    description = "Append measured QA/QC findings to an approved workbook and verify the written cells.";
                    body =
                        "The report workbook is: " + workbookPath + "\nEvidence source: " + evidenceSource +
                        "\n\nCall horizun_health first and confirm the active document. Use the supplied completed audit reply or " +
                        "durable receipt as the sole source of report rows; do not infer a score, clean status, requirement, " +
                        "element id or owner that was not measured. Ask for full_write confirmation before calling " +
                        "horizun_excel_write_rows. Append rows only to the approved sheet and use a fresh idempotency_key. " +
                        "The tool creates a .horizunbak backup and re-reads each written cell; report its backup path, " +
                        "sheet_has_table flag and any rows that could not be represented. Do not overwrite, email, upload " +
                        "or claim the workbook is a complete QA/QC report when its evidence coverage is incomplete.";
                    break;
                case "planimetry-review":
                    string scope = Argument(args, "scope", false);
                    description = "Audit and visually review Revit planimetry without a PDF intermediary.";
                    body =
                        "Review planimetry DIRECTLY FROM THE ACTIVE REVIT MODEL; do not export or inspect a PDF. " +
                        "Call horizun_health first. Use horizun_query_planimetry mode=inventory, then sheets, " +
                        "placements and annotations with complete pagination for " +
                        (string.IsNullOrWhiteSpace(scope) ? "every non-placeholder sheet" : "this scope: " + scope) +
                        ". Run horizun_audit_planimetry with the approved inline requirement_set when one exists. " +
                        "For every sheet in scope call horizun_capture_view by sheet view_id and actually inspect " +
                        "the attached PNG. Judge hierarchy, density, alignment, balance, clipping, whitespace, " +
                        "legibility, collisions, orphan marks, missing tags/dimensions and consistency across the " +
                        "set. Cross-reference every visual suspicion with model rows; label subjective findings " +
                        "visual and database findings measured. A failed capture, truncated page or unreadable fact " +
                        "is UNKNOWN, never clean. Return findings by sheet with severity, evidence and element/view " +
                        "ids. Use the narrowest typed correction only after approval and its dry run.";
                    break;
                case "framing-from-detail":
                {
                    string kind = (Argument(args, "kind", true) ?? "").Trim().ToLowerInvariant();
                    if (kind != "wall" && kind != "ceiling")
                        throw new McpError(-32602, "Invalid params: framing-from-detail 'kind' must be wall or ceiling.");
                    string sources = Argument(args, "element_ids", false);
                    bool wall = kind == "wall";
                    description = "Read a " + kind + " detail into a horizun_framing spec, confirm it, build it verified.";
                    body =
                        "Build the " + (wall ? "light-gauge/drywall wall framing" : "suspended drywall ceiling framing") + " a detail shows. " +
                        "YOU read the detail (an attached image or 2-D detail); horizun_framing never reads an image, it builds exactly the spec " +
                        "you give it. 1) Call horizun_health. " +
                        (string.IsNullOrWhiteSpace(sources)
                            ? "Ask the person which " + kind + "s the detail applies to and resolve their ids with horizun_query_model. "
                            : "The detail applies to " + kind + "s " + sources + ": confirm each is a " + (wall ? "straight Basic wall" : "Ceiling") + " with horizun_query_model. ") +
                        "2) Read the detail into spec." + kind + ", every length in MILLIMETRES (convert inches: 3-5/8\" = 92.1 mm, 16\" o.c. = 406.4 mm; " +
                        "a scale bar or a dimension string wins over proportions; never measure pixels). " +
                        (wall
                            ? "Fields: layer ('core' or the compound layer index the studs sit in), stud {type_id, spacing_mm, start wall_start|wall_end|centred, " +
                              "max_first_bay_mm, double_at_ends, width_mm}, track {bottom_type_id, top_type_id or top_same_as_bottom, thickness_mm}, openings " +
                              "{king_studs 1|2, jack_studs, header_type_id, sill_type_id, cripple_spacing_mm, header_depth_mm, sill_depth_mm}, blocking [{height_mm, type_id}]. "
                            : "Fields: main {type_id, spacing_mm, direction short|long|<angle_deg>, depth_mm}, cross {type_id, spacing_mm, depth_mm}, perimeter {type_id, depth_mm}, " +
                              "hanger {type_id, spacing_mm along each main, max_length_mm (default 3000), attach structure_above}, drop_mm (ceiling top face " +
                              "up to the mains' underside, default 0). ") +
                        "If the model already frames with CURTAIN types (a Curtain Wall type whose grid and mullions are the studs and tracks" +
                        (wall ? "" : ", Sloped Glazing roof types as the ceiling layers") +
                        "), propose method 'curtain' instead and ASK which of its types maps to which part of the detail: " +
                        (wall
                            ? "spec.wall {method:'curtain', curtain_type_id, header_type_id, sill_type_id, placeholder_type_id (a thin Basic wall type that keeps " +
                              "the door or window), multi_opening keep_carrier (default; the pieces overlap the kept wall)|refuse, min_segment_mm}. "
                            : "spec.ceiling {method:'curtain', layers [{type_id, offset_mm above the ceiling top, angle_deg -89..89 (a layer across another is a type " +
                              "whose members sit on grid 2, never 90)}], hanger {type_id (a Curtain Wall " +
                              "type), spacing_mm, max_length_mm}}. ") +
                        "3) Types are the person's: list candidate family types with horizun_query_model and let the person choose; a vertical member needs " +
                        "a Structural Columns or line-based Generic Model type, a horizontal one Structural Framing or line-based Generic Model. " +
                        "4) A value the detail does not show or you cannot read is ASKED, with the options the detail allows and what each changes - " +
                        "never filled from a guess or a typical value. 5) Show the complete spec as JSON with, for each value, where in the detail it came from, " +
                        "and get it confirmed. 6) Call horizun_framing operation=" + kind + " with dry_run=true; show counts per role, warnings and the openings it read. " +
                        "7) Only after the person agrees, call again with dry_run=false, the confirmation_token and an idempotency_key; report the postconditions as re-read. " +
                        "A refusal names the field to fix; framing from another spec must be removed (operation=remove) before a new one.";
                    break;
                }
                case "cross-app-coordination":
                {
                    string coordinationObjective = Argument(args, "objective", true);
                    string registry = Argument(args, "integrations_path", false);
                    bool hasRegistry = !string.IsNullOrWhiteSpace(registry);
                    description = "Coordinate one objective across Horizun apps, with the agent as the only bus.";
                    body =
                        "Objective: " + coordinationObjective + ". You, the agent in this client, are the ONLY bus: MCP servers never call each " +
                        "other; you pass manifest PATHS between them (horizun.exchange/0), never large JSON inline. " +
                        "0) PREFLIGHT. Call each peer's health tool that this client exposes - horizun_health (Revit), and the " +
                        "Navisworks, MS Project and Power BI servers' own health/capabilities tools - and list which are reachable. " +
                        "If one app is registered twice (two servers or a plugin plus a server), say so and use neither until the " +
                        "person names one. " +
                        (hasRegistry
                            ? "Read the project registry with horizun_project_context operation=integrations integrations_path=" + registry + ". "
                            : "No project registry was given: ask for project-integrations.json; without it no hop can prove which model it came from. ") +
                        "For every Revit model involved, read get_document_info and resolve its model_identity with " +
                        "horizun_project_context operation=resolve_model; a model with no strong evidence is NOT matched and is " +
                        "reported identity_proven:false (Revit 2023 always is). Print the hops that are possible and the ones that are not. " +
                        "1) INTAKE, ONE ROUND for the whole chain: WHAT outcome, to WHICH elements/models/tasks, and HOW each app's " +
                        "result will be recognised as correct. If any of the three is missing or reads two ways, ask with options " +
                        "and trade-offs. Create one correlation_id for the run. " +
                        "2) APPROVAL PER STAGE: present one envelope per stage (hops, documents, the maximum elements touched) and " +
                        "get a yes for that stage; inside it every write is rehearsed (dry_run=true) and applied with its own token. " +
                        "Approvals expire: Revit tokens after 10 minutes, Power BI plans after 3600 s, a Navisworks fingerprint as " +
                        "soon as anything upstream writes - re-rehearse rather than reuse. " +
                        "3) EXECUTE IN OWNER ORDER: Revit the model, Navisworks the clashes, MS Project dates and progress, Power BI " +
                        "the presentation, the CDE the containers. A downstream hop runs only when horizun_exchange operation=verify " +
                        "on its input says may_continue=true; partial or failed STOPS the chain there. Idempotency is correlation_id + " +
                        "hop + attempt; never resend a call whose outcome is in doubt - read its state first. " +
                        "4) VERIFY IN THREE LAYERS: the producer re-reads what it wrote (its own reply); the consumer checks sha256 and " +
                        "identity (horizun_exchange verify) and re-derives its part; you reconcile the invariants across apps (counts " +
                        "per code equal on both sides, issues = reproduced + not reproduced + untraceable). Reloading a " +
                        "re-exported NWC in Navisworks is MANUAL: ask the person to reload, then re-run the SAME test - 0 active " +
                        "for what was resolved is the closing proof. " +
                        "5) CLOSE: one row per hop - app, manifest path, state (verified | self_reported_verified | partial | failed), " +
                        "counts, evidence - and the overall state is the WEAKEST hop's. A hop through Python is at most " +
                        "self_reported_verified. WHEN NOBODY IS AT THE KEYBOARD, only read and export files: every model write, " +
                        "Navisworks status, Power BI refresh or Project write needs a person's yes.";
                    break;
                }
                case "model-with-template-kit":
                {
                    string kitName = Argument(args, "kit_name", false);
                    string task = Argument(args, "task", false);
                    description = "Model with an AI-ready Horizun template / Modelar con una plantilla AI-ready.";
                    body =
                        "You are modelling in Revit through the Horizun MCP with an AI-READY TEMPLATE: its kit tells you every type, " +
                        "where it inserts, what it needs as host, its code and unit, how this server creates it and how to prove it. " +
                        "1) Call horizun_health (active document, Revit busy?). 2) Brief yourself: horizun_template_kit operation=load " +
                        (string.IsNullOrWhiteSpace(kitName)
                            ? "with model_path = the active document's path; if no kit is found, read Project Information HZ_AI_Kit with horizun_query_model " +
                              "(categories OST_ProjectInformation, return_parameters [HZ_AI_Kit]) and pass that kit_name, or operation=list. "
                            : "kit_name=" + kitName + ". ") +
                        "Read conventions, playbook_tasks, checks and limitations, and check contract_match - if false, rehearse every call. " +
                        "3) The task: " + (string.IsNullOrWhiteSpace(task) ? "ask the person what to model and from which drawings. " : task + ". ") +
                        "Follow horizun_template_kit operation=playbook for each stage in order (levels_and_grids, foundations, columns, beams, slabs, " +
                        "walls, openings, stairs_and_railings, furniture_and_fixtures, rooms, reinforcement, documentation, verify_and_report). " +
                        "4) NEVER invent a type: find it with operation=find (text in Spanish or English, category, host, dims) and use the hit's " +
                        "placement.recipe and resolve call; use rules for choices (walls by use, doors by room, mounting heights, covers). " +
                        "Check the hit's footprint against the clearances before placing. If no type fits, say so and propose duplicating the " +
                        "closest one - ask before creating types. 5) Every write: dry_run=true first, then apply " +
                        "with the token and a new idempotency_key; the reply must be verified_applied. Set HZ_AI_Source (drawing/instruction) " +
                        "and HZ_AI_Status='draft' on what you create. 6) Close with operation=checks: run every check, capture 'HZ AI - 3D Check', " +
                        "read 'HZ AI - Quantities by code', and report counts by category, each check's result, what is NOT done and the " +
                        "verdict usable today / with corrections / not yet. WHEN NOBODY IS AT THE KEYBOARD, rehearse and report; do not apply.";
                    break;
                }
                case "project-intake":
                    string contextPath = Argument(args, "path", false);
                    bool hasPath = !string.IsNullOrWhiteSpace(contextPath);
                    description = "Start a BIM project the ISO 19650 way / Arrancar un proyecto BIM según ISO 19650.";
                    body =
                        "Set up this project's ISO 19650 information-management context. " +
                        (hasPath
                            ? "The project context file is: " + contextPath + ". "
                            : "No project-context.json path was given: agree one with the person before anything is written. ") +
                        "This needs no Revit. 1) Call horizun_project_context operation=questions" +
                        (hasPath ? " with that path" : "") +
                        " and read the ORDERED list of what is still missing: role in the appointment, stage, EIR, BEP " +
                        "(pre- or post-appointment), MIDP/TIDP, responsibility matrix, CDE (platform, root, the WIP/Shared/" +
                        "Published/Archived folders, where work happens today, who approves each transition), naming, " +
                        "classification, LOIN/IDS, georeference/CRS, IFC delivery (version, MVD, Pset mapping) and the Revit " +
                        "version. 2) Ask the person in short blocks, one topic at a time, in their language (every question " +
                        "carries text.es and text.en), offering the question's options and saying why it matters. NEVER answer " +
                        "a question yourself or fill a value from a guess: an unknown stays out of the file and is listed in " +
                        "intake.missing. If a document does not exist, record status=missing - that is an answer, and a finding. " +
                        "IF YOUR CLIENT SUPPORTS MCP ELICITATION, let the server ask instead: call horizun_project_context " +
                        "operation=elicit" + (hasPath ? " with that path" : "") + ", language es or en, dry_run=true - it opens " +
                        "one short form per topic and returns 'answers' plus every question still 'unanswered' with its reason " +
                        "(declined, cancelled, left_blank, not_elicitable...). Ask only those in the chat. If it answers " +
                        "code elicitation_unsupported, the client cannot show forms: ask everything in the chat as above. " +
                        "3) Send the answers to horizun_project_context operation=draft as {pointer: value} with dry_run=true, " +
                        "show the person the state (invalid / inconsistent / incomplete / complete) and every coherence " +
                        "finding, and get it confirmed. 4) Only then call draft with dry_run=false" +
                        (hasPath ? "" : " and the agreed path") +
                        "; it re-reads the file before reporting it written and refuses to replace an existing file " +
                        "without overwrite=true. Writing needs the full_write profile; under a stricter profile, hand the " +
                        "drafted document to the person instead of editing settings. 5) Finish with operation=validate " +
                        "and report what is still missing as missing, never as done.";
                    // Not a model workflow: the model-query efficiency note appended below does not apply.
                    return new JObject
                    {
                        ["description"] = description,
                        ["messages"] = new JArray
                        {
                            new JObject
                            {
                                ["role"] = "user",
                                ["content"] = new JObject { ["type"] = "text", ["text"] = body }
                            }
                        }
                    };
                default: throw new McpError(-32602, "Unknown Horizun prompt: '" + name + "'.");
            }

            body += "\n\nEfficiency: query summary first, then compact scoped details; model_scan summary preserves coverage but samples inventories. " +
                "Use cache_mode=bypass for verification. Never interpret missing coverage as a clean model. " +
                "Use typed rehearsed actions and independently reread the affected scope before declaring completion.";
            return new JObject
            {
                ["description"] = description,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JObject { ["type"] = "text", ["text"] = body }
                    }
                }
            };
        }

        private static JObject Prompt(string name, string title, string description, params JObject[] args)
        {
            var result = new JObject { ["name"] = name, ["title"] = title, ["description"] = description };
            if (args != null && args.Length > 0) result["arguments"] = new JArray(args);
            return result;
        }

        private static JObject Arg(string name, string description, bool required) => new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["required"] = required
        };

        private static string RequiredString(JObject o, string key)
        {
            JToken t = o?[key];
            if (t == null || t.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)t))
                throw new McpError(-32602, "Invalid params: prompts/get requires a non-empty string '" + key + "'.");
            return (string)t;
        }

        private static string Argument(JObject args, string name, bool required)
        {
            JToken t = args?[name];
            if (t == null)
            {
                if (required) throw new McpError(-32602, "Missing required prompt argument '" + name + "'.");
                return null;
            }
            if (t.Type != JTokenType.String)
                throw new McpError(-32602, "Prompt argument '" + name + "' must be a string.");
            string value = (string)t;
            if (required && string.IsNullOrWhiteSpace(value))
                throw new McpError(-32602, "Prompt argument '" + name + "' cannot be empty.");
            return value;
        }

        private static void RejectCursor(JObject prms)
        {
            JToken cursor = prms?["cursor"];
            if (cursor != null && cursor.Type != JTokenType.Null)
                throw new McpError(-32602,
                    "prompts/list has one bounded page and does not accept a cursor. Omit params.cursor.");
        }
    }
}
