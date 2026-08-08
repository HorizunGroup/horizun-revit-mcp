// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The UI-thread bridge and command registry.
//
// The Revit API may only be touched on Revit's own UI thread. Our transport runs
// on a background thread (a named pipe). This class is the crossing: a background
// caller hands us a command name + params, we raise a Revit ExternalEvent, Revit
// calls us back on the UI thread where we run the command, and we hand the result
// back to the blocked caller. ExternalEvent is the documented, supported way to
// do exactly this (Autodesk's own async-API guidance).
//
// One request EXECUTES at a time; later callers wait in a bounded FIFO queue.
// RequestGate owns the queue, per-request completion signals and cancellation
// before start. Once work reaches the UI thread it is not interruptible, so the
// distinction between queued and running is a safety boundary, not presentation.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;

namespace Horizun.Revit.Core
{
    public sealed class Dispatcher : IExternalEventHandler
    {
        private readonly Dictionary<string, ICommand> _commands =
            new Dictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase);

        private ExternalEvent _event;

        private readonly RequestGate _gate = new RequestGate();
        private readonly DurableCommandLedger _idempotency = new DurableCommandLedger();
        private bool _preferAsync;

        /// <summary>
        /// The ExternalEvent behind IWorkRaiser, which is the ONLY Revit-dependent part
        /// of deciding what a refused raise means.
        ///
        /// Denied needs a Revit that is shutting down, so that path was "reasoned and
        /// compiled" and could not be exercised. Everything on the other side of this
        /// interface is now an ordinary test case.
        /// </summary>
        private sealed class ExternalEventRaiser : IWorkRaiser
        {
            private readonly ExternalEvent _ev;
            public ExternalEventRaiser(ExternalEvent ev) { _ev = ev; }

            public RaiseOutcome Raise()
            {
                if (_ev == null) return RaiseOutcome.Unknown;
                return Map(_ev.Raise());
            }
        }

        /// <summary>
        /// Revit's answer, in our terms. Anything unrecognised maps to Unknown and is
        /// treated exactly like Denied - an answer this code does not understand is not
        /// evidence that a callback is coming.
        /// </summary>
        internal static RaiseOutcome Map(ExternalEventRequest r)
        {
            switch (r)
            {
                case ExternalEventRequest.Accepted: return RaiseOutcome.Accepted;
                case ExternalEventRequest.Pending: return RaiseOutcome.Pending;
                case ExternalEventRequest.Denied: return RaiseOutcome.Denied;
                default: return RaiseOutcome.Unknown;
            }
        }

        private IWorkRaiser Raiser() => new ExternalEventRaiser(_event);

        public void Register(ICommand command)
        {
            if (command == null) return;
            _commands[command.Name] = command;
        }

        internal ICommand ResolveCommand(string name)
        {
            ICommand command;
            return name != null && _commands.TryGetValue(name, out command) ? command : null;
        }

        public IEnumerable<string> CommandNames => _commands.Keys;

        /// <summary>Create the ExternalEvent. Call once, from OnStartup, on the UI thread.</summary>
        public void Initialize()
        {
            _event = ExternalEvent.Create(this);
        }

        /// <summary>
        /// Called from the transport (background) thread. Blocks until the command has
        /// run on the UI thread and returns its result. A timeout means Revit is busy
        /// or stuck in a modal — we say so rather than hang the pipe forever, and the
        /// abandoned request is marked so the next caller learns why the thread is not
        /// free instead of meeting an unexplained refusal.
        /// </summary>
        public CommandResult Invoke(string name, string paramsJson, int timeoutMs)
            => Invoke(null, name, paramsJson, timeoutMs);

        public CommandResult Invoke(string wireId, string name, string paramsJson, int timeoutMs)
        {
            if (name == null || !_commands.ContainsKey(name))
            {
                Log.Warn($"unknown command '{name}' requested");
                return CommandResult.Fail($"Unknown command: '{name}'.");
            }

            string refusal;
            RequestGate.Request req = _gate.Begin(wireId, name, paramsJson, out refusal);
            if (req == null)
            {
                Log.Warn($"'{name}' refused: {refusal}");
                return CommandResult.Fail(refusal);
            }

            if (req.AheadAtAdmission > 0)
                Log.Info($"'{name}' queued as ticket {req.Ticket} behind {req.AheadAtAdmission} request(s)");

            // Only what happened and how long it took. Never the parameters: those
            // carry model content, paths and values, and a log is not the place for
            // a client's data.
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Raise() ANSWERS, and the answer used to be thrown away. Revit can refuse
            // to queue the event - the commonest reason being that it is shutting down,
            // or that the ExternalEvent has been disposed - and a refused raise means no
            // callback is ever coming. Discarding it turned that into a 600-second wait
            // for something that had already been declined, and then a timeout message
            // blaming a modal dialog that was never there.
            RaiseOutcome raised;
            try { raised = Raiser().Raise(); }
            catch (Exception ex)
            {
                raised = RaiseOutcome.Unknown;
                Log.Warn("initial ExternalEvent.Raise() threw: " + ex.Message);
            }
            if (raised != RaiseOutcome.Accepted && raised != RaiseOutcome.Pending)
            {
                // Denied/unknown means this ExternalEvent will not produce a callback.
                // Wake every ordinary and async waiter now; leaving older entries behind
                // would turn one definitive refusal into a row of ten-minute timeouts.
                int failed = _gate.FailQueued(
                    "Revit refused the shared ExternalEvent before this queued request started. It NEVER RAN.");
                AsyncPump.FailEverythingWaiting(
                    "Revit refused the shared ExternalEvent before this queued job started. It NEVER RAN.",
                    message => Log.Warn(message));
                clock.Stop();
                Log.Warn($"'{name}' NOT QUEUED: Revit answered Raise() with {raised}; " +
                         $"closed {failed} ordinary waiter(s)");
                return CommandResult.Fail(
                    $"Revit refused to queue '{name}': Raise() returned {raised}. Nothing was done, and nothing " +
                    "will be - no callback is coming, so this is reported now instead of after the " +
                    $"{timeoutMs} ms timeout. " +
                    (raised == RaiseOutcome.Denied
                        ? "Denied usually means Revit is closing down or the bridge's external event has been " +
                          "disposed. If Revit is still open, restart it to reload the add-in."
                        : $"Raise() itself reported {raised}."));
            }

            // The wait, in slices. One flat Wait(timeoutMs) used to be the whole story,
            // and its measured failure mode was the expensive kind of honesty: a "New
            // Project" dialog left open cost three health calls 600 s EACH - 30 minutes
            // to learn what this class's own log line said in the first second ("it
            // never started"). Revit only services the ExternalEvent when idle, and a
            // modal means it never will; the caller's thread is not stuck, so between
            // slices it asks ModalProbe. A dialog that persists across consecutive
            // probes (ModalSighting's rule - one sighting can be a dialog Interference
            // is already cancelling) while this request has NOT started is returned as
            // a RESULT, now, with the dialog named - not as a timeout, ten minutes
            // late, with a guess.
            bool completed = false;
            string declaredModal = null;
            var sighting = new ModalSighting();
            int waitedSoFarMs = 0;
            while (waitedSoFarMs < timeoutMs)
            {
                int slice = Math.Min(ModalSighting.ProbeSliceMs, timeoutMs - waitedSoFarMs);
                if (req.Wait(slice)) { completed = true; break; }
                waitedSoFarMs += slice;

                // Once the command is RUNNING the UI thread is doing the work it was
                // asked to do; a dialog it raises is Interference's to record and this
                // caller's only honest option is to keep waiting.
                if (req.Started) continue;

                declaredModal = sighting.Observe(ModalProbe.DescribeModal());
                if (declaredModal != null) break;
            }

            if (!completed && declaredModal != null)
            {
                _gate.Abandon(req);
                Log.Warn($"'{name}' REMOVED FROM QUEUE after {waitedSoFarMs} ms: Revit is on modal dialog " +
                         declaredModal + " and the request never started");
                return CommandResult.Fail(
                    "Revit has a MODAL DIALOG open: " + declaredModal + ". '" + name + "' was queued but Revit " +
                    "does not service the bridge until the dialog is answered by a human, so the request was " +
                    "removed from the queue after " + waitedSoFarMs + " ms instead of holding this call for the " +
                    "full " + timeoutMs + " ms timeout. It NEVER STARTED: nothing ran and nothing was written. " +
                    "The dialog persisted across " + ModalSighting.ConsecutiveSightingsToDeclare + " probes " +
                    "about a second apart, so it is not one the bridge auto-cancels during a command - it " +
                    "predates this request. Answer or close it in the Revit UI (check every monitor: it can " +
                    "open on another screen) and retry.");
            }

            if (!completed)
            {
                _gate.Abandon(req);
                // The probe again, once, for the final message: a modal seen here could
                // not be declared above (the request had started, or it never persisted),
                // but naming what is on screen right now beats "may be waiting".
                string modalNow = ModalProbe.DescribeModal();
                Log.Warn($"'{name}' TIMED OUT after {timeoutMs} ms - Revit busy or on a modal dialog" +
                         (req.Started ? " (it is still running; its result will be discarded)" : " (it never started)"));
                return CommandResult.Fail(
                    $"'{name}' timed out after {timeoutMs} ms. Revit may be busy or waiting on a modal dialog. " +
                    (req.Started
                        ? "The command is STILL RUNNING inside Revit - it cannot be cancelled from here, and whatever " +
                          "it does will complete unseen. Nothing else can run until it returns."
                        : "It was removed from the FIFO queue before Revit started it, so nothing was done.") +
                    (modalNow != null
                        ? " Revit is showing a modal dialog RIGHT NOW: " + modalNow + "."
                        : ""));
            }

            clock.Stop();
            CommandResult result = req.Result ?? CommandResult.Fail($"'{name}' produced no result.");
            long waitedMs = req.StartedUtc == default(DateTime)
                ? clock.ElapsedMilliseconds
                : Math.Max(0, (long)(req.StartedUtc - req.QueuedUtc).TotalMilliseconds);
            Newtonsoft.Json.Linq.JObject data = result.Data as Newtonsoft.Json.Linq.JObject;
            if (result.Success && data == null && result.Data != null)
            {
                // A number of typed commands deliberately return anonymous objects.
                // Newtonsoft can serialize those directly, but normalizing them here
                // makes queue telemetry consistent instead of silently omitting it.
                Newtonsoft.Json.Linq.JToken token = Newtonsoft.Json.Linq.JToken.FromObject(result.Data);
                data = token as Newtonsoft.Json.Linq.JObject;
                if (data != null) result.ReplaceData(data);
            }
            if (result.Success && data != null && data["bridge_queue"] == null)
            {
                data["bridge_queue"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["queued"] = req.AheadAtAdmission > 0,
                    ["ahead_at_admission"] = req.AheadAtAdmission,
                    ["waited_ms"] = waitedMs,
                    // WHAT the wait was. waited_ms is queued-to-started, and that interval
                    // has two different causes that used to share one number: requests
                    // ahead in the FIFO, and Revit itself not servicing the ExternalEvent.
                    // Measured in the field (2026-08-04): a first call after add-in load
                    // reported waited_ms 25014 with queued=false and ahead_at_admission 0
                    // - 25 seconds attributed to a queue it was never in. Revit only runs
                    // external events when idle, and just after start it is not: that is
                    // warm-up, and with a modal dialog open it is the dialog. The bridge
                    // cannot tell those two apart from here, so the field names the
                    // boundary it CAN see: with nothing ahead, none of the wait was queue.
                    ["waited_on"] = req.AheadAtAdmission > 0
                        ? "queue_then_revit_ui_thread"
                        : "revit_ui_thread_only (nothing was ahead; a long wait here is Revit not yet idle - add-in warm-up just after start, or a modal dialog)",
                    ["capacity"] = _gate.Capacity,
                    ["execution_and_wait_ms"] = clock.ElapsedMilliseconds
                };
            }
            if (result.Success) Log.Info($"{name} ok in {clock.ElapsedMilliseconds} ms");
            else Log.Warn($"{name} FAILED in {clock.ElapsedMilliseconds} ms: {result.Error}");

            // ---- The receipt (5.2). Written from what the reply itself carried, after
            // the reply is final, and NEVER able to fail the operation it records: the
            // answer the caller is waiting on outranks the diary. Failures are counted
            // and surfaced by health rather than swallowed.
            try
            {
                Newtonsoft.Json.Linq.JObject receipt = ReceiptLedger.Build(
                    name, result.Success, result.Success ? null : result.Error,
                    result.Data as Newtonsoft.Json.Linq.JObject,
                    waitedMs, clock.ElapsedMilliseconds,
                    req.Ticket.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    DateTime.UtcNow);
                ReceiptLedger.Append(ReceiptLedger.DefaultDirectory(), receipt,
                                     Settings.RawValue, DateTime.UtcNow);
            }
            catch { /* counted inside Append; a diary must never cost an answer */ }

            return result;
        }

        /// <summary>The ExternalEvent callback — runs on Revit's UI thread.</summary>
        public void Execute(UIApplication app)
        {
            // Fairness between ordinary waiting callers and explicit run_async jobs.
            // If both queues stay busy, turns alternate; neither can starve the other.
            if (_preferAsync && AsyncQueue.Count > 0)
            {
                _preferAsync = false;
                RunOneAsync(app);
                return;
            }

            // Whose request is this? Taking is destructive, so a duplicate raise finds
            // nothing and does nothing - which is what keeps a write from running twice.
            RequestGate.Request req = _gate.Take();
            if (req == null)
            {
                // No caller is waiting, so this raise is for the async queue. Same rule:
                // Take() is destructive and there is no requeue, because the entries are
                // mutations and re-running one is a second write, not a retry.
                RunOneAsync(app);
                return;
            }

            DurableCommandDecision durableClaim = null;
            try
            {
                ICommand cmd;
                if (!_commands.TryGetValue(req.Name, out cmd))
                {
                    req.Result = CommandResult.Fail($"Unknown command: '{req.Name}'.");
                    return;
                }

                JObject request;
                try { request = string.IsNullOrWhiteSpace(req.ParamsJson) ? new JObject() : JObject.Parse(req.ParamsJson); }
                catch { request = null; } // the command owns its normal invalid-JSON error

                CommandContract contract = Contract.Find(req.Name);
                string permissionReason;
                if (contract != null && !Settings.IsToolAllowed(contract, out permissionReason))
                {
                    req.Result = CommandResult.Fail(permissionReason + " Nothing ran.");
                    return;
                }
                if (request != null && RequiresIdempotency(contract, request))
                {
                    string key = request.Value<string>("idempotency_key");
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        req.Result = CommandResult.Fail(
                            "idempotency_key is REQUIRED when '" + req.Name + "' will mutate or change the Revit " +
                            "session. Generate one UUID for this deliberate operation and keep it unchanged only " +
                            "when retrying the identical call. Nothing ran.");
                        return;
                    }

                    string documentFingerprint = DocumentFingerprintFor(app, contract, request);
                    string fingerprint = RequestFingerprint.OfOperation(
                        req.Name, documentFingerprint, request, "idempotency_key", "confirmation_token");
                    try { durableClaim = _idempotency.Claim(key, req.Name, fingerprint); }
                    catch (Exception ex)
                    {
                        req.Result = CommandResult.Fail(
                            "Could not establish durable idempotency before '" + req.Name + "': " + ex.Message +
                            ". Nothing ran; Horizun refuses a mutation whose retry safety could not be recorded.");
                        return;
                    }

                    if (durableClaim.Outcome == DurableCommandOutcome.Replay)
                    {
                        req.Result = durableClaim.ReplayResult;
                        StampIdempotency(req.Result, key, "replayed", false, durableClaim.Message);
                        return;
                    }
                    if (!durableClaim.IsFresh)
                    {
                        req.Result = CommandResult.Fail(durableClaim.Message);
                        return;
                    }
                }

                // Watch for the whole execution, for every command, without any of them
                // having to opt in: a modal dialog stops this thread until the caller
                // times out, and a dismissed warning that nobody reports is a lie by
                // omission. Both are caught here and travel back with the result.
                using (var watch = new Interference(app))
                {
                    try
                    {
                        req.Result = cmd.Execute(app, req.ParamsJson);
                    }
                    finally
                    {
                        object said = watch.Report();
                        if (said != null)
                        {
                            if (req.Result == null) req.Result = CommandResult.Fail($"'{req.Name}' produced no result.");
                            req.Result.RevitSaid = said;
                            Log.Warn($"{req.Name}: Revit raised {watch.WarningCount} warning(s), " +
                                     $"{watch.ErrorCount} error(s), {watch.DialogCount} dialog(s)");
                        }
                    }
                }
                if (durableClaim != null && durableClaim.IsFresh)
                    StampIdempotency(req.Result, durableClaim.Key, "executed_once", true,
                        "The durable claim and result were recorded; an identical retry will replay this answer.");
            }
            catch (Exception ex)
            {
                // A command threw instead of returning Fail. Surface it; never let the
                // UI-thread callback die with an unobserved exception.
                Log.Error($"'{req.Name}' threw on the UI thread", ex);
                req.Result = CommandResult.Fail(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (durableClaim != null && durableClaim.IsFresh)
                {
                    try { _idempotency.Complete(durableClaim, req.Result); }
                    catch (Exception ex)
                    {
                        // The model may already have changed. Never replace its real result with a
                        // cheerful success when the durable completion record did not land.
                        Log.Error("could not durably complete idempotency key for '" + req.Name + "'", ex);
                        req.Result = CommandResult.Fail(
                            "The command returned inside Revit, but its durable idempotency completion record could " +
                            "not be written: " + ex.Message + ". Its outcome is now AMBIGUOUS; do not retry with a " +
                            "new key until the model has been inspected.");
                    }
                }
                // A result nobody is waiting for is still worth a line in the log: it is
                // the only record that the work Revit was holding the thread for is done.
                if (req.Abandoned)
                    Log.Warn($"'{req.Name}' finished after its caller had given up; " +
                             $"result discarded ({(req.Result != null && req.Result.Success ? "it had succeeded" : "it had failed")}). " +
                             "The UI thread is free again.");
                _gate.Complete(req);
                _preferAsync = AsyncQueue.Count > 0;
                PumpNext();
            }
        }

        private static bool RequiresIdempotency(CommandContract contract, JObject request)
        {
            if (contract == null) return false;
            switch (contract.Effect)
            {
                case ToolEffect.Mutating:
                    // A Python preflight validates and compiles but executes nothing, so
                    // like a dry run it never crosses the mutation boundary and needs no
                    // durable key. Only an explicit preflight=true is exempt.
                    if (contract.Name == "horizun_execute_python" &&
                        request.Value<bool?>("preflight") == true) return false;
                    return true;
                case ToolEffect.MutatingUnlessDryRun:
                    // All current plan/apply commands default to dry_run=true. Only an
                    // explicit false crosses the mutation boundary.
                    return request["dry_run"] != null && request.Value<bool?>("dry_run") == false;
                case ToolEffect.DocumentSession:
                    string operation = (request.Value<string>("operation") ?? "").ToLowerInvariant();
                    if (operation == "inspect" || string.IsNullOrEmpty(operation)) return false;
                    if (operation == "close" && request.Value<bool?>("dry_run") == true) return false;
                    return true;
                default:
                    return false;
            }
        }

        private static string DocumentFingerprintFor(UIApplication app, CommandContract contract, JObject request)
        {
            // Session operations change which document is active or can close it. Their
            // target is already in the canonical arguments; binding the fingerprint to
            // the pre-call active document would make a successful open/close change its
            // own retry identity and defeat replay after a lost response.
            if (contract != null &&
                (contract.Name == "horizun_open_document" || contract.Effect == ToolEffect.DocumentSession))
                return "(document-session-target-is-in-arguments)";

            try
            {
                var doc = app?.ActiveUIDocument?.Document;
                string year = app?.Application?.VersionNumber;
                return DocumentGate.IdentityOf(doc, year)?.Fingerprint() ?? "(no-active-document)";
            }
            catch { return "(active-document-unreadable)"; }
        }

        private static void StampIdempotency(CommandResult result, string key, string status, bool executed,
                                             string note)
        {
            if (result == null || !result.Success) return;
            JObject data = result.Data as JObject;
            if (data == null && result.Data != null)
            {
                data = JToken.FromObject(result.Data) as JObject;
                if (data != null) result.ReplaceData(data);
            }
            if (data == null) return;
            data["idempotency"] = new JObject
            {
                ["key"] = key,
                ["status"] = status,
                ["command_executed_in_this_call"] = executed,
                ["note"] = note
            };
        }

        /// <summary>
        /// One queued script, on the UI thread, with nobody waiting for the answer.
        ///
        /// Everything this produces goes into the job record, because that is the only
        /// place an async caller can read it. Every exit path finishes the record: a job
        /// with no finish line is indistinguishable from one whose process died, and
        /// horizun_job_status refuses to guess between the two.
        /// </summary>
        private void RunOneAsync(UIApplication app)
        {
            AsyncWork work = AsyncQueue.Take();
            if (work == null) return;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            CommandResult result = null;
            try
            {
                ICommand cmd;
                if (!_commands.TryGetValue(work.Command, out cmd))
                    result = CommandResult.Fail("Unknown command: '" + work.Command + "'.");
                else
                {
                    CommandContract contract = Contract.Find(work.Command);
                    string permissionReason;
                    if (contract != null && !Settings.IsToolAllowed(contract, out permissionReason))
                        result = CommandResult.Fail(permissionReason + " The queued job did not run.");
                    else using (var watch = new Interference(app))
                    {
                        // The command writes into the record the CALLER was handed. Without
                        // this it opens its own, and the caller polls an id whose
                        // checkpoint_count never leaves zero while the progress accumulates
                        // in a second file it was never told about. Measured on a 150 s job.
                        Job.Ambient = work.Record;
                        // The record moves from queued to running HERE, and not before.
                        // Until this line the entry was waiting for a turn on the UI
                        // thread, and job_status must be able to say which of the two it
                        // is - "no finish line" used to cover queued, running and died.
                        try { work.Record.MarkRunning(); } catch { }
                        try { result = cmd.Execute(app, work.ParamsJson); }
                        finally
                        {
                            Job.Ambient = null;
                            object said = watch.Report();
                            if (said != null)
                            {
                                if (result == null) result = CommandResult.Fail("'" + work.Command + "' produced no result.");
                                result.RevitSaid = said;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("async '" + work.Command + "' threw on the UI thread", ex);
                result = CommandResult.Fail(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    if (result == null) result = CommandResult.Fail("'" + work.Command + "' produced no result.");
                    // revit_said, built EXACTLY as PipeEnvelope builds it for the sync
                    // path, so run_async and a synchronous call report the same shape. It
                    // travels on failure too: what Revit raised is usually the REASON, and
                    // the async caller has no other channel to learn it - the four
                    // undiagnosed models on 2026-08-07 lost precisely this.
                    string revitSaidJson = null;
                    if (result.RevitSaid != null)
                    {
                        try { revitSaidJson = Newtonsoft.Json.Linq.JToken.FromObject(result.RevitSaid).ToString(Newtonsoft.Json.Formatting.None); }
                        catch (Exception rex) { Log.Warn("could not serialize revit_said for async job: " + rex.Message); }
                    }
                    work.Record.Result(result.Success ? Newtonsoft.Json.JsonConvert.SerializeObject(result.Data) : null,
                                       revitSaidJson);
                    work.Record.Finish(result.Success ? "ok" : "failed", result.Success ? null : result.Error);
                }
                catch (Exception ex) { Log.Error("could not close the async job record", ex); }

                Log.Warn("async " + work.Command + " (" + work.JobId + ") " +
                         (result != null && result.Success ? "ok" : "FAILED") + " in " + clock.ElapsedMilliseconds + " ms");

                _preferAsync = false;
                PumpNext();
            }
        }

        /// <summary>
        /// Schedule one more callback when either queue has work. A denied raise closes
        /// every still-waiting entry as never started; leaving them queued would promise
        /// executions for a callback Revit has explicitly said will not come.
        /// </summary>
        private void PumpNext()
        {
            if (!_gate.HasPending && AsyncQueue.Count == 0) return;

            RaiseOutcome outcome;
            try { outcome = Raiser().Raise(); }
            catch (Exception ex)
            {
                outcome = RaiseOutcome.Unknown;
                Log.Warn("raising the external event for queued work threw: " + ex.Message);
            }

            if (outcome == RaiseOutcome.Accepted || outcome == RaiseOutcome.Pending) return;

            string reason = "Revit refused to schedule queued work: Raise() returned " + outcome +
                            ". It NEVER STARTED; nothing was executed or written. Revit is closing or the " +
                            "ExternalEvent is no longer available.";
            int sync = _gate.FailQueued(reason);
            int async = AsyncPump.FailEverythingWaiting(reason, Log.Warn);
            Log.Warn("queued work abandoned after Raise()=" + outcome + ": " + sync +
                     " synchronous request(s), " + async + " async job(s)");
        }

        public bool CancelQueued(string wireId, out string detail)
            => _gate.CancelQueued(wireId, out detail);

        public int Shutdown()
        {
            string reason = "Revit shut down before this queued command started. It NEVER RAN: nothing was " +
                            "executed and nothing was written.";
            return _gate.FailQueued(reason);
        }

        public string GetName() => "Horizun.Dispatcher";
    }
}
