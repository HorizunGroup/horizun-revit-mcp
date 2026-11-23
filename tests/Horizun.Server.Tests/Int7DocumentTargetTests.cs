// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// INT-7 m-h: a window title with no file extension ("Torre A - Floor Plan: Level 1") carries the view as well as the model. The
// view is not part of the document: changing the view must never change which document a pinned target follows.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class Int7DocumentTargetTests
    {
        private static string DocumentOf(string title) => (string)((JObject)WindowTitleText.Describe(title))["document"];
        private static string ViewOf(string title) => (string)((JObject)WindowTitleText.Describe(title))["view"];

        [Theory]
        [InlineData("Autodesk Revit 2024 - [Torre A - Floor Plan: Level 1]", "Torre A", "Floor Plan: Level 1")]
        [InlineData("Autodesk Revit 2026 - [Torre A - Estructura - Planta estructural: Nivel 1]", "Torre A - Estructura", "Planta estructural: Nivel 1")]
        [InlineData("Autodesk Revit 2025 - [Torre A - 3D View: {3D}]", "Torre A", "3D View: {3D}")]
        [InlineData("Autodesk Revit 2025 - [Torre A - Sheet: A101 - Planta nivel 1]", "Torre A", "Sheet: A101 - Planta nivel 1")]
        [InlineData("Autodesk Revit 2025 - [Torre A - Sections (Building Section): Section 1]", "Torre A", "Sections (Building Section): Section 1")]
        [InlineData("Autodesk Revit 2024 - [Torre A.rvt - Floor Plan: Level 1]", "Torre A.rvt", "Floor Plan: Level 1")]
        [InlineData("Autodesk Revit 2024 - [Torre A - Estructura.rvt - Floor Plan: Level 1]", "Torre A - Estructura.rvt", "Floor Plan: Level 1")]
        public void The_view_suffix_is_not_part_of_the_document(string title, string document, string view)
        {
            Assert.Equal(document, DocumentOf(title));
            Assert.Equal(view, ViewOf(title));
        }

        [Fact]
        public void A_title_with_only_the_model_name_or_the_home_screen_says_what_it_says()
        {
            Assert.Equal("Torre A", DocumentOf("Autodesk Revit 2024 - [Torre A]"));
            Assert.Null(ViewOf("Autodesk Revit 2024 - [Torre A]"));
            Assert.Null(DocumentOf("Autodesk Revit 2024 - [Home]"));
            Assert.Null(DocumentOf("Autodesk Revit 2024 - [Inicio]"));
        }

        private static DocumentTarget.Live LiveFromTitle(int pid, string title) =>
            new DocumentTarget.Live { Pid = pid, Year = "2024", ActiveDocument = DocumentOf(title) };

        [Fact]
        public void Changing_the_view_never_makes_a_pinned_document_ambiguous()
        {
            TargetSelection pinned = TargetSelection.ByDocument(5, "Torre A");
            string[] views =
            {
                "Autodesk Revit 2024 - [Torre A - Floor Plan: Level 1]",
                "Autodesk Revit 2024 - [Torre A - Sections (Building Section): Section 1]",
                "Autodesk Revit 2024 - [Torre A - 3D View: {3D}]",
                "Autodesk Revit 2024 - [Torre A - Sheet: A101 - Planta]"
            };
            foreach (string title in views)
            {
                TargetSelection r = DocumentTarget.Refresh(pinned, () => new List<DocumentTarget.Live> { LiveFromTitle(5, title) });
                Assert.Null(r.Unresolved);
                Assert.Equal(5, r.Pid);
            }
        }

        [Fact]
        public void A_view_change_in_the_pinned_instance_does_not_send_the_call_to_another_instance_that_shows_the_same_name()
        {
            // Two Revits show "Torre A - Estructura" and "Torre A"; the pin is "Torre A - Estructura" and its view changes.
            TargetSelection pinned = TargetSelection.ByDocument(5, "Torre A - Estructura");
            TargetSelection r = DocumentTarget.Refresh(pinned, () => new List<DocumentTarget.Live>
            {
                LiveFromTitle(5, "Autodesk Revit 2024 - [Torre A - Estructura - Section: S1]"),
                LiveFromTitle(6, "Autodesk Revit 2024 - [Torre A - Floor Plan: Level 1]")
            });
            Assert.Null(r.Unresolved);
            Assert.Equal(5, r.Pid);
        }

        [Fact]
        public void A_pin_by_name_chooses_the_instance_whatever_view_it_shows()
        {
            var live = new List<DocumentTarget.Live>
            {
                LiveFromTitle(5, "Autodesk Revit 2024 - [Torre A - Estructura - Section: S1]"),
                LiveFromTitle(6, "Autodesk Revit 2024 - [Torre A - Floor Plan: Level 1]")
            };
            Assert.Equal(6, DocumentTarget.Match("Torre A", live, allowFragment: false).Chosen.Pid);
            Assert.Equal(5, DocumentTarget.Match("Torre A - Estructura", live, allowFragment: false).Chosen.Pid);
        }
    }
}
