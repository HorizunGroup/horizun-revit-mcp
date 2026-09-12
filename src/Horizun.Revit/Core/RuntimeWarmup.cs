using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    /// <summary>Measured runtime lifecycle, readable without acquiring the engine lock or Revit UI thread.</summary>
    public sealed class RuntimeWarmup
    {
        public static readonly RuntimeWarmup Python = new RuntimeWarmup();
        private readonly object gate = new object();
        private readonly Stopwatch clock = new Stopwatch();
        private string state = "cold", error;
        private int attempts;
        public void Begin() { lock (gate) { state = "warming"; error = null; attempts++; clock.Restart(); } }
        public void Ready() { lock (gate) { clock.Stop(); state = "ready"; } }
        public void Failed(Exception ex) { lock (gate) { clock.Stop(); state = "failed"; error = ex.GetType().FullName + ": " + ex.Message; } }
        public JObject Snapshot()
        {
            lock (gate) return new JObject
            {
                ["state"] = state,
                ["initialization_attempts"] = attempts,
                ["initialization_elapsed_ms"] = attempts == 0 ? (JToken)null : clock.ElapsedMilliseconds,
                ["last_error"] = error,
                ["first_call_requires_initialization"] = state != "ready"
            };
        }
    }
}
