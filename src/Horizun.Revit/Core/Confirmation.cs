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
        WrongCommand,

        /// <summary>
        /// The request is identical and the document is the same one, but the MODEL moved:
        /// the elements the dry run resolved, or the values it read off them, are not what is
        /// there now. This is the state the old code confessed it could not detect.
        /// </summary>
        StalePlan
    }

    public sealed class ConfirmationCheck
    {
        public ConfirmationState State { get; internal set; }
        public bool Ok => State == ConfirmationState.Valid;

        /// <summary>A sentence naming what broke and what to do about it.</summary>
        public string Message { get; internal set; }
        internal JArray RehearsedRows { get; set; }
    }

    public sealed class Confirmation
    {
        public string Token { get; internal set; }
        public string Command { get; internal set; }
        public string DocumentKey { get; internal set; }
        public string PlanHash { get; internal set; }

        /// <summary>
        /// The digest of the identity (unique_id / expected_* / identity map) the rehearsal carried, or null when it carried none.
        /// An apply must carry the same identity: one that omits it, or changes it, is a different request.
        /// </summary>
        public string IdentityDigest { get; internal set; }

        /// <summary>
        /// The fingerprint of the ELEMENTS the dry run resolved - see ResolvedPlan. Null for
        /// a command that has not been taught to materialise its plan yet, and the check
        /// below says so out loud rather than reporting a guarantee it did not make.
        /// </summary>
        public string ElementFingerprint { get; internal set; }

        /// <summary>
        /// The PLAN the dry run resolved, kept beside its fingerprint so a stale refusal
        /// can NAME what moved instead of asking the caller to diff two runs by hand.
        /// The fingerprint decides; this only explains. Null for commands that do not
        /// materialise their plan, and for tokens issued before this existed.
        /// </summary>
        public ResolvedPlan RehearsedPlan { get; internal set; }
        internal JArray RehearsedRows { get; set; }
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

        /// <summary>Publish a document and, for a confirmable rehearsal, issue its plan-bound token.</summary>
        internal void StampResponse(JObject o, DocIdentity document, string commandName, string planHash,
                                    bool issueToken, ResolvedPlan plan, string limitNote = null, TimeSpan? ttl = null)
        {
            o["document"] = document?.Describe();
            o["document_fingerprint"] = document?.FingerprintDigest();
            if (plan != null) o["change_preview"] = PlanPreview.Describe(plan);
            if (!issueToken) return;

            string fingerprint = plan?.Fingerprint();
            Confirmation issued = Issue(commandName, document?.Fingerprint(), planHash, ttl, fingerprint, plan);
            if (plan != null)
            {
                // What was approved, in the answer itself. A count in the reply is what a
                // person actually reads before saying yes, and it is the same number the
                // fingerprint is taken over.
                o["plan_resolved"] = plan.DescribeResolution();
            }
            o["confirmation_token"] = issued.Token;
            o["confirmation_expires_utc"] = issued.ExpiresUtc.ToString("u");
            o["confirmation_note"] =
                "To execute this, call again with dry_run=false and this confirmation_token. It is SINGLE USE, it " +
                "expires, and it is bound to this document and this request - if either changes it is refused and " +
                "nothing is written." +
                (o["plan_resolved"] != null
                    ? " It is ALSO bound to the elements resolved above: if the model moves before you spend it, " +
                      "the apply is refused as a stale plan rather than applied to a different set."
                    : " NOTE: it is bound to the REQUEST, not to the resolved element set - this command does not " +
                      "materialise its plan yet, so a model that moves before you spend it would not be detected.") +
                (limitNote == null ? "" : " STATED LIMIT: " + limitNote);
        }

        /// <summary>Issue a token for a plan that was just computed. Never for one that was not.</summary>
        public Confirmation Issue(string command, string documentKey, string planHash, TimeSpan? ttl = null,
                                  string elementFingerprint = null, ResolvedPlan rehearsedPlan = null,
                                  JArray rehearsedRows = null)
        {
            DateTime issuedUtc = _now();
            var c = new Confirmation
            {
                Token = NewToken(),
                Command = command,
                DocumentKey = documentKey,
                PlanHash = planHash,
                IdentityDigest = IdentityBinding.Digest,
                ElementFingerprint = elementFingerprint,
                RehearsedPlan = rehearsedPlan,
                RehearsedRows = (JArray)rehearsedRows?.DeepClone(),
                IssuedUtc = issuedUtc,
                ExpiresUtc = issuedUtc.Add(ttl ?? DefaultTtl)
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
        public ConfirmationCheck Validate(string token, string command, string documentKey, string planHash) =>
            ValidateCore(token, command, documentKey, planHash, null, null, null);

        /// <summary>Refuse a mismatched request before measuring a reversible preview without spending its token.</summary>
        internal ConfirmationCheck CheckRequest(string token, string command, string documentKey, string planHash) =>
            ValidateCore(token, command, documentKey, planHash, null, null, null, spend: false);

        /// <summary>Validate with expiry judged at the moment the call arrived (see ValidateAndRenew with asOfUtc).</summary>
        public ConfirmationCheck Validate(string token, string command, string documentKey, string planHash, DateTime? asOfUtc) =>
            ValidateCore(token, command, documentKey, planHash, null, null, null, asOfUtc);

        /// <summary>
        /// Whether a token could still be spent at <paramref name="asOfUtc"/>: known, unused, not expired. Spends nothing.
        /// Null when it could; otherwise the refusal. Lets a long command refuse a dead approval before minutes of work.
        /// </summary>
        public string Liveness(string token, DateTime asOfUtc)
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(token) || !_issued.TryGetValue(token, out Confirmation c))
                    return "no such confirmation token (run the dry run first; tokens do not survive a restart of the bridge)";
                if (c.Used) return "that confirmation was already used";
                DateTime at = asOfUtc > _now() ? _now() : asOfUtc;
                if (at > c.ExpiresUtc) return "that confirmation expired (issued " + c.IssuedUtc.ToString("u") + ")";
                return null;
            }
        }

        /// <summary>
        /// Liveness, plus whether the token was issued for <paramref name="command"/> on <paramref name="documentKey"/>.
        /// Spends nothing. Null when it could still be spent here; otherwise the refusal. The plan is NOT compared
        /// (the caller has not resolved it yet): Validate does that, and spends the token, once the plan is known.
        /// </summary>
        public string Precheck(string token, string command, string documentKey, DateTime asOfUtc)
        {
            lock (_lock)
            {
                string dead = Liveness(token, asOfUtc);
                if (dead != null) return dead;
                if (!_issued.TryGetValue(token, out Confirmation c))
                    return "no such confirmation token (run the dry run first; tokens do not survive a restart of the bridge)";
                if (!string.Equals(c.Command, command, StringComparison.Ordinal))
                    return "that confirmation was issued for '" + c.Command + "', not for '" + command + "'";
                if (!string.Equals(c.DocumentKey, documentKey, StringComparison.Ordinal))
                    return "that confirmation was issued for a different document than the one this would run against";
                return null;
            }
        }

        /// <summary>
        /// Validates and spends the caller's approval now, then issues a private single-use
        /// continuation with the identical command, document, request hash and resolved-plan
        /// fingerprint. Long composite operations use this only after validating every child
        /// approval up front; child commands still re-resolve against the preserved fingerprint.
        /// </summary>
        public ConfirmationCheck ValidateAndRenew(string token, string command, string documentKey, string planHash,
                                                   TimeSpan? ttl, out string continuationToken) =>
            ValidateAndRenew(token, command, documentKey, planHash, ttl, null, out continuationToken);

        /// <summary>
        /// 5.0.4: the same, with expiry judged at <paramref name="asOfUtc"/> - the moment the composite call ARRIVED - instead
        /// of now. A composite command that resolves its plan for minutes before it reaches its tokens (apply_scan_plan on a
        /// large model) otherwise refused, as expired, an approval sent seconds after it was issued. Never later than now;
        /// model drift is still caught by each child's stale-plan comparison.
        /// </summary>
        public ConfirmationCheck ValidateAndRenew(string token, string command, string documentKey, string planHash,
                                                   TimeSpan? ttl, DateTime? asOfUtc, out string continuationToken)
        {
            continuationToken = null;
            ConfirmationCheck check = ValidateCore(token, command, documentKey, planHash, null, null, null, asOfUtc);
            if (!check.Ok) return check;
            lock (_lock)
            {
                Confirmation original = _issued[token];
                var continuation = new Confirmation
                {
                    Token = NewToken(), Command = original.Command, DocumentKey = original.DocumentKey,
                    PlanHash = original.PlanHash, IdentityDigest = original.IdentityDigest,
                    ElementFingerprint = original.ElementFingerprint, RehearsedPlan = original.RehearsedPlan,
                    RehearsedRows = original.RehearsedRows,
                    IssuedUtc = _now(), ExpiresUtc = ttl.HasValue ? _now().Add(ttl.Value) : DateTime.MaxValue
                };
                _issued[continuation.Token] = continuation;
                continuationToken = continuation.Token;
            }
            return check;
        }

        /// <summary>
        /// The same check, plus the one the old code said it could not make: whether the
        /// MODEL still looks the way the dry run found it.
        ///
        /// `elementFingerprint` is what ResolvedPlan.Fingerprint() returns for the plan
        /// recomputed NOW. Pass null from a command that does not materialise its plan yet -
        /// the check then behaves exactly as before and the reply says which guarantee was
        /// not made, because a guarantee nobody mentions reads like one that held.
        /// </summary>
        public ConfirmationCheck Validate(string token, string command, string documentKey, string planHash,
                                          string elementFingerprint, string driftDescription) =>
            ValidateCore(token, command, documentKey, planHash, elementFingerprint, driftDescription, null);

        /// <summary>
        /// The same check, handed the plan RECOMPUTED NOW as an object. The fingerprint
        /// still decides staleness; the object is only consulted AFTER the mismatch, to
        /// name what moved against the rehearsed plan this store kept with the token -
        /// so every token-gated command gets a named drift without persisting its own
        /// rehearsal across two MCP calls.
        /// </summary>
        public ConfirmationCheck Validate(string token, string command, string documentKey, string planHash,
                                          ResolvedPlan atApply, string driftDescription) =>
            ValidateCore(token, command, documentKey, planHash, atApply?.Fingerprint(), driftDescription, atApply);

        internal CommandResult Require(string token, string command, string documentKey, string planHash,
            ResolvedPlan atApply, ResolvedPlan rehearsed)
        {
            ConfirmationCheck check = Validate(token, command, documentKey, planHash, atApply,
                atApply != null && rehearsed != null ? ResolvedPlan.DescribeDrift(rehearsed, atApply) : null);
            return check.Ok ? null : CommandResult.FailWithDetail(check.Message + " (Nothing was changed.)", new JObject
            {
                ["error_code"] = string.IsNullOrWhiteSpace(token)
                    ? ErrorCodes.ConfirmacionRequerida : ErrorCodes.TokenInvalidoOVencido,
                ["state"] = check.State == ConfirmationState.StalePlan ? "stale_plan" : "refused",
                ["confirmation_state"] = check.State.ToString()
            });
        }

        internal Confirmation IssueForPlan(string command, string documentKey, string planHash, TimeSpan? ttl, ResolvedPlan resolved) =>
            Issue(command, documentKey, planHash, ttl, resolved?.Fingerprint(), resolved);

        // Resolving the rehearsal's observed effects is a read. Require still validates
        // and spends the approval against the freshly resolved plan before a write.
        internal ResolvedPlan ReadRehearsedPlan(string token, string command, string documentKey, string planHash)
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(token) || !_issued.TryGetValue(token, out Confirmation c) ||
                    c.Command != command || c.DocumentKey != documentKey || c.PlanHash != planHash ||
                    c.IdentityDigest != IdentityBinding.Digest || c.Used || c.ExpiresUtc < _now()) return null;
                return c.RehearsedPlan;
            }
        }

        private ConfirmationCheck ValidateCore(string token, string command, string documentKey, string planHash,
                                               string elementFingerprint, string driftDescription,
                                               ResolvedPlan atApply, DateTime? asOfUtc = null, bool spend = true)
        {
            DateTime now = _now();
            DateTime at = asOfUtc.HasValue && asOfUtc.Value < now ? asOfUtc.Value : now;
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

                if (at > c.ExpiresUtc)
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

                if (!string.Equals(c.IdentityDigest, IdentityBinding.Digest, StringComparison.Ordinal))
                    return Fail(ConfirmationState.PlanChanged,
                        "THIS REQUEST IS NOT THE ONE THAT WAS REHEARSED: " + (c.IdentityDigest == null
                            ? "the rehearsal carried no element identity (unique_id / expected_category / expected_type / identity) and this call does. "
                            : IdentityBinding.Digest == null
                                ? "the rehearsal carried an element identity and this call omits it. "
                                : "the element identity of this call is not the one of the rehearsal. ") +
                        "The token is bound to the identity that was checked. Nothing was changed. Re-run the dry run with the identity you intend to apply, and approve THAT one.");

                // THE MODEL, not the request. Everything above compared the question; this
                // compares the answer. Only when both sides materialised a plan: a token
                // issued before this existed carries null, and inventing a comparison there
                // would report a check that never ran.
                if (c.ElementFingerprint != null && elementFingerprint != null &&
                    !string.Equals(c.ElementFingerprint, elementFingerprint, StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(driftDescription) && c.RehearsedPlan != null && atApply != null)
                        driftDescription = ResolvedPlan.DescribeDrift(c.RehearsedPlan, atApply);
                    return Fail(ConfirmationState.StalePlan,
                        "THE MODEL MOVED AFTER THE DRY RUN. The request is identical and the document is the " +
                        "same one, but the elements this resolves to - or the values read off them - are not " +
                        "what was rehearsed. " +
                        (string.IsNullOrWhiteSpace(driftDescription) ? "" : "What changed: " + driftDescription + " ") +
                        "Nothing was changed. This is the case a request hash cannot see: the question did not " +
                        "change, the answer did. Re-run the dry run, read the CURRENT plan, and approve that one.");
                }

                if (spend) c.Used = true;
                return new ConfirmationCheck
                {
                    State = ConfirmationState.Valid,
                    RehearsedRows = (JArray)c.RehearsedRows?.DeepClone(),
                    // Said out loud when the token predates materialised plans or the command
                    // has not been taught to build one: the caller is entitled to know that
                    // "approved" covered the request and not the element set.
                    Message = (c.ElementFingerprint == null || elementFingerprint == null)
                        ? "Approved against the REQUEST only: this command does not materialise its resolved " +
                          "element set yet, so a model that moved between the dry run and now would not have " +
                          "been detected."
                        : null
                };
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
            // THE IDENTITY COUNTS AS HASH, not as presentation. `identity` / unique_id / expected_* are not scope fields of any tool
            // schema (the guard consumes them before the command parses), but they change what the caller asked to be protected
            // against: a request that adds, drops or changes them is a different request. Their digest was taken by the guard
            // BEFORE its commit erased them (ElementIdentityGuard / IdentityBinding), and joins every plan hash here.
            if (IdentityBinding.Digest != null) parts.Add("identity_digest=" + IdentityBinding.Digest);
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
