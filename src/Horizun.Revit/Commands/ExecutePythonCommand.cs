// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Direct Python against the Revit API, on the UI thread. The standing fallback:
// no set of typed tools ever covers the whole API, this does.
//
// DISABLED BY DEFAULT: when a typed command cannot cover a request, the owner may
// grant a bounded exception from Revit's Python ON/OFF button, or configure a
// durable developer opt-in. It runs arbitrary code inside Revit with the full API
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
            "revit_raised(since=0) and dialog_answer(answer). " +
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
            "list is serialized to JSON and output_kind reports scalar|structure|text_only, where text_only " +
            "means the structure could NOT be serialized and only its text rendering survived. The standard " +
            "library is available (json, re, csv, datetime, math). " +
            "TRANSACTIONS: NO rollback is attempted, and none is possible. This text used to say one was " +
            "'attempted, best-effort'; the code that attempted it had already been deleted for opening a NEW " +
            "transaction and rolling THAT back, which does nothing to the orphan. The Revit API gives no handle " +
            "on a transaction opened by other code. What happens instead: Document.IsModifiable is re-read after " +
            "the script, and a document left modifiable makes this command FAIL and says so. Wrap every " +
            "Transaction in try/finally with RollBack in the finally. Prefer a typed command for anything " +
            "recurring: a typed command is verified BY THE HOST, and this can only ever be self-reported.";

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

                foreach (var asm in new[]
                {
                    typeof(Document).Assembly,       // RevitAPI
                    typeof(UIApplication).Assembly,  // RevitAPIUI
                    typeof(System.Uri).Assembly      // System
                })
                {
                    try { eng.Runtime.LoadAssembly(asm); } catch { }
                }

                // Point at the bundled stdlib so `import json` resolves.
                try
                {
                    string here = Path.GetDirectoryName(typeof(ExecutePythonCommand).Assembly.Location);
                    var paths = new List<string>(eng.GetSearchPaths());
                    foreach (string candidate in new[] { Path.Combine(here, "Lib"), here })
                        if (Directory.Exists(candidate) && !paths.Contains(candidate)) paths.Add(candidate);
                    eng.SetSearchPaths(paths);
                }
                catch { }

                _engine = eng;
                return _engine;
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

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            // Checked HERE as well as in the server. The two halves ship separately, so a
            // stale server that still advertises this tool must not be able to run code on
            // a machine whose owner switched it off.
            if (!Horizun.Revit.Core.Settings.ExecutePythonEnabled)
            {
                Log.Warn("execute_python REFUSED: disabled in settings");
                return CommandResult.Fail(Horizun.Revit.Core.Settings.ExecutePythonRefusal());
            }

            JObject request;
            bool runAsync;
            bool preflight;
            string idempotencyKey;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                runAsync = request.Value<bool?>("run_async") ?? false;
                preflight = request.Value<bool?>("preflight") ?? false;
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
            // The DURABLE idempotency claim is the one exception, and it is stated rather
            // than glossed: the dispatcher takes it over the REQUEST before this command
            // runs, and the request names the path, not the bytes. So the same key with an
            // edited file replays the recorded answer instead of running the new script -
            // at-most-once doing its job. A changed script needs a new key, and the
            // response says so.
            SourceResolution source = ResolveSource(request);
            if (source.Error != null) return CommandResult.Fail(source.Error);
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

            // Computed before the gate and before anything is queued, and carried on the
            // response either way: a script that duplicates a typed command's own API
            // call is TOLD where the verified version lives, and still allowed to run -
            // the composite script and the uncovered variant are exactly what this
            // fallback exists for.
            JArray typedAlternatives = TypedAlternatives(code);

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
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;

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
                foreach (JToken advisory in typedAlternatives) warnings.Add(advisory);

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

                string syntaxError = null;
                try
                {
                    ScriptSource compileOnly = SourceFor(GetEngine(), code, source.Path);
                    compileOnly.Compile(); // parse and compile; nothing executes
                }
                catch (Exception ex)
                {
                    try { syntaxError = GetEngine().GetService<ExceptionOperations>().FormatException(ex); }
                    catch { syntaxError = ex.Message; }
                }

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
                    ["would_run"] = syntaxError == null,
                    ["source"] = source.Describe(),
                    ["checks"] = new JObject
                    {
                        ["permission"] = "ok",
                        ["target_document"] = "ok - matches the active document",
                        ["size"] = code.Length + " of " + MaxScriptChars + " chars",
                        ["source"] = source.Path == null
                            ? "ok - inline"
                            : "ok - read from " + source.Path + " as " + source.Encoding,
                        ["syntax"] = syntaxError == null ? "ok" : "failed"
                    },
                    ["syntax_error"] = syntaxError,
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
                    // The RESOLVED source, never the path. A code_path run reads the file
                    // once, HERE, at submit: the deferred run must execute the script the
                    // fingerprint was taken of, not whatever that path holds twenty
                    // minutes later when the queue reaches it.
                    ["code"] = code,
                    ["code_origin_path"] = source.Path,
                    // The dispatcher runs the script itself; a second async hop would
                    // queue it forever.
                    ["run_async"] = false,
                    ["target_document"] = request.Value<string>("target_document")
                                          ?? request.Value<string>("expected_document")
                                          ?? request.Value<string>("target_document_title")
                };

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

            ScriptEngine engine = GetEngine();
            ScriptScope scope = engine.CreateScope();
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
            Job job = Job.Ambient ?? Job.StartBestEffort(Name);
            // Whoever OPENED the record closes it. On the async path the dispatcher owns
            // it and writes the result before the finish line; finishing here as well
            // would put two finish events in one record and land the result after the
            // close, which is the ordering job_status is allowed to rely on.
            bool ownsJob = Job.Ambient == null;
            // On the synchronous path the work starts the instant the record opens, so
            // the two happen together. On the async path the dispatcher marked it
            // running when it took the entry, and MarkRunning is idempotent - the first
            // transition is the true one.
            job.MarkRunning();
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
            bool leftModifiable = false;
            PythonStreamRestorer streamRestorer = PythonStreamRestorer.Capture(engine);

            try
            {
                // checkpoint(), revit_raised() and dialog_answer(), plus the stdout
                // capture. It is Python, so it lives in ScriptPrelude.cs where a real
                // engine parses it in the test suite - a syntax error here breaks every
                // execute_python call on the machine and no C# compiler would see it.
                engine.Execute(ScriptPrelude.Prologue, scope);

                ScriptSource script = SourceFor(engine, code, source.Path);
                script.Execute(scope);
                scope.TryGetVariable("__output__", out output);
            }
            catch (Exception ex)
            {
                try { error = engine.GetService<ExceptionOperations>().FormatException(ex); }
                catch { error = ex.Message; }
            }
            finally
            {
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
                try
                {
                    if (doc != null && doc.IsModifiable)
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

            if (ownsJob) job.Finish(error == null ? "ok" : "failed", error);
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
            raised["stdout_capture_error"] = printedCaptureError == null
                ? (JToken)null
                : printedCaptureError;

            if (error != null)
                return CommandResult.FailWithDetail(
                    error + (string.IsNullOrEmpty(printed) ? "" : "\n--- stdout before the error ---\n" + printed),
                    raised);

            // A dict assigned to __output__ used to come back as the string
            // "IronPython.Runtime.PythonDictionary": ToString() on a value that does not
            // override it, published as if it were the data. Now it is serialized, and
            // when it cannot be, the reply SAYS the structure was lost.
            ScriptOutputRendering rendered = ScriptOutput.Render(output);

            // What the script CLAIMS, classified without crediting it. The ceiling for
            // arbitrary code is self_reported_verified - the host never re-reads the
            // model after Python, so no field here may read as a bridge guarantee.
            EvidenceReport evidence = ScriptEvidence.Classify(rendered.Value);

            return CommandResult.Ok(new
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
                    ["read_from_disk_by_this_call"] = ReadNow,
                    ["note"] = Path == null
                        ? "The source arrived inline."
                        : ReadNow
                            ? "The source was READ FROM DISK AT THIS INSTANT: the size limit and " +
                              "submitted_source_sha256 measure these bytes, not the path. The DURABLE " +
                              "idempotency claim does not - it is taken over the request, and the request names " +
                              "the path - so re-sending the same idempotency_key after editing the file REPLAYS " +
                              "this answer and runs nothing. That is at-most-once doing its job, not a silent " +
                              "re-run; a changed script is new work and needs a new key."
                            : "The source came from the queue, having been read from this path when the job was " +
                              "SUBMITTED. The file is not re-read here, deliberately: a deferred run must execute " +
                              "the script its fingerprint was taken of, not whatever the path holds now. The name " +
                              "is carried so tracebacks and this record can point at the real file."
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
            string code = request.Value<string>("code");
            string path = request.Value<string>("code_path");

            bool hasCode = !string.IsNullOrWhiteSpace(code);
            bool hasPath = !string.IsNullOrWhiteSpace(path);

            if (hasCode && hasPath)
                return new SourceResolution
                {
                    Error = "Both 'code' and 'code_path' were sent. Send exactly ONE: running either of them " +
                            "would be a guess about which script you meant, and the other would be silently " +
                            "ignored. Nothing ran."
                };

            if (!hasCode && !hasPath)
                return new SourceResolution
                {
                    Error = "One of 'code' (the Python source inline) or 'code_path' (a .py file on this machine, " +
                            "read as UTF-8 unless it declares otherwise) is required. Nothing ran."
                };

            // Inline - including the queue's copy of a code_path run, which carries the
            // file it came from so a traceback can still name it.
            if (hasCode)
                return new SourceResolution
                {
                    Code = code,
                    Path = request.Value<string>("code_origin_path"),
                    ReadNow = false
                };

            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch (Exception ex)
            {
                return new SourceResolution
                {
                    Error = "code_path '" + path + "' is not a usable path: " + ex.Message + ". Nothing ran."
                };
            }

            try
            {
                if (!File.Exists(full))
                    return new SourceResolution
                    {
                        Error = "code_path does not exist: " + full +
                                (string.Equals(full, path, StringComparison.OrdinalIgnoreCase)
                                    ? ""
                                    : " (resolved from '" + path + "')") +
                                ". The file is read on the machine RUNNING REVIT, which is this one. Nothing ran."
                    };
            }
            catch (Exception ex)
            {
                return new SourceResolution
                {
                    Error = "code_path " + full + " could not be tested: " + ex.Message + ". Nothing ran."
                };
            }

            byte[] raw;
            try
            {
                // ReadWrite|Delete: the same share mode the rest of this codebase uses to
                // read a file something else may be holding - an editor with the driver
                // open must not be able to fail the run.
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    raw = ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                return new SourceResolution
                {
                    Error = "code_path " + full + " could not be read: " + ex.Message + ". Nothing ran."
                };
            }

            DecodedSource decoded = PythonSourceText.Decode(raw);
            if (!decoded.Ok)
                return new SourceResolution { Error = "code_path " + full + ": " + decoded.Error };

            if (string.IsNullOrWhiteSpace(decoded.Text))
                return new SourceResolution
                {
                    Error = "code_path " + full + " decoded to " + raw.Length + " byte(s) of nothing but " +
                            "whitespace. An empty script is not a run. Nothing ran."
                };

            return new SourceResolution
            {
                Code = decoded.Text,
                Path = full,
                ReadNow = true,
                Encoding = decoded.Encoding,
                NewlinesNormalized = decoded.NewlinesNormalized
            };
        }

        /// <summary>
        /// The script source, NAMED. Without the path an IronPython traceback says
        /// "&lt;string&gt;" and a 535-line driver's failure points nowhere; with it, the
        /// traceback names the real file and line, which is the whole difference between
        /// a stack trace you can act on and one you cannot.
        /// </summary>
        private static ScriptSource SourceFor(ScriptEngine engine, string code, string path)
        {
            var kind = Microsoft.Scripting.SourceCodeKind.Statements;
            if (string.IsNullOrEmpty(path)) return engine.CreateScriptSourceFromString(code, kind);
            return engine.CreateScriptSourceFromString(code, path, kind);
        }

        private static string SafeUser(UIApplication app)
        {
            try { return app?.Application?.Username; } catch { return null; }
        }

        private static string SafeTitle(Document d)
        {
            try { return d == null ? "(no document)" : d.Title; } catch { return "(title unreadable)"; }
        }
    }
}
