// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code. Revit-free.
//
// horizun_request_python_access WITHOUT HOLDING REVIT.
//
// Field report: the call waited up to 600 s for the person to answer a modal dialog,
// and for all that time Revit's UI thread belonged to the bridge - every other call
// queued behind it, and an unattended client timed out with Revit still blocked. The
// request now opens a NON-MODAL window in Revit and returns at once with
// state=pending_approval and a request_id; the decision is read later with
// operation=status or from horizun_health.python_runtime.access_request.
//
// What does NOT change: only the person at Revit can approve. This ledger records the
// question and the window's answer; nothing an MCP call can send moves a request to
// granted. Decide is called by the window's own buttons (Revit side), never by a
// command, and a grant is still only what Settings.IsToolAllowed says afterwards.
//
// One request open at a time: a second request while one is pending returns the same
// id instead of stacking windows on the person. A pending request expires (the window
// closes itself) so an abandoned question does not stay on screen forever.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class PythonAccessRequest
    {
        public string Id;
        public DateTime CreatedUtc;
        public DateTime ExpiresUtc;
        public string Reason;
        public string State;          // pending | granted | declined | failed | expired
        public DateTime? DecidedUtc;
        public string Detail;
    }

    public static class PythonAccessRequests
    {
        public const string Pending = "pending";
        public const string Granted = "granted";
        public const string Declined = "declined";
        public const string Failed = "failed";
        public const string Expired = "expired";

        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
        private const int Kept = 20;

        /// <summary>Replaceable for tests only.</summary>
        public static Func<DateTime> Clock = () => DateTime.UtcNow;

        private static readonly object Gate = new object();
        private static readonly List<PythonAccessRequest> Requests = new List<PythonAccessRequest>();

        /// <summary>
        /// Opens a request, or returns the one already pending (reused=true): one window
        /// at a time. The caller shows the window only when reused is false.
        /// </summary>
        public static PythonAccessRequest Open(string reason, out bool reused)
        {
            lock (Gate)
            {
                ExpireLocked();
                PythonAccessRequest pending = Requests.FindLast(r => r.State == Pending);
                if (pending != null) { reused = true; return Copy(pending); }
                DateTime now = Clock();
                var created = new PythonAccessRequest
                {
                    Id = "pyreq-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    CreatedUtc = now, ExpiresUtc = now + Lifetime, Reason = reason, State = Pending
                };
                Requests.Add(created);
                if (Requests.Count > Kept) Requests.RemoveRange(0, Requests.Count - Kept);
                reused = false;
                return Copy(created);
            }
        }

        /// <summary>The request with this id (expiry applied), or null when unknown.</summary>
        public static PythonAccessRequest Get(string id)
        {
            lock (Gate)
            {
                ExpireLocked();
                PythonAccessRequest r = Requests.Find(x => string.Equals(x.Id, id, StringComparison.Ordinal));
                return r == null ? null : Copy(r);
            }
        }

        /// <summary>The most recent request, or null when none was made in this Revit session.</summary>
        public static PythonAccessRequest Latest()
        {
            lock (Gate)
            {
                ExpireLocked();
                return Requests.Count == 0 ? null : Copy(Requests[Requests.Count - 1]);
            }
        }

        /// <summary>
        /// Records the window's answer. Only a PENDING request moves, and only to a final
        /// state; a decided or expired request is never reopened or changed. Returns false
        /// when nothing moved.
        /// </summary>
        public static bool Decide(string id, string state, string detail = null)
        {
            if (state != Granted && state != Declined && state != Failed && state != Expired) return false;
            lock (Gate)
            {
                ExpireLocked();
                PythonAccessRequest r = Requests.Find(x => string.Equals(x.Id, id, StringComparison.Ordinal));
                if (r == null || r.State != Pending) return false;
                r.State = state; r.DecidedUtc = Clock(); r.Detail = detail;
                return true;
            }
        }

        public static JObject ToJson(PythonAccessRequest r)
        {
            if (r == null) return null;
            return new JObject
            {
                ["request_id"] = r.Id,
                ["state"] = r.State,
                ["created_utc"] = Iso(r.CreatedUtc),
                ["expires_utc"] = r.State == Pending ? Iso(r.ExpiresUtc) : null,
                ["decided_utc"] = r.DecidedUtc.HasValue ? Iso(r.DecidedUtc.Value) : null,
                ["detail"] = r.Detail
            };
        }

        /// <summary>For horizun_health.python_runtime: the latest request, or null.</summary>
        public static JObject Snapshot() => ToJson(Latest());

        public static void ResetForTests() { lock (Gate) Requests.Clear(); Clock = () => DateTime.UtcNow; }

        private static void ExpireLocked()
        {
            DateTime now = Clock();
            foreach (PythonAccessRequest r in Requests)
                if (r.State == Pending && now >= r.ExpiresUtc)
                {
                    r.State = Expired; r.DecidedUtc = r.ExpiresUtc;
                    r.Detail = "Nobody answered in Revit within " + (int)Lifetime.TotalMinutes + " minutes; the window closed.";
                }
        }

        private static PythonAccessRequest Copy(PythonAccessRequest r) => new PythonAccessRequest
        {
            Id = r.Id, CreatedUtc = r.CreatedUtc, ExpiresUtc = r.ExpiresUtc, Reason = r.Reason,
            State = r.State, DecidedUtc = r.DecidedUtc, Detail = r.Detail
        };

        private static string Iso(DateTime t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    }

    /// <summary>The reply of horizun_request_python_access, from the request and the setting as it is NOW.</summary>
    public static class PythonAccessStatus
    {
        /// <summary>
        /// granted is the setting re-read now (Settings.IsToolAllowed), never the request's
        /// state alone: a grant later revoked by the owner or overridden by policy is not ON.
        /// </summary>
        public static JObject Reply(JObject request, bool allowedNow)
        {
            string state = request?.Value<string>("state");
            string id = request?.Value<string>("request_id");
            string decision =
                request == null ? (allowedNow ? "already_enabled" : "no_request")
              : state == PythonAccessRequests.Pending ? (allowedNow ? "already_enabled" : "pending_approval")
              : state == PythonAccessRequests.Granted ? (allowedNow ? "enabled_by_owner" : "granted_then_revoked")
              : state == PythonAccessRequests.Declined ? (allowedNow ? "already_enabled" : "rejected_or_cancelled_by_owner")
              : state == PythonAccessRequests.Expired ? (allowedNow ? "already_enabled" : "expired_unanswered")
              : allowedNow ? "already_enabled" : "failed";
            string means =
                decision == "pending_approval"
                    ? "A window is open in Revit and Revit keeps working. Only the person there can enable Python. Poll with " +
                      "operation=status (every 10-30 s is plenty); do not ask again while it is pending."
              : decision == "enabled_by_owner" ? "The person in Revit enabled Python. It remains ON until that Windows user disables it."
              : decision == "already_enabled" ? "Python is ON. No consent window was needed."
              : decision == "rejected_or_cancelled_by_owner"
                    ? "The person in Revit did not grant permission. Python remains OFF; do not retry or bypass their choice."
              : decision == "expired_unanswered" ? "Nobody answered in Revit. Python remains OFF. Ask the person before requesting again."
              : decision == "granted_then_revoked"
                    ? "The person enabled Python and it is OFF again now (revoked, or overridden by a machine policy). Python remains OFF."
              : decision == "no_request" ? "No request was made in this Revit session. Python is OFF."
              : "The permission could not be written: " + (request?.Value<string>("detail") ?? "unknown") + " Python remains OFF.";
            return new JObject
            {
                ["granted"] = allowedNow,
                ["decision"] = decision,
                ["state"] = decision == "pending_approval" ? "pending_approval" : state ?? (allowedNow ? "granted" : "none"),
                ["request_id"] = id,
                ["request"] = request,
                ["persistent"] = allowedNow,
                ["expires_at"] = JValue.CreateNull(),
                ["blocks_revit"] = false,
                ["poll"] = id == null || decision != "pending_approval" ? null : new JObject
                {
                    ["tool"] = "horizun_request_python_access",
                    ["arguments"] = new JObject { ["operation"] = "status", ["request_id"] = id },
                    ["also"] = "horizun_health python_runtime.access_request"
                },
                ["what_this_means"] = means
            };
        }
    }
}
