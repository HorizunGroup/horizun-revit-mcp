using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // INT-4: the deletion rule of m-6 (a category-only identity is refused where the call deletes) applied to the tools new in 4.2.
    public class ElementIdentityGuardInt42DeleteTests
    {
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

        private static JObject Pre(string json, string tool) { IdentityBinding.Reset(); return ElementIdentityGuard.PreCheck(JObject.Parse(json), tool, Facts, ByUid, () => false); }

        [Fact]
        public void A_revision_node_that_removes_sheets_is_a_deletion_whatever_its_operation_is_called()
        {
            JObject error = Pre("{\"operation\":\"update_revision\",\"revision_id\":7,\"remove_sheet_ids\":[8],\"expected_category\":\"Walls\"}", "horizun_manage_revisions");
            Assert.NotNull(error);
            Assert.Equal("entrada_invalida", (string)error["error_code"]);
            Assert.Contains("deletes", (string)error["detail"]);
            Assert.Null(Pre("{\"operation\":\"update_revision\",\"revision_id\":7,\"sheet_ids\":[8],\"expected_category\":\"Walls\"}", "horizun_manage_revisions"));
            Assert.Null(Pre("{\"operation\":\"update_revision\",\"revision_id\":7,\"remove_sheet_ids\":[8],\"identity\":{\"revision_id\":\"u7\",\"remove_sheet_ids\":[\"u8\"]}}", "horizun_manage_revisions"));
        }

        [Fact]
        public void The_delete_keys_scope_the_rule_to_the_ids_they_hold_and_every_one_is_an_id_key_of_a_covered_tool()
        {
            // R2 integration (INT-8): remove_sheet_ids takes sheets out of a revision. The rule applies to those ids; the revision stays.
            Assert.NotNull(Pre("{\"operation\":\"update_revision\",\"revision_id\":7,\"remove_sheet_ids\":[8],\"identity\":{\"remove_sheet_ids\":{\"expected_category\":\"Walls\"}}}", "horizun_manage_revisions"));
            Assert.Null(Pre("{\"operation\":\"update_revision\",\"revision_id\":7,\"remove_sheet_ids\":[8],\"identity\":{\"revision_id\":{\"expected_category\":\"Walls\"},\"remove_sheet_ids\":[\"u8\"]}}", "horizun_manage_revisions"));
            foreach (string key in IdentityCoverage.DeleteKeys)
            {
                var carriers = IdentityCoverage.DocumentedIdKeys.Where(kv => kv.Value.Split(';').Any(part => part.Substring(part.IndexOf(':') + 1).Split(',').Contains(key))).ToList();
                Assert.True(carriers.Count > 0, key + " is a delete key and no covered tool lists it as an id key");
                foreach (var kv in carriers)
                    Assert.True(ElementIdentityGuard.IsDeleting(kv.Key, new JObject { [key] = new JArray(8) }), kv.Key + " " + key);
            }
        }

        [Fact]
        public void An_ifc_setup_object_keyed_by_exporter_options_is_data_not_an_identity_field()
        {
        }

        [Fact]
        public void The_nodes_of_planimetry_ops_and_sheet_layout_delete_nothing_the_caller_names()
        {
            // legend_from_families deletes inside the legend it has just duplicated, pack_sheets deletes its own provisional placement:
            // neither deletes an element the caller identified, so a category-only identity is judged by ownership alone (these are Owned).
            Assert.Null(Pre("{\"operation\":\"move_note\",\"note_ids\":[7],\"expected_category\":\"Walls\"}", "horizun_planimetry_ops"));
            Assert.Null(Pre("{\"operation\":\"move_viewport\",\"viewport_id\":7,\"expected_category\":\"Walls\"}", "horizun_sheet_layout"));
        }

        [Fact]
        public void Every_operation_of_a_covered_tool_named_like_a_deletion_is_judged_as_one()
        {
            var misses = new List<string>();
            // The operations a tool lists as NotDeletingOperations (R2T: a "remove" that detaches or un-binds without deleting the element
            // it names) are judged too, as not deleting; the opposite direction is asserted below.
            var notDeleting = IdentityCoverage.NotDeletingOperations.SelectMany(r => r.Value.Select(o => r.Key + " " + o)).ToHashSet();
            foreach (CommandContract c in Contract.All.Where(c => IdentityCoverage.VerifiedTools.Contains(c.Name)))
                foreach (string op in Enums(c.InputSchema))
                    if (IdentityCoverage.DeleteWords.Any(w => op.ToLowerInvariant().Split('_').Contains(w)))
                    {
                        var node = new JObject { ["operation"] = op };
                        bool deleting = ElementIdentityGuard.IsDeleting(c.Name, node);
                        if (notDeleting.Contains(c.Name + " " + op)) { if (deleting) misses.Add(c.Name + " " + op + " is listed as not deleting and is judged a deletion"); }
                        else if (!deleting) misses.Add(c.Name + " " + op);
                    }
            Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));
        }

        [Fact]
        public void Every_operation_that_says_it_removes_something_in_a_4_2_tool_is_either_judged_a_deletion_or_justified()
        {
            // The enum values of the tools new in 4.2 that mean "take away" without using one of the delete words.
            string[] tools = { "horizun_planimetry_ops", "horizun_sheet_layout", "horizun_room_finishes",
                               "horizun_pack_sheets", "horizun_manage_revisions", "horizun_manage_schedules", "horizun_structural_connections",
                               "horizun_manage_materials", "horizun_link_schedule" };
            string[] takeAway = { "clear", "detach", "drop", "unplace", "unlink", "discard", "reset", "prune", "replace", "overwrite" };
            var found = new List<string>();
            foreach (string t in tools)
            {
                CommandContract c = Contract.Find(t);
                Assert.NotNull(c);
                foreach (string op in Enums(c.InputSchema))
                    if (takeAway.Any(w => op.ToLowerInvariant().Split('_').Contains(w))) found.Add(t + " " + op);
            }
            // replace_text rewrites text in place; ChangeTypeId may replace the fixture.
            // replace_family occurs in the operation enum AND the conditional enum
            // requiring new_type_id. Assert both occurrences; do not hide duplicates.
        }

        private static IEnumerable<string> Enums(JToken schema)
        {
            var result = new List<string>();
            void Walk(JToken t, string name)
            {
                if (t is JObject o)
                {
                    if ((name == "operation" || name == "action" || name == "mode" || name == "decision") && o["enum"] is JArray e)
                        result.AddRange(e.Where(x => x.Type == JTokenType.String).Select(x => (string)x));
                    foreach (JProperty p in o.Properties()) Walk(p.Value, p.Name);
                }
                else if (t is JArray a) foreach (JToken x in a) Walk(x, name);
            }
            Walk(schema, "");
            return result;
        }
    }
}
