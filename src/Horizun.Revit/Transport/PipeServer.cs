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
// Accepting a connection is not permission to run: the dispatcher admits one
// command at a time and REFUSES the rest with a description of what is holding
// the thread. So the second caller now gets a sentence instead of a hang.
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
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "Horizun.PipeServer" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
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
        private const int MaxConcurrentConnections = 8;

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

        private readonly SemaphoreSlim _slots = new SemaphoreSlim(MaxConcurrentConnections);

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
                    if (!_slots.Wait(0))
                    {
                        Refuse(server, "Too many concurrent connections to this Revit (limit " +
                                       MaxConcurrentConnections + "). Nothing was run. This bridge serves one " +
                                       "command at a time, so a queue this deep means something is retrying.");
                        Close(server);
                        server = null;
                        continue;
                    }

                    // Ownership moves to the worker, which disposes it. The listener must
                    // not wait for the command to finish - that is the whole point.
                    NamedPipeServerStream connection = server;
                    server = null;
                    var worker = new Thread(() =>
                    {
                        try { HandleOne(connection); }
                        catch { /* one bad connection is not everyone's problem */ }
                        finally { Close(connection); _slots.Release(); }
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
            try
            {
                var w = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                w.WriteLine(Envelope(null, false, null, why).ToString(Formatting.None));
            }
            catch { }
        }

        private void HandleOne(NamedPipeServerStream server)
        {
            var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };

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
                    try { writer.WriteLine(Envelope(null, false, null, read.Error).ToString(Formatting.None)); }
                    catch { }
                }
                return;
            }
            string line = read.Line;

            string id = null;
            JObject reply;
            try
            {
                JObject req = JObject.Parse(line);
                id = (string)req["id"];
                string command = (string)req["command"];
                string token = (string)req["token"];

                if (!ConstantTimeEquals(token, _token))
                {
                    reply = Envelope(id, false, null, "Auth failed: token missing or wrong.");
                }
                else if (string.IsNullOrWhiteSpace(command))
                {
                    reply = Envelope(id, false, null, "No command given.");
                }
                else
                {
                    string paramsJson = req["params"] != null ? req["params"].ToString(Formatting.None) : "{}";
                    CommandResult result = _dispatcher.Invoke(command, paramsJson, _commandTimeoutMs);
                    reply = result.Success
                        ? Envelope(id, true, result.Data, null)
                        : Envelope(id, false, null, result.Error);
                    // Travels on success AND on failure: what Revit objected to is usually
                    // the reason a command failed, and on success it is the part nobody
                    // would otherwise be told.
                    if (result.RevitSaid != null) reply["revit_said"] = JToken.FromObject(result.RevitSaid);
                }
            }
            catch (Exception ex)
            {
                reply = Envelope(id, false, null, "Malformed request: " + ex.Message);
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
                text = Envelope(id, false,
                    null,
                    "THE COMMAND RAN, and its answer is too large to return: " + bytes + " bytes, over the " +
                    MaxReplyBytes + " byte limit. Whatever it was going to do inside Revit, it did - this is the " +
                    "answer being refused, not the work. Nothing was truncated and handed over as if complete. " +
                    "Ask for less of the model at a time: a narrower category, a smaller id list, or one view.")
                    .ToString(Formatting.None);
            }

            try { writer.WriteLine(text); } catch { }
        }

        private static JObject Envelope(string id, bool success, object data, string error)
        {
            return new JObject
            {
                ["id"] = id,
                ["success"] = success,
                ["data"] = data == null ? null : JToken.FromObject(data),
                ["error"] = error
            };
        }

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
