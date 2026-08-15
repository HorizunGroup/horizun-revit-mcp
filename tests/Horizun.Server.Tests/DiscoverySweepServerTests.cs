// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The SERVER sweeps orphaned discovery files at startup (story 5.24). The add-in
// swept only when it published one - when a Revit STARTS - so a server coming up
// after a crash, with no new Revit, was the exact moment nothing cleaned them.
// Asserted over real files on disk through PipeClient.SweepStaleDiscovery, with
// the liveness probe steered so a "dead" pid needs no dead process to exist.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Server.Tests
{
    public class DiscoverySweepServerTests : IDisposable
    {
        private readonly string _root;
        private readonly string _savedRoot;

        public DiscoverySweepServerTests()
        {
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            _root = Path.Combine(Path.GetTempPath(), "hz-sweep-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            PipeClient.LivenessProbe = null;
            try { Directory.Delete(_root, true); } catch { }
        }

        private string Discovery()
        {
            string dir = HorizunPaths.DiscoveryDir();
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Write(string dir, string name)
            => File.WriteAllText(Path.Combine(dir, name), "{\"pid\":0}");

        [Fact]
        public void The_server_deletes_a_dead_instances_file_and_keeps_a_live_one()
        {
            string dir = Discovery();
            Write(dir, "revit-2025-100.json");   // will be alive
            Write(dir, "revit-2025-200.json");   // dead -> swept
            PipeClient.LivenessProbe = pid => pid == 100;

            int swept = PipeClient.SweepStaleDiscovery();

            Assert.Equal(1, swept);
            Assert.True(File.Exists(Path.Combine(dir, "revit-2025-100.json")));
            Assert.False(File.Exists(Path.Combine(dir, "revit-2025-200.json")));
        }

        [Fact]
        public void The_server_never_deletes_a_legacy_named_file()
        {
            string dir = Discovery();
            Write(dir, "revit-2025.json");        // a not-yet-redeployed add-in's file
            PipeClient.LivenessProbe = pid => false;   // nothing alive

            int swept = PipeClient.SweepStaleDiscovery();

            Assert.Equal(0, swept);
            Assert.True(File.Exists(Path.Combine(dir, "revit-2025.json")));
        }

        [Fact]
        public void A_missing_discovery_directory_is_not_an_error()
        {
            // No directory yet: the sweep returns 0 and does not throw, so it never
            // stops the server from coming up.
            Assert.Equal(0, PipeClient.SweepStaleDiscovery());
        }
    }
}
