// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The request channel, bounded while it is being read.
//
// The loop used StreamReader.ReadLine() and checked the length afterwards. The
// check was correct and it never got a chance to run: ReadLine reads to the next
// newline however far away that is, so a client that sends a gigabyte with no
// newline in it - a broken client pumping a file into the wrong pipe, which is how
// this actually happens - allocated a gigabyte before anyone could object, and the
// process died with the user's only bridge to Revit inside it.
//
// So: the limit is enforced WHILE reading, the oversized line is drained rather
// than kept, and the session survives it. That last part is the one worth testing
// hardest - a refusal that leaves the stream halfway through a message turns one
// bad request into a stream of nonsense.
//
// A MemoryStream proves every branch, including the one that used to be fatal.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class BoundedLineReaderTests
    {
        private static BoundedLineReader Over(string text, int max = 64) =>
            new BoundedLineReader(new MemoryStream(Encoding.UTF8.GetBytes(text)), max);

        [Fact]
        public void One_line_comes_back_without_its_newline()
        {
            BoundedLine r = Over("{\"jsonrpc\":\"2.0\"}\n").ReadLine();

            Assert.True(r.Ok);
            Assert.Equal("{\"jsonrpc\":\"2.0\"}", r.Line);
        }

        [Fact]
        public void Lines_come_back_one_at_a_time_in_order()
        {
            BoundedLineReader reader = Over("first\nsecond\nthird\n");

            Assert.Equal("first", reader.ReadLine().Line);
            Assert.Equal("second", reader.ReadLine().Line);
            Assert.Equal("third", reader.ReadLine().Line);
            Assert.Equal(BoundedLineOutcome.EndOfStream, reader.ReadLine().Outcome);
        }

        [Fact]
        public void A_windows_line_ending_is_not_part_of_the_request()
        {
            Assert.Equal("ping", Over("ping\r\n").ReadLine().Line);
        }

        [Fact]
        public void Utf8_survives_the_byte_level_scan()
        {
            // The limit counts BYTES and the line is decoded once at the end. A multi-byte
            // character split across two 16 KB buffer reads must still come back whole.
            string text = "{\"m\":\"" + new string('ñ', 20000) + "\"}";
            BoundedLine r = new BoundedLineReader(
                new MemoryStream(Encoding.UTF8.GetBytes(text + "\n")), 1024 * 1024).ReadLine();

            Assert.True(r.Ok);
            Assert.Equal(text, r.Line);
        }

        [Fact]
        public void An_empty_line_is_a_line_and_not_the_end_of_the_stream()
        {
            // A client that sends a blank line has not disconnected. Treating the two the
            // same would end the session on a stray newline.
            BoundedLineReader reader = Over("\nafter\n");

            BoundedLine blank = reader.ReadLine();
            Assert.True(blank.Ok);
            Assert.Equal("", blank.Line);
            Assert.Equal("after", reader.ReadLine().Line);
        }

        // ---- the byte-order mark -----------------------------------------------

        /// <summary>
        /// FOUND BY DRIVING THE BUILT SERVER, not by any test here - which is the point of
        /// having both. The loop this replaced held a StreamReader, and StreamReader strips
        /// a leading BOM for you, quietly, by default. Reading raw bytes does not, and a
        /// unit test never noticed because a test writes the bytes it means to write.
        ///
        /// A real client does not. .NET's StreamWriter and PowerShell's both emit a UTF-8
        /// BOM ahead of the first line unless told otherwise, and MCP over stdio has no
        /// handshake before that first line. So the FIRST request of the session came back
        /// as a parse error carrying id null - unmatchable to anything the client sent -
        /// and the session was broken from its opening message.
        /// </summary>
        [Fact]
        public void A_byte_order_mark_before_the_first_request_is_not_part_of_it()
        {
            var bytes = new System.Collections.Generic.List<byte> { 0xEF, 0xBB, 0xBF };
            bytes.AddRange(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}\n"));

            BoundedLine r = new BoundedLineReader(new MemoryStream(bytes.ToArray())).ReadLine();

            Assert.True(r.Ok);
            Assert.Equal("{\"jsonrpc\":\"2.0\"}", r.Line);
            JObject.Parse(r.Line);      // the thing that used to fail
        }

        [Fact]
        public void Only_the_first_line_of_the_stream_can_carry_one()
        {
            // A U+FEFF anywhere later is CONTENT, and content is the caller's business.
            // Stripping it everywhere would quietly edit a request.
            var bytes = new System.Collections.Generic.List<byte> { 0xEF, 0xBB, 0xBF };
            bytes.AddRange(Encoding.UTF8.GetBytes("first\n"));
            bytes.AddRange(new byte[] { 0xEF, 0xBB, 0xBF });
            bytes.AddRange(Encoding.UTF8.GetBytes("second\n"));

            var reader = new BoundedLineReader(new MemoryStream(bytes.ToArray()));

            Assert.Equal("first", reader.ReadLine().Line);
            Assert.Equal(((char)0xFEFF) + "second", reader.ReadLine().Line);
        }

        [Fact]
        public void A_stream_with_no_mark_is_untouched()
        {
            Assert.Equal("{\"id\":1}", Over("{\"id\":1}\n").ReadLine().Line);
        }

        [Fact]
        public void A_closed_stream_is_the_end_and_not_an_error()
        {
            BoundedLine r = new BoundedLineReader(new MemoryStream()).ReadLine();

            Assert.Equal(BoundedLineOutcome.EndOfStream, r.Outcome);
            Assert.Null(r.Error);
        }

        [Fact]
        public void A_stream_that_closes_mid_request_says_so()
        {
            // Not the same event as a clean shutdown, and the difference is the first
            // thing anybody asks when a session ends unexpectedly.
            BoundedLine r = Over("{\"half\":").ReadLine();

            Assert.Equal(BoundedLineOutcome.EndOfStream, r.Outcome);
            Assert.Contains("mid-request", r.Error);
            Assert.Equal(8, r.Bytes);
        }

        // ---- the limit, enforced while reading ---------------------------------

        [Fact]
        public void A_line_over_the_limit_is_refused_and_reports_its_true_length()
        {
            BoundedLine r = Over(new string('x', 500) + "\n", max: 64).ReadLine();

            Assert.Equal(BoundedLineOutcome.TooLong, r.Outcome);
            Assert.Null(r.Line);
            Assert.Equal(500, r.Bytes);            // the real size, not the limit
            Assert.Contains("500 bytes", r.Error);
            Assert.Contains("Nothing was done", r.Error);
        }

        /// <summary>
        /// THE ONE THIS EXISTS FOR. After an oversized line the reader must be sitting on
        /// the next request, not halfway through the one it refused. Otherwise a single
        /// bad message becomes a parse error for every message after it.
        /// </summary>
        [Fact]
        public void The_request_after_a_refused_one_is_read_normally()
        {
            BoundedLineReader reader = Over(new string('x', 500) + "\n{\"id\":2}\n", max: 64);

            Assert.Equal(BoundedLineOutcome.TooLong, reader.ReadLine().Outcome);

            BoundedLine next = reader.ReadLine();
            Assert.True(next.Ok);
            Assert.Equal("{\"id\":2}", next.Line);
        }

        [Fact]
        public void Several_oversized_lines_in_a_row_do_not_desynchronise_the_stream()
        {
            string big = new string('x', 300) + "\n";
            BoundedLineReader reader = Over(big + big + "{\"id\":3}\n", max: 64);

            Assert.Equal(BoundedLineOutcome.TooLong, reader.ReadLine().Outcome);
            Assert.Equal(BoundedLineOutcome.TooLong, reader.ReadLine().Outcome);
            Assert.Equal("{\"id\":3}", reader.ReadLine().Line);
        }

        [Fact]
        public void A_line_exactly_at_the_limit_is_accepted()
        {
            // Off by one here means either refusing a legal request or accepting one byte
            // more than the limit says. Both are worth pinning.
            Assert.True(Over(new string('x', 64) + "\n", max: 64).ReadLine().Ok);
            Assert.Equal(BoundedLineOutcome.TooLong, Over(new string('x', 65) + "\n", max: 64).ReadLine().Outcome);
        }

        [Fact]
        public void An_oversized_line_that_never_ends_is_still_refused_at_the_close()
        {
            // No trailing newline: the stream simply stops. The answer must be TooLong,
            // which is the actionable truth, rather than "closed mid-request".
            BoundedLine r = Over(new string('x', 500), max: 64).ReadLine();

            Assert.Equal(BoundedLineOutcome.TooLong, r.Outcome);
            Assert.Equal(500, r.Bytes);
        }

        /// <summary>
        /// The case that used to kill the process: far more data than the limit, with no
        /// newline anywhere. It has to come back as a refusal, and the peak memory has to
        /// stay near the limit rather than near the payload - which is what dropping the
        /// buffer at the moment of refusal buys.
        /// </summary>
        [Fact]
        public void A_payload_far_larger_than_the_limit_is_refused_without_holding_it()
        {
            const int limit = 4 * 1024 * 1024;
            const long payload = 64L * 1024 * 1024;

            long before = GC.GetTotalMemory(true);
            BoundedLine r = new BoundedLineReader(new EndlessStream(payload), limit).ReadLine();
            long after = GC.GetTotalMemory(true);

            Assert.Equal(BoundedLineOutcome.TooLong, r.Outcome);
            Assert.Equal(payload, r.Bytes);
            Assert.True(after - before < 2L * limit,
                        "held " + (after - before) + " bytes for a " + payload + " byte line; the limit is " + limit);
        }

        [Fact]
        public void The_default_limit_is_the_one_both_halves_agreed_on()
        {
            Assert.Equal(Horizun.Contracts.Contract.MaxRequestBytes, BoundedLineReader.DefaultMaxBytes);
            Assert.Equal(4 * 1024 * 1024, BoundedLineReader.DefaultMaxBytes);
        }

        [Fact]
        public void A_reader_needs_a_stream_and_a_positive_limit()
        {
            Assert.Throws<ArgumentNullException>(() => new BoundedLineReader(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedLineReader(new MemoryStream(), 0));
        }

        /// <summary>'x' forever, up to a length, then end. Never allocates the payload.</summary>
        private sealed class EndlessStream : Stream
        {
            private readonly long _length;
            private long _served;

            public EndlessStream(long length) { _length = length; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_served >= _length) return 0;
                int n = (int)Math.Min(count, _length - _served);
                for (int i = 0; i < n; i++) buffer[offset + i] = (byte)'x';
                _served += n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get => _served; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }
}
