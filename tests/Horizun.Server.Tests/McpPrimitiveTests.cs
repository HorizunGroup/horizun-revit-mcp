using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public sealed class McpPrimitiveTests
    {
        [Fact]
        public void Resources_are_bounded_virtual_and_readable()
        {
            JObject listed = McpResources.List(null);
            JArray resources = Assert.IsType<JArray>(listed["resources"]);
            // Seven documents (the ISO 19650 project-context schema and horizun://session/toolsets joined the five) and one
            // MCP App. The app lives under ui:// because that is the scheme the Apps
            // extension names; it is as virtual as the rest.
            Assert.Equal(8, resources.Count);
            foreach (JObject resource in resources)
            {
                string uri = (string)resource["uri"];
                if (uri.StartsWith("ui://", System.StringComparison.Ordinal))
                    Assert.Equal("text/html;profile=mcp-app", (string)resource["mimeType"]);
                else
                    Assert.StartsWith("horizun://", uri);
                Assert.False(string.IsNullOrWhiteSpace((string)resource["mimeType"]));
                Assert.True((int)resource["size"] > 0);

                JObject read = McpResources.Read(new JObject { ["uri"] = resource["uri"] });
                JObject content = Assert.IsType<JObject>(Assert.Single((JArray)read["contents"]));
                Assert.Equal(resource["uri"], content["uri"]);
                Assert.False(string.IsNullOrWhiteSpace((string)content["text"]));
            }
        }

        /// <summary>
        /// The catalogue is a PROCEDURE catalogue now, not a menu of tool names.
        ///
        /// What each row has to carry is the point of the change: a procedure with no
        /// declared inputs, no scope, no expected errors and no acceptance criterion can
        /// be run two different ways by two people and neither can be checked. The old
        /// assertions - a prompt, some tools, an evidence sentence - passed happily on a
        /// row that said none of that.
        /// </summary>
        [Fact]
        public void Procedure_catalog_declares_what_each_procedure_takes_produces_and_proves()
        {
            JObject read = McpResources.Read(new JObject { ["uri"] = "horizun://workflows/bim-production" });
            JObject catalog = JObject.Parse((string)read["contents"][0]["text"]);
            JArray workflows = (JArray)catalog["workflows"];

            Assert.Equal(McpWorkflowCatalog.Schema, (string)catalog["schema"]);
            Assert.True(workflows.Count >= 20,
                        "the gap asked for 20 priority procedures; there are " + workflows.Count);

            // The old key still resolves to the same array, so a reader written against
            // the first shape does not break on the day it changed.
            Assert.Equal(workflows.Count, ((JArray)catalog["procedures"]).Count);

            var ids = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (JObject procedure in workflows.OfType<JObject>())
            {
                string id = (string)procedure["id"];
                Assert.True(ids.Add(id), "procedure id '" + id + "' appears twice");

                Assert.False(string.IsNullOrWhiteSpace((string)procedure["prompt"]));
                Assert.NotEmpty((JArray)procedure["tools"]);
                Assert.False(string.IsNullOrWhiteSpace((string)procedure["evidence"]));

                // A procedure is versioned individually: a run recorded against version 1
                // is not evidence for version 2.
                Assert.True((int)procedure["version"] >= 1, id + " has no version");

                Assert.NotEmpty((JArray)procedure["inputs"]);
                Assert.NotEmpty((JArray)procedure["errors"]);
                Assert.False(string.IsNullOrWhiteSpace((string)procedure["scope"]), id + " declares no scope");
                Assert.False(string.IsNullOrWhiteSpace((string)procedure["output"]), id + " declares no output");
                Assert.False(string.IsNullOrWhiteSpace((string)procedure["acceptance"]),
                             id + " has no acceptance criterion, so nobody can say whether it worked");
            }

            // Every declared dependency names a procedure that exists. A dependency on
            // something absent is a procedure nobody can complete.
            foreach (JObject procedure in workflows.OfType<JObject>())
                foreach (JToken dependency in (JArray)procedure["depends_on"])
                    Assert.Contains((string)dependency, ids);
        }

        /// <summary>
        /// Every procedure's `prompt` must name a prompt this server actually serves.
        ///
        /// The two lists live in different files and drifted the moment three procedures
        /// were added: a catalogue entry pointing at a prompt that does not exist is an
        /// entry a client can read, act on, and be refused by - which reads as a broken
        /// server rather than as a catalogue nobody finished.
        /// </summary>
        [Fact]
        public void Every_procedure_prompt_is_a_prompt_this_server_serves()
        {
            JObject read = McpResources.Read(new JObject { ["uri"] = "horizun://workflows/bim-production" });
            JObject catalog = JObject.Parse((string)read["contents"][0]["text"]);

            var served = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (JObject prompt in ((JArray)McpPrompts.List(null)["prompts"]).OfType<JObject>())
                served.Add((string)prompt["name"]);

            foreach (JObject procedure in ((JArray)catalog["workflows"]).OfType<JObject>())
                Assert.True(served.Contains((string)procedure["prompt"]),
                            "procedure '" + (string)procedure["id"] + "' names prompt '" +
                            (string)procedure["prompt"] + "', which prompts/list does not offer");
        }

        [Fact]
        public void Resource_contract_is_the_compiled_contract_not_a_second_table()
        {
            JObject read = McpResources.Read(new JObject { ["uri"] = "horizun://contract/tools" });
            JObject contract = JObject.Parse((string)read["contents"][0]["text"]);
            Assert.Equal(Horizun.Contracts.Contract.Hash, (string)contract["contract_hash"]);
            Assert.Equal(Horizun.Contracts.Contract.All.Count, ((JArray)contract["tools"]).Count);
            Assert.All(((JArray)contract["tools"]), t =>
                Assert.False(string.IsNullOrWhiteSpace((string)t["description"])));
        }

        [Fact]
        public void Tools_list_is_context_bounded_while_resource_keeps_full_contract()
        {
            JArray tools = Tools.List(true);
            Assert.NotEmpty(tools);
            foreach (JObject tool in tools)
            {
                string description = (string)tool["description"];
                Assert.True(description.Length <= 900, (string)tool["name"] + " description is " + description.Length);
                Assert.Contains("horizun://contract/tools", description);
            }
            int bytes = System.Text.Encoding.UTF8.GetByteCount(tools.ToString(Newtonsoft.Json.Formatting.None));
            Assert.True(bytes <= 512 * 1024, "tools/list is " + bytes + " bytes; progressive discovery budget is 512 KiB");
        }

        [Fact]
        public void Advertised_argument_descriptions_are_budgeted_but_the_contract_keeps_every_word()
        {
            JArray tools = Tools.List(true);
            foreach (JObject tool in tools)
                foreach (JToken d in tool["inputSchema"].SelectTokens("$..description"))
                    if (d.Type == JTokenType.String)
                        Assert.True(((string)d).Length <= Tools.SchemaDescriptionMax, (string)tool["name"] + ": " + ((string)d).Length);

            // The compacted copy never changes what a call is validated against, never renames
            // an argument, and the full text is still served by the contract resource.
            foreach (var c in Horizun.Contracts.Contract.All)
            {
                JObject advertised = Tools.CompactSchema(c.Name, c.InputSchema);
                if (c.InputSchema == null) continue;
                var fullArgs = ((JObject)c.InputSchema["properties"])?.Properties().Select(p => p.Name).ToArray() ?? new string[0];
                var advArgs = ((JObject)advertised["properties"])?.Properties().Select(p => p.Name).ToArray() ?? new string[0];
                Assert.Equal(fullArgs, advArgs);
            }
            string contract = (string)McpResources.Read(new JObject { ["uri"] = "horizun://contract/tools" })["contents"][0]["text"];
            var longest = Horizun.Contracts.Contract.All.Where(c => c.InputSchema != null)
                .SelectMany(c => c.InputSchema.SelectTokens("$..description")).Where(t => t.Type == JTokenType.String)
                .Select(t => (string)t).OrderByDescending(t => t.Length).First();
            Assert.True(longest.Length > Tools.SchemaDescriptionMax);
            Assert.Contains(JsonConvert.SerializeObject(longest).Trim('"').Substring(0, 60), contract);
        }

        [Fact]
        public void Prompts_require_the_declared_arguments_and_return_standard_messages()
        {
            JArray prompts = (JArray)McpPrompts.List(null)["prompts"];
            // Twenty, three procedures added with the 2026-09-15 catalogue, the two
            // DWG procedures that had no prompt behind them, and project-intake.
            Assert.Equal(28, prompts.Count);
            foreach (var item in new[] { ("room-documentation", "specification"), ("family-recipe", "specification"), ("review-correct-verify", "selection") })
            {
                Assert.Throws<McpError>(() => McpPrompts.Get(new JObject { ["name"] = item.Item1 }));
                var prompt = McpPrompts.Get(new JObject { ["name"] = item.Item1, ["arguments"] = new JObject { [item.Item2] = "explicit fixture specification" } });
                string text = (string)prompt["messages"][0]["content"]["text"];
                Assert.Contains("horizun_health", text);
                Assert.Contains("dry_run=true", text);
                Assert.Contains("cache_mode=bypass", text);
            }

            McpError missing = Assert.Throws<McpError>(() => McpPrompts.Get(new JObject
            {
                ["name"] = "verified-change",
                ["arguments"] = new JObject { ["objective"] = "move walls" }
            }));
            Assert.Equal(-32602, missing.Code);

            JObject result = McpPrompts.Get(new JObject
            {
                ["name"] = "verified-change",
                ["arguments"] = new JObject
                {
                    ["objective"] = "move walls",
                    ["applies_to"] = "ids 10 and 11",
                    ["correct_when"] = "their locations re-read at the requested coordinates"
                }
            });
            JObject message = Assert.IsType<JObject>(Assert.Single((JArray)result["messages"]));
            Assert.Equal("user", (string)message["role"]);
            Assert.Equal("text", (string)message["content"]["type"]);
            Assert.Contains("horizun_health", (string)message["content"]["text"]);

            JObject workflow = McpPrompts.Get(new JObject
            {
                ["name"] = "safe-batch-parameter-update",
                ["arguments"] = new JObject { ["updates"] = "door ids 10 and 11; Type Mark = D-101" }
            });
            string workflowText = (string)workflow["messages"][0]["content"]["text"];
            Assert.Contains("dry_run=true", workflowText);
            Assert.Contains("Do not apply", workflowText);

            JObject coordination = McpPrompts.Get(new JObject
            {
                ["name"] = "architecture-structure-coordination"
            });
            Assert.Contains("horizun_clash", (string)coordination["messages"][0]["content"]["text"]);

            JObject report = McpPrompts.Get(new JObject
            {
                ["name"] = "qaqc-report-export",
                ["arguments"] = new JObject
                {
                    ["workbook_path"] = "C:\\reports\\qaqc.xlsx",
                    ["evidence_source"] = "receipt 4f6a"
                }
            });
            Assert.Contains(".horizunbak", (string)report["messages"][0]["content"]["text"]);
        }

        [Fact]
        public void Prompt_completion_is_bounded_public_and_does_not_invent_user_intent()
        {
            JObject result = McpCompletions.Complete(new JObject
            {
                ["ref"] = new JObject { ["type"] = "ref/prompt", ["name"] = "read-only-audit" },
                ["argument"] = new JObject { ["name"] = "focus", ["value"] = "work" }
            });
            JArray values = (JArray)result["completion"]["values"];
            Assert.Single(values);
            Assert.Equal("links and worksets", (string)values[0]);
            Assert.True(values.Count <= 100);
            Assert.False((bool)result["completion"]["hasMore"]);

            JObject intent = McpCompletions.Complete(new JObject
            {
                ["ref"] = new JObject { ["type"] = "ref/prompt", ["name"] = "verified-change" },
                ["argument"] = new JObject { ["name"] = "objective", ["value"] = "" }
            });
            Assert.Empty((JArray)intent["completion"]["values"]);

            Assert.Throws<McpError>(() => McpCompletions.Complete(new JObject
            {
                ["ref"] = new JObject { ["type"] = "ref/resource", ["uri"] = "horizun://contract/tools" },
                ["argument"] = new JObject { ["name"] = "path", ["value"] = "C:\\Users" }
            }));
        }

        [Fact]
        public void Client_logging_is_opt_in_filtered_and_contains_only_supplied_metadata()
        {
            McpLogging.ResetForTests();
            var seen = new System.Collections.Generic.List<JObject>();
            Action<string, JObject> capture = (method, prms) =>
            {
                Assert.Equal("notifications/message", method);
                seen.Add(prms);
            };

            McpLogging.Emit("error", new JObject { ["event"] = "before_opt_in" }, capture);
            Assert.Empty(seen);
            McpLogging.SetLevel(new JObject { ["level"] = "warning" });
            McpLogging.Emit("info", new JObject { ["event"] = "below_threshold" }, capture);
            McpLogging.Emit("error", new JObject
            {
                ["event"] = "tool_finished", ["tool"] = "horizun_health"
            }, capture);
            JObject one = Assert.Single(seen);
            Assert.Equal("error", (string)one["level"]);
            Assert.Equal("horizun_health", (string)one["data"]["tool"]);
            Assert.Throws<McpError>(() => McpLogging.SetLevel(new JObject { ["level"] = "verbose" }));
            McpLogging.ResetForTests();
        }

        [Fact]
        public void Tool_list_monitor_announces_only_effective_permission_changes()
        {
            WithDataRoot(() =>
            {
                File.WriteAllText(HorizunPaths.SettingsPath(),
                    @"{""permission_profile"":""safe_write"",""unrelated"":1}");
                int notifications = 0;
                using (var monitor = new ToolListMonitor((method, prms) =>
                {
                    Assert.Equal("notifications/tools/list_changed", method);
                    Assert.Null(prms);
                    notifications++;
                }, watch: false))
                {
                    File.WriteAllText(HorizunPaths.SettingsPath(),
                        @"{""permission_profile"":""safe_write"",""unrelated"":2}");
                    monitor.CheckNow();
                    Assert.Equal(0, notifications);

                    Assert.True(Horizun.Revit.Core.Settings.TryGrantExecutePythonPersistently(
                        out string error), error);
                    monitor.CheckNow();
                    Assert.Equal(1, notifications);
                    monitor.CheckNow();
                    Assert.Equal(1, notifications);
                }
            });
        }

        [Fact]
        public void Unknown_resource_prompt_and_nonempty_cursor_fail_as_invalid_params()
        {
            Assert.Equal(-32602, Assert.Throws<McpError>(() =>
                McpResources.Read(new JObject { ["uri"] = "horizun://unknown" })).Code);
            Assert.Equal(-32602, Assert.Throws<McpError>(() =>
                McpPrompts.Get(new JObject { ["name"] = "unknown" })).Code);
            Assert.Equal(-32602, Assert.Throws<McpError>(() =>
                McpResources.List(new JObject { ["cursor"] = "invented" })).Code);
        }

        [Fact]
        public void Standard_task_is_backed_by_the_durable_Revit_job_and_returns_tool_result()
        {
            WithDataRoot(() =>
            {
                Job job = null;
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan",
                    ["arguments"] = new JObject { ["sections"] = new JArray("health") },
                    ["task"] = new JObject { ["ttl"] = 600000 }
                }, (request, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    Assert.Equal("horizun_submit_job", (string)request["name"]);
                    job = Job.Start("horizun_model_scan");
                    return McpResult.Structured(new JObject
                    {
                        ["job_id"] = job.Id, ["status"] = "queued"
                    }, "queued");
                }, CancellationToken.None);

                string taskId = (string)created["task"]["taskId"];
                Assert.Equal("working", (string)created["task"]["status"]);
                Assert.Equal(taskId,
                    (string)created["_meta"]["io.modelcontextprotocol/related-task"]["taskId"]);
                Assert.True(File.Exists(Path.Combine(HorizunPaths.DataRoot(), "mcp-tasks", taskId + ".json")));

                job.MarkRunning();
                job.Result(@"{""complete"":true}");
                job.Finish("ok", null);

                JObject state = McpTasks.Get(new JObject { ["taskId"] = taskId });
                Assert.Equal("completed", (string)state["status"]);
                JObject result = McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException("completed task must not resubmit"),
                    CancellationToken.None);
                Assert.False((bool)result["isError"]);
                Assert.True((bool)result["structuredContent"]["complete"]);
                Assert.Equal(taskId,
                    (string)result["_meta"]["io.modelcontextprotocol/related-task"]["taskId"]);
            });
        }

        [Fact]
        public void Failed_task_submission_is_terminal_and_retains_the_underlying_tool_error()
        {
            WithDataRoot(() =>
            {
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan",
                    ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 1 }
                }, (request, token) => McpResult.Text("Revit is unavailable", true), CancellationToken.None);

                Assert.Equal(McpTasks.MinTtlMs, (long)created["task"]["ttl"]);
                Assert.Equal("failed", (string)created["task"]["status"]);
                string taskId = (string)created["task"]["taskId"];
                JObject result = McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException(), CancellationToken.None);
                Assert.True((bool)result["isError"]);
                Assert.Contains("unavailable", (string)result["content"][0]["text"]);
            });
        }

        [Fact]
        public void Task_follows_an_admitted_job_even_when_submission_acknowledgement_is_incomplete()
        {
            WithDataRoot(() =>
            {
                Job job = Job.Start("horizun_model_scan");
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan",
                    ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 60000 }
                }, (request, token) => McpResult.Error(
                    "submission ledger failed", null, null,
                    new JObject { ["job_id"] = job.Id, ["submission_record_incomplete"] = true }),
                    CancellationToken.None);

                Assert.Equal("working", (string)created["task"]["status"]);
                job.MarkRunning();
                job.Result(@"{""complete"":true}");
                job.Finish("ok", null);
                Assert.Equal("completed", (string)McpTasks.Get(new JObject
                {
                    ["taskId"] = created["task"]["taskId"]
                })["status"]);
            });
        }

        [Fact]
        public void Dead_durable_job_stays_terminal_and_arguments_are_redacted_after_admission()
        {
            WithDataRoot(() =>
            {
                Job job = null;
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan",
                    ["arguments"] = new JObject { ["target_document"] = "sensitive-model-name" },
                    ["task"] = new JObject { ["ttl"] = 60000 }
                }, (request, token) =>
                {
                    job = Job.Start("horizun_model_scan");
                    return McpResult.Structured(new JObject { ["job_id"] = job.Id }, "queued");
                }, CancellationToken.None);

                string taskId = (string)created["task"]["taskId"];
                JObject sidecar = JObject.Parse(File.ReadAllText(Path.Combine(
                    HorizunPaths.DataRoot(), "mcp-tasks", taskId + ".json")));
                Assert.Null(sidecar["arguments"]);
                Assert.Null(sidecar["submit_idempotency_key"]);

                PipeClient.LivenessProbe = _ => false;
                JObject dead = McpTasks.Get(new JObject { ["taskId"] = taskId });
                Assert.Equal("failed", (string)dead["status"]);
                Assert.Contains("exited", (string)dead["statusMessage"]);

                // PID reuse/liveness change and even a later contradictory finish
                // cannot reverse an MCP terminal state or its frozen result.
                PipeClient.LivenessProbe = _ => true;
                job.MarkRunning();
                job.Result(@"{""complete"":true}");
                job.Finish("ok", null);
                JObject stillFailed = McpTasks.Get(new JObject { ["taskId"] = taskId });
                Assert.Equal("failed", (string)stillFailed["status"]);
                Assert.Equal((string)dead["statusMessage"], (string)stillFailed["statusMessage"]);
                JObject result = McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException(), CancellationToken.None);
                Assert.True((bool)result["isError"]);
            });
        }

        [Fact]
        public void Completed_capture_task_snapshots_the_exact_image_result()
        {
            WithDataRoot(() =>
            {
                string image = Path.Combine(HorizunPaths.DataRoot(), "capture.png");
                File.WriteAllBytes(image, BuildRgbaPng());
                Job job = null;
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_capture_view", ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 60000 }
                }, (request, token) =>
                {
                    job = Job.Start("horizun_capture_view");
                    return McpResult.Structured(new JObject { ["job_id"] = job.Id }, "queued");
                }, CancellationToken.None);
                job.MarkRunning();
                string durablePayload = AsyncResultPayload.Serialize(
                    new JObject { ["captured"] = true, ["image_path"] = image }, job.Id);
                job.Result(durablePayload);
                job.Finish("ok", null);

                string taskId = (string)created["task"]["taskId"];
                // Delete the Revit export BEFORE the first task poll. The job payload
                // points at its durable copy, so completion/result cannot depend on poll timing.
                File.Delete(image);
                Assert.Equal("completed", (string)McpTasks.Get(new JObject { ["taskId"] = taskId })["status"]);
                JObject first = McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException(), CancellationToken.None);
                JObject second = McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException(), CancellationToken.None);
                Assert.False((bool)first["isError"]);
                Assert.Equal("image", (string)first["content"][1]["type"]);
                Assert.Equal(first.ToString(), second.ToString());
            });
        }

        [Fact]
        public void Temporarily_missing_job_record_freezes_one_consistent_terminal_failure()
        {
            WithDataRoot(() =>
            {
                Job job = null;
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan", ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 60000 }
                }, (request, token) =>
                {
                    job = Job.Start("horizun_model_scan");
                    return McpResult.Structured(new JObject { ["job_id"] = job.Id }, "queued");
                }, CancellationToken.None);
                string taskId = (string)created["task"]["taskId"];
                string hidden = job.Path + ".hidden";
                File.Move(job.Path, hidden);
                JObject failed = McpTasks.Get(new JObject { ["taskId"] = taskId });
                Assert.Equal("failed", (string)failed["status"]);
                Assert.Contains("missing", (string)failed["statusMessage"]);

                File.Move(hidden, job.Path);
                job.MarkRunning();
                job.Result(@"{""complete"":true}");
                job.Finish("ok", null);
                Assert.Equal("failed", (string)McpTasks.Get(new JObject { ["taskId"] = taskId })["status"]);
                JObject result = McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException(), CancellationToken.None);
                Assert.True((bool)result["isError"]);
                Assert.Contains("missing", (string)result["content"][0]["text"]);
            });
        }

        [Fact]
        public void Task_recovers_a_legitimate_result_larger_than_the_JSONL_record_limit()
        {
            WithDataRoot(() =>
            {
                Job job = null;
                string blob = new string('x', JobStatus.MaxRecordBytes * 2);
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan", ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 60000 }
                }, (request, token) =>
                {
                    job = Job.Start("horizun_model_scan");
                    job.ProtectUntil(DateTimeOffset.Parse((string)request["arguments"]["retain_until_utc"]));
                    return McpResult.Structured(new JObject { ["job_id"] = job.Id }, "queued");
                }, CancellationToken.None);

                job.MarkRunning();
                job.Result(new JObject { ["blob"] = blob }.ToString(Newtonsoft.Json.Formatting.None));
                job.Finish("ok", null);

                JObject listed = JobStatus.Handle(new JObject { ["limit"] = 1, ["checkpoints"] = 1 });
                Assert.Equal("ok", (string)listed["jobs"][0]["state"]);
                Assert.True((bool)listed["jobs"][0]["result_external"]);
                Assert.True((bool)listed["jobs"][0]["result_omitted_for_response_budget"]);

                JObject exact = JobStatus.Handle(new JObject
                {
                    ["job_id"] = job.Id, ["limit"] = 1, ["checkpoints"] = 1
                });
                Assert.False((bool)exact["jobs"][0]["result_omitted_for_response_budget"]);
                Assert.Equal(blob, (string)exact["jobs"][0]["result"]["blob"]);

                string taskId = (string)created["task"]["taskId"];
                Assert.Equal("completed", (string)McpTasks.Get(new JObject { ["taskId"] = taskId })["status"]);
                JObject result = McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException(), CancellationToken.None);
                Assert.False((bool)result["isError"]);
                Assert.Equal(blob.Length, ((string)result["structuredContent"]["blob"]).Length);
                Assert.True(File.Exists(Path.Combine(HorizunPaths.JobsDir(), "results", job.Id + ".json")));
            });
        }

        [Fact]
        public void External_result_near_the_transport_limit_keeps_envelope_within_budget()
        {
            WithDataRoot(() =>
            {
                // Leave a little artifact-schema slack inside the payload allowance;
                // the production constant independently reserves a full MiB for the
                // eventual job-status/MCP envelope.
                string blob = new string('x', Job.MaxExternalResultBytes - 4096);
                Job job = null;
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan", ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 60000 }
                }, (request, token) =>
                {
                    job = Job.Start("horizun_model_scan");
                    job.ProtectUntil(DateTimeOffset.Parse(
                        (string)request["arguments"]["retain_until_utc"]));
                    return McpResult.Structured(new JObject { ["job_id"] = job.Id }, "queued");
                }, CancellationToken.None);
                job.MarkRunning();
                job.Result(new JObject { ["blob"] = blob }
                    .ToString(Newtonsoft.Json.Formatting.None));
                job.Finish("ok", null);

                JObject exact = JobStatus.Handle(new JObject
                {
                    ["job_id"] = job.Id, ["limit"] = 1, ["checkpoints"] = 1
                });
                Assert.Equal(blob.Length, ((string)exact["jobs"][0]["result"]["blob"]).Length);
                JObject jobStatusEnvelope = McpResult.Structured(exact,
                    exact.ToString(Newtonsoft.Json.Formatting.Indented));
                Assert.Contains("structuredContent",
                    (string)jobStatusEnvelope["content"][0]["text"]);
                Assert.True(Encoding.UTF8.GetByteCount(
                    jobStatusEnvelope.ToString(Newtonsoft.Json.Formatting.None)) <=
                    Horizun.Contracts.Contract.MaxReplyBytes);

                string taskId = (string)created["task"]["taskId"];
                Assert.Equal("completed", (string)McpTasks.Get(
                    new JObject { ["taskId"] = taskId })["status"]);
                JObject envelope = McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException(), CancellationToken.None);
                Assert.Equal(blob.Length, ((string)envelope["structuredContent"]["blob"]).Length);
                Assert.Contains("structuredContent", (string)envelope["content"][0]["text"]);
                Assert.True(Encoding.UTF8.GetByteCount(
                    envelope.ToString(Newtonsoft.Json.Formatting.None)) <=
                    Horizun.Contracts.Contract.MaxReplyBytes);
            });
        }

        [Fact]
        public void Corrupt_external_result_fails_closed_instead_of_returning_truncated_JSON()
        {
            WithDataRoot(() =>
            {
                Job job = null;
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan", ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 60000 }
                }, (request, token) =>
                {
                    job = Job.Start("horizun_model_scan");
                    job.ProtectUntil(DateTimeOffset.Parse((string)request["arguments"]["retain_until_utc"]));
                    return McpResult.Structured(new JObject { ["job_id"] = job.Id }, "queued");
                }, CancellationToken.None);
                job.MarkRunning();
                job.Result(new JObject { ["blob"] = new string('x', JobStatus.MaxRecordBytes * 2) }
                    .ToString(Newtonsoft.Json.Formatting.None));
                string artifact = Path.Combine(HorizunPaths.JobsDir(), "results", job.Id + ".json");
                byte[] bytes = File.ReadAllBytes(artifact);
                bytes[bytes.Length / 2] ^= 1;
                File.WriteAllBytes(artifact, bytes);
                job.Finish("ok", null);

                string taskId = (string)created["task"]["taskId"];
                JObject state = McpTasks.Get(new JObject { ["taskId"] = taskId });
                Assert.Equal("failed", (string)state["status"]);
                JObject result = McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException(), CancellationToken.None);
                Assert.True((bool)result["isError"]);
                Assert.Contains("hash", (string)result["content"][0]["text"], StringComparison.OrdinalIgnoreCase);
            });
        }

        [Fact]
        public void Job_retention_cannot_purge_a_task_before_its_first_poll()
        {
            WithDataRoot(() =>
            {
                Job job = null;
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan", ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 60000 }
                }, (request, token) =>
                {
                    job = Job.Start("horizun_model_scan");
                    job.ProtectUntil(DateTimeOffset.Parse((string)request["arguments"]["retain_until_utc"]));
                    return McpResult.Structured(new JObject { ["job_id"] = job.Id }, "queued");
                }, CancellationToken.None);
                job.MarkRunning();
                job.Result(@"{""complete"":true}");
                job.Finish("ok", null);

                DurableStoreRetention.Apply(HorizunPaths.JobsDir(), DurableStoreKind.Jobs,
                    key => key == "job_max_bytes" ? "1" : null, DateTime.UtcNow);
                Assert.True(File.Exists(job.Path));

                string taskId = (string)created["task"]["taskId"];
                Assert.Equal("completed", (string)McpTasks.Get(new JObject { ["taskId"] = taskId })["status"]);
                Assert.True((bool)McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException(), CancellationToken.None)
                    ["structuredContent"]["complete"]);
            });
        }

        [Fact]
        public void Lease_and_retention_race_never_publish_a_lease_for_a_deleted_job()
        {
            WithDataRoot(() =>
            {
                for (int i = 0; i < 16; i++)
                {
                    Job job = Job.Start("horizun_model_scan");
                    job.MarkRunning();
                    job.Result(@"{""iteration"":" + i + "}");
                    job.Finish("ok", null);
                    var start = new ManualResetEventSlim(false);
                    bool leaseSucceeded = false;
                    Task lease = Task.Run(() =>
                    {
                        start.Wait();
                        try
                        {
                            job.ProtectUntil(DateTimeOffset.UtcNow.AddMinutes(10));
                            leaseSucceeded = true;
                        }
                        catch (IOException) { }
                    });
                    Task purge = Task.Run(() =>
                    {
                        start.Wait();
                        DurableStoreRetention.Apply(HorizunPaths.JobsDir(), DurableStoreKind.Jobs,
                            key => key == "job_max_bytes" ? "1" : null, DateTime.UtcNow);
                    });
                    start.Set();
                    Task.WaitAll(lease, purge);

                    if (leaseSucceeded)
                    {
                        Assert.True(File.Exists(job.Path));
                        Assert.True(File.Exists(Path.Combine(Job.LeasesDir(), job.Id + ".json")));
                    }
                }
            });
        }

        [Fact]
        public void Submission_failure_snapshot_prevents_reenqueue_after_sidecar_crash_window()
        {
            WithDataRoot(() =>
            {
                JObject created = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan", ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 60000 }
                }, (request, token) => McpResult.Text("admission refused", true),
                    CancellationToken.None);
                string taskId = (string)created["task"]["taskId"];
                string sidecar = Path.Combine(HorizunPaths.DataRoot(), "mcp-tasks", taskId + ".json");
                JObject stale = JObject.Parse(File.ReadAllText(sidecar));
                stale["status"] = "working";
                stale["status_message"] = "admitting";
                stale["job_id"] = JValue.CreateNull();
                stale["arguments"] = new JObject();
                stale["submit_idempotency_key"] = "mcp-task-submit-" + taskId;
                File.WriteAllText(sidecar, stale.ToString(Newtonsoft.Json.Formatting.Indented));

                JObject result = McpTasks.WaitResult(new JObject { ["taskId"] = taskId },
                    (request, token) => throw new InvalidOperationException("must not re-submit"),
                    CancellationToken.None);
                Assert.True((bool)result["isError"]);
                Assert.Contains("admission refused", (string)result["content"][0]["text"]);
                Assert.Equal("failed", (string)McpTasks.Get(
                    new JObject { ["taskId"] = taskId })["status"]);
            });
        }

        [Fact]
        public void Locked_orphan_snapshot_counts_against_the_aggregate_store_budget()
        {
            // Windows denies delete/open under FileShare.None; Unix deliberately
            // permits unlinking an open inode. The production bridge is Windows-only,
            // and the hosted Linux gate cannot manufacture Windows share semantics.
            if (!OperatingSystem.IsWindows()) return;
            WithDataRoot(() =>
            {
                string directory = Path.Combine(HorizunPaths.DataRoot(), "mcp-tasks");
                Directory.CreateDirectory(directory);
                string orphan = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".result.json");
                using (var locked = new FileStream(orphan, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    locked.SetLength(McpTasks.MaxTaskStoreBytes);
                    McpError error = Assert.Throws<McpError>(() => McpTasks.Create(new JObject
                    {
                        ["name"] = "horizun_model_scan", ["arguments"] = new JObject(),
                        ["task"] = new JObject { ["ttl"] = 60000 }
                    }, (request, token) => throw new InvalidOperationException("store-full task must not submit"),
                    CancellationToken.None));
                    Assert.Equal(-32000, error.Code);
                    Assert.Contains("aggregate limit", error.Message);
                }
            });
        }

        [Fact]
        public void Expired_task_keeps_its_sidecar_when_snapshot_cannot_be_deleted()
        {
            if (!OperatingSystem.IsWindows()) return;
            WithDataRoot(() =>
            {
                JObject first = McpTasks.Create(new JObject
                {
                    ["name"] = "horizun_model_scan", ["arguments"] = new JObject(),
                    ["task"] = new JObject { ["ttl"] = 60000 }
                }, (request, token) => McpResult.Text("failed admission", true), CancellationToken.None);
                string taskId = (string)first["task"]["taskId"];
                string directory = Path.Combine(HorizunPaths.DataRoot(), "mcp-tasks");
                string sidecar = Path.Combine(directory, taskId + ".json");
                string snapshot = Path.Combine(directory, taskId + ".result.json");
                JObject record = JObject.Parse(File.ReadAllText(sidecar));
                record["created_at"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
                File.WriteAllText(sidecar, record.ToString());

                using (var locked = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    McpTasks.Create(new JObject
                    {
                        ["name"] = "horizun_model_scan", ["arguments"] = new JObject(),
                        ["task"] = new JObject { ["ttl"] = 60000 }
                    }, (request, token) => McpResult.Text("another failure", true), CancellationToken.None);
                    Assert.True(File.Exists(sidecar));
                    Assert.True(File.Exists(snapshot));
                }
            });
        }

        private static byte[] BuildRgbaPng()
        {
            using (var compressed = new MemoryStream())
            {
                using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true))
                    z.Write(new byte[] { 0, 10, 20, 30, 255 }, 0, 5);
                using (var png = new MemoryStream())
                {
                    png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                    var ihdr = new byte[] { 0,0,0,1, 0,0,0,1, 8,6,0,0,0 };
                    WriteChunk(png, "IHDR", ihdr);
                    WriteChunk(png, "IDAT", compressed.ToArray());
                    WriteChunk(png, "IEND", Array.Empty<byte>());
                    return png.ToArray();
                }
            }
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            byte[] length = { (byte)(data.Length >> 24), (byte)(data.Length >> 16),
                              (byte)(data.Length >> 8), (byte)data.Length };
            byte[] name = Encoding.ASCII.GetBytes(type);
            stream.Write(length, 0, 4); stream.Write(name, 0, 4); stream.Write(data, 0, data.Length);
            uint crc = 0xffffffff;
            foreach (byte value in name) crc = Crc(crc, value);
            foreach (byte value in data) crc = Crc(crc, value);
            crc ^= 0xffffffff;
            byte[] bytes = { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc };
            stream.Write(bytes, 0, 4);
        }

        private static uint Crc(uint crc, byte value)
        {
            crc ^= value;
            for (int i = 0; i < 8; i++) crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320u ^ (crc >> 1);
            return crc;
        }

        private static void WithDataRoot(Action action)
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Func<int, bool> savedProbe = PipeClient.LivenessProbe;
            string temp = Path.Combine(Path.GetTempPath(), "hz-mcp-tasks-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, temp);
                PipeClient.LivenessProbe = _ => true;
                action();
            }
            finally
            {
                PipeClient.LivenessProbe = savedProbe;
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved);
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }
}
