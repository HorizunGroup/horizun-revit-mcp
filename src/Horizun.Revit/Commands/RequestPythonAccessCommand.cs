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
    /// Brings the owner-controlled consent question to the person in Revit WITHOUT
    /// holding Revit: operation=request (default) opens a non-modal window and returns
    /// at once with state=pending_approval and a request_id; operation=status reads that
    /// request (also published as horizun_health.python_runtime.access_request). The
    /// MCP caller can ask, and can read the answer; it cannot answer the question or
    /// write the privilege itself - only the window's own Enable button does, after the
    /// person ticks the acknowledgement (PythonAccessWindow, Core/PythonAccessRequests).
    /// Field report: the old modal dialog kept Revit's UI thread for up to 600 s.
    /// </summary>
    public sealed class RequestPythonAccessCommand : ICommand
    {
        public string Name => "horizun_request_python_access";

        public string Description =>
            "Ask the machine owner in Revit to enable persistent arbitrary Python. Returns at once with " +
            "pending_approval and a request_id; read the decision with operation=status. The caller cannot grant " +
            "permission. Do not call this unattended.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            string operation = (request.Value<string>("operation") ?? "request").Trim().ToLowerInvariant();
            if (operation != "request" && operation != "status")
                return CommandResult.Fail("operation must be request or status. No window was shown.");
            string reason = request.Value<string>("reason");
            if (reason != null && reason.Length > 500)
                return CommandResult.Fail("reason is limited to 500 characters. No window was shown.");
            if (operation == "status" && reason != null)
                return CommandResult.Fail("reason applies to operation=request. Nothing was asked.");
            string requestId = request.Value<string>("request_id");
            if (operation == "request" && requestId != null)
                return CommandResult.Fail("request_id applies to operation=status. No window was shown.");

            CommandContract python = Contract.Find("horizun_execute_python");
            bool allowed = Settings.IsToolAllowed(python, out string refusal);

            if (operation == "status")
            {
                PythonAccessRequest r = requestId == null ? PythonAccessRequests.Latest() : PythonAccessRequests.Get(requestId);
                if (r == null && requestId != null)
                    return CommandResult.Fail("No Python access request '" + requestId + "' exists in this Revit session " +
                                              "(requests live in memory and end with Revit). Ask again with operation=request.");
                return CommandResult.Ok(PythonAccessStatus.Reply(PythonAccessRequests.ToJson(r), allowed));
            }

            if (allowed)
                return CommandResult.Ok(PythonAccessStatus.Reply(null, true));

            PythonAccessRequest opened = PythonAccessRequests.Open(reason, out bool reused);
            if (!reused)
            {
                bool spanish = PythonPermissionCommand.IsSpanishLanguage(app.Application.Language);
                IntPtr owner = IntPtr.Zero;
                try { owner = app.MainWindowHandle; } catch { }
                // Show(), not ShowDialog(): this returns at once and Revit keeps working.
                PythonAccessWindow.ShowFor(opened, refusal, spanish, owner);
                opened = PythonAccessRequests.Get(opened.Id) ?? opened;   // a window that failed to show recorded it
            }
            JObject reply = PythonAccessStatus.Reply(PythonAccessRequests.ToJson(opened), false);
            reply["window_reused"] = reused;
            return CommandResult.Ok(reply);
        }
    }
}
