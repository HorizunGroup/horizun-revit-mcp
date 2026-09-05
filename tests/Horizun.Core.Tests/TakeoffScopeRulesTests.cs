// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE DEFECT. horizun_quantities mode='takeoff' with include_links=true kept the
// link instance each measured document came from in a Dictionary keyed by
// Document. Revit loads a linked FILE once, so two RevitLinkInstances of the same
// file answer GetLinkDocument() with the SAME Document - and the dictionary
// collapsed. Both placements were measured (correct: the building is placed
// twice) and BOTH were reported under the last instance's id (wrong: half the
// quantity is traced to a placement that never produced it).
//
// A takeoff that cannot be traced back to the model is a number taken on faith,
// and include_links exists precisely so it can be traced. These tests are the
// numbering and the declaration, at a desk, with no Revit in the room.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class TakeoffScopeRulesTests
    {
        private static TakeoffLinkFact Link(string instanceId, string path, string title = null)
            => new TakeoffLinkFact
            {
                LinkInstanceId = instanceId,
                DocumentKey = path,
                Path = path,
                Title = title ?? System.IO.Path.GetFileNameWithoutExtension(path ?? "")
            };

        [Fact]
        public void One_placement_per_link_instance_in_the_order_given()
        {
            TakeoffScope scope = TakeoffScopeRules.Resolve(new[]
            {
                Link("100", @"C:\p\Struct.rvt"),
                Link("200", @"C:\p\Mep.rvt")
            });

            Assert.Equal(new[] { "100", "200" }, scope.Placements.Select(p => p.Link.LinkInstanceId).ToArray());
            Assert.All(scope.Placements, p => Assert.Equal(1, p.OccurrencesOfDocument));
            Assert.All(scope.Placements, p => Assert.False(p.IsRepeated));
            Assert.False(scope.HasRepeatedDocuments);
            Assert.Empty(scope.RepeatedDocuments);
        }

        /// <summary>
        /// THE DEFECT, as a test. The same file placed twice is TWO placements, each
        /// keeping its own link instance id. One entry - the old behaviour - would mean
        /// the second tower was measured under the first tower's name, or not at all.
        /// </summary>
        [Fact]
        public void Two_instances_of_the_same_file_are_two_placements_told_apart_by_instance_id()
        {
            TakeoffScope scope = TakeoffScopeRules.Resolve(new[]
            {
                Link("100", @"C:\p\Tower.rvt"),
                Link("200", @"C:\p\Tower.rvt")
            });

            Assert.Equal(2, scope.Placements.Count);
            Assert.Equal(new[] { "100", "200" }, scope.Placements.Select(p => p.Link.LinkInstanceId).ToArray());
            Assert.Equal(new[] { 1, 2 }, scope.Placements.Select(p => p.Occurrence).ToArray());
            Assert.All(scope.Placements, p => Assert.Equal(2, p.OccurrencesOfDocument));
            Assert.All(scope.Placements, p => Assert.True(p.IsRepeated));

            // No id is used twice: that is what "told apart" has to mean.
            Assert.Equal(2, scope.Placements.Select(p => p.Link.LinkInstanceId).Distinct(StringComparer.Ordinal).Count());
        }

        /// <summary>
        /// AND THE REPETITION IS DECLARED, not left to be discovered by whoever notices
        /// the total is twice what they expected. Every id is named, so each copy can be
        /// traced to its own placement.
        /// </summary>
        [Fact]
        public void A_file_placed_more_than_once_is_named_with_all_of_its_instances()
        {
            TakeoffScope scope = TakeoffScopeRules.Resolve(new[]
            {
                Link("100", @"C:\p\Tower.rvt", "Tower"),
                Link("150", @"C:\p\Mep.rvt", "Mep"),
                Link("200", @"C:\p\Tower.rvt", "Tower"),
                Link("300", @"C:\p\Tower.rvt", "Tower")
            });

            JObject declared = Assert.Single(scope.RepeatedDocuments.OfType<JObject>());
            Assert.Equal("Tower", (string)declared["document"]);
            Assert.Equal(@"C:\p\Tower.rvt", (string)declared["path"]);
            Assert.Equal(3, (int)declared["placements"]);
            Assert.Equal(new[] { "100", "200", "300" },
                         declared["link_instance_ids"].Select(t => (string)t).ToArray());
            Assert.Contains("once PER PLACEMENT", (string)declared["means"]);
            Assert.Contains("not a double count", (string)declared["means"]);

            // The file placed once is NOT declared: a declaration nobody needs teaches the
            // reader to skip the ones they do.
            Assert.DoesNotContain("Mep", scope.RepeatedDocuments.ToString());
        }

        [Fact]
        public void Each_repeated_file_is_declared_exactly_once_however_many_placements_it_has()
        {
            TakeoffScope scope = TakeoffScopeRules.Resolve(new[]
            {
                Link("1", @"C:\p\A.rvt", "A"), Link("2", @"C:\p\B.rvt", "B"),
                Link("3", @"C:\p\A.rvt", "A"), Link("4", @"C:\p\B.rvt", "B"),
                Link("5", @"C:\p\A.rvt", "A")
            });

            Assert.Equal(2, scope.RepeatedDocuments.Count);
            Assert.Equal(new[] { "A", "B" },
                         scope.RepeatedDocuments.Select(t => (string)t["document"]).ToArray());
            Assert.Equal(new[] { 3, 2 }, scope.RepeatedDocuments.Select(t => (int)t["placements"]).ToArray());
        }

        /// <summary>
        /// Two DIFFERENT files that happen to share a title are two files. Title is what
        /// Revit shows; the path is what identifies the document, and a takeoff that
        /// counted "Estructura.rvt" from two folders as one placement of one file would
        /// hide a real repetition or invent one.
        /// </summary>
        [Fact]
        public void Same_title_from_two_paths_is_two_files_not_one_placed_twice()
        {
            TakeoffScope scope = TakeoffScopeRules.Resolve(new[]
            {
                Link("100", @"C:\a\Estructura.rvt", "Estructura"),
                Link("200", @"C:\b\Estructura.rvt", "Estructura")
            });

            Assert.All(scope.Placements, p => Assert.Equal(1, p.OccurrencesOfDocument));
            Assert.Empty(scope.RepeatedDocuments);
        }

        /// <summary>
        /// A path-less linked document still has to be counted as SOMETHING. Two of them
        /// under one title are one file placed twice; with no title at all each placement
        /// stands alone rather than every anonymous link merging into one imaginary file.
        /// </summary>
        [Fact]
        public void A_document_with_no_path_falls_back_to_its_title_and_then_to_its_instance()
        {
            TakeoffScope byTitle = TakeoffScopeRules.Resolve(new[]
            {
                new TakeoffLinkFact { LinkInstanceId = "1", Title = "Unsaved" },
                new TakeoffLinkFact { LinkInstanceId = "2", Title = "Unsaved" }
            });
            Assert.All(byTitle.Placements, p => Assert.Equal(2, p.OccurrencesOfDocument));

            TakeoffScope anonymous = TakeoffScopeRules.Resolve(new[]
            {
                new TakeoffLinkFact { LinkInstanceId = "1" },
                new TakeoffLinkFact { LinkInstanceId = "2" }
            });
            Assert.All(anonymous.Placements, p => Assert.Equal(1, p.OccurrencesOfDocument));
            Assert.Empty(anonymous.RepeatedDocuments);
        }

        /// <summary>
        /// The instance id is the identity. A placement offered without one is refused
        /// rather than numbered - it could not be told from the next, which is the entire
        /// property this file defends.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_placement_with_no_instance_id_is_refused(string id)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                TakeoffScopeRules.Resolve(new[] { new TakeoffLinkFact { LinkInstanceId = id, Path = @"C:\p\A.rvt" } }));
            Assert.Contains("link_instance_id", ex.Message);
        }

        [Fact]
        public void No_links_and_no_argument_are_both_an_empty_scope_not_a_crash()
        {
            Assert.Empty(TakeoffScopeRules.Resolve(null).Placements);
            Assert.Empty(TakeoffScopeRules.Resolve(new List<TakeoffLinkFact>()).Placements);
            Assert.Empty(TakeoffScopeRules.Resolve(new TakeoffLinkFact[] { null }).Placements);
        }

        /// <summary>
        /// The provenance block the documents entry carries, so a row and a document
        /// cannot describe one placement two different ways.
        /// </summary>
        [Fact]
        public void The_placement_block_names_the_instance_and_where_it_sits()
        {
            TakeoffScope scope = TakeoffScopeRules.Resolve(new[]
            {
                Link("100", @"C:\p\Tower.rvt"), Link("200", @"C:\p\Tower.rvt")
            });

            JObject second = TakeoffScopeRules.PlacementJson(scope.Placements[1]);
            Assert.Equal("200", (string)second["link_instance_id"]);
            Assert.Equal(2, (int)second["placement"]);
            Assert.Equal(2, (int)second["placements_of_this_document"]);
            Assert.Null(TakeoffScopeRules.PlacementJson(null));
        }

        // ---------------------------------------------------------------------
        // AND THE COMMAND ACTUALLY USES IT.
        //
        // The rules above are provable at a desk; the sweep that feeds them needs a
        // Document and cannot be. So the wiring is asserted on the source: the collapsing
        // dictionary is GONE, the scope is resolved through these rules, and the reply
        // still carries the provenance a reader traces a quantity with. Correct arithmetic
        // nothing calls is not a fix.
        // ---------------------------------------------------------------------

        private static string QuantitiesSource()
        {
            System.IO.DirectoryInfo d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !System.IO.Directory.Exists(System.IO.Path.Combine(d.FullName, "src"))) d = d.Parent;
            Assert.NotNull(d);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(
                d.FullName, "src", "Horizun.Revit", "Commands", "QuantitiesCommand.cs"));
        }

        [Fact]
        public void The_takeoff_resolves_its_scope_through_these_rules()
        {
            string src = QuantitiesSource();
            Assert.Contains("TakeoffScopeRules.Resolve(linkFacts)", src);
            Assert.DoesNotContain("Dictionary<Document, RevitLinkInstance>", src);
        }

        [Fact]
        public void Every_takeoff_row_still_names_the_document_and_the_placement_it_came_from()
        {
            string src = QuantitiesSource();
            foreach (string field in new[]
                     {
                         "[\"element_id\"] = e.Id.ToString()",
                         "[\"document\"] = title",
                         "[\"document_path\"] = SafePath(owner)",
                         "[\"link_instance_id\"] = link == null ? null : link.Id.ToString()",
                         "[\"placement\"] = entry.Placement == null ? null : (JToken)entry.Placement.Occurrence"
                     })
                Assert.Contains(field, src);
            Assert.Contains("[\"repeated_link_documents\"] = repeatedDocuments", src);
        }
    }
}
