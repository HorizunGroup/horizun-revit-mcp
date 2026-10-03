// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE DEFECT (course rehearsal, 2026-10-03). horizun_bind_shared_param with
// binding_kind=Type committed the binding and read it back correctly, then
// answered outcome not_bound / application.state failed: it had called
// SetAllowVaryBetweenGroups(true), which Revit rejects for a type parameter, and
// gated the verdict on a flag a type parameter can never carry.
//
// A Type binding has one value per type; it cannot vary between groups and
// cannot raise the DESAGRUPAR modal. The flag is not applicable there - and the
// Instance path, where the flag is the whole point, must not get softer.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class SharedParamBindingRulesTests
    {
        private static SharedParamBindingRules.Facts Clean(bool typeBinding) => new SharedParamBindingRules.Facts
        {
            Committed = true,
            ExistsAfter = true,
            KindMeasured = true,
            KindMatches = true,
            CatsMeasured = true,
            CatsComplete = true,
            CatsHaveUnreadable = false,
            NoUnintendedDrop = true,
            VariesAfter = typeBinding ? (bool?)null : true,
            AllowVaryRequested = true,
            TypeBinding = typeBinding
        };

        [Fact]
        public void Vary_between_groups_applies_only_to_instance_bindings()
        {
            Assert.True(SharedParamBindingRules.VaryApplies(typeBinding: false));
            Assert.False(SharedParamBindingRules.VaryApplies(typeBinding: true));
        }

        [Fact]
        public void A_type_binding_read_back_clean_is_confirmed_without_a_vary_flag()
        {
            // The rehearsal case: HRZ_COD_PRES as Type, committed, kind and categories re-read.
            Assert.Equal(SharedParamBindingRules.Confirmed, SharedParamBindingRules.Classify(Clean(typeBinding: true)));
        }

        [Fact]
        public void A_type_binding_whose_flag_reads_false_is_still_confirmed()
        {
            var f = Clean(typeBinding: true);
            f.VariesAfter = false;      // what Revit reports for a type parameter, if anyone asks
            Assert.Equal(SharedParamBindingRules.Confirmed, SharedParamBindingRules.Classify(f));
        }

        [Fact]
        public void An_instance_binding_with_the_flag_off_is_still_not_bound()
        {
            var f = Clean(typeBinding: false);
            f.VariesAfter = false;
            Assert.Equal(SharedParamBindingRules.NotBound, SharedParamBindingRules.Classify(f));
        }

        [Fact]
        public void An_instance_binding_with_the_flag_unread_is_still_unknown()
        {
            var f = Clean(typeBinding: false);
            f.VariesAfter = null;
            Assert.Equal(SharedParamBindingRules.Unknown, SharedParamBindingRules.Classify(f));
        }

        [Fact]
        public void An_instance_binding_that_did_not_ask_for_the_flag_is_confirmed_when_it_reads_false()
        {
            var f = Clean(typeBinding: false);
            f.AllowVaryRequested = false;
            f.VariesAfter = false;
            Assert.Equal(SharedParamBindingRules.Confirmed, SharedParamBindingRules.Classify(f));
        }

        [Fact]
        public void A_type_binding_still_fails_on_everything_that_does_apply_to_it()
        {
            var rolledBack = Clean(true); rolledBack.Committed = false;
            Assert.Equal(SharedParamBindingRules.NotBound, SharedParamBindingRules.Classify(rolledBack));

            var unread = Clean(true); unread.ExistsAfter = null;
            Assert.Equal(SharedParamBindingRules.Unknown, SharedParamBindingRules.Classify(unread));

            var wrongKind = Clean(true); wrongKind.KindMatches = false;
            Assert.Equal(SharedParamBindingRules.NotBound, SharedParamBindingRules.Classify(wrongKind));

            var missingCats = Clean(true); missingCats.CatsComplete = false;
            Assert.Equal(SharedParamBindingRules.NotBound, SharedParamBindingRules.Classify(missingCats));

            var holeyCats = Clean(true); holeyCats.CatsComplete = false; holeyCats.CatsHaveUnreadable = true;
            Assert.Equal(SharedParamBindingRules.Unknown, SharedParamBindingRules.Classify(holeyCats));

            var dropped = Clean(true); dropped.NoUnintendedDrop = false;
            Assert.Equal(SharedParamBindingRules.CategoriesDropped, SharedParamBindingRules.Classify(dropped));

            var dropUnread = Clean(true); dropUnread.NoUnintendedDrop = null;
            Assert.Equal(SharedParamBindingRules.Unknown, SharedParamBindingRules.Classify(dropUnread));
        }
    }
}
