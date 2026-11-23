// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// What the SERVER itself has seen, kept so horizun_health can report it when the
// add-in does not answer (4.2-c): the last failed tool call, with its stable code.
// It is memory of an observation, not a diagnosis; health labels it with its scope
// ("this server session") and never merges it into the current request's error.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class ServerFacts
    {
        private static readonly object Gate = new object();
        private static JObject _last;

        internal static void Reset() { lock (Gate) _last = null; }

        /// <summary>
        /// Remember a failed call. horizun_health is excluded on purpose: its own failure is
        /// reported as this request's error, and echoing it back as history would make a
        /// busy Revit look like a long record of failures.
        /// </summary>
        internal static void RecordFailure(string tool, string errorCode, string message)
        {
            if (string.IsNullOrEmpty(tool) || tool == HealthAnswer.ToolName) return;
            string text = message ?? "";
            if (text.Length > 300) text = text.Substring(0, 300) + "...";
            lock (Gate)
                _last = new JObject
                {
                    ["tool"] = tool,
                    ["error_code"] = errorCode,
                    ["message"] = text.Replace("\r", " ").Replace("\n", " ").Trim(),
                    ["at_utc"] = DateTime.UtcNow.ToString("o"),
                    ["scope"] = "this server session"
                };
        }

        /// <summary>A copy of the last recorded failure, or null when none was seen.</summary>
        internal static JObject LastFailure()
        {
            lock (Gate) return (JObject)_last?.DeepClone();
        }

        /// <summary>Record the failure carried by a tools/call result, if it is one.</summary>
        internal static void Observe(string tool, JToken result)
        {
            if (!(result is JObject r) || r["isError"]?.Type != JTokenType.Boolean || !(bool)r["isError"]) return;
            JObject sc = r["structuredContent"] as JObject;
            string text = null;
            if (r["content"] is JArray blocks)
                foreach (JToken b in blocks)
                    if ((string)b["type"] == "text" && b["text"]?.Type == JTokenType.String) { text = (string)b["text"]; break; }
            RecordFailure(tool, (string)sc?["error_code"], text);
        }
    }
}
