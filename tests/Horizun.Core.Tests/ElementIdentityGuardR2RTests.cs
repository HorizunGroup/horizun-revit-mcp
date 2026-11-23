using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // R2R - closing P1-4 for real: the findings of the independent review of R2Q (B-1, M-1, M-2, m-1..m-5).
    // Everything here goes through the entry the dispatcher uses (ElementIdentityGuard.PreCheck) and then through the
    // validators that used to refuse the identity before the guard ever ran.
    public class ElementIdentityGuardR2RTests
    {
        // Every element N has the unique_id "uN", is a wall, and exists (1..999); 500 and up are views. "text-N" are text notes.
        private static ElementIdentityFacts Facts(long id)
        {
            if (id < 1 || id > 999) return null;
            bool view = id >= 500;
            return new ElementIdentityFacts
            {
                Owned = true, Id = id, UniqueId = "u" + id,
                Category = view ? "Views" : "Walls", CategoryBuiltIn = view ? "OST_Views" : "OST_Walls",
                Type = view ? "Floor Plan" : "Generic - 200mm", Family = view ? "Floor Plan" : "Basic Wall"
            };
        }

        private static ElementIdentityFacts ByUid(string uid) => uid != null && uid.StartsWith("u", StringComparison.Ordinal) && long.TryParse(uid.Substring(1), out long id) ? Facts(id) : null;
        private static JObject Req(string json) => JObject.Parse(json);
        private static JObject Pre(JObject r, string tool) { IdentityBinding.Reset(); return ElementIdentityGuard.PreCheck(r, tool, Facts, ByUid, () => false); }

        private static bool HasIdentityName(JToken t)
        {
            if (t is JObject o) return o.Properties().Any(p => ElementIdentityGuard.IsIdentityName(p.Name) || HasIdentityName(p.Value));
            if (t is JArray a) return a.Any(HasIdentityName);
            return false;
        }

        // ------------------------------------------------------------------ B-1: every covered tool takes the identity

        public static IEnumerable<object[]> RootKeyedTools()
        {
            foreach (string tool in ElementIdentityGuard.CoveredTools.OrderBy(t => t, StringComparer.Ordinal))
            {
                if (tool == "horizun_execute_plan" || tool == "horizun_annotate" || tool == "horizun_edit_dimensions") continue;   // plan / per-action tools: tested below
                IReadOnlyList<string> singles = ElementIdentityGuard.DocumentedKeys(tool, false);
                IReadOnlyList<string> lists = ElementIdentityGuard.DocumentedKeys(tool, true);
                string key = singles.FirstOrDefault() ?? lists.First();
                yield return new object[] { tool, key, singles.Contains(key) };
            }
        }

        [Theory]
        [MemberData(nameof(RootKeyedTools))]
        public void B1_every_covered_tool_verifies_an_identity_by_key_and_hands_the_command_a_clean_request(string tool, string key, bool single)
        {
            JToken ids = single ? (JToken)new JValue(7L) : new JArray(7L);
            JToken uid = single ? (JToken)"u7" : new JArray("u7");

            var ok = new JObject { ["operation"] = "x", [key] = ids, ["identity"] = new JObject { [key] = uid } };
            if (tool == "horizun_capture_view") ok["element_ids"] = new JArray(7L);   // a capture with no temporary option writes nothing
            Assert.Null(Pre(ok, tool));
            Assert.False(HasIdentityName(ok), tool + ": the command must receive a request without identity fields");
            Assert.True(IdentityBinding.Carried);

            var bad = new JObject { ["operation"] = "x", [key] = ids, ["identity"] = new JObject { [key] = single ? (JToken)"u8" : new JArray("u8") } };
            if (tool == "horizun_capture_view") bad["element_ids"] = new JArray(7L);
            JObject error = Pre(bad, tool);
            Assert.NotNull(error);
            Assert.Equal("precondicion_no_cumplida", (string)error["error_code"]);
            Assert.Equal(key, (string)error["key"]);
            Assert.False((bool)error["write_started"]);
        }

        [Theory]
        [MemberData(nameof(RootKeyedTools))]
        public void B1_the_bare_fields_reach_every_covered_tool_too(string tool, string key, bool single)
        {
            var r = new JObject { ["operation"] = "x", [key] = single ? (JToken)new JValue(7L) : new JArray(7L) };
            r[single ? "unique_id" : "unique_ids"] = single ? (JToken)"u7" : new JArray("u7");
            if (tool == "horizun_capture_view") r["element_ids"] = new JArray(7L);
            // The bare form compares with the FIRST present key of the tool's list; a tool whose first key is not `key` still has to
            // either accept (that key) or name the problem. What it must never do is ignore the fields.
            JObject error = Pre(r, tool);
            if (error == null) Assert.False(HasIdentityName(r), tool + ": accepted identity fields must be consumed, never left behind");
            else Assert.True(error["reason"] != null);
        }

        [Fact]
        public void B1_every_covered_tool_declares_at_least_one_id_property_an_identity_can_be_attached_to()
        {
            // A tool is covered only if an identity can be attached to something its schema accepts (identity{} takes any id key).
            var uncovered = new List<string>();
            foreach (string tool in ElementIdentityGuard.CoveredTools)
            {
                if (tool == "horizun_execute_plan") continue;
                var names = new HashSet<string>(StringComparer.Ordinal);
                Collect(Contract.Find(tool).InputSchema, names);
                if (!names.Any(ElementIdentityGuard.IsIdArgumentName)) uncovered.Add(tool);
            }
            Assert.True(uncovered.Count == 0, "covered tools whose contract declares no id property: " + string.Join(", ", uncovered));
        }

        private static void Collect(JToken node, HashSet<string> names)
        {
            if (node is JObject o)
            {
                foreach (JProperty p in o.Properties())
                {
                    if (p.Name == "properties" && p.Value is JObject props) foreach (JProperty q in props.Properties()) names.Add(q.Name);
                    Collect(p.Value, names);
                }
            }
            else if (node is JArray a) foreach (JToken t in a) Collect(t, names);
        }

        // The validators that refused the identity before the guard ran (review B-1): raw they refuse, after the pre-pass they accept.
        public static IEnumerable<object[]> Validators()
        {
            yield return new object[] { "joingeometry", "horizun_join_geometry",
                "{\"target_document\":\"d\",\"operation\":\"corners\",\"element_ids\":[7,8],\"unique_ids\":[\"u7\",\"u8\"]}" };
            yield return new object[] { "joingeometry", "horizun_join_geometry",
                "{\"target_document\":\"d\",\"operation\":\"join\",\"pairs\":[[7,8]],\"identity\":{\"pairs[0][0]\":\"u7\",\"pairs[0][1]\":\"u8\"}}" };
            yield return new object[] { "sheetlayout", "horizun_sheet_layout",
                "{\"target_document\":\"d\",\"operation\":\"auto_layout\",\"sheet_id\":600,\"identity\":{\"sheet_id\":\"u600\"}}" };
            yield return new object[] { "railing", "horizun_create_railing",
                "{\"target_document\":\"d\",\"type_id\":7,\"host_id\":8,\"identity\":{\"host_id\":\"u8\",\"type_id\":\"u7\"}}" };
            yield return new object[] { "corrections", "horizun_apply_corrections",
                "{\"target_document\":\"T\",\"finding_set_fingerprint\":\"fs:x\",\"actions\":[{\"finding_id\":\"f:1\",\"element_ids\":[7],\"unique_ids\":[\"u7\"]}]}" };
        }





        [Fact]
        public void B1_an_identity_at_the_plan_level_of_a_child_is_consumed_before_the_child_parses_it()
        {
            var plan = Req("{\"actions\":[{\"tool\":\"horizun_join_geometry\",\"arguments\":{\"target_document\":\"d\",\"operation\":\"join\",\"pairs\":[[7,8]],\"identity\":{\"pairs[0][1]\":\"u8\"}}}]}");
            Assert.Null(Pre(plan, "horizun_execute_plan"));
            Assert.False(HasIdentityName(plan));
            var bad = Req("{\"actions\":[{\"tool\":\"horizun_join_geometry\",\"arguments\":{\"target_document\":\"d\",\"operation\":\"join\",\"pairs\":[[7,8]],\"identity\":{\"pairs[0][1]\":\"u9\"}}}]}");
            Assert.Equal("pairs[0][1]", (string)Pre(bad, "horizun_execute_plan")["key"]);
        }

        // ------------------------------------------------------------------ M-2: a category alone does not identify

        [Fact]
        public void M2_a_category_only_expectation_proceeds_but_the_reply_says_it_cannot_tell_two_elements_of_the_category_apart()
        {
            var r = Req("{\"element_id\":7,\"expected_category\":\"OST_Walls\"}");
            Assert.Null(Pre(r, "horizun_write_params_verified"));
            Assert.True(IdentityBinding.Weak);
            JObject notice = IdentityBinding.Notice();
            Assert.Equal("categoria", (string)notice["identity_strength"]);
            Assert.Contains("element_id", notice["keys_without_unique_id"].Values<string>());
            Assert.Contains("same category", (string)notice["note"]);
            Assert.Contains("unique_id", (string)notice["note"]);
        }

        [Fact]
        public void M2_with_a_unique_id_the_strength_is_unique_id_and_there_is_no_warning()
        {
            var r = Req("{\"element_id\":7,\"unique_id\":\"u7\",\"expected_category\":\"OST_Walls\"}");
            Assert.Null(Pre(r, "horizun_write_params_verified"));
            Assert.False(IdentityBinding.Weak);
            Assert.Equal("unique_id", (string)IdentityBinding.Notice()["identity_strength"]);
            Assert.Null(IdentityBinding.Notice()["note"]);
        }

        [Fact]
        public void M2_one_weak_key_among_strong_ones_makes_the_call_weak_and_names_that_key()
        {
            var r = Req("{\"operation\":\"place_view\",\"view_id\":510,\"sheet_id\":600,\"identity\":{\"view_id\":\"u510\",\"sheet_id\":{\"expected_category\":\"Views\"}}}");
            Assert.Null(Pre(r, "horizun_manage_views"));
            Assert.Equal(new[] { "sheet_id" }, IdentityBinding.WeakKeys.ToArray());
        }

        [Fact]
        public void M2_validate_ids_answers_coincide_categoria_for_a_category_match_and_all_match_needs_a_unique_id_on_every_row()
        {
            var checks = new JArray(
                JObject.Parse("{\"id\":7,\"expected_category\":\"OST_Walls\"}"),
                JObject.Parse("{\"id\":8,\"unique_id\":\"u8\"}"));
            JObject answer = ElementIdentityGuard.Validate(checks, Facts, ByUid);
            Assert.Equal("coincide_categoria", (string)answer["results"][0]["status"]);
            Assert.Equal("coincide", (string)answer["results"][1]["status"]);
            Assert.Equal(1, (int)answer["summary"]["coincide_categoria"]);
            Assert.Equal(1, (int)answer["summary"]["coincide"]);
            Assert.False((bool)answer["summary"]["all_match"]);
            Assert.Contains("unique_id", (string)answer["results"][0]["reason"]);

            var strong = new JArray(JObject.Parse("{\"id\":7,\"unique_id\":\"u7\",\"expected_category\":\"OST_Walls\"}"));
            Assert.True((bool)ElementIdentityGuard.Validate(strong, Facts, ByUid)["summary"]["all_match"]);
        }

        [Fact]
        public void M2_a_category_or_type_that_moved_is_still_a_change_not_a_category_match()
        {
            var checks = new JArray(JObject.Parse("{\"id\":7,\"expected_category\":\"OST_Views\"}"));
            Assert.Equal("cambio", (string)ElementIdentityGuard.Validate(checks, Facts, ByUid)["results"][0]["status"]);
        }

        // ------------------------------------------------------------------ m-2: the identity is part of what the token approves

        private const string Doc = "rvt:2026|guid:abc|path:x|title:tower";

        [Fact]
        public void M2_a_token_issued_for_a_rehearsal_with_identity_is_refused_for_an_apply_that_omits_or_changes_it()
        {
            var store = new ConfirmationStore();
            var rehearsal = Req("{\"element_id\":7,\"unique_id\":\"u7\"}");
            Assert.Null(Pre(rehearsal, "horizun_write_params_verified"));
            string hash = ConfirmationStore.PlanHash(rehearsal, "element_id");
            Confirmation token = store.Issue("horizun_write_params_verified", Doc, hash);
            Assert.Equal(IdentityBinding.Digest, token.IdentityDigest);

            // The apply omits the identity: the request hash is the same (identity fields are consumed), the token still refuses.
            IdentityBinding.Reset();
            ConfirmationCheck omitted = store.Validate(token.Token, "horizun_write_params_verified", Doc, hash);
            Assert.Equal(ConfirmationState.PlanChanged, omitted.State);
            Assert.Contains("omits it", omitted.Message);

            // A second token: the apply carries ANOTHER identity (a different expectation about the same ids).
            var first = Req("{\"element_id\":7,\"unique_id\":\"u7\"}");
            Pre(first, "horizun_write_params_verified");
            Confirmation token2 = store.Issue("horizun_write_params_verified", Doc, hash);
            var changed = Req("{\"element_id\":7,\"unique_id\":\"u7\",\"expected_category\":\"OST_Walls\"}");
            Pre(changed, "horizun_write_params_verified");
            Assert.Equal(ConfirmationState.PlanChanged, store.Validate(token2.Token, "horizun_write_params_verified", Doc, hash).State);

            // Resending the same identity is accepted.
            Pre(Req("{\"element_id\":7,\"unique_id\":\"u7\"}"), "horizun_write_params_verified");
            Confirmation token3 = store.Issue("horizun_write_params_verified", Doc, hash);
            Pre(Req("{\"element_id\":7,\"unique_id\":\"u7\"}"), "horizun_write_params_verified");
            Assert.True(store.Validate(token3.Token, "horizun_write_params_verified", Doc, hash).Ok);
        }

        [Fact]
        public void M2_a_token_issued_without_identity_is_refused_for_an_apply_that_adds_one_and_a_plain_token_still_works()
        {
            var store = new ConfirmationStore();
            string hash = ConfirmationStore.PlanHash(Req("{\"element_id\":7}"), "element_id");
            IdentityBinding.Reset();
            Confirmation plain = store.Issue("horizun_write_params_verified", Doc, hash);
            Assert.Null(plain.IdentityDigest);
            Assert.True(store.Validate(plain.Token, "horizun_write_params_verified", Doc, hash).Ok);   // nothing changed for a call without identity

            Confirmation plain2 = store.Issue("horizun_write_params_verified", Doc, hash);
            Pre(Req("{\"element_id\":7,\"unique_id\":\"u7\"}"), "horizun_write_params_verified");
            Assert.Equal(ConfirmationState.PlanChanged, store.Validate(plain2.Token, "horizun_write_params_verified", Doc, hash).State);
            IdentityBinding.Reset();
        }

        // ------------------------------------------------------------------ m-3: a misspelt identity field is refused

        [Theory]
        [InlineData("horizun_write_params_verified", "{\"element_id\":7,\"uniqueId\":\"u7\"}", "uniqueId", "unique_id")]
        [InlineData("horizun_write_params_verified", "{\"element_id\":7,\"UniqueId\":\"u7\"}", "UniqueId", "unique_id")]
        [InlineData("horizun_delete_verified", "{\"ids\":[7],\"unique-id\":\"u7\"}", "unique-id", "unique_id")]
        [InlineData("horizun_delete_verified", "{\"ids\":[7],\"uniqueIds\":[\"u7\"]}", "uniqueIds", "unique_ids")]
        [InlineData("horizun_set_keynote", "{\"element_id\":7,\"expectedCategory\":\"OST_Walls\"}", "expectedCategory", "expected_category")]
        [InlineData("horizun_set_keynote", "{\"element_id\":7,\"expectedType\":\"x\"}", "expectedType", "expected_type")]
        [InlineData("horizun_set_keynote", "{\"element_id\":7,\"Identity\":{\"element_id\":\"u7\"}}", "Identity", "identity")]
        [InlineData("horizun_write_params_verified", "{\"operations\":[{\"element_id\":7,\"uniqueId\":\"u7\"}]}", "uniqueId", "unique_id")]
        [InlineData("horizun_manage_views", "{\"operation\":\"place_view\",\"view_id\":510,\"identity\":{\"view_id\":{\"uniqueId\":\"u510\"}}}", "uniqueId", "unique_id")]
        public void M3_a_misspelt_identity_field_is_refused_naming_the_right_one(string tool, string json, string wrong, string right)
        {
            JObject error = Pre(Req(json), tool);
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            string detail = (string)error["detail"];
            Assert.Contains(wrong, detail);
            Assert.Contains("'" + right + "'", detail);
            Assert.False((bool)error["write_started"]);
        }

        [Fact]
        public void M3_inside_a_plan_only_the_children_the_guard_covers_are_read_for_misspellings()
        {
            var covered = Req("{\"actions\":[{\"tool\":\"horizun_set_keynote\",\"arguments\":{\"element_id\":7,\"uniqueId\":\"u7\"}}]}");
            JObject error = Pre(covered, "horizun_execute_plan");
            Assert.NotNull(error);
            Assert.Equal("actions[0].arguments.uniqueId", (string)error["path"]);
            Assert.Equal(0, (int)error["index"]);
            // A family parameter called like an identity word belongs to a tool the guard does not cover: its own business.
            var other = Req("{\"actions\":[{\"tool\":\"horizun_family_apply\",\"arguments\":{\"parameters\":{\"Identity\":\"x\"}}}]}");
            Assert.Null(Pre(other, "horizun_execute_plan"));
        }

        // ------------------------------------------------------------------ m-4: a read refuses an identity instead of ignoring it

        [Theory]
        [InlineData("horizun_capture_view", "{\"view_id\":510,\"unique_id\":\"u510\"}")]
        [InlineData("horizun_join_geometry", "{\"operation\":\"read\",\"element_ids\":[7],\"unique_ids\":[\"u7\"]}")]
        [InlineData("horizun_sheet_layout", "{\"operation\":\"get_viewport_box\",\"sheet_id\":600,\"unique_id\":\"u600\"}")]
        [InlineData("horizun_manage_groups", "{\"operation\":\"list\",\"element_ids\":[7],\"unique_ids\":[\"u7\"]}")]
        public void M4_a_call_that_writes_nothing_refuses_an_identity_and_points_to_validate_ids(string tool, string json)
        {
            var r = Req(json);
            JObject error = Pre(r, tool);
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("does not write", (string)error["detail"]);
            Assert.Contains("validate_ids", (string)error["detail"]);
            Assert.False((bool)error["write_started"]);
        }

        [Theory]
        [InlineData("horizun_capture_view", "{\"view_id\":510,\"element_ids\":[7],\"unique_ids\":[\"u7\"]}")]
        [InlineData("horizun_join_geometry", "{\"operation\":\"join\",\"element_ids\":[7,8],\"unique_ids\":[\"u7\",\"u8\"]}")]
        [InlineData("horizun_sheet_layout", "{\"operation\":\"auto_layout\",\"sheet_id\":600,\"unique_id\":\"u600\"}")]
        public void M4_the_writing_operations_of_the_same_tools_still_verify(string tool, string json)
        {
            Assert.Null(Pre(Req(json), tool));
            Assert.True(IdentityBinding.Carried);
        }

        [Fact]
        public void M4_a_read_without_identity_is_untouched()
        {
            var r = Req("{\"operation\":\"network_census\",\"element_ids\":[7]}");
            Assert.False(IdentityBinding.Carried);
            Assert.Equal(7L, (long)r["element_ids"][0]);
        }

        // ------------------------------------------------------------------ m-1: an `identity` text is own only where the tool defines it

        [Fact]
        public void M1_an_identity_text_is_the_tools_own_only_at_the_exact_path_it_defines()
        {
            var signed = Req("{\"format\":\"pdf\",\"view_ids\":[510],\"require_gate\":{\"profile\":{},\"override\":{\"identity\":\"Jane Doe\",\"reason\":\"r\"}}}");
            Assert.Null(Pre(signed, "horizun_export"));
            Assert.Equal("Jane Doe", (string)signed["require_gate"]["override"]["identity"]);
            Assert.False(IdentityBinding.Carried);

            // The same word anywhere else in that tool is an identity map that is not one: refused, not skipped.
            var top = Req("{\"format\":\"pdf\",\"view_ids\":[510],\"identity\":\"Jane Doe\"}");
            JObject error = Pre(top, "horizun_export");
            Assert.NotNull(error);
            Assert.Contains("identity must be an object", (string)error["detail"]);
            var elsewhere = Req("{\"format\":\"pdf\",\"view_ids\":[510],\"require_gate\":{\"identity\":\"Jane Doe\"}}");
            Assert.NotNull(Pre(elsewhere, "horizun_export"));
        }

        // ------------------------------------------------------------------ m-5: ids inside arrays of arrays

        [Fact]
        public void M5_join_geometry_pairs_are_checked_with_keys_like_pairs_0_1()
        {
            var ok = Req("{\"operation\":\"unjoin\",\"pairs\":[[7,8],[9,10]],\"identity\":{\"pairs[0][0]\":\"u7\",\"pairs[1][1]\":\"u10\"}}");
            Assert.Null(Pre(ok, "horizun_join_geometry"));
            Assert.Equal(10L, (long)ok["pairs"][1][1]);
            var bad = Req("{\"operation\":\"unjoin\",\"pairs\":[[7,8],[9,10]],\"identity\":{\"pairs[1][0]\":\"u7\"}}");
            JObject error = Pre(bad, "horizun_join_geometry");
            Assert.Equal("pairs[1][0]", (string)error["key"]);
            var missing = Req("{\"operation\":\"unjoin\",\"pairs\":[[7,8]],\"identity\":{\"pairs[3][0]\":\"u7\"}}");
            Assert.Contains("has no 'pairs[3][0]'", (string)Pre(missing, "horizun_join_geometry")["detail"]);
        }

        // ------------------------------------------------------------------ merge with int/4.2 (coordinator notes)

        [Fact]
        public void Merge_ids_of_a_node_with_link_instance_id_belong_to_the_linked_document_and_are_not_looked_up_in_the_host()
        {
            var linked = Req("{\"operation\":\"notes\",\"link_instance_id\":7,\"element_ids\":[8],\"identity\":{\"element_ids\":[\"u8\"]}}");
            JObject error = Pre(linked, "horizun_planimetry_ops");
            Assert.NotNull(error);
            Assert.Contains("LINKED document", (string)error["detail"]);
            var instanceOnly = Req("{\"operation\":\"notes\",\"link_instance_id\":7,\"element_ids\":[8],\"identity\":{\"link_instance_id\":\"u7\"}}");
            Assert.Null(Pre(instanceOnly, "horizun_planimetry_ops"));
        }

        [Fact]
        public void Merge_view_visibility_and_manage_worksets_check_element_ids_and_view_ids()
        {
            foreach (string tool in new[] { "horizun_view_visibility", "horizun_manage_worksets" })
            {
                var ok = Req("{\"operation\":\"hide\",\"view_ids\":[510],\"element_ids\":[7],\"identity\":{\"view_ids\":[\"u510\"],\"element_ids\":[\"u7\"]}}");
                Assert.Null(Pre(ok, tool));
                var bad = Req("{\"operation\":\"hide\",\"view_ids\":[510],\"element_ids\":[7],\"identity\":{\"view_ids\":[\"u511\"]}}");
                Assert.Equal("view_ids", (string)Pre(bad, tool)["key"]);
            }
        }

        [Fact]
        public void Merge_the_identity_digest_is_part_of_every_plan_hash()
        {
            var r = Req("{\"element_id\":7}");
            IdentityBinding.Reset();
            string without = ConfirmationStore.PlanHash(r, "element_id");
            Pre(Req("{\"element_id\":7,\"unique_id\":\"u7\"}"), "horizun_write_params_verified");
            string with = ConfirmationStore.PlanHash(r, "element_id");
            Pre(Req("{\"element_id\":7,\"unique_id\":\"u7\",\"expected_type\":\"Generic - 200mm\"}"), "horizun_write_params_verified");
            string other = ConfirmationStore.PlanHash(r, "element_id");
            IdentityBinding.Reset();
            Assert.NotEqual(without, with);
            Assert.NotEqual(with, other);
        }

        // ------------------------------------------------------------------ M-1: the sweep compares the commandName that reaches ForMutation

        private static string RepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        // Every name a command passes to DocumentGate.ForMutation, resolved from the source of the call site.
        private static SortedDictionary<string, string> ForMutationNames()
        {
            string commands = Path.Combine(RepoRoot(), "src", "Horizun.Revit", "Commands");
            var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var call = new Regex(@"ForMutation\(\s*\w+\s*,\s*\w+\s*,\s*(""[^""]+""|\w+)\s*\)");
            // partial classes: the file that defines Name may not be the one that calls ForMutation
            var nameOfClass = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(commands, "*.cs"))
            {
                string text = File.ReadAllText(file);
                Match cls = Regex.Match(text, @"partial class (\w+)");
                Match def = Regex.Match(text, @"(?:string\s+Name\s*=>|(?:Tool|ToolName)\s*=)\s*""(horizun_[a-z0-9_]+|get_document_info)""");
                if (cls.Success && def.Success) nameOfClass[cls.Groups[1].Value] = def.Groups[1].Value;
            }
            foreach (string file in Directory.GetFiles(commands, "*.cs"))
            {
                string text = File.ReadAllText(file);
                if (file.EndsWith("ModelEditRunner.cs", StringComparison.Ordinal) || file.EndsWith("RecipeCommand.cs", StringComparison.Ordinal))
                    continue;   // they pass the Name of the command that calls them (checked by the second sweep)
                foreach (Match m in call.Matches(text))
                {
                    string arg = m.Groups[1].Value;
                    var names = new List<string>();
                    if (arg.StartsWith("\"", StringComparison.Ordinal)) names.Add(arg.Trim('"'));
                    else
                    {
                        // Name / Tool / ToolName / name -> the literal the same file defines for it.
                        foreach (Match d in Regex.Matches(text, @"(?:string\s+Name\s*=>|\b(?:Tool|ToolName)\s*=)\s*""(horizun_[a-z0-9_]+|get_document_info)"""))
                            names.Add(d.Groups[1].Value);
                    }
                    if (names.Count == 0)
                    {
                        Match cls = Regex.Match(text, @"partial class (\w+)");
                        if (cls.Success && nameOfClass.TryGetValue(cls.Groups[1].Value, out string viaClass)) names.Add(viaClass);
                    }
                    if (names.Count == 0) found[Path.GetFileName(file) + " -> " + arg] = "";   // unresolved: listed so a test can decide
                    foreach (string n in names) found[n] = Path.GetFileName(file);
                }
            }
            return found;
        }

        private static bool HasIdArgument(JToken node)
        {
            if (node is JObject o)
                foreach (JProperty p in o.Properties())
                {
                    if (p.Name == "properties" && p.Value is JObject props)
                    {
                        foreach (JProperty arg in props.Properties())
                            if (ElementIdentityGuard.IsIdArgumentName(arg.Name) || HasIdArgument(arg.Value)) return true;
                    }
                    else if (HasIdArgument(p.Value)) return true;
                }
            else if (node is JArray a) foreach (JToken item in a) if (HasIdArgument(item)) return true;
            return false;
        }

        [Fact]
        public void M1_the_production_id_name_rule_recognizes_pairs_and_the_element_ids_a_b_family()
        {
            Assert.True(ElementIdentityGuard.IsIdArgumentName("pairs"));
            Assert.True(ElementIdentityGuard.IsIdArgumentName("element_ids_a"));
            Assert.True(ElementIdentityGuard.IsIdArgumentName("element_ids_b"));
            Assert.True(HasIdArgument(JObject.Parse("{\"properties\":{\"pairs\":{\"type\":\"array\"}}}")));
        }

        [Fact]
        public void M1_every_name_that_reaches_ForMutation_with_ids_is_a_contract_tool_that_is_covered_refused_or_justified()
        {
            SortedDictionary<string, string> names = ForMutationNames();
            Assert.Empty(names.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key));   // every call site resolves to a literal
            var problems = new List<string>();
            foreach (KeyValuePair<string, string> kv in names)
            {
                CommandContract c = Contract.Find(kv.Key);
                if (c == null) { problems.Add(kv.Key + " (" + kv.Value + ") is not a contract tool, so ForMutation would not recognise it"); continue; }
                if (c.InputSchema == null || !HasIdArgument(c.InputSchema)) continue;
                if (ElementIdentityGuard.Covers(kv.Key) || ElementIdentityGuard.Refuses(kv.Key) || kv.Key == "horizun_execute_python") continue;
                if (NotElementIds.ContainsKey(kv.Key)) continue;
                problems.Add(kv.Key + " (" + kv.Value + ") reaches ForMutation with id arguments and is neither covered, refused nor justified");
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void M1_every_covered_writer_reaches_the_gate_under_its_own_contract_name()
        {
            // A covered tool whose command passes another name to ForMutation would be refused by the very guard that covers it.
            SortedDictionary<string, string> names = ForMutationNames();
            var viaRunner = new HashSet<string>(StringComparer.Ordinal)
            {   // ModelEditRunner.Run(app, json, Name, ...) passes the command's own Name
                "horizun_create_railing", "horizun_join_geometry", "horizun_manage_curtain",
                "horizun_planimetry_ops", "horizun_room_finishes", "horizun_sheet_layout", "horizun_slab_shape",
                "horizun_view_crop", "horizun_view_visibility", "horizun_wall_finish", "horizun_wall_sweep"
            };
            var recipes = new HashSet<string>(StringComparer.Ordinal)
            {   // RecipeCommand passes the Name each recipe defines
                "horizun_split_floor_loops", "horizun_split_multilayer_slabs", "horizun_ungroup_and_mark", "horizun_copy_slab_elevations",
                "horizun_embed_floors_in_toposolid", "horizun_grade_toposolid_around_floors", "horizun_rectangularize_walls"
            };
            var missing = ElementIdentityGuard.CoveredTools
                .Where(t => !names.ContainsKey(t) && !viaRunner.Contains(t) && !recipes.Contains(t))
                .OrderBy(t => t, StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0, "covered tools no ForMutation call site names (so the sweep cannot vouch for them): " + string.Join(", ", missing));
        }

        // Writers whose id-like arguments are not existing elements of the ACTIVE document that the write acts on.
        private static readonly Dictionary<string, string> NotElementIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["horizun_save_document"] = "accept_findings_ids selects pending ledger findings, not elements to mutate; unmatched ids are refused",
            ["horizun_budget_compare"] = "Power BI dataset ids",
            ["horizun_power_bi_push"] = "Power BI dataset ids",
            ["horizun_cde_cloud"] = "cloud service ids",
            ["horizun_exchange"] = "records of an exchange manifest",
            ["horizun_information_container"] = "records of the information container",
            ["horizun_project_context"] = "requirement ids of project-context.json",
            ["horizun_request_python_access"] = "the access request",
            ["horizun_submit_job"] = "a job of the bridge",
            ["horizun_navigate"] = "moves the user's selection and writes nothing",
            ["horizun_verify_changes"] = "a read-back of the session's own writes",
        };
    }
}
