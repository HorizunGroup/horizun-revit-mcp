// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// The origin split uses the bridge's record of committed writes in this
// Revit session (ChangeLedger + SyncLedgerRules ids). So every changed element is
// either in that record ("horizun_session") or not ("not_in_session_record").
// The second group is NOT claimed to be other people's work: it also holds hand
// edits in this Revit, writes older than the record, and anything the record
// dropped at its cap. Writes made before the baseline snapshot are not changes
// since it, so they are ignored.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public static class ModelDiffOrigin
    {
        public const string Session = "horizun_session";
        public const string NotInRecord = "not_in_session_record";
        public const string Unknown = "unknown";

        public sealed class SessionWrite
        {
            public DateTime AtUtc;
            public long[] Ids = new long[0];
        }

        /// <summary>Marks each row's Origin and returns the block for the reply.</summary>
        public static JObject Classify(DiffResult result, IEnumerable<SessionWrite> writes, DateTime baselineUtc, bool recordMayBeTruncated)
        {
            var ids = new HashSet<long>();
            foreach (SessionWrite w in writes ?? Enumerable.Empty<SessionWrite>())
                if (w != null && w.AtUtc >= baselineUtc && w.Ids != null) foreach (long id in w.Ids) ids.Add(id);
            foreach (DiffRow row in result.Rows) row.Origin = ids.Contains(row.ElementId) ? Session : recordMayBeTruncated ? Unknown : NotInRecord;
            return new JObject
            {
                ["assessed"] = true,
                ["basis"] = "this Revit session's record of Horizun writes since the baseline snapshot",
                ["session_record_complete"] = !recordMayBeTruncated,
                [Session] = recordMayBeTruncated ? UnknownCounts() : Counts(result.Rows, Session),
                [NotInRecord] = recordMayBeTruncated ? Counts(result.Rows, Unknown) : Counts(result.Rows, NotInRecord),
                [Unknown] = Counts(result.Rows, Unknown),
                ["note"] = "not_in_session_record means only that no write was found in the available session record: it may be another " +
                           "person's change, a hand edit in this Revit, or a write missing from the record. Worksharing metadata " +
                           "is separate evidence and does not prove ownership or turn this group into 'others'."
            };
        }

        private static JObject UnknownCounts() => new JObject
        {
            ["total"] = JValue.CreateNull(), ["added"] = JValue.CreateNull(),
            ["deleted"] = JValue.CreateNull(), ["modified"] = JValue.CreateNull()
        };

        public static JObject NotAssessed(string reason) => new JObject
        {
            ["assessed"] = false,
            ["reason"] = reason,
            ["note"] = "No origin is claimed: the session record can only be compared against a live document."
        };

        private static JObject Counts(IEnumerable<DiffRow> rows, string origin)
        {
            List<DiffRow> mine = rows.Where(r => r.Origin == origin).ToList();
            return new JObject
            {
                ["total"] = mine.Count,
                ["added"] = mine.Count(r => r.State == ModelDiffRules.Added),
                ["deleted"] = mine.Count(r => r.State == ModelDiffRules.Deleted),
                ["modified"] = mine.Count(r => r.State == ModelDiffRules.Modified)
            };
        }
    }
}
