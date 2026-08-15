// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// WHO ELSE is talking to this Revit (story 5.16).
//
// Measured on 2026-08-05: two agents on one machine, and Revit 2025 died three
// times - journal ends mid-activity, no exception, killed from outside - while
// a second agent recompiled and redeployed the add-in underneath the first.
// Neither agent could see the other. The bridge COULD have: every request
// arrives over the named pipe, and Windows will name the pid on the other end
// of a pipe connection. Three journal autopsies would have been one line in
// horizun_health.
//
// This is that line's bookkeeping: a registry of client pids and when each one
// last sent a request. The transport records; health reads. Two honest limits,
// stated rather than smoothed over:
//
//   * The transport is ONE CONNECTION PER REQUEST, so there is no such thing as
//     a "currently attached" client - "connected" here means "sent at least one
//     request within the window". That is the approximation the field report
//     asked for by name ("even an approximate count").
//   * The caller of health is itself a client, and its own connection is
//     recorded when its request arrives - so "other clients" is simply the
//     distinct count minus one, without the UI thread ever needing to know
//     which pid is asking. A pid the transport could not read is counted as
//     unidentified, never silently dropped: it makes the count a lower bound
//     and the snapshot says how many such connections there were.
//
// Revit-free: the facts (a pipe handle, a pid) are the transport's; the window
// arithmetic, the pruning and the minus-one rule are provable here.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;

namespace Horizun.Revit.Core
{
    public sealed class ClientPresence
    {
        /// <summary>The one instance the transport writes and health reads.</summary>
        public static readonly ClientPresence Default = new ClientPresence();

        /// <summary>
        /// How long a client stays "connected" after its last request. Ten minutes:
        /// long enough that an agent between commands still shows, short enough that
        /// yesterday's session does not haunt today's count.
        /// </summary>
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        private readonly object _lock = new object();
        private readonly Dictionary<long, DateTime> _lastSeenUtc = new Dictionary<long, DateTime>();
        private readonly List<DateTime> _unidentifiedUtc = new List<DateTime>();

        /// <summary>A request arrived from this pid. Called by the transport, per connection.</summary>
        public void Seen(long pid, DateTime nowUtc)
        {
            lock (_lock)
            {
                _lastSeenUtc[pid] = nowUtc;
                PruneLocked(nowUtc);
            }
        }

        /// <summary>
        /// A request arrived whose pid could not be read. Counted, never dropped:
        /// an unreadable pid makes the distinct count a lower bound, and only a
        /// recorded number can say so.
        /// </summary>
        public void SeenUnidentified(DateTime nowUtc)
        {
            lock (_lock)
            {
                _unidentifiedUtc.Add(nowUtc);
                PruneLocked(nowUtc);
            }
        }

        public PresenceSnapshot Take(DateTime nowUtc)
        {
            lock (_lock)
            {
                PruneLocked(nowUtc);
                return new PresenceSnapshot(
                    _lastSeenUtc.OrderByDescending(kv => kv.Value)
                                .Select(kv => new ClientSeen(kv.Key, kv.Value))
                                .ToList(),
                    _unidentifiedUtc.Count);
            }
        }

        private void PruneLocked(DateTime nowUtc)
        {
            DateTime cutoff = nowUtc - Window;
            var stale = _lastSeenUtc.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
            foreach (long pid in stale) _lastSeenUtc.Remove(pid);
            _unidentifiedUtc.RemoveAll(t => t < cutoff);
        }
    }

    public sealed class ClientSeen
    {
        public long Pid { get; }
        public DateTime LastSeenUtc { get; }

        public ClientSeen(long pid, DateTime lastSeenUtc)
        {
            Pid = pid;
            LastSeenUtc = lastSeenUtc;
        }
    }

    public sealed class PresenceSnapshot
    {
        /// <summary>Distinct clients in the window, most recently seen first.</summary>
        public IReadOnlyList<ClientSeen> Clients { get; }

        /// <summary>Connections in the window whose pid could not be read.</summary>
        public int UnidentifiedInWindow { get; }

        public PresenceSnapshot(IReadOnlyList<ClientSeen> clients, int unidentifiedInWindow)
        {
            Clients = clients;
            UnidentifiedInWindow = unidentifiedInWindow;
        }

        /// <summary>
        /// The headline number: distinct clients minus the caller. The caller's own
        /// connection is in the registry by the time any command runs (the transport
        /// records before dispatch), so no caller identity is needed - and the floor
        /// at zero covers the one path that arrives without a recorded connection,
        /// which must read as "no others seen", never as -1.
        /// </summary>
        public int OtherThanCaller
        {
            get { return Math.Max(0, Clients.Count - 1); }
        }

        /// <summary>
        /// The sentence that keeps an unreadable pid from vanishing: appended to the
        /// health note whenever any connection in the window went unidentified, so a
        /// lower-bound count never reads like an exact one. Empty when exact.
        /// </summary>
        public string UnidentifiedNote()
        {
            return UnidentifiedInWindow > 0
                ? " " + UnidentifiedInWindow + " connection(s) in the window had an unreadable client pid, so " +
                  "the distinct count is a LOWER BOUND - one of those could be another client."
                : "";
        }
    }
}
