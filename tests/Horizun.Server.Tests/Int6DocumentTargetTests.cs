// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// INT-6 m-10: a document target is followed by its EXACT name (or the file name of a full path). A fragment may choose a document
// when it is first pinned, and only when it is unique; it never moves a pinned document, and a read is never sent, without a word,
// to another model: "Torre A" is not "Torre A - Estructura".
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class Int6DocumentTargetTests
    {
        private static DocumentTarget.Live L(int pid, string year, string doc) =>
            new DocumentTarget.Live { Pid = pid, Year = year, ActiveDocument = doc };

        [Fact]
        public void Torre_A_is_not_Torre_A_Estructura_when_following_a_pinned_document()
        {
            var t = TargetSelection.ByDocument(5, "Torre A");
            // The pinned instance now shows another model; the only other one shows "Torre A - Estructura".
            TargetSelection r = DocumentTarget.Refresh(t, () => new List<DocumentTarget.Live>
                { L(5, "2026", "Torre A - Arquitectura.rvt"), L(6, "2024", "Torre A - Estructura.rvt") });
            Assert.NotEqual(6, r.Pid);          // never moved to a model it was not asked for
            Assert.Equal(5, r.Pid);
        }

        [Fact]
        public void A_pinned_instance_that_shows_another_model_is_unresolved_not_routed_there_silently()
        {
            var t = TargetSelection.ByDocument(5, "Torre A");
            TargetSelection r = DocumentTarget.Refresh(t, () => new List<DocumentTarget.Live>
                { L(5, "2026", "Torre A - Estructura.rvt"), L(6, "2024", "Otro.rvt") });
            Assert.NotSame(t, r);
            Assert.NotNull(r.Unresolved);
            Assert.Contains("Torre A - Estructura", r.Unresolved);
            Assert.Contains("Nothing was sent", r.Unresolved);
            Assert.Null(t.Unresolved);
        }

        [Fact]
        public void Two_instances_showing_the_exact_name_while_the_pinned_one_shows_another_is_unresolved_and_names_them()
        {
            var t = TargetSelection.ByDocument(5, "Torre A");
            TargetSelection r = DocumentTarget.Refresh(t, () => new List<DocumentTarget.Live>
                { L(5, "2026", "Otro.rvt"), L(6, "2026", "Torre A.rvt"), L(7, "2026", "Torre A.rvt") });
            Assert.NotNull(r.Unresolved);
            Assert.Contains("pid 6", r.Unresolved);
            Assert.Contains("pid 7", r.Unresolved);
        }

        [Fact]
        public void The_pinned_document_moves_to_the_one_instance_that_shows_it_by_exact_name()
        {
            var t = TargetSelection.ByDocument(5, "Torre A", "origin");
            TargetSelection r = DocumentTarget.Refresh(t, () => new List<DocumentTarget.Live>
                { L(5, "2026", "Otro.rvt"), L(9, "2026", "torre a.rvt"), L(6, "2024", "Torre A - Estructura.rvt") });
            Assert.Equal(9, r.Pid);
            Assert.Null(r.Unresolved);
            Assert.Equal("origin", r.Origin);
        }

        [Fact]
        public void A_pinned_process_that_is_gone_keeps_the_pin_so_the_call_says_there_is_no_bridge()
        {
            var t = TargetSelection.ByDocument(5, "Torre A");
            Assert.Same(t, DocumentTarget.Refresh(t, () => new List<DocumentTarget.Live> { L(6, "2024", "Torre A - Estructura.rvt") }));
        }

        [Fact]
        public void A_unique_fragment_still_chooses_a_document_when_it_is_first_pinned_and_says_it_was_a_fragment()
        {
            var live = new[] { L(1, "2026", "Torre A - Estructura.rvt"), L(2, "2024", "Otro.rvt") };
            DocumentTarget.Outcome o = DocumentTarget.Match("Estructura", live);
            Assert.Equal(1, o.Chosen.Pid);
            Assert.True(o.ByFragment);
            Assert.False(DocumentTarget.Match("Otro", live).ByFragment);
        }

        [Fact]
        public void An_ambiguous_fragment_is_a_refusal_listing_both()
        {
            var live = new[] { L(1, "2026", "Torre A - Estructura.rvt"), L(2, "2024", "Torre A - Arquitectura.rvt") };
            DocumentTarget.Outcome o = DocumentTarget.Match("Torre A", live);
            Assert.Null(o.Chosen);
            Assert.Contains("pid 1", o.Refusal);
            Assert.Contains("pid 2", o.Refusal);
        }

        [Fact]
        public void Without_fragments_only_the_exact_name_matches()
        {
            var live = new[] { L(1, "2026", "Torre A - Estructura.rvt") };
            Assert.Null(DocumentTarget.Match("Torre A", live, allowFragment: false).Chosen);
            Assert.Equal(1, DocumentTarget.Match("Torre A - Estructura", live, allowFragment: false).Chosen.Pid);
        }

        [Theory]
        [InlineData(@"D:\Proyectos\Torre A.rvt")]
        [InlineData("D:/Proyectos/Torre A.rvt")]
        [InlineData(@"\\servidor\carpeta\Torre A")]
        public void A_full_path_names_its_file_and_matches_that_name_exactly(string path)
        {
            var live = new[] { L(1, "2026", "Torre A - Estructura.rvt"), L(2, "2024", "Torre A.rvt") };
            Assert.Equal(2, DocumentTarget.Match(path, live, allowFragment: false).Chosen.Pid);
            Assert.Equal(2, DocumentTarget.Match(path, live).Chosen.Pid);
            Assert.StartsWith("Torre A", DocumentTarget.NameOf(path));
        }

        [Fact]
        public void An_unresolved_target_is_refused_for_every_tool_with_the_ambiguity_code_and_is_never_stored()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-int6-target-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            PipeClient.DirectoryOverride = dir;
            PipeClient.LivenessProbe = pid => false;
            try
            {
                TargetSelection unresolved = TargetSelection.ByDocument(5, "Torre A").AsUnresolved("pinned to 5, which shows another model. Nothing was sent.");
                RoutingDecision decision = InstanceRouting.Decide(unresolved, null);
                Assert.Null(decision.Instance);
                Assert.True(decision.Ambiguous);
                Assert.Contains("Nothing was sent", decision.Refusal);
                Assert.NotNull(decision.Candidates);
            }
            finally
            {
                PipeClient.DirectoryOverride = null;
                PipeClient.LivenessProbe = null;
                // INT-7 m-i: this test's own folder, and only it.
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void The_session_keeps_an_answer_that_moved_the_pin_and_never_an_unresolved_one()
        {
            // INT-7 m-j: the rule the getter applies (PipeClient.Target), held by behaviour and not by a search in the source.
            var pinned = TargetSelection.ByDocument(5, "Torre A");
            TargetSelection unresolved = DocumentTarget.Refresh(pinned, () => new List<DocumentTarget.Live> { L(5, "2026", "Otro.rvt") });
            Assert.NotNull(unresolved.Unresolved);
            Assert.False(DocumentTarget.ShouldStore(pinned, unresolved));

            TargetSelection moved = DocumentTarget.Refresh(pinned, () => new List<DocumentTarget.Live> { L(5, "2026", "Otro.rvt"), L(9, "2026", "Torre A.rvt") });
            Assert.Equal(9, moved.Pid);
            Assert.True(DocumentTarget.ShouldStore(pinned, moved));

            Assert.False(DocumentTarget.ShouldStore(pinned, pinned));      // nothing moved: nothing to store
        }
    }
}
