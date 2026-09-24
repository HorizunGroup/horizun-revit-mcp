using System;
using Horizun.Server;
using Horizun.Server.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>
    /// The two defects traced from server.log on 2026-09-24.
    ///
    /// 1. 132 "parse error answered" entries with nothing in them to say where the line
    ///    came from. Every one was this suite's own "{this is not json" and
    ///    "{not json at all" (JsonRpcErrorCodeTests), paired within 200 ms, next to
    ///    the same process's other deliberate refusals. The diagnosis now carries the
    ///    position, a hint and a masked shape - and still never the content.
    /// 2. Ten "Cancelled while waiting for Revit to answer 'horizun_create_elements'"
    ///    ERRORs, every one verify-live W13 case 11 (request id 770002) cancelling a
    ///    queued write on purpose. The retry verdict is now named.
    /// </summary>
    public class ParseErrorAndCancellationAdviceTests
    {
        private static ParseErrorDiagnosis Diagnose(string line)
        {
            try { JObject.Parse(line); }
            catch (Exception ex) { return ParseErrorDiagnosis.Of(line, ex); }
            throw new Xunit.Sdk.XunitException("expected the line to fail to parse: " + line);
        }

        [Theory]
        // The exact two lines behind all 132 logged entries: offsets 6 and 5.
        [InlineData("{this is not json", 6, ParseErrorDiagnosis.HintBareWord)]
        [InlineData("{not json at all", 5, ParseErrorDiagnosis.HintBareWord)]
        public void The_logged_lines_reproduce_with_their_exact_position(string line, int offset, string hint)
        {
            ParseErrorDiagnosis d = Diagnose(line);
            Assert.Equal(offset, d.Offset);
            Assert.Equal(1, d.Line);
            Assert.Equal(hint, d.Hint);
            Assert.Equal(line.Length, d.Length);
            // The caret points at the character the parser refused, in the masked shape.
            Assert.Equal('a', d.Shape[d.ShapeCaret]);
        }

        [Fact]
        public void An_unescaped_windows_path_is_named_and_not_echoed()
        {
            string line = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"x\"," +
                          "\"arguments\":{\"path\":\"C:\\hz-live\\secret-model.rvt\"}}}";
            ParseErrorDiagnosis d = Diagnose(line);
            Assert.Equal(ParseErrorDiagnosis.HintUnescapedBackslash, d.Hint);
            string all = d.Message() + d.ToJson() + d.LogLine("wire-tests");
            Assert.DoesNotContain("secret", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hz-live", all, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("path", d.Reason, StringComparison.OrdinalIgnoreCase);
            // The structure that decides parsing survives the mask: the backslash is there.
            Assert.Contains("\\", d.Shape);
            Assert.Contains("ConvertTo-Json", d.Message());
        }

        [Fact]
        public void Two_messages_on_one_line_are_named()
        {
            ParseErrorDiagnosis d = Diagnose("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}");
            Assert.Equal(ParseErrorDiagnosis.HintConcatenated, d.Hint);
        }

        [Fact]
        public void A_message_cut_by_a_raw_newline_is_named()
        {
            // What the reader sees when a string value carried an unescaped line break:
            // the first half ends inside the string.
            ParseErrorDiagnosis d = Diagnose("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"code\":\"import x");
            Assert.Equal(ParseErrorDiagnosis.HintTruncated, d.Hint);
            Assert.Contains("\\n", d.Advice());
        }

        [Fact]
        public void A_byte_order_mark_and_a_non_json_line_are_named()
        {
            Assert.Equal(ParseErrorDiagnosis.HintByteOrderMark, Diagnose("\uFEFF{\"a\" 1}").Hint);
            Assert.Equal(ParseErrorDiagnosis.HintNotJson, Diagnose("Content-Length: 42").Hint);
        }

        [Fact]
        public void The_mask_keeps_json_punctuation_and_hides_every_letter_and_digit()
        {
            int caret;
            string shape = ParseErrorDiagnosis.MaskedWindow("{\"k\":\"Ab9é\\q\"}", 9, out caret);
            Assert.Equal("{\"a\":\"aa0?\\a\"}", shape);
            Assert.Equal(9, caret);
            foreach (char c in shape) Assert.False(char.IsDigit(c) && c != '0');
        }

        [Fact]
        public void A_long_line_is_windowed_around_the_failure()
        {
            string line = "{\"a\":\"" + new string('x', 500) + "\" bad }";
            ParseErrorDiagnosis d = Diagnose(line);
            Assert.True(d.Shape.Length <= ParseErrorDiagnosis.WindowBefore + ParseErrorDiagnosis.WindowAfter + 6);
            Assert.StartsWith("...", d.Shape);
            Assert.Equal(d.Offset, line.IndexOf(" bad", StringComparison.Ordinal) + 1);
        }

        [Fact]
        public void The_reply_data_is_machine_readable()
        {
            JObject j = Diagnose("{this is not json").ToJson();
            Assert.Equal("bare_word", (string)j["hint"]);
            Assert.Equal(6, (int)j["offset"]);
            Assert.False((bool)j["content_echoed"]);
        }

        [Theory]
        [InlineData("wire-tests", "wire-tests")]
        [InlineData("claude-code 2.1", "claude-code 2.1")]
        [InlineData("a\"b\\c\nd", "a_b_c_d")]
        [InlineData("   ", null)]
        public void Client_names_are_reduced_to_a_safe_token(string input, string expected)
        {
            Assert.Equal(expected, ParseErrorDiagnosis.SafeClientName(new JValue(input)));
        }

        [Fact]
        public void Client_name_is_capped_and_ignores_non_strings()
        {
            Assert.Equal(40, ParseErrorDiagnosis.SafeClientName(new JValue(new string('z', 90))).Length);
            Assert.Null(ParseErrorDiagnosis.SafeClientName(new JValue(7)));
            Assert.Null(ParseErrorDiagnosis.SafeClientName(null));
        }

        // ---- cancellation / timeout retry verdicts --------------------------------

        [Fact]
        public void Nothing_started_means_the_same_key_runs_fresh()
        {
            Assert.Equal(CancellationAdvice.SameKeyRunsFresh, CancellationAdvice.Classify(true, true));
            Assert.Equal(CancellationAdvice.SameKeyRunsFresh, CancellationAdvice.Classify(true, false));
        }

        [Fact]
        public void Possibly_started_with_a_key_means_replay_never_a_new_key()
        {
            string v = CancellationAdvice.Classify(false, true);
            Assert.Equal(CancellationAdvice.SameKeyReplays, v);
            string s = CancellationAdvice.Sentence(v, 0, 1000);
            Assert.Contains("SAME idempotency_key", s);
            Assert.Contains("NEW key would be a second write", s);
        }

        [Fact]
        public void Possibly_started_without_a_key_means_inspect_first()
        {
            string v = CancellationAdvice.Classify(false, false);
            Assert.Equal(CancellationAdvice.InspectModelFirst, v);
            Assert.False((bool)CancellationAdvice.ToJson(v, false, 0, 10)["reuse_same_idempotency_key"]);
        }

        [Theory]
        [InlineData(CancellationAdvice.SameKeyReplays)]
        [InlineData(CancellationAdvice.InspectModelFirst)]
        public void An_uncertain_verdict_never_carries_the_never_started_proof_words(string verdict)
        {
            // Program.IsNeverStartedProof keys on these phrases; an uncertain outcome must
            // not be upgraded to a proof by the advice appended to it.
            string s = CancellationAdvice.Sentence(verdict, 900, 120000);
            Assert.DoesNotContain("FIFO queue", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NEVER STARTED", s, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_long_wait_recommends_submit_job_and_a_short_one_does_not()
        {
            var args = new JObject { ["idempotency_key"] = "k", ["elements"] = new JArray(new JObject(), new JObject(), new JObject()) };
            Assert.Equal(3, CancellationAdvice.BatchSize(args));
            string v = CancellationAdvice.Classify(false, CancellationAdvice.HasKey(args));

            string longWait = CancellationAdvice.Sentence(v, 3, CancellationAdvice.LongWaitMs);
            Assert.Contains("horizun_submit_job", longWait);
            Assert.Contains("batch of 3 items", longWait);
            Assert.Equal("horizun_submit_job", (string)CancellationAdvice.ToJson(v, true, 3, CancellationAdvice.LongWaitMs)["prefer"]);

            // The ten logged cancellations waited 10-170 ms: no such advice for them.
            Assert.DoesNotContain("submit_job", CancellationAdvice.Sentence(v, 3, 170));
            Assert.Null(CancellationAdvice.ToJson(v, true, 3, 170)["prefer"]);
        }

        [Fact]
        public void A_blank_or_non_string_key_is_no_key()
        {
            Assert.False(CancellationAdvice.HasKey(new JObject { ["idempotency_key"] = " " }));
            Assert.False(CancellationAdvice.HasKey(new JObject { ["idempotency_key"] = 5 }));
            Assert.False(CancellationAdvice.HasKey(null));
            Assert.True(CancellationAdvice.HasKey(new JObject { ["idempotency_key"] = "abc" }));
        }
    }
}
