// -----------------------------------------------------------------------------
// Horizun MCP server - what a caller whose call was cancelled or timed out may
// safely do next. Original Horizun code.
//
// The transport already told the truth about WHETHER the work started: removed
// from the queue before start (nothing claimed, nothing written), or already
// running / finished / unprovable. What it did not say is what the retry does,
// and "do not resend it assuming nothing happened" leaves the caller two bad
// options - give up, or resend with a fresh key and risk a second write.
//
// The durable idempotency ledger (Horizun.Revit/Core/DurableCommandIdempotency.cs)
// already makes the right retry safe, and this names it:
//
//   * cancelled BEFORE START, or never submitted: the key was never claimed, so
//     the IDENTICAL call with the SAME idempotency_key (and the same confirmation
//     token) executes freshly, exactly once. Measured live by verify-live W13
//     case 11 in all five Revit years.
//   * possibly started, WITH an idempotency_key: Revit runs one command at a time,
//     so an identical retry waits in the queue behind the original and then finds
//     its durable completion record - it REPLAYS the recorded answer, success or
//     failure, and writes nothing. A NEW key would be a second write.
//   * possibly started, WITHOUT a key: nothing can prove the outcome; inspect the
//     model before sending anything again. (Every typed mutation requires a key,
//     so this is a read or a call the add-in would have refused.)
//
// Pure: no I/O, so the three verdicts are unit-tested rather than trusted.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class CancellationAdvice
    {
        public const string SameKeyRunsFresh = "same_key_runs_fresh";
        public const string SameKeyReplays = "same_key_replays_recorded_answer";
        public const string InspectModelFirst = "inspect_model_first";

        /// <summary>
        /// A call that waited at least this long before it was cancelled or timed out
        /// is told about horizun_submit_job. Sixty seconds is the default tool timeout
        /// of several MCP clients (AGENTS.md, "Raise the tool timeout"); the elapsed
        /// time of the call itself is the measurement, not a guess from its size. In
        /// the 1,447 horizun_create_elements calls logged 2026-09-19..24 the slowest
        /// took 3.4 s, so no batch size has yet been measured near that limit.
        /// </summary>
        public const long LongWaitMs = 60000;

        public static string Classify(bool nothingStarted, bool hasIdempotencyKey)
        {
            if (nothingStarted) return SameKeyRunsFresh;
            return hasIdempotencyKey ? SameKeyReplays : InspectModelFirst;
        }

        public static bool HasKey(JObject args)
        {
            JToken k = args?["idempotency_key"];
            return k != null && k.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)k);
        }

        /// <summary>
        /// Items in the batch, for the batch tools that take an array of work
        /// (elements, rows, actions, operations). 0 when there is none.
        /// </summary>
        public static int BatchSize(JObject args)
        {
            if (args == null) return 0;
            int max = 0;
            foreach (string name in new[] { "elements", "rows", "actions", "operations", "items" })
                if (args[name] is JArray a && a.Count > max) max = a.Count;
            return max;
        }

        /// <summary>
        /// One sentence for the caller. Deliberately never contains the words the
        /// server treats as a proof that nothing started ("FIFO queue", "NEVER
        /// STARTED") unless that verdict is the one being given.
        /// </summary>
        public static string Sentence(string verdict, int batchSize, long elapsedMs)
        {
            string s;
            switch (verdict)
            {
                case SameKeyRunsFresh:
                    s = "RETRY: nothing started, so the identical call is safe to send again - with the SAME " +
                        "idempotency_key and confirmation_token if it carried them, since neither was consumed; " +
                        "it then runs freshly, exactly once.";
                    break;
                case SameKeyReplays:
                    s = "RETRY: send the IDENTICAL call with the SAME idempotency_key. Revit runs one command at a " +
                        "time, so it waits behind the original and then replays that call's recorded answer " +
                        "(success or failure) without writing again. A NEW key would be a second write.";
                    break;
                default:
                    s = "RETRY: this call carried no idempotency_key, so nothing can prove its outcome; inspect " +
                        "the model before sending anything again.";
                    break;
            }
            if (elapsedMs >= LongWaitMs)
                s += " This call waited " + (elapsedMs / 1000) + " s" +
                     (batchSize > 0 ? " for a batch of " + batchSize + " items" : "") +
                     "; work that outlives the client's timeout belongs in horizun_submit_job, polled with " +
                     "horizun_job_status.";
            return s;
        }

        public static JObject ToJson(string verdict, bool hasIdempotencyKey, int batchSize, long elapsedMs)
        {
            var o = new JObject
            {
                ["verdict"] = verdict,
                ["reuse_same_idempotency_key"] = verdict != InspectModelFirst,
                ["has_idempotency_key"] = hasIdempotencyKey,
                ["batch_items"] = batchSize,
                ["waited_ms"] = elapsedMs
            };
            if (elapsedMs >= LongWaitMs) o["prefer"] = "horizun_submit_job";
            return o;
        }
    }
}
