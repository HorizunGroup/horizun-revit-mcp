using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class AppearanceInputRulesTests
    {
        [Theory]
        [InlineData("{generic_diffuse:[0,128,255],transparency:0.5}", true)]
        [InlineData("{match_shading_color:true,remove_diffuse_texture:true,transparency:1}", true)]
        [InlineData("{match_shading_color:true,replace_with_generic:true}", true)]
        [InlineData("{match_shading_color:true,replace_with_generic:'yes'}", false)]
        [InlineData("{generic_diffuse:[-1,0,0]}", false)]
        [InlineData("{generic_diffuse:[0,256,0]}", false)]
        [InlineData("{generic_diffuse:[0,0.5,0]}", false)]
        [InlineData("{generic_diffuse:[0,0]}", false)]
        [InlineData("{transparency:-0.01}", false)]
        [InlineData("{transparency:1.01}", false)]
        [InlineData("{generic_diffuse:[0,0,0],match_shading_color:true}", false)]
        [InlineData("{remove_diffuse_texture:false}", false)]
        public void ValidatesInputs(string input, bool valid)
        {
            Assert.Equal(valid, AppearanceInputRules.Validate(JObject.Parse(input)) == null);
        }
    }
}
