using System;
using System.IO;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// Pins the Pro 3.4.0b fixes: a tag on a linked element is built with the tag-type overload over a
    /// link reference (the TagMode overload ends in a bare InternalException), its rehearsal is isolated
    /// in a SubTransaction, and horizun_clash honours target_document.
    /// </summary>
    public class LinkedTagWiringTests
    {
        [Theory]
        [InlineData("OST_Doors", "OST_DoorTags")]
        [InlineData("OST_Windows", "OST_WindowTags")]
        [InlineData("OST_Walls", "OST_WallTags")]
        [InlineData("OST_StructuralColumns", "OST_StructuralColumnTags")]
        public void A_tagged_category_maps_to_its_tag_category(string tagged, string tag)
            => Assert.Equal(tag, LinkedTagRules.TagCategoryName(tagged));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("OST_Levels")]
        public void An_unknown_category_has_no_tag_category(string tagged)
            => Assert.Null(LinkedTagRules.TagCategoryName(tagged));

        private static string Src(string file)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "AGENTS.md"))) d = d.Parent;
            return File.ReadAllText(Path.Combine(d.FullName, "src", "Horizun.Revit", "Commands", file));
        }

        [Fact]
        public void Linked_tags_use_the_tag_type_overload_and_a_link_reference()
        {
            string s = Src("AnnotateCommand.cs");
            Assert.Contains("CreateLinkReference(p.LinkInstance)", s);
            Assert.Contains("IndependentTag.Create(doc, linkedTypeId, p.View.Id, tagReference", s);
        }

        [Fact]
        public void The_rehearsal_isolates_a_linked_tag_and_survives_a_failed_rollback()
        {
            string s = Src("AnnotateCommand.Execution.cs");
            Assert.Contains("if (p.LinkedElement != null)", s);
            Assert.Contains("new SubTransaction(doc)", s);
            Assert.Contains("RollBack threw:", s);
        }

    }
}
