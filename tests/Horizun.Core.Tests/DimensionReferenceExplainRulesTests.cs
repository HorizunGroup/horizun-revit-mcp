using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// horizun_get_dimension_references explain mode (4.2-N3, CT-P2-2f): why a reference cannot carry a dimension.
    /// </summary>
    public sealed class DimensionReferenceExplainRulesTests
    {
        private static string[] Codes(ReferenceFacts f) => DimensionReferenceExplainRules.Explain(f).Select(r => r.Code).ToArray();

        [Fact]
        public void A_clean_host_reference_has_no_reasons()
        {
            Assert.Empty(Codes(new ReferenceFacts { InViewDirection = new[] { 1.0, 0, 0 } }));
        }

        [Fact]
        public void An_unparseable_text_stops_at_that()
        {
            Assert.Equal(new[] { DimensionReferenceExplainRules.Unparseable }, Codes(new ReferenceFacts { Parsed = false, ElementExists = false }));
        }

        [Fact]
        public void A_deleted_host_element_is_named()
        {
            var why = DimensionReferenceExplainRules.Explain(new ReferenceFacts { ElementExists = false });
            Assert.Equal(DimensionReferenceExplainRules.ElementMissing, Assert.Single(why).Code);
            Assert.Contains("horizun_annotate", why[0].NextStep);
        }

        [Fact]
        public void A_linked_grid_names_the_measured_api_refusal()
        {
            var why = DimensionReferenceExplainRules.Explain(new ReferenceFacts { IsLinked = true, IsDatum = true, InViewDirection = new[] { 0.0, 1, 0 } });
            Assert.Equal("linked_datum_rejected_by_dimension_api", Assert.Single(why).Code);
            Assert.Contains("faces", why[0].NextStep);
        }

        [Fact]
        public void A_linked_wall_face_is_not_refused_for_being_linked()
        {
            Assert.Empty(Codes(new ReferenceFacts { IsLinked = true, IsDatum = false, InViewDirection = new[] { 1.0, 0, 0 } }));
        }

        [Fact]
        public void Every_way_a_link_can_fail_is_a_separate_code()
        {
            Assert.Equal(new[] { DimensionReferenceExplainRules.LinkInstanceMissing }, Codes(new ReferenceFacts { IsLinked = true, LinkInstanceExists = false }));
            Assert.Equal(new[] { DimensionReferenceExplainRules.LinkUnloaded }, Codes(new ReferenceFacts { IsLinked = true, LinkLoaded = false }));
            Assert.Equal(new[] { DimensionReferenceExplainRules.LinkedElementMissing }, Codes(new ReferenceFacts { IsLinked = true, LinkedElementExists = false }));
        }

        [Fact]
        public void An_unloaded_link_is_reported_before_the_datum_rule()
        {
            Assert.Equal(new[] { DimensionReferenceExplainRules.LinkUnloaded }, Codes(new ReferenceFacts { IsLinked = true, IsDatum = true, LinkLoaded = false }));
        }

        [Fact]
        public void Hidden_element_and_hidden_category_can_both_be_named()
        {
            Assert.Equal(new[] { DimensionReferenceExplainRules.HiddenInView, DimensionReferenceExplainRules.CategoryHiddenInView },
                Codes(new ReferenceFacts { HiddenInView = true, CategoryHiddenInView = true, InViewDirection = new[] { 1.0, 0, 0 } }));
        }

        [Fact]
        public void A_face_parallel_to_the_view_is_named()
        {
            Assert.Equal(new[] { DimensionReferenceExplainRules.FaceOnToView }, Codes(new ReferenceFacts { FaceOnToView = true }));
        }

        [Fact]
        public void Unreadable_geometry_is_unknown_not_usable()
        {
            Assert.Equal(new[] { DimensionReferenceExplainRules.GeometryUnreadable }, Codes(new ReferenceFacts { GeometryUnreadable = true }));
        }

        [Fact]
        public void Every_reason_carries_a_next_step()
        {
            var all = new[]
            {
                new ReferenceFacts { Parsed = false }, new ReferenceFacts { ElementExists = false },
                new ReferenceFacts { IsLinked = true, LinkInstanceExists = false }, new ReferenceFacts { IsLinked = true, LinkLoaded = false },
                new ReferenceFacts { IsLinked = true, LinkedElementExists = false }, new ReferenceFacts { IsLinked = true, IsDatum = true },
                new ReferenceFacts { HiddenInView = true }, new ReferenceFacts { CategoryHiddenInView = true },
                new ReferenceFacts { FaceOnToView = true }, new ReferenceFacts { GeometryUnreadable = true }
            };
            foreach (var f in all)
                foreach (var r in DimensionReferenceExplainRules.Explain(f))
                {
                    Assert.False(string.IsNullOrWhiteSpace(r.Message), r.Code);
                    Assert.False(string.IsNullOrWhiteSpace(r.NextStep), r.Code);
                    Assert.Equal(r.Code, r.ToJson().Value<string>("code"));
                }
        }

        // ---- pairs ------------------------------------------------------------------------------

        [Theory]
        [InlineData(1, 0, 0, 1, 0, 0, true)]
        [InlineData(1, 0, 0, -1, 0, 0, true)]      // anti-parallel is parallel
        [InlineData(1, 0, 0, 0, 1, 0, false)]
        [InlineData(1, 0, 0, 1, 0.01, 0, false)]   // 0.57 degrees apart
        [InlineData(1, 0, 0, 1, 0.0005, 0, true)]  // 0.03 degrees apart
        public void Parallelism(double ax, double ay, double az, double bx, double by, double bz, bool expected) =>
            Assert.Equal(expected, DimensionReferenceExplainRules.Parallel(new[] { ax, ay, az }, new[] { bx, by, bz }));

        [Fact]
        public void Zero_or_missing_vectors_are_not_parallel()
        {
            Assert.False(DimensionReferenceExplainRules.Parallel(null, new[] { 1.0, 0 }));
            Assert.False(DimensionReferenceExplainRules.Parallel(new[] { 0.0, 0 }, new[] { 1.0, 0 }));
            Assert.False(DimensionReferenceExplainRules.Parallel(new[] { 1.0, 0 }, new[] { 1.0, 0, 0 }));
        }

        [Fact]
        public void A_pair_of_parallel_references_has_no_reasons()
        {
            Assert.Empty(DimensionReferenceExplainRules.ExplainPair("a", "b", new[] { 1.0, 0, 0 }, new[] { -1.0, 0, 0 }));
        }

        [Fact]
        public void A_pair_that_is_not_parallel_says_what_to_use_instead()
        {
            var why = DimensionReferenceExplainRules.ExplainPair("a", "b", new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 });
            Assert.Equal(DimensionReferenceExplainRules.PairNotParallel, Assert.Single(why).Code);
            Assert.Contains("angular", why[0].NextStep);
        }

        [Fact]
        public void The_same_reference_twice_and_unknown_directions_are_named()
        {
            Assert.Equal(DimensionReferenceExplainRules.PairSameReference,
                Assert.Single(DimensionReferenceExplainRules.ExplainPair("a", "a", new[] { 1.0, 0, 0 }, new[] { 1.0, 0, 0 })).Code);
            Assert.Equal(DimensionReferenceExplainRules.PairDirectionUnknown,
                Assert.Single(DimensionReferenceExplainRules.ExplainPair("a", "b", null, new[] { 1.0, 0, 0 })).Code);
        }

        // ---- arguments ----------------------------------------------------------------------------

        [Fact]
        public void Without_explain_fields_the_normal_search_is_untouched()
        {
            Assert.Null(DimensionReferenceExplainRules.Validate(JObject.Parse("{\"view_id\":1,\"element_ids\":[5]}")));
        }

        [Fact]
        public void Explain_arguments_validate()
        {
            Assert.Null(DimensionReferenceExplainRules.Validate(JObject.Parse("{\"view_id\":1,\"explain_references\":[\"abc\",\"def\"]}")));
            Assert.Null(DimensionReferenceExplainRules.Validate(JObject.Parse("{\"view_id\":1,\"dimension_id\":77}")));
        }

        [Theory]
        [InlineData("{\"explain_references\":[]}")]
        [InlineData("{\"explain_references\":[\"\"]}")]
        [InlineData("{\"explain_references\":[5]}")]
        [InlineData("{\"explain_references\":\"abc\"}")]
        [InlineData("{\"dimension_id\":\"7\"}")]
        [InlineData("{\"explain_references\":[\"a\"],\"dimension_id\":7}")]
        [InlineData("{\"explain_references\":[\"a\"],\"element_ids\":[1]}")]
        [InlineData("{\"dimension_id\":7,\"selectors\":[\"grid\"]}")]
        [InlineData("{\"dimension_id\":7,\"linked_targets\":[]}")]
        public void Explain_refusals(string json) => Assert.NotNull(DimensionReferenceExplainRules.Validate(JObject.Parse(json)));

        [Fact]
        public void Too_many_references_are_refused()
        {
            var r = new JObject { ["explain_references"] = new JArray(Enumerable.Range(0, 51).Select(i => "r" + i)) };
            Assert.Contains("50", DimensionReferenceExplainRules.Validate(r));
        }
    }
}
