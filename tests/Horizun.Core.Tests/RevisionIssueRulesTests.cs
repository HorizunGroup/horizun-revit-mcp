// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Issued is written last; an issued revision the caller re-states is un-issued
// first (audit 2026-10-08, 6.15).
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RevisionIssueRulesTests
    {
        [Fact]
        public void An_issued_revision_is_unissued_before_edits_only_when_issued_is_stated()
        {
            Assert.True(RevisionIssueRules.UnissueFirst(true, true));
            Assert.True(RevisionIssueRules.UnissueFirst(true, false));
            Assert.False(RevisionIssueRules.UnissueFirst(true, null));   // not stated: left as it is
            Assert.False(RevisionIssueRules.UnissueFirst(false, true));
        }

        [Fact]
        public void Issued_is_written_last_as_stated()
        {
            Assert.True(RevisionIssueRules.IssuedLast(true));
            Assert.Null(RevisionIssueRules.IssuedLast(null));
        }
    }
}
