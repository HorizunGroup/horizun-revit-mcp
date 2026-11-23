using System;
using System.Diagnostics;
using IronPython.Hosting;
using Xunit;

namespace Horizun.Server.Tests
{
    public class PythonTraceFeasibilityTests
    {
        [Fact]
        public void Default_engine_trace_can_interrupt_a_finite_python_loop()
        {
            var engine = Python.CreateEngine();
            var scope = engine.CreateScope();
            engine.Execute(@"import sys
events = [0]
def trace(frame, event, arg):
    events[0] += 1
    if events[0] >= 25:
        raise RuntimeError('cooperative limit')
    return trace
sys.settrace(trace)
def loop():
    for i in range(100000):
        x = i + 1
try:
    loop()
except RuntimeError:
    interrupted = True
finally:
    sys.settrace(None)
", scope);
            Assert.True(scope.GetVariable<bool>("interrupted"));
            Assert.Equal(2, engine.Execute<int>("1 + 1"));
        }

        [Fact]
        public void Script_can_disable_trace_so_it_is_not_a_runtime_guarantee()
        {
            var engine = Python.CreateEngine();
            var scope = engine.CreateScope();
            engine.Execute(@"import sys
def trace(frame, event, arg):
    raise RuntimeError('limit')
sys.settrace(trace)
sys.settrace(None)
def loop():
    return sum(range(1000))
answer = loop()
", scope);
            Assert.Equal(499500, scope.GetVariable<int>("answer"));
        }

        [Fact]
        public void Trace_cannot_interrupt_an_in_progress_native_call()
        {
            var engine = Python.CreateEngine();
            var scope = engine.CreateScope();
            var timer = Stopwatch.StartNew();
            scope.SetVariable("timer", timer);
            scope.SetVariable("nativeSleep", new Action(() =>
            {
                timer.Restart();
                System.Threading.Thread.Sleep(350);
            }));
            engine.Execute(@"import sys
from System.Threading import Thread
def trace(frame, event, arg):
    if timer.IsRunning and timer.ElapsedMilliseconds >= 50:
        raise RuntimeError('limit')
    return trace
def native_call():
    nativeSleep()
    x = 1
timer.Stop()
sys.settrace(trace)
try:
    native_call()
except RuntimeError:
    interrupted = True
finally:
    sys.settrace(None)
", scope);
            Assert.True(scope.GetVariable<bool>("interrupted"));
            Assert.True(timer.ElapsedMilliseconds >= 300);
        }
    }
}
