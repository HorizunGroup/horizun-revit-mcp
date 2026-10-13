// -----------------------------------------------------------------------------
// Horizun Revit MCP — original Horizun code.
//
// Named-pipe transport. Listens on Horizun-<pid>, reads one JSON request per
// connection, runs it through the dispatcher (which crosses to the UI thread),
// and writes one JSON reply. Line-delimited UTF-8, no BOM.
//
// Request : { "id": "...", "command": "...", "params": {...}, "token": "..." }
// Reply   : { "id": "...", "success": true|false, "data": ..., "error": null|"...",
//             "revit_said": {...}|absent }
//
// revit_said carries the warnings, errors and modal dialogs Revit raised while the
// command ran. It is a sibling of data, not part of it, so no command has to
// remember to include it and a FAILED command still reports what Revit objected to.
//
// One connection = one request/reply. Connections are accepted CONCURRENTLY -
// each is served on its own thread and the listener goes straight back to
// waiting - because the alternative is that a caller cannot even ask whether
// Revit is alive while a long command has the UI thread. It could not: a
// serving loop that handled the request inline left every later client stuck in
// its connect timeout, with nothing to read but "the pipe did not answer".
//
// Accepting a connection is not permission to run in parallel: the dispatcher
// executes one command at a time and holds later connections in a bounded FIFO
// queue. Four slots beyond the maximum executing+waiting command count provide
// headroom for cancellation control traffic under ordinary queue saturation.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Transport
{
    public sealed class PipeServer
    {
        private readonly string _pipeName;
        private readonly Dispatcher _dispatcher;
        private readonly string _token;
        private readonly int _commandTimeoutMs;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>
        /// Whether a pipe server in this process is accepting connections.
        ///
        /// STATIC BECAUSE THERE IS ONE PER PROCESS, and because the thing that needs to ask
        /// - the operations pane - has no route to the instance. It exists so a panel can
        /// tell a bridge that stopped answering from one that is idle: the pane reads files,
        /// and files outlive the process that wrote them, so without this a dead bridge and
        /// a quiet one look identical and the history underneath looks current.
        /// </summary>
        public static bool IsListening { get; private set; }

        public string PipeName => _pipeName;

        public PipeServer(Dispatcher dispatcher, string token, int commandTimeoutMs = 600000)
        {
            _dispatcher = dispatcher;
            _token = token;
            _commandTimeoutMs = commandTimeoutMs;
            _pipeName = "Horizun-" + System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        public void Start()
        {
            _running = true;
            IsListening = true;
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "Horizun.PipeServer" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            IsListening = false;
            // A dummy connect unblocks WaitForConnection so the loop can exit.
            try
            {
                using (var c = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))
                    c.Connect(200);
            }
            catch { }
        }

        /// <summary>
        /// The listening pipe, restricted to THIS Windows user.
        ///
        /// Without an explicit ACL a named pipe takes the process token's default
        /// DACL, which is wider than it needs to be — on a shared workstation or a
        /// terminal server another logged-in user can reach it. The auth token in the
        /// discovery file already gates every request, but a token is one secret in
        /// one file: if it leaks, the ACL is what still stands between someone else's
        /// session and a command that edits a building model. Two locks, not one.
        /// </summary>
        private NamedPipeServerStream CreateServerStream()
        {
            var rules = new PipeSecurity();
            var me = WindowsIdentity.GetCurrent().User;
            rules.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));

            // Nothing else is granted: no Everyone, no Users, no NETWORK. A pipe is not
            // reachable across the network anyway, but saying so in the ACL costs nothing.
#if NET
            return NamedPipeServerStreamAcl.Create(
                _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, rules);
#else
            return new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, rules);
#endif
        }

        /// <summary>
        /// How many connections may be served at once. The dispatcher admits ONE command
        /// at a time, so anything above this is either a caller asking about a busy Revit
        /// or something wrong; either way it is a small number, and an unbounded thread
        /// per connection is how a stuck client becomes a stuck Revit.
        /// </summary>
        private const int MaxConcurrentConnections = RequestGate.MaxDepth + 5;

        /// <summary>
        /// A request is a command and some ids. The megabytes go the other way. Both
        /// numbers come from the shared contract so the server applies the same ones -
        /// a limit only one end enforces is a place where one process dies and the other
        /// cannot say why.
        /// </summary>
        private const int MaxRequestBytes = Horizun.Contracts.Contract.MaxRequestBytes;

        /// <summary>How big an answer may get before it is refused instead of sent.</summary>
        private const int MaxReplyBytes = Horizun.Contracts.Contract.MaxReplyBytes;

        /// <summary>
        /// How long a connected peer has to finish sending its request. A client that
        /// connects and then says nothing used to hold a thread until Revit closed.
        /// </summary>
        private const int RequestReadTimeoutMs = 30000;

        /// <summary>How long an over-limit connection has to send the request it is being refused for.</summary>
        private const int RefusalReadTimeoutMs = 2000;

        /// <summary>
        /// The ordinary slots, plus PipeAdmission.ControlReserve held back for control
        /// messages: with every ordinary slot taken by a queued command, the cancel and
        /// status controls that exist for exactly that moment were refused (audit
        /// 2026-10-08).
        /// </summary>
        private readonly PipeAdmission _admission = new PipeAdmission(MaxConcurrentConnections);

        private void AcceptLoop()
        {
            while (_running)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateServerStream();
                    server.WaitForConnection();
                    if (!_running) return;

                    // Take a slot BEFORE spawning anything. Refusing politely costs one
                    // sentence; an unbounded thread count costs the process.
                    PipeAdmission.Slot slot = _admission.TryAdmit();
                    if (slot == PipeAdmission.Slot.None)
                    {
                        // NEVER ON THIS THREAD. The pipe has no buffer: a refusal written
                        // here before the request is read blocks against the client's own
                        // write, and the listener stops accepting anyone (measured live
                        // 2026-10-08). The refusal is answered on a bounded thread that
                        // reads first; past that bound the connection is simply closed.
                        NamedPipeServerStream refused = server;
                        server = null;
                        if (!_admission.TryTakeRefusal()) { Close(refused); continue; }
                        new Thread(() =>
                        {
                            try
                            {
                                LineResult ignored = LineReader.Read(refused, MaxRequestBytes, RefusalReadTimeoutMs);
                                Refuse(refused, "Too many concurrent connections to this Revit (limit " +
                                                MaxConcurrentConnections + "). Nothing was run. This bridge serves one " +
                                                "command at a time, so a queue this deep means something is retrying.");
                            }
                            catch { }
                            finally { Close(refused); _admission.ReleaseRefusal(); }
                        })
                        { IsBackground = true, Name = "Horizun.PipeRefuse" }.Start();
                        continue;
                    }

                    // Ownership moves to the worker, which disposes it. The listener must
                    // not wait for the command to finish - that is the whole point.
                    NamedPipeServerStream connection = server;
                    server = null;
                    var worker = new Thread(() =>
                    {
                        try { HandleOne(connection, slot); }
                        catch { /* one bad connection is not everyone's problem */ }
                        finally { Close(connection); _admission.Release(slot); }
                    })
                    { IsBackground = true, Name = "Horizun.PipeConn" };
                    worker.Start();
                }
                catch (Exception)
                {
                    // A broken connection must not take the whole listener down.
                    if (!_running) return;
                    Thread.Sleep(50);
                }
                finally
                {
                    // Only reached with a non-null stream when we never handed it off.
                    if (server != null) Close(server);
                }
            }
        }

        private static void Close(NamedPipeServerStream s)
        {
            try { s.Dispose(); } catch { }
        }

        /// <summary>Tell a peer why it is being turned away, best effort.</summary>
        private static void Refuse(NamedPipeServerStream server, string why)
        {
            // Bounded: a peer still writing does not read this (backlog #37).
            LineWriter.TryWriteLine(server, PipeEnvelope.Of(null, false, null, why).ToString(Formatting.None),
                                    ErrorReplyWriteTimeoutMs);
        }

        /// <summary>
        /// How long a reply refusing a request that was never read whole may take to write
        /// (backlog #37). The peer may still be blocked writing that request - the pipe has
        /// no buffer - so it is not reading; past this the connection is closed, which fails
        /// its write instead of leaving both ends blocked writing at each other.
        /// </summary>
        private const int ErrorReplyWriteTimeoutMs = 5000;

        /// <summary>How long an ordinary reply may take to be read by the peer before the connection is closed.</summary>
        private const int ReplyWriteTimeoutMs = 60000;

        private void HandleOne(NamedPipeServerStream server, PipeAdmission.Slot slot)
        {
            // WHO is on the other end (5.16). Windows names the client pid of a pipe
            // connection; recorded BEFORE dispatch so that by the time any command
            // runs - health included - the asking client is already in the registry,
            // and "other clients" is the distinct count minus one. A pid that cannot
            // be read is counted as unidentified, never dropped. Best effort either
            // way: presence bookkeeping must never cost a request.
            try
            {
                uint clientPid;
                if (GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out clientPid))
                    ClientPresence.Default.Seen(clientPid, DateTime.UtcNow);
                else
                    ClientPresence.Default.SeenUnidentified(DateTime.UtcNow);
            }
            catch { ClientPresence.Default.SeenUnidentified(DateTime.UtcNow); }

            // Bounded in size and in time. StreamReader.ReadLine() was neither: a peer that
            // connected and said nothing held this thread until Revit closed, and a line
            // with no newline in it was read until memory ran out.
            LineResult read = LineReader.Read(server, MaxRequestBytes, RequestReadTimeoutMs);
            if (!read.Ok)
            {
                // Closed-without-sending is ordinary (a probe, a cancelled client) and gets
                // no reply because there is nobody to reply to. The rest are worth saying.
                if (read.Outcome != LineOutcome.Closed)
                {
                    Log.Warn("pipe: " + read.Outcome + " - " + read.Error);
                    // BOUNDED (backlog #37): after TooLong the peer is typically still blocked
                    // writing the rest of its request, so it is not reading this. HandleOne's
                    // caller closes the connection when this returns, which ends both writes.
                    if (!LineWriter.TryWriteLine(server, PipeEnvelope.Of(null, false, null, read.Error).ToString(Formatting.None),
                                                 ErrorReplyWriteTimeoutMs))
                        Log.Warn("pipe: the peer did not read the " + read.Outcome + " refusal within " +
                                 ErrorReplyWriteTimeoutMs + " ms; connection closed");
                }
                return;
            }
            string line = read.Line;

            string id = null;
            JObject reply;
            try
            {
                JObject req = Horizun.Revit.Core.JsonText.ParseObject(line);
                id = (string)req["id"];
                string command = (string)req["command"];
                string token = (string)req["token"];

                if (!ConstantTimeEquals(token, _token))
                {
                    reply = PipeEnvelope.Of(id, false, null, "Auth failed: token missing or wrong.");
                }
                else if (string.IsNullOrWhiteSpace(command))
                {
                    reply = PipeEnvelope.Of(id, false, null, "No command given.");
                }
                else if (!PipeAdmission.Allows(slot, command))
                {
                    reply = PipeEnvelope.Of(id, false, null,
                        "Too many concurrent connections to this Revit (limit " + MaxConcurrentConnections + "); this " +
                        "connection used a slot reserved for control messages, and '" + command + "' is not one. " +
                        "Nothing was run.");
                }
                else if (string.Equals(command, "__horizun_request_status", StringComparison.Ordinal))
                {
                    var status=_dispatcher.ObserveRequest((string)req["params"]?["wire_id"]);
                    status["python_runtime"]=PythonRuntimeHealth.Snapshot(Settings.ExecutePythonEnabled);
                    reply=PipeEnvelope.Of(id,true,status,null);
                }
                else if (string.Equals(command, "__horizun_cancel_job", StringComparison.Ordinal))
                {
                    // A DURABLE job (horizun_submit_job / an MCP task) still waiting in
                    // AsyncQueue is taken out and its record finished as cancelled; one the
                    // UI thread already claimed is NOT reported cancelled - it runs to the end.
                    // Never enters Revit's UI queue; answered from the pipe thread.
                    string jobId = (string)req["params"]?["job_id"];
                    AsyncWork removed;
                    bool cancelled = AsyncQueue.TryRemove(jobId, out removed);
                    if (cancelled)
                    {
                        try { removed.Record?.Finish("cancelled", "Removed from the queue before it started, on a cancellation request. Nothing ran."); }
                        catch (Exception ex) { Log.Warn("cancel_job: the record of " + jobId + " could not be finished: " + ex.Message); }
                    }
                    reply = PipeEnvelope.Of(id, true, new JObject
                    {
                        ["cancelled_before_start"] = cancelled,
                        ["job_id"] = jobId,
                        ["state"] = cancelled ? "removed_before_start" : "not_in_queue"
                    }, null);
                }
                else if (string.Equals(command, "__horizun_cancel_queued", StringComparison.Ordinal))
                {
                    string target = (string)req["params"]?["wire_id"];
                    string detail;
                    bool cancelled = _dispatcher.CancelQueued(target, out detail);
                    reply = PipeEnvelope.Of(id, true, new JObject
                    {
                        ["cancelled_before_start"] = cancelled,
                        ["wire_id"] = target,
                        ["state"] = detail,
                        // Not found: finished, or not arrived yet - and if it arrives, the gate
                        // refuses it (backlog #36). Said so the server can say it too.
                        ["refused_on_arrival"] = string.Equals(detail, "not_found_or_finished", StringComparison.Ordinal)
                    }, null);
                }
                else
                {
                    string paramsJson = req["params"] != null ? req["params"].ToString(Formatting.None) : "{}";
                    CommandResult result = _dispatcher.Invoke(id, command, paramsJson, _commandTimeoutMs);
                    // ONE assembly of the wire shape, in PipeEnvelope, so nothing can be
                    // attached on this path and forgotten on another - and so the tests
                    // read the JSON that would go down the pipe instead of asserting
                    // about this file's source text.
                    reply = PipeEnvelope.Of(id, result);
                }
            }
            catch (Exception ex)
            {
                reply = PipeEnvelope.Of(id, false, null, "Malformed request: " + ex.Message);
            }

            string text = reply.ToString(Formatting.None);

            // REFUSED AT THE SOURCE. A reply this big is one nobody can use: the server
            // will not read it, the client would not render it, and pushing it down the
            // pipe first only means both processes carry the bytes before one of them
            // gives up. Saying so HERE is the only place that can also say the command
            // ran - by the time the server times out on a read, that is exactly what it
            // no longer knows. Measured in bytes, not characters: the limit is about
            // memory, and UTF-8 makes those two different numbers.
            int bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > MaxReplyBytes)
            {
                Log.Warn("pipe: reply for '" + (id ?? "?") + "' was " + bytes + " bytes, over the " +
                         MaxReplyBytes + " limit; refused instead of sent");
                text = PipeEnvelope.Of(id, false,
                    null,
                    "THE COMMAND RAN, and its answer is too large to return: " + bytes + " bytes, over the " +
                    MaxReplyBytes + " byte limit. Whatever it was going to do inside Revit, it did - this is the " +
                    "answer being refused, not the work. Nothing was truncated and handed over as if complete. " +
                    "Ask for less of the model at a time: a narrower category, a smaller id list, or one view.")
                    .ToString(Formatting.None);
            }

            // BOUNDED (backlog #37): a peer that stopped reading no longer holds this thread
            // and its connection slot until Revit closes.
            if (!LineWriter.TryWriteLine(server, text, ReplyWriteTimeoutMs))
                Log.Warn("pipe: the reply for '" + (id ?? "?") + "' was not read within " + ReplyWriteTimeoutMs +
                         " ms (or the peer had gone); connection closed");
        }

        // WHO is on the other end of a pipe connection. Win32 answers with the client
        // pid; .NET never surfaced it. Used for presence only (5.16) - never for auth,
        // which stays with the token and the ACL.
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

        // Compares two strings without leaking length/content through timing.
        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
