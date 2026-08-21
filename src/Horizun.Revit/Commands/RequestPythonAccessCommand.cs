// -----------------------------------------------------------------------------
// Horizun MCP — request, never self-grant, arbitrary Python permission.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.UI;
using Horizun.Contracts;
using Horizun.Revit;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Commands
{
    /// <summary>
    /// Opens the same owner-controlled consent UI as the ribbon. The MCP caller can
    /// bring the question to the person in Revit, but it cannot answer the question or
    /// write the privilege itself. This command must remain synchronous: queueing a
    /// surprise permission dialog for later would sever the request from its human.
    /// </summary>
    public sealed class RequestPythonAccessCommand : ICommand
    {
        public string Name => "horizun_request_python_access";

        public string Description =>
            "Ask the machine owner in Revit to enable persistent arbitrary Python. The caller cannot grant " +
            "permission; it waits for the visible human decision. Do not call this unattended.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string reason = request.Value<string>("reason");
            if (reason != null && reason.Length > 500)
                return CommandResult.Fail("reason is limited to 500 characters. No dialog was shown.");

            CommandContract python = Contract.Find("horizun_execute_python");
            if (Settings.IsToolAllowed(python, out string refusal))
            {
                return CommandResult.Ok(new JObject
                {
                    ["granted"] = true,
                    ["decision"] = "already_enabled",
                    ["persistent"] = true,
                    ["expires_at"] = JValue.CreateNull(),
                    ["what_this_means"] = "Python was already ON. No consent dialog was needed."
                });
            }

            string message = null;
            bool spanish = PythonPermissionCommand.IsSpanishLanguage(app.Application.Language);
            Result decision = PythonPermissionCommand.Enable(ref message, refusal, reason, spanish);
            if (decision == Result.Failed)
                return CommandResult.Fail(string.IsNullOrWhiteSpace(message)
                    ? "The owner permission update failed. Python remains OFF."
                    : message + " Python remains OFF.");

            bool granted = decision == Result.Succeeded && Settings.IsToolAllowed(python, out _);
            return CommandResult.Ok(new JObject
            {
                ["granted"] = granted,
                ["decision"] = granted ? "enabled_by_owner" : "rejected_or_cancelled_by_owner",
                ["persistent"] = granted,
                ["expires_at"] = JValue.CreateNull(),
                ["what_this_means"] = granted
                    ? "The person in Revit enabled Python. It remains ON until that Windows user disables it."
                    : "The person in Revit did not grant permission. Python remains OFF; do not retry or bypass their choice."
            });
        }
    }
}
