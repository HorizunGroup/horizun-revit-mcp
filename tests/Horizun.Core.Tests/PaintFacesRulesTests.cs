using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class PaintFacesRulesTests
    {
        private static JObject Request(string action = "paint") => new JObject
        {
            ["operation"] = "paint_faces", ["target_document"] = "Example model",
            ["pairs"] = new JArray(new JObject { ["element_id"] = 10, ["face_reference"] = "stable-face", ["action"] = action,
                ["material_id"] = action == "paint" ? new JValue(20) : null })
        };

        [Fact]
        public void Paint_requires_explicit_face_owner_and_material_and_document()
        {
            JObject r = Request(); Assert.Null(PaintFacesRules.Validate(r));
            r.Remove("target_document"); Assert.Contains("target_document", PaintFacesRules.Validate(r));
            r = Request(); r["pairs"][0]["element_id"] = -1; Assert.Contains("element_id", PaintFacesRules.Validate(r));
            r = Request(); ((JObject)r["pairs"][0]).Remove("material_id"); Assert.Contains("material_id", PaintFacesRules.Validate(r));
        }

        [Fact]
        public void Remove_is_explicit_and_cannot_silently_accept_material_or_duplicate_face()
        {
            JObject r = Request("remove"); ((JObject)r["pairs"][0]).Remove("material_id"); Assert.Null(PaintFacesRules.Validate(r));
            r["pairs"][0]["material_id"] = 20; Assert.Contains("does not accept", PaintFacesRules.Validate(r));
            r = Request(); ((JArray)r["pairs"]).Add(r["pairs"][0].DeepClone()); Assert.Contains("same face", PaintFacesRules.Validate(r));
            r = Request(); r["wall_ids"] = new JArray(10); Assert.Contains("not applicable", PaintFacesRules.Validate(r));
        }

        [Theory]
        [InlineData("paint", 20L, true, 20L, true)]
        [InlineData("paint", 20L, true, 21L, false)]
        [InlineData("paint", 20L, false, null, false)]
        [InlineData("paint", 20L, null, null, false)]
        [InlineData("remove", null, false, null, true)]
        [InlineData("remove", null, true, 20L, false)]
        [InlineData("remove", null, null, null, false)]
        public void Re_read_requires_the_exact_painted_material_or_confirmed_unpainted_state(string action, long? wanted, bool? painted, long? actual, bool expected)
        {
            Assert.Equal(expected, PaintFacesRules.Matches(action, wanted, painted, actual));
        }

        [Fact]
        public void Batch_limit_and_unknown_nested_fields_are_refused()
        {
            JObject r = Request(); r["pairs"][0]["typo"] = true; Assert.Contains("Unknown", PaintFacesRules.Validate(r));
            r = Request(); for (int i = 0; i < PaintFacesRules.MaxFaces; i++) ((JArray)r["pairs"]).Add(new JObject());
            Assert.Contains("1..200", PaintFacesRules.Validate(r));
        }
    }
}
