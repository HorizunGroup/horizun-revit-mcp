// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// The two ways this server says no.
//
// They lived in Program.cs, beside Main. That was fine until the host-resident
// tools that throw them needed proving without starting a server: linking
// Program.cs into a test project drags in the whole message loop and every tool
// table it references, so the alternative was to test the tools through a process,
// which is how the pid/year rules went untested in the first place. Two small
// exception types in their own file cost nothing and make Targets.cs testable.
// -----------------------------------------------------------------------------
using System;

namespace Horizun.Server
{
    /// <summary>A JSON-RPC error with the code the caller will see.</summary>
    internal sealed class McpError : Exception
    {
        public int Code { get; }
        public McpError(int code, string message) : base(message) { Code = code; }
    }

    /// <summary>
    /// A host-resident tool declining to act, on purpose - a bad argument, a target that
    /// does not exist. The caller gets an error either way; this only tells the log the
    /// difference between "it said no" and "it broke".
    /// </summary>
    internal sealed class ToolRefusal : Exception
    {
        public ToolRefusal(string message) : base(message) { }

        /// <summary>
        /// A refusal a client has to BRANCH on, not read: the fields are spread into the
        /// result's structuredContent (e.g. code=elicitation_unsupported), beside the prose.
        /// </summary>
        public ToolRefusal(string message, Newtonsoft.Json.Linq.JObject detail) : base(message) { Detail = detail; }

        /// <summary>Machine-readable detail, or null for an ordinary text refusal.</summary>
        public Newtonsoft.Json.Linq.JObject Detail { get; }
    }

    /// <summary>
    /// Not a failure: a host-resident tool, called by a 2026-07-28 client, needs the
    /// person's input before it can finish. It carries the InputRequiredResult body
    /// (inputRequests + requestState); the dispatcher answers the tools/call with it,
    /// stamped resultType "input_required", instead of a CallToolResult. Only ever
    /// thrown when the call's ClientContext says the multi round-trip pattern applies.
    /// </summary>
    internal sealed class InputRequiredException : Exception
    {
        public InputRequiredException(Newtonsoft.Json.Linq.JObject result)
            : base("input required") { Result = result; }

        public Newtonsoft.Json.Linq.JObject Result { get; }
    }
}
