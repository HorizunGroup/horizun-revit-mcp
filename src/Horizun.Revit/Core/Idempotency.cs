// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// "I never got your answer. Here it is again."
//
// AT-MOST-ONCE ONLY HELD IF THE REQUEST ARRIVED ONCE. AsyncQueue guarantees a
// queued entry is claimed exactly once, and that guarantee was described as the
// reason run_async is safe to point at a mutation. It is half the story. The
// other half is the wire:
//
//   caller -> server -> pipe -> add-in       the script is queued
//   caller <- server <- pipe <- add-in       THE REPLY IS LOST
//
// The work is queued and will run. The caller has no job_id, no evidence
// anything happened, and a timeout. What it does next is send the request again -
// which is the correct thing for a client to do and produces a SECOND queue
// entry, claimed exactly once, executed exactly once, for a total of twice.
// Nothing downstream can tell those two apart from two deliberate runs.
//
// So a run_async request must carry an idempotency_key, and a key is bound to
// the WHOLE claim:
//
//   * the Revit process id      - a key means nothing across a restart
//   * the document identity     - the same script against another model is
//                                 another operation, not a retry
//   * SHA-256 of the code       - the script IS the payload
//   * every other argument      - canonicalised, so key order in the JSON does
//                                 not change the answer and a changed value does
//
// Re-sending the same key with the same claim returns the ORIGINAL job_id and
// queues nothing. Re-sending it with a different claim is REFUSED: a key that
// silently covered a different payload would be worse than no key at all, since
// the caller would believe the second request had been deduplicated when in fact
// it was discarded.
//
// WHAT THIS DOES NOT SURVIVE, stated because the guarantee is otherwise easy to
// over-read: the ledger is in memory, in this Revit process, exactly like
// ConfirmationStore and for the same reason. If Revit restarts, every key it
// issued is forgotten, and a retry after a restart WILL run the script again.
// That is not a hole that can be closed by persisting it - a key whose job was
// in flight when the process died has an outcome nobody knows, and replaying a
// mutation on that basis is the failure this file exists to prevent, not the
// cure for it. The process id is in the fingerprint so the binding is explicit
// and the reply can say which process the promise belongs to.
//
// Revit-free: the rule has to be provable without a building.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public enum IdempotencyOutcome
    {
        /// <summary>Never seen. The caller owns it; do the work.</summary>
        Fresh,

        /// <summary>Seen, and the claim is identical. Hand back the original answer and do NOTHING.</summary>
        Replay,

        /// <summary>Seen, and the claim is DIFFERENT. Refuse - see the header.</summary>
        Conflict
    }

    public sealed class IdempotentClaim
    {
        public string Key { get; internal set; }
        public string Command { get; internal set; }
        /// <summary>pid + document + code + arguments, hashed. See RequestFingerprint.</summary>
        public string Fingerprint { get; internal set; }
        public string JobId { get; internal set; }
        public DateTime ClaimedUtc { get; internal set; }
        /// <summary>How many times the caller has re-sent it. Reported, never used to decide anything.</summary>
        public int ReplayCount { get; internal set; }
    }

    public sealed class IdempotencyDecision
    {
        public IdempotencyOutcome Outcome { get; internal set; }
        public IdempotentClaim Claim { get; internal set; }
        /// <summary>Set on Conflict: what differs, and what to do instead.</summary>
        public string Message { get; internal set; }

        public bool IsFresh => Outcome == IdempotencyOutcome.Fresh;
    }

    public sealed class IdempotencyLedger
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, IdempotentClaim> _claims =
            new Dictionary<string, IdempotentClaim>(StringComparer.Ordinal);

        /// <summary>How many keys this session has seen. Reported, never used to decide anything.</summary>
        public int Count { get { lock (_gate) return _claims.Count; } }

        /// <summary>
        /// Take the key, or find out somebody already did.
        ///
        /// THE WHOLE THING IS UNDER ONE LOCK, including creating the job record. Two
        /// requests carrying the same key can be in flight at once - that is precisely
        /// what a client retrying a timeout produces - and a check-then-act would let
        /// both see "not present" and both queue. `startWork` runs only on the Fresh
        /// path and only while the lock is held, so the record and the ledger entry
        /// cannot disagree about whether this key has been claimed.
        ///
        /// It is deliberately the CALLER's job to queue the work after a Fresh
        /// decision: this file knows nothing about queues, which is what lets the rule
        /// be tested without Revit.
        /// </summary>
        public IdempotencyDecision Claim(string key, string command, string fingerprint, Func<string> startWork)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("An idempotency key is required.", "key");
            if (startWork == null) throw new ArgumentNullException("startWork");

            lock (_gate)
            {
                IdempotentClaim existing;
                if (_claims.TryGetValue(key, out existing))
                {
                    if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                        return new IdempotencyDecision
                        {
                            Outcome = IdempotencyOutcome.Conflict,
                            Claim = existing,
                            Message =
                                "idempotency_key '" + key + "' was already used in this Revit session for a " +
                                "DIFFERENT request (job " + existing.JobId + ", claimed " +
                                existing.ClaimedUtc.ToString("u") + "). A key identifies one operation: the code, " +
                                "every argument, the target document and the Revit process are all part of it, and " +
                                "at least one of them has changed. Nothing was queued and nothing ran. If this is a " +
                                "retry, send the IDENTICAL request. If it is new work, use a new key - reusing one " +
                                "would make this request look deduplicated when it had in fact been discarded."
                        };

                    existing.ReplayCount++;
                    return new IdempotencyDecision { Outcome = IdempotencyOutcome.Replay, Claim = existing };
                }

                var claim = new IdempotentClaim
                {
                    Key = key,
                    Command = command,
                    Fingerprint = fingerprint,
                    ClaimedUtc = DateTime.UtcNow,
                    ReplayCount = 0
                };
                claim.JobId = startWork();
                _claims[key] = claim;
                return new IdempotencyDecision { Outcome = IdempotencyOutcome.Fresh, Claim = claim };
            }
        }

        /// <summary>Look one up without claiming it. For reporting only.</summary>
        public IdempotentClaim Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (_gate)
            {
                IdempotentClaim c;
                return _claims.TryGetValue(key, out c) ? c : null;
            }
        }

        /// <summary>Empties it. Tests only - a running session never forgets a key.</summary>
        internal void Clear()
        {
            lock (_gate) _claims.Clear();
        }
    }

    /// <summary>
    /// What a request IS, reduced to one string.
    ///
    /// Canonical, because the same request may be serialised differently by two
    /// clients - or by the same client twice - and a retry that reordered its JSON
    /// keys must not read as new work. Object members are sorted; ARRAY ORDER IS
    /// KEPT, because a list of arguments in another order is a different call.
    /// </summary>
    public static class RequestFingerprint
    {
        /// <summary>
        /// pid + document + code + arguments.
        ///
        /// `ignore` names fields that are not part of the claim - the key itself, and
        /// anything that changes how the answer is DELIVERED rather than what is done.
        /// Everything not named is included: a field left out of a fingerprint is a
        /// field a caller can change without the guard noticing, which is the defect
        /// found in family_apply's plan hash.
        /// </summary>
        public static string Of(int revitPid, string documentFingerprint, string code, JObject request,
                                params string[] ignore)
        {
            var scrubbed = request == null ? new JObject() : (JObject)request.DeepClone();
            foreach (string f in ignore ?? new string[0]) scrubbed.Remove(f);

            var sb = new StringBuilder();
            sb.Append("revit_pid=").Append(revitPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append("\ndocument=").Append(documentFingerprint ?? "(none)");
            sb.Append("\ncode_sha256=").Append(Sha256Hex(code ?? ""));
            sb.Append("\nargs=").Append(Canonical(scrubbed));
            return Sha256Hex(sb.ToString());
        }

        /// <summary>
        /// A stable rendering of any JSON value. Members sorted by ordinal name so two
        /// serialisations of one request agree; arrays untouched so two different calls
        /// do not.
        /// </summary>
        public static string Canonical(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return "null";

            if (t is JObject o)
            {
                var names = new List<string>();
                foreach (JProperty p in o.Properties()) names.Add(p.Name);
                names.Sort(StringComparer.Ordinal);

                var sb = new StringBuilder("{");
                for (int i = 0; i < names.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonConvertName(names[i])).Append(':').Append(Canonical(o[names[i]]));
                }
                return sb.Append('}').ToString();
            }

            if (t is JArray a)
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Canonical(a[i]));
                }
                return sb.Append(']').ToString();
            }

            return t.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static string Sha256Hex(string s)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string JsonConvertName(string name) =>
            new JValue(name).ToString(Newtonsoft.Json.Formatting.None);
    }
}
