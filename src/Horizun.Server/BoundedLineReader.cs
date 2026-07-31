// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// Reading one request off stdin without letting the client decide how much memory
// this process uses.
//
// The loop used StreamReader.ReadLine() and then checked the length:
//
//     line = stdin.ReadLine();
//     if (line.Length > MaxRequestChars) { refuse }
//
// The check is real and the refusal is correct, and both happen AFTER the entire
// line is already a string in memory. ReadLine has no bound: it reads until it
// finds a newline or the stream ends, so a client that sends a gigabyte with no
// newline in it - a broken client pumping a file into the wrong pipe is the
// ordinary way this happens, not an attack - allocates a gigabyte here first, and
// StringBuilder's doubling means the peak is roughly twice that. The process dies
// with an OutOfMemoryException and the user loses their bridge to Revit, having
// been told nothing. The limit that was supposed to prevent exactly this never got
// a chance to run.
//
// So the bound is enforced WHILE reading. Past the limit nothing more is kept:
// the buffer is dropped and the rest of the line is drained and counted so that
// the NEXT request still starts on a request boundary. Refusing a line and then
// parsing its tail as if it were the next message would turn one oversized
// request into a stream of nonsense.
//
// Bytes, not characters. The limit exists to bound MEMORY, and a limit counted in
// UTF-16 chars after decoding is a limit applied to the thing it was meant to
// prevent. Decoding happens once, at the end, on something already known to fit.
//
// It takes a Stream, so a MemoryStream proves every branch and no client is
// needed to test the case that used to kill the process.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;

namespace Horizun.Server
{
    public enum BoundedLineOutcome
    {
        /// <summary>A complete line, within the limit.</summary>
        Ok,

        /// <summary>stdin closed. The client is done with us.</summary>
        EndOfStream,

        /// <summary>Over the limit. Nothing was kept; the stream is positioned at the next line.</summary>
        TooLong,

        /// <summary>The read itself failed.</summary>
        Failed
    }

    public sealed class BoundedLine
    {
        public BoundedLineOutcome Outcome { get; internal set; }

        /// <summary>The decoded line, without its newline. Null unless Outcome is Ok.</summary>
        public string Line { get; internal set; }

        /// <summary>
        /// How many bytes the line actually ran to. On TooLong this is the FULL length,
        /// counted while draining - the number the caller needs in order to say how far
        /// over the limit the request was, rather than reporting the limit back at them.
        /// </summary>
        public long Bytes { get; internal set; }

        /// <summary>Set on TooLong and Failed. Null otherwise.</summary>
        public string Error { get; internal set; }

        public bool Ok => Outcome == BoundedLineOutcome.Ok;
    }

    internal sealed class BoundedLineReader
    {
        /// <summary>
        /// A request is a method, some ids and a handful of arguments. From the shared
        /// contract, so the add-in's pipe applies the same number to the same hop.
        /// </summary>
        public const int DefaultMaxBytes = Horizun.Contracts.Contract.MaxRequestBytes;

        private readonly Stream _in;
        private readonly int _maxBytes;
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _have;      // bytes sitting in _buffer
        private int _next;      // where the unread part of _buffer starts
        private bool _atStreamStart = true;

        public BoundedLineReader(Stream input, int maxBytes = DefaultMaxBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            _in = input;
            _maxBytes = maxBytes;
        }

        /// <summary>
        /// Read one newline-terminated line, never holding more than the limit.
        ///
        /// Blocks until something arrives: stdin going quiet is a client thinking, not a
        /// client gone, and the only event that ends this loop is the stream closing.
        /// </summary>
        public BoundedLine ReadLine()
        {
            var acc = new MemoryStream();
            long length = 0;              // the true length, which keeps counting past the limit
            bool over = false;

            while (true)
            {
                if (_next >= _have)
                {
                    int read;
                    try { read = _in.Read(_buffer, 0, _buffer.Length); }
                    catch (Exception ex)
                    {
                        return new BoundedLine
                        {
                            Outcome = BoundedLineOutcome.Failed,
                            Bytes = length,
                            Error = "Reading stdin failed: " + Innermost(ex).Message
                        };
                    }

                    if (read <= 0)
                    {
                        // Closed. A partial line is not a request: answering it would mean
                        // guessing what the rest of it said. An oversized partial still
                        // reports as oversized - that is the more useful of the two truths.
                        if (over) return TooLong(length);
                        if (length > 0)
                            return new BoundedLine
                            {
                                Outcome = BoundedLineOutcome.EndOfStream,
                                Bytes = length,
                                Error = "stdin closed after " + length + " bytes, mid-request."
                            };
                        return new BoundedLine { Outcome = BoundedLineOutcome.EndOfStream, Bytes = 0 };
                    }

                    _have = read;
                    _next = 0;
                }

                while (_next < _have)
                {
                    byte b = _buffer[_next++];
                    if (b == (byte)'\n')
                        return over
                            ? TooLong(length)
                            : new BoundedLine
                            {
                                Outcome = BoundedLineOutcome.Ok,
                                Bytes = length,
                                Line = Decode(acc)
                            };

                    length++;
                    if (over) continue;               // still counting, no longer keeping

                    if (length > _maxBytes)
                    {
                        // The moment it is too long, the bytes already read stop being
                        // worth holding. Drop them and keep draining to the newline so
                        // the next request starts where a request starts.
                        over = true;
                        acc.Dispose();
                        continue;
                    }
                    if (b != (byte)'\r') acc.WriteByte(b);
                }
            }
        }

        /// <summary>
        /// Decode a line, dropping a byte-order mark at the very start of the stream.
        ///
        /// THIS IS WHY IT IS HERE. The loop used to hold a StreamReader, and StreamReader
        /// strips a leading BOM for you - quietly, by default. Reading raw bytes does not,
        /// and nothing in a unit test noticed, because a test writes the bytes it means.
        /// A real client does not: .NET's StreamWriter and PowerShell's both emit a UTF-8
        /// BOM ahead of the first line unless told otherwise, and MCP over stdio has no
        /// handshake before that first line. So the FIRST request of the session came back
        /// as a parse error with id null - unmatchable to anything the client sent - and
        /// the session looked broken from its opening message.
        ///
        /// Measured against the built server on 2026-07-31, by driving it the way a client
        /// drives it rather than the way a test does.
        ///
        /// Only at the stream start, because that is the only place the mark means
        /// anything. A U+FEFF in the middle of a request is content, and content is the
        /// caller's business.
        /// </summary>
        private string Decode(MemoryStream acc)
        {
            string s = Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length);
            if (_atStreamStart)
            {
                _atStreamStart = false;
                // Spelled by its code point, not written as the character: a literal BOM
                // in source is invisible in every editor and diff that would review it.
                const char ByteOrderMark = (char)0xFEFF;
                if (s.Length > 0 && s[0] == ByteOrderMark) return s.Substring(1);
            }
            return s;
        }

        private BoundedLine TooLong(long length) => new BoundedLine
        {
            Outcome = BoundedLineOutcome.TooLong,
            Bytes = length,
            Error = "Request refused: " + length + " bytes, over the " + _maxBytes + " byte limit. Nothing was " +
                    "done, and none of it was kept in memory - the limit is enforced while reading, not after. " +
                    "This channel carries a method and its arguments; send a path or an id instead of the payload " +
                    "itself. The next request is read normally."
        };

        private static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
