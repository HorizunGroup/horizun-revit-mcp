// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
// horizun_manage_worksets operation=status: the per-element reading rules.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class WorksharingStatusRulesTests
    {
        [Theory]
        [InlineData("OwnedByCurrentUser", "owned_by_me")]
        [InlineData("OwnedByOtherUser", "owned_by_others")]
        [InlineData("NotOwned", "not_owned")]
        [InlineData(null, "unreadable")]
        [InlineData("SomethingNew", "unreadable")]
        public void Checkout_status_maps_to_the_census_state_and_unknown_is_never_free(string status, string expected)
            => Assert.Equal(expected, WorksharingStatusRules.State(status));

        [Fact]
        public void Borrowed_by_names_only_another_user()
        {
            Assert.Equal("ana", WorksharingStatusRules.BorrowedBy(CheckoutState.Others, " ana "));
            Assert.Equal("(owner name unreadable)", WorksharingStatusRules.BorrowedBy(CheckoutState.Others, ""));
            Assert.Null(WorksharingStatusRules.BorrowedBy(CheckoutState.Me, "maria"));
            Assert.Null(WorksharingStatusRules.BorrowedBy(CheckoutState.NoOne, null));
            Assert.Null(WorksharingStatusRules.BorrowedBy(CheckoutState.Unreadable, "x"));
        }

        [Fact]
        public void Same_user_is_case_insensitive_and_unknown_when_a_name_is_missing()
        {
            Assert.True(WorksharingStatusRules.SameUser("Maria.T", " maria.t"));
            Assert.False(WorksharingStatusRules.SameUser("ana", "maria"));
            Assert.Null(WorksharingStatusRules.SameUser("", "maria"));
            Assert.Null(WorksharingStatusRules.SameUser("ana", null));
        }

        [Fact]
        public void Validation_caps_the_ids_and_refuses_non_positive_ones()
        {
            Assert.Null(WorksharingStatusRules.Validate(new List<long>()));
            Assert.Null(WorksharingStatusRules.Validate(new List<long> { 1, 2 }));
            Assert.NotNull(WorksharingStatusRules.Validate(new List<long> { 0 }));
            Assert.NotNull(WorksharingStatusRules.Validate(Enumerable.Range(1, 501).Select(i => (long)i).ToList()));
        }
    }

    public class WorksharingEnableRulesTests
    {
        private const string Central = @"C:\ws\central\Modelo_central.rvt";

        [Fact]
        public void Enable_accepts_a_new_absolute_rvt_in_an_existing_folder_with_two_distinct_names()
            => Assert.Null(WorksharingEnableRules.EnableProblem(Central, false, true, "Shared Levels and Grids", "Workset1"));

        [Theory]
        [InlineData(null, false, true, "required")]
        [InlineData(@"central.rvt", false, true, "absolute")]
        [InlineData(@"C:\ws\central\m.rfa", false, true, ".rvt")]
        [InlineData(Central, true, true, "already exists")]
        [InlineData(Central, false, false, "folder")]
        public void Enable_never_overwrites_and_never_guesses_a_path(string path, bool exists, bool folder, string expected)
            => Assert.Contains(expected, WorksharingEnableRules.EnableProblem(path, exists, folder, "A", "B"));

        [Fact]
        public void Enable_refuses_equal_or_illegal_workset_names()
        {
            Assert.Contains("differ", WorksharingEnableRules.EnableProblem(Central, false, true, "Ejes", "ejes"));
            Assert.Contains("levels_grids_name", WorksharingEnableRules.EnableProblem(Central, false, true, "a:b", "B"));
            Assert.Contains("name", WorksharingEnableRules.EnableProblem(Central, false, true, "A", " "));
        }

        [Fact]
        public void Create_local_needs_a_workshared_central_and_a_new_different_file()
        {
            Assert.Null(WorksharingEnableRules.LocalProblem(Central, true, @"C:\ws\local_ana.rvt", false, true));
            Assert.Contains("workshared", WorksharingEnableRules.LocalProblem(Central, false, @"C:\ws\l.rvt", false, true));
            Assert.Contains("workshared", WorksharingEnableRules.LocalProblem(Central, null, @"C:\ws\l.rvt", false, true));
            Assert.Contains("already exists", WorksharingEnableRules.LocalProblem(Central, true, @"C:\ws\l.rvt", true, true));
            Assert.Contains("differ", WorksharingEnableRules.LocalProblem(Central, true, Central.ToUpperInvariant(), false, true));
        }

        [Fact]
        public void Enable_is_verified_only_when_document_and_file_both_read_back()
        {
            var names = new[] { "Shared Levels and Grids", "Workset1" };
            Assert.Empty(WorksharingEnableRules.EnableVerdict(true, names, names[0], names[1], Central.Replace((char)92, '/'), Central, true, true));
            Assert.Single(WorksharingEnableRules.EnableVerdict(true, names, names[0], names[1], Central, Central, true, null));
            Assert.Equal(2, WorksharingEnableRules.EnableVerdict(true, new[] { "Workset1" }, names[0], names[1], @"C:\other.rvt", Central, true, true).Count);
            Assert.Equal(3, WorksharingEnableRules.EnableVerdict(false, names, names[0], names[1], Central, Central, false, false).Count);
        }
    }
}
