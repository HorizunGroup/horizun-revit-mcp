// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT `rollback_scope: per_action` MEANS, in the code that means it.
//
// The loop that applies a confirmed correction plan used to live inside
// ApplyCorrectionsCommand, where it needs a UIApplication and therefore could not
// be exercised at a desk. Every question anyone asks about a partial result -
// does an action that failed undo one that applied? can an action whose
// transaction failed still read as applied? what does a call report when it
// cannot tell? - was answerable only by getting Revit to fail on cue, which it
// will not do.
//
// So the loop is here, Revit-free, driven by a delegate the caller supplies. The
// command passes the real child dispatch; the tests pass outcomes they choose.
// There is NO failure switch in the product: the substitutable thing is the step
// executor itself, and the only executor the shipped command ever builds is the
// one that calls the typed child.
//
// THE THREE OUTCOMES AN ACTION MAY HAVE, and the third is the point:
//
//   applied   - every step came back Success AND fully applied, re-read after
//               the commit by the typed tool itself.
//   failed    - a step did not apply and we KNOW nothing of it landed: it was
//               refused before its transaction, or its transaction rolled back.
//   uncertain - a step's postcondition could NOT be read. The write may have
//               happened. Reporting that as `failed` would claim knowledge
//               nobody has, and reporting it as `applied` would be worse.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>What one step's execution came back as, in terms this loop judges.</summary>
    public sealed class StepExecution
    {
        /// <summary>The child call itself succeeded.</summary>
        public bool Success;

        /// <summary>What the child DECLARED about the model after its commit.</summary>
        public ApplicationState State = ApplicationState.Uncertain;

        public string Error;
        public JToken Data;

        /// <summary>
        /// The step never ran: no permitted tool for it. Nothing was written, and
        /// that is a stronger statement than "it failed".
        /// </summary>
        public static StepExecution NotStarted(string why) =>
            new StepExecution { Success = false, State = ApplicationState.NoOp, Error = why };
    }

    public static class CorrectionApplyLoop
    {
        public const string RollbackScope = "per_action";

        public const string RollbackMeans =
            "PER ACTION, and exactly this: (1) every action is rehearsed BEFORE any of them is applied, and a " +
            "plan with an action that did not rehearse cleanly is refused whole, with nothing written. " +
            "(2) Each action's steps run inside their typed tool's own transaction; a step that fails is rolled " +
            "back BY THAT TOOL, and this command opens no transaction of its own. (3) An action that fails does " +
            "NOT undo an action that already applied, and does not stop the actions after it - each one is " +
            "attempted and reported on its own. (4) An action whose postcondition could not be read is reported " +
            "`uncertain`, not `failed`: the write may have happened, and its elements come back not_verifiable " +
            "from the re-audit rather than corrected or failed. (5) This is NOT one atomic group and does not " +
            "claim to be; compose horizun_execute_plan for that.";

        /// <summary>
        /// Apply every action in order. `execute` runs one step and says what came
        /// back; this decides what each ACTION is, and writes it onto the actions.
        /// </summary>
        public static void Apply(IEnumerable<CorrectionAction> actions, Func<CorrectionStep, StepExecution> execute)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));

            foreach (CorrectionAction action in actions ?? Enumerable.Empty<CorrectionAction>())
            {
                bool allOk = true;
                bool inDoubt = false;

                foreach (CorrectionStep step in action.Steps)
                {
                    StepExecution outcome = execute(step) ?? new StepExecution { Error = "the executor returned nothing" };

                    step.ApplyOk = outcome.Success && ApplicationOutcome.IsFullyApplied(outcome.State);
                    step.ApplyState = ApplicationOutcome.Name(outcome.State);
                    step.ApplyError = outcome.Error;
                    step.ApplyData = outcome.Data;

                    if (step.ApplyOk == true) continue;
                    allOk = false;
                    // UNCERTAIN IS NOT FAILED. A child that could not re-read its own
                    // work may still have written it, and an action carrying one of
                    // those cannot be reported as though nothing happened.
                    if (outcome.State == ApplicationState.Uncertain) inDoubt = true;
                }

                if (allOk)
                {
                    action.State = CorrectionActionState.Applied;
                    continue;
                }

                action.State = inDoubt ? CorrectionActionState.Uncertain : CorrectionActionState.Failed;
                action.Why = inDoubt
                    ? "at least one typed call could not re-read its own work, so whether it landed is UNKNOWN - " +
                      "not known to have failed. " + RollbackMeans
                    : "at least one typed call did not come back fully applied and verified; read the steps. " +
                      RollbackMeans;
            }
        }
    }
}
