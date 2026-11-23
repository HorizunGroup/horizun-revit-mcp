// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Direct Python against the Revit API, on the UI thread. The standing fallback:
// no set of typed tools ever covers the whole API, this does.
//
// DISABLED BY DEFAULT: when a typed command cannot cover a request, the owner may
// grant persistent access from Revit's Python ON/OFF button until they revoke it,
// or configure the same durable opt-in administratively. It runs arbitrary code inside Revit with the full API
// and the rights of the signed-in user, so the per-machine switch is checked here
// AND in the server, because the two halves ship separately and neither may be
// the only gate.
//
// The standard library ships (IronPython.StdLib), so `import json`, `re`, `csv`,
// `datetime` resolve - no hand-rolling JSON with string joins.
//
// WHAT IT CANNOT DO, stated because this file used to claim the opposite: it
// cannot undo a transaction a script left open. The old code opened a NEW
// Transaction and rolled THAT back, which does nothing to the orphan - the Revit
// API offers no handle on a transaction opened by other code. So the document is
// checked afterwards, and a script that left it modifiable makes the command
// FAIL, with the consequence spelled out. A gesture that looks like a cure is
// worse than a stated limitation.
//
// print() is captured through a Python StringIO, never the runtime's byte
// stream — routing it through Runtime.IO returned UTF-16-as-UTF-8 ("h\0o\0l\0a").
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    public sealed class ExecutePythonCommand : ICommand
    {
        public string Name => "horizun_execute_python";

        public string Description =>
            "THE EXECUTION FALLBACK - runs arbitrary code inside Revit on the UI thread with the full API and " +
            "the rights of the signed-in user. Disabled by default; a machine owner must grant it temporarily " +
            "from Revit or enable it durably, and every explicit off choice is respected. " +
            "WHEN TO USE IT: prefer a typed command whenever one fully covers the operation - typed commands " +
            "rehearse, verify and re-read their work. When no typed capability exists, or a typed command " +
            "refuses BEFORE writing because the operation/kind/category is outside its contract, generate " +
            "minimal Python and run it HERE instead of answering 'not supported'. Do NOT fall back here after " +
            "a typed write FAILED mid-operation - it may have partially written, and a Python retry is a " +
            "second write, not a recovery; report the real state instead. " +
            "REQUIRES target_document, like every other command that can change a model: it is matched against " +
            "the ACTIVE document and refused if they differ, and this command will not switch documents for you. " +
            "A script that needs no document cannot run here. " +
            "preflight=true validates WITHOUT executing: permission, document, size, script SHA-256, basic " +
            "syntax, plus advisory warnings (typed-command overlaps, missing transaction hygiene, no " +
            "__output__). It cannot prove what arbitrary code will do. When the objective is already " +
            "unambiguous and preflight passes, CONTINUE to execution in the same task - preflight is a check, " +
            "not an approval step. " +
            "run_async=true additionally REQUIRES idempotency_key - the reply carrying your job_id is the message " +
            "that gets lost, so a retry needs a way to be recognised as one. The key is bound to the Revit " +
            "process, the target document, a SHA-256 of the code and every other argument: the same key with the " +
            "same request returns the original job_id and queues nothing, the same key with a different request " +
            "is refused. " +
            "IT IS STILL A PRIVILEGED BYPASS: unlike the typed commands it has no dry run, no plan and no " +
            "confirmation token, so nothing rehearses what it will do. That is an accepted risk, not a policy it " +
            "satisfies - see docs/security-model.md. " +
            "SCRIPTS THAT ONLY DUPLICATE A TYPED COMMAND get an advisory naming it (ViewSheet.Create / " +
            "Viewport.Create / ScheduleSheetInstance.Create -> horizun_manage_views, ViewSchedule.CreateSchedule " +
            "-> horizun_create_schedule, ElementTransformUtils.Move/Rotate/MirrorElement -> " +
            "horizun_transform_elements, doc.Delete -> horizun_delete_verified). The advisory does not block: " +
            "a composite script that also needs one of those calls runs fine, and the typed command remains " +
            "the better tool when it is the WHOLE job. " +
            "SOURCE: send 'code' inline, OR 'code_path' - a .py file on the machine running Revit, read as " +
            "UTF-8 (a BOM or a '# -*- coding: -*-' line is honoured), CRLF normalised, and measured, hashed and " +
            "bound to the idempotency key exactly like inline code. Exactly one of the two. A file makes " +
            "tracebacks name the real file and line instead of <string>. " +
            "INJECTED NAMES: doc (Document), uidoc (UIDocument), uiapp AND __revit__ (UIApplication - the " +
            "pyRevit name, aliased so it resolves), app (Application), checkpoint(label, done, total), " +
            "revit_raised(since=0), dialog_answer(answer), safe_point() and should_stop(). timeout_s and a " +
            "job cancel stop the script at its next checkpoint()/safe_point() by raising HorizunInterrupted. " +
            "VERSION COMPATIBILITY: `import hz_compat` is available. Use hz_compat.eid_int(id) and " +
            "hz_compat.element_id(number) across the ElementId change, its Python 3 aliases `unicode`, " +
            "`basestring`, `long`, and the unit/parameter/group helpers. For example, " +
            "`mm = hz_compat.unit_type_id('Millimeters', 'DUT_MILLIMETERS')`; use " +
            "horizun_api_help(operation=\"describe\", type_name=\"Autodesk.Revit.DB.ElementId\") to inspect " +
            "the API loaded by this Revit before choosing a version-specific member. " +
            "WHAT REVIT RAISED comes back as 'dialogs' and 'failures' beside __output__, each with the " +
            "script's last checkpoint as 'while'. Read them when an open fails: the bridge CANCELS modal " +
            "dialogs (nobody is at the keyboard), and all Revit tells the script is 'Opening was canceled'. " +
            "Inside the script, revit_raised(since) reads the same records DURING the run - take " +
            "len(revit_raised()) before an open and pass it back after to see exactly what that open raised. " +
            "To let one call continue past its dialog: `with dialog_answer('dismiss'): " +
            "doc = app.OpenDocumentFile(...)`, around that call ONLY - dismiss answers OK to everything, and " +
            "Revit reads OK on a close-with-changes dialog as SAVE. " +
            "RETURN EVIDENCE, not prints: assign __output__ the structured " +
            "shape {status: verified|completed_unverified|partial|failed, summary, created_ids, modified_ids, " +
            "deleted_ids, verification:{checked, evidence:[]}, warnings:[]} and RE-READ what you wrote before " +
            "claiming verified. WHAT COMES BACK IS SELF-REPORTED, NOT HOST-VERIFIED: your 'verified' is " +
            "classified as self_reported_verified, because the bridge does not re-read the model after " +
            "arbitrary code - only typed commands carry that guarantee, and no field on this path may be read " +
            "as one. A verified claim with no evidence is downgraded to completed_unverified. " +
            "print() remains as compatibility output. A dict or " +
            "list is serialized to JSON and output_kind reports scalar|structure|paged|text_only; paged = page 1 as text, see output_paging; text_only " +
            "means the structure could NOT be serialized and only its text rendering survived. The standard " +
            "library is available (json, re, csv, datetime, math). " +
            "TRANSACTIONS: NO rollback is attempted, and none is possible. This text used to say one was " +
            "'attempted, best-effort'; the code that attempted it had already been deleted for opening a NEW " +
            "transaction and rolling THAT back, which does nothing to the orphan. The Revit API gives no handle " +
            "on a transaction opened by other code. What happens instead: Document.IsModifiable is re-read after " +
            "the script, and a document left modifiable makes this command FAIL and says so. Wrap every " +
            "Transaction in try/finally with RollBack in the finally. Prefer a typed command for anything " +
            "recurring: a typed command is verified BY THE HOST, and this can only ever be self-reported. " +
            "read_only=true runs the script inside a TransactionGroup this handler ALWAYS rolls back, then " +
            "re-reads Document.IsModified and a cheap element/type census to CONFIRM the model did not move - " +
            "reported as read_only_check, never assumed. It does not lower the permission gate above, and it " +
            "does not sandbox the machine: a Save()/SaveAs()/Close()/open-document call escapes the rollback " +
            "entirely (it writes outside any transaction), so those are REFUSED before the script runs rather " +
            "than reported as a violation afterwards - remove them, or drop read_only if a real write is meant. " +
            "Not combinable with run_async=true.";

        /// <summary>
        /// The rule, carried on EVERY response rather than left in a description
        /// somebody may not have read.
        ///
        /// It is stated as a requirement on the caller because it cannot be enforced
        /// here: there is no API by which this code can reach a transaction your script
        /// opened. What CAN be done is detect the damage afterwards and refuse to call
        /// it success, and that is what happens.
        /// </summary>
        public const string TransactionPolicy =
            "REQUIRED of any script that opens a Transaction: wrap it in try/finally and Commit() or RollBack() in " +
            "the finally. This command CANNOT close it for you - the Revit API exposes no handle on a transaction " +
            "opened by other code, so there is no implementation that would satisfy a promise to do it. " +
            "WHAT IS ENFORCED: Document.IsModifiable is re-read after your script, and a document left modifiable " +
            "makes this command FAIL with transaction_left_open=true. " +
            "WHAT ACTUALLY HAPPENS TO THE ORPHAN, measured on Revit 2026 rather than assumed: when this handler " +
            "returns to Revit, Revit ends the transaction itself and its status becomes RolledBack - verified by " +
            "holding a reference to it across calls and reading GetStatus(). So the DOCUMENT recovers, and your " +
            "script's writes DO NOT: everything inside that transaction is discarded, which is why leaving one " +
            "open is reported as a failure rather than a warning. That cleanup is a host behaviour this command " +
            "observed, not a guarantee it offers - do not build on it.";

        /// <summary>
        /// Which idempotency keys this Revit session has already accepted for
        /// run_async, and which job each one belongs to.
        ///
        /// SUBORDINATE, not the authority. The single cross-process authority for "has this
        /// key run?" is the dispatcher's DurableCommandLedger, which claims the key on disk
        /// BEFORE this command is called and REPLAYS a re-sent key after any restart - so a
        /// retry across a process boundary is answered there and never reaches this ledger.
        /// This one exists for the race the durable ledger cannot see cheaply: two requests
        /// carrying ONE key in flight AT ONCE within THIS process, before the durable claim's
        /// terminal result is recorded. It stops both of them from creating a Job. It is in
        /// memory and per process on purpose, exactly like DocumentGate.Confirmations: a claim
        /// that outlived the process would be a claim about work whose outcome nobody knows,
        /// and that judgement belongs to the durable ledger, not to a stale in-memory copy.
        /// See Core/Idempotency.cs and Core/DurableCommandIdempotency.cs.
        /// </summary>
        public static readonly IdempotencyLedger AsyncClaims = new IdempotencyLedger();

        // One engine per session: creating a ScriptEngine is expensive, and the
        // point of this command is a fast round trip.
        private static ScriptEngine _engine;
        private static readonly object _engineLock = new object();

        private static ScriptEngine GetEngine()
        {
            if (_engine != null) return _engine;
            lock (_engineLock)
            {
                if (_engine != null) return _engine;
                RuntimeWarmup.Python.Begin();
                try
                {
                    // IronPython asks for the ANSI codepage (1252) while it builds the
                    // engine. On .NET 5+ that codepage is not in the base library, so
                    // CreateEngine throws "No data is available for encoding 1252" unless
                    // the provider is registered first. The registration is process-wide
                    // and idempotent: it looked intermittent because another add-in in
                    // the same Revit process sometimes registered it before us.
#if NET
                    try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
                    catch { }
#endif
                    ScriptEngine eng = Python.CreateEngine();

                    // RevitAPI, RevitAPIUI, then the base-class libraries a script imports
                    // without clr.AddReference (Regex, Linq, SHA256, Xml...). On .NET 8+
                    // System.Uri's assembly alone left those as ImportError; see
                    // Core/PythonBclAssemblies.cs. Best effort, one by one.
                    var preload = new List<System.Reflection.Assembly>
                    {
                        typeof(Document).Assembly,
                        typeof(UIApplication).Assembly
                    };
                    preload.AddRange(PythonBclAssemblies.Assemblies());
                    foreach (var asm in preload)
                    {
                        try { eng.Runtime.LoadAssembly(asm); } catch { }
                    }

                    // Point at the bundled stdlib so `import json` resolves.
                    try
                    {
                        string here = Path.GetDirectoryName(typeof(ExecutePythonCommand).Assembly.Location);
                        var paths = new List<string>(eng.GetSearchPaths());
                        foreach (string candidate in new[] { Path.Combine(here, "Lib"), here, Path.Combine(here, "Recipes") })
                            if (Directory.Exists(candidate) && !paths.Contains(candidate)) paths.Add(candidate);
                        eng.SetSearchPaths(paths);
                    }
                    catch { }

                    _engine = eng;
                    RuntimeWarmup.Python.Ready();
                    return _engine;
                }
                catch (Exception ex) { RuntimeWarmup.Python.Failed(ex); throw; }
            }
        }

        /// <summary>Scripts above this are refused. A megabyte of source is not a script.</summary>
        private const int MaxScriptChars = 200000;

        /// <summary>
        /// Direct 1:1 overlaps with a typed command - not an attempt to catch everything
        /// execute_python can do, only the handful of Revit API calls this codebase
        /// already performs, verifies and re-reads elsewhere. These RECOMMEND, they do
        /// not block: a composite script legitimately needs one of these calls alongside
        /// work no typed command covers, and refusing it forced the caller to either
        /// split one operation across two transactions or give up. The advisory tells a
        /// caller whose script is ONLY that call where the verified version lives.
        /// </summary>
        private static readonly (Regex Pattern, string TypedCommand, string Hint)[] TypedOverlaps =
        {
            (new Regex(@"\b(Wall|Floor)\s*\.\s*Create\s*\(", RegexOptions.Compiled),
             "horizun_create_elements", "Creates walls/floors with post-commit geometry checks."),
            (new Regex(@"\bNewOpening\s*\(", RegexOptions.Compiled),
             "horizun_create_elements", "kind=wall_opening covers rectangular wall openings; other opening variants may still require Python. Openings may have no Comments parameter."),
            (new Regex(@"\bViewSheet\s*\.\s*Create\s*\(", RegexOptions.Compiled),
             "horizun_manage_views",
             "operation=\"create_sheet\" creates a sheet in the same verified batch as everything else that command does."),
            (new Regex(@"\bViewport\s*\.\s*Create\s*\(", RegexOptions.Compiled),
             "horizun_manage_views",
             "operation=\"place_view\" places a view on a sheet."),
            (new Regex(@"\bScheduleSheetInstance\s*\.\s*Create\s*\(", RegexOptions.Compiled),
             "horizun_manage_views",
             "operation=\"place_schedule\" places a schedule on a sheet."),
            (new Regex(@"\bViewSchedule\s*\.\s*CreateSchedule\s*\(", RegexOptions.Compiled),
             "horizun_create_schedule",
             "creates a schedule and verifies it, rather than a bare API call this command cannot check."),
            (new Regex(@"\bElementTransformUtils\s*\.\s*(Move|Rotate|Mirror)Element", RegexOptions.Compiled),
             "horizun_transform_elements",
             "moves/rotates/mirrors elements and re-reads their position afterwards."),
            (new Regex(@"(?<!\w)doc\s*\.\s*Delete\s*\(", RegexOptions.Compiled),
             "horizun_delete_verified",
             "deletes elements and confirms they are actually gone afterwards."),
            (new Regex(@"\b(LinearArray|RadialArray)\s*\.\s*(Create|ArrayElements?WithoutAssociation)\s*\(", RegexOptions.Compiled),
             "horizun_transform_elements",
             "operation=\"array_linear\" / \"array_radial\" arrays elements and re-reads every copy at its formula position."),
            (new Regex(@"\bRailing\s*\.\s*Create\s*\(", RegexOptions.Compiled),
             "horizun_create_railing",
             "creates a railing on a stair/ramp or along a path and re-reads its type, host and path."),
            (new Regex(@"\bWallSweep\s*\.\s*Create\s*\(", RegexOptions.Compiled),
             "horizun_wall_sweep",
             "creates a wall sweep/reveal type from a profile and places sweeps on walls at a height, re-reading host, distance and side."),
            (new Regex(@"\b(NewSlabEdge|NewFascia|NewGutter)\s*\(", RegexOptions.Compiled),
             "horizun_wall_sweep",
             "operation=\"read\" with host_id lists a floor's or roof's edges as stable references and operation=\"place_edge\" makes the slab edge, fascia or gutter on the edges named, re-reading class, type and host."),
            (new Regex(@"\.CropBox\s*=|\bCropBox(Active|Visible)\s*=|AnnotationCropOffset\s*=", RegexOptions.Compiled),
             "horizun_view_crop",
             "sets a view's crop from a model rectangle, elements or a room (rotated views included), annotation crop offsets and the crop switches, re-reading the crop after commit."),
            (new Regex(@"\bJoinGeometryUtils\s*\.\s*(JoinGeometry|UnjoinGeometry|SwitchJoinOrder)\s*\(", RegexOptions.Compiled),
             "horizun_join_geometry",
             "joins, unjoins or switches who cuts between walls, floors, framing, columns and foundations (first_cuts, priority or keep_revit), re-reading the join, the cutting element and the shared volume (0 after a join)."),
            (new Regex(@"\b(JoinGeometryUtils\s*\.\s*(GetJoinedElements|IsCuttingElementInJoin)|InstanceVoidCutUtils\s*\.\s*(GetCuttingVoidInstances|GetElementsBeingCut)|SolidSolidCutUtils\s*\.\s*Get\w+|get_ElementsAtJoin)\s*\(", RegexOptions.Compiled),
             "horizun_query_relations",
             "read-only: what an element is joined to, cut by, cutting, hosted by, hosting and near (neighbors within radius_mm), with category and relation type; unreadable relations are named."),
            (new Regex(@"\.\s*Volume\b|\bPlanarFace\b|\.\s*FaceNormal\b|\.\s*get_BoundingBox\s*\(", RegexOptions.Compiled),
             "horizun_measure_geometry",
             "read-only: solid_volume, section_profile (e.g. a 300x500 beam), bounding_box, end_points and face_normals of ids or a selector; what cannot be read is unreadable, never 0."),
            (new Regex(@"\bSetBoxCenter\s*\(", RegexOptions.Compiled),
             "horizun_sheet_layout",
             "moves a viewport by its crop (or box) centre or corner in paper mm and lays out a sheet's viewports in rows, re-reading positions, overlaps and the usable area after commit."),
            (new Regex(@"\b(AddGridLine|AddMullions|ChangePanelType)\s*\(", RegexOptions.Compiled),
             "horizun_manage_curtain",
             "edits curtain grid lines, mullions and panel types and re-reads the committed grid."),
            (new Regex(@"\b(DrawPoint|AddPoint|DrawSplitLine|AddSplitLine|ModifySubElement|ResetSlabShape)\s*\(", RegexOptions.Compiled),
             "horizun_slab_shape",
             "edits a floor or roof shape and re-reads every touched vertex elevation."),
            (new Regex(@"\b(CreatedPhaseId|DemolishedPhaseId)\s*=(?!=)|\bPhaseFilter\s*\.\s*Create\s*\(", RegexOptions.Compiled),
             "horizun_manage_phases",
             "operation=\"set_element_phases\" / \"create_phase_filter\" set phases and phase filters and re-read them."),
            (new Regex(@"\bPartUtils\s*\.\s*(CreateParts|DivideParts)\s*\(|\bAssemblyInstance\s*\.\s*Create\s*\(", RegexOptions.Compiled),
             "horizun_manage_assemblies_parts",
             "creates parts and assemblies and re-reads their associations and members."),
            (new Regex(@"\bParameterFilterElement\s*\.\s*Create\s*\(|\.\s*SetElementFilter\s*\(", RegexOptions.Compiled),
             "horizun_manage_views",
             "operation=\"create_filter\"/\"edit_filter\" builds or edits a filter's rules and re-reads them after the commit."),
            (new Regex(@"\.\s*SetCategory(Hidden|Overrides)\s*\(", RegexOptions.Compiled),
             "horizun_manage_views",
             "operation=\"set_category_visibility\" hides or overrides a category/subcategory and refuses a view whose template governs V/G."),
            (new Regex(@"\bViewSchedule\s*\.\s*CreateKeySchedule\s*\(", RegexOptions.Compiled),
             "horizun_create_schedule",
             "key_schedule=true creates the key schedule and its key rows and re-reads them."),
            (new Regex(@"\.\s*SetExportLayerTable\s*\(", RegexOptions.Compiled),
             "horizun_export",
             "format=\"dwg_layers\" writes a named DWG setup's layer table and re-reads it from a fresh lookup."),
            (new Regex(@"\b(LinePatternElement|FillPatternElement)\s*\.\s*Create\s*\(|\bNewSubcategory\s*\(", RegexOptions.Compiled),
             "horizun_manage_styles",
             "creates line patterns, fill patterns and subcategories and re-reads them after commit."),
            (new Regex(@"\.\s*SetUnits\s*\(|\bSetProjectPosition\s*\(", RegexOptions.Compiled),
             "horizun_manage_units",
             "operation=set / base_points writes units or shared coordinates and re-reads them after commit."),
        };

        /// <summary>
        /// Advisory lines naming the typed command each detected call duplicates. Empty
        /// when the script touches none of them. This used to be a hard refusal; it is
        /// deliberately not one any more - the typed command is recommended where it
        /// covers the WHOLE job, and Python remains available for the composite script,
        /// the uncovered variant, and the sequence no typed command satisfies.
        /// </summary>
        private static JArray TypedAlternatives(string code)
        {
            var advisories = new JArray();
            // Match against CODE only. A live run advised a script whose sole mention of
            // ElementTransformUtils.MoveElement was inside a comment - advice that fires
            // on prose is advice people learn to skip, including when it is right.
            //
            // Python's own lexer first (TokenCategorizer is a public DLR hosting service
            // and lexes WITHOUT executing); the hand scanner when it is unavailable or
            // the source will not lex. Failing soft is deliberate: an advisory must never
            // be the reason a run does not happen.
            string scanned = PythonTokenMask.Mask(GetEngine(), code)
                             ?? PythonSourceMask.StripCommentsAndStrings(code);
            foreach (string tool in PythonSourceMask.S22Alternatives(scanned))
                advisories.Add("For the corresponding read consider " + tool + ": audit_model operation=warnings, measure_geometry measures=[summary], or query_planimetry modes=placements/views. These reads do not perform script writes; check coverage before substituting.");
            foreach (var overlap in TypedOverlaps)
            {
                if (overlap.Pattern.IsMatch(scanned))
                {
                    advisories.Add(
                        "This script calls an API that " + overlap.TypedCommand + " already performs and verifies: " +
                        overlap.Hint + " If that single call is the WHOLE job, prefer " + overlap.TypedCommand +
                        " - it re-reads the result after the write. If the script does more than that call, " +
                        "proceeding here is fine; verify from inside the script and say so in __output__.");
                }
            }
            return advisories;
        }

        // True once this call has crossed the point of no return: the script is about to run,
        // or has been handed to the async queue. A refusal BEFORE that point is stamped
        // RejectedBeforeRun so the dispatcher gives the idempotency key back (CT-P1-5b).
        // Per thread and restored on exit, because Execute can be re-entered by the async path.
        [ThreadStatic] private static bool s_runBegan;

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            bool outerRunBegan = s_runBegan;
            s_runBegan = false;
            try
            {
                CommandResult result = ExecuteCore(app, paramsJson);
                if (result != null && !result.Success && !s_runBegan) result.AsRejectedBeforeRun();
                return result;
            }
            catch (Exception ex)
            {
                var detail = ErrorCodes.Detail(ErrorCodes.ErrorInterno, ex.GetType().Name);
                detail["internal_trace"] = PythonInternalTrace.FromException(ex);
                var result = CommandResult.FailWithDetail("Internal Python bridge error: " + ex.GetType().Name, detail);
                if (!s_runBegan) result.AsRejectedBeforeRun();
                return result;
            }
            finally { s_runBegan = outerRunBegan; }
        }

        private CommandResult ExecuteCore(UIApplication app, string paramsJson)
        {
            // Checked HERE as well as in the server. The two halves ship separately, so a
            // stale server that still advertises this tool must not be able to run code on
            // a machine whose owner switched it off.
            //
            // read_only=true (below) does NOT relax this gate, and that is a deliberate
            // decision, not an oversight: a script labelled read_only can still write a
            // file to disk, open a socket, or shell out - the TransactionGroup rollback
            // only ever protects the REVIT MODEL, never the machine. Trusting the caller's
            // own label to unlock a lower permission tier would let "read_only=true" become
            // the new way to run unsafe_code from safe_write. The permission ladder in
            // Settings.cs is unchanged; read_only only adds a stronger, VERIFIED promise
            // on top of the same gate everything else here already requires.
            if (!Horizun.Revit.Core.Settings.ExecutePythonEnabled)
            {
                Log.Warn("execute_python REFUSED: disabled in settings");
                return CommandResult.FailWithDetail(Horizun.Revit.Core.Settings.ExecutePythonRefusal(),
                    ErrorCodes.Detail(ErrorCodes.PermisoPythonRequerido, extra: new JObject
                    {
                        ["write_started"] = false, ["changes_applied"] = false, ["transaction_status"] = "not_started"
                    }));
            }

            JObject request;
            bool runAsync;
            bool preflight;
            bool readOnly;
            bool rehearsal;
            bool documentationOnly;
            string idempotencyKey;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson);
                runAsync = request.Value<bool?>("run_async") ?? false;
                preflight = request.Value<bool?>("preflight") ?? false;
                // Field 6.x: run the script inside a TransactionGroup that is ALWAYS
                // rolled back, and prove afterwards that the model did not move. See
                // Core/ReadOnlyPythonGuard.cs for what this can and cannot promise.
                readOnly = request.Value<bool?>("read_only") ?? false;
                // rehearsal=true IS read_only (the same always-rolled-back group, the same
                // refusal of Save/Close/Open) plus the host's account of what the script
                // changed while it ran. See Core/RehearsalRules.cs.
                rehearsal = request.Value<bool?>("rehearsal") ?? false;
                if (rehearsal) readOnly = true;
                // documentation_only=true (4.2-a): the script may change views, sheets, annotation
                // and elements a view owns, and nothing else. Enforced on the DIFF of the run.
                documentationOnly = request.Value<bool?>("documentation_only") ?? false;
                idempotencyKey = request.Value<string>("idempotency_key");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }

            // WHERE THE SOURCE COMES FROM - inline `code` or a file named by `code_path`,
            // never both (5.27). The file is read HERE, on the host side of the rules that
            // follow: the size limit below and the SHA-256 measure what was read, so
            // code_path is not a way around either.
            //
            // The dispatcher freezes and hashes this source before durable admission.
            // Async jobs carry the same snapshot and never re-read its path.
            SourceResolution source = ResolveSource(request);
            if (source.Error != null) return CommandResult.Fail(source.Error);
            string responseMode = request.Value<string>("response_mode") ?? "compact";
            if (responseMode != "compact" && responseMode != "full") return CommandResult.Fail("response_mode must be compact or full. Nothing ran.");
            string responseShape = request.Value<string>("response") ?? "normal";
            if (responseShape != "normal" && responseShape != "output_only") return CommandResult.Fail("response must be normal or output_only. Nothing ran.");
            double? timeoutLimit;
            string timeoutError;
            if (!PythonRunControl.TryParseLimit(request["timeout_s"], out timeoutLimit, out timeoutError))
                return CommandResult.Fail(timeoutError);
            double? deadlineLimit;
            if (!PythonRunControl.TryParseDeadline(request["deadline_s"], timeoutLimit, out deadlineLimit, out timeoutError))
                return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, timeoutError);
            // Page size AND threshold of __output__: max_output_chars when sent (an explicit null counts as omitted), else 20,000.
            // Above it the output is saved whole and only page 1 comes back (Core/OutputPages.cs, ScriptOutput.cs).
            int? askedOutput = request["max_output_chars"] == null || request["max_output_chars"].Type == JTokenType.Null ? (int?)null : request.Value<int?>("max_output_chars");
            int outputPageChars = askedOutput.HasValue ? Math.Max(1024, Math.Min(Horizun.Contracts.Contract.MaxScriptTextChars, askedOutput.Value)) : OutputPages.PageChars;
            string code = source.Code;

            if (code.Length > MaxScriptChars)
                return CommandResult.Fail("Script is " + code.Length + " characters" +
                    (source.Path == null ? "" : " (read from " + source.Path + ")") + ", over the " +
                    MaxScriptChars + " limit. This limit is deliberate and must not be evaded: everything that " +
                    "runs is the source this command resolved, and it is hashed as submitted_source_sha256. " +
                    "Use code_path if the problem is only that the script is awkward to send inline - it is read " +
                    "here, then measured and hashed exactly like code. What is NOT the answer is a stub that " +
                    "opens a file at RUNTIME: that slips past this limit and past that hash. Reduce the script, " +
                    "or split the work across separate execute_python calls. Nothing ran.");
            if (preflight && runAsync)
                return CommandResult.Fail(
                    "preflight=true cannot be combined with run_async=true: a preflight executes nothing, so " +
                    "there is nothing to queue. Preflight synchronously, then submit the real run. Nothing ran.");
            if (documentationOnly && (readOnly || runAsync))
                return CommandResult.Fail(
                    "documentation_only=true cannot be combined with " + (readOnly ? "read_only/rehearsal" : "run_async") + ": " +
                    (readOnly
                        ? "read_only always rolls back everything, documentation_only keeps what is documentation. Choose one."
                        : "the documentation-only TransactionGroup is judged and closed around a SINGLE synchronous run of this handler.") +
                    " Nothing ran.");
            if (readOnly && runAsync)
            {
                JObject advice = PythonSourceSnapshot.AsyncReadOnlyRefusal(request);
                return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, (string)advice["reason"], extra: advice);
            }

            // Computed before the gate and before anything is queued, and carried on the
            // response either way: a script that duplicates a typed command's own API
            // call is TOLD where the verified version lives, and still allowed to run -
            // the composite script and the uncovered variant are exactly what this
            // fallback exists for.
            JArray typedAlternatives = TypedAlternatives(code);

            // Same treatment: computed before the gate, before anything queues, and
            // carried into the preflight report so a caller sees the refusal coming.
            // Save/SaveAs/Close/open-a-document escape a TransactionGroup rollback (they
            // write outside any transaction), so read_only refuses them OUTRIGHT rather
            // than letting them run and reporting a violation afterwards - by the time
            // "afterwards" arrives, the file on disk is already real.
            // EVERY source that runs in the scope is scanned: the includes execute before the
            // main script in the same scope, so a scan of `code` alone is a guard with a side
            // door. Masked once, and only when a guard needs it.
            var rawSources = new List<string> { code };
            foreach (var include in source.Includes) rawSources.Add((string)include["code"] ?? "");
            List<string> maskedSources = null;
            Func<List<string>> masked = () => maskedSources ?? (maskedSources = rawSources
                .Select(s => PythonTokenMask.Mask(GetEngine(), s) ?? PythonSourceMask.StripCommentsAndStrings(s)).ToList());
            List<string> readOnlyViolations = (readOnly || documentationOnly)
                ? masked().SelectMany(ReadOnlyPythonGuard.Violations).Distinct().ToList()
                : new List<string>();

            // THE SAME GATE AS EVERY TYPED MUTATION, and it used to be the one command
            // without it.
            //
            // A script here can do anything the API can, so "the active document" was
            // the target of the most powerful command in the surface - decided by
            // whichever window happened to be in front when the call arrived. Every
            // typed write refuses that, and the claim that Horizun had one mutation
            // policy was false for exactly as long as this command evaded it.
            //
            // The narrowing is real and deliberate: a script that needs NO document can
            // no longer run through this tool, because there is no way to name a target
            // that does not exist without also creating a hole. That is the cost, and
            // it is smaller than an arbitrary write landing in the wrong building.
            GateResult gate = readOnly ? DocumentGate.ForReadOnlyScript(app, request, Name) : DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;

            // The owner's Synchronize-with-central switch covers scripts as well as
            // horizun_document_session: a sync the owner did not authorise, or any sync while
            // shared models are protected (that policy wins over the grant), is refused here,
            // before preflight or run, whatever read_only says - over the main source AND its
            // includes. Best-effort text scan - see
            // SyncWithCentralRules.PythonSyncRefusal for its honest ceiling.
            if (rawSources.Any(s => s.IndexOf("Synchroniz", StringComparison.Ordinal) >= 0))
            {
                // Unreadable settings fall CLOSED, exactly as in the typed operation.
                bool syncGranted, protectedShared;
                try { syncGranted = Horizun.Revit.Core.Settings.SyncWithCentralOwnerEnabled; } catch { syncGranted = false; }
                try { protectedShared = Horizun.Revit.Core.Settings.ForceReadOnlyOnWorkshared; } catch { protectedShared = true; }
                SyncRefusal syncRefusal = SyncWithCentralRules.PythonSyncRefusal(masked(), syncGranted, protectedShared);
                if (syncRefusal != null)
                    return CommandResult.FailWithDetail(syncRefusal.Message, new JObject
                    {
                        ["code"] = syncRefusal.Code, ["write_started"] = false, ["changes_applied"] = false
                    });
            }

            // ---- compatibility with THIS Revit and Python 3, before anything runs ----
            // (Core/PythonCompat.cs). What is certainly wrong and certainly fixable is
            // rewritten on the same line and reported; what cannot be adapted is refused
            // here with the exact correction - preflight or not, nothing has run.
            // `code` stays the submitted source (hashes, idempotency, the async queue);
            // `runCode` is what compiles and executes.
            int revitYear = RevitYearOf(app);
            PythonCompat.Result compat = PythonCompat.Apply(code, revitYear, BuiltInCategoryNames());
            string runCode = compat.Code;
            List<string> autoTransactionMarkers = PythonCompat.AutoTransactionMarkers(code);
            if (compat.Errors.Count > 0)
            {
                var fixes = compat.Errors.OfType<JObject>()
                    .Select(e => "line " + (int?)e["line"] + ": " + (string)e["fix"]).ToList();
                Log.Warn("execute_python REFUSED before running: " + compat.Errors.Count + " compatibility error(s)");
                return CommandResult.FailWithDetail(
                    "The script cannot run on Revit " + revitYear + " as written, so nothing ran and the model was " +
                    "not touched. " + string.Join(" ", fixes),
                    new JObject
                    {
                        ["code"] = "python_compat_refused", ["tool"] = Name,
                        ["executed"] = false, ["write_started"] = false, ["changes_applied"] = false,
                        ["transaction_status"] = "not_started",
                        ["compat_errors"] = compat.Errors,
                        ["compat_rewrites"] = compat.Rewrites
                    });
            }

            // ---- preflight: validate everything checkable, execute NOTHING. ----
            //
            // Runs AFTER the same gate as the real run, so a preflight that passes has
            // already proved permission, size and document targeting for real. What it
            // adds is a compile-only syntax check and advisory warnings. What it CANNOT
            // do is prove the safety or effect of arbitrary code without running it,
            // and the response says so instead of implying a rehearsal happened.
            if (preflight)
            {
                var warnings = new JArray();
                warnings.Add(PythonSourceSnapshot.PreflightTimeoutAdvice(readOnly));
                foreach (var advisory in PythonInternalTrace.Advisories(typedAlternatives).Children()) warnings.Add(advisory);
                if (PythonCollectorHazard.HasViewLinkOverload(code))
                    warnings.Add("FilteredElementCollector(doc, viewId, linkId) can terminate Revit in affected builds. " +
                        "Use horizun_manage_links (Pro edition) operation=visible_elements to inspect the link without that overload; " +
                        "preflight has not executed this script.");

                if ((readOnly || documentationOnly) && readOnlyViolations.Count > 0)
                    warnings.Add(
                        (documentationOnly ? "documentation_only=true" : "read_only=true") + " would be REFUSED for this script: it mentions " +
                        string.Join(", ", readOnlyViolations) + ", which write outside any transaction and " +
                        "escape a TransactionGroup rollback. Remove the call, or drop " + (documentationOnly ? "documentation_only" : "read_only") + ".");

                if (code.IndexOf("Transaction", StringComparison.Ordinal) >= 0 &&
                    code.IndexOf("finally", StringComparison.Ordinal) < 0)
                    warnings.Add(
                        "The script mentions Transaction but contains no finally block, so an exception between " +
                        "Start() and Commit() leaves the transaction open and the run is reported as a failure. " +
                        "Wrap it in try/finally with Commit() or RollBack() in the finally.");

                if (code.IndexOf("__output__", StringComparison.Ordinal) < 0)
                    warnings.Add(
                        "The script never assigns __output__, so its result will be classified " +
                        "completed_unverified - it can succeed but cannot prove it. Assign the structured " +
                        "evidence shape and re-read what you wrote: " + ScriptEvidence.ContractShape);

                ScriptPrecompile.Failure preflightCompile = ScriptPrecompile.Check(GetEngine(), runCode, source.Path,
                    source.Includes.Select(i => new KeyValuePair<string, string>((string)i["code"], (string)i["path"])));
                string syntaxError = preflightCompile?.Formatted;

                string scriptSha;
                using (var sha = SHA256.Create())
                    scriptSha = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(code)))
                                            .Replace("-", "").ToLowerInvariant();

                Log.Warn("execute_python PREFLIGHT by '" + SafeUser(app) + "' on '" + SafeTitle(gate.Document) +
                         "', " + code.Length + " chars, syntax " + (syntaxError == null ? "ok" : "FAILED"));

                return CommandResult.Ok(new JObject
                {
                    ["mode"] = "preflight",
                    ["executed"] = false,
                    ["would_run"] = syntaxError == null && !((readOnly || documentationOnly) && readOnlyViolations.Count > 0),
                    ["documentation_only"] = documentationOnly,
                    ["source"] = source.Describe(),
                    ["read_only"] = readOnly,
                    ["checks"] = new JObject
                    {
                        ["permission"] = "ok",
                        ["target_document"] = string.IsNullOrWhiteSpace(ReadDocumentRules.Target(request)) ? "not required (read_only/rehearsal) - the active document is read" : "ok - matches the active document",
                        ["size"] = code.Length + " of " + MaxScriptChars + " chars",
                        ["source"] = source.Path == null
                            ? "ok - inline"
                            : "ok - read from " + source.Path + " as " + source.Encoding,
                        ["syntax"] = syntaxError == null ? "ok" : "failed",
                        ["read_only_scan"] = !(readOnly || documentationOnly)
                            ? "not requested"
                            : (readOnlyViolations.Count == 0
                                ? "ok - no Save/SaveAs/Close/open-document call found in the source"
                                : "WOULD BE REFUSED: " + string.Join(", ", readOnlyViolations))
                    },
                    ["syntax_error"] = syntaxError,
                    ["advice"] = PythonErrorAdvice.Find(syntaxError, revitYear),
                    ["syntax_error_summary"] = syntaxError == null ? null : ScriptErrorSummary.Build(syntaxError, preflightCompile.SourceCode, preflightCompile.SourcePath),
                    // What the bridge will change before running (same line numbers), and
                    // whether it will run the script inside a transaction of its own.
                    ["compat_rewrites"] = compat.Rewrites,
                    ["auto_transaction"] = AutoTransactionReport(autoTransactionMarkers, readOnly),
                    // Named for what it actually is. It is the hash of the SOURCE that was
                    // submitted, and nothing more - not a fingerprint of everything that runs.
                    ["submitted_source_sha256"] = scriptSha,
                    // DEPRECATED, and kept working on purpose. The old name claimed to
                    // identify "the script", which over-promised: it never covered imports,
                    // exec/eval or a file read at runtime. RELEASE-POLICY forbids removing a
                    // returned field outright - a renamed field keeps its old name working for
                    // two MINOR releases - so both travel, with identical values, until then.
                    ["script_sha256"] = scriptSha,
                    ["script_sha256_deprecated"] =
                        "DEPRECATED since 0.7.0, removed no earlier than 0.9.0: use " +
                        "submitted_source_sha256, which says what the hash actually covers. Both fields carry " +
                        "the same value today.",
                    ["submitted_source_sha256_covers"] =
                        "The SHA-256 of the SUBMITTED source text ONLY. It is NOT a fingerprint of everything that " +
                        "executes: any modules this script imports, anything it builds and hands to exec() or " +
                        "eval(), and any file it opens at runtime are OUTSIDE this hash. Treat it as the identity " +
                        "of what was sent, not of what ran.",
                    ["warnings"] = warnings,
                    ["what_preflight_cannot_do"] =
                        "It proves permission, document targeting, size and that the source parses. It CANNOT " +
                        "prove the safety or effect of arbitrary code - only running it and re-reading the model " +
                        "does that. If the objective, document, scope and success criterion are already " +
                        "unambiguous and this preflight passed, continue to execution in this same task rather " +
                        "than stopping to ask.",
                    ["transaction_policy"] = TransactionPolicy
                });
            }

            // read_only's REAL refusal - not a preflight report, an actual stop. Checked
            // after the gate (so the document check above ran for real) and before
            // anything executes or queues: a Save() this scan caught is a real file on
            // disk that no TransactionGroup rollback could undo, so this has to happen
            // BEFORE the script runs, not as a violation reported afterwards.
            if ((readOnly || documentationOnly) && readOnlyViolations.Count > 0)
                return CommandResult.Fail(
                    (documentationOnly ? "documentation_only=true" : "read_only=true") + " REFUSES to run this script: it mentions " + string.Join(", ", readOnlyViolations) +
                    ". These write OUTSIDE any transaction - a TransactionGroup rollback undoes MODEL writes, not " +
                    "a file Save() already put on disk or a document Close()/open that already changed the " +
                    "session. read_only can only guarantee the MODEL is unchanged, never the filesystem or the " +
                    "document session. Remove the call, or drop read_only if you accept a real write. This is a " +
                    "static, best-effort scan of the masked source text, not a sandbox: code built with " +
                    "getattr()/string concatenation to dodge these exact names will not be caught. Nothing ran.");

            // ---- run_async: hand back a job_id and get off the request. ----
            //
            // AT MOST ONCE. The entry is queued here and claimed exactly once by the
            // dispatcher; a duplicate raise of the external event finds nothing. There is
            // no retry and no requeue, deliberately: these scripts write to models, and
            // re-running one that already wrote is a second write, not a recovery.
            //
            // The client's request ends here. Whatever it does afterwards - times out,
            // cancels, disconnects - changes nothing about the work, because the work has
            // not started and will start on Revit's UI thread regardless.
            if (runAsync)
            {
                // A key is REQUIRED, because the reply carrying the job_id is exactly
                // the message that goes missing. Without one, a client doing the correct
                // thing after a timeout - sending it again - queues a second run of a
                // script that is already running, and nothing downstream can tell that
                // from two deliberate runs.
                if (string.IsNullOrWhiteSpace(idempotencyKey))
                    return CommandResult.Fail(
                        "'idempotency_key' is required when run_async is true. This reply - the one carrying your " +
                        "job_id - is the message that gets lost, and a caller that re-sends after a timeout would " +
                        "queue the script a SECOND time. Send any string you can reproduce on a retry (a uuid is " +
                        "fine). Re-sending the same key with the same request returns the original job_id and " +
                        "queues nothing; re-sending it with a different request is refused. Nothing was queued.");

                int revitPid;
                try { revitPid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { revitPid = -1; }

                // Everything the claim is made of. The key itself is the handle, not part
                // of what it identifies; run_async is excluded because it says how the
                // answer is DELIVERED, not what is done - a caller retrying the same work
                // synchronously is still the same work.
                string fingerprint = RequestFingerprint.Of(
                    revitPid, gate.Fingerprint, code, request, "idempotency_key", "run_async");

                // The queued copy must carry the TARGET as well as the code. Without it
                // the deferred run reaches the gate with no target_document and refuses
                // itself - and it is re-checked deliberately: by the time the UI thread
                // takes this entry, the active document may have moved, and a queued
                // mutation whose target is no longer in front must not land somewhere
                // else. It fails into the job record, which is where an async caller
                // reads outcomes anyway.
                var queued = new JObject
                {
                    ["run_async"] = false,
                    ["target_document"] = request.Value<string>("target_document")
                                          ?? request.Value<string>("expected_document")
                                          ?? request.Value<string>("target_document_title"),
                    // The RESOLVED source, never the path. A code_path run reads the file
                    // once, HERE, at submit: the deferred run must execute the script the
                    // fingerprint was taken of, not whatever that path holds twenty
                    // minutes later when the queue reaches it.
                    ["code"] = code,
                    ["code_origin_path"] = source.Path,
                    ["source_includes"] = source.Includes.DeepClone(),
                    // MEASURED 2026-09-25: a field session found the deferred run read
                    // HORIZUN_ARGS_JSON as '{}' and KeyError'd on its own arguments, because
                    // this queued copy never carried them - only the synchronous path (below,
                    // request["arguments"]) ever read them. The dispatcher replays the run by
                    // re-Executing THIS command with 'queued' as the whole request, so
                    // whatever this object omits, the deferred script never sees.
                    ["arguments"] = request["arguments"]?.DeepClone(),
                    ["response_mode"] = request["response_mode"],
                    ["response"] = responseShape,
                    ["timeout_s"] = request["timeout_s"]?.DeepClone(),
                    ["deadline_s"] = request["deadline_s"]?.DeepClone(),
                    ["max_output_chars"] = askedOutput.HasValue ? (JToken)outputPageChars : JValue.CreateNull(),
                    // The dispatcher runs the script itself; a second async hop would
                    // queue it forever.
                    ["helpers_version"] = 1
                };

                s_runBegan = true; // queued: from here on a failure is a record of an attempt, not a refusal
                Job asyncJob = null;
                IdempotencyDecision decision;
                try
                {
                    decision = AsyncClaims.Claim(idempotencyKey, Name, fingerprint, () =>
                    {
                        // Inside the ledger's lock. Two requests carrying one key can be in
                        // flight at once - that is what a retry IS - and creating the record
                        // out here would let both of them make one.
                        asyncJob = Job.Start(Name);
                        return asyncJob.Id;
                    });
                }
                catch (JobRecordException ex)
                {
                    // The record could not be created, so no job_id exists to hand back and
                    // the script must not be queued: run_async reports ONLY through that
                    // record. The key is left UNCLAIMED - Claim assigns _claims[key] only
                    // after the factory returns - so an honest retry is still possible once
                    // whatever broke the disk is fixed.
                    Log.Warn("execute_python run_async REFUSED: " + ex.Message);
                    return CommandResult.Fail(ex.Message + " The idempotency_key was NOT consumed; retry it once " +
                                              "the job directory is writable.");
                }

                if (decision.Outcome == IdempotencyOutcome.Conflict)
                {
                    Log.Warn("execute_python REFUSED: idempotency_key '" + idempotencyKey + "' reused for a " +
                             "different request");
                    return CommandResult.Fail(decision.Message);
                }

                if (decision.Outcome == IdempotencyOutcome.Replay)
                {
                    // NOTHING IS QUEUED HERE. This is the whole point.
                    Log.Warn("execute_python REPLAY of idempotency_key '" + idempotencyKey + "' -> job " +
                             decision.Claim.JobId + " (replay #" + decision.Claim.ReplayCount + "); nothing queued");
                    return CommandResult.Ok(new JObject
                    {
                        ["mode"] = "async",
                        ["job_id"] = decision.Claim.JobId,
                        ["status"] = "already_claimed",
                        ["replayed"] = true,
                        ["replay_count"] = decision.Claim.ReplayCount,
                        ["queued_again"] = false,
                        ["executed"] = false,
                        ["claimed_utc"] = decision.Claim.ClaimedUtc.ToString("u"),
                        ["poll_with"] = "horizun_job_status with job_id=" + decision.Claim.JobId,
                        ["what_this_means"] =
                            "This exact request was already accepted in this Revit session under idempotency_key '" +
                            idempotencyKey + "'. NOTHING WAS QUEUED and nothing ran a second time - you are being " +
                            "handed the original job_id. Read horizun_job_status for what it did. If you expected " +
                            "new work, you re-used a key.",
                        ["idempotency_scope"] =
                            "The command-local claim is bound to this Revit process (pid " + revitPid + "), the " +
                            "target document, a SHA-256 of the code and every other argument. The dispatcher also " +
                            "holds the universal durable operation claim used by every mutation."
                    });
                }

                string queueRefusal;
                if (!AsyncQueue.TryAdd(new AsyncWork
                    {
                        JobId = asyncJob.Id,
                        Command = Name,
                        ParamsJson = queued.ToString(),
                        Record = asyncJob,
                        QueuedUtc = DateTime.UtcNow
                    }, out queueRefusal))
                {
                    // The record was opened inside the ledger's lock, before we could
                    // know the queue was full. Close it as not_started rather than leave
                    // it open: a record with no finish line is reported as "running, or
                    // the process died", and this one is neither.
                    //
                    // The KEY STAYS CLAIMED, deliberately. A retry with the same key gets
                    // this same job_id, whose record says not_started in as many words -
                    // which is the truth. Releasing it would let a retry queue the script
                    // for real, turning a refusal the caller has already been told about
                    // into an execution it is not expecting.
                    try { asyncJob.Finish("not_started", queueRefusal); } catch { }
                    Log.Warn("execute_python REFUSED: async queue full (" + AsyncQueue.Count + "/" +
                             AsyncQueue.MaxDepth + "); job " + asyncJob.Id + " closed as not_started");
                    return CommandResult.Fail(queueRefusal + " (job " + asyncJob.Id +
                                              " was opened for this request and is recorded as not_started.)");
                }

                Log.Warn("execute_python QUEUED async as " + asyncJob.Id + " by '" + SafeUser(app) + "', " +
                         code.Length + " chars, key '" + idempotencyKey + "'");

                return CommandResult.Ok(new JObject
                {
                    ["mode"] = "async",
                    ["job_id"] = asyncJob.Id,
                    ["status"] = "queued",
                    ["source"] = source.Describe(),
                    ["typed_alternatives"] = typedAlternatives.Count == 0 ? null : typedAlternatives,
                    ["queue_depth"] = AsyncQueue.Count,
                    ["poll_with"] = "horizun_job_status with job_id=" + asyncJob.Id,
                    ["executed"] = false,
                    ["idempotency_key"] = idempotencyKey,
                    ["safe_to_retry"] =
                        "YES, with this same key and this same request. You will get this same job_id back and " +
                        "nothing will be queued a second time. The key is bound to this Revit process (pid " +
                        revitPid + "), the target document, a SHA-256 of the code and every other argument - " +
                        "change any of them and the retry is REFUSED rather than silently treated as this one. " +
                        "The dispatcher records this operation durably before it reaches this queue.",
                    ["executed_means"] =
                        "false because NOTHING HAS RUN YET. This reply means the script is queued, not that it " +
                        "worked. Read horizun_job_status for what it did.",
                    ["at_most_once"] =
                        "AT MOST ONCE, on TWO layers with the DURABLE one as the single authority. (1) The entry " +
                        "is claimed from the queue DESTRUCTIVELY, so a duplicate raise of the external event finds " +
                        "nothing. (2) IN THIS PROCESS, a re-sent request is recognised by its idempotency_key and " +
                        "queues nothing - the same job_id comes back. (3) ACROSS PROCESSES, the dispatcher's " +
                        "durable ledger recorded this key+fingerprint before you got this reply, and it SURVIVES a " +
                        "Revit or MCP-server restart: a retry after one is intercepted by durable REPLAY before the " +
                        "script layer is even reached, so it returns this same queued reply and runs NOTHING new. " +
                        "(4) If Revit refuses to schedule the queue, or shuts down first, this job is closed as " +
                        "not_started rather than left open. WHAT IT DOES NOT COVER: the CERTAINTY of the original " +
                        "run's OUTCOME if the process died mid-script. The durable ledger still refuses to re-run " +
                        "it - a key whose claim has no terminal result stays in_doubt rather than being replayed as " +
                        "either done or not - so the residual risk is an unknown outcome to inspect, never a second " +
                        "execution.",
                    ["cancelling_does_not_stop_revit"] =
                        "Cancelling the MCP request, or losing the connection, stops YOU WAITING and nothing else. " +
                        "The Revit API cannot interrupt work on its UI thread, so the script runs to completion " +
                        "either way and its result lands in the job record.",
                    ["if_revit_closes"] =
                        "A job whose record has no finish line either is still running or died with the process. " +
                        "horizun_job_status reports that ambiguity rather than resolving it. The in-memory QUEUE is " +
                        "not persisted, but the dispatcher's durable idempotency ledger IS: it never replays a " +
                        "mutation whose outcome is unknown - the key stays in_doubt until a human inspects the model.",
                    ["transaction_policy"] = TransactionPolicy
                });
            }

            UIDocument uidoc = app.ActiveUIDocument;
            // The GATE's document, not whatever is in front now. They are the same
            // document - the gate proved it - and taking it from the gate is what makes
            // that a fact rather than a re-read that could disagree.
            Document doc = gate.Document;

            // An audit line for a capability that can do anything: who, which document, how
            // big, and later how long and how it ended. Never the source itself - that is
            // the caller's content, and a log is not the place for it.
            var auditClock = System.Diagnostics.Stopwatch.StartNew();
            Log.Warn("execute_python RUN by '" + SafeUser(app) + "' on '" + SafeTitle(doc) + "', " +
                     code.Length + " chars");

            JObject warningsBefore = PythonHostObservations.Warnings(doc);
            // Group members a moving script may silently fail to move (Core/PythonGroupMoveWatch.cs).
            PythonGroupMoveWatch groupMoves = PythonGroupMoveWatch.Start(doc, runCode);
            ScriptEngine engine = GetEngine();

            // COMPILE BEFORE ANYTHING CAN TOUCH THE MODEL. Execute() compiles lazily, so a
            // script with a typo used to get as far as the read-only TransactionGroup, the
            // prelude and the job record before the parser said no - and the caller saw an
            // exception with no cause. Compile() parses and compiles and runs nothing, so a
            // syntax error is answered here, as python_error_sintaxis, with no transaction
            // opened and the document untouched.
            ScriptPrecompile.Failure compile = ScriptPrecompile.Check(engine, runCode, source.Path,
                source.Includes.Select(i => new KeyValuePair<string, string>((string)i["code"], (string)i["path"])));
            if (compile != null)
            {
                Log.Warn("execute_python REFUSED before running: the script does not compile");
                JObject compileSummary = ScriptErrorSummary.Build(compile.Formatted, compile.SourceCode, compile.SourcePath);
                JObject compileDetail = ErrorCodes.ScriptFailureDetail(new JObject
                    {
                        ["code"] = "python_syntax_error", ["tool"] = Name,
                        ["executed"] = false, ["write_started"] = false, ["changes_applied"] = false,
                        ["transaction_status"] = "not_started"
                    }, true, null, compile.ErrorType, false);
                compileDetail["script_error"] = compileSummary;
                if (compileSummary != null) compileDetail["error_help"] = PythonErrorHelpData.Build(compileSummary, compile.SourceCode);
                JObject compileAdvice = PythonErrorAdvice.Find(compile.Formatted, revitYear, (int?)compileSummary?["line"]);
                if (compileAdvice != null) compileDetail["advice"] = compileAdvice;
                compileDetail["compat_rewrites"] = compat.Rewrites;
                return CommandResult.FailWithDetail(
                    (ScriptErrorSummary.Render(compileSummary) ?? "") +
                    "The script does not compile, so nothing ran and the model was not touched: " + compile.Formatted,
                    compileDetail);
            }

            s_runBegan = true; // compiled: the script is about to run
            ScriptScope scope = engine.CreateScope();
            scope.SetVariable("__hz_import_revit", true);
            scope.SetVariable("uiapp", app);
            scope.SetVariable("app", app.Application);
            // pyRevit and RevitPythonShell both call the UIApplication `__revit__`, so it
            // is the first thing anybody coming from either types - and it answered
            // "NameError: name '__revit__' is not defined", which reads like the bridge
            // has no application object at all. It has: `app` (Application) and `uiapp`
            // (UIApplication) were always here. The alias costs one line and removes a
            // wrong turn nobody could have avoided by reading the error.
            scope.SetVariable("__revit__", app);
            // The casts are load-bearing on .NET Framework (Revit <= 2024), not decoration.
            // ScriptScope there carries a second overload, SetVariable(string, ObjectHandle),
            // for remoting. An untyped null binds to it — ObjectHandle is more specific than
            // object — and it throws ArgumentNullException("handle") at runtime. A null
            // typed as object binds to the right overload. .NET 8 has no such overload, so
            // this failed only on net48, and only with no document open.
            scope.SetVariable("uidoc", (object)uidoc);
            scope.SetVariable("doc", (object)doc);
            scope.SetVariable("__output__", (object)null);

            // ARGUMENTS, as ONE variable holding JSON TEXT.
            //
            // A promoted script is a script somebody calls WITH something, and until this
            // existed there was no way to pass it: the only inputs were the source itself
            // and the active document. Callers worked around that by generating a new
            // script per call, which defeats the hash that makes a promoted generation
            // attributable in the first place.
            //
            // ONE NAME, so a caller whose argument is called `doc` cannot shadow the name
            // every Revit script expects. TEXT, so there is no JSON-to-Python conversion
            // with opinions about integers, nulls and nested objects - every one of those
            // opinions is a place where what the caller sent is not what the script reads.
            // The script calls json.loads itself, in its own runtime, with its own rules.
            //
            // ALWAYS BOUND, even when empty: a script that reads it must not have to ask
            // whether it exists, and `{}` is the honest value for "no arguments".
            JObject scriptArguments = request["arguments"] as JObject;
            scope.SetVariable("HORIZUN_ARGS_JSON",
                (scriptArguments ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None));

            // A record of the run that survives the run. Long scripts are exactly where
            // the caller cannot ask "how far along?" - the pipe is busy being this call -
            // and exactly where a Revit crash costs the most.
            // Ambient when the async dispatcher already opened one for this work, so the
            // caller's job_id is the record the script's checkpoints land in. Null on the
            // synchronous path, where this opens its own exactly as before.
            //
            // BEST EFFORT here, and durable on the async path above. The difference is
            // which channel carries the answer: a synchronous run replies over the pipe,
            // so an unopenable record costs a progress log and nothing else, and failing
            // the command over it would be the worse trade.
            // Synchronously the record is the CALL's (Core/CallProgress.cs): obtained under the
            // lock a timeout takes, so the job_id a timed-out caller is handed is the record
            // this script's checkpoint() calls write to, and the dispatcher closes it.
            CallProgress call = Job.Ambient == null ? CallProgress.Current : null;
            Job job = Job.Ambient ?? (call != null ? call.OpenForCommand(() => Job.StartBestEffort(Name)) : null) ?? Job.StartBestEffort(Name);
            // Whoever OPENED the record closes it. On the async path the dispatcher owns
            // it and writes the result before the finish line; finishing here as well
            // would put two finish events in one record and land the result after the
            // close, which is the ordering job_status is allowed to rely on.
            bool ownsJob = Job.Ambient == null && !(call != null && ReferenceEquals(call.Record, job));
            // On the synchronous path the work starts the instant the record opens, so
            // the two happen together. On the async path the dispatcher marked it
            // running when it took the entry, and MarkRunning is idempotent - the first
            // transition is the true one.
            job.MarkRunning();
            RequestGate.AttachProgress(() => new JObject
            {
                ["job_id"] = job.Id, ["checkpoint_count"] = job.Checkpoints,
                ["last_checkpoint"] = job.LastCheckpoint, ["record_complete"] = job.RecordIsComplete,
                ["last_done"] = job.LastDone, ["last_total"] = job.LastTotal,
                ["host_verified"] = false,
                ["means"] = "Script checkpoints report progress, not committed or verified model changes."
            });
            scope.SetVariable("__hz_job", job);

            // THE WATCHER OF THIS COMMAND, borrowed from the dispatcher (5.25). Two things
            // come of holding it here: the script can ASK what Revit has raised so far,
            // and everything Revit raises gets stamped with the script's own last
            // checkpoint - so a batch that checkpoints per model can say WHICH model a
            // cancelled dialog belonged to. Null if the dispatcher could not subscribe;
            // that is reported as "not observed", never as "nothing happened".
            Interference watch = Interference.Current;
            if (watch != null) watch.Locator = () => job.LastCheckpoint;

            // What Revit has raised so far, as JSON, from index `since`. Null - never an
                // empty list - when either channel was not subscribed OR a subscribed
                // handler failed while processing: an empty list would read as "Revit
                // raised nothing", which partial observation can never establish.
            scope.SetVariable("__hz_raised", (Func<int, string>)(since =>
            {
                Interference w = Interference.Current;
                if (w == null || !w.FullyObserved) return null;
                return w.Since(since).ToString(Newtonsoft.Json.Formatting.None);
            }));

            // Widen the dialog answer around ONE call, and no further. Deliberately not a
            // whole-script switch: "dismiss" answers OK to every dialog, and Revit's
            // close-with-changes dialog reads OK as SAVE - a read-only audit could write
            // to 250 models by asking for one open to continue. Scoped, it is the same
            // mechanism open_document uses, aimed at the same one call.
            scope.SetVariable("__hz_dialog_scope", (Func<string, IDisposable>)(answer =>
            {
                string parseError;
                DialogAnswer parsed = OpenDialogPolicy.Parse(answer, out parseError);
                if (parseError != null)
                    throw new ArgumentException(
                        "dialog_answer() takes 'cancel' or 'dismiss' (got '" + answer + "'). Nothing was " +
                        "changed about how dialogs are answered, and the default stands: cancel.");
                return Interference.WithDialogAnswer(parsed);
            }));

            string printed = null;
            string printedCaptureError = null;
            object output = null;
            string error = null;
            Exception scriptException = null;
            string failurePhase = "bridge_prologue";
            bool leftModifiable = false;
            Transaction autoTransaction = null;
            string autoTransactionOutcome = null;
            JArray transactionsClosed = new JArray();
            PythonStreamRestorer streamRestorer = PythonStreamRestorer.Capture(engine);

            // read_only=true: wrap the whole run in a TransactionGroup this handler
            // always rolls back, no matter what the script does inside it. The script
            // still opens and commits its own Transaction(s) exactly as it would
            // normally - that is what a nested transaction inside a group is for - and
            // rolling back the GROUP discards everything committed inside it. Measured
            // BEFORE the group opens, so the comparison after rollback has a real
            // baseline rather than a baseline taken after this handler already touched
            // the document.
            TransactionGroup readOnlyGroup = null;
            bool? readOnlyModifiedBefore = null;
            int? readOnlyInstancesBefore = null, readOnlyTypesBefore = null;
            if (readOnly)
            {
                try { readOnlyModifiedBefore = doc.IsModified; } catch { }
                CheapCensus(doc, out readOnlyInstancesBefore, out readOnlyTypesBefore);
                readOnlyGroup = new TransactionGroup(doc, rehearsal
                    ? "Horizun: python rehearsal (always rolled back)"
                    : "Horizun: read-only python (always rolled back)");
                readOnlyGroup.Start();
            }
            RehearsalTracker rehearsalTracker = RehearsalTracker.Start(app.Application, doc);

            // THE STOP SWITCH of this run. The time limit counts from HERE - when the script
            // starts running on the UI thread, not while it waited in the queue - and a
            // cancellation from another thread (the control message for this job id) reaches
            // the script through the same object, at its next checkpoint()/safe_point().
            var runControl = new PythonRunControl(deadlineLimit ?? timeoutLimit);
            // INT-7 m-4: the owner's sync grants as they are BEFORE the script; after it, a difference is REPORTED (settings_changed), never undone.
            Horizun.Revit.Core.Settings.SyncGrantPhoto grantsBefore = Horizun.Revit.Core.Settings.PhotoSyncGrants();
            JArray settingsChanged = null;
            PythonRunRegistry.Register(job.Id, runControl);
            // A synchronous call is cancelled by its request id, not by a job id its caller never saw (review M2).
            string wireKey = PythonRunRegistry.WireKey(RequestGate.CurrentWireId);
            if (wireKey != null) PythonRunRegistry.Register(wireKey, runControl);
            scope.SetVariable("__hz_control", runControl);

            // documentation_only=true: the same kind of group, but KEPT (assimilated) when the
            // diff of the run is documentation and rolled back, verified, when it is not.
            TransactionGroup docGroup = null;
            DocumentationOnlyTracker docTracker = null;
            if (documentationOnly)
            {
                docGroup = new TransactionGroup(doc, "Horizun: documentation-only python");
                if (docGroup.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Documentation-only transaction group did not start; script did not run.");
                docTracker = DocumentationOnlyTracker.Start(app.Application, doc);
            }
            // A script that saves, closes, opens, synchronizes or (un)loads a link cannot run
            // inside a TransactionGroup: Revit refuses those calls while any transaction phase
            // started by an API client is open ("Operation is not permitted when there is any
            // open transaction phase"). Such a script runs WITHOUT the group, and the reply says
            // so: a stop at a checkpoint then has no group to roll back.
            List<string> groupEscapingCalls = (!readOnly && !documentationOnly)
                ? masked().SelectMany(ReadOnlyPythonGuard.Violations).Distinct().ToList()
                : new List<string>();
            // documentation_only has its own group, opened above: only a normal write that
            // calls nothing Revit refuses inside a group opens this one.
            if (!readOnly && !documentationOnly && groupEscapingCalls.Count == 0)
            {
                docGroup = new TransactionGroup(doc, "Horizun: python");
                if (docGroup.Start() != TransactionStatus.Started)
                    throw new InvalidOperationException("Python transaction group did not start; script did not run.");
            }

            try
            {
                // checkpoint(), revit_raised() and dialog_answer(), plus the stdout
                // capture. It is Python, so it lives in ScriptPrelude.cs where a real
                // engine parses it in the test suite - a syntax error here breaks every
                // execute_python call on the machine and no C# compiler would see it.
                // The module is shipped beside the trusted recipes; importing it here
                // makes the same version guards available as a global in caller scripts.
                engine.Execute("import hz_compat", scope);
                engine.Execute(ScriptPrelude.Prologue, scope);

                failurePhase = "include";
                foreach (var include in source.Includes)
                    SourceFor(engine, (string)include["code"], (string)include["path"]).Execute(scope);

                ScriptSource script = SourceFor(engine, runCode, source.Path);
                // A script that WRITES without opening any transaction would die on its
                // first write ("Attempt to modify the model outside of transaction").
                // The bridge runs it inside one transaction it owns: committed when the
                // script ends cleanly, rolled back when it throws (Core/PythonCompat.cs
                // AutoTransactionMarkers says which scripts qualify).
                if (autoTransactionMarkers.Count > 0 && !doc.IsModifiable)
                {
                    failurePhase = "automatic_transaction_start";
                    autoTransaction = new Transaction(doc, "Horizun: script (automatic transaction)");
                    if (autoTransaction.Start() != TransactionStatus.Started)
                    {
                        autoTransaction.Dispose();
                        autoTransaction = null;
                        autoTransactionOutcome = "not_started";
                    }
                }
                failurePhase = "script";
                script.Execute(scope);
                scope.TryGetVariable("__output__", out output);
                if (autoTransaction != null)
                {
                    failurePhase = "automatic_transaction_commit";
                    TransactionStatus committed = autoTransaction.Commit();
                    autoTransactionOutcome = committed == TransactionStatus.Committed ? "committed" : "not_committed:" + committed;
                    if (committed != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "The bridge's automatic transaction did not report Committed (status " + committed + "). " +
                            "Inspect the transaction outcome; the final state is not asserted by this exception.");
                }
            }
            catch (Exception ex)
            {
                if (autoTransaction != null)
                {
                    try
                    {
                        if (autoTransaction.GetStatus() == TransactionStatus.Started)
                        {
                            Guard.RollbackResult rolled = Guard.RollBack(autoTransaction);
                            autoTransactionOutcome = rolled.Confirmed ? "rolled_back" : "rollback_unconfirmed:" + rolled.StatusName;
                        }
                    }
                    catch { autoTransactionOutcome = "rollback_unconfirmed"; }
                }
                scriptException = ex;
                try { error = engine.GetService<ExceptionOperations>().FormatException(ex); }
                catch { error = ex.Message; }
            }
            finally
            {
                try { settingsChanged = Horizun.Revit.Core.Settings.SyncGrantsChangedSince(grantsBefore); } catch { }
                // A script of an async plan ends without ending the plan's job (its stop key stays); any other script's key is released (INT-7 m-e).
                PythonRunRegistry.ChildEnded(job.Id);
                // Not Unregister: the request closes later (RequestGate.Complete), and a cancel in between is "finished" (m-6).
                if (wireKey != null) PythonRunRegistry.Finish(wireKey);
                try
                {
                    engine.Execute(ScriptPrelude.Epilogue, scope);
                    object p;
                    if (scope.TryGetVariable("__hz_printed", out p) && p != null) printed = p.ToString();
                    object captureProblem;
                    if (scope.TryGetVariable("__hz_capture_error_text", out captureProblem) && captureProblem != null)
                        printedCaptureError = captureProblem.ToString();
                }
                catch (Exception cleanupEx)
                {
                    // Do not convert loss of stdout capture into proof that restoration ran.
                    // The authoritative C# references are restored in the nested finally
                    // below, independently of whether this best-effort capture succeeded.
                    printedCaptureError = "Python stdout/stderr cleanup failed: " + cleanupEx.Message;
                    Log.Error("execute_python could not prove stdout/stderr restoration", cleanupEx);
                }
                finally
                {
                    string restorationError;
                    if (!streamRestorer.TryRestore(out restorationError))
                    {
                        string message = "Python stdout/stderr restoration failed: " + restorationError;
                        printedCaptureError = printedCaptureError == null
                            ? message
                            : printedCaptureError + "; " + message;
                        Log.Error(message, null);
                    }
                }

                // A script that leaves the document modifiable has left a transaction open,
                // and every later command will fail with "Modification of the document is
                // forbidden". This used to open a NEW Transaction and roll THAT back, which
                // does nothing to the orphan - the API gives no handle on a transaction this
                // code did not open. It looked like a cure and was a gesture.
                //
                // So: detect it, say so, and make the command FAIL. A caller told the truth
                // can close Revit or undo by hand; a caller told "executed: true" cannot.
                //
                // Since the compatibility pass every Transaction the script constructs is
                // tracked (_hz_new_tx), so the ones it left Started are rolled back here,
                // innermost first, and named. The document is clean again; the script's
                // work in them is still lost, which is why it still fails (below).
                try { autoTransaction?.Dispose(); } catch { }
                try
                {
                    engine.Execute("__hz_rollback_open()", scope);
                    object closedJson;
                    engine.Execute("import json as __hz_j2\n__hz_tx_closed_json = __hz_j2.dumps(__hz_tx_closed)", scope);
                    if (scope.TryGetVariable("__hz_tx_closed_json", out closedJson) && closedJson != null)
                        transactionsClosed = JArray.Parse(closedJson.ToString());
                }
                catch (Exception ex) { Log.Error("execute_python: could not roll back the script's open transactions", ex); }
                try
                {
                    // A script that CLOSED the document it was aimed at (it activated another one
                    // first) leaves a dead handle: IsModifiable would throw and read as "left a
                    // transaction open". A closed document has no open transaction.
                    if (doc != null && doc.IsValidObject && doc.IsModifiable)
                    {
                        leftModifiable = true;
                        Log.Error("execute_python left the document MODIFIABLE - an open transaction it owns", null);
                    }
                }
                catch (Exception ex)
                {
                    // Even the check failed. That is not evidence of a clean document.
                    leftModifiable = true;
                    Log.Error("execute_python: could not determine whether the document was left modifiable", ex);
                }

                // The closure holds this run's job record; the watcher outlives the command
                // by a few lines in the dispatcher, and nothing after this point is "the
                // script's position".
                if (watch != null) watch.Locator = null;
            }

            // Close the record either way. A job with no finish line is indistinguishable
            // from one whose process died, and horizun_job_status refuses to guess between
            // the two — so leaving it open on a normal exit would manufacture that doubt.
            // An open transaction is a failure even when the script itself "succeeded":
            // the document is now poisoned for every later command, and reporting success
            // hands that surprise to whoever calls next.
            if (leftModifiable && error == null)
                error = "The script finished but left the document MODIFIABLE - it opened a transaction and never " +
                        "committed or rolled it back, so WHATEVER IT WROTE IS GONE. Measured on Revit 2026: when " +
                        "this handler returns, Revit ends that transaction itself and its status becomes " +
                        "RolledBack - the transaction object survives, reports HasEnded, and the document is " +
                        "modifiable again by the next call. So the document is NOT left poisoned; the script's " +
                        "work is simply lost, which is why this is a failure and not a warning. " +
                        "Nothing here rolled it back and nothing here could: the Revit API gives no handle on a " +
                        "transaction opened by other code. Do not rely on Revit's cleanup - it is a host behaviour " +
                        "this command observes, not a guarantee it offers. Commit or RollBack in a finally.";

            // Transactions the script left open and the bridge rolled back at the end: the
            // document is clean, the work in them is lost - a failure, named precisely.
            var leftOpen = transactionsClosed.OfType<JObject>()
                .Where(t => ((string)t["why"] ?? "").StartsWith("end of script", StringComparison.Ordinal)).ToList();
            if (leftOpen.Count > 0 && error == null)
                error = "The script finished with " + leftOpen.Count + " transaction(s) still open (" +
                        string.Join(", ", leftOpen.Select(t => (string)t["kind"] + " '" + (string)t["name"] + "'")) +
                        "). The bridge rolled them back, so the document is usable, but WHAT THEY WROTE WAS NOT KEPT. " +
                        "Commit them in the script - `with horizun.transaction('what it does'):` commits on success " +
                        "and rolls back on error.";

            // read_only's own finish line. Runs AFTER the leftModifiable check above, on
            // purpose: rolling back a TransactionGroup while a Transaction it contains is
            // still open is not something this add-in will attempt, so leftModifiable and
            // rolled_back are mutually exclusive by construction, never raced.
            if (runControl.LimitSeconds.HasValue && runControl.Kind == null) runControl.Poll();
            bool interrupted = error != null && runControl.Kind != null &&
                               (error.IndexOf("HorizunInterrupted", StringComparison.Ordinal) >= 0 ||
                                error.IndexOf("HorizunCancelled", StringComparison.Ordinal) >= 0);
            groupMoves?.Capture(doc);
            rehearsalTracker?.StopBeforeRollback();
            JObject readOnlyCheck = readOnly
                ? FinishReadOnly(doc, readOnlyGroup, leftModifiable, readOnlyModifiedBefore, readOnlyInstancesBefore, readOnlyTypesBefore)
                : null;
            if (readOnly && readOnlyCheck.Value<bool>("ok") == false)
            {
                string readOnlyNote = "READ_ONLY GUARANTEE NOT CONFIRMED: " + readOnlyCheck.Value<string>("note") +
                    " Treat this run as a REAL write until you check the model, not as a rehearsal.";
                error = error == null ? readOnlyNote : error + " " + readOnlyNote;
            }

            // An interruption is its own outcome, not a plain failure: the run was stopped on
            // purpose at a safe point, and its open transactions were rolled back.
            bool normalGroupRolledBack = false;
            if (!readOnly && !documentationOnly && docGroup != null)
            {
                try
                {
                    if (interrupted || leftModifiable)
                        normalGroupRolledBack = Guard.RollBack(docGroup).Confirmed;
                    else if (docGroup.Assimilate() != TransactionStatus.Committed)
                        error = (error ?? "") + " Python transaction group did not assimilate.";
                }
                catch (Exception ex) { error = (error ?? "") + " Python transaction group could not close: " + ex.Message; }
            }

            // documentation_only's own finish line: judge the diff of the run while the group is
            // still open, then keep it (assimilate) or roll it back and prove the rollback.
            JObject documentationOnlyCheck = null;
            bool documentationViolation = false;
            if (documentationOnly)
            {
                documentationOnlyCheck = FinishDocumentationOnly(docGroup, docTracker, leftModifiable, interrupted, error != null, out string docOnlyFailure);
                if (docOnlyFailure != null)
                {
                    documentationViolation = true;
                    error = error == null ? docOnlyFailure : error + " " + docOnlyFailure;
                }
            }

            if (ownsJob)
                job.Finish(!interrupted ? (error == null ? "ok" : "failed")
                                        : runControl.Kind == PythonRunControl.TimedOut ? "timed_out" : "cancelled_running", error);
            Log.Warn("execute_python " + (error == null ? "ok" : "FAILED") + " in " +
                     auditClock.ElapsedMilliseconds + " ms on '" + SafeTitle(doc) + "'" +
                     (leftModifiable ? " - LEFT THE DOCUMENT MODIFIABLE" : ""));

            // Bounded before it goes anywhere. A loop printing per element produces tens of
            // megabytes that have to cross the pipe and sit in the server before a client
            // renders none of it - and the cap is declared inside the text, so nobody reads
            // a cut-off tail and concludes the loop finished.
            printed = ScriptOutput.Clamp(printed);

            // WHAT REVIT RAISED, AS STRUCTURE, on the way out (5.25). The dispatcher also
            // attaches it beside every reply as revit_said - but on the success path that
            // rides in the human text only, and a batch driver reads structuredContent.
            // Three models were reported unauditable with no cause for exactly this
            // reason: the dialog record existed and never reached the program that needed
            // it. Here it is a field, on success and on failure both.
            JObject raised = RaisedBlock(watch);
            JObject nonPreemptibleFailure = runControl.NotPreemptible(job.Checkpoints, timeoutLimit);
            if (error != null && nonPreemptibleFailure != null) raised["not_preemptible"] = nonPreemptibleFailure;
            raised["stdout_capture_error"] = printedCaptureError == null
                ? (JToken)null
                : printedCaptureError;

            raised["warning_delta"] = PythonHostObservations.Delta(warningsBefore, PythonHostObservations.Warnings(doc));
            if (settingsChanged != null) raised[Horizun.Revit.Core.Settings.SettingsChangedField] = settingsChanged;
            raised["read_only"] = readOnly;
            raised["read_only_check"] = readOnlyCheck;
            raised["documentation_only"] = documentationOnly;
            if (groupEscapingCalls.Count > 0)
                raised["transaction_group"] = new JObject
                {
                    ["opened"] = false,
                    ["reason"] = "the script calls an API that Revit refuses inside a transaction group",
                    ["calls"] = new JArray(groupEscapingCalls),
                    ["checkpoint_rollback"] = "unavailable: what the script committed stays committed"
                };
            if (documentationOnlyCheck != null) raised["documentation_only_check"] = documentationOnlyCheck;
            bool groupRolled = readOnly ? readOnlyCheck?.Value<bool?>("rolled_back") == true
                : documentationOnly ? (string)documentationOnlyCheck?["outcome"] == "rolled_back_interrupted" : normalGroupRolledBack;
            JObject observedRollback = rehearsalTracker.Report(groupRolled);
            JObject rehearsalReport = rehearsal ? observedRollback : null;
            if (rehearsalReport != null) raised["rehearsal"] = rehearsalReport;
            if (error != null)
            {
                raised["code"] = "python_execution_failed";
                raised["tool"] = Name;
                raised["write_started"] = null;
                raised["changes_applied"] = null;
                raised["transaction_status"] = leftModifiable ? "left_open" : "not_modifiable";
                raised["compat_rewrites"] = compat.Rewrites;
                raised["auto_transaction"] = AutoTransactionReport(autoTransactionMarkers, readOnly, autoTransactionOutcome);
                raised["transactions_closed_by_bridge"] = transactionsClosed;
                raised["failure_phase"] = failurePhase;
                bool bridgeFailure = failurePhase == "bridge_prologue" || failurePhase.StartsWith("automatic_transaction_");
                JObject scriptError = scriptException != null && !bridgeFailure ? ScriptErrorSummary.Build(error, runCode, source.Path) : null;
                if ((string)scriptError?["type"] == "AttributeError" || (string)scriptError?["type"] == "NameError" ||
                    ((string)scriptError?["type"] == "TypeError" &&
                     ((string)scriptError?["message"] ?? "").StartsWith("expected ", StringComparison.Ordinal)) ||
                    scriptError?["signature_check"] != null)
                {
                    // Diagnostics must never replace the original failure. Restrict
                    // reflection to the Revit API assemblies already in this process.
                    try
                    {
                        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a =>
                        {
                            string n = a.GetName().Name;
                            return n == "RevitAPI" || n == "RevitAPIUI" || n == "RevitAPIIFC";
                        });
                        var apiTypes = ApiHelpRules.PublicTypes(assemblies);
                        ScriptErrorSummary.AttachAttributeApiHelp(scriptError, apiTypes, app.Application.VersionNumber);
                        ScriptErrorSummary.AttachEnumApiHelp(scriptError, runCode, apiTypes, app.Application.VersionNumber);
                        ScriptErrorSummary.AttachNameApiHelp(scriptError, apiTypes, app.Application.VersionNumber);
                        ScriptErrorSummary.AttachSignatureApiHelp(scriptError, apiTypes, app.Application.VersionNumber);
                        ScriptErrorSummary.AttachExpectedTypeApiHelp(scriptError, apiTypes, app.Application.VersionNumber);
                    }
                    catch (Exception ex) { Log.Error("execute_python: API suggestion unavailable", ex); }
                }
                raised["script_error"] = scriptError;
                if (scriptError != null) raised["error_help"] = PythonErrorHelpData.Build(scriptError, runCode);
                if (scriptException != null && !bridgeFailure) PythonErrorAdvice.Attach(raised, error, revitYear);
                // THE CAUSE AS DATA: the script's own error, the Revit API's, or a transaction
                // Revit threw away. Never the message, only the code and the type NAME.
                string revitApiType = RevitApiTypeOf(scriptException);
                string scriptType = ErrorCodes.PythonTypeFromTraceback(error) ?? ErrorCodes.TypeNameOf(scriptException);
                raised = ErrorCodes.ScriptFailureDetail(raised, scriptException is Microsoft.Scripting.SyntaxErrorException,
                                                        revitApiType, scriptType, leftModifiable, bridgeFailure);
                string interruptionText = "";
                if (interrupted)
                {
                    var lineMatch = System.Text.RegularExpressions.Regex.Match(error ?? "", @"line\s+(\d+)");
                    int lineNumber;
                    raised["cancelled_at_checkpoint"] = RehearsalRules.CancellationReport(observedRollback,
                        lineMatch.Success && int.TryParse(lineMatch.Groups[1].Value, out lineNumber) ? (int?)lineNumber : null,
                        runControl.ElapsedWallSeconds);
                    int autoRolled = autoTransactionOutcome == "rolled_back" ? 1 : 0;
                    bool? groupRolledBack = (bool)raised["cancelled_at_checkpoint"]["rolled_back_verified"];
                    raised["interrupted"] = runControl.Describe(
                        job.LastCheckpoint,
                        leftModifiable ? "left_open" : (autoRolled + transactionsClosed.Count > 0 ? "rolled_back" : "none_open_at_stop"),
                        autoRolled + transactionsClosed.Count,
                        readOnly ? "read_only" : documentationOnly ? "documentation_only" : "normal", groupRolledBack);
                    raised["changes_applied"] = (bool)raised["cancelled_at_checkpoint"]["rolled_back_verified"] ? (JToken)false : null;
                    if (runControl.Kind == PythonRunControl.TimedOut) raised[ErrorCodes.CodeField] = ErrorCodes.TiempoAgotado;
                    interruptionText = "SCRIPT INTERRUPTED (" + runControl.Kind + ": " + runControl.Reason + ") at '" +
                                       (job.LastCheckpoint ?? "start") + "'. Transactions still open were rolled back; " +
                                       "See cancelled_at_checkpoint for the rollback evidence and its limits." +
                                       "\n";
                }
                PythonRunControl.StampChangesAppliedScope(raised);
                if (documentationViolation)
                {
                    // The cause the caller must act on is the refusal, not the script's own outcome.
                    raised[ErrorCodes.CodeField] = ErrorCodes.DocumentacionViolada;
                    raised["offending_ids"] = documentationOnlyCheck?["offending_ids"];
                    bool kept = documentationOnlyCheck?.Value<bool?>("rollback_verified") == true;
                    raised["changes_applied"] = kept ? (JToken)false : null;
                    raised["transaction_status"] = kept ? "rolled_back" : "rollback_unconfirmed";
                    PythonRunControl.StampChangesAppliedScope(raised);
                }
                if (responseMode == "compact") PythonReplyCompaction.Compact(raised);
                return CommandResult.FailWithDetail(
                    interruptionText + (ScriptErrorSummary.Render(scriptError) ?? "") +
                    error + (string.IsNullOrEmpty(printed) ? "" : "\n--- stdout before the error ---\n" + printed),
                    raised);
            }

            // A dict assigned to __output__ used to come back as the string
            // "IronPython.Runtime.PythonDictionary": ToString() on a value that does not
            // override it, published as if it were the data. Now it is serialized, and
            // when it cannot be, the reply SAYS the structure was lost.
            // The full output is saved under the job (<jobs>\outputs\<job_id>.<page_chars>.json) so that
            // horizun_job_status output_page=N can read it in pages without Revit, and so that it
            // expires with its job record. No job record on disk: a temp file as before. Written to a
            // temp name and renamed, so a reader never meets half a file.
            bool savedByJob = !string.IsNullOrEmpty(job.Path) && OutputPages.ValidJobId(job.Id);
            Func<string, string, string> writeOutput = (json, jobFileName) =>
            {
                string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Horizun", "python-output");
                string file = Guid.NewGuid().ToString("N") + ".json";
                if (savedByJob)
                {
                    directory = OutputPages.Folder(System.IO.Path.GetDirectoryName(job.Path));
                    file = jobFileName;
                }
                Directory.CreateDirectory(directory);
                string path = System.IO.Path.Combine(directory, file);
                string temp = System.IO.Path.Combine(directory, System.IO.Path.GetFileNameWithoutExtension(file) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                try
                {
                    File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
                    if (File.ReadAllText(temp, System.Text.Encoding.UTF8) != json) throw new IOException("Output file verification failed.");
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temp, path);
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
                return path;
            };
            ScriptOutputRendering rendered = ScriptOutput.Render(output, Horizun.Contracts.Contract.MaxScriptTextChars,
                json => writeOutput(json, OutputPages.FileName(job.Id ?? "", outputPageChars)),
                outputPageChars,
                // Above what output_page reads (4 MB) the output is still saved whole: <job_id>.full.json (counted and
                // deleted with its job by the retention, never read by pages).
                json => writeOutput(json, OutputPages.FullFileName(job.Id ?? "")));
            if (rendered.Kind == "paged" && savedByJob)
                rendered.Note += " Next pages: horizun_job_status job_id=" + job.Id + " output_page=2 (and so on), or read that file.";

            // What the script CLAIMS, classified without crediting it. The ceiling for
            // arbitrary code is self_reported_verified. Limited ID observations below
            // do not verify script intent or geometry and cannot raise that ceiling.
            // A paged output is classified from the COMPLETE value, never from its first page.
            JToken outputWhole = rendered.FullValue ?? rendered.Value;
            EvidenceReport evidence = ScriptEvidence.Classify(outputWhole);

            var response = JObject.FromObject(new
            {
                mode = "sync",
                executed = true,
                source = source.Describe(),
                // Beside __output__, exactly where a program looking for the cause of
                // "Opening was canceled" will find it.
                dialogs = raised["dialogs"],
                failures = raised["failures"],
                revit_raised_observed = raised["revit_raised_observed"],
                revit_raised_note = raised["revit_raised_note"],
                dialogs_observed = raised["dialogs_observed"],
                failures_observed = raised["failures_observed"],
                observation_complete = raised["observation_complete"],
                observation_note = raised["observation_note"],
                dialogs_subscribed = raised["dialogs_subscribed"],
                failures_subscribed = raised["failures_subscribed"],
                dialogs_processing_complete = raised["dialogs_processing_complete"],
                failures_processing_complete = raised["failures_processing_complete"],
                observer_errors = raised["observer_errors"],
                output = rendered.Value,
                output_kind = rendered.Kind,
                output_note = rendered.Note,
                stdout_capture_error = printedCaptureError,
                evidence_status = evidence.Status,
                // The status the SCRIPT declared, kept verbatim so classifying it never
                // destroys information.
                script_reported_status = evidence.ScriptReportedStatus,
                // Always false for Python, and stated rather than implied: a client
                // scanning for a verification signal must find an explicit "no".
                host_verified = evidence.HostVerified,
                evidence_summary = evidence.Summary,
                evidence_structured = evidence.Structured,
                evidence_warnings = evidence.Warnings,
                evidence_contract =
                    "self_reported_verified means the SCRIPT declared verified and attached evidence - the " +
                    "bridge did NOT re-read the model to confirm it. completed_unverified means it finished " +
                    "without anything structured to classify (a verified claim with no evidence is downgraded " +
                    "to this). partial means part happened or part verified. failed is the script's own " +
                    "failure report. There is no 'verified' state on this path at all: that word is reserved " +
                    "for typed commands, which the host re-reads after the commit. Recommended __output__ " +
                    "shape: " + ScriptEvidence.ContractShape,
                host_verification_note = ScriptEvidence.HostVerificationDisclaimer,
                typed_alternatives = typedAlternatives.Count == 0 ? null : typedAlternatives,
                printed = string.IsNullOrEmpty(printed) ? null : printed,
                // MEASURED after the script, not assumed. False here means Document.
                // IsModifiable was re-read and came back false; it is reported on the
                // success path too, so a caller never has to infer it from the absence
                // of an error.
                transaction_left_open = leftModifiable,
                transaction_policy = TransactionPolicy,
                // How to watch the next long one from outside: horizun_job_status reads this
                // file straight from disk and does not need Revit to be free to answer.
                job_id = job.Id,
                job_file = job.Path,
                checkpoints = job.Checkpoints
            });
            response["warning_delta"] = raised["warning_delta"];
            response["compat_rewrites"] = compat.Rewrites;
            response["auto_transaction"] = AutoTransactionReport(autoTransactionMarkers, readOnly, autoTransactionOutcome);
            response["transactions_closed_by_bridge"] = transactionsClosed;
            response["read_only"] = readOnly;
            response["read_only_check"] = readOnlyCheck;
            response["documentation_only"] = documentationOnly;
            if (documentationOnlyCheck != null) response["documentation_only_check"] = documentationOnlyCheck;
            if (rehearsalReport != null) response["rehearsal"] = rehearsalReport;
            response["host_observations"] = PythonHostObservations.CreatedIds(doc, outputWhole);
            if (groupMoves != null) response["group_member_moves"] = groupMoves.Report(runCode, outputWhole);
            response["execution_sha256"] = request["execution_sha256"];
            response["runtime"] = new JObject { ["python"] = typeof(IronPython.Hosting.Python).Assembly.GetName().Version.ToString(), ["clr"] = Environment.Version.ToString(), ["helpers_version"] = 1 };
            response["output_truncated"] = rendered.Kind == "too_large";
            response["output_original_chars"] = rendered.OriginalChars;
            response["output_full_path"] = rendered.FullOutputPath;
            if (rendered.PersistenceError != null) response["persistence_error"] = rendered.PersistenceError;
            if (rendered.Kind == "paged")
                response["output_paging"] = new JObject
                {
                    ["page"] = 1, ["pages"] = rendered.Pages, ["page_chars"] = rendered.PageChars, ["total_chars"] = rendered.OriginalChars,
                    ["last_page"] = false, ["full_path"] = rendered.FullOutputPath,
                    ["next_page_with"] = savedByJob ? "horizun_job_status job_id=" + job.Id + " output_page=2" : null
                };
            // Compact (the default) carries counts and the warnings this script added or
            // resolved, bounded, and refers to the contract text instead of repeating it
            // (field report: ~127,000 characters for a few lines of __output__). Full is
            // unchanged. See Core/PythonReplyCompaction.cs.
            if (responseMode == "compact") PythonReplyCompaction.Compact(response);
            if (runControl.LimitSeconds.HasValue || runControl.Kind != null)
            {
                JObject notPreemptible = runControl.NotPreemptible(job.Checkpoints, timeoutLimit);
                if (notPreemptible != null) response["not_preemptible"] = notPreemptible;
                // The limit and what became of it, so "no safe point was ever reached" is a
                // finding rather than silence: the script ran past its limit and nothing could stop it.
                response["run_control"] = new JObject
                {
                    ["timeout_s"] = runControl.LimitSeconds.HasValue ? (JToken)runControl.LimitSeconds.Value : JValue.CreateNull(),
                    ["elapsed_s"] = Math.Round(runControl.ElapsedSeconds, 3),
                    ["exceeded"] = runControl.Kind == PythonRunControl.TimedOut,
                    ["tripped"] = runControl.Kind,
                    ["stopped_the_script"] = false,
                    ["note"] = runControl.Kind == null
                        ? "Finished inside its limit."
                        : "The limit or a cancellation tripped, but the script finished anyway: it never reached a " +
                          "checkpoint()/safe_point() after that, or it swallowed HorizunInterrupted. Its work was kept."
                };
            }
            if (responseShape == "output_only" && runControl.Kind == null) response = PythonReplyCompaction.OutputOnly(response);
            return CommandResult.Ok(response);

        }

        /// <summary>
        /// Everything Revit raised during this script, split the way a caller reads it:
        /// DIALOGS (a modal the bridge answered, which is why an open "was canceled") and
        /// FAILURES (the warnings and errors Revit's failure processing produced). The
        /// shaping - including the field that keeps an unobserved run from reading like a
        /// quiet one - is RaisedRecord's, and is unit-tested there.
        /// </summary>
        private static JObject RaisedBlock(Interference watch) => Interference.BlockOf(watch);

        /// <summary>Where the source came from, or exactly why there is none.</summary>
        private sealed class SourceResolution
        {
            public string Code;
            public string Path;                 // the file this source came from, if any
            public bool ReadNow;                // THIS call read it off disk
            public string Encoding;             // null when the source arrived inline
            public bool NewlinesNormalized;
            public JArray Includes;
            public string Error;

            /// <summary>
            /// Reported on every reply, because a caller that passes a path must be able
            /// to confirm WHICH file ran and how it was read - a run that silently used
            /// yesterday's copy of a driver is the failure this field exists to prevent.
            /// </summary>
            public JObject Describe()
            {
                return new JObject
                {
                    ["from"] = Path == null ? "code" : "code_path",
                    ["path"] = Path,
                    ["chars"] = Code == null ? 0 : Code.Length,
                    ["decoded_as"] = Encoding,
                    ["newlines_normalized"] = NewlinesNormalized,
                    ["includes"] = new JArray(Includes.Select(i => new JObject { ["path"] = i["path"], ["sha256"] = i["sha256"] })),
                    ["helpers_version"] = 1,
                    ["read_from_disk_by_this_call"] = ReadNow,
                    ["note"] = "Source is frozen before durable admission. Same key with different source " +
                        "is refused with both hashes; queued work executes its admitted snapshot. " +
                        "SHA-256 covers UTF-8 of the decoded submitted text (file newlines normalized)."
                };
            }
        }

        /// <summary>
        /// Resolve `code` or `code_path` into one source string. Exactly one of them, and
        /// every failure is a sentence naming the file: this path exists because the
        /// alternative - a hand-written stub that reads and compiles the file inside the
        /// script - failed twice with IronPython errors that told a caller nothing.
        /// </summary>
        private static SourceResolution ResolveSource(JObject request)
        {
            var source = PythonSourceSnapshot.Resolve(request);
            return new SourceResolution
            {
                Code = source.Code, Path = source.Path, ReadNow = source.ReadNow,
                Encoding = source.Encoding, NewlinesNormalized = source.NewlinesNormalized,
                Includes = source.Includes,
                Error = source.Error
            };
        }

        /// <summary>
        /// The script source, NAMED. Without the path an IronPython traceback says
        /// "&lt;string&gt;" and a 535-line driver's failure points nowhere; with it, the
        /// traceback names the real file and line, which is the whole difference between
        /// a stack trace you can act on and one you cannot.
        /// </summary>
        /// <summary>The bare name of the first Autodesk.Revit.* exception in the chain, or null.</summary>
        private static string RevitApiTypeOf(Exception ex)
        {
            for (int depth = 0; ex != null && depth < 6; depth++, ex = ex.InnerException)
                if (ErrorCodes.IsRevitApiType(ex.GetType().FullName)) return ErrorCodes.TypeNameOf(ex);
            return null;
        }

        private static ScriptSource SourceFor(ScriptEngine engine, string code, string path)
        {
            var kind = Microsoft.Scripting.SourceCodeKind.Statements;
            if (string.IsNullOrEmpty(path)) return engine.CreateScriptSourceFromString(code, kind);
            return engine.CreateScriptSourceFromString(code, path, kind);
        }

        private static int RevitYearOf(UIApplication app)
        {
            try { return int.Parse(app.Application.VersionNumber, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static HashSet<string> _builtInCategoryNames;

        /// <summary>This Revit's own BuiltInCategory names, read once from the enumeration.</summary>
        private static HashSet<string> BuiltInCategoryNames()
        {
            if (_builtInCategoryNames != null) return _builtInCategoryNames;
            try { _builtInCategoryNames = new HashSet<string>(Enum.GetNames(typeof(BuiltInCategory)), StringComparer.Ordinal); }
            catch { return null; }
            return _builtInCategoryNames;
        }

        private static JObject AutoTransactionReport(List<string> markers, bool readOnly, string outcome = null)
        {
            if (markers == null || markers.Count == 0) return null;
            return new JObject
            {
                ["used"] = outcome != null && outcome != "not_started",
                ["why"] = "The script writes (" + string.Join(", ", markers) + ") but opens no transaction, so the bridge " +
                          "runs it inside one of its own: committed when the script ends cleanly, rolled back when it throws." +
                          (readOnly ? " read_only still rolls everything back afterwards." : ""),
                ["outcome"] = outcome
            };
        }

        private static string SafeUser(UIApplication app)
        {
            try { return app?.Application?.Username; } catch { return null; }
        }

        private static string SafeTitle(Document d)
        {
            try { return d == null ? "(no document)" : d.Title; } catch { return "(title unreadable)"; }
        }

        /// <summary>
        /// Cheap and deliberately shallow: two GetElementCount() calls, no properties of
        /// any element read. It is the same class of measurement DeleteCommand's Census2
        /// takes before/after a delete - a count, not a full model diff - and it is
        /// exactly what read_only's guarantee claims: nothing was added or removed.
        /// </summary>
        private static void CheapCensus(Document doc, out int? instances, out int? types)
        {
            instances = null; types = null;
            try { instances = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount(); } catch { }
            try { types = new FilteredElementCollector(doc).WhereElementIsElementType().GetElementCount(); } catch { }
        }

        /// <summary>
        /// Close out documentation_only: judge the diff the tracker saw, then assimilate the group
        /// (the script's work stays, as one undo step) or roll it back and re-read the proof.
        /// failure is null when the result stands, else the sentence that fails the call.
        /// </summary>
        private static JObject FinishDocumentationOnly(TransactionGroup group, DocumentationOnlyTracker tracker,
                                                       bool leftModifiable, bool interrupted, bool scriptFailed, out string failure)
        {
            failure = null;
            DocumentationOnlyVerdict verdict = tracker.Judge();
            if (scriptFailed && !interrupted)
            {
                verdict.Ok = false;
                verdict.Uncertainties.Add("the script failed before its work could be accepted");
                verdict.Reason = "the script failed before its work could be accepted";
            }
            switch (DocumentationOnlyRules.Decide(leftModifiable, interrupted, verdict.Ok))
            {
                case DocumentationOnlyRules.GroupFate.RollBackAfterOpenTransaction:
                {
                    // Review m8: the group is never left open. Rolling a group back around an open transaction may be
                    // refused by Revit; whichever it answers is what the reply says.
                    bool closed = false;
                    try { closed = group.GetStatus() == TransactionStatus.Started && Guard.RollBack(group).Confirmed; } catch { closed = false; }
                    failure = DocumentationOnlyRules.OpenTransactionNote(closed);
                    return DocumentationOnlyRules.Report(verdict, closed ? DocumentationOnlyRules.OutcomeRolledBackOpenTransaction : DocumentationOnlyRules.OutcomeGroupLeftOpen,
                                                         null, tracker.SnapshotComplete, tracker.SnapshotSize);
                }
                case DocumentationOnlyRules.GroupFate.RollBackInterrupted:
                {
                    // Review M6: a stopped script's partial work is not judged and not kept: the whole group goes, and the
                    // rollback is re-read like every other one. The call already fails because of the interruption.
                    bool rolled = false;
                    try { rolled = Guard.RollBack(group).Confirmed; } catch { rolled = false; }
                    bool? proven = tracker.RollbackProven(rolled);
                    return DocumentationOnlyRules.Report(verdict, rolled ? DocumentationOnlyRules.OutcomeRolledBackInterrupted : "rollback_unconfirmed",
                                                         proven == true, tracker.SnapshotComplete, tracker.SnapshotSize);
                }
                case DocumentationOnlyRules.GroupFate.Assimilate:
                {
                    try
                    {
                        TransactionStatus st = group.Assimilate();
                        if (st == TransactionStatus.Committed)
                            return DocumentationOnlyRules.Report(verdict, "kept", null, tracker.SnapshotComplete, tracker.SnapshotSize);
                        failure = "documentation_only=true: the verified-documentation group did not assimilate (status " + st + ").";
                        try { if (group.GetStatus() == TransactionStatus.Started) Guard.RollBack(group); } catch { }
                    }
                    catch (Exception ex) { failure = "documentation_only=true: assimilating the group threw: " + ex.Message; }
                    return DocumentationOnlyRules.Report(verdict, "not_assimilated", null, tracker.SnapshotComplete, tracker.SnapshotSize);
                }
                default:
                {
                    bool rolled = false;
                    try { rolled = Guard.RollBack(group).Confirmed; } catch { rolled = false; }
                    bool? proven = tracker.RollbackProven(rolled);
                    failure = DocumentationOnlyRules.FailureMessage(verdict, proven == true);
                    return DocumentationOnlyRules.Report(verdict, rolled ? "rolled_back" : "rollback_unconfirmed", proven == true,
                                                         tracker.SnapshotComplete, tracker.SnapshotSize);
                }
            }
        }

        /// <summary>
        /// Close out read_only: roll back the group (unless a transaction the script
        /// opened is still open, in which case rolling back a group around it is not
        /// attempted - see the comment on leftModifiable above), then measure whether
        /// the document actually held still. Never assumes the rollback worked; always
        /// re-reads.
        /// </summary>
        private static JObject FinishReadOnly(Document doc, TransactionGroup group, bool leftModifiable,
            bool? modifiedBefore, int? instancesBefore, int? typesBefore)
        {
            bool rolledBack = false;
            string rollbackError = null;
            if (leftModifiable)
            {
                rollbackError = "the script left a Transaction open, so the read-only TransactionGroup was NOT " +
                    "rolled back here - rolling back a group while a Transaction it contains is still open is not " +
                    "something this add-in will attempt. What happens to it is the same host behaviour named in " +
                    "transaction_policy, MEASURED there only for a bare Transaction, not re-confirmed here for a " +
                    "Transaction nested inside a TransactionGroup - so this run's read_only guarantee is " +
                    "UNVERIFIED, not upheld, and the command fails rather than guessing which one it was.";
            }
            else
            {
                try
                {
                    Guard.RollbackResult result = Guard.RollBack(group);
                    rolledBack = result.Confirmed;
                    if (!rolledBack)
                        rollbackError = "the read-only group's rollback returned " + result.StatusName +
                            ", not RolledBack - it did not confirm.";
                }
                catch (Exception ex) { rollbackError = "rolling back the read-only group threw: " + ex.Message; }
            }

            bool? modifiedAfter = null;
            try { modifiedAfter = doc.IsModified; } catch { }
            int? instancesAfter, typesAfter;
            CheapCensus(doc, out instancesAfter, out typesAfter);

            ReadOnlyOutcome outcome = rolledBack
                ? ReadOnlyPythonGuard.CompareCensus(modifiedBefore, modifiedAfter, instancesBefore, instancesAfter, typesBefore, typesAfter)
                : new ReadOnlyOutcome { Ok = false, Reason = rollbackError };

            return new JObject
            {
                ["rolled_back"] = rolledBack,
                ["is_modified_before"] = modifiedBefore,
                ["is_modified_after"] = modifiedAfter,
                ["census_before"] = new JObject { ["instances"] = instancesBefore, ["types"] = typesBefore },
                ["census_after"] = new JObject { ["instances"] = instancesAfter, ["types"] = typesAfter },
                ["ok"] = outcome.Ok,
                ["note"] = outcome.Reason,
                ["compares"] =
                    "Document.IsModified and a cheap element/type instance census (FilteredElementCollector." +
                    "GetElementCount(), no property of any element read) taken before the read-only " +
                    "TransactionGroup opened and after it rolled back. Equal on both sides is the evidence this " +
                    "run left the MODEL unchanged; it does not prove every parameter value is byte-identical, and " +
                    "it says nothing about files written or network calls made outside the transaction - " +
                    "read_only's guarantee is scoped to the Revit model only."
            };
        }
    }
}
