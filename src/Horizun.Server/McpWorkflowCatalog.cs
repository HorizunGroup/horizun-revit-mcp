// -----------------------------------------------------------------------------
// Horizun MCP - the procedure catalogue. Original Horizun code.
//
// G13 of the 2026-09-14 competitive inventory: "20 workflows prioritarios con
// entrada/salida versionada, ejemplo y prueba".
//
// WHAT THIS WAS, AND WHY IT WAS NOT ENOUGH. The first version listed seventeen
// flows as id, title, minimum permission and a list of tool names. That is a menu.
// It told a reader which tools to reach for and nothing about what the procedure
// TAKES, what it PRODUCES, how it FAILS, or how anybody would know it worked - so
// two people running "deliverable-production" could do two different things and
// both be right, and neither could be checked.
//
// A PROCEDURE IS NOT A SHORTCUT AND IT IS NOT A SECOND EXECUTOR. Every one of
// these is an entry point over the typed tools: it cannot bypass a permission, a
// dry run, a confirmation token or a verification, and nothing here runs anything.
// The catalogue is data. What it adds over the menu is the six things a procedure
// needs to be repeatable by somebody who did not write it:
//
//   inputs      what must be decided BEFORE starting, each one named, because a
//               procedure that discovers halfway through that nobody chose the
//               sheet size has already written something.
//   scope       which elements it touches, and - just as load-bearing - which it
//               does not.
//   output      what exists afterwards that did not before.
//   errors      the failures that are EXPECTED, with what to do about each. A
//               failure nobody wrote down is a failure somebody will read as a
//               bug in the bridge.
//   depends_on  other procedures that must have run first. Stated, not implied
//               by the order of a list.
//   acceptance  how a person decides it worked - measured, not felt.
//
// VERSIONED PER PROCEDURE, not per catalogue. A procedure whose steps change is a
// different procedure, and a run recorded against version 1 must not be read as
// evidence for version 2. The catalogue version changes when the SHAPE changes;
// each procedure carries its own.
//
// ORGANISATION-NEUTRAL, like the rest of the bridge. No company's standards,
// naming conventions or catalogues are in here. Where a procedure needs one it
// says so as an INPUT, which is the same discipline the tools themselves follow.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpWorkflowCatalog
    {
        /// <summary>The catalogue's own shape. Bumped when the SHAPE changes, never when a procedure does.</summary>
        public const string Schema = "horizun.workflow-catalog/2";

        /// <summary>
        /// One procedure, by id, as JSON — or null.
        ///
        /// The catalogue could only be read whole. A run of one procedure needs one, and
        /// filtering the whole document at every call would make the executor depend on the
        /// shape of a report rather than on the entry it is running.
        /// </summary>
        public static JObject Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            Procedure found = All.FirstOrDefault(
                p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            return found?.Json();
        }

        public static JObject Document()
        {
            var procedures = new JArray();
            foreach (Procedure p in All) procedures.Add(p.Json());

            return new JObject
            {
                ["schema"] = Schema,
                ["title"] = "Horizun BIM Production Procedures",
                ["principle"] =
                    "A procedure is an entry point over typed tools. It does not bypass permissions, dry runs, " +
                    "confirmation tokens or verification, and nothing in this catalogue executes anything.",
                ["neutrality"] =
                    "No organisation's standards, naming conventions or catalogues are compiled in. Where a " +
                    "procedure needs one it appears as a named INPUT.",
                ["count"] = procedures.Count,
                ["executable"] = All.Count(p => p.Detail == "executable"),
                ["with_steps"] = All.Count(p => p.Detail == "steps"),
                ["tool_list_only"] = All.Count(p => p.Detail == "tool_list_only"),
                ["templates_disagreeing_with_their_tools"] =
                    All.Count(p => !p.TemplatesAgree()),
                ["procedures_with_an_unevaluable_check"] =
                    All.Count(p => (p.Checks ?? new AcceptanceCheck[0]).Any(c => !c.IsEvaluable)),
                ["templates_mean"] =
                    "checked against the contract's own schemas: a template that sends a key its tool does " +
                    "not declare, or omits one its tool requires, is named here rather than discovered on " +
                    "the step where it fails.",
                ["detail_means"] =
                    "THREE STATES, COUNTED APART. tool_list_only names its tools and not the route. " +
                    "with_steps has the route, in prose, for a person to follow. EXECUTABLE has argument " +
                    "templates and an input schema, so horizun_run_procedure can dispatch it. A catalogue " +
                    "where the three look alike is one nobody can plan from, and the middle state is the one " +
                    "that reads as finished and is not.",
                ["workflows"] = procedures,
                // The old key, kept so an existing reader does not break on the day this
                // changed shape. Same array, same objects.
                ["procedures"] = procedures
            };
        }

        // =====================================================================
        // The twenty.
        // =====================================================================

        /// <summary>
        /// The catalogue itself, for a test that wants to assert something about
        /// EVERY procedure rather than about one.
        ///
        /// The list stays private: a caller that could add to it could add a
        /// procedure nobody reviewed. This is a read-only view of it.
        /// </summary>
        internal static IEnumerable<Procedure> Procedures => All;

        private static readonly List<Procedure> All = new List<Procedure>
        {
            new Procedure
            {
                Id = "deliverable-production",
                Title = "Deliverable Production",
                Version = 2,
                Permission = "read_only_to_full_write",
                Outcome = "A PDF set published from sheets that were laid out, annotated, audited and looked at.",
                Tools = new[] { "horizun_plan_views", "horizun_plan_annotations", "horizun_annotate",
                                "horizun_pack_sheets", "horizun_audit_planimetry", "horizun_capture_view",
                                "horizun_export" },
                Inputs = new[]
                {
                    "the sheet size and title block type, by id",
                    "the view list to publish, by id - never 'all views'",
                    "the annotation rules this project uses (they are not in the bridge)",
                    "the output folder, which full_write must already authorize"
                },
                Scope = "The named views and the sheets they land on. No model geometry is changed at any step.",
                Output = "Sheets, viewports, annotations, an audit report and one PDF per sheet set.",
                Errors = new[]
                {
                    "a view already placed on another sheet - Revit allows one viewport per view, so the " +
                    "procedure stops and names the sheet it is already on",
                    "a title block whose label overflows - the sheet's paper size grows and the layout audit " +
                    "catches it; fix the field, do not widen the sheet",
                    "export_in_background returning before the file exists - the export step re-reads the " +
                    "directory and refuses to report a file it cannot stat"
                },
                DependsOn = new string[0],
                Acceptance = "Every named view is on exactly one sheet, the planimetry audit reports zero " +
                             "overlaps at the declared tolerance, and every expected PDF exists on disk with a " +
                             "non-zero size.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document everything after this acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step below names a target_document, and naming the wrong " +
                                  "one publishes somebody else's project.",
                        ReadsBack = "the document title and the bridge's own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_plan_views",
                        // operation=deliverable_set ALSO requires delivery_profile -
                        // a conditional in the schema, and a template that satisfied
                        // only the top-level `required` would fail at dispatch.
                        ArgumentsJson = @"{
  ""operation"": ""deliverable_set"",
  ""delivery_profile"": { ""$input"": ""delivery_profile"" }
}",
                        Purpose = "decide which views go on which sheets, before anything is created.",
                        Needs = "the document title from step 1.",
                        Preconditions = "the title block type and sheet size are known BY ID - not by name, " +
                                        "because two title blocks can share a name.",
                        OnError = "a view that cannot be placed is named with its reason. Fix the view or " +
                                  "drop it from the list; do not proceed with a partial plan and discover " +
                                  "the gap in the PDF.",
                        ReadsBack = "the plan: sheet by sheet, view by view, with the ones it refused."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_pack_sheets",
                        // WHAT GOES ON WHICH SHEET IS THE DELIVERABLE.
                        //
                        // A catalogue that chose it would be laying out somebody's
                        // drawing set, and the packing that fits is not the same
                        // question as the packing that reads.
                        RequiresDecision = true,
                        DecisionNeeded = "the items: which views land on which sheet, each with its sheet_id. " +
                                         "Step 2 says what exists; what belongs together on a page is a " +
                                         "decision about the set.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""items"": { ""$decision"": ""items"" },
  ""units"": ""mm"",
  ""dry_run"": false
}",
                        Purpose = "create the sheets and place the views.",
                        Needs = "the plan from step 2, sent back unchanged.",
                        Preconditions = "dry_run first. This is the first step that writes.",
                        OnError = "the reply names which sheets landed. Do NOT re-send the whole plan: " +
                                  "re-run with the same idempotency_key, which replays rather than " +
                                  "duplicating.",
                        ReadsBack = "each created sheet id, re-read from the model after the commit."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_plan_annotations",
                        // operation AND view_id are both required by the schema.
                        ArgumentsJson = @"{
  ""operation"": { ""$input"": ""annotation_operation"" },
  ""view_id"": { ""$input"": ""annotation_view_id"" }
}",
                        Purpose = "work out what to annotate, against THIS project's rules.",
                        Needs = "the sheet and view ids created in step 3.",
                        Preconditions = "the annotation rules are supplied as an input. The bridge carries " +
                                        "no organisation's conventions and will not invent one.",
                        OnError = "a rule that matches nothing is reported as matching nothing, which is a " +
                                  "finding about the rule rather than about the model.",
                        ReadsBack = "the planned annotations, per view, with what each one would say."
                    },
                    new Step
                    {
                        N = 5, Tool = "horizun_annotate",
                        // WHICH ANNOTATIONS TO PLACE is the other half of the
                        // deliverable. Step 4 proposes; a person decides, because a
                        // tag in the wrong place is a drawing somebody signs.
                        RequiresDecision = true,
                        DecisionNeeded = "the annotation actions to place, from the proposals in step 4. " +
                                         "Each needs an operation and a view_id.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""units"": ""mm"",
  ""actions"": { ""$decision"": ""actions"" },
  ""dry_run"": false
}",
                        Purpose = "place them.",
                        Needs = "the plan from step 4.",
                        Preconditions = "the views are OPEN or have been shown at least once - Revit " +
                                        "materialises view geometry lazily, and a dimension against a view " +
                                        "nobody has shown fails for a reason that reads as a bad reference.",
                        OnError = "partial by design: the reply says which landed. Re-run for the rest.",
                        ReadsBack = "every annotation re-read after the commit, with its own text."
                    },
                    new Step
                    {
                        N = 6, Tool = "horizun_audit_planimetry",
                        ArgumentsJson = @"{
  ""scope"": ""sheets"",
  ""units"": ""mm"",
  ""include_passed_checks"": true
}",
                        Purpose = "find what the layout broke before a person looks at it.",
                        Needs = "the sheet ids from step 3.",
                        Preconditions = "annotations are placed, or the audit measures an unfinished sheet.",
                        OnError = "findings are findings, not errors. Fix and re-audit.",
                        ReadsBack = "overlaps, off-sheet text and empty viewports, per sheet."
                    },
                    new Step
                    {
                        N = 7, Tool = "horizun_capture_view",
                        ArgumentsJson = @"{ ""view_id"": { ""$input"": ""evidence_view_id"" } }",
                        Purpose = "LOOK at the sheets. An audit checks what it was told to check.",
                        Needs = "the sheet ids from step 3.",
                        Preconditions = "none.",
                        OnError = "a capture that fails is a sheet nobody looked at. Do not publish it.",
                        ReadsBack = "a PNG per sheet, with its pixel calibration."
                    },
                    new Step
                    {
                        N = 8, Tool = "horizun_export",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""format"": { ""$input"": ""format"" },
  ""output_path"": { ""$input"": ""output_path"" },
  ""emit_manifest"": true
}",
                        Purpose = "publish the PDF set.",
                        Needs = "the sheet ids from step 3.",
                        Preconditions = "steps 6 and 7 are clean, or their findings are accepted knowingly.",
                        OnError = "export_in_background returns BEFORE the file exists. Re-read the path; " +
                                  "a reply is not a file.",
                        ReadsBack = "the file on disk, its size and its page count."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""delivery_profile"", ""annotation_operation"", ""annotation_view_id"", ""evidence_view_id"", ""format"", ""output_path""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document. Steps 3, 5 and 8 name it."" },
    ""delivery_profile"": { ""type"": ""object"",
      ""description"": ""What the set IS: which views, which sheets, which naming. horizun_plan_views requires it for operation=deliverable_set, and it is the caller's artefact - no delivery convention is compiled into this bridge."" },
    ""annotation_operation"": { ""type"": ""string"",
      ""description"": ""Which annotation question step 4 answers. Required by the tool alongside view_id, and there is no default across the operations it offers."" },
    ""annotation_view_id"": { ""type"": ""integer"", ""description"": ""The view step 4 plans annotations for."" },
    ""evidence_view_id"": { ""type"": ""integer"",
      ""description"": ""The sheet captured before the export. A deliverable route whose evidence is a file list and no picture is one nobody checked."" },
    ""format"": { ""type"": ""string"", ""description"": ""The export format. Required by horizun_export."" },
    ""output_path"": { ""type"": ""string"", ""description"": ""Where the export lands. Required by horizun_export, and outside the model - the permission profile has to allow it."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 6, Path = "coverage_complete", Expect = "true",
                        Why = "a sheet audit that could not read part of the set has not measured it, and " +
                              "this route exports whatever it measured. A check with no population reports " +
                              "not_applicable and is never a pass."
                    }
                },
                Example =
                    "health → plan_views(sheet_type_id=345678, views=[101,102,103]) → pack_sheets(that plan, " +
                    "dry_run=true, then false) → plan_annotations(sheets from 3, rules=<your file>) → " +
                    "annotate(that plan) → audit_planimetry(sheets) → capture_view(each sheet) → " +
                    "export(format=pdf, sheets).",
                Limits = new[]
                {
                    "A title block whose labels overflow WIDENS the sheet's own bounding box, so an " +
                    "overlap audit against 'the title block' can pass while the text is plainly over the " +
                    "border. The real paper is SHEET_WIDTH and SHEET_HEIGHT.",
                    "Revit exposes no camera acknowledgement, so 'the view was framed' is never a verified " +
                    "claim anywhere in this route.",
                    "Nothing here decides WHICH views belong in a deliverable. That is the project's " +
                    "decision and it arrives as an input."
                },
            },

            new Procedure
            {
                Id = "room-documentation",
                Title = "Room Documentation",
                Version = 2,
                Permission = "safe_write",
                Outcome = "One documented set of room views, created atomically from an explicit plan.",
                Tools = new[] { "horizun_plan_views", "horizun_manage_views", "horizun_query_planimetry" },
                Inputs = new[] { "the rooms, by id", "the view family types and templates, by id",
                                 "the scale, which the template may override" },
                Scope = "Views and their placement. Rooms themselves are not edited.",
                Output = "Plan and elevation views per room, named by the project's convention.",
                Errors = new[]
                {
                    "a room that is not placed - it has no boundary to document and is reported, not skipped",
                    "a template that governs the view scale - the requested scale is silently ignored by Revit, " +
                    "so the plan step refuses instead"
                },
                DependsOn = new string[0],
                Acceptance = "Every named room has its full set of views, each carrying the requested template, " +
                             "and the re-read scale matches what was asked for.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_plan_views",
                        // operation=room_views ALSO requires plan_view_id - a
                        // conditional in the schema, and a template that satisfied
                        // only the top-level `required` would fail at dispatch.
                        ArgumentsJson = @"{
  ""operation"": ""room_views"",
  ""plan_view_id"": { ""$input"": ""plan_view_id"" },
  ""room_ids"": { ""$input"": ""room_ids"" }
}",
                        Purpose = "decide which views each room gets, and with which template, before anything exists.",
                        Needs = "the document title from step 1, plus the room ids and the view family type and " +
                                "template ids, which arrive as inputs.",
                        Preconditions = "the phase is decided. A room that is not placed in THAT phase has no boundary to " +
                                        "document, and the plan reports it rather than skipping it.",
                        OnError = "a room that will not resolve is named with its reason. Do not fall back to matching " +
                                  "by name: two rooms on different levels routinely share one.",
                        ReadsBack = "per room, the views it would create, their names, their template and the scale each " +
                                    "would end up with."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_manage_views",
                        // THE TOOL THE PLANNER ACTUALLY HANDS YOU.
                        //
                        // This step said horizun_execute_plan and referenced a
                        // `workflow` from step 2. horizun_plan_views returns no
                        // workflow for room_views: it returns next_tool
                        // "horizun_manage_views" and next_arguments, and its own note
                        // says "only its rehearsal validates the batch and only its
                        // confirmation token writes". The prose and the tool had
                        // disagreed since this was written, and nothing caught it
                        // because nothing executed it.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""units"": ""mm"",
  ""actions"": { ""$ref"": { ""step"": 2, ""path"": ""next_arguments.actions"" } },
  ""dry_run"": true
}",
                        Purpose = "rehearse the whole set. Nothing is written.",
                        Needs = "the actions step 2 emitted, unchanged.",
                        Preconditions = "none: this is the rehearsal.",
                        OnError = "a view that will not build is named here, before anything exists. That is the " +
                                  "whole reason the rehearsal is its own step.",
                        ReadsBack = "the batch as it would land, and the confirmation token step 4 needs."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_manage_views",
                        // THE SAME ACTIONS AND THE TOKEN THE REHEARSAL PRODUCED. The
                        // token is what ties this call to that rehearsal: a batch
                        // that changed between them is refused rather than written.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""units"": ""mm"",
  ""actions"": { ""$ref"": { ""step"": 2, ""path"": ""next_arguments.actions"" } },
  ""confirmation_token"": { ""$ref"": { ""step"": 3, ""path"": ""confirmation_token"" } },
  ""dry_run"": false
}",
                        Purpose = "create the set.",
                        Needs = "step 2's actions and step 3's token.",
                        Preconditions = "step 3 rehearsed clean. This is the first step that writes.",
                        OnError = "the reply names what landed. The token binds this call to that rehearsal, so a " +
                                  "batch that changed in between is refused rather than written.",
                        ReadsBack = "each created view id, re-read from the model after the commit, with the template it " +
                                    "actually carries."
                    },
                    new Step
                    {
                        N = 5, Tool = "horizun_query_planimetry",
                        ArgumentsJson = @"{
  ""mode"": ""views"",
  ""units"": ""mm""
}",
                        Purpose = "confirm every room got its full set and that the names follow the convention.",
                        Needs = "the view ids from step 4.",
                        Preconditions = "none.",
                        OnError = "a room short of a view is a finding about the plan in step 2, not about this step. " +
                                  "Fix the plan and re-run it.",
                        ReadsBack = "the count of views per room and each name measured against the convention that " +
                                    "arrived as an input."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""plan_view_id"", ""room_ids""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document. Step 3 writes and names it."" },
    ""plan_view_id"": { ""type"": ""integer"",
      ""description"": ""The plan the room views are cut from. horizun_plan_views requires it for operation=room_views, and it decides what the resulting views actually show."" },
    ""room_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1,
      ""description"": ""The rooms to document, by id. Required: 'every room' on a tower is a batch nobody reviewed."" }
  },
  ""additionalProperties"": false
}",
                Example = "health then plan_views(rooms=[4421,4422], type_id=..., template_id=...) then " +
                          "execute_plan(that plan, dry_run=true, then false) then query_planimetry(the created " +
                          "views).",
                Limits = new[]
                {
                    "A view TEMPLATE governs scale, crop and visibility. When it does, Revit accepts a " +
                    "requested scale and ignores it in silence, so step 2 refuses rather than planning a " +
                    "value that will not survive. The re-read value in step 3 is the only truth.",
                    "The naming convention is an input. The bridge carries no organisation conventions " +
                    "and will not invent one to measure against.",
                    "Rooms are never edited by this route. A room that is unplaced stays unplaced and is " +
                    "reported."
                }
            },

            new Procedure
            {
                Id = "family-recipe",
                Title = "Family Recipe",
                Version = 2,
                Permission = "full_write",
                Outcome = "A parametric family compiled from an explicit recipe and inspected before acceptance.",
                Tools = new[] { "horizun_create_family", "horizun_query_model" },
                Inputs = new[] { "the recipe, with its fixed footprint and its parametric dimension",
                                 "the family template, which exists per Revit year and per language",
                                 "the flex values to test" },
                Scope = "One family document. The project is not touched until the family is loaded.",
                Output = "A .rfa, its parameter census, and a PNG of the flexed result.",
                Errors = new[]
                {
                    "a 2D template - create_family does not support one, and says so rather than producing " +
                    "something that opens and cannot be used",
                    "a flex that does not move geometry - reported as a failed flex, never as a pass"
                },
                DependsOn = new string[0],
                Acceptance = "The family reopens with the declared parameters and types, and the flex changes " +
                             "the geometry in the direction the recipe predicted.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and
                        // refuses extras. An empty template is what makes this step
                        // EXECUTABLE rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish the Revit YEAR, which decides which family templates exist.",
                        Needs = "nothing",
                        Preconditions = "Revit is running.",
                        OnError = "stop. A family template path is per year AND per installed language, and a template " +
                                  "that does not exist fails in a way that reads like a bad recipe.",
                        ReadsBack = "the Revit year, the document title and the bridge version."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_create_family",
                        // THE RECIPE IS THE THING BEING DESIGNED.
                        //
                        // Its dimensions and its type table are the component, and a
                        // catalogue that supplied them would be designing somebody's
                        // family. So it arrives as a decision, and the rehearsal and
                        // the apply share the SAME one.
                        RequiresDecision = true,
                        DecisionNeeded = "the recipe: name, width, depth, height parameter and the type " +
                                         "table, plus the template to author from. These are the component, " +
                                         "not settings for producing it.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""template_path"": { ""$input"": ""template_path"" },
  ""output_path"": { ""$input"": ""output_path"" },
  ""recipe"": { ""$decision"": ""recipe"" },
  ""dry_run"": true
}",
                        Purpose = "rehearse the recipe without producing a file.",
                        Needs = "the Revit year from step 1, plus the recipe and the template, which arrive as " +
                                "inputs.",
                        Preconditions = "the template is a 3D family template. A 2D one is not supported, and the refusal " +
                                        "says so rather than producing a file that opens and cannot be used.",
                        OnError = "a refusal names the template and the part of the recipe it could not satisfy. Change " +
                                  "the recipe; do not retry the same call.",
                        ReadsBack = "what it would create: the parameters, the types and the geometry the recipe " +
                                    "describes."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_create_family",
                        // The SAME recipe as the rehearsal, by reference. A second
                        // ask is a second chance to change what step 2 rehearsed.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""template_path"": { ""$input"": ""template_path"" },
  ""output_path"": { ""$input"": ""output_path"" },
  ""recipe"": { ""$decision"": ""recipe"", ""from_step"": 2 },
  ""dry_run"": false
}",
                        Purpose = "build it.",
                        Needs = "the rehearsed recipe from step 2, unchanged.",
                        Preconditions = "step 2 rehearsed clean. This step writes a file.",
                        OnError = "a partial family is still a file on disk. The reply names what it built; delete it " +
                                  "before retrying rather than loading a half-built family into a project.",
                        ReadsBack = "the .rfa path and the parameter census re-read from the REOPENED document, not from " +
                                    "the builder own idea of what it wrote."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_query_model",
                        ArgumentsJson = @"{
  ""categories"": { ""$input"": ""categories"" },
  ""response_mode"": ""summary""
}",
                        Purpose = "flex it: prove the parametric dimension actually moves the geometry.",
                        Needs = "the family and type from step 3, plus the flex values from the recipe.",
                        Preconditions = "the family is loaded, or the flex is measured in the family document itself.",
                        OnError = "a flex that does not move geometry is a FAILED flex. It is never reported as a pass " +
                                  "because the call returned without throwing.",
                        ReadsBack = "the measured dimension at each flex value, against what the recipe predicted."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""template_path"", ""output_path"", ""categories""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document the family is loaded into."" },
    ""template_path"": { ""type"": ""string"",
      ""description"": ""Absolute existing .rft. The TEMPLATE decides the category and the hosting behaviour, which is why it is an input and not a detail of the recipe: the same recipe on a face-based template and on a free-standing one produces two different components."" },
    ""output_path"": { ""type"": ""string"", ""description"": ""Absolute .rfa destination in an existing directory."" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""Read back in step 4 to confirm the family arrived in the project with its types."" }
  },
  ""additionalProperties"": false
}",
                Example = "health (note the year) then create_family(recipe, dry_run=true) then " +
                          "create_family(same, dry_run=false) then query_model(measure the flexed dimension at " +
                          "each test value).",
                Limits = new[]
                {
                    "A family that reopens is not a family that works. Only step 4 distinguishes the two, " +
                    "and it is the step people skip.",
                    "Family templates ship with the Revit installation and differ by language. A recipe " +
                    "that names a template by an English path fails on a Spanish install for a reason " +
                    "that looks like a bad recipe.",
                    "Nothing here loads the family into a project. That is a separate, reversible " +
                    "decision."
                }
            },

            new Procedure
            {
                Id = "review-correct-verify",
                Title = "Review, Correct, Verify",
                Version = 2,
                Permission = "read_only_to_safe_write",
                Outcome = "Audit findings turned into typed corrections, each re-audited per element.",
                Tools = new[] { "horizun_audit_model", "horizun_apply_corrections" },
                Inputs = new[] { "the finding set to act on, by id - never 'everything'",
                                 "the standard the audit measured against" },
                Scope = "Only the elements the selected findings name.",
                Output = "Corrected elements and a per-element re-audit.",
                Errors = new[]
                {
                    "a finding whose element has since been deleted - reported as no longer applicable",
                    "a correction that a workset permission refuses - the element is named and the batch rolls back"
                },
                DependsOn = new[] { "model-health-audit" },
                Acceptance = "Every selected finding re-audits clean, and the count of findings NOT selected is " +
                             "reported so nobody reads a partial pass as a whole one.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_audit_model",
                        ArgumentsJson = @"{ ""target_document"": { ""$input"": ""document"" } }",
                        Purpose = "produce the finding set the corrections will be selected FROM.",
                        Needs = "the document from step 1, plus the standard, which arrives as an input.",
                        Preconditions = "the standard is explicit. An audit with no standard measures nothing and reports a " +
                                        "clean model.",
                        OnError = "a check that could not read what it needed reports NOT COVERED. That is not a clean " +
                                  "result and must not be rendered as one.",
                        ReadsBack = "the finding set id, the count per check, and the element ids each finding names."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_apply_corrections",
                        // THE DECISION STOPS THE RUN. Which findings to act on cannot be an
                        // input at start - they do not exist until step 2 has run - and it
                        // is not a choice this executor may make: selecting everything an
                        // audit produced is a decision, and it would be made on somebody
                        // else's model.
                        RequiresDecision = true,
                        DecisionNeeded =
                            "the ACTIONS to apply: an array of { finding_id, ... } chosen from step 2's " +
                            "findings[]. Supply it with operation=decide, values={ \"actions\": [...] }. " +
                            "An empty selection is refused by the tool itself.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""finding_set_fingerprint"": { ""$ref"": { ""step"": 2, ""path"": ""finding_set_fingerprint"" } },
  ""actions"": { ""$decision"": ""actions"" },
  ""dry_run"": true
}",
                        Purpose = "rehearse the corrections for the findings that were SELECTED.",
                        Needs = "the finding set id and the selected finding ids from step 2. Never the whole set " +
                                "implicitly.",
                        Preconditions = "dry_run. Somebody has read the findings and chosen. Selecting everything an audit " +
                                        "produced is a decision, and it should be made on purpose rather than by default.",
                        OnError = "a finding whose element was deleted since step 2 is reported as no longer " +
                                  "applicable, which is information about the model, not a failure.",
                        ReadsBack = "per finding, what would change and on which element."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_apply_corrections",
                        // The token comes from the rehearsal, which is the whole point of
                        // having one: an apply that carried a token from somewhere else
                        // would be applying a plan nobody rehearsed.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""finding_set_fingerprint"": { ""$ref"": { ""step"": 2, ""path"": ""finding_set_fingerprint"" } },
  ""actions"": { ""$decision"": ""actions"", ""from_step"": 3 },
  ""dry_run"": false,
  ""confirmation_token"": { ""$ref"": { ""step"": 3, ""path"": ""confirmation_token"" } }
}",
                        Purpose = "apply them.",
                        Needs = "the rehearsed selection from step 3.",
                        Preconditions = "step 3 rehearsed clean, or its exceptions were accepted knowingly.",
                        OnError = "a correction a workset permission refuses names the element and the batch rolls " +
                                  "back. Borrow the element or drop it from the selection; do not retry the same batch.",
                        ReadsBack = "per element, the value re-read from the model after the commit."
                    },
                    new Step
                    {
                        N = 5, Tool = "horizun_audit_model",
                        ArgumentsJson = @"{ ""target_document"": { ""$input"": ""document"" } }",
                        Purpose = "re-audit, so the proof comes from the model and not from the reply that just wrote " +
                                  "it.",
                        Needs = "the same finding set and standard as step 2, narrowed to the elements from step 4.",
                        Preconditions = "the corrections committed.",
                        OnError = "a finding that survives the correction is the interesting result of this whole " +
                                  "route. Report it; do not re-apply and re-check in a loop.",
                        ReadsBack = "each selected finding clean, AND the count of findings that were never selected, so " +
                                    "nobody reads a partial pass as a whole one."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document. The audit's fingerprint is checked against it before anything is written."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 5, Path = "coverage_complete", Expect = "true",
                        Why = "a re-audit that could not read part of the model has not verified the " +
                              "corrections. This is NOT the whole criterion above - that one is about each " +
                              "selected finding re-auditing clean, and reading it is still somebody's job."
                    }
                },
                Example = "health then audit_model(standard) then apply_corrections(finding_set, " +
                          "selected=[...], dry_run=true) then the same with dry_run=false then audit_model " +
                          "again over those elements.",
                Limits = new[]
                {
                    "The count of findings NOT selected travels all the way to the end on purpose. A " +
                    "re-audit that only reports what it fixed is how a partial pass becomes a claim about " +
                    "the model.",
                    "A correction applies to the elements a finding names. It does not generalise to " +
                    "elements that look similar, and this route never widens a selection on its own."
                }
            },

            new Procedure
            {
                Id = "model-health-audit",
                Title = "Model Health Audit",
                Version = 2,
                Permission = "read_only",
                Outcome = "A measured picture of weight, hygiene, links and readiness.",
                Tools = new[] { "horizun_model_scan", "horizun_audit_model" },
                Inputs = new[] { "the document, by title", "which checks to run, or all of them" },
                Scope = "The whole document, read only.",
                Output = "A scan with counts and a finding set with element ids.",
                Errors = new[]
                {
                    "a check that could not read what it needed - reported as NOT COVERED, which is not the " +
                    "same as a clean result and must never be rendered as one"
                },
                DependsOn = new string[0],
                Acceptance = "Every check reports either a measurement or an explicit non-coverage. No check is " +
                             "silently absent.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_model_scan",
                        // target_document_title, not target_document: the scan's own schema
                        // names it that, and it aborts when the active document differs.
                        ArgumentsJson = @"{ ""target_document_title"": { ""$input"": ""document"" } }",
                        Purpose = "measure the shape of the document: counts, weight, links, worksets, phases.",
                        Needs = "the document title from step 1.",
                        Preconditions = "none. This reads.",
                        OnError = "a scan section that could not read its subject reports NOT COVERED with the reason. " +
                                  "Treat a missing section as missing, never as zero.",
                        ReadsBack = "counts per category, link status per link, and the sections that could not be " +
                                    "covered."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_audit_model",
                        ArgumentsJson = @"{ ""target_document"": { ""$input"": ""document"" } }",
                        Purpose = "measure hygiene against the checks that were asked for.",
                        Needs = "the document and the link coverage from step 2: an unloaded link is a part of the " +
                                "model this audit did not see, and the report has to say so.",
                        Preconditions = "which checks to run is decided. Running all of them is a valid choice made on " +
                                        "purpose.",
                        OnError = "a check that throws is reported as a check that did not run. The audit never returns " +
                                  "a shorter list quietly.",
                        ReadsBack = "per check, a measurement or an explicit non-coverage. Never silence."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the document to audit. It must be the ACTIVE one: both tools abort rather than measure a model nobody asked about."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 3, Path = "coverage_complete", Expect = "true",
                        Why = "a check that could not read what it needed is NOT a clean result, and this " +
                              "field is how the audit says so. Reported by AuditModelCommand beside " +
                              "checks_with_incomplete_coverage."
                    },
                    new AcceptanceCheck
                    {
                        Step = 3, Path = "checks_run", Expect = "non_empty",
                        Why = "an audit that ran no checks produces no findings and looks identical to a " +
                              "clean model."
                    }
                },
                Example = "health then model_scan(document) then audit_model(checks=all) and reconcile the two " +
                          "coverage statements against each other.",
                Limits = new[]
                {
                    "Coverage is the whole point of this procedure. A model that reports zero findings " +
                    "because six checks could not run is not a healthy model, and the two are trivially " +
                    "confused by anyone reading only the totals.",
                    "An unloaded link is not zero clashes and not a clean discipline. It is a part of the " +
                    "model nobody looked at."
                }
            },

            new Procedure
            {
                Id = "sheet-qaqc",
                Title = "Sheet QA/QC",
                Version = 2,
                Permission = "read_only",
                Outcome = "Sheets, views and annotation checked against measurable layout rules.",
                Tools = new[] { "horizun_query_planimetry", "horizun_audit_planimetry", "horizun_capture_view" },
                Inputs = new[] { "the sheets, by id", "the overlap tolerance, in millimetres" },
                Scope = "Sheets and what is on them.",
                Output = "An overlap and completeness report, plus a capture of each sheet.",
                Errors = new[]
                {
                    "a sheet with a placeholder title block - reported, because it prints",
                    "zero words outside the page is NOT zero overlaps: both are measured and reported separately"
                },
                DependsOn = new string[0],
                Acceptance = "Every sheet has a capture, and every overlap above tolerance is listed with the " +
                             "two elements that overlap.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_query_planimetry",
                        // mode=sheets is the population this route is about. Each
                        // mode answers about ONE kind of thing, and every row carries
                        // entity_kind so a list never mixes two without a
                        // discriminator.
                        ArgumentsJson = @"{
  ""mode"": ""sheets"",
  ""units"": ""mm""
}",
                        Purpose = "inventory what is actually on each sheet before measuring anything.",
                        Needs = "the document from step 1 and the sheet ids, which arrive as an input.",
                        Preconditions = "none.",
                        OnError = "a sheet that will not read is named. An empty inventory is not an empty sheet.",
                        ReadsBack = "per sheet: its title block, its viewports, its annotation, and the real paper size."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_audit_planimetry",
                        ArgumentsJson = @"{
  ""scope"": ""sheets"",
  ""units"": ""mm"",
  ""include_passed_checks"": true
}",
                        Purpose = "measure overlaps and off-sheet content at the declared tolerance.",
                        Needs = "the sheet ids and the paper size from step 2.",
                        Preconditions = "the tolerance is declared in millimetres. A tolerance nobody stated is a result " +
                                        "nobody can interpret.",
                        OnError = "findings are findings. A sheet with a placeholder title block is reported because it " +
                                  "prints that way.",
                        ReadsBack = "every overlap above tolerance with BOTH elements named, and off-sheet text counted " +
                                    "separately."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_capture_view",
                        ArgumentsJson = @"{ ""view_id"": { ""$input"": ""sheet_view_id"" } }",
                        Purpose = "LOOK at each sheet. An audit checks what it was told to check.",
                        Needs = "the sheet ids from step 2.",
                        Preconditions = "none.",
                        OnError = "a capture that fails is a sheet nobody looked at. Do not sign off on it.",
                        ReadsBack = "one PNG per sheet with its pixel calibration, so a measured distance in the image " +
                                    "means something."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""sheet_view_id""],
  ""properties"": {
    ""sheet_view_id"": { ""type"": ""integer"",
      ""description"": ""The sheet captured as evidence. Required: a QA route whose reply is a list of findings and no picture is one nobody acts on, and picking a sheet here would be this catalogue choosing which one mattered."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 3, Path = "coverage_complete", Expect = "true",
                        Why = "an audit that could not read part of the set has not measured it, and a " +
                              "check with no population reports not_applicable rather than passing."
                    }
                },
                Example = "health then query_planimetry(sheets) then audit_planimetry(sheets, tolerance_mm=2) " +
                          "then capture_view(each sheet).",
                Limits = new[]
                {
                    "Zero words outside the page is NOT zero overlaps. They are different measurements, " +
                    "they are reported separately, and a sheet has repeatedly passed one while failing " +
                    "the other.",
                    "A title block whose label text overflows WIDENS the sheet bounding box, so an " +
                    "overlap test against the title block can pass while the text is plainly over the " +
                    "border. The real paper is SHEET_WIDTH and SHEET_HEIGHT, which is what step 2 reads.",
                    "This route measures. It changes nothing, which is why planimetry-review exists."
                }
            },

            new Procedure
            {
                Id = "family-qaqc",
                Title = "Family QA/QC",
                Version = 2,
                Permission = "read_only",
                Outcome = "Measurable family hygiene: in-place content, duplicates, unused types.",
                Tools = new[] { "horizun_model_scan", "horizun_query_model" },
                Inputs = new[] { "the document", "the naming convention to measure against, as an input" },
                Scope = "Families and types. Instances are counted, not judged.",
                Output = "A census by family, with counts and the types nothing uses.",
                Errors = new[] { "a family that cannot be opened for inspection - named, never counted as clean" },
                DependsOn = new string[0],
                Acceptance = "The census totals reconcile with the document's own element count.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_model_scan",
                        // target_document_TITLE, not target_document. This tool
                        // predates that convention and declares the older name, so
                        // the template uses what the schema says rather than what
                        // the rest of the bridge says - which is precisely the kind
                        // of mismatch the catalogue's template audit catches.
                        ArgumentsJson = @"{
  ""target_document_title"": { ""$input"": ""document"" },
  ""response_mode"": ""summary""
}",
                        Purpose = "census the families and types, with instance counts.",
                        Needs = "the document title from step 1.",
                        Preconditions = "none.",
                        OnError = "a family that cannot be opened for inspection is NAMED. It is never counted as " +
                                  "clean, and never dropped from the total.",
                        ReadsBack = "per family: its types, how many instances each type has, and which families are " +
                                    "in-place."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_query_model",
                        ArgumentsJson = @"{
  ""categories"": { ""$input"": ""categories"" },
  ""response_mode"": ""summary""
}",
                        Purpose = "measure the census against the naming convention and find what nothing uses.",
                        Needs = "the family and type ids from step 2.",
                        Preconditions = "the naming convention arrives as an input. The bridge has none of its own.",
                        OnError = "a name that does not parse under the convention is a finding about the name, unless " +
                                  "the convention is wrong, which is a finding about the convention.",
                        ReadsBack = "types with zero instances, near-duplicate families, and the count that must " +
                                    "reconcile with the document own element total."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""categories""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document."" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""The categories whose families are counted. Required: a family census over a whole model reports a number nobody can check against anything."" }
  },
  ""additionalProperties"": false
}",
                Example = "health then model_scan(document) then query_model over the returned family ids, " +
                          "measured against your naming file.",
                Limits = new[]
                {
                    "Instances are counted, not judged. Whether a family SHOULD be used somewhere is a " +
                    "project decision and this route does not hold an opinion.",
                    "A type with zero instances is not automatically deletable: schedules, legends and " +
                    "other families reference types that nothing places."
                }
            },

            new Procedure
            {
                Id = "parameter-compliance",
                Title = "Parameter Compliance",
                Version = 2,
                Permission = "read_only_to_safe_write",
                Outcome = "An explicit parameter standard measured, then corrected under review.",
                Tools = new[] { "horizun_query_model", "horizun_write_params_verified" },
                Inputs = new[] { "the parameter standard - names, types and required values - as an input",
                                 "the element selection, by id or by a declared filter" },
                Scope = "Only the named parameters on the named elements.",
                Output = "A compliance table and, after review, verified writes.",
                Errors = new[]
                {
                    "a parameter that is read-only on the element - named per element",
                    "a Model Group member - the write reaches the group's other instances, and the tool says " +
                    "so before it writes"
                },
                DependsOn = new string[0],
                Acceptance = "Every written value is re-read from the model and matches; every refusal names " +
                             "its element and reason.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_query_model",
                        ArgumentsJson = @"{
  ""categories"": { ""$input"": ""categories"" },
  ""parameters"": { ""$input"": ""parameters"" },
  ""response_mode"": ""full""
}",
                        Purpose = "measure what the named parameters currently hold, before proposing any change.",
                        Needs = "the document from step 1, plus the parameter standard and the element selection, " +
                                "which arrive as inputs.",
                        Preconditions = "the selection is explicit: element ids, or a filter that is stated in the request. " +
                                        "Never everything.",
                        OnError = "a parameter that does not exist on an element is reported per element. It is not the " +
                                  "same finding as a parameter that exists and is empty, and the fixes differ.",
                        ReadsBack = "a compliance table: per element, per parameter, the current value and whether it " +
                                    "matches."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_write_params_verified",
                        // WHICH ELEMENTS GET WHICH VALUE IS NOT DERIVABLE FROM A CENSUS.
                        //
                        // Step 2 says which are empty and which disagree with the
                        // standard. What each one SHOULD say is a fact about the
                        // building, and a procedure that derived it would be writing
                        // a value nobody chose onto somebody else's model.
                        RequiresDecision = true,
                        DecisionNeeded = "the writes: element ids, parameter names and values, from the " +
                                         "census in step 2. Filling a blank parameter with a plausible " +
                                         "value is the mistake this stop exists to prevent.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""writes"": { ""$decision"": ""writes"" },
  ""on_failure"": ""atomic"",
  ""dry_run"": false
}",
                        Purpose = "rehearse the writes.",
                        Needs = "the non-compliant rows from step 2 that a person approved.",
                        Preconditions = "dry_run. The rehearsal is also where group membership surfaces, which is the thing " +
                                        "that surprises people.",
                        OnError = "a read-only parameter is named per element. A Model Group member is flagged BEFORE " +
                                  "the write, because the value reaches every other instance of that group.",
                        ReadsBack = "per row: what would be written, and every warning the write would raise."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_write_params_verified",
                        // THE SAME WRITES AS THE REHEARSAL, by reference to the same
                        // decision. Asking again here would be a second chance to
                        // change what step 3 actually rehearsed, and the rehearsal
                        // would then be evidence about a different call.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""writes"": { ""$decision"": ""writes"", ""from_step"": 3 },
  ""on_failure"": ""atomic"",
  ""dry_run"": false
}",
                        Purpose = "write them.",
                        Needs = "the approved, rehearsed rows from step 3.",
                        Preconditions = "step 3 is clean or its warnings were accepted knowingly.",
                        OnError = "the batch reports which rows landed. Re-run the remainder with the same " +
                                  "idempotency_key rather than re-sending the whole set.",
                        ReadsBack = "every written value re-read from the model after the commit, plus every refusal with " +
                                    "its element and reason."
                    },
                    new Step
                    {
                        N = 5, Tool = "horizun_query_model",
                        // THE SAME CATEGORIES AND THE SAME PARAMETERS AS STEP 2, from
                        // the same inputs. A verification that read different fields
                        // would be measuring something else and reporting it as proof.
                        ArgumentsJson = @"{
  ""categories"": { ""$input"": ""categories"" },
  ""parameters"": { ""$input"": ""parameters"" },
  ""response_mode"": ""full""
}",
                        Purpose = "measure again, independently of the reply that did the writing.",
                        Needs = "the element ids from step 4 and the same standard as step 2.",
                        Preconditions = "the writes committed.",
                        OnError = "a value that reads back differently from what step 4 reported is the most important " +
                                  "result this route can produce. Stop and report it.",
                        ReadsBack = "the compliance table from step 2, re-measured."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""categories"", ""parameters""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document. Step 3 writes and names it."" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""The categories the compliance census covers. Required: 'the whole model' is not a scope for a route that ends in a write."" },
    ""parameters"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""The parameters to read back. A parameter the element does not carry is reported as missing, and one that exists and will not answer is reported as unreadable - which is its own finding and is never allowed to read as agreement."" }
  },
  ""additionalProperties"": false
}",
                Example = "health then query_model(selection, standard) then write_params_verified(approved " +
                          "rows, dry_run=true) then the same with dry_run=false then query_model again.",
                Limits = new[]
                {
                    "Writing a parameter on a Model Group member reaches every instance of that group. " +
                    "The rehearsal says so; a batch approved without reading the rehearsal will not.",
                    "Some parameters are read-only for a reason that is per element, not per type: a " +
                    "value driven by a formula, by a host, or by a workset somebody else owns.",
                    "The standard is an input. This route measures compliance with what it was given and " +
                    "makes no claim that the standard itself is right."
                }
            },

            new Procedure
            {
                Id = "room-area-audit",
                Title = "Room and Area Audit",
                Version = 2,
                Permission = "read_only",
                Outcome = "Placement, enclosure and data completeness for rooms and areas.",
                Tools = new[] { "horizun_query_model", "horizun_audit_model" },
                Inputs = new[] { "the phase", "the area scheme, when areas are in scope" },
                Scope = "Rooms and areas in the named phase.",
                Output = "A table of unplaced, unenclosed and redundant rooms.",
                Errors = new[] { "an unplaced room has no geometry: it is listed apart from unenclosed ones, " +
                                 "because the fixes are different" },
                DependsOn = new string[0],
                Acceptance = "Counts of placed, unplaced, unenclosed and redundant sum to the document total.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_query_model",
                        ArgumentsJson = @"{
  ""categories"": [""OST_Rooms""],
  ""level"": { ""$input"": ""level_name"" },
  ""response_mode"": ""full""
}",
                        Purpose = "list rooms and areas in the named phase, with their placement state.",
                        Needs = "the document from step 1, plus the phase and, when areas are in scope, the area " +
                                "scheme.",
                        Preconditions = "the phase is named. A room is placed or unplaced PER PHASE, so a phase-less question " +
                                        "has no answer.",
                        OnError = "an area scheme that does not exist is named. Do not fall back to the first one.",
                        ReadsBack = "per room and area: placed, unplaced, and the level each belongs to."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_audit_model",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""top"": 50
}",
                        Purpose = "measure enclosure and redundancy for what step 2 found placed.",
                        Needs = "the room and area ids from step 2, split by placement state.",
                        Preconditions = "none.",
                        OnError = "an unenclosed room has geometry that leaks; an unplaced room has none at all. They " +
                                  "are reported apart because the fix for one is a boundary and for the other is a " +
                                  "placement.",
                        ReadsBack = "counts of placed, unplaced, unenclosed and redundant, which must sum to the document " +
                                    "total."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""level_name""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document."" },
    ""level_name"": { ""type"": ""string"",
      ""description"": ""The storey whose rooms are read. Required: an area audit over a whole tower reports a number nobody can check against a drawing."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 3, Path = "coverage_complete", Expect = "true",
                        Why = "an audit that could not read part of the model has not measured it. A check " +
                              "with no population reports not_applicable and is never a pass."
                    }
                },
                Example = "health then query_model(phase=Nueva construccion, scheme=...) then audit_model over " +
                          "the returned rooms, keeping the four counts separate.",
                Limits = new[]
                {
                    "The four states are kept apart the whole way through. Summing them into one number " +
                    "of bad rooms hides which work is needed.",
                    "A redundant room is a modelling decision somebody made, not automatically a defect."
                }
            },

            new Procedure
            {
                Id = "quantity-export-pack",
                Title = "Quantity and Power BI Pack",
                Version = 2,
                Permission = "read_only_to_full_write",
                Outcome = "A take-off measured first, exported only after the destination is approved.",
                Tools = new[] { "horizun_quantities", "horizun_budget_compare", "horizun_power_bi_push" },
                Inputs = new[] { "the take-off scope", "the destination workbook or dataset",
                                 "the budget to compare against, when comparing" },
                Scope = "Reading the model; writing only to the named destination.",
                Output = "Quantities, a comparison, and rows in the destination.",
                Errors = new[]
                {
                    "a destination the permission profile does not authorize - refused before anything is read",
                    "a retry after a timeout - the push is idempotent by key and does not double rows"
                },
                DependsOn = new string[0],
                Acceptance = "The destination's row count and totals match the take-off, re-read from the " +
                             "destination rather than from the call.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_quantities",
                        Purpose = "measure the take-off from the model.",
                        Needs = "the document from step 1 and the take-off scope, which arrives as an input.",
                        Preconditions = "the scope is explicit. A take-off of everything is a number nobody can check.",
                        OnError = "an element whose quantity cannot be computed is listed, with the reason, and " +
                                  "excluded from the total rather than counted as zero.",
                        ReadsBack = "quantities by the declared grouping, with the element count behind each row."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_budget_compare",
                        Purpose = "compare the measurement against the budget, when there is one.",
                        Needs = "the take-off rows from step 2.",
                        Preconditions = "the budget arrives as an input with its own units. Comparing a volume against an " +
                                        "area is a silent error and the comparison refuses it.",
                        OnError = "a budget line with no matching take-off row is reported as unmatched IN BOTH " +
                                  "DIRECTIONS. A one-sided comparison reads as agreement.",
                        ReadsBack = "per line: model quantity, budget quantity, difference, and what matched nothing."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_power_bi_push",
                        Purpose = "write to the destination, and only then.",
                        Needs = "the take-off from step 2 and the comparison from step 3.",
                        Preconditions = "the destination is authorized by the permission profile BEFORE anything is read. " +
                                        "This is the only step that leaves the machine.",
                        OnError = "a retry after a timeout is idempotent by key and does not double the rows. " +
                                  "Re-sending without the key does.",
                        ReadsBack = "the destination row count and totals, re-read FROM the destination rather than taken " +
                                    "from the call that wrote them."
                    }
                },
                Example = "health then quantities(scope) then budget_compare(those rows, budget file) then " +
                          "power_bi_push(dataset, rows, idempotency_key=...).",
                Limits = new[]
                {
                    "Step 4 is the only one that sends data anywhere. Everything before it is local, and " +
                    "a run that stops after step 3 has cost nothing outside this machine.",
                    "A push that reports success is not a dataset that holds the rows. Only the read-back " +
                    "is, and it is part of the step rather than an optional extra."
                }
            },

            new Procedure
            {
                Id = "dwg-to-bim-review",
                Title = "DWG to BIM",
                Version = 2,
                Permission = "read_only_to_safe_write",
                Outcome = "Native elements built from a CAD drawing against an explicit requirement set.",
                Tools = new[] { "horizun_query_cad", "horizun_plan_from_cad", "horizun_apply_cad_plan",
                                "horizun_audit_cad_model" },
                Inputs = new[] { "the CAD link, by id", "the layer-to-category mapping, as an input",
                                 "the level and the wall/floor types to build with" },
                Scope = "Only the layers the mapping names.",
                Output = "Native elements with recorded CAD provenance, and an audit of what was not converted.",
                Errors = new[]
                {
                    "an ambiguous run - two parallel polylines with no closure are reported for review BEFORE " +
                    "anything is written, never guessed",
                    "a layer whose name does not match its content - the mapping is the caller's, and the " +
                    "mismatch is reported rather than corrected"
                },
                DependsOn = new string[0],
                Acceptance = "Every converted element carries its CAD provenance, and every unconverted entity " +
                             "is listed with the reason.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_query_cad",
                        ArgumentsJson = @"{ ""mode"": ""layers"", ""instance_id"": { ""$input"": ""instance_id"" } }",
                        Purpose = "read what the CAD link actually contains, layer by layer.",
                        Needs = "the document from step 1 and the CAD link id, which arrives as an input.",
                        Preconditions = "the link is LOADED. An unloaded link reads as an empty drawing.",
                        OnError = "a layer whose name does not match its content is reported. The mapping is the caller " +
                                  "decision and this step does not correct it.",
                        ReadsBack = "per layer: entity counts by type, and the geometry that is not closed."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_plan_from_cad",
                        // The requirement set travels as ONE input. It is the caller's
                        // artefact and this catalogue compiles no organisation's layer
                        // convention into itself - a template that filled in a mapping
                        // here would convert the next office's drawing wrong, into a
                        // model that looked entirely plausible.
                        ArgumentsJson = @"{
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""requirement_set"": { ""$input"": ""requirement_set"" },
  ""target_document"": { ""$input"": ""document"" },
  ""level_name"": { ""$input"": ""level_name"" }
}",
                        Purpose = "work out what would be built, without building it.",
                        Needs = "the layer inventory from step 2, plus the layer-to-category mapping, the level and " +
                                "the types to build with.",
                        Preconditions = "the mapping names layers that step 2 actually found. A mapping entry matching " +
                                        "nothing is a finding about the mapping.",
                        OnError = "an ambiguous run - two parallel polylines with no closure - is reported for REVIEW. " +
                                  "It is never resolved by guessing which pair is a wall.",
                        ReadsBack = "per planned element: its geometry, its type, its level, and the CAD entity it came " +
                                    "from."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_apply_cad_plan",
                        // EVERY BINDING FIELD COMES FROM STEP 3'S OWN REPLY, by
                        // reference. Re-deriving them here would be a second opinion
                        // about what the plan said, and the whole point of the binding
                        // is that the apply refuses when the two differ.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""requirement_set"": { ""$input"": ""requirement_set"" },
  ""apply_binding"": { ""$ref"": { ""step"": 3, ""path"": ""apply_binding"" } },
  ""actions"": { ""$ref"": { ""step"": 3, ""path"": ""execute_plan_request.actions"" } },
  ""candidate_index"": { ""$ref"": { ""step"": 3, ""path"": ""candidate_index"" } },
  ""dry_run"": false
}",
                        Purpose = "build them.",
                        Needs = "the plan from step 3, unchanged, after somebody read the ambiguities.",
                        Preconditions = "dry_run first. This is the first step that writes to the model.",
                        OnError = "the reply names what landed. Re-run the remainder with the same idempotency_key; " +
                                  "re-sending the whole plan builds the successful part twice.",
                        ReadsBack = "each created element id with its recorded CAD provenance, re-read after the commit."
                    },
                    new Step
                    {
                        N = 5, Tool = "horizun_audit_cad_model",
                        ArgumentsJson = @"{
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""requirement_set"": { ""$input"": ""requirement_set"" }
}",
                        Purpose = "measure what was NOT converted, which is the half that gets forgotten.",
                        Needs = "the layer inventory from step 2 and the created elements from step 4.",
                        Preconditions = "the apply committed.",
                        OnError = "an unconverted entity with no reason is a defect in this route, not an acceptable " +
                                  "result.",
                        ReadsBack = "every CAD entity accounted for: converted with its element id, or unconverted with " +
                                    "its reason."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""instance_id"", ""requirement_set"", ""level_name""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document. Every write below names it."" },
    ""instance_id"": { ""type"": ""integer"", ""description"": ""The CAD instance. List them with horizun_query_cad mode='instances'; there is no default drawing."" },
    ""requirement_set"": { ""type"": ""object"", ""description"": ""The versioned DWG-to-BIM mapping. The caller's artefact: no layer convention is compiled into this bridge, and one that was would convert the next office's drawing wrong into a model that looked plausible."" },
    ""level_name"": { ""type"": ""string"", ""description"": ""The storey to build on, for rules that do not declare one. A 2D drawing carries no level, and without this the plan REFUSES rather than choosing a storey in somebody's building. REQUIRED by this route: step 3 references it, and a step whose input nobody supplied blocks the run rather than falling back."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 4, Path = "stages_failed", Expect = "zero",
                        Why = "a non-zero stages_failed means part of the conversion did not land. It is not " +
                              "the whole criterion: that one is about every converted element carrying its " +
                              "provenance and every unconverted entity having a reason, and reading those is " +
                              "still somebody's job."
                    }
                },
                Example = "health then query_cad(link) then plan_from_cad(mapping, level, types) then " +
                          "apply_cad_plan(dry_run=true, then false) then audit_cad_model(link).",
                Limits = new[]
                {
                    "The layer-to-category mapping is the caller judgement about somebody else drawing. " +
                    "This route reports where the drawing disagrees with it and does not silently repair " +
                    "either one.",
                    "CAD provenance is what makes a second run able to tell a re-import from a duplicate. " +
                    "An element created outside this route has none."
                }
            },

            new Procedure
            {
                Id = "dwg-mep-unit-conversion",
                Title = "DWG MEP Units to a Connected Model",
                Version = 1,
                Permission = "read_only_to_safe_write",
                Outcome = "MEP built from a unit drawing as a CONNECTED network, with the repeated layouts " +
                          "recognised rather than converted one by one.",
                Tools = new[] { "horizun_health", "horizun_cad_extract", "horizun_cad_networks",
                                "horizun_cad_unit_instances", "horizun_plan_from_cad", "horizun_apply_cad_plan",
                                "horizun_cad_connect", "horizun_audit_cad_model", "horizun_cad_review" },
                Inputs = new[] { "the CAD link, by id",
                                 "the requirement set: what each layer MEANS, with system, bore and elevation",
                                 "the unit boundaries, as regions the caller draws",
                                 "the level to build on",
                                 "OPTIONAL, and required for anything that drains: the outfall - where the " +
                                 "network leaves - and its invert. Without it every run is built LEVEL, " +
                                 "which for sanitary or storm is the whole design missing" },
                Scope = "Only the layers the requirement set names, inside the regions the caller gave.",
                Output = "MEP curves with recorded CAD provenance, joined at their junctions, plus the list of " +
                         "junctions, crossings and gaps that were deliberately NOT joined.",
                Errors = new[]
                {
                    "a crossing with no shared endpoint is never joined - in a plan that is two services at " +
                    "different heights, and a tee there routes waste through a water main",
                    "a junction whose runs were declared at different elevations is a riser, not an elbow, " +
                    "and it holds for a person",
                    "a region that matches a unit type's signature and fits no rigid transform is a VARIANT " +
                    "and is read on its own, never instantiated from the type"
                },
                DependsOn = new string[0],
                Acceptance = "Every automatic junction is joined and re-read from the model; the review in " +
                             "step 9 reports no junction as not_made; every junction, crossing and gap that " +
                             "was not joined is listed with its reason; every unit occurrence marked " +
                             "exact reproduces its region's geometry; and where an outfall was named, the " +
                             "review's invert check reports no junction as disagreeing - both sides of that " +
                             "comparison being inverts, not centrelines.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every write below names a target_document.",
                        ReadsBack = "the document title, the Revit year, and the bridge's own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_cad_extract",
                        ArgumentsJson = @"{ ""instance_id"": { ""$input"": ""instance_id"" } }",
                        Purpose = "find out what this reading of the drawing can and cannot reach, BEFORE " +
                                  "anything depends on it.",
                        Needs = "the CAD instance, which arrives as an input.",
                        Preconditions = "the link is LOADED. An unloaded link reads as an empty drawing, and " +
                                        "an empty drawing and an unreadable one look the same to everything " +
                                        "downstream.",
                        OnError = "a reader blind to an axis a later step needs is a reason to stop here, not " +
                                  "to run the later step and read its zero as a finding.",
                        ReadsBack = "per axis - geometry, layers, text, block names, handles, units and the " +
                                    "rest - supplied, absent from the drawing, partial, or unavailable from " +
                                    "this reader, each with its evidence. Read blind_to first."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_cad_networks",
                        // THE REQUIREMENT SET, NOT A SECOND SET OF DECLARATIONS.
                        // What a layer's system, bore and height are is said once,
                        // in the artefact the conversion will build from - otherwise
                        // a run is BUILT at one height by step 5 and CONNECTED at
                        // another by this one, and both replies look correct because
                        // each agrees with its own input.
                        ArgumentsJson = @"{
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""layers"": { ""$input"": ""mep_layers"" },
  ""requirement_set"": { ""$input"": ""requirement_set"" }
}",
                        Purpose = "read the drawing as runs and junctions, and find out how much of it can be " +
                                  "connected without a person.",
                        Needs = "the instance and the requirement set, which is where what each layer IS " +
                                "is said ONCE. Nothing is inferred from a layer name here or anywhere in " +
                                "this bridge.",
                        Preconditions = "the declarations name a system for every layer that will be built. " +
                                        "Revit will not create a pipe or a duct without a system type, and a " +
                                        "drawn line carries no width.",
                        OnError = "a component with two declared systems is a contradiction Revit cannot hold. " +
                                  "It is named, not resolved: either the declarations are wrong or the " +
                                  "drawing joins two things that are separate.",
                        ReadsBack = "runs, junctions by kind, connection intents, and - the half that matters " +
                                    "- crossings_not_connected, unresolved_gaps and " +
                                    "connections_needing_review."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_cad_unit_instances",
                        ArgumentsJson = @"{
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""regions"": { ""$input"": ""regions"" },
  ""layers"": { ""$input"": ""mep_layers"" }
}",
                        Purpose = "find which regions are the SAME layout, so one careful reading covers many " +
                                  "apartments instead of one.",
                        Needs = "the regions, which the caller draws. This step does not invent them: where " +
                                "one unit stops and the corridor begins is a reading of the building.",
                        Preconditions = "the regions enclose the geometry being compared. A boundary that " +
                                        "encloses nothing compares as empty and may match another empty one.",
                        OnError = "a NEAR MISS - same signature, no fitting transform - is two apartments that " +
                                  "differ. It is reported, never rounded to a match.",
                        ReadsBack = "types with their signatures, occurrences with their exact rigid " +
                                    "transform, near misses, and unrecognised regions. Types are UNNAMED: the " +
                                    "name lives in the drawing's text, which step 2 says whether this reader " +
                                    "can reach."
                    },
                    new Step
                    {
                        N = 5, Tool = "horizun_plan_from_cad",
                        ArgumentsJson = @"{
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""requirement_set"": { ""$input"": ""requirement_set"" },
  ""target_document"": { ""$input"": ""document"" },
  ""level_name"": { ""$input"": ""level_name"" },
  ""outfall"": { ""$input"": ""outfall"", ""when_missing"": ""omit"" },
  ""outfall_invert_mm"": { ""$input"": ""outfall_invert_mm"", ""when_missing"": ""omit"" }
}",
                        Purpose = "work out what would be built, without building it - and, where an outfall " +
                                  "is given, at what HEIGHT each run would be built.",
                        Needs = "the same requirement set step 3 read. That is the point of passing it to " +
                                "both: the network reading and the plan resolve every layer through one " +
                                "artefact, by one precedence, so they cannot disagree about a layer. FOR A " +
                                "SYSTEM THAT DRAINS, supply 'outfall' and 'outfall_invert_mm' as inputs to " +
                                "the run - the same two the network reading takes. They are OPTIONAL: the " +
                                "template omits them when the run does not carry them, so the same " +
                                "procedure converts a pressure system, which has no outfall, without " +
                                "refusing.",
                        Preconditions = "the network reading in step 3 was looked at. The plan builds runs; " +
                                        "it does not know which junctions a person has accepted. For a " +
                                        "system that drains, the outfall is named here and every layer that " +
                                        "falls declares a slope: without the outfall the runs are built " +
                                        "LEVEL, and a level drain is connected, passes every other check in " +
                                        "this procedure, and drains nowhere. A run whose bore is not " +
                                        "declared takes no fall either - an invert cannot be turned into a " +
                                        "centreline without one.",
                        OnError = "an ambiguous reading is deferred for review and NOT planned. That is the " +
                                  "difference between a route you can leave running and one you must watch.",
                        ReadsBack = "the staged actions, the deferred candidates with their reasons, and the " +
                                    "apply binding that ties this plan to these bytes of this drawing."
                    },
                    new Step
                    {
                        N = 6, Tool = "horizun_apply_cad_plan",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""requirement_set"": { ""$input"": ""requirement_set"" },
  ""apply_binding"": { ""$ref"": { ""step"": 5, ""path"": ""apply_binding"" } },
  ""actions"": { ""$ref"": { ""step"": 5, ""path"": ""execute_plan_request.actions"" } },
  ""candidate_index"": { ""$ref"": { ""step"": 5, ""path"": ""candidate_index"" } },
  ""dry_run"": false
}",
                        Purpose = "build the runs, each stamped with the drawing it came from.",
                        Needs = "step 5's plan, unchanged. A caller that edits a coordinate is refused: the " +
                                "binding covers the exact actions emitted.",
                        Preconditions = "this is the first step that writes.",
                        OnError = "the reply names which stages landed. Re-run the remainder with the same " +
                                  "idempotency_key; re-sending the whole plan builds the successful part twice.",
                        ReadsBack = "each created element id with its CAD provenance, re-read after the commit."
                    },
                    new Step
                    {
                        N = 7, Tool = "horizun_cad_connect",
                        // THIS USED TO BE A DECISION STOP, AND THE REASON FOR IT IS GONE.
                        //
                        // The junctions of step 3 name RUNS and the elements exist only
                        // after step 6, so somebody had to map one to the other by
                        // reading two replies side by side - on the one step where a
                        // wrong match joins the wrong two pipes invisibly.
                        //
                        // A run now carries the SAME semantic id the conversion stamps
                        // on what it builds: same merge, same tolerance, same identity
                        // function. So step 3's connections array IS this command's
                        // junctions array, and the translation nobody could check is not
                        // performed at all. Step 3 is passed the requirement set, which
                        // is what makes the two tolerances agree; when they do not, its
                        // reply says run_identity.matches_the_conversion is false and
                        // these ids resolve to nothing rather than to the wrong thing.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""junctions"": { ""$ref"": { ""step"": 3, ""path"": ""connections"" } },
  ""dry_run"": false
}",
                        Purpose = "turn pipes that touch into a network.",
                        Needs = "step 3's connections, unchanged, resolved against the elements step 6 built.",
                        Preconditions = "step 6 committed, and step 3 reported " +
                                        "run_identity.matches_the_conversion. A junction naming a run that " +
                                        "was deferred for review and never built is SKIPPED with that " +
                                        "reason - not refused, because the junctions that can be made are " +
                                        "still the ones the drawing shows.",
                        OnError = "an ambiguous connector - two of one element's connectors almost equally " +
                                  "near - is refused, not guessed. On a fitting that is the run outlet and " +
                                  "the branch outlet, and the wrong one flows wrong while looking right.",
                        ReadsBack = "per junction: joined, skipped or refused, with the connection RE-READ " +
                                    "from the model for every direct join - plus skipped_unresolved, which " +
                                    "counts the junctions whose runs the conversion deliberately did not " +
                                    "build."
                    },
                    new Step
                    {
                        N = 8, Tool = "horizun_audit_cad_model",
                        ArgumentsJson = @"{
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""requirement_set"": { ""$input"": ""requirement_set"" }
}",
                        Purpose = "measure what was NOT converted, which is the half that gets forgotten.",
                        Needs = "the same drawing and the same rules the plan used.",
                        Preconditions = "the apply committed.",
                        OnError = "an unconverted entity with no reason is a defect in this route, not an " +
                                  "acceptable result.",
                        ReadsBack = "every CAD entity accounted for: converted with its element id, or " +
                                    "unconverted with its reason."
                    },
                    new Step
                    {
                        N = 9, Tool = "horizun_cad_review",
                        ArgumentsJson = @"{
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""requirement_set"": { ""$input"": ""requirement_set"" },
  ""layers"": { ""$input"": ""mep_layers"" }
}",
                        Purpose = "ask the MODEL whether what was built is a network, which no step before " +
                                  "this one answers.",
                        Needs = "the same drawing and rules every step above used.",
                        Preconditions = "step 7 ran. Reviewing before connecting reports every junction as " +
                                        "not_made, which is true and useless.",
                        OnError = "a junction reported not_made is the finding: the elements are there, they " +
                                  "meet, and their connectors are open. One reported not_proposed is NOT a " +
                                  "finding - the reading refused that junction and an open connector there " +
                                  "is correct.",
                        ReadsBack = "per junction: made, not_made, nothing_built or not_proposed, each with " +
                                    "the distance from the drawing's point to the nearest connector - plus " +
                                    "open connectors on elements this drawing built that sit nowhere near " +
                                    "anything it shows."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""instance_id"", ""requirement_set"", ""mep_layers"", ""regions"", ""level_name""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document."" },
    ""instance_id"": { ""type"": ""integer"", ""description"": ""The CAD instance. List them with horizun_query_cad mode='instances'."" },
    ""requirement_set"": { ""type"": ""object"", ""description"": ""The versioned DWG-to-BIM mapping. It is the ONE place this route learns what a layer means: step 3 reads the network through it and step 5 plans through it, so the two cannot disagree about a layer's system, bore or height."" },
    ""mep_layers"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1, ""description"": ""GLOBS limiting the network and unit readings. REQUIRED: without them the network is read over the title block too, and a step referencing an input nobody supplied blocks the run rather than reading everything."" },
    ""layer_declarations"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""RESERVED. This route derives what each layer is from the requirement set, so that the network reading and the conversion resolve every layer through one artefact. Declarations are for a caller reading a drawing BEFORE a requirement set exists, which is not this route."" },
    ""regions"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""minItems"": 1, ""description"": ""The unit boundaries to compare, each with an id and a closed ring. REQUIRED, because step 4 references it: horizun_cad_unit_instances refuses an empty list rather than inventing outlines, and a drawing with no repeated units belongs on a route without step 4 rather than on this one with a hole in it."" },
    ""level_name"": { ""type"": ""string"", ""description"": ""The storey to build on, for rules that do not declare one. REQUIRED by this route: steps 5 and 6 reference it."" },
    ""outfall"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3, ""description"": ""OPTIONAL, and required for anything that drains: [x, y] in millimetres in the drawing's own coordinates, the point the network leaves by. Give it and step 5 plans every run at the heights its declared slopes imply; leave it out and step 5 omits it entirely - the same procedure converts a pressure system, which has no outfall, without refusing. A drainage layout built level is connected, drains nowhere, and passes every other check in this procedure."" },
    ""outfall_invert_mm"": { ""type"": ""number"", ""description"": ""OPTIONAL: the INVERT - inside bottom - at that point, in the datum the requirement set's elevations use. Every other invert is this plus the fall along the network, and each run is then placed on its CENTRELINE at the invert plus half its declared bore."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 3, Path = "summary.crossing_check_skipped", Expect = "false",
                        Why = "a network reading whose crossing check was skipped reports zero crossings, " +
                              "which reads as 'nothing crosses' and is the opposite of what happened. Raise " +
                              "crossing_check_limit or read in layer-filtered passes."
                    },
                    new AcceptanceCheck
                    {
                        Step = 6, Path = "stages_failed", Expect = "zero",
                        Why = "a non-zero stages_failed means part of the conversion did not land, so the " +
                              "junctions of step 7 name elements that may not exist."
                    },
                    new AcceptanceCheck
                    {
                        Step = 9, Path = "summary.not_made", Expect = "zero",
                        Why = "a junction the drawing shows, whose elements exist and meet, and whose " +
                              "connectors are open. This is the one number that says whether the model is a " +
                              "network rather than a picture of one - and it is measured from the MODEL, " +
                              "after the fact, not from the reply that wrote it."
                    },
                    new AcceptanceCheck
                    {
                        Step = 7, Path = "refused", Expect = "zero",
                        Why = "a refused junction is a place where two pipes touch and are not joined. It is " +
                              "not a failure of the route - it is usually correct - but it is never a " +
                              "connected network, and a run that reports success with refusals in it would " +
                              "say the model is finished when it is not."
                    }
                },
                Example = "health, cad_extract(link), cad_networks(link, requirement_set), " +
                          "cad_unit_instances(link, regions), plan_from_cad, apply_cad_plan(dry_run false), " +
                          "decide the junctions, cad_connect, audit_cad_model, cad_review.",
                Limits = new[]
                {
                    "THE UNIT TYPES HAVE NO NAMES. A unit's name is text in the drawing, and whether text " +
                    "is reachable is a property of the reader - step 2 reports which. A type called 'A1' " +
                    "because it was found first is the kind of plausible wrong answer nobody checks.",
                    "ELEVATION COMES FROM THE DECLARATIONS OR FROM NOWHERE. A plan is drawn flat. A run " +
                    "with no declared elevation is a run whose height nobody stated, which is not the same " +
                    "as one at zero, and this route never fills that in.",
                    "STACKING A UNIT ONTO OTHER FLOORS IS NOT PART OF THIS ROUTE. Which storeys repeat is a " +
                    "fact about the building - a podium with a different core is not a typical floor - and " +
                    "the transform is published so somebody who knows can repeat it deliberately.",
                    "THE RUN IDENTITIES ONLY MATCH THE CONVERSION'S WHEN THE TOLERANCES DO. Step 3 takes " +
                    "them from the requirement set for exactly this reason. Override them and the " +
                    "connections of step 3 name nothing the model holds - which step 3 says outright " +
                    "rather than leaving step 7 to discover.",
                    "A CROSSING IS NEVER JOINED. Two lines crossing in a plan without a shared end are two " +
                    "services at different heights. If one of them should be a tee, the drawing has to say " +
                    "so by breaking the line.",
                    "THE FALL IS NOT PART OF THIS ROUTE, and it is available beside it. " +
                    "horizun_cad_networks computes every invert from a declared OUTFALL and the slope each " +
                    "layer declares - which is the only honest source of direction, because a drawn line " +
                    "has a first point and a second point and that is drawing order. It is not templated " +
                    "here because an outfall is a POINT with no sensible default, and a step referencing an " +
                    "input nobody supplied blocks the run. Call it separately when the drawing is drainage - " +
                    "and give the same outfall to horizun_cad_review, which then checks the model's heights " +
                    "against it. JOINED IS NOT THE SAME AS CORRECT: a drainage network laid perfectly level " +
                    "is connected, flows nowhere, and passes step 9 without that comparison."
                }
            },

            new Procedure
            {
                Id = "dwg-electrical-symbols",
                Title = "DWG Symbols to Family Instances",
                Version = 1,
                Permission = "read_only_to_safe_write",
                Outcome = "Every mark of one symbol placed as the same family, from ONE naming decision.",
                Tools = new[] { "horizun_health", "horizun_cad_extract", "horizun_cad_symbols",
                                "horizun_create_elements", "horizun_query_model" },
                Inputs = new[] { "the CAD link, by id",
                                 "the symbol layers, as globs",
                                 "how large a piece of connected line work may be and still be a symbol",
                                 "a family type for each symbol type the reading finds" },
                Scope = "Only the layers named, and only the groups that fit inside the declared footprint.",
                Output = "Family instances at each mark's centre, rotated as the mark is, each re-read after " +
                         "the commit - plus every group that was rejected, with its reason.",
                Errors = new[]
                {
                    "a group that outgrows the footprint is rejected WHOLE - a symbol touching its home run " +
                    "is connected to the circuit, and half a circuit is not a symbol either",
                    "a MIRRORED occurrence is reported as not placeable: a mirror is not a rotation of a " +
                    "family instance, and placing it rotated puts the symbol's face the wrong way round",
                    "a symbol drawn differently the second time is unrecognised rather than folded in, which " +
                    "on a real drawing is the interesting finding"
                },
                DependsOn = new string[0],
                Acceptance = "Every placement row sent is created and re-read, every rejected group has a " +
                             "reason, and no symbol type was named by this route rather than by a person.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Step 4 writes and names a target_document.",
                        ReadsBack = "the document title, the Revit year, and the bridge's own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_cad_extract",
                        ArgumentsJson = @"{
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""arc_sagitta_mm"": { ""$input"": ""arc_sagitta_mm"" }
}",
                        Purpose = "find out what this reading can reach, and settle the chord tolerance " +
                                  "BEFORE it matters.",
                        Needs = "the CAD instance, which arrives as an input.",
                        Preconditions = "the link is LOADED.",
                        OnError = "a reader blind to text is the normal case here and is not a reason to " +
                                  "stop - it is the reason step 4 needs a person. A reader that returned no " +
                                  "geometry at all IS a reason to stop.",
                        ReadsBack = "the twelve axes with their verdicts. At symbol scale the chord " +
                                    "tolerance matters more than anywhere else: a 5 mm sagitta over a 40 mm " +
                                    "circle is a hexagon, and two instances chorded differently do not match."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_cad_symbols",
                        ArgumentsJson = @"{
  ""instance_id"": { ""$input"": ""instance_id"" },
  ""layers"": { ""$input"": ""symbol_layers"" },
  ""max_footprint_mm"": { ""$input"": ""max_footprint_mm"" },
  ""arc_sagitta_mm"": { ""$input"": ""arc_sagitta_mm"" }
}",
                        Purpose = "group the marks into types, so one decision covers every occurrence.",
                        Needs = "the layers and the footprint, both of which arrive as inputs. Neither has a " +
                                "default: a receptacle is forty millimetres across on one drawing and four " +
                                "hundred on another.",
                        Preconditions = "the layers name symbol layers. Grouping over the whole drawing " +
                                        "makes the wiring one enormous rejected component and buries the " +
                                        "symbols in it.",
                        OnError = "a reading where every group was rejected as too large means the footprint " +
                                  "is too small or the symbols touch their home runs. Both are findings " +
                                  "about the drawing and neither is fixed by raising the bound until " +
                                  "something passes.",
                        ReadsBack = "symbol types with their occurrence counts, the placement rows ready for " +
                                    "step 5 with type_name empty, every rejected group with its reason, and " +
                                    "the unrecognised one-offs."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_create_elements",
                        // THE ONE DECISION, AND IT IS THE POINT OF THE ROUTE.
                        //
                        // What a symbol MEANS lives in the drawing's legend, which is
                        // text, and step 2 reports whether this reader can see text.
                        // So a person names each type once - and that one sentence
                        // applies to every occurrence of it, which is the entire
                        // reason for grouping them in the first place.
                        RequiresDecision = true,
                        DecisionNeeded = "the family type for each symbol type step 3 found, and the level " +
                                         "to place on. Take step 3's placements array, fill in type_name on " +
                                         "each row from the type it belongs to, add level_name, and drop the " +
                                         "rows marked placeable:false - those are mirrored, and a mirror is " +
                                         "not a rotation of a family instance.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""units"": ""mm"",
  ""elements"": { ""$decision"": ""elements"" },
  ""dry_run"": false
}",
                        Purpose = "place them.",
                        Needs = "step 3's placement rows with a type name filled in, and the level.",
                        Preconditions = "the named family types are LOADED in this document. A type name " +
                                        "that matches nothing is refused per row rather than substituted.",
                        OnError = "a refused row names its reason. The batch is atomic, so a refusal leaves " +
                                  "nothing partial behind.",
                        ReadsBack = "created_verified, and a row per element with its id, re-read after the " +
                                    "commit."
                    },
                    new Step
                    {
                        N = 5, Tool = "horizun_query_model",
                        ArgumentsJson = @"{
  ""categories"": { ""$input"": ""categories"" },
  ""level"": { ""$input"": ""level_name"" },
  ""response_mode"": ""summary""
}",
                        Purpose = "count what is now on that level, from the MODEL rather than from the " +
                                  "reply that wrote it.",
                        Needs = "the categories and the level, which arrive as inputs.",
                        Preconditions = "step 4 committed.",
                        OnError = "none: this step only reads.",
                        ReadsBack = "the count per category on that level. It includes anything already " +
                                    "there, so it is a check that the number MOVED by what was placed - not " +
                                    "a proof that only this route placed it."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""instance_id"", ""symbol_layers"", ""max_footprint_mm"", ""categories"", ""level_name""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document."" },
    ""instance_id"": { ""type"": ""integer"", ""description"": ""The CAD instance. List them with horizun_query_cad mode='instances'."" },
    ""symbol_layers"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""GLOBS naming the symbol layers. Required: grouping over the whole drawing makes the wiring one enormous rejected component and buries the symbols in it."" },
    ""max_footprint_mm"": { ""type"": ""number"", ""minimum"": 0.001,
      ""description"": ""How large a piece of connected line work may be and still be a symbol. No default anywhere in this route - a receptacle is forty millimetres across on one drawing and four hundred on another."" },
    ""arc_sagitta_mm"": { ""type"": ""number"", ""minimum"": 0.001, ""default"": 0.5,
      ""description"": ""How far a chord may depart from the arc it replaces. At symbol scale this is the setting that decides whether two instances match at all: 5 mm over a 40 mm circle is a hexagon."" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""The categories step 5 counts on the level. They are the caller's because which category a symbol becomes is part of the naming decision."" },
    ""level_name"": { ""type"": ""string"", ""description"": ""The storey the instances sit on."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 4, Path = "created_verified", Expect = "non_empty",
                        Why = "the count of instances created AND re-read from the model after the commit. " +
                              "Zero means the batch placed nothing, which with a decision that supplied rows " +
                              "means every row was refused - usually a family type name that matches nothing " +
                              "loaded in this document."
                    }
                },
                Example = "health, cad_extract(link), cad_symbols(link, layers, footprint) → 6 types, 412 " +
                          "occurrences, 9 rejected → name the 6 types → create_elements(the rows) → " +
                          "query_model on that level.",
                Limits = new[]
                {
                    "ONE NAMING DECISION, NOT NONE. This route never guesses what a symbol means. The " +
                    "legend that says so is text, and whether text is reachable is a property of the " +
                    "reader - horizun_cad_extract reports which.",
                    "horizun_create_elements takes at most 2000 elements per call. A drawing with more " +
                    "occurrences than that is sent in batches, and each batch is its own atomic " +
                    "transaction - so a partial result is possible across batches and is not across one.",
                    "A MIRRORED occurrence is not placeable as a rotation and is excluded by the decision. " +
                    "It needs a flipped instance or a different family, and it looks identical on the " +
                    "drawing.",
                    "The count in step 5 includes what was already on that level. It checks that the number " +
                    "MOVED, not that this route placed everything there."
                }
            },

            new Procedure
            {
                Id = "planimetry-review",
                Title = "Planimetry Review",
                Version = 2,
                Permission = "read_only_to_safe_write",
                Outcome = "Sheets inspected and corrected with cited, rehearsed, verified fixes.",
                Tools = new[] { "horizun_audit_planimetry", "horizun_capture_view", "horizun_fix_planimetry" },
                Inputs = new[] { "the sheets", "the tolerance", "which finding classes may be auto-fixed" },
                Scope = "Annotation and viewport placement. No model geometry.",
                Output = "Corrected sheets and a before/after capture of each.",
                Errors = new[] { "a fix that would move an annotation outside its view's crop - refused with " +
                                 "the measured distance" },
                DependsOn = new[] { "sheet-qaqc" },
                Acceptance = "The re-audit reports zero findings in the classes that were authorized, and the " +
                             "captures show it.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_audit_planimetry",
                        ArgumentsJson = @"{
  ""scope"": { ""$input"": ""scope"" },
  ""units"": ""mm"",
  ""include_passed_checks"": true
}",
                        Purpose = "measure the sheets before touching them.",
                        Needs = "the document from step 1, the sheet ids and the tolerance.",
                        Preconditions = "the tolerance is declared in millimetres.",
                        OnError = "a sheet that cannot be measured is named, and is excluded from the fix step rather " +
                                  "than fixed blind.",
                        ReadsBack = "findings by class, each naming the elements involved and the measured distance."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_capture_view",
                        ArgumentsJson = @"{ ""view_id"": { ""$input"": ""evidence_view_id"" } }",
                        Purpose = "capture the BEFORE image, while the findings are still there.",
                        Needs = "the sheet ids from step 2.",
                        Preconditions = "none. Captured now or never: after step 4 the evidence is gone.",
                        OnError = "a capture that fails leaves a sheet with no before image. Fix it before changing " +
                                  "anything, or the change is unreviewable.",
                        ReadsBack = "one PNG per sheet with its calibration."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_fix_planimetry",
                        // THE AUTHORISATION IS THE DECISION, and it is not arithmetic.
                        //
                        // The audit reports what it found. WHICH finding classes may
                        // be changed on somebody's drawing set is a permission, and
                        // this procedure's own acceptance criterion is written about
                        // "the classes that were authorized" - so the run holds until
                        // somebody says which.
                        RequiresDecision = true,
                        DecisionNeeded = "the actions to apply, from the findings step 2 reported. Selecting " +
                                         "everything an audit produced is a decision, and it would be made " +
                                         "on somebody else's sheets.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""units"": ""mm"",
  ""source_audit"": {
    ""finding_set_fingerprint"": { ""$ref"": { ""step"": 2, ""path"": ""finding_set_fingerprint"" } }
  },
  ""actions"": { ""$decision"": ""actions"" },
  ""dry_run"": false
}",
                        Purpose = "apply only the finding classes that were authorized.",
                        Needs = "the findings from step 2, filtered to the authorized classes.",
                        Preconditions = "dry_run first, and the authorized classes are stated. Everything is not a class.",
                        OnError = "a fix that would push an annotation outside its view crop is refused WITH the " +
                                  "measured distance. That refusal is a finding about the sheet, not a failure of the " +
                                  "fix.",
                        ReadsBack = "per finding: what moved, from where to where, re-read after the commit."
                    },
                    new Step
                    {
                        N = 5, Tool = "horizun_audit_planimetry",
                        // THE SAME SCOPE AND THE SAME UNITS AS STEP 2, from the same
                        // input. A re-audit at a different scope is a different
                        // measurement wearing the same name.
                        ArgumentsJson = @"{
  ""scope"": { ""$input"": ""scope"" },
  ""units"": ""mm"",
  ""include_passed_checks"": true
}",
                        Purpose = "re-measure, using the same tolerance as step 2.",
                        Needs = "the same sheets and tolerance as step 2.",
                        Preconditions = "the fixes committed.",
                        OnError = "a finding that survives is reported. Do not loosen the tolerance to make it pass, " +
                                  "which is the one thing that would make these numbers meaningless.",
                        ReadsBack = "zero findings in the authorized classes, and the unchanged count in every other " +
                                    "class."
                    },
                    new Step
                    {
                        N = 6, Tool = "horizun_capture_view",
                        // THE SAME VIEW AS STEP 3, from the same input. Two captures
                        // of different views are two pictures, not a comparison - and
                        // the whole value of this pair is that a person can put them
                        // side by side.
                        ArgumentsJson = @"{ ""view_id"": { ""$input"": ""evidence_view_id"" } }",
                        Purpose = "capture the AFTER image, so a person can compare.",
                        Needs = "the sheet ids from step 2 and the before images from step 3.",
                        Preconditions = "none.",
                        OnError = "a missing after image means this sheet was changed and nobody looked. That is worse " +
                                  "than not having fixed it.",
                        ReadsBack = "one PNG per sheet, at the same calibration as step 3 so the two are comparable."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""evidence_view_id""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document. Step 4 writes and names it."" },
    ""scope"": { ""type"": ""string"", ""enum"": [""model"", ""sheets"", ""views""], ""default"": ""sheets"",
      ""description"": ""What the audit examines, in BOTH step 2 and step 5 from this one input - a re-audit at a different scope is a different measurement wearing the same name. A check with no population reports not_applicable, never passed."" },
    ""evidence_view_id"": { ""type"": ""integer"",
      ""description"": ""The view captured as the BEFORE image, while the findings are still there. Required: a review whose evidence was taken after the fix shows a sheet that already looks right."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 5, Path = "coverage_complete", Expect = "true",
                        Why = "a re-audit that could not read part of the set has not verified the fixes. " +
                              "It is NOT the whole criterion - that one is about the authorized classes " +
                              "reporting zero findings and the unauthorized ones being listed untouched, " +
                              "and reading those is still somebody's job."
                    }
                },
                Example = "health then audit_planimetry(sheets, tol) then capture_view(before) then " +
                          "fix_planimetry(authorized classes, dry_run=true then false) then audit_planimetry " +
                          "again then capture_view(after).",
                Limits = new[]
                {
                    "The tolerance never changes between steps 2 and 5. A re-audit at a looser tolerance " +
                    "is not a verification, and it is an easy accident.",
                    "Only annotation and viewport placement move. No model geometry is touched by any " +
                    "step here, so a finding whose real cause is in the model stays open on purpose."
                }
            },

            new Procedure
            {
                Id = "safe-batch-parameter-update",
                Title = "Safe Batch Parameter Update",
                Version = 2,
                Permission = "safe_write",
                Outcome = "A large parameter write that is rehearsed, approved, and verified value by value.",
                Tools = new[] { "horizun_query_model", "horizun_write_params_verified" },
                Inputs = new[] { "the target selection, resolved to ids", "the parameter and the values" },
                Scope = "Exactly the resolved ids. The plan token refuses if that set changed.",
                Output = "Written values, each re-read.",
                Errors = new[]
                {
                    "a stale plan - somebody changed the model between the rehearsal and the apply, and the " +
                    "token refuses rather than writing to a different set than the one approved"
                },
                DependsOn = new string[0],
                Acceptance = "Written count equals approved count, and every value re-reads as written.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and
                        // refuses extras. An empty template is what makes this step
                        // EXECUTABLE rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "name the document before touching it.",
                        Needs = "nothing",
                        Preconditions = "none.",
                        OnError = "stop.",
                        ReadsBack = "the active document title."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_query_model",
                        ArgumentsJson = @"{
  ""element_ids"": { ""$input"": ""element_ids"" },
  ""parameters"": { ""$input"": ""parameters"" },
  ""response_mode"": ""full""
}",
                        Purpose = "find the elements the update is FOR, and count them.",
                        Needs = "the document from step 1.",
                        Preconditions = "the filter is written down before it is run. A batch aimed at a " +
                                        "filter nobody reviewed is a batch nobody agreed to.",
                        OnError = "an empty result is a finding: it usually means the filter is wrong, not " +
                                  "that the model is.",
                        ReadsBack = "the matched ids and the total. CHECK THE TOTAL against what you " +
                                    "expected before going on - this is the last cheap moment."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_write_params_verified",
                        RequiresDecision = true,
                        DecisionNeeded = "the writes: element ids, parameter names and values. The census " +
                                         "in step 2 says what is there now; what it SHOULD say is a fact " +
                                         "about the building.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""writes"": { ""$decision"": ""writes"" },
  ""on_failure"": ""atomic"",
  ""dry_run"": true
}",
                        Purpose = "rehearse the write.",
                        Needs = "the element ids from step 2.",
                        Preconditions = "dry_run=true. Always, and first.",
                        OnError = "a row that cannot be written says which parameter and why - a name that " +
                                  "matches two parameters is refused rather than guessed at.",
                        ReadsBack = "per row: what would be written, coerced to the parameter's storage " +
                                    "type, with the rows that would fail."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_write_params_verified",
                        // THE SAME WRITES AS THE REHEARSAL, carried forward by
                        // reference to the SAME decision rather than asked for
                        // again. A second decision here would be a second chance to
                        // change what the rehearsal actually rehearsed, and the
                        // rehearsal would then be evidence about a different call.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""writes"": { ""$decision"": ""writes"", ""from_step"": 3 },
  ""on_failure"": ""atomic"",
  ""dry_run"": false
}",
                        Purpose = "write it.",
                        Needs = "the SAME rows from step 3, plus its confirmation token.",
                        Preconditions = "the rehearsal was read by a person. An idempotency_key is set, so " +
                                        "a lost reply does not become a second write.",
                        OnError = "on_failure decides: stop at the first, or continue and report. Neither " +
                                  "is a default here because a half-written batch and an abandoned one " +
                                  "are different problems.",
                        ReadsBack = "every parameter RE-READ from the model after the commit. A silent " +
                                    "rollback surfaces here as a mismatch, not as a success."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""element_ids"", ""parameters""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document. Steps 3 and 4 name it."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1, ""maxItems"": 500,
      ""description"": ""The elements in scope, by id. Required and explicit: a batch parameter update whose scope is a filter is a batch whose scope changes when somebody edits the model between the census and the write."" },
    ""parameters"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""The parameters read back before and after, so the census and the verification are about the same fields."" }
  },
  ""additionalProperties"": false
}",
                Example =
                    "query_model(category=Walls, predicate Fire Rating is empty) → 42 matched → " +
                    "write_params_verified(writes for those 42, dry_run=true) → read it → same call with " +
                    "dry_run=false and the token.",
                Limits = new[]
                {
                    "Writing a TYPE parameter changes every instance of that type. The rehearsal says how " +
                    "many, and the decision is the caller's.",
                    "A parameter name that resolves to more than one parameter is refused, not " +
                    "disambiguated. Pass a GUID or a BuiltInParameter name.",
                    "Group members vary between groups only when allow_vary_between_groups says so; " +
                    "without it Revit refuses the write and the reply says which elements were in groups."
                },
            },

            new Procedure
            {
                Id = "architecture-structure-coordination",
                Title = "Architecture and Structure Coordination",
                Version = 2,
                Permission = "read_only",
                Outcome = "A measured coordination scope with its unresolved parts preserved.",
                Tools = new[] { "horizun_query_structure", "horizun_query_model", "horizun_clash" },
                Inputs = new[] { "the two disciplines' models, by id", "the clash tolerance" },
                Scope = "The declared element sets of both models.",
                Output = "A clash list with host and link identity per side.",
                Errors = new[] { "a link that is unloaded - reported as NOT COVERED rather than as zero clashes" },
                DependsOn = new string[0],
                Acceptance = "Every clash names both elements with the document each belongs to, and the " +
                             "coverage statement accounts for every link.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_query_structure",
                        // mode is REQUIRED and this route asks for members: the
                        // schema says outright that there is no honest default
                        // across eight different questions.
                        ArgumentsJson = @"{
  ""mode"": ""members"",
  ""categories"": { ""$input"": ""structure_categories"" }
}",
                        Purpose = "establish what the structural side actually contains.",
                        Needs = "the document from step 1 and the two model ids, which arrive as inputs.",
                        Preconditions = "both models are loaded. This is the step where an unloaded link is caught, and it " +
                                        "has to be caught HERE rather than read as a clean result later.",
                        OnError = "a link that is unloaded is reported as NOT COVERED. Loading it is a decision with " +
                                  "consequences for the session, so this route reports and does not load.",
                        ReadsBack = "the structural element set, with the document each element belongs to."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_query_model",
                        ArgumentsJson = @"{
  ""categories"": { ""$input"": ""architecture_categories"" },
  ""response_mode"": ""summary""
}",
                        Purpose = "establish the architectural side the same way.",
                        Needs = "the document from step 1 and the coverage statement from step 2, so both sides are " +
                                "described in the same terms.",
                        Preconditions = "none.",
                        OnError = "a category that is empty is reported as empty, which is different from a category " +
                                  "that was not asked for.",
                        ReadsBack = "the architectural element set, with counts per category."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_clash",
                        ArgumentsJson = @"{
  ""categories_a"": { ""$input"": ""architecture_categories"" },
  ""categories_b"": { ""$input"": ""structure_categories"" },
  ""clearance_mm"": { ""$input"": ""clearance_mm"" },
  ""include_links"": true
}",
                        Purpose = "measure the intersections between the two declared sets.",
                        Needs = "the two element sets from steps 2 and 3, and the tolerance.",
                        Preconditions = "the tolerance is declared. A clash result without one cannot be compared to any " +
                                        "other run.",
                        OnError = "a clash between two elements of the SAME document is still a clash and is reported " +
                                  "rather than filtered as uninteresting.",
                        ReadsBack = "per clash: both elements, the document each is in, and the measured overlap. Plus " +
                                    "the coverage statement, which has to account for every link."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""architecture_categories"", ""structure_categories""],
  ""properties"": {
    ""architecture_categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""One side of the coordination: the architectural categories. Used for BOTH the census in step 3 and side A of the clash test, from this one input - two lists that drift apart would compare something other than what was censused."" },
    ""structure_categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""The structural categories, used for both the member reading and side B of the clash test."" },
    ""clearance_mm"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 500, ""default"": 0,
      ""description"": ""How much room around each element counts as occupied. At zero the test finds only what already overlaps; the interesting coordination finding is what will not fit once somebody has to build it. The number comes from a standard, not from this bridge."" }
  },
  ""additionalProperties"": false
}",
                Example = "health then query_structure(models) then query_model(same scope) then clash(set A, " +
                          "set B, tolerance_mm=10).",
                Limits = new[]
                {
                    "An unloaded link produces zero clashes, which looks exactly like a coordinated " +
                    "model. The coverage statement is what tells the two apart, and it travels with the " +
                    "result for that reason.",
                    "This route measures and proposes nothing. Deciding which side moves is a " +
                    "coordination decision between two teams."
                }
            },

            new Procedure
            {
                Id = "mep-coordination-review",
                Title = "MEP Coordination Review",
                Version = 2,
                Permission = "read_only",
                Outcome = "Systems, connectivity and clashes measured. Nothing is written and no route "
                          + "is designed.",
                Tools = new[] { "horizun_query_model", "horizun_clash", "horizun_plan_mep" },
                Inputs = new[] { "the categories in scope",
                                 "the two sides of the clash test",
                                 "the clearance - it comes from a standard or a contract, not from here",
                                 "the seeds that bound the connectivity census" },
                Scope = "Reading only. No step opens a transaction.",
                Output = "A census by category, the clashes at the declared clearance, and the connected "
                         + "components with their open connectors.",
                Errors = new[] { "an open connector is a finding, not an error: a network can be correct and " +
                                 "incomplete at the same time" },
                DependsOn = new string[0],
                Acceptance = "The census reached a non-empty population, the clash test ran at the " +
                             "clearance the caller declared, and the open-connector count is reported - a " +
                             "network can be correct and incomplete at the same time, so the number is the " +
                             "deliverable rather than a target.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_query_model",
                        ArgumentsJson = @"{
  ""categories"": { ""$input"": ""categories"" },
  ""response_mode"": ""summary""
}",
                        Purpose = "census the systems, their elements, and their OPEN connectors.",
                        Needs = "the document from step 1 and the systems in scope.",
                        Preconditions = "the systems are named. All systems is a valid scope stated on purpose.",
                        OnError = "an element whose connectors cannot be read is named. A network that cannot be read " +
                                  "is not a complete network.",
                        ReadsBack = "per system: element count, open connector count, and where each open connector is."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_clash",
                        // THE CLEARANCE IS THE CALLER'S. A clash test at zero
                        // clearance finds only what already overlaps, and the
                        // interesting finding in coordination is what will not fit
                        // once somebody has to install it.
                        ArgumentsJson = @"{
  ""categories_a"": { ""$input"": ""categories_a"" },
  ""categories_b"": { ""$input"": ""categories_b"" },
  ""clearance_mm"": { ""$input"": ""clearance_mm"" },
  ""include_links"": true
}",
                        Purpose = "measure interferences against the clearance rules.",
                        Needs = "the system element sets from step 2.",
                        Preconditions = "the clearance rules arrive as an input. A clearance is a project rule, not a " +
                                        "property of the geometry.",
                        OnError = "a clash inside a single system is reported, not filtered. Two branches of the same " +
                                  "duct run can still collide.",
                        ReadsBack = "per clash: both elements, the measured overlap and which clearance rule it breaches."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_plan_mep",
                        // CENSUS, NOT route_run.
                        //
                        // route_run needs a pipe or duct type, a system type, a
                        // level and the points of the route. A REVIEW has decided
                        // none of those, and a template that supplied them would be
                        // this catalogue designing somebody's pipework. What a
                        // review can do is measure connectivity: which components
                        // exist and where they are still open.
                        ArgumentsJson = @"{
  ""operation"": ""network_census"",
  ""element_ids"": { ""$input"": ""element_ids"" },
  ""units"": ""mm""
}",
                        Purpose = "measure connectivity: which components exist and where they are open. " +
                                  "Nothing is written.",
                        Needs = "the open connectors from step 2 and the clashes from step 3.",
                        Preconditions = "none. plan_mep writes nothing by design.",
                        OnError = "a route it cannot find is reported as not found, with what blocked it. It is never " +
                                  "replaced by a straight line that ignores the obstruction.",
                        ReadsBack = "each proposed route as a create_elements request that HAS NOT been sent."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""categories"", ""categories_a"", ""categories_b"", ""element_ids""],
  ""properties"": {
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""The categories step 2 censuses. Required: 'all the MEP' is not a scope, and a review over the whole model reports interferences nobody asked about alongside the ones they did."" },
    ""categories_a"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""One side of the clash test."" },
    ""categories_b"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""The other side."" },
    ""clearance_mm"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 500, ""default"": 0,
      ""description"": ""How much room around each element counts as occupied. At zero the test finds only what already overlaps, and the interesting finding in coordination is what will not fit once somebody has to install it. The number is the caller's: it comes from a standard or a contract, not from this bridge."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1, ""maxItems"": 500,
      ""description"": ""The seeds that bound the connectivity census in step 4. Membership is CONNECTOR connectivity, never geometric coincidence."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 4, Path = "open_connectors_total", Expect = "non_empty",
                        Why = "this is a REVIEW, and a review's job is to produce the number rather than to " +
                              "make it zero. non_empty here asserts that the census actually measured " +
                              "something: a zero from a census that seeded nothing and a zero from a " +
                              "complete network look identical, and only the first is a defect in the run."
                    }
                },
                Example = "health, query_model(categories, summary), clash(A against B at 50 mm), " +
                          "plan_mep(network_census over the seeds).",
                Limits = new[]
                {
                    "An open connector is a finding, not an error. A network can be correct and " +
                    "incomplete at the same time, and this route reports the count either way.",
                    "STEP 4 MEASURES, IT DOES NOT ROUTE. horizun_plan_mep's route_run needs a pipe or " +
                    "duct type, a system type, a level and the points of the route, and a review has " +
                    "decided none of them - a template that supplied them would be this catalogue " +
                    "designing somebody's pipework. What a review can do is measure connectivity.",
                    "THE CLEARANCE IS THE CALLER'S. At zero the clash test finds only what already " +
                    "overlaps, and the interesting finding in coordination is what will not fit once " +
                    "somebody has to install it. The number comes from a standard or a contract."
                }
            },

            new Procedure
            {
                Id = "mep-network-completion",
                Title = "MEP Network Completion",
                Version = 1,
                Permission = "safe_write",
                Outcome = "Segments that merely touch become a connected network, verified connector by connector.",
                Tools = new[] { "horizun_query_model", "horizun_plan_mep", "horizun_create_elements",
                                "horizun_connect_mep" },
                Inputs = new[] { "the open connectors to join, by element and connector id",
                                 "the tolerance for coincidence, in millimetres",
                                 "whether a size mismatch at equipment is expected" },
                Scope = "Only the named connectors. Geometry is never moved to close a gap.",
                Output = "Fittings where a fitting is needed, direct connections where one is not, and a list " +
                         "of every connector still open afterwards.",
                Errors = new[]
                {
                    "connectors in different domains - refused with both domains named",
                    "connectors not at the same point - refused with the measured gap, because closing it " +
                    "would move somebody's geometry",
                    "an element with several free connectors and no named one - refused rather than guessed"
                },
                DependsOn = new[] { "mep-coordination-review" },
                Acceptance = "Every requested pair re-reads as connected, and the count of connectors still " +
                             "open is stated even on success.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_query_model",
                        ArgumentsJson = @"{
  ""element_ids"": { ""$input"": ""element_ids"" },
  ""response_mode"": ""summary""
}",
                        Purpose = "scope the network: read back the elements the rest of this run acts on, " +
                                  "so the scope is a measured set rather than a sentence.",
                        Needs = "the element ids, which arrive as an input.",
                        Preconditions = "the scope is named as a set of elements. 'All the pipes' is not a " +
                                        "scope, and this procedure requires the ids rather than allowing " +
                                        "the census to seed itself from every MEP curve in the model.",
                        OnError = "stop; an unscoped completion pass touches the whole model.",
                        ReadsBack = "the elements in scope and their count."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_plan_mep",
                        // network_census measures membership through CONNECTORS, never
                        // through geometric coincidence - which is the whole distinction
                        // this procedure is about. Seeds bound the walk; without them
                        // every MEP curve seeds and the command refuses above 2000.
                        ArgumentsJson = @"{
  ""operation"": ""network_census"",
  ""element_ids"": { ""$input"": ""element_ids"" },
  ""units"": ""mm""
}",
                        Purpose = "find the open connectors and the pairs that could be joined.",
                        Needs = "the element ids from step 1.",
                        Preconditions = "none.",
                        OnError = "a connector that cannot be resolved is named. It is a finding.",
                        ReadsBack = "open connectors with their owner, index and domain, and candidate " +
                                    "pairs with the measured distance between them."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_connect_mep",
                        // WHICH OPENS TO JOIN IS NOT ARITHMETIC. The census says which
                        // connectors are open; some are the end of a run, some are
                        // equipment nobody has placed yet, and pairing them by proximity
                        // would connect a vent to a waste stack because they happened to
                        // be near each other. So the run HOLDS.
                        //
                        // This is NOT the same situation as the DWG route, where the
                        // drawing itself says which ends meet and a run carries the same
                        // identity the conversion stamps. Here nothing says it.
                        RequiresDecision = true,
                        DecisionNeeded = "the pairs to join, each as {key, a_element_id, a_connector, " +
                                         "b_element_id, b_connector}. Step 2 lists the open connectors per " +
                                         "component; which of them belong together is a reading of the " +
                                         "building, not of the model.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""units"": ""mm"",
  ""actions"": { ""$decision"": ""pairs"" },
  ""dry_run"": false
}",
                        Purpose = "join the pairs that are unambiguous.",
                        Needs = "the (element, connector index) pairs from step 2 - NOT a connector object: " +
                                "connector handles do not survive the rehearsal's rollback, so identity " +
                                "travels and the connector is re-resolved per phase.",
                        Preconditions = "dry_run first. The rehearsal measures domain, size and physical " +
                                        "coincidence and refuses a pair that does not meet them.",
                        OnError = "an ambiguous pair - several free connectors and none named - is REFUSED " +
                                  "rather than chosen for you. Name the connector index and re-send.",
                        ReadsBack = "each connection re-read from the model, plus the connectors still " +
                                    "open. Connecting what was asked and leaving three loose ends is a " +
                                    "correct command and an incomplete network, and both are reported."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_plan_mep",
                        ArgumentsJson = @"{
  ""operation"": ""network_census"",
  ""element_ids"": { ""$input"": ""element_ids"" },
  ""units"": ""mm""
}",
                        Purpose = "re-measure. The list of open connectors is the acceptance criterion.",
                        Needs = "the same scope as step 1.",
                        Preconditions = "step 3 committed.",
                        OnError = "none: this step only reads.",
                        ReadsBack = "the open connectors that remain. Zero is the result; anything else is " +
                                    "the work left."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""element_ids""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document. The connection step names it."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1, ""maxItems"": 500,
      ""description"": ""The seeds that bound the census. REQUIRED here even though horizun_plan_mep allows omitting them: 'all the pipes' is not a scope, and an unscoped completion pass measures the whole model and then proposes work across it."" }
  },
  ""additionalProperties"": false
}",
                Checks = new[]
                {
                    new AcceptanceCheck
                    {
                        Step = 4, Path = "open_connectors_total", Expect = "zero",
                        Why = "the count of connectors still open after the pass, measured from the MODEL " +
                              "through connector connectivity rather than from the reply that wrote it. " +
                              "Zero is a network with no loose ends IN THE SCOPE MEASURED - it is not a " +
                              "hydraulic check and not a code check, and a non-zero count is the work left " +
                              "rather than a failure."
                    }
                },
                Example =
                    "query_model(system=SAN-01) → plan_mep(those ids) → 12 open, 5 joinable pairs → " +
                    "connect_mep(the 5 pairs, dry_run=true, then false) → plan_mep again → 2 open, both " +
                    "named, both needing a fitting nobody has chosen.",
                Limits = new[]
                {
                    "Connecting two connectors does not create a fitting. Where a fitting is needed the " +
                    "pair is refused with that as the reason.",
                    "A network reported as complete is a network with no OPEN connectors in the scope " +
                    "measured. It is not a hydraulic or a code check.",
                    "Duct and pipe accessories inserted in-line are a different operation with a different " +
                    "break-and-reconnect path."
                },
            },

            new Procedure
            {
                Id = "western-forms-concrete-review",
                Title = "Concrete and Reinforcement Review",
                Version = 2,
                Permission = "read_only",
                Outcome = "Declared concrete and reinforcement requirements audited without applying any.",
                Tools = new[] { "horizun_query_structure", "horizun_audit_reinforcement" },
                Inputs = new[] { "the requirement set - covers, spacings, shapes - as an input",
                                 "the hosts in scope" },
                Scope = "Reading. No rebar is created.",
                Output = "A per-host compliance table.",
                Errors = new[] { "a host with no reinforcement at all is reported apart from one whose " +
                                 "reinforcement is wrong: the two need different work" },
                DependsOn = new string[0],
                Acceptance = "Every host in scope appears exactly once, with a measurement or an explicit " +
                             "non-coverage.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_query_structure",
                        // mode is REQUIRED: the schema says outright that there is
                        // no honest default across eight different questions.
                        ArgumentsJson = @"{
  ""mode"": ""members"",
  ""categories"": { ""$input"": ""categories"" }
}",
                        Purpose = "resolve the hosts in scope and what reinforcement each one currently has.",
                        Needs = "the document from step 1 and the hosts in scope, which arrive as an input.",
                        Preconditions = "the requirement set - covers, spacings, shapes - is explicit. Auditing against an " +
                                        "unstated requirement measures nothing.",
                        OnError = "a host that cannot be read is named and stays in the total. Dropping it makes the " +
                                  "table look complete when it is not.",
                        ReadsBack = "per host: its geometry, its cover settings, and the rebar it hosts."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_audit_reinforcement",
                        // The requirement set is REQUIRED by the tool, and rightly:
                        // an audit with no rules measures nothing and reports a
                        // clean model.
                        ArgumentsJson = @"{
  ""requirement_set"": { ""$input"": ""requirement_set"" }
}",
                        Purpose = "measure each host against the requirement set.",
                        Needs = "the hosts and their rebar from step 2.",
                        Preconditions = "none. This reads.",
                        OnError = "a host with NO reinforcement at all is reported apart from one whose reinforcement " +
                                  "is wrong. The first needs design, the second needs correction, and merging them " +
                                  "hides which.",
                        ReadsBack = "per host, exactly once: a measurement or an explicit non-coverage."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""requirement_set""],
  ""properties"": {
    ""requirement_set"": { ""type"": ""object"",
      ""description"": ""A horizun.structural-requirements/1 document - the same one that was applied, or a newer one to audit against. Required by the tool and by sense: an audit with no rules measures nothing and reports a clean model."" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1, ""default"": [""OST_StructuralFraming"", ""OST_StructuralColumns""],
      ""description"": ""The member categories read in step 2. The default is the tool's own, written here so that a step referencing it does not block when the caller leaves it out."" }
  },
  ""additionalProperties"": false
}",
                Example = "health then query_structure(hosts) then audit_reinforcement(hosts, requirement set).",
                Limits = new[]
                {
                    "No rebar is created anywhere in this route. It measures what exists against what was " +
                    "declared.",
                    "Revit does not honour a suppressed FIRST bar in a maximum_spacing layout: it commits " +
                    "with the bar in place. An audit that trusts the layout parameters rather than the " +
                    "placed bars will report a spacing the model does not have.",
                    "A cover setting is per face and per host. A single cover number for an element is " +
                    "usually a simplification somebody made in the requirement set."
                }
            },

            new Procedure
            {
                Id = "graphic-review-pack",
                Title = "Graphic Review Pack",
                Version = 1,
                Permission = "safe_write",
                Outcome = "A review view where one parameter's values are told apart by colour, with a legend.",
                Tools = new[] { "horizun_manage_views", "horizun_capture_view" },
                Inputs = new[] { "the source view to duplicate", "the categories and the parameter to colour by",
                                 "how many distinct values may get a colour" },
                Scope = "A DUPLICATE of the source view. The original is not touched.",
                Output = "A coloured review view, its returned legend, and a capture.",
                Errors = new[]
                {
                    "a view whose template governs V/G - refused with the template named, because Revit would " +
                    "accept the call and keep the template's values",
                    "more distinct values than the palette has colours - the reply says the palette wrapped, " +
                    "and the legend is then the only way to tell two values apart"
                },
                DependsOn = new string[0],
                Acceptance = "Every legend row names a filter that is present on the view and visible, and the " +
                             "capture shows the colours.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_manage_views",
                        // WHICH VIEWS, AND HOW, IS A GRAPHIC STANDARD. This bridge
                        // compiles none, and a template that named a filter or a
                        // template here would be one office's convention applied to
                        // the next office's model.
                        RequiresDecision = true,
                        DecisionNeeded = "the view actions to rehearse: which views, and what to change on " +
                                         "them. The standard is the caller's artefact, not this " +
                                         "catalogue's.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""units"": ""mm"",
  ""actions"": { ""$decision"": ""actions"" },
  ""dry_run"": true
}",
                        Purpose = "DUPLICATE the source view. The original is never touched.",
                        Needs = "the document from step 1 and the source view id, which arrives as an input.",
                        Preconditions = "the duplicate is made with detailing when the review needs the annotation, and the " +
                                        "source is not a dependent view.",
                        OnError = "a source view that cannot be duplicated is named. Do not colour the original as a " +
                                  "fallback: it is somebody working view.",
                        ReadsBack = "the new view id and the fact that it carries no view template."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_manage_views",
                        // THE SAME ACTIONS AS THE REHEARSAL, carried forward by
                        // reference to the SAME decision. A second ask here is a
                        // second chance to change what step 2 rehearsed, and the
                        // rehearsal would then be evidence about a different call.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""units"": ""mm"",
  ""actions"": { ""$decision"": ""actions"", ""from_step"": 2 },
  ""dry_run"": false
}",
                        Purpose = "colour by the parameter, with filters on the duplicate.",
                        Needs = "the view id from step 2, plus the categories, the parameter and the colour budget.",
                        Preconditions = "the duplicate has NO view template governing V/G. When a template governs it, Revit " +
                                        "accepts the call and keeps the template values, so this step refuses with the " +
                                        "template named.",
                        OnError = "more distinct values than the palette has colours means the palette WRAPPED. The " +
                                  "reply says so, and the legend is then the only way to tell two values apart.",
                        ReadsBack = "the legend: every row naming a filter that is present on the view and visible."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_capture_view",
                        ArgumentsJson = @"{ ""view_id"": { ""$input"": ""evidence_view_id"" } }",
                        Purpose = "capture it, because the point of this pack is that a person looks.",
                        Needs = "the view id from step 2 and the legend from step 3.",
                        Preconditions = "none.",
                        OnError = "a capture that fails leaves a coloured view nobody saw. The legend alone is not the " +
                                  "review.",
                        ReadsBack = "the PNG, in which the colours in the legend are the colours on the elements."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""evidence_view_id""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document. Step 3 writes and names it."" },
    ""evidence_view_id"": { ""type"": ""integer"",
      ""description"": ""The view captured after the change. A graphic review whose reply is a list of view ids and no picture is one nobody can check: the whole point of this route is that somebody LOOKS."" }
  },
  ""additionalProperties"": false
}",
                Example = "health then manage_views(duplicate with detailing) then manage_views(colour by " +
                          "parameter=Comentarios, max_colours=12) then capture_view(the duplicate).",
                Limits = new[]
                {
                    "A view template that governs V/G makes this silently not work: the call succeeds and " +
                    "nothing changes colour. Step 3 refuses instead, and names the template.",
                    "Dependent views cannot hide categories independently of their parent, which is why " +
                    "step 2 duplicates rather than reusing a dependent.",
                    "The palette is finite. When it wraps, two different values share a colour and only " +
                    "the legend separates them."
                }
            },

            new Procedure
            {
                Id = "material-standardisation",
                Title = "Material Standardisation",
                Version = 1,
                Permission = "safe_write",
                Outcome = "Project materials brought to a declared standard without changing anything else.",
                Tools = new[] { "horizun_manage_materials", "horizun_manage_system_types", "horizun_query_model" },
                Inputs = new[] { "the material standard - names, classes, colours, patterns - as an input",
                                 "whether an appearance asset may be shared between materials" },
                Scope = "The named materials. Elements referencing them are not edited.",
                Output = "Created, duplicated and edited materials, each value re-read.",
                Errors = new[]
                {
                    "a name that already exists - refused with the existing id, because Revit material names " +
                    "are unique",
                    "a shared appearance asset - the dry run says how many materials already use it, since " +
                    "editing it later changes all of them"
                },
                DependsOn = new string[0],
                Acceptance = "Every material in the standard exists with the declared values re-read from the " +
                             "model, and no material outside the standard changed.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_manage_materials",
                        // The inventory first: which materials exist and which of
                        // them are used. An `actions` array with one read operation
                        // is how this tool answers, because everything it does is an
                        // action and a read is one too.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""actions"": { ""$input"": ""inventory_actions"" },
  ""dry_run"": true
}",
                        Purpose = "list what is actually in the model before deciding anything.",
                        Needs = "nothing",
                        Preconditions = "none.",
                        OnError = "stop.",
                        ReadsBack = "every material, with how many elements and types use it, and which " +
                                    "share an appearance asset."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_manage_materials",
                        // WHICH MATERIALS ARE THE SAME MATERIAL UNDER TWO NAMES is a
                        // judgement about somebody's own library, and merging two
                        // that are not the same is not undone by re-running
                        // anything. So the run holds.
                        RequiresDecision = true,
                        DecisionNeeded = "the actions to rehearse: which materials to rename, merge or " +
                                         "retire, from the inventory in step 1. Two materials with similar " +
                                         "names are not evidence that they are the same material.",
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""actions"": { ""$decision"": ""actions"" },
  ""dry_run"": true
}",
                        Purpose = "create or rename to the project's standard names.",
                        Needs = "the material ids from step 1, and the naming standard as an INPUT - the " +
                                "bridge carries none.",
                        Preconditions = "dry_run first. A rename is visible in every schedule at once.",
                        OnError = "a name that already exists is refused rather than suffixed. Two " +
                                  "materials called 'Concrete 2' is worse than a refusal.",
                        ReadsBack = "each material re-read by id, with its new name."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_manage_materials",
                        // THE SAME ACTIONS AS THE REHEARSAL, by reference to the same
                        // decision rather than asked for again.
                        ArgumentsJson = @"{
  ""target_document"": { ""$input"": ""document"" },
  ""actions"": { ""$decision"": ""actions"", ""from_step"": 2 },
  ""dry_run"": false
}",
                        Purpose = "set graphics and identity: colour, patterns, class, category.",
                        Needs = "the material ids from step 2.",
                        Preconditions = "the appearance asset is DUPLICATED by default. Several materials " +
                                        "commonly share one, and editing a shared asset changes all of " +
                                        "them - the rehearsal says how many.",
                        OnError = "the reply names which property Revit refused.",
                        ReadsBack = "every property re-read from the material after the commit."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_query_model",
                        ArgumentsJson = @"{
  ""categories"": { ""$input"": ""categories"" },
  ""parameters"": { ""$input"": ""parameters"" },
  ""response_mode"": ""summary""
}",
                        Purpose = "confirm nothing still uses the old names.",
                        Needs = "the old names from step 1.",
                        Preconditions = "step 3 committed.",
                        OnError = "none: this step only reads.",
                        ReadsBack = "elements still carrying a non-standard material, by id."
                    }
                },
                InputSchemaJson = @"{
  ""type"": ""object"",
  ""required"": [""document"", ""inventory_actions"", ""categories"", ""parameters""],
  ""properties"": {
    ""document"": { ""type"": ""string"", ""description"": ""Title of the ACTIVE document. Step 3 writes and names it."" },
    ""inventory_actions"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""minItems"": 1,
      ""description"": ""The read actions that produce the inventory in step 1, each with a key and an operation. They are the caller's because what counts as the inventory - every material, or the ones on one category - is a choice about the review, not about the model."" },
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""The categories re-read in step 4, so the verification looks at the elements the standardisation was about."" },
    ""parameters"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
      ""description"": ""The parameters re-read, so the check is about the fields that were supposed to change."" }
  },
  ""additionalProperties"": false
}",
                Example =
                    "manage_materials(operation=list) → 38 materials, 11 off-standard → rename those 11 " +
                    "(dry_run, then apply) → set their graphics → query_model to confirm nothing uses the " +
                    "old names.",
                Limits = new[]
                {
                    "Material assignment lives on the TYPE or in a compound structure layer, not on the " +
                    "instance. This procedure standardises the materials themselves; moving an element " +
                    "from one material to another is a type change.",
                    "Physical and thermal properties are a different asset surface and are NOT covered.",
                    "Appearance assets are duplicated rather than shared unless asked; the sharing count " +
                    "is reported so the decision is visible."
                },
            },

            new Procedure
            {
                Id = "qaqc-report-export",
                Title = "QA/QC Report Export",
                Version = 2,
                Permission = "full_write",
                Outcome = "Measured findings appended to an approved workbook and read back cell by cell.",
                Tools = new[] { "horizun_excel_write_rows", "horizun_excel_read_rows" },
                Inputs = new[] { "the workbook path, which full_write must authorize", "the sheet and header row",
                                 "the findings to write" },
                Scope = "The named sheet of the named workbook.",
                Output = "Appended rows, re-read.",
                Errors = new[]
                {
                    "a workbook open in Excel - the write refuses rather than producing a lock file and a " +
                    "half-written sheet",
                    "a retry after a timeout - the write is idempotent by key and does not duplicate rows"
                },
                DependsOn = new[] { "model-health-audit" },
                Acceptance = "Every written row is read back from the file and matches what was sent.",
                Steps = new[]
                {
                    new Step
                    {
                        N = 1, Tool = "horizun_health",
                        // No arguments: horizun_health's schema declares none and refuses
                        // extras. An empty template is what makes this step EXECUTABLE
                        // rather than merely described.
                        ArgumentsJson = @"{}",
                        Purpose = "establish WHICH document every step below acts on.",
                        Needs = "nothing",
                        Preconditions = "Revit is running with the intended model in front.",
                        OnError = "stop. Every step names a target_document, and naming the wrong one works on somebody " +
                                  "else project.",
                        ReadsBack = "the document title, the Revit year and the bridge own version and commit."
                    },
                    new Step
                    {
                        N = 2, Tool = "horizun_excel_write_rows",
                        Purpose = "rehearse the append: which rows, under which headers.",
                        Needs = "the document from step 1 for provenance, plus the findings, the workbook path, the " +
                                "sheet and the header row.",
                        Preconditions = "dry_run, and the workbook path is authorized by the permission profile. This route " +
                                        "writes outside the model.",
                        OnError = "a workbook open in Excel is refused HERE, before anything is written. The " +
                                  "alternative is a lock file and a half-written sheet.",
                        ReadsBack = "the rows as they would land, mapped to the existing headers."
                    },
                    new Step
                    {
                        N = 3, Tool = "horizun_excel_write_rows",
                        Purpose = "append them.",
                        Needs = "the rehearsed rows from step 2 and an idempotency key.",
                        Preconditions = "step 2 rehearsed clean.",
                        OnError = "a retry after a timeout is idempotent BY KEY and does not duplicate rows. A retry " +
                                  "without the key appends a second copy that looks exactly like the first.",
                        ReadsBack = "the appended range and the row count the file now holds."
                    },
                    new Step
                    {
                        N = 4, Tool = "horizun_excel_read_rows",
                        Purpose = "read the file back. The write reply is not the file.",
                        Needs = "the appended range from step 3.",
                        Preconditions = "the write closed the file.",
                        OnError = "a cell that reads back differently from what was sent is the finding this step " +
                                  "exists to produce. Report it rather than rewriting over it.",
                        ReadsBack = "every written row, cell by cell, against what was sent."
                    }
                },
                Example = "health then excel_write_rows(workbook, sheet, rows, dry_run=true) then the same with " +
                          "dry_run=false and an idempotency_key then excel_read_rows(that range).",
                Limits = new[]
                {
                    "Step 4 is not optional. Excel accepts values and stores them as something else - a " +
                    "code that becomes a number, a date that becomes text - and only the read-back sees " +
                    "it.",
                    "The workbook is outside the model and outside the undo of Revit. There is no " +
                    "rollback here, which is why step 2 rehearses."
                }
            }
        };

        // =====================================================================

        /// <summary>
        /// One step of a procedure: what to call, with what, and how to tell it worked.
        ///
        /// `Needs` is the field that turns a catalogue into a route. "The view ids from step
        /// 1" is a reference between steps; a list of tools is not.
        /// </summary>
        internal sealed class Step
        {
            public int N;
            public string Tool;
            public string Purpose;

            /// <summary>What this step takes from EARLIER steps, by number.</summary>
            public string Needs;

            /// <summary>What must already be true before it is called.</summary>
            public string Preconditions;

            /// <summary>What to do when it fails. Never "retry" by default: half of these write.</summary>
            public string OnError;

            /// <summary>What to check afterwards. A step with nothing to read back cannot be verified.</summary>
            public string ReadsBack;

            /// <summary>
            /// The arguments to send, as a template. Null means this step is not executable.
            ///
            /// TWO KINDS OF HOLE are filled at run time and nothing else is:
            ///   { "$input": "name" }                  a run input
            ///   { "$ref": { "step": 2, "path": "…" } } a value out of an earlier RESULT
            /// Anything else in the template is a literal. `Needs` says which steps in
            /// prose; this says it in a form the executor can resolve, and the two are
            /// written together so they cannot drift.
            /// </summary>
            public string ArgumentsJson;

            /// <summary>
            /// True when this step needs a person to decide before it can run.
            ///
            /// The run STOPS here rather than choosing. A procedure that selects "all the
            /// findings" because nobody was asked is a procedure that committed somebody
            /// else's decision.
            /// </summary>
            public bool RequiresDecision;

            /// <summary>What the person has to supply or approve. Required when RequiresDecision.</summary>
            public string DecisionNeeded;

            public JObject Arguments() =>
                string.IsNullOrWhiteSpace(ArgumentsJson) ? null : JObject.Parse(ArgumentsJson);

            public JObject Json()
            {
                var json = new JObject
                {
                    ["step"] = N,
                    ["tool"] = Tool,
                    ["purpose"] = Purpose,
                    ["needs_from_earlier_steps"] = Needs,
                    ["preconditions"] = Preconditions,
                    ["on_error"] = OnError,
                    ["reads_back"] = ReadsBack,
                    ["executable"] = ArgumentsJson != null,
                    ["requires_decision"] = RequiresDecision,
                    ["decision_needed"] = DecisionNeeded == null ? (JToken)JValue.CreateNull() : DecisionNeeded
                };
                if (ArgumentsJson != null) json["arguments_template"] = Arguments();

                // DOES THIS TEMPLATE AGREE WITH THE TOOL IT NAMES?
                //
                // Decidable from the contract alone, so it is decided here rather
                // than on step six of a run that has already written five times.
                JObject audit = TemplateAudit();
                if (audit != null) json["template_audit"] = audit;
                return json;
            }

            /// <summary>
            /// Compare this step's template against the target tool's own schema.
            ///
            /// Returns null for a step with no template - that step is documented,
            /// not executable, and the catalogue already says so. Otherwise it names
            /// every key the tool does not declare and every required key the
            /// template does not supply.
            /// </summary>
            public JObject TemplateAudit()
            {
                if (string.IsNullOrWhiteSpace(ArgumentsJson)) return null;

                Horizun.Contracts.CommandContract contract = Horizun.Contracts.Contract.Find(Tool);
                if (contract == null || contract.InputSchema == null)
                    return new JObject
                    {
                        ["agrees"] = false,
                        ["reason"] = "no_such_tool",
                        ["tool"] = Tool,
                        ["means"] = "this step names a tool the contract does not carry. The run would refuse " +
                                    "at dispatch, after every step before it had already run."
                    };

                JObject template;
                try { template = JObject.Parse(ArgumentsJson); }
                catch (Exception ex)
                {
                    return new JObject
                    {
                        ["agrees"] = false,
                        ["reason"] = "template_is_not_json",
                        ["error"] = ex.Message
                    };
                }

                var declared = contract.InputSchema["properties"] as JObject;
                JToken extraAllowed = contract.InputSchema["additionalProperties"];
                bool strict = !(extraAllowed != null && extraAllowed.Type == JTokenType.Boolean &&
                                (bool)extraAllowed);

                var undeclared = new JArray();
                if (declared != null)
                    foreach (JProperty property in template.Properties())
                        if (declared[property.Name] == null) undeclared.Add(property.Name);

                var missing = new JArray();
                var required = contract.InputSchema["required"] as JArray;
                if (required != null)
                    foreach (JToken token in required)
                    {
                        string name = (string)token;
                        if (string.IsNullOrEmpty(name)) continue;
                        // A required key the executor supplies for every step is not
                        // missing from the template: idempotency_key is added by the
                        // run for a write, and target_document is often an input the
                        // template fills - both appear in the template when they do.
                        if (template[name] == null) missing.Add(name);
                    }

                bool agrees = missing.Count == 0 && (undeclared.Count == 0 || !strict);
                var result = new JObject
                {
                    ["agrees"] = agrees,
                    ["tool"] = Tool,
                    ["schema_refuses_undeclared_keys"] = strict
                };
                if (undeclared.Count > 0)
                {
                    result["keys_the_tool_does_not_declare"] = undeclared;
                    result["undeclared_means"] = strict
                        ? "this tool's schema refuses a key it does not declare, so these make the call " +
                          "INVALID - and the refusal reads like a bad input rather than a bad template."
                        : "this tool's schema permits extra keys, so these are passed through harmlessly.";
                }
                if (missing.Count > 0)
                {
                    result["required_keys_the_template_omits"] = missing;
                    result["missing_means"] =
                        "the tool requires these and the template does not supply them. The step fails at " +
                        "DISPATCH - which on a later step means after everything before it has already run.";
                }
                if (agrees)
                    result["means"] =
                        "every key this template sends is one the tool declares, and every key the tool " +
                        "requires is supplied. That is a statement about NAMES, not about values: a " +
                        "correctly named argument can still carry the wrong thing.";
                return result;
            }
        }

        /// <summary>One evaluable piece of an acceptance criterion.</summary>
        internal sealed class AcceptanceCheck
        {
            /// <summary>Which step's result to look at.</summary>
            public int Step;

            /// <summary>Where in that result, e.g. "coverage_complete" or "rows[*].verified".</summary>
            public string Path;

            /// <summary>What it has to be. One of <see cref="Evaluable"/>; anything else never passes.</summary>
            public string Expect;

            /// <summary>What this check is for, in one line. Shown when it fails.</summary>
            public string Why;

            /// <summary>
            /// The expectations ProcedureRun.EvaluateChecks actually implements.
            ///
            /// It fails an unknown one closed and names it, which is right - but
            /// only when the run REACHES the check, which on a nine-step route is
            /// after eight steps have run and some of them have written. The
            /// catalogue can say the same thing for nothing, so it does.
            ///
            /// The one that made this worth writing down: FOUR checks in one
            /// campaign were written as "false" against integer counts, and the
            /// false predicate requires a Boolean - so a clean run would have been
            /// reported as unacceptable by every one of them.
            /// </summary>
            public static readonly string[] Evaluable = { "true", "false", "zero", "non_empty", "all_true" };

            public bool IsEvaluable =>
                Expect != null && Evaluable.Contains(Expect, StringComparer.Ordinal);

            public JObject Json()
            {
                var o = new JObject
                {
                    ["step"] = Step,
                    ["path"] = Path,
                    ["expect"] = Expect,
                    ["why"] = Why,
                    ["evaluable"] = IsEvaluable
                };
                if (!IsEvaluable)
                    o["evaluable_means"] =
                        "'" + (Expect ?? "(none)") + "' is not an expectation this build evaluates, so this " +
                        "check can never pass. Known: " + string.Join(", ", Evaluable) + ".";
                return o;
            }
        }

        internal sealed class Procedure
        {
            public string Id;
            public string Title;
            public int Version;
            public string Permission;
            public string Outcome;
            public string[] Tools;
            public string[] Inputs;
            public string Scope;
            public string Output;
            public string[] Errors;
            public string[] DependsOn;
            public string Acceptance;

            /// <summary>The route. Empty means this entry is still a tool list.</summary>
            public Step[] Steps = new Step[0];

            /// <summary>
            /// The run inputs, as a JSON Schema fragment. Null means this procedure's
            /// inputs are prose only and it cannot be started as an executable run.
            ///
            /// THE PROSE LIST STAYS, because it says WHY each input is needed and a schema
            /// cannot. This says what a program has to check before starting.
            /// </summary>
            public string InputSchemaJson;

            /// <summary>
            /// The acceptance criterion, as checks a program can evaluate. Empty means the
            /// criterion is prose and a person has to read it.
            ///
            /// NOT A SUBSTITUTE FOR THE PROSE. `Acceptance` says what the MODEL must look
            /// like; these say what the recorded results must show, which is the part a
            /// program can decide. Reporting the second as though it were the first is the
            /// error this pair exists to prevent, and the run summary keeps them apart.
            /// </summary>
            public AcceptanceCheck[] Checks = new AcceptanceCheck[0];

            /// <summary>A worked example somebody can follow, or null.</summary>
            public string Example;

            /// <summary>What this procedure is known NOT to cover.</summary>
            public string[] Limits = new string[0];

            /// <summary>
            /// "steps" when the route is written, "tool_list_only" when it is not.
            ///
            /// Reported rather than hidden: an entry that names its tools and not its order is
            /// documentation, and a catalogue where those look identical to real procedures is
            /// a catalogue nobody can plan from.
            /// </summary>
            public string Detail
            {
                get
                {
                    if (Steps == null || Steps.Length == 0) return "tool_list_only";
                    bool runnable = InputSchemaJson != null && Steps.All(s => s.ArgumentsJson != null);
                    return runnable ? "executable" : "steps";
                }
            }

            /// <summary>
            /// Does every templated step agree with the tool it names?
            ///
            /// A procedure whose templates disagree with the contract is worse than
            /// one with no templates at all: the second refuses to be dispatched and
            /// says why, the first is dispatched and fails somewhere in the middle.
            /// </summary>
            public bool TemplatesAgree()
            {
                foreach (Step step in Steps ?? new Step[0])
                {
                    JObject audit = step.TemplateAudit();
                    if (audit != null && audit.Value<bool?>("agrees") != true) return false;
                }
                return true;
            }

            public JObject Json() => new JObject
            {
                ["id"] = Id,
                ["title"] = Title,
                ["version"] = Version,
                ["minimum_permission"] = Permission,
                ["expected_outcome"] = Outcome,
                ["prompt"] = Id,
                ["tools"] = new JArray(Tools),
                ["inputs"] = new JArray(Inputs),
                ["scope"] = Scope,
                ["output"] = Output,
                ["errors"] = new JArray(Errors),
                ["depends_on"] = new JArray(DependsOn),
                ["acceptance"] = Acceptance,
                ["detail"] = Detail,
                ["input_schema"] = InputSchemaJson == null
                    ? (JToken)JValue.CreateNull() : JObject.Parse(InputSchemaJson),
                ["acceptance_checks"] = new JArray((Checks ?? new AcceptanceCheck[0])
                    .Select(c => (JToken)c.Json())),
                ["acceptance_checks_evaluable"] = (Checks ?? new AcceptanceCheck[0]).All(c => c.IsEvaluable),
                ["steps"] = new JArray((Steps ?? new Step[0]).Select(step => (JToken)step.Json())),
                ["templates_agree_with_their_tools"] = TemplatesAgree(),
                ["templates_mean"] = TemplatesAgree()
                    ? "every step that has a template sends only keys its tool declares and supplies every " +
                      "key its tool requires. Checked against the contract, not asserted."
                    : "AT LEAST ONE STEP'S TEMPLATE DISAGREES WITH ITS TOOL. See template_audit on the step. " +
                      "A run would fail at that step - which, for a step in the middle, means after the " +
                      "earlier ones have already written.",
                ["example"] = Example,
                ["limits"] = new JArray(Limits ?? new string[0]),
                ["detail_means"] = Detail == "executable"
                    ? "EXECUTABLE. Every input has a schema and every step has an argument template, so " +
                      "horizun_run_procedure can dispatch it. The write controls are untouched: a step that " +
                      "writes still carries its own dry_run and confirmation, and a step that needs somebody " +
                      "to decide stops the run."
                    : Detail == "steps"
                    ? "THE ROUTE IS WRITTEN AND IS NOT EXECUTABLE. Each step names what it takes from the " +
                      "ones before it, what must already be true, what to do when it fails and what to read " +
                      "back - in prose, for a person. It has no argument templates, so nothing can dispatch " +
                      "it: horizun_run_procedure refuses it by name rather than guessing arguments."
                    : "TOOL LIST ONLY. This entry names its tools, its inputs and its acceptance criterion " +
                      "and does NOT say what to call in what order with what from the previous step. That " +
                      "is documentation, not a procedure, and it is labelled so rather than left to look " +
                      "like the ones that are finished.",
                ["evidence"] =
                    "Tool replies must state measured findings, coverage and verification. Unknown coverage is " +
                    "never a pass, and a step that could not look is not a step that found nothing."
            };
        }
    }
}
