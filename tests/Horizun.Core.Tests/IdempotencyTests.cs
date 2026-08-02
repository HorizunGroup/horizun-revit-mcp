// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE LOST REPLY, which is where at-most-once was only half true.
//
// AsyncQueueTests proves a QUEUED entry is claimed exactly once, and that was
// written down as the reason run_async is safe to point at a mutation. It is the
// second half of the story. The first half is the wire: the reply carrying the
// job_id can be lost, and then the caller - correctly, by every retry convention
// there is - sends the request again. Two queue entries. Each claimed exactly
// once. The script runs twice.
//
// Nothing downstream can tell that from two deliberate runs, which is why it has
// to be stopped here rather than detected later.
//
// These tests drive the ledger and the REAL AsyncQueue together, because the
// claim being made is about how many entries end up in that queue.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IdempotencyLedgerTests
    {
        private const int Pid = 4242;
        private const string Doc = "doc-fingerprint-abc";

        private static JObject Request(string code, string key = "k-1") => new JObject
        {
            ["code"] = code,
            ["target_document"] = "A Model.rvt",
            ["run_async"] = true,
            ["idempotency_key"] = key
        };

        private static string Fingerprint(JObject r) =>
            RequestFingerprint.Of(Pid, Doc, (string)r["code"], r, "idempotency_key", "run_async");

        private static void DrainQueue() { while (AsyncQueue.Take() != null) { } }

        /// <summary>
        /// THE HEADLINE. A reply goes missing, the caller retries exactly as it
        /// should, and the script is queued ONCE.
        ///
        /// Fails against the previous commit, where run_async had no key at all and
        /// the second send produced a second entry.
        /// </summary>
        [Fact]
        public void A_lost_reply_and_a_retry_queue_the_script_once()
        {
            DrainQueue();
            var ledger = new IdempotencyLedger();
            JObject request = Request("print('write something')");
            string fp = Fingerprint(request);
            int recordsOpened = 0;

            // --- attempt 1: accepted, queued... and the reply never arrives ---
            IdempotencyDecision first = ledger.Claim("key-A", "horizun_execute_python", fp,
                () => { recordsOpened++; return "job-1"; });
            if (first.IsFresh) AsyncQueue.TryAdd(new AsyncWork { JobId = first.Claim.JobId, Command = "horizun_execute_python" }, out _);

            // --- attempt 2: the caller times out and sends the SAME request again ---
            IdempotencyDecision second = ledger.Claim("key-A", "horizun_execute_python", fp,
                () => { recordsOpened++; return "job-2"; });
            if (second.IsFresh) AsyncQueue.TryAdd(new AsyncWork { JobId = second.Claim.JobId, Command = "horizun_execute_python" }, out _);

            Assert.Equal(IdempotencyOutcome.Fresh, first.Outcome);
            Assert.Equal(IdempotencyOutcome.Replay, second.Outcome);

            // The caller gets the ORIGINAL id back, so its poll finds the real work.
            Assert.Equal("job-1", second.Claim.JobId);
            Assert.Equal(first.Claim.JobId, second.Claim.JobId);

            // ONE record was opened and ONE entry is on the queue. This is the whole
            // property: the script runs once.
            Assert.Equal(1, recordsOpened);
            var queued = new List<AsyncWork>();
            AsyncWork w;
            while ((w = AsyncQueue.Take()) != null) queued.Add(w);
            Assert.Single(queued);
            Assert.Equal("job-1", queued[0].JobId);
        }

        [Fact]
        public void Ten_retries_still_queue_it_once_and_the_replays_are_counted()
        {
            DrainQueue();
            var ledger = new IdempotencyLedger();
            string fp = Fingerprint(Request("x = 1"));
            int queuedCount = 0;

            for (int i = 0; i < 10; i++)
            {
                IdempotencyDecision d = ledger.Claim("key-B", "horizun_execute_python", fp, () => "job-only");
                if (d.IsFresh) { queuedCount++; AsyncQueue.TryAdd(new AsyncWork { JobId = d.Claim.JobId }, out _); }
                Assert.Equal("job-only", d.Claim.JobId);
            }

            Assert.Equal(1, queuedCount);
            Assert.Equal(1, AsyncQueue.Count);
            // Reported so a caller retrying in a loop can SEE that it is, rather than
            // inferring it from a job that never seems to start twice.
            Assert.Equal(9, ledger.Find("key-B").ReplayCount);
            DrainQueue();
        }

        /// <summary>
        /// A key reused for other work must be REFUSED, not quietly honoured. Honouring
        /// it would discard the new request while telling the caller it had been
        /// deduplicated - the worst of the three possible behaviours.
        /// </summary>
        [Fact]
        public void The_same_key_with_a_different_script_is_refused()
        {
            var ledger = new IdempotencyLedger();
            ledger.Claim("key-C", "horizun_execute_python", Fingerprint(Request("delete_walls()")), () => "job-1");

            IdempotencyDecision clash = ledger.Claim("key-C", "horizun_execute_python",
                Fingerprint(Request("delete_everything()")), () => "job-2");

            Assert.Equal(IdempotencyOutcome.Conflict, clash.Outcome);
            Assert.Contains("key-C", clash.Message);
            // The message has to say what to do, or the caller's only option is to guess.
            Assert.Contains("new key", clash.Message);
            Assert.Contains("Nothing was queued", clash.Message);
        }

        [Fact]
        public void A_conflict_does_not_open_a_record_or_disturb_the_original()
        {
            var ledger = new IdempotencyLedger();
            int opened = 0;
            ledger.Claim("key-D", "horizun_execute_python", Fingerprint(Request("a()")),
                () => { opened++; return "job-original"; });

            ledger.Claim("key-D", "horizun_execute_python", Fingerprint(Request("b()")),
                () => { opened++; return "job-second"; });

            Assert.Equal(1, opened);
            // The original claim is untouched: a refused request must not be able to
            // rewrite the answer an earlier caller is polling.
            Assert.Equal("job-original", ledger.Find("key-D").JobId);
            Assert.Equal(0, ledger.Find("key-D").ReplayCount);
        }

        /// <summary>
        /// A retry does not have to arrive after the first request finishes - it
        /// arrives while the first is in flight, which is what a timeout MEANS. A
        /// check-then-act would let both see "not present" and both queue.
        /// </summary>
        [Fact]
        public async Task Concurrent_retries_produce_exactly_one_claim()
        {
            DrainQueue();
            var ledger = new IdempotencyLedger();
            string fp = Fingerprint(Request("slow()"));
            var start = new ManualResetEventSlim(false);
            var outcomes = new ConcurrentBag<IdempotencyOutcome>();
            int opened = 0;

            Task[] callers = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                start.Wait();
                IdempotencyDecision d = ledger.Claim("key-E", "horizun_execute_python", fp,
                    () => "job-" + Interlocked.Increment(ref opened));
                outcomes.Add(d.Outcome);
                if (d.IsFresh) AsyncQueue.TryAdd(new AsyncWork { JobId = d.Claim.JobId }, out _);
            })).ToArray();

            start.Set();
            await Task.WhenAll(callers);

            Assert.Equal(1, outcomes.Count(o => o == IdempotencyOutcome.Fresh));
            Assert.Equal(7, outcomes.Count(o => o == IdempotencyOutcome.Replay));
            Assert.Equal(1, opened);
            Assert.Equal(1, AsyncQueue.Count);
            DrainQueue();
        }

        [Fact]
        public void A_different_key_is_different_work()
        {
            var ledger = new IdempotencyLedger();
            string fp = Fingerprint(Request("same()"));

            IdempotencyDecision a = ledger.Claim("key-F1", "horizun_execute_python", fp, () => "job-1");
            IdempotencyDecision b = ledger.Claim("key-F2", "horizun_execute_python", fp, () => "job-2");

            // Identical scripts under two keys are two deliberate runs. Deduplicating
            // them would make it impossible to run the same script twice on purpose.
            Assert.Equal(IdempotencyOutcome.Fresh, a.Outcome);
            Assert.Equal(IdempotencyOutcome.Fresh, b.Outcome);
            Assert.NotEqual(a.Claim.JobId, b.Claim.JobId);
        }

        [Fact]
        public void An_empty_key_is_rejected_rather_than_treated_as_one_key()
        {
            var ledger = new IdempotencyLedger();
            // "" is not a key. Accepting it would put every keyless caller into one
            // bucket, where the second unrelated script would be silently discarded.
            Assert.Throws<ArgumentException>(() =>
                ledger.Claim("", "horizun_execute_python", "fp", () => "job"));
            Assert.Throws<ArgumentException>(() =>
                ledger.Claim(null, "horizun_execute_python", "fp", () => "job"));
        }
    }

    /// <summary>
    /// What the key is BOUND to. Every field named in the brief is checked by
    /// changing it and requiring the fingerprint to move - a field left out is a
    /// field a caller can change while the guard says "same request", which is
    /// exactly the defect found in family_apply's plan hash.
    /// </summary>
    public class RequestFingerprintTests
    {
        private static JObject Req() => new JObject
        {
            ["code"] = "print(1)",
            ["target_document"] = "A Model.rvt",
            ["run_async"] = true,
            ["idempotency_key"] = "k"
        };

        private static string Of(int pid, string doc, JObject r) =>
            RequestFingerprint.Of(pid, doc, (string)r["code"], r, "idempotency_key", "run_async");

        [Fact]
        public void The_same_request_gives_the_same_fingerprint()
        {
            Assert.Equal(Of(1, "d", Req()), Of(1, "d", Req()));
        }

        [Fact]
        public void The_revit_process_is_part_of_it()
        {
            Assert.NotEqual(Of(1, "d", Req()), Of(2, "d", Req()));
        }

        [Fact]
        public void The_document_identity_is_part_of_it()
        {
            // The same script against another model is another operation. A key that
            // spanned documents would let a retry aimed at model B be answered with
            // model A's job id.
            Assert.NotEqual(Of(1, "doc-a", Req()), Of(1, "doc-b", Req()));
        }

        [Fact]
        public void The_code_is_part_of_it()
        {
            JObject other = Req();
            other["code"] = "print(2)";
            Assert.NotEqual(Of(1, "d", Req()), Of(1, "d", other));
        }

        [Fact]
        public void A_one_character_change_in_a_long_script_moves_it()
        {
            JObject a = Req(), b = Req();
            a["code"] = new string('x', 100000) + "delete(1)";
            b["code"] = new string('x', 100000) + "delete(2)";
            // SHA-256 over the whole source, not a length or a prefix.
            Assert.NotEqual(Of(1, "d", a), Of(1, "d", b));
        }

        [Fact]
        public void Every_other_argument_is_part_of_it()
        {
            JObject a = Req(), b = Req();
            a["target_document"] = "Model A.rvt";
            b["target_document"] = "Model B.rvt";
            Assert.NotEqual(Of(1, "d", a), Of(1, "d", b));

            JObject c = Req();
            c["some_future_option"] = 7;
            Assert.NotEqual(Of(1, "d", Req()), Of(1, "d", c));
        }

        [Fact]
        public void Key_order_in_the_json_does_not_change_it()
        {
            var straight = new JObject { ["code"] = "p()", ["target_document"] = "M.rvt", ["a"] = 1, ["b"] = 2 };
            var shuffled = new JObject { ["b"] = 2, ["target_document"] = "M.rvt", ["a"] = 1, ["code"] = "p()" };

            // Two serialisations of one request. A retry that reordered its keys - or a
            // client that serialises differently from the one that sent the original -
            // must not read as new work.
            Assert.Equal(Of(1, "d", straight), Of(1, "d", shuffled));
        }

        [Fact]
        public void Array_order_does_change_it()
        {
            var a = new JObject { ["code"] = "p()", ["ids"] = new JArray(1, 2, 3) };
            var b = new JObject { ["code"] = "p()", ["ids"] = new JArray(3, 2, 1) };

            // A list of arguments in another order is a different call. Sorting arrays
            // "for stability" would erase a real difference.
            Assert.NotEqual(Of(1, "d", a), Of(1, "d", b));
        }

        [Fact]
        public void The_key_itself_is_not_part_of_what_it_identifies()
        {
            JObject a = Req(), b = Req();
            a["idempotency_key"] = "one";
            b["idempotency_key"] = "two";

            // The key is the handle, not the claim. If it were inside the fingerprint,
            // a retry could never conflict - every reused key would look like new work.
            Assert.Equal(Of(1, "d", a), Of(1, "d", b));
        }

        [Fact]
        public void Nested_objects_are_canonicalised_too()
        {
            var a = new JObject { ["code"] = "p()", ["opts"] = new JObject { ["x"] = 1, ["y"] = 2 } };
            var b = new JObject { ["code"] = "p()", ["opts"] = new JObject { ["y"] = 2, ["x"] = 1 } };
            Assert.Equal(Of(1, "d", a), Of(1, "d", b));

            var c = new JObject { ["code"] = "p()", ["opts"] = new JObject { ["x"] = 1, ["y"] = 99 } };
            Assert.NotEqual(Of(1, "d", a), Of(1, "d", c));
        }

        [Fact]
        public void It_is_a_sha256_of_the_whole_claim_not_a_truncation()
        {
            string fp = Of(1, "d", Req());
            Assert.Equal(64, fp.Length);
            Assert.True(fp.ToLowerInvariant() == fp && System.Text.RegularExpressions.Regex.IsMatch(fp, "^[0-9a-f]{64}$"),
                "expected lowercase hex sha-256, got " + fp);
        }
    }
}
