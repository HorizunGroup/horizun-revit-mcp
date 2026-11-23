using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ControlPipeDeadlineTests
    {
        [Theory]
        [InlineData("ReadRequestStatus")]
        [InlineData("TryCancelQueued")]
        public async Task A_control_peer_that_never_reads_cannot_hold_the_caller(string method)
        {
            string name = "hrz-control-deadline-" + Guid.NewGuid().ToString("N");
            using (var release = new CancellationTokenSource())
            using (var peer = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0))
            {
                Task accept = Task.Run(async () =>
                {
                    await peer.WaitForConnectionAsync(release.Token);
                    try { await Task.Delay(20000, release.Token); } catch (OperationCanceledException) { }
                });
                // Long wire id makes a zero-buffer control pipe block in WriteLine. No Revit involved.
                var target = new Discovered { PipeName = name };
                MethodInfo control = typeof(PipeClient).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
                Task call = Task.Run(() => control.Invoke(null, new object[] { target, new string('x', 256 * 1024) }));
                var clock = Stopwatch.StartNew();
                bool bounded = await Task.WhenAny(call, Task.Delay(7000)) == call;
                release.Cancel();
                peer.Dispose();
                try { await accept; } catch (OperationCanceledException) { }
                try { await call; } catch { }
                Assert.True(bounded, method + " blocked sending control traffic for " + clock.ElapsedMilliseconds + " ms");
            }
        }
    }
}
