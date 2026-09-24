// -----------------------------------------------------------------------------
// Horizun MCP server — original Horizun code.
//
// A stdio MCP server. It reads newline-delimited JSON-RPC from stdin, answers the
// MCP handshake, and for tools/call forwards the arguments to the Revit add-in
// over its named pipe and returns the reply. The MCP wire format is implemented
// directly from the open spec — no third-party MCP SDK.
//
// Tools are declared in one place (the Tools table). Each maps an MCP tool name
// to the plugin command it forwards to, plus the JSON schema the client sees.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static class Program
    {
        private const string ServerName = "horizun-mcp";
        // Read from the assembly the build stamped, never hand-typed: a version that has
        // to be edited in two places is a version that will disagree with itself.
        private static readonly string ServerVersion = ReadVersion();
        private const int CommandTimeoutMs = 600000;
        // horizun_health is the diagnostic command: its whole job is to answer fast, and
        // an answer that does not come fast IS the diagnosis (Revit busy, or a modal up).
        // Measured 2026-08-07: three health calls each waited the full 600 s behind a
        // "New Project" dialog - 30 minutes to learn what one minute would have said.
        // The ceiling is generous against warm-up (25 s measured on first call after
        // add-in load) and still 20x faster than the general timeout. The add-in side
        // now also detects a persistent modal and answers in seconds; this bound is the
        // backstop for when that probe cannot see (health is queued behind long real
        // work, or the probe never captured its facts).
        private const int HealthTimeoutMs = 30000;
        // The version list and the negotiation rule live in ProtocolNegotiation.cs,
        // where they are golden-tested - see that file for why 2026-07-28 is absent.

        // Which Revit year to target. Empty = the most recently seen Revit.
        private static readonly string TargetYear = Environment.GetEnvironmentVariable("HORIZUN_REVIT_YEAR") ?? "";

        private static string ReadVersion()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var attr = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                string v = attr != null ? attr.InformationalVersion : asm.GetName().Version?.ToString();
                if (string.IsNullOrEmpty(v)) return "unknown";
                int plus = v.IndexOf('+');   // SourceLink appends the commit sha
                return plus > 0 ? v.Substring(0, plus) : v;
            }
            catch { return "unknown"; }
        }



        private static OutboundWriter _writer;
        private static readonly InFlight _inFlight = new InFlight();
        private static readonly McpSession _session = new McpSession();
        private static string _negotiatedProtocol;

        /// <summary>
        /// True once a LEGACY initialize handshake has completed in this process.
        ///
        /// NOT A VERSION FLAG, and it is never consulted to decide how to shape a result:
        /// every request carries its own protocol declaration and ResultEnvelope reads that
        /// and nothing else. This answers a different question - "is the peer on this stdio
        /// channel a client that speaks the handshake dialect?" - which the specification
        /// scopes to the process for exactly this kind of decision. It exists so an
        /// unsolicited notification goes only to a client whose protocol has unsolicited
        /// notifications in it.
        /// </summary>
        private static bool _legacyHandshake;

        private static ToolListMonitor _toolListMonitor;

        /// <summary>
        /// Requests this server sends TO the client (elicitation/create), and what the
        /// client declared it can answer. See McpClientRequests.cs.
        /// </summary>
        private static McpClientRequests _clientRequests;
        private static ClientElicitationSupport _elicitation = ClientElicitationSupport.NotInitialized();

        /// <summary>
        /// Set once the response channel has failed. The read loop stops on it, so no
        /// further request - least of all a mutation - is accepted after the point where
        /// its answer could no longer be delivered.
        /// </summary>
        private static int _responseChannelLost;

        private static int Main()
        {
            // MCP stdio is UTF-8, newline-delimited, no BOM.
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            // The raw stream, not a StreamReader: the size limit has to be enforced WHILE
            // reading, and ReadLine() allocates the whole line before anyone can object.
            // See BoundedLineReader.cs.
            var stdin = new BoundedLineReader(Console.OpenStandardInput());

            // LOSING STDOUT IS TERMINAL, and it has to be acted on rather than waited out.
            // stdout and stdin are separate pipes: a client that has stopped reading its
            // end of stdout has not closed stdin, so nothing in the read loop would ever
            // find out. The old writer swallowed the error and reported success, and the
            // server carried on accepting tool calls and running MUTATIONS whose results
            // went nowhere. Cancel what is in flight and stop reading.
            _writer = new OutboundWriter(stdout, reason =>
            {
                Log.Error("the response channel is gone (" + reason + "); cancelling in-flight work and " +
                          "shutting down. Nothing further will be accepted: results could not be delivered, " +
                          "and a mutation whose outcome cannot be reported must not be started.", null);
                Volatile.Write(ref _responseChannelLost, 1);
                _clientRequests?.FailAll("the response channel was lost (" + reason + ")");
                Protocol.SubscriptionStream.MarkAllTermination(Protocol.SubscriptionStream.TransportLost);
                try { _inFlight.CancelAll(); } catch (Exception ex) { Log.Warn("cancel-all after channel loss: " + ex.Message); }
            });

            _clientRequests = new McpClientRequests(_writer.Write);

            Log.Start();

            // Orphaned discovery files - left by a Revit that crashed or was killed past a
            // modal - are swept at startup. The add-in sweeps only when it publishes (when
            // a Revit STARTS); a server that comes up after a crash, with no new Revit, is
            // the exact moment nothing else would clean them (story 5.24).
            try
            {
                int swept = PipeClient.SweepStaleDiscovery();
                if (swept > 0) Log.Info("swept " + swept + " orphaned discovery file(s) at startup");
            }
            catch (Exception ex) { Log.Warn("discovery sweep at startup failed: " + ex.Message); }

            Log.Info("server " + ServerVersion + " up" +
                     (string.IsNullOrEmpty(TargetYear) ? "" : ", HORIZUN_REVIT_YEAR=" + TargetYear));

            // THE READER NEVER BLOCKS ON WORK. It used to run each request to completion
            // before reading the next line, so while a two-minute model_scan ran, ping,
            // health and job_status were all unanswerable - and job_status exists precisely
            // to be asked while Revit is busy.
            while (true)
            {
                // Checked before the read AND after it: the channel can die while this
                // thread is blocked waiting for the next line, and the request that
                // arrives next would then be accepted with nowhere to send its answer.
                if (Volatile.Read(ref _responseChannelLost) != 0)
                {
                    Log.Error("stopping the read loop: the response channel was lost.", null);
                    break;
                }

                BoundedLine incoming = stdin.ReadLine();

                if (Volatile.Read(ref _responseChannelLost) != 0)
                {
                    Log.Error("a request arrived after the response channel was lost; it was NOT dispatched.", null);
                    break;
                }

                if (incoming.Outcome == BoundedLineOutcome.Failed)
                {
                    Log.Error("stdin failed; the client is gone: " + incoming.Error, null);
                    break;
                }
                if (incoming.Outcome == BoundedLineOutcome.EndOfStream)
                {
                    // A half-sent request is worth a line in the log: the difference
                    // between a client that finished and one that died mid-write is the
                    // first thing anybody asks afterwards.
                    if (incoming.Error != null) Log.Warn("stdin: " + incoming.Error);
                    break;                        // the client shut us down
                }
                if (incoming.Outcome == BoundedLineOutcome.TooLong)
                {
                    // Answered, not dropped, and the session CONTINUES: the reader is
                    // positioned at the start of the next request, so one oversized line
                    // costs that request and nothing else.
                    Log.Warn("request refused: " + incoming.Bytes + " bytes, over the " +
                             BoundedLineReader.DefaultMaxBytes + " byte limit");
                    _writer.TryError(null, -32600, incoming.Error);
                    continue;
                }

                string line = incoming.Line;
                if (line.Length == 0) continue;

                // EVERY step of reading a message is inside the guard, including pulling
                // out the id and the method. They used to sit outside it, and a message
                // whose "id" or "method" was an object rather than a scalar threw an
                // uncaught cast on the way in - killing the whole process, and with it
                // the client's only bridge to Revit, over one malformed line.
                try
                {
                    JObject msg;
                    try { msg = JObject.Parse(line); }
                    catch (Exception ex)
                    {
                        // -32700, and it is ANSWERED rather than dropped. This used to log
                        // and `continue`, which left a client that sent one malformed line
                        // waiting for a reply that was never coming - indistinguishable, from
                        // where it sits, from a bridge that has hung. The id is null because
                        // the id is exactly what could not be read.
                        //
                        // The offending text is NOT echoed back. A line that failed to parse
                        // is still a line the caller sent, and it can carry a path, a token,
                        // or a model name; reporting its length and the parser's position
                        // says where to look without repeating the content.
                        Log.Warn("parse error answered: " + ex.Message);
                        _writer.TryError(null, -32700,
                            "Parse error: that line is not valid JSON (" + ex.Message + "). The line was " +
                            line.Length + " characters. Nothing was run, and no id could be read from it, so this " +
                            "reply carries id null - it cannot be matched to your request. The content is not " +
                            "echoed back because a line that failed to parse can still contain a path or a token.");
                        continue;
                    }

                    // A RESPONSE TO ONE OF OUR REQUESTS (elicitation/create), recognised
                    // by shape before anything else: it reuses an id WE chose, so the
                    // lifetime rule for the client's request ids below does not apply
                    // to it, and it is never answered. Handing it over never blocks -
                    // the tool waiting for it runs on its own thread.
                    if (McpClientRequests.IsResponse(msg))
                    {
                        _clientRequests.TryDeliver(msg);
                        continue;
                    }

                    // MCP narrows JSON-RPC: ids are string/integer, never null, and a
                    // requestor MUST NOT reuse one anywhere in this session. The session
                    // owns that lifetime rule; Wire owns only one answer for one request.
                    bool isNotification;
                    object id;
                    string idError;
                    if (!_session.TryAcceptId(msg, out isNotification, out id, out idError))
                    {
                        Log.Warn("request refused: " + idError);
                        _writer.TryError(null, -32600, idError);
                        continue;
                    }

                    // JSON-RPC 2.0 requires this field to be exactly "2.0". It was never
                    // checked: a client announcing 1.0, or a different protocol entirely,
                    // was served as though it had agreed to this one - and the first thing
                    // it would disagree about is how errors come back, which is exactly
                    // when a caller can least afford a surprise. Absent is refused too:
                    // this is a REQUEST on a JSON-RPC stream, not a guess about intent.
                    JToken ver = msg["jsonrpc"];
                    string verText = ver is JValue vv ? vv.Value as string : null;
                    if (verText != "2.0")
                    {
                        string saw = ver == null ? "absent" : "'" + ver.ToString(Formatting.None) + "'";
                        Log.Warn("request refused: jsonrpc was " + saw);
                        if (!isNotification)
                            _writer.TryError(id, -32600,
                                "Invalid request: 'jsonrpc' must be exactly \"2.0\", and it was " + saw + ". " +
                                "Nothing was done. This server speaks JSON-RPC 2.0 only, and serving a caller that " +
                                "announced a different protocol would mean agreeing about requests while disagreeing " +
                                "about how failures come back.");
                        continue;
                    }

                    string method;
                    string methodError;
                    if (!_session.TryReadMethod(msg, out method, out methodError))
                    {
                        if (!isNotification) _writer.TryError(id, -32600, methodError);
                        continue;
                    }

                    JObject prms = msg["params"] as JObject;

                    // WHICH PROTOCOL ERA IS THIS REQUEST IN. 2026-07-28 removed the
                    // handshake: a modern request carries its version and the client's
                    // capabilities in _meta and expects to be served with no session at
                    // all. Read before the lifecycle gate, because the gate's whole
                    // premise - "nothing happens before initialize" - is a legacy rule
                    // and applying it to a modern request would refuse every one of them.
                    //
                    // A request with no modern _meta reads exactly as it did before this
                    // existed, so the legacy path is untouched.
                    Protocol.RequestEnvelope envelope;
                    try
                    {
                        envelope = Protocol.RequestEnvelope.Read(method, prms);
                    }
                    catch (Protocol.McpDataError de)
                    {
                        Log.Warn("protocol metadata refused: " + de.Message);
                        if (!isNotification) _writer.TryError(id, de.Code, de.Message, de.ErrorData);
                        continue;
                    }

                    // A modern session has no notifications/initialized to hang this off.
                    if (envelope.Era == Protocol.McpEra.Modern) EnsureToolListMonitor();

                    string lifecycleError;
                    if (!_session.Allows(method, isNotification, envelope.Era, out lifecycleError))
                    {
                        Log.Warn("message refused by MCP lifecycle: " + lifecycleError);
                        if (!isNotification) _writer.TryError(id, -32600, lifecycleError);
                        continue;
                    }

                    // Cancellation is a notification and must be handled by the READER,
                    // immediately - queueing it behind the work it cancels would be a joke.
                    if (method == "notifications/cancelled")
                    {
                        HandleCancel(prms);
                        continue;
                    }

                    // tools/call is the only method that can take minutes, so it is the only
                    // one that leaves this thread. Everything else answers instantly and in
                    // order, which is what `initialize` in particular needs.
                    if (method == "tools/call" && !isNotification)
                    {
                        DispatchToolCall(id, prms, envelope);
                        continue;
                    }

                    if (method == "tasks/result" && !isNotification)
                    {
                        if (envelope.Era == Protocol.McpEra.Modern)
                        {
                            _writer.TryError(id, -32601,
                                "tasks/result was removed when tasks became the " +
                                Protocol.ExtensionRegistry.Tasks + " extension. Poll tasks/get: its reply carries " +
                                "the final result once the task is completed, and the error once it has failed.");
                            continue;
                        }
                        DispatchTaskResult(id, prms);
                        continue;
                    }

                    // subscriptions/listen is the other method that outlives its request.
                    // On stdio the response IS the stream: it is dispatched off this
                    // thread, stays open while notifications flow tagged with its id, and
                    // answers once when it ends. See Protocol/SubscriptionStream.cs.
                    if (method == "subscriptions/listen" && !isNotification)
                    {
                        DispatchSubscription(id, prms, envelope);
                        continue;
                    }

                    try
                    {
                        JToken result = Protocol.ResultEnvelope.Stamp(
                            Handle(method, prms, envelope), envelope.Era, method, prms);
                        if (!isNotification && result != null)
                        {
                            bool delivered = _writer.TryReply(id, result);
                            if (delivered && method == "initialize") _session.InitializeAnswerDelivered();
                        }
                        else if (isNotification && method == "notifications/initialized")
                        {
                            _session.InitializedNotificationAccepted();
                            EnsureToolListMonitor();
                        }
                    }
                    catch (Protocol.McpDataError de)
                    {
                        if (!isNotification) _writer.TryError(id, de.Code, de.Message, de.ErrorData);
                    }
                    catch (McpError me)
                    {
                        if (!isNotification) _writer.TryError(id, me.Code, me.Message);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("'" + method + "' threw", ex);
                        if (!isNotification) _writer.TryError(id, -32603, ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    // Last resort. Whatever just happened, the next message still deserves
                    // a server: a bad line must never be able to end the session.
                    Log.Error("message loop recovered from an unexpected failure", ex);
                }
            }

            // stdin closed. That is not always a client that vanished: a caller can send a
            // batch of requests and close the stream, which is how scripts drive this. The
            // reader used to return here immediately and the process exited with every
            // in-flight task still working - so every answer was lost. Measured: a
            // verification run of twenty probes received zero replies.
            //
            // So give outstanding work a bounded chance to answer. If the client really is
            // gone the writes fail harmlessly; if it is waiting, it gets what it asked for.
            // The bound is DERIVED, not chosen. Each outstanding request carries the
            // instant by which it must have answered - CommandTimeoutMs from its start,
            // the same limit PipeClient.Send already enforces - so draining until the
            // latest of those has passed terminates for a reason, and terminates exactly
            // when there is provably nothing left to wait for.
            //
            // The first version waited a flat 120 s. That is far too long for a
            // host-resident call that answered in 3 ms, and too SHORT for a scan of a
            // 200k-element model which is entitled to ten minutes - so the arbitrary
            // number could discard the very answer the drain was added to protect.
            //
            // A tool waiting for the CLIENT (elicitation) can never be answered now -
            // the answer would arrive on the stdin that just closed - so those waits end
            // first, and the tool reports the question as unanswered instead of holding
            // the drain for its full timeout.
            _clientRequests.FailAll("the client closed stdin, so no answer can arrive");
            if (_inFlight.Count > 0)
            {
                DateTime? deadline = _inFlight.DrainDeadlineUtc();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                int budgetMs = deadline.HasValue
                    ? (int)Math.Max(0, (deadline.Value - DateTime.UtcNow).TotalMilliseconds)
                    : 0;

                Log.Info("stdin closed with work outstanding (" + _inFlight.Describe() +
                         "); draining until every outstanding request has reached its own deadline, at most " +
                         (budgetMs / 1000) + " s from now");

                while (_inFlight.Count > 0 && DateTime.UtcNow < deadline.Value)
                {
                    Thread.Sleep(50);
                    // A later request cannot appear - stdin is closed - but one finishing
                    // shortens the wait, so re-read rather than holding the first answer.
                    DateTime? d = _inFlight.DrainDeadlineUtc();
                    if (!d.HasValue) break;
                    deadline = d;
                }

                if (_inFlight.Count > 0)
                    Log.Warn("every outstanding request has passed its own deadline and " + _inFlight.Describe() +
                             " has still not answered. Revit is not interruptible from here, so that work continues " +
                             "inside Revit and its result will reach nobody.");
                else
                    Log.Info("all outstanding work answered in " + clock.ElapsedMilliseconds + " ms");
            }
            Protocol.SubscriptionStream.MarkAllTermination(Protocol.SubscriptionStream.ServerTornDown);
            _inFlight.CancelAll();
            try { _toolListMonitor?.Dispose(); } catch { }

            Log.Info("server down (stdin closed)");
            return 0;
        }

        /// <summary>
        /// Run a tool OFF the reader thread, so the next request can be read and answered
        /// while this one waits on Revit. Every exit path answers exactly once - the writer
        /// enforces that, because a timeout and a completion can both arrive.
        /// </summary>
        private static void DispatchToolCall(object id, JObject prms, Protocol.RequestEnvelope envelope)
        {
            string key = OutboundWriter.Key(id);
            string toolName = (string)prms?["name"] ?? "(unnamed)";
            var cts = new CancellationTokenSource();

            string refusal;
            // The deadline this request will be drained against on shutdown - the same
            // limit the call itself is held to, so the two cannot drift apart.
            int requestDeadlineMs = toolName == "horizun_health" ? HealthTimeoutMs : CommandTimeoutMs;
            if (!_inFlight.TryStart(key, toolName, cts, out refusal, requestDeadlineMs))
            {
                cts.Dispose();
                Log.Warn("request id already in flight, refused for '" + toolName + "'");
                _writer.TryError(id, -32600, refusal);
                return;
            }

            // One answer for THIS request. McpSession separately owns the stronger MCP
            // rule that an id is never reused during the lifetime of this connection.
            ReplySlot reply = _writer.Slot(id);

            // A progressToken means the caller wants to know it is still alive. We do not
            // invent a percentage - a Revit command reports no fraction of itself - so the
            // heartbeat carries elapsed time and nothing it cannot support.
            JToken progressToken = prms?["_meta"]?["progressToken"];

            // THE QUEUE CLOCK, started here rather than inside the task. One command runs
            // at a time against Revit, so the wait before work begins is often the whole
            // of a "slow" call - and a total that hides it measures the queue and reports
            // the tool.
            var queueClock = System.Diagnostics.Stopwatch.StartNew();
            string scenario = Metrics.ScenarioOf(prms);
            int requestBytes = prms == null ? 0 : prms.ToString(Formatting.None).Length;

            Task.Run(() =>
            {
                long queuedMs = queueClock.ElapsedMilliseconds;
                var metric = new CallMetric
                {
                    Scenario = scenario,
                    Tool = toolName,
                    Era = envelope != null && envelope.Era == Protocol.McpEra.Modern ? "modern" : "legacy",
                    RequestId = key,
                    QueuedMs = queuedMs,
                    RequestBytes = requestBytes,
                    StartedUtc = DateTime.UtcNow.ToString("o")
                };
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var started = new JObject
                {
                    ["event"] = "tool_started", ["tool"] = toolName, ["request_id"] = key
                };
                if (envelope != null && envelope.Era == Protocol.McpEra.Modern)
                    McpLogging.EmitForRequest("info", started, _writer.Notify, envelope.LogLevel, id);
                else
                    McpLogging.Emit("info", started, _writer.Notify);
                JObject bridgeObservation=null;

                // DELIBERATE SILENCE IS NOT THE BUG THE FINAL GUARD LOOKS FOR. A modern
                // cancelled request is answered by not answering; the guard below exists
                // for a request that fell through every path, and it must not fill this in.
                bool silencedByCancellation = false;
                using (var heartbeat = StartHeartbeat(progressToken, toolName, clock, cts.Token, observation:()=>Volatile.Read(ref bridgeObservation)))
                {
                    try
                    {
                        JToken result;
                        // What THIS call may ask the client. It flows with the call into
                        // the host handler's own task. A modern request cannot receive a
                        // server-to-client request, and a task-augmented one outlives the
                        // request that could carry a form.
                        ClientElicitationSupport elicitation =
                            envelope != null && envelope.Era == Protocol.McpEra.Modern
                                ? ClientElicitationSupport.Modern(envelope.DeclaredVersion)
                                : prms?["task"] != null ? _elicitation.ForTask() : _elicitation;
                        using (ClientContext.Enter(new ClientContext(_clientRequests, elicitation)))
                            result = CallTool(prms, cts.Token, progressToken==null ? (Action<JObject>)null : status=>Volatile.Write(ref bridgeObservation,status), envelope);
                        if (cts.IsCancellationRequested)
                        {
                            // Two events race after cancellation: PipeClient may observe
                            // the token first and throw, or the add-in may remove the FIFO
                            // entry and return its failure envelope first. Preserve the
                            // stronger NEVER STARTED proof in either ordering.
                            string proof = CancellationProof(result);
                            metric.Outcome = "cancelled";
                            silencedByCancellation = AnswerCancellation(
                                reply, envelope, toolName,
                                proof ?? CancelledMessage(toolName, clock.ElapsedMilliseconds));
                            ClientToolFinished(toolName, "cancelled", clock.ElapsedMilliseconds, "notice", envelope, id);
                        }
                        else
                        {
                            // A tools/call answered as a TASK carries resultType "task";
                            // an ordinary one carries "complete". The augmentation the
                            // caller sent is what decides, so the two can never disagree.
                            string resultType = prms?["task"] != null
                                ? Protocol.ResultEnvelope.Task
                                : Protocol.ResultEnvelope.Complete;
                            reply.TryReply(Protocol.ResultEnvelope.Stamp(
                                result, envelope != null ? envelope.Era : Protocol.McpEra.Legacy,
                                "tools/call", prms, resultType));
                            bool resultError = (bool?)result?["isError"] == true;
                            metric.ResponseBytes = result == null
                                ? 0 : result.ToString(Formatting.None).Length;
                            // FOUR OUTCOMES. `partial` comes from what the command itself
                            // declared, never from guessing at the payload.
                            metric.Outcome = resultError ? "error" : PartialOrOk(result);
                            ClientToolFinished(toolName, resultError ? "error" : "ok",
                                clock.ElapsedMilliseconds, resultError ? "warning" : "info", envelope, id);
                        }
                    }
                    catch (McpError me)
                    {
                        metric.Outcome = "error";
                        reply.TryError(me.Code, me.Message);
                        ClientToolFinished(toolName, "protocol_error", clock.ElapsedMilliseconds, "error", envelope, id);
                    }
                    catch (OperationCanceledException oce)
                    {
                        metric.Outcome = "cancelled";
                        string exact = oce.Message;
                        silencedByCancellation = AnswerCancellation(
                            reply, envelope, toolName,
                            IsNeverStartedProof(exact) ? exact : CancelledMessage(toolName, clock.ElapsedMilliseconds));
                        ClientToolFinished(toolName, "cancelled", clock.ElapsedMilliseconds, "notice", envelope, id);
                    }
                    catch (Exception ex)
                    {
                        metric.Outcome = "error";
                        Log.Error("'" + toolName + "' threw", ex);
                        reply.TryError(-32603, ex.Message);
                        ClientToolFinished(toolName, "internal_error", clock.ElapsedMilliseconds, "error", envelope, id);
                    }
                    finally
                    {
                        // A request that somehow reached here without answering would leave
                        // the client waiting forever. It cannot happen through the paths
                        // above, and saying so out loud costs one branch.
                        metric.ExecutedMs = clock.ElapsedMilliseconds;
                        metric.TotalMs = queueClock.ElapsedMilliseconds;
                        metric.FinishedUtc = DateTime.UtcNow.ToString("o");
                        Metrics.Record(metric);

                        if (!reply.Answered && !silencedByCancellation)
                        {
                            Log.Error("'" + toolName + "' finished without answering; answering now", null);
                            reply.TryError(-32603,
                                "'" + toolName + "' finished without producing a result or an error. That is a bug " +
                                "in this server. Nothing can be said about whether the work reached Revit.");
                        }
                        // The in-flight slot is released for resource accounting. The
                        // session still remembers the id and refuses lifetime reuse.
                        _inFlight.Finish(key);
                    }
                }
            });
        }

        /// <summary>
        /// One client-visible log line, routed by the protocol the REQUEST declared.
        ///
        /// Modern: only if that request named a level, because its revision has no session
        /// level and reading the legacy one would make this request's logging depend on a
        /// call somebody else made. Legacy: the session minimum, unchanged.
        /// </summary>
        /// <summary>
        /// Whether a successful reply says it did only part of the job.
        ///
        /// READ FROM WHAT THE COMMAND DECLARED, never inferred from the payload's shape.
        /// Two fields carry it today: `coverage_complete`, which a read sets when it
        /// stopped short, and the application-outcome state, which a write sets when some
        /// rows landed and others did not. A guess here would be a measurement of this
        /// file's opinion.
        /// </summary>
        private static string PartialOrOk(JToken result)
        {
            JObject structured = result as JObject;
            if (structured == null) return "ok";

            if ((bool?)structured["coverage_complete"] == false) return "partial";

            string state = (string)structured["state"];
            if (state != null && state.IndexOf("partial", StringComparison.OrdinalIgnoreCase) >= 0)
                return "partial";

            JToken applied = structured["application_outcome"] ?? structured["applied_outcome"];
            string declared = (string)(applied is JObject ? applied["state"] : null);
            if (declared != null && declared.IndexOf("partial", StringComparison.OrdinalIgnoreCase) >= 0)
                return "partial";

            return "ok";
        }

        /// <summary>
        /// Answer a cancelled request the way its protocol says to. Returns true when the
        /// correct answer was to send NOTHING.
        ///
        /// Modern: silence. The revision tells servers not to respond to a cancelled
        /// request and tells clients to ignore a response that arrives anyway, so sending
        /// one is work that produces something the peer is instructed to discard.
        ///
        /// Legacy: -32800 with the message, unchanged. That is what every client installed
        /// today has been receiving, and the message carries the fact that matters most at
        /// that moment - that Revit work already underway cannot be stopped and the model
        /// may still change. The sentence is logged on both paths, so it is never lost.
        /// </summary>
        private static bool AnswerCancellation(ReplySlot reply, Protocol.RequestEnvelope envelope,
                                               string tool, string message)
        {
            bool modern = envelope != null && envelope.Era == Protocol.McpEra.Modern;
            if (!modern)
            {
                reply.TryError(-32800, message);
                return false;
            }

            Log.Warn("'" + tool + "' was cancelled and, per " + Protocol.McpRevision.Latest +
                     ", no response is being sent for it. " + message);
            return true;
        }

        private static void ClientToolFinished(string tool, string outcome, long elapsedMs, string level,
                                               Protocol.RequestEnvelope envelope, object id)
        {
            var data = new JObject
            {
                ["event"] = "tool_finished", ["tool"] = tool,
                ["outcome"] = outcome, ["elapsed_ms"] = elapsedMs
            };
            if (envelope != null && envelope.Era == Protocol.McpEra.Modern)
                McpLogging.EmitForRequest(level, data, _writer.Notify, envelope.LogLevel, id);
            else
                McpLogging.Emit(level, data, _writer.Notify);
        }

        private static void DispatchTaskResult(object id, JObject prms)
        {
            if (_negotiatedProtocol != ProtocolNegotiation.Latest)
            {
                _writer.TryError(id, -32601,
                    "MCP Tasks require negotiated protocol " + ProtocolNegotiation.Latest + ".");
                return;
            }
            string key = OutboundWriter.Key(id);
            var cts = new CancellationTokenSource();
            string refusal;
            if (!_inFlight.TryStart(key, "tasks/result", cts, out refusal, CommandTimeoutMs))
            {
                cts.Dispose();
                _writer.TryError(id, -32600, refusal);
                return;
            }
            ReplySlot reply = _writer.Slot(id);
            JToken progressToken = prms?["_meta"]?["progressToken"];
            Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using (var heartbeat = StartHeartbeat(progressToken, "tasks/result", clock, cts.Token,
                                                       (string)prms?["taskId"]))
                {
                    try { reply.TryReply(McpTasks.WaitResult(prms, CallTool, cts.Token)); }
                    catch (McpError me) { reply.TryError(me.Code, me.Message); }
                    catch (OperationCanceledException) { reply.TryError(-32800, "tasks/result was cancelled."); }
                    catch (Exception ex)
                    {
                        Log.Error("tasks/result threw", ex);
                        reply.TryError(-32603, ex.Message);
                    }
                    finally
                    {
                        if (!reply.Answered) reply.TryError(-32603, "tasks/result finished without an answer.");
                        _inFlight.Finish(key);
                    }
                }
            });
        }

        /// <summary>
        /// subscriptions/listen: open a stream and DO NOT ANSWER until it ends.
        ///
        /// 2026-07-28 models this as an ordinary request whose response is a long-lived
        /// stream of notifications. On stdio there is one stream and everything shares
        /// it, which is exactly why each notification carries the subscription's id. So
        /// the request is registered, parked off the reader thread, and answered once -
        /// when the client cancels it, or when the process is shutting down and every
        /// in-flight request is cancelled.
        ///
        /// It is registered in the in-flight table with a ZERO drain deadline on purpose.
        /// Shutdown waits for work that can still produce an answer; a subscription's
        /// answer is "the stream ended", which shutdown itself causes. Holding the drain
        /// open for it would mean a server that never exits while anyone is listening.
        /// </summary>
        private static void DispatchSubscription(object id, JObject prms, Protocol.RequestEnvelope envelope)
        {
            if (envelope == null || envelope.Era != Protocol.McpEra.Modern)
            {
                _writer.TryError(id, -32601,
                    "subscriptions/listen is part of protocol " + Protocol.McpRevision.Latest + ", and this " +
                    "request did not declare that revision. Under the protocol you are speaking, list changes " +
                    "arrive as unsolicited notifications/*/list_changed and need no subscription.");
                return;
            }

            Protocol.SubscriptionFilter requested;
            try { requested = Protocol.SubscriptionStream.Read(prms); }
            catch (McpError me) { _writer.TryError(id, me.Code, me.Message); return; }

            string key = OutboundWriter.Key(id);
            var cts = new CancellationTokenSource();
            string refusal;
            if (!_inFlight.TryStart(key, "subscriptions/listen", cts, out refusal, 0))
            {
                cts.Dispose();
                _writer.TryError(id, -32600, refusal);
                return;
            }

            ReplySlot reply = _writer.Slot(id);

            // REGISTERED UNACKNOWLEDGED, then acknowledged, then the listener starts. That
            // order is the protocol's requirement that the acknowledgement be the first
            // message on this subscription, and it is held by SubscriptionStream's own lock
            // rather than by the order of these three lines alone.
            //
            // The ID handed over is the REQUEST'S id, not `key`. `key` is this server's
            // dedup identity ("Int64:1") and publishing it as the subscription id meant a
            // client that sent 1 and looked for 1 matched nothing.
            Protocol.Subscription subscription = Protocol.SubscriptionStream.Register(
                key, id == null ? null : Newtonsoft.Json.Linq.JToken.FromObject(id), requested);
            try
            {
                Protocol.SubscriptionStream.Acknowledge(subscription, _writer.Notify);
            }
            catch (Exception ex)
            {
                // NOTHING IS DELIVERED WITHOUT THE ACKNOWLEDGEMENT. A subscription that
                // could not be acknowledged is closed here rather than left open and
                // silent, which is the state the acknowledgement exists to rule out.
                Protocol.SubscriptionStream.Release(key);
                _inFlight.Finish(key);
                cts.Dispose();
                _writer.TryError(id, -32603,
                    "the subscription was accepted and its acknowledgement could not be written (" + ex.Message +
                    "), so it was closed. Nothing was delivered under it. Open another subscriptions/listen.");
                return;
            }

            Log.Info("subscriptions/listen opened for " +
                     subscription.Honoured.Json().ToString(Newtonsoft.Json.Formatting.None));

            // A DEDICATED THREAD, not the pool. This one blocks for as long as the
            // subscription lives - minutes, hours - and a pool thread parked that long is
            // a pool thread the tool calls cannot have. Background, so it can never keep
            // the process alive past shutdown.
            var listener = new Thread(() =>
            {
                // THE CAUSE IS READ FROM THE SUBSCRIPTION, where whoever decided it wrote
                // it, and BEFORE Release removes the record. Reading a process-wide flag
                // here is what made a client cancellation look like a server teardown when
                // the two happened milliseconds apart.
                string cause = null;

                // ASSIGNED HERE because the finally below reads it, and a variable
                // set in a try and a catch is not definitely assigned in a finally -
                // the project did not compile. The default is a sentence rather than
                // null: this text reaches a client, and "the subscription ended
                // because " followed by nothing explains nothing.
                string reason = "the subscription ended before a cause was recorded";
                try
                {
                    cts.Token.WaitHandle.WaitOne();
                    cause = Protocol.SubscriptionStream.CauseOf(key);
                    reason = ReasonFor(cause);
                }
                catch (Exception ex)
                {
                    cause = Protocol.SubscriptionStream.CauseOf(key);
                    reason = "the subscription ended unexpectedly: " + ex.Message;
                }
                finally
                {
                    // Three endings, three behaviours. See the cancellation and
                    // subscriptions pages of the revision; the rules are not symmetrical.
                    switch (cause)
                    {
                        case Protocol.SubscriptionStream.ClientCancelled:
                            // "Servers receiving cancellation notifications SHOULD ... Not
                            // send a response for the cancelled request." The client has
                            // stopped waiting; a response now is noise it is told to ignore.
                            Log.Info("subscriptions/listen " + key + " cancelled by the client; no response sent");
                            break;

                        case Protocol.SubscriptionStream.TransportLost:
                            // Nothing can be written. Recording a delivery that could not
                            // have happened is worse than recording none.
                            Log.Warn("subscriptions/listen " + key + " ended because the channel is gone; " +
                                     "nothing was sent");
                            break;

                        default:
                            // THE SERVER TORE IT DOWN. Two obligations in this order:
                            // notifications/cancelled is a MUST, the final response a SHOULD.
                            try
                            {
                                Protocol.SubscriptionStream.AnnounceTeardown(subscription, reason, _writer.Notify);
                                reply.TryReply(Protocol.ResultEnvelope.Stamp(
                                    Protocol.SubscriptionStream.Closed(subscription, reason),
                                    Protocol.McpEra.Modern, "subscriptions/listen", prms));
                            }
                            catch (Exception ex)
                            {
                                Log.Warn("the graceful close of " + key + " could not be written: " + ex.Message);
                            }
                            break;
                    }

                    Protocol.SubscriptionStream.Release(key);
                    _inFlight.Finish(key);
                }
            }) { IsBackground = true, Name = "horizun-subscription-" + key };
            listener.Start();
        }

        /// <summary>
        /// One way out for a change notification, serving both eras at once.
        ///
        /// The untagged notification goes out EXACTLY as it always has - that is what a
        /// 2025-11-25 client is listening for, and this must not become conditional on
        /// anybody having subscribed. Modern subscribers additionally receive their own
        /// copy carrying the subscriptionId they can correlate it with.
        /// </summary>
        /// <summary>
        /// Start watching for tool-list changes, once, whichever era asked first.
        ///
        /// It used to hang off notifications/initialized, which is the right moment in a
        /// legacy session and does not exist at all in a modern one - so a 2026-07-28
        /// client would have subscribed to toolsListChanged and then never heard about a
        /// change, which is worse than not offering the subscription. The modern path
        /// starts it on the first request that arrives.
        ///
        /// The live bridge is installed BEFORE the monitor, so the monitor's first
        /// snapshot is the filtered list rather than the unfiltered one - otherwise its
        /// very first comparison reports a change that never happened.
        /// </summary>
        private static void EnsureToolListMonitor()
        {
            if (_toolListMonitor != null) return;
            Tools.LiveBridge = ResolveLiveBridge;

            // THE PROCEDURE EXECUTOR DISPATCHES THROUGH THE SAME ENTRY POINT AS tools/call.
            // Wired here rather than inside ProcedureRun so that file holds no second tool
            // registry and no second permission check: a step is dispatched by the code
            // that dispatches every other call, or it is not dispatched.
            ProcedureRun.Invoker = (call, token) => CallTool(call, token);
            try { _toolListMonitor = new ToolListMonitor(NotifyChange); }
            catch (Exception ex)
            {
                // tools/list still re-reads settings on every call. Only the convenience
                // notification is lost, so startup must not fail over this watcher.
                Log.Warn("dynamic tool-list notifications unavailable: " + ex.Message);
            }
        }

        /// <summary>
        /// One change, routed by the protocol the peer is actually speaking.
        ///
        /// MODERN: delivered to the subscriptions that asked for it, each copy carrying its
        /// own subscription id. A modern client that did not subscribe gets NOTHING, which
        /// is the rule - a server must not send notification types the client has not
        /// explicitly requested - and a modern client that did subscribe gets exactly one
        /// copy, correlated.
        ///
        /// LEGACY: the untagged notification, unchanged, exactly as 2025-11-25 defines it -
        /// unsolicited, uncorrelated, and the only way a client of that revision hears
        /// about a change at all. It is emitted only when a legacy handshake happened on
        /// this channel, which is the one fact that distinguishes the two peers.
        ///
        /// The previous version did both, always. A modern client received an unsolicited
        /// notification it had no way to attribute and, if it had subscribed, a duplicate
        /// of something it had just been sent.
        /// </summary>
        /// <summary>The sentence that goes with a termination cause.</summary>
        private static string ReasonFor(string cause)
        {
            switch (cause)
            {
                case Protocol.SubscriptionStream.ClientCancelled:
                    return "the client cancelled the subscription";
                case Protocol.SubscriptionStream.TransportLost:
                    return "the response channel was lost";
                case Protocol.SubscriptionStream.ServerTornDown:
                    return "the server is shutting down and closed this subscription. This response IS the " +
                           "graceful close: re-send subscriptions/listen after reconnecting, because no " +
                           "subscription state survives a stdio reconnection.";
                default:
                    // NOT "the client cancelled". Nobody recorded a cause, so nothing is
                    // known about who ended it, and guessing the cheerful answer is how a
                    // client learns to distrust the field.
                    return "this subscription ended and nothing recorded why. Treat it as a server-side close: " +
                           "re-send subscriptions/listen if you still want the stream.";
            }
        }

        private static void NotifyChange(string method, JObject parameters)
        {
            string type = Protocol.SubscriptionStream.TypeForMethod(method);
            if (type != null)
            {
                try { Protocol.SubscriptionStream.Publish(type, parameters, _writer.Notify); }
                catch (Exception ex) { Log.Warn("subscription delivery failed: " + ex.Message); }
            }

            // The legacy broadcast. Not conditional on anybody having subscribed - a legacy
            // client cannot subscribe - and not sent to a peer that never handshook.
            if (_legacyHandshake) _writer.Notify(method, parameters);
        }

        /// <summary>
        /// Fallback wording when the bridge could not prove a queued request was removed.
        /// PipeClient supplies a stronger exact message when cancellation wins before start;
        /// this path must preserve uncertainty for work that may already be on the UI thread.
        /// </summary>
        private static string CancelledMessage(string tool, long ms) =>
            "'" + tool + "' was cancelled after " + ms + " ms. IMPORTANT: this stops this server waiting for it; it " +
            "could not prove the request was removed before it started. If the command had already reached Revit, " +
            "it is still running there and will finish - the Revit API offers no way to interrupt a command on its " +
            "UI thread. Do not resend it assuming the model is untouched.";

        private static string CancellationProof(JToken result)
        {
            if ((bool?)result?["isError"] != true) return null;
            string text = (string)result?["content"]?[0]?["text"];
            return IsNeverStartedProof(text) ? text : null;
        }

        private static bool IsNeverStartedProof(string text)
            => !string.IsNullOrEmpty(text) &&
               (text.IndexOf("FIFO queue", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("NEVER STARTED", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// A heartbeat while a call waits for Revit, only when the caller asked for one.
        /// Request-specific observations arrive over a separate authenticated control
        /// connection; until one arrives the state remains unknown. No invented percentage.
        /// </summary>
        private static IDisposable StartHeartbeat(JToken progressToken, string tool,
                                                  System.Diagnostics.Stopwatch clock, CancellationToken ct,
                                                  string relatedTaskId = null, Func<JObject> observation = null)
        {
            if (progressToken == null || progressToken.Type == JTokenType.Null) return new NoHeartbeat();

            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        await Task.Delay(5000, stop.Token).ConfigureAwait(false);
                        if (stop.IsCancellationRequested) break;
                        var progress = new JObject
                        {
                            ["progressToken"] = progressToken.DeepClone(),
                            ["progress"] = clock.ElapsedMilliseconds / 1000,
                            ["message"] = "'" + tool + "' is still waiting for Revit to answer (" +
                                          clock.ElapsedMilliseconds / 1000 + " s). It may be waiting in the FIFO " +
                                          "queue or executing on Revit's UI thread; no request-specific observation " +
                                          "is available yet. No percentage is reported because Revit does not report one."
                        };
                        if (!string.IsNullOrWhiteSpace(relatedTaskId))
                            progress["_meta"] = new JObject
                            {
                                ["io.modelcontextprotocol/related-task"] =
                                    new JObject { ["taskId"] = relatedTaskId }
                            };
                        JObject measured=observation?.Invoke();
                        if(measured!=null)
                        {
                            progress["message"]="'"+tool+"': bridge state="+(string)measured["state"]+", elapsed="+clock.ElapsedMilliseconds/1000+" s; Python runtime="+(string)measured["python_runtime"]?["state"]+". No completion percentage is inferred.";
                            if(!(progress["_meta"] is JObject)) progress["_meta"]=new JObject();
                            progress["_meta"]["horizun_bridge"]=measured.DeepClone();
                        }
                        _writer.Notify("notifications/progress", progress);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn("heartbeat stopped: " + ex.Message); }
            });
            return stop;
        }

        private sealed class NoHeartbeat : IDisposable { public void Dispose() { } }

        private static void HandleCancel(JObject prms)
        {
            JToken req = prms?["requestId"];
            object rid = req is JValue jv ? jv.Value : null;
            string key = OutboundWriter.Key(rid);

            // RECORDED BEFORE THE TOKEN FIRES, so the listener cannot wake to a different
            // answer. Harmless when the key names something that is not a subscription.
            Protocol.SubscriptionStream.MarkTermination(key, Protocol.SubscriptionStream.ClientCancelled);

            if (_inFlight.Cancel(key))
                Log.Warn("cancellation accepted for request " + key +
                         " (queued work will be removed when possible; running Revit work cannot be interrupted)");
            else
                Log.Warn("cancellation for request " + (key ?? "(no id)") + " matched nothing in flight");
        }

        private static JToken Handle(string method, JObject prms, Protocol.RequestEnvelope envelope)
        {
            bool modern = envelope != null && envelope.Era == Protocol.McpEra.Modern;

            switch (method)
            {
                // ---- 2026-07-28: the handshake's replacement -------------------------
                // MUST be implemented, and is also the stdio backward-compatibility
                // probe: a dual-era client sends this first and reads our answer - or our
                // recognisably-modern error - to decide which era to speak.
                case "server/discover":
                    return Protocol.DiscoverHandler.Handle(envelope);

                case "initialize":
                    string negotiatedProtocol = ProtocolNegotiation.Answer(prms?.Value<string>("protocolVersion"));
                    _negotiatedProtocol = negotiatedProtocol;
                    // The peer speaks the handshake dialect. See _legacyHandshake.
                    _legacyHandshake = true;
                    // What the client may be asked, from what IT declared and the
                    // revision agreed - never from what a client "usually" supports.
                    _elicitation = ClientElicitationSupport.FromInitialize(
                        negotiatedProtocol, prms?["capabilities"] as JObject);
                    var capabilities = new JObject
                    {
                        // FROM THE SAME SET AS THE MODERN BLOCK. A legacy client reads
                        // listChanged and decides whether to re-list on notification; a
                        // literal here is how the two eras come to advertise different
                        // facts about the same process.
                        ["tools"] = new JObject
                        {
                            ["listChanged"] =
                                Protocol.SubscriptionStream.Emits(Protocol.SubscriptionStream.ToolsListChanged)
                        },
                        ["resources"] = new JObject
                        {
                            ["subscribe"] = false,
                            ["listChanged"] =
                                Protocol.SubscriptionStream.Emits(Protocol.SubscriptionStream.ResourcesListChanged)
                        },
                        ["prompts"] = new JObject
                        {
                            ["listChanged"] =
                                Protocol.SubscriptionStream.Emits(Protocol.SubscriptionStream.PromptsListChanged)
                        },
                        ["completions"] = new JObject(),
                        ["logging"] = new JObject()
                    };
                    if (negotiatedProtocol == ProtocolNegotiation.Latest)
                        capabilities["tasks"] = new JObject
                        {
                            ["requests"] = new JObject
                            {
                                ["tools"] = new JObject { ["call"] = new JObject() }
                            }
                        };
                    return new JObject
                    {
                        ["protocolVersion"] = negotiatedProtocol,
                        ["capabilities"] = capabilities,
                        ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = ServerVersion },
                        // The protocol's own slot for "how to use this server". A caller
                        // that reads it knows two things it would otherwise learn by
                        // failing: that health comes first because these commands act on
                        // whichever document is active, and that this bridge is
                        // deliberately organisation-neutral, so the standards a delivery
                        // actually needs are not in here and should not be invented.
                        ["instructions"] = ServerInstructions.Text
                    };

                case "notifications/initialized":
                    return null; // notification, no reply

                case "ping":
                    // Removed in 2026-07-28. A modern client asking for it is asking for a
                    // method this revision does not have, and answering anyway would teach
                    // it that the method exists here.
                    if (modern)
                        throw new McpError(-32601,
                            "'ping' was removed in " + Protocol.McpRevision.Latest + ". Liveness is answered by any " +
                            "ordinary request; for the bridge's own health call the horizun_health tool.");
                    return new JObject();

                case "tools/list":
                    // The task-support hint is per era: the legacy field describes the
                    // 2025-11-25 core tasks, and the modern client learns the same thing
                    // from the extension it declared.
                    return new JObject
                    {
                        ["tools"] = Tools.List(modern
                            ? envelope.Declares(Protocol.ExtensionRegistry.Tasks)
                            : _negotiatedProtocol == ProtocolNegotiation.Latest)
                    };

                case "resources/list":
                    return McpResources.List(prms);

                case "resources/read":
                    return McpResources.Read(prms);

                case "prompts/list":
                    return McpPrompts.List(prms);

                case "prompts/get":
                    return McpPrompts.Get(prms);

                case "completion/complete":
                    return McpCompletions.Complete(prms);

                case "resources/templates/list":
                    // No templated resources: every horizun:// URI this server serves is
                    // a fixed one. An empty list is the answer, and it still has to be an
                    // ANSWER - a client that gets "method not found" for a list method the
                    // spec requires cannot tell that apart from a broken server.
                    return new JObject { ["resourceTemplates"] = new JArray() };

                case "logging/setLevel":
                    // Removed in 2026-07-28: the level is a per-request _meta field now, and
                    // this server honours it - see McpLogging.EmitForRequest. The sentence
                    // below is an instruction that works; until this build it named a field
                    // that was parsed and never read.
                    if (modern)
                        throw new McpError(-32601,
                            "'logging/setLevel' was removed in " + Protocol.McpRevision.Latest + ". Set " +
                            "_meta['" + Protocol.RequestEnvelope.LogLevelKey + "'] on the request you want logs " +
                            "for; this server emits none for a request that did not ask. The notifications it " +
                            "emits for such a request carry the request id under _meta['io.horizunhub/requestId'], " +
                            "which is a vendor key because this revision defines no correlation field for logs.");
                    return McpLogging.SetLevel(prms);

                case "tasks/get":
                    // Two spellings of one durable store. The modern one carries the
                    // outcome, because its revision has no blocking tasks/result to
                    // carry it; the legacy one is untouched.
                    if (modern) return Protocol.ModernTasks.Get(prms, envelope);
                    RequireTasksProtocol();
                    return McpTasks.Get(prms);

                case "tasks/update":
                    return Protocol.ModernTasks.Update(prms, envelope);

                case "tasks/cancel":
                    return Protocol.ModernTasks.Cancel(prms, envelope);

                case "tasks/list":
                    if (modern)
                        throw new McpError(-32601,
                            "tasks/list does not exist in the " + Protocol.ExtensionRegistry.Tasks + " extension; " +
                            "it was removed when tasks left the core protocol. Use the taskId you were given.");
                    RequireTasksProtocol();
                    // stdio has no requestor identity to bind persistent task metadata
                    // to. The Tasks security guidance says such receivers SHOULD NOT
                    // expose list, so task ids remain capability URLs with 128 bits of
                    // entropy and only direct get/result access is available.
                    throw new McpError(-32601,
                        "tasks/list is not available on this identity-less stdio transport. Use the taskId returned when the task was created.");

                case "tasks/result":
                    if (modern)
                        throw new McpError(-32601,
                            "tasks/result was removed when tasks became the " + Protocol.ExtensionRegistry.Tasks +
                            " extension. Poll tasks/get: its reply carries the final result once the task is " +
                            "completed, and the error once it has failed.");
                    throw new McpError(-32600,
                        "tasks/result was sent as a notification (no id), so there is nowhere to return its result.");

                case "subscriptions/listen":
                    // A request reaches DispatchSubscription and never gets here. Arriving
                    // here means it came as a notification - no id, so no subscriptionId
                    // to tag anything with and nowhere to report that the stream ended.
                    throw new McpError(-32600,
                        "subscriptions/listen was sent as a notification (no id). The subscription is identified " +
                        "by the request id and the stream ends by answering it, so there is nothing to open. " +
                        "Send it as a request with an id.");

                case "tools/call":
                    // A request reaches DispatchToolCall and never gets here. Arriving here
                    // means it came as a NOTIFICATION - no id, so no way to return a result.
                    // Running it anyway would do the work and throw the answer away, which
                    // for a tool that writes to a model is the worst of both.
                    throw new McpError(-32600,
                        "tools/call was sent as a notification (no id), so there is nowhere to return its result. " +
                        "Nothing was run. Send it as a request with an id.");

                default:
                    throw new McpError(-32601, "Method not found: " + method);
            }
        }

        /// <summary>
        /// The add-in a call would be routed to right now, for the TOOL LIST - never
        /// throwing, and null whenever the answer is not certain. An ambiguous choice
        /// (two live instances, nothing saying which) is unknown, and unknown withholds
        /// nothing: the refusal for that belongs to the call, which can name both.
        /// </summary>
        private static Discovered ResolveLiveBridge()
        {
            try
            {
                TargetSelection t = PipeClient.Target;
                string y = t.Year ?? (t.Pid != null ? null : TargetYear);
                string ambiguity;
                Discovered d = PipeClient.Resolve(y, t.Pid, out ambiguity);
                return ambiguity == null ? d : null;
            }
            catch { return null; }
        }

        private static JToken CallTool(JObject prms, CancellationToken ct) => CallTool(prms,ct,null,null);

        private static JToken CallTool(JObject prms, CancellationToken ct, Action<JObject> observe)
            => CallTool(prms, ct, observe, null);

        /// <summary>
        /// Every tools/call result leaves through here. For a tool whose replies carry
        /// text authored outside this bridge (CommandContract.ExternalContent) the payload
        /// has already been neutralised and marked inside CallToolCore; this adds the same
        /// verdict to the result's _meta and neutralises any text block that was built from
        /// a message rather than a payload. See ContentSafety.cs.
        /// </summary>
        private static JToken CallTool(JObject prms, CancellationToken ct, Action<JObject> observe,
                                       Protocol.RequestEnvelope envelope)
        {
            ContentSafety.Report safety = null;
            JToken result = CallToolCore(prms, ct, observe, envelope, ref safety);
            return safety == null ? result : ContentSafety.Finish(result, safety);
        }

        private static JToken CallToolCore(JObject prms, CancellationToken ct, Action<JObject> observe,
                                           Protocol.RequestEnvelope envelope, ref ContentSafety.Report safety)
        {
            // -32602 is INVALID PARAMS, and each of these is a different way of being
            // invalid. They used to collapse: a missing name became "Unknown tool: ''",
            // and `arguments` of the wrong shape was quietly replaced with {} - so a
            // caller that sent arguments as a string or an array got a call with NO
            // arguments and a plausible answer to a question it never asked.
            JToken nameToken = prms?["name"];
            if (nameToken == null)
                throw new McpError(-32602, "Invalid params: tools/call needs a 'name'. Nothing was run.");
            if (nameToken.Type != JTokenType.String)
                throw new McpError(-32602,
                    "Invalid params: 'name' must be a string, not " + nameToken.Type + ". Nothing was run.");
            string name = (string)nameToken;

            JToken argsToken = prms?["arguments"];
            if (argsToken != null && argsToken.Type != JTokenType.Object && argsToken.Type != JTokenType.Null)
                throw new McpError(-32602,
                    "Invalid params: 'arguments' must be an object, not " + argsToken.Type + ". Nothing was run - " +
                    "this is refused rather than treated as no arguments at all, which would answer a different " +
                    "question from the one you asked.");
            JObject args = argsToken as JObject ?? new JObject();

            ToolDef def = Tools.Find(name);
            if (def == null)
                throw new McpError(-32602, "Unknown tool: '" + name + "'.");

            // A tool that exists but is switched off. Refused here AND at the far end: the
            // two halves ship separately, so neither may be the only gate.
            string disabled = Tools.DisabledReason(name);
            if (disabled != null)
            {
                Log.Warn(name + " refused: disabled by settings");
                return TextResult("Error: " + disabled, true);
            }

            if (prms?["task"] != null)
            {
                // Two spellings, one durable store. A modern client gets the extension's
                // CreateTaskResult shape; a legacy one gets the 2025-11-25 core shape it
                // negotiated. Neither can reach the other's field names.
                if (envelope != null && envelope.Era == Protocol.McpEra.Modern)
                    return Protocol.ModernTasks.CreateResult(prms, CallTool, ct, envelope);
                RequireTasksProtocol();
                return McpTasks.Create(prms, CallTool, ct);
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();

            // From here on every answer - success, refusal or failure - belongs to this tool,
            // so a tool whose replies carry model or file text gets the content verdict.
            if (def.ExternalContent)
                safety = new ContentSafety.Report
                {
                    Origin = def.Host != null ? ContentSafety.OriginExternal : ContentSafety.OriginModel
                };

            // Host-resident tool: answer in this process, never touch Revit. Same result shape
            // as the pipe path — the handler returns the data payload, we wrap it in TextResult.
            if (def.Host != null)
            {
                try
                {
                    JObject data = HostCallRunner.Run(name, token => def.Host(args, token), ct, CommandTimeoutMs);
                    Log.Info(name + " (host) ok in " + clock.ElapsedMilliseconds + " ms");
                    if (safety != null)
                    {
                        ContentSafety.Scrub(data, safety);
                        ContentSafety.Attach(data, safety);
                    }
                    return StructuredResult(data);
                }
                catch (ToolRefusal refusal)
                {
                    // The tool did its job and said no. That is an error to the CALLER, but
                    // it is not a fault here, and logging it with a stack trace buries the
                    // faults that are: a log full of expected refusals is a log nobody reads
                    // on the day something actually breaks.
                    Log.Warn(name + " (host) refused: " + refusal.Message);
                    return refusal.Detail == null
                        ? TextResult("Error: " + refusal.Message, true)
                        : ErrorResult("Error: " + refusal.Message, null, null, refusal.Detail);
                }
                catch (Exception ex)
                {
                    Log.Error(name + " (host) FAILED in " + clock.ElapsedMilliseconds + " ms", ex);
                    return TextResult("Error: " + ex.Message, true);
                }
            }

            // A target chosen with horizun_target wins over the environment variable; with
            // neither, the newest live Revit. Which one it landed on goes in the log, because
            // "the right answer from the wrong model" is the failure nobody notices.
            //
            // ONE snapshot, used for every decision below. This was two separate reads of
            // two separate fields, and horizun_target writes from another thread while
            // calls are in flight - so the pair could be read across a change and this
            // command routed to an instance that was never selected. See TargetSelection.cs.
            TargetSelection target = PipeClient.Target;

            // A pinned INSTANCE ignores the environment's year outright. Passing the env
            // year alongside a pid worked only because Resolve happens to ignore the year
            // when a pid is given; saying it here means the intent survives a change there.
            string year = target.Year ?? (target.Pid != null ? null : TargetYear);
            string ambiguity;
            Discovered d = PipeClient.Resolve(year, target.Pid, out ambiguity);

            // More than one live instance and nothing saying which: refused, not guessed.
            // A command sent to the wrong session is a correct edit to the wrong model.
            if (ambiguity != null)
            {
                Log.Warn(name + " refused: " + ambiguity);
                return TextResult("Error: " + ambiguity, true);
            }

            if (d == null)
            {
                if (target.Pid != null)
                {
                    Log.Warn(name + ": no bridge to route to for the pinned process " + target.Pid);
                    return TextResult(
                        "Error: no Revit with process id " + target.Pid + " is reachable. That instance was pinned " +
                        "explicitly with horizun_target; call horizun_target to see which instances did publish a " +
                        "bridge, or pass year 'auto' to clear the choice.", true);
                }
                Log.Warn(name + ": no bridge to route to" +
                         (string.IsNullOrEmpty(year) ? "" : " for the requested Revit " + year));
                // A Revit that loaded the add-in and refused to start looks exactly like
                // a Revit that is not running. If one said why, say so here.
                string refused = PipeClient.StartupFailures(year);
                return TextResult(
                    (string.IsNullOrEmpty(year)
                        ? "Error: no Revit is reachable. Is Revit running with the Horizun add-in loaded?"
                        : "Error: no Revit " + year + " is reachable. That target was chosen explicitly (" +
                          (target.Year != null ? "horizun_target" : "HORIZUN_REVIT_YEAR") +
                          "); call horizun_target to see which Revit versions did publish a bridge.") + refused, true);
            }

            // A discovery file can outlive the Revit that wrote it (a crash, or a close that
            // never reached OnShutdown). Say that, instead of spending five seconds failing
            // to connect and then reporting something about pipes.
            if (!d.ProcessAlive)
            {
                Log.Warn(name + ": Revit " + d.Year + " (pid " + d.Pid + ") is gone; its discovery file is stale");
                return TextResult(
                    "Error: the Revit " + d.Year + " that published this bridge (process " + d.Pid + ") is no longer " +
                    "running, so there is nothing to talk to. Its discovery file was left behind at " + d.SourceFile +
                    " - Revit did not get to clean up, which usually means it crashed or was killed. Start Revit " +
                    "again; the add-in republishes the file on load.", true);
            }

            // Does the add-in on the other end actually have this command? The two halves
            // ship separately, so a server built today routinely meets a plugin installed
            // months ago. Unchecked, the caller gets "Unknown command", which reads like a
            // bug in the request rather than the truth: these two builds do not match.
            // A file that does not publish a list says nothing, and nothing is not "no".
            // Do the two halves agree about what these commands TAKE? Checking that the
            // name exists is not enough: a schema can gain a required argument, or change
            // what one means, while the name stays put - and the far end then ignores it
            // silently. Both sides hash the shared contract; a mismatch is refused here.
            //
            // A null hash is an add-in from before the contract was shared. That is
            // unknown, not disagreement, so it is not refused - the command-list check
            // below still applies to it.
            if (d.ContractHash != null && d.ContractHash != Horizun.Contracts.Contract.Hash)
            {
                string msg = "Error: this server and the Horizun add-in loaded in Revit " + d.Year + " (version " +
                             (d.AddinVersion ?? "unknown") + ", pid " + d.Pid + ") were built from DIFFERENT command " +
                             "contracts - server " + Horizun.Contracts.Contract.Hash + ", add-in " + d.ContractHash +
                             ". They may disagree about what '" + name + "' takes, and an argument one side does not " +
                             "know about is silently ignored by the other. Nothing was sent. Rebuild and redeploy the " +
                             "add-in for Revit " + d.Year + " (scripts/deploy.ps1 -Year " + d.Year + ", with Revit " +
                             "closed), then restart Revit.";
                Log.Warn(name + " refused: contract hash mismatch (server " + Horizun.Contracts.Contract.Hash +
                         " vs add-in " + d.ContractHash + ")");
                return TextResult(msg, true);
            }

            string legacyRefusal = PipeClient.LegacyContractRefusal(d, def);
            if (legacyRefusal != null)
            {
                Log.Warn(name + " refused: " + legacyRefusal);
                return TextResult("Error: " + legacyRefusal, true);
            }

            if (d.ProtocolVersion != 0 && d.ProtocolVersion != Horizun.Contracts.Contract.ProtocolVersion)
            {
                Log.Warn(name + " refused: protocol " + d.ProtocolVersion + " vs " +
                         Horizun.Contracts.Contract.ProtocolVersion);
                return TextResult("Error: the add-in in Revit " + d.Year + " speaks wire protocol " +
                                  d.ProtocolVersion + " and this server speaks " +
                                  Horizun.Contracts.Contract.ProtocolVersion + ". The shape of the exchange itself " +
                                  "differs. Nothing was sent - redeploy the add-in.", true);
            }

            bool? supported = d.Supports(def.Command);
            if (supported == false)
            {
                string msg = "Error: this server offers '" + name + "', but the Horizun add-in loaded in Revit " +
                             d.Year + " (version " + (d.AddinVersion ?? "unknown") + ") does not have the '" +
                             def.Command + "' command - it is an older build than this server (" + ServerVersion +
                             "). Rebuild and redeploy the add-in for Revit " + d.Year +
                             " (scripts/deploy.ps1 -Year " + d.Year + ", with Revit closed), then restart Revit.";
                Log.Warn(name + " refused: plugin " + d.Year + " v" + (d.AddinVersion ?? "unknown") + " lacks it");
                return TextResult(msg, true);
            }

            JObject reply;
            try
            {
                reply = PipeClient.Send(d, def.Command, args,
                                        def.Command == "horizun_health" ? HealthTimeoutMs : CommandTimeoutMs,
                                        ct,observe);
            }
            catch (Exception ex)
            {
                Log.Error(name + " -> Revit " + d.Year + " (pid " + d.Pid + ") FAILED in " +
                          clock.ElapsedMilliseconds + " ms", ex);
                return ErrorResult("Error talking to Revit: " + ex.Message, null, null,
                    ex.Data["horizun_transport_detail"] as JObject ?? new JObject
                    {
                        ["code"] = "revit_transport_failed", ["tool"] = name,
                        ["exception_type"] = ex.GetType().FullName, ["exception_message"] = ex.Message,
                        ["write_started"] = null, ["changes_applied"] = null, ["transaction_status"] = "unknown"
                    });
            }

            bool ok = reply["success"] != null && reply["success"].Type == JTokenType.Boolean && (bool)reply["success"];
            Log.Info(name + " -> Revit " + d.Year + " (pid " + d.Pid + ") " + (ok ? "ok" : "error") +
                     " in " + clock.ElapsedMilliseconds + " ms" +
                     (supported == null ? " [plugin did not publish its command list]" : ""));

            // MODEL TEXT IS DATA. Neutralise invisible/bidi controls and flag agent-directed
            // phrasing in everything the add-in said, before any of it is rendered.
            if (safety != null)
            {
                safety = ContentSafety.ScrubReply(reply, safety.Origin);
                if (ok) ContentSafety.Attach(reply["data"], safety);
                else if (safety.HasFindings)
                {
                    JObject detail = reply["detail"] as JObject ?? new JObject();
                    detail[ContentSafety.PayloadKey] = safety.ToJson();
                    reply["detail"] = detail;
                }
            }

            if (ok)
            {
                JToken data = reply["data"];

                // BOTH HALVES, IN THE ONE ANSWER THAT EXISTS TO IDENTIFY THEM. health
                // already reports the add-in's version, commit and assembly hash, asked
                // of the add-in rather than guessed - and said nothing about the server
                // that forwarded it, which is the other half of "which build is that?".
                // Two binaries built from the same commit with different uncommitted
                // changes are the same version and different software; this is where a
                // support conversation, or a benchmark run, gets to tell them apart.
                if (def.Command == "horizun_health" && data is JObject health)
                {
                    health["server_provenance"] = Protocol.ProvenanceStamp.Current();
                    // The toolset selection lives in THIS process's environment, which the
                    // add-in cannot see, so the server reports it: which toolsets are active
                    // and how many characters/estimated tokens the tool list costs.
                    health["toolsets"] = ToolsetReport.HealthBlock();
                }

                // model_diff explain: the ISO 19650 gaps come from the SAME validator
                // horizun_project_context runs, which lives in this process, not the add-in.
                if (def.Command == "horizun_model_diff" && data is JObject explained &&
                    (string)explained["operation"] == "explain")
                    explained["iso19650"] = ModelExplainIso.Evaluate(args?.Value<string>("project_context_path"));

                return WithImageIfAny(data, reply["revit_said"],
                                      reply["fallback"] as JObject,
                                      reply["capability_gaps"] as JArray);
            }
            // A failure carries what Revit objected to as well: that is usually the reason.
            // It may ALSO carry the machine-readable fallback signal AND the structured
            // failure diagnostic (an atomic plan's rollback trace), and both have to
            // survive as structure: a client deciding whether to write Python - or whether
            // a rollback actually landed - must branch on a field, not parse this English.
            // `detail` was dropped here once already: the unit tests exercised
            // McpResult.FromPluginReply, this forwarder did not call it, and the live
            // probe caught the difference - the same shipped-unnoticed shape as the
            // success path's verdict, now guarded the same way.
            return ErrorResult("Error: " + (string)reply["error"] + RevitSaidText(reply["revit_said"]),
                               reply["fallback"] as JObject, reply["capability_gaps"] as JArray,
                               reply["detail"] as JObject);
        }

        /// <summary>
        /// If the plugin reported an image it wrote, send the IMAGE, not just its path.
        ///
        /// A path is only useful to something that can open files. The point of capturing
        /// a view is that the caller can look at the model, so the bytes ride back in the
        /// response as an MCP image block. The file is read here rather than shipped
        /// through the pipe as base64: server and plugin are on the same machine, and a
        /// few megabytes of base64 have no business crossing a request channel.
        ///
        /// If the file named in the payload is not there, the text still goes out and the
        /// caller is told the image could not be attached - never a silent absence.
        /// </summary>
        /// <summary>
        /// What Revit raised, rendered for a human, or nothing at all. Never folded into
        /// the data: a caller reading the payload must not have to know it exists to be
        /// told that Revit objected while the work was being done.
        /// </summary>
        private static string RevitSaidText(JToken said)
        {
            if (said == null || said.Type == JTokenType.Null) return "";
            return Environment.NewLine + Environment.NewLine +
                   "--- what Revit raised while this ran ---" + Environment.NewLine +
                   said.ToString(Formatting.Indented);
        }

        private static JObject WithImageIfAny(JToken data, JToken said = null,
                                              JObject fallback = null, JArray capabilityGaps = null)
        {
            string text = (data == null ? "null" : data.ToString(Formatting.Indented)) + RevitSaidText(said);
            // THE SUCCESS PATH CARRIES THE VERDICT TOO. The helper is the production
            // attachment path and is internal so its missing/oversized-file behavior can
            // be exercised without starting an MCP process.
            return McpResult.AttachImageIfAny(data, text, fallback, capabilityGaps);
        }

        private static JObject TextResult(string text, bool isError) => McpResult.Text(text, isError);

        /// <summary>
        /// A failure that may carry the fallback signal. The text keeps the human reason;
        /// the signal ALSO travels as structuredContent, because a client that has to read
        /// prose to decide whether it may generate Python is exactly the fragile arrangement
        /// the signal exists to replace. No fallback block means an ordinary text error,
        /// byte for byte as before.
        /// </summary>
        private static JObject ErrorResult(string text, JObject fallback, JArray capabilityGaps,
                                           JObject detail = null)
            => McpResult.Error(text, fallback, capabilityGaps, detail);

        private static JObject StructuredResult(JObject data)
            => StructuredResult((JToken)data, data == null ? "null" : data.ToString(Formatting.Indented));

        private static JObject StructuredResult(JToken data, string text) => McpResult.Structured(data, text);

        private static JObject Reply(object id, JToken result)
            => new JObject { ["jsonrpc"] = "2.0", ["id"] = id == null ? null : JToken.FromObject(id), ["result"] = result };

        private static JObject ErrorReply(object id, int code, string message)
            => new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id == null ? null : JToken.FromObject(id),
                ["error"] = new JObject { ["code"] = code, ["message"] = message }
            };

        private static void RequireTasksProtocol()
        {
            if (_negotiatedProtocol != ProtocolNegotiation.Latest)
                throw new McpError(-32601,
                    "MCP Tasks require negotiated protocol " + ProtocolNegotiation.Latest + ".");
        }

    }
}
