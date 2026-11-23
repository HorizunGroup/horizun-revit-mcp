// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// "PYTHON ONLY WHEN THE FALLBACK ALLOWS IT", ENFORCED WHERE IT CAN BE (review 2026-11-20, I3).
//
// The rule was advice: a typed write that failed after writing started answers
// fallback.allowed=false with write_started=true, and an agent that called
// horizun_execute_python right after it was still let through - a second write on top
// of a partial one. The server sees every reply, so it remembers that state and
// refuses a Python execution (not its preflight) until the model has been read again:
// horizun_verify_changes, horizun_undo, a model read or the same tool answering an applied
// write clears it. Only tools whose contract can write the model arm it (review R1). It is memory of this
// server session only, like ServerFacts.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class PartialWriteGate
    {
        internal const string PythonTool = "horizun_execute_python";

        private static readonly object Gate = new object();
        private static JObject _pending;

        /// <summary>Successful calls that re-read (or undo) the model after a partial write.</summary>
        private static readonly HashSet<string> Clearing = new HashSet<string>(StringComparer.Ordinal)
        {
            "horizun_verify_changes", "horizun_undo", "horizun_query_model", "horizun_list_elements",
            "horizun_selection_context", "horizun_model_diff"
        };

        internal static void Reset() { lock (Gate) _pending = null; }

        /// <summary>
        /// Whether a tool can write the model, from its contract (review R1: a read that timed out answered
        /// write_started=null and armed the gate although nothing could have been written). An unknown name
        /// never ran, so it does not arm either.
        /// </summary>
        internal static bool WritesModel(string tool)
        {
            Horizun.Contracts.ToolEffect? e = Tools.Find(tool)?.Effect;
            return e == Horizun.Contracts.ToolEffect.Mutating || e == Horizun.Contracts.ToolEffect.MutatingUnlessDryRun ||
                   e == Horizun.Contracts.ToolEffect.DocumentSession;
        }

        internal static JObject Pending() { lock (Gate) return (JObject)_pending?.DeepClone(); }

        /// <summary>Remember a typed write that failed after writing started; forget it once the model is re-read.</summary>
        internal static void Observe(string tool, JToken result)
        {
            if (string.IsNullOrEmpty(tool) || tool == PythonTool || !(result is JObject r)) return;
            bool isError = r["isError"]?.Type == JTokenType.Boolean && (bool)r["isError"];
            JObject sc = r["structuredContent"] as JObject;
            if (isError && sc != null && WritesModel(tool) && WriteStarted(sc) && !RolledBackClean(sc))
            {
                lock (Gate)
                    _pending = new JObject
                    {
                        ["tool"] = tool,
                        ["error_code"] = sc["error_code"]?.DeepClone(),
                        ["at_utc"] = DateTime.UtcNow.ToString("o"),
                        ["scope"] = "this server session"
                    };
                return;
            }
            if (isError) return;
            lock (Gate)
            {
                // A re-read clears it; so does the SAME tool answering an applied write (review R1: a write that timed
                // out and was retried with its idempotency_key re-reads its own elements). A rehearsal does not.
                bool sameToolApplied = _pending != null && (string)_pending["tool"] == tool && !IsTrue(sc?["dry_run"]);
                if (Clearing.Contains(tool) || sameToolApplied) _pending = null;
            }
        }

        /// <summary>
        /// True, or explicitly null at the TOP level: there null is "unknown" (a timeout with the request in
        /// flight, a command that threw mid-way), and an unknown start is treated as started (review r1). The
        /// fallback block's null is only the default every error is stamped with, so there only true counts (r2).
        /// </summary>
        private static bool WriteStarted(JObject sc) =>
            TrueOrNull(sc, "write_started") || IsTrue((sc["fallback"] as JObject)?["write_started"]);

        private static bool TrueOrNull(JObject o, string key) =>
            o != null && o.TryGetValue(key, out JToken t) && (t.Type == JTokenType.Null || IsTrue(t));

        /// <summary>A rehearsal or a write whose transaction was rolled back and that says nothing was applied left nothing partial.</summary>
        private static bool RolledBackClean(JObject sc) =>
            sc["changes_applied"]?.Type == JTokenType.Boolean && !(bool)sc["changes_applied"] &&
            string.Equals((string)sc["rollback_status"] ?? (string)sc["transaction_status"], "RolledBack", StringComparison.OrdinalIgnoreCase);

        private static bool IsTrue(JToken t) => t != null && t.Type == JTokenType.Boolean && (bool)t;

        /// <summary>The refusal text for a Python execution while a partial write is unread, or null.</summary>
        internal static string Refusal(string tool, JObject args)
        {
            if (tool != PythonTool) return null;
            if (args?["preflight"]?.Type == JTokenType.Boolean && (bool)args["preflight"]) return null;
            JObject pending = Pending();
            if (pending == null) return null;
            return "horizun_execute_python refused: the last typed write (" + (string)pending["tool"] +
                   (pending["error_code"]?.Type == JTokenType.String ? ", " + (string)pending["error_code"] : "") +
                   ") failed after writing started, or at a point where whether it started is unknown (write_started true or " +
                   "null), so its fallback did not allow Python and the model " +
                   "may hold part of that write. A script now would be a second write on top of it. Re-read first " +
                   "(horizun_verify_changes or horizun_query_model) or undo it (horizun_undo); Python is admitted again after " +
                   "that. preflight=true is still admitted. Nothing was run.";
        }
    }
}
