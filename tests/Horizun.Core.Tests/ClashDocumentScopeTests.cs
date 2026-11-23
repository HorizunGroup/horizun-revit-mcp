using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ClashDocumentScopeTests
    {
        

        

        

        

        [Fact]
        public void Boolean_retry_keeps_both_failed_orientations_unknown()
        {
            bool ok = ClashBooleanRetry.Try<int>(() => throw new InvalidOperationException("first"),
                () => throw new InvalidOperationException("second"), out int value, out bool recovered, out string error);
            Assert.False(ok);
            Assert.False(recovered);
            Assert.Contains("first", error);
            Assert.Contains("second", error);
        }

        [Fact]
        public void Successful_exact_reverse_retry_is_measured_and_named()
        {
            Assert.True(ClashBooleanRetry.Try<int>(() => throw new InvalidOperationException("first"),
                () => 17, out int value, out bool recovered, out string error));
            Assert.Equal(17, value);
            Assert.True(recovered);
            Assert.Null(error);
        }

        [Fact]
        public void Working_boolean_is_never_retried()
        {
            Assert.True(ClashBooleanRetry.Try(() => 11, () => throw new Exception("must not run"),
                out int value, out bool recovered, out string error));
            Assert.Equal(11, value);
            Assert.False(recovered);
        }
    }
}
