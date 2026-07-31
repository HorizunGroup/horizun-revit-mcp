// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The result of a command. Two shapes only: success carrying a data payload, or
// failure carrying a message. The transport serializes this to
// { "id", "success", "data", "error" } — one envelope, no ambiguity about
// whether a call worked.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public sealed class CommandResult
    {
        public bool Success { get; private set; }

        /// <summary>Payload on success (any JSON-serializable object). Null on failure.</summary>
        public object Data { get; private set; }

        /// <summary>Message on failure. Null on success.</summary>
        public string Error { get; private set; }

        /// <summary>
        /// What Revit raised while this ran — warnings, errors, modal dialogs — filled in
        /// by the dispatcher, never by the command. It rides beside the result rather than
        /// inside it so no command has to remember to report it, and so a command that
        /// FAILED still carries what Revit objected to, which is usually the reason.
        /// </summary>
        public object RevitSaid { get; set; }

        private CommandResult() { }

        public static CommandResult Ok(object data)
            => new CommandResult { Success = true, Data = data };

        public static CommandResult Fail(string error)
            => new CommandResult { Success = false, Error = error };
    }
}
