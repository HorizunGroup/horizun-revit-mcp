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
using System.Diagnostics;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;

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
        private readonly Dictionary<long, ClientProcessIdentity> _identities = new Dictionary<long, ClientProcessIdentity>();
        private readonly List<DateTime> _unidentifiedUtc = new List<DateTime>();

        /// <summary>A request arrived from this pid. Called by the transport, per connection.</summary>
        public void Seen(long pid, DateTime nowUtc)
        {
            var identity = ClientProcessIdentity.Capture(pid);
            lock (_lock)
            {
                _lastSeenUtc[pid] = nowUtc;
                _identities[pid] = identity;
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
                                .Select(kv => new ClientSeen(kv.Key, kv.Value, _identities[kv.Key]))
                                .ToList(),
                    _unidentifiedUtc.Count);
            }
        }

        private void PruneLocked(DateTime nowUtc)
        {
            DateTime cutoff = nowUtc - Window;
            var stale = _lastSeenUtc.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
            foreach (long pid in stale) { _lastSeenUtc.Remove(pid); _identities.Remove(pid); }
            _unidentifiedUtc.RemoveAll(t => t < cutoff);
        }
    }

    public sealed class ClientSeen
    {
        public long Pid { get; }
        public DateTime LastSeenUtc { get; }
        public ClientProcessIdentity Identity { get; }

        public ClientSeen(long pid, DateTime lastSeenUtc, ClientProcessIdentity identity = null)
        {
            Pid = pid;
            LastSeenUtc = lastSeenUtc;
            Identity = identity;
        }
    }

    /// <summary>Best-effort process facts; null means unknown, never dead by assumption.</summary>
    public sealed class ClientProcessIdentity
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicInfo { public IntPtr A, B, C, D, ProcessId, ParentId; }
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr handle, int kind, out BasicInfo info, int size, out int returned);

        public string ExecutableName { get; private set; }
        public string ExecutablePath { get; private set; }
        public DateTime? StartedUtc { get; private set; }
        public int? ParentPid { get; private set; }
        public DateTime? ParentStartedUtc { get; private set; }
        private bool _parentMissingAtCapture;

        public static ClientProcessIdentity Capture(long pid)
        {
            var result = new ClientProcessIdentity();
            try
            {
                using (var process = Process.GetProcessById(checked((int)pid)))
                {
                    try { result.ExecutableName = process.ProcessName; } catch { }
                    try { result.ExecutablePath = process.MainModule.FileName; } catch { }
                    try { result.StartedUtc = process.StartTime.ToUniversalTime(); } catch { }
                    try
                    {
                        BasicInfo info; int returned;
                        if (NtQueryInformationProcess(process.Handle, 0, out info, Marshal.SizeOf(typeof(BasicInfo)), out returned) == 0)
                        {
                            result.ParentPid = checked((int)info.ParentId.ToInt64());
                            try
                            {
                                using (var parent = Process.GetProcessById(result.ParentPid.Value))
                                    result.ParentStartedUtc = parent.StartTime.ToUniversalTime();
                                if (result.StartedUtc.HasValue && result.ParentStartedUtc > result.StartedUtc)
                                    result._parentMissingAtCapture = true;
                            }
                            catch (ArgumentException) { result._parentMissingAtCapture = true; }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        public bool? ParentAlive()
        {
            if (_parentMissingAtCapture) return false;
            if (!ParentPid.HasValue || !ParentStartedUtc.HasValue) return null;
            try
            {
                using (var parent = Process.GetProcessById(ParentPid.Value))
                    return !parent.HasExited && parent.StartTime.ToUniversalTime() == ParentStartedUtc.Value;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch { return null; }
        }

        public static bool? IsOrphan(bool? parentAlive)
            => parentAlive.HasValue ? (bool?)(!parentAlive.Value) : null;

        public static string OrphanReason(bool? parentAlive)
            => parentAlive == false ? "parent_exited" : parentAlive == true ? "parent_alive" : "parent_unknown";
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

namespace Horizun.Revit.Core
{
    /// <summary>
    /// A write while another MCP client is talking to the same Revit. Health already counts
    /// the others; a write is where it matters - whatever the other client does next (close
    /// the document, undo, write the same elements) lands on this change too. Field
    /// 2026-10-27. The caller is taken to be the most recent connection (the transport
    /// records it before dispatch); clients whose process is known to have exited are not
    /// counted, so short-lived callers (one process per call) do not raise it on themselves.
    /// </summary>
    public static class ConcurrentClientRule
    {
        public static List<long> OthersAlive(PresenceSnapshot snap, Func<long, bool?> alive)
        {
            var rows = new List<long>();
            if (snap == null || snap.Clients.Count < 2) return rows;
            foreach (ClientSeen c in snap.Clients.Skip(1))
                if ((alive == null ? (bool?)null : alive(c.Pid)) != false) rows.Add(c.Pid);
            return rows;
        }

        /// <summary>Attach parent liveness to the same structured rows used by the warning.</summary>
        public static void AddOrphanStates(JArray processes, PresenceSnapshot snapshot, Func<ClientSeen, bool?> parentAlive)
        {
            foreach (JObject process in processes)
            {
                long pid = (long)process["pid"];
                ClientSeen client = snapshot?.Clients.FirstOrDefault(c => c.Pid == pid);
                bool? alive = client == null || parentAlive == null ? null : parentAlive(client);
                process["orphan"] = alive.HasValue ? new JValue(!alive.Value) : JValue.CreateNull();
            }
        }

        /// <summary>
        /// A call the contract always rolls back writes nothing, so it is not "a write that landed":
        /// horizun_execute_python with read_only=true or rehearsal=true. Those idempotency-keyed
        /// calls used to carry the warning that their write "landed in a model they can also change".
        /// </summary>
        public static bool RolledBackByContract(string tool, JObject request)
            => tool == "horizun_execute_python" &&
               (request?["read_only"]?.Type == JTokenType.Boolean && (bool)request["read_only"] ||
                request?["rehearsal"]?.Type == JTokenType.Boolean && (bool)request["rehearsal"]);

        /// <summary>
        /// The line horizun_health shows (also in detail=compact, which leaves the clients block out) when a second
        /// MCP client is talking to THIS Revit: whatever it does next - closes the document, undoes, writes the same
        /// elements, recompiles the add-in - lands on this session too. Null when nobody else is alive in the window.
        /// </summary>
        public static string HealthWarning(IList<long> others)
            => others == null || others.Count == 0 ? null :
               "Another MCP client is connected to this same Revit: " + others.Count + " other client process(es) (pid " +
               string.Join(", ", others) + ") sent requests in the last " + (int)ClientPresence.Window.TotalMinutes +
               " minutes. Two agents on one Revit overwrite each other's work and can close the document under you: " +
               "check with whoever runs the other one, name target_document in every call, and do not run two agents on one Revit.";

        /// <summary>The warning for a snapshot, given how to ask whether a client pid is still alive (null = unknown, counted).</summary>
        public static string HealthWarning(PresenceSnapshot snap, Func<ClientSeen, bool?> alive)
        {
            var rows = new List<long>();
            if (snap == null || snap.Clients.Count < 2) return null;
            foreach (ClientSeen c in snap.Clients.Skip(1))
                if ((alive == null ? (bool?)null : alive(c)) != false) rows.Add(c.Pid);
            return HealthWarning(rows);
        }
        public static string Warning(IList<long> others)
            => others == null || others.Count == 0 ? null :
               others.Count + " other MCP client process(es) (pid " + string.Join(", ", others) + ") sent requests to this Revit in the last " +
               (int)ClientPresence.Window.TotalMinutes + " minutes. This write landed in a model they can also change: check with whoever runs them " +
               "before relying on it, and do not run two agents on one Revit.";
    }
}

namespace Horizun.Revit.Core
{
    /// <summary>
    /// A request marked as an instance PROBE is the MCP server looking at this Revit (health without a target,
    /// routing by document), not a client working with it. The add-in does not count it as presence: with two Revit
    /// open, every agent's server probes both, and counting that made every agent warn about every other one.
    /// The server writes the field as PipeClient.ProbeField; a test pins that the two names are the same.
    /// </summary>
    public static class ProbeFlag
    {
        public const string Field = "probe";

        public static bool IsProbe(Newtonsoft.Json.Linq.JObject request) =>
            request?[Field]?.Type == Newtonsoft.Json.Linq.JTokenType.Boolean && (bool)request[Field];
    }

    /// <summary>Whether a client process seen on the pipe is still THAT process, and still a live client.</summary>
    public static class ClientLiveness
    {
        /// <summary>Start times within this are the same process (the clock resolution of two reads).</summary>
        public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

        /// <summary>
        /// false: no such process; a process with that pid but another start time (a RECYCLED pid, which is not the client
        /// that connected); or an orphan (its parent exited: a server left running by a client that is gone).
        /// null: could not be told (counted, like an unknown pid is elsewhere). true: alive.
        /// </summary>
        public static bool? Alive(bool? processExists, DateTime? currentStartUtc, DateTime? recordedStartUtc, bool? parentAlive)
        {
            if (processExists == false) return false;
            if (processExists == null) return null;
            if (currentStartUtc.HasValue && recordedStartUtc.HasValue &&
                (currentStartUtc.Value - recordedStartUtc.Value).Duration() > StartTolerance) return false;
            if (parentAlive == false) return false;
            return true;
        }
    }
}
