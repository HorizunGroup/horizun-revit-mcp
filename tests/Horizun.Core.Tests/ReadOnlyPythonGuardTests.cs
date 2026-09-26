// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// horizun_execute_python read_only=true: the static scan that refuses a script
// BEFORE it runs when it mentions a call that escapes a TransactionGroup
// rollback, and the pure comparison that decides whether the model actually
// held still afterwards. Both are Revit-free (see Core/ReadOnlyPythonGuard.cs).
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReadOnlyPythonGuardTests
    {
        [Theory]
        [InlineData("doc.Save()", "Document.Save()")]
        [InlineData("doc.SynchronizeWithCentral(t, s)", "Document.SynchronizeWithCentral()")]
        [InlineData("WorksharingUtils.RelinquishOwnership(doc, r, o)", "WorksharingUtils.RelinquishOwnership()")]
        [InlineData("link_type.Unload(None)", "RevitLinkType/CADLinkType load state")]
        [InlineData("doc.SaveAs(path)", "Document.SaveAs()")]
        [InlineData("doc.Close(False)", "Document.Close()")]
        [InlineData("app.OpenDocumentFile(mp)", "Application.OpenDocumentFile()")]
        [InlineData("uiapp.OpenAndActivateDocument(path)", "UIApplication.OpenAndActivateDocument()")]
        [InlineData("app.NewProjectDocument(template)", "Application.NewProjectDocument()")]
        public void Flags_each_forbidden_api_by_its_Revit_name(string code, string expectedApi)
        {
            List<string> hits = ReadOnlyPythonGuard.Violations(code);
            Assert.Contains(expectedApi, hits);
        }

        [Fact]
        public void An_ordinary_read_and_write_script_is_clean()
        {
            string code = "t = Transaction(doc, 'x')\nt.Start()\nwall = Wall.Create(doc, curve, level.Id, False)\nt.Commit()\n";
            Assert.Empty(ReadOnlyPythonGuard.Violations(code));
        }

        [Fact]
        public void A_mention_inside_a_comment_or_string_does_not_trip_the_guard_once_masked()
        {
            // The command masks comments/strings before calling this (PythonTokenMask /
            // PythonSourceMask, the same technique TypedOverlaps uses) - so what THIS
            // function receives for a harmless script is already blanked. Simulate that.
            string raw = "# doc.Save() is mentioned only in this comment\nprint('doc.Save() in a string too')\n";
            string masked = PythonSourceMask.StripCommentsAndStrings(raw);
            Assert.Empty(ReadOnlyPythonGuard.Violations(masked));
        }

        [Fact]
        public void Save_and_SaveAs_are_both_reported_when_both_appear()
        {
            List<string> hits = ReadOnlyPythonGuard.Violations("doc.SaveAs(p)\ndoc.Save()\n");
            Assert.Contains("Document.SaveAs()", hits);
            Assert.Contains("Document.Save()", hits);
        }

        [Fact]
        public void Null_or_empty_source_has_no_violations()
        {
            Assert.Empty(ReadOnlyPythonGuard.Violations(null));
            Assert.Empty(ReadOnlyPythonGuard.Violations(""));
        }

        [Fact]
        public void Compare_census_passes_when_everything_before_and_after_agrees()
        {
            ReadOnlyOutcome outcome = ReadOnlyPythonGuard.CompareCensus(false, false, 100, 100, 20, 20);
            Assert.True(outcome.Ok);
        }

        [Fact]
        public void Compare_census_fails_when_IsModified_flipped()
        {
            ReadOnlyOutcome outcome = ReadOnlyPythonGuard.CompareCensus(false, true, 100, 100, 20, 20);
            Assert.False(outcome.Ok);
            Assert.Contains("IsModified", outcome.Reason);
        }

        [Fact]
        public void Compare_census_fails_when_instance_count_changed()
        {
            ReadOnlyOutcome outcome = ReadOnlyPythonGuard.CompareCensus(false, false, 100, 101, 20, 20);
            Assert.False(outcome.Ok);
            Assert.Contains("census changed", outcome.Reason);
        }

        [Fact]
        public void Compare_census_fails_when_type_count_changed()
        {
            ReadOnlyOutcome outcome = ReadOnlyPythonGuard.CompareCensus(false, false, 100, 100, 20, 21);
            Assert.False(outcome.Ok);
            Assert.Contains("census changed", outcome.Reason);
        }

        [Fact]
        public void An_unreadable_measurement_is_reported_as_unmeasured_not_as_unchanged()
        {
            ReadOnlyOutcome outcome = ReadOnlyPythonGuard.CompareCensus(null, false, 100, 100, 20, 20);
            Assert.False(outcome.Ok);
            Assert.Contains("UNMEASURED", outcome.Reason);
        }

        [Fact]
        public void An_unreadable_census_side_is_unmeasured_not_unchanged()
        {
            ReadOnlyOutcome outcome = ReadOnlyPythonGuard.CompareCensus(false, false, 100, null, 20, 20);
            Assert.False(outcome.Ok);
            Assert.Contains("UNMEASURED", outcome.Reason);
        }
    }
}
