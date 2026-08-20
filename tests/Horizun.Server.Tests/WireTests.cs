// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Making the server concurrent introduces two ways to corrupt a session that a
// sequential loop could not have:
//
//   two tasks writing to stdout at once -> one mangled line, and a client that
//   disconnects on the spot;
//
//   a timeout path and a completion path both answering -> two responses for one
//   id, which JSON-RPC does not allow and a client will either mis-attribute or
//   choke on.
//
// Both are removed here rather than hoped away, and both are testable with a
// StringWriter and no Revit in sight.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class WireTests
    {
        private static string[] Lines(StringWriter w) =>
            w.ToString().Split(new[] { Environment.NewLine, "\n" }, StringSplitOptions.RemoveEmptyEntries);

        // ---- one reply per REQUEST, not per id ---------------------------------

        [Fact]
        public void A_second_answer_to_the_same_request_is_refused()
        {
            var sw = new StringWriter();
            var w = new OutboundWriter(sw);
            ReplySlot slot = w.Slot(1);

            Assert.True(slot.TryReply(new JObject { ["ok"] = true }));
            Assert.False(slot.TryReply(new JObject { ["ok"] = false }));
            Assert.False(slot.TryError(-32603, "late failure"));

            Assert.Single(Lines(sw));
            Assert.True(slot.Answered);
        }

        /// <summary>
        /// THE ONE THIS CHANGE EXISTS FOR. Two SEPARATE requests that happen to carry the
        /// same id - one after the other, which is what a client with a small pool of ids
        /// does all day - must each get an answer.
        ///
        /// The writer used to remember every id it had ever answered, for the life of the
        /// process. So the second request under id 7 ran in full, touched whatever it was
        /// asked to touch, and then had its reply discarded on the way out: the client sat
        /// waiting forever for an answer to work that had already happened.
        /// </summary>
        [Fact]
        public void Two_requests_that_reuse_one_id_sequentially_both_get_an_answer()
        {
            var sw = new StringWriter();
            var w = new OutboundWriter(sw);

            Assert.True(w.Slot(7).TryReply(new JObject { ["call"] = "first" }));
            Assert.True(w.Slot(7).TryReply(new JObject { ["call"] = "second" }));

            string[] lines = Lines(sw);
            Assert.Equal(2, lines.Length);
            foreach (string line in lines) Assert.Equal(7, (int)JObject.Parse(line)["id"]);
            Assert.Equal("first", (string)JObject.Parse(lines[0])["result"]["call"]);
            Assert.Equal("second", (string)JObject.Parse(lines[1])["result"]["call"]);
        }

        [Fact]
        public void An_id_reused_after_a_failure_still_gets_its_next_answer()
        {
            // The first request under this id ERRORED. That is an answer, and it must not
            // poison the id for the request that comes next.
            var sw = new StringWriter();
            var w = new OutboundWriter(sw);

            Assert.True(w.Slot(7).TryError(-32603, "the first one broke"));
            Assert.True(w.Slot(7).TryReply(new JObject { ["ok"] = true }));

            Assert.Equal(2, Lines(sw).Length);
        }

        // async/await rather than Task.WaitAll + .Result: xUnit1031 flags blocking on a
        // task inside a test because it can deadlock on a synchronization context, and
        // three of those warnings were being carried rather than fixed. The race is
        // unchanged - both tasks still start together on the same signal.
        [Fact]
        public async Task A_timeout_and_a_completion_racing_produce_exactly_one_answer()
        {
            var sw = new StringWriter();
            var w = new OutboundWriter(sw);
            ReplySlot slot = w.Slot(7);
            var start = new ManualResetEventSlim(false);

            var a = Task.Run(() => { start.Wait(); return slot.TryError(-32800, "cancelled"); });
            var b = Task.Run(() => { start.Wait(); return slot.TryReply(new JObject()); });
            start.Set();
            bool[] outcomes = await Task.WhenAll(a, b);

            Assert.True(outcomes[0] ^ outcomes[1]);   // exactly one of them won
            Assert.Single(Lines(sw));
        }

        [Fact]
        public async Task Many_paths_racing_for_one_request_still_write_exactly_one_line()
        {
            var sw = new StringWriter();
            var w = new OutboundWriter(sw);
            ReplySlot slot = w.Slot("scan-1");
            var start = new ManualResetEventSlim(false);

            var racers = new Task<bool>[16];
            for (int i = 0; i < racers.Length; i++)
            {
                int n = i;
                racers[i] = Task.Run(() =>
                {
                    start.Wait();
                    return n % 2 == 0 ? slot.TryReply(new JObject { ["n"] = n })
                                      : slot.TryError(-32603, "path " + n);
                });
            }
            start.Set();
            bool[] outcomes = await Task.WhenAll(racers);

            int winners = 0;
            foreach (bool o in outcomes) if (o) winners++;
            Assert.Equal(1, winners);
            Assert.Single(Lines(sw));
        }

        [Fact]
        public void Numeric_and_string_ids_are_different_requests()
        {
            // JSON-RPC treats 1 and "1" as distinct, and the id is echoed back exactly as
            // it arrived - a client that uses both must be able to tell its answers apart.
            var sw = new StringWriter();
            var w = new OutboundWriter(sw);

            Assert.True(w.Slot(1).TryReply(new JObject()));
            Assert.True(w.Slot("1").TryReply(new JObject()));

            string[] lines = Lines(sw);
            Assert.Equal(2, lines.Length);
            Assert.Equal(JTokenType.Integer, JObject.Parse(lines[0])["id"].Type);
            Assert.Equal(JTokenType.String, JObject.Parse(lines[1])["id"].Type);
        }

        [Fact]
        public void Invalid_requests_answered_with_a_null_id_each_get_an_answer()
        {
            // A null id is not an identity, it is what an unusable request gets answered
            // with. Two malformed messages must both get an answer.
            var sw = new StringWriter();
            var w = new OutboundWriter(sw);

            Assert.True(w.TryError(null, -32600, "first"));
            Assert.True(w.TryError(null, -32600, "second"));

            Assert.Equal(2, Lines(sw).Length);
        }

        // ---- no interleaving ---------------------------------------------------

        [Fact]
        public void Concurrent_writers_never_interleave_a_line()
        {
            var sw = new StringWriter();
            var w = new OutboundWriter(sw);

            Parallel.For(0, 200, i =>
            {
                if (i % 2 == 0) w.Slot(i).TryReply(new JObject { ["payload"] = new string('x', 500) });
                else w.Notify("notifications/progress", new JObject { ["progress"] = i });
            });

            string[] lines = Lines(sw);
            Assert.Equal(200, lines.Length);
            // Every line must be complete JSON on its own. A torn write shows up here.
            foreach (string line in lines) JObject.Parse(line);
        }

        [Fact]
        public void Every_line_is_a_single_json_object_with_the_protocol_version()
        {
            var sw = new StringWriter();
            var w = new OutboundWriter(sw);

            w.Slot("a").TryReply(new JObject { ["x"] = 1 });
            w.Slot("b").TryError(-1, "no");
            w.Notify("notifications/progress", new JObject { ["progress"] = 1 });

            foreach (string line in Lines(sw))
                Assert.Equal("2.0", (string)JObject.Parse(line)["jsonrpc"]);
        }

        [Fact]
        public void A_notification_carries_no_id()
        {
            var sw = new StringWriter();
            new OutboundWriter(sw).Notify("notifications/progress", new JObject { ["progress"] = 3 });

            JObject o = JObject.Parse(Lines(sw)[0]);
            Assert.Null(o["id"]);
            Assert.Equal("notifications/progress", (string)o["method"]);
        }

        // ---- in-flight registry ------------------------------------------------

        [Fact]
        public void A_reused_request_id_is_refused_while_the_first_is_still_running()
        {
            var f = new InFlight();
            string refusal;

            Assert.True(f.TryStart("Int32:1", "horizun_model_scan", new CancellationTokenSource(), out refusal));
            Assert.False(f.TryStart("Int32:1", "horizun_health", new CancellationTokenSource(), out refusal));

            Assert.Contains("already in flight", refusal);
            Assert.Contains("horizun_model_scan", refusal);
            Assert.Contains("Nothing was started", refusal);
        }

        [Fact]
        public void An_id_is_reusable_once_its_request_has_finished()
        {
            var f = new InFlight();
            string refusal;

            f.TryStart("Int32:1", "a", new CancellationTokenSource(), out refusal);
            f.Finish("Int32:1");

            Assert.True(f.TryStart("Int32:1", "b", new CancellationTokenSource(), out refusal));
            Assert.Equal(1, f.Count);
        }

        /// <summary>
        /// The two halves together, in the order Program.DispatchToolCall runs them: claim
        /// the id, answer, release it - twice, under the same id 7. Both requests must
        /// start and both must be answered.
        ///
        /// Neither half alone shows the bug this pins. InFlight always released the id on
        /// Finish; the writer never released it at all, so the second answer was built,
        /// handed over, and dropped. Crossing them is the only place that is visible.
        /// </summary>
        [Fact]
        public void Two_requests_under_the_same_id_one_after_the_other_are_both_served()
        {
            var sw = new StringWriter();
            var w = new OutboundWriter(sw);
            var f = new InFlight();
            const string key = "Int32:7";
            string refusal;

            Assert.True(f.TryStart(key, "horizun_health", new CancellationTokenSource(), out refusal));
            Assert.True(w.Slot(7).TryReply(new JObject { ["call"] = "first" }));
            f.Finish(key);

            Assert.True(f.TryStart(key, "horizun_health", new CancellationTokenSource(), out refusal),
                        "the id was still held after its request finished: " + refusal);
            Assert.True(w.Slot(7).TryReply(new JObject { ["call"] = "second" }));
            f.Finish(key);

            Assert.Equal(2, Lines(sw).Length);
            Assert.Equal(0, f.Count);
        }

        [Fact]
        public void Cancelling_signals_the_token_of_that_request_only()
        {
            var f = new InFlight();
            var one = new CancellationTokenSource();
            var two = new CancellationTokenSource();
            string refusal;
            f.TryStart("Int32:1", "a", one, out refusal);
            f.TryStart("Int32:2", "b", two, out refusal);

            Assert.True(f.Cancel("Int32:1"));

            Assert.True(one.IsCancellationRequested);
            Assert.False(two.IsCancellationRequested);
        }

        [Fact]
        public void Cancelling_something_that_is_not_running_says_so_rather_than_pretending()
        {
            var f = new InFlight();

            Assert.False(f.Cancel("Int32:99"));
            Assert.False(f.Cancel(null));
        }

        [Fact]
        public void A_notification_has_no_id_to_track_and_is_not_refused_as_a_duplicate()
        {
            var f = new InFlight();
            string refusal;

            Assert.True(f.TryStart(null, "a", new CancellationTokenSource(), out refusal));
            Assert.True(f.TryStart(null, "b", new CancellationTokenSource(), out refusal));
            Assert.Equal(0, f.Count);
        }

        [Fact]
        public void A_client_that_disconnects_cancels_everything_outstanding()
        {
            var f = new InFlight();
            var one = new CancellationTokenSource();
            var two = new CancellationTokenSource();
            string refusal;
            f.TryStart("Int32:1", "a", one, out refusal);
            f.TryStart("Int32:2", "b", two, out refusal);

            f.CancelAll();

            Assert.True(one.IsCancellationRequested);
            Assert.True(two.IsCancellationRequested);
            Assert.Contains("'a'", f.Describe());
        }

        [Fact]
        public void An_idle_registry_describes_itself_as_idle()
        {
            Assert.Equal("nothing is running", new InFlight().Describe());
        }

        [Fact]
        public void In_flight_admission_is_bounded_before_tasks_are_spawned()
        {
            var f = new InFlight(2);
            string refusal;
            Assert.True(f.TryStart("1", "one", new CancellationTokenSource(), out refusal));
            Assert.True(f.TryStart("2", "two", new CancellationTokenSource(), out refusal));
            Assert.False(f.TryStart("3", "three", new CancellationTokenSource(), out refusal));
            Assert.Contains("limit 2", refusal);
            Assert.Contains("Nothing was started", refusal);
            Assert.Equal(2, f.Count);
        }

        [Fact]
        public void A_cancelled_host_call_never_starts_its_handler()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            bool entered = false;

            Assert.Throws<OperationCanceledException>(() => HostCallRunner.Run(
                "host", ct => { entered = true; return new JObject(); }, cts.Token, 1000));
            Assert.False(entered);
        }

        [Fact]
        public void A_host_handler_has_a_real_response_deadline_and_receives_cancellation()
        {
            bool observedCancellation = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            Assert.Throws<TimeoutException>(() => HostCallRunner.Run("host", ct =>
            {
                ct.WaitHandle.WaitOne();
                observedCancellation = ct.IsCancellationRequested;
                ct.ThrowIfCancellationRequested();
                return new JObject();
            }, CancellationToken.None, 50));

            Assert.True(SpinWait.SpinUntil(() => observedCancellation, 1000));
            Assert.True(clock.ElapsedMilliseconds < 1000, "deadline did not release the caller promptly");
        }

        [Fact]
        public void Non_cooperative_timed_out_host_handlers_keep_their_leases_and_apply_backpressure()
        {
            using (var release = new ManualResetEventSlim(false))
            {
                int entered = 0;
                for (int i = 0; i < HostCallRunner.MaxOutstandingTasks; i++)
                {
                    Assert.Throws<TimeoutException>(() => HostCallRunner.Run("blocked", ct =>
                    {
                        Interlocked.Increment(ref entered);
                        // Deliberately ignores ct: this models an OS call already in progress.
                        release.Wait();
                        return new JObject();
                    }, CancellationToken.None, 20));
                }

                Assert.Equal(HostCallRunner.MaxOutstandingTasks, entered);
                Assert.Equal(HostCallRunner.MaxOutstandingTasks, HostCallRunner.OutstandingTaskCount);

                bool extraEntered = false;
                ToolRefusal refusal = Assert.Throws<ToolRefusal>(() => HostCallRunner.Run("blocked", ct =>
                {
                    extraEntered = true;
                    return new JObject();
                }, CancellationToken.None, 20));
                Assert.False(extraEntered);
                Assert.Contains("capacity", refusal.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Nothing new was started", refusal.Message);

                release.Set();
                Assert.True(SpinWait.SpinUntil(
                    () => HostCallRunner.OutstandingTaskCount == 0, 3000),
                    "leases were not released when the residual Tasks terminated");
            }
        }
    }
}
