// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// A REPORT STAGE THAT THROWS AFTER A COMMAND RETURNED.
//
// FIELD FEEDBACK E1: horizun_execute_plan answered
// "ArgumentException ... KeyValuePair" while the model carried every change. The
// dispatcher runs report stages after a command returns (the spatial check, the
// change stamp, response shaping) and used to let an exception from any of them
// reach its outer catch, which REPLACES the command's result with a Fail. A write
// that committed then reads as one that did not, and the caller's retry writes it
// twice.
//
// The rule: the command's own result is the truth about the model. A report stage
// that throws is recorded INSIDE a successful result as report_error, with the
// phase, the exception type and message, and what it means. A failed result stays
// failed; nothing here can turn either into the other.
// -----------------------------------------------------------------------------
using System;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class PostCommitReport
    {
        public static void Record(CommandResult result, string phase, Exception error)
        {
            if (result == null || !result.Success) return;
            JObject data;
            try
            {
                data = result.Data as JObject ??
                       (result.Data == null ? new JObject() : JObject.FromObject(result.Data));
            }
            catch (Exception serialize)
            {
                data = new JObject { ["data_unserializable"] = serialize.Message };
            }
            data["report_error"] = new JObject
            {
                ["phase"] = phase,
                ["type"] = error?.GetType().FullName,
                ["message"] = error?.Message,
                ["means"] = "The command itself returned success and its result stands: whatever it committed is in " +
                            "the model. Only a report stage that runs afterwards failed, so a field it would have " +
                            "added (spatial_check, model_changes, response shaping) may be missing. Do not retry the " +
                            "write; re-read with the query tools if you need the missing part."
            };
            result.ReplaceData(data);
        }
    }
}
