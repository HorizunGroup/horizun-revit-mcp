using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DryRunDirtyRulesTests
    {
        [Fact] public void A_clean_document_turned_dirty_by_a_rehearsal_is_reported()
        {
            JObject b = DryRunDirtyRules.Evaluate(true, false, true);
            Assert.NotNull(b);
            Assert.True((bool)b["document_modified_after"]);
        }
        [Fact] public void Nothing_is_said_when_it_was_already_dirty_or_stayed_clean_or_is_unknown()
        {
            Assert.Null(DryRunDirtyRules.Evaluate(true, true, true));
            Assert.Null(DryRunDirtyRules.Evaluate(true, false, false));
            Assert.Null(DryRunDirtyRules.Evaluate(true, null, true));
            Assert.Null(DryRunDirtyRules.Evaluate(true, false, null));
        }
        [Fact] public void A_real_write_is_never_flagged()
        {
            Assert.Null(DryRunDirtyRules.Evaluate(false, false, true));
        }
        [Fact] public void Only_a_reply_that_says_dry_run_counts()
        {
            Assert.True(DryRunDirtyRules.IsDryRunReply(new JObject { ["dry_run"] = true }));
            Assert.False(DryRunDirtyRules.IsDryRunReply(new JObject { ["dry_run"] = false }));
            Assert.False(DryRunDirtyRules.IsDryRunReply(new JObject()));
        }
    }
}
