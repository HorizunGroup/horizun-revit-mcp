// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// WHICH FINDING IS THIS. The property that matters: two audits of an unchanged
// model at the same top reproduce every finding id and the set fingerprint;
// a different element, a different top, or a different document does not.
// And the prose beside the ids - a localized description, a triage label - is
// NOT part of the identity, or a session in another language would report
// that the model moved.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FindingIdentityTests
    {
        private static JArray Items(params long[] ids)
        {
            var a = new JArray();
            foreach (long id in ids)
                a.Add(new JObject { ["element_id"] = id.ToString(), ["name"] = "Link " + id });
            return a;
        }

        [Fact]
        public void The_same_finding_reproduces_the_same_id()
        {
            string a = FindingIdentity.IdOf("unpinned_links", Items(1, 2, 3), 20, 3);
            string b = FindingIdentity.IdOf("unpinned_links", Items(1, 2, 3), 20, 3);
            Assert.Equal(a, b);
            Assert.StartsWith(FindingIdentity.FindingPrefix, a);
        }

        [Fact]
        public void Item_order_is_not_part_of_the_identity_but_the_elements_are()
        {
            // Revit may enumerate a collector differently between two calls.
            Assert.Equal(FindingIdentity.IdOf("x", Items(1, 2, 3), 20, 3),
                         FindingIdentity.IdOf("x", Items(3, 1, 2), 20, 3));
            // A different element IS a different finding.
            Assert.NotEqual(FindingIdentity.IdOf("x", Items(1, 2, 3), 20, 3),
                            FindingIdentity.IdOf("x", Items(1, 2, 4), 20, 3));
        }

        [Fact]
        public void Top_and_check_and_total_are_folded_in()
        {
            string baseline = FindingIdentity.IdOf("x", Items(1), 20, 1);
            Assert.NotEqual(baseline, FindingIdentity.IdOf("x", Items(1), 50, 1));
            Assert.NotEqual(baseline, FindingIdentity.IdOf("y", Items(1), 20, 1));
            // Same shown items, more in the model: a different scope.
            Assert.NotEqual(baseline, FindingIdentity.IdOf("x", Items(1), 20, 7));
        }

        [Fact]
        public void Prose_beside_the_ids_is_not_identity()
        {
            var spanish = new JArray(new JObject
            {
                ["element_id"] = "5", ["description"] = "Los muros se solapan", ["severity"] = "Warning",
                ["label"] = "triaged by a profile"
            });
            var english = new JArray(new JObject
            {
                ["element_id"] = "5", ["description"] = "Walls overlap", ["severity"] = "Error",
                ["label"] = "untriaged"
            });
            Assert.Equal(FindingIdentity.IdOf("warnings", spanish, 20, 1),
                         FindingIdentity.IdOf("warnings", english, 20, 1));
        }

        [Fact]
        public void Identity_keys_are_id_shaped_plus_the_typed_codes_findings_use_without_ids()
        {
            foreach (string yes in new[] { "id", "element_id", "group_type_id", "failing_element_ids", "first_id",
                                           "role", "code", "family", "status", "problem_code" })
                Assert.True(FindingIdentity.IsIdentityKey(yes), yes);
            foreach (string no in new[] { "description", "summary", "problem", "name", "why", "label", "count" })
                Assert.False(FindingIdentity.IsIdentityKey(no), no);
        }

        [Fact]
        public void The_set_fingerprint_folds_document_top_and_every_finding_id_order_free()
        {
            string a = FindingIdentity.SetFingerprint("doc-1", 20, new[] { "f:a", "f:b" });
            Assert.Equal(a, FindingIdentity.SetFingerprint("doc-1", 20, new[] { "f:b", "f:a" }));
            Assert.NotEqual(a, FindingIdentity.SetFingerprint("doc-2", 20, new[] { "f:a", "f:b" }));
            Assert.NotEqual(a, FindingIdentity.SetFingerprint("doc-1", 50, new[] { "f:a", "f:b" }));
            Assert.NotEqual(a, FindingIdentity.SetFingerprint("doc-1", 20, new[] { "f:a" }));
            Assert.StartsWith(FindingIdentity.SetPrefix, a);
        }

        [Fact]
        public void Element_ids_are_read_from_the_three_keys_the_audit_uses_as_strings_or_integers()
        {
            var items = new JArray(
                new JObject { ["element_id"] = "11" },
                new JObject { ["id"] = 12 },
                new JObject { ["group_type_id"] = "13" },
                new JObject { ["example_id"] = "14" },          // a family's example: not a correction target
                new JObject { ["element_id"] = "not-a-number" });
            List<long> ids = FindingIdentity.ElementIdsOf(items);
            Assert.Equal(new long[] { 11, 12, 13 }, ids.ToArray());
        }

        [Fact]
        public void A_typed_filter_keeps_only_the_items_whose_code_is_accepted()
        {
            var items = new JArray(
                new JObject { ["id"] = "1", ["problem_code"] = "unplaced", ["problem"] = "unplaced (no location)" },
                new JObject { ["id"] = "2", ["problem_code"] = "not_enclosed", ["problem"] = "unplaced-looking sentence" },
                new JObject { ["id"] = "3" });
            JArray kept = FindingIdentity.ItemsWhere(items, "problem_code", new[] { "unplaced" });
            Assert.Single(kept);
            Assert.Equal("1", (string)kept[0]["id"]);
            // No filter: everything.
            Assert.Equal(3, FindingIdentity.ItemsWhere(items, null, null).Count);
        }
    }
}
