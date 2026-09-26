// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// The `requestState` of a 2026-07-28 multi round-trip request (MRTR, SEP-2322).
//
// In that revision a tool that needs the person's input does not send a request to
// the client; it RETURNS an InputRequiredResult - the questions plus an opaque
// `requestState` - and the client calls the tool again with the answers and that
// state echoed back. Everything the server needs to continue rides in the state.
//
// The specification is explicit about what that makes the state (basic/patterns/
// mrtr, "Server Requirements"): servers MUST treat it as attacker-controlled input,
// MUST protect its integrity when it influences business logic and reject state that
// fails verification, SHOULD bind it to the principal, a short expiry and the
// originating request, and MUST enforce single use server-side when a state must be
// consumed at most once. Here it decides which answers are written to which file, so
// every one of those applies:
//
//   * INTEGRITY. payload + HMAC-SHA256 under a 256-bit key generated in this process
//     and never written anywhere. A state the client altered does not verify; a state
//     from an earlier server process does not verify either, which is the safe
//     failure (ask again) rather than the dangerous one (trust it).
//   * BINDING. The payload carries the tool name, a digest of the call's arguments,
//     and the client's declared name. A retry whose arguments differ - another
//     `path`, `dry_run` flipped - does not match its state and is refused: the state
//     can never carry answers to a file the person was not asked about.
//   * EXPIRY. Each state lives `ttl` from the moment it was issued.
//   * SINGLE USE. A state is consumed when its answer is applied. The consumed set is
//     in memory, bounded, and pruned by expiry - a replayed state is refused, so an
//     old retry cannot re-apply old answers over a file somebody has since edited.
//
// The payload is signed, not encrypted: it holds the answers the person typed and
// the ids of the questions, nothing the client did not already see. The client is
// told not to read it (the spec says MUST NOT), and nothing here relies on that.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class MrtrRequestState
    {
        /// <summary>Format marker; a state from another format version is refused, never guessed at.</summary>
        public const string Version = "hz-mrtr-1";

        /// <summary>The most consumed states remembered at once. Past it, the oldest expired ones go first.</summary>
        internal const int MaxConsumed = 4096;

        private static readonly byte[] Key = NewKey();
        private static readonly ConcurrentDictionary<string, long> Consumed =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);

        /// <summary>The clock, replaceable by tests so expiry can be proved without sleeping.</summary>
        internal static Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;

        private static byte[] NewKey()
        {
            var key = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            return key;
        }

        /// <summary>
        /// The digest a state is bound to: the tool and its arguments, canonicalised so
        /// property order does not matter and anything else does.
        /// </summary>
        public static string Binding(string tool, JObject arguments)
        {
            string canonical = (tool ?? "") + "\n" + Canonical(arguments ?? new JObject()).ToString(Formatting.None);
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        }

        /// <summary>
        /// Seal a payload. The caller's fields are kept; the envelope adds v, iat, exp and
        /// a fresh nonce. Returns the opaque string that goes out as requestState.
        /// </summary>
        public static string Seal(JObject payload, TimeSpan ttl)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var body = (JObject)payload.DeepClone();
            DateTimeOffset now = Now();
            body["v"] = Version;
            body["iat"] = now.ToUnixTimeMilliseconds();
            body["exp"] = now.Add(ttl).ToUnixTimeMilliseconds();
            body["nonce"] = Guid.NewGuid().ToString("N");
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            return B64(bytes) + "." + B64(Mac(bytes));
        }

        /// <summary>
        /// Verify a state and return its payload. <paramref name="reason"/> is one of
        /// malformed, tampered, expired, or null on success. An EXPIRED state is still
        /// authentic, so its payload is returned alongside the reason - the answers in it
        /// are the person's and may be handed back - but it must not be acted on.
        /// </summary>
        public static JObject Open(string state, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(state) || state.Length > 1024 * 1024) { reason = "malformed"; return null; }
            int dot = state.IndexOf('.');
            if (dot <= 0 || dot != state.LastIndexOf('.')) { reason = "malformed"; return null; }
            byte[] bytes, mac;
            if (!TryUnB64(state.Substring(0, dot), out bytes) || !TryUnB64(state.Substring(dot + 1), out mac))
            {
                reason = "malformed";
                return null;
            }
            if (!FixedTimeEquals(Mac(bytes), mac)) { reason = "tampered"; return null; }

            JObject body;
            try { body = JObject.Parse(Encoding.UTF8.GetString(bytes)); }
            catch (JsonException) { reason = "malformed"; return null; }
            if ((string)body["v"] != Version || body["exp"]?.Type != JTokenType.Integer ||
                body["nonce"]?.Type != JTokenType.String)
            {
                reason = "malformed";
                return null;
            }
            if (Now().ToUnixTimeMilliseconds() > (long)body["exp"]) reason = "expired";
            return body;
        }

        /// <summary>
        /// Mark a state as used. False when it could not be, with <paramref name="refusalReason"/>
        /// naming why: "replayed" (this nonce was already consumed - single use, spent) or
        /// "capacity" (the table is full of OTHER still-valid nonces and none could be forgotten
        /// safely). Called only once the state is about to be acted on, so a retry that merely
        /// lacked its answer (and is asked again with the same state) does not burn it.
        /// </summary>
        public static bool TryConsume(JObject payload, out string refusalReason)
        {
            refusalReason = null;
            string nonce = (string)payload["nonce"];
            long exp = (long)payload["exp"];
            Prune();
            if (Consumed.ContainsKey(nonce)) { refusalReason = "replayed"; return false; }
            // THE CAPACITY GATE. A full table used to evict the oldest-expiring LIVE nonce to
            // make room - which forgets a state that is still within its single-use replay
            // window, exactly the guarantee this whole file exists to hold. An attacker (or an
            // accidental retry storm) filling the table with unrelated requests could evict a
            // target's still-valid nonce and then replay the target's own original state.
            // Refuse the new consume instead: the caller is told plainly and can retry once the
            // table has room (expiry, not eviction, is the only thing that frees a slot).
            if (Consumed.Count >= MaxConsumed) { refusalReason = "capacity"; return false; }
            bool added = Consumed.TryAdd(nonce, exp);
            if (!added) refusalReason = "replayed";   // lost a race with a concurrent consume of the same nonce
            return added;
        }

        /// <summary>Removes only EXPIRED entries, and only bothers scanning once the table is
        /// near its cap (the common case never pays for this at all). Never evicts a live
        /// (unexpired) one - see TryConsume's capacity gate for why a full table refuses
        /// instead of forgetting.</summary>
        private static void Prune()
        {
            if (Consumed.Count < MaxConsumed) return;
            long now = Now().ToUnixTimeMilliseconds();
            foreach (var kv in Consumed)
                if (kv.Value < now) Consumed.TryRemove(kv.Key, out _);
        }

        internal static void ResetForTests() => Consumed.Clear();

        private static JToken Canonical(JToken t)
        {
            if (t is JObject o)
            {
                var sorted = new JObject();
                var names = new System.Collections.Generic.List<string>();
                foreach (JProperty p in o.Properties()) names.Add(p.Name);
                names.Sort(StringComparer.Ordinal);
                foreach (string n in names) sorted[n] = Canonical(o[n]);
                return sorted;
            }
            if (t is JArray a)
            {
                var copy = new JArray();
                foreach (JToken item in a) copy.Add(Canonical(item));
                return copy;
            }
            return t.DeepClone();
        }

        private static byte[] Mac(byte[] bytes)
        {
            using (var h = new HMACSHA256(Key)) return h.ComputeHash(bytes);
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string B64(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static bool TryUnB64(string s, out byte[] bytes)
        {
            bytes = null;
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_'))
                    return false;
            string padded = s.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 1: return false;
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            try { bytes = Convert.FromBase64String(padded); return true; }
            catch (FormatException) { return false; }
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
