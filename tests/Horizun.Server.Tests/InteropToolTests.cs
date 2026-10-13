// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// 3.0.0 interoperability, the host-resident half: the project registry operations
// of horizun_project_context (decision D0), horizun_exchange (emit / verify /
// correction plans) and the cross-app-coordination prompt. Real disposable files
// under a settings root of their own - never the machine's.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public sealed class InteropToolTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _settingsRoot;
        private readonly string _savedRoot;

        private const string Creation = "0b5c2f8e-1111-4c3a-9d2b-7e1f00000001";

        public InteropToolTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-interop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _settingsRoot = Path.Combine(Path.GetTempPath(), "hz-interop-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_settingsRoot);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _settingsRoot);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_dir, true); } catch { }
            try { Directory.Delete(_settingsRoot, true); } catch { }
        }

        private void Profile(string profile)
            => File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""" + profile + @"""}");

        private string Registry => Path.Combine(_dir, "project-integrations.json");

        /// <summary>Shaped like get_document_info's model_identity, as the agent relays it.</summary>
        private static JObject Identity(string creation, string version = "2026") => new JObject
        {
            ["creation_guid"] = creation, ["central_guid"] = null, ["cloud_model_guid"] = null, ["unique_document_guid"] = null,
            ["project_info_unique_id"] = "4a1b0000-aaaa-bbbb-cccc-000000000000-0000aaaa", ["cloud_project_guid"] = null,
            ["revit_version"] = version, ["title"] = "ARQ", ["path"] = @"C:\p\ARQ.rvt",
            ["binding_key"] = creation == null ? null : "creation_guid", ["identity_proven"] = true, ["identity_note"] = null
        };

        private static JObject Pc(JObject args) => ProjectContext.Handle(args);

        private JObject RegisterArgs(JObject evidence, bool dryRun) => new JObject
        {
            ["operation"] = "register_model", ["integrations_path"] = Registry, ["project_code"] = "P01", ["dry_run"] = dryRun,
            ["model"] = new JObject { ["name"] = "Architecture", ["discipline"] = "architecture", ["evidence"] = evidence }
        };

        // ---- the registry ----------------------------------------------------------

        [Fact]
        public void Register_rehearses_by_default_then_writes_only_under_full_write_and_reads_back()
        {
            JObject rehearsal = Pc(RegisterArgs(Identity(Creation), dryRun: true));
            Assert.False((bool)rehearsal["written"]);
            Assert.False(File.Exists(Registry));
            Assert.StartsWith("mdl-", (string)rehearsal["model"]["model_ref"]);

            Profile("safe_write");
            Assert.Throws<ToolRefusal>(() => Pc(RegisterArgs(Identity(Creation), dryRun: false)));
            Assert.False(File.Exists(Registry));

            Profile("full_write");
            JObject written = Pc(RegisterArgs(Identity(Creation), dryRun: false));
            Assert.True((bool)written["written"]);
            Assert.Equal(ProjectContext.Sha256Hex(File.ReadAllBytes(Registry)), (string)written["verification"]["sha256"]);
            Assert.Empty(ProjectIntegrations.Validate(JObject.Parse(File.ReadAllText(Registry))));
            Assert.True((bool)written["model"]["identity_proven"]);
        }

        [Fact]
        public void A_model_with_project_information_only_is_never_registered_nor_resolved()
        {
            ToolRefusal r = Assert.Throws<ToolRefusal>(() => Pc(RegisterArgs(Identity(null), dryRun: true)));
            Assert.Contains("without strong evidence", r.Message);
            ToolRefusal q = Assert.Throws<ToolRefusal>(() => Pc(new JObject
            {
                ["operation"] = "resolve_model", ["integrations_path"] = Registry, ["evidence"] = Identity(null)
            }));
            Assert.Contains("without strong evidence", q.Message);
        }

        [Fact]
        public void A_caller_cannot_declare_its_evidence_proven_and_2023_is_never_proven()
        {
            JObject e = Identity(Creation, version: "2023");
            e["identity_proven"] = true;
            JObject rehearsal = Pc(RegisterArgs(e, dryRun: true));
            Assert.False((bool)rehearsal["model"]["identity_proven"]);
        }

        [Fact]
        public void A_registered_model_resolves_to_its_ref_and_is_not_registered_twice()
        {
            Profile("full_write");
            string modelRef = (string)Pc(RegisterArgs(Identity(Creation), dryRun: false))["model"]["model_ref"];
            JObject resolved = Pc(new JObject { ["operation"] = "resolve_model", ["integrations_path"] = Registry, ["evidence"] = Identity(Creation) });
            Assert.Equal("matched", (string)resolved["state"]);
            Assert.Equal(modelRef, (string)resolved["model_ref"]);
            Assert.True((bool)resolved["identity_proven"]);
            ToolRefusal again = Assert.Throws<ToolRefusal>(() => Pc(RegisterArgs(Identity(Creation), dryRun: true)));
            Assert.Contains("already registered as " + modelRef, again.Message);

            JObject other = Pc(new JObject { ["operation"] = "resolve_model", ["integrations_path"] = Registry, ["evidence"] = Identity(Guid.NewGuid().ToString()) });
            Assert.Equal("no_match", (string)other["state"]);
        }

        [Fact]
        public void The_registry_sits_beside_the_project_context_and_the_exchange_root_is_local_by_default()
        {
            JObject read = Pc(new JObject { ["operation"] = "integrations", ["path"] = Path.Combine(_dir, "project-context.json") });
            Assert.Equal(Registry, (string)read["integrations_path"]);
            Assert.False((bool)read["exists"]);
            Assert.Equal("default_local", (string)read["exchange_root_source"]);
            Assert.StartsWith(_settingsRoot, (string)read["exchange_root"]);
        }

        [Fact]
        public void An_invalid_registry_refuses_every_operation_on_it()
        {
            File.WriteAllText(Registry, @"{""schema"":""horizun.project-integrations/0"",""models"":[{""model_ref"":""mdl-aaaaaaaaaaaa"",""name"":""x"",""evidence"":{""project_info_unique_id"":""pi""}}]}");
            Assert.Throws<ToolRefusal>(() => Pc(new JObject { ["operation"] = "integrations", ["integrations_path"] = Registry }));
        }

        // ---- horizun_exchange ------------------------------------------------------------

        private string Payload()
        {
            string p = Path.Combine(_dir, "ledger.csv");
            File.WriteAllText(p, "issue_id,state\nC-1,open\n");
            return p;
        }

        private JObject EmitArgs(bool dryRun, string state = "verified") => new JObject
        {
            ["operation"] = "emit", ["kind"] = "coordination.ledger", ["dry_run"] = dryRun,
            ["files"] = new JArray(new JObject { ["path"] = Payload(), ["format"] = "horizun.coordination-ledger.csv/2", ["rows"] = 1 }),
            ["verification_state"] = state, ["project_code"] = "P01", ["exchange_root"] = Path.Combine(_dir, "exchange")
        };

        [Fact]
        public void A_wrap_by_the_server_is_at_most_self_reported_even_when_the_producer_said_verified()
        {
            JObject rehearsal = ExchangeTool.Handle(EmitArgs(true), default);
            Assert.False((bool)rehearsal["written"]);
            Assert.Equal("self_reported_verified", (string)rehearsal["manifest"]["verification"]["state"]);
            Assert.False((bool)rehearsal["manifest"]["verification"]["host_verified"]);
            Assert.Equal("verified", (string)rehearsal["state_capped_from"]);
            Assert.False(Directory.Exists(Path.Combine(_dir, "exchange")));
        }

        [Fact]
        public void Emit_writes_under_full_write_and_verify_lets_the_next_hop_continue()
        {
            Profile("read_only");
            Assert.Throws<ToolRefusal>(() => ExchangeTool.Handle(EmitArgs(false), default));
            Profile("full_write");
            JObject written = ExchangeTool.Handle(EmitArgs(false), default);
            Assert.True((bool)written["written"]);
            JObject v = ExchangeTool.Handle(new JObject
            {
                ["operation"] = "verify", ["manifest_path"] = (string)written["manifest_path"], ["kind"] = "coordination.ledger"
            }, default);
            Assert.Equal("ok", (string)v["integrity"]);
            Assert.True((bool)v["may_continue"]);
            JObject wrongKind = ExchangeTool.Handle(new JObject
            {
                ["operation"] = "verify", ["manifest_path"] = (string)written["manifest_path"], ["kind"] = "quantities.rows"
            }, default);
            Assert.False((bool)wrongKind["may_continue"]);
        }

        [Fact]
        public void Emit_attaches_the_registered_evidence_and_verify_proves_the_identity()
        {
            Profile("full_write");
            string modelRef = (string)Pc(RegisterArgs(Identity(Creation), dryRun: false))["model"]["model_ref"];
            JObject args = EmitArgs(false, "self_reported_verified");
            args["model_ref"] = modelRef;
            args["integrations_path"] = Registry;
            JObject written = ExchangeTool.Handle(args, default);
            Assert.Equal(Creation, (string)written["manifest"]["source"]["model_evidence"]["creation_guid"]);
            JObject v = ExchangeTool.Handle(new JObject
            {
                ["operation"] = "verify", ["manifest_path"] = (string)written["directory"], ["integrations_path"] = Registry
            }, default);
            Assert.Equal("proven", (string)v["identity"]["state"]);
            Assert.True((bool)v["may_continue"]);
        }

        [Fact]
        public void A_model_ref_the_registry_does_not_know_is_refused()
        {
            Profile("full_write");
            Pc(RegisterArgs(Identity(Creation), dryRun: false));
            JObject args = EmitArgs(true);
            args["model_ref"] = "mdl-ffffffffffff";
            args["integrations_path"] = Registry;
            Assert.Throws<ToolRefusal>(() => ExchangeTool.Handle(args, default));
        }

        [Fact]
        public void The_correction_plan_operations_judge_only_published_tools()
        {
            JObject plan = JObject.Parse(@"{ ""pasos"": [
              { ""id"": ""a"", ""motivo"": ""TB-002"", ""herramienta"": ""horizun_write_params_verified"",
                ""argumentos"": { ""target_document"": ""M"", ""dry_run"": true, ""writes"": [ { ""target"": ""project_info"", ""parameter"": ""PROJECT_NUMBER"", ""value"": ""P-1"" } ] } },
              { ""id"": ""b"", ""motivo"": ""Z"", ""herramienta"": ""horizun_execute_python"", ""argumentos"": { ""dry_run"": true } } ],
              ""manual"": [] }");
            JObject check = ExchangeTool.Handle(new JObject { ["operation"] = "correction_plan_check", ["plan"] = plan }, default);
            Assert.Equal("ready", (string)check["steps"][0]["state"]);
            Assert.Equal("not_permitted", (string)check["steps"][1]["state"]);
            Assert.Throws<ToolRefusal>(() => ExchangeTool.Handle(new JObject { ["operation"] = "correction_plan_reverify", ["plan"] = plan }, default));
        }

        // ---- contract and prompt ---------------------------------------------------------

        [Fact]
        public void The_exchange_tool_is_host_resident_external_on_request_and_in_coordination()
        {
            CommandContract c = Contract.Find("horizun_exchange");
            Assert.NotNull(c);
            Assert.Null(c.Command);
            Assert.True(c.ExternalContent);
            Assert.Contains("coordination", c.Toolsets);
            Assert.Contains("interoperability", c.Toolsets);
        }

        [Fact]
        public void Cross_app_coordination_is_a_prompt_with_the_bus_rules()
        {
            Assert.Throws<McpError>(() => McpPrompts.Get(new JObject { ["name"] = "cross-app-coordination" }));
            string text = (string)McpPrompts.Get(new JObject
            {
                ["name"] = "cross-app-coordination",
                ["arguments"] = new JObject { ["objective"] = "coordinate MEP against structure", ["integrations_path"] = @"C:\p\project-integrations.json" }
            })["messages"][0]["content"]["text"];
            foreach (string must in new[] { "ONLY bus", "horizun_health", "resolve_model", "correlation_id", "APPROVAL PER STAGE",
                                            "OWNER ORDER", "may_continue", "THREE LAYERS", "WEAKEST", "NOBODY IS AT THE KEYBOARD",
                                            @"C:\p\project-integrations.json" })
                Assert.Contains(must, text);
        }
    }
}
