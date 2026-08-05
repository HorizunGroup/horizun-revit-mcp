// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE WIRE SHAPE of a command's answer, in one place both halves can point at.
//
// It lived inside PipeServer as a private helper, which made every claim about
// what reaches the client an assertion about SOURCE TEXT rather than about a
// value - and the things that ride beside the payload (what Revit raised, the
// fallback signal, the per-action capability gaps) are exactly the ones a caller
// never sees until they are missing. A structured signal dropped in
// serialization is indistinguishable from one that was never granted, and the
// safe-looking failure is the wrong one: the client silently stops falling back.
//
// So the envelope is a pure function of a CommandResult, and the tests build one
// and read the JSON that would go down the pipe.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Transport
{
    public static class PipeEnvelope
    {
        /// <summary>The bare envelope: id, success, data, error.</summary>
        public static JObject Of(string id, bool success, object data, string error)
        {
            return new JObject
            {
                ["id"] = id,
                ["success"] = success,
                ["data"] = data == null ? null : JToken.FromObject(data),
                ["error"] = error
            };
        }

        /// <summary>
        /// The envelope for a completed command, with everything that travels beside the
        /// payload attached. One function, so nothing can be attached on one path and
        /// forgotten on another.
        /// </summary>
        public static JObject Of(string id, CommandResult result)
        {
            JObject reply = result.Success
                ? Of(id, true, result.Data, null)
                : Of(id, false, null, result.Error);

            // Travels on success AND on failure: what Revit objected to is usually the
            // reason a command failed, and on success it is the part nobody would
            // otherwise be told.
            if (result.RevitSaid != null) reply["revit_said"] = JToken.FromObject(result.RevitSaid);

            // The machine-readable fallback decision. Beside the error, never inside its
            // text, so a client branches on data instead of matching English.
            if (result.Fallback != null) reply["fallback"] = result.Fallback.ToJson();

            // WHICH actions were capability gaps. Travels even when the request-level
            // grant was REFUSED, because a mixed batch is exactly where a caller needs to
            // know which entries have no typed path and which are theirs to fix.
            if (result.CapabilityGaps != null) reply["capability_gaps"] = result.CapabilityGaps;

            // The structured failure diagnostic (an atomic plan's rollback trace). Beside
            // the error, never inside it, so a client branches on transaction_group_started /
            // rollback_status instead of matching the words of a sentence.
            if (result.Detail != null) reply["detail"] = result.Detail;

            return reply;
        }
    }
}
