// -----------------------------------------------------------------------------
// Horizun Server tests - a write that did not happen must not be counted, and
// must not be reported as an answer.
//
// THE DEFECT, in five lines of Wire.cs:
//
//     try { _out.WriteLine(...); }
//     catch { /* the client is gone; the reader will notice */ }
//     _written++;
//     return true;
//
// Three separate untruths. The counter advanced for a message nobody received.
// The return value said "answered", and ReplySlot had ALREADY claimed its
// one-shot latch by then, so no other path would ever try again - the request
// was permanently, silently unanswered. And the comment's premise is wrong:
// stdout and stdin are different pipes. A client that stops reading its end of
// stdout does not close stdin, so "the reader will notice" describes a coupling
// that does not exist. The observed shape is a server that goes on accepting
// tool calls, executing MUTATIONS against a live model, and posting each result
// into a pipe that is not there.
//
// So: Write returns false, the counter does not move, and losing the response
// channel starts an orderly shutdown - because a bridge that cannot answer must
// stop being asked, above all for calls that change somebody's model.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class OutboundWriterFailureTests
    {
        /// <summary>A stdout that fails the way a closed client pipe fails.</summary>
        private sealed class BrokenWriter : TextWriter
        {
            private readonly bool _failWrite;
            private readonly bool _failFlush;
            public int WriteAttempts;
            public int FlushAttempts;

            internal BrokenWriter(bool failWrite = true, bool failFlush = false)
            {
                _failWrite = failWrite;
                _failFlush = failFlush;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void WriteLine(string value)
            {
                WriteAttempts++;
                if (_failWrite) throw new IOException("The pipe is being closed.");
            }

            public override void Flush()
            {
                FlushAttempts++;
                if (_failFlush) throw new IOException("The pipe is being closed.");
            }
        }

        private static JObject Message() => new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = new JObject() };

        [Fact]
        public void A_failed_write_returns_false()
        {
            var writer = new OutboundWriter(new BrokenWriter());
            Assert.False(writer.Write(Message()));
        }

        [Fact]
        public void A_failed_write_does_not_advance_the_delivered_count()
        {
            var writer = new OutboundWriter(new BrokenWriter());
            Assert.Equal(0, writer.WrittenCount);

            writer.Write(Message());

            Assert.Equal(0, writer.WrittenCount);
        }

        /// <summary>
        /// AutoFlush is on in production, so WriteLine is where a broken pipe usually
        /// surfaces - but a writer that accepts the line and fails to flush it has
        /// delivered exactly as little, and must be treated the same.
        /// </summary>
        [Fact]
        public void A_failed_flush_is_a_failed_write()
        {
            var writer = new OutboundWriter(new BrokenWriter(failWrite: false, failFlush: true));

            Assert.False(writer.Write(Message()));
            Assert.Equal(0, writer.WrittenCount);
        }

        [Fact]
        public void A_successful_write_still_counts_and_returns_true()
        {
            var sink = new StringWriter();
            var writer = new OutboundWriter(sink);

            Assert.True(writer.Write(Message()));
            Assert.Equal(1, writer.WrittenCount);
            Assert.Contains("\"jsonrpc\"", sink.ToString(), StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------
        // Losing the channel is a terminal condition, and it is announced.
        // -----------------------------------------------------------------

        [Fact]
        public void Losing_the_channel_is_announced_exactly_once()
        {
            var reasons = new List<string>();
            var writer = new OutboundWriter(new BrokenWriter(), reason => reasons.Add(reason));

            writer.Write(Message());
            writer.Write(Message());
            writer.Write(Message());

            Assert.Single(reasons);
            Assert.Contains("pipe", reasons[0], StringComparison.OrdinalIgnoreCase);
            Assert.True(writer.ChannelIsLost);
        }

        /// <summary>
        /// Once the channel is gone, later writes do not even try. Not an optimisation:
        /// the shutdown path answers everything still outstanding, and each of those
        /// would otherwise throw again and re-announce a loss already being handled.
        /// </summary>
        [Fact]
        public void After_the_channel_is_lost_no_further_write_is_attempted()
        {
            var broken = new BrokenWriter();
            var writer = new OutboundWriter(broken);

            writer.Write(Message());
            int attemptsAfterFirst = broken.WriteAttempts;
            writer.Write(Message());
            writer.Write(Message());

            Assert.Equal(1, attemptsAfterFirst);
            Assert.Equal(1, broken.WriteAttempts);
        }

        /// <summary>
        /// The reason is kept, because "why did the server stop?" is the first question
        /// afterwards and the exception is the only thing that knows.
        /// </summary>
        [Fact]
        public void The_reason_the_channel_was_lost_is_kept()
        {
            var writer = new OutboundWriter(new BrokenWriter());
            writer.Write(Message());

            Assert.NotNull(writer.ChannelLostReason);
            Assert.Contains("The pipe is being closed.", writer.ChannelLostReason, StringComparison.Ordinal);
        }

        /// <summary>
        /// TryReply and TryError report the same truth as Write. A caller that branches
        /// on them - and ReplySlot does - must not be told an unsent message was sent.
        /// </summary>
        [Fact]
        public void TryReply_and_TryError_report_the_failure_too()
        {
            var writer = new OutboundWriter(new BrokenWriter());

            Assert.False(writer.TryReply(1, new JObject()));

            var second = new OutboundWriter(new BrokenWriter());
            Assert.False(second.TryError(2, -32603, "boom"));
        }

        /// <summary>
        /// The one-shot latch is the reason this matters so much: it is claimed before
        /// the write, so if the write silently "succeeds" nothing ever answers that
        /// request again. The slot must pass the real outcome through.
        /// </summary>
        [Fact]
        public void A_reply_slot_reports_a_failed_write_rather_than_a_phantom_answer()
        {
            var writer = new OutboundWriter(new BrokenWriter());
            ReplySlot slot = writer.Slot(7);

            Assert.False(slot.TryReply(new JObject()));
        }
    }
}
