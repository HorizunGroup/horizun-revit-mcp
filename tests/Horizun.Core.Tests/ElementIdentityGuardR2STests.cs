using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // R2S - the minors of the independent review of R2R (m-1..m-9) and the category-only rule (m-6, revised by Atlas):
    // a category / type without a unique_id is refused on deletions and on elements without the Horizun ownership mark.
    public class ElementIdentityGuardR2STests
    {
        // 1..99 are ours (Horizun mark); 100..199 carry no mark (somebody else's); 200..299 have a mark that could not be read (Owned = null).
        // Every element N has the unique_id "uN"; 500 and up are views.
        private static ElementIdentityFacts Facts(long id)
        {
            if (id < 1 || id > 999) return null;
            bool view = id >= 500;
            return new ElementIdentityFacts
            {
                Id = id, UniqueId = "u" + id,
                Owned = id >= 200 && id < 300 ? (bool?)null : !(id >= 100 && id < 200),
                Category = view ? "Views" : "Walls", CategoryBuiltIn = view ? "OST_Views" : "OST_Walls",
                Type = view ? "Floor Plan" : "Generic - 200mm", Family = view ? "Floor Plan" : "Basic Wall"
            };
        }

        private static ElementIdentityFacts ByUid(string uid) =>
            uid != null && uid.StartsWith("u", StringComparison.Ordinal) && long.TryParse(uid.Substring(1), out long id) ? Facts(id) : null;

        private static JObject Req(string json) => JObject.Parse(json);
        private static JObject Pre(JObject r, string tool) { IdentityBinding.Reset(); return ElementIdentityGuard.PreCheck(r, tool, Facts, ByUid, () => false); }
        private static IdentityLookups Lookups() => new IdentityLookups { ById = Facts, ByUniqueId = ByUid };

        // ------------------------------------------------------------------ m-6: category-only identity

        [Fact]
        public void M6_a_category_only_identity_on_a_deletion_is_refused_asking_for_the_unique_id()
        {
            var r = Req("{\"ids\":[7],\"expected_category\":\"Walls\"}");
            JObject error = Pre(r, "horizun_delete_verified");
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.False((bool)error["write_started"]);
            Assert.Contains("unique_id", (string)error["detail"]);
            Assert.Contains("deletes", (string)error["detail"]);
            Assert.NotNull(r["expected_category"]);   // nothing was consumed
        }

        [Fact]
        public void M6_a_deleting_operation_of_another_covered_tool_is_refused_too()
        {
            var r = Req("{\"operation\":\"remove_grid_line\",\"element_id\":7,\"expected_type\":\"Generic - 200mm\"}");
            Assert.Contains("deletes", (string)Pre(r, "horizun_manage_curtain")["detail"]);
            var keep = Req("{\"operation\":\"set_mullions\",\"element_id\":7,\"expected_type\":\"Generic - 200mm\"}");
            Assert.Null(Pre(keep, "horizun_manage_curtain"));   // our own element, not a deletion: proceeds
        }

        [Fact]
        public void M6_a_category_only_identity_on_somebody_elses_element_is_refused()
        {
            var r = Req("{\"target_id\":150,\"expected_category\":\"Walls\"}");
            JObject error = Pre(r, "horizun_write_params_verified");
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("ownership mark", (string)error["detail"]);
            Assert.Contains("unique_id", (string)error["detail"]);
        }

        [Fact]
        public void M6_an_ownership_mark_that_cannot_be_read_counts_as_somebody_elses()
        {
            var r = Req("{\"target_id\":250,\"expected_category\":\"Walls\"}");
            JObject error = Pre(r, "horizun_write_params_verified");
            Assert.NotNull(error);
            Assert.Contains("could not be read", (string)error["detail"]);
        }

        [Fact]
        public void M6_a_category_only_identity_on_our_own_element_proceeds_with_the_visible_warning()
        {
            var r = Req("{\"target_id\":7,\"expected_category\":\"Walls\"}");
            Assert.Null(Pre(r, "horizun_write_params_verified"));
            JObject notice = IdentityBinding.Notice();
            Assert.Equal("categoria", (string)notice["identity_strength"]);
            Assert.Contains("Only unique_id identifies", (string)notice["note"]);
            // The reply of the call carries it at the top level, not only inside identity_check.
            var reply = IdentityPrepassCore.Stamp(CommandResult.Ok(new JObject { ["written"] = 1 }));
            var data = (JObject)reply.Data;
            Assert.Equal("categoria", (string)data["identity_strength"]);
            Assert.Contains("Only unique_id identifies", (string)data["identity_warning"]);
            Assert.Equal("categoria", (string)data["identity_check"]["identity_strength"]);
            IdentityBinding.Reset();
        }

        [Theory]
        [InlineData("horizun_delete_verified", "{\"ids\":[7],\"unique_ids\":[\"u7\"]}")]
        [InlineData("horizun_write_params_verified", "{\"target_id\":150,\"unique_id\":\"u150\"}")]
        [InlineData("horizun_write_params_verified", "{\"target_id\":250,\"unique_id\":\"u250\"}")]
        public void M6_with_a_unique_id_all_three_cases_proceed(string tool, string json)
        {
            var r = Req(json);
            Assert.Null(Pre(r, tool));
            Assert.Equal("unique_id", (string)IdentityBinding.Notice()["identity_strength"]);
            IdentityBinding.Reset();
        }

        [Fact]
        public void M6_validate_ids_is_unchanged_category_match_is_not_a_full_match()
        {
            var checks = new JArray(JObject.Parse("{\"id\":7,\"expected_category\":\"Walls\"}"));
            JObject answer = ElementIdentityGuard.Validate(checks, Facts, ByUid);
            Assert.Equal("coincide_categoria", (string)answer["results"][0]["status"]);
            Assert.False((bool)answer["summary"]["all_match"]);
            var strong = new JArray(JObject.Parse("{\"id\":150,\"unique_id\":\"u150\",\"expected_category\":\"Walls\"}"));
            Assert.True((bool)ElementIdentityGuard.Validate(strong, Facts, ByUid)["summary"]["all_match"]);
        }

        // ------------------------------------------------------------------ m-2: the linked document is decided before the host lookup

        [Fact]
        public void M2_an_id_of_a_linked_node_that_does_not_exist_in_the_host_is_diagnosed_as_a_linked_document_id()
        {
            var r = Req("{\"operation\":\"notes\",\"link_instance_id\":7,\"element_ids\":[5000],\"identity\":{\"element_ids\":[\"x\"]}}");
            Assert.Null(Facts(5000));
            JObject error = Pre(r, "horizun_planimetry_ops");
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("LINKED document", (string)error["detail"]);
        }

        // ------------------------------------------------------------------ m-3: the pre-pass, with a fake command

        private static CommandResult Exec(string tool, string json, out string seen, out int earlyCalls, Func<string, CommandResult> command = null)
        {
            string captured = null; int calls = 0;
            CommandResult r = IdentityPrepassCore.Execute(tool, json,
                request => { calls++; return IdentityPrepassCore.Early(request, tool, Lookups); },
                text => { captured = text; return command != null ? command(text) : CommandResult.Ok(new JObject { ["ran"] = true }); });
            seen = captured; earlyCalls = calls;
            return r;
        }

        [Fact]
        public void Prepass_a_covered_tool_without_identity_is_handed_the_original_text_untouched_and_not_inspected()
        {
            const string json = "{\"target_id\":7,\"values\":{}}";
            CommandResult r = Exec("horizun_write_params_verified", json, out string seen, out int early);
            Assert.True(r.Success);
            Assert.Equal(json, seen);
            Assert.Equal(0, early);
            Assert.Null(((JObject)r.Data)["identity_check"]);
        }

        [Fact]
        public void Prepass_json_that_does_not_parse_goes_to_the_command_which_reports_its_own_error()
        {
            CommandResult r = Exec("horizun_write_params_verified", "{not json unique_id", out string seen, out int early);
            Assert.Equal("{not json unique_id", seen);
            Assert.Equal(0, early);
            Assert.True(r.Success);   // whatever the command answered
        }

        [Fact]
        public void Prepass_a_near_miss_is_inspected_even_without_a_canonical_field_and_refused_before_any_model_lookup()
        {
            int lookups = 0;
            CommandResult r = IdentityPrepassCore.Execute("horizun_write_params_verified", "{\"target_id\":7,\"uniqueId\":\"u7\"}",
                request => IdentityPrepassCore.Early(request, "horizun_write_params_verified", () => { lookups++; return Lookups(); }),
                text => throw new InvalidOperationException("the command must not run"));
            Assert.False(r.Success);
            Assert.Equal(0, lookups);
            Assert.Contains("unique_id", r.Error);
        }

        [Fact]
        public void Prepass_the_command_receives_the_verified_ids_and_no_identity_fields_and_the_reply_is_stamped()
        {
            CommandResult r = Exec("horizun_write_params_verified", "{\"element_ids\":[7,8],\"unique_ids\":[\"u7\",\"u8\"]}", out string seen, out int early);
            Assert.Equal(1, early);
            JObject given = JObject.Parse(seen);
            Assert.Null(given["unique_ids"]);
            Assert.Null(given["identity"]);
            Assert.Equal(new long[] { 7, 8 }, given["element_ids"].Select(t => (long)t).ToArray());
            var data = (JObject)r.Data;
            Assert.Equal("unique_id", (string)data["identity_strength"]);
            Assert.Equal("unique_id", (string)data["identity_check"]["identity_strength"]);
        }

        [Fact]
        public void Prepass_a_refusal_never_runs_the_command_and_leaves_the_binding_clean()
        {
            bool ran = false;
            CommandResult r = Exec("horizun_write_params_verified", "{\"target_id\":7,\"unique_id\":\"u8\"}", out string seen, out int early,
                text => { ran = true; return CommandResult.Ok(new JObject()); });
            Assert.False(ran);
            Assert.False(r.Success);
            Assert.Null(seen);
            Assert.Equal("elemento_no_coincide", (string)r.Detail["reason"]);
            Assert.False(IdentityBinding.Carried);
        }

        [Fact]
        public void Prepass_an_identity_on_a_call_that_writes_nothing_is_refused_without_a_document()
        {
            int lookups = 0;
            CommandResult r = IdentityPrepassCore.Execute("horizun_join_geometry", "{\"operation\":\"read\",\"unique_id\":\"u7\",\"element_id\":7}",
                request => IdentityPrepassCore.Early(request, "horizun_join_geometry", () => { lookups++; return Lookups(); }),
                text => throw new InvalidOperationException("the command must not run"));
            Assert.False(r.Success);
            Assert.Equal(0, lookups);
            Assert.Contains("validate_ids", r.Error + r.Detail);
        }

        [Fact]
        public void Prepass_the_binding_is_reset_before_and_after_even_when_the_command_throws()
        {
            Pre(Req("{\"target_id\":7,\"unique_id\":\"u7\"}"), "horizun_write_params_verified");   // leaves a digest behind
            Assert.True(IdentityBinding.Carried);
            string digestSeenByTheNextCall = "unset";
            IdentityPrepassCore.Execute("horizun_health", "{}", r => null, t => { digestSeenByTheNextCall = IdentityBinding.Digest; return CommandResult.Ok(new JObject()); });
            Assert.Null(digestSeenByTheNextCall);   // reset first: nothing leaks into the next call

            Assert.Throws<InvalidOperationException>(() => IdentityPrepassCore.Execute("horizun_write_params_verified",
                "{\"target_id\":7,\"unique_id\":\"u7\"}",
                request => IdentityPrepassCore.Early(request, "horizun_write_params_verified", Lookups),
                text => { Assert.True(IdentityBinding.Carried); throw new InvalidOperationException("boom"); }));
            Assert.False(IdentityBinding.Carried);   // reset in finally
        }

        [Fact]
        public void Prepass_a_failure_at_the_second_node_leaves_the_whole_request_unconsumed()
        {
            string json = "{\"actions\":[{\"tool\":\"horizun_write_params_verified\",\"arguments\":{\"target_id\":7,\"unique_id\":\"u7\"}}," +
                          "{\"tool\":\"horizun_write_params_verified\",\"arguments\":{\"target_id\":8,\"unique_id\":\"u9\"}}]}";
            JObject request = Req(json);
            JObject before = (JObject)request.DeepClone();
            JObject error = Pre(request, "horizun_execute_plan");
            Assert.NotNull(error);
            Assert.True(JToken.DeepEquals(before, request));   // the first child was NOT normalized: nothing is consumed unless all passed
        }

        [Fact]
        public void Prepass_an_uncovered_tool_that_receives_an_identity_it_does_not_declare_is_refused_by_the_add_in_too()
        {
            foreach (string tool in new[] { "horizun_quantities", "horizun_plan_structure" })
            {
                Assert.False(ElementIdentityGuard.Covers(tool));
                bool ran = false;
                CommandResult r = IdentityPrepassCore.Execute(tool, "{\"unique_id\":\"u7\"}", q => null, t => { ran = true; return CommandResult.Ok(new JObject()); });
                Assert.False(ran);
                Assert.False(r.Success);
                Assert.Contains("unique_id", r.Error);
            }
            // a tool that does not carry any identity is left alone
            bool ranPlain = false;
            IdentityPrepassCore.Execute("horizun_health", "{}", q => null, t => { ranPlain = true; return CommandResult.Ok(new JObject()); });
            Assert.True(ranPlain);
        }

        // ------------------------------------------------------------------ m-4: the sweeps test production code

        [Fact]
        public void M4_IsIdArgumentName_is_the_production_rule()
        {
            foreach (string n in new[] { "ids", "pairs", "element_ids_a", "element_ids_b", "view_id", "wall_ids" }) Assert.True(ElementIdentityGuard.IsIdArgumentName(n), n);
            foreach (string n in new[] { "identity", "operation", "idempotency_key", "name", null }) Assert.False(ElementIdentityGuard.IsIdArgumentName(n), n);
        }

        [Fact]
        public void M4_view_visibility_and_manage_worksets_check_view_ids_through_the_loose_form()
        {
            // The loose form compares ONE key: the first present of the tool's list. With only view_ids present, a generic Spec has no
            // such key and refuses; the Spec of these two tools names it.
            foreach (string tool in new[] { "horizun_view_visibility", "horizun_manage_worksets" })
            {
                Assert.Null(Pre(Req("{\"operation\":\"hide\",\"view_ids\":[510],\"unique_ids\":[\"u510\"]}"), tool));
                JObject bad = Pre(Req("{\"operation\":\"hide\",\"view_ids\":[510],\"unique_ids\":[\"u511\"]}"), tool);
                Assert.Equal("view_ids", (string)bad["key"]);
            }
            // a generic Spec has no view_ids: the loose form cannot be resolved there (so the new Spec is what made the two above pass)
            Assert.NotNull(Pre(Req("{\"operation\":\"hide\",\"view_ids\":[510],\"unique_ids\":[\"u510\"]}"), "horizun_create_elements"));
        }

        private static string RepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Src(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel.Replace('/', Path.DirectorySeparatorChar)));

        [Fact]
        public void M4_the_runner_tools_reach_the_gate_through_ModelEditRunner_with_their_own_name()
        {
            string runner = Src("src/Horizun.Revit/Commands/ModelEditRunner.cs");
            Assert.Contains("DocumentGate.ForMutation(app, request, name)", runner);   // the runner gates with the name it is given
            string[] viaRunner =
            {
                "horizun_create_railing", "horizun_join_geometry", "horizun_manage_curtain", 
                "horizun_planimetry_ops", "horizun_room_finishes", "horizun_sheet_layout", "horizun_slab_shape",
                "horizun_view_crop", "horizun_view_visibility", "horizun_wall_finish", "horizun_wall_sweep"
            };
            var problems = new List<string>();
            string commands = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands");
            string[] entries = Directory.EnumerateFiles(commands, "*Command.cs")
                .Concat(Directory.EnumerateFiles(commands, "*Command.Entry.cs")).ToArray();
            foreach (string tool in viaRunner)
            {
                string file = entries
                    .FirstOrDefault(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), "string Name\\s*=>\\s*\"" + tool + "\""));
                if (file == null) { problems.Add(tool + ": no command file defines this Name"); continue; }
                string text = File.ReadAllText(file);
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"ModelEditRunner\.Run\(app,\s*\w+(\.ToString\([^)]*\))?,\s*Name\b"))
                    problems.Add(tool + ": " + Path.GetFileName(file) + " does not pass its own Name to ModelEditRunner.Run");
                if (!ElementIdentityGuard.Covers(tool)) problems.Add(tool + " is not covered");
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void M4_the_recipe_tools_reach_the_gate_through_RecipeCommand_with_their_own_name()
        {
            Assert.Contains("DocumentGate.ForMutation(app, request, Name)", Src("src/Horizun.Revit/Commands/RecipeCommand.cs"));
            string tools = Src("src/Horizun.Revit/Commands/RecipeTools.cs");
            var problems = new List<string>();
            foreach (string tool in new[]
            {
                "horizun_split_floor_loops", "horizun_split_multilayer_slabs", "horizun_ungroup_and_mark", "horizun_copy_slab_elevations",
                "horizun_embed_floors_in_toposolid", "horizun_grade_toposolid_around_floors", "horizun_rectangularize_walls"
            })
            {
                int at = tools.IndexOf("\"" + tool + "\"", StringComparison.Ordinal);
                if (at < 0) { problems.Add(tool + ": not defined in RecipeTools.cs"); continue; }
                int cls = tools.LastIndexOf("class ", at, StringComparison.Ordinal);
                if (cls < 0 || !tools.Substring(cls, at - cls).Contains(": RecipeCommand")) problems.Add(tool + ": its class is not a RecipeCommand");
                if (!ElementIdentityGuard.Covers(tool)) problems.Add(tool + " is not covered");
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        // ------------------------------------------------------------------ m-5: the coverage is part of the contract hash
        // (the table-by-table test lives in ElementIdentityGuardR2TTests: m1_every_table_moves_the_hash; R2T made the tables read-only)

        // ------------------------------------------------------------------ m-7: names the user chose are data

        // A property is a free-form map when its schema has no fixed `properties` and no combinator, is not closed (additionalProperties:false),
        // and is an object: either typed "object" or UNTYPED but with additionalProperties / patternProperties (R2T m-5).
        private static bool IsFreeFormMap(JObject ps)
        {
            if (ps["properties"] != null || ps["oneOf"] != null || ps["anyOf"] != null || ps["allOf"] != null) return false;
            if (ps["additionalProperties"] != null && ps["additionalProperties"].Type == JTokenType.Boolean && !(bool)ps["additionalProperties"]) return false;
            bool objectTyped = ps["type"] != null && ps["type"].ToString().Contains("object");
            bool mapShaped = ps["additionalProperties"] != null || ps["patternProperties"] != null;
            return objectTyped || (ps["type"] == null && mapShaped);
        }

        private static void Collect(JToken schema, string name, ISet<string> maps)
        {
            if (schema is JObject o)
            {
                if (o["properties"] is JObject props)
                    foreach (JProperty p in props.Properties())
                    {
                        if (p.Value is JObject ps && IsFreeFormMap(ps)) maps.Add(p.Name);
                        Collect(p.Value, p.Name, maps);
                    }
                foreach (JProperty p in o.Properties())
                    if (p.Name != "properties") Collect(p.Value, p.Name, maps);
            }
            else if (schema is JArray a) foreach (JToken t in a) Collect(t, name, maps);
        }

        [Fact]
        public void M7_every_map_keyed_by_user_names_in_a_covered_schema_is_listed()
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string tool in ElementIdentityGuard.CoveredTools)
                Collect(Contract.Find(tool).InputSchema, "", found);
            // Free-form objects whose keys are NOT user data (a fixed set of option names, the arguments of a plan child, our own identity map):
            // they are walked, so a misspelt identity field inside them is still caught. Every other free-form object must be a listed map.
            var fixedKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "arguments", "classification", "correction_plan", "display", "identity", "information_container", "inputs", "limits", "observed", "options",
                "overrides", "presentation", "requirement_set", "requirements", "spec", "structural", "sun", "template_kit", "thermal",
                "tolerances", "warning_profile", "workset_rules", "write"
            };
            var missing = found.Where(n => !ElementIdentityGuard.UserKeyedMaps.Contains(n) && !fixedKeys.Contains(n)).ToList();
            Assert.True(missing.Count == 0, "free-form objects neither listed in UserKeyedMaps nor justified as fixed-key: " + string.Join(", ", missing));
            var stale = ElementIdentityGuard.UserKeyedMaps.Where(n => !found.Contains(n)).ToList();
            Assert.True(stale.Count == 0, "UserKeyedMaps names no covered schema declares as a map: " + string.Join(", ", stale));
        }

        [Fact]
        public void M7_a_parameter_called_identity_or_Identity_in_a_user_keyed_map_is_data_not_an_identity_field()
        {
            const string tool = "horizun_write_params_verified";
            foreach (string json in new[]
            {
                "{\"selector\":{\"category\":\"OST_Walls\"},\"set\":{\"identity\":\"A\",\"Identity\":\"B\",\"unique_id\":\"C\",\"uniqueId\":\"D\"}}",
                "{\"tabular_source\":{\"path\":\"x.csv\",\"key_column\":\"k\",\"category\":\"OST_Walls\",\"value_columns\":{\"Identity\":\"identity\",\"unique_id\":\"Uniqueid\"}}}"
            })
            {
                JObject r = Req(json);
                JObject before = (JObject)r.DeepClone();
                Assert.Null(ElementIdentityGuard.NearMiss(r, tool));
                Assert.Null(ElementIdentityGuard.ShapeCheck(r, tool));
                Assert.False(ElementIdentityGuard.NeedsCheck(r, tool));
                Assert.Null(Pre(r, tool));
                Assert.True(JToken.DeepEquals(before, r));   // untouched
            }
            // The same words as REAL arguments are still caught.
            Assert.NotNull(ElementIdentityGuard.NearMiss(Req("{\"target_id\":7,\"Identity\":{}}"), tool));
            Assert.NotNull(ElementIdentityGuard.NearMiss(Req("{\"writes\":[{\"target_id\":7,\"uniqueId\":\"u7\"}]}"), tool));
        }

        // The tools new in 4.2: the sweep of user-keyed maps reaches every one of them, and each free-form object they declare is either listed or justified.
        private static readonly string[] Tools42 =
        {
            "horizun_planimetry_ops", "horizun_sheet_layout", "horizun_room_finishes", 
            "horizun_pack_sheets", "horizun_manage_revisions", "horizun_manage_schedules", "horizun_structural_connections",
            "horizun_manage_materials", "horizun_link_schedule"
        };

        [Fact]
        public void M7_the_sweep_reaches_the_maps_of_the_4_2_tools_and_ifc_setup()
        {
            var all = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string tool in ElementIdentityGuard.CoveredTools) Collect(Contract.Find(tool).InputSchema, "", all);
            foreach (string tool in Tools42)
            {
                Assert.True(ElementIdentityGuard.Covers(tool), tool + " is not covered");
                var own = new SortedSet<string>(StringComparer.Ordinal);
                Collect(Contract.Find(tool).InputSchema, "", own);
                foreach (string map in own)
                {
                    Assert.Contains(map, all);   // the global sweep (M7_every_map_keyed_by_user_names...) sees, and judges, what the tool declares
                }
            }
        }

        [Fact]
        public void M7_the_set_and_value_columns_maps_are_found_in_the_schemas_and_are_listed()
        {
            // Derived from the schemas, not asserted against the list itself: the sweep must FIND these maps, and the list must name them.
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string tool in ElementIdentityGuard.CoveredTools) Collect(Contract.Find(tool).InputSchema, "", found);
            foreach (string name in new[] { "set", "value_columns" })
            {
                Assert.Contains(name, found);
                Assert.Contains(name, ElementIdentityGuard.UserKeyedMaps);
            }
        }

        [Fact]
        public void M7_the_sweep_detects_maps_declared_with_patternProperties_or_without_a_type()
        {
            var found = new SortedSet<string>(StringComparer.Ordinal);
            Collect(JObject.Parse("{\"properties\":{\"by_pattern\":{\"type\":\"object\",\"patternProperties\":{\"^x\":{\"type\":\"string\"}}}," +
                                  "\"untyped\":{\"additionalProperties\":{\"type\":\"string\"}}," +
                                  "\"typed_map\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"number\"}}," +
                                  "\"closed\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}},\"additionalProperties\":false}," +
                                  "\"plain\":{\"type\":\"string\"}}}"), "", found);
            Assert.Contains("by_pattern", found);
            Assert.Contains("untyped", found);
            Assert.Contains("typed_map", found);
            Assert.DoesNotContain("closed", found);
            Assert.DoesNotContain("plain", found);
        }

        // ------------------------------------------------------------------ m-8: a correction step is verified with its own tool's Spec

        private static JObject Plan(string tool, string arguments) => Req(
            "{\"target_document\":\"d\",\"correction_plan\":{\"pasos\":[{\"id\":\"s1\",\"herramienta\":\"" + tool + "\",\"argumentos\":" + arguments + "}]}}");

        [Fact]
        public void M8_a_correction_step_is_verified_with_the_spec_of_its_own_tool_and_consumed()
        {
            // schedule_id belongs to manage_schedules and is not a key of the generic Spec apply_corrections has.
            JObject ok = Plan("horizun_manage_schedules", "{\"target_document\":\"d\",\"dry_run\":true,\"schedule_id\":7,\"unique_id\":\"u7\"}");
            Assert.Null(Pre(ok, "horizun_apply_corrections"));
            JObject step = (JObject)ok["correction_plan"]["pasos"][0]["argumentos"];
            Assert.Null(step["unique_id"]);
            Assert.Equal(7, (int)step["schedule_id"]);
            JObject bad = Plan("horizun_manage_schedules", "{\"target_document\":\"d\",\"schedule_id\":7,\"unique_id\":\"u8\"}");
            JObject error = Pre(bad, "horizun_apply_corrections");
            Assert.Equal("schedule_id", (string)error["key"]);
            Assert.Contains("correction_plan.pasos[0]", (string)error["path"]);
        }

        [Fact]
        public void M8_a_correction_step_of_a_tool_the_guard_does_not_cover_is_refused_when_it_carries_identity()
        {
            JObject error = Pre(Plan("horizun_audit_model", "{\"target_document\":\"d\",\"unique_id\":\"u7\"}"), "horizun_apply_corrections");
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("correction_plan.pasos[0]", (string)error["path"]);
            Assert.Null(Pre(Plan("horizun_audit_model", "{\"target_document\":\"d\"}"), "horizun_apply_corrections"));
        }

        // ------------------------------------------------------------------ m-9: the digest is canonical

        [Fact]
        public void M9_the_same_identity_in_another_order_or_shorthand_has_the_same_digest()
        {
            string a = "{\"view_id\":510,\"sheet_id\":7,\"identity\":{\"view_id\":\"u510\",\"sheet_id\":{\"unique_id\":\"u7\",\"expected_category\":\"Walls\"}}}";
            string b = "{\"sheet_id\":7,\"identity\":{\"sheet_id\":{\"expected_category\":\"Walls\",\"unique_id\":\"u7\"},\"view_id\":{\"unique_id\":\"u510\"}},\"view_id\":510}";
            Assert.Null(Pre(Req(a), "horizun_manage_views"));
            string first = IdentityBinding.Digest;
            Assert.Null(Pre(Req(b), "horizun_manage_views"));
            Assert.Equal(first, IdentityBinding.Digest);
            Assert.Null(Pre(Req(a.Replace("u510", "u511").Replace("\"view_id\":510", "\"view_id\":511")), "horizun_manage_views"));
            Assert.NotEqual(first, IdentityBinding.Digest);
            IdentityBinding.Reset();
        }

        [Fact]
        public void M9_a_token_issued_for_one_order_is_valid_for_the_other_order_of_the_same_map()
        {
            var store = new ConfirmationStore();
            JObject rehearsal = Req("{\"view_id\":510,\"sheet_id\":7,\"identity\":{\"view_id\":\"u510\",\"sheet_id\":\"u7\"}}");
            Assert.Null(Pre(rehearsal, "horizun_manage_views"));
            string hashA = ConfirmationStore.PlanHash(rehearsal, "view_id", "sheet_id");
            JObject apply = Req("{\"sheet_id\":7,\"view_id\":510,\"identity\":{\"sheet_id\":\"u7\",\"view_id\":\"u510\"}}");
            Assert.Null(Pre(apply, "horizun_manage_views"));
            Assert.Equal(hashA, ConfirmationStore.PlanHash(apply, "view_id", "sheet_id"));
            IdentityBinding.Reset();
        }
    }
}
