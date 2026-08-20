// -----------------------------------------------------------------------------
// MCP structured client logging. Messages contain operational metadata only:
// never tool arguments, model names, paths, parameter values or exception stacks.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpLogging
    {
        private static readonly Dictionary<string, int> Severity =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["debug"] = 0, ["info"] = 1, ["notice"] = 2, ["warning"] = 3,
                ["error"] = 4, ["critical"] = 5, ["alert"] = 6, ["emergency"] = 7
            };

        private static int _minimum = int.MaxValue;

        public static JObject SetLevel(JObject prms)
        {
            JToken token = prms?["level"];
            string level = token?.Type == JTokenType.String ? (string)token : null;
            if (level == null || !Severity.TryGetValue(level, out int severity))
                throw new McpError(-32602,
                    "Invalid params: logging level must be debug, info, notice, warning, error, critical, alert or emergency.");
            Volatile.Write(ref _minimum, severity);
            return new JObject();
        }

        internal static void ResetForTests() => Volatile.Write(ref _minimum, int.MaxValue);

        public static void Emit(string level, JObject data, Action<string, JObject> notify)
        {
            if (!Severity.TryGetValue(level, out int severity) || severity < Volatile.Read(ref _minimum)) return;
            if (data == null || notify == null) return;
            notify("notifications/message", new JObject
            {
                ["level"] = level,
                ["logger"] = "horizun-mcp",
                ["data"] = data.DeepClone()
            });
        }
    }
}
