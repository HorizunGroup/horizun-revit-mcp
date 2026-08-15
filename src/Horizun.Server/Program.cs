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
                try { _inFlight.CancelAll(); } catch (Exception ex) { Log.Warn("cancel-all after channel loss: " + ex.Message); }
            });

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

                    string method = msg["method"] is JValue mv ? mv.Value as string : null;
                    if (method == null)
                    {
                        // A reply to something we never sent, or a malformed request. The
                        // difference matters: one is noise, the other deserves an answer.
                        if (msg["method"] != null && !isNotification)
                            _writer.TryError(id, -32600, "Invalid request: 'method' must be a string.");
                        continue;
                    }

                    string lifecycleError;
                    if (!_session.Allows(method, isNotification, out lifecycleError))
                    {
                        Log.Warn("message refused by MCP lifecycle: " + lifecycleError);
                        if (!isNotification) _writer.TryError(id, -32600, lifecycleError);
                        continue;
                    }

                    JObject prms = msg["params"] as JObject;

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
                        DispatchToolCall(id, prms);
                        continue;
                    }

                    try
                    {
                        JToken result = Handle(method, prms);
                        if (!isNotification && result != null)
                        {
                            bool delivered = _writer.TryReply(id, result);
                            if (delivered && method == "initialize") _session.InitializeAnswerDelivered();
                        }
                        else if (isNotification && method == "notifications/initialized")
                            _session.InitializedNotificationAccepted();
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
            _inFlight.CancelAll();

            Log.Info("server down (stdin closed)");
            return 0;
        }

        /// <summary>
        /// Run a tool OFF the reader thread, so the next request can be read and answered
        /// while this one waits on Revit. Every exit path answers exactly once - the writer
        /// enforces that, because a timeout and a completion can both arrive.
        /// </summary>
        private static void DispatchToolCall(object id, JObject prms)
        {
            string key = OutboundWriter.Key(id);
            string toolName = (string)prms?["name"] ?? "(unnamed)";
            var cts = new CancellationTokenSource();

            string refusal;
            // The deadline this request will be drained against on shutdown - the same
            // limit the call itself is held to, so the two cannot drift apart.
            if (!_inFlight.TryStart(key, toolName, cts, out refusal, CommandTimeoutMs))
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

            Task.Run(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using (var heartbeat = StartHeartbeat(progressToken, toolName, clock, cts.Token))
                {
                    try
                    {
                        JToken result = CallTool(prms, cts.Token);
                        if (cts.IsCancellationRequested)
                        {
                            // Two events race after cancellation: PipeClient may observe
                            // the token first and throw, or the add-in may remove the FIFO
                            // entry and return its failure envelope first. Preserve the
                            // stronger NEVER STARTED proof in either ordering.
                            string proof = CancellationProof(result);
                            reply.TryError(-32800, proof ?? CancelledMessage(toolName, clock.ElapsedMilliseconds));
                        }
                        else
                            reply.TryReply(result);
                    }
                    catch (McpError me) { reply.TryError(me.Code, me.Message); }
                    catch (OperationCanceledException oce)
                    {
                        string exact = oce.Message;
                        reply.TryError(-32800,
                            IsNeverStartedProof(exact)
                                ? exact
                                : CancelledMessage(toolName, clock.ElapsedMilliseconds));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("'" + toolName + "' threw", ex);
                        reply.TryError(-32603, ex.Message);
                    }
                    finally
                    {
                        // A request that somehow reached here without answering would leave
                        // the client waiting forever. It cannot happen through the paths
                        // above, and saying so out loud costs one branch.
                        if (!reply.Answered)
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
        /// It may be queued or executing; the stdio half cannot observe that boundary, so
        /// it names both instead of calling queued work "running". No invented percentage.
        /// </summary>
        private static IDisposable StartHeartbeat(JToken progressToken, string tool,
                                                  System.Diagnostics.Stopwatch clock, CancellationToken ct)
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
                        _writer.Notify("notifications/progress", new JObject
                        {
                            ["progressToken"] = progressToken.DeepClone(),
                            ["progress"] = clock.ElapsedMilliseconds / 1000,
                            ["message"] = "'" + tool + "' is still waiting for Revit to answer (" +
                                          clock.ElapsedMilliseconds / 1000 + " s). It may be waiting in the FIFO " +
                                          "queue or executing on Revit's UI thread; this side cannot distinguish " +
                                          "those states. No percentage is reported because Revit does not report one."
                        });
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

            if (_inFlight.Cancel(key))
                Log.Warn("cancellation accepted for request " + key +
                         " (queued work will be removed when possible; running Revit work cannot be interrupted)");
            else
                Log.Warn("cancellation for request " + (key ?? "(no id)") + " matched nothing in flight");
        }

        private static JToken Handle(string method, JObject prms)
        {
            switch (method)
            {
                case "initialize":
                    string negotiatedProtocol = ProtocolNegotiation.Answer(prms?.Value<string>("protocolVersion"));
                    return new JObject
                    {
                        ["protocolVersion"] = negotiatedProtocol,
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
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
                    return new JObject();

                case "tools/list":
                    return new JObject { ["tools"] = Tools.List() };

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

        private static JToken CallTool(JObject prms, CancellationToken ct)
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

            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Host-resident tool: answer in this process, never touch Revit. Same result shape
            // as the pipe path — the handler returns the data payload, we wrap it in TextResult.
            if (def.Host != null)
            {
                try
                {
                    JObject data = def.Host(args);
                    Log.Info(name + " (host) ok in " + clock.ElapsedMilliseconds + " ms");
                    return StructuredResult(data);
                }
                catch (ToolRefusal refusal)
                {
                    // The tool did its job and said no. That is an error to the CALLER, but
                    // it is not a fault here, and logging it with a stack trace buries the
                    // faults that are: a log full of expected refusals is a log nobody reads
                    // on the day something actually breaks.
                    Log.Warn(name + " (host) refused: " + refusal.Message);
                    return TextResult("Error: " + refusal.Message, true);
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
                return TextResult(
                    string.IsNullOrEmpty(year)
                        ? "Error: no Revit is reachable. Is Revit running with the Horizun add-in loaded?"
                        : "Error: no Revit " + year + " is reachable. That target was chosen explicitly (" +
                          (target.Year != null ? "horizun_target" : "HORIZUN_REVIT_YEAR") +
                          "); call horizun_target to see which Revit versions did publish a bridge.", true);
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
                                        ct);
            }
            catch (Exception ex)
            {
                Log.Error(name + " -> Revit " + d.Year + " (pid " + d.Pid + ") FAILED in " +
                          clock.ElapsedMilliseconds + " ms", ex);
                return TextResult("Error talking to Revit: " + ex.Message, true);
            }

            bool ok = reply["success"] != null && reply["success"].Type == JTokenType.Boolean && (bool)reply["success"];
            Log.Info(name + " -> Revit " + d.Year + " (pid " + d.Pid + ") " + (ok ? "ok" : "error") +
                     " in " + clock.ElapsedMilliseconds + " ms" +
                     (supported == null ? " [plugin did not publish its command list]" : ""));

            if (ok)
            {
                JToken data = reply["data"];
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
            string path = data is JObject obj ? (string)obj["image_path"] : null;
            // THE SUCCESS PATH CARRIES THE VERDICT TOO. A rehearsal is a SUCCESS that
            // found a capability gap, and this is the function the forwarder actually
            // calls - McpResult.FromPluginReply is used by the tests, and testing a
            // helper the production path does not call is how this shipped unnoticed.
            if (string.IsNullOrEmpty(path))
                return McpResult.Structured(data, text, fallback, capabilityGaps);

            try
            {
                if (!File.Exists(path))
                    return TextResult(text + "\n\n[the image could not be attached: " + path + " is not there]", false);

                byte[] bytes = File.ReadAllBytes(path);
                string mime = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                              path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";

                return new JObject
                {
                    ["content"] = new JArray
                    {
                        new JObject { ["type"] = "text", ["text"] = text },
                        new JObject
                        {
                            ["type"] = "image",
                            ["data"] = Convert.ToBase64String(bytes),
                            ["mimeType"] = mime
                        }
                    },
                    ["isError"] = false,
                    ["structuredContent"] = data is JObject ? data.DeepClone() : null
                };
            }
            catch (Exception ex)
            {
                return TextResult(text + "\n\n[the image could not be attached: " + ex.Message + "]", false);
            }
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

    }
}
