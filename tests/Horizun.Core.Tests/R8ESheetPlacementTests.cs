using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class R8ESheetPlacementTests
    {
        [Fact]
        public void ViewportReportsViewScaleAndKeepsUnknownDistinct()
        {
            var placement = new PlacementFact { Class = "viewport", ViewScale = 50 };
            Assert.Equal(50, (int)placement.ToJson(304.8)["view_scale"]);
            placement.ViewScale = null;
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, placement.ToJson(304.8)["view_scale"].Type);
        }
    }
}
