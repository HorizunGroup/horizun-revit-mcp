// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// TURNING A COMMAND'S OWN COUNTERS INTO A DECLARATION, without the two arithmetic
// mistakes review found in the first pass of that work.
//
// ApplicationOutcome decides what a set of counts MEANS. It cannot check that the
// counts handed to it were assembled correctly, and both defects below produced
// perfectly well-formed counts that described something other than the model:
//
//   1. A HARDCODED STATUS. create_schedule called tx.Commit() and threw the
//      TransactionStatus away, then stamped the literal "Committed". Revit's
//      Commit() returns RolledBack or Pending WITHOUT throwing - that is the whole
//      reason Guard exists - so the one field a caller reads to learn what happened
//      to the transaction was a constant, true by construction. OneObject takes the
//      status as an argument for exactly that reason: there is no overload that
//      assumes it.
//
//   2. A MIXED BUCKET COUNTED TWICE. set_keynote appends to one `failed` array from
//      three different places: ids that never resolved to a target, targets whose
//      Parameter.Set was refused, and (separately) targets the post-commit read did
//      not confirm. Deriving `requested` as targets + failed.Count therefore counted
//      a target whose write was refused TWICE - once as a target, once as a failure -
//      and reported a refused write as an unresolved id. PerTarget takes the three
//      as three arguments so they cannot merge on the way in.
//
// Revit-free, because both rules are arithmetic and both are things a live Revit
// will not produce on demand: a Commit that returns Pending, or a batch where a
// Set() is refused on one target and the read-back fails on another.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class WriteTally
    {
        /// <summary>
        /// ONE object was requested - a schedule, a binding, a single created thing.
        ///
        /// <paramref name="commitStatus"/> must be the value the commit actually RETURNED.
        /// Passing a literal here is the defect this signature exists to make visible: a
        /// status that was never read cannot be told from one that was, so the argument is
        /// mandatory and the caller has to have something to put in it.
        ///
        /// verified_applied is reachable only when the commit came back Committed AND the
        /// post-commit read confirmed the object. A commit that returned RolledBack is a
        /// rollback; Pending, Error or anything unrecognised is uncertain; a confirmed
        /// commit whose post-condition did not hold is a failure, not a success with a
        /// caveat.
        /// </summary>
        public static JObject OneObject(string commitStatus, bool postconditionVerified)
            => ApplicationOutcome.DeclareApplied(
                   commitStatus,
                   requested: 1,
                   applied: postconditionVerified ? 1 : 0,
                   verified: postconditionVerified ? 1 : 0,
                   unresolved: 0,
                   failed: postconditionVerified ? 0 : 1,
                   unknown: 0);

        /// <summary>
        /// A batch addressed BY ID, where three different things go wrong and each must be
        /// counted exactly once:
        ///
        ///   unresolvedIds       an id that never became a target. It asked for something
        ///                       and produced nothing to write to.
        ///   unverifiedTargets   a target the post-commit read did not find carrying the
        ///                       value - which includes every target whose write was
        ///                       refused, because a refused write leaves the old value and
        ///                       the read says so. Counting refusals AGAIN on top of this
        ///                       is the double count.
        ///   verifiedTargets     a target the post-commit read confirmed.
        ///
        /// `requested` is resolvedTargets + unresolvedIds, so a write that failed on a
        /// target already inside resolvedTargets does NOT inflate what was asked for.
        ///
        /// STRICTLY FAIL-CLOSED, and this is the part review had to send back. The first
        /// version clamped negatives to zero and let over-counts through, which turned
        /// corrupt input into the two states a plan is allowed to keep:
        ///
        ///   * all four negative clamped to (0,0,0,0), which is NoOp - assimilable.
        ///   * resolved=1, verified=2 passed straight through as verified_applied, because
        ///     the classifier only ever asks whether verified is at least requested.
        ///
        /// A clamp is a repair, and there is nothing here to repair: counts that cannot
        /// describe any real batch mean the caller's bookkeeping is broken, and the honest
        /// answer to broken bookkeeping is that nothing about this batch is known. Every
        /// impossible combination is Uncertain, the RAW numbers are published exactly as
        /// they were passed, and `counts_contradict` names which rule they broke.
        /// </summary>
        public static JObject PerTarget(string commitStatus, int resolvedTargets, int unresolvedIds,
                                        int verifiedTargets, int unverifiedTargets)
        {
            string contradiction = Contradiction(resolvedTargets, unresolvedIds, verifiedTargets, unverifiedTargets);
            if (contradiction != null)
            {
                // Declared, not classified: the classifier's inputs assume counts that
                // describe something, and these do not. Raw values go out unaltered - a
                // caller has to be able to see what it actually passed.
                // Saturated rather than wrapped: this field is an int, and the inputs that
                // produced it are published raw beside it (resolved_targets, unresolved), so
                // a reader still has the real numbers. A wrapped negative here would be a
                // third wrong number in a reply whose whole point is that the counts are wrong.
                long impossibleRequested = (long)resolvedTargets + unresolvedIds;
                JObject impossible = ApplicationOutcome.Declare(
                    ApplicationState.Uncertain, commitStatus,
                    requested: impossibleRequested > int.MaxValue ? int.MaxValue
                             : impossibleRequested < int.MinValue ? int.MinValue
                             : (int)impossibleRequested,
                    applied: verifiedTargets,
                    verified: verifiedTargets,
                    unresolved: unresolvedIds,
                    failed: unverifiedTargets,
                    unknown: 0);
                impossible["counts_contradict"] = contradiction;
                impossible["resolved_targets"] = resolvedTargets;
                return impossible;
            }

            // Every target is now accounted for or explicitly not: the difference is what
            // the post-commit pass never reached, and one of those makes the batch
            // uncertain rather than being absorbed into agreement.
            int unmeasured = resolvedTargets - verifiedTargets - unverifiedTargets;

            return ApplicationOutcome.DeclareApplied(
                commitStatus,
                requested: resolvedTargets + unresolvedIds,
                applied: verifiedTargets,
                verified: verifiedTargets,
                unresolved: unresolvedIds,
                failed: unverifiedTargets,
                unknown: unmeasured);
        }

        /// <summary>
        /// Why these four numbers cannot describe a real batch, or null when they can.
        ///
        /// One sentence per rule, because "the counts are wrong" sends somebody reading
        /// four call sites. Arithmetic in long so a caller near int.MaxValue cannot wrap a
        /// sum into looking legal.
        /// </summary>
        private static string Contradiction(int resolvedTargets, int unresolvedIds,
                                            int verifiedTargets, int unverifiedTargets)
        {
            if (resolvedTargets < 0) return "resolved_targets is negative (" + resolvedTargets + ")";
            if (unresolvedIds < 0) return "unresolved_ids is negative (" + unresolvedIds + ")";
            if (verifiedTargets < 0) return "verified_targets is negative (" + verifiedTargets + ")";
            if (unverifiedTargets < 0) return "unverified_targets is negative (" + unverifiedTargets + ")";

            if (verifiedTargets > resolvedTargets)
                return "verified_targets (" + verifiedTargets + ") exceeds resolved_targets (" + resolvedTargets +
                       "): more targets were confirmed than were ever resolved to write to";
            if (unverifiedTargets > resolvedTargets)
                return "unverified_targets (" + unverifiedTargets + ") exceeds resolved_targets (" + resolvedTargets +
                       "): more targets failed than were ever resolved to write to";

            long accounted = (long)verifiedTargets + unverifiedTargets;
            if (accounted > resolvedTargets)
                return "verified_targets + unverified_targets (" + accounted + ") exceeds resolved_targets (" +
                       resolvedTargets + "): at least one target was counted twice";

            // `requested` is published as a JSON integer and every consumer reads it as one,
            // so a sum that does not fit is not a large number - it is a number that would
            // WRAP NEGATIVE and then classify as a no-op, which is the assimilable state.
            // Near int.MaxValue this is arithmetic, not an attack, and the honest answer is
            // that a batch this size was not measured by anything here.
            long requested = (long)resolvedTargets + unresolvedIds;
            if (requested > int.MaxValue)
                return "resolved_targets + unresolved_ids (" + requested + ") does not fit the integer this " +
                       "contract publishes, so what was requested cannot be reported at all";

            return null;
        }
    }
}
