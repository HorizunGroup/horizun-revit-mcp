// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The identity check of a write (ElementIdentityGuard), run ONCE, BEFORE the command parses its arguments.
//
// Why here and not inside each command: about forty commands validate their own arguments with a closed list of
// fields ("x is not applicable to operation y") BEFORE they reach DocumentGate.ForMutation, and a few more refuse
// unknown fields of their own. An identity sent to any of them was refused as an unknown field, so the documented
// protection never reached those writers (review of R2Q, finding B-1). Verifying and consuming the identity at the one
// place every command passes through means no validator ever sees an identity field, whatever it validates and however
// deep the node that carries it.
//
// The command then receives a normalized request: the verified ids are where it reads them and the identity fields are
// gone. The logic is in IdentityPrepassCore (Revit-free, unit-tested); this is the Revit side.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.UI;

namespace Horizun.Revit.Core
{
    internal static class IdentityPrepass
    {
        internal static CommandResult Execute(UIApplication app, ICommand command, string json,
                                              Func<UIApplication, ICommand, string, CommandResult> run)
        {
            string tool = command?.Name;
            return IdentityPrepassCore.Execute(tool, json,
                request => DocumentGate.CheckIdentityEarly(app, request, tool),
                text => run(app, command, text));
        }
    }
}
