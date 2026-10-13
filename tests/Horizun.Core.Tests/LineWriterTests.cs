// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Backlog #37: the add-in wrote its replies to the pipe with no bound. The pipe has
// no buffer, so a write completes only when the other end reads - and when a
// request was refused as TooLong, the server was still blocked WRITING that request
// while the add-in blocked writing its refusal: both ends stuck, a connection slot
// held until Revit closed. A reply write now gives up after a deadline.
// -----------------------------------------------------------------------------
using System;
using System.IO.Pipes;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class LineWriterTests
    {
        [Fact]
        public async Task A_reply_the_peer_never_reads_gives_up_at_its_deadline()
        {
            string name = "hz-linewriter-" + Guid.NewGuid().ToString("N");
            // Zero buffers, the way PipeServer creates its pipe.
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                       PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0))
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                var accept = server.WaitForConnectionAsync();
                client.Connect(5000);
                await accept.WaitAsync(TimeSpan.FromSeconds(5));

                // The peer never reads (it is blocked writing its own request).
                string line = new string('x', 1024 * 1024);
                var write = Task.Run(() => LineWriter.TryWriteLine(server, line, 300));

                Assert.False(await write.WaitAsync(TimeSpan.FromSeconds(10)));
            }
        }

        [Fact]
        public async Task A_reply_the_peer_reads_is_written_whole()
        {
            string name = "hz-linewriter-" + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                       PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0))
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                var accept = server.WaitForConnectionAsync();
                client.Connect(5000);
                await accept.WaitAsync(TimeSpan.FromSeconds(5));

                string line = new string('y', 200000);
                var read = Task.Run(() => LineReader.Read(client, 1024 * 1024, 10000));
                Assert.True(LineWriter.TryWriteLine(server, line, 10000));
                var received = await read.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(received.Ok);
                Assert.Equal(line, received.Line);
            }
        }
    }
}
