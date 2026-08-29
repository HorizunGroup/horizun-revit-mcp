// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Per-tool timing facts, measured at the one seat every call passes through.
// In-memory and process-scoped ON PURPOSE: these numbers describe THIS session
// on THIS machine with THIS model, and the snapshot says so - persisting them
// would invite reading last week's model into today's complaint. Bounded ring
// per tool, so a chatty session cannot grow the process.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ToolTimings
    {
        public const int RingSize = 32;
        public const int MaxTools = 128;

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, Ring> _byTool = new Dictionary<string, Ring>(StringComparer.Ordinal);
        private static long _dropped;

        private sealed class Ring
        {
            public readonly long[] Samples = new long[RingSize];
            public int Next;
            public long Calls;
            public long TotalMs;
            public long MaxMs;
        }

        public static void Record(string tool, long elapsedMs)
        {
            if (string.IsNullOrEmpty(tool) || elapsedMs < 0) return;
            lock (_gate)
            {
                Ring ring;
                if (!_byTool.TryGetValue(tool, out ring))
                {
                    if (_byTool.Count >= MaxTools) { _dropped++; return; }
                    _byTool[tool] = ring = new Ring();
                }
                ring.Samples[ring.Next] = elapsedMs;
                ring.Next = (ring.Next + 1) % RingSize;
                ring.Calls++;
                ring.TotalMs += elapsedMs;
                if (elapsedMs > ring.MaxMs) ring.MaxMs = elapsedMs;
            }
        }

        /// <summary>For tests: forget everything.</summary>
        public static void Reset()
        {
            lock (_gate) { _byTool.Clear(); _dropped = 0; }
        }

        /// <summary>
        /// The facts, ordered by total time descending so the expensive tools lead.
        /// avg is over EVERY call this session; recent_avg over the ring - a session
        /// whose model grew shows the drift between the two.
        /// </summary>
        public static JObject Snapshot()
        {
            lock (_gate)
            {
                var tools = new JObject();
                foreach (var pair in _byTool.OrderByDescending(p => p.Value.TotalMs)
                                            .ThenBy(p => p.Key, StringComparer.Ordinal))
                {
                    Ring ring = pair.Value;
                    int recentCount = (int)Math.Min(ring.Calls, RingSize);
                    long recentTotal = 0;
                    for (int i = 0; i < recentCount; i++) recentTotal += ring.Samples[i];
                    tools[pair.Key] = new JObject
                    {
                        ["calls"] = ring.Calls,
                        ["total_ms"] = ring.TotalMs,
                        ["avg_ms"] = ring.Calls == 0 ? 0 : ring.TotalMs / ring.Calls,
                        ["max_ms"] = ring.MaxMs,
                        ["recent_avg_ms"] = recentCount == 0 ? 0 : recentTotal / recentCount,
                        ["recent_window"] = recentCount
                    };
                }
                return new JObject
                {
                    ["scope"] = "this process session only; resets when Revit restarts",
                    ["tools_tracked"] = _byTool.Count,
                    ["tools_dropped"] = _dropped,
                    ["tools"] = tools
                };
            }
        }
    }
}
