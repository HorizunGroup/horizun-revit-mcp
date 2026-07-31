// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// Direct Python against the Revit API, on the UI thread. The escape hatch: no
// set of typed tools ever covers the whole API, this does.
//
// DISABLED BY DEFAULT. It runs arbitrary code inside Revit with the full API and
// the rights of the signed-in user. That is right for a developer at their own
// machine and wrong for a production surface, so it is switched on per machine in
// settings.json - checked here AND in the server, because the two halves ship
// separately and neither may be the only gate.
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
            "DISABLED BY DEFAULT - runs arbitrary code inside Revit on the UI thread with the full API and the " +
            "rights of the signed-in user. Enable per machine in %USERPROFILE%\\.horizun\\settings.json. " +
            "REQUIRES target_document, like every other command that can change a model: it is matched against " +
            "the ACTIVE document and refused if they differ, and this command will not switch documents for you. " +
            "A script that needs no document cannot run here. " +
            "run_async=true additionally REQUIRES idempotency_key - the reply carrying your job_id is the message " +
            "that gets lost, so a retry needs a way to be recognised as one. The key is bound to the Revit " +
            "process, the target document, a SHA-256 of the code and every other argument: the same key with the " +
            "same request returns the original job_id and queues nothing, the same key with a different request " +
            "is refused. " +
            "IT IS STILL A PRIVILEGED BYPASS: unlike the typed commands it has no dry run, no plan and no " +
            "confirmation token, so nothing rehearses what it will do. That is an accepted risk, not a policy it " +
            "satisfies - see docs/security-model.md. " +
            "doc/uidoc/uiapp/app are injected. Return data by assigning __output__ or with print(); a dict or " +
            "list is serialized to JSON and output_kind reports scalar|structure|text_only, where text_only " +
            "means the structure could NOT be serialized and only its text rendering survived. The standard " +
            "library is available (json, re, csv, datetime, math). " +
            "TRANSACTIONS: NO rollback is attempted, and none is possible. This text used to say one was " +
            "'attempted, best-effort'; the code that attempted it had already been deleted for opening a NEW " +
            "transaction and rolling THAT back, which does nothing to the orphan. The Revit API gives no handle " +
            "on a transaction opened by other code. What happens instead: Document.IsModifiable is re-read after " +
            "the script, and a document left modifiable makes this command FAIL and says so. Wrap every " +
            "Transaction in try/finally with RollBack in the finally. Prefer a typed command for anything " +
            "recurring: a typed command can be verified, and this cannot.";

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
        /// In memory and per process, exactly like DocumentGate.Confirmations and for
        /// the same reason: a claim that outlived the process would be a claim about
        /// work whose outcome nobody knows. See Core/Idempotency.cs.
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
            string code;
            bool runAsync;
            string idempotencyKey;
            try
            {
                request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson);
                code = request.Value<string>("code");
                runAsync = request.Value<bool?>("run_async") ?? false;
                idempotencyKey = request.Value<string>("idempotency_key");
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message);
            }
            if (string.IsNullOrWhiteSpace(code))
                return CommandResult.Fail("'code' is required (the Python source to run).");
            if (code.Length > MaxScriptChars)
                return CommandResult.Fail("Script is " + code.Length + " characters, over the " + MaxScriptChars +
                                          " limit. Put it in a file and read it from a short script instead.");

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
                    ["code"] = code,
                    // The dispatcher runs the script itself; a second async hop would
                    // queue it forever.
                    ["run_async"] = false,
                    ["target_document"] = request.Value<string>("target_document")
                                          ?? request.Value<string>("expected_document")
                                          ?? request.Value<string>("target_document_title")
                };

                Job asyncJob = null;
                IdempotencyDecision decision = AsyncClaims.Claim(idempotencyKey, Name, fingerprint, () =>
                {
                    // Inside the ledger's lock. Two requests carrying one key can be in
                    // flight at once - that is what a retry IS - and creating the record
                    // out here would let both of them make one.
                    asyncJob = Job.Start(Name);
                    return asyncJob.Id;
                });

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
                            "The key is bound to this Revit process (pid " + revitPid + "), the target document, a " +
                            "SHA-256 of the code, and every other argument. It is held IN MEMORY: if Revit " +
                            "restarts, this key is forgotten and the same request would run again."
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
                    ["queue_depth"] = AsyncQueue.Count,
                    ["poll_with"] = "horizun_job_status with job_id=" + asyncJob.Id,
                    ["executed"] = false,
                    ["idempotency_key"] = idempotencyKey,
                    ["safe_to_retry"] =
                        "YES, with this same key and this same request. You will get this same job_id back and " +
                        "nothing will be queued a second time. The key is bound to this Revit process (pid " +
                        revitPid + "), the target document, a SHA-256 of the code and every other argument - " +
                        "change any of them and the retry is REFUSED rather than silently treated as this one. " +
                        "The ledger is in memory: across a Revit restart the key is forgotten and the request " +
                        "would run again.",
                    ["executed_means"] =
                        "false because NOTHING HAS RUN YET. This reply means the script is queued, not that it " +
                        "worked. Read horizun_job_status for what it did.",
                    ["at_most_once"] =
                        "AT MOST ONCE, and here is exactly what that rests on, because it used to say 'exactly " +
                        "once' and was only half true. (1) The entry is claimed from the queue DESTRUCTIVELY, so " +
                        "a duplicate raise of the external event finds nothing - that half always held. (2) A " +
                        "re-sent REQUEST is recognised by its idempotency_key and queues nothing - that half did " +
                        "not exist, so a client retrying a lost reply produced a second entry, each claimed once, " +
                        "for two executions. (3) If Revit refuses to schedule the queue, or shuts down first, " +
                        "this job is closed as not_started rather than left open. WHAT IT DOES NOT COVER: a Revit " +
                        "restart forgets every key, so a retry across one runs the script again.",
                    ["cancelling_does_not_stop_revit"] =
                        "Cancelling the MCP request, or losing the connection, stops YOU WAITING and nothing else. " +
                        "The Revit API cannot interrupt work on its UI thread, so the script runs to completion " +
                        "either way and its result lands in the job record.",
                    ["if_revit_closes"] =
                        "A job whose record has no finish line either is still running or died with the process. " +
                        "horizun_job_status reports that ambiguity rather than resolving it, and this queue is NOT " +
                        "persisted - nothing replays a mutation whose outcome nobody knows.",
                    ["transaction_policy"] = TransactionPolicy
                });
            }

            // A key on the SYNCHRONOUS path is refused rather than ignored. Ignoring it
            // would be the worse failure by far: the caller believes its retry is
            // deduplicated, and it is not - a synchronous run hands its result back over
            // the wire, so there is no stored answer to replay and a second call is a
            // second execution. Saying so is the only honest option.
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
                return CommandResult.Fail(
                    "'idempotency_key' was supplied without run_async=true. It is REFUSED rather than ignored: a " +
                    "synchronous run returns its result over the wire and keeps no stored answer to replay, so " +
                    "this call cannot be deduplicated and accepting the key would tell you otherwise. Nothing ran. " +
                    "Either drop the key - and accept that a lost reply means you cannot safely retry - or set " +
                    "run_async=true, which is the path that carries the guarantee.");

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
            Job job = Job.Ambient ?? Job.Start(Name);
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

            string printed = null;
            object output = null;
            string error = null;
            bool leftModifiable = false;

            try
            {
                engine.Execute(
                    "import sys as __hz_sys, io as __hz_io\n" +
                    "__hz_buf = __hz_io.StringIO()\n" +
                    "__hz_stdout, __hz_stderr = __hz_sys.stdout, __hz_sys.stderr\n" +
                    "__hz_sys.stdout = __hz_buf\n" +
                    "__hz_sys.stderr = __hz_buf\n" +
                    // Available to every script with no import: checkpoint("placing walls", 40, 300).
                    // Each call reaches the disk immediately, so progress is readable from
                    // OUTSIDE while this thread is still busy being this call — and it
                    // survives a crash, which is when knowing how far it got matters most.
                    "def checkpoint(label, done=None, total=None):\n" +
                    "    __hz_job.Write(str(label), done, total)\n", scope);

                ScriptSource source = engine.CreateScriptSourceFromString(code, Microsoft.Scripting.SourceCodeKind.Statements);
                source.Execute(scope);
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
                    engine.Execute(
                        "__hz_printed = __hz_buf.getvalue()\n" +
                        "__hz_sys.stdout, __hz_sys.stderr = __hz_stdout, __hz_stderr\n", scope);
                    object p;
                    if (scope.TryGetVariable("__hz_printed", out p) && p != null) printed = p.ToString();
                }
                catch { }

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

            if (error != null)
                return CommandResult.Fail(error + (string.IsNullOrEmpty(printed) ? "" : "\n--- stdout before the error ---\n" + printed));

            // A dict assigned to __output__ used to come back as the string
            // "IronPython.Runtime.PythonDictionary": ToString() on a value that does not
            // override it, published as if it were the data. Now it is serialized, and
            // when it cannot be, the reply SAYS the structure was lost.
            ScriptOutputRendering rendered = ScriptOutput.Render(output);

            return CommandResult.Ok(new
            {
                mode = "sync",
                executed = true,
                output = rendered.Value,
                output_kind = rendered.Kind,
                output_note = rendered.Note,
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
