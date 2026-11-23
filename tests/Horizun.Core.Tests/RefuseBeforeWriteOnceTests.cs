// Horizun Core tests - original Horizun code.
// A refusal that already ends with "Nothing was written." says it once.
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RefuseBeforeWriteOnceTests
    {
        [Fact]
        public void SaysNothingWasWrittenOnceWhenTheTextAlreadyDoes()
        {
            var r = CommandResult.RefuseBeforeWrite("the view takes its filters from a template. Nothing was written.");
            Assert.Equal("the view takes its filters from a template. Nothing was written.", r.Error);
        }

        [Fact]
        public void AddsItWhenTheTextDoesNot()
        {
            Assert.Equal("bad input. Nothing was written.", CommandResult.RefuseBeforeWrite("bad input.").Error);
        }
    }
}
