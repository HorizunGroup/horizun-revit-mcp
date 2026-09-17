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
                    Prompt("family-recipe", "Rehearse a family recipe",
                        "Compile a height-parametric rectangular prism or tube recipe.", Arg("specification", "Explicit template/output paths, units, dimensions, height parameter, distinct types and load policy.", true)),
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
                    Prompt("dwg-to-bim-review", "Plan DWG to BIM safely",
                        "Read the selected drawing and requirement set, returning a plan and ambiguities without building anything.",
                        Arg("requirement_set", "Approved DWG-to-BIM requirement set to use; required because layer meaning is never guessed.", true)),
                    Prompt("safe-batch-parameter-update", "Rehearse a safe batch parameter update",
                        "Resolve explicit targets and rehearse a verified parameter change without applying it.",
                        Arg("updates", "Explicit element ids, parameter names and requested values; required.", true)),
                    Prompt("architecture-structure-coordination", "Review architecture / structure coordination",
                        "Measure the stated architecture/structure coordination scope and clashes without changing either model.",
                        Arg("scope", "Explicit disciplines, categories, links or levels to coordinate; omit only to inspect the active model's measurable scope.", false)),
                    Prompt("mep-coordination-review", "Review MEP coordination",
                        "Inspect MEP coordination and return only a non-writing routing proposal where applicable.",
                        Arg("scope", "Explicit systems, disciplines, links or levels to inspect; omit for the active model's measurable MEP scope.", false)),
                    Prompt("western-forms-concrete-review", "Review Western Forms / concrete",
                        "Audit the supplied concrete and reinforcement requirements without creating or modifying reinforcement.",
                        Arg("requirement_set", "Approved concrete, reinforcement or formwork requirement set; required because Horizun does not invent engineering rules.", true)),
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
                    Prompt("mep-network-completion", "Complete an MEP network",
                        "Join MEP segments that merely touch into a connected network, with every precondition measured and every connection re-read.",
                        Arg("scope", "The systems, levels or element ids whose open connectors are in scope; required because 'connect everything' is not a scope.", true),
                        Arg("tolerance_mm", "How far apart two connectors may be and still be joined. Default 1 mm; geometry is never moved to close a gap.", false)),
                    Prompt("graphic-review-pack", "Build a coloured review view",
                        "Duplicate a view and colour it by one parameter's values, with a returned legend and a capture.",
                        Arg("source_view_id", "The view to duplicate. The original is never modified.", true),
                        Arg("parameter", "The parameter whose distinct values become colours; it must be filterable for the chosen categories.", true),
                        Arg("categories", "The BuiltInCategory names the colouring applies to.", true)),
                    Prompt("dwg-to-bim-unit", "Convert one unit from a drawing, end to end",
                        "Inventory, walls, devices and two audits for one unit, as a resumable run a batch repeats per unit.",
                        Arg("walls_set", "The walls requirement set, bounded to the unit.", true),
                        Arg("devices_set", "The devices requirement set, bounded to the same unit.", true),
                        Arg("dwg_path", "The drawing the link shows, readable on this machine.", true)),
                    Prompt("dwg-to-bim-update", "Bring a converted unit to a new drawing revision",
                        "Apply what an update decides alone, then ask one grouped decision per set for what it holds, and audit against the new revision.",
                        Arg("walls_set", "The walls requirement set of the unit.", true),
                        Arg("devices_set", "The devices requirement set of the unit.", true),
                        Arg("dwg_path", "The NEW revision of the drawing, readable on this machine.", true)),
                    // The two DWG procedures the catalogue carried with no prompt behind them.
                    Prompt("dwg-mep-unit-conversion", "Convert DWG MEP units to a connected model",
                        "Plan, build, connect and audit MEP runs from a unit drawing, with repeated unit layouts recognised and every unjoined junction reported.",
                        Arg("requirement_set", "Approved requirement set: what each layer means, with system, bore and elevation; required because layer meaning is never guessed.", true),
                        Arg("unit_regions", "The unit boundaries as regions in drawing millimetres; required because a whole-floor reading is not a unit.", true),
                        Arg("outfall", "Where a draining network leaves, with its invert. Omit only for networks that do not drain; without it every run is built level.", false)),
                    Prompt("dwg-electrical-symbols", "Place DWG symbols as family instances",
                        "Group the marks on the named symbol layers into types and place each type as the family a person named, with every rejected group reported.",
                        Arg("symbol_layers", "The symbol layers, as globs; required because grouping over the whole drawing buries the symbols in the wiring.", true),
                        Arg("max_footprint_mm", "How large connected line work may be and still be one symbol; required because a receptacle is 40 mm on one drawing and 400 mm on another.", true),
                        Arg("family_types", "The family type for each symbol type, as decided by a person; required because this route never names a symbol type itself.", true)),
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
                case "family-recipe":
                    description = "Rehearse a fixed-footprint, height-parametric family recipe.";
                    body = "Call horizun_health first. Specification: " + Argument(args, "specification", true) +
                        ". Discover an existing compatible RFT and confirm output/overwrite/load choices. Use horizun_create_family recipe " +
                        "rectangular_prism or rectangular_tube; require explicit width/depth, wall for tube, height_parameter and at least two " +
                        "distinct type heights. Only height flexes; XY is fixed. Rehearse with dry_run=true and return confirmation, without applying. " +
                        "After approval inspect reopened-file verification, measured flex and PNG; never substitute a successful export for visual approval.";
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
                        "horizun_audit_model, horizun_quantities and horizun_clash. " +
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
                case "dwg-to-bim-review":
                    string dwgSet = Argument(args, "requirement_set", true);
                    description = "Plan a DWG-to-BIM conversion without creating model elements.";
                    body =
                        "Call horizun_health and horizun_query_cad first. Use this approved requirement set: " + dwgSet +
                        ". Run horizun_plan_from_cad only. Report geometry coverage, source units, layer mapping, " +
                        "unresolved candidates, rival readings and the plan fingerprint. Never infer layer meaning, " +
                        "and do not call apply until a human reviews this plan.";
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
                case "architecture-structure-coordination":
                    string architectureScope = Argument(args, "scope", false);
                    description = "Measure architecture/structure coordination without changing either discipline.";
                    body =
                        "Call horizun_health first and confirm the active document and all measurable links. Use " +
                        "horizun_query_structure and horizun_query_model " +
                        (string.IsNullOrWhiteSpace(architectureScope) ? "for the active model's measurable coordination scope." : "for this stated scope: " + architectureScope + ".") +
                        " Run horizun_clash only with explicit, reviewable source/target categories. Report host/link coverage, " +
                        "unloaded links, phases, design options, clash tolerances and unknowns separately. Do not move, resize, " +
                        "create or delete elements; proposed corrections require a separate approved dry run.";
                    break;
                case "mep-network-completion":
                    string networkScope = Argument(args, "scope", true);
                    string networkTolerance = Argument(args, "tolerance_mm", false);
                    description = "Connect an MEP network with measured preconditions and verified connections.";
                    body =
                        "Call horizun_health first. Work only inside this scope: " + networkScope + ". Read the open " +
                        "connectors with horizun_plan_mep network_census and horizun_query_model; name every pair you " +
                        "intend to join by element id AND connector id. Where the run needs a fitting, build it with " +
                        "horizun_create_elements; where two things simply meet, join them with horizun_connect_mep" +
                        (string.IsNullOrWhiteSpace(networkTolerance) ? "" : " at a tolerance of " + networkTolerance + " mm") +
                        ". Run every call as a dry run first and read the rehearsal. NEVER move geometry to close a gap: " +
                        "a refusal that reports the measured distance is the answer, not an obstacle. Report the " +
                        "connectors still open afterwards - a network can be correctly connected and still incomplete, " +
                        "and only the person who asked can tell which.";
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
                case "dwg-to-bim-update":
                    string updWalls = Argument(args, "walls_set", true);
                    string updDevices = Argument(args, "devices_set", true);
                    string updDrawing = Argument(args, "dwg_path", true);
                    description = "Bring a converted unit to a new revision of its drawing.";
                    body =
                        "Call horizun_health first. Repoint the CAD link to " + updDrawing + " with horizun_manage_cad_links " +
                        "(rehearse, then apply). Start horizun_run_procedure with procedure dwg-to-bim-update and inputs: " +
                        "walls set " + updWalls + ", devices set " + updDevices + ", drawing " + updDrawing + ", the hash of " +
                        "the revision it supersedes, the document, the CAD link id and the level. Advance it. When it waits " +
                        "for a decision, show the person the held rows with their classification and origin, and record " +
                        "their answer with operation decide - never decide for them. A change whose origin is unknown is " +
                        "said to be unknown.";
                    break;
                case "dwg-to-bim-unit":
                    string unitWalls = Argument(args, "walls_set", true);
                    string unitDevices = Argument(args, "devices_set", true);
                    string unitDrawing = Argument(args, "dwg_path", true);
                    description = "Convert one unit from a drawing: inventory, walls, devices, audits.";
                    body =
                        "Call horizun_health first. Start horizun_run_procedure with procedure dwg-to-bim-unit and these " +
                        "inputs: walls set " + unitWalls + ", devices set " + unitDevices + ", drawing " + unitDrawing +
                        ", plus the document, the CAD link id and the level. Advance it step by step. A step that holds " +
                        "- a rehearsal that is not clean, a reply that never arrived - is read and decided, never sent " +
                        "again. At the end, report the inventory by outcome, what each audit found not built and why, " +
                        "and say that finishing the steps is not accepting the unit.";
                    break;
                case "dwg-mep-unit-conversion":
                    string mepSet = Argument(args, "requirement_set", true);
                    string unitRegions = Argument(args, "unit_regions", true);
                    string outfall = Argument(args, "outfall", false);
                    description = "Convert DWG MEP units into a connected, audited model.";
                    body =
                        "Call horizun_health first, then horizun_cad_extract and read what the reader is blind to before " +
                        "anything depends on it. Use this approved requirement set: " + mepSet + ", and only inside these " +
                        "unit regions: " + unitRegions + ". Read the networks with horizun_cad_networks and the repeated " +
                        "layouts with horizun_cad_unit_instances. Plan with horizun_plan_from_cad" +
                        (string.IsNullOrWhiteSpace(outfall)
                            ? "; no outfall was given, so every run will be built LEVEL - say so in the report, and do " +
                              "not build a draining system that way"
                            : " with this outfall and invert: " + outfall) +
                        ". Rehearse horizun_apply_cad_plan with dry_run=true, then apply; join with horizun_cad_connect " +
                        "the same way; finish with horizun_audit_cad_model and horizun_cad_review. Never join a crossing " +
                        "that shares no endpoint, never treat a riser as an elbow, and list every junction, crossing and " +
                        "gap that was NOT joined with its reason.";
                    break;
                case "dwg-electrical-symbols":
                    string symbolLayers = Argument(args, "symbol_layers", true);
                    string footprint = Argument(args, "max_footprint_mm", true);
                    string familyTypes = Argument(args, "family_types", true);
                    description = "Place DWG symbols as the family types a person named.";
                    body =
                        "Call horizun_health first, then horizun_cad_extract, and settle the chord tolerance before " +
                        "grouping. Group the marks with horizun_cad_symbols on these layers only: " + symbolLayers +
                        ", with a footprint of " + footprint + " mm. Place each symbol type as the family type this " +
                        "person named: " + familyTypes + " - never name a type yourself, and leave any type they did not " +
                        "name unplaced and listed. Rehearse horizun_create_elements with dry_run=true before applying, " +
                        "and re-read the result with horizun_query_model. Report every rejected group with its reason; " +
                        "do not raise the footprint until something passes, and report mirrored occurrences instead of " +
                        "placing them rotated.";
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
                case "mep-coordination-review":
                    string mepScope = Argument(args, "scope", false);
                    description = "Inspect MEP coordination and provide only non-writing routing plans.";
                    body =
                        "Call horizun_health first. Inspect MEP elements and links " +
                        (string.IsNullOrWhiteSpace(mepScope) ? "in the active model's measurable scope." : "for this stated scope: " + mepScope + ".") +
                        " Use horizun_query_model and horizun_clash with explicit categories and tolerance. Where a route must be " +
                        "evaluated, call horizun_plan_mep only and report its alternatives, conflicts and plan fingerprint. Do not " +
                        "create, reroute, resize or connect MEP elements; missing systems, unloaded links and unreadable data remain unknown.";
                    break;
                case "western-forms-concrete-review":
                    string concreteRequirements = Argument(args, "requirement_set", true);
                    description = "Audit declared concrete, formwork and reinforcement requirements without writes.";
                    body =
                        "Call horizun_health first. Use this approved requirement set: " + concreteRequirements +
                        ". Resolve it into explicit structural categories, levels, marks, cover, bar and formwork checks before reading " +
                        "the model. Use horizun_query_structure and horizun_audit_reinforcement only. Report measured compliance, " +
                        "missing evidence, unmodeled formwork and incomplete coverage separately. Do not generate, apply, renumber or " +
                        "modify reinforcement or forms.";
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
