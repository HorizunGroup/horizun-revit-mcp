using Newtonsoft.Json.Linq;
using Xunit;
using Horizun.Revit.Core;

namespace Horizun.Core.Tests
{
    public class RailingInfillRulesTests
    {
        private static JObject Req(JObject infill) => new JObject
        {
            ["target_document"] = "d", ["type_id"] = 1, ["level_id"] = 2,
            ["path"] = new JArray(new JArray(0, 0, 0), new JArray(1000, 0, 0)), ["infill"] = infill
        };
        private static JObject Good() => new JObject { ["new_type_name"] = "Glass 1100", ["baluster_type_id"] = 5, ["spacing"] = 1000 };

        [Fact] public void A_complete_infill_is_accepted() => Assert.Null(ArchitecturalEditRules.ValidateRailing(Req(Good())));
        [Fact] public void No_infill_is_still_accepted() => Assert.Null(ArchitecturalEditRules.ValidateRailingInfill(null));
        [Fact] public void Infill_needs_a_new_type_name()
        {
            JObject i = Good(); i.Remove("new_type_name");
            Assert.Contains("new_type_name", ArchitecturalEditRules.ValidateRailing(Req(i)));
        }
        [Fact] public void Infill_needs_a_baluster()
        {
            JObject i = Good(); i.Remove("baluster_type_id");
            Assert.Contains("baluster_type_id", ArchitecturalEditRules.ValidateRailing(Req(i)));
        }
        [Theory] [InlineData(0)] [InlineData(-100)]
        public void Spacing_must_be_positive(double spacing)
        {
            JObject i = Good(); i["spacing"] = spacing;
            Assert.Contains("spacing", ArchitecturalEditRules.ValidateRailing(Req(i)));
        }
        [Fact] public void An_unknown_infill_field_is_refused()
        {
            JObject i = Good(); i["glass"] = true;
            Assert.Contains("infill.glass", ArchitecturalEditRules.ValidateRailing(Req(i)));
        }
        [Fact] public void A_post_type_must_be_an_integer()
        {
            JObject i = Good(); i["post_type_id"] = "x";
            Assert.Contains("post_type_id", ArchitecturalEditRules.ValidateRailing(Req(i)));
        }
        [Fact] public void Infill_must_be_an_object() =>
            Assert.Contains("infill must be an object", ArchitecturalEditRules.ValidateRailingInfill(new JArray()));
    }
}
