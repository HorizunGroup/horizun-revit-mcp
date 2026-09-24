// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// Requests FROM the server TO the client, and the one use they have today:
// elicitation (elicitation/create, MCP 2025-06-18 and 2025-11-25).
//
// Until this file the stdio channel ran one way: the client asked, the server
// answered. Elicitation turns that around in the middle of a tools/call - the tool
// asks the client to ask its user, and waits. Four things have to hold for that
// not to break the rest of the server:
//
//   1. THE READER NEVER WAITS FOR IT. The client's answer arrives on stdin, which
//      only the reader thread reads. A tool that blocked the reader while waiting
//      would wait for a line nobody can read. So the waiting happens on the tool's
//      own thread (tools/call is already dispatched off the reader), and the reader
//      does exactly one thing with a response: hand it to the pending request that
//      owns its id. That is TryDeliver, and it never blocks.
//
//   2. OUR IDS ARE NOT THE CLIENT'S. JSON-RPC ids are per sender, but a log, a
//      proxy or a confused client reading "id 3" cannot tell whose 3 it is. The
//      server's ids are strings with a fixed prefix the client never sends, and a
//      response is recognised by its SHAPE (no method; result or error) before the
//      session's request-id rule is applied - a response reuses our id, and running
//      it through the rule for new requests would refuse it as a duplicate.
//
//   3. EVERY WAIT ENDS. A human may never answer. Each request carries its own
//      timeout; the tool call's cancellation token ends it too; and when stdin
//      closes or stdout is lost every pending request is failed at once, so the
//      shutdown drain is not held hostage by a form nobody will ever submit. When
//      the server gives up on a request it tells the client with
//      notifications/cancelled, as the cancellation utility allows either side to.
//
//   4. ONLY WHEN THE CLIENT SAID SO. A server MUST NOT send elicitation/create to a
//      client that did not declare the capability, or in a mode it did not declare.
//      What the client declared at initialize, and which revision was negotiated,
//      decide it - never a guess. When the answer is no, the tool refuses with the
//      machine-readable code elicitation_unsupported so the agent asks in the chat.
//
// The 2026-07-28 revision does not send elicitation/create as a request at all: it
// returns an InputRequiredResult and the client retries the call with the answers
// (multi round-trip requests). This server does not implement that pattern yet, so
// a modern request is told elicitation is unsupported, with that reason.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal enum ClientReplyOutcome
    {
        /// <summary>The client answered with a JSON-RPC result.</summary>
        Result,
        /// <summary>The client answered with a JSON-RPC error.</summary>
        Error,
        /// <summary>No answer inside the request's own timeout.</summary>
        TimedOut,
        /// <summary>The tool call that was waiting was cancelled.</summary>
        Cancelled,
        /// <summary>The request could not be written, or the channel closed before an answer.</summary>
        ChannelLost
    }

    /// <summary>What came back for one server-to-client request.</summary>
    internal sealed class ClientReply
    {
        public string RequestId;
        public ClientReplyOutcome Outcome;
        public JObject Result;
        public int ErrorCode;
        public string ErrorMessage;
        public long ElapsedMs;
    }

    /// <summary>
    /// The outbound request table. One per server process; thread-safe.
    /// </summary>
    internal sealed class McpClientRequests
    {
        /// <summary>
        /// Prefix of every id this server puts on a request of its own. A client never
        /// uses it, so an id cannot be read as belonging to the other side.
        /// </summary>
        public const string IdPrefix = "horizun-server-";

        /// <summary>
        /// How many server-to-client requests may wait at once. Host-resident work is
        /// already capped at eight concurrent tasks; this is the same order, and the
        /// ninth caller is told so instead of queueing forms behind forms.
        /// </summary>
        public const int MaxPending = 8;

        private sealed class Pending
        {
            public TaskCompletionSource<ClientReply> Completion =
                new TaskCompletionSource<ClientReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly Func<JObject, bool> _write;
        private readonly ConcurrentDictionary<string, Pending> _pending =
            new ConcurrentDictionary<string, Pending>(StringComparer.Ordinal);
        private long _next;
        private string _closedReason;

        /// <param name="write">Writes one complete JSON-RPC line; false when it was not delivered.</param>
        public McpClientRequests(Func<JObject, bool> write)
        {
            _write = write ?? throw new ArgumentNullException(nameof(write));
        }

        public int PendingCount => _pending.Count;

        /// <summary>
        /// Is this message a RESPONSE (to a request of ours) rather than a request or a
        /// notification? Decided by shape, as JSON-RPC defines it: no method, and a
        /// result or an error.
        /// </summary>
        public static bool IsResponse(JObject message)
            => message != null && message.Property("method") == null &&
               (message.Property("result") != null || message.Property("error") != null);

        /// <summary>
        /// Send one request and wait for its answer ON THE CALLER'S THREAD. Never call
        /// this from the reader thread: the answer is read there.
        /// </summary>
        public ClientReply Send(string method, JObject parameters, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(method)) throw new ArgumentException("method is required", nameof(method));
            if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            string id = IdPrefix + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);

            string closed = Volatile.Read(ref _closedReason);
            if (closed != null)
                return new ClientReply
                {
                    RequestId = id, Outcome = ClientReplyOutcome.ChannelLost, ErrorMessage = closed, ElapsedMs = 0
                };

            if (_pending.Count >= MaxPending)
                return new ClientReply
                {
                    RequestId = id, Outcome = ClientReplyOutcome.Error, ErrorCode = -32000,
                    ErrorMessage = MaxPending + " server-to-client requests are already waiting for the client; " +
                                   "this one was not sent.",
                    ElapsedMs = clock.ElapsedMilliseconds
                };

            var pending = new Pending();
            _pending[id] = pending;

            // Registered BEFORE the write, so an answer that arrives faster than this
            // thread returns from the write still finds its owner.
            var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters != null) message["params"] = parameters;
            if (!_write(message))
            {
                _pending.TryRemove(id, out _);
                return new ClientReply
                {
                    RequestId = id, Outcome = ClientReplyOutcome.ChannelLost,
                    ErrorMessage = "the request could not be written to the client", ElapsedMs = clock.ElapsedMilliseconds
                };
            }

            // FailAll may have run between the closed check and the registration; if so
            // nobody else will ever complete this entry.
            closed = Volatile.Read(ref _closedReason);
            if (closed != null) pending.Completion.TrySetResult(new ClientReply
            {
                RequestId = id, Outcome = ClientReplyOutcome.ChannelLost, ErrorMessage = closed
            });

            Task<ClientReply> task = pending.Completion.Task;
            int which;
            try
            {
                which = WaitHandle.WaitAny(new[] { ((IAsyncResult)task).AsyncWaitHandle, ct.WaitHandle }, timeoutMs);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }

            if (task.IsCompleted)
            {
                ClientReply reply = task.GetAwaiter().GetResult();
                reply.ElapsedMs = clock.ElapsedMilliseconds;
                return reply;
            }

            bool cancelled = which == 1;
            string reason = cancelled
                ? "the tool call that sent this request was cancelled"
                : "no answer within " + timeoutMs + " ms";
            // Tell the client we stopped waiting, so it can close the form. Best effort:
            // a client that never shows it loses nothing.
            _write(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/cancelled",
                ["params"] = new JObject { ["requestId"] = id, ["reason"] = reason }
            });
            return new ClientReply
            {
                RequestId = id,
                Outcome = cancelled ? ClientReplyOutcome.Cancelled : ClientReplyOutcome.TimedOut,
                ErrorMessage = reason,
                ElapsedMs = clock.ElapsedMilliseconds
            };
        }

        /// <summary>
        /// Called by the reader for every message <see cref="IsResponse"/> accepts.
        /// Completes the matching request and returns true; an unknown id (late, after a
        /// timeout, or never ours) is logged and dropped - a response is never answered.
        /// Never blocks.
        /// </summary>
        public bool TryDeliver(JObject response)
        {
            JToken idToken = response?["id"];
            string id = idToken != null && idToken.Type == JTokenType.String ? (string)idToken : null;
            if (id == null || !_pending.TryGetValue(id, out Pending pending))
            {
                Log.Warn("a response arrived for " + (idToken == null ? "no id" : "id " + idToken.ToString(Formatting.None)) +
                         ", which matches no request this server is waiting on (late after a timeout, or never ours); " +
                         "it was dropped.");
                return false;
            }

            var reply = new ClientReply { RequestId = id };
            if (response["error"] is JObject error)
            {
                reply.Outcome = ClientReplyOutcome.Error;
                reply.ErrorCode = error["code"] != null && error["code"].Type == JTokenType.Integer ? (int)error["code"] : 0;
                reply.ErrorMessage = error["message"] != null && error["message"].Type == JTokenType.String
                    ? (string)error["message"] : "(the client's error carried no message)";
            }
            else if (response["result"] is JObject result)
            {
                reply.Outcome = ClientReplyOutcome.Result;
                reply.Result = result;
            }
            else
            {
                reply.Outcome = ClientReplyOutcome.Error;
                reply.ErrorCode = -32603;
                reply.ErrorMessage = "the client's response carried neither an object result nor an error object";
            }
            return pending.Completion.TrySetResult(reply);
        }

        /// <summary>
        /// End every pending request now and refuse new ones: stdin closed or stdout was
        /// lost, so no answer can arrive or no request can be read.
        /// </summary>
        public void FailAll(string reason)
        {
            Interlocked.CompareExchange(ref _closedReason, reason ?? "the channel closed", null);
            foreach (var kv in _pending)
                kv.Value.Completion.TrySetResult(new ClientReply
                {
                    RequestId = kv.Key, Outcome = ClientReplyOutcome.ChannelLost, ErrorMessage = reason
                });
        }
    }

    /// <summary>
    /// What the client said it can do about elicitation, and whether this server may
    /// therefore send it one. Captured once, at initialize, from the negotiated
    /// revision and the declared capabilities.
    /// </summary>
    internal sealed class ClientElicitationSupport
    {
        public const string CodeUnsupported = "elicitation_unsupported";

        public string ProtocolVersion { get; private set; }
        public bool Form { get; private set; }
        public bool Url { get; private set; }
        public JToken Declared { get; private set; }

        /// <summary>Null when form-mode elicitation may be sent; otherwise a machine-readable reason.</summary>
        public string UnsupportedReason { get; private set; }

        public bool CanElicitForm => UnsupportedReason == null && Form;

        /// <summary>The revisions in which elicitation/create is a server-to-client request.</summary>
        public static bool RevisionHasElicitationRequests(string version)
            => version == "2025-06-18" || version == "2025-11-25";

        /// <summary>
        /// Read the legacy handshake. 2025-06-18: `elicitation: {}` means form.
        /// 2025-11-25: `form` and `url` are the modes, and an EMPTY object is form only,
        /// for backwards compatibility. Anything else is not a declaration of form.
        /// </summary>
        public static ClientElicitationSupport FromInitialize(string negotiatedVersion, JObject clientCapabilities)
        {
            var s = new ClientElicitationSupport { ProtocolVersion = negotiatedVersion };
            JToken declared = clientCapabilities?["elicitation"];
            s.Declared = declared?.DeepClone();

            if (!RevisionHasElicitationRequests(negotiatedVersion))
            {
                s.UnsupportedReason = "protocol_version";
                return s;
            }
            if (!(declared is JObject obj))
            {
                s.UnsupportedReason = "client_did_not_declare";
                return s;
            }

            bool hasForm = obj["form"] is JObject, hasUrl = obj["url"] is JObject;
            if (negotiatedVersion == "2025-06-18" || (obj["form"] == null && obj["url"] == null))
                s.Form = true;
            else
                s.Form = hasForm;
            s.Url = negotiatedVersion == "2025-11-25" && hasUrl;
            if (!s.Form) s.UnsupportedReason = "form_mode_not_declared";
            return s;
        }

        public static ClientElicitationSupport NotInitialized() => new ClientElicitationSupport
        {
            UnsupportedReason = "no_handshake"
        };

        /// <summary>A 2026-07-28 request: elicitation there is an InputRequiredResult, not a request.</summary>
        public static ClientElicitationSupport Modern(string declaredVersion) => new ClientElicitationSupport
        {
            ProtocolVersion = declaredVersion,
            UnsupportedReason = "input_required_result_not_implemented"
        };

        /// <summary>A task-augmented call runs detached from the request that could carry a form.</summary>
        public ClientElicitationSupport ForTask() => new ClientElicitationSupport
        {
            ProtocolVersion = ProtocolVersion, Declared = Declared?.DeepClone(), Form = Form, Url = Url,
            UnsupportedReason = "task_augmented_call"
        };

        public string Explain(string language)
        {
            bool es = language == "es";
            switch (UnsupportedReason)
            {
                case null: return es ? "El cliente declaró elicitation en modo formulario." : "The client declared form-mode elicitation.";
                case "protocol_version":
                    return es
                        ? "La versión de protocolo negociada (" + ProtocolVersion + ") no tiene elicitation; existe desde 2025-06-18."
                        : "The negotiated protocol version (" + ProtocolVersion + ") has no elicitation; it exists from 2025-06-18.";
                case "client_did_not_declare":
                    return es
                        ? "El cliente no declaró la capacidad 'elicitation' en initialize, y un servidor no puede enviársela."
                        : "The client did not declare the 'elicitation' capability at initialize, and a server must not send it one.";
                case "form_mode_not_declared":
                    return es
                        ? "El cliente declaró elicitation solo en modo URL; estas preguntas necesitan el modo formulario."
                        : "The client declared elicitation in URL mode only; these questions need form mode.";
                case "input_required_result_not_implemented":
                    return es
                        ? "En la revisión 2026-07-28 la elicitation viaja como InputRequiredResult y este servidor aún no lo implementa."
                        : "In revision 2026-07-28 elicitation travels as an InputRequiredResult, which this server does not implement yet.";
                case "task_augmented_call":
                    return es
                        ? "Una llamada ejecutada como tarea no puede abrir formularios; llámala sin 'task'."
                        : "A call run as a task cannot open forms; call it without 'task'.";
                case "no_handshake":
                    return es ? "No hubo handshake initialize en esta sesión." : "No initialize handshake happened in this session.";
                default: return UnsupportedReason;
            }
        }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["protocol_version"] = ProtocolVersion,
                ["declared"] = Declared?.DeepClone(),
                ["form"] = Form,
                ["url"] = Url,
                ["supported"] = CanElicitForm
            };
            if (UnsupportedReason != null) o["reason"] = UnsupportedReason;
            return o;
        }
    }

    /// <summary>
    /// What the tool currently running may ask the client. Flows with the tool call
    /// (AsyncLocal), so a host handler reads it without a parameter, and a handler
    /// that was never given one - a unit test, a procedure step - sees null and
    /// refuses exactly as an unsupported client would.
    /// </summary>
    internal sealed class ClientContext
    {
        private static readonly AsyncLocal<ClientContext> _current = new AsyncLocal<ClientContext>();

        public static ClientContext Current => _current.Value;

        public McpClientRequests Channel { get; }
        public ClientElicitationSupport Elicitation { get; }

        public ClientContext(McpClientRequests channel, ClientElicitationSupport elicitation)
        {
            Channel = channel;
            Elicitation = elicitation ?? ClientElicitationSupport.NotInitialized();
        }

        /// <summary>Install for the current flow; dispose restores what was there.</summary>
        public static IDisposable Enter(ClientContext context)
        {
            ClientContext previous = _current.Value;
            _current.Value = context;
            return new Restore(previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly ClientContext _previous;
            public Restore(ClientContext previous) { _previous = previous; }
            public void Dispose() { _current.Value = _previous; }
        }

        /// <summary>Send one form-mode elicitation/create. Callers check CanElicitForm first.</summary>
        public ClientReply ElicitForm(string message, JObject requestedSchema, int timeoutMs, CancellationToken ct)
        {
            if (!Elicitation.CanElicitForm || Channel == null)
                throw new InvalidOperationException("form-mode elicitation is not available: " + Elicitation.UnsupportedReason);
            var prms = new JObject { ["message"] = message, ["requestedSchema"] = requestedSchema };
            // 2025-11-25 names the mode; 2025-06-18 has no such field.
            if (Elicitation.ProtocolVersion == "2025-11-25") prms.AddFirst(new JProperty("mode", "form"));
            return Channel.Send("elicitation/create", prms, timeoutMs, ct);
        }
    }
}
