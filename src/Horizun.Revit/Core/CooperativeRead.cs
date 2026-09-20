// -----------------------------------------------------------------------------
// Horizun Revit MCP - stopping a long READ politely. Original Horizun code.
//
// G25 of the 2026-09-14 competitive inventory: "dividir lecturas seguras;
// progreso medido, límite de bloqueo UI y restauración coherente al cancelar".
//
// THE THING THIS CANNOT DO, SAID FIRST. A Revit command runs on Revit's UI
// thread, and nothing - not this bridge, not Revit itself - can interrupt a call
// that is already inside the API. Cancellation here is COOPERATIVE in the exact
// sense of the word: a loop that chooses to ask, between units of work, whether
// it should stop. A loop that never asks cannot be stopped, and this file does
// not pretend otherwise; what it offers is the asking.
//
// READS ONLY, AND THE REASON IS NOT SQUEAMISHNESS. A read stopped halfway has
// produced fewer facts - which is recoverable, because the reply says how many
// and why. A WRITE stopped halfway has produced a model in a state nobody chose,
// and the answer to that is the transaction, which either commits whole or
// rolls back whole. Mixing the two would replace an atomic write with a
// partially-applied one and call it progress. So: a scope opened while a
// transaction is open is a bug, and Assert() says so out loud.
//
// TWO REASONS TO STOP, AND THEY ARE DIFFERENT FINDINGS:
//
//   client_abandoned    the caller went away - cancelled, disconnected, timed
//                       out. Continuing would spend Revit's UI thread producing
//                       an answer for nobody.
//
//   ui_budget_exhausted the read has held the UI thread for as long as it was
//                       allowed to. Revit is unresponsive for exactly as long as
//                       a command runs, and a scan that takes four minutes is
//                       four minutes in which its user cannot click anything.
//                       The budget makes that a DECLARED number instead of a
//                       property of the model somebody happened to open.
//
// A PARTIAL RESULT IS NEVER A COMPLETE ONE. Every scope reports `complete`, the
// units it got through, the units it knew about, and the reason it stopped. A
// caller that renders a partial scan as a finished one is the failure this shape
// exists to make impossible - "we did not finish looking" and "we looked and
// found nothing" must never read the same.
//
// Revit-free on purpose: the clock, the counters and the stop decision are
// arithmetic, so the behaviour is provable without a building. The one thing
// that needs a host - "has this request been abandoned?" - arrives as a
// delegate the dispatcher installs.
// -----------------------------------------------------------------------------
using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CooperativeRead
    {
        /// <summary>
        /// How long a read may hold Revit's UI thread before it stops and says so.
        ///
        /// Twenty seconds is chosen against what a person does: below about that, a
        /// frozen window reads as "it is working"; past it, people start clicking, then
        /// killing Revit. A partial answer that arrives is worth more than a complete one
        /// nobody waited for.
        /// </summary>
        public const int DefaultUiBudgetMs = 20000;

        public const int MaxUiBudgetMs = 600000;

        /// <summary>
        /// Has the request currently executing been abandoned by its caller? Installed by
        /// the dispatcher, which is the only thing that knows. Null means "nothing is
        /// watching", and a scope with nothing watching simply never stops for that
        /// reason - it does not guess.
        /// </summary>
        public static Func<bool> Abandoned;

        /// <summary>
        /// The scope of the request currently executing, when it opened one.
        ///
        /// FOR THE COMMANDS TOO LARGE TO THREAD A PARAMETER THROUGH - model_scan above all,
        /// a dozen independent section emitters several hundred lines deep each, most of
        /// which never need to know. Where the shape allows it, an explicit parameter is
        /// better and query_model and list_elements use one.
        ///
        /// ONE REQUEST AT A TIME IS THE ARCHITECTURE, not an assumption: this bridge runs one
        /// command at a time on Revit's UI thread and queues concurrent calls. If that ever
        /// changes, this static is the first thing that breaks, and this is where somebody
        /// changing it would look.
        /// </summary>
        public static Scope Ambient;

        /// <summary>
        /// Install a scope for the duration of one request, and give back the token that
        /// removes it. ALWAYS in a using or a finally: a scope left behind belongs to a
        /// request that has finished, and the NEXT command would stop early on somebody
        /// else's budget - a bug that appears at random and never reproduces.
        /// </summary>
        public static IDisposable Install(Scope scope)
        {
            Ambient = scope;
            return new Removal();
        }

        private sealed class Removal : IDisposable
        {
            public void Dispose() { Ambient = null; }
        }

        /// <summary>
        /// Count a unit against the ambient scope and answer whether to carry on. TRUE when
        /// there is no ambient scope, which is what keeps every existing caller unchanged.
        /// </summary>
        public static bool AmbientContinue()
        {
            Scope scope = Ambient;
            return scope == null || scope.Continue();
        }

        public static Scope Begin(string what, int? uiBudgetMs = null, int knownUnits = 0)
        {
            int budget = uiBudgetMs ?? DefaultUiBudgetMs;
            if (budget < 1000) budget = 1000;
            if (budget > MaxUiBudgetMs) budget = MaxUiBudgetMs;
            return new Scope(what, budget, knownUnits);
        }

        public sealed class Scope
        {
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly string _what;
            private readonly int _budgetMs;
            private readonly int _knownUnits;
            private int _done;
            private string _stoppedBecause;

            internal Scope(string what, int budgetMs, int knownUnits)
            {
                _what = what;
                _budgetMs = budgetMs;
                _knownUnits = knownUnits;
            }

            public int Done => _done;
            public bool Complete => _stoppedBecause == null;
            public long ElapsedMs => _clock.ElapsedMilliseconds;

            /// <summary>
            /// Count one unit of work and answer whether to carry on.
            ///
            /// Called BETWEEN units, never inside one: stopping mid-element would leave a
            /// half-read element in the results, which is a different and worse failure
            /// than reading fewer of them.
            /// </summary>
            public bool Continue()
            {
                _done++;
                if (_stoppedBecause != null) return false;

                // The abandonment check is the cheap one and the one that matters most:
                // continuing to spend the UI thread on an answer nobody will receive is
                // the worst use of it available.
                Func<bool> abandoned = Abandoned;
                if (abandoned != null)
                {
                    bool gone;
                    try { gone = abandoned(); } catch { gone = false; }
                    if (gone) { _stoppedBecause = "client_abandoned"; return false; }
                }

                if (_clock.ElapsedMilliseconds >= _budgetMs)
                {
                    _stoppedBecause = "ui_budget_exhausted";
                    return false;
                }
                return true;
            }

            /// <summary>
            /// The scope's own account of itself. It goes in the reply - always, including
            /// when the read finished - because a caller must be able to tell a complete
            /// answer from a partial one without inferring it from a count.
            /// </summary>
            public JObject Report() => new JObject
            {
                ["what"] = _what,
                ["complete"] = Complete,
                ["units_done"] = _done,
                ["units_known"] = _knownUnits > 0 ? (JToken)_knownUnits : JValue.CreateNull(),
                ["elapsed_ms"] = _clock.ElapsedMilliseconds,
                ["ui_budget_ms"] = _budgetMs,
                ["stopped_because"] = _stoppedBecause == null ? (JToken)JValue.CreateNull() : _stoppedBecause,
                ["means"] = Complete
                    ? "the read finished. Every unit in scope was examined."
                    : (_stoppedBecause == "client_abandoned"
                        ? "THE RESULT IS PARTIAL. The caller went away, so the read stopped rather than spend " +
                          "Revit's UI thread producing an answer for nobody. What is here was measured; what is " +
                          "missing was never looked at, which is not the same as nothing being there."
                        : "THE RESULT IS PARTIAL. The read held Revit's UI thread for its whole declared budget " +
                          "of " + _budgetMs + " ms and stopped. Revit is unresponsive for exactly as long as a " +
                          "command runs; the budget makes that a number somebody chose. Raise it, or narrow the " +
                          "scope. What is missing was never looked at.")
            };

            /// <summary>
            /// The sentence a caller has to see when the answer is partial, or null.
            /// Separate from the report so a command can put it where a human reads.
            /// </summary>
            public string PartialWarning() => Complete
                ? null
                : "PARTIAL: " + _what + " examined " + _done +
                  (_knownUnits > 0 ? " of " + _knownUnits : "") + " and stopped (" + _stoppedBecause +
                  "). Do not read this as a clean result.";
        }

        /// <summary>
        /// A scope must never be opened inside a transaction. Reads are resumable;
        /// writes are atomic, and turning an atomic write into a partially-applied one
        /// would be the opposite of the guarantee this codebase makes everywhere else.
        /// </summary>
        public static void Assert(bool insideTransaction)
        {
            if (insideTransaction)
                throw new InvalidOperationException(
                    "A cooperative read scope was opened inside an open transaction. Reads may stop early and " +
                    "report a partial result; a WRITE may not, because a write stopped halfway leaves a model " +
                    "in a state nobody chose. Whole-or-nothing is the transaction's job and this must not " +
                    "compete with it.");
        }
    }
}
