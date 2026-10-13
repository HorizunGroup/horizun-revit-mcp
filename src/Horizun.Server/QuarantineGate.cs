// -----------------------------------------------------------------------------
// Horizun MCP - the quarantine, enforced. Original Horizun code.
//
// horizun_repair_memory lets a person quarantine a failure shape. A shape is the
// failure's own message, so it is known only once a call has failed; what is known
// BEFORE a call runs is the call itself. The memory keeps, per shape, the hashes of
// the calls that failed that way (RepairMemory.CallFingerprint - the tool and its
// normalised arguments, never the values). This gate runs on every tools/call before
// anything is routed to Revit: a call whose fingerprint belongs to a QUARANTINED
// shape is refused with code call_quarantined, the reason, who set it and the exact
// way to lift it.
//
// READ-ONLY AND CHEAP. The memory is re-read only when its file changes (path, size,
// last write), without the memory tool's mutex: Save writes atomically, so a reader
// sees the old file or the new one. A file that cannot be read refuses nothing and
// is left exactly where it is - moving a damaged memory aside is the memory tool's
// decision, under its lock, and a gate that refused every call because a JSON file
// was damaged would be a worse failure than the one it guards against.
//
// The memory's own tool is never refused: it is how a quarantine is lifted.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class QuarantineGate
    {
        public const string Code = RepairMemory.CallQuarantinedCode;

        private static readonly object Sync = new object();
        private static string _cachedKey;
        private static List<RepairEntry> _cached = new List<RepairEntry>();

        /// <summary>Forget the cached memory (tests, and a data root that moved).</summary>
        public static void ResetCache()
        {
            lock (Sync) { _cachedKey = null; _cached = new List<RepairEntry>(); }
        }

        /// <summary>
        /// The refusal detail for a quarantined call, or null when the call may run. The
        /// detail carries code, message, shape, reason, quarantined_by, quarantined_at_utc,
        /// remedy and ran=false; the caller spreads it into structuredContent.
        /// </summary>
        public static JObject Check(string tool, JObject arguments)
        {
            if (string.IsNullOrEmpty(tool) || string.Equals(tool, RepairMemoryTool.Name, StringComparison.Ordinal))
                return null;

            List<RepairEntry> entries;
            try { entries = Current(); }
            catch (Exception ex)
            {
                Log.Warn("repair memory unreadable for the quarantine gate; nothing refused: " + ex.Message);
                return null;
            }
            if (entries.Count == 0) return null;

            RepairEntry hit = RepairMemory.QuarantinedCall(entries, tool, arguments);
            if (hit == null) return null;

            return new JObject
            {
                ["code"] = Code,
                ["message"] = RepairMemory.QuarantineRefusal(hit),
                ["tool"] = tool,
                ["shape"] = hit.Shape,
                ["reason"] = hit.QuarantineReason,
                ["quarantined_by"] = hit.QuarantinedBy,
                ["quarantined_at_utc"] = hit.QuarantinedAtUtc,
                ["ran"] = false,
                ["remedy"] = "Do not retry this call as it is; ask the owner why it was quarantined. " +
                             RepairMemory.ReleaseHint(hit) + " horizun_repair_memory operation=advice with the " +
                             "original failure message shows the history."
            };
        }

        private static List<RepairEntry> Current()
        {
            string path = RepairMemory.Path();
            var info = new FileInfo(path);
            string key = info.Exists
                ? path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks
                : path + "|absent";
            lock (Sync)
            {
                if (key == _cachedKey) return _cached;
            }
            string problem = null;
            List<RepairEntry> read = info.Exists
                ? RepairMemory.ReadForGate(path, out problem)
                : new List<RepairEntry>();
            if (problem != null)
                Log.Warn("repair memory at " + path + " did not parse; the quarantine gate refuses nothing until it " +
                         "does: " + problem);
            lock (Sync)
            {
                _cachedKey = key;
                _cached = read;
                return read;
            }
        }
    }
}
