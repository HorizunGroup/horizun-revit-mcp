// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// One shape for a queued run that ends early, whatever ended it.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>One block, whatever ended the run early: change, failure or cancellation (M1, M4).</summary>
    public static class CooperativeInterruption
    {
        public enum Terminal { Failed, Publish, PublishDespiteLateCancel, Cancelled, NoResult }

        /// <summary>
        /// How a cooperative job ends (review m6). A cancel that arrives after the last slice computed a COMPLETE result must not
        /// discard it: the scan is done, the ledger may already be written, and the caller asked to stop work that no longer exists.
        /// A failure still wins; a cancel wins over an unfinished run.
        /// </summary>
        public static Terminal Decide(bool failed, bool cancelRequested, bool hasResult, bool moreToDo)
        {
            if (failed) return Terminal.Failed;
            bool complete = hasResult && !moreToDo;
            if (complete) return cancelRequested ? Terminal.PublishDespiteLateCancel : Terminal.Publish;
            if (cancelRequested) return Terminal.Cancelled;
            return Terminal.NoResult;
        }

        public const string DocumentChanged = "document_changed";
        public const string FederationChanged = "federation_changed";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";

        public static JObject Block(string kind, string reason, string jobId, bool scanRestartsFromStart)
        {
            return new JObject
            {
                ["kind"] = kind,
                ["reason"] = reason,
                ["resumable"] = kind == DocumentChanged || kind == FederationChanged || kind == Cancelled,
                ["resume_from_job_id"] = string.IsNullOrEmpty(jobId) ? (JToken)JValue.CreateNull() : jobId,
                ["resume_mode"] = scanRestartsFromStart ? "restart_from_first_pair" : "continue",
                ["how"] = "Send the same arguments again (async: resume_from_job_id=<this job>). The clashes listed here are an observation, not a result."
            };
        }

        /// <summary>
        /// Applies the same treatment to a snapshot whatever ended it: validity comes from the document
        /// stamp alone (staleReason null = still valid), the block names what happened.
        /// </summary>
        public static string Apply(string partialJson, string kind, string reason, string jobId, string staleReason)
        {
            if (partialJson == null) return null;
            JObject partial;
            try { partial = JObject.Parse(partialJson); }
            catch (Newtonsoft.Json.JsonException) { return null; }
            partial["result"] = "partial";
            if (staleReason != null)
            {
                partial["valid"] = false;
                partial["invalid_reason"] = staleReason;
            }
            partial["interruption"] = Block(kind, reason, jobId, true);
            return partial.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
