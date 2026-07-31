// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// The command contract. A command is one named operation the MCP can ask Revit
// to perform. It runs on the Revit UI thread (the dispatcher guarantees that),
// so it may touch the API freely; it must not start its own thread.
//
// Deliberately tiny: a name, a human description, and Execute. Everything else
// (transactions, verification, JSON shaping) is the command's own business.
// -----------------------------------------------------------------------------
using Autodesk.Revit.UI;

namespace Horizun.Revit.Core
{
    /// <summary>One named, UI-thread Revit operation exposed over the MCP.</summary>
    public interface ICommand
    {
        /// <summary>Wire name, e.g. "get_document_info". Stable; the MCP calls by this.</summary>
        string Name { get; }

        /// <summary>One line for humans/logs. Not the MCP tool schema.</summary>
        string Description { get; }

        /// <summary>
        /// Run the command. <paramref name="paramsJson"/> is the raw JSON object the
        /// caller sent (may be null/empty). Return a <see cref="CommandResult"/>; do
        /// not throw for expected failures — return CommandResult.Fail so the caller
        /// gets a clean error instead of a stack trace.
        /// </summary>
        CommandResult Execute(UIApplication app, string paramsJson);
    }
}
