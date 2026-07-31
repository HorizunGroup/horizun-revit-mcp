// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE SERVER READS WHERE THE ADD-IN WRITES, across the seam between the two
// halves.
//
// The Core suite proves the root is not LocalApplicationData and that no shipped
// source computes its own. This proves the thing those are FOR: a discovery file
// published through the function the ADD-IN calls is found through
// PipeClient.ListAll - the real resolution path the running server uses, with
// DirectoryOverride deliberately null.
//
// That combination had no test at all. DiscoveryResolveTests, which owns every
// other rule about picking an instance, sets DirectoryOverride to a temp folder
// precisely so it does NOT depend on where discovery really lives - so nothing
// was left proving the two halves agree about that. They agreed by coincidence:
// each computed LocalApplicationData + "Horizun" separately.
//
// What a packaged host does to that coincidence is in HorizunPaths.cs. It cannot
// be reproduced from inside one process - GetFolderPath ignores the environment -
// so nothing here pretends to; these prove the seam is closed by construction,
// which is the property that makes the split impossible rather than unlikely.
//
// These set the environment and PipeClient's static session state, so the suite
// runs serially - see Parallelism.cs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class SharedDataRootTests : IDisposable
    {
        private static readonly string[] EnvNames =
            { HorizunPaths.RootOverrideVariable, "USERPROFILE", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH" };

        private readonly string[] _savedEnv = new string[EnvNames.Length];
        private readonly string _root;

        public SharedDataRootTests()
        {
            for (int i = 0; i < EnvNames.Length; i++) _savedEnv[i] = Environment.GetEnvironmentVariable(EnvNames[i]);

            _root = Path.Combine(Path.GetTempPath(), "hz-shared-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _root);

            // NULL, so ListAll goes through the real resolution instead of the test
            // hook. That is the whole point of this class.
            PipeClient.DirectoryOverride = null;
            PipeClient.LivenessProbe = pid => true;
            PipeClient.Target = TargetSelection.Automatic;
        }

        public void Dispose()
        {
            PipeClient.DirectoryOverride = null;
            PipeClient.LivenessProbe = null;
            PipeClient.Target = TargetSelection.Automatic;
            for (int i = 0; i < EnvNames.Length; i++)
                Environment.SetEnvironmentVariable(EnvNames[i], _savedEnv[i]);
            try { Directory.Delete(_root, true); } catch { }
        }

        /// <summary>
        /// Publish a discovery file the way the ADD-IN does: into the directory
        /// HorizunPaths hands the Revit half. Deliberately not into a path this test
        /// composes itself - a test that builds the path it then asserts on proves
        /// only that it can concatenate strings.
        /// </summary>
        private static string PublishAsAddin(string year, int pid)
        {
            string dir = HorizunPaths.DiscoveryDir();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "revit-" + year + "-" + pid + ".json");
            File.WriteAllText(path, new JObject
            {
                ["schema"] = 3,
                ["revit_year"] = year,
                ["pipe_name"] = "Horizun-" + pid,
                ["auth_token"] = "t",
                ["pid"] = pid,
                ["instance_id"] = Guid.NewGuid().ToString("N"),
                ["started_utc"] = DateTime.UtcNow.ToString("o"),
                ["addin_version"] = "0.2.0",
                ["commands"] = new JArray("horizun_health")
            }.ToString());
            return path;
        }

        [Fact]
        public void The_server_finds_the_file_the_addin_published()
        {
            string written = PublishAsAddin("2026", 4242);

            List<Discovered> found = PipeClient.ListAll();

            Assert.Single(found);
            Assert.Equal("2026", found[0].Year);
            Assert.Equal(4242, found[0].Pid);
            // The same FILE, not merely a file that looks similar.
            Assert.Equal(Path.GetFullPath(written), Path.GetFullPath(found[0].SourceFile));
        }

        [Fact]
        public void Resolution_reaches_a_named_instance_through_the_shared_root()
        {
            PublishAsAddin("2025", 111);
            PublishAsAddin("2026", 222);

            string refusal;
            Discovered chosen = PipeClient.Resolve("2026", null, out refusal);

            // Not just "the directory was readable" - the server got all the way to a
            // named instance, which is what every model call depends on.
            Assert.Null(refusal);
            Assert.NotNull(chosen);
            Assert.Equal(222, chosen.Pid);
        }

        [Fact]
        public void The_server_does_not_look_under_localappdata()
        {
            PublishAsAddin("2026", 777);

            // Where the OLD rule would have looked. If the server still resolved that
            // way it would find nothing here, because the add-in no longer writes there.
            string oldLocation = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Horizun");

            List<Discovered> found = PipeClient.ListAll();

            Assert.Single(found);
            Assert.False(found[0].SourceFile.StartsWith(oldLocation, StringComparison.OrdinalIgnoreCase),
                "the server resolved discovery under LocalApplicationData: " + found[0].SourceFile);
            Assert.StartsWith(_root, found[0].SourceFile, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Both_halves_name_the_same_jobs_directory()
        {
            // horizun_job_status exists to answer while Revit's UI thread is busy, so it
            // reads the record off disk rather than asking the plugin. If the two halves
            // disagree about which folder that is, the tool answers "no job has been
            // recorded on this machine yet" about a job that is running right now.
            Job record = Job.Start("horizun_execute_python");
            record.Finish("ok", null);

            Assert.False(string.IsNullOrEmpty(record.Path));
            Assert.True(File.Exists(record.Path));
            // Written by the add-in's Job, located by the shared accessor the server's
            // JobStatus calls.
            Assert.StartsWith(HorizunPaths.JobsDir(), record.Path, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(_root, HorizunPaths.JobsDir(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
