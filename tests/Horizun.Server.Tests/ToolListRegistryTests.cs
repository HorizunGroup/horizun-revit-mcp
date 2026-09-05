// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// A TOOL A CLIENT CAN SEE IS A TOOL A CLIENT WILL CALL.
//
// The per-call guard has always refused a command the loaded add-in does not
// register, and refused a server and add-in built from different contracts. But
// tools/list advertised all of them anyway, so the refusal arrived in the middle
// of somebody's work instead of as an absence they could have planned around.
// These tests hold the list to the same answer the call gives - and hold the
// three things it must NOT do: never withhold a host-resident tool, never read
// "unknown" as "absent", and never keep a third list of its own.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ToolListRegistryTests : IDisposable
    {
        private readonly Func<Discovered> _saved = Tools.LiveBridge;

        public void Dispose() { Tools.LiveBridge = _saved; }

        /// <summary>An add-in that publishes exactly these plugin commands.</summary>
        private static Discovered Bridge(IEnumerable<string> commands, string contractHash = null)
        {
            return new Discovered
            {
                Year = "2026",
                Pid = 4242,
                AddinVersion = "1.2.0",
                Schema = 3,
                Commands = commands == null ? null : commands.ToList(),
                ContractHash = contractHash ?? Contract.Hash
            };
        }

        private static ToolDef Def(string name) => Tools.Find(name);

        private static ToolDef AnyPluginTool() =>
            Tools.All.First(t => !string.IsNullOrEmpty(t.Command) && t.Host == null);

        private static ToolDef AnyHostTool() =>
            Tools.All.First(t => t.Host != null);

        // ------------------------------------------------------- what is listed

        [Fact]
        public void A_plugin_tool_the_addin_registers_is_advertised()
        {
            ToolDef t = AnyPluginTool();
            Assert.Null(Tools.WithheldReason(t, Bridge(new[] { t.Command })));
        }

        [Fact]
        public void A_plugin_tool_the_addin_does_NOT_register_is_withheld_and_says_which_command()
        {
            ToolDef t = AnyPluginTool();
            string why = Tools.WithheldReason(t, Bridge(new[] { "horizun_something_else" }));
            Assert.NotNull(why);
            Assert.Contains(t.Command, why);
            Assert.Contains("does not register", why);
            Assert.Contains("install.ps1", why);   // it says what to do about it
        }

        [Fact]
        public void A_host_resident_tool_is_never_withheld_because_Revit_is_not_involved()
        {
            ToolDef t = AnyHostTool();
            Assert.Null(Tools.WithheldReason(t, Bridge(new string[0])));
            Assert.Null(Tools.WithheldReason(t, Bridge(new string[0], "a-different-contract")));
        }

        [Fact]
        public void No_bridge_discovered_withholds_nothing_because_unknown_is_not_absent()
        {
            foreach (ToolDef t in Tools.All) Assert.Null(Tools.WithheldReason(t, null));
        }

        [Fact]
        public void An_addin_that_published_no_command_list_withholds_nothing()
        {
            // Schema-1 discovery: it never said what it has, which is not the same as
            // saying it has nothing. The codebase refuses that substitution everywhere.
            Discovered old = Bridge(null);
            foreach (ToolDef t in Tools.All) Assert.Null(Tools.WithheldReason(t, old));
        }

        [Fact]
        public void A_contract_hash_mismatch_withholds_every_plugin_tool_and_no_host_tool()
        {
            // Two builds that disagree about the contract cannot exchange arguments
            // safely, so every plugin tool is unusable until they are redeployed.
            Discovered drifted = Bridge(Contract.PluginCommands.ToList(), "0123456789abcdef01234567");
            foreach (ToolDef t in Tools.All)
            {
                string why = Tools.WithheldReason(t, drifted);
                if (t.Host != null) { Assert.Null(why); continue; }
                Assert.NotNull(why);
                Assert.Contains("DIFFERENT", why);
                Assert.Contains(Contract.Hash, why);
            }
        }

        // ------------------------------------------------- the published list

        [Fact]
        public void The_published_list_omits_the_withheld_tool_and_keeps_the_rest()
        {
            ToolDef victim = Def("horizun_clash");
            Assert.NotNull(victim);
            var registered = Contract.PluginCommands.Where(c => c != victim.Command).ToList();
            Tools.LiveBridge = () => Bridge(registered);

            var names = Tools.List(false).OfType<JObject>().Select(o => (string)o["name"]).ToList();
            Assert.DoesNotContain("horizun_clash", names);
            Assert.Contains("horizun_health", names);          // still there
            Assert.Contains("horizun_job_status", names);      // host-resident, untouched

            JArray withheld = Tools.Withheld();
            Assert.Single(withheld);
            Assert.Equal("horizun_clash", (string)withheld[0]["name"]);
            Assert.Equal(victim.Command, (string)withheld[0]["command"]);
        }

        [Fact]
        public void With_every_command_registered_nothing_is_withheld()
        {
            Tools.LiveBridge = () => Bridge(Contract.PluginCommands.ToList());
            Assert.Empty(Tools.Withheld());
            var names = Tools.List(false).OfType<JObject>().Select(o => (string)o["name"]).ToList();
            Assert.Contains("horizun_clash", names);
        }

        [Fact]
        public void A_bridge_resolver_that_throws_withholds_nothing_rather_than_failing_the_list()
        {
            // Listing tools must not fail because Revit is busy or a file is locked.
            Tools.LiveBridge = () => { throw new InvalidOperationException("discovery unreadable"); };
            Assert.Empty(Tools.Withheld());
            Assert.NotEmpty(Tools.List(false));
        }

        [Fact]
        public void The_tool_list_monitor_sees_the_set_change_when_a_command_disappears()
        {
            // The monitor's snapshot is the published names, so a bridge that stops
            // registering a command is a tools/list_changed rather than a surprise.
            Tools.LiveBridge = () => Bridge(Contract.PluginCommands.ToList());
            string before = ToolListMonitor.Capture();
            Tools.LiveBridge = () => Bridge(Contract.PluginCommands.Where(c => c != "horizun_clash").ToList());
            string after = ToolListMonitor.Capture();
            Assert.NotEqual(before, after);
            Assert.Contains("horizun_clash", before);
            Assert.DoesNotContain("horizun_clash", after);
        }

        // --------------------------------------------------- the diagnostic

        [Fact]
        public void Build_identity_publishes_the_withheld_tools_and_why()
        {
            ToolDef victim = Def("horizun_clash");
            Tools.LiveBridge = () => Bridge(Contract.PluginCommands.Where(c => c != victim.Command).ToList());

            JObject read = McpResources.Read(new JObject { ["uri"] = "horizun://build/identity" });
            var body = JObject.Parse((string)read["contents"][0]["text"]);
            JObject registry = (JObject)body["registry"];
            Assert.Equal(1, (int)registry["withheld_count"]);
            Assert.Equal("horizun_clash", (string)registry["withheld"][0]["name"]);
            Assert.Contains("NOT in tools/list", (string)registry["means"]);
        }

        [Fact]
        public void Build_identity_says_so_when_nothing_is_withheld()
        {
            Tools.LiveBridge = () => null;
            JObject read = McpResources.Read(new JObject { ["uri"] = "horizun://build/identity" });
            var body = JObject.Parse((string)read["contents"][0]["text"]);
            JObject registry = (JObject)body["registry"];
            Assert.Equal(0, (int)registry["withheld_count"]);
            Assert.Contains("unknown is not absent", (string)registry["means"]);
        }

        // ------------------------------------------------------- one source

        [Fact]
        public void The_commands_checked_are_exactly_the_contracts_plugin_commands()
        {
            // There is no third list. Every tool the filter can withhold is a tool
            // whose Command the contract names, and every such command is checked.
            var checkable = Tools.All.Where(t => t.Host == null && !string.IsNullOrEmpty(t.Command))
                                     .Select(t => t.Command).OrderBy(c => c, StringComparer.Ordinal).ToList();
            var contract = Contract.PluginCommands.OrderBy(c => c, StringComparer.Ordinal).ToList();
            Assert.Equal(contract, checkable);
        }
    }
}
