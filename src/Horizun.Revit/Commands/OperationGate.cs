// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE GATE, CONSULTED BY THE OPERATIONS THIS BRIDGE OWNS.
//
// horizun_save_document and horizun_export call this with their `require_gate`
// argument before they touch the file. It re-runs the audit's checks on the
// document as it stands, evaluates the caller's profile with the audit's own
// evaluator, and decides with the prevention rules. One evaluator, not two:
// the rows a refusal carries are the rows horizun_audit_model would return for
// the same requirement set on the same model at the same moment.
//
// It writes nothing, subscribes to nothing, and is not consulted by anything
// that this bridge does not itself perform - which the reply says every time.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal sealed class OperationGateResult
    {
        /// <summary>Non-null when the operation must not proceed. Return it unchanged.</summary>
        public CommandResult Refusal;

        /// <summary>The `prevention` block the reply carries, refused or not. Null when no gate was asked for.</summary>
        public JObject Prevention;

        public bool Requested { get { return Prevention != null; } }
    }

    internal static class OperationGate
    {
        /// <summary>The default list length when no cited audit fixes it. The audit's own default.</summary>
        private const int DefaultTop = 20;

        /// <summary>
        /// Evaluate `require_gate` for one operation on one document. Null in
        /// `requireGate` means the caller asked for no gate: the operation behaves
        /// exactly as it did before this existed, and the result says nothing.
        /// </summary>
        public static OperationGateResult Evaluate(UIApplication app, Document doc, JToken requireGate,
                                                   string operation, string commandName)
        {
            var result = new OperationGateResult();
            if (requireGate == null || requireGate.Type == JTokenType.Null) return result;

            string parseRefusal;
            RequireGateRequest request = RequireGateRequest.Parse(requireGate as JObject, out parseRefusal);
            if (request == null)
            {
                result.Refusal = CommandResult.Fail(commandName + ": require_gate refused - " + parseRefusal +
                                                    " Nothing was done.");
                return result;
            }

            // A CITED AUDIT MUST BE ONE THIS SESSION PRODUCED. The fingerprint alone
            // says nothing about which top it was taken at; the record does, and the
            // fresh run has to use the same top or the comparison is meaningless.
            int top = DefaultTop;
            if (!string.IsNullOrEmpty(request.FindingSetFingerprint))
            {
                FindingSetRecord cited;
                if (!AuditFindingSetStore.Session.TryGet(request.FindingSetFingerprint, out cited))
                {
                    result.Refusal = CommandResult.FailWithDetail(commandName + ": require_gate cites audit " +
                        request.FindingSetFingerprint + ", which this Revit session never produced. Finding sets " +
                        "do not survive a restart. Run horizun_audit_model again and cite the fingerprint it " +
                        "returns, or omit finding_set_fingerprint to gate on a fresh measurement alone. " +
                        "Nothing was done.",
                        new JObject { ["state"] = "refused", ["decision"] = OperationGateDecision.NotAssessable });
                    return result;
                }
                top = cited.Top;
            }

            AuditModelCommand.AuditOptions options;
            string optionsRefusal = AuditModelCommand.AuditOptions.Read(new JObject
            {
                ["tolerances"] = request.Tolerances,
                ["readiness_roles"] = request.ReadinessRoles,
                ["workset_rules"] = request.WorksetRules,
                ["warning_profile"] = request.WarningProfile
            }, out options);
            if (optionsRefusal != null)
            {
                result.Refusal = CommandResult.Fail(commandName + ": require_gate.profile refused - " + optionsRefusal +
                                                    " Nothing was done.");
                return result;
            }

            // THE MEASUREMENT, NOW. Not a stored snapshot: a snapshot says what the
            // model was; the file is about to be written as it IS.
            AuditModelCommand.AuditRun run = AuditModelCommand.RunChecks(app, doc, top, options, null);

            System.Collections.Generic.List<GateRow> rows; string verdict;
            string gateError = PreDeliveryGateRules.Evaluate(AuditModelCommand.Declared(request.Requirements),
                                                             AuditModelCommand.Measurements(doc, run),
                                                             out rows, out verdict);
            if (gateError != null)
            {
                result.Refusal = CommandResult.Fail(commandName + ": require_gate.profile.requirements refused - " +
                                                    gateError + " Nothing was done.");
                return result;
            }

            var evidence = new OperationGateEvidence
            {
                Operation = operation,
                DocumentTitle = SafeTitle(doc),
                DocumentFingerprint = run.DocumentFingerprint,
                FindingSetFingerprint = run.FindingSetFingerprint,
                Rows = rows,
                Verdict = verdict,
                FindingIdsByCheck = run.FindingIds(),
                VisibilityComplete = run.Visibility.CoverageComplete,
                VisibilityNote = run.Visibility.CoverageComplete ? null : run.Visibility.Note()
            };
            foreach (JToken t in run.ChecksFailed) evidence.ChecksFailed.Add((string)t["check"]);
            foreach (JToken t in run.IncompleteChecks) evidence.ChecksIncomplete.Add((string)t["check"]);

            // THE ONLY CLOCK READ ON THIS PATH, and the reason it is read here rather
            // than taken from the request: the caller's now_utc arrives in the same
            // object as the override it would be used to judge, so an expired
            // override plus a convenient now_utc was a working override. GateClock
            // makes this machine's clock the authority and demotes the caller's value
            // to a constraint it must agree with.
            GateClockReference clock = GateClock.Resolve(request.NowUtc, DateTime.UtcNow);

            OperationGateVerdict decision = OperationGateRules.Decide(request, evidence, clock);
            result.Prevention = OperationGateRules.ToJson(request, evidence, decision, clock);

            if (!decision.Proceed)
                result.Refusal = CommandResult.FailWithDetail(
                    commandName + " REFUSED by require_gate (" + decision.Decision + "): " + decision.Why +
                    " The file was not touched.",
                    new JObject
                    {
                        ["state"] = "refused",
                        ["decision"] = decision.Decision,
                        ["prevention"] = result.Prevention
                    });
            return result;
        }

        private static string SafeTitle(Document d) { try { return d?.Title; } catch { return null; } }
    }
}
