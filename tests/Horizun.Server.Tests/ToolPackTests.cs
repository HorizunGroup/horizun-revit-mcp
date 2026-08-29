// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// TOOL PACKS, proved from the outside: what tools/list actually advertises under
// each configuration, that hidden means refused and not merely unlisted, that
// the dangerous capability cannot ride in on any pack, and that the map itself
// cannot rot (a pack naming a renamed tool, a contract tool no pack carries).
//
// The stakes: a pack that quietly dropped horizun_health would strand every
// session at its first call; a pack that quietly INCLUDED execute_python would
// hand out arbitrary code with a friendlier name; and a golden list nobody pins
// is a golden list that drifts.
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
    public class ToolPackTests
    {
        // ---- the map itself ---------------------------------------------------

        [Fact]
        public void Every_pack_tool_exists_in_the_contract_and_every_pack_declares_dependencies()
        {
            List<string> problems = ToolPacks.Audit(name => Horizun.Contracts.Contract.Find(name) != null);
            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }

        [Fact]
        public void Every_contract_tool_belongs_to_at_least_one_pack()
        {
            var orphans = new List<string>();
            foreach (Horizun.Contracts.CommandContract c in Horizun.Contracts.Contract.All)
            {
                bool found = ToolPacks.KnownPacks.Any(p => ToolPacks.MembersOf(p).Contains(c.Name));
                if (!found) orphans.Add(c.Name);
            }
            Assert.True(orphans.Count == 0,
                "These tools belong to no pack, so no restricted session could ever reach them: " +
                string.Join(", ", orphans) + ". Add each to the pack(s) whose kind of work calls it.");
        }

        [Fact]
        public void The_core_pack_is_exactly_the_four_tools_a_session_cannot_live_without()
        {
            Assert.Equal(new[] { "horizun_health", "horizun_target", "horizun_job_status", "horizun_submit_job" },
                         ToolPacks.MembersOf("core"));
        }

        [Fact]
        public void Execute_python_rides_in_ONLY_on_the_unsafe_code_pack()
        {
            foreach (string pack in ToolPacks.KnownPacks)
            {
                bool carries = ToolPacks.MembersOf(pack).Contains("horizun_execute_python") ||
                               ToolPacks.MembersOf(pack).Contains("horizun_request_python_access");
                Assert.True(carries == (pack == "unsafe_code"),
                    "pack '" + pack + "' " + (carries ? "carries" : "does not carry") + " the Python surface.");
            }
        }

        [Fact]
        public void Document_session_tools_ride_only_on_administration()
        {
            foreach (string tool in new[]
            {
                "horizun_document_session", "horizun_open_document", "horizun_save_document",
                "horizun_relinquish_all"
            })
                foreach (string pack in ToolPacks.KnownPacks)
                    Assert.True(ToolPacks.MembersOf(pack).Contains(tool) == (pack == "administration"),
                        tool + " vs pack '" + pack + "'");
        }

        [Fact]
        public void No_pack_advertises_a_tool_whose_mandatory_flow_it_hides()
        {
            // The flows with a hard input dependency: the consumer's pack must carry
            // the producer, directly or through Requires.
            var flows = new[]
            {
                new { Consumer = "horizun_fix_planimetry", Producer = "horizun_audit_planimetry" },
                new { Consumer = "horizun_plan_annotations", Producer = "horizun_annotate" },
                new { Consumer = "horizun_plan_views", Producer = "horizun_manage_views" },
                new { Consumer = "horizun_annotate", Producer = "horizun_get_dimension_references" },
                new { Consumer = "horizun_edit_dimensions", Producer = "horizun_query_dimensions" }
            };
            foreach (var flow in flows)
                foreach (string pack in ToolPacks.KnownPacks)
                {
                    if (!ToolPacks.MembersOf(pack).Contains(flow.Consumer)) continue;
                    ToolPacks.Resolution r = ToolPacks.Resolve(null, new[] { pack }, false);
                    Assert.True(r.Tools().Contains(flow.Producer),
                        "pack '" + pack + "' shows " + flow.Consumer + " but selecting it alone hides " +
                        flow.Producer + " - the workflow dead-ends. Add the producer to the pack or a Requires " +
                        "edge to the pack that carries it.");
                }
        }

        // ---- resolution --------------------------------------------------------

        [Fact]
        public void The_default_is_everything_and_the_all_token_restores_it()
        {
            Assert.False(ToolPacks.Resolve(null, null, false).Restricting);
            Assert.False(ToolPacks.Resolve("all", null, false).Restricting);
            Assert.False(ToolPacks.Resolve(null, new[] { "all" }, false).Restricting);
        }

        [Fact]
        public void Dependencies_arrive_transitively_and_are_reported_as_such()
        {
            ToolPacks.Resolution r = ToolPacks.Resolve(null, new[] { "planimetry" }, false);
            Assert.Equal(new[] { "documentation", "planimetry", "read" }, r.ActivePacks.ToArray());
            Assert.Equal(new[] { "documentation", "read" }, r.AddedByDependency.ToArray());
            Assert.Equal(new[] { "planimetry" }, r.ChosenPacks.ToArray());

            ToolPacks.Resolution powerbi = ToolPacks.Resolve(null, new[] { "powerbi" }, false);
            Assert.Contains("interoperability", powerbi.ActivePacks);
            Assert.Contains("read", powerbi.ActivePacks);
        }

        [Fact]
        public void An_unknown_pack_falls_closed_to_core_only_with_the_problem_named()
        {
            ToolPacks.Resolution r = ToolPacks.Resolve(null, new[] { "documentation", "plumbing" }, false);
            Assert.Equal(ToolPacks.SelectionSource.Malformed, r.Source);
            Assert.Contains("plumbing", r.Problem);
            Assert.True(r.Restricting);
            // Core survives even the broken configuration.
            Assert.Contains("horizun_health", r.Tools());
            Assert.DoesNotContain("horizun_manage_views", r.Tools());
        }

        [Fact]
        public void A_malformed_settings_value_falls_closed_not_open()
        {
            ToolPacks.Resolution r = ToolPacks.Resolve(null, null, settingsValueMalformed: true);
            Assert.Equal(ToolPacks.SelectionSource.Malformed, r.Source);
            Assert.True(r.Restricting);
            Assert.Contains("horizun_health", r.Tools());
        }

        [Fact]
        public void The_environment_override_wins_over_the_settings_file()
        {
            ToolPacks.Resolution r = ToolPacks.Resolve("read", new[] { "documentation" }, false);
            Assert.Equal(ToolPacks.SelectionSource.Environment, r.Source);
            Assert.Equal(new[] { "read" }, r.ActivePacks.ToArray());

            ToolPacks.Resolution restore = ToolPacks.Resolve("all", new[] { "documentation" }, false);
            Assert.False(restore.Restricting);
        }

        [Fact]
        public void The_hidden_reason_names_the_packs_that_would_surface_the_tool()
        {
            ToolPacks.Resolution r = ToolPacks.Resolve(null, new[] { "read" }, false);
            string reason = ToolPacks.HiddenReason("horizun_manage_views", r);
            Assert.Contains("documentation", reason);
            Assert.Contains("tool_packs", reason);
            Assert.Contains("list_changed", reason);
        }

        // ---- through the real tools/list ---------------------------------------

        [Fact]
        public void A_pack_selection_shrinks_tools_list_without_touching_any_schema()
        {
            WithDataRoot(null, () =>
            {
                JArray everything = Tools.List(false);
                JObject fullSchema = (JObject)everything.OfType<JObject>()
                    .First(t => (string)t["name"] == "horizun_query_model")["inputSchema"];

                WriteSettings("{\"tool_packs\":[\"read\"]}");
                JArray restricted = Tools.List(false);

                Assert.True(restricted.Count < everything.Count,
                    "read-pack list (" + restricted.Count + ") is not smaller than the default (" +
                    everything.Count + ")");
                var names = restricted.OfType<JObject>().Select(t => (string)t["name"]).ToHashSet();
                Assert.Contains("horizun_health", names);
                Assert.Contains("horizun_query_model", names);
                Assert.DoesNotContain("horizun_manage_views", names);
                Assert.DoesNotContain("horizun_execute_python", names);

                // The schema of a surviving tool is BYTE-identical: packs decide whether,
                // never what.
                JObject restrictedSchema = (JObject)restricted.OfType<JObject>()
                    .First(t => (string)t["name"] == "horizun_query_model")["inputSchema"];
                Assert.True(JToken.DeepEquals(fullSchema, restrictedSchema));
            });
        }

        [Fact]
        public void A_hidden_tool_is_refused_at_dispatch_not_merely_unlisted()
        {
            WithDataRoot("{\"tool_packs\":[\"read\"]}", () =>
            {
                string reason;
                bool allowed = Settings.IsToolAllowed(
                    Horizun.Contracts.Contract.Find("horizun_manage_views"), out reason);
                Assert.False(allowed);
                Assert.Contains("documentation", reason);
            });
        }

        [Fact]
        public void Read_only_profile_still_removes_writes_from_a_pack_that_carries_them()
        {
            WithDataRoot("{\"tool_packs\":[\"documentation\"],\"permission_profile\":\"read_only\"}", () =>
            {
                var names = Tools.List(false).OfType<JObject>().Select(t => (string)t["name"]).ToHashSet();
                Assert.Contains("horizun_query_dimensions", names);   // read tool of the pack
                Assert.DoesNotContain("horizun_annotate", names);      // write tool: profile wins
                Assert.DoesNotContain("horizun_manage_views", names);
            });
        }

        [Fact]
        public void Unsafe_code_pack_alone_does_not_surface_python_without_the_owner_grant()
        {
            WithDataRoot("{\"tool_packs\":[\"unsafe_code\",\"read\"]}", () =>
            {
                var names = Tools.List(false).OfType<JObject>().Select(t => (string)t["name"]).ToHashSet();
                // The pack admits the tool; the OWNER switch still gates it. A pack is a
                // visibility choice, never an elevation.
                Assert.DoesNotContain("horizun_execute_python", names);
                Assert.Contains("horizun_request_python_access", names);
            });
        }

        [Fact]
        public void The_golden_lists_of_the_recommended_profiles()
        {
            // The exact tool set of each recommended profile, pinned. A drift here is
            // either deliberate (update the golden) or a regression (fix the map);
            // either way it cannot be silent.
            WithDataRoot(null, () =>
            {
                // capture_view is IN the read pack and absent from both lists: it
                // writes a PNG outside the model, and the default safe_write profile
                // hides external effects. A pack is a visibility choice; the profile
                // stays the authority on effects, and the goldens pin that layering.
                AssertProfile(new[] { "read" }, new[]
                {
                    "get_document_info", "horizun_audit_cad_model", "horizun_audit_reinforcement",
                    "horizun_file_info",
                    "horizun_get_dimension_references", "horizun_get_schedule_data", "horizun_health",
                    "horizun_job_status", "horizun_list_elements", "horizun_list_schedules",
                    "horizun_model_scan", "horizun_navigate", "horizun_plan_cad_update",
                    "horizun_plan_from_cad", "horizun_plan_reinforcement", "horizun_quantities",
                    "horizun_query_cad", "horizun_query_detail_2d", "horizun_query_dimensions", "horizun_query_model",
                    "horizun_query_planimetry", "horizun_query_structure", "horizun_submit_job",
                    "horizun_target"
                });
                AssertProfile(new[] { "schedules" }, new[]
                {
                    "get_document_info", "horizun_audit_cad_model", "horizun_audit_reinforcement",
                    "horizun_create_schedule",
                    "horizun_file_info", "horizun_get_dimension_references", "horizun_get_schedule_data",
                    "horizun_health", "horizun_job_status", "horizun_list_elements",
                    "horizun_list_schedules", "horizun_manage_schedules", "horizun_model_scan",
                    "horizun_navigate", "horizun_plan_cad_update", "horizun_plan_from_cad",
                    "horizun_plan_reinforcement",
                    "horizun_quantities", "horizun_query_cad", "horizun_query_detail_2d",
                    "horizun_query_dimensions", "horizun_query_model", "horizun_query_planimetry",
                    "horizun_query_structure", "horizun_submit_job", "horizun_target"
                });
            });
        }

        [Fact]
        public void The_pack_sizes_are_measured_so_the_context_cost_is_a_number()
        {
            WithDataRoot(null, () =>
            {
                int fullCount = Tools.List(false).Count;
                long fullBytes = Tools.List(false).ToString(Newtonsoft.Json.Formatting.None).Length;
                var report = new List<string> { "all: " + fullCount + " tools, " + fullBytes + " bytes" };
                foreach (string pack in ToolPacks.KnownPacks.OrderBy(p => p, StringComparer.Ordinal))
                {
                    WriteSettings("{\"tool_packs\":[\"" + pack + "\"]}");
                    JArray list = Tools.List(false);
                    long bytes = list.ToString(Newtonsoft.Json.Formatting.None).Length;
                    report.Add(pack + ": " + list.Count + " tools, " + bytes + " bytes");
                    Assert.True(list.Count <= fullCount);
                    Assert.True(bytes <= fullBytes);
                }
                // The numbers travel in the assertion so a failing run prints the report.
                // The default profile (safe_write) already hides the session/external/
                // Python tools, so "everything" here is smaller than the contract count.
                Assert.True(fullCount >= 45, string.Join(" | ", report));
            });
        }

        // ---- plumbing ----------------------------------------------------------

        private static string _dir;

        private static void AssertProfile(string[] packs, string[] expected)
        {
            WriteSettings("{\"tool_packs\":[" +
                string.Join(",", packs.Select(p => "\"" + p + "\"")) + "]}");
            var names = Tools.List(false).OfType<JObject>()
                .Select(t => (string)t["name"]).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, names);
        }

        private static void WriteSettings(string json)
        {
            File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
        }

        private static void WithDataRoot(string settingsJson, Action action)
        {
            string old = Environment.GetEnvironmentVariable("HORIZUN_DATA_ROOT");
            string oldPacks = Environment.GetEnvironmentVariable(ToolPacks.EnvironmentOverride);
            _dir = Path.Combine(Path.GetTempPath(), "hz-packs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            try
            {
                Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", _dir);
                Environment.SetEnvironmentVariable(ToolPacks.EnvironmentOverride, null);
                if (settingsJson != null) WriteSettings(settingsJson);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable("HORIZUN_DATA_ROOT", old);
                Environment.SetEnvironmentVariable(ToolPacks.EnvironmentOverride, oldPacks);
                try { Directory.Delete(_dir, true); } catch { }
            }
        }
    }
}
