using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OpenRecoveryEffectTests
    {
        [Fact]
        public void Deletion_is_reported_per_id_after_open()
        {
            Assert.Equal("verified_deleted", OpenRecoveryEffect.CheckDeletedId(12, id => false));
            Assert.Equal("still_present", OpenRecoveryEffect.CheckDeletedId(13, id => true));
            Assert.Equal("unverified", OpenRecoveryEffect.CheckDeletedId(14, id => throw new System.Exception()));
        }
        [Fact]
        public void Delete_elements_reports_absence_only_when_all_ids_are_absent_after_open()
        {
            Assert.Equal("elements_absent", OpenRecoveryEffect.Check("DeleteElements", new long[] { 12, 13 }, id => false));
            Assert.Equal("unverified", OpenRecoveryEffect.Check("DeleteElements", new long[] { 12, 13 }, id => id == 13));
            Assert.Equal("unverified", OpenRecoveryEffect.Check("DeleteElements", new long[0], id => false));
        }

        [Fact]
        public void Unsupported_resolution_and_failed_read_are_unverified()
        {
            Assert.Equal("unverified", OpenRecoveryEffect.Check("DetachElements", new long[] { 12 }, id => false));
            Assert.Equal("unverified", OpenRecoveryEffect.Check("DeleteElements", new long[] { 12 }, id => throw new System.Exception()));
        }
    }
}


