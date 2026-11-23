using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // INT-5: R2T (M-2 synchronize + weak identity, M-1 deleting tables, m-1 hash) applied to the tools new in 4.2.
    public class ElementIdentityGuardInt5Tests
    {
        // Every element is ours (Horizun mark): only the synchronize state can make a category-only identity unsafe.
        private static ElementIdentityFacts Facts(long id)
        {
            if (id < 1 || id > 999) return null;
            return new ElementIdentityFacts
            {
                Id = id, UniqueId = "u" + id, Owned = true,
                Category = "Walls", CategoryBuiltIn = "OST_Walls", Type = "Generic - 200mm", Family = "Basic Wall"
            };
        }

        private static ElementIdentityFacts ByUid(string uid) =>
            uid != null && uid.StartsWith("u", StringComparison.Ordinal) && long.TryParse(uid.Substring(1), out long id) ? Facts(id) : null;

        private static JObject Pre(string json, string tool, Func<bool> synced = null)
        {
            IdentityBinding.Reset();
            return ElementIdentityGuard.PreCheck(JObject.Parse(json), tool, Facts, ByUid, synced);
        }

        // (tool, a node of the 4.2 tool with a weak identity, the same with a unique_id)
        public static IEnumerable<object[]> Int42Nodes => new[]
        {
            new object[] { "horizun_sheet_layout",
                "{\"operation\":\"move_viewport\",\"viewport_id\":7,\"expected_category\":\"Walls\"}",
                "{\"operation\":\"move_viewport\",\"viewport_id\":7,\"unique_id\":\"u7\"}" },
            new object[] { "horizun_planimetry_ops",
                "{\"operation\":\"move_note\",\"note_ids\":[7,8],\"expected_category\":\"Walls\"}",
                "{\"operation\":\"move_note\",\"note_ids\":[7,8],\"unique_ids\":[\"u7\",\"u8\"]}" },
            new object[] { "horizun_planimetry_ops",
                "{\"operation\":\"clone_tag\",\"model_tag_id\":7,\"expected_category\":\"Walls\"}",
                "{\"operation\":\"clone_tag\",\"model_tag_id\":7,\"unique_id\":\"u7\"}" },
            new object[] { "horizun_manage_revisions",
                "{\"operation\":\"update_revision\",\"revision_id\":7,\"sheet_ids\":[8],\"expected_category\":\"Walls\"}",
                "{\"operation\":\"update_revision\",\"revision_id\":7,\"sheet_ids\":[8],\"identity\":{\"revision_id\":\"u7\",\"sheet_ids\":[\"u8\"]}}" },
            new object[] { "horizun_view_visibility",
                "{\"view_ids\":[7],\"element_ids\":[8],\"expected_category\":\"Walls\"}",
                "{\"view_ids\":[7],\"element_ids\":[8],\"unique_ids\":[\"u8\"]}" },
        };

        [Theory]
        [MemberData(nameof(Int42Nodes))]
        public void M2_after_a_synchronize_a_category_only_identity_on_a_4_2_tool_is_refused_and_a_unique_id_proceeds(string tool, string weak, string strong)
        {
            JObject error = Pre(weak, tool, () => true);
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("synchroniz", (string)error["detail"]);
            Assert.Contains("unique_id", (string)error["detail"]);
            Assert.Null(Pre(strong, tool, () => true));
            // Without a recent synchronize our own element proceeds with the warning, as before.
            Assert.Null(Pre(weak, tool, () => false));
            Assert.Equal("categoria", (string)IdentityBinding.Notice()["identity_strength"]);
            IdentityBinding.Reset();
        }

        [Fact]
        public void M1_a_node_with_remove_sheet_ids_is_a_deletion_through_the_table_and_the_delete_keys_move_the_hash()
        {
            Assert.True(ElementIdentityGuard.IsDeleting("horizun_manage_revisions", JObject.Parse("{\"remove_sheet_ids\":[8]}")));
            Assert.False(ElementIdentityGuard.IsDeleting("horizun_manage_revisions", JObject.Parse("{\"sheet_ids\":[8]}")));
            // The R2T deletions still work beside it.
            Assert.Contains("remove_sheet_ids", IdentityCoverage.Summary());
            Assert.True(((System.Collections.IList)IdentityCoverage.DeleteKeys).IsReadOnly);

            FieldInfo f = typeof(IdentityCoverage).GetField("DeleteKeysArray", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(f);
            string[] a = (string[])f.GetValue(null);
            string baseline = Hash();
            string saved = a[0];
            try
            {
                a[0] = "changed_by_test";
                Assert.NotEqual(baseline, Hash());
            }
            finally { a[0] = saved; }
            Assert.Equal(baseline, Hash());
        }

        private static string Hash() =>
            (string)typeof(Contract).GetMethod("ComputeHash", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);

        [Fact]
        public void m1_the_documented_keys_of_the_4_2_tools_include_what_the_guard_resolves_with()
        {
            var table = IdentityCoverage.DocumentedIdKeys.ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
            Assert.Contains("view_id", table["horizun_sheet_layout"].Split(';')[0].Split(':')[1].Split(','));
            Assert.Contains("viewport_ids", table["horizun_sheet_layout"].Split(';')[1].Split(':')[1].Split(','));
            string[] single = table["horizun_planimetry_ops"].Split(';')[0].Split(':')[1].Split(',');
            foreach (string key in new[] { "model_tag_id", "element_id", "text_note_type_id", "link_instance_id" }) Assert.Contains(key, single);
            Assert.Contains("note_ids", table["horizun_planimetry_ops"].Split(';')[1].Split(':')[1].Split(','));
        }
    }
}
