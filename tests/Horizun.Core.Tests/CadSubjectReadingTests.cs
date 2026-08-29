// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE AUDIT AND THE UPDATE ASK THE MODEL THE SAME QUESTION.
//
// Both need to know what an element is and where it sits, and both had their own
// copy of the reading. The copies diverged in the direction that hurts: the
// update's copy never read the element's TYPE, so the classification that
// compares the drawing's requested type against the element's own could not fire
// through the command that needs it. It fired perfectly in unit tests, because
// unit tests build subjects by hand - which is exactly the shape of bug that
// survives a green suite.
//
// The reading now lives in one file. These tests cannot open a Revit document,
// so they pin the thing that can be pinned without one: that there is exactly
// one reading, and that both commands use it.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadSubjectReadingTests
    {
        private static string Source(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Horizun.Revit")))
                dir = dir.Parent;
            Assert.True(dir != null, "the repository root must be findable from the test binary");
            string path = Path.Combine(new[] { dir.FullName, "src", "Horizun.Revit" }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), path + " must exist");
            return File.ReadAllText(path);
        }

        [Fact]
        public void Both_commands_measure_a_subject_through_the_one_reader()
        {
            foreach (string file in new[] { "AuditCadModelCommand.cs", "PlanCadUpdateCommand.cs" })
            {
                string source = Source("Commands", file);
                Assert.True(source.Contains("CadSubjectReader.Measure(e"),
                            file + " must build its subjects through CadSubjectReader");
            }
        }

        [Fact]
        public void Neither_command_keeps_a_second_copy_of_the_reading()
        {
            // A copy is how the two diverged. The tell is a command constructing
            // a CadAuditSubject and then filling its geometry itself.
            foreach (string file in new[] { "AuditCadModelCommand.cs", "PlanCadUpdateCommand.cs" })
            {
                string source = Source("Commands", file);
                Assert.False(Regex.IsMatch(source, @"s\.Geometry\.Add\("),
                             file + " fills a subject's geometry itself - that is the second copy this " +
                             "file exists to prevent");
                Assert.False(source.Contains("e.Location as LocationCurve"),
                             file + " reads an element's location itself rather than through CadSubjectReader");
            }
        }

        [Fact]
        public void The_reader_answers_every_fact_a_classification_depends_on()
        {
            // Each of these is the sole input to a classification the update can
            // emit. A reader that stops answering one of them turns that
            // classification off silently - which is what happened to the type.
            string reader = Source("Commands", "CadSubjectReader.cs");
            foreach (string fact in new[] { "s.TypeName", "s.WidthMm", "s.HostElementId",
                                            "s.ArcCentre", "s.LevelName", "s.Category" })
                Assert.True(reader.Contains(fact), "CadSubjectReader must read " + fact);
        }

        [Fact]
        public void A_width_that_cannot_be_measured_stays_NULL_rather_than_becoming_zero()
        {
            // "Not comparable" and "the wrong thickness" are different findings.
            // Returning 0 would put a resize on every element nobody can measure.
            string reader = Source("Commands", "CadSubjectReader.cs");
            Assert.Contains("private static double? WidthOf(Element e)", reader);
            Assert.DoesNotContain("return 0;", reader);
        }
    }
}
