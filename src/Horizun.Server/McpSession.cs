// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// The protocol session state that the stdio reader enforces before dispatching
// anything. Kept outside Program so the lifecycle and the lifetime-wide request
// id rule can be proved without starting a process or Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal enum McpSessionPhase
    {
        AwaitingInitialize,
        AwaitingInitializedNotification,
        Operational
    }

    internal sealed class McpSession
    {
        private readonly HashSet<string> _usedRequestIds = new HashSet<string>(StringComparer.Ordinal);
        private McpSessionPhase _phase = McpSessionPhase.AwaitingInitialize;

        public McpSessionPhase Phase => _phase;

        /// <summary>
        /// Read the mandatory JSON-RPC method. Missing and wrong-shaped methods are both
        /// invalid requests; a request with an id must always receive -32600 rather than
        /// disappearing after its id has already been reserved.
        /// </summary>
        public bool TryReadMethod(JObject message, out string method, out string error)
        {
            method = null;
            error = null;
            JProperty property = message?.Property("method");
            if (property == null)
            {
                error = "Invalid request: required field 'method' is missing. Nothing was done.";
                return false;
            }
            if (property.Value == null || property.Value.Type != JTokenType.String)
            {
                error = "Invalid request: 'method' must be a string. Nothing was done.";
                return false;
            }
            method = (string)property.Value;
            if (string.IsNullOrEmpty(method))
            {
                error = "Invalid request: 'method' cannot be empty. Nothing was done.";
                method = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Read and reserve a request id. An absent id means notification; an explicit
        /// null does not. MCP narrows JSON-RPC ids to string or integer and forbids a
        /// requestor from reusing one anywhere in the same session, even after the first
        /// request completed.
        /// </summary>
        public bool TryAcceptId(JObject message, out bool isNotification, out object id, out string error)
        {
            isNotification = false;
            id = null;
            error = null;

            JProperty property = message?.Property("id");
            if (property == null)
            {
                isNotification = true;
                return true;
            }

            JToken token = property.Value;
            if (token == null || (token.Type != JTokenType.String && token.Type != JTokenType.Integer))
            {
                string saw = token == null ? "missing value" : token.Type.ToString();
                error = "Invalid request: 'id' must be a string or integer, not " + saw +
                        ". MCP does not permit null or floating-point request ids. Nothing was done.";
                return false;
            }

            id = ((JValue)token).Value;
            string key = token.Type + ":" + token.ToString(Formatting.None);
            if (!_usedRequestIds.Add(key))
            {
                error = "Invalid request: id " + token.ToString(Formatting.None) +
                        " was already used in this MCP session. Request ids are lifetime-unique; nothing was done.";
                id = null; // an invalid reused id cannot identify a new request
                return false;
            }

            return true;
        }

        /// <summary>Refuse messages that do not belong to the current MCP lifecycle phase.</summary>
        public bool Allows(string method, bool isNotification, out string error)
        {
            error = null;
            switch (_phase)
            {
                case McpSessionPhase.AwaitingInitialize:
                    if (method == "initialize" && !isNotification) return true;
                    error = "MCP initialization must be the first interaction. Send an initialize request before '" +
                            method + "'. Nothing was done.";
                    return false;

                case McpSessionPhase.AwaitingInitializedNotification:
                    if (method == "notifications/initialized" && isNotification) return true;
                    if (method == "ping" && !isNotification) return true;
                    if (method == "initialize")
                    {
                        error = "The MCP session has already answered initialize; it cannot be initialized twice.";
                        return false;
                    }
                    error = "The server has answered initialize but has not received notifications/initialized. " +
                            "Only ping is accepted until that notification arrives; nothing was done.";
                    return false;

                default:
                    if (method == "initialize")
                    {
                        error = "The MCP session is already operational; initialize cannot be sent again.";
                        return false;
                    }
                    if (method == "notifications/initialized")
                    {
                        error = "The MCP session is already operational; notifications/initialized cannot be sent again.";
                        return false;
                    }
                    return true;
            }
        }

        public void InitializeAnswerDelivered()
        {
            if (_phase != McpSessionPhase.AwaitingInitialize)
                throw new InvalidOperationException("initialize was answered outside the initialization phase.");
            _phase = McpSessionPhase.AwaitingInitializedNotification;
        }

        public void InitializedNotificationAccepted()
        {
            if (_phase != McpSessionPhase.AwaitingInitializedNotification)
                throw new InvalidOperationException("notifications/initialized arrived outside its lifecycle phase.");
            _phase = McpSessionPhase.Operational;
        }
    }
}
