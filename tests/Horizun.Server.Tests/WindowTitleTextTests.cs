// E10: horizun_target names the active document of each running Revit, from its window title.
using Horizun.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class WindowTitleTextTests
    {
        [Fact]
        public void A_model_and_its_view_are_read_from_the_title()
        {
            var d = (JObject)WindowTitleText.Describe("Autodesk Revit 2024 - [Tower_T3_User.rvt - Floor Plan: Level 1]");
            Assert.Equal("Tower_T3_User.rvt", (string)d["document"]);
            Assert.Equal("Floor Plan: Level 1", (string)d["view"]);
            Assert.Equal("main_window_title", (string)d["source"]);
        }

        [Fact]
        public void A_dash_inside_the_model_name_stays_in_the_name()
        {
            var d = (JObject)WindowTitleText.Describe("Autodesk Revit 2023 - [Torre 3 - ARQ.rvt - Planta de planta: Nivel 1]");
            Assert.Equal("Torre 3 - ARQ.rvt", (string)d["document"]);
            Assert.Equal("Planta de planta: Nivel 1", (string)d["view"]);
        }

        [Fact]
        public void The_home_screen_has_no_document()
        {
            var d = (JObject)WindowTitleText.Describe("Autodesk Revit 2023 - [Home]");
            Assert.Equal(JTokenType.Null, d["document"].Type);
            Assert.Equal(JTokenType.Null, WindowTitleText.Describe("").Type);
        }
    }
}
