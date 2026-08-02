// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Reading one request off a socket without trusting whoever is on the other end.
//
// The transport used StreamReader.ReadLine(). Three things follow from that, and
// all three are reachable by a client that is merely broken, never mind hostile:
//
//   * NO TIMEOUT. A client that connects and then says nothing holds the
//     connection, and its thread, until Revit closes. PipeStream does not support
//     ReadTimeout - setting it throws - so the timeout has to be built here.
//
//   * NO LENGTH LIMIT. A line with no newline in it is read until memory runs
//     out. The request channel carries commands and ids; the megabytes travel the
//     other way.
//
//   * NO WAY TO REFUSE EARLY. The whole line was read before the auth token in it
//     was checked, so an unauthenticated caller could make us allocate first and
//     ask questions afterwards. Bounding the read is what makes checking the token
//     second acceptable.
//
// Chunked rather than byte-at-a-time: a real request is a few hundred bytes and a
// per-byte async read would cost thousands of awaits for no benefit. Anything
// past the newline is kept, because this transport is one request per connection
// and a client that pipelined would otherwise have its extra bytes vanish
// silently - we would rather see them and say so.
//
// Revit-free: it takes a Stream, so a MemoryStream proves it.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Text;

namespace Horizun.Revit.Core
{
    public enum LineOutcome
    {
        Ok,
        /// <summary>The peer closed without sending a complete line.</summary>
        Closed,
        /// <summary>Nothing arrived within the deadline.</summary>
        TimedOut,
        /// <summary>The line exceeded the limit and was not read to its end.</summary>
        TooLong,
        /// <summary>The read itself failed.</summary>
        Failed
    }

    public sealed class LineResult
    {
        public LineOutcome Outcome { get; internal set; }
        public string Line { get; internal set; }
        public string Error { get; internal set; }

        /// <summary>Bytes read before giving up. For the message, not for logic.</summary>
        public int BytesRead { get; internal set; }

        public bool Ok => Outcome == LineOutcome.Ok;
    }

    public static class LineReader
    {
        /// <summary>
        /// Read one newline-terminated line, bounded in both size and time.
        ///
        /// A line longer than <paramref name="maxBytes"/> stops the read at the limit
        /// rather than draining the rest: the connection is about to be closed anyway, and
        /// reading a megabyte we have already decided to refuse is the cost we are avoiding.
        /// </summary>
        public static LineResult Read(Stream stream, int maxBytes, int timeoutMs)
        {
            var acc = new MemoryStream();
            var buffer = new byte[4096];
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (true)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                    return new LineResult
                    {
                        Outcome = LineOutcome.TimedOut,
                        BytesRead = (int)acc.Length,
                        Error = "No complete request arrived within " + timeoutMs + " ms" +
                                (acc.Length > 0 ? " (" + acc.Length + " bytes were received)" : " (nothing was sent)") + "."
                    };

                int read;
                try
                {
                    var task = stream.ReadAsync(buffer, 0, buffer.Length);
                    if (!task.Wait(remaining))
                        return new LineResult
                        {
                            Outcome = LineOutcome.TimedOut,
                            BytesRead = (int)acc.Length,
                            Error = "No complete request arrived within " + timeoutMs + " ms" +
                                    (acc.Length > 0 ? " (" + acc.Length + " bytes were received)" : " (nothing was sent)") + "."
                        };
                    read = task.Result;
                }
                catch (Exception ex)
                {
                    return new LineResult
                    {
                        Outcome = LineOutcome.Failed,
                        BytesRead = (int)acc.Length,
                        Error = "The connection failed while reading: " + Innermost(ex).Message
                    };
                }

                if (read == 0)
                {
                    // Peer closed. A partial line is not a request - answering it would mean
                    // guessing what the rest said.
                    return new LineResult
                    {
                        Outcome = LineOutcome.Closed,
                        BytesRead = (int)acc.Length,
                        Error = acc.Length == 0
                            ? "The peer connected and closed without sending anything."
                            : "The peer closed after " + acc.Length + " bytes, mid-request."
                    };
                }

                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    if (b == (byte)'\n')
                        return new LineResult
                        {
                            Outcome = LineOutcome.Ok,
                            BytesRead = (int)acc.Length,
                            Line = Encoding.UTF8.GetString(acc.ToArray())
                        };
                    if (b == (byte)'\r') continue;

                    if (acc.Length >= maxBytes)
                        return new LineResult
                        {
                            Outcome = LineOutcome.TooLong,
                            BytesRead = (int)acc.Length,
                            Error = "The request exceeded " + maxBytes + " bytes without a newline and was refused. " +
                                    "This channel carries commands and ids; large payloads belong in a file whose " +
                                    "path you pass instead."
                        };
                    acc.WriteByte(b);
                }
            }
        }

        private static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
