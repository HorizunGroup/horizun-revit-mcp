// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// DID THIS CALL LEAVE ANYTHING IN THE MODEL? A reply may say "The write is committed"
// only when it did. A rehearsal (dry_run) rolls back; so does horizun_execute_python
// with read_only=true, which runs the script and writes nothing. The spatial headline
// only knew the first, so a read-only script that merely touched elements answered
// "The write is committed" about work that was never kept.
// -----------------------------------------------------------------------------
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class NoCommitRules
    {
        /// <summary>True when the reply says nothing stayed in the model: dry_run=true or read_only=true.</summary>
        public static bool LeftNothing(JObject data)
        {
            if (data == null) return false;
            return data.Value<bool?>("dry_run") == true || data.Value<bool?>("read_only") == true;
        }
    }
}
