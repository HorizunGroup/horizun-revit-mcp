// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// WHAT host_ids DOES TO A RULE. The contract says it only intersects: "Narrow every
// rule to these hosts. The rules still select; this only intersects." So a rule whose
// hosts all lie OUTSIDE host_ids has nothing to do here and is skipped - it is not a
// missing host, and it must not abort an apply that other rules can complete. A rule
// that matches nothing in the document at all is still refused (backlog P0 #7).
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public static class HostNarrowing
    {
        public enum Outcome { Build, SkipOutsideHostIds, HostNotFound }

        public static Outcome Decide(int matchedInDocument, int matchedInHostIds, bool narrowed)
        {
            if (matchedInHostIds > 0) return Outcome.Build;
            if (narrowed && matchedInDocument > 0) return Outcome.SkipOutsideHostIds;
            return Outcome.HostNotFound;
        }
    }
}
