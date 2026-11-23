using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class ParentLifetimeWireTests
    {
        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);

        private static ProcessStartInfo ServerInfo()
        {
            var info = new ProcessStartInfo(JsonRpcErrorCodeTests.ServerExe())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.Environment[Horizun.Revit.Core.HorizunPaths.RootOverrideVariable] = Path.Combine(Path.GetTempPath(), "horizun-lifetime-" + Guid.NewGuid().ToString("N"));
            return info;
        }

        [Fact]
        public void Closing_stdin_stops_a_real_server()
        {
            using (var server = Process.Start(ServerInfo()))
            {
                try
                {
                    server.StandardInput.Close();
                    Assert.True(server.WaitForExit(15000), "Server did not exit after EOF");
                    Assert.Equal(0, server.ExitCode);
                }
                finally { if (!server.HasExited) server.Kill(); }
            }
        }

        [Fact]
        public async Task Dead_launcher_stops_server_even_when_input_remains_open()
        {
            if (!OperatingSystem.IsWindows()) return;
            string script = "import os,subprocess,sys,time; " +
                "p=subprocess.Popen([os.environ['HZ_TEST_SERVER']],stdin=sys.stdin,stdout=sys.stdout,stderr=sys.stderr,close_fds=False,creationflags=0x08000000); " +
                "print(p.pid,flush=True); time.sleep(120)";
            var launcherInfo = new ProcessStartInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python", "Python312", "python.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            launcherInfo.ArgumentList.Add("-u");
            launcherInfo.ArgumentList.Add("-c");
            launcherInfo.ArgumentList.Add(script);
            launcherInfo.Environment["HZ_TEST_SERVER"] = JsonRpcErrorCodeTests.ServerExe();
            launcherInfo.Environment[Horizun.Revit.Core.HorizunPaths.RootOverrideVariable] = Path.Combine(Path.GetTempPath(), "horizun-lifetime-" + Guid.NewGuid().ToString("N"));
            using (var launcher = Process.Start(launcherInfo))
            {
                Process server = null;
                try
                {
                    string pidLine = await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    server = Process.GetProcessById(int.Parse(pidLine));
                    var identity = Horizun.Revit.Core.ClientProcessIdentity.Capture(server.Id);
                    Assert.Equal(launcher.Id, identity.ParentPid);
                    Assert.False(Horizun.Revit.Core.ClientProcessIdentity.IsOrphan(identity.ParentAlive()));
                    launcher.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
                    launcher.StandardInput.Flush();
                    string response = await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Contains("\"id\":1", response);
                    launcher.Kill();
                    Assert.True(launcher.WaitForExit(10000));
                    Assert.True(Horizun.Revit.Core.ClientProcessIdentity.IsOrphan(identity.ParentAlive()));
                    Assert.True(server.WaitForExit(15000), "Server survived its parent with stdin open");
                }
                finally
                {
                    if (!launcher.HasExited) launcher.Kill();
                    if (server != null) { if (!server.HasExited) server.Kill(); server.Dispose(); }
                }
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Unknown_or_disabled_parent_keeps_server_alive_until_eof(bool missingAtCapture)
        {
            if (!OperatingSystem.IsWindows()) return;
            // Suspended creation ensures the launcher has exited before Capture runs.
            string script = "import os,subprocess,sys,time; " +
                "p=subprocess.Popen([os.environ['HZ_TEST_SERVER']],stdin=sys.stdin,stdout=sys.stdout,stderr=sys.stderr,close_fds=False,creationflags=" +
                (missingAtCapture ? "0x08000004" : "0x08000000") + "); " +
                "print(p.pid,flush=True); " + (missingAtCapture ? "sys.exit(0)" : "time.sleep(120)");
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Python", "Python312", "python.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add("-u"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(script);
            info.Environment["HZ_TEST_SERVER"] = JsonRpcErrorCodeTests.ServerExe();
            string root = Path.Combine(Path.GetTempPath(), "horizun-lifetime-" + Guid.NewGuid().ToString("N"));
            info.Environment[Horizun.Revit.Core.HorizunPaths.RootOverrideVariable] = root;
            if (!missingAtCapture) info.Environment["HORIZUN_PARENT_WATCH"] = "0";
            else info.Environment.Remove("HORIZUN_PARENT_WATCH");
            using (var launcher = Process.Start(info))
            {
                Process server = null;
                try
                {
                    string pid = await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    server = Process.GetProcessById(int.Parse(pid));
                    if (missingAtCapture)
                    {
                        Assert.True(launcher.WaitForExit(10000));
                        Assert.Equal(0, NtResumeProcess(server.Handle));
                    }
                    else
                    {
                        launcher.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
                        launcher.StandardInput.Flush();
                        Assert.Contains("\"id\":1", await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
                        launcher.Kill();
                        Assert.True(launcher.WaitForExit(10000));
                    }
                    await Task.Delay(3000);
                    Assert.False(server.HasExited);
                    launcher.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
                    launcher.StandardInput.Flush();
                    Assert.Contains("\"id\":2", await launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
                    string log = File.ReadAllText(Path.Combine(root, "logs", "server.log"));
                    Assert.Single(log.Split('\n'), line => line.Contains("parent not watched:"));
                    Assert.Contains(missingAtCapture ? "parent process does not exist" : "disabled by HORIZUN_PARENT_WATCH=0", log);
                    launcher.StandardInput.Close();
                    Assert.True(server.WaitForExit(15000), "Server did not exit after EOF");
                }
                finally
                {
                    if (!launcher.HasExited) launcher.Kill();
                    if (server != null) { if (!server.HasExited) server.Kill(); server.Dispose(); }
                }
            }
        }
    }
}
