// -----------------------------------------------------------------------------
// Horizun Core - original Horizun code. Revit-free.
//
// A PROVENANCE RECORD IS WRITTEN WHEN THE MODEL SAYS SO, NOT WHEN SetEntity RETURNED.
//
// The CAD and IFC applies stamped each element inside a transaction and reported
// `written: true` the moment SetEntity returned - before the transaction was
// committed, and with its commit status ignored (audit 2026-10-08, 6.2). A
// transaction Revit rolled back left elements ANONYMOUS under a reply that said
// they remembered their origin, and the next run built them a second time.
//
// The ledger holds every row that claimed a write until the commit is known and
// the record has been read back off the element. Only then is `written` true.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class ProvenanceLedger
    {
        private sealed class Pending
        {
            public JObject Row;
            public Func<string> ReadBack;
        }

        private readonly List<Pending> _pending = new List<Pending>();

        /// <summary>Rows that claimed a write and are waiting for the commit.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// A row whose SetEntity returned inside the transaction. It is held, NOT
        /// written: <paramref name="readBack"/> runs after the commit and answers null
        /// when the element carries the record that was meant, or why it does not.
        /// </summary>
        public void Wrote(JObject row, Func<string> readBack)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            row["written"] = false;
            row["written_state"] = "pending_commit";
            _pending.Add(new Pending { Row = row, ReadBack = readBack });
        }

        /// <summary>
        /// Decide every held row. <paramref name="committed"/> is the transaction's own
        /// status; <paramref name="status"/> names it for the reply. Returns how many
        /// rows are now written, every one of them read back off the model.
        /// </summary>
        public int Settle(bool committed, string status)
        {
            int confirmed = 0;
            foreach (Pending p in _pending)
            {
                if (!committed)
                {
                    Refute(p.Row, "the provenance transaction did not commit (" + (status ?? "unknown") +
                                  "), so this element is BUILT and ANONYMOUS: the next run will not recognise it");
                    continue;
                }
                string why;
                try { why = p.ReadBack == null ? "nothing could read it back" : p.ReadBack(); }
                catch (Exception ex) { why = "reading it back threw: " + ex.Message; }
                if (why == null)
                {
                    p.Row["written"] = true;
                    p.Row["written_state"] = "read_back";
                    confirmed++;
                }
                else Refute(p.Row, "the transaction committed but the record did not read back off the element: " + why);
            }
            _pending.Clear();
            return confirmed;
        }

        private static void Refute(JObject row, string means)
        {
            row["written"] = false;
            row["written_state"] = "not_written";
            row["means"] = means;
        }

        /// <summary>
        /// Null when the record read off the element is the one that was written, or the
        /// first field that differs. Empty and null are the same value: the store writes
        /// a null field as an empty string.
        /// </summary>
        public static string CadDiffers(CadProvenance expected, CadProvenance got, string readProblem)
        {
            if (!string.IsNullOrEmpty(readProblem)) return readProblem;
            if (expected == null) return "no record was meant";
            if (got == null) return "the element carries no CAD provenance";
            if (!Same(expected.CandidateId, got.CandidateId)) return "candidate_id differs";
            if (!Same(expected.SemanticId, got.SemanticId)) return "semantic_id differs";
            if (!Same(expected.RuleId, got.RuleId)) return "rule_id differs";
            if (!Same(expected.PlanFingerprint, got.PlanFingerprint)) return "plan_fingerprint differs";
            if (!Same(expected.RequirementSetSha256, got.RequirementSetSha256)) return "requirement_set_sha256 differs";
            if (!Same(expected.PlacementId, got.PlacementId)) return "placement_id differs";
            return null;
        }

        /// <summary>Ordinal, with null and empty equal.</summary>
        public static bool Same(string a, string b) =>
            string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    }
}
