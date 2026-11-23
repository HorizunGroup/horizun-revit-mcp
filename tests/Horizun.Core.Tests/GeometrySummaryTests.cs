using Xunit;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;

namespace Horizun.Core.Tests
{
    public class GeometrySummaryTests
    {
        [Fact] public void ConvertsAreaAndVolumeToMillimetres()
        {
            Assert.Equal(92903.04, GeometryMeasureRules.SquareMillimetres(1), 5);
            Assert.Equal  (28316846.592, GeometryMeasureRules.CubicMillimetres(1), 3);
        }
        [Fact] public void PagesAreBoundedAndContinueAtNextElement()
        {
            var page = GeometryMeasureRules.Page(10, 3, 4);
            Assert.Equal(3, page.Start); Assert.Equal(4, page.Count); Assert.Equal(7, page.Next);
            Assert.Null(GeometryMeasureRules.Page(10, 9, 4).Next);
        }
        [Fact] public void SummaryRequestValidatesPaging()
        {
            var request = JObject.Parse("{ 'ids':[1], 'measures':['summary'], 'offset':-1 }");
            Assert.False(GeometryMeasureRules.TryParse(request, out _, out _));
            request["offset"] = 0; request["page_size"] = 101;
            Assert.False(GeometryMeasureRules.TryParse(request, out _, out _));
            request["page_size"] = 10;
            Assert.True(GeometryMeasureRules.TryParse(request, out _, out _));
        }
        [Fact] public void RotatedLinkEnvelopeUsesEveryCorner()
        {
            GeometryMeasureRules.HostEnvelope(new[] { 0d, 0d, 0d }, new[] { 2d, 1d, 3d },
                p => new[] { p[0] - p[1] + 10, p[0] + p[1], p[2] }, out var min, out var max);
            Assert.Equal(new[] { 9d, 0d, 0d }, min);
            Assert.Equal(new[] { 12d, 3d, 3d }, max);
        }
    }
}
