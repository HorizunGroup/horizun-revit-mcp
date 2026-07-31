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
// One request at a time, and a second one is REFUSED rather than queued: a Revit
// command cannot be aborted from outside, so a run that already outlived its
// caller's timeout is still holding the thread, and lining up behind it would
// only move the hang. RequestGate owns that bookkeeping - and owns the rule that
// a caller is only ever handed the result of its OWN request. Read it before
// touching anything here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace Horizun.Revit.Core
{
    public sealed class Dispatcher : IExternalEventHandler
    {
        private readonly Dictionary<string, ICommand> _commands =
            new Dictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase);

        private ExternalEvent _event;

        private readonly RequestGate _gate = new RequestGate();

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
        {
            if (name == null || !_commands.ContainsKey(name))
            {
                Log.Warn($"unknown command '{name}' requested");
                return CommandResult.Fail($"Unknown command: '{name}'.");
            }

            string refusal;
            RequestGate.Request req = _gate.Begin(name, paramsJson, out refusal);
            if (req == null)
            {
                Log.Warn($"'{name}' refused: {refusal}");
                return CommandResult.Fail(refusal);
            }

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
            RaiseOutcome raised = Raiser().Raise();
            if (raised != RaiseOutcome.Accepted && raised != RaiseOutcome.Pending)
            {
                _gate.Abandon(req);
                clock.Stop();
                Log.Warn($"'{name}' NOT QUEUED: Revit answered Raise() with {raised}");
                return CommandResult.Fail(
                    $"Revit refused to queue '{name}': Raise() returned {raised}. Nothing was done, and nothing " +
                    "will be - no callback is coming, so this is reported now instead of after the " +
                    $"{timeoutMs} ms timeout. " +
                    (raised == RaiseOutcome.Denied
                        ? "Denied usually means Revit is closing down or the bridge's external event has been " +
                          "disposed. If Revit is still open, restart it to reload the add-in."
                        : $"Raise() itself reported {raised}."));
            }

            if (!req.Wait(timeoutMs))
            {
                _gate.Abandon(req);
                Log.Warn($"'{name}' TIMED OUT after {timeoutMs} ms - Revit busy or on a modal dialog" +
                         (req.Started ? " (it is still running; its result will be discarded)" : " (it never started)"));
                return CommandResult.Fail(
                    $"'{name}' timed out after {timeoutMs} ms. Revit may be busy or waiting on a modal dialog. " +
                    (req.Started
                        ? "The command is STILL RUNNING inside Revit - it cannot be cancelled from here, and whatever " +
                          "it does will complete unseen. Nothing else can run until it returns."
                        : "Revit never started it, so nothing was done."));
            }

            clock.Stop();
            CommandResult result = req.Result ?? CommandResult.Fail($"'{name}' produced no result.");
            if (result.Success) Log.Info($"{name} ok in {clock.ElapsedMilliseconds} ms");
            else Log.Warn($"{name} FAILED in {clock.ElapsedMilliseconds} ms: {result.Error}");
            return result;
        }

        /// <summary>The ExternalEvent callback — runs on Revit's UI thread.</summary>
        public void Execute(UIApplication app)
        {
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

            try
            {
                ICommand cmd;
                if (!_commands.TryGetValue(req.Name, out cmd))
                {
                    req.Result = CommandResult.Fail($"Unknown command: '{req.Name}'.");
                    return;
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
                // A result nobody is waiting for is still worth a line in the log: it is
                // the only record that the work Revit was holding the thread for is done.
                if (req.Abandoned)
                    Log.Warn($"'{req.Name}' finished after its caller had given up; " +
                             $"result discarded ({(req.Result != null && req.Result.Success ? "it had succeeded" : "it had failed")}). " +
                             "The UI thread is free again.");
                _gate.Complete(req);

                // A request that QUEUED async work leaves the queue non-empty. Raise
                // again so the UI thread comes back for it once this reply is on its way,
                // rather than waiting for whatever the caller happens to ask next.
                //
                // This used to log a warning and carry on, which left the entries on a
                // queue nothing would pump again and their records open forever. The
                // pump closes them as not_started instead - see AsyncLifecycle.cs.
                AsyncPump.Pump(Raiser(), Log.Warn);
            }
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
                    using (var watch = new Interference(app))
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
                    work.Record.Result(result.Success ? Newtonsoft.Json.JsonConvert.SerializeObject(result.Data) : null);
                    work.Record.Finish(result.Success ? "ok" : "failed", result.Success ? null : result.Error);
                }
                catch (Exception ex) { Log.Error("could not close the async job record", ex); }

                Log.Warn("async " + work.Command + " (" + work.JobId + ") " +
                         (result != null && result.Success ? "ok" : "FAILED") + " in " + clock.ElapsedMilliseconds + " ms");

                // Another one may be waiting behind this. One per raise keeps each job
                // its own turn on the UI thread instead of one long unbroken block.
                //
                // THE ANSWER USED TO BE DISCARDED ENTIRELY - a bare _event.Raise(). So a
                // refusal here stranded every SUCCESSIVE job silently: the one that just
                // ran reported its result correctly, and the rest of the batch sat in a
                // queue nothing would ever pump, with their records open.
                AsyncPump.Pump(Raiser(), Log.Warn);
            }
        }

        public string GetName() => "Horizun.Dispatcher";
    }
}
