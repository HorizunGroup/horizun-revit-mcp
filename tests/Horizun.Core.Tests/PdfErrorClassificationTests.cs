using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PdfErrorClassificationTests
    {
        [Fact]
        public void Unknown_pattern_token_is_a_classified_invalid_input()
        {
            var views = new[] { new PdfNameSource { ViewId = 1, ViewName = "Plan" } };
            var error = Assert.Throws<ArgumentException>(() =>
                PdfNamePattern.Paths(@"C:\out", "{unknown}", views, false, DateTime.Today, 1));
            Assert.Equal(ErrorCodes.EntradaInvalida, ErrorCodes.Classify(error.Message, null).Code);
        }
    }
}
