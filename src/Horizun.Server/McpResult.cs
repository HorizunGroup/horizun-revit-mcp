// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// HOW A PLUGIN REPLY BECOMES AN MCP TOOL RESULT. Extracted from Program.cs for
// the same reason ServerInstructions was: it is a contract surface, and while it
// sat as three private helpers the only way to assert anything about it was to
// read the source.
//
// What travels here decides whether a client can act. The human text is what a
// person reads; structuredContent is what a program branches on. The fallback
// block in particular must survive this hop intact - a signal dropped in
// translation looks exactly like a signal that was never granted, and the client
// then silently stops falling back to Python, which is the failure nobody
// notices because it presents as "the model just says no".
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class McpResult
    {
        public static JObject Text(string text, bool isError)
        {
            return new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
                ["isError"] = isError
            };
        }

        public static JObject Structured(JToken data, string text)
            => Structured(data, text, null, null);

        /// <summary>
        /// A success, with the fallback verdict merged into structuredContent beside the
        /// payload when there is one. The payload is never rewritten - the verdict is
        /// extra information about what the call FOUND, and a client reads one object.
        /// </summary>
        public static JObject Structured(JToken data, string text, JObject fallback, JArray capabilityGaps)
        {
            // The TEXT of a success stays exactly the payload. On the error path the
            // block is also rendered as prose, because a human reading a log has
            // nothing else; here that would splice a second JSON document into a
            // string clients parse as one, and every parser downstream would break on
            // precisely the replies that carry the new signal. structuredContent is
            // where a success publishes structure - that is what it is for.
            JObject result = Text(text, false);

            JObject structured = data is JObject o ? (JObject)o.DeepClone() : null;
            if (structured == null && (fallback != null || capabilityGaps != null)) structured = new JObject();
            if (structured == null) return result;

            if (fallback != null) structured["fallback"] = fallback.DeepClone();
            if (capabilityGaps != null) structured["capability_gaps"] = capabilityGaps.DeepClone();

            result["structuredContent"] = structured;
            return result;
        }

        /// <summary>
        /// A failure, with the fallback decision and any per-action capability gaps
        /// carried as STRUCTURE as well as prose. No fallback and no gaps gives exactly
        /// the plain text error it always did.
        /// </summary>
        public static JObject Error(string text, JObject fallback, JArray capabilityGaps)
            => Error(text, fallback, capabilityGaps, null);

        /// <summary>
        /// A failure, with the fallback decision, per-action capability gaps AND a structured
        /// diagnostic (an atomic plan's rollback trace) carried as STRUCTURE as well as prose.
        /// The diagnostic's fields are spread at the top of structuredContent so a client reads
        /// $s.transaction_group_started / $s.rollback_status directly - the shape a live probe
        /// asserts against instead of parsing the sentence.
        /// </summary>
        public static JObject Error(string text, JObject fallback, JArray capabilityGaps, JObject detail)
        {
            if (fallback == null && capabilityGaps == null && detail == null) return Text(text, true);

            var structured = new JObject();
            string rendered = text;

            // The diagnostic first, spread flat: transaction_group_started, rollback_status,
            // execution_trace and the rest become top-level fields of structuredContent.
            if (detail != null)
            {
                foreach (var prop in detail.Properties())
                    structured[prop.Name] = prop.Value.DeepClone();
            }

            if (fallback != null)
            {
                rendered += Environment.NewLine + Environment.NewLine +
                            "--- fallback ---" + Environment.NewLine + fallback.ToString(Formatting.Indented);
                structured["fallback"] = fallback.DeepClone();
            }
            // The gaps ride along even when the grant was refused - a mixed batch is
            // precisely where a caller needs to see which entries have no typed path and
            // which are theirs to fix.
            if (capabilityGaps != null)
            {
                rendered += Environment.NewLine + Environment.NewLine +
                            "--- capability_gaps (actions with no typed path) ---" + Environment.NewLine +
                            capabilityGaps.ToString(Formatting.Indented);
                structured["capability_gaps"] = capabilityGaps.DeepClone();
            }

            JObject result = Text(rendered, true);
            result["structuredContent"] = structured;
            return result;
        }

        /// <summary>
        /// The whole hop, from the envelope the plugin put on the pipe to the MCP result.
        /// One function, so a test can prove end to end that nothing is lost - which is
        /// the only claim worth making about a transport.
        /// </summary>
        public static JObject FromPluginReply(JObject reply, string revitSaidText)
        {
            bool ok = reply["success"] != null && reply["success"].Type == JTokenType.Boolean &&
                      (bool)reply["success"];

            if (ok)
            {
                JToken data = reply["data"];
                // A SUCCESSFUL reply can still carry the fallback verdict: the dry run
                // is a success that found a capability gap. Dropping the block here was
                // the defect that made the first, default call useless for deciding -
                // the caller got success=true, invalid=1 and no signal at all.
                return Structured(data,
                                  (data == null ? "null" : data.ToString(Formatting.Indented)) +
                                  (revitSaidText ?? ""),
                                  reply["fallback"] as JObject,
                                  reply["capability_gaps"] as JArray);
            }

            return Error("Error: " + (string)reply["error"] + (revitSaidText ?? ""),
                         reply["fallback"] as JObject,
                         reply["capability_gaps"] as JArray,
                         reply["detail"] as JObject);
        }
    }
}
