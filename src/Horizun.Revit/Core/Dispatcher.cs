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
using System.Linq;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Contracts;

namespace Horizun.Revit.Core
{
    public sealed partial class Dispatcher : IExternalEventHandler
    {
        // A short, privacy-safe operational fact for the local Revit UI and
        // horizun_health. Parameters and model names never enter it: an operator
        // needs to know that Revit is busy and with which tool, not project data.
        private static readonly object ActivityLock = new object();
        private static string _activeTool;
        private static DateTime _activeToolStartedUtc;

        public static string CurrentActivityDescription()
        {
            lock (ActivityLock)
            {
                if (string.IsNullOrEmpty(_activeTool)) return null;
                int seconds = (int)Math.Max(0, (DateTime.UtcNow - _activeToolStartedUtc).TotalSeconds);
                return "Revit is executing '" + _activeTool + "' (" + seconds + " s elapsed).";
            }
        }

        private static void BeginActivity(string tool)
        {
            lock (ActivityLock)
            {
                _activeTool = tool;
                _activeToolStartedUtc = DateTime.UtcNow;
            }
        }

        private static void EndActivity(string tool)
        {
            lock (ActivityLock)
            {
                if (string.Equals(_activeTool, tool, StringComparison.OrdinalIgnoreCase))
                {
                    _activeTool = null;
                    _activeToolStartedUtc = default(DateTime);
                }
            }
        }

        private readonly Dictionary<string, ICommand> _commands =
            new Dictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase);

        // Every name handed to Register, in order, repeats included. The dictionary
        // above cannot remember a second registration of one name; this can, and
        // it is what the contract comparison at startup reads.
        private readonly List<string> _registrationAttempts = new List<string>();
        private readonly HashSet<string> _registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly RequestGate _gate = new RequestGate();
        private readonly DurableCommandLedger _idempotency =
            new DurableCommandLedger(retentionLog: message => Log.Info(message));
        private bool _preferAsync;
        private volatile string _documentSnapshot;
        internal string DocumentSnapshot => _documentSnapshot;
        private void CaptureDocument(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                _documentSnapshot = doc == null ? "" : DocumentGate.IdentityOf(doc, app.Application.VersionNumber).Fingerprint();
            }
            catch { _documentSnapshot = null; }
        }

        /// <summary>
        /// Register one command. A SECOND registration of the same name THROWS: this was
        /// an indexer assignment, so two classes sharing a Name kept whichever was
        /// registered last, silently, and the one that answered was not the one somebody
        /// had tested. A duplicate is a build defect and the add-in says so at startup.
        /// </summary>
        public void Register(ICommand command)
        {
            if (command == null) throw new ArgumentNullException("command");
            // Community build: a Pro-only command is not registered at all, so it cannot run over the pipe even when
            // asked for by name, and the registry still matches the (filtered) contract.
            if (Edition.IsCommunity && Edition.ProOnlyTools.Contains(command.Name)) return;
            _registrationAttempts.Add(command.Name);
            RegistryContract.Admit(_registeredNames, command.Name);
            _commands[command.Name] = command;
        }

        /// <summary>
        /// Compare what was registered against what the contract forwards to Revit.
        /// The contract is the single source: nothing here keeps a second list.
        /// </summary>
        public RegistryContract.Report VerifyAgainstContract()
        {
            return RegistryContract.Compare(_registrationAttempts, Contract.PluginCommands);
        }

        internal ICommand ResolveCommand(string name)
        {
            ICommand command;
            return name != null && _commands.TryGetValue(name, out command) ? command : null;
        }

        public IEnumerable<string> CommandNames => _commands.Keys;

        private CommandResult InvokeWork(string wireId, string name, string paramsJson, int timeoutMs)
        {
            timeoutMs = CommandDeadlinePolicy.DispatchWaitMs(name, timeoutMs);
            if (_shuttingDown)
                return CommandResult.FailWithDetail("Revit is shutting down. '" + name +
                    "' was not queued and NEVER RAN; nothing was executed or written.",
                    ErrorCodes.Detail(ErrorCodes.RevitNoDisponible, extra: RequestGate.NeverStartedDetail()));

            if (name == null || !_commands.ContainsKey(name))
            {
                Log.Warn($"unknown command '{name}' requested");
                return CommandResult.Fail($"Unknown command: '{name}'.");
            }

            // Submission itself touches no Revit API. Sending it through RequestGate
            // placed it behind the long command it was meant to outlive, so a standard
            // MCP task could wait ten minutes merely to receive its task id. Admit it on
            // the pipe thread, durably, then raise the shared event for the queued child.
            if (string.Equals(name, "horizun_submit_job", StringComparison.OrdinalIgnoreCase))
                return SubmitJobWithoutWaitingForUi(name, paramsJson);

            string refusal;
            RequestGate.Request req = _gate.Begin(wireId, name, paramsJson, false, out refusal);
            if (req == null)
            {
                Log.Warn($"'{name}' refused: {refusal}");
                return CommandResult.Fail(refusal);
            }
            if (req.CancelledBeforeStart)
            {
                // Its cancel overtook it on the control connection (backlog #36): refused on
                // arrival, never queued, no event raised.
                Log.Warn($"'{name}' refused on arrival: its cancellation arrived first; it never started");
                return req.Result;
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
            RaiseAttemptResult raise = RaiseWithBoundedRetry("initial ExternalEvent raise for '" + name + "'");
            if (!raise.Scheduled)
            {
                // THIS request's raise was refused, so THIS request is withdrawn. The
                // others were admitted by raises that were accepted, and failing them all
                // on one refusal (as this did) reported work as never run that Revit was
                // still going to service (audit 2026-10-08). Withdrawn under the gate's
                // lock: if the UI thread claimed it after all, it is not reported as never run.
                bool withdrawn = _gate.Abandon(req);
                clock.Stop();
                Log.Warn($"'{name}' NOT QUEUED: Revit kept answering Raise() with {raise.Outcome} " +
                         $"for {raise.Attempts} attempt(s); this request " + (withdrawn ? "was withdrawn" : "had already been claimed"));
                if (!withdrawn)
                {
                    // Claimed by a callback that another raise produced: it runs, and its
                    // result is what this caller gets.
                    return RequestGate.AwaitClaimed(req, name, timeoutMs);
                }
                return CommandResult.FailWithDetail(
                    $"Revit refused to queue '{name}': Raise() returned {raise.Outcome} after " +
                    $"{raise.Attempts} bounded attempt(s). It NEVER STARTED: it was withdrawn from the queue and " +
                    "nothing was done, so this is reported now instead of after the " +
                    $"{timeoutMs} ms timeout. " +
                    (raise.Outcome == RaiseOutcome.Denied
                        ? "Repeated Denied usually means Revit is closing down or the bridge's external event has been " +
                          "disposed. If Revit is still open, restart it to reload the add-in."
                        : $"Raise() itself reported {raise.Outcome}."),
                    new Newtonsoft.Json.Linq.JObject
                    {
                        ["error_code"] = ErrorCodes.RevitOcupado,
                        ["never_started"] = true, ["transaction_status"] = "not_started"
                    });
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
            var dialogsClosed = new JArray();
            var dialogAttempts = new JArray();
            int waitedSoFarMs = 0;
            bool queueDeadlineReached = false;
            int busyRetryAttempts = 0;
            int busyRetryDelayTotalMs = 0;
            int nextBusyRetryAtMs = BusyRetryPolicy.DelayForAttempt(1,
                Settings.BusyRetryBaseDelayMs, Settings.BusyRetryMaxDelayMs);
            // The wait is measured on a clock, never by adding up the slices asked for. Live 5.0.4
            // (2026-11-12): once a request had started, a busy-retry instant already passed left
            // untilRetryMs at 1, so the loop waited in 1 ms slices that each took longer than 1 ms
            // of wall time; 44 s of real waiting counted as 31 s, a 60 s script finished before the
            // 45 s Python handoff was ever reached, and every deadline ran late while the loop spun.
            var waitClock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                int remainingMs = CommandDeadlinePolicy.RemainingMs(name, timeoutMs, waitedSoFarMs, req.Started);
                if (remainingMs == 0)
                {
                    queueDeadlineReached = !req.Started && waitedSoFarMs < timeoutMs;
                    break;
                }
                // Re-signalling is only for a request still in the queue; a started one waits its slice.
                bool hasBusyRetry = BusyRetryPolicy.TryGetNextDelay(busyRetryAttempts,
                    busyRetryDelayTotalMs, Settings.BusyRetryMaxAttempts,
                    Settings.BusyRetryBaseDelayMs, Settings.BusyRetryMaxDelayMs,
                    Settings.BusyRetryBudgetMs, out int nextBusyRetryDelayMs) && !req.Started;
                int untilRetryMs = hasBusyRetry
                    ? Math.Max(100, nextBusyRetryAtMs - waitedSoFarMs) : int.MaxValue;
                int slice = Math.Min(ModalSighting.ProbeSliceMs, Math.Min(remainingMs, untilRetryMs));
                if (req.Wait(slice)) { completed = true; break; }
                waitedSoFarMs = (int)Math.Min(int.MaxValue, waitClock.ElapsedMilliseconds);

                // Once the command is RUNNING the UI thread is doing the work it was
                // asked to do; a dialog it raises is Interference's to record and this
                // caller's only honest option is to keep waiting.
                if (req.Started) continue;

                string modalObservation = ModalProbe.DescribeModal();
                declaredModal = sighting.Observe(modalObservation);
                if (declaredModal == null)
                {
                    // Re-signal only the same queued RequestGate entry. Its one-shot
                    // claim remains the sole authority that can execute the command,
                    // so this is a scheduling retry, never a second write attempt.
                    // Modal requests leave through the named-dialog path below.
                    if (BusyRetryPolicy.CanResignal(req.Started, modalObservation != null) &&
                        hasBusyRetry && waitedSoFarMs >= nextBusyRetryAtMs)
                    {
                        if (!req.Started)
                        {
                            busyRetryAttempts++;
                            busyRetryDelayTotalMs += nextBusyRetryDelayMs;
                            nextBusyRetryAtMs = waitedSoFarMs + nextBusyRetryDelayMs;
                            try
                            {
                                RaiseOutcome outcome = Raiser().Raise();
                                Log.Info($"'{name}' remains queued with no modal after {waitedSoFarMs} ms; " +
                                    $"re-signalled its single request ({busyRetryAttempts}/{Settings.BusyRetryMaxAttempts}, " +
                                    $"backoff {nextBusyRetryDelayMs} ms, outcome {outcome})");
                            }
                            catch (Exception ex)
                            {
                                Log.Warn($"'{name}' queued retry signal {busyRetryAttempts} failed: {ex.GetType().Name}");
                            }
                        }
                    }
                    continue;
                }

                // Owner decision 2026-10-27: two notices that change no model - the save
                // reminder and External Tool Failure - are closed by the bridge while an
                // MCP request waits on them, and only those two (Core/ModalAutoClose.cs).
                // Logged and returned with the notice's name and the option chosen.
                if (Settings.AutoCloseDialogs && dialogsClosed.Count < ModalAutoClose.MaxClosesPerRequest)
                {
                    ModalDialogInfo info = ModalProbe.DescribeModalDetail();
                    ModalAutoClose.Decision close = ModalAutoClose.Decide(info, Settings.AutoCloseExternalToolFailure, Settings.AutoCloseSaveReminder);
                    JObject attempt = ModalAutoClose.Attempt(info, close, name, () => req.Wait(250), dialogAttempts, dialogsClosed);
                    if (attempt != null)
                    {
                        bool closed = attempt.Value<bool>("closed_verified");
                        if (!closed) break;
                        Log.Warn($"'{name}' waiting: dialog '{close.Title}' disappeared after a close attempt.");
                        sighting = new ModalSighting();
                        declaredModal = null;
                        continue;
                    }
                }
                break;
            }

            // The modal was declared from a read of Started taken BEFORE the removal; the
            // UI thread may have claimed the request in between. Only a removal proven
            // under the gate's lock may say "never started" (audit 2026-10-08).
            if (!completed && declaredModal != null && !_gate.Abandon(req))
            {
                declaredModal = null;
                completed = req.Wait(0);
            }

            if (!completed && declaredModal != null)
            {
                Log.Warn($"'{name}' REMOVED FROM QUEUE after {waitedSoFarMs} ms: Revit is on modal dialog " +
                         declaredModal + " and the request never started");
                // Re-probed once more for the structured detail: the persistence check
                // above is deliberately string-only (ModalSighting is Revit-free and
                // unit-tested on that arithmetic), so the richer read - title, best-effort
                // body text, buttons, owning module - is taken now, right before the
                // reply is built. The dialog that triggered the declaration is still up
                // in every measured case; if it has already changed, detail.dialog_window
                // reflects that rather than inventing continuity with 'declaredModal'.
                JObject modalDetail = ModalProbe.DescribeModalDetail()?.ToJson();
                JObject neverStartedDetail = new JObject
                {
                    ["error_code"] = ErrorCodes.RevitOcupado,
                    ["never_started"] = true, ["transaction_status"] = "not_started",
                    ["busy_retry_attempts"] = busyRetryAttempts
                };
                if (dialogsClosed.Count > 0) neverStartedDetail["modal_dialogs_closed"] = dialogsClosed;
                if (dialogAttempts.Count > 0) neverStartedDetail["modal_dialog_close_attempts"] = dialogAttempts;
                if (modalDetail != null)
                {
                    neverStartedDetail["modal_dialog"] = modalDetail;
                    ModalDialogKinds.Verdict kind = ModalDialogKinds.Classify(modalDetail);
                    ModalDialogKinds.Stamp(neverStartedDetail, kind);
                    if (kind != null) neverStartedDetail["que_hacer"] = kind.QueHacer;
                }
                return CommandResult.FailWithDetail(
                    "Revit has a MODAL DIALOG open: " + declaredModal + ". '" + name + "' was queued but Revit " +
                    "does not service the bridge until the dialog is answered by a human, so the request was " +
                    "removed from the queue after " + waitedSoFarMs + " ms instead of holding this call for the " +
                    "full " + timeoutMs + " ms timeout. It NEVER STARTED: nothing ran and nothing was written. " +
                    "The dialog persisted across " + ModalSighting.ConsecutiveSightingsToDeclare + " probes " +
                    "about a second apart, so it is not one the bridge auto-cancels during a command - it " +
                    "predates this request. Answer or close it in the Revit UI (check every monitor: it can " +
                    "open on another screen) and retry.",
                    neverStartedDetail);
            }

            bool removedBeforeStart = false;
            if (!completed)
            {
                // Proven under the gate's lock, not read from Started before it. And a
                // request that finished between the last slice and this point returns its
                // REAL result instead of a timeout.
                removedBeforeStart = _gate.Abandon(req);
                if (!removedBeforeStart && req.Wait(0)) completed = true;
            }

            // The caller gives up; the work does not. The call's ONE record (Core/CallProgress.cs)
            // is obtained under the same lock the completion takes: the command's own if it has
            // one (execute_python), else a new one with the in-memory checkpoints replayed, and
            // the rest of the run plus its final result - success or the failure cause - land
            // there for horizun_job_status. Field 2026-10-27: an 11-minute tail ended in "result
            // discarded (it had failed)". If the call completed first, its real answer is about
            // to be signalled and is returned instead of a job_id.
            Job lateRecord = null;
            if (!completed && !removedBeforeStart)
            {
                lateRecord = OpenLateRecord(req, name, out bool finishedFirst);
                if (finishedFirst && req.Wait(name == "horizun_health" ? 500 : Math.Min(30000, timeoutMs))) completed = true;
            }

            if (!completed)
            {
                // The probe again, once, for the final message: a modal seen here could
                // not be declared above (the request had started, or it never persisted),
                // but naming what is on screen right now beats "may be waiting".
                ModalDialogInfo modalNowDetail = ModalProbe.DescribeModalDetail();
                string modalNow = modalNowDetail?.ToSummaryLine();
                // 5.0.4: a RUNNING Python script with a record is handed off, not timed out (5.0.6: an export too).
                if (lateRecord != null && !removedBeforeStart && req.DetachedCancellation == null &&
                    CommandDeadlinePolicy.HandsOff(name))
                {
                    Log.Info($"'{name}' handed off after {clock.ElapsedMilliseconds} ms: still running, job {lateRecord.Id}");
                    return CommandResult.Ok(PythonHandoff.Payload(lateRecord.Id, clock.ElapsedMilliseconds,
                        lateRecord.Checkpoints, lateRecord.LastCheckpoint,
                        modalNowDetail?.ToJson(), name));
                }
                Log.Warn($"'{name}' TIMED OUT after {timeoutMs} ms - Revit busy or on a modal dialog" +
                         (removedBeforeStart ? " (it never started)" : lateRecord != null
                             ? " (it is still running; its progress and result go to job " + lateRecord.Id + ")"
                             : " (it is still running; no record could be opened, its result will be discarded)"));
                var timeoutDetail = new JObject
                {
                    // tiempo_agotado names the limit that was hit; a modal dialog seen at the
                    // same moment is the better explanation, so revit_ocupado keeps the limit too.
                    ["error_code"] = modalNowDetail != null || removedBeforeStart ? ErrorCodes.RevitOcupado : ErrorCodes.TiempoAgotado,
                    ["limite_ms"] = queueDeadlineReached ? CommandDeadlinePolicy.OpenDocumentQueueMs : timeoutMs,
                    ["timeout_phase"] = removedBeforeStart ? "queue" : "execution",
                    ["execution_may_still_run"] = !removedBeforeStart,
                    ["retryable"] = removedBeforeStart,
                    ["never_started"] = removedBeforeStart,
                    ["transaction_status"] = removedBeforeStart ? "not_started" : "unknown"
                };
                timeoutDetail["busy_retry_attempts"] = busyRetryAttempts;
                if (modalNowDetail != null) timeoutDetail["modal_dialog"] = modalNowDetail.ToJson();
                if (dialogsClosed.Count > 0) timeoutDetail["modal_dialogs_closed"] = dialogsClosed;
                if (dialogAttempts.Count > 0) timeoutDetail["modal_dialog_close_attempts"] = dialogAttempts;
                if (lateRecord != null)
                {
                    timeoutDetail["job_id"] = lateRecord.Id;
                    timeoutDetail["checkpoints_so_far"] = lateRecord.Checkpoints;
                    timeoutDetail["last_checkpoint"] = lateRecord.LastCheckpoint;
                    timeoutDetail["follow_with"] = "horizun_job_status job_id=" + lateRecord.Id;
                }
                return CommandResult.FailWithDetail(
                    $"'{name}' reached its {(queueDeadlineReached ? "queue" : "response")} deadline after {waitedSoFarMs} ms. Revit may be busy or waiting on a modal dialog. " +
                    (removedBeforeStart
                        ? "It was removed from the FIFO queue before Revit started it, so nothing was done."
                        : (req.DetachedCancellation != null
                            ? "The cooperative command has started and may continue between callbacks. "
                            : "The command is STILL RUNNING inside Revit - it cannot be cancelled from here. ") +
                          (lateRecord != null
                              ? "Its progress and its final result (including the failure cause) are recorded as job " +
                                lateRecord.Id + ": poll horizun_job_status job_id=" + lateRecord.Id + " - that does not need " +
                                "Revit's UI thread. Do NOT retry the call meanwhile: health and every other Revit call queue " +
                                "behind it until it returns. For work this long use horizun_submit_job."
                              : "Whatever it does will complete unseen. Nothing else can run until it returns.")) +
                    (modalNow != null
                        ? " Revit is showing a modal dialog RIGHT NOW: " + modalNow + "."
                        : ""),
                    timeoutDetail);
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
            if (dialogsClosed.Count > 0)
            {
                if (result.Success && data != null) data["modal_dialogs_closed"] = dialogsClosed;
                else if (result.Detail != null) result.Detail["modal_dialogs_closed"] = dialogsClosed;
            }
            if (dialogAttempts.Count > 0)
            {
                if (result.Success && data != null) data["modal_dialog_close_attempts"] = dialogAttempts;
                else if (result.Detail != null) result.Detail["modal_dialog_close_attempts"] = dialogAttempts;
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
            // The one seat every call passes through is where timing facts are
            // collected; health publishes the fold (ToolTimings.Snapshot).
            ToolTimings.Record(name, clock.ElapsedMilliseconds);

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
                    DateTime.UtcNow, ReceiptRequest(paramsJson));
                ReceiptLedger.Append(ReceiptLedger.DefaultDirectory(), receipt,
                                     Settings.RawValue, DateTime.UtcNow);
            }
            catch { /* counted inside Append; a diary must never cost an answer */ }

            return result;
        }

        private static Newtonsoft.Json.Linq.JObject ReceiptRequest(string paramsJson)
        {
            try { return string.IsNullOrWhiteSpace(paramsJson) ? null : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch { return null; }
        }

        private CommandResult SubmitJobWithoutWaitingForUi(string name, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            CommandContract contract = Contract.Find(name);
            string permissionReason = null;
            if (contract == null || !Settings.IsToolAllowed(contract, out permissionReason))
                return CommandResult.Fail((permissionReason ?? "The submit_job contract is unavailable.") + " Nothing ran.");

            JToken keyToken = request["idempotency_key"];
            string key = keyToken?.Type == JTokenType.String ? (string)keyToken : null;
            if (string.IsNullOrWhiteSpace(key))
                return CommandResult.Fail("idempotency_key is REQUIRED when submitting asynchronous work. " +
                    "Generate one UUID for this deliberate submission. Nothing was queued.");

            string fingerprint = RequestFingerprint.OfOperation(
                name, "(async-child-target-is-in-arguments)", request, "idempotency_key", "confirmation_token");
            DurableCommandDecision claim;
            try { claim = _idempotency.Claim(key, name, fingerprint); }
            catch (Exception ex)
            {
                return CommandResult.Fail("Could not establish durable idempotency before async submission: " +
                    ex.Message + ". Nothing was queued.");
            }

            if (claim.Outcome == DurableCommandOutcome.Replay)
            {
                StampIdempotency(claim.ReplayResult, key, "replayed", false,
                    "The original task id is replayed; no second job was queued.");
                return claim.ReplayResult;
            }
            if (!claim.IsFresh) return CommandResult.Fail(claim.Message);

            CommandResult result = null;
            string admittedJobId = null;
            try
            {
                DocumentGate.ClearResolvedPlan();
                result = _commands[name].Execute(null, paramsJson);
                if (result != null && result.Success)
                {
                    admittedJobId = (string)(result.Data as JObject)?["job_id"];
                    RaiseAttemptResult raise = RaiseWithBoundedRetry("async submission raise");
                    if (!raise.Scheduled)
                    {
                        // THIS job is withdrawn; the jobs already waiting were admitted by
                        // raises Revit accepted and are not this refusal's to fail (audit
                        // 2026-10-08). If a callback already claimed it, it runs.
                        AsyncWork withdrawn;
                        if (AsyncQueue.TryRemove(admittedJobId, out withdrawn))
                        {
                            try { withdrawn.Record?.Finish("not_started", "Revit refused the ExternalEvent after task admission. It NEVER RAN."); }
                            catch (Exception ex) { Log.Warn("async admission: the record of " + admittedJobId + " could not be finished: " + ex.Message); }
                            result = CommandResult.Fail("The task record was created, but Revit refused the callback " +
                                "that would execute it (" + raise.Outcome + " after " + raise.Attempts +
                                " attempt(s)). The queued job was withdrawn and closed as not_started.");
                        }
                    }
                }
                if (result == null) result = CommandResult.Fail("horizun_submit_job produced no result.");
                StampIdempotency(result, key, "executed_once", true,
                    "The durable submission was recorded; an identical retry returns the same task id.");
            }
            catch (Exception ex)
            {
                result = CommandResult.Fail(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                DocumentGate.ClearResolvedPlan();
                try { _idempotency.Complete(claim, result); }
                catch (Exception ex)
                {
                    Log.Error("could not durably complete async submission idempotency", ex);
                    var detail = new JObject
                    {
                        ["job_id"] = string.IsNullOrWhiteSpace(admittedJobId)
                            ? JValue.CreateNull() : JToken.FromObject(admittedJobId),
                        ["submission_record_incomplete"] = true,
                        ["execution_may_continue"] = !string.IsNullOrWhiteSpace(admittedJobId)
                    };
                    result = CommandResult.FailWithDetail(
                        "The job may have been queued, but the durable submission result could not be recorded: " +
                        ex.Message + ". Do not submit it again; inspect the supplied job_id with job_status.", detail);
                }
            }
            return result;
        }

        private void ExecuteWork(UIApplication app)
        {
            CaptureDocument(app);
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

            BeginActivity(req.Name);
            DurableCommandDecision durableClaim = null;
            string generatedPythonKey = null;
            try
            {
                if (ExitPending.IsQueued) { req.Result = CommandResult.RefuseBeforeWrite("Revit is exiting"); return; }
                ICommand cmd;
                if (!_commands.TryGetValue(req.Name, out cmd))
                {
                    req.Result = CommandResult.Fail($"Unknown command: '{req.Name}'.");
                    return;
                }

                JObject request;
                try { request = string.IsNullOrWhiteSpace(req.ParamsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(req.ParamsJson); }
                catch { request = null; } // the command owns its normal invalid-JSON error

                CommandContract contract = Contract.Find(req.Name);
                JObject originalPythonRequest = req.Name == "horizun_execute_python" && request != null
                    ? (JObject)request.DeepClone() : null;
                string permissionReason;
                if (contract != null && !Settings.IsToolAllowed(contract, out permissionReason))
                {
                    req.Result = CommandResult.Fail(permissionReason + " Nothing ran.");
                    return;
                }
                if (BlocksWorksharedWrite(app, contract, request, out string worksharedRefusal))
                {
                    req.Result = CommandResult.Fail(worksharedRefusal + " Nothing ran.");
                    return;
                }
                string sourceSha = null;
                if (request != null && req.Name == "horizun_execute_python")
                {
                    var source = PythonSourceSnapshot.Resolve(request);
                    if (source.Error != null)
                    {
                        req.Result = CommandResult.FailWithDetail(source.Error, new JObject
                        {
                            ["code"] = "source_resolution_failed", ["write_started"] = false,
                            ["changes_applied"] = false
                        });
                        return;
                    }
                    sourceSha = source.ExecutionSha256;
                    request = source.ExecutionRequest(request);
                    req.ParamsJson = request.ToString(Newtonsoft.Json.Formatting.None);
                }
                if (request != null && RequiresIdempotency(contract, request))
                {
                    string key = request.Value<string>("idempotency_key");
                    // Never for run_async: the reply that would carry a generated key is the one that
                    // gets lost, so a retry would mint a second key and queue the script twice.
                    if (string.IsNullOrWhiteSpace(key) && PythonIdempotencyRules.GeneratesKey(req.Name) &&
                        !PythonIdempotencyRules.IsAsyncPython(req.Name, request))
                    {
                        generatedPythonKey = PythonIdempotencyRules.EnsureKey(request);
                        key = generatedPythonKey;
                        req.ParamsJson = request.ToString(Newtonsoft.Json.Formatting.None);
                    }
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
                    try { durableClaim = _idempotency.Claim(key, req.Name, fingerprint, sourceSha); }
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
                        var detail = new JObject
                        {
                            ["code"] = "idempotency_" + durableClaim.Outcome.ToString().ToLowerInvariant(),
                            ["previous_source_sha256"] = durableClaim.PreviousSourceSha256,
                            ["submitted_source_sha256"] = sourceSha,
                            ["hash_scope"] = "execution_bundle_v1: normalized main source, ordered include snapshots and helpers version",
                            ["previous_execution_sha256"] = durableClaim.PreviousSourceSha256,
                            ["submitted_execution_sha256"] = sourceSha,
                            ["write_started"] = false, ["changes_applied"] = false
                        };
                        if (durableClaim.Outcome == DurableCommandOutcome.Conflict && originalPythonRequest != null)
                            detail["suggested_call"] = PythonIdempotencyRules.SuggestedCall(originalPythonRequest);
                        req.Result = CommandResult.FailWithDetail(durableClaim.Message, detail);
                        return;
                    }
                }

                // Watch for the whole execution, for every command, without any of them
                // having to opt in: a modal dialog stops this thread until the caller
                // times out, and a dismissed warning that nobody reports is a lie by
                // omission. Both are caught here and travel back with the result.
                using (var watch = new Interference(app))
                using (var changes = new ChangeWatch(app?.Application))
                {
                    try
                    {
                        // WHO KNOWS THE CALLER LEFT. Only the gate does, and only for the
                        // request executing right now. A long READ can ask, between units,
                        // whether it is still producing an answer for somebody - see
                        // Core/CooperativeRead.cs. A loop that never asks is unaffected,
                        // and a write is never allowed to ask at all.
                        CooperativeRead.Abandoned = () => req.Abandoned;
                        CallProgress.Current = req.Progress;
                        int undoRecordsBefore = Horizun.Revit.Commands.UndoCapture.RecordCount;
                        DocumentGate.ClearResolvedPlan();
                        bool? dirtyBefore = ActiveDocumentModified(app);
                        try { req.Result = IdentityPrepass.Execute(app, cmd, req.ParamsJson, LargeModelDispatch.Execute); }
                        finally { DocumentGate.ClearResolvedPlan(); }
                        // AFTER THE COMMAND RETURNED, NOTHING MAY TURN ITS ANSWER INTO AN ERROR
                        // (field report E1: a committed execute_plan answered an ArgumentException).
                        // The command's own result is the truth about the model; a report stage
                        // that throws is recorded inside that result (PostCommitReport), and
                        // the outer catch - which answers a bare Fail - is left for a command
                        // that threw itself.
                        try
                        {
                            // What the command left changed in the model gets the spatial
                            // coherence check (Core/SpatialAfterWrite.cs); reads cost nothing.
                            SpatialAfterWrite.Attach(req.Name, changes, req.Result);
                            // response_mode=summary (create_elements, clash): shaped only after
                            // the spatial check, so the rows it names stay whole.
                            ResponseSummaryRules.ApplyToResult(req.Name, req.ParamsJson, req.Result);
                            // A rehearsal that turned a clean document dirty says so (Core/DryRunDirtyRules.cs).
                            try
                            {
                                if (req.Result != null && req.Result.Success && req.Result.Data is JObject dryData &&
                                    DryRunDirtyRules.IsDryRunReply(dryData))
                                {
                                    JObject dirty = DryRunDirtyRules.Evaluate(true, dirtyBefore, ActiveDocumentModified(app));
                                    if (dirty != null) dryData["rehearsal_dirtied_document"] = dirty;
                                }
                            }
                            catch { /* a probe never fails the call */ }
                            // 4.2-N5 (CT-P1-5f): a real Python run leaves an undo batch for what it CREATED.
                            // Before the ownership stamp, so the stamp's own transaction is not part of it.
                            Horizun.Revit.Commands.PythonUndoRecord.AfterCommand(req.Name, request, changes, req.Result);
                            // 5.0.4: a committed write that recorded no inverse blocks undo_last instead of being skipped.
                            Horizun.Revit.Commands.UnrecordedWriteUndo.AfterCommand(req.Name, contract, request, changes, req.Result, undoRecordsBefore);
                            // HRZ ownership mark (4.1): what the command ADDED gets stamped in one
                            // short transaction of its own. Never fails the call - see HrzOwnership.cs.
                            HrzOwnership.AfterCommand(req.Name, contract, changes, req.Result);
                            // Workshared: record the write for the end-of-task sync, and remind (SyncReminder.cs).
                            SyncReminder.AfterCommand(app, req.Name, req.ParamsJson, changes, req.Result);
                            // A write while another MCP client is on this Revit (ClientPresence.cs).
                            if (request != null && RequiresIdempotency(contract, request) &&
                                !ConcurrentClientRule.RolledBackByContract(req.Name, request)) WarnConcurrentClients(req.Result);
                        }
                        catch (Exception reportError)
                        {
                            Log.Error($"'{req.Name}' returned, then its post-command report threw", reportError);
                            PostCommitReport.Record(req.Result, "post_command_report", reportError);
                        }
                    }
                    finally
                    {
                        // Cleared on EVERY path. A stale delegate would let the next
                        // command read the previous request's abandonment and stop for a
                        // caller who is still waiting.
                        CooperativeRead.Abandoned = null;
                        CallProgress.Current = null;
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
                if (durableClaim != null && durableClaim.IsFresh && req.Result != null && req.Result.RejectedBeforeRun)
                {
                    // CT-P1-5b: refused before anything ran (validation, size, syntax,
                    // compatibility). A key must identify work that was attempted, so the
                    // claim is voided and the same key is usable after the caller fixes the call.
                    try
                    {
                        if (_idempotency.Release(durableClaim, req.Result))
                        {
                            req.Result.NoteKeyReleased(durableClaim.Key);
                            durableClaim = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Fail closed: keep the claim; the finally block records the refusal as usual.
                        Log.Error("could not release the idempotency key of a call refused before it ran", ex);
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
                string typeMismatch = ArgumentTypeMismatch.Explain(ex, req.Name);
                req.Result = CommandResult.FailWithDetail(typeMismatch ?? (ex.GetType().Name + ": " + ex.Message), new JObject
                {
                    ["code"] = typeMismatch != null ? "invalid_operation_arguments" : "unhandled_command_exception", ["tool"] = req.Name,
                    [ErrorSignature.Field] = ErrorSignature.From(ex),
                    ["correlation_id"] = req.WireId, ["phase"] = "handler",
                    ["exception_type"] = ex.GetType().FullName, ["exception_message"] = ex.Message,
                    ["inner_exception_type"] = ex.InnerException?.GetType().FullName,
                    ["inner_exception_message"] = ex.InnerException?.Message,
                    ["write_started"] = null, ["changes_applied"] = null, ["transaction_status"] = "unknown"
                });
            }
            finally
            {
                if (generatedPythonKey != null && req.Result != null)
                {
                    JObject payload = req.Result.Success ? req.Result.Data as JObject : req.Result.Detail;
                    if (payload == null && req.Result.Success && req.Result.Data != null)
                        payload = JToken.FromObject(req.Result.Data) as JObject;
                    if (payload == null) payload = new JObject();
                    payload["idempotency_key_generated"] = generatedPythonKey;
                    payload["idempotency_key_generated_note"] = "Reuse this key only to retry this same operation.";
                    if (req.Result.Success) req.Result.ReplaceData(payload); else req.Result.ReplaceDetail(payload);
                }
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
                CaptureDocument(app);
                // A result nobody is waiting for is still worth a line in the log: it is
                // the only record that the work Revit was holding the thread for is done.
                // Handed to the call's progress unconditionally: it writes only into a record
                // the timeout path attached (now, or a moment from now - the two threads may
                // cross here), and costs nothing when the caller got its answer over the pipe.
                CommandResult finalResult = req.Result;
                // A record the COMMAND opened and handed over (execute_python's own, so its
                // checkpoint() calls and the timeout's job_id are one record) is closed here:
                // with the full result when the caller gave up, with the finish line alone when
                // the answer went back over the pipe - what the command used to write itself.
                req.Progress.Complete((record, callerGaveUp) =>
                {
                    if (callerGaveUp) WriteLateResult(record, finalResult);
                    else record.Finish(PythonRunControl.TerminalStatus(finalResult != null && finalResult.Success, finalResult?.Detail), finalResult?.Success == true ? null : finalResult?.Error);
                });
                if (req.Abandoned)
                    Log.Warn($"'{req.Name}' finished after its caller had given up; result " +
                             (req.Progress.Record != null ? "recorded in job " + req.Progress.Record.Id : "discarded") +
                             $" ({(req.Result != null && req.Result.Success ? "it had succeeded" : "it had failed")}). " +
                             "The UI thread is free again.");
                _gate.Complete(req);
                EndActivity(req.Name);
                _preferAsync = AsyncQueue.Count > 0;
                if (!ExitPending.IsQueued) PumpNext();
            }
        }

        // A PID can be reused: cache only together with the process start time. The expensive
        // module query runs off the Revit thread and is never awaited for more than 100 ms total.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Tasks.Task<string>> ClientPaths =
            new System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Tasks.Task<string>>();

        private static void WarnConcurrentClients(CommandResult result)
        {
            if (result == null) return;
            PresenceSnapshot snapshot = ClientPresence.Default.Take(DateTime.UtcNow);
            List<long> others = ConcurrentClientRule.OthersAlive(snapshot, pid =>
            {
                try { using (System.Diagnostics.Process.GetProcessById((int)pid)) return true; }
                catch (ArgumentException) { return false; }
                catch { return null; }
            });
            string warning = ConcurrentClientRule.Warning(others);
            if (warning == null) return;
            var processes = new JArray();
            var descriptions = new List<string>();
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            var keys = new HashSet<string>();
            foreach (long pid in others)
            {
                string name = null, started = null, path = null;
                try
                {
                    using (var process = System.Diagnostics.Process.GetProcessById((int)pid))
                    {
                        try { name = process.ProcessName; } catch { }
                        try { started = process.StartTime.ToUniversalTime().ToString("o"); } catch { }
                    }
                    if (started != null)
                    {
                        string key = pid + ":" + started;
                        keys.Add(key);
                        var task = ClientPaths.GetOrAdd(key, _ => System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                                {
                                    if (p.StartTime.ToUniversalTime().ToString("o") != started) return null;
                                    return p.MainModule.FileName;
                                }
                            }
                            catch { return null; }
                        }));
                        int remaining = Math.Max(0, 100 - (int)deadline.ElapsedMilliseconds);
                        if (task.IsCompleted || (remaining > 0 && task.Wait(remaining))) path = task.Result;
                    }
                }
                catch { }
                processes.Add(new JObject { ["pid"] = pid, ["name"] = name, ["path"] = path, ["started_utc"] = started });
                descriptions.Add((name ?? "unknown process") + " (pid " + pid + ", started " + (started ?? "unknown") + ")");
            }
            foreach (string key in ClientPaths.Keys)
                if (!keys.Contains(key)) ClientPaths.TryRemove(key, out _);
            ConcurrentClientRule.AddOrphanStates(processes, snapshot, c => c.Identity?.ParentAlive());
            warning += " Other clients: " + string.Join(", ", descriptions) + ".";
            var block = new JObject { ["other_client_pids"] = new JArray(others), ["other_clients"] = processes, ["warning"] = warning };
            if (result.Success && result.Data is JObject data) data["concurrent_clients"] = block;
            else if (!result.Success && result.Detail != null) result.Detail["concurrent_clients"] = block;
        }

        /// <summary>A durable record for a synchronous call whose caller timed out; null when the disk refuses.</summary>
        private static Job OpenLateRecord(RequestGate.Request req, string name, out bool finishedFirst)
        {
            finishedFirst = false;
            try
            {
                return req.Progress.OpenForTimeout(() =>
                {
                    Job record = Job.Start(name);
                    record.MarkRunning();
                    return record;
                }, "caller timed out; the call keeps running in Revit and reports here", out finishedFirst);
            }
            catch (Exception ex)
            {
                Log.Warn("no late record for '" + name + "': " + ex.Message);
                return null;
            }
        }

        /// <summary>The result of a call nobody waited for, written the way an async job's is.</summary>
        private static void WriteLateResult(Job record, CommandResult result)
        {
            if (result == null) result = CommandResult.Fail("the call produced no result.");
            string payload = null;
            if (result.Success)
            {
                try { payload = AsyncResultPayload.Serialize(result.Data, record.Id); }
                catch (Exception ex) { result = CommandResult.Fail("The call succeeded but its result could not be recorded: " + ex.Message); }
            }
            string said = null;
            if (result.RevitSaid != null)
                try { said = JToken.FromObject(result.RevitSaid).ToString(Newtonsoft.Json.Formatting.None); } catch { }
            record.Result(result.Success ? payload : null, said,
                result.Fallback?.ToJson().ToString(Newtonsoft.Json.Formatting.None),
                result.CapabilityGaps?.ToString(Newtonsoft.Json.Formatting.None),
                result.Detail?.ToString(Newtonsoft.Json.Formatting.None));
            record.Finish(PythonRunControl.TerminalStatus(result.Success, result.Detail),
                result.Success ? "the caller had timed out; this is the call's own result" : result.Error);
        }

        /// <summary>
        /// A machine owner can designate workshared models as audit-only. The decision
        /// sits on the UI thread, beside the actual active document; a ribbon warning
        /// alone would be bypassable by any MCP call. Unknown worksharing state blocks
        /// a possible write for the same reason an unreadable lock is not an unlocked
        /// model. Dry runs are explicitly admitted because the command's own contract
        /// says they roll back.
        /// </summary>
        private static bool BlocksWorksharedWrite(UIApplication app, CommandContract contract, JObject request,
                                                  out string refusal)
        {
            refusal = null;
            if (!Settings.ForceReadOnlyOnWorkshared || contract == null ||
                contract.Effect == ToolEffect.ReadOnly || contract.Effect == ToolEffect.HostState)
                return false;
            if (contract.Name == "horizun_view_visibility" && request?.Value<string>("operation") == "graphics_report") return false;

            // A declared dry run is the only safe exemption. An omitted dry_run on a
            // write-capable surface must not be treated as true by a generic gate: each
            // command owns its defaults, and this policy is deliberately conservative.
            if (contract.Effect == ToolEffect.MutatingUnlessDryRun && request?.Value<bool?>("dry_run") == true)
                return false;

            // horizun_code_check writes only with operation=travel_distance and
            // travel.create_paths=true; a requirement-set check or a measurement is a read.
            if (contract.Name == "horizun_code_check" &&
                (request?["travel"] as JObject)?.Value<bool?>("create_paths") != true)
                return false;

            bool? workshared = null;
            try { workshared = app?.ActiveUIDocument?.Document?.IsWorkshared; }
            catch { }
            if (workshared == false) return false;

            refusal = workshared == true
                ? "This machine is configured with force_read_only_on_workshared=true and the active document is workshared. " +
                  "Typed writes, document-session operations and external side effects are refused; run a dry_run or disable this local policy from the Revit ribbon after approval."
                : "This machine is configured with force_read_only_on_workshared=true but the active document's workshared state could not be read. " +
                  "An unknown collaboration state is not permission to write; run a dry_run or resolve the document state locally.";
            return true;
        }

        private static bool RequiresIdempotency(CommandContract contract, JObject request)
        {
            if (contract == null) return false;
            switch (contract.Effect)
            {
                case ToolEffect.Mutating:
                    if (ToolInputRules.IsSaveDocumentRehearsal(contract.Name, request)) return false;
                    // A Python preflight validates and compiles but executes nothing, so
                    // like a dry run it never crosses the mutation boundary and needs no
                    // durable key. Only an explicit preflight=true is exempt.
                    if (contract.Name == "horizun_execute_python" &&
                        request.Value<bool?>("preflight") == true) return false;
                    // read_only / rehearsal run inside a TransactionGroup that is always rolled
                    // back and re-read: they cannot change the model, so a key would only be
                    // friction (4.2-N5, CT-P1-5c).
                    if (contract.Name == "horizun_execute_python" &&
                        (request.Value<bool?>("read_only") == true || request.Value<bool?>("rehearsal") == true)) return false;
                    return true;
                case ToolEffect.MutatingUnlessDryRun:
                    // All current plan/apply commands default to dry_run=true. Only an
                    // explicit false crosses the mutation boundary.
                    return request["dry_run"] != null && request.Value<bool?>("dry_run") == false;
                case ToolEffect.DocumentSession:
                    string operation = (request.Value<string>("operation") ?? "").ToLowerInvariant();
                    if (operation == "inspect" || string.IsNullOrEmpty(operation)) return false;
                    if (request.Value<bool?>("dry_run") == true) return false;
                    // sync_with_central previews when dry_run is omitted; only false syncs.
                    if (operation == "sync_with_central" && request["dry_run"] == null) return false;
                    // new_project rehearses when dry_run is omitted, the same way.
                    if (operation == "new_project" && request["dry_run"] == null) return false;
                    if (operation == "batch" && request["dry_run"] == null) return false;
                    if (operation == "exit_application" && request["dry_run"] == null) return false;
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

        /// <summary>Document.IsModified of the active document, or null when it cannot be read.</summary>
        private static bool? ActiveDocumentModified(UIApplication app)
        {
            try { return app?.ActiveUIDocument?.Document?.IsModified; } catch { return null; }
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

        // Off the UI thread: a synchronous job opened its record without retention.
        internal static void ScheduleJobRetention()
        {
            try { System.Threading.ThreadPool.QueueUserWorkItem(_ => Job.ApplyStoreRetention()); }
            catch (Exception ex) { Log.Warn("job retention could not be scheduled (" + ex.Message + ")."); }
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

            // A SEQUENCE IS ITS OWN RUN. It writes its own steps and finishes its own
            // record, so the single-call path below is left exactly as it was rather
            // than growing a second meaning for every line in it.
            if (!ExitPending.IsQueued && work.Sequence != null && work.Sequence.Count > 0) { RunSequence(app, work); return; }

            // Review M3 / m-2: from the moment the work is taken until the script registers its stop switch, a cancel must
            // not be lost. AsyncQueue.Take claims the key under the same lock that dequeues it; Register applies it.

            var clock = System.Diagnostics.Stopwatch.StartNew();
            CommandResult result = null;
            bool began = false;
            try
            {
                ICommand cmd;
                if (ExitPending.IsQueued) result = CommandResult.RefuseBeforeWrite("Revit is exiting");
                else if (!_commands.TryGetValue(work.Command, out cmd))
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
                        // The record moves from queued to running HERE, and not before.
                        // Until this line the entry was waiting for a turn on the UI
                        // thread, and job_status must be able to say which of the two it
                        // is - "no finish line" used to cover queued, running and died.
                        if (work.DocumentFingerprint != null)
                        {
                            if (work.DocumentFingerprint.Length == 0)
                            {
                                if (app.ActiveUIDocument?.Document != null)
                                    throw new InvalidOperationException("A document opened after this job was admitted. Nothing was executed.");
                            }
                            else
                            {
                                CommandResult moved = DocumentGate.StillTheSame(app, work.DocumentFingerprint, work.Command);
                                if (moved != null) throw new InvalidOperationException(moved.Error);
                            }
                        }
                        AsyncResumeGuard.Begin(work.Record);
                        began = true;
                        work.HasStarted = true;
                        Job.Ambient = work.Record;
                        using (var changes = new ChangeWatch(app?.Application))
                        {
                            int undoRecordsBefore = Horizun.Revit.Commands.UndoCapture.RecordCount;
                            DocumentGate.ClearResolvedPlan();
                            try { result = IdentityPrepass.Execute(app, cmd, work.ParamsJson, LargeModelDispatch.Execute);
                                try
                                {
                                    SpatialAfterWrite.Attach(work.Command, changes, result);
                                    ResponseSummaryRules.ApplyToResult(work.Command, work.ParamsJson, result);
                                    // Review m1: an async script leaves the same undo batch for what it CREATED as a
                                    // synchronous one (4.2-N5). Before the ownership stamp, like the synchronous path.
                                    JObject asyncRequest = null;
                                    try { asyncRequest = string.IsNullOrWhiteSpace(work.ParamsJson) ? null : Horizun.Revit.Core.JsonText.ParseObject(work.ParamsJson); } catch { }
                                    Horizun.Revit.Commands.PythonUndoRecord.AfterCommand(work.Command, asyncRequest, changes, result);
                                    Horizun.Revit.Commands.UnrecordedWriteUndo.AfterCommand(work.Command, Contract.Find(work.Command), asyncRequest, changes, result, undoRecordsBefore);
                                    HrzOwnership.AfterCommand(work.Command, Contract.Find(work.Command), changes, result);
                                    SyncReminder.AfterCommand(app, work.Command, work.ParamsJson, changes, result);
                                }
                                catch (Exception reportError)
                                {
                                    Log.Error("'" + work.Command + "' returned, then its post-command report threw", reportError);
                                    PostCommitReport.Record(result, "post_command_report", reportError);
                                }
                            }
                        finally
                        {
                            DocumentGate.ClearResolvedPlan();
                            Job.Ambient = null;
                            CaptureDocument(app);
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
            }
            catch (Exception ex)
            {
                Log.Error("async '" + work.Command + "' threw on the UI thread", ex);
                string typeMismatch = ArgumentTypeMismatch.Explain(ex, work.Command);
                result = CommandResult.FailWithDetail(typeMismatch ?? (ex.GetType().Name + ": " + ex.Message),
                    ErrorCodes.Detail(typeMismatch != null ? ErrorCodes.EsquemaInvalido
                                      : ErrorCodes.IsRevitApiType(ex.GetType().FullName)
                                          ? ErrorCodes.ApiRevitExcepcion : ErrorCodes.ErrorInterno,
                                      ex.GetType().Name,
                                      extra: new JObject { [ErrorSignature.Field] = ErrorSignature.From(ex) }));
            }
            finally
            {
                try
                {
                    if (result == null) result = CommandResult.Fail("'" + work.Command + "' produced no result.");
                    string payloadJson = null;
                    if (result.Success)
                    {
                        try { payloadJson = AsyncResultPayload.Serialize(result.Data, work.JobId); }
                        catch (Exception imageEx)
                        {
                            object revitSaid = result.RevitSaid;
                            result = CommandResult.FailWithDetail(
                                "The command completed, but its file-backed async result could not be made durable: " +
                                imageEx.Message + ". The job is failed rather than returning a path that may disappear.",
                                new JObject
                                {
                                    ["job_id"] = work.JobId,
                                    ["stage"] = "persist_async_result",
                                    ["underlying_command_completed"] = true,
                                    ["result_available"] = false
                                });
                            result.RevitSaid = revitSaid;
                        }
                    }
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
                    string fallbackJson = result.Fallback == null ? null :
                        result.Fallback.ToJson().ToString(Newtonsoft.Json.Formatting.None);
                    string gapsJson = result.CapabilityGaps == null ? null :
                        result.CapabilityGaps.ToString(Newtonsoft.Json.Formatting.None);
                    string detailJson = result.Detail == null ? null :
                        result.Detail.ToString(Newtonsoft.Json.Formatting.None);
                    if (began)
                    {
                        // A later slice can fail after earlier slices produced a
                        // snapshot. Keep that snapshot with the failure details.
                        work.Record.Result(result.Success ? payloadJson : work.PartialResultJson,
                                           revitSaidJson, fallbackJson, gapsJson, detailJson);
                        work.Record.Finish(PythonRunControl.TerminalStatus(result.Success, result.Detail), result.Success ? null : result.Error);
                    }
                    else work.FinishBeforeCallback(result?.Error ?? "The callback did not start.");
                }
                catch (Exception ex) { Log.Error("could not close the async job record", ex); }
                if (PythonRunRegistry.ClaimsAtTake(work.Command) && !string.IsNullOrEmpty(work.JobId))
                    PythonRunRegistry.Unregister(work.JobId);

                Log.Warn("async " + work.Command + " (" + work.JobId + ") " +
                         (result != null && result.Success ? "ok" : "FAILED") + " in " + clock.ElapsedMilliseconds + " ms");

                _preferAsync = false;
                if (!ExitPending.IsQueued) PumpNext();
            }
        }

        /// <summary>
        /// ONE JOB WHOSE WORK IS AN ORDERED SEQUENCE - a read-only sweep over several
        /// models, on the queue that already exists.
        ///
        /// Three rules carry it, and each one is a way a sweep lies:
        ///
        ///   THE START IS RECORDED BEFORE THE STEP RUNS. A cloud open takes minutes,
        ///   and a step whose start is written only on completion is indistinguishable
        ///   from a stuck job for the whole time it is working.
        ///
        ///   EXECUTION STOPS AT THE FIRST FAILURE, and every later step is reported
        ///   not_run - never omitted, never succeeded. A sequence that stops at step
        ///   three and returns two steps reads as a two-step sequence that worked.
        ///
        ///   WHAT THE SEQUENCE OPENED IS CLOSED ON THE FAILURE PATH. This is exactly
        ///   where an implementation is tempted to swallow the exception and move on,
        ///   and the cost of doing so is a document left open in somebody's Revit.
        /// </summary>
        private void RunSequence(UIApplication app, AsyncWork work)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            List<SequenceEntry> steps = work.Sequence;
            // TWO FACTS, NOT ONE INDEX. `stoppedAt = -1` used to mean both "nothing
            // stopped" and "stopped before the first step ran", and on the second
            // reading the cleanup closes below were skipped - the one invariant that
            // must not depend on WHERE the sweep stopped, because its cost is a
            // document left open in somebody's Revit.
            int stoppedAt = -1;
            bool stopped = false;
            string failure = null;

            try { work.Record.MarkRunning(); } catch { }
            Job.Ambient = work.Record;
            try
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    SequenceEntry step = steps[i];
                    if (_shuttingDown)
                    {
                        failure = "Revit began closing before this step ran.";
                        stoppedAt = i - 1;
                        stopped = true;
                        break;
                    }

                    step.Status = StepStatus.Running;
                    step.StartedUtc = DateTime.UtcNow.ToString("o");
                    // BEFORE the step, not after: see the header.
                    try { work.Record.Step(step.Key, step.Tool, StepStatus.Running, null, null); } catch { }

                    CommandResult r = RunOneSequenceStep(app, step);
                    step.FinishedUtc = DateTime.UtcNow.ToString("o");

                    if (r != null && r.Success)
                    {
                        step.Status = StepStatus.Succeeded;
                        try { step.ResultRef = AsyncResultPayload.Serialize(r.Data, work.JobId); }
                        catch (Exception ex)
                        {
                            step.ResultRef = null;
                            step.Error = "the step succeeded and its result could not be stored: " + ex.Message;
                        }
                    }
                    else
                    {
                        step.Status = StepStatus.Failed;
                        step.Error = r == null ? "the step produced no result." : r.Error;
                        step.Detail = r?.Detail;
                        failure = "step " + step.Key + " (" + step.Tool + ") failed: " + step.Error;
                        stoppedAt = i;
                        stopped = true;
                    }
                    try { work.Record.Step(step.Key, step.Tool, step.Status, step.ResultRef, step.Error); } catch { }

                    if (step.Status == StepStatus.Failed) break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("async sequence threw on the UI thread", ex);
                failure = ex.GetType().Name + ": " + ex.Message;
                stopped = true;
                if (stoppedAt < 0) stoppedAt = steps.FindIndex(s => s.Status == StepStatus.Running);
            }
            finally
            {
                Job.Ambient = null;
            }

            // WHAT THE SEQUENCE OPENED, THE SEQUENCE CLOSES. The remaining close steps
            // run even though the sweep has stopped: they are the only reason a failed
            // model does not leave a document sitting in somebody's Revit. Their own
            // outcome is recorded honestly - a close that was attempted and failed is
            // not the same as one that never ran.
            if (stopped) RunPendingCloses(app, work, steps, stoppedAt);

            // A sweep that ran to the end settles nothing: every step already has its
            // own outcome. One that stopped settles everything after the stop, and a
            // stop BEFORE the first step settles all of them.
            if (stopped) JobSequenceRules.SettleAfterStop(steps, stoppedAt);
            foreach (SequenceEntry s in steps)
                if (s.Status == StepStatus.NotRun)
                    try { work.Record.Step(s.Key, s.Tool, s.Status, null, s.Error); } catch { }

            string terminal = JobSequenceRules.TerminalStatus(steps);
            var payload = new JObject
            {
                ["mode"] = "sequence",
                ["job_id"] = work.JobId,
                ["steps"] = JobSequenceRules.StepsJson(steps),
                ["steps_submitted"] = steps.Count,
                ["steps_succeeded"] = steps.Count(s => s.Status == StepStatus.Succeeded),
                ["steps_not_run"] = steps.Count(s => s.Status == StepStatus.NotRun),
                ["read_only"] = true,
                ["read_only_means"] = JobSequenceRules.ReadOnlyMeans,
                ["not_run_means"] = JobSequenceRules.NotRunMeans
            };
            try
            {
                work.Record.Result(payload.ToString(Newtonsoft.Json.Formatting.None));
                work.Record.Finish(terminal, failure);
            }
            catch (Exception ex) { Log.Error("could not close the async sequence record", ex); }

            Log.Warn("async sequence (" + work.JobId + ") " + terminal + " in " + clock.ElapsedMilliseconds + " ms");
            _preferAsync = false;
            PumpNext();
        }

        /// <summary>
        /// The close steps of a stopped sweep. ONLY closes: nothing else is retried,
        /// because the sweep has already failed and running more reads over a Revit in
        /// an unknown state is how one bad model becomes twelve bad results.
        ///
        /// AND ONLY THE CLOSES WHOSE OWN MODEL WAS OPENED. The test is the NEAREST
        /// PRECEDING open, not any open anywhere in the sequence: in a twelve-model
        /// sweep that stopped at model three, "some open succeeded" is true because
        /// model one's did, and the cleanup would then aim closes at nine documents
        /// this sweep never opened. A close that finds one of them open - because the
        /// USER has it open - closes the user's document. That is the failure this
        /// predicate exists to prevent; reporting "a document may be left open" about
        /// a document that never existed is only the second-worst outcome.
        /// </summary>
        private void RunPendingCloses(UIApplication app, AsyncWork work, List<SequenceEntry> steps, int stoppedAt)
        {
            Job.Ambient = work.Record;
            try
            {
                for (int i = stoppedAt + 1; i < steps.Count; i++)
                {
                    SequenceEntry step = steps[i];
                    if (step.Tool != "horizun_document_session") continue;

                    // THE NEAREST PRECEDING OPEN, not any open anywhere. "Some
                    // open succeeded" is true in a twelve-model sweep that
                    // stopped at model three, because model one's did - and the
                    // cleanup would then run the closes for models four through
                    // twelve, each aimed at a document this sweep never opened.
                    // If the user has one of those open, the sweep closes the
                    // user's document. Only the open belonging to THIS close can
                    // justify attempting it.
                    bool ownOpenSucceeded = false;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (steps[j].Tool != "horizun_open_document") continue;
                        ownOpenSucceeded = steps[j].Status == StepStatus.Succeeded;
                        break;
                    }
                    if (!ownOpenSucceeded) continue;
                    step.Status = StepStatus.Running;
                    step.StartedUtc = DateTime.UtcNow.ToString("o");
                    try { work.Record.Step(step.Key, step.Tool, StepStatus.Running, null, null); } catch { }

                    CommandResult r = RunOneSequenceStep(app, step);
                    step.FinishedUtc = DateTime.UtcNow.ToString("o");
                    step.Status = r != null && r.Success ? StepStatus.Succeeded : StepStatus.Failed;
                    // A close that worked has NO error. Explaining itself in the error
                    // field would make a reader scanning for failures find one.
                    step.Error = step.Status == StepStatus.Failed
                        ? "the sweep had already stopped and this close was attempted anyway: " +
                          (r == null ? "no result." : r.Error) + " A document may be left open."
                        : null;
                    try { work.Record.Step(step.Key, step.Tool, step.Status, null, step.Error); } catch { }
                }
            }
            catch (Exception ex) { Log.Error("a cleanup close threw on the UI thread", ex); }
            finally { Job.Ambient = null; }
        }

        private CommandResult RunOneSequenceStep(UIApplication app, SequenceEntry step)
        {
            ICommand cmd;
            if (!_commands.TryGetValue(step.Tool, out cmd))
                return CommandResult.Fail("Unknown command: " + step.Tool + ".");

            CommandContract contract = Contract.Find(step.Tool);
            string permissionReason;
            // Permissions are checked AGAIN here, not only at admission: a sequence can
            // sit in the queue while the machine owner revokes something.
            if (contract != null && !Settings.IsToolAllowed(contract, out permissionReason))
                return CommandResult.Fail(permissionReason + " This sequence step did not run.");

            using (var watch = new Interference(app))
            {
                CommandResult r = null;
                DocumentGate.ClearResolvedPlan();
                try { r = IdentityPrepass.Execute(app, cmd, step.Arguments.ToString(Newtonsoft.Json.Formatting.None), LargeModelDispatch.Execute); }
                catch (Exception ex) { r = CommandResult.Fail(ex.GetType().Name + ": " + ex.Message); }
                finally
                {
                    DocumentGate.ClearResolvedPlan();
                    object said = watch.Report();
                    if (said != null)
                    {
                        if (r == null) r = CommandResult.Fail(step.Tool + " produced no result.");
                        r.RevitSaid = said;
                    }
                }
                return r;
            }
        }

        public bool CancelQueued(string wireId, out string detail, bool callerTimedOut = false)
            => _gate.CancelQueued(wireId, out detail, callerTimedOut);

        public JObject ObserveRequest(string wireId) => _gate.Observe(wireId);

    }
}
