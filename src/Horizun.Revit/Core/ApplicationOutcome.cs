// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT A COMMAND ACTUALLY DID TO THE MODEL, as one word a program can branch on.
//
// THE DEFECT THIS EXISTS TO FIX, found in review. ExecutePlanCommand read
// CommandResult.Success and nothing else, and treated it as "this action was
// completely applied and verified". Success does not mean that and never did - it
// means the command answered instead of throwing. Measured against this tree, four
// children return Success=true over a model they did not change:
//
//   * write_params_verified, on_failure='atomic' with any row failing: it rolls the
//     batch back, sets transaction_status to the status Revit returned, and answers
//     Ok. Nothing was written.
//   * write_params_verified, SilentRollbackException: every counter is zeroed, the
//     note says so, and the result is Ok.
//   * write_params_verified, on_failure='best_effort': commits what worked and
//     reports the rest. Partial, and Ok.
//   * the recipe tools, when a re-read count disagrees: all_verified=false with a
//     note that says "do not treat this as finished", returned as Ok.
//
// So a confirmed plan could roll one action back, keep going, run a DELETE behind
// it, assimilate the group and answer actions_verified = executed.Count. Every
// number in that reply is true about calls that returned; none is true about the
// model. It is the exact failure Guard.cs was written to end, one level up.
//
// THE RULE. Success is the transport answering. This is the model answering, and
// the two are kept apart on purpose:
//
//   transport success  -> the command replied (CommandResult.Success)
//   command success    -> the command did its job without error
//   full application   -> every requested change is IN THE MODEL and was RE-READ
//
// Only the third one lets a plan assimilate its TransactionGroup, and only a state
// this file grants may be read as the third one.
//
// WHY A DECLARED FIELD AND NOT AN INFERENCE. The obvious shortcut is for the plan
// to sniff the child's payload - look for a `failed` key, an `unresolved` key, an
// `all_verified` bool. That is a list of field names instead of a list of tool
// names: same fragility, moved. A command that renames a counter, or a new command
// that spells its own, silently becomes "verified" again - and the failure is
// invisible, because the shape that stops being recognised is the shape that says
// something went wrong. So every child DECLARES, through Classify below so the
// arithmetic is not re-invented per command, and an undeclared child reads as
// Uncertain. Fail-closed: the plan refuses what it cannot read rather than
// assuming the best about it.
//
// Revit-free on purpose: this is the decision whose honesty has to be provable in
// CI without loading Autodesk assemblies, and every state that matters (a rollback
// that returned Pending, a batch where one row is unknown) is exactly what a live
// Revit will not produce on demand.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>
    /// What happened to the model, in the vocabulary a plan is allowed to branch on.
    /// Uncertain is first and is the default for a reason: an unreadable answer must
    /// never fall into a state that lets work continue on top of it.
    /// </summary>
    public enum ApplicationState
    {
        /// <summary>We cannot tell. Not applied, not failed - unmeasured. Never assimilable.</summary>
        Uncertain = 0,

        /// <summary>Every requested change is in the model and was re-read after the commit.</summary>
        VerifiedApplied,

        /// <summary>A valid dry run: resolvable end to end, and nothing was written.</summary>
        Rehearsed,

        /// <summary>Nothing was requested, so nothing was written. Legitimately complete.</summary>
        NoOp,

        /// <summary>Some of what was requested landed and some did not.</summary>
        Partial,

        /// <summary>The transaction was reverted - deliberately or silently. Nothing landed.</summary>
        RolledBack,

        /// <summary>Changes were requested and none of them landed.</summary>
        Failed
    }

    public static class ApplicationOutcome
    {
        /// <summary>The payload key every mutating command stamps its declaration into.</summary>
        public const string Key = "application";

        // The transaction-status vocabulary this file recognises. Anything else is
        // uncertainty, INCLUDING a value that looks reassuring: Revit's own
        // TransactionStatus has Pending, Error, Proceed and Uninitialized, and none of
        // them is a commit.
        public const string Committed = "Committed";
        public const string NotStarted = "not_started";
        public const string RolledBackStatus = "RolledBack";

        private const string SUncertain = "uncertain";
        private const string SVerifiedApplied = "verified_applied";
        private const string SRehearsed = "rehearsed";
        private const string SNoOp = "no_op";
        private const string SPartial = "partial";
        private const string SRolledBack = "rolled_back";
        private const string SFailed = "failed";

        /// <summary>The wire name of a state. One spelling, used by every command and by the plan.</summary>
        public static string Name(ApplicationState state)
        {
            switch (state)
            {
                case ApplicationState.VerifiedApplied: return SVerifiedApplied;
                case ApplicationState.Rehearsed: return SRehearsed;
                case ApplicationState.NoOp: return SNoOp;
                case ApplicationState.Partial: return SPartial;
                case ApplicationState.RolledBack: return SRolledBack;
                case ApplicationState.Failed: return SFailed;
                default: return SUncertain;
            }
        }

        /// <summary>
        /// A wire name back to a state. An unrecognised name is Uncertain and returns
        /// false - a caller that wants to know whether it READ a state or merely got the
        /// default can tell the two apart, which is the difference between "this command
        /// says unknown" and "this command said nothing".
        /// </summary>
        public static bool TryParse(string name, out ApplicationState state)
        {
            switch (name)
            {
                case SVerifiedApplied: state = ApplicationState.VerifiedApplied; return true;
                case SRehearsed: state = ApplicationState.Rehearsed; return true;
                case SNoOp: state = ApplicationState.NoOp; return true;
                case SPartial: state = ApplicationState.Partial; return true;
                case SRolledBack: state = ApplicationState.RolledBack; return true;
                case SFailed: state = ApplicationState.Failed; return true;
                case SUncertain: state = ApplicationState.Uncertain; return true;
                default: state = ApplicationState.Uncertain; return false;
            }
        }

        /// <summary>
        /// THE ONE QUESTION A PLAN MAY ASSIMILATE ON. True for exactly two states: every
        /// requested change verified in the model, and nothing requested at all. Partial,
        /// RolledBack, Failed, Uncertain and Rehearsed are all false - a rehearsal above
        /// all, because a dry run inside a confirmed apply means the write never ran.
        /// </summary>
        public static bool IsFullyApplied(ApplicationState state)
            => state == ApplicationState.VerifiedApplied || state == ApplicationState.NoOp;

        /// <summary>
        /// Whether a dry run may be turned into an executable confirmation. Only a clean
        /// rehearsal: one that resolved everything it was given and wrote nothing.
        /// </summary>
        public static bool IsValidRehearsal(ApplicationState state)
            => state == ApplicationState.Rehearsed;

        /// <summary>
        /// A DRY RUN's state. It never touched the model, so the only question is whether
        /// what it was given resolves end to end - a rehearsal that could not resolve half
        /// its rows has not rehearsed the request, it has rehearsed the half it understood,
        /// and a token issued on it authorises an apply nobody previewed.
        /// </summary>
        public static ApplicationState Rehearsal(int unresolved, int failed, int unknown)
        {
            if (unknown > 0) return ApplicationState.Uncertain;
            if (unresolved > 0 || failed > 0) return ApplicationState.Partial;
            return ApplicationState.Rehearsed;
        }

        /// <summary>
        /// AN APPLY's state, from the transaction's real outcome and the counts the command
        /// re-read from the model. The order of these tests is the whole rule:
        ///
        ///   1. The transaction first. Nothing measured inside a transaction that did not
        ///      commit means anything, so a status that is not "Committed" is answered
        ///      before a single counter is looked at.
        ///   2. Unknown second. One row we could not read back poisons any claim over the
        ///      batch - "we could not look" is not "it is there" and not "it is absent".
        ///   3. Then nothing-requested, then nothing-landed, then partial.
        ///
        /// `applied` is what the model was measured to carry; `verified` is what was
        /// re-read AFTER the commit and matched what the caller asked for. A command that
        /// can only prove the weaker of the two passes the weaker one - the point is that
        /// it cannot pass the stronger one by accident.
        /// </summary>
        public static ApplicationState Applied(string transactionStatus, int requested, int applied,
                                               int verified, int unresolved, int failed, int unknown)
        {
            // Counts are evidence, not hints. A negative or internally impossible tally is
            // corrupt bookkeeping and must never be repaired into NoOp/VerifiedApplied.
            if (requested < 0 || applied < 0 || verified < 0 || unresolved < 0 || failed < 0 || unknown < 0)
                return ApplicationState.Uncertain;
            if (verified > applied || applied > requested)
                return ApplicationState.Uncertain;

            if (string.IsNullOrEmpty(transactionStatus)) return ApplicationState.Uncertain;

            if (string.Equals(transactionStatus, RolledBackStatus, StringComparison.Ordinal))
                return ApplicationState.RolledBack;

            if (string.Equals(transactionStatus, NotStarted, StringComparison.Ordinal))
            {
                // No transaction was opened on an apply. Legitimate only when there was
                // nothing to do; otherwise it is the zero-writes case wearing a success.
                if (requested == 0)
                    return applied == 0 && verified == 0 && unresolved == 0 && failed == 0 && unknown == 0
                        ? ApplicationState.NoOp
                        : ApplicationState.Uncertain;
                return ApplicationState.Failed;
            }

            if (!string.Equals(transactionStatus, Committed, StringComparison.Ordinal))
                return ApplicationState.Uncertain;

            if (unknown > 0) return ApplicationState.Uncertain;
            if (requested == 0)
                return applied == 0 && verified == 0 && unresolved == 0 && failed == 0
                    ? ApplicationState.NoOp
                    : ApplicationState.Uncertain;
            if (applied <= 0) return ApplicationState.Failed;
            if (unresolved > 0 || failed > 0) return ApplicationState.Partial;
            if (applied < requested || verified < requested) return ApplicationState.Partial;
            return ApplicationState.VerifiedApplied;
        }

        /// <summary>
        /// The declaration block, built once so every command spells it the same way.
        /// It carries the counts as well as the verdict, because a plan that refuses an
        /// action has to be able to say WHICH of the seven states it read and on what.
        /// </summary>
        public static JObject Declare(ApplicationState state, string transactionStatus, int requested,
                                      int applied, int verified, int unresolved, int failed, int unknown)
        {
            return new JObject
            {
                ["state"] = Name(state),
                ["fully_applied"] = IsFullyApplied(state),
                ["transaction_status"] = transactionStatus,
                ["requested"] = requested,
                ["applied"] = applied,
                ["verified"] = verified,
                ["unresolved"] = unresolved,
                ["failed"] = failed,
                ["unknown"] = unknown,
                ["state_means"] = Means(state)
            };
        }

        /// <summary>Classify and declare in one call - the shape every command uses on an apply.</summary>
        public static JObject DeclareApplied(string transactionStatus, int requested, int applied,
                                             int verified, int unresolved, int failed, int unknown)
            => Declare(Applied(transactionStatus, requested, applied, verified, unresolved, failed, unknown),
                       transactionStatus, requested, applied, verified, unresolved, failed, unknown);

        /// <summary>Classify and declare in one call - the shape every command uses on a dry run.</summary>
        public static JObject DeclareRehearsal(int requested, int unresolved, int failed, int unknown)
            => Declare(Rehearsal(unresolved, failed, unknown), NotStarted, requested, 0, 0,
                       unresolved, failed, unknown);

        /// <summary>
        /// Machine-readable failure after a write was attempted. This is deliberately a
        /// failure diagnostic rather than a success payload: callers must see both that a
        /// command failed and that the model may already carry an object.
        /// </summary>
        public static JObject FailureAfterWrite(string idField, long? id, string stage,
                                                string transactionStatus, ApplicationState state,
                                                bool objectReread, JObject evidence = null)
        {
            if (string.IsNullOrWhiteSpace(idField)) throw new ArgumentException("idField is required", nameof(idField));
            int applied = objectReread ? 1 : 0;
            int failed = state == ApplicationState.Partial ? 1 : 0;
            int unknown = state == ApplicationState.Uncertain ? 1 : 0;
            var detail = new JObject
            {
                ["write_started"] = true,
                ["stage"] = stage,
                ["transaction_status"] = transactionStatus,
                [idField] = id.HasValue ? (JToken)id.Value : JValue.CreateNull(),
                [Key] = Declare(state, transactionStatus, 1, applied, 0, 0, failed, unknown)
            };
            if (evidence != null) detail["evidence"] = evidence.DeepClone();
            return detail;
        }

        /// <summary>Stamp a declaration into a payload. Null payload is a no-op, never a throw.</summary>
        public static void Stamp(JObject payload, JObject declaration)
        {
            if (payload == null || declaration == null) return;
            payload[Key] = declaration;
        }

        /// <summary>Stamp an apply declaration, computed here.</summary>
        public static void StampApplied(JObject payload, string transactionStatus, int requested, int applied,
                                        int verified, int unresolved, int failed, int unknown)
            => Stamp(payload, DeclareApplied(transactionStatus, requested, applied, verified,
                                             unresolved, failed, unknown));

        /// <summary>Stamp a dry-run declaration, computed here.</summary>
        public static void StampRehearsal(JObject payload, int requested, int unresolved, int failed, int unknown)
            => Stamp(payload, DeclareRehearsal(requested, unresolved, failed, unknown));

        /// <summary>
        /// READ a declaration back out of whatever a command returned as its data.
        ///
        /// TOTAL AND FAIL-CLOSED. A null payload, a payload that is not an object, a
        /// missing block, a block that is not an object, a state nobody recognises - all
        /// Uncertain. That is the case this whole file exists for: the plan must treat
        /// "this command told me nothing about what it did" exactly as seriously as
        /// "this command told me it rolled back", because the model is in the same
        /// unmeasured condition either way.
        ///
        /// AND THE STATE STRING IS NOT TAKEN ON ITS OWN WORD WHEN IT CLAIMS SOMETHING
        /// ASSIMILABLE. Measured during the audit of this change: 15 of 29 adversarial
        /// blocks were accepted, including `verified_applied` beside
        /// transaction_status="RolledBack", beside unknown=4, beside requested=10 with
        /// verified=0, and with no counters at all. Every current command computes the
        /// state from its own counts, so none of those is reachable from this tree today -
        /// which is exactly the argument that would have to be re-made after every future
        /// edit, and this file exists because that argument is not worth making twice.
        /// Declare() also accepts an explicit state by design, so the door is open by
        /// construction.
        ///
        /// So a claim of full application is CORROBORATED against the block's own numbers
        /// before it is believed. Non-assimilable states are taken as declared: they cannot
        /// cause the harm this guards against, and downgrading a command's own diagnosis to
        /// "uncertain" would throw away the more useful answer (the purge branch that could
        /// not look, WriteTally's contradiction verdict - both deliberately more cautious
        /// than their counters alone would be). A command may always know LESS than its
        /// numbers suggest. It may not know more.
        /// </summary>
        public static ApplicationState Read(object data)
        {
            JObject payload = AsObject(data);
            if (payload == null) return ApplicationState.Uncertain;
            var block = payload[Key] as JObject;
            if (block == null) return ApplicationState.Uncertain;
            var name = block["state"] as JValue;
            ApplicationState declared;
            if (!TryParse(name?.Value as string, out declared)) return ApplicationState.Uncertain;

            // Three states open a gate: VerifiedApplied and NoOp let the apply continue;
            // Rehearsed mints the executable confirmation. Every one must be corroborated.
            bool opensGate = IsFullyApplied(declared) || IsValidRehearsal(declared);
            if (!opensGate) return declared;
            return Corroborated(block, declared) ? declared : ApplicationState.Uncertain;
        }

        /// <summary>
        /// Do the block's OWN numbers produce the assimilable state it claims?
        ///
        /// One arithmetic rule, reused: Applied() is what every command already classifies
        /// with, so this cannot drift into a second opinion. What is added on top are the
        /// three coherence facts Applied() has no reason to test, because a command that
        /// counts honestly cannot produce them:
        ///
        ///   verified &lt;= applied      you cannot re-read more than you wrote
        ///   applied  &lt;= requested    you cannot write more than was asked for
        ///   every counter present, integral, non-negative and inside int range
        ///
        /// Deliberately NOT tested: unresolved + failed + unknown &lt;= requested. It looks
        /// like an invariant and it is not - write_params classifies an unresolved row as
        /// not-written too, so a batch where every row fails to resolve legitimately
        /// reports unresolved=4 with failed=4 over requested=4. A check that rejected that
        /// would refuse real results, which is the failure mode opposite to this one and
        /// just as bad.
        /// </summary>
        private static bool Corroborated(JObject block, ApplicationState declared)
        {
            // A `fully_applied` that disagrees with its own state is a block assembled by
            // something that was not this file.
            var fullyApplied = block["fully_applied"] as JValue;
            if (fullyApplied == null || fullyApplied.Type != JTokenType.Boolean ||
                (bool)fullyApplied.Value != IsFullyApplied(declared))
                return false;

            string status = (block["transaction_status"] as JValue)?.Value as string;
            if (string.IsNullOrEmpty(status)) return false;

            long requested, applied, verified, unresolved, failed, unknown;
            if (!Counter(block, "requested", out requested)) return false;
            if (!Counter(block, "applied", out applied)) return false;
            if (!Counter(block, "verified", out verified)) return false;
            if (!Counter(block, "unresolved", out unresolved)) return false;
            if (!Counter(block, "failed", out failed)) return false;
            if (!Counter(block, "unknown", out unknown)) return false;

            if (verified > applied) return false;
            if (applied > requested) return false;

            if (declared == ApplicationState.Rehearsed)
            {
                return string.Equals(status, NotStarted, StringComparison.Ordinal) &&
                       applied == 0 && verified == 0 && unresolved == 0 && failed == 0 && unknown == 0;
            }

            ApplicationState recomputed = Applied(status, (int)requested, (int)applied, (int)verified,
                                                  (int)unresolved, (int)failed, (int)unknown);
            return recomputed == declared;
        }

        /// <summary>
        /// One counter, as a number this file is willing to reason about: present, a JSON
        /// integer, not negative, and inside int range. A string "five", an array, an
        /// absent key or a value past int.MaxValue all answer false - none of them is a
        /// count, and guessing what one meant is how a malformed block becomes an applied
        /// one.
        /// </summary>
        private static bool Counter(JObject block, string field, out long value)
        {
            value = 0;
            var token = block[field] as JValue;
            if (token == null || token.Type != JTokenType.Integer) return false;
            try { value = Convert.ToInt64(token.Value); }
            catch { return false; }
            return value >= 0 && value <= int.MaxValue;
        }

        /// <summary>
        /// Whether the payload carried a declaration at all - so a refusal can say "this
        /// command does not report what it did" instead of "this command reported
        /// uncertain", which are different bugs to go and fix.
        /// </summary>
        public static bool IsDeclared(object data)
        {
            JObject payload = AsObject(data);
            return payload != null && payload[Key] is JObject;
        }

        private static JObject AsObject(object data)
        {
            if (data == null) return null;
            var token = data as JToken;
            if (token == null)
            {
                try { token = JToken.FromObject(data); }
                catch { return null; }
            }
            return token as JObject;
        }

        private static string Means(ApplicationState state)
        {
            switch (state)
            {
                case ApplicationState.VerifiedApplied:
                    return "verified_applied: every requested change is in the model and was re-read after the " +
                           "commit. This is the only state besides no_op that lets a plan keep its work.";
                case ApplicationState.Rehearsed:
                    return "rehearsed: a valid dry run. Everything resolved and NOTHING was written.";
                case ApplicationState.NoOp:
                    return "no_op: nothing was requested, so nothing was written. Complete, legitimately.";
                case ApplicationState.Partial:
                    return "partial: some of what was requested landed and some did not. Do not build on it - " +
                           "inside an atomic plan this rolls the whole group back.";
                case ApplicationState.RolledBack:
                    return "rolled_back: the transaction was reverted, deliberately or silently by Revit. " +
                           "Nothing from it reached the model.";
                case ApplicationState.Failed:
                    return "failed: changes were requested and none of them landed.";
                default:
                    return "uncertain: what reached the model was not measured. This is NOT 'nothing happened' " +
                           "and NOT 'it worked' - it is the absence of evidence, and no work may be stacked on it.";
            }
        }
    }
}
