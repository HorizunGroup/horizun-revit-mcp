// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// Finds the Revit add-in's pipe from the discovery file and sends it one request.
// One connection per call: connect, write the JSON line, read the JSON reply.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal sealed class Discovered
    {
        public string PipeName;
        public string Token;
        public string Year;

        /// <summary>The Revit process that published this file, and whether it is still alive.</summary>
        public int Pid;
        public bool ProcessAlive;

        public string SourceFile;

        /// <summary>The add-in's own version, or null when the file predates schema 2.</summary>
        public string AddinVersion;

        /// <summary>
        /// The commands that add-in actually has, or NULL when the file does not say.
        /// Null is not "none": a schema-1 file was written by an add-in that never
        /// published a list, and reporting that as an empty capability set would turn
        /// "I do not know" into "it cannot do anything" - the exact substitution this
        /// codebase refuses to make anywhere else.
        /// </summary>
        public List<string> Commands;

        public int Schema;

        /// <summary>Distinguishes two runs that were assigned the same pid. Null before schema 3.</summary>
        public string InstanceId;

        /// <summary>When that Revit published its bridge. Null before schema 3.</summary>
        public string StartedUtc;

        /// <summary>
        /// What that add-in believes every command takes. Null before schema 3, and null
        /// is UNKNOWN - an old add-in that never published one is not an add-in that
        /// disagrees, so it is not refused for it.
        /// </summary>
        public string ContractHash;

        /// <summary>The wire protocol it speaks. 0 when it did not say.</summary>
        public int ProtocolVersion;

        /// <summary>true / false / null (unknown - the add-in did not publish a list).</summary>
        public bool? Supports(string command)
        {
            if (Commands == null) return null;
            foreach (string c in Commands)
                if (string.Equals(c, command, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal static class PipeClient
    {
        private static TargetSelection _target = TargetSelection.Automatic;

        /// <summary>
        /// The Revit chosen for this session by horizun_target. Sticky on purpose: picking
        /// a target is a decision, and a decision that silently expires after one call is
        /// worse than not offering it.
        ///
        /// ONE reference, read and written whole. This was two fields - a year and a pid -
        /// written one after the other and read one after the other from a different
        /// thread, so a call in flight could see the pid of the new target beside the year
        /// of the old one and route to an instance nobody had chosen. Immutable value,
        /// single assignment, no window. See TargetSelection.cs.
        ///
        /// A caller must take ONE snapshot and use it throughout: reading this property
        /// twice can straddle a change just as the two fields did.
        /// </summary>
        public static TargetSelection Target
        {
            get { return System.Threading.Volatile.Read(ref _target); }
            set { System.Threading.Volatile.Write(ref _target, value ?? TargetSelection.Automatic); }
        }

        /// <summary>
        /// Where the discovery files live. Overridable ONLY so the multi-instance rules can
        /// be proved against a temp directory - two Revit 2026 running at once is exactly
        /// the case that must not be tested by starting two Revits and hoping.
        /// Null means the real location.
        /// </summary>
        internal static string DirectoryOverride { get; set; }

        /// <summary>
        /// How liveness is decided. Overridable for the same reason: a test needs to say
        /// "this instance is running and that one is not" without owning a Revit.
        /// </summary>
        internal static Func<int, bool> LivenessProbe { get; set; }

        private static string DiscoveryDir()
            => DirectoryOverride ?? Horizun.Revit.Core.HorizunPaths.DiscoveryDir();

        /// <summary>
        /// Read the discovery file. If a year is given, use revit-&lt;year&gt;.json; otherwise
        /// prefer the most recently written one whose Revit is STILL RUNNING.
        ///
        /// The liveness check matters: Revit does not always get to run its shutdown
        /// handler (a crash, or a close that does not reach OnShutdown), so a file can
        /// outlive the process that wrote it. Connecting to that pipe costs a five second
        /// timeout and returns an error about pipes, when the real answer is "that Revit
        /// is gone". The pid is in the file; reading it turns a confusing wait into a
        /// sentence the user can act on.
        /// </summary>
        public static Discovered Discover(string year) => Resolve(year, null, out _);

        /// <summary>
        /// Pick the Revit to talk to, and REFUSE rather than guess when more than one fits.
        ///
        /// Two instances of the same Revit year is not a hypothetical - opening a file
        /// starts a second one, and it happened twice in one afternoon here. Choosing the
        /// most recent of them silently is how a write lands in the wrong session, so an
        /// ambiguous year is an error that names the candidates and asks for a pid.
        /// </summary>
        public static Discovered Resolve(string year, int? pid, out string refusal)
        {
            refusal = null;
            List<Discovered> all = ListAll();
            if (all.Count == 0) return null;

            // A pid names exactly one instance. Nothing to weigh.
            if (pid.HasValue)
            {
                foreach (Discovered d in all)
                    if (d.Pid == pid.Value) return d;
                refusal = "No Revit with process id " + pid.Value + " has published a bridge. Call horizun_target " +
                          "with no arguments to see the ones that have.";
                return null;
            }

            var candidates = new List<Discovered>();
            foreach (Discovered d in all)
                if (string.IsNullOrEmpty(year) || string.Equals(d.Year, year, StringComparison.Ordinal))
                    candidates.Add(d);

            if (candidates.Count == 0) return null;

            // A live Revit always beats a stale file, whatever the timestamps say.
            var live = candidates.FindAll(c => c.ProcessAlive);
            if (live.Count == 1) return live[0];

            if (live.Count > 1)
            {
                var names = new List<string>();
                foreach (Discovered c in live) names.Add("Revit " + c.Year + " pid " + c.Pid);
                refusal = live.Count + " Revit instances are running with the bridge loaded (" +
                          string.Join(", ", names) + ") and nothing says which one you mean. Refusing to pick: a " +
                          "command sent to the wrong session is a correct edit to the wrong model. Call " +
                          "horizun_target with a pid to choose one" +
                          (string.IsNullOrEmpty(year) ? ", or with a year if only one instance of it is running." : ".");
                return null;
            }

            // Nothing alive. Return the newest stale file so the caller gets the "that Revit
            // is gone" message rather than a bare "nothing found".
            Discovered best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (Discovered c in candidates)
            {
                DateTime t;
                try { t = File.GetLastWriteTimeUtc(c.SourceFile); } catch { t = DateTime.MinValue; }
                if (best == null || t > bestTime) { best = c; bestTime = t; }
            }
            return best;
        }

        /// <summary>Every Revit that has published a discovery file, newest first.</summary>
        public static List<Discovered> ListAll()
        {
            var found = new List<Discovered>();
            string dir = DiscoveryDir();
            if (!Directory.Exists(dir)) return found;

            var times = new Dictionary<string, DateTime>();
            foreach (string p in Directory.GetFiles(dir, "revit-*.json"))
            {
                Discovered d = Read(p);
                if (d == null) continue;
                times[p] = File.GetLastWriteTimeUtc(p);
                found.Add(d);
            }
            found.Sort((a, b) => times[b.SourceFile].CompareTo(times[a.SourceFile]));
            return found;
        }

        private static Discovered Read(string file)
        {
            try
            {
                JObject o = JObject.Parse(File.ReadAllText(file));
                int pid = o["pid"] != null ? (int)o["pid"] : 0;

                List<string> commands = null;
                if (o["commands"] is JArray arr)
                {
                    commands = new List<string>();
                    foreach (JToken t in arr)
                    {
                        var s = t.Type == JTokenType.String ? (string)t : null;
                        if (!string.IsNullOrEmpty(s)) commands.Add(s);
                    }
                }

                return new Discovered
                {
                    PipeName = (string)o["pipe_name"],
                    Token = (string)o["auth_token"],
                    Year = (string)o["revit_year"],
                    Pid = pid,
                    ProcessAlive = IsRevitAlive(pid),
                    SourceFile = file,
                    AddinVersion = (string)o["addin_version"],
                    Commands = commands,
                    Schema = o["schema"] != null ? (int)o["schema"] : 0,
                    InstanceId = (string)o["instance_id"],
                    StartedUtc = (string)o["started_utc"],
                    ContractHash = (string)o["contract_hash"],
                    ProtocolVersion = o["protocol_version"] != null ? (int)o["protocol_version"] : 0
                };
            }
            catch { return null; }
        }

        /// <summary>
        /// Is that pid still a running Revit? The name is checked too: pids are recycled,
        /// and "some process has this number" is not evidence that Revit is up.
        ///
        /// Internal because job_status asks the same question about the pid a job record
        /// carries: two liveness checks with two sets of rules would disagree eventually,
        /// and both are steered by the same LivenessProbe in tests.
        /// </summary>
        internal static bool IsRevitAlive(int pid)
        {
            Func<int, bool> probe = LivenessProbe;
            if (probe != null) return probe(pid);

            if (pid <= 0) return false;
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                    return p.ProcessName.IndexOf("revit", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Send one command to the plugin and return its reply envelope.
        ///
        /// The token stops US waiting. It does NOT stop Revit: the Revit API has no way
        /// to interrupt a command already running on the UI thread, so a cancelled call
        /// abandons the reply and the work carries on inside Revit to completion. That is
        /// stated here and in the message the caller gets, because "cancelled" that
        /// silently means "still running, result discarded" is worse than no cancellation
        /// at all - it invites a retry on top of work that is still in progress.
        ///
        /// Before this, the token was accepted by the layers above and never reached
        /// here: connect, write and read all blocked to their own timeouts regardless,
        /// so a cancel notification changed nothing about when this returned.
        /// </summary>
        public static JObject Send(Discovered d, string command, JObject args, int timeoutMs,
                                   CancellationToken ct = default(CancellationToken))
        {
            ct.ThrowIfCancellationRequested();

            using (var pipe = new NamedPipeClientStream(".", d.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                // Connect in slices so a cancel between them is noticed. ConnectAsync
                // exists on newer targets only, and this file is shared, so the wait is
                // chunked rather than made async.
                const int connectBudgetMs = 5000;
                const int sliceMs = 250;
                var connectClock = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    try { pipe.Connect(sliceMs); break; }
                    catch (TimeoutException)
                    {
                        if (connectClock.ElapsedMilliseconds >= connectBudgetMs)
                            throw new TimeoutException(
                                "Could not connect to Revit's bridge within " + connectBudgetMs + " ms. Nothing was sent.");
                    }
                }

                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

                var req = new JObject
                {
                    ["id"] = Guid.NewGuid().ToString("N").Substring(0, 8),
                    ["command"] = command,
                    ["params"] = args ?? new JObject(),
                    ["token"] = d.Token
                };

                // Last check before the write. Past this point the command IS in Revit's
                // hands and cancelling can only stop us listening for the answer.
                ct.ThrowIfCancellationRequested();
                writer.WriteLine(req.ToString(Newtonsoft.Json.Formatting.None));

                // BOUNDED. This was StreamReader.ReadLineAsync(), which reads until it
                // finds a newline however far away that is - so the size of this process
                // was decided by whatever came back down the pipe. The add-in caps what it
                // sends, but the two halves ship separately and a server built today
                // routinely meets an add-in installed months ago, so neither end may be
                // the only gate. Same limit, from the shared contract, at both ends.
                var reader = new BoundedLineReader(pipe, Horizun.Contracts.Contract.MaxReplyBytes);
                var task = System.Threading.Tasks.Task.Run(() => reader.ReadLine());
                int waited = WaitHandle.WaitAny(
                    new[] { ((IAsyncResult)task).AsyncWaitHandle, ct.WaitHandle }, timeoutMs);

                if (waited == WaitHandle.WaitTimeout)
                    throw new TimeoutException($"No reply from Revit within {timeoutMs} ms.");

                if (waited == 1)
                    throw new OperationCanceledException(
                        "Cancelled while waiting for Revit to answer '" + command + "'. This stopped US waiting: " +
                        "the Revit API cannot interrupt a command already running on its UI thread, so that work " +
                        "CONTINUES inside Revit and finishes unseen. Do not re-send it assuming nothing happened.",
                        ct);

                BoundedLine reply = task.Result;
                if (reply.Outcome == BoundedLineOutcome.TooLong)
                    throw new IOException(
                        "Revit's reply to '" + command + "' was " + reply.Bytes + " bytes, over the " +
                        Horizun.Contracts.Contract.MaxReplyBytes + " byte limit, so it was NOT read into memory and " +
                        "cannot be returned. THE COMMAND ITSELF RAN: whatever it was going to do inside Revit, it " +
                        "did - only the answer is lost. Ask for less of the model at a time (a narrower category, " +
                        "a smaller id list, or a view) rather than re-running this unchanged.");
                if (reply.Outcome == BoundedLineOutcome.Failed)
                    throw new IOException(reply.Error);
                if (reply.Outcome == BoundedLineOutcome.EndOfStream)
                    throw new IOException("Pipe closed before a reply arrived" +
                                          (reply.Bytes > 0 ? " (" + reply.Bytes + " bytes of one had been sent)." : "."));
                return JObject.Parse(reply.Line);
            }
        }
    }
}
