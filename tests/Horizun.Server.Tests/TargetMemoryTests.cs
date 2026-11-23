// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// T2 (field cycle 2026-10-24): about 64 horizun_target calls per agent, because hz-call starts a server
// per call. A pid pinned by the environment, or a choice remembered under a session key, now
// survives the process - and never lands on a Revit nobody chose.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Horizun.Server;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class TargetMemoryTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hz-target-" + Guid.NewGuid().ToString("N"));
        private readonly string _oldPid = Environment.GetEnvironmentVariable(TargetMemory.PidVariable);
        private readonly string _oldKey = Environment.GetEnvironmentVariable(TargetMemory.SessionVariable);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(TargetMemory.PidVariable, _oldPid);
            Environment.SetEnvironmentVariable(TargetMemory.SessionVariable, _oldKey);
            try { Directory.Delete(_root, true); } catch { }
        }

        private static void Env(string pid, string key)
        {
            Environment.SetEnvironmentVariable(TargetMemory.PidVariable, pid);
            Environment.SetEnvironmentVariable(TargetMemory.SessionVariable, key);
        }

        [Fact]
        public void Nothing_set_is_automatic_and_writes_nothing()
        {
            Env(null, null);
            Assert.True(TargetMemory.Initial(p => true, _root, out string note).IsAutomatic);
            Assert.Null(note);
            Assert.Null(TargetMemory.Remember(TargetSelection.ByPid(10), _root));
            Assert.False(Directory.Exists(_root));
        }

        [Fact]
        public void An_environment_pid_pins_that_instance_even_when_it_is_gone()
        {
            Env("4321", null);
            TargetSelection t = TargetMemory.Initial(p => false, _root, out string note);
            Assert.Equal(4321, t.Pid);
            Assert.Contains("calls will fail, not move to another Revit", note);
            Assert.Contains("HORIZUN_REVIT_PID", t.Describe(""));
        }

        [Fact]
        public void A_choice_is_remembered_for_its_session_and_restored_by_the_next_server()
        {
            Env(null, "ele-agent");
            Assert.Contains("remembered for session 'ele-agent'", TargetMemory.Remember(TargetSelection.ByPid(43312), _root));
            TargetSelection t = TargetMemory.Initial(p => p == 43312, _root, out string note);
            Assert.Equal(43312, t.Pid);
            Assert.Contains("restored", note);
            Assert.Contains("remembered for session 'ele-agent'", t.Describe(""));
            // another session does not see it
            Env(null, "hs-agent");
            Assert.True(TargetMemory.Initial(p => true, _root, out _).IsAutomatic);
        }

        [Fact]
        public void A_remembered_pid_whose_revit_is_gone_is_forgotten_never_moved()
        {
            Env(null, "arq");
            TargetMemory.Remember(TargetSelection.ByPid(99), _root);
            TargetSelection t = TargetMemory.Initial(p => false, _root, out string note);
            Assert.True(t.IsAutomatic);
            Assert.Contains("forgotten", note);
            Assert.False(File.Exists(TargetMemory.FileFor("arq", _root)));
        }

        [Fact]
        public void Auto_forgets_and_a_bad_key_remembers_nothing()
        {
            Env(null, "arq");
            TargetMemory.Remember(TargetSelection.ByYear("2023"), _root);
            Assert.Equal("2023", TargetMemory.Initial(p => true, _root, out _).Year);
            Assert.Contains("forgotten", TargetMemory.Remember(TargetSelection.Automatic, _root));
            Assert.True(TargetMemory.Initial(p => true, _root, out _).IsAutomatic);
            Env(null, "../escape");
            Assert.Null(TargetMemory.Remember(TargetSelection.ByYear("2023"), _root));
            Assert.True(TargetMemory.Initial(p => true, _root, out string note).IsAutomatic);
            Assert.Contains("not a usable key", note);
        }
    }
}
