// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
// What counts as "ours" in a shared model, and the visibility tool's arguments.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OwnershipRulesTests
    {
        [Fact]
        public void A_file_nobody_else_works_in_is_all_own()
        {
            OwnershipVerdict v = OwnershipRules.Decide(false, null, false, null);
            Assert.True(v.Own);
            Assert.Equal(OwnershipRules.NotWorkshared, v.Evidence);
        }

        [Fact]
        public void Evidence_is_taken_strongest_first()
        {
            Assert.Equal(OwnershipRules.Mark, OwnershipRules.Decide(true, true, true, true).Evidence);
            Assert.Equal(OwnershipRules.Session, OwnershipRules.Decide(true, null, true, false).Evidence);
            Assert.Equal(OwnershipRules.Creator, OwnershipRules.Decide(true, false, false, true).Evidence);
        }

        [Fact]
        public void Another_creator_is_foreign_and_no_evidence_is_foreign_too()
        {
            OwnershipVerdict other = OwnershipRules.Decide(true, null, false, false, "ana");
            Assert.False(other.Own);
            Assert.Contains("ana", other.Reason);
            OwnershipVerdict unknown = OwnershipRules.Decide(true, null, false, null);
            Assert.False(unknown.Own);
            Assert.Equal(OwnershipRules.Unknown, unknown.Evidence);
        }

        [Fact]
        public void Not_own_lists_foreign_and_missing_verdicts_in_order()
        {
            var list = OwnershipRules.NotOwn(new[]
            {
                new KeyValuePair<long, OwnershipVerdict>(1, new OwnershipVerdict { Own = true }),
                new KeyValuePair<long, OwnershipVerdict>(2, new OwnershipVerdict { Own = false }),
                new KeyValuePair<long, OwnershipVerdict>(3, null)
            });
            Assert.Equal(new List<long> { 2, 3 }, list);
        }

        [Theory]
        [InlineData(true, null, 1)]
        [InlineData(false, null, null)]
        [InlineData(true, 0, null)]
        [InlineData(false, 50, 50)]
        public void New_detail_views_hide_their_marker_everywhere_in_a_shared_model_by_default(bool workshared, int? requested, int? expected)
            => Assert.Equal(expected, ViewMarkerRules.ForNewView(workshared, requested));

        [Fact]
        public void Marker_scale_is_validated()
        {
            Assert.Null(ViewMarkerRules.Validate(null));
            Assert.Null(ViewMarkerRules.Validate(0));
            Assert.NotNull(ViewMarkerRules.Validate(-1));
            Assert.NotNull(ViewMarkerRules.Validate(30000));
        }

        [Theory]
        [InlineData("{\"operation\":\"hide\",\"view_id\":5,\"element_ids\":[1]}", true)]
        [InlineData("{\"operation\":\"hide\",\"view_id\":5}", false)]
        [InlineData("{\"operation\":\"hide\",\"element_ids\":[1]}", false)]
        [InlineData("{\"operation\":\"set_overrides\",\"view_id\":5,\"categories\":[\"OST_Walls\"]}", false)]
        [InlineData("{\"operation\":\"set_overrides\",\"view_id\":5,\"categories\":[\"OST_Walls\"],\"overrides\":{\"halftone\":true}}", true)]
        [InlineData("{\"operation\":\"hide_foreign_markers\",\"view_id\":5}", true)]
        [InlineData("{\"operation\":\"marker_scale\",\"view_ids\":[5],\"coarser_than\":1}", true)]
        [InlineData("{\"operation\":\"marker_scale\",\"view_ids\":[5]}", false)]
        [InlineData("{\"operation\":\"isolate\",\"view_id\":5}", false)]
        public void Visibility_arguments_are_refused_before_revit(string json, bool ok)
            => Assert.Equal(ok, ViewVisibilityRules.Validate(JObject.Parse(json)) == null);
    }
}
