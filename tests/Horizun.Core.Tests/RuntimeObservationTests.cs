using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RuntimeObservationTests
    {
        [Fact]
        public void Request_observations_do_not_guess_when_request_disappears()
        {
            var gate=new RequestGate(); var first=gate.Begin("one","test","{}",out _); gate.Begin("two","test","{}",out _);
            Assert.Equal("queued",(string)gate.Observe("one")["state"]);
            Assert.Equal(2,(int)gate.Observe("two")["queue_position"]);
            Assert.Same(first,gate.Take());
            Assert.Equal("executing",(string)gate.Observe("one")["state"]);
            Assert.Equal(1,(int)gate.Observe("two")["queue_position"]);
            gate.Complete(first);
            Assert.Equal("not_found_or_finished",(string)gate.Observe("one")["state"]);
            gate.CancelQueued("two",out _);
            Assert.Equal("not_found_or_finished",(string)gate.Observe("two")["state"]);
        }
        [Fact]
        public void Warmup_failure_and_retry_remain_distinct_from_ready()
        {
            var runtime=new RuntimeWarmup(); Assert.Equal("cold",(string)runtime.Snapshot()["state"]);
            runtime.Begin(); Assert.Equal("warming",(string)runtime.Snapshot()["state"]);
            runtime.Failed(new InvalidOperationException("fixture failure"));
            Assert.Equal("failed",(string)runtime.Snapshot()["state"]);
            Assert.Contains("fixture failure",(string)runtime.Snapshot()["last_error"]);
            runtime.Begin(); runtime.Ready();
            Assert.Equal("ready",(string)runtime.Snapshot()["state"]);
            Assert.Equal(2,(int)runtime.Snapshot()["initialization_attempts"]);
            Assert.False((bool)runtime.Snapshot()["first_call_requires_initialization"]);
        }
    }
}
