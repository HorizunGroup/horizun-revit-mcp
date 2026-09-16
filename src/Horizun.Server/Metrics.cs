// -----------------------------------------------------------------------------
// Horizun MCP - what a call cost, measured where it happens. Original Horizun code.
//
// The benchmark needs numbers that mean something, and the only place they mean
// something is the path that produced them. A collector that estimates from the
// outside measures its own estimate.
//
// WHAT IS MEASURED, and why each one separately:
//
//   QUEUED      how long the request waited before work started. One command runs
//               at a time here, so this is the number that explains a slow call
//               that was not slow - and merging it into the total hides the one
//               thing a user can act on.
//   EXECUTED    the work itself.
//   TOTAL       receipt to answer, which is what the caller actually felt. It is
//               not queued + executed: serialisation and the pipe sit in between,
//               and reporting a sum as a measurement invents the difference.
//   BYTES       in and out. A response nobody can parse because it is 40 MB is a
//               performance result, not a content one.
//   ATTEMPTS    how many times the work was tried, and how many of those were
//               retries. One call is not one attempt on any path with a retry.
//   OUTCOME     ok, error, cancelled or PARTIAL - four, not two. A partial result
//               counted as a success is how a benchmark rewards a tool that did
//               most of the job, and counted as a failure is how it punishes the
//               only tool honest enough to say so.
//
// THE SCENARIO IS DECLARED BY THE CALLER, never inferred. A measurement with no
// scenario is still recorded - it just cannot be compared with anything, and
// saying that is better than filing it under whatever ran last.
//
// NOTHING HERE HAS BEEN RUN. The collector is written, wired into the dispatch
// path, and has produced no measurement: this campaign executes nothing.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    /// <summary>What one call cost. Every field is measured; none is derived from another.</summary>
    internal sealed class CallMetric
    {
        public string Scenario;
        public string Tool;
        public string Era;
        public string RequestId;

        public long QueuedMs;
        public long ExecutedMs;
        public long TotalMs;

        public int RequestBytes;
        public int ResponseBytes;

        public int Attempts = 1;
        public int Retries;

        /// <summary>ok | error | cancelled | partial</summary>
        public string Outcome = "ok";

        public string StartedUtc;
        public string FinishedUtc;

        /// <summary>What the measurement was taken on, so two runs can be compared honestly.</summary>
        public JObject Context;

        public JObject Json() => new JObject
        {
            ["scenario"] = Scenario == null ? (JToken)JValue.CreateNull() : Scenario,
            ["tool"] = Tool,
            ["era"] = Era,
            ["request_id"] = RequestId,
            ["queued_ms"] = QueuedMs,
            ["executed_ms"] = ExecutedMs,
            ["total_ms"] = TotalMs,
            ["request_bytes"] = RequestBytes,
            ["response_bytes"] = ResponseBytes,
            ["attempts"] = Attempts,
            ["retries"] = Retries,
            ["outcome"] = Outcome,
            ["started_utc"] = StartedUtc,
            ["finished_utc"] = FinishedUtc,
            ["context"] = Context,
            ["means"] =
                "queued_ms, executed_ms and total_ms are three MEASUREMENTS, not one measurement and two " +
                "derivations: total is receipt to answer and includes serialisation and the pipe, so it is " +
                "not the sum of the other two and must not be reported as one."
        };
    }

    /// <summary>
    /// Where measurements go. Bounded in memory, appended to a file when one is named.
    ///
    /// THE BOUND IS THE POINT OF THE RING. A server that records every call forever
    /// becomes the thing it is measuring, and a benchmark whose collector leaks is a
    /// benchmark that reports its own leak as the product's memory use.
    /// </summary>
    internal static class Metrics
    {
        /// <summary>The `_meta` key a caller labels a measured run with.</summary>
        public const string ScenarioKey = "io.horizunhub/scenario";

        /// <summary>Where the benchmark asks for the structured export.</summary>
        public const string PathVariable = "HORIZUN_METRICS_PATH";

        private const int MaxHeld = 500;

        private static readonly object Gate = new object();
        private static readonly Queue<CallMetric> Held = new Queue<CallMetric>();
        private static JObject _context;

        /// <summary>
        /// What every measurement in this process was taken on.
        ///
        /// Recorded ONCE and attached to each metric, because a number without its
        /// conditions cannot be compared with another number. A comparison between two
        /// products measured on different Revit years, with different models, is not a
        /// comparison - and this is what makes that visible rather than arguable.
        /// </summary>
        public static void Describe(JObject context)
        {
            lock (Gate) _context = context == null ? null : (JObject)context.DeepClone();
        }

        /// <summary>The scenario this request declared, or null.</summary>
        public static string ScenarioOf(JObject prms)
        {
            JToken token = prms?["_meta"]?[ScenarioKey];
            return token != null && token.Type == JTokenType.String ? (string)token : null;
        }

        public static void Record(CallMetric metric)
        {
            if (metric == null) return;
            lock (Gate)
            {
                metric.Context = _context == null ? null : (JObject)_context.DeepClone();
                Held.Enqueue(metric);
                while (Held.Count > MaxHeld) Held.Dequeue();
            }

            // THE EXPORT IS APPEND-ONLY AND BEST-EFFORT. A measurement that cannot be
            // written must never break the call it measured: the whole point of the
            // collector is that it is invisible to the work.
            string path = Environment.GetEnvironmentVariable(PathVariable);
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string line = metric.Json().ToString(Formatting.None) + Environment.NewLine;
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Warn("a measurement could not be exported: " + ex.Message);
            }
        }

        /// <summary>Everything held, newest last, as the benchmark reads it.</summary>
        public static JObject Snapshot()
        {
            List<CallMetric> all;
            JObject context;
            lock (Gate)
            {
                all = Held.ToList();
                context = _context == null ? null : (JObject)_context.DeepClone();
            }

            var byOutcome = new JObject();
            foreach (IGrouping<string, CallMetric> group in all.GroupBy(m => m.Outcome))
                byOutcome[group.Key] = group.Count();

            return new JObject
            {
                ["schema"] = "horizun.metrics/1",
                ["held"] = all.Count,
                ["bound"] = MaxHeld,
                ["complete"] = all.Count < MaxHeld,
                ["context"] = context,
                ["by_outcome"] = byOutcome,
                ["calls"] = new JArray(all.Select(m => (JToken)m.Json())),
                ["means"] = all.Count >= MaxHeld
                    ? "INCOMPLETE. The ring is full and the oldest measurements have been dropped, so this " +
                      "is the most recent " + MaxHeld + " calls and not the run. Set " + PathVariable +
                      " to a file for a complete record."
                    : "every call this process measured since it started.",
                ["not_a_benchmark_result"] =
                    "these are measurements of THIS process on THIS machine. They become a comparison only " +
                    "beside the same measurements of another product, taken under the conditions in " +
                    "'context'. A number without its conditions is not a result."
            };
        }
    }
}
