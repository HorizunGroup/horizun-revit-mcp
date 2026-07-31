// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// "Yes, do it" - tied to a specific document and a specific plan.
//
// A destructive command that takes a boolean confirm=true is not confirmed. The
// caller agreed to SOMETHING; nothing in the request says what. Between the
// dry-run that produced the plan and the call that executes it, any of these can
// change without anybody noticing:
//
//   * the active document (a second Revit, a user clicking another window);
//   * the model itself (a synchronize pulls in somebody else's work);
//   * the scope (the same category now matches 400 elements instead of 12).
//
// So the plan is what gets confirmed, not the intention. A dry run issues a token
// bound to THREE things - the command, a fingerprint of the document, and a hash
// of the plan - and execution is refused unless all three still match. The token
// is single-use and expires, because an approval from twenty minutes ago is an
// approval of a model that no longer exists.
//
// Refusals name which of the three broke. "Invalid token" tells the caller
// nothing they can act on; "the document changed" tells them exactly what to
// re-run.
//
// Revit-free: the rule has to be provable without a building, and the store is
// just a dictionary with a clock.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public enum ConfirmationState
    {
        /// <summary>Everything matches. The token is now spent.</summary>
        Valid,

        /// <summary>No such token was ever issued by this process.</summary>
        Unknown,

        /// <summary>Issued, but its time ran out.</summary>
        Expired,

        /// <summary>Already used. A confirmation authorises one execution.</summary>
        AlreadyUsed,

        /// <summary>Issued for a different document than the one in front of us now.</summary>
        DocumentChanged,

        /// <summary>The document matches but the plan does not: the scope moved.</summary>
        PlanChanged,

        /// <summary>Issued for a different command.</summary>
        WrongCommand
    }

    public sealed class ConfirmationCheck
    {
        public ConfirmationState State { get; internal set; }
        public bool Ok => State == ConfirmationState.Valid;

        /// <summary>A sentence naming what broke and what to do about it.</summary>
        public string Message { get; internal set; }
    }

    public sealed class Confirmation
    {
        public string Token { get; internal set; }
        public string Command { get; internal set; }
        public string DocumentKey { get; internal set; }
        public string PlanHash { get; internal set; }
        public DateTime IssuedUtc { get; internal set; }
        public DateTime ExpiresUtc { get; internal set; }
        public bool Used { get; internal set; }
    }

    public sealed class ConfirmationStore
    {
        /// <summary>
        /// Long enough to read a plan and decide, short enough that the model has probably
        /// not moved. A confirmation is not a licence.
        /// </summary>
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

        private readonly Dictionary<string, Confirmation> _issued = new Dictionary<string, Confirmation>(StringComparer.Ordinal);
        private readonly Func<DateTime> _now;
        private readonly object _lock = new object();

        public ConfirmationStore(Func<DateTime> utcNow = null)
        {
            _now = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>Issue a token for a plan that was just computed. Never for one that was not.</summary>
        public Confirmation Issue(string command, string documentKey, string planHash, TimeSpan? ttl = null)
        {
            var c = new Confirmation
            {
                Token = NewToken(),
                Command = command,
                DocumentKey = documentKey,
                PlanHash = planHash,
                IssuedUtc = _now(),
                ExpiresUtc = _now().Add(ttl ?? DefaultTtl)
            };
            lock (_lock)
            {
                Prune();
                _issued[c.Token] = c;
            }
            return c;
        }

        /// <summary>
        /// Check a token against the situation NOW. On success the token is spent, so the
        /// same approval cannot execute twice.
        /// </summary>
        public ConfirmationCheck Validate(string token, string command, string documentKey, string planHash)
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(token) || !_issued.TryGetValue(token, out Confirmation c))
                    return Fail(ConfirmationState.Unknown,
                        "No such confirmation token. Run this command with dry_run=true first; it returns the plan " +
                        "and a token for exactly that plan. Tokens do not survive a restart of the bridge.");

                if (c.Used)
                    return Fail(ConfirmationState.AlreadyUsed,
                        "That confirmation was already used. One approval authorises one execution - re-run the " +
                        "dry run to see the CURRENT plan and get a new token.");

                if (_now() > c.ExpiresUtc)
                    return Fail(ConfirmationState.Expired,
                        "That confirmation expired (issued " + c.IssuedUtc.ToString("u") + ", valid for " +
                        (int)(c.ExpiresUtc - c.IssuedUtc).TotalMinutes + " minutes). The model may have moved since. " +
                        "Re-run the dry run.");

                if (!string.Equals(c.Command, command, StringComparison.Ordinal))
                    return Fail(ConfirmationState.WrongCommand,
                        "That confirmation was issued for '" + c.Command + "', not for '" + command +
                        "'. A confirmation authorises one operation, not a session.");

                if (!string.Equals(c.DocumentKey, documentKey, StringComparison.Ordinal))
                    return Fail(ConfirmationState.DocumentChanged,
                        "That confirmation was issued for a DIFFERENT document than the one this would run against. " +
                        "Nothing was changed. Re-run the dry run against the document you mean, and check " +
                        "horizun_target if more than one Revit is open.");

                if (!string.Equals(c.PlanHash, planHash, StringComparison.Ordinal))
                    return Fail(ConfirmationState.PlanChanged,
                        "THIS REQUEST IS NOT THE ONE THAT WAS REHEARSED. The token is bound to a hash of the " +
                        "request's own fields, and at least one of them differs from the dry run - INCLUDING " +
                        "fields that do not change which elements are touched, such as 'save'. Approving a " +
                        "rehearsal of one request and executing a different one is exactly what this refuses. " +
                        "Nothing was changed. Re-run the dry run with the request you actually intend to " +
                        "execute - same flags and all - and approve THAT one. " +
                        "WHAT THIS DOES NOT TELL YOU: this hash is computed from the request, not from the " +
                        "model, so it cannot detect that the model moved underneath you. Staleness is covered " +
                        "by the token's expiry, and the document by its own separate check.");

                c.Used = true;
                return new ConfirmationCheck { State = ConfirmationState.Valid, Message = null };
            }
        }

        /// <summary>Issued and still usable. For reporting, never for deciding.</summary>
        public int OutstandingCount
        {
            get { lock (_lock) { Prune(); return _issued.Count; } }
        }

        private static ConfirmationCheck Fail(ConfirmationState s, string message) =>
            new ConfirmationCheck { State = s, Message = message };

        private void Prune()
        {
            DateTime now = _now();
            var dead = new List<string>();
            foreach (var kv in _issued)
                if (kv.Value.Used || now > kv.Value.ExpiresUtc) dead.Add(kv.Key);
            foreach (string k in dead) _issued.Remove(k);
        }

        private static string NewToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return "hz-" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// A stable fingerprint of a request's SCOPE: the fields that decide WHAT gets
        /// touched, hashed in the order the caller named them.
        ///
        /// ARRAY ORDER IS PART OF THE PLAN. The first version sorted the elements of every
        /// array before hashing, on the reasoning that "the same set in another order is
        /// the same plan". For a list of element ids that is true. For a list of
        /// OPERATIONS it is false, and write_params_verified takes a list of operations:
        /// two writes to the SAME parameter apply in order and the last one wins, so
        /// [{p:X,v:1},{p:X,v:2}] and [{p:X,v:2},{p:X,v:1}] leave the model in two different
        /// states. Sorted, they hashed identically - and a token issued for the rehearsal
        /// of one was accepted for the execution of the other, which is the single thing
        /// the confirmation exists to refuse.
        ///
        /// The cost of the fix is that a caller who reorders an id list must re-rehearse.
        /// That is a re-run; the alternative was a silently different write.
        /// </summary>
        public static string PlanHash(JObject request, params string[] scopeFields)
        {
            var parts = new List<string>();
            foreach (string field in scopeFields ?? new string[0])
            {
                JToken t = request?[field];
                if (t == null) { parts.Add(field + "=-"); continue; }

                if (t is JArray arr)
                {
                    // Index-prefixed, so position is hashed as explicitly as content, and
                    // separated by a control character: Newtonsoft escapes those inside
                    // strings, so no element can contain the separator and two arrays
                    // cannot run together into one that hashes the same.
                    var sb = new StringBuilder();
                    sb.Append(field).Append("[").Append(arr.Count).Append("]=");
                    for (int i = 0; i < arr.Count; i++)
                    {
                        sb.Append(i).Append(':');
                        sb.Append(arr[i]?.ToString(Newtonsoft.Json.Formatting.None) ?? "");
                        sb.Append((char)30);   // a record separator between elements
                    }
                    parts.Add(sb.ToString());
                }
                else parts.Add(field + "=" + t.ToString(Newtonsoft.Json.Formatting.None));
            }
            return HashPlan(parts.ToArray());
        }

        /// <summary>
        /// A stable fingerprint of a plan, over parts whose order is already meaningful.
        /// </summary>
        public static string HashPlan(params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (string p in parts ?? new string[0])
            {
                sb.Append(p ?? "");
                sb.Append((char)31);   // a unit separator, so parts cannot run together
            }
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(h, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
