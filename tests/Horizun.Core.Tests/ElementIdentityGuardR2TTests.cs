using System;
using System.Collections;
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
    // (R2U moved the synchronize / copied-mark rules and the delete sweep to ElementIdentityGuardR2UTests: the collaborative rule, the user in the mark, the ledger by UniqueId, one classification per call.)
    // R2T - the findings of the independent review of R2S: M-1 (deletions the rule missed), M-2 (a synchronize makes the weak identity
    // unsafe on our own elements; a copied mark is not ours), m-1 (the whole coverage in the hash, read-only tables), m-2 (identity outside
    // the executed arguments), m-3 (the operation of the ancestors), m-5 (weak tests made real).
    public class ElementIdentityGuardR2TTests
    {
        // 1..99 are ours (Horizun mark); 100..199 carry no mark. Every element N has the unique_id "uN"; 500 and up are views.
        private static ElementIdentityFacts Facts(long id)
        {
            if (id < 1 || id > 999) return null;
            bool view = id >= 500;
            return new ElementIdentityFacts
            {
                Id = id, UniqueId = "u" + id, Owned = !(id >= 100 && id < 200),
                Category = view ? "Views" : "Walls", CategoryBuiltIn = view ? "OST_Views" : "OST_Walls",
                Type = view ? "Floor Plan" : "Generic - 200mm", Family = view ? "Floor Plan" : "Basic Wall"
            };
        }

        private static ElementIdentityFacts ByUid(string uid) =>
            uid != null && uid.StartsWith("u", StringComparison.Ordinal) && long.TryParse(uid.Substring(1), out long id) ? Facts(id) : null;

        private static JObject Req(string json) => JObject.Parse(json);
        private static JObject Pre(JObject r, string tool, Func<bool> collaborative = null) { IdentityBinding.Reset(); return ElementIdentityGuard.PreCheck(r, tool, Facts, ByUid, collaborative ?? (() => false)); }

        private static string RepoRoot()
        {
            DirectoryInfo d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        // ------------------------------------------------------------------ M-1: the deleting operations that carry no deletion word

        // (tool, request that deletes by category only, the same with a unique_id, a sibling operation of the tool that does not delete)
        public static IEnumerable<object[]> NewDeletions => new[]
        {
            new object[] { "horizun_manage_assemblies_parts",
                "{\"operation\":\"dissolve_parts\",\"element_ids\":[7],\"expected_category\":\"Walls\"}",
                "{\"operation\":\"dissolve_parts\",\"element_ids\":[7],\"unique_ids\":[\"u7\"]}",
                "{\"operation\":\"create_parts\",\"element_ids\":[7],\"expected_category\":\"Walls\"}" },
            new object[] { "horizun_manage_assemblies_parts",
                "{\"operation\":\"disassemble\",\"assembly_id\":7,\"identity\":{\"assembly_id\":{\"expected_category\":\"Walls\"}}}",
                "{\"operation\":\"disassemble\",\"assembly_id\":7,\"identity\":{\"assembly_id\":\"u7\"}}",
                "{\"operation\":\"assembly_views\",\"assembly_id\":7,\"identity\":{\"assembly_id\":{\"expected_category\":\"Walls\"}}}" },
            new object[] { "horizun_manage_groups",
                "{\"operation\":\"add_members\",\"group_ids\":[7],\"expected_category\":\"Walls\"}",
                "{\"operation\":\"add_members\",\"group_ids\":[7],\"unique_ids\":[\"u7\"]}",
                "{\"operation\":\"rename_type\",\"group_ids\":[7],\"expected_category\":\"Walls\"}" },
            new object[] { "horizun_manage_groups",
                "{\"operation\":\"edit_type_members\",\"type_id\":7,\"expected_category\":\"Walls\"}",
                "{\"operation\":\"edit_type_members\",\"type_id\":7,\"unique_id\":\"u7\"}",
                "{\"operation\":\"duplicate_type\",\"type_id\":7,\"expected_category\":\"Walls\"}" },
            // The curtain method replaces the wall it is given when the curtain covers it whole (FramingCurtain.cs: the carrier is deleted).
            new object[] { "horizun_framing",
                "{\"operation\":\"wall\",\"element_ids\":[7],\"expected_category\":\"Walls\",\"spec\":{\"wall\":{\"method\":\"curtain\"}}}",
                "{\"operation\":\"wall\",\"element_ids\":[7],\"unique_ids\":[\"u7\"],\"spec\":{\"wall\":{\"method\":\"curtain\"}}}",
                "{\"operation\":\"wall\",\"element_ids\":[7],\"expected_category\":\"Walls\",\"spec\":{\"wall\":{\"method\":\"members\"}}}" },
        };

        [Theory]
        [MemberData(nameof(NewDeletions))]
        public void M1_a_deletion_without_a_deletion_word_is_refused_on_a_category_only_identity_and_its_sibling_proceeds(string tool, string deleting, string strong, string sibling)
        {
            JObject r = Req(deleting);
            Assert.True(ElementIdentityGuard.IsDeleting(tool, r));
            JObject error = Pre(r, tool);
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("deletes", (string)error["detail"]);
            Assert.Contains("unique_id", (string)error["detail"]);
            // With the unique_id the deletion proceeds (identity is the point, not a ban).
            Assert.Null(Pre(Req(strong), tool));
            IdentityBinding.Reset();
            // The sibling operation of the same tool is not a deletion: our own element proceeds with the category-only warning.
            JObject s = Req(sibling);
            Assert.False(ElementIdentityGuard.IsDeleting(tool, s));
            Assert.Null(Pre(s, tool));
            Assert.Equal("categoria", (string)IdentityBinding.Notice()["identity_strength"]);
            IdentityBinding.Reset();
        }

        [Fact]
        public void M1_the_table_and_the_conditions_are_in_the_hash_summary()
        {
            string summary = IdentityCoverage.Summary();
            Assert.Contains("deleting_operations=", summary);
            Assert.Contains("deleting_conditions=", summary);
            Assert.Contains("spec.wall.method=curtain", summary);
        }

        // ------------------------------------------------------------------ m-3: the operation of an ancestor

        [Fact]
        public void M3_the_operation_of_an_ancestor_node_makes_the_identity_of_its_children_a_deletion()
        {
            // The operation sits on the request, the ids on a child: the child inherits "deletes".
            var r = Req("{\"operation\":\"remove_grid_line\",\"targets\":[{\"element_id\":7,\"expected_category\":\"Walls\"}]}");
            JObject error = Pre(r, "horizun_manage_curtain");
            Assert.NotNull(error);
            Assert.Contains("deletes", (string)error["detail"]);
            Assert.Contains("targets[0]", (string)error["path"]);
            var keep = Req("{\"operation\":\"set_panel_type\",\"targets\":[{\"element_id\":7,\"expected_category\":\"Walls\"}]}");
            Assert.Null(Pre(keep, "horizun_manage_curtain"));
            IdentityBinding.Reset();
            var viaTable = Req("{\"operation\":\"level_steps\",\"targets\":[{\"element_id\":7,\"expected_category\":\"Walls\"}]}");
        }

        // ------------------------------------------------------------------ m-1: the whole coverage is in the hash and the tables are read-only

        private static string RecomputeHash() =>
            (string)typeof(Contract).GetMethod("ComputeHash", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);

        [Fact]
        public void m1_the_accepted_names_and_the_documented_keys_of_every_covered_tool_are_in_the_summary()
        {
            string summary = IdentityCoverage.Summary();
            Assert.Contains("accepted=", summary);
            foreach (string name in IdentityCoverage.AcceptedNames) Assert.Contains(name, summary);
            Assert.Contains("keys=", summary);
            foreach (string tool in ElementIdentityGuard.CoveredTools) Assert.Contains(tool + "=", summary);
        }

        [Fact]
        public void m1_the_documented_keys_table_is_the_one_the_guard_resolves_with()
        {
            var table = IdentityCoverage.DocumentedIdKeys.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (string tool in ElementIdentityGuard.CoveredTools)
            {
                string expected = "s:" + string.Join(",", ElementIdentityGuard.DocumentedKeys(tool, false)) + ";l:" + string.Join(",", ElementIdentityGuard.DocumentedKeys(tool, true));
                if (!table.TryGetValue(tool, out string actual)) problems.Add(tool + ": missing (expected " + expected + ")");
                else if (actual != expected) problems.Add(tool + ": " + actual + " != " + expected);
            }
            foreach (string tool in table.Keys) if (!ElementIdentityGuard.Covers(tool)) problems.Add(tool + ": not a covered tool");
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void m1_the_tables_are_read_only_views_not_arrays()
        {
            foreach (PropertyInfo p in typeof(IdentityCoverage).GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                object value = p.GetValue(null);
                Assert.False(value is Array, p.Name + " exposes an array that a caller can modify");
                var list = Assert.IsAssignableFrom<IList>(value);
                Assert.True(list.IsReadOnly, p.Name + " is not read-only");
            }
        }

        // ------------------------------------------------------------------ m-2: an identity outside the arguments that run

        [Fact]
        public void m2_an_identity_beside_the_correction_plan_is_refused_naming_its_path()
        {
            JObject r = Req("{\"target_document\":\"d\",\"correction_plan\":{\"pasos\":[]},\"manual\":[{\"element_id\":7,\"unique_id\":\"u7\"}]}");
            JObject error = Pre(r, "horizun_apply_corrections");
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("manual[0]", (string)error["path"]);
            Assert.Contains("ignored", (string)error["detail"]);
        }

        [Fact]
        public void m2_an_identity_inside_the_correction_plan_but_outside_a_step_argumentos_is_refused()
        {
            JObject r = Req("{\"target_document\":\"d\",\"correction_plan\":{\"nota\":{\"unique_id\":\"u7\"},\"pasos\":[]}}");
            JObject error = Pre(r, "horizun_apply_corrections");
            Assert.NotNull(error);
            Assert.Contains("correction_plan.nota", (string)error["path"]);
            JObject step = Req("{\"target_document\":\"d\",\"correction_plan\":{\"pasos\":[{\"id\":\"s\",\"herramienta\":\"horizun_manage_schedules\",\"argumentos\":{\"schedule_id\":7},\"evidencia\":[{\"expected_type\":\"x\"}]}]}}");
            JObject e2 = Pre(step, "horizun_apply_corrections");
            Assert.NotNull(e2);
            Assert.Contains("correction_plan.pasos[0].evidencia[0]", (string)e2["path"]);
        }

        [Fact]
        public void m2_an_identity_beside_the_arguments_of_a_plan_action_is_refused_at_any_depth()
        {
            JObject r = Req("{\"actions\":[{\"tool\":\"horizun_write_params_verified\",\"arguments\":{\"target_id\":7,\"unique_id\":\"u7\"},\"notes\":[{\"unique_id\":\"u8\"}]}]}");
            JObject error = Pre(r, "horizun_execute_plan");
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("actions[0].notes[0]", (string)error["path"]);
            JObject top = Req("{\"meta\":{\"identity\":{\"element_id\":\"u7\"}},\"actions\":[]}");
            Assert.Contains("meta", (string)Pre(top, "horizun_execute_plan")["path"]);
        }

        [Fact]
        public void m2_the_executed_arguments_are_still_verified_and_consumed()
        {
            JObject ok = Req("{\"actions\":[{\"tool\":\"horizun_write_params_verified\",\"arguments\":{\"target_id\":7,\"unique_id\":\"u7\"}}]}");
            Assert.Null(Pre(ok, "horizun_execute_plan"));
            Assert.Null(ok["actions"][0]["arguments"]["unique_id"]);
            IdentityBinding.Reset();
        }

        // ------------------------------------------------------------------ m-5: the weak-identity rule inside a plan child and a correction step

        [Fact]
        public void m5_a_category_only_identity_on_somebody_elses_element_is_refused_inside_a_plan_child()
        {
            JObject r = Req("{\"actions\":[{\"tool\":\"horizun_write_params_verified\",\"arguments\":{\"target_id\":150,\"expected_category\":\"Walls\"}}]}");
            JObject error = Pre(r, "horizun_execute_plan");
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("ownership mark", (string)error["detail"]);
            Assert.Contains("actions[0].arguments", (string)error["path"]);
            JObject del = Req("{\"actions\":[{\"tool\":\"horizun_delete_verified\",\"arguments\":{\"ids\":[7],\"expected_category\":\"Walls\"}}]}");
            Assert.Contains("deletes", (string)Pre(del, "horizun_execute_plan")["detail"]);
            JObject own = Req("{\"actions\":[{\"tool\":\"horizun_write_params_verified\",\"arguments\":{\"target_id\":7,\"expected_category\":\"Walls\"}}]}");
            Assert.Null(Pre(own, "horizun_execute_plan"));
            Assert.Equal("categoria", (string)IdentityBinding.Notice()["identity_strength"]);
            IdentityBinding.Reset();
        }

        [Fact]
        public void m5_a_category_only_identity_on_somebody_elses_element_is_refused_inside_a_correction_step()
        {
            JObject r = Req("{\"target_document\":\"d\",\"correction_plan\":{\"pasos\":[{\"id\":\"s1\",\"herramienta\":\"horizun_write_params_verified\",\"argumentos\":{\"target_id\":150,\"expected_category\":\"Walls\"}}]}}");
            JObject error = Pre(r, "horizun_apply_corrections");
            Assert.NotNull(error);
            Assert.Contains("ownership mark", (string)error["detail"]);
            Assert.Contains("correction_plan.pasos[0].argumentos", (string)error["path"]);
            JObject del = Req("{\"target_document\":\"d\",\"correction_plan\":{\"pasos\":[{\"id\":\"s1\",\"herramienta\":\"horizun_delete_verified\",\"argumentos\":{\"ids\":[7],\"expected_category\":\"Walls\"}}]}}");
            Assert.Contains("deletes", (string)Pre(del, "horizun_apply_corrections")["detail"]);
        }

        [Fact]
        public void m1_every_table_moves_the_hash()
        {
            string baseline = RecomputeHash();
            Assert.Equal(Contract.Hash, baseline);
            var problems = new List<string>();
            foreach (FieldInfo f in typeof(IdentityCoverage).GetFields(BindingFlags.NonPublic | BindingFlags.Static).Where(x => x.FieldType.IsArray))
            {
                Array a = (Array)f.GetValue(null);
                // Community edition: no tool takes ids of another document, so NoSubjectArray is empty; its name still enters the summary.
                if (a.Length == 0) { if (f.Name != "NoSubjectArray") problems.Add(f.Name + " is empty"); continue; }
                object saved = a.GetValue(0);
                // The public views wrap these arrays (or, for the {tool: operations[]} tables, the inner operations[]), so a change is visible
                // to Summary() through them; mutate the element the view really reads.
                if (saved is KeyValuePair<string, string[]> kv)
                {
                    string old = kv.Value[0];
                    try { kv.Value[0] = "changed_by_test"; if (RecomputeHash() == baseline) problems.Add(f.Name + " does not move the hash"); }
                    finally { kv.Value[0] = old; }
                    continue;
                }
                try
                {
                    object changed;
                    if (saved is string) changed = "changed_by_test";
                    else if (saved is KeyValuePair<string, string> ks) changed = new KeyValuePair<string, string>(ks.Key, "changed_by_test");
                    else { problems.Add(f.Name + ": unexpected element type " + saved.GetType()); continue; }
                    a.SetValue(changed, 0);
                    if (RecomputeHash() == baseline) problems.Add(f.Name + " does not move the hash");
                }
                finally { a.SetValue(saved, 0); }
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
            Assert.Equal(baseline, RecomputeHash());
        }
    }
}
