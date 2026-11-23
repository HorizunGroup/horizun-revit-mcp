using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ScanQueueTests
    {
        [Fact]
        public void ModelScansQueueFifoWithSixteenWaitingAndBackpressure()
        {
            var gate = new RequestGate();
            var first = gate.Begin("first", "horizun_model_scan", "{}", out var refusal);
            Assert.Same(first, gate.Take());
            var waiting = new RequestGate.Request[16];
            for (int i = 0; i < waiting.Length; i++)
                waiting[i] = gate.Begin("scan-" + i, "horizun_model_scan", "{}", out refusal);
            Assert.All(waiting, item => Assert.NotNull(item));
            Assert.Null(gate.Begin("overflow", "horizun_model_scan", "{}", out refusal));
            Assert.Contains("queue is full", refusal);
            Assert.Null(gate.Take());
            gate.Complete(first);
            foreach (var item in waiting)
            {
                Assert.Same(item, gate.Take());
                gate.Complete(item);
            }
            Assert.Null(gate.Take());
        }
    }
}
