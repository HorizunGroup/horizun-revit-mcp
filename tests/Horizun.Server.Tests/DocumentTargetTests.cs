// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// CT-P2-1a: the Revit is chosen by the model it holds, and the choice follows the model
// when the process id behind it changes. Only the pure half is proved here (Match, Refresh
// over supplied instances); reading window titles is the thin edge.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Server;
using Xunit;

namespace Horizun.Server.Tests
{
    public class DocumentTargetTests
    {
        private static DocumentTarget.Live L(int pid, string year, string doc) =>
            new DocumentTarget.Live { Pid = pid, Year = year, ActiveDocument = doc };

        [Theory]
        [InlineData("Torre A", "Torre A")]
        [InlineData("torre a.rvt", "Torre A")]
        [InlineData("Torre A.RVT", "torre a")]
        public void The_name_matches_without_extension_or_case(string asked, string shown)
        {
            var o = DocumentTarget.Match(asked, new[] { L(10, "2026", shown + ".rvt"), L(11, "2024", "Otro.rvt") });
            Assert.Equal(10, o.Chosen.Pid);
            Assert.Null(o.Refusal);
        }

        [Fact]
        public void A_unique_fragment_is_enough_and_an_exact_name_beats_a_fragment()
        {
            var live = new[] { L(1, "2026", "Torre A - estructura.rvt"), L(2, "2024", "Torre A.rvt") };
            Assert.Equal(2, DocumentTarget.Match("Torre A", live).Chosen.Pid);
            Assert.Equal(1, DocumentTarget.Match("estructura", live).Chosen.Pid);
        }

        [Fact]
        public void Two_instances_showing_the_model_is_a_refusal_naming_both()
        {
            var o = DocumentTarget.Match("Torre A", new[] { L(1, "2026", "Torre A.rvt"), L(2, "2026", "Torre A.rvt") });
            Assert.Null(o.Chosen);
            Assert.Contains("pid 1", o.Refusal);
            Assert.Contains("pid 2", o.Refusal);
        }

        [Fact]
        public void No_instance_showing_it_is_a_refusal_that_lists_what_each_one_shows()
        {
            var o = DocumentTarget.Match("Torre Z", new[] { L(1, "2026", "Torre A.rvt"), L(2, "2024", null) });
            Assert.Null(o.Chosen);
            Assert.Contains("Torre A", o.Refusal);
            Assert.Contains("unreadable", o.Refusal);
        }

        [Fact]
        public void A_blank_name_is_refused()
        {
            Assert.NotNull(DocumentTarget.Match("  ", new[] { L(1, "2026", "A.rvt") }).Refusal);
        }

        [Fact]
        public void Refresh_keeps_the_pin_while_its_instance_still_shows_the_model_or_cannot_be_read()
        {
            var t = TargetSelection.ByDocument(5, "Torre A");
            Assert.Same(t, DocumentTarget.Refresh(t, () => new List<DocumentTarget.Live> { L(5, "2026", "Torre A.rvt"), L(6, "2026", "Torre A.rvt") }));
            Assert.Same(t, DocumentTarget.Refresh(t, () => new List<DocumentTarget.Live> { L(5, "2026", null), L(6, "2026", "Torre A.rvt") }));
        }

        [Fact]
        public void Refresh_follows_the_model_to_the_one_instance_that_now_shows_it()
        {
            var t = TargetSelection.ByDocument(5, "Torre A", "origin");
            var moved = DocumentTarget.Refresh(t, () => new List<DocumentTarget.Live> { L(9, "2026", "Torre A.rvt"), L(6, "2024", "Otro.rvt") });
            Assert.Equal(9, moved.Pid);
            Assert.Equal("Torre A", moved.Document);
            Assert.Equal("origin", moved.Origin);
        }

        [Fact]
        public void Refresh_does_not_guess_when_zero_or_two_instances_show_the_model()
        {
            var t = TargetSelection.ByDocument(5, "Torre A");
            Assert.Same(t, DocumentTarget.Refresh(t, () => new List<DocumentTarget.Live> { L(6, "2024", "Otro.rvt") }));
            Assert.Same(t, DocumentTarget.Refresh(t, () => new List<DocumentTarget.Live> { L(7, "2026", "Torre A.rvt"), L(8, "2026", "Torre A.rvt") }));
        }

        [Fact]
        public void A_plain_pid_target_is_never_touched_by_refresh()
        {
            var t = TargetSelection.ByPid(5);
            Assert.Same(t, DocumentTarget.Refresh(t, () => throw new System.InvalidOperationException("must not read")));
        }

        [Fact]
        public void The_answer_names_the_document_as_the_reason()
        {
            var t = TargetSelection.ByDocument(5, "Torre A");
            Assert.Contains("Torre A", t.Describe(""));
            var d = new Discovered { Pid = 5, Year = "2026" };
            Assert.Contains("document 'Torre A'", (string)InstanceRouting.AnsweredBy(d, t, "", 2)["selected_by"]);
        }
    }
}
