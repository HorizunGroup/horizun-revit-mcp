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

        /// <summary>
        /// The LEGACY path: emit if the session minimum set by logging/setLevel allows it.
        ///
        /// Unchanged, and it must stay unchanged: every client installed today negotiates a
        /// legacy revision, and the minimum starts at "emit nothing" so a client that never
        /// called setLevel is never sent anything it did not ask for.
        /// </summary>
        public static void Emit(string level, JObject data, Action<string, JObject> notify)
        {
            if (!Severity.TryGetValue(level, out int severity) || severity < Volatile.Read(ref _minimum)) return;
            Write(level, data, notify, null);
        }

        /// <summary>
        /// The MODERN path: emit if THIS REQUEST asked for this severity.
        ///
        /// 2026-07-28 removed logging/setLevel, so there is no session level to consult -
        /// and consulting the legacy one anyway would make one request's logging depend on
        /// a call another client made. A request that named no level gets nothing, which is
        /// the revision's own default: no field, no logs.
        ///
        /// `requestId` travels under a VENDOR-PREFIXED key. The revision defines no
        /// correlation field for log notifications, and inventing one under
        /// io.modelcontextprotocol/ would be this server making up protocol. Without it a
        /// client running several requests at once cannot tell whose log it is reading.
        /// </summary>
        public static void EmitForRequest(string level, JObject data, Action<string, JObject> notify,
                                          string requestLevel, object requestId)
        {
            if (requestLevel == null) return;
            if (!Severity.TryGetValue(level, out int severity)) return;
            if (!Severity.TryGetValue(requestLevel, out int wanted) || severity < wanted) return;
            Write(level, data, notify, requestId);
        }

        /// <summary>Is this a level this server understands? Used to refuse a bad one loudly.</summary>
        public static bool IsLevel(string level) => level != null && Severity.ContainsKey(level);

        private static void Write(string level, JObject data, Action<string, JObject> notify, object requestId)
        {
            if (data == null || notify == null) return;
            var body = new JObject
            {
                ["level"] = level,
                ["logger"] = "horizun-mcp",
                ["data"] = data.DeepClone()
            };
            if (requestId != null)
                body["_meta"] = new JObject
                {
                    ["io.horizunhub/requestId"] = JToken.FromObject(requestId)
                };
            notify("notifications/message", body);
        }
    }
}
