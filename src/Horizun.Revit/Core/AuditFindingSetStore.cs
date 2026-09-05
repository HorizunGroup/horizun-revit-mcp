// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHAT AN AUDIT FOUND, kept for the correction that cites it.
//
// horizun_apply_corrections takes a finding_set_fingerprint and a list of
// finding ids. To act on those it needs the findings themselves - which check,
// which elements, whether the list was cut - and the caller's copy of them is
// not evidence: a client could hand back four ids it never received. So the
// audit RECORDS the set it published, keyed by its fingerprint, and the
// correction reads the record rather than the request.
//
// IN MEMORY, FOR THIS SESSION, exactly as confirmation tokens are. A finding set
// that survived a restart would describe a model that may have been saved,
// synced or replaced in between, and the fingerprint could not tell. The
// correction re-runs the cited checks before it acts in any case; the record is
// what it compares them TO, and what it lists as skipped.
//
// Revit-free, so all of it is provable at a desk.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class RecordedFinding
    {
        public string FindingId;
        public string Check;
        public bool IsIssue;
        /// <summary>True when shown &lt; total: the scope of this finding is unknown.</summary>
        public bool Truncated;
        public long Total;
        public int Shown;
        /// <summary>The items as published, cloned - a recipe filters on their typed codes.</summary>
        public JArray Items = new JArray();
        public List<long> ElementIds = new List<long>();
    }

    public sealed class FindingSetRecord
    {
        public string Fingerprint;
        public string DocumentTitle;
        public string DocumentFingerprint;
        public int Top;
        public string RecordedUtc;
        public List<RecordedFinding> Findings = new List<RecordedFinding>();

        public RecordedFinding Find(string findingId)
        {
            if (string.IsNullOrEmpty(findingId)) return null;
            foreach (RecordedFinding f in Findings)
                if (string.Equals(f.FindingId, findingId, StringComparison.Ordinal)) return f;
            return null;
        }

        public RecordedFinding FindByCheck(string check)
        {
            if (string.IsNullOrEmpty(check)) return null;
            foreach (RecordedFinding f in Findings)
                if (string.Equals(f.Check, check, StringComparison.Ordinal)) return f;
            return null;
        }

        /// <summary>
        /// Build the record from the findings an audit is about to publish. Each
        /// finding must already carry its finding_id; the ids are read, never
        /// recomputed here, so the record and the reply cannot disagree.
        /// </summary>
        public static FindingSetRecord From(string fingerprint, string title, string documentFingerprint,
                                            int top, string recordedUtc, JArray findings)
        {
            var r = new FindingSetRecord
            {
                Fingerprint = fingerprint,
                DocumentTitle = title,
                DocumentFingerprint = documentFingerprint,
                Top = top,
                RecordedUtc = recordedUtc
            };
            foreach (JToken t in findings ?? new JArray())
            {
                var o = t as JObject;
                if (o == null) continue;
                var items = (o["items"] as JArray) ?? new JArray();
                int shown = o["shown"]?.Type == JTokenType.Integer ? (int)o["shown"] : items.Count;
                long total = o["total"]?.Type == JTokenType.Integer ? (long)o["total"] : items.Count;
                r.Findings.Add(new RecordedFinding
                {
                    FindingId = (string)o["finding_id"],
                    Check = (string)o["check"],
                    IsIssue = o["is_issue"]?.Type == JTokenType.Boolean && (bool)o["is_issue"],
                    Truncated = total > shown,
                    Total = total,
                    Shown = shown,
                    Items = (JArray)items.DeepClone(),
                    ElementIds = FindingIdentity.ElementIdsOf(items)
                });
            }
            return r;
        }
    }

    public sealed class AuditFindingSetStore
    {
        /// <summary>The session's store. Nothing here outlives the Revit process.</summary>
        public static readonly AuditFindingSetStore Session = new AuditFindingSetStore();

        /// <summary>
        /// How many runs are kept. A session that audits a dozen models keeps them
        /// all; one that audits the same model a hundred times keeps the last
        /// hundred distinct sets, which is more than any correction cycle needs.
        /// </summary>
        public const int Capacity = 128;

        private readonly object _lock = new object();
        private readonly List<FindingSetRecord> _records = new List<FindingSetRecord>();

        public void Record(FindingSetRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Fingerprint)) return;
            lock (_lock)
            {
                _records.RemoveAll(r => string.Equals(r.Fingerprint, record.Fingerprint, StringComparison.Ordinal));
                _records.Add(record);
                while (_records.Count > Capacity) _records.RemoveAt(0);
            }
        }

        public bool TryGet(string fingerprint, out FindingSetRecord record)
        {
            record = null;
            if (string.IsNullOrEmpty(fingerprint)) return false;
            lock (_lock)
            {
                record = _records.FirstOrDefault(r => string.Equals(r.Fingerprint, fingerprint, StringComparison.Ordinal));
                return record != null;
            }
        }

        public int Count { get { lock (_lock) return _records.Count; } }
    }
}
