using System;
using System.Collections.Generic;
using Horizun.Revit.Core;
using IronPython.Hosting;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Xunit;

namespace Horizun.Server.Tests
{
    /// <summary>checkpoint() and friends stop a script that must stop (4.2-d).</summary>
    public class ScriptPreludeInterruptTests
    {
        public sealed class FakeJob
        {
            public readonly List<string> Labels = new List<string>();
            public void Write(string label, object done, object total) { Labels.Add(label); }
        }

        private static ScriptScope Prepared(ScriptEngine engine, PythonRunControl control, FakeJob job)
        {
            ScriptScope scope = engine.CreateScope();
            scope.SetVariable("__hz_job", job);
            scope.SetVariable("__hz_raised", (Func<int, string>)(_ => "[]"));
            scope.SetVariable("__hz_dialog_scope", (Func<string, IDisposable>)(_ => null));
            if (control != null) scope.SetVariable("__hz_control", control);
            engine.Execute(ScriptPrelude.Prologue, scope);
            return scope;
        }

        [Fact]
        public void A_cancelled_run_stops_at_the_next_checkpoint_and_the_label_is_kept()
        {
            var engine = Python.CreateEngine();
            var control = new PythonRunControl(null);
            var job = new FakeJob();
            var scope = Prepared(engine, control, job);
            scope.SetVariable("cancel_now", (Action)(() => control.Cancel("user asked")));
            Assert.ThrowsAny<Exception>(() => engine.Execute(@"
done = []
try:
    for i in range(10):
        checkpoint('item %d' % i, i, 10)
        done.append(i)
        if i == 3:
            cancel_now()
except Exception:
    swallowed = True
", scope));
            Assert.Equal(4, ((System.Collections.IList)scope.GetVariable("done")).Count);
            Assert.Equal("item 4", job.Labels[job.Labels.Count - 1]);
        }

        [Fact]
        public void The_interruption_is_not_caught_by_except_Exception()
        {
            var engine = Python.CreateEngine();
            var control = new PythonRunControl(null);
            var scope = Prepared(engine, control, new FakeJob());
            control.Cancel("stop");
            var ex = Assert.ThrowsAny<Exception>(() => engine.Execute(@"
swallowed = False
try:
    checkpoint('x')
except Exception:
    swallowed = True
", scope));
            Assert.Contains("HorizunCancelled", engine.GetService<ExceptionOperations>().FormatException(ex));
            Assert.False((bool)scope.GetVariable("swallowed"));
        }

        [Fact]
        public void Checkpoint_without_label_observes_cancellation()
        {
            var engine = Python.CreateEngine();
            var control = new PythonRunControl(null);
            var scope = Prepared(engine, control, new FakeJob());
            engine.Execute("checkpoint()", scope);
            control.Cancel("client");
            var ex = Assert.ThrowsAny<Exception>(() => engine.Execute("checkpoint()", scope));
            Assert.Contains("HorizunCancelled", engine.GetService<ExceptionOperations>().FormatException(ex));
        }

        [Fact]
        public void Finally_blocks_still_run_so_a_script_can_roll_back_its_own_work()
        {
            var engine = Python.CreateEngine();
            var control = new PythonRunControl(null);
            var scope = Prepared(engine, control, new FakeJob());
            control.Cancel("stop");
            Assert.ThrowsAny<Exception>(() => engine.Execute(@"
cleaned = False
try:
    safe_point()
finally:
    cleaned = True
", scope));
            Assert.True((bool)scope.GetVariable("cleaned"));
        }

        [Fact]
        public void should_stop_reports_without_raising_and_a_run_without_control_never_stops()
        {
            var engine = Python.CreateEngine();
            var control = new PythonRunControl(null);
            var scope = Prepared(engine, control, new FakeJob());
            engine.Execute("before = should_stop()", scope);
            control.Cancel("stop");
            engine.Execute("after = should_stop()", scope);
            Assert.False((bool)scope.GetVariable("before"));
            Assert.True((bool)scope.GetVariable("after"));

            var plain = Prepared(engine, null, new FakeJob());
            engine.Execute("checkpoint('a'); safe_point(); stop = should_stop()", plain);
            Assert.False((bool)plain.GetVariable("stop"));
        }

        [Fact]
        public void The_interrupt_unwinds_horizun_transaction_as_a_rollback()
        {
            var engine = Python.CreateEngine();
            var control = new PythonRunControl(null);
            var scope = Prepared(engine, control, new FakeJob());
            control.Cancel("stop");
            Assert.ThrowsAny<Exception>(() => engine.Execute(@"
import types, sys
events = []
class Doc:
    IsModifiable = False
doc = Doc()
class Status:
    Started = 'Started'
    Committed = 'Committed'
    RolledBack = 'RolledBack'
class Transaction:
    def __init__(self, d, name=None): self.s = None
    def Start(self): self.s = Status.Started; return self.s
    def Commit(self): events.append('commit'); self.s = Status.Committed; return self.s
    def RollBack(self): events.append('rollback'); self.s = Status.RolledBack; return self.s
    def GetStatus(self): return self.s
    def Dispose(self): pass
m = types.ModuleType('Autodesk.Revit.DB')
m.Transaction = Transaction
m.SubTransaction = Transaction
m.TransactionStatus = Status
sys.modules['Autodesk'] = types.ModuleType('Autodesk')
sys.modules['Autodesk.Revit'] = types.ModuleType('Autodesk.Revit')
sys.modules['Autodesk.Revit.DB'] = m
with horizun.transaction('t'):
    checkpoint('inside')
    events.append('not reached')
", scope));
            var events = (System.Collections.IList)scope.GetVariable("events");
            Assert.True(events.Contains("rollback"));
            Assert.False(events.Contains("commit"));
            Assert.False(events.Contains("not reached"));
        }
    }
}
