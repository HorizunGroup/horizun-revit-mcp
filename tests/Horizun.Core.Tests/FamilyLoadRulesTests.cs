using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{

    public class RevitInternalTouchRulesTests
    {
        [Theory]
        [InlineData(true, "SystemBrowser", true)]
        [InlineData(true, "ProjectBrowser", true)]
        [InlineData(true, "Internal", false)]
        [InlineData(true, "FloorPlan", false)]
        [InlineData(true, "DraftingView", false)]
        [InlineData(true, "Schedule", false)]
        [InlineData(true, "systembrowser", false)]
        [InlineData(true, null, false)]
        [InlineData(false, "SystemBrowser", false)]
        public void Only_the_browser_panes_are_internal_touches(bool isView, string viewType, bool expected) =>
            Assert.Equal(expected, RevitInternalTouchRules.IsBrowserPane(isView, viewType));

        private static bool Pane(long id) => id == 69851 || id == 70000;

        [Fact]
        public void A_rolled_back_call_that_left_only_browser_panes_sets_them_aside()
        {
            Assert.Equal(new long[] { 69851 }, RevitInternalTouchRules.SetAside(new long[] { 69851 }, 0, 0, true, Pane));
            Assert.Equal(new long[] { 69851, 70000 }, RevitInternalTouchRules.SetAside(new long[] { 69851, 70000 }, 0, 0, true, Pane));
        }

        [Fact]
        public void A_committed_call_keeps_the_browser_pane_as_a_change()
        {
            // no rollback seen: a commit (or a script) keeps it for the ledger, the sync reminder and the undo records
            Assert.Empty(RevitInternalTouchRules.SetAside(new long[] { 69851 }, 0, 0, false, Pane));
            // a rollback was seen but something real stayed: everything stays
            Assert.Empty(RevitInternalTouchRules.SetAside(new long[] { 69851, 500 }, 0, 0, true, Pane));
            Assert.Empty(RevitInternalTouchRules.SetAside(new long[] { 69851 }, 1, 0, true, Pane));
            Assert.Empty(RevitInternalTouchRules.SetAside(new long[] { 69851 }, 0, 1, true, Pane));
            Assert.Empty(RevitInternalTouchRules.SetAside(new long[0], 0, 0, true, Pane));
        }

        [Fact]
        public void A_deletion_the_rollback_restored_does_not_block_the_set_aside()
        {
            // 777 was reported deleted, then the rollback brought it back: it resolves again, so nothing was left behind.
            var added = new HashSet<long> { 900 };           // added then rolled back: no longer resolves
            var modified = new HashSet<long> { 69851 };
            var deleted = new HashSet<long> { 777 };
            var touches = new HashSet<long>();
            RevitInternalTouchRules.Reconcile(added, modified, deleted, touches, true, id => id != 900, _ => false, Pane);
            Assert.Empty(added); Assert.Empty(deleted); Assert.Empty(modified);
            Assert.Equal(new long[] { 69851 }, touches);
        }

        [Fact]
        public void A_deletion_that_stayed_keeps_the_browser_pane_counted()
        {
            var modified = new HashSet<long> { 69851 };
            var deleted = new HashSet<long> { 777 };
            var touches = new HashSet<long>();
            RevitInternalTouchRules.Reconcile(new HashSet<long>(), modified, deleted, touches, true, id => id != 777, _ => false, Pane);
            Assert.Equal(new long[] { 777 }, deleted);
            Assert.Equal(new long[] { 69851 }, modified);
            Assert.Empty(touches);
        }

        /// <summary>The field case: a rehearsal whose only residue is the System Browser reports no model change, and says what Revit touched.</summary>
        [Fact]
        public void A_rehearsal_that_only_refreshed_a_browser_pane_reports_no_model_change()
        {
            var document = new Autodesk.Revit.DB.Document { Title = "touch-" + System.Guid.NewGuid() };
            var change = new ChangeWatch.DocChanges { Document = document };
            change.InternalTouches.Add(69851);
            var watch = new ChangeWatch();
            watch.Documents.Add(change);
            var result = CommandResult.Ok(new JObject { ["dry_run"] = true });
            SpatialAfterWrite.Attach("horizun_apply_cad_plan", watch, result);
            var data = (JObject)result.Data;
            Assert.Null(data["model_changes"]);
            Assert.Equal(1, (int)data["revit_internal_touches"]["count"]);
            Assert.Equal(69851, (long)data["revit_internal_touches"]["sample"][0]["id"]);
        }

        [Fact]
        public void A_real_change_beside_a_browser_pane_is_still_counted()
        {
            var document = new Autodesk.Revit.DB.Document { Title = "touch-" + System.Guid.NewGuid() };
            var change = new ChangeWatch.DocChanges { Document = document };
            change.InternalTouches.Add(69851);
            change.Modified.Add(500);
            var watch = new ChangeWatch();
            watch.Documents.Add(change);
            var result = CommandResult.Ok(new JObject());
            SpatialAfterWrite.Attach("horizun_write_params_verified", watch, result);
            var data = (JObject)result.Data;
            Assert.Equal(1, (int)data["model_changes"]["modified"]);
            Assert.NotNull(data["revit_internal_touches"]);
        }
    }
}
