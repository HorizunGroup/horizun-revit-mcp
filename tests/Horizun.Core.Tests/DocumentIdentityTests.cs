// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Which document is it, and what happens when that cannot be answered.
//
// The bug these come from was measured on a real model: horizun_health named an
// active document and simultaneously marked all three open documents
// is_active=false, because it compared Revit wrappers with ReferenceEquals.
//
// The harder case is the one with no bug to point at: two open documents that
// share a title. Nothing available distinguishes them, and the only honest answer
// is "unknown". These tests exist to keep that answer from being replaced by a
// convenient guess.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DocumentIdentityTests
    {
        private static DocIdentity Doc(string title, string path = null, string guid = null) =>
            new DocIdentity { Title = title, Path = path, ModelGuid = guid };

        [Fact]
        public void The_active_document_is_found_among_the_open_ones()
        {
            // THE REGRESSION: this is the shape that reported "none of these is active".
            var open = new List<DocIdentity>
            {
                Doc("MOD_ARCH_A",  "Autodesk Docs://Sample Project/MOD_ARCH_A.rvt"),
                Doc("MOD_ARCH_SITE","Autodesk Docs://Sample Project/MOD_ARCH_SITE.rvt"),
                Doc("MOD_STRC-REF_A","Autodesk Docs://Sample Project/MOD_STRC-REF_A.rvt")
            };

            var m = DocumentMatcher.Find(open, Doc("MOD_ARCH_A", "Autodesk Docs://Sample Project/MOD_ARCH_A.rvt"));

            Assert.Equal(DocMatchOutcome.Matched, m.Outcome);
            Assert.Equal(0, m.Index);
            Assert.Equal("path", m.Basis);
            Assert.True(m.IsMatch(0));
            Assert.False(m.IsMatch(1));
        }

        [Fact]
        public void Two_documents_with_the_same_title_are_unknown_not_guessed()
        {
            var open = new List<DocIdentity> { Doc("TOWER-A"), Doc("TOWER-A") };

            var m = DocumentMatcher.Find(open, Doc("TOWER-A"));

            Assert.Equal(DocMatchOutcome.Ambiguous, m.Outcome);
            Assert.Null(m.IsMatch(0));      // not true, and not false
            Assert.Null(m.IsMatch(1));
            Assert.Equal(-1, m.Index);
            Assert.Contains("cannot be determined", m.Explain());
        }

        [Fact]
        public void Strong_identity_never_accepts_homonymous_unsaved_or_detached_documents()
        {
            Assert.False(DocumentMatcher.SameStableIdentity(Doc("TOWER-A"), Doc("TOWER-A")));
            Assert.False(DocumentMatcher.SameStableIdentity(
                Doc("TOWER-A_detached", ""), Doc("TOWER-A_detached", "")));
        }

        [Fact]
        public void Strong_cloud_identity_requires_both_project_and_model_guids()
        {
            DocIdentity a = Doc("A", null, "22222222-2222-2222-2222-222222222222");
            DocIdentity b = Doc("A", null, "22222222-2222-2222-2222-222222222222");
            Assert.True(DocumentMatcher.SameStableIdentity(
                a, b, "11111111-1111-1111-1111-111111111111", "11111111-1111-1111-1111-111111111111"));
            Assert.False(DocumentMatcher.SameStableIdentity(
                a, b, "11111111-1111-1111-1111-111111111111", "99999999-9999-9999-9999-999999999999"));
            Assert.False(DocumentMatcher.SameStableIdentity(a, b));
        }

        [Fact]
        public void Strong_local_identity_is_path_based_not_title_based()
        {
            Assert.True(DocumentMatcher.SameStableIdentity(
                Doc("same", @"C:\North\A.rvt"), Doc("different", "c:/north/A.RVT")));
            Assert.False(DocumentMatcher.SameStableIdentity(
                Doc("same", @"C:\North\A.rvt"), Doc("same", @"C:\South\A.rvt")));
        }

        [Fact]
        public void The_same_title_in_different_folders_is_settled_by_path()
        {
            var open = new List<DocIdentity>
            {
                Doc("TOWER-A", @"C:\proj\north\TOWER-A.rvt"),
                Doc("TOWER-A", @"C:\proj\south\TOWER-A.rvt")
            };

            var m = DocumentMatcher.Find(open, Doc("TOWER-A", @"C:\proj\south\TOWER-A.rvt"));

            Assert.Equal(DocMatchOutcome.Matched, m.Outcome);
            Assert.Equal(1, m.Index);
            Assert.Equal("path", m.Basis);
        }

        [Fact]
        public void A_model_guid_outranks_a_shared_title_and_a_shared_path()
        {
            var open = new List<DocIdentity>
            {
                Doc("TOWER-A", "cloud://x/TOWER-A.rvt", "11111111-1111-1111-1111-111111111111"),
                Doc("TOWER-A", "cloud://x/TOWER-A.rvt", "22222222-2222-2222-2222-222222222222")
            };

            var m = DocumentMatcher.Find(open, Doc("TOWER-A", "cloud://x/TOWER-A.rvt", "22222222-2222-2222-2222-222222222222"));

            Assert.Equal(DocMatchOutcome.Matched, m.Outcome);
            Assert.Equal(1, m.Index);
            Assert.Equal("model guid", m.Basis);
        }

        [Fact]
        public void Path_comparison_survives_separator_and_casing_differences()
        {
            var open = new List<DocIdentity> { Doc("A", @"C:\Proj\Tower\A.rvt") };

            var m = DocumentMatcher.Find(open, Doc("A", "c:/proj/tower/A.RVT"));

            Assert.Equal(DocMatchOutcome.Matched, m.Outcome);
            Assert.Equal("path", m.Basis);
        }

        [Fact]
        public void An_unsaved_document_has_no_path_and_falls_back_to_title()
        {
            var open = new List<DocIdentity> { Doc("Project1"), Doc("Other", @"C:\x\Other.rvt") };

            var m = DocumentMatcher.Find(open, Doc("Project1"));

            Assert.Equal(DocMatchOutcome.Matched, m.Outcome);
            Assert.Equal(0, m.Index);
            Assert.Equal("title", m.Basis);
        }

        [Fact]
        public void An_ambiguous_stronger_tier_does_not_fall_through_to_a_weaker_one()
        {
            // Two documents share the path. Falling through to title could "resolve" it by
            // accident; a weaker tier cannot make an uncertain answer certain.
            var open = new List<DocIdentity>
            {
                Doc("A", @"C:\x\same.rvt"),
                Doc("B", @"C:\x\same.rvt")
            };

            var m = DocumentMatcher.Find(open, Doc("B", @"C:\x\same.rvt"));

            Assert.Equal(DocMatchOutcome.Ambiguous, m.Outcome);
        }

        [Fact]
        public void A_document_that_is_not_open_matches_nothing()
        {
            var open = new List<DocIdentity> { Doc("A", @"C:\x\A.rvt") };

            var m = DocumentMatcher.Find(open, Doc("B", @"C:\x\B.rvt"));

            Assert.Equal(DocMatchOutcome.None, m.Outcome);
            Assert.False(m.IsMatch(0));
            Assert.Contains("No open document", m.Explain());
        }

        [Fact]
        public void An_empty_or_null_candidate_list_is_no_match_not_a_crash()
        {
            Assert.Equal(DocMatchOutcome.None, DocumentMatcher.Find(new List<DocIdentity>(), Doc("A")).Outcome);
            Assert.Equal(DocMatchOutcome.None, DocumentMatcher.Find(null, Doc("A")).Outcome);
            Assert.Equal(DocMatchOutcome.None, DocumentMatcher.Find(new List<DocIdentity> { Doc("A") }, null).Outcome);
        }

        [Fact]
        public void A_blank_path_is_normalized_to_null_so_it_never_matches_another_blank()
        {
            // Two unsaved documents both have "" for a path. If blank matched blank, every
            // pair of unsaved documents would look like the same document.
            Assert.Null(DocIdentity.NormalizePath(""));
            Assert.Null(DocIdentity.NormalizePath("   "));
            Assert.Null(DocIdentity.NormalizePath(null));

            var open = new List<DocIdentity> { Doc("A", ""), Doc("B", "") };
            var m = DocumentMatcher.Find(open, Doc("B", ""));

            Assert.Equal(DocMatchOutcome.Matched, m.Outcome);   // settled by title, not by empty path
            Assert.Equal("title", m.Basis);
        }
    }
}
