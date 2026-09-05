// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// What horizun_apply_cad_update remembers about its own runs, inside one Revit
// session.
//
// The typed commands the update drives each rehearse, verify and re-read their
// own work, and the bridge's idempotency lives at the dispatch boundary - which
// an in-process child call never crosses. So a caller who re-sends the same
// apply after a timeout would, without this, drive the same creates a second
// time and build the drawing twice. Two rules close that:
//
//   the SAME key with the SAME actions  → the recorded reply comes back, marked
//                                          as a replay, and nothing runs;
//   the SAME key with DIFFERENT actions → refused: a key is a promise about one
//                                          piece of work, and reusing it for
//                                          another is how a retry writes the
//                                          wrong thing under a familiar name.
//
// And a run that ended PARTIAL leaves a note against its placement, so the next
// plan applied to that placement - a different plan, because the model moved -
// carries what landed and what did not, rather than starting the story afresh.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class CadUpdateLedgerEntry
    {
        public string IdempotencyKey;
        public string ActionsFingerprint;
        public string PlacementId;
        public string State;               // applied | partial | rehearsed
        public JObject Reply;
        public DateTime RecordedUtc;
        public int ReplayCount;
    }

    /// <summary>The verdict on an incoming apply, before anything runs.</summary>
    public sealed class CadUpdateLedgerDecision
    {
        /// <summary>proceed | replay | refuse</summary>
        public string Outcome;
        public CadUpdateLedgerEntry Entry;
        public string Refusal;
    }

    public static class CadUpdateLedger
    {
        private const int Bound = 256;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CadUpdateLedgerEntry> ByKey =
            new Dictionary<string, CadUpdateLedgerEntry>(StringComparer.Ordinal);
        private static readonly Dictionary<string, CadUpdateLedgerEntry> LastPartialByPlacement =
            new Dictionary<string, CadUpdateLedgerEntry>(StringComparer.Ordinal);

        public static CadUpdateLedgerDecision Decide(string idempotencyKey, string actionsFingerprint)
        {
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return new CadUpdateLedgerDecision { Outcome = "proceed" };
            lock (Gate)
            {
                CadUpdateLedgerEntry existing;
                if (!ByKey.TryGetValue(idempotencyKey, out existing))
                    return new CadUpdateLedgerDecision { Outcome = "proceed" };
                if (string.Equals(existing.ActionsFingerprint, actionsFingerprint, StringComparison.Ordinal))
                {
                    existing.ReplayCount++;
                    return new CadUpdateLedgerDecision { Outcome = "replay", Entry = existing };
                }
                return new CadUpdateLedgerDecision
                {
                    Outcome = "refuse",
                    Entry = existing,
                    Refusal = "idempotency_key_reused: '" + idempotencyKey + "' was already used in this Revit " +
                              "session for a DIFFERENT set of actions (recorded " +
                              existing.RecordedUtc.ToString("o") + ", state " + existing.State + "). A key " +
                              "names one piece of work; the same key over different actions is a retry aimed " +
                              "at the wrong thing. Nothing was written. Send the new plan under a new key."
                };
            }
        }

        public static void Record(string idempotencyKey, string actionsFingerprint, string placementId,
                                  string state, JObject reply)
        {
            lock (Gate)
            {
                var entry = new CadUpdateLedgerEntry
                {
                    IdempotencyKey = idempotencyKey,
                    ActionsFingerprint = actionsFingerprint,
                    PlacementId = placementId,
                    State = state,
                    Reply = reply == null ? null : (JObject)reply.DeepClone(),
                    RecordedUtc = DateTime.UtcNow
                };
                if (!string.IsNullOrWhiteSpace(idempotencyKey))
                {
                    if (ByKey.Count >= Bound)
                    {
                        // Oldest out. A bounded ledger forgets, and forgetting a
                        // key means a very late retry runs again - the reply says
                        // that the ledger is session-scoped and bounded for exactly
                        // this reason.
                        string oldest = ByKey.OrderBy(k => k.Value.RecordedUtc).First().Key;
                        ByKey.Remove(oldest);
                    }
                    ByKey[idempotencyKey] = entry;
                }
                if (!string.IsNullOrWhiteSpace(placementId))
                {
                    if (state == "partial") LastPartialByPlacement[placementId] = entry;
                    else if (state == "applied") LastPartialByPlacement.Remove(placementId);
                }
            }
        }

        /// <summary>The last run on this placement that ended partial and has not been followed by a clean one.</summary>
        public static CadUpdateLedgerEntry LastPartialFor(string placementId)
        {
            if (string.IsNullOrWhiteSpace(placementId)) return null;
            lock (Gate)
            {
                CadUpdateLedgerEntry e;
                return LastPartialByPlacement.TryGetValue(placementId, out e) ? e : null;
            }
        }

        /// <summary>What a later reply says about an earlier partial run, so nothing is hidden.</summary>
        public static JObject Describe(CadUpdateLedgerEntry partial)
        {
            if (partial == null) return null;
            JObject reply = partial.Reply ?? new JObject();
            return new JObject
            {
                ["idempotency_key"] = partial.IdempotencyKey,
                ["recorded_utc"] = partial.RecordedUtc.ToString("o"),
                ["state"] = partial.State,
                ["actions_attempted"] = reply["actions_attempted"],
                ["actions_failed"] = reply["actions_failed"],
                ["elements_touched"] = reply["elements_touched"],
                ["provenance_written"] = reply["provenance_written"],
                ["actions"] = reply["actions"],
                ["means"] = "an earlier apply on this same placement ended PARTIAL in this Revit session: the " +
                            "actions listed as ok are IN the model and stamped, the failed one and everything " +
                            "after it are not. This plan was made against the model as it is now, so it already " +
                            "sees what landed; it is reported here so the earlier failure is not forgotten."
            };
        }

        /// <summary>Tests only: a ledger that remembers the last test's keys would fail the next one.</summary>
        public static void ResetForTests()
        {
            lock (Gate) { ByKey.Clear(); LastPartialByPlacement.Clear(); }
        }
    }
}
