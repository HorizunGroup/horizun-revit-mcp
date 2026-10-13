// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Backlog #37: the request was written to the pipe with no bound. The add-in's pipe
// has no buffer, so the write completes only as the add-in reads it - and when the
// add-in refuses a request as TooLong it STOPS reading and writes its refusal, which
// this end never read because it was still blocked writing. Both ends stuck, with no
// timeout anywhere. The fakes below are zero-buffer pipes, the add-in's real shape.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class PipeWriteBoundTests
    {
        private static NamedPipeServerStream AddInPipe(string name) =>
            new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
                                      PipeOptions.Asynchronous, 0, 0);

        private static JObject Big(int chars) =>
            new JObject { ["payload"] = new string('x', chars) };

        [Fact]
        public async Task A_request_the_add_in_refuses_unread_returns_its_refusal_instead_of_blocking()
        {
            string name = "hrz-write-bound-" + Guid.NewGuid().ToString("N");
            using (var release = new CancellationTokenSource())
            using (var addIn = AddInPipe(name))
            {
                var peer = Task.Run(async () =>
                {
                    await addIn.WaitForConnectionAsync();
                    // Read a little, as LineReader does before it decides TooLong, then stop reading.
                    var buffer = new byte[4096];
                    int seen = 0;
                    while (seen < 64 * 1024) seen += await addIn.ReadAsync(buffer, 0, buffer.Length);
                    byte[] refusal = Encoding.UTF8.GetBytes(new JObject
                    {
                        ["id"] = null, ["success"] = false,
                        ["error"] = "The request exceeded 65536 bytes without a newline and was refused."
                    }.ToString(Newtonsoft.Json.Formatting.None) + "\n");
                    try { await addIn.WriteAsync(refusal, 0, refusal.Length, release.Token); } catch { }
                    try { await Task.Delay(30000, release.Token); } catch { }
                });

                var clock = System.Diagnostics.Stopwatch.StartNew();
                var send = Task.Run(() => PipeClient.Send(new Discovered { PipeName = name }, "horizun_create_elements",
                                                          Big(1024 * 1024), 60000));
                bool finished = ((IAsyncResult)send).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                clock.Stop();
                release.Cancel();
                try { await peer; } catch { }

                Assert.True(finished, "Send was still blocked writing after " + clock.ElapsedMilliseconds + " ms");
                Assert.True(clock.ElapsedMilliseconds < 15000, clock.ElapsedMilliseconds + " ms");
                var ex = Assert.IsType<IOException>(send.Exception.GetBaseException());
                Assert.Contains("refused", ex.Message);
                var detail = (JObject)ex.Data["horizun_transport_detail"];
                Assert.False((bool)detail["request_may_have_been_submitted"]);
                Assert.Equal("not_started", (string)detail["transaction_status"]);
            }
        }

        [Fact]
        public async Task A_request_nobody_reads_times_out_instead_of_blocking_forever()
        {
            string name = "hrz-write-stall-" + Guid.NewGuid().ToString("N");
            int saved = PipeClient.WriteTimeoutMs;
            PipeClient.WriteTimeoutMs = 500;
            try
            {
                using (var release = new CancellationTokenSource())
                using (var addIn = AddInPipe(name))
                {
                    var peer = Task.Run(async () =>
                    {
                        await addIn.WaitForConnectionAsync();
                        try { await Task.Delay(30000, release.Token); } catch { }   // connected, never reads
                    });

                    var send = Task.Run(() => PipeClient.Send(new Discovered { PipeName = name }, "horizun_model_scan",
                                                              Big(256 * 1024), 60000));
                    bool finished = ((IAsyncResult)send).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                    release.Cancel();
                    try { await peer; } catch { }

                    Assert.True(finished, "Send was still blocked writing");
                    var ex = Assert.IsType<TimeoutException>(send.Exception.GetBaseException());
                    var detail = (JObject)ex.Data["horizun_transport_detail"];
                    Assert.Equal("send", (string)detail["phase"]);
                }
            }
            finally { PipeClient.WriteTimeoutMs = saved; }
        }

        [Fact]
        public void A_request_over_the_add_in_limit_is_refused_before_anything_is_sent()
        {
            var target = new Discovered { PipeName = "unused-" + Guid.NewGuid().ToString("N") };
            var ex = Assert.Throws<IOException>(() => PipeClient.Send(target, "horizun_create_elements",
                Big(Horizun.Contracts.Contract.MaxRequestBytes + 1), 5000));
            Assert.Contains(Horizun.Contracts.Contract.MaxRequestBytes.ToString(), ex.Message);
            var detail = (JObject)ex.Data["horizun_transport_detail"];
            Assert.False((bool)detail["request_may_have_been_submitted"]);
            Assert.Equal("not_started", (string)detail["transaction_status"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not an envelope\n")]
        [InlineData("{\"id\":\"other\",\"success\":true,\"data\":{}}\n")]
        public async Task An_early_close_or_unproven_reply_never_proves_nothing_ran(string reply)
        {
            string name = "hrz-write-uncertain-" + Guid.NewGuid().ToString("N");
            using (var addIn = AddInPipe(name))
            {
                var peer = Task.Run(async () =>
                {
                    await addIn.WaitForConnectionAsync();
                    var buffer = new byte[4096];
                    await addIn.ReadAsync(buffer, 0, buffer.Length);
                    if (reply != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(reply);
                        await addIn.WriteAsync(bytes, 0, bytes.Length);
                    }
                    addIn.Disconnect();
                });
                Exception ex = await Task.Run(() => Record.Exception(() => PipeClient.Send(
                    new Discovered { PipeName = name }, "horizun_create_elements", Big(1024 * 1024), 5000)));
                await peer;
                Assert.NotNull(ex);
                var detail = (JObject)ex.Data["horizun_transport_detail"];
                Assert.True((bool)detail["request_may_have_been_submitted"]);
                Assert.Equal("unknown", (string)detail["transaction_status"]);
            }
        }
    }
}
