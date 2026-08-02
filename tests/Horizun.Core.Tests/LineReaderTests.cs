// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The pipe read used StreamReader.ReadLine(), which has no timeout and no length
// limit. Every case below is reachable by a client that is merely broken:
// connect and say nothing, send a line with no newline in it, close mid-request.
// The first one held a thread until Revit closed; the second read until memory
// ran out.
//
// A MemoryStream is enough to prove all of it - no pipe, no Revit.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LineReaderTests
    {
        private static MemoryStream Bytes(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

        [Fact]
        public void A_complete_line_is_read_without_its_newline()
        {
            var r = LineReader.Read(Bytes("{\"command\":\"health\"}\n"), 1024, 5000);

            Assert.True(r.Ok);
            Assert.Equal("{\"command\":\"health\"}", r.Line);
        }

        [Fact]
        public void Carriage_returns_are_not_part_of_the_request()
        {
            var r = LineReader.Read(Bytes("hello\r\n"), 1024, 5000);

            Assert.True(r.Ok);
            Assert.Equal("hello", r.Line);
        }

        [Fact]
        public void Utf8_survives_the_chunk_boundaries()
        {
            // Read in 4 KB chunks, so a multi-byte character can straddle two reads.
            string payload = new string('á', 5000) + "ñ";
            var r = LineReader.Read(Bytes(payload + "\n"), 100000, 5000);

            Assert.True(r.Ok);
            Assert.Equal(payload, r.Line);
        }

        [Fact]
        public void A_peer_that_connects_and_says_nothing_times_out_rather_than_waiting_forever()
        {
            // THE THREAD LEAK. A stream that never yields and never closes.
            var r = LineReader.Read(new NeverEndingStream(), 1024, 300);

            Assert.Equal(LineOutcome.TimedOut, r.Outcome);
            Assert.Contains("nothing was sent", r.Error);
        }

        [Fact]
        public void A_peer_that_stops_mid_request_times_out_and_says_how_much_arrived()
        {
            var r = LineReader.Read(new StallingStream("{\"comm"), 1024, 300);

            Assert.Equal(LineOutcome.TimedOut, r.Outcome);
            Assert.Contains("6 bytes were received", r.Error);
        }

        [Fact]
        public void A_line_with_no_newline_is_refused_at_the_limit_not_read_to_exhaustion()
        {
            var r = LineReader.Read(Bytes(new string('x', 10000)), 100, 5000);

            Assert.Equal(LineOutcome.TooLong, r.Outcome);
            Assert.Equal(100, r.BytesRead);          // stopped AT the limit
            Assert.Contains("exceeded 100 bytes", r.Error);
            Assert.Contains("path", r.Error);        // and says what to do instead
        }

        [Fact]
        public void A_request_exactly_at_the_limit_is_accepted()
        {
            string payload = new string('x', 100);
            var r = LineReader.Read(Bytes(payload + "\n"), 100, 5000);

            Assert.True(r.Ok);
            Assert.Equal(payload, r.Line);
        }

        [Fact]
        public void A_peer_that_closes_without_sending_is_ordinary_and_distinguishable()
        {
            var r = LineReader.Read(Bytes(""), 1024, 5000);

            Assert.Equal(LineOutcome.Closed, r.Outcome);
            Assert.Contains("without sending anything", r.Error);
        }

        [Fact]
        public void A_peer_that_closes_mid_request_is_not_treated_as_a_request()
        {
            // Half a request is not a request: answering it would mean guessing the rest.
            var r = LineReader.Read(Bytes("{\"command\":\"del"), 1024, 5000);

            Assert.Equal(LineOutcome.Closed, r.Outcome);
            Assert.Null(r.Line);
            Assert.Contains("mid-request", r.Error);
        }

        [Fact]
        public void A_stream_that_throws_is_reported_not_swallowed()
        {
            var r = LineReader.Read(new ThrowingStream(), 1024, 5000);

            Assert.Equal(LineOutcome.Failed, r.Outcome);
            Assert.Contains("broken pipe", r.Error);
        }

        [Fact]
        public void Only_the_first_line_is_taken_from_a_pipelined_peer()
        {
            // One request per connection. The second line is not silently executed.
            var r = LineReader.Read(Bytes("first\nsecond\n"), 1024, 5000);

            Assert.True(r.Ok);
            Assert.Equal("first", r.Line);
        }

        // ---- streams that behave badly on purpose ------------------------------

        private sealed class NeverEndingStream : Stream
        {
            public override int Read(byte[] b, int o, int c) { System.Threading.Thread.Sleep(50); return 0; }
            public override Task<int> ReadAsync(byte[] b, int o, int c, System.Threading.CancellationToken t)
                => Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ => 0);
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => 0; public override long Position { get => 0; set { } }
            public override void Flush() { } public override long Seek(long a, SeekOrigin b) => 0;
            public override void SetLength(long v) { } public override void Write(byte[] b, int o, int c) { }
        }

        private sealed class StallingStream : Stream
        {
            private readonly byte[] _first; private bool _sent;
            public StallingStream(string s) { _first = Encoding.UTF8.GetBytes(s); }
            public override Task<int> ReadAsync(byte[] b, int o, int c, System.Threading.CancellationToken t)
            {
                if (_sent) return Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ => 0);
                _sent = true;
                Array.Copy(_first, 0, b, o, _first.Length);
                return Task.FromResult(_first.Length);
            }
            public override int Read(byte[] b, int o, int c) => 0;
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => 0; public override long Position { get => 0; set { } }
            public override void Flush() { } public override long Seek(long a, SeekOrigin b) => 0;
            public override void SetLength(long v) { } public override void Write(byte[] b, int o, int c) { }
        }

        private sealed class ThrowingStream : Stream
        {
            public override Task<int> ReadAsync(byte[] b, int o, int c, System.Threading.CancellationToken t)
                => Task.FromException<int>(new IOException("broken pipe"));
            public override int Read(byte[] b, int o, int c) => throw new IOException("broken pipe");
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => 0; public override long Position { get => 0; set { } }
            public override void Flush() { } public override long Seek(long a, SeekOrigin b) => 0;
            public override void SetLength(long v) { } public override void Write(byte[] b, int o, int c) { }
        }
    }
}
