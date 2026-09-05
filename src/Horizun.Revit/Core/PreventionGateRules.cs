// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// PREVENTION: deciding whether an operation this bridge controls may proceed.
//
// THE ASYMMETRY THAT DEFINES THIS FILE, and it is the same one the workset
// placement check uses:
//
//     a gate with incomplete coverage may BLOCK, and may never ALLOW.
//
// Blocking is sound: a defect found in the part that was examined is a real
// defect, and not having examined the rest cannot un-find it. Allowing is not:
// "nothing wrong here" is a claim about the whole model, and half a model was
// never looked at. A gate that passes on partial coverage tells a team their
// delivery is clean because a scan timed out.
//
// AN OVERRIDE IS A SIGNED STATEMENT, not a flag. It names who, when, which
// operation, which profile, and exactly which findings are being accepted -
// because an override that says only "approved" is indistinguishable from a
// mistake six months later, and somebody will have to tell them apart.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class GateDecision
    {
        public const string Allow = "allow";
        public const string Block = "block";
        public const string RequiresOverride = "requires_override";
        public const string NotAssessable = "not_assessable";

        public static readonly string[] All = { Allow, Block, RequiresOverride, NotAssessable };
    }

    /// <summary>The operations this bridge can actually gate. Nothing else is claimed.</summary>
    public static class GatedOperation
    {
        public const string Save = "save";
        public const string SaveAs = "save_as";
        public const string SyncWithCentral = "sync_with_central";
        public const string Export = "export";
        public const string Publish = "publish";
        public const string CloseWithSave = "close_with_save";
        public const string BatchOpenClose = "batch_open_close";

        public static readonly string[] All =
        {
            Save, SaveAs, SyncWithCentral, Export, Publish, CloseWithSave, BatchOpenClose
        };
    }

    public sealed class GateOverride
    {
        public string Identity;
        public string Reason;
        public string TimestampUtc;
        public string Operation;
        public string ProfileVersion;
        public List<string> FindingsIgnored = new List<string>();
        public string Evidence;
        /// <summary>Optional. An override with no expiry outlives the reason for it.</summary>
        public string ExpiresUtc;

        public bool IsComplete
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Identity)
                    && !string.IsNullOrWhiteSpace(Reason)
                    && !string.IsNullOrWhiteSpace(TimestampUtc)
                    && !string.IsNullOrWhiteSpace(Operation)
                    && FindingsIgnored != null && FindingsIgnored.Count > 0;
            }
        }
    }

    public sealed class GateInput
    {
        public string Operation;
        public string DocumentTitle;
        public string DocumentFingerprint;
        public string ProfileVersion;
        /// <summary>False when the audit did not see the whole model.</summary>
        public bool CoverageComplete;
        /// <summary>Findings that block. Ids, so an override can name them.</summary>
        public List<string> BlockingFindings = new List<string>();
        public GateOverride Override;
        /// <summary>False when this bridge does not control the operation at all.</summary>
        public bool OperationIsControlled = true;
        /// <summary>Null when no audit was supplied - which is not a clean audit.</summary>
        public bool? AuditSupplied;
    }

    public sealed class GateVerdict
    {
        public string Decision;
        public string Operation;
        public string Why;
        public List<string> BlockingFindings = new List<string>();
        public bool OverrideAccepted;
        public string OverrideRejectedBecause;
    }

    public static class PreventionGateRules
    {
        public const string AsymmetryMeans =
            "a gate with incomplete coverage may BLOCK and may never ALLOW. A defect found in the part that " +
            "was examined is real, and not having examined the rest cannot un-find it - but 'nothing wrong " +
            "here' is a claim about the whole model, and half of it was never looked at. A gate that passes on " +
            "partial coverage tells a team their delivery is clean because a scan timed out.";

        public const string OverrideMeans =
            "an override is a signed statement, not a flag: who, when, which operation, which profile, and " +
            "exactly which findings are being accepted. An override that says only 'approved' is " +
            "indistinguishable from a mistake six months later, and somebody will have to tell them apart.";

        public static GateVerdict Decide(GateInput input, string nowUtc)
        {
            var v = new GateVerdict { Operation = input == null ? null : input.Operation };
            if (input == null)
            {
                v.Decision = GateDecision.NotAssessable;
                v.Why = "nothing was submitted to the gate.";
                return v;
            }

            v.BlockingFindings = new List<string>(input.BlockingFindings ?? new List<string>());

            // AN OPERATION THIS BRIDGE DOES NOT CONTROL IS NOT ALLOWED BY IT.
            // Answering "allow" would claim an authority the bridge does not have.
            if (!input.OperationIsControlled)
            {
                v.Decision = GateDecision.NotAssessable;
                v.Why = "'" + input.Operation + "' is not an operation this bridge controls, so it has no " +
                        "opinion to give. That is not permission.";
                return v;
            }

            // NO AUDIT IS NOT A CLEAN AUDIT.
            if (input.AuditSupplied != true)
            {
                v.Decision = GateDecision.NotAssessable;
                v.Why = "no audit was supplied, so nothing is known about this model. An absent audit is not a " +
                        "clean one.";
                return v;
            }

            // BLOCKING FIRST. A finding survives incomplete coverage.
            if (v.BlockingFindings.Count > 0)
            {
                GateOverride o = input.Override;
                if (o == null)
                {
                    v.Decision = GateDecision.Block;
                    v.Why = v.BlockingFindings.Count + " blocking finding(s). " +
                            (input.CoverageComplete ? "" : "Coverage was incomplete, which cannot excuse a " +
                                                          "defect that was found.");
                    return v;
                }

                string rejected = RejectOverride(o, input, nowUtc);
                if (rejected != null)
                {
                    v.Decision = GateDecision.Block;
                    v.OverrideRejectedBecause = rejected;
                    v.Why = "an override was supplied and refused: " + rejected;
                    return v;
                }

                // An override covers only the findings it NAMES.
                var uncovered = v.BlockingFindings
                    .Where(f => !o.FindingsIgnored.Contains(f, StringComparer.Ordinal)).ToList();
                if (uncovered.Count > 0)
                {
                    v.Decision = GateDecision.Block;
                    v.OverrideRejectedBecause =
                        "the override names " + o.FindingsIgnored.Count + " finding(s) and does not cover " +
                        uncovered.Count + " of the blocking ones. An override accepts what it lists and " +
                        "nothing else.";
                    v.Why = v.OverrideRejectedBecause;
                    return v;
                }

                v.Decision = GateDecision.RequiresOverride;
                v.OverrideAccepted = true;
                v.Why = "blocked by " + v.BlockingFindings.Count + " finding(s), accepted under an override by " +
                        o.Identity + ". The findings stand; the operation proceeds on that signature.";
                return v;
            }

            // NOTHING FOUND. Now coverage decides whether that means anything.
            if (!input.CoverageComplete)
            {
                v.Decision = GateDecision.NotAssessable;
                v.Why = "no blocking finding was recorded, but the audit did not cover the whole model, so " +
                        "this is not a pass. " + AsymmetryMeans;
                return v;
            }

            v.Decision = GateDecision.Allow;
            v.Why = "the audit covered the whole model and recorded no blocking finding.";
            return v;
        }

        /// <summary>Why an override does not count. Null when it does.</summary>
        public static string RejectOverride(GateOverride o, GateInput input, string nowUtc)
        {
            if (o == null) return "there is no override.";
            if (!o.IsComplete)
                return "the override is incomplete. " + OverrideMeans;

            if (!string.Equals(o.Operation, input.Operation, StringComparison.Ordinal))
                return "this override was signed for '" + o.Operation + "' and the operation is '" +
                       input.Operation + "'. An override for one operation is not permission for another.";

            if (input.ProfileVersion != null && o.ProfileVersion != null &&
                !string.Equals(o.ProfileVersion, input.ProfileVersion, StringComparison.Ordinal))
                return "this override was signed against profile '" + o.ProfileVersion + "' and this run used '" +
                       input.ProfileVersion + "'. The rules changed, so what was accepted may not be what is " +
                       "being asked now.";

            // AN EXPIRY NOBODY CAN EVALUATE DOES NOT PASS.
            //
            // This used to require BOTH strings to be present before comparing, so an
            // override carrying an expiry was accepted whenever now_utc was absent - and
            // now_utc is optional and supplied by the CALLER. Holding an expired override
            // and omitting one optional field was enough to keep it working forever, which
            // makes the expiry a suggestion rather than a limit.
            //
            // Deliberately still no clock read: the gate is pure so an expiry is exact in
            // a test rather than dependent on when the suite runs. But determinism is a
            // reason to REQUIRE the caller to state the time, not a reason to skip the
            // check when they do not. An override with an expiry and no time to judge it
            // against is refused, and the reason says which field is missing.
            if (!string.IsNullOrEmpty(o.ExpiresUtc))
            {
                if (string.IsNullOrEmpty(nowUtc))
                    return "this override expires at " + o.ExpiresUtc + " and no now_utc was supplied, so " +
                           "whether it is still valid cannot be established. An expiry that cannot be " +
                           "evaluated is not a pass: send now_utc.";

                if (string.CompareOrdinal(nowUtc, o.ExpiresUtc) > 0)
                    return "this override expired at " + o.ExpiresUtc + ".";
            }

            return null;
        }

        public static JObject ToJson(GateVerdict v)
        {
            if (v == null) return null;
            return new JObject
            {
                ["decision"] = v.Decision,
                ["operation"] = v.Operation,
                ["why"] = v.Why,
                ["blocking_findings"] = new JArray(v.BlockingFindings.Select(x => (JToken)x)),
                ["override_accepted"] = v.OverrideAccepted,
                ["override_rejected_because"] = v.OverrideRejectedBecause,
                ["asymmetry_means"] = AsymmetryMeans
            };
        }
    }
}
