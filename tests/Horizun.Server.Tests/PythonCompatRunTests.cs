// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// PythonCompat's rewrites, EXECUTED: the source it produces must compile in the
// IronPython the add-in ships and behave with the prelude's transaction tracking.
// Stand-ins replace Revit's Transaction and Document; the names and methods they
// expose are the ones the prelude calls (GetStatus, RollBack, GetName, Equals).
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;
using Xunit;

namespace Horizun.Server.Tests
{
    public class PythonCompatRunTests
    {
        public sealed class FakeJob { public void Write(string label, object done, object total) { } }
        public sealed class FakeScope : IDisposable { public void Dispose() { } }

        private const string Fakes =
            "class Status(object):\n" +
            "    def __init__(self, s): self.s = s\n" +
            "    def __str__(self): return self.s\n" +
            "class Transaction(object):\n" +
            "    log = []\n" +
            "    def __init__(self, doc, name='t'):\n" +
            "        self.doc, self.name, self.state = doc, name, 'Created'\n" +
            "    def Start(self): self.state = 'Started'\n" +
            "    def Commit(self): self.state = 'Committed'\n" +
            "    def RollBack(self):\n" +
            "        self.state = 'RolledBack'\n" +
            "        Transaction.log.append('rollback ' + self.name)\n" +
            "    def GetStatus(self): return Status(self.state)\n" +
            "    def GetName(self): return self.name\n" +
            "class Document(object):\n" +
            "    def __init__(self, title): self.title = title\n" +
            "    def Equals(self, other): return self is other\n" +
            "    def Close(self, save):\n" +
            "        open_tx = [t for t, d in globals()['__hz_tx_open'] if d is self and str(t.GetStatus()) == 'Started']\n" +
            "        if open_tx: raise Exception('Close is not allowed when there is any open transaction')\n" +
            "        Transaction.log.append('closed ' + self.title)\n";

        private static ScriptScope Run(string userScript, int year = 2026)
        {
            ScriptEngine engine = Python.CreateEngine();
            ScriptScope scope = engine.CreateScope();
            scope.SetVariable("__hz_job", new FakeJob());
            scope.SetVariable("__hz_raised", (Func<int, string>)(_ => "[]"));
            scope.SetVariable("__hz_dialog_scope", (Func<string, IDisposable>)(_ => new FakeScope()));
            engine.Execute(ScriptPrelude.Prologue, scope);
            engine.Execute(Fakes, scope);
            engine.Execute(PythonCompat.Apply(userScript, year, null).Code, scope);
            return scope;
        }

        [Fact]
        public void A_transaction_left_open_is_rolled_back_by_the_bridge_and_named()
        {
            ScriptScope scope = Run("doc = Document('m')\nt = Transaction(doc, 'paint')\nt.Start()\n");
            scope.Engine.Execute("closed = __hz_rollback_open()\nn = len(__hz_tx_closed)\nname = __hz_tx_closed[0]['name']\n", scope);
            Assert.Equal(1, (int)scope.GetVariable("n"));
            Assert.Equal("paint", (string)scope.GetVariable("name"));
        }

        [Fact]
        public void A_committed_transaction_is_left_alone()
        {
            ScriptScope scope = Run("doc = Document('m')\nt = Transaction(doc, 'ok')\nt.Start()\nt.Commit()\n");
            scope.Engine.Execute("n = len(__hz_rollback_open())\n", scope);
            Assert.Equal(0, (int)scope.GetVariable("n"));
        }

        [Fact]
        public void Close_rolls_back_that_documents_open_transactions_first_and_then_closes()
        {
            ScriptScope scope = Run(
                "fam = Document('family')\nother = Document('model')\n" +
                "a = Transaction(fam, 'edit')\na.Start()\n" +
                "b = Transaction(other, 'keep')\nb.Start()\n" +
                "fam.Close(False)\n");
            scope.Engine.Execute("log = list(Transaction.log)\nb_state = str(b.GetStatus())\n", scope);
            dynamic log = scope.GetVariable("log");
            Assert.Equal("rollback edit", (string)log[0]);
            Assert.Equal("closed family", (string)log[1]);
            Assert.Equal("Started", (string)scope.GetVariable("b_state"));
        }

        [Fact]
        public void Tracking_works_inside_a_class_body_where_python_mangles_double_underscores()
        {
            ScriptScope scope = Run(
                "class Job(object):\n" +
                "    def run(self, d):\n" +
                "        t = Transaction(d, 'in-class')\n" +
                "        t.Start()\n" +
                "        return t\n" +
                "Job().run(Document('m'))\n");
            scope.Engine.Execute("n = len(__hz_rollback_open())\n", scope);
            Assert.Equal(1, (int)scope.GetVariable("n"));
        }

        [Fact]
        public void A_net_member_named_None_compiles_and_reads_the_member()
        {
            ScriptScope scope = Run("import System\nx = System.StringSplitOptions.None\nok = x == getattr(System.StringSplitOptions, 'None')\n");
            Assert.True((bool)scope.GetVariable("ok"));
        }
    }
}
