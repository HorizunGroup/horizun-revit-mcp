using System;
using System.IO;
using Xunit;
using Horizun.Revit.Core;

namespace Horizun.Core.Tests
{
    public class PythonDependencyIsolationTests
    {
        private static string Source(string relative)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Horizun.Revit"))) d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(d.FullName, "src", "Horizun.Revit", relative));
        }

        [Fact]
        public void Startup_installs_private_python_resolution_before_registering_commands()
        {
            string app = Source("App.cs");
            int install = app.IndexOf("PythonDependencyIsolation.Install()", StringComparison.Ordinal);
            int register = app.IndexOf("RegisterCommands(_dispatcher)", StringComparison.Ordinal);
            Assert.True(install >= 0 && register >= 0 && install < register);
        }

        [Fact]
        public void Resolver_requires_exact_identity_and_never_substitutes_another_version()
        {
            string resolver = Source("Core/PythonDependencyIsolation.cs");
            Assert.Contains("AssemblyName.ReferenceMatchesDefinition", resolver);
            Assert.Contains("AssemblyLoadContext", resolver);
            Assert.Contains("Assembly.Load(File.ReadAllBytes", resolver);
            Assert.DoesNotContain("context.LoadFromAssemblyName(request)", resolver);
        }

        [Fact]
        public void Health_reports_permission_and_load_failure_separately()
        {
            string health = Source("Commands/HealthCommand.cs");
            Assert.Contains("PythonRuntimeHealth.Snapshot", health);
        }

        [Fact]
        public void Health_distinguishes_all_four_states_and_preserves_failure_cause()
        {
            var runtime = new RuntimeWarmup();
            Assert.Equal("not_authorized", (string)PythonRuntimeHealth.Snapshot(false, runtime)["state"]);
            Assert.Equal("cold", (string)PythonRuntimeHealth.Snapshot(true, runtime)["state"]);
            runtime.Begin();
            runtime.Failed(new FileLoadException("private DLR refused"));
            var failed = PythonRuntimeHealth.Snapshot(true, runtime);
            Assert.Equal("not_loadable", (string)failed["state"]);
            Assert.Contains("private DLR refused", (string)failed["last_error"]);
            runtime.Begin();
            runtime.Ready();
            Assert.Equal("warm", (string)PythonRuntimeHealth.Snapshot(true, runtime)["state"]);
        }
    }
}
