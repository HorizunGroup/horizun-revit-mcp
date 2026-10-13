// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// WHEN MAY A CANCELLED OR TIMED-OUT CALL BE REPORTED AS "NEVER STARTED"?
//
// Only on structured proof. This used to be decided by searching the message for
// "NEVER STARTED" or "FIFO queue" - and the add-in could write those words for a
// request its UI thread had claimed a moment before the removal (audit 2026-10-08).
// The add-in now sets never_started only after a removal proven under its queue
// lock, and PipeClient sets cancelled_before_start only when the add-in confirmed
// one; those flags, and nothing in the prose, decide.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class NeverStartedProof
    {
        /// <summary>
        /// The failed result's text when its structuredContent proves the request never
        /// started; otherwise null. Two proofs, both booleans set only after a confirmed
        /// removal: the add-in's never_started, and PipeClient's cancelled_before_start,
        /// which CallTool spreads into the result when it catches the cancellation.
        /// </summary>
        internal static string FromResult(JToken result)
        {
            if ((bool?)result?["isError"] != true) return null;
            JObject structured = result?["structuredContent"] as JObject;
            if (!IsTrue(structured?["never_started"]) && !IsTrue(structured?["cancelled_before_start"])) return null;
            return (string)result?["content"]?[0]?["text"];
        }

        private static bool IsTrue(JToken flag) => flag != null && flag.Type == JTokenType.Boolean && (bool)flag;

        /// <summary>
        /// A tools/call RETURNED and a cancellation arrived for it. Answer it as cancelled
        /// only when the returned result itself proves the request never started; any
        /// other returned result is work that HAPPENED, and is delivered. It used to be
        /// replaced by "cancelled - it may still be running", which hid a finished result
        /// and invited a second write (audit 2026-10-08).
        /// </summary>
        internal static bool AnswerAsCancelled(bool cancellationRequested, JToken result)
            => cancellationRequested && FromResult(result) != null;

        /// <summary>Does PipeClient's transport detail on this exception prove the request was removed before start?</summary>
        internal static bool FromException(Exception ex)
        {
            JObject detail = ex?.Data?["horizun_transport_detail"] as JObject;
            JToken flag = detail?["cancelled_before_start"];
            return flag != null && flag.Type == JTokenType.Boolean && (bool)flag;
        }
    }
}
