// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The contract advertises; the add-in answers. These tests hold the two lists
// against each other with Contract.PluginCommands as the ONLY source of what
// must be registered - there is no third list here to go stale.
//
// The registry itself lives in App.RegisterCommands, which is Revit-bound and
// cannot be instantiated in this suite, so the registrations are read from the
// source the way a reviewer would: every `d.Register(new XCommand(` line, and
// each class's declared Name. Deleting one of those lines makes a test here
// fail; pasting one twice makes a different test fail; renaming a Name string
// away from the contract makes a third fail.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Server.Tests
{
    public class RegistryContractTests
    {
        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "src", "Horizun.Revit", "App.cs"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
        }

        private static string AppSource() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "App.cs"));

        /// <summary>class name -> wire name, read from every command source file.</summary>
        private static Dictionary<string, string> NamesByClass()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string dir = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands");
            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                foreach (KeyValuePair<string, string> pair in RegistryContract.CommandNamesInSource(File.ReadAllText(file)))
                {
                    Assert.False(map.ContainsKey(pair.Key), pair.Key + " is declared in two source files.");
                    Assert.NotNull(pair.Value);
                    map[pair.Key] = pair.Value;
                }
            return map;
        }

        /// <summary>The wire names the add-in registers, in registration order, repeats included.</summary>
        private static List<string> RegisteredNames()
        {
            Dictionary<string, string> names = NamesByClass();
            var result = new List<string>();
            foreach (string cls in RegistryContract.RegistrationsInSource(AppSource()))
            {
                string name;
                Assert.True(names.TryGetValue(cls, out name),
                    cls + " is registered in App.cs but no file under src/Horizun.Revit/Commands declares its Name.");
                result.Add(name);
            }
            return result;
        }

        // ------------------------------------------------------ the real tree

        [Fact]
        public void Every_plugin_command_the_contract_advertises_is_registered_exactly_once()
        {
            RegistryContract.Report r = RegistryContract.Compare(RegisteredNames(), Contract.PluginCommands);
            Assert.True(r.Clean, r.Describe());
            Assert.Equal(Contract.PluginCommands.Count(), r.Registered);
        }

        [Fact]
        public void Nothing_is_registered_that_the_contract_does_not_name()
        {
            RegistryContract.Report r = RegistryContract.Compare(RegisteredNames(), Contract.PluginCommands);
            Assert.Empty(r.Unadvertised);
        }

        [Fact]
        public void Host_resident_tools_are_not_expected_from_the_addin()
        {
            // Command == null is the whole declaration of "answered in the server". The
            // registry comparison must not ask the add-in for those.
            IEnumerable<string> hostResident = Contract.All.Where(c => string.IsNullOrEmpty(c.Command)).Select(c => c.Name);
            Assert.NotEmpty(hostResident);
            foreach (string name in hostResident)
                Assert.DoesNotContain(name, Contract.PluginCommands);
        }

        [Fact]
        public void The_registration_count_matches_the_contract_count()
        {
            // 72 today. The number is not pinned; the equality is.
            Assert.Equal(Contract.PluginCommands.Count(), RegistryContract.RegistrationsInSource(AppSource()).Count);
        }

        // ------------------------------------------- the failures it exists for

        [Fact]
        public void Removing_a_registration_is_reported_as_missing()
        {
            List<string> names = RegisteredNames();
            string victim = names.First(n => n == "horizun_clash");
            names.Remove(victim);
            RegistryContract.Report r = RegistryContract.Compare(names, Contract.PluginCommands);
            Assert.False(r.Clean);
            Assert.Equal(new[] { "horizun_clash" }, r.Missing);
            Assert.Contains("advertised but NOT registered: horizun_clash", r.Describe());
        }

        [Fact]
        public void Duplicating_a_registration_is_reported_as_a_duplicate()
        {
            List<string> names = RegisteredNames();
            names.Add("horizun_quantities");
            RegistryContract.Report r = RegistryContract.Compare(names, Contract.PluginCommands);
            Assert.False(r.Clean);
            Assert.Equal(new[] { "horizun_quantities" }, r.Duplicates);
            Assert.Empty(r.Missing);
        }

        [Fact]
        public void Duplicating_a_register_line_in_the_source_is_seen_as_a_duplicate()
        {
            string src = AppSource();
            string line = "d.Register(new ClashCommand());";
            Assert.Contains(line, src);
            string doubled = src.Replace(line, line + "\n            " + line);
            List<string> classes = RegistryContract.RegistrationsInSource(doubled);
            Assert.Equal(2, classes.Count(c => c == "ClashCommand"));
        }

        [Fact]
        public void A_commented_out_register_line_does_not_count()
        {
            string src = "d.Register(new AlphaCommand());\n// d.Register(new BetaCommand());\nd.Register(new GammaCommand(d.ResolveCommand));";
            Assert.Equal(new[] { "AlphaCommand", "GammaCommand" }, RegistryContract.RegistrationsInSource(src));
        }

        [Fact]
        public void A_name_that_differs_only_by_case_is_a_mismatch_not_a_match()
        {
            RegistryContract.Report r = RegistryContract.Compare(
                new[] { "horizun_Clash", "horizun_health" }, new[] { "horizun_clash", "horizun_health" });
            Assert.False(r.Clean);
            Assert.Equal(new[] { "horizun_clash" }, r.CaseMismatches);
            Assert.Empty(r.Missing);
            Assert.Empty(r.Unadvertised);
        }

        [Fact]
        public void A_registered_command_the_contract_does_not_name_is_unadvertised()
        {
            RegistryContract.Report r = RegistryContract.Compare(
                new[] { "horizun_health", "horizun_secret" }, new[] { "horizun_health" });
            Assert.Equal(new[] { "horizun_secret" }, r.Unadvertised);
            Assert.Contains("registered but not in the contract", r.Describe());
        }

        [Fact]
        public void Admit_refuses_a_second_registration_of_one_name_with_both_in_the_message()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RegistryContract.Admit(set, "horizun_clash");
            var ex = Assert.Throws<InvalidOperationException>(() => RegistryContract.Admit(set, "horizun_clash"));
            Assert.Contains("horizun_clash", ex.Message);
            Assert.Contains("registered twice", ex.Message);
            Assert.Throws<InvalidOperationException>(() => RegistryContract.Admit(set, "HORIZUN_CLASH"));
            Assert.Throws<InvalidOperationException>(() => RegistryContract.Admit(set, " "));
        }

        [Fact]
        public void The_dispatcher_registers_through_Admit_and_the_app_verifies_at_startup()
        {
            string dispatcher = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "Dispatcher.cs"));
            Assert.Contains("RegistryContract.Admit(_registeredNames, command.Name)", dispatcher);
            Assert.DoesNotContain("if (command == null) return;", dispatcher);
            Assert.Contains("RegistryContract.Compare(_registrationAttempts, Contract.PluginCommands)", dispatcher);

            string app = AppSource();
            Assert.Contains("_dispatcher.VerifyAgainstContract()", app);
            Assert.Contains("RegistryContract.Startup = registry", app);

            string health = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "HealthCommand.cs"));
            Assert.Contains("registry = RegistryContract.HealthBlock()", health);
        }

        [Fact]
        public void The_health_block_names_its_own_absence_before_startup_ran()
        {
            RegistryContract.Report keep = RegistryContract.Startup;
            try
            {
                RegistryContract.Startup = null;
                Assert.Null(RegistryContract.HealthBlock()["clean"].Type == Newtonsoft.Json.Linq.JTokenType.Null ? null : "x");
                RegistryContract.Startup = RegistryContract.Compare(new[] { "a" }, new[] { "a", "b" });
                Assert.False((bool)RegistryContract.HealthBlock()["clean"]);
                Assert.Equal("b", (string)RegistryContract.HealthBlock()["missing"][0]);
            }
            finally { RegistryContract.Startup = keep; }
        }

        [Fact]
        public void Registry_drift_is_applied_and_leaves_a_breadcrumb_saying_why()
        {
            // The verdict is not only published. A drifted add-in still publishes its
            // REAL command list, so the server withholds exactly the affected tools -
            // and it records why, because a tool that vanished with no reason is its
            // own kind of failure.
            string app = AppSource();
            Assert.Contains("Discovery.WriteStartupFailure(_year, why)", app);
            Assert.Contains("Discovery.ClearStartupFailure(_year)", app);
            Assert.Contains("the add-in did not finish starting: ", app);

            string discovery = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "Discovery.cs"));
            Assert.Contains("public static void WriteStartupFailure(", discovery);
            // The breadcrumb must NOT be mistaken for a bridge: the server globs
            // revit-*.json for those, and a failure is the opposite of one.
            Assert.Contains("\"startup-failure-\"", discovery);
            Assert.DoesNotContain("\"revit-failure", discovery);
        }

        [Fact]
        public void The_breadcrumb_name_cannot_be_read_as_a_discovery_file()
        {
            // A glob is a contract too. revit-*.json is what the server reads as a
            // bridge; the failure file must fall outside it whatever the year or pid.
            // Discovery.cs is Revit-bound and is not linked into this suite, so the
            // name is read from the one place that produces it.
            string discovery = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "Discovery.cs"));
            Match m = Regex.Match(discovery, @"FailureFileName\(string year\) =>\s*""([^""]+)""");
            Assert.True(m.Success, "Discovery.FailureFileName no longer builds its name from a literal prefix.");
            string prefix = m.Groups[1].Value;
            Assert.Equal("startup-failure-", prefix);
            Assert.False(prefix.StartsWith("revit-", StringComparison.Ordinal),
                "the failure breadcrumb would be globbed as a bridge by PipeClient.");
        }

        [Fact]
        public void The_server_explains_a_missing_bridge_with_that_breadcrumb()
        {
            string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Server", "Program.cs"));
            Assert.Contains("PipeClient.StartupFailures(year)", program);
            string pipe = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Server", "PipeClient.cs"));
            Assert.Contains("startup-failure-*.json", pipe);
            // A complaint from a Revit that is gone is not a diagnosis.
            Assert.Contains("BarePidExists(pid)", pipe);
        }

        [Fact]
        public void The_addin_hashes_the_assembly_Revit_actually_loaded()
        {
            // A live run used to hash a deployment path it built by hand - and the path
            // was wrong, so every campaign recorded addin_sha256: null.
            string build = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Core", "Build.cs"));
            Assert.Contains("public static Newtonsoft.Json.Linq.JObject Assembly_", build);
            Assert.Contains("asm.Location", build);
            string health = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands", "HealthCommand.cs"));
            Assert.Contains("addin_assembly = Build.Assembly_", health);

            string lib = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "live", "horizun-live.lib.ps1"));
            Assert.Contains("Get-HzProp $health 'addin_assembly'", lib);
            Assert.Contains("addin_sha256_source", lib);
            // and the fallback path is the REAL installed one, with Addins in it
            Assert.Contains(@"Autodesk\Revit\Addins\$year\Horizun\Horizun.Revit.dll", lib);
        }

        [Fact]
        public void No_command_class_carries_its_own_copy_of_the_parameter_schema()
        {
            // Ten classes used to restate the contract's InputSchema by hand under a
            // property nothing read. A copy invites editing the wrong one.
            string dir = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands");
            var offenders = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("ParametersSchema"))
                .Select(Path.GetFileName).ToList();
            Assert.Empty(offenders);
        }
    }
}
