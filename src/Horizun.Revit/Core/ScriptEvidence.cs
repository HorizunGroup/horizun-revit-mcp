// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// What a script's __output__ CLAIMS, classified WITHOUT crediting it.
//
// THE DISTINCTION THIS FILE EXISTS TO KEEP. A typed command is verified BY THE
// HOST: it re-reads the model after the commit, in code this repository owns, and
// "verified" there is a fact the bridge is willing to stand behind. A Python
// script is arbitrary code. It can print any words it likes into __output__, and
// nothing on this side re-reads the model to check them.
//
// So this classifier NEVER emits "verified". The strongest thing it can say
// about arbitrary code is self_reported_verified: the script followed the
// contract, said it checked, and attached something it called evidence - none of
// which the host confirmed. An earlier version of this file returned "verified"
// for exactly that input, which let a script that assigned
// {"status":"verified","verification":{"checked":true,"evidence":["ok"]}} come
// back looking identical to a typed write that had actually been re-read. That
// was the contract lying in the one place it was written to protect.
//
// The four states:
//
//   self_reported_verified - the script declared verified, said checked=true and
//                            attached non-empty evidence. NOT host-verified.
//   completed_unverified   - it finished without structured evidence to classify.
//   partial                - part of the operation happened, or only part verified.
//   failed                 - the script's own failure report.
//
// The rule that cannot bend: nothing here ever raises a claim. Every path either
// keeps the script's declared status or lowers it.
//
// A REAL "verified" for Python would need a typed evidence contract - ids and
// property names the host could re-read itself - plus a Revit-side verifier. That
// does not exist, and inventing a generic one is not possible: the host cannot
// know what an arbitrary script was trying to achieve.
//
// Revit-free on purpose: this is arithmetic over a JToken, provable in CI.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>How a script's own success claim was classified, and why.</summary>
    public sealed class EvidenceReport
    {
        /// <summary>
        /// self_reported_verified | completed_unverified | partial | failed.
        /// NEVER "verified" - see the header. This is the host's classification of
        /// what the script claimed, not the host's own finding.
        /// </summary>
        public string Status { get; internal set; }

        /// <summary>
        /// The status string the SCRIPT declared, verbatim (trimmed and lower-cased),
        /// or null when it declared none. Kept so the classification never destroys
        /// information: a script that said "verified" and was classified
        /// self_reported_verified can still be told apart from one that said
        /// "completed_unverified" itself.
        /// </summary>
        public string ScriptReportedStatus { get; internal set; }

        /// <summary>True when __output__ followed the recommended contract shape.</summary>
        public bool Structured { get; internal set; }

        /// <summary>
        /// ALWAYS FALSE, and present so no reader has to infer it. The host does not
        /// re-read the model after arbitrary Python; only typed commands carry that
        /// guarantee. It is a field rather than a comment because a client scanning
        /// the response for a verification signal must find an explicit "no".
        /// </summary>
        public bool HostVerified => false;

        /// <summary>Everything that kept the claim from being taken at face value.</summary>
        public JArray Warnings { get; } = new JArray();

        /// <summary>The script's own summary, when it gave one.</summary>
        public string Summary { get; internal set; }
    }

    public static class ScriptEvidence
    {
        /// <summary>The recommended __output__ shape, quoted wherever a caller needs it.</summary>
        public const string ContractShape =
            "{\"status\":\"verified|completed_unverified|partial|failed\",\"summary\":\"...\"," +
            "\"created_ids\":[],\"modified_ids\":[],\"deleted_ids\":[]," +
            "\"verification\":{\"checked\":true,\"evidence\":[]},\"warnings\":[]}";

        /// <summary>
        /// One sentence for every response that carries a classification, so the
        /// difference between a typed write and a Python run is stated where it is read
        /// rather than left in a document.
        /// </summary>
        public const string HostVerificationDisclaimer =
            "SELF-REPORTED, NOT HOST-VERIFIED. Typed commands are re-read from the model by the bridge after " +
            "the commit; arbitrary Python is not. self_reported_verified means the SCRIPT said it checked and " +
            "attached evidence - the host did not confirm any of it, and there is no value of any field here " +
            "that constitutes a bridge guarantee for Python. Treat the evidence as the script's testimony: " +
            "read it, and re-read the model with a typed query if the outcome matters.";

        /// <summary>
        /// Classify what the script handed back. Never throws, never raises a claim: the
        /// strictest reading the data supports is the one reported.
        /// </summary>
        public static EvidenceReport Classify(JToken output)
        {
            var report = new EvidenceReport { Status = "completed_unverified", Structured = false };

            JObject o = output as JObject;
            if (o == null)
            {
                report.Warnings.Add(
                    "__output__ was not the structured evidence object, so there is nothing to classify. The " +
                    "script ran, and that is all this response can honestly say. Assign the recommended " +
                    "shape - " + ContractShape + " - and re-read what you wrote before setting status.");
                return report;
            }

            string status = (o.Value<string>("status") ?? "").Trim().ToLowerInvariant();
            report.ScriptReportedStatus = string.IsNullOrEmpty(status) ? null : status;
            report.Summary = o.Value<string>("summary");

            // The script's own warnings ride along; they are its content, not ours to drop.
            if (o["warnings"] is JArray scriptWarnings)
                foreach (JToken w in scriptWarnings)
                    if (w.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)w))
                        report.Warnings.Add("script warning: " + (string)w);

            switch (status)
            {
                case "failed":
                    report.Structured = true;
                    report.Status = "failed";
                    return report;

                case "partial":
                    report.Structured = true;
                    report.Status = "partial";
                    return report;

                case "completed_unverified":
                    report.Structured = true;
                    report.Status = "completed_unverified";
                    return report;

                // "verified" is what the recommended contract shape tells a script to
                // write; "self_reported_verified" is the word every disclaimer and doc
                // popularises for the same thing, so a careful script writes it back
                // verbatim. Both mean the same claim - "I checked and attached evidence" -
                // and both are held to the same bar. Rejecting the second (it used to fall
                // into default and be DOWNGRADED with a warning naming a list that omitted
                // it) was the vocabulary contradicting itself: the bridge taught a word it
                // then refused.
                case "verified":
                case "self_reported_verified":
                    report.Structured = true;
                    if (!HasSelfReportedEvidence(o))
                    {
                        report.Status = "completed_unverified";
                        report.Warnings.Add(
                            "__output__ claimed status=" + status + " but verification.checked was not true or " +
                            "verification.evidence was empty, so the claim was DOWNGRADED to " +
                            "completed_unverified. Re-read the elements you touched inside the script and " +
                            "put what you re-read into verification.evidence.");
                        return report;
                    }
                    // The ceiling for arbitrary code. The script declared it checked and
                    // attached evidence; the host confirmed nothing, and says so in the
                    // name of the state rather than in a footnote. A script that already
                    // said self_reported_verified is recorded as exactly that, unchanged.
                    report.Status = "self_reported_verified";
                    report.Warnings.Add(
                        "status=" + status + " was reported BY THE SCRIPT and is recorded as " +
                        "self_reported_verified. The bridge did not re-read the model to confirm it - only typed " +
                        "commands carry that guarantee. " + HostVerificationDisclaimer);
                    return report;

                case "":
                    report.Warnings.Add(
                        "__output__ is an object but carries no 'status' field, so it does not follow the " +
                        "evidence contract and is reported completed_unverified. Recommended shape: " +
                        ContractShape);
                    return report;

                default:
                    report.Warnings.Add(
                        "__output__ carried status='" + status + "', which is not one of " +
                        "verified|self_reported_verified|completed_unverified|partial|failed. Unknown claims are " +
                        "read as completed_unverified, never as success.");
                    return report;
            }
        }

        /// <summary>
        /// Whether the script attached anything it CALLS evidence. Deliberately shallow:
        /// the host cannot judge whether ["ok"] or a document title actually demonstrates
        /// the write, so it does not pretend to - it only distinguishes "attached
        /// something" from "attached nothing", and the resulting state is named
        /// self_reported so the shallowness is visible rather than implied.
        /// </summary>
        private static bool HasSelfReportedEvidence(JObject o)
        {
            try
            {
                JObject verification = o["verification"] as JObject;
                if (verification == null) return false;
                JToken checkedToken = verification["checked"];
                if (checkedToken == null || checkedToken.Type != JTokenType.Boolean || !(bool)checkedToken)
                    return false;
                JArray evidence = verification["evidence"] as JArray;
                return evidence != null && evidence.Count > 0;
            }
            catch { return false; }
        }
    }
}
