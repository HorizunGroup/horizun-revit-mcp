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
        public const int ProtocolVersion = 2;

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

        public static readonly List<CommandContract> All = Annotate(new List<CommandContract>{
            new CommandContract
            {
                Name = "get_document_info",
                Command = "get_document_info",
                Description = "Basic facts about the active Revit document: title, path, version, element count. Read-only.",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject(),
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
                    "one attached to the wrong instance.",
                InputSchema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject(),
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
                    "the LOCAL file only - it is NOT a synchronize with central, and the response says so.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""target_document"": { ""type"": ""string"", ""description"": ""REQUIRED. Title or full path of the document to change. It must be the document ACTIVE in Revit; this never switches documents for you. Aliases accepted for compatibility: expected_document, target_document_title."" },
    ""expected_document"": { ""type"": ""string"", ""description"": ""GUARD. If given, the save is refused unless the ACTIVE document's title matches it. Cheap insurance against saving the wrong open model."" }
  },
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
                    "flag is required for it. " +
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
    ""path"": { ""type"": ""string"", ""description"": ""Full path of the .rvt or .rfa to open. Mutually exclusive with the cloud_* GUIDs - naming a model two ways is refused, not guessed."" },
    ""expected_version"": { ""type"": ""string"", ""description"": ""OPTIONAL here, REQUIRED by horizun_document_session, and the same check either way: the Revit year you believe this bridge is, e.g. '2026'. Checked against the HOST before anything is touched - the file and the host can both be 2025 and you can still be talking to the Revit next door. For a cloud model this is the only version check that CAN run."" },
    ""cloud_project_guid"": { ""type"": ""string"", ""description"": ""ACC / BIM 360 PROJECT GUID as Revit knows it. NOT the project id in the ACC web URL, which is a different identifier for the same project. An all-zero GUID is refused: Revit would build a path that looks valid and resolves to nothing."" },
    ""cloud_model_guid"": { ""type"": ""string"", ""description"": ""ACC / BIM 360 MODEL GUID as Revit knows it. NOT the 'urn:adsk.wipprod:dm.lineage:...' from the web UI - that decodes to a valid-looking GUID from the document manager, and opening it answers 'the central model is missing'. These GUIDs can be read off Revit's own CollaborationCache on disk."" },
    ""cloud_region"": { ""type"": ""string"", ""default"": ""US"", ""description"": ""Data centre region of the ACC hub, e.g. 'US' or 'EMEA'. Wrong region means the model is simply not found there."" },
    ""allow_upgrade"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Permit opening a file saved in an OLDER Revit version, which upgrades it irreversibly. Also required when the file's version cannot be read at all - unknown is not treated as safe. It CANNOT open a file saved in a NEWER Revit: there is no downgrade, so that is refused whatever this says. Applies to 'path' only; there is no equivalent check to waive for a cloud model."" },
    ""detach"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Open a workshared model detached from central (worksets preserved). The safe way to read a central model, on disk or in the cloud. A detached document has no path that exists until you save it - Revit reports a synthetic '<original>_detached.rvt'."" },
    ""open_central"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Permit opening the CENTRAL model directly - working in the file everyone else synchronizes to. REQUIRED for a cloud model unless you pass detach: a model in ACC / BIM 360 is the central, and living in the cloud rather than on a server share does not make it less shared. Prefer detach."" },
    ""open_all_worksets"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Open every workset. Needed when something downstream MEASURES worksets, because a closed workset is indistinguishable from an empty one - a scan over a partly loaded model quietly scores the parts it cannot see. IT CAN ALSO KILL REVIT, and that is measured, not feared: on 2026-07-30, 2 of 24 ACC models took Revit 2025.4 down with an access violation (0xc0000005) inside SelectedPartitionsForEdit/decommitDocument on open, identical signature both times, with 30 GB of RAM free - and both opened and read fine with this left false. So it is off by default, and if a specific model dies on open this is the first thing to drop. The cost of dropping it is that whatever measures worksets is then measuring what got loaded, not what is there."" },
    ""audit"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Run Revit's audit while opening. Slow, and it can modify the model to repair it."" },
    ""on_open_dialog"": { ""type"": ""string"", ""enum"": [""cancel"", ""dismiss""], ""default"": ""cancel"", ""description"": ""How a modal dialog raised WHILE opening is answered when nobody is at the keyboard. 'cancel' (default) presses Cancel - the safe answer, and a model that will not open unattended is a finding. 'dismiss' presses OK/continue, for READING a model whose open raises a dialog whose only unattended answer is 'acknowledge and continue'. Best effort: a dialog whose continue button is not the default is recorded as answered in revit_said rather than silently proceeding. Scoped to the open call only; every other dialog still cancels."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_relinquish_all",
                Command = "horizun_relinquish_all",
                Description =
                    "Relinquish every workset and element this user owns in the ACTIVE workshared document, then " +
                    "MEASURE it at both levels: workset owners and every collectable element's checkout status are " +
                    "read before and after; unreadable element ownership makes the result unknown, never complete. " +
                    "The element/workset ids returned by Revit are call telemetry, not a substitute for the " +
                    "postcondition census, so a partial relinquish cannot pass as complete. Refuses a document that is " +
                    "not workshared rather than reporting a cheerful no-op - that request means the caller " +
                    "believes something false about the model. Does not synchronize and does not save.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""target_document"": { ""type"": ""string"", ""description"": ""REQUIRED. Title or full path of the document to change. It must be the document ACTIVE in Revit; this never switches documents for you. Aliases accepted for compatibility: expected_document, target_document_title."" },
    ""expected_document"": { ""type"": ""string"", ""description"": ""GUARD. If given, the relinquish is refused unless the ACTIVE document's title matches it."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_capture_view",
                Command = "horizun_capture_view",
                Description =
                    "Export a view as a PNG and hand the IMAGE back, so you can actually look at the model rather " +
                    "than only read its parameters. Reports the file Revit really produced: ExportImage treats the " +
                    "path you give it as a stem and appends the view type and name, so the requested path is not " +
                    "the resulting one, and a handler that echoes the request is naming a file that does not exist. " +
                    "Pixel dimensions are read out of the PNG header - what the file IS, not what was asked for. " +
                    "Refuses schedules, which Revit cannot raster-export, instead of reporting a capture that is " +
                    "not there.",
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
                Name = "horizun_create_schedule",
                Command = "horizun_create_schedule",
                Description =
                    "Create one native Revit schedule for one category, optionally including elements from loaded RVT links. " +
                    "Dry-run is the default. Resolves fields by Revit display name or stable token, groups non-itemized schedules, commits once, " +
                    "then re-reads the schedule, fields, IncludeLinkedFiles flag and body row count. Zero host elements is valid: " +
                    "the linked elements are included by Revit itself when include_links=true.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""category"", ""name"", ""target_document""],
  ""properties"": {
    ""category"": { ""type"": ""string"", ""description"": ""BuiltInCategory token such as OST_Walls, or the Revit category display name."" },
    ""name"": { ""type"": ""string"", ""description"": ""Name of the new schedule. An existing name is refused, never overwritten."" },
    ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Fields to add: localized Revit display names, Count/Family/Type aliases, or BuiltInParameter tokens. Defaults to Count, Family, Type."" },
    ""include_links"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Set Revit's Include elements in links option."" },
    ""itemized"": { ""type"": ""boolean"", ""default"": false, ""description"": ""List every element when true; otherwise group by the requested non-Count fields."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Validate category and scope without opening a transaction."" },
    ""confirmation_token"": { ""type"": ""string"", ""description"": ""Required when dry_run=false; returned by the exact dry run."" },
    ""target_document"": { ""type"": ""string"", ""description"": ""REQUIRED. Title or full path of the ACTIVE document to change."" }
  },
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
  ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""select"", ""clear_selection"", ""zoom"", ""select_and_zoom"", ""open_view""] },
    ""element_ids"": { ""type"": ""array"", ""maxItems"": 5000, ""items"": { ""type"": ""integer"" }, ""description"": ""Host-document ElementIds for select/zoom. Linked element ids are document-local and cannot be selected without their link-instance identity."" },
    ""view_id"": { ""type"": ""integer"", ""description"": ""Host-document ViewId for open_view."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_create_elements",
                Command = "horizun_create_elements",
                Description =
                    "Create a heterogeneous batch of architectural, structural and MEP elements in one atomic transaction: " +
                    "levels, grids, walls, floors, ceilings, footprint roofs, rooms, family instances, structural framing, " +
                    "structural columns, ducts, pipes, conduits and cable trays. Geometry enters in explicit " +
                    "mm/m/feet units; every referenced type and level resolves before a transaction opens. Dry-run " +
                    "is the default, apply requires confirmation and idempotency, and every created id is re-read " +
                    "after commit and checked against the requested element kind.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""elements""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""elements"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": {
      ""type"": ""object"", ""required"": [""kind""], ""properties"": {
        ""kind"": { ""type"": ""string"", ""enum"": [""level"", ""grid"", ""wall"", ""floor"", ""ceiling"", ""roof"", ""room"", ""family_instance"", ""structural_framing"", ""structural_column"", ""duct"", ""pipe"", ""conduit"", ""cable_tray""] },
        ""name"": { ""type"": ""string"", ""description"": ""Level/grid name where supported."" },
        ""elevation"": { ""type"": ""number"" },
        ""start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""profile"": { ""type"": ""array"", ""description"": ""Floor/ceiling loops, or one roof footprint loop; each loop is an array of at least three XYZ points."" },
        ""level_id"": { ""type"": ""integer"" }, ""type_id"": { ""type"": ""integer"" },
        ""system_type_id"": { ""type"": ""integer"", ""description"": ""Required for duct and pipe."" },
        ""height"": { ""type"": ""number"" }, ""offset"": { ""type"": ""number"", ""default"": 0 },
        ""slope_degrees"": { ""type"": ""number"", ""minimum"": 0, ""exclusiveMaximum"": 90, ""default"": 0, ""description"": ""Uniform slope on all footprint-roof edges."" },
        ""flip"": { ""type"": ""boolean"", ""default"": false }, ""structural"": { ""type"": ""boolean"", ""default"": false },
        ""structural_type"": { ""type"": ""string"", ""enum"": [""NonStructural"", ""Beam"", ""Brace"", ""Column"", ""Footing""] }
      }, ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: create elements"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_create_family",
                Command = "horizun_create_family",
                Description =
                    "Compile a loadable parametric RFA from an absolute Revit family-template (.rft) path. The typed " +
                    "specification covers family parameters (length/area/volume/angle/number/integer/yes-no/text/material), " +
                    "formulas, named types with per-type values, solid or void extrusions/blends/revolutions/sweeps/swept " +
                    "blends, reference planes, labeled dimensions, symbolic/model lines and point-placed nested RFA " +
                    "instances with outer-parameter associations, association " +
                    "of form depth/offset/angle/material/visibility to family parameters, and pipe/duct/electrical/conduit/" +
                    "cable-tray connectors hosted on a planar face selected by normal with optional size-parameter " +
                    "associations. Dimensions take an optional family view, an explicit linear DimensionType, lock and " +
                    "EQ, and are verified against the reference planes they measure. Dry-run opens no document. Apply " +
                    "creates one family transaction, re-reads forms, connectors, parameters and types, saves the RFA, " +
                    "then CLOSES and REOPENS the saved file and re-reads every dimension from the bytes on disk - " +
                    "references available, label, measured value - because a non-empty SaveAs is not verification. " +
                    "Only the reopened, verified file is loaded into the guarded project, where the Family is re-read " +
                    "again. System-family types are not RFA files and belong to " +
                    "horizun_manage_system_types; general in-place-family creation is not exposed by the public Revit API. " +
                    "Requires full_write because it creates an external file.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""template_path"", ""output_path""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""template_path"": { ""type"": ""string"", ""description"": ""Absolute existing .rft path. The template determines category and hosting behavior."" },
    ""output_path"": { ""type"": ""string"", ""description"": ""Absolute .rfa destination in an existing directory."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""parameters"": { ""type"": ""array"", ""items"": {
      ""type"": ""object"", ""required"": [""name""], ""properties"": {
        ""name"": { ""type"": ""string"" },
        ""data_type"": { ""type"": ""string"", ""enum"": [""length"", ""area"", ""volume"", ""angle"", ""number"", ""integer"", ""yesno"", ""text"", ""material""], ""default"": ""text"" },
        ""group"": { ""type"": ""string"", ""enum"": [""data"", ""identity_data"", ""geometry"", ""materials"", ""general""], ""default"": ""data"" },
        ""instance"": { ""type"": ""boolean"", ""default"": false },
        ""formula"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""Optional Revit family formula. Omit to preserve an existing template formula."" }
      }, ""additionalProperties"": false
    }},
    ""types"": { ""type"": ""array"", ""items"": {
      ""type"": ""object"", ""required"": [""name""], ""properties"": {
        ""name"": { ""type"": ""string"" },
        ""values"": { ""type"": ""object"", ""additionalProperties"": { ""type"": [""string"", ""number"", ""boolean"", ""null""] } }
      }, ""additionalProperties"": false
    }},
    ""forms"": { ""type"": ""array"", ""items"": {
      ""type"": ""object"", ""required"": [""kind"", ""profile""], ""properties"": {
        ""key"": { ""type"": ""string"" },
        ""kind"": { ""type"": ""string"", ""enum"": [""extrusion"", ""blend"", ""revolution"", ""sweep"", ""swept_blend""] },
        ""solid"": { ""type"": ""boolean"", ""default"": true },
        ""plane"": { ""type"": ""string"", ""enum"": [""xy"", ""xz"", ""yz""] },
        ""profile"": { ""type"": ""array"", ""description"": ""One or more closed loops; each loop has at least three XYZ points."" },
        ""top_profile"": { ""type"": ""array"", ""description"": ""Required for blend; currently exactly one loop."" },
        ""depth"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 1000 },
        ""bottom_offset"": { ""type"": ""number"", ""default"": 0 }, ""top_offset"": { ""type"": ""number"", ""default"": 1000 },
        ""axis_start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""axis_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""path"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 100, ""description"": ""Sweep polyline or single swept-blend segment as XYZ points."" },
        ""path_plane"": { ""type"": ""string"", ""enum"": [""xy"", ""xz"", ""yz""], ""default"": ""xz"" },
        ""profile_location_curve_index"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 },
        ""profile_plane_location"": { ""type"": ""string"", ""enum"": [""Start"", ""MidPoint"", ""End""], ""default"": ""Start"" },
        ""start_angle_degrees"": { ""type"": ""number"", ""default"": 0 }, ""end_angle_degrees"": { ""type"": ""number"", ""default"": 360 },
        ""start_parameter"": { ""type"": ""string"" }, ""end_parameter"": { ""type"": ""string"" },
        ""material_parameter"": { ""type"": ""string"" }, ""visibility_parameter"": { ""type"": ""string"" }
      }, ""additionalProperties"": false
    }},
    ""connectors"": { ""type"": ""array"", ""items"": {
      ""type"": ""object"", ""required"": [""host_form_key"", ""kind"", ""face_normal""], ""properties"": {
        ""key"": { ""type"": ""string"" }, ""host_form_key"": { ""type"": ""string"" },
        ""kind"": { ""type"": ""string"", ""enum"": [""pipe"", ""duct"", ""electrical"", ""conduit"", ""cable_tray""] },
        ""face_normal"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""system_type"": { ""type"": ""string"" }, ""profile"": { ""type"": ""string"", ""enum"": [""Round"", ""Rectangular"", ""Oval""] },
        ""primary"": { ""type"": ""boolean"", ""default"": false },
        ""diameter_parameter"": { ""type"": ""string"" }, ""width_parameter"": { ""type"": ""string"" }, ""height_parameter"": { ""type"": ""string"" }
      }, ""additionalProperties"": false
    }},
    ""reference_planes"": { ""type"": ""array"", ""items"": {
      ""type"": ""object"", ""required"": [""bubble_end"", ""free_end"", ""cut_vector""], ""properties"": {
        ""key"": { ""type"": ""string"" }, ""name"": { ""type"": ""string"" },
        ""bubble_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""free_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""cut_vector"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } }
      }, ""additionalProperties"": false
    }},
    ""dimensions"": { ""type"": ""array"", ""items"": {
      ""type"": ""object"", ""required"": [""reference_plane_keys"", ""line_start"", ""line_end""], ""properties"": {
        ""key"": { ""type"": ""string"" },
        ""reference_plane_keys"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 20, ""items"": { ""type"": ""string"" } },
        ""line_start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""line_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""label_parameter"": { ""type"": ""string"", ""description"": ""Optional declared length parameter used as the family dimension label."" },
        ""view_name"": { ""type"": ""string"", ""description"": ""Family-document view to create this dimension in. Omitted: the command's default view. Resolved against the OPEN family document, so a wrong name is refused at apply before the transaction, naming the views that exist."" },
        ""dimension_type_name"": { ""type"": ""string"", ""description"": ""A LINEAR DimensionType existing in the family document. Unknown names are refused naming the available ones."" },
        ""lock"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Lock the created dimension (IsLocked), read back in verification and again after the saved RFA is reopened. Two references only, and not combinable with label_parameter - a labeled dimension is already constrained by its parameter."" },
        ""eq"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Set the EQ constraint (AreSegmentsEqual). Requires at least three reference planes."" }
      }, ""additionalProperties"": false
    }},
    ""family_lines"": { ""type"": ""array"", ""items"": {
      ""type"": ""object"", ""required"": [""start"", ""end""], ""properties"": {
        ""key"": { ""type"": ""string"" }, ""kind"": { ""type"": ""string"", ""enum"": [""symbolic"", ""model""], ""default"": ""symbolic"" },
        ""plane"": { ""type"": ""string"", ""enum"": [""xy"", ""xz"", ""yz""], ""default"": ""xy"" },
        ""start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } }
      }, ""additionalProperties"": false
    }},
    ""nested_instances"": { ""type"": ""array"", ""maxItems"": 100, ""items"": {
      ""type"": ""object"", ""required"": [""family_path"", ""type_name"", ""point""], ""properties"": {
        ""key"": { ""type"": ""string"" },
        ""family_path"": { ""type"": ""string"", ""description"": ""Absolute existing nested .rfa path."" },
        ""type_name"": { ""type"": ""string"" },
        ""point"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""placement"": { ""type"": ""string"", ""enum"": [""model_point"", ""view_point""], ""default"": ""model_point"" },
        ""rotation_degrees"": { ""type"": ""number"", ""default"": 0 },
        ""associations"": { ""type"": ""object"", ""description"": ""Nested instance parameter name to declared outer family parameter name."", ""additionalProperties"": { ""type"": ""string"" } }
      }, ""additionalProperties"": false
    }},
    ""overwrite"": { ""type"": ""boolean"", ""default"": false },
    ""load_into_project"": { ""type"": ""boolean"", ""default"": true },
    ""overwrite_parameter_values"": { ""type"": ""boolean"", ""default"": false },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: create parametric family"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_system_types",
                Command = "horizun_manage_system_types",
                Description =
                    "Create project-resident system-family types by duplicating explicit source ElementType ids in one " +
                    "atomic transaction. This covers wall/floor/roof/ceiling and MEP system types as well as other " +
                    "non-loadable ElementTypes; loadable FamilySymbols are refused because they belong to RFA-family " +
                    "authoring. Host types can replace their complete homogeneous compound structure with typed exterior-to-" +
                    "interior layers: function, material, width, wrapping, shell/core boundaries, structural/variable layer " +
                    "and structural-deck metadata. Parameter keys resolve by BuiltInParameter, shared GUID or one unambiguous exact display " +
                    "name. Apply re-reads each duplicate's runtime class, name and raw stored values after commit; unit-aware " +
                    "strings are marked as parsed by Revit rather than falsely claimed as literal-intent verification.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"", ""description"": ""Units for compound layer widths."" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": {
      ""type"": ""object"", ""required"": [""source_type_id"", ""new_name""], ""properties"": {
        ""source_type_id"": { ""type"": ""integer"", ""description"": ""A project-resident non-FamilySymbol ElementType."" },
        ""new_name"": { ""type"": ""string"" },
        ""values"": { ""type"": ""object"", ""description"": ""Parameter spec to value. Numbers are raw Revit storage; strings use unit-aware SetValueString where applicable."", ""additionalProperties"": { ""type"": [""string"", ""number"", ""boolean"", ""null""] } },
        ""compound_structure"": { ""type"": ""object"", ""description"": ""Optional complete vertically-homogeneous composition for HostObjAttributes types. Layers are ordered exterior to interior."", ""required"": [""layers""], ""properties"": {
          ""layers"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": { ""type"": ""object"", ""required"": [""function"", ""width""], ""properties"": {
            ""function"": { ""type"": ""string"", ""enum"": [""Structure"", ""Substrate"", ""Insulation"", ""Finish1"", ""Finish2"", ""Membrane"", ""StructuralDeck""] },
            ""width"": { ""type"": ""number"", ""minimum"": 0 },
            ""material_id"": { ""type"": ""integer"", ""default"": -1 },
            ""wraps"": { ""type"": ""boolean"", ""default"": false },
            ""deck_profile_id"": { ""type"": ""integer"", ""default"": -1 },
            ""deck_embedding"": { ""type"": ""string"", ""enum"": [""MergeWithLayerAbove"", ""Standalone""], ""default"": ""Standalone"" }
          }, ""additionalProperties"": false } },
          ""exterior_shell_layers"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 },
          ""interior_shell_layers"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 },
          ""structural_layer_index"": { ""type"": ""integer"", ""minimum"": -1, ""default"": -1 },
          ""variable_layer_index"": { ""type"": ""integer"", ""minimum"": -1, ""default"": -1 },
          ""end_cap"": { ""type"": ""string"", ""enum"": [""None"", ""Exterior"", ""Interior"", ""NoEndCap""], ""default"": ""None"" },
          ""opening_wrapping"": { ""type"": ""string"", ""enum"": [""None"", ""Exterior"", ""Interior"", ""ExteriorAndInterior""], ""default"": ""None"" }
        }, ""additionalProperties"": false }
      }, ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: manage system family types"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_list_elements",
                Command = "horizun_list_elements",
                Description =
                    "List elements of one category in the active model and loaded RVT links. Totals are exact and independent " +
                    "of max_rows; every row names its source model and link instance. Unloaded links and read failures are " +
                    "reported, and host/link workset coverage travels with the result, so an empty result is never presented " +
                    "as complete when part of the federation was unavailable.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""category""],
  ""properties"": {
    ""category"": { ""type"": ""string"", ""description"": ""BuiltInCategory token such as OST_Walls, or the Revit display name."" },
    ""include_links"": { ""type"": ""boolean"", ""default"": true },
    ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 },
    ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""default"": 200 }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_query_model",
                Command = "horizun_query_model",
                Description =
                    "Composable, read-only model query across the host and loaded RVT links: filter by categories, " +
                    "family, type, name, level, parameter predicates and an optional 3D bounding box; choose the " +
                    "fields returned and receive counts grouped by category, level and source. Results use a " +
                    "stale-detecting cursor rather than a naked offset, and every unreadable element, unloaded link " +
                    "or closed workset keeps coverage from being called complete. For histograms, pass group_by " +
                    "(with optional sum_parameters) and receive aggregated groups computed server-side over the " +
                    "whole matched set in ONE call - no rows, no paging, and every sum reports how many elements " +
                    "actually contributed to it.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""BuiltInCategory tokens or localized Revit category names. Omit for all non-type elements."" },
    ""family"": { ""type"": ""string"", ""description"": ""Case-insensitive substring."" },
    ""type"": { ""type"": ""string"", ""description"": ""Case-insensitive substring of the type name."" },
    ""name"": { ""type"": ""string"", ""description"": ""Case-insensitive substring of the element name."" },
    ""level"": { ""type"": ""string"", ""description"": ""Case-insensitive substring of the level name. Matches the level the element is ASSOCIATED with, wherever its category keeps it - walls' base constraint, family instances' base level, MEP curves' reference level - not only the plain Level parameter, so it works for walls without knowing about WALL_BASE_CONSTRAINT."" },
    ""parameters"": { ""type"": ""array"", ""description"": ""All predicates must match. Names may be BuiltInParameter tokens, shared-parameter GUIDs or display names; an ambiguous display name is unreadable, never guessed."", ""items"": {
      ""type"": ""object"", ""required"": [""name"", ""operator""], ""properties"": {
        ""name"": { ""type"": ""string"" },
        ""operator"": { ""type"": ""string"", ""enum"": [""exists"", ""not_exists"", ""equals"", ""not_equals"", ""contains"", ""starts_with"", ""ends_with"", ""gt"", ""gte"", ""lt"", ""lte""] },
        ""value"": { ""description"": ""For numeric comparisons, a JSON number is compared to the raw Revit internal-unit value. Strings compare to the stored/displayed text, case-insensitively."" }
      }, ""additionalProperties"": false
    }},
    ""bounding_box"": { ""type"": ""object"", ""required"": [""min"", ""max""], ""properties"": {
      ""min"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
      ""max"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
      ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" }
    }, ""additionalProperties"": false },
    ""scope"": { ""type"": ""string"", ""enum"": [""model"", ""current_view"", ""view""], ""default"": ""model"" },
    ""view_id"": { ""type"": ""integer"", ""description"": ""Required for scope=view. View scope is host-only; combine links with model scope."" },
    ""include_links"": { ""type"": ""boolean"", ""default"": true },
    ""return_parameters"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Specific parameters to project into each row."" },
    ""include_bounding_box"": { ""type"": ""boolean"", ""default"": false },
    ""coordinate_units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""include_types"": { ""type"": ""boolean"", ""default"": false },
    ""cursor"": { ""type"": ""string"", ""description"": ""next_cursor from the previous page. It is refused if the query or result set changed."" },
    ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 100 },
    ""group_by"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""category"", ""level"", ""type"", ""family"", ""source_model"", ""source_kind""] }, ""description"": ""Aggregate instead of listing: returns groups with counts over the WHOLE matched set in one call, no rows and no cursor. 'how many wall types per floor' is group_by:[type,level]."" },
    ""parameter_format"": { ""type"": ""string"", ""enum"": [""full"", ""compact""], ""default"": ""full"", ""description"": ""compact returns each readable parameter as name:raw-value instead of the five-field object (~5x smaller per parameter). Parameters that were absent or unreadable move to a per-row parameter_issues object rather than disappearing - compact is a diet, not an amnesty."" },
    ""return_fields"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""unique_id"", ""category"", ""name"", ""family"", ""type"", ""type_id"", ""level"", ""is_element_type"", ""source_kind"", ""source_model"", ""link_instance_id""] }, ""description"": ""Row fields to include besides element_id, which is always present. The identity and federation fields repeat identically down a page and are most of the payload; name only what you will read."" },
    ""sum_parameters"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""With group_by: numeric parameters to sum per group. Each sum reports summed/absent/unreadable/non_numeric counts and a complete flag - a sum over part of a group never reads like a sum over all of it."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_transform_elements",
                Command = "horizun_transform_elements",
                Description =
                    "Apply an atomic batch of move, copy, rotate, pin, unpin or type-change operations to explicit " +
                    "host ElementIds. Dry-run resolves every target and refuses duplicate targets across operations. " +
                    "Move/rotate are accepted only for elements whose Location can be sampled and are verified from " +
                    "fresh post-commit location points; copies, pin state and type ids are likewise re-read.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""operations""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""operations"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": {
      ""type"": ""object"", ""required"": [""operation"", ""element_ids""], ""properties"": {
        ""operation"": { ""type"": ""string"", ""enum"": [""move"", ""copy"", ""rotate"", ""pin"", ""unpin"", ""change_type""] },
        ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" } },
        ""vector"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""axis_start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""axis_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""angle_degrees"": { ""type"": ""number"" }, ""type_id"": { ""type"": ""integer"" }
      }, ""additionalProperties"": false
    }},
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: transform elements"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_list_schedules",
                Command = "horizun_list_schedules",
                Description = "List native schedules with their real fields, linked-file setting, itemization, body dimensions and host/link coverage. Read-only. Titleblock revision schedules (one per titleblock family - they can be over half the list) are labelled per row and excludable; the document's revision-schedule count is reported either way.",
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
                    "Create and compose documentation in one atomic batch: floor/ceiling/structural plans, sections, " +
                    "elevations, drafting and isometric 3D views, view duplicates, template assignment, sheets, " +
                    "viewports and schedule instances. Actions may assign " +
                    "a key and later actions can reference that created object in the same transaction. Dry-run " +
                    "validates the dependency graph; apply re-reads every created or changed object after commit.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" }, ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": {
      ""type"": ""object"", ""required"": [""operation""], ""properties"": {
        ""operation"": { ""type"": ""string"", ""enum"": [""create_floor_plan"", ""create_ceiling_plan"", ""create_structural_plan"", ""create_3d"", ""create_drafting"", ""create_section"", ""create_elevation"", ""duplicate_view"", ""apply_template"", ""create_sheet"", ""place_view"", ""place_schedule""] },
        ""key"": { ""type"": ""string"", ""description"": ""Unique alias for an object this action creates."" },
        ""name"": { ""type"": ""string"" }, ""number"": { ""type"": ""string"" },
        ""level_id"": { ""type"": ""integer"" }, ""view_family_type_id"": { ""type"": ""integer"" }, ""plan_view_id"": { ""type"": ""integer"" },
        ""source_view_id"": { ""type"": ""integer"" }, ""source_view_key"": { ""type"": ""string"" },
        ""duplicate_option"": { ""type"": ""string"", ""enum"": [""Duplicate"", ""WithDetailing"", ""AsDependent""], ""default"": ""Duplicate"" },
        ""view_id"": { ""type"": ""integer"" }, ""view_key"": { ""type"": ""string"" },
        ""template_view_id"": { ""type"": ""integer"" }, ""title_block_type_id"": { ""type"": ""integer"" },
        ""sheet_id"": { ""type"": ""integer"" }, ""sheet_key"": { ""type"": ""string"" },
        ""schedule_id"": { ""type"": ""integer"" }, ""schedule_key"": { ""type"": ""string"" },
        ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""bottom_offset"": { ""type"": ""number"", ""default"": -1000 }, ""top_offset"": { ""type"": ""number"", ""default"": 3000 },
        ""depth"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 5000 },
        ""elevation_index"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 3, ""default"": 0 },
        ""marker_scale"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 24000, ""default"": 100 }
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
                    "Export verified deliverables from the active document: combined PDF, one-view DWG, configurable " +
                    "IFC, model/view Navisworks NWC, one or more 3D views to FBX, one-view image or one native schedule " +
                    "as delimited text/CSV. Dry-run validates paths, exporters, " +
                    "views and overwrite policy without writing; apply requires confirmation and idempotency, then " +
                    "discovers and re-reads the files actually produced instead of echoing the requested path.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""format"", ""output_path""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""format"": { ""type"": ""string"", ""enum"": [""pdf"", ""dwg"", ""ifc"", ""nwc"", ""fbx"", ""image"", ""schedule_csv""] },
    ""output_path"": { ""type"": ""string"", ""description"": ""Absolute target file with an extension matching format (.pdf/.dwg/.ifc/.nwc/.fbx; an image extension; or .csv/.txt). Image export may create a family of names, all of which are reported."" },
    ""view_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""PDF: one or more printable views/sheets. DWG/image: exactly one. FBX: one or more 3D views. NWC view scope: exactly one."" },
    ""schedule_id"": { ""type"": ""integer"" },
    ""image_pixels"": { ""type"": ""integer"", ""minimum"": 128, ""maximum"": 8192, ""default"": 2048 },
    ""ifc_version"": { ""type"": ""string"", ""enum"": [""Default"", ""IFC2x2"", ""IFC2x3"", ""IFC2x3CV2"", ""IFC2x3BFM"", ""IFC2x3FM"", ""IFCBCA"", ""IFCCOBIE"", ""IFC4"", ""IFC4DTV"", ""IFC4RV""], ""default"": ""Default"" },
    ""ifc_filter_view_id"": { ""type"": ""integer"", ""description"": ""Optional non-template view whose visibility filters the IFC export."" },
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
    ""dry_run"": { ""type"": ""boolean"", ""default"": true }, ""confirmation_token"": { ""type"": ""string"" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_get_dimension_references",
                Command = "horizun_get_dimension_references",
                Description =
                    "Discover DIMENSIONABLE references without guessing faces from element ids: for explicit " +
                    "elements or a reproducible filter, in ONE named view, produce Revit stable references " +
                    "selected semantically - a wall's exterior/interior face (HostObjectUtils, never a pick), " +
                    "its centerline, grids, levels, reference planes, edges, curve endpoints, nearest/farthest " +
                    "planar face from an explicit probe point. Read-only: ComputeReferences geometry, no " +
                    "transaction. Every candidate carries its stable representation, owner identity, reference " +
                    "type, the geometry that justified it, a fingerprint that changes when that geometry moves, " +
                    "and compatible_with_dimension with a STRUCTURED reason when false. Two equivalent " +
                    "candidates are BOTH returned marked ambiguous - choosing one silently is the mistake this " +
                    "tool exists to remove. Results are deterministically ordered and paginated with exact " +
                    "totals; elements that could not be inspected are named in coverage rather than skipped. " +
                    "References into RVT links are refused with a structured reason: they are not proven " +
                    "consumable by dimension creation, and an unproven reference presented as usable is a " +
                    "dimension that fails at apply time.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""view_id""],
  ""properties"": {
    ""view_id"": { ""type"": ""integer"", ""description"": ""REQUIRED. The view the dimension will live in; reference compatibility is view-dependent, so candidates are evaluated against THIS view."" },
    ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": { ""type"": ""integer"" }, ""description"": ""Host-document elements to inspect. Exactly one of element_ids or filter."" },
    ""filter"": { ""type"": ""object"", ""description"": ""Reproducible alternative to explicit ids. Same semantics as horizun_query_model's core filters; matched elements are inspected in ascending element-id order. Over 200 matches is refused, not sampled."", ""properties"": {
      ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
      ""family"": { ""type"": ""string"" }, ""type"": { ""type"": ""string"" },
      ""name"": { ""type"": ""string"" }, ""level"": { ""type"": ""string"" }
    }, ""additionalProperties"": false },
    ""selectors"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""centerline"", ""grid"", ""level"", ""reference_plane"", ""exterior_face"", ""interior_face"", ""nearest_face"", ""farthest_face"", ""edge"", ""endpoint""] }, ""description"": ""Semantic selection. Omit for every selector applicable to each element's class except nearest/farthest_face, which must be asked for by name because they require probe_point. A selector that does not apply to an element produces a structured warning for it, never a guess. An element's 'axis' in the linear sense IS centerline."" },
    ""probe_point"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""REQUIRED when selectors include nearest_face/farthest_face: nearest to WHAT is not a guessable fact. In 'units'."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""max_results"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 100 },
    ""offset"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 },
    ""include_incompatible"": { ""type"": ""boolean"", ""default"": true, ""description"": ""false hides candidates whose compatible_with_dimension is false; totals still count them and the reply says how many were excluded."" }
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
                    "still exists, AreReferencesAvailable and a broken-reference count (references into links are " +
                    "labelled linked and never counted broken - 'not inspected' is not 'gone'), per-segment values " +
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
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"", ""description"": ""Units of move_by."" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 200, ""items"": {
      ""type"": ""object"", ""required"": [""element_id""], ""properties"": {
        ""element_id"": { ""type"": ""integer"", ""description"": ""A Dimension in the active document. The same id twice in one batch is refused."" },
        ""set_type_id"": { ""type"": ""integer"", ""description"": ""A DimensionType of the SAME DimensionStyleType as the dimension's current type."" },
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
            ""value_override"": { ""type"": ""string"" }, ""lock"": { ""type"": ""boolean"" }
          }, ""additionalProperties"": false } },
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
                Name = "horizun_query_planimetry",
                Command = "horizun_query_planimetry",
                Description =
                    "Read the DOCUMENTATION surface of the active model, from the database rather than from a PDF: " +
                    "six explicit modes, because one answer would be enormous. inventory is the census (sheets, " +
                    "views, templates, viewports, schedule placements, title blocks, dimensions, tags, text, " +
                    "detail curves, filled regions, detail components, generic annotations, sections, elevations, " +
                    "drafting views, legends, schedules) and names any total it could NOT compute instead of " +
                    "reporting it as zero. sheets gives number, name, placeholder state, every title-block " +
                    "instance with its type and family, the title-block extent and the sheet outline, placed " +
                    "views, viewports, schedule placements, revisions, guide grid and requested parameters. views " +
                    "gives type, template, scale, discipline, detail level, phase and phase filter, level, crop " +
                    "and annotation crop as geometry, scope box, plan view range, underlay, parent/dependents, " +
                    "filters, and the sheets it is placed on. placements gives viewports AND ScheduleSheetInstances " +
                    "with box outline, label outline, their union, centre, rotation, type, title and detail number " +
                    "in SHEET coordinates. annotations gives dimensions (references available, broken/linked/" +
                    "unreadable reference counts, overrides, segments), tags (targets, orphan state, leader, head " +
                    "position, target categories, host vs linked), text notes (text, width, alignment, position, " +
                    "empty state) and 2D detail, each with a view-plane bounding box. references gives elevation " +
                    "markers, reference callouts and reference viewers with their target view - or an explicit " +
                    "unknown with the reason, NEVER a relation inferred from a name. Read-only: no transaction is " +
                    "opened. Deterministic order, cursor bound to both the arguments and the result set, exact " +
                    "totals whether or not the page was truncated, ids the caller named that matched nothing " +
                    "listed back, and a coverage block that says why an empty answer may not be read as clean.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""enum"": [""inventory"", ""sheets"", ""views"", ""placements"", ""annotations"", ""references""], ""default"": ""inventory"", ""description"": ""Which population to return. Each mode answers about ONE kind of thing; every row carries entity_kind so a list never mixes two without a discriminator."" },
    ""sheet_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" }, ""description"": ""Narrow to these sheets. Ids that matched nothing come back in unmatched_ids."" },
    ""view_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" } },
    ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 2000, ""items"": { ""type"": ""integer"" }, ""description"": ""annotations/references modes: specific ids."" },
    ""categories"": { ""type"": ""array"", ""minItems"": 1, ""items"": { ""type"": ""string"", ""enum"": [""dimensions"", ""tags"", ""text_notes"", ""detail_curves"", ""filled_regions"", ""detail_components"", ""generic_annotations"", ""revision_clouds""] }, ""description"": ""mode=annotations ONLY. Refused in any other mode rather than accepted and ignored."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
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
                    "beyond an explicit tolerance, a placement wholly off the sheet, a viewport or schedule " +
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
            ""operator"": { ""type"": ""string"", ""enum"": [""matches"", ""not_matches"", ""equals"", ""not_equals"", ""in_list"", ""not_in_list"", ""required"", ""not_empty"", ""greater_than"", ""less_than"", ""between"", ""minimum_gap"", ""inside_extent"", ""allowed_type"", ""allowed_template"", ""allowed_scale"", ""required_parameter"", ""forbid_numeric_override"", ""requires_tag""] },
            ""value"": { ""description"": ""Shape follows the operator: a regex string, a scalar, a list, [min,max] for between, a length in the call's units for minimum_gap/inside_extent, or category names (optionally objects with exclude_types/exclude_families/exclude_type_matches/exclude_when_parameter_set) for requires_tag."" }
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
                    "re-read corrections. Nine operations, closed: set_view_template (explicit template " +
                    "ElementId, validated as a compatible ViewTemplate, ViewTemplateId re-read), set_view_scale " +
                    "(explicit 1..24000, refused for views that take no scale), rename_view and rename_sheet " +
                    "(explicit final name/number, duplicates refused before the transaction, both re-read), " +
                    "place_title_block (explicit sheet and title-block FamilySymbol, placeholders and wrong " +
                    "categories refused, symbol activated safely, instance/family/type/sheet re-read, never a " +
                    "second title block), move_viewport and move_schedule (explicit final point in SHEET " +
                    "coordinates, GetBoxCenter/Point re-read within a declared tolerance), " +
                    "clear_element_override (explicit view and element, ONLY that element's override cleared, " +
                    "OverrideGraphicSettings re-read as defaults, category and template overrides proven " +
                    "untouched), set_crop (rectangular, view-plane coordinates with declared units, crop " +
                    "active/visible/geometry re-read; a non-rectangular shape is refused by capability). EVERY " +
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
        ""operation"": { ""type"": ""string"", ""enum"": [""set_view_template"", ""set_view_scale"", ""rename_view"", ""rename_sheet"", ""place_title_block"", ""move_viewport"", ""move_schedule"", ""clear_element_override"", ""set_crop""] },
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
        ""view_id"": { ""type"": ""integer"", ""description"": ""set_view_template / set_view_scale / rename_view / set_crop: the view to change. clear_element_override: the view whose element override is cleared."" },
        ""template_id"": { ""type"": ""integer"", ""description"": ""set_view_template: the ViewTemplate's ElementId. Never resolved from a name."" },
        ""scale"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 24000 },
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
          ""loop"": { ""type"": ""array"", ""description"": ""NOT COVERED in this phase: a non-rectangular crop is refused by capability, with nothing written."" }
        }, ""additionalProperties"": false, ""description"": ""set_crop: the rectangular crop in VIEW-PLANE coordinates (x along RightDirection, y along UpDirection from the view origin), in the call's units."" }
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
  ""type"": ""object"", ""required"": [""target_document"", ""sheet_id"", ""items""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""sheet_id"": { ""type"": ""integer"" },
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
                    "Read-only production planner for two operations. auto_tags takes explicit elements and " +
                    "chooses deterministic collision-aware tag-head points in one view, skips targets already " +
                    "tagged by default, names incomplete bounding-box coverage and returns a complete " +
                    "horizun_annotate dry-run request. intent_dimension obtains semantic stable references from " +
                    "horizun_get_dimension_references, requires exactly one compatible unambiguous reference per " +
                    "target, resolves horizontal/vertical axis and signed offset deterministically, orders the " +
                    "chain and returns a complete horizun_annotate dry-run request. This command NEVER writes: " +
                    "horizun_annotate remains the single provisional-create, confirmation and host-verification path.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""operation"", ""view_id"", ""element_ids""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""auto_tags"", ""intent_dimension""] },
    ""view_id"": { ""type"": ""integer"" },
    ""element_ids"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": { ""type"": ""integer"" }, ""description"": ""auto_tags: up to 500. intent_dimension: 2..32; duplicates are refused."" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""tag_type_id"": { ""type"": ""integer"", ""description"": ""auto_tags: explicit type forwarded to horizun_annotate, which proves validity and verifies the committed type."" },
    ""tag_mode"": { ""type"": ""string"", ""enum"": [""by_category"", ""multi_category"", ""material""], ""default"": ""by_category"" },
    ""orientation"": { ""type"": ""string"", ""enum"": [""horizontal"", ""vertical""], ""default"": ""horizontal"" },
    ""add_leader"": { ""type"": ""boolean"", ""default"": true },
    ""skip_existing"": { ""type"": ""boolean"", ""default"": true },
    ""clearance"": { ""type"": ""number"", ""minimum"": 0, ""default"": 10, ""description"": ""auto_tags: search step and annotation clearance in units."" },
    ""selector"": { ""type"": ""string"", ""enum"": [""centerline"", ""grid"", ""level"", ""reference_plane"", ""endpoint"", ""face"", ""nearest_face"", ""farthest_face""], ""default"": ""centerline"" },
    ""probe_point"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""Required by nearest_face/farthest_face; forwarded to reference discovery."" },
    ""axis"": { ""type"": ""string"", ""enum"": [""auto"", ""horizontal"", ""vertical""], ""default"": ""auto"" },
    ""side"": { ""type"": ""string"", ""enum"": [""positive"", ""negative""], ""default"": ""positive"" },
    ""offset"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""default"": 15, ""description"": ""intent_dimension: perpendicular distance from the outermost selected reference, in units."" },
    ""dimension_type_id"": { ""type"": ""integer"" }
  },
  ""allOf"": [
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""auto_tags"" } } }, ""then"": { ""properties"": { ""element_ids"": { ""maxItems"": 500 } } } },
    { ""if"": { ""properties"": { ""operation"": { ""const"": ""intent_dimension"" } } }, ""then"": { ""properties"": { ""element_ids"": { ""minItems"": 2, ""maxItems"": 32 } } } },
    { ""if"": { ""properties"": { ""selector"": { ""enum"": [""nearest_face"", ""farthest_face""] } }, ""required"": [""selector""] }, ""then"": { ""required"": [""probe_point""] } }
  ],
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_manage_revisions",
                Command = "horizun_manage_revisions",
                Description =
                    "Create or update revisions, add them to explicit sheets and create revision clouds in explicit " +
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
                Name = "horizun_detail_2d",
                Command = "horizun_detail_2d",
                Description =
                    "Draw verified 2D detail in one atomic batch: detail lines, arcs (two unambiguous forms), " +
                    "polylines (one action, one key, every segment created and verified together), filled regions " +
                    "and masking regions (IsMasking read from the TYPE, never from its name - a masking type drawn " +
                    "as an ordinary fill is refused unless explicitly allowed, and vice versa), view-based detail " +
                    "components and generic-annotation symbols (activated inside the transaction when needed), and " +
                    "line-style changes over existing curves or over curves created earlier in the SAME batch by " +
                    "key. Loops are validated pure before Revit is asked: closed, non-degenerate, non-self-" +
                    "intersecting, exactly one exterior containing every hole. The dry run CREATES the whole batch " +
                    "provisionally and rolls it back; the token binds views, types, styles, symbols and the " +
                    "normalised geometry, so anything swapped before the apply refuses as stale_plan. Apply commits " +
                    "inside a TransactionGroup, regenerates, verifies every element - class by hierarchy, owner " +
                    "view, style, geometry against the request under a declared tolerance, signatures against the " +
                    "rehearsal, bounding boxes - and rolls the WHOLE batch back on any failed check. Coordinates " +
                    "are view-plane (x along RightDirection, y along UpDirection from the view origin); a non-zero " +
                    "third component is refused, never silently projected. Deleting detail belongs to " +
                    "horizun_delete_verified.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 500, ""items"": {
      ""type"": ""object"", ""required"": [""operation""],
      ""properties"": {
        ""operation"": { ""type"": ""string"", ""enum"": [""create_detail_line"", ""create_detail_arc"", ""create_detail_polyline"", ""create_filled_region"", ""create_masking_region"", ""place_detail_component"", ""place_symbol"", ""set_line_style""] },
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
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""set_line_style"" } } }, ""then"": { ""required"": [""line_style_id""] } }
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
                    "Create text notes, host-element tags and DIMENSIONS - linear (simple and chains of up to 32 " +
                    "references), angular, radial, diameter, arc-length and spot elevation/coordinate - in an " +
                    "atomic batch with total rollback. Dimensions consume Revit stable references " +
                    "(horizun_get_dimension_references produces them semantically) rather than guessing faces " +
                    "from element ids. The dry run does not merely parse: it CREATES the whole batch provisionally " +
                    "inside a transaction and rolls it back, so 'constructible' is Revit's own answer and the " +
                    "reported rollback status is Revit's too. For tags, the materialised explicit OR default type " +
                    "and the pre-existing tag count are bound and re-read, so a changed default or concurrent " +
                    "duplicate makes the plan stale. The confirmation token binds the view, the EFFECTIVE dimension " +
                    "type (the materialised default when none was named), every reference's stable " +
                    "representation, owner and 0.1 mm geometry fingerprint, the line and the measured value - a " +
                    "face moved, a reference deleted, a view renamed or a default type changed between rehearsal " +
                    "and apply refuses as a stale plan. Apply re-creates everything in one TransactionGroup, " +
                    "verifies every dimension in a still-reversible state - real class and shape, owner view, type, " +
                    "curve under a declared tolerance, references in order, segment count, values in internal feet " +
                    "and display units, requested overrides/EQ/lock read back - and rolls the WHOLE batch back if " +
                    "any check fails; expected_value makes the intended measurement itself a postcondition. The " +
                    "closed outcome set is committed_verified, rolled_back, refused, stale_plan and uncertain " +
                    "(uncertain only when Revit does not confirm a rollback). Radial, diameter and arc-length " +
                    "need the API Revit added in 2025: on 2023/2024 they are refused naming " +
                    "RadialDimension.Create/ArcLengthDimension.Create, and no Python fallback is offered because " +
                    "Python calls the same absent class. Spot slope has no creation API in any supported year and " +
                    "is refused the same way, as are references that resolve into RVT links. Dimension.Leader does " +
                    "not exist in the API, so no leader option is published for linear dimensions; spot dimensions " +
                    "take 'leader' at creation. The per-reference fingerprint in this command's plan is NOT the " +
                    "geometry_fingerprint horizun_get_dimension_references returns - the two hash different facts " +
                    "for different jobs, and equality between them means nothing.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" }, ""units"": { ""type"": ""string"", ""enum"": [""mm"", ""m"", ""feet""], ""default"": ""mm"", ""description"": ""Units of every coordinate and of expected_value/expected_tolerance - except that for angular_dimension expected_value/expected_tolerance are DEGREES."" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 1000, ""items"": {
      ""type"": ""object"", ""required"": [""operation"", ""view_id""], ""properties"": {
        ""operation"": { ""type"": ""string"", ""enum"": [""text"", ""tag"", ""dimension"", ""angular_dimension"", ""radial_dimension"", ""diameter_dimension"", ""arc_length_dimension"", ""spot_elevation"", ""spot_coordinate"", ""spot_slope""], ""description"": ""spot_slope is always refused: Revit exposes no creation API for it in 2023-2027. radial/diameter/arc_length require Revit 2025+."" },
        ""view_id"": { ""type"": ""integer"", ""description"": ""Not a template, schedule or sheet; a 3D view only when its orientation is locked. For DIMENSION operations this must be the ACTIVE graphical view: Revit materialises dimension references and values only for a displayed view (measured live), so a dimension aimed at a never-shown view is refused at plan time naming horizun_navigate operation=open_view as the fix. Text and tags have no such requirement."" },
        ""point"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""text/tag: placement. spot_elevation/spot_coordinate: the origin ON the reference."" },
        ""text"": { ""type"": ""string"" }, ""text_type_id"": { ""type"": ""integer"" },
        ""element_id"": { ""type"": ""integer"" }, ""add_leader"": { ""type"": ""boolean"", ""default"": false },
        ""tag_type_id"": { ""type"": ""integer"", ""description"": ""Optional explicit tag type. The dry run proves it is valid for the created tag, the confirmation binds it, and apply verifies the committed type. Omitted: Revit's resolved default type is used."" },
        ""tag_mode"": { ""type"": ""string"", ""enum"": [""by_category"", ""multi_category"", ""material""], ""default"": ""by_category"" },
        ""orientation"": { ""type"": ""string"", ""enum"": [""horizontal"", ""vertical""], ""default"": ""horizontal"" },
        ""line_start"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""line_end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" } },
        ""references"": { ""type"": ""array"", ""minItems"": 2, ""maxItems"": 32, ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""description"": ""Stable representations. Duplicates are refused. dimension: 2..32. angular_dimension: exactly 2. arc_length_dimension: the 2 endpoint references."" },
        ""dimension_type_id"": { ""type"": ""integer"", ""description"": ""dimension and angular_dimension only - the other shapes' creation APIs take no type. Omitted: the document's default type for the shape is resolved, validated and bound into the plan."" },
        ""arc_center"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""angular_dimension/arc_length_dimension: centre of the dimension arc, in the view plane."" },
        ""arc_radius"": { ""type"": ""number"", ""exclusiveMinimum"": 0 },
        ""arc_reference"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""arc_length_dimension: the stable reference of the arc edge being measured."" },
        ""reference"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""radial_dimension/diameter_dimension: the arc edge. spot_elevation/spot_coordinate: the referenced face or edge."" },
        ""leader"": { ""type"": ""boolean"", ""default"": false, ""description"": ""spot_elevation/spot_coordinate only; Revit's own hasLeader creation argument."" },
        ""bend"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""spot leader bend point. Defaulted from the view plane when omitted."" },
        ""end"": { ""type"": ""array"", ""minItems"": 3, ""maxItems"": 3, ""items"": { ""type"": ""number"" }, ""description"": ""spot leader end point. Defaulted when omitted."" },
        ""prefix"": { ""type"": ""string"" }, ""suffix"": { ""type"": ""string"" },
        ""above"": { ""type"": ""string"" }, ""below"": { ""type"": ""string"" },
        ""value_override"": { ""type"": ""string"", ""description"": ""Overrides and lock apply to two-reference dimensions only; chains take them per segment through horizun_edit_dimensions."" },
        ""eq"": { ""type"": ""boolean"", ""description"": ""EQ constraint; chains of 3+ references only."" },
        ""lock"": { ""type"": ""boolean"" },
        ""expected_value"": { ""type"": ""number"", ""description"": ""POSTCONDITION on the measured value, checked at apply in the still-reversible state and again after commit; a miss rolls the WHOLE batch back. In 'units'; DEGREES for angular_dimension; not accepted on spots (they have no Value)."" },
        ""expected_tolerance"": { ""type"": ""number"", ""exclusiveMinimum"": 0, ""description"": ""Tolerance for expected_value. Defaults: 0.1 mm for lengths, 0.01 degrees for angular."" }
      },
      ""allOf"": [
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""text"" } } }, ""then"": { ""required"": [""view_id"", ""point"", ""text"", ""text_type_id""] } },
        { ""if"": { ""properties"": { ""operation"": { ""const"": ""tag"" } } }, ""then"": { ""required"": [""view_id"", ""point"", ""element_id""] } },
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
                    "process-death state without waiting for Revit.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""tool"", ""arguments""],
  ""properties"": {
    ""tool"": { ""type"": ""string"", ""description"": ""An installed Revit-side MCP tool. Host-only tools, horizun_execute_python, horizun_request_python_access and horizun_submit_job are refused."" },
    ""arguments"": { ""type"": ""object"", ""description"": ""The exact typed arguments, including target_document, dry_run/confirmation_token where that tool requires them."" },
    ""retain_until_utc"": { ""type"": ""string"", ""format"": ""date-time"", ""description"": ""Optional durable-retention lease for MCP Tasks. Must be a future UTC instant no more than seven days away; it protects this job record from configured retention but does not extend execution."" }
  }, ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_execute_plan",
                Command = "horizun_execute_plan",
                Description =
                    "Compose up to 100 typed Revit write commands into one ordered, atomic plan. Exact result " +
                    "references such as ${scan.rows.0.element_id} feed rehearsed values into later actions without " +
                    "string coercion. Confirmation is issued only when every action and reference resolves during " +
                    "the dry run; each resolved reference is bound to its exact canonical value. References to " +
                    "values that exist only after creation are currently refused rather than authorised unseen. " +
                    "Apply uses an outer TransactionGroup, so a failure in any action rolls every action back. Session changes, " +
                    "exports and arbitrary Python are intentionally excluded because they are not transaction-reversible.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"", ""required"": [""target_document"", ""actions""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""actions"": { ""type"": ""array"", ""minItems"": 1, ""maxItems"": 100, ""items"": {
      ""type"": ""object"", ""required"": [""key"", ""tool"", ""arguments""], ""properties"": {
        ""key"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""Unique action name used by later ${key.path} references."" },
        ""tool"": { ""type"": ""string"", ""enum"": [
          ""horizun_write_params_verified"", ""horizun_delete_verified"", ""horizun_create_schedule"",
          ""horizun_set_keynote"", ""horizun_family_apply"", ""horizun_bind_shared_param"",
          ""horizun_create_elements"", ""horizun_manage_system_types"", ""horizun_transform_elements"", ""horizun_manage_views"", ""horizun_annotate"",
          ""horizun_split_floor_loops"", ""horizun_split_multilayer_walls"", ""horizun_split_multilayer_slabs"",
          ""horizun_ungroup_and_mark"", ""horizun_regroup_by_param"", ""horizun_copy_slab_elevations"",
          ""horizun_embed_floors_in_toposolid"", ""horizun_grade_toposolid_around_floors"", ""horizun_rectangularize_walls""
        ] },
        ""arguments"": { ""type"": ""object"", ""description"": ""Arguments for the typed tool. target_document, dry_run, confirmation_token and idempotency_key are controlled by the plan."" }
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
                    "REQUEST, NEVER SELF-GRANT, persistent arbitrary-Python permission. Opens a visible consent " +
                    "dialog inside Revit and waits for the machine owner to approve or reject it. Approval is " +
                    "indefinite across files, batches and Revit restarts until that Windows user turns Python " +
                    "OFF from the ribbon or the administrator -Disable command. The MCP caller cannot press the " +
                    "button, cannot bypass the acknowledgement and cannot disable the owner's refusal. Do not " +
                    "call this when nobody is at the Revit keyboard.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""reason"": { ""type"": ""string"", ""maxLength"": 500,
      ""description"": ""Optional human-readable reason shown as UNVERIFIED caller text in the Revit consent dialog. It never grants permission."" }
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
                    "target_document is REQUIRED and is matched against the ACTIVE document - this command will " +
                    "not switch documents for you, and a script that needs no document cannot run here. " +
                    "run_async=true returns a job_id immediately for work longer than the request timeout. " +
                    "Every execution requires a durable idempotency_key; a preflight executes nothing and " +
                    "needs none. " +
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
                                "REQUIRED for every execution; not needed (and not claimed) for preflight=true, " +
                                "which executes nothing. Claimed durably before Python runs: the same key " +
                                "with the identical operation replays its recorded answer without executing, a " +
                                "different operation under that key is refused, and a claimed operation with no " +
                                "terminal record after a crash is reported in_doubt instead of repeated."
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
                                "because an async caller never sees a reply. REQUIRES idempotency_key."
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
  ""required"": [""target_document_title""],
  ""properties"": {
    ""target_document_title"": { ""type"": ""string"",
      ""description"": ""Title of the document you believe is active. The scan ABORTS if the active document differs. Required because two Revit hosts run side by side (2025 on one port, 2026 on another) and a scan of the wrong model is a clean bill of health for a file nobody looked at. '.rvt' is optional."" },
    ""top"": { ""type"": ""integer"", ""default"": 50, ""minimum"": 1,
      ""description"": ""Max items returned per bucket. Totals are always exact and independent of this; a shortened list always says truncated=true."" },
    ""sections"": { ""type"": ""array"", ""items"": { ""type"": ""string"",
        ""enum"": [""document"",""categories"",""cleanliness"",""naming"",""documentation"",""project_info"",""health"",""links"",""worksets"",""design_options"",""lines"",""types""] },
      ""description"": ""Which sections to run. Default: all of them. A section you did not ask for is reported as 'not_requested', never as empty."" },
    ""target_parameter"": { ""type"": ""string"",
      ""description"": ""Optional parameter name read off every element type in the 'types' section (e.g. 'Keynote', 'MyOrg_Code'). Reported as absent / empty / value, which are three different things."" }
  }
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
                    "transaction state is a first-class field. A unit STRING on Double/Integer storage is applied with " +
                    "SetValueString, which parses the units inside Revit and never returns the parsed number, so those rows " +
                    "can only be verified against a re-read of themselves: they are counted separately under " +
                    "writes_confirmed_by_parse_read_back_only and never claimed as verified against your value. Use " +
                    "dry_run=true to resolve every target and see what would be written without opening a transaction.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""writes""],
  ""properties"": {
    ""writes"": {
      ""type"": ""array"", ""minItems"": 1,
      ""description"": ""The batch. Each entry names ONE parameter on ONE target."",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""parameter"", ""value""],
        ""properties"": {
          ""target_id"": { ""type"": ""integer"", ""description"": ""Element id OR type id. Omit (or set target='project_info') to write a Project Information field. Writing a type id affects every instance of that type."" },
          ""target"": { ""type"": ""string"", ""enum"": [""project_info""], ""description"": ""Write to the document's Project Information element instead of an id."" },
          ""parameter"": { ""type"": ""string"", ""description"": ""A BuiltInParameter name (e.g. KEYNOTE_PARAM), a shared/project parameter GUID, or a parameter name as it reads in the UI. A name matching more than one parameter is an error, not a guess."" },
          ""value"": { ""description"": ""String | number | boolean | null. Coerced to the parameter's storage type; a value that cannot be coerced is an error naming that storage type, never a silent skip. For Double/Integer storage, a STRING value is applied with SetValueString (unit-aware, e.g. '3000 mm'); a NUMBER is applied raw, in Revit internal units (feet)."" }
        }
      }
    },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: write params"",
                            ""description"": ""The label of the single undo step this batch becomes."" },
    ""on_failure"": { ""type"": ""string"", ""enum"": [""atomic"", ""best_effort""], ""default"": ""atomic"",
                      ""description"": ""atomic: if ANY write fails, roll the whole batch back â€” nothing partial. best_effort: commit what worked and report the rest."" },
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the document to delete from. It must be the document that is ACTIVE in Revit; this command will not switch documents for you. A delete aimed at whatever window happens to be in front is a delete aimed at whatever turns up."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token returned by the dry run of this exact request. Single-use, expires, and bound to this document and this request - if either changed, execution is refused and nothing is deleted."" },
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
                    "Delete an explicit list of ElementIds, or purge unused elements to a real fixed point, and report " +
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
    ""mode"": { ""type"": ""string"", ""enum"": [""ids"", ""purge_unused""],
                ""description"": ""REQUIRED. ids: delete exactly the ids given. purge_unused: ask Revit for unused elements and delete them, repeating until a pass finds none. Omission is refused; it never selects the broader purge operation."" },
    ""ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
               ""description"": ""Required for mode='ids'. Ids that do not resolve are reported as not_found, never dropped."" },
    ""protect_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
                       ""description"": ""Never delete these, even if a purge pass calls them unused. Use for view templates you are about to assign rather than delete."" },
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
                    "so audit_ran only ever describes the open. It never syncs to central.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""open"", ""save"", ""save_as"", ""close"", ""inspect""],
                     ""description"": ""inspect: read a file's version off disk without opening it. open/save/save_as/close do what they say."" },
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
    ""on_open_dialog"": { ""type"": ""string"", ""enum"": [""cancel"", ""dismiss""], ""default"": ""cancel"",
                     ""description"": ""open only: how a modal dialog raised WHILE opening is answered unattended. 'cancel' (default) presses Cancel; 'dismiss' presses OK/continue, for READING a model whose open raises a dialog whose only unattended answer is 'acknowledge and continue'. Best effort, recorded in revit_said; scoped to the open call - every other dialog still cancels."" },
    ""save_as_path"": { ""type"": ""string"", ""description"": ""save_as: absolute destination path."" },
    ""compact"": { ""type"": ""boolean"", ""default"": false, ""description"": ""save/save_as: pass Compact to the API. The response reports the byte delta it actually produced."" },
    ""overwrite"": { ""type"": ""boolean"", ""default"": false, ""description"": ""save_as: allow overwriting an existing destination file."" },
    ""max_backups"": { ""type"": ""integer"", ""minimum"": 1, ""description"": ""save_as: cap the .000N backup pile Revit leaves behind."" },
    ""force_workshared"": { ""type"": ""boolean"", ""default"": false,
                            ""description"": ""Required to save/save_as a workshared document, to close one with save_on_close, and in either case when the workshared state cannot be read at all (unknown is not a clearance). This tool never syncs to central; on a central model a save still writes to central."" },
    ""save_on_close"": { ""type"": ""boolean"", ""default"": false, ""description"": ""close: save before closing. Off by default - closing should not be a write you did not ask for."" },
    ""discard_unsaved"": { ""type"": ""boolean"", ""default"": false,
                     ""description"": ""close: REQUIRED to close a document that has unsaved changes without saving them. Close() discards them, returns true, and leaves nothing behind to detect it - the file on disk is untouched and IsModified cannot be asked of a closed document, so an hour of lost edits and an untouched model produce identical responses. Not enough on its own: a dry_run token is required too. Unknown counts as modified."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": false,
                     ""description"": ""close: rehearse. Closes NOTHING and activates nothing, reports is_modified, would_discard_unsaved and the activation that WOULD happen under activate_other, and issues a confirmation_token when the close would discard work."" },
    ""activate_other"": { ""type"": ""boolean"", ""default"": false,
                     ""description"": ""close: Revit's API cannot close the ACTIVE document, so closing the last document of a batch used to need a decoy opened by hand (and a relaunched batch SKIPPED the model that stayed open). With this true, the command activates another open document first - or opens the bridge's own empty anchor project when nothing else qualifies - then closes the target, and REPORTS which document it activated. Off by default because activation changes what the user is looking at; it must be asked for, never a side effect."" },
    ""confirmation_token"": { ""type"": ""string"",
                     ""description"": ""close: the token from a dry_run, required alongside discard_unsaved=true. Single use, expires, and bound to THIS document and THIS request - if either changes it is refused and nothing is closed."" }
  }
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
                Description = @"Pre-delivery audit of the open model: warnings, orphan group types, in-place families, imported (not linked) CAD, views off sheets, unplaced/redundant rooms, links, design options and file weight. Read-only. Every count is the model's, every list states total vs. shown, and any check that could not run is reported as failed rather than skipped silently.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""top"": { ""type"": ""integer"", ""default"": 20, ""minimum"": 1,
               ""description"": ""How many items to list per finding. Totals are always exact regardless of this."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_quantities",
                Command = "horizun_quantities",
                Description = @"Volume takeoff in m3 from all three sources Revit offers â€” the Volume parameter, the real solid geometry, and the material takeoff â€” reported side by side with the disagreement measured. Handlers that report a single volume are picking one silently; we have measured a 75% gap between the parameter and the geometry on the same beam. COVERAGE IS EXPLICIT AND NEVER A ZERO: a read that failed is reported as failed, so each source carries candidates/measured/not_applicable/failed plus known_total_m3 and total_is_complete, and known_total is the sum over MEASURED elements only. Two sources are compared only where BOTH produced a number: 'all_agree' is null when nothing could be compared and false when coverage is partial â€” it is never true unless every candidate was compared and agreed. Totals from different sources cover different element sets, so total_reconciliation is computed over the intersection, not over each source's own sum. A volume of exactly zero is a measurement, not an absence. Pass element_ids or a category. Read-only.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
                       ""description"": ""Elements to measure. Omit and pass 'category' instead to sweep a whole category."" },
    ""category"": { ""type"": ""string"",
                    ""description"": ""BuiltInCategory name, e.g. OST_StructuralFraming, OST_Walls, OST_Floors. Used when element_ids is omitted."" },
    ""detail_level"": { ""type"": ""string"", ""enum"": [""Coarse"", ""Medium"", ""Fine""], ""default"": ""Fine"",
                        ""description"": ""Geometry detail level. Fine is the default here on purpose: Coarse geometry under-reports, and this number gets billed."" },
    ""tolerance_pct"": { ""type"": ""number"", ""default"": 1.0,
                         ""description"": ""Relative disagreement above which sources are flagged as not agreeing."" },
    ""top"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1,
                ""description"": ""Max element rows returned. Totals and coverage are EXACT and independent of this; a shortened list sets truncated=true and rows_matching says how many there were."" },
    ""code_parameter"": { ""type"": ""string"", ""description"": ""Parameter carrying each element's budget/classification code (instance first, then type). Supplied per call - no organisation's parameter is compiled in. Adds 'code' per row and a by_code rollup whose sums state how many elements they cover."" },
    ""only_disagreements"": { ""type"": ""boolean"", ""default"": false,
                              ""description"": ""List only the elements whose sources disagree. Totals still cover everything."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_clash",
                Command = "horizun_clash",
                Description = @"Clash detection between two category sets, across the host model AND loaded Revit links (link geometry is transformed into host coordinates). Every clash names the source model of both elements. COVERAGE IS ACCOUNTED FOR PER ELEMENT AND PER PAIR: every element that never entered the check is counted with its reason (no bounding box, a read that threw, a collector that failed), so candidate counts are not survivor counts; a pair with no usable solid and a pair whose boolean threw are both reported as unresolved rather than clean; a clash whose intersection volume is short because some booleans failed says so; and one physical pair is reported once even when the two category sets overlap. If any of that happened, or links were excluded or unloaded, the result is PARTIAL rather than clean â€” a zero from this tool means zero. Read-only.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""categories_a"", ""categories_b""],
  ""properties"": {
    ""categories_a"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1,
                        ""description"": ""BuiltInCategory names, e.g. [\""OST_StructuralFraming\""]."" },
    ""categories_b"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""minItems"": 1 },
    ""include_links"": { ""type"": ""boolean"", ""default"": true,
                         ""description"": ""Include loaded Revit links. Turning this off on a model that HAS links makes the result partial, and it will be labelled as such."" },
    ""tolerance_mm"": { ""type"": ""number"", ""default"": 0.0,
                        ""description"": ""Intersections whose overlap is under this are ignored. 0 = report any real overlap."" },
    ""max_results"": { ""type"": ""integer"", ""default"": 200, ""minimum"": 1, ""maximum"": 2000 }
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
  ""required"": [""element_ids"", ""keynote""],
  ""properties"": {
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""minItems"": 1,
                       ""description"": ""Elements to code. If the Keynote lives on their type, the type is what gets written."" },
    ""keynote"": { ""type"": ""string"", ""description"": ""The keynote code. Empty string clears it."" },
    ""scope"": { ""type"": ""string"", ""enum"": [""auto"", ""instance"", ""type""], ""default"": ""auto"",
                 ""description"": ""auto: write wherever the parameter lives (type if that is the only place). instance: only write an instance-level Keynote; fail rather than spill onto siblings. type: always write the type."" },
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the document to delete from. It must be the document that is ACTIVE in Revit; this command will not switch documents for you. A delete aimed at whatever window happens to be in front is a delete aimed at whatever turns up."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token returned by the dry run of this exact request. Single-use, expires, and bound to this document and this request - if either changed, execution is refused and nothing is deleted."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Resolve targets and report the blast radius without writing."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_family_apply",
                Command = "horizun_family_apply",
                Description = @"Homologate the ACTIVE family document (.rfa) in ONE transaction: collapse surplus types down to one and rename it to family_name, add missing shared parameters from an SPF (respecting instance/type and the parameter group), clear formulas on the parameters about to be written (a formula-driven parameter refuses a value), set values, remove named parameters (the caller's parameter spec's 'NA' entries), and strip vendor junk under the conservative rule: String storage, no formula, matches a junk pattern, not excluded, not kept, not in the caller-supplied protected prefix (protected_prefix). TWO checks run. A PARAMETER SCHEMA CHECK IS ENFORCED, NOT LOGGED (it is NOT a geometry check and is no longer called one): the count of Double parameters and the presence of IsCustom are captured before, re-enumerated fresh after the writes, and if either changed â€” or if either census could not be read completely â€” the WHOLE transaction is rolled back and the family is left untouched. Every reported field is a fresh read of the family document after the commit: params_set reports value_written vs value_read_back and a mismatch is a failure, type_name_after comes from fm.CurrentType.Name, params_added/params_removed are counted by re-reading fm.Parameters and never by counting calls that did not throw (FamilyManager.Set and RemoveParameter return void â€” there is not even a bool to check). Never opens a file: rfa_path is a guard that must match the active document, because opening a 2025 .rfa in Revit 2026 upgrades it irreversibly. SEPARATELY, the SHAPE is measured: bounding box, solid volume, surface area, solid count and connector positions of the ACTIVE family type are captured before and compared after, and reported in geometry_check as unchanged / unchanged_where_measured / unproven (zero dimensions compared - a verdict that measured nothing does not wear the word of a clean pass) / changed. Only the active type is measured, because activating another type to measure it would itself modify the file - the others are listed as not verified rather than assumed intact. Idempotent: a second run reports nothing to do, not an error. Use dry_run=true to see the plan without a transaction.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""confirmation_token"": { ""type"": ""string"", ""description"": ""REQUIRED when dry_run=false. The token the dry run of this exact request returned. Single-use, expiring, bound to this document and this request - if either changed, execution is refused and nothing is written."" },
    ""rfa_path"": { ""type"": ""string"", ""description"": ""GUARD, not an instruction to open anything. If given, the run aborts unless it resolves to the ACTIVE family document's PathName. This handler never calls OpenDocumentFile: opening a 2025 .rfa from Revit 2026 upgrades the file irreversibly and breaks the family catalog. Open the family yourself (or via horizun_document_session) in the right Revit, then pass its path here to prove this is the one."" },
    ""expected_revit_version"": { ""type"": ""string"", ""description"": ""GUARD, e.g. '2025'. Aborts unless the running Revit reports this VersionNumber. The families are 2025; saving one from 2026 upgrades it with no way back."" },
    ""family_name"": { ""type"": ""string"", ""description"": ""The canonical Family Name (no .rfa). Given, the family is collapsed to exactly ONE type named this. Omitted, no type is created, deleted or renamed."" },
    ""keep_type"": { ""type"": ""string"", ""description"": ""Which existing type survives the collapse. Default: the one already named family_name, else the first. Every other type is deleted, so name it when the family carries real different sizes â€” those must be split into one family per size BEFORE this runs, not collapsed here."" },
    ""collapse_types"": { ""type"": ""boolean"", ""default"": true, ""description"": ""With family_name set: delete the surplus types. false renames the surviving/current type only and leaves the others alone."" },
    ""spf_path"": { ""type"": ""string"", ""description"": ""Your shared parameter file (the .txt Revit exports for a Shared Parameter File) to take add_shared_params from. The app's SharedParametersFilename is restored afterwards."" },
    ""add_shared_params"": {
      ""type"": ""array"",
      ""description"": ""Shared parameters to add if missing. A parameter already present is left exactly as it is (idempotence), never re-added."",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""name""],
        ""properties"": {
          ""name"": { ""type"": ""string"", ""description"": ""Definition name as it reads in the SPF."" },
          ""instance"": { ""type"": ""boolean"", ""default"": false, ""description"": ""true = instance parameter, carrying its own value per placed element. false (default) = type parameter, one value shared by every instance of the type. Pick instance only for values that must vary per occurrence."" },
          ""group"": { ""type"": ""string"", ""default"": ""PG_DATA"", ""description"": ""Parameter group: 'PG_DATA', 'PG_IDENTITY_DATA', a GroupTypeId name ('Data', 'IdentityData'), or a full group ForgeTypeId. A group that cannot be resolved is an ERROR for that row â€” never a silent fallback to Data, which would file the parameter in the wrong place and report success."" }
        }
      }
    },
    ""values"": { ""type"": ""object"", ""description"": ""{ parameter_name: value }. Set on the surviving type. String | number | boolean | null. A number on Double/Integer storage is raw Revit internal units; a STRING on Double/Integer goes through SetValueString (unit-aware) and can only be confirmed against a re-read of itself â€” those rows are reported separately and never claimed as verified against your value."" },
    ""clear_formulas"": { ""type"": ""boolean"", ""default"": true, ""description"": ""SetFormula(p, null) on a parameter in 'values' that is driven by a formula, BEFORE writing it. Imported families arrive with Description/Manufacturer/Material governed by a vendor formula, and Revit refuses a value on those ('Cannot set the value of a parameter determined by a formula'). false = such a row is refused and reported, never silently skipped."" },
    ""clear_formulas_on"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Extra parameter names to clear the formula of even though no value is written to them."" },
    ""remove_params"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Parameters to delete by name â€” typically the caller's parameter spec's 'NA' entries. A name that is not in the family is 'nothing to do', not an error (idempotence). Revit refuses to remove a referenced parameter: that is reported as skipped with Revit's reason, never counted as removed."" },
    ""junk_rules"": {
      ""type"": ""object"",
      ""description"": ""Vendor metadata stripping (BIMobject/manufacturer families arrive with dozens â€” a Caleffi valve had 70). Off unless enabled."",
      ""properties"": {
        ""enabled"": { ""type"": ""boolean"", ""default"": false },
        ""patterns"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""REQUIRED when enabled. Lowercase substrings that mark a parameter as junk. There is NO default list: what counts as vendor junk depends on whose families these are, and a built-in list would delete parameters by rules you never read. This command owns HOW to strip safely - match, veto, protect, one transaction, verify by re-reading, roll back if the parameter census moved; WHAT to strip is yours to state."" },
        ""exclude"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Optional. Lowercase substrings that VETO removal even on a junk match. Empty means veto nothing. IsCustom is refused regardless of this list, because it moves geometry - that is a fact about Revit, not a policy."" },
        ""keep"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Optional. Exact names (lowercased) never removed. Empty means keep nothing by name."" }
      }
    },
    ""protected_prefix"": { ""type"": ""string"", ""description"": ""Optional caller-supplied prefix. Parameters whose name starts with it are counted in the census (protected_prefix_count_before/after) and are never removed by the junk sweep â€” remove_params can still delete one by exact name. Omitted: they are not tracked at all, and the counts are reported as null, which is NOT the same as zero."" },
    ""save"": { ""type"": ""boolean"", ""default"": false, ""description"": ""doc.Save() in place after a successful commit. Never SaveAs, never a rename, never a delete of the original â€” an earlier scripted approach lost a family that way. saved_path is reported only after the file is found on disk, re-read from disk as a real family file, AND PROVEN TO HAVE CHANGED: size, timestamp and a SHA-256 of the contents are taken BEFORE the save and compared after, because a valid file that was already there is not evidence that Save wrote anything. A save that leaves the bytes identical is reported as saved=false with both hashes, since the commit already changed the family in memory and the file on disk is now behind it. The response also states whether a recoverable backup was left beside it. A rolled-back run never saves."" },
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the document to delete from. It must be the document that is ACTIVE in Revit; this command will not switch documents for you. A delete aimed at whatever window happens to be in front is a delete aimed at whatever turns up."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token returned by the dry run of this exact request. Single-use, expires, and bound to this document and this request - if either changed, execution is refused and nothing is deleted."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true, ""description"": ""Resolve everything and report the plan and the before-census. Opens no transaction and saves nothing."" },
    ""transaction_name"": { ""type"": ""string"", ""default"": ""Horizun: homologar familia"", ""description"": ""The label of the single undo step this becomes."" }
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
                                     ""description"": ""Call SetAllowVaryBetweenGroups(true) on the InternalDefinition. Without it, writing different values to instances inside Model Groups raises the DESAGRUPAR modal, which hangs the bridge. Reported as read back from the InternalDefinition, never as assumed."" },
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
                Name = "horizun_split_multilayer_walls",
                Command = "horizun_split_multilayer_walls",
                Description = @"Split compound walls into ONE WALL PER MATERIAL LAYER: each layer becomes its own wall at its own offset, doors and windows are re-hosted on the structural layer, and the finish walls are joined to it so Revit cuts their openings. Stacked walls are handled through their members - select any member and the whole stack is processed once. CURVED WALLS ARE REFUSED AND REPORTED, NEVER SPLIT: every offset here is built as a straight Line between the centreline's endpoints, which on an arc wall is the CHORD, so splitting one would move the wall and report success. Refusing is the correct answer until an arc-aware offset exists. A single-layer wall is reported as skipped with its reason, and an id that resolves to nothing or to a non-wall comes back in scope.missing_ids / scope.wrong_type_ids. Revit's 'walls overlap' warning is suppressed because layer walls overlap BY CONSTRUCTION; every other warning reaches you. Originals are UNPINNED before deletion - a pinned delete raises a warning nobody is there to dismiss, and an unanswered warning is a modal that holds Revit's UI thread. would_create is reported as null (not guessed) when a stacked wall is in scope, because its layer count is only knowable per member at apply time. VERIFIED AFTER THE COMMIT: created_present re-reads each new id and confirms it is a Wall, deleted_gone confirms each original is absent. dry_run defaults to TRUE.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""target_document""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the model to change. It must be the document ACTIVE in Revit; this never switches documents for you."" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
      ""description"": ""Exactly these walls. A stacked-wall MEMBER resolves to its parent stack, and the stack is processed once however many of its members you name. Omit to use view_id."" },
    ""view_id"": { ""type"": ""integer"",
      ""description"": ""Every eligible wall VISIBLE IN THIS VIEW. Used only when element_ids is omitted. Omit both and the whole model is in scope."" },
    ""origin_group_param"": { ""type"": ""string"",
      ""description"": ""OPTIONAL, and never assumed: the name of a text INSTANCE parameter whose value is carried from each original wall onto every layer wall it produces (the pyRevit button this was ported from hard-coded one organisation's '_GrupoOrigen'). Omit it and nothing is copied - the count comes back as null, meaning NOT TRACKED, never as 0."" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true,
      ""description"": ""DEFAULTS TO TRUE. A dry run opens no transaction and writes nothing: it returns which walls are eligible, how many layers each has, which were refused and why, plus a single-use confirmation_token."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token this exact request's dry run returned. Single-use, expiring, bound to this document and this scope."" }
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
    ""limit"": { ""type"": ""integer"", ""default"": 5, ""description"": ""How many recent jobs to describe."" },
    ""checkpoints"": { ""type"": ""integer"", ""default"": 10, ""description"": ""How many of the LAST checkpoints of each job to include."" }
  },
  ""additionalProperties"": false
}")
            },
            new CommandContract
            {
                Name = "horizun_catalog_lookup",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "Resolve whether a hierarchical code is a LEAF of a catalog you pass at call time. Generic: " +
                    "the catalog is a file (catalog_path), no codes are baked in. A code is a LEAF iff it EXISTS in " +
                    "the catalog AND no OTHER code is its strict descendant (no other code begins with code + the " +
                    "hierarchy separator). HONESTY: a code that is NOT in the catalog returns is_leaf=null (unknown) " +
                    "â€” never false; 'exists' is a separate field, so 'absent' and 'not a leaf' are never conflated. " +
                    "The CSV is read simply: one code per line, the first comma-separated field of each non-blank " +
                    "line (trimmed, surrounding double quotes stripped); every non-blank line is a code, no header " +
                    "is assumed or skipped. If 'separator' is given, a descendant is other=code+separator+â€¦; if it " +
                    "is omitted the code is opaque and any of - . _ / or space counts as the separator. PROVENANCE: " +
                    "the response carries a sha256 of the catalog bytes, the parsed row_count and the distinct code " +
                    "count, so the verdict is auditable. Read-only; touches no Revit model.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""catalog_path"", ""code""],
  ""properties"": {
    ""catalog_path"": { ""type"": ""string"",
      ""description"": ""Absolute path to the catalog CSV. Codes are read from the FIRST comma-separated field of every non-blank line (a plain one-code-per-line file works too). A missing or unreadable file is an ERROR, not an empty catalog â€” the tool never answers off a file it could not read."" },
    ""code"": { ""type"": ""string"",
      ""description"": ""The code to test. If it is not present in the catalog the answer is exists=false, is_leaf=null (unknown) â€” never is_leaf=false."" },
    ""separator"": { ""type"": ""string"",
      ""description"": ""The hierarchy segment separator, e.g. '-' or '.'. A code X is a parent of Y when Y begins with X+separator. If omitted, the code is treated as opaque and any of - . _ / or space is accepted as the separator; state it explicitly when your codes use exactly one."" }
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
                    "sheet, all styles, tables and formatting). HONESTY: the original is BACKED UP first (file_path + " +
                    "'.horizunbak'); a file that is not a valid .xlsx (a zip carrying xl/workbook.xml) is REFUSED, never " +
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
    ""file_path"": { ""type"": ""string"",
      ""description"": ""Absolute path to an existing .xlsx. It is backed up to <file_path>.horizunbak before any write. A file that is not a valid .xlsx package is an ERROR, never overwritten."" },
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
                Name = "horizun_target",
                Command = null,           // host-resident: answered in the server, never forwarded to Revit
                Description =
                    "Which Revit these tools are talking to, and how to change it - answered WITHOUT touching Revit. " +
                    "With no arguments it reports every Revit that has published a bridge (year, pid, whether that " +
                    "process is still running, add-in version) and which one is selected and why. Pass 'year' to send " +
                    "every later call in this session to that Revit, or 'auto' to go back to the most recently started " +
                    "one. This matters because two Revit versions open at once is normal - a model saved by one year " +
                    "does not open in another - and the expensive failure is not a dead bridge, it is a healthy one " +
                    "attached to the wrong instance: a read answers about the wrong model, a WRITE lands in it. " +
                    "HONESTY: an add-in too old to publish its command list reports command_count=null (unknown), " +
                    "never 0; a year that has published nothing is REFUSED and the current target is left unchanged.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""pid"": { ""type"": ""integer"",
      ""description"": ""Process id of a SPECIFIC Revit instance to direct calls to. Use this when two instances of the same year are running - a year no longer names one process. Wins over year."" },
    ""year"": { ""type"": ""string"",
      ""description"": ""Four-digit Revit year to direct all later calls to, e.g. '2026'. Pass 'auto' to clear the choice and go back to the most recently started running Revit. Omit to only report, changing nothing."" }
  },
  ""additionalProperties"": false
}")
            }
        });

        /// <summary>
        /// Attach behavioural metadata and the argument every mutating execution shares.
        /// Keeping this here means a newly-added mutating command cannot forget to tell
        /// either the server or the add-in about idempotency.
        /// </summary>
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
                "horizun_create_family",
                "horizun_manage_system_types",
                "horizun_transform_elements",
                "horizun_manage_views",
                "horizun_export",
                "horizun_power_bi_push",
                "horizun_annotate",
                "horizun_edit_dimensions",
                "horizun_detail_2d",
                "horizun_fix_planimetry",
                "horizun_pack_sheets",
                "horizun_manage_revisions",
                "horizun_execute_plan",
                "horizun_set_keynote", "horizun_family_apply", "horizun_bind_shared_param",
                "horizun_split_floor_loops", "horizun_split_multilayer_walls",
                "horizun_split_multilayer_slabs", "horizun_ungroup_and_mark",
                "horizun_regroup_by_param", "horizun_copy_slab_elevations",
                "horizun_embed_floors_in_toposolid", "horizun_grade_toposolid_around_floors",
                "horizun_rectangularize_walls"
            };
            // Writes something outside the model that is still there after the call: a PNG,
            // a workbook. full_write is the rung that authorizes these.
            var external = new HashSet<string>(StringComparer.Ordinal)
            {
                "horizun_capture_view", "horizun_excel_write_rows"
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
                "horizun_export", "horizun_create_family", "horizun_power_bi_push"
            };

            // MCP's openWorldHint. Effect already covers ExternalSideEffect and
            // DocumentSession; these are the ones that reach outside the model while being
            // classified by Effect as ordinary model writes.
            var openWorld = new HashSet<string>(StringComparer.Ordinal)
            {
                "horizun_open_document", "horizun_export", "horizun_create_family",
                "horizun_power_bi_push", "horizun_execute_python", "horizun_catalog_lookup"
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
                if (hostState.Contains(n))
                    throw new InvalidOperationException(
                        "'" + n + "' is in BOTH external and hostState. One of them decides its effect " +
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
                else if (hostState.Contains(c.Name)) c.Effect = ToolEffect.HostState;
                else c.Effect = ToolEffect.ReadOnly;

                c.Destructive = destructive.Contains(c.Name);
                // HostState is listed here so the MCP annotations do not change when the
                // classification split: navigate and target reported openWorldHint=true
                // under their old effect and still describe something outside this process.
                c.OpenWorld = openWorld.Contains(c.Name) ||
                              c.Effect == ToolEffect.ExternalSideEffect ||
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
}
