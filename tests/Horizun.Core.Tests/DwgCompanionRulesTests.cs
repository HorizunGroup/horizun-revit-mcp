using System.Text;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DwgCompanionRulesTests
    {
        [Theory]
        [InlineData("C:/out/sheet.dwg", "C:/out/sheet-view.dwg", "xref")]
        [InlineData("C:/out/sheet.dwg", "C:/out/sheet_logo.png", "image")]
        [InlineData("C:/out/sheet.dwg", "C:/out/sheet.jpg", "image")]
        [InlineData("C:/out/sheet.dwg", "C:/out/sheet2.png", null)]
        [InlineData("C:/out/sheet.dwg", "C:/other/sheet.png", null)]
        [InlineData("C:/out/sheet.dwg", "C:/out/sheet.pdf", null)]
        public void Companion_kind_requires_same_folder_and_stem_family(string drawing, string candidate, string expected)
            => Assert.Equal(expected, DwgCompanionRules.Kind(drawing, candidate));

        [Fact]
        public void Raster_headers_are_checked_before_a_reference_is_claimed()
        {
            Assert.True(DwgCompanionRules.ValidImageHeader("a.png", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.True(DwgCompanionRules.ValidImageHeader("a.jpg", new byte[] { 255, 216, 255, 224 }));
            Assert.False(DwgCompanionRules.ValidImageHeader("a.jpg", Encoding.ASCII.GetBytes("%PDF")));
            Assert.False(DwgCompanionRules.ValidImageHeader("a.png", new byte[] { 137, 80 }));
        }
    }
}
