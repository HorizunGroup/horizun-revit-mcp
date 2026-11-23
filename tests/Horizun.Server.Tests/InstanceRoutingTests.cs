// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// Several Revit instances: one rule for every tool (InstanceRouting.cs). Field
// report: with Revit 2024 and 2026 open, health and get_document_info answered while
// request_python_access demanded a choice. Proved against a temp discovery directory
// with an injected liveness probe.
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
    public class InstanceRoutingTests : IDisposable
    {
        private readonly string _dir;
        private readonly HashSet<int> _alive = new HashSet<int>();

        public InstanceRoutingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-routing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            PipeClient.DirectoryOverride = _dir;
            PipeClient.LivenessProbe = pid => _alive.Contains(pid);
            PipeClient.Target = TargetSelection.Automatic;
        }

        public void Dispose()
        {
            PipeClient.DirectoryOverride = null;
            PipeClient.LivenessProbe = null;
            PipeClient.Target = TargetSelection.Automatic;
            try { Directory.Delete(_dir, true); } catch { }
        }

        private void Publish(string year, int pid)
        {
            var o = new JObject
            {
                ["schema"] = 3, ["revit_year"] = year, ["pipe_name"] = "Horizun-" + pid, ["auth_token"] = "t", ["pid"] = pid,
                ["addin_version"] = "3.7.0", ["commands"] = new JArray("horizun_health"),
                ["instance_id"] = Guid.NewGuid().ToString("N"), ["started_utc"] = DateTime.UtcNow.ToString("o")
            };
            File.WriteAllText(Path.Combine(_dir, "revit-" + year + "-" + pid + ".json"), o.ToString());
            _alive.Add(pid);
        }

        /// <summary>The refusal exactly as Program.CallToolCore builds it for a tool.</summary>
        private static JObject Refusal(RoutingDecision r) =>
            McpResult.Error("Error: " + r.Refusal, null, null,
                ErrorCodes.Detail(ErrorCodes.DocumentoObjetivoAmbiguo, extra: InstanceRouting.AmbiguityExtra(InstanceRouting.LiveCandidates())));

        private static JArray InstanceReports() => new JArray
        {
            new JObject
            {
                ["pid"] = 100, ["year"] = "2024", ["addin_version"] = "5.0.0",
                ["active_document"] = new JObject { ["title"] = "Model A", ["path"] = "C:\\Models\\A.rvt" },
                ["open_documents"] = new JArray
                {
                    new JObject { ["title"] = "Model A", ["path"] = "C:\\Models\\A.rvt", ["is_active"] = true },
                    new JObject { ["title"] = "Model A link", ["path"] = "C:\\Models\\Link.rvt", ["is_active"] = false, ["is_linked"] = true }
                }
            },
            new JObject
            {
                ["pid"] = 200, ["year"] = "2026", ["addin_version"] = "5.0.0",
                ["active_document"] = new JObject { ["title"] = "Model B", ["path"] = "D:\\Models\\B.rvt" },
                ["open_documents"] = new JArray
                {
                    new JObject { ["title"] = "Model B", ["path"] = "D:\\Models\\B.rvt", ["is_active"] = true }
                }
            }
        };

        [Fact]
        public void Live_target_recommendations_omit_dead_discovery_processes()
        {
            Publish("2025", 100);
            Publish("2026", 200);
            _alive.Remove(100);

            JArray live = InstanceRouting.LiveTargets();

            Assert.Single(live);
            Assert.Equal(200, (int)live[0]["pid"]);
            Assert.Equal("2026", (string)live[0]["year"]);
            Assert.Equal("horizun_target", (string)live[0]["select_with"]["tool"]);
            Assert.Equal(200, (int)live[0]["select_with"]["arguments"]["pid"]);
        }

        [Fact]
        public void Two_years_open_and_nothing_chosen_every_tool_refuses_the_same_way_with_the_candidates()
        {
            Publish("2024", 100);
            Publish("2026", 200);
            Func<JArray> before = HealthAnswer.ListInstances;
            HealthAnswer.ListInstances = InstanceReports;

            try
            {
                RoutingDecision r = InstanceRouting.Decide(TargetSelection.Automatic, null);
                Assert.Null(r.Instance);
                Assert.True(r.Ambiguous);
                Assert.Equal(new[] { 100, 200 }, r.Candidates.Select(c => (int)c["pid"]).ToArray());
                Assert.Equal(new[] { "2024", "2026" }, r.Candidates.Select(c => (string)c["year"]).ToArray());

                // A read and a write both refuse, list candidate documents, and show how
                // to select a process and repeat the original call with its target_document.
                JObject refusal = Refusal(r);
                JObject sc = (JObject)refusal["structuredContent"];
                Assert.True((bool)refusal["isError"]);
                Assert.Equal("documento_objetivo_ambiguo", (string)sc["error_code"]);
                Assert.True((bool)sc["target_required"]);
                Assert.False((bool)sc["write_started"]);
                Assert.Equal(2, ((JArray)sc["instances"]).Count);
                Assert.Equal(100, (int)sc["instances"][0]["pid"]);
                Assert.Equal("2024", (string)sc["instances"][0]["year"]);
                Assert.Equal("Model A", (string)sc["instances"][0]["title"]);
                Assert.Equal("C:\\Models\\A.rvt", (string)sc["instances"][0]["path"]);
                Assert.True((bool)sc["instances"][0]["active"]);
                Assert.Equal(100, (int)sc["example"]["select_instance"]["arguments"]["pid"]);
                Assert.Equal("C:\\Models\\A.rvt", (string)sc["example"]["repeat_original_tool_arguments"]["target_document"]);
                Assert.Equal("several_instances_refuse_uniformly", (string)sc["rule"]);
                Assert.Contains("target_document", (string)sc["que_hacer"]);
            }
            finally { HealthAnswer.ListInstances = before; }
        }

        [Fact]
        public void Health_answers_the_same_situation_with_the_same_code_rule_and_candidates()
        {
            Publish("2024", 100);
            Publish("2026", 200);
            RoutingDecision r = InstanceRouting.Decide(TargetSelection.Automatic, null);

            Func<JArray> before = HealthAnswer.ListInstances;
            try
            {
                HealthAnswer.ListInstances = InstanceReports;
                JObject h = (JObject)((JObject)HealthAnswer.Apply(Refusal(r)))["structuredContent"];
                Assert.Equal("varias_instancias", (string)h["estado"]);
                Assert.Equal("documento_objetivo_ambiguo", (string)h["error_code"]);
                Assert.True((bool)h["target_required"]);
                Assert.Equal("several_instances_refuse_uniformly", (string)h["rule"]);
                Assert.Equal(new[] { 100, 200 }, ((JArray)h["instances"]).Select(c => (int)c["pid"]).ToArray());
                Assert.Equal("Model A", (string)h["instances"][0]["title"]);
                Assert.Equal("C:\\Models\\A.rvt", (string)h["instances"][0]["path"]);
                Assert.Equal(100, (int)h["example"]["select_instance"]["arguments"]["pid"]);
                Assert.Equal("C:\\Models\\A.rvt", (string)h["example"]["repeat_original_tool_arguments"]["target_document"]);
            }
            finally { HealthAnswer.ListInstances = before; }
        }

        [Fact]
        public void One_instance_answers_and_says_it_was_the_only_one()
        {
            Publish("2025", 300);
            RoutingDecision r = InstanceRouting.Decide(TargetSelection.Automatic, null);
            Assert.Null(r.Refusal);
            Assert.Equal(300, r.Instance.Pid);

            JObject by = InstanceRouting.AnsweredBy(r.Instance, TargetSelection.Automatic, null, InstanceRouting.LiveCount());
            Assert.Equal(300, (int)by["pid"]);
            Assert.Equal("2025", (string)by["year"]);
            Assert.Equal("sole_instance", (string)by["selected_by"]);
            Assert.Equal(0, (int)by["other_live_instances"]);
        }

        [Fact]
        public void A_chosen_instance_answers_and_the_reply_names_it_and_the_others()
        {
            Publish("2024", 100);
            Publish("2026", 200);

            TargetSelection byPid = TargetSelection.ByPid(200);
            RoutingDecision r = InstanceRouting.Decide(byPid, null);
            Assert.Equal(200, r.Instance.Pid);
            JObject by = InstanceRouting.AnsweredBy(r.Instance, byPid, null, InstanceRouting.LiveCount());
            Assert.Equal("horizun_target pid", (string)by["selected_by"]);
            Assert.Equal(1, (int)by["other_live_instances"]);

            // HORIZUN_REVIT_YEAR naming one of them is a choice too, and is said.
            RoutingDecision byEnv = InstanceRouting.Decide(TargetSelection.Automatic, "2024");
            Assert.Equal(100, byEnv.Instance.Pid);
            Assert.Equal("HORIZUN_REVIT_YEAR",
                (string)InstanceRouting.AnsweredBy(byEnv.Instance, TargetSelection.Automatic, "2024", 2)["selected_by"]);

            // Pinned from the environment at startup.
            TargetSelection pinned = TargetSelection.ByPid(100, "HORIZUN_REVIT_PID");
            Assert.Equal("HORIZUN_REVIT_PID pid",
                (string)InstanceRouting.AnsweredBy(InstanceRouting.Decide(pinned, null).Instance, pinned, null, 2)["selected_by"]);
        }

        [Fact]
        public void A_pinned_pid_that_published_nothing_is_not_ambiguous_and_lists_nothing()
        {
            Publish("2026", 200);
            RoutingDecision r = InstanceRouting.Decide(TargetSelection.ByPid(999), null);
            Assert.NotNull(r.Refusal);
            Assert.False(r.Ambiguous);
            Assert.Null(r.Candidates);
        }

        [Fact]
        public void Stamp_adds_answered_by_once_and_leaves_other_shapes_alone()
        {
            var by = new JObject { ["pid"] = 1 };
            var payload = new JObject { ["ok"] = true };
            InstanceRouting.Stamp(payload, by);
            Assert.Equal(1, (int)payload["answered_by"]["pid"]);
            InstanceRouting.Stamp(payload, new JObject { ["pid"] = 2 });
            Assert.Equal(1, (int)payload["answered_by"]["pid"]);
            JToken text = "plain";
            InstanceRouting.Stamp(text, by);   // no throw
            InstanceRouting.Stamp(payload, null);
        }
    }
}
