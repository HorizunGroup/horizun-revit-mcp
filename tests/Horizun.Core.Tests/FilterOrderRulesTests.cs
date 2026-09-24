// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// order_filters of horizun_manage_views. MEASURED 2026-09-24: on 2026 a duplicated
// view held five filters (three inherited), so a two-id list was refused; on 2023
// a correct reorder failed its end-of-batch verification because a later
// apply_filter in the same batch disabled one of the reordered filters. What must
// hold: Reorder moves only the named filters within their own slots; the
// end-of-batch check accepts a later append or state change but not a changed
// relative order.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FilterOrderRulesTests
    {
        [Fact]
        public void Reorder_keeps_inherited_filters_in_place_and_swaps_only_the_named_ones()
        {
            var current = new List<long> { 1364036, 576429, 576430, 201, 202 };
            Assert.Equal(new List<long> { 1364036, 576429, 576430, 202, 201 }, FilterOrderRules.Reorder(current, new List<long> { 202, 201 }));
        }

        [Fact]
        public void Reorder_uses_the_slots_the_named_filters_already_hold_even_when_interleaved()
        {
            var current = new List<long> { 150, 201, 151, 202 };
            Assert.Equal(new List<long> { 150, 202, 151, 201 }, FilterOrderRules.Reorder(current, new List<long> { 202, 201 }));
        }

        [Fact]
        public void Reorder_refuses_a_filter_not_on_the_view_or_named_twice()
        {
            var current = new List<long> { 150, 201 };
            Assert.Null(FilterOrderRules.Reorder(current, new List<long> { 202, 201 }));
            Assert.Null(FilterOrderRules.Reorder(current, new List<long> { 201, 201 }));
        }

        [Fact]
        public void KeepsRelativeOrder_accepts_a_filter_appended_later_in_the_batch()
        {
            Assert.True(FilterOrderRules.KeepsRelativeOrder(new List<long> { 150, 202, 201, 999 }, new List<long> { 150, 202, 201 }));
        }

        [Fact]
        public void KeepsRelativeOrder_rejects_a_changed_order_or_a_missing_filter()
        {
            Assert.False(FilterOrderRules.KeepsRelativeOrder(new List<long> { 150, 201, 202 }, new List<long> { 150, 202, 201 }));
            Assert.False(FilterOrderRules.KeepsRelativeOrder(new List<long> { 150, 202 }, new List<long> { 150, 202, 201 }));
            Assert.False(FilterOrderRules.KeepsRelativeOrder(new List<long> { 150 }, new List<long>()));
        }
    }
}
