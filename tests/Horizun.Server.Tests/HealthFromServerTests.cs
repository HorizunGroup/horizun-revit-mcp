// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_health answers FROM THE SERVER when the add-in does not (4.2-c). What the
// server knows by itself - which Revit processes published the bridge, their add-in
// version and contract hash against its own, and the last failure it saw - is
// reported with how each fact was obtained. What only Revit could say (active
// document, the command in flight, the open dialog) is listed as NOT verified and is
// never filled in.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class HealthFromServerTests : IDisposable
    {
        private readonly string _dir;
        private readonly HashSet<int> _alive = new HashSet<int>();
        private readonly Func<bool> _runningBefore;
        private readonly Func<JArray> _instancesBefore;

        public HealthFromServerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-health-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            PipeClient.DirectoryOverride = _dir;
            PipeClient.LivenessProbe = pid => _alive.Contains(pid);
            PipeClient.Target = TargetSelection.Automatic;
            _runningBefore = HealthAnswer.RevitProcessRunning;
            _instancesBefore = HealthAnswer.ListInstances;
            ServerFacts.Reset();
        }

        public void Dispose()
        {
            PipeClient.DirectoryOverride = null;
            PipeClient.LivenessProbe = null;
            PipeClient.Target = TargetSelection.Automatic;
            HealthAnswer.RevitProcessRunning = _runningBefore;
            HealthAnswer.ListInstances = _instancesBefore;
            ServerFacts.Reset();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private void Publish(string year, int pid, string contractHash)
        {
            var o = new JObject
            {
                ["schema"] = 3, ["revit_year"] = year, ["pipe_name"] = "Horizun-" + pid, ["auth_token"] = "t", ["pid"] = pid,
                ["addin_version"] = "4.2.0", ["commands"] = new JArray("horizun_health"),
                ["instance_id"] = Guid.NewGuid().ToString("N"), ["started_utc"] = DateTime.UtcNow.ToString("o"),
                ["protocol_version"] = 1
            };
            if (contractHash != null) o["contract_hash"] = contractHash;
            File.WriteAllText(Path.Combine(_dir, "revit-" + year + "-" + pid + ".json"), o.ToString());
            _alive.Add(pid);
        }

        private static JObject Timeout() => McpResult.Error(
            "Error: Revit did not answer within 30000 ms.", null, null,
            ErrorCodes.Detail(ErrorCodes.TiempoAgotado));

        private static JObject Sc(JToken result) => (JObject)((JObject)result)["structuredContent"];

        [Fact]
        public void A_busy_revit_is_described_by_what_the_server_can_check_about_its_instance()
        {
            Publish("2026", 321, "deadbeef");
            HealthAnswer.RevitProcessRunning = () => true;
            JObject h = Sc(HealthAnswer.Apply(Timeout()));

            JObject row = (JObject)((JArray)h["instances"]).Single();
            Assert.Equal(321, (int)row["pid"]);
            Assert.Equal("2026", (string)row["year"]);
            Assert.Equal("4.2.0", (string)row["addin_version"]);
            Assert.Equal("deadbeef", (string)row["contract_hash"]);
            Assert.True((bool)row["process_alive"]);
            Assert.False((bool)row["contract_matches_server"]);
            Assert.NotNull(h["server"]?["contract_hash"]);
            Assert.NotNull(h["server"]?["version"]);
        }

        [Fact]
        public void A_matching_contract_is_reported_as_matching_and_an_unpublished_one_as_unknown()
        {
            Publish("2026", 1, Horizun.Contracts.Contract.Hash);
            Publish("2024", 2, null);
            JObject h = Sc(HealthAnswer.Apply(Timeout()));
            JArray rows = (JArray)h["instances"];
            JObject matching = (JObject)rows.Single(r => (int)r["pid"] == 1);
            JObject unknown = (JObject)rows.Single(r => (int)r["pid"] == 2);
            Assert.True((bool)matching["contract_matches_server"]);
            // "The add-in did not publish a hash" is not "it differs": null, not false.
            Assert.Equal(JTokenType.Null, unknown["contract_matches_server"].Type);
        }

        [Fact]
        public void What_only_revit_can_say_is_listed_as_not_verified_and_never_guessed()
        {
            Publish("2026", 7, "x");
            JObject h = Sc(HealthAnswer.Apply(Timeout()));

            string[] notVerified = ((JArray)h["not_verified"]).Select(t => (string)t).ToArray();
            Assert.Contains("active_document", notVerified);
            Assert.Contains("command_in_progress", notVerified);
            Assert.Contains("modal_dialog", notVerified);
            Assert.Equal(JTokenType.Null, h["active_document"].Type);

            JArray verified = (JArray)h["verified"];
            Assert.NotEmpty(verified);
            Assert.All(verified, v => { Assert.NotNull(v["fact"]); Assert.NotNull(v["how"]); });
            Assert.DoesNotContain(verified, v => (string)v["fact"] == "active_document");
        }

        [Fact]
        public void No_published_instance_says_so_and_never_claims_a_start_in_progress()
        {
            HealthAnswer.RevitProcessRunning = () => true;
            JObject h = Sc(HealthAnswer.Apply(Timeout()));
            Assert.Empty((JArray)h["instances"]);
            Assert.DoesNotContain(((JArray)h["verified"]).Select(v => (string)v["fact"]), f => f == "revit_starting");
            Assert.NotEqual("arrancando", (string)h["estado"]);
            Assert.Contains(((JArray)h["verified"]), v => (string)v["fact"] == "live_instances" && (int)v["count"] == 0);
        }

        [Fact]
        public void The_last_failure_the_server_saw_is_kept_apart_from_this_requests_error()
        {
            Publish("2026", 9, "x");
            JObject none = Sc(HealthAnswer.Apply(Timeout()));
            Assert.Equal(JTokenType.Null, none["last_server_error"].Type);

            ServerFacts.RecordFailure("horizun_export", "pdf_exportador_rechazado", "PDF exporter rejected output x");
            JObject h = Sc(HealthAnswer.Apply(Timeout()));
            JObject last = (JObject)h["last_server_error"];
            Assert.Equal("horizun_export", (string)last["tool"]);
            Assert.Equal("pdf_exportador_rechazado", (string)last["error_code"]);
            Assert.Contains("rejected", (string)last["message"]);
            Assert.NotNull(last["at_utc"]);
            Assert.Equal("this health request", (string)h["last_error"]["scope"]);
        }

        [Fact]
        public void A_health_failure_is_not_recorded_as_the_last_server_error_of_another_tool()
        {
            ServerFacts.RecordFailure("horizun_health", "tiempo_agotado", "x");
            Assert.Null(ServerFacts.LastFailure());
        }

        [Fact]
        public void Several_instances_also_carry_the_server_facts()
        {
            Publish("2024", 100, "a");
            Publish("2026", 200, "b");
            HealthAnswer.ListInstances = () => new JArray();
            JObject refusal = McpResult.Error("Error: 2 Revit instances are running with the bridge loaded, choose one.",
                null, null, ErrorCodes.Detail(ErrorCodes.DocumentoObjetivoAmbiguo));
            JObject h = Sc(HealthAnswer.Apply(refusal));
            Assert.Equal("varias_instancias", (string)h["estado"]);
            Assert.NotNull(h["not_verified"]);
            Assert.NotNull(h["server"]?["contract_hash"]);
        }
    }
}
