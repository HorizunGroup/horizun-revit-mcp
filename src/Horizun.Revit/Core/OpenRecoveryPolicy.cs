using System;
using System.IO;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    // Hash collisions stay protected; conflicting origins prevent even save_as.
    public sealed class OpenRecoveryIdentityRegistry
    {
        private readonly Dictionary<int, string> _origins = new Dictionary<int, string>();
        public void Mark(int identity, string origin)
        {
            lock (_origins)
            {
                if (_origins.TryGetValue(identity, out string existing))
                {
                    if (!string.Equals(existing, origin, StringComparison.OrdinalIgnoreCase)) _origins[identity] = null;
                }
                else _origins.Add(identity, origin);
            }
        }
        public bool IsMarked(int identity) { lock (_origins) return _origins.ContainsKey(identity); }
        public string Original(int identity) { lock (_origins) return _origins.TryGetValue(identity, out string path) ? path : null; }
    }

    public static class OpenRecoveryPolicy
    {
        public static string OpenRefusal(bool recovery, bool detach)
        {
            return recovery && !detach ? "resolve_in_memory requires detach=true. Nothing was opened." : null;
        }

        public static string SaveRefusal(bool protectedDocument, bool saveAs, bool confirmed, string original, string target, bool overwrite = false)
        {
            if (!protectedDocument) return null;
            if (overwrite) return "Recovered documents cannot overwrite an existing file.";
            if (!saveAs || !confirmed || string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(target))
                return "This document was recovered in memory. Only an explicitly confirmed save_as to a different path is allowed.";
            try
            {
                if (!Path.IsPathRooted(original) || !Path.IsPathRooted(target) ||
                    string.Equals(Path.GetFullPath(original), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    return "The recovered document cannot be saved over its original file.";
            }
            catch { return "Cannot verify that the destination differs from the original; nothing was saved."; }
            return null;
        }
    }
}
