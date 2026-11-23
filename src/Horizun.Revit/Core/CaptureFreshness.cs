// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// E6 (field report, 2026-10-18): horizun_capture_view sometimes returned the image of
// the PREVIOUS state - same sha256 - right after a change. ExportImage renders from
// Revit's view graphics, which a change committed in the same idle cycle may not
// have refreshed yet.
//
// So a capture now knows whether the model changed since the last capture of the
// same view (the read cache's epoch, which DocumentChanged, open, save, sync and view
// activation advance - a superset, so at worst one extra export) and whether the bytes
// came back identical. Identical bytes after a change are re-exported once after a
// regeneration; still identical is reported as possibly_stale, never as a fresh
// image. The decision is pure (Decide) and tested without Revit.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;

namespace Horizun.Revit.Core
{
    public static class CaptureFreshness
    {
        public const string Fresh = "fresh";
        public const string FirstCapture = "first_capture";
        public const string UnchangedModel = "unchanged_model";
        public const string RetryNeeded = "retry_needed";
        public const string PossiblyStale = "possibly_stale";

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, KeyValuePair<string, long>> Last =
            new Dictionary<string, KeyValuePair<string, long>>(StringComparer.Ordinal);

        /// <summary>
        /// previousSha/previousChanges: the last capture of this view (null when none);
        /// retried: whether this sha already comes from the re-export.
        /// </summary>
        public static string Decide(string previousSha, long? previousChanges, string sha, long changesNow, bool retried)
        {
            if (previousSha == null || previousChanges == null) return FirstCapture;
            if (!string.Equals(previousSha, sha, StringComparison.Ordinal)) return Fresh;
            if (previousChanges.Value == changesNow) return UnchangedModel;
            return retried ? PossiblyStale : RetryNeeded;
        }

        public static bool TryGetLast(string key, out string sha, out long changes)
        {
            lock (Gate)
            {
                if (Last.TryGetValue(key, out KeyValuePair<string, long> v)) { sha = v.Key; changes = v.Value; return true; }
            }
            sha = null; changes = 0; return false;
        }

        public static void Remember(string key, string sha, long changes)
        {
            if (key == null || sha == null) return;
            lock (Gate)
            {
                if (Last.Count > 512) Last.Clear();
                Last[key] = new KeyValuePair<string, long>(sha, changes);
            }
        }
    }
}
