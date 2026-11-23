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
    public class InteractiveDeadlineTests
    {
        [Fact]
        public async Task Health_does_not_inherit_a_long_callers_deadline()
        {
            string name = "hrz-health-deadline-" + Guid.NewGuid().ToString("N");
            using (var release = new CancellationTokenSource())
            using (var safety = new CancellationTokenSource(10000))
            using (var peer = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0))
            {
                Task server = Task.Run(async () =>
                {
                    await peer.WaitForConnectionAsync(release.Token);
                    using (var reader = new StreamReader(peer, new UTF8Encoding(false), false, 1024, true))
                        await reader.ReadLineAsync();
                    try { await Task.Delay(20000, release.Token); } catch (OperationCanceledException) { }
                });
                Exception failure = await Task.Run(() => Record.Exception(() => PipeClient.Send(
                    new Discovered { PipeName = name }, "horizun_health", new JObject(), 30000, safety.Token)));
                release.Cancel();
                peer.Dispose();
                try { await server; } catch (OperationCanceledException) { }
                var timeout = Assert.IsType<TimeoutException>(failure);
                var detail = (JObject)timeout.Data["horizun_transport_detail"];
                Assert.Equal(5000, (int)detail["limite_ms"]);
                // The peer never proved removal, so a retry must not assume nothing ran.
                Assert.NotEqual(false, (bool?)detail["request_may_have_been_submitted"]);
            }
        }
    }
}
