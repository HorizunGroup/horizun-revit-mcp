// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Federation and foreign content, proved by running the rules. The cycle
// detection is the part worth the most: A links B links A stays invisible
// until an ordinary open takes ten minutes, and nothing else in a model audit
// looks for it.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FederationContentTests
    {
        private static Dictionary<string, List<string>> G(params string[] edges)
        {
            // "A>B" means A loads B.
            var g = new Dictionary<string, List<string>>();
            foreach (string e in edges)
            {
                string[] parts = e.Split('>');
                if (!g.ContainsKey(parts[0])) g[parts[0]] = new List<string>();
                if (!g.ContainsKey(parts[1])) g[parts[1]] = new List<string>();
                g[parts[0]].Add(parts[1]);
            }
            return g;
        }

        private static ExternalPathFact P(string kind, bool? resolves) =>
            new ExternalPathFact { Kind = kind, ElementId = 1, Name = "x", Resolves = resolves };

        // -------------------------------------------------------- cycles

        [Fact]
        public void A_link_that_loads_a_link_that_loads_it_back_is_found()
        {
            List<List<string>> cycles = FederationContentRules.CircularReferences(G("A>B", "B>A"));
            List<string> one = Assert.Single(cycles);
            Assert.Equal(new[] { "A", "B" }, one.ToArray());
        }

        [Fact]
        public void A_longer_loop_is_found_and_reported_once()
        {
            List<List<string>> cycles = FederationContentRules.CircularReferences(G("A>B", "B>C", "C>A"));
            List<string> one = Assert.Single(cycles);
            Assert.Equal(new[] { "A", "B", "C" }, one.ToArray());
        }

        [Fact]
        public void A_cycle_is_reported_the_same_way_whichever_link_is_walked_first()
        {
            // Two runs of one model must produce the same list rather than a
            // rotation of it, or a snapshot diff reports a loop as new every time.
            List<List<string>> a = FederationContentRules.CircularReferences(G("B>C", "C>B"));
            List<List<string>> b = FederationContentRules.CircularReferences(G("C>B", "B>C"));
            Assert.Equal(a[0].ToArray(), b[0].ToArray());
            Assert.Equal("B", a[0][0]);

            // AND the case that makes the rotation load-bearing: the walk reaches
            // the loop through A, so it MEETS the cycle at C. Reported as walked it
            // would read C>B; canonicalised it reads B>C, which is what the other
            // graph produces. Without this, the two runs disagree about the same
            // loop and a snapshot diff calls it new.
            List<string> viaA = Assert.Single(
                FederationContentRules.CircularReferences(G("A>C", "C>B", "B>C")));
            Assert.Equal(new[] { "B", "C" }, viaA.ToArray());
        }

        [Fact]
        public void A_link_that_loads_itself_is_a_cycle()
        {
            List<string> one = Assert.Single(FederationContentRules.CircularReferences(G("A>A")));
            Assert.Equal(new[] { "A" }, one.ToArray());
        }

        [Fact]
        public void A_tree_of_links_with_no_loop_reports_none()
        {
            // Deep nesting is not a cycle, and reporting it as one would make the
            // check useless on any real federation.
            Assert.Empty(FederationContentRules.CircularReferences(G("A>B", "B>C", "A>C")));
        }

        [Fact]
        public void An_empty_or_missing_graph_reports_no_cycles()
        {
            Assert.Empty(FederationContentRules.CircularReferences(null));
            Assert.Empty(FederationContentRules.CircularReferences(new Dictionary<string, List<string>>()));
        }

        [Fact]
        public void The_reply_explains_why_a_nested_link_matters()
        {
            Assert.Contains("seen by nobody", FederationContentRules.NestingMeans);
            Assert.Contains("ten minutes", FederationContentRules.NestingMeans);
        }

        // --------------------------------------------------------- paths

        [Fact]
        public void A_path_that_does_not_resolve_is_apart_from_having_no_path()
        {
            // THE ONE THAT MATTERS. A texture whose file moved breaks a render on
            // somebody else's machine; a material that never had one is a choice.
            JObject t = FederationContentRules.PathTally(new[]
            {
                P("texture", true), P("texture", false), P("texture", null)
            });

            JObject tex = (JObject)t["by_kind"]["texture"];
            Assert.Equal(3, tex.Value<int>("total"));
            Assert.Equal(1, tex.Value<int>("with_path_resolving"));
            Assert.Equal(1, tex.Value<int>("with_path_not_resolving"));
            Assert.Equal(1, tex.Value<int>("without_a_path"));
            Assert.Equal(1, t.Value<int>("unresolved_total"));
            Assert.Contains("not an absent one", FederationContentRules.PathsMean);
        }

        [Fact]
        public void Kinds_are_kept_apart_so_a_missing_texture_is_not_a_missing_point_cloud()
        {
            JObject t = FederationContentRules.PathTally(new[]
            {
                P("texture", false), P("point_cloud", false), P("image", true)
            });
            Assert.Equal(1, ((JObject)t["by_kind"]["texture"]).Value<int>("with_path_not_resolving"));
            Assert.Equal(1, ((JObject)t["by_kind"]["point_cloud"]).Value<int>("with_path_not_resolving"));
            Assert.Equal(0, ((JObject)t["by_kind"]["image"]).Value<int>("with_path_not_resolving"));
        }

        [Fact]
        public void An_empty_model_reports_no_kinds_and_no_unresolved_paths()
        {
            JObject t = FederationContentRules.PathTally(null);
            Assert.Empty((JObject)t["by_kind"]);
            Assert.Equal(0, t.Value<int>("unresolved_total"));
        }

        // ---------------------------------------------------------- decals

        [Fact]
        public void Decals_are_declared_unobservable_rather_than_counted_as_zero()
        {
            // Zero is a count. This is the absence of a way to count, and the
            // difference is the whole reason the sentence exists.
            Assert.Contains("NOT OBSERVABLE", FederationContentRules.DecalsMean);
            Assert.Contains("rather than as zero", FederationContentRules.DecalsMean);
            Assert.Contains("checked by reflection", FederationContentRules.DecalsMean);
        }

        // ------------------------------------------------------------ links

        [Fact]
        public void A_link_whose_nesting_could_not_be_read_reports_null_and_not_zero()
        {
            // Zero nested links is a real answer; unreadable is not the same one.
            var f = new LinkFederationFact { ElementId = 1, Name = "A", NestedReadable = false };
            Assert.Null(FederationContentRules.ToJson(f)["nested_link_count"].Value<int?>());

            var ok = new LinkFederationFact { ElementId = 2, Name = "B" };
            Assert.Equal(0, FederationContentRules.ToJson(ok).Value<int>("nested_link_count"));
        }

        [Fact]
        public void Attachment_and_room_bounding_are_reported_and_null_when_unread()
        {
            var f = new LinkFederationFact
            {
                ElementId = 1, Name = "A", AttachmentType = "Overlay",
                IsRoomBounding = true, IsLoaded = true, WorksetName = "Shared Links"
            };
            JObject j = FederationContentRules.ToJson(f);
            Assert.Equal("Overlay", j.Value<string>("attachment_type"));
            Assert.True(j.Value<bool>("is_room_bounding"));
            Assert.Equal("Shared Links", j.Value<string>("workset"));

            var unread = new LinkFederationFact { ElementId = 2, Name = "B" };
            Assert.Null(FederationContentRules.ToJson(unread)["attachment_type"].Value<string>());
            Assert.Null(FederationContentRules.ToJson(unread)["is_room_bounding"].Value<bool?>());
        }
    }
}
