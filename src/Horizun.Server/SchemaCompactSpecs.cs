// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// THE COMPACT TABLE (HORIZUN_SCHEMA_MODE=compact, SchemaCompact.cs). One row per tool of the
// toolset "local" and of the core: the description a small model reads (hand-written, English,
// what it does + when to use it, at most 200 characters INCLUDING the closing
// "Full schema: horizun://contract/tools/<name>") and the arguments it shows besides the ones
// every tool keeps (its `required` and the write-flow ones: target_document, dry_run,
// confirmation_token, idempotency_key).
//
// HOW TO READ A ROW. A("name", "why") - `why` is documentation, never published. A("name", "why",
// note: "...") publishes `note` instead of the contract's description, for the arguments where
// the contract's text is an essay or misses a trap that was MEASURED; note: "" publishes none
// (name and type are enough). schema: replaces the argument's whole schema, for the two that are a
// union or a 46-value enum a small model cannot use. Items("array_arg", ...) lists the fields of
// an array argument's items that are shown (plus whatever the item requires).
//
// WHAT IS NOT HERE. Every tool outside the local toolset and the core has no row: it shows its
// required and flow arguments plus its first SchemaCompact.GenericExtraArguments, and a cut of
// its contract description. A tool in this table is a tool somebody measured; to add one, add a
// row and let SchemaCompactTests hold the rest. An argument that a row omits is still ACCEPTED:
// the call is validated against the full contract.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class SchemaCompactSpecs
    {
        internal static bool HasSpec(string tool) => Table.ContainsKey(tool);

        internal static CompactSpec Find(string tool)
        {
            Table.TryGetValue(tool, out CompactSpec spec);
            return spec;
        }

        private static CompactArg A(string name, string why, string note = null, JObject schema = null) =>
            new CompactArg { Name = name, Why = why, Note = note, Schema = schema };

        private static CompactSpec Row(string description, CompactArg[] args, params KeyValuePair<string, CompactArg[]>[] items)
        {
            var spec = new CompactSpec { Description = description, Args = args };
            foreach (var kv in items) spec.ItemArgs[kv.Key] = kv.Value;
            return spec;
        }

        private static KeyValuePair<string, CompactArg[]> Items(string argument, params CompactArg[] fields) =>
            new KeyValuePair<string, CompactArg[]>(argument, fields);

        private static readonly CompactArg[] None = new CompactArg[0];

        // A profile is a LIST OF LOOPS, each loop a list of [x,y,z] points. The contract also accepts a
        // bare list of [x,y] points (a single 2D loop); that second shape is a union a small model
        // mixes up, and the server still accepts it.
        private static readonly JObject ProfileSchema = JObject.Parse(@"{
            ""type"": ""array"", ""minItems"": 1,
            ""items"": { ""type"": ""array"", ""minItems"": 2,
                         ""items"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } } },
            ""description"": ""A LIST OF LOOPS; each loop is a list of [x,y,z] points, outer loop first, holes after. Z is ABSOLUTE from the internal origin, not relative to the level.""
        }");

        /// <summary>The operations of horizun_manage_views a model uses first (SchemaCompactTests checks each is in the contract's enum).</summary>
        internal static readonly string[] ViewOperations =
        {
            "create_floor_plan", "create_ceiling_plan", "create_section", "create_elevation", "create_3d", "create_drafting",
            "duplicate_view", "apply_template", "create_sheet", "place_view", "place_schedule", "create_filter", "apply_filter",
            "hide_elements", "isolate_elements", "set_view_range", "set_crop"
        };

        private static readonly JObject ViewOperationSchema = new JObject
        {
            ["type"] = "string",
            ["description"] = "What to do: " + string.Join(", ", ViewOperations) + ". More in the full schema."
        };

        /// <summary>The scan sections worth naming (SchemaCompactTests checks each is in the contract's enum).</summary>
        internal static readonly string[] ScanSections =
        {
            "document", "categories", "cleanliness", "naming", "documentation", "health", "links", "worksets", "views", "sheets"
        };

        private static readonly JObject ScanSectionsSchema = new JObject
        {
            ["type"] = "array",
            ["items"] = new JObject { ["type"] = "string" },
            ["description"] = "Only these sections, e.g. " + string.Join(", ", ScanSections) + ". Default: all."
        };

        private static readonly Dictionary<string, CompactSpec> Table = new Dictionary<string, CompactSpec>(StringComparer.Ordinal)
        {
            // ---- core: welded to every selection --------------------------------------------------

            { "horizun_health", Row(
                "Call FIRST. Shows whether the bridge is alive, which Revit and which document is ACTIVE, and which toolsets are enabled.",
                None /* include_verification_catalog: a long catalogue nobody needs to start a session */) },

            { "horizun_target", Row(
                "Pick which running Revit (year or pid) later calls go to. With no arguments it lists the running instances.",
                new[] {
                    A("year", "the usual way to choose between two Revit versions", note: "Revit year, e.g. \"2025\", or \"auto\"."),
                    A("pid", "two instances of the same year: the only way to tell them apart", note: "")
                }) },

            { "horizun_job_status", Row(
                "Progress and result of a long job by job_id; never touches Revit. Use it after horizun_submit_job.",
                new[] {
                    A("job_id", "which job; omitted lists the latest", note: ""),
                    A("limit", "how many recent jobs to list when job_id is omitted", note: "")
                }) },

            { "horizun_submit_job", Row(
                "Run another Horizun tool in the background and get a job_id at once. Use it for long runs; poll with horizun_job_status.",
                new[] {
                    A("tool", "which tool to run", note: "Name of the tool to run."),
                    A("arguments", "that tool's arguments, exactly as its own schema lists them", note: "That tool's arguments.")
                    // sequence / models / batch / resume / retain: ordered sweeps and resumes, not a first job
                }) },

            // ---- reading the model --------------------------------------------------------------

            { "get_document_info", Row(
                "Facts about the active Revit document: title, path, model identity. Read-only. Use it to confirm which model you are in.",
                None) },

            { "horizun_navigate", Row(
                "Select, zoom to or open elements and views in the Revit window. Read-only on the model; use it to show the user something.",
                new[] {
                    A("element_ids", "what to select or frame, in the host document", note: ""),
                    A("view_id", "which view open_view activates", note: "")
                    // selections: composite (link instance + element) identities, for clash results
                }) },

            { "horizun_list_elements", Row(
                "List elements of ONE category (e.g. Walls) with exact totals and paging. Use horizun_query_model to filter by level, type or parameter.",
                new[] {
                    A("include_links", "linked models are read by default; false limits it to the host", note: ""),
                    A("offset", "paging", note: ""),
                    A("max_rows", "page size", note: "")
                    // cooperative: yield-to-user tuning, not something a model chooses
                }) },

            { "horizun_query_model", Row(
                "Read-only element query: filter by category, level, name, type or parameter; list rows or count/sum with group_by. Use it for any lookup.",
                new[] {
                    A("categories", "the first filter of nearly every question", note: "Category names, e.g. [\"Walls\",\"Doors\"]."),
                    A("element_ids", "re-read known elements", note: "Read exactly these elements."),
                    A("level", "second most common filter (substring of the level name)", note: "Substring of the level name."),
                    A("name", "filter by element name", note: "Substring of the element name."),
                    A("type", "filter by type name", note: "Substring of the type name."),
                    A("parameters", "parameter predicates (name, operator, value): the only way to filter by a value",
                      note: "Filters, all must match: [{name, operator, value}]."),
                    A("return_parameters", "which parameter values to bring back with each row", note: "Parameter names to return with each row."),
                    A("group_by", "counts per category/level/type in ONE call, instead of paging every row",
                      note: "Count instead of list: groups over the WHOLE match in one call."),
                    A("sum_parameters", "sums per group; needs group_by", note: "With group_by: numeric parameters to sum."),
                    A("response_mode", "full | compact | summary; compact is what this mode asks for by default",
                      note: "compact (default here): lean rows. full: all fields. summary: counts only, no rows."),
                    A("max_rows", "page size", note: ""),
                    A("cursor", "the next page: the stale-detecting cursor a truncated reply returns", note: "next_cursor of the previous page."),
                    A("include_types", "type elements (not instances): how to find a type_id for a create", note: "")
                    // target_document is kept for every tool that has it.
                    // bounding_box, scope, view_id, source_models, include_*: specialised; still accepted
                }) },

            { "horizun_model_scan", Row(
                "Deep read-only scan of the whole model: cleanliness, naming, views and sheets, warnings, links, worksets. Use it for model quality checks.",
                new[] {
                    A("response_mode", "summary samples each bucket; full lists them", note: "summary: 3 samples per bucket. full: everything."),
                    A("top", "how many items per bucket", note: ""),
                    A("sections", "run only some sections: a full scan is slow on a big model",
                      schema: ScanSectionsSchema),
                    A("cursor", "continue a truncated scan", note: "")
                    // the *_profile / *_rules objects are the client's own standards, supplied as data
                }) },

            { "horizun_model_diff", Row(
                "Compare two model states: record a snapshot, then compare before/after to list added, deleted and changed elements.",
                new[] {
                    A("before", "snapshot id to compare from", note: "Snapshot id to compare from."),
                    A("after", "snapshot id to compare to; 'active' is the model now", note: "Snapshot id, or 'active' (default)."),
                    A("categories", "narrow a compare to some categories", note: ""),
                    A("limit", "page size", note: ""),
                    A("offset", "paging", note: "")
                    // file_path/expected_version (detached snapshot), colorize and quality-trend inputs: specialised
                }) },

            { "horizun_quantities", Row(
                "Quantities from the model (mode: volume, takeoff, room_finishes, carbon). Read-only. Use it for takeoffs and volume checks.",
                new[] {
                    A("mode", "which question: volume | takeoff | room_finishes | carbon", note: ""),
                    A("category", "one category to measure", note: "One category, e.g. Walls."),
                    A("categories", "several categories (takeoff and room_finishes)", note: ""),
                    A("element_ids", "measure exactly these elements", note: ""),
                    A("level", "limit to one level", note: "")
                    // quantities (the takeoff definitions), phase, group_by, top, carbon inputs: specialised
                }) },

            { "horizun_capture_view", Row(
                "Export a view as a PNG so you can look at the model. Omit view_id and view_name for the ACTIVE view.",
                new[] {
                    // MEASURED (qwen3:8b): with no note it invented view_name "Active View" and view_id 0.
                    A("view_id", "which view, by id", note: "ElementId of a view. Omit for the active view; never 0."),
                    A("view_name", "which view, by name", note: "Exact view name. Omit for the active view."),
                    A("pixel_size", "image size", note: ""),
                    A("orientation", "top | front | right | isometric, when no view is named", note: "")
                    // display_style, element_ids, margin, hide_*: finishing touches
                }) },

            // ---- the six typed writes ---------------------------------------------------------------

            { "horizun_create_elements", Row(
                "Create levels, walls, floors, rooms, family instances and more in one batch. Rehearse with dry_run, then apply with the token.",
                new[] {
                    A("units", "every length and coordinate of the call is in this unit; the default surprises",
                      note: "Top level, not inside elements. mm (default), m or feet, for EVERY length in the call."),
                    A("elements", "the batch",
                      note: "1-2000 elements: a kind plus that kind's fields. All fields: horizun://contract/tools/horizun_create_elements/{kind}.")
                    // tabular_source (CSV batch), response_mode, validation_mode, transaction_name: not first-call material
                },
                Items("elements",
                    A("kind", "discriminator: the WHOLE enum stays so the model sees every kind that exists",
                      note: "Common: level, wall, floor, room, family_instance."),
                    A("name", "level/grid/room name", note: "level, grid or room name."),
                    A("number", "a room's number is separate from its name and must be unique", note: "room: unique room number."),
                    A("level_id", "walls, floors, rooms and instances sit on a level",
                      note: "ElementId of a Level, read from horizun_query_model categories=[\"OST_Levels\"]. Never 0."),
                    A("type_id", "wall/floor/family type to use", note: "ElementId of the type (horizun_query_model include_types=true)."),
                    A("start", "wall start point. MEASURED trap: models treat Z as level-relative; it is absolute",
                      note: "wall: start [x,y,z] in `units`. Z is ABSOLUTE (internal origin), NOT relative to the level."),
                    A("end", "wall end point; same Z trap",
                      note: "wall: end [x,y,z] in `units`. Z is ABSOLUTE (internal origin), NOT relative to the level."),
                    A("height", "wall height", note: "wall: height in `units`."),
                    A("profile", "floor/roof outline. MEASURED trap: a flat list of points instead of a list of loops",
                      schema: ProfileSchema),
                    A("point", "family_instance / room location",
                      note: "family_instance or room: [x,y] or [x,y,z] in `units`."),
                    A("elevation", "level elevation", note: "level: elevation in `units`."),
                    A("host_id", "hosted families (doors, windows) need their wall",
                      note: "family_instance: ElementId of the host wall or floor."),
                    A("coordinate_mode", "REQUIRED for point families; absolute vs level-relative Z",
                      note: "family_instance with point (required): absolute (Z from origin) or level_offset (Z from level_id)."),
                    A("rotation_degrees", "orient a placed instance", note: ""),
                    A("parameters", "set instance parameters at creation", note: "Instance parameters to set: {\"Name\": value}.")
                    // arc, slopes, structural, fitting, MEP and stair fields: other kinds; still accepted
                )) },

            { "horizun_transform_elements", Row(
                "Move, copy, rotate, mirror, pin or change the type of existing elements by id, in one batch. Rehearse first, then apply.",
                new[] {
                    A("units", "length unit of vectors and points; mm by default", note: ""),
                    A("operations", "the batch",
                      note: "1-500 operations: operation + element_ids + that operation's fields. All: horizun://contract/tools/horizun_transform_elements.")
                    // tolerance_mm: verification tolerance, rarely changed
                },
                Items("operations",
                    A("operation", "discriminator: the whole enum stays", note: ""),
                    A("element_ids", "what to act on", note: ""),
                    A("vector", "move/copy translation", note: "move/copy: translation [x,y,z] in `units`."),
                    A("angle_degrees", "rotate", note: ""),
                    A("axis_start", "rotate: axis start", note: "rotate: axis start point."),
                    A("axis_end", "rotate: axis end", note: "rotate: axis end point."),
                    A("type_id", "change_type target", note: "")
                    // mirror plane, set_curve start/end, array count...: other operations; still accepted
                )) },

            { "horizun_write_params_verified", Row(
                "Write parameter values on elements, types or project info, then re-read them to verify. Rehearse first, then apply.",
                new[] {
                    A("writes", "the batch", note: "Each write names ONE parameter on ONE target: {target_id, parameter, value}."),
                    A("on_failure", "atomic (default) vs best_effort: changes what a partial failure leaves behind",
                      note: "atomic (default): all or nothing. best_effort: keep what worked.")
                    // tabular_source (CSV batch), sequence, max_rows, allow_vary_between_groups: not first-call material
                },
                Items("writes",
                    A("target_id", "element id, or type id (a type id changes every instance of it)",
                      note: "Element id, or type id (a type id changes EVERY instance)."),
                    // MEASURED (qwen3:8b): it sent target="project_info" together with a wall's target_id.
                    A("target", "project_info instead of an id",
                      note: "Omit for elements. Only \"project_info\" for Project Information, without target_id."),
                    A("parameter", "BuiltInParameter name, GUID or UI name",
                      note: "Name as Revit shows it in THIS model's language (e.g. Comments), or a BuiltInParameter."),
                    A("value", "the new value", note: "")
                )) },

            { "horizun_delete_verified", Row(
                "Delete elements by id (mode=ids) or purge unused ones (mode=purge_unused); reports only what the model confirms. Rehearse first.",
                new[] {
                    A("ids", "what to delete in mode=ids", note: "ElementIds to delete (mode=ids)."),
                    A("protect_ids", "purge: never delete these", note: "")
                    // max_passes, id_cap: limits nobody changes on a first call
                }) },

            { "horizun_manage_views", Row(
                "Create and edit views and sheets: plans, sections, 3D, templates, sheets, viewports, filters. Rehearse first, then apply.",
                new[] {
                    A("units", "length unit of points and offsets; mm by default", note: ""),
                    A("actions", "the batch",
                      note: "1-500 actions: operation + that operation's fields. All fields: horizun://contract/tools/horizun_manage_views.")
                },
                Items("actions",
                    A("operation", "discriminator with 46 values (~1 000 characters as an enum): the common ones are named in the " +
                                   "description instead, the contract keeps the whole list and the call is validated against it",
                      schema: ViewOperationSchema),
                    A("key", "alias so a later action can name an object this one creates", note: "Your alias for what this action creates."),
                    A("name", "name of the new view or sheet", note: ""),
                    A("number", "sheet number", note: ""),
                    A("level_id", "level of a new plan", note: ""),
                    A("view_family_type_id", "view type of a new view", note: ""),
                    A("view_id", "an existing view", note: ""),
                    A("view_key", "a view created earlier in the same batch", note: "Key of a view created earlier in this batch."),
                    A("source_view_id", "view to duplicate", note: ""),
                    A("template_view_id", "view template to apply", note: ""),
                    A("sheet_id", "an existing sheet", note: ""),
                    A("title_block_type_id", "title block of a new sheet", note: "")
                    // point, sheet_key, filters, crops, overrides...: other operations; still accepted
                )) },

            { "horizun_undo", Row(
                "Undo the last Horizun batch (operation=undo_last) or list undoable batches (operation=list). Rehearse the undo first.",
                new[] {
                    A("operation", "list (default) or undo_last", note: "")
                }) }
        };
    }
}
