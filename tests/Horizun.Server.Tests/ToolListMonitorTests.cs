using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class ToolListMonitorTests
    {
        [Fact]
        public async Task Concurrent_checks_cannot_restore_an_older_snapshot()
        {
            using var oldCaptured = new ManualResetEventSlim();
            using var releaseOld = new ManualResetEventSlim();
            using var secondStarted = new ManualResetEventSlim();
            var notifications = new ConcurrentQueue<string>();
            int calls = 0;
            string current = "old";
            string Capture()
            {
                int call = Interlocked.Increment(ref calls);
                string value = Volatile.Read(ref current);
                if (call == 2)
                {
                    oldCaptured.Set();
                    if (!releaseOld.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
                return value;
            }

            using var monitor = new ToolListMonitor((method, _) => notifications.Enqueue(method), false, Capture);
            var oldCheck = Task.Factory.StartNew(monitor.CheckNow, TaskCreationOptions.LongRunning);
            Task newCheck = null;
            try
            {
                Assert.True(oldCaptured.Wait(TimeSpan.FromSeconds(10)));
                Volatile.Write(ref current, "new");
                newCheck = Task.Factory.StartNew(() => { secondStarted.Set(); monitor.CheckNow(); },
                    TaskCreationOptions.LongRunning);
                Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));
                // With the old implementation the new check completes before the stale
                // capture is released; with serialization it waits for the first check.
                await Task.WhenAny(newCheck, Task.Delay(TimeSpan.FromSeconds(1)));
            }
            finally
            {
                releaseOld.Set();
                await oldCheck.WaitAsync(TimeSpan.FromSeconds(10));
                if (newCheck != null) await newCheck.WaitAsync(TimeSpan.FromSeconds(10));
            }

            monitor.CheckNow(); // A subsequent tick must not rediscover the same change.
            Assert.Equal(new[] { "notifications/tools/list_changed" }, notifications.ToArray());
        }
    }
}
