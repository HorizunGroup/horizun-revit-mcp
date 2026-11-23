// -----------------------------------------------------------------------------
// Horizun Revit MCP - the honest account of a read that stopped early. Original
// Horizun code.
//
// ONE SHAPE FOR EVERY BUDGETED READER. model_scan, query_model, audit_model,
// quantities and query_planimetry all accept the same opt-in `cooperative` object
// (Core/CooperativeReadOptions.cs). What they owe the caller when the budget runs
// out is also the same, so it lives here once instead of five times:
//
//   cooperative.measured_account   WHAT was measured and WHAT was not, by name or by
//                                  range, plus the grain at which this reader can stop.
//   partial / coverage_complete    false, with partial_reason.
//   cursor                         issued only when the reader can resume at a position
//                                  that still means what it meant (audit checks,
//                                  quantities elements). Otherwise null and the reply says
//                                  why, instead of handing out a token that resumes nothing.
//
// NOTHING IS ADDED WHEN THE CALLER DID NOT ASK. Options.Requested false returns before
// touching the result: the reply of a request without `cooperative` is byte for byte the
// reply it always was.
//
// WHAT IS NEVER CLAIMED. Under not_measured the reader says "never looked at". It does
// not say "empty", "clean" or "zero": an absence of a finding in a stopped read is an
// absence of a look.
//
// Revit-free on purpose (JSON in, JSON out) so the arithmetic is provable without a
// building; the commands pass in what they know.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class CooperativePartial
    {
        /// <summary>The block that says what a stop covers: what is in, what is out, how fine the stop is.</summary>
        public static JObject Account(string unit, int examined, int? known, JToken measured, JToken notMeasured,
                                      string stopGrain)
        {
            return new JObject
            {
                ["unit"] = unit,
                ["examined"] = examined,
                ["known"] = known.HasValue ? (JToken)known.Value : JValue.CreateNull(),
                ["measured"] = measured ?? JValue.CreateNull(),
                ["not_measured"] = notMeasured ?? JValue.CreateNull(),
                ["stop_grain"] = stopGrain,
                ["reading_rule"] = "Everything under `measured` was examined in full. Nothing is claimed about " +
                                   "`not_measured`: it was never looked at, which is not the same as nothing " +
                                   "being there. Do not add a partial reply's totals to a complete one's."
            };
        }

        /// <summary>
        /// Put the cooperative block on a reply and mark the reply partial when the scope
        /// stopped. Returns true when the reply is partial.
        /// </summary>
        /// <param name="nextPosition">Where a continuation starts. Only used when resumable.</param>
        /// <param name="resumable">
        /// True when a cursor at <paramref name="nextPosition"/> is a real continuation. False
        /// when the reader cannot resume (the work done so far is not a prefix a position names).
        /// </param>
        /// <param name="resumeHint">What to do instead when it is not resumable.</param>
        public static bool Apply(JObject result, CooperativeOptions options, CooperativeRead.Scope scope,
                                 string fingerprint, JObject account, bool resumable, int nextPosition,
                                 string resumeHint)
        {
            if (result == null || options == null || !options.Requested) return false;

            bool partial = scope != null && !scope.Complete;
            JObject report = options.Report(scope, nextPosition, fingerprint, partial);
            if (report == null) return false;

            if (account != null) report["measured_account"] = account;

            if (partial && !resumable)
            {
                report["cursor"] = JValue.CreateNull();
                report["collection_continuation_available"] = false;
                report["cursor_means"] = "This reader cannot resume where it stopped: " + resumeHint;
            }
            else if (partial)
            {
                report["collection_continuation_available"] = report["cursor"] != null &&
                                                              report["cursor"].Type != JTokenType.Null;
            }

            result["cooperative"] = report;
            result["partial"] = partial;
            if (partial)
            {
                result["coverage_complete"] = false;
                result["partial_reason"] = scope.StoppedBecause;
            }
            return partial;
        }

        /// <summary>
        /// The refusal for a mode that does not honour a budget. A budget accepted and
        /// ignored is a long freeze with a reassuring field in the reply.
        /// </summary>
        public static string RefuseUnhonoured(string command, string mode, string honouredBy)
        {
            return "'cooperative' is not honoured by " + command + " mode '" + mode + "': it would be accepted and " +
                   "ignored, and the read would still hold Revit's UI thread to the end while the request looked " +
                   "budgeted. It is honoured by: " + honouredBy + ". Drop it, or use one of those. Nothing was read.";
        }
    }

    /// <summary>What a walk does with the next item.</summary>
    public enum WalkStep { Skip, Go, Stop }

    /// <summary>
    /// A cursor position over an ordered population, shared by every reader that resumes by
    /// position (quantities volume, quantities takeoff, the audit's checks). Items before
    /// <c>From</c> belong to an earlier page and are skipped WITHOUT being counted; every other item
    /// asks the scope first. Without a scope the walk visits everything (the original behaviour).
    /// </summary>
    public sealed class PositionWalk
    {
        private readonly CooperativeRead.Scope _scope;
        private int _position;

        public PositionWalk(CooperativeRead.Scope scope, int startAt, int total)
        {
            _scope = scope;
            Total = total;
            From = scope == null ? 0 : Math.Max(0, Math.Min(startAt, total));
            StoppedAt = total;
        }

        public int Total { get; private set; }
        public int From { get; private set; }

        /// <summary>The position of the first item NOT measured because the scope stopped; Total when none.</summary>
        public int StoppedAt { get; private set; }

        public bool Stopped { get { return StoppedAt < Total; } }

        /// <summary>Items measured by THIS call.</summary>
        public int Measured { get { return Math.Max(0, StoppedAt - From); } }

        public WalkStep Next()
        {
            if (_scope == null) { _position++; return WalkStep.Go; }
            if (_position < From) { _position++; return WalkStep.Skip; }
            if (!_scope.Continue()) { StoppedAt = _position; return WalkStep.Stop; }
            _position++;
            return WalkStep.Go;
        }
    }
}
