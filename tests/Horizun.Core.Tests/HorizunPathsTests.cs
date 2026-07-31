// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// ONE DATA ROOT, for state that TWO PROCESSES SHARE.
//
// Revit writes a discovery file the server reads, Revit writes a job record the
// server reports on, and both read one settings.json. Each of those locations
// used to be computed separately at each end, from
// Environment.SpecialFolder.LocalApplicationData - seven lines in two projects
// that ship separately and agreed by coincidence.
//
// The coincidence holds until the two processes resolve that folder differently,
// which is not exotic: a packaged (MSIX/AppContainer) host redirects
// FOLDERID_LocalAppData into its own per-package LocalCache, and a different
// user or elevation context is a different profile outright. The MCP server is
// launched by the MCP client; Revit is launched by the user. When they diverge
// nothing errors - the server lists an empty directory and reports "no Revit has
// published a bridge" while Revit sits there with the add-in loaded.
//
// WHAT THESE TESTS CAN AND CANNOT DO, stated because the first version of this
// file got it wrong. That divergence CANNOT be simulated by setting
// %LOCALAPPDATA%: measured on .NET 8, Environment.GetFolderPath ignores the
// environment variable and goes to the Win32 known-folder API. A test that moved
// the variable would have passed against the old code too - the exact shape of
// "a passing test that never ran". So the property is pinned two ways that do
// fail against the previous commit: the root is asserted NOT to be under
// LocalApplicationData, and HorizunPathsSourceTests reads the shipped sources and
// fails if any state path is computed from it again.
//
// They set the environment, so the suite runs serially - see Parallelism.cs.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// Saves the variables the root can be resolved from and puts them back, whatever
    /// the test did or threw. Without this, one failing test leaves every later one
    /// resolving to a temp folder that has been deleted.
    /// </summary>
    internal sealed class EnvGuard : IDisposable
    {
        private static readonly string[] Names =
            { HorizunPaths.RootOverrideVariable, "USERPROFILE", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH" };

        private readonly string[] _saved;

        public EnvGuard()
        {
            _saved = new string[Names.Length];
            for (int i = 0; i < Names.Length; i++) _saved[i] = Environment.GetEnvironmentVariable(Names[i]);
        }

        public static void Set(string name, string value) => Environment.SetEnvironmentVariable(name, value);

        public void Dispose()
        {
            for (int i = 0; i < Names.Length; i++) Environment.SetEnvironmentVariable(Names[i], _saved[i]);
        }
    }

    public class HorizunPathsTests
    {
        /// <summary>
        /// THE REGRESSION, in the only form that can be observed from inside one
        /// process: the root is not the folder the old rule produced.
        ///
        /// Fails against the previous commit, where DataRoot was
        /// LocalApplicationData + "Horizun" by construction.
        /// </summary>
        [Fact]
        public void The_data_root_is_not_under_localappdata()
        {
            using (new EnvGuard())
            {
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, null);

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string root = HorizunPaths.DataRoot();

                Assert.False(string.IsNullOrEmpty(localAppData), "this machine must have a LocalApplicationData to test against");
                Assert.False(root.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase),
                    "the data root must not live under LocalApplicationData - it is redirected per package and " +
                    "per user, which is what split the server's view from Revit's. Root was " + root);

                // And it is where the brief asked for it to be.
                Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".horizun"),
                             root);
            }
        }

        /// <summary>
        /// Same for the four locations that actually carry state, because moving the
        /// root and leaving one of them behind is worse than not moving at all: a
        /// settings.json left in the old place is a machine whose posture silently
        /// reverted to the default.
        /// </summary>
        [Fact]
        public void No_state_location_is_under_localappdata()
        {
            using (new EnvGuard())
            {
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, null);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                foreach (string p in new[]
                         {
                             HorizunPaths.SettingsPath(), HorizunPaths.DiscoveryDir(),
                             HorizunPaths.JobsDir(), HorizunPaths.LogsDir(),
                             // The REAL accessors the add-in calls, not restatements of the
                             // rule: Job.Dir decides where a record is written and
                             // Settings.Path decides which file gates execute_python.
                             Job.Dir(), Settings.Path()
                         })
                {
                    Assert.False(p.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase),
                        p + " is still under LocalApplicationData");
                }
            }
        }

        [Fact]
        public void Every_state_location_lives_under_the_one_root()
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-root-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    // Relocating the root relocates all four together. That is the whole
                    // value of centralising them.
                    foreach (string p in new[]
                             {
                                 HorizunPaths.SettingsPath(), HorizunPaths.DiscoveryDir(),
                                 HorizunPaths.JobsDir(), HorizunPaths.LogsDir(),
                                 Job.Dir(), Settings.Path()
                             })
                    {
                        Assert.StartsWith(temp, p, StringComparison.OrdinalIgnoreCase);
                    }

                    // The add-in's accessors are not merely under the root - they ARE the
                    // shared ones. If either still computed its own location this fails.
                    Assert.Equal(HorizunPaths.JobsDir(), Job.Dir());
                    Assert.Equal(HorizunPaths.SettingsPath(), Settings.Path());
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        [Fact]
        public void A_job_record_is_actually_written_under_the_resolved_root()
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-root-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Job job = Job.Start("horizun_execute_python");
                    job.Finish("ok", null);

                    // Not "the path string looks right" - the file is on disk, under the
                    // root, in the folder the server's job reader is pointed at.
                    Assert.False(string.IsNullOrEmpty(job.Path));
                    Assert.True(File.Exists(job.Path), "the record should exist at " + job.Path);
                    Assert.StartsWith(HorizunPaths.JobsDir(), job.Path, StringComparison.OrdinalIgnoreCase);
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        /// <summary>
        /// %USERPROFILE% is INHERITABLE - a parent process can hand its child a
        /// different value, and the MCP server is a child of the MCP client. The
        /// known-folder API is not inheritable, so it is consulted first. This pins
        /// that order: it was the other way round when this file first shipped.
        /// </summary>
        [Fact]
        public void A_parent_process_cannot_move_the_root_with_userprofile()
        {
            using (new EnvGuard())
            {
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, null);
                string before = HorizunPaths.DataRoot();

                EnvGuard.Set("USERPROFILE", @"C:\Hijacked\Home");
                string after = HorizunPaths.DataRoot();

                Assert.Equal(before, after);
                Assert.DoesNotContain("Hijacked", after, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("SpecialFolder.UserProfile", HorizunPaths.ResolvedFrom());
            }
        }

        [Fact]
        public void The_override_wins_and_says_so_in_describe()
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-root-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Assert.Equal(temp, HorizunPaths.DataRoot());

                    // A root that came from an environment variable must never be
                    // indistinguishable from the default. A variable set for the server
                    // and not for Revit is the original failure wearing a new hat, and
                    // the only defence is that health SAYS where the root came from.
                    Assert.Equal(HorizunPaths.RootOverrideVariable, (string)HorizunPaths.Describe()["resolved_from"]);
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        [Fact]
        public void Describe_names_every_path_the_brief_asks_for_and_probes_each_one()
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-root-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    JObject d = HorizunPaths.Describe();
                    var access = (JObject)d["access"];
                    Assert.NotNull(access);

                    foreach (string key in new[]
                             { "data_root", "settings_path", "discovery_path", "jobs_path", "logs_path" })
                    {
                        Assert.True(d[key] != null && !string.IsNullOrEmpty((string)d[key]), key + " must be reported");

                        var probe = (JObject)access[key];
                        Assert.True(probe != null, key + " must be probed, not omitted");
                        Assert.Equal((string)d[key], (string)probe["path"]);
                        Assert.True((bool)probe["readable"], key + " should be readable under a temp root");
                        Assert.True((bool)probe["writable"], key + " should be writable under a temp root");
                    }
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        [Fact]
        public void A_root_that_cannot_be_created_is_reported_unwritable_with_the_reason()
        {
            using (new EnvGuard())
            {
                // A FILE where the directory should be. Directory.CreateDirectory cannot
                // succeed, which is the shape of every real version of this: a read-only
                // volume, a full disk, a denied ACL.
                string file = Path.Combine(Path.GetTempPath(), "hz-root-" + Guid.NewGuid().ToString("N") + ".not-a-dir");
                File.WriteAllText(file, "occupied");
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, file);
                try
                {
                    var probe = (JObject)((JObject)HorizunPaths.Describe()["access"])["data_root"];

                    // The value of reporting this is that it turns "Horizun does not work"
                    // into one sentence naming the path and the OS error.
                    Assert.False((bool)probe["writable"]);
                    Assert.False(string.IsNullOrEmpty((string)probe["error"]));
                }
                finally { try { File.Delete(file); } catch { } }
            }
        }

        [Fact]
        public void Probing_a_settings_file_does_not_destroy_it()
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-root-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Directory.CreateDirectory(temp);
                    const string content = "{\"enable_execute_python\": true}";
                    File.WriteAllText(HorizunPaths.SettingsPath(), content);

                    PathProbe p = HorizunPaths.ProbeFile(HorizunPaths.SettingsPath());

                    Assert.True(p.Readable);
                    Assert.True(p.Writable);
                    // The probe opens for write to prove it can. Opening with Create or
                    // Truncate would prove the same thing by destroying the machine's
                    // posture - a health check that turns execute_python back off.
                    Assert.Equal(content, File.ReadAllText(HorizunPaths.SettingsPath()));
                    Assert.True(Settings.ExecutePythonEnabled, "the probe must leave the setting readable and true");
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        [Fact]
        public void An_absent_settings_file_is_not_an_error()
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-root-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    PathProbe p = HorizunPaths.ProbeFile(HorizunPaths.SettingsPath());

                    // Absence is the shipped default and the safe one. Reporting it as a
                    // fault would send people looking for a problem that is not there.
                    Assert.False(p.Exists);
                    Assert.True(p.Writable);
                    Assert.Null(p.Error);
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }
    }

    /// <summary>
    /// The regression pinned where it can actually be caught: in the SOURCE.
    ///
    /// The behavioural tests above cannot fail on a second copy of the old rule
    /// appearing in a file they do not import - and that is exactly how there came to
    /// be seven of them. This reads what ships.
    ///
    /// Same technique as ConfirmationRoundTripTests, for the same reason: the
    /// invariant is about the code that is deployed, and no amount of exercising one
    /// entry point proves the other six do not compute their own answer.
    /// </summary>
    public class HorizunPathsSourceTests
    {
        private static string SrcDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit", "Core")))
                d = d.Parent;
            Assert.True(d != null, "Could not locate src/ from " + AppContext.BaseDirectory);
            return Path.Combine(d.FullName, "src");
        }

        [Fact]
        public void No_shipped_source_computes_a_state_path_from_localappdata()
        {
            var offenders = new System.Collections.Generic.List<string>();

            foreach (string path in Directory.EnumerateFiles(SrcDir(), "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;

                // HorizunPaths itself names it once, in LegacyDataRoot, to REPORT that
                // the old folder still holds files. That is the one legitimate mention:
                // nothing reads state through it.
                if (Path.GetFileName(path) == "HorizunPaths.cs") continue;

                foreach (string line in File.ReadAllLines(path))
                {
                    string code = line.TrimStart();
                    if (code.StartsWith("//") || code.StartsWith("///")) continue;   // prose may discuss it
                    if (code.Contains("SpecialFolder.LocalApplicationData"))
                        offenders.Add(Path.GetFileName(path) + ": " + code.Trim());
                }
            }

            Assert.True(offenders.Count == 0,
                "State paths must come from HorizunPaths, which both halves share. These compute their own from " +
                "LocalApplicationData, a folder the MCP server and Revit can resolve differently:\n  " +
                string.Join("\n  ", offenders));
        }

        /// <summary>
        /// The other half of the same rule. A file that never says
        /// "LocalApplicationData" but rebuilds the LAYOUT by hand -
        /// Combine(someRoot, "jobs") instead of HorizunPaths.JobsDir() - has forked
        /// the answer just as effectively, and the next change to the layout moves one
        /// of them and not the other.
        ///
        /// SCOPED TO THE FOUR SHARED LOCATIONS, on purpose. The first draft flagged
        /// any "Horizun" folder name and caught CaptureViewCommand writing exported
        /// images to %TEMP%\Horizun\captures - which is not this problem. Those are
        /// transient output whose absolute path is RETURNED to the caller, so nothing
        /// re-derives it at the other end and there is nothing to disagree about. The
        /// rule is about state two processes locate INDEPENDENTLY.
        /// </summary>
        [Fact]
        public void No_shipped_source_rebuilds_the_shared_layout_by_hand()
        {
            string[] sharedNames = { "\"jobs\"", "\"discovery\"", "\"logs\"", "\"settings.json\"" };
            var offenders = new System.Collections.Generic.List<string>();

            foreach (string path in Directory.EnumerateFiles(SrcDir(), "*.cs", SearchOption.AllDirectories))
            {
                if (path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                // HorizunPaths IS the layout. It is the one file allowed to say so.
                if (Path.GetFileName(path) == "HorizunPaths.cs") continue;

                foreach (string line in File.ReadAllLines(path))
                {
                    string code = line.TrimStart();
                    if (code.StartsWith("//") || code.StartsWith("///")) continue;
                    if (!code.Contains("Path.Combine")) continue;
                    foreach (string n in sharedNames)
                        if (code.Contains(n)) offenders.Add(Path.GetFileName(path) + ": " + code.Trim());
                }
            }

            Assert.True(offenders.Count == 0,
                "The shared layout belongs to HorizunPaths - call JobsDir/DiscoveryDir/LogsDir/SettingsPath. " +
                "These build it themselves:\n  " + string.Join("\n  ", offenders));
        }
    }
}
