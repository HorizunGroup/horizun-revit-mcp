using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OpenPathRulesTests
    {
        [Theory]
        [InlineData(@"C:\Templates\Office.rte")]
        [InlineData(@"C:\Templates\Office.RTE")]
        public void Project_templates_are_routed_to_document_session_new_project(string path)
        {
            string message = OpenPathRules.TemplateRefusal(path);
            Assert.Contains("operation=new_project", message);
            Assert.Contains("template_path", message);
            Assert.Contains("save_as_path", message);
            Assert.Contains("Nothing was opened", message);
        }

        [Fact]
        public void A_background_library_read_may_open_a_project_template()
        {
            Assert.Null(OpenPathRules.TemplateRefusal(@"C:\Templates\Office.rte", allowProjectTemplate: true));
            Assert.NotNull(OpenPathRules.TemplateRefusal(@"C:\Templates\Office.rte", allowProjectTemplate: false));
        }

        [Theory]
        [InlineData(@"C:\Models\Building.rvt")]
        [InlineData(@"C:\Families\Door.rfa")]
        [InlineData(null)]
        public void Models_and_families_are_not_misrouted(string path)
        {
            Assert.Null(OpenPathRules.TemplateRefusal(path));
        }
    }
}
