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
    /// <summary>One command, exactly as both halves must understand it.</summary>
    public sealed class CommandContract
    {
        /// <summary>The MCP tool name the client sees.</summary>
        public string Name;

        /// <summary>The plugin command it forwards to. Null for a host-resident tool.</summary>
        public string Command;

        public string Description;
        public JObject InputSchema;
    }

    public static class Contract
    {
        /// <summary>
        /// The shape of the exchange between server and add-in. Bumped when that shape
        /// changes, not when a tool is added - a new tool is caught by the hash.
        /// </summary>
        public const int ProtocolVersion = 1;

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

        /// <summary>
        /// How much free-flowing TEXT a command may return - what a script printed, or a
        /// value that could only be rendered as a string. Unlike the reply limit this one
        /// truncates rather than refuses, because these are for a human to read and the
        /// first quarter-megabyte of them answers the question. The truncation is always
        /// declared in the reply, with the full length, so nobody mistakes the part for
        /// the whole.
        /// </summary>
        public const int MaxScriptTextChars = 256 * 1024;

        public static readonly List<CommandContract> All = new List<CommandContract>{
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
    ""audit"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Run Revit's audit while opening. Slow, and it can modify the model to repair it."" }
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
                    "MEASURE it: the count of worksets owned by this user is read before and after and both are " +
                    "reported, so a partial relinquish cannot pass as a complete one. Refuses a document that is " +
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
                Name = "horizun_execute_python",
                Command = "horizun_execute_python",
                Description =
                    "Run Python directly against the Revit API on the UI thread. doc/uidoc/uiapp/app are injected. " +
                    "Return data by assigning __output__ or with print(). The standard library is available " +
                    "(json, re, csv, datetime, math). " +
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
                    "run_async=true returns a job_id immediately for work longer than the request timeout and " +
                    "additionally REQUIRES idempotency_key. " +
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
                            ["description"] = "Python source to execute. Assign __output__ for the return value; print() is captured."
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
                                "REQUIRED when run_async is true, and REFUSED otherwise. Any string you can " +
                                "reproduce on a retry. The reply carrying your job_id is the message that gets " +
                                "lost, and a caller doing the correct thing after a timeout - sending it again - " +
                                "would otherwise queue the script a SECOND time. Bound to the Revit process, the " +
                                "target document, a SHA-256 of the code and every other argument: the same key " +
                                "with the same request returns the original job_id and queues nothing; the same " +
                                "key with a different request is refused rather than silently deduplicated. The " +
                                "ledger is in memory - across a Revit restart the key is forgotten."
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
                    ["required"] = new JArray { "code", "target_document" },
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
    ""dry_run"": { ""type"": ""boolean"", ""default"": false,
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
                    "transaction is an error, not a count. dry_run defaults to TRUE in purge mode; this is destructive and it " +
                    "is a client's model.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""enum"": [""ids"", ""purge_unused""],
                ""description"": ""ids: delete exactly the ids given. purge_unused: ask Revit for unused elements and delete them, repeating until a pass finds none."" },
    ""ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
               ""description"": ""Required for mode='ids'. Ids that do not resolve are reported as not_found, never dropped."" },
    ""protect_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" },
                       ""description"": ""Never delete these, even if a purge pass calls them unused. Use for view templates you are about to assign rather than delete."" },
    ""target_document"": { ""type"": ""string"",
      ""description"": ""REQUIRED. Title or full path of the document to delete from. It must be the document that is ACTIVE in Revit; this command will not switch documents for you. A delete aimed at whatever window happens to be in front is a delete aimed at whatever turns up."" },
    ""confirmation_token"": { ""type"": ""string"",
      ""description"": ""REQUIRED when dry_run=false. The token returned by the dry run of this exact request. Single-use, expires, and bound to this document and this request - if either changed, execution is refused and nothing is deleted."" },
    ""dry_run"": { ""type"": ""boolean"",
                   ""description"": ""Default TRUE for purge_unused, FALSE for ids. Opens a transaction, asks Revit what would die (the real dependent closure, cascades included), then ROLLS BACK on purpose."" },
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
                    "reports the IsModified it measured before closing. " +
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
                     ""description"": ""open only: open every workset. Needed when something downstream MEASURES worksets, because a closed workset is indistinguishable from an empty one. IT CAN ALSO KILL REVIT - measured: 2 of 24 ACC models took Revit 2025.4 down with an access violation inside SelectedPartitionsForEdit on open, and both opened fine with this left false. Off by default, and the first thing to drop when a specific model dies on open."" },
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
                     ""description"": ""close: rehearse. Closes NOTHING, reports is_modified and would_discard_unsaved, and issues a confirmation_token when the close would discard work."" },
    ""confirmation_token"": { ""type"": ""string"",
                     ""description"": ""close: the token from a dry_run, required alongside discard_unsaved=true. Single use, expires, and bound to THIS document and THIS request - if either changes it is refused and nothing is closed."" }
  }
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
    ""dry_run"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Resolve targets and report the blast radius without writing."" }
  }
}")
            },
            new CommandContract
            {
                Name = "horizun_family_apply",
                Command = "horizun_family_apply",
                Description = @"Homologate the ACTIVE family document (.rfa) in ONE transaction: collapse surplus types down to one and rename it to family_name, add missing shared parameters from an SPF (respecting instance/type and the parameter group), clear formulas on the parameters about to be written (a formula-driven parameter refuses a value), set values, remove named parameters (the caller's parameter spec's 'NA' entries), and strip vendor junk under the conservative rule: String storage, no formula, matches a junk pattern, not excluded, not kept, not in the caller-supplied protected prefix (protected_prefix). TWO checks run. A PARAMETER SCHEMA CHECK IS ENFORCED, NOT LOGGED (it is NOT a geometry check and is no longer called one): the count of Double parameters and the presence of IsCustom are captured before, re-enumerated fresh after the writes, and if either changed â€” or if either census could not be read completely â€” the WHOLE transaction is rolled back and the family is left untouched. Every reported field is a fresh read of the family document after the commit: params_set reports value_written vs value_read_back and a mismatch is a failure, type_name_after comes from fm.CurrentType.Name, params_added/params_removed are counted by re-reading fm.Parameters and never by counting calls that did not throw (FamilyManager.Set and RemoveParameter return void â€” there is not even a bool to check). Never opens a file: rfa_path is a guard that must match the active document, because opening a 2025 .rfa in Revit 2026 upgrades it irreversibly. SEPARATELY, the SHAPE is measured: bounding box, solid volume, surface area, solid count and connector positions of the ACTIVE family type are captured before and compared after, and reported in geometry_check as unchanged / unchanged_where_measured / changed. Only the active type is measured, because activating another type to measure it would itself modify the file - the others are listed as not verified rather than assumed intact. Idempotent: a second run reports nothing to do, not an error. Use dry_run=true to see the plan without a transaction.",
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
    ""dry_run"": { ""type"": ""boolean"", ""default"": false, ""description"": ""Resolve everything and report the plan and the before-census. Opens no transaction and saves nothing."" },
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
                    "inside it. Writes to disk; touches no Revit model.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""file_path"", ""rows""],
  ""properties"": {
    ""file_path"": { ""type"": ""string"",
      ""description"": ""Absolute path to an existing .xlsx. It is backed up to <file_path>.horizunbak before any write. A file that is not a valid .xlsx package is an ERROR, never overwritten."" },
    ""sheet"": { ""type"": ""string"",
      ""description"": ""Worksheet name to append to (case-insensitive). Omit to use the FIRST sheet in workbook order. A name matching no sheet is an error; the response lists the sheets that do exist."" },
    ""rows"": {
      ""type"": ""array"", ""minItems"": 1,
      ""description"": ""Rows to append. Each element is an array of cell values, filled left-to-right from column A. A cell may be a string, number, boolean or null (null leaves the cell blank â€” a blank is not a zero)."",
      ""items"": { ""type"": ""array"", ""items"": { ""type"": [""string"", ""number"", ""boolean"", ""null""] } }
    }
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
        };
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
                // Canonical form, so whitespace in a schema literal is not a contract change.
                sb.Append(c.InputSchema == null ? "-" : c.InputSchema.ToString(Newtonsoft.Json.Formatting.None));
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