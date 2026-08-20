// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// The contract has to WARN about the message it is correcting (5.26).
//
// horizun_file_info now returns a file's first eight bytes when BasicFileInfo
// cannot read its header. That is only worth anything if the caller is told to
// read them BEFORE repeating Revit's own explanation - which names two causes,
// both about Revit files, and was believed twice about a pair of files that were
// ZIPs. The description is the only place a client agent learns that, so its
// absence is a failing test rather than a missed opportunity.
// -----------------------------------------------------------------------------
using Horizun.Contracts;
using Xunit;

namespace Horizun.Server.Tests
{
    public class FileInfoSignatureContractTests
    {
        private static string Description() => Contract.Find("horizun_file_info").Description;

        [Fact]
        public void It_names_the_field_and_the_two_signatures_that_decide()
        {
            string d = Description();

            Assert.Contains("signature", d);
            Assert.Contains("is_revit_container", d);
            // The two that carry the whole diagnosis.
            Assert.Contains("d0cf11e0a1b11ae1", d);
            Assert.Contains("504b0304", d);
        }

        [Fact]
        public void It_says_not_to_believe_the_read_error_on_its_own()
        {
            string d = Description();

            Assert.Contains("before repeating the read_error", d);
            Assert.Contains("NOT a model", d);
            // And it names the measured cost, so the warning reads as a finding rather
            // than as caution in general.
            Assert.Contains("1,193", d);
        }

        [Fact]
        public void The_summary_counts_files_that_are_not_models_at_all()
        {
            // Separate from `unreadable`, because they are a different finding: nothing
            // about a renamed ZIP is a Revit file that could not be read.
            Assert.Contains("not_revit_files", Description());
        }
    }
}
