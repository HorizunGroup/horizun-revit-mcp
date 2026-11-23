using System;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class GraphicDisplayRulesTests
    {
        [Fact]
        public void Lighting_and_gradient_are_validated_without_revit()
        {
            GraphicDisplayRules.Validate(JObject.Parse(@"{'style':'realistic','shadows':true,'sun':{'setting':'lighting','azimuth_deg':45,'altitude_deg':30,'relative_to_view':true},'background':{'type':'gradient','sky_color':'#112233','horizon_color':'#445566','ground_color':'#778899'}}"));
        }

        [Fact]
        public void NoneBackgroundIsAccepted() => GraphicDisplayRules.Validate(JObject.Parse(@"{'background':{'type':'none'}}"));

        [Theory]
        [InlineData("{}")]
        [InlineData("{'shadows':'true'}")]
        [InlineData("{'style':'invented'}")]
        [InlineData("{'sun':{'setting':'still','azimuth_deg':45}}")]
        [InlineData("{'sun':{'setting':'lighting','altitude_deg':91}}")]
        [InlineData("{'background':{'type':'gradient','sky_color':'#fff'}}")]
        [InlineData("{'background':{'type':'sky','sky_color':'#112233'}}")]
        public void Invalid_or_ignored_values_are_refused(string json)
        {
            Assert.Throws<ArgumentException>(() => GraphicDisplayRules.Validate(JObject.Parse(json)));
        }
    }
}
