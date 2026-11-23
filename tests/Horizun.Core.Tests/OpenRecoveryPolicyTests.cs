using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OpenRecoveryIdentityTests
    {
        [Xunit.Fact]
        public void Same_session_identity_stays_marked_across_wrappers_and_collisions_fail_closed()
        {
            var marks = new Horizun.Revit.Core.OpenRecoveryIdentityRegistry();
            marks.Mark(17, @"C:\models\one.rvt");
            Xunit.Assert.True(marks.IsMarked(17));
            Xunit.Assert.Equal(@"C:\models\one.rvt", marks.Original(17));
            marks.Mark(17, @"C:\models\two.rvt");
            Xunit.Assert.True(marks.IsMarked(17));
            Xunit.Assert.Null(marks.Original(17));
            Xunit.Assert.False(marks.IsMarked(18));
        }
    }
    public class OpenRecoveryPolicyTests
    {
        [Fact]
        public void Recovery_requires_detach_before_open()
        {
            Assert.NotNull(OpenRecoveryPolicy.OpenRefusal(true, false));
            Assert.Null(OpenRecoveryPolicy.OpenRefusal(true, true));
            Assert.Null(OpenRecoveryPolicy.OpenRefusal(false, false));
        }

        [Fact]
        public void Recovery_document_requires_confirmed_save_as_to_another_path()
        {
            Assert.NotNull(OpenRecoveryPolicy.SaveRefusal(true, false, false, @"C:\models\original.rvt", @"C:\models\original.rvt"));
            Assert.NotNull(OpenRecoveryPolicy.SaveRefusal(true, true, false, @"C:\models\original.rvt", @"C:\models\copy.rvt"));
            Assert.NotNull(OpenRecoveryPolicy.SaveRefusal(true, true, true, @"C:\models\original.rvt", @"C:\models\ORIGINAL.rvt"));
            Assert.Null(OpenRecoveryPolicy.SaveRefusal(true, true, true, @"C:\models\original.rvt", @"C:\models\copy.rvt"));
        }

        [Fact]
        public void Recovery_save_as_rejects_overwrite_even_before_destination_exists()
        {
            Assert.NotNull(OpenRecoveryPolicy.SaveRefusal(true, true, true, @"C:\models\original.rvt", @"C:\models\copy.rvt", true));
            Assert.Null(OpenRecoveryPolicy.SaveRefusal(true, true, true, @"C:\models\original.rvt", @"C:\models\copy.rvt", false));
            Assert.Null(OpenRecoveryPolicy.SaveRefusal(false, true, true, @"C:\models\original.rvt", @"C:\models\copy.rvt", true));
        }
    }
}
