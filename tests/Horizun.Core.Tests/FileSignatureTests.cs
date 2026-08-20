// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The eight bytes that end an argument (5.26).
//
// The case behind every test here: two files of 337,648,956 and 337,661,244 bytes
// that BasicFileInfo refused, with a message naming only Revit-file causes. They
// were diagnosed as "a newer Revit version" (written into a client's documentation
// and believed for six days) and then as "a corrupt download" (they re-downloaded
// byte-identical). Both wrong. They were ZIPs.
//
// So the properties worth pinning are about what this REFUSES to say as much as
// what it says: a ZIP must not be reported as a Revit file, and an unrecognised
// signature must not be reported as anything at all.
// -----------------------------------------------------------------------------
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class FileSignatureTests
    {
        private static byte[] Head(params int[] bytes)
        {
            var b = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) b[i] = (byte)bytes[i];
            return b;
        }

        [Fact]
        public void The_measured_file_reads_as_a_zip_and_is_named_as_not_a_model()
        {
            // 50 4b 03 04 - the bytes read by hand on 2026-08-13 that ended the argument.
            string hex = FileSignature.ToHex(Head(0x50, 0x4b, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00));

            Assert.Equal("504b0304", hex.Substring(0, 8));
            Assert.Contains("ZIP", FileSignature.Describe(hex));
            Assert.Contains("NOT a Revit model", FileSignature.Describe(hex));
            // The whole point: this must not be mistakeable for a version problem.
            Assert.False(FileSignature.LooksLikeRevit(hex));
        }

        [Fact]
        public void An_ole_header_is_the_one_case_where_the_version_story_is_worth_believing()
        {
            string hex = FileSignature.ToHex(Head(0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1));

            Assert.Equal(FileSignature.Ole, hex);
            Assert.True(FileSignature.LooksLikeRevit(hex));
            string means = FileSignature.Describe(hex);
            Assert.Contains("OLE compound file", means);
            // It says the CONTAINER is Revit's - never that the file is fine.
            Assert.Contains("could not parse", means);
        }

        [Fact]
        public void An_unrecognised_signature_is_reported_and_not_interpreted()
        {
            string hex = FileSignature.ToHex(Head(0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a)); // PNG

            Assert.Equal("89504e470d0a1a0a", hex);
            string means = FileSignature.Describe(hex);
            Assert.Contains("Unrecognised", means);
            Assert.Contains("Nothing is claimed", means);
            Assert.False(FileSignature.LooksLikeRevit(hex));
        }

        [Fact]
        public void Only_the_first_eight_bytes_are_reported_however_many_are_offered()
        {
            byte[] more = Head(0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1, 0xff, 0xff, 0xff);

            Assert.Equal(16, FileSignature.ToHex(more).Length);
        }

        [Fact]
        public void A_file_too_short_to_have_a_header_says_so_instead_of_guessing()
        {
            string hex = FileSignature.ToHex(Head(0x50, 0x4b));

            Assert.Equal("504b", hex);
            string means = FileSignature.Describe(hex);
            // "PK" alone is not the ZIP local-file-header magic and is not claimed to be.
            Assert.Contains("2 byte(s) could be read", means);
            Assert.Contains("Unrecognised", means);
        }

        [Fact]
        public void Nothing_read_answers_null_which_is_not_the_same_as_meaningless_bytes()
        {
            Assert.Null(FileSignature.ToHex(null));
            Assert.Null(FileSignature.ToHex(new byte[0]));
            Assert.Null(FileSignature.Describe(null));
            Assert.Null(FileSignature.Describe(""));
            Assert.False(FileSignature.LooksLikeRevit(null));
        }

        [Fact]
        public void The_empty_and_spanned_zip_headers_are_named_too()
        {
            Assert.Contains("empty archive", FileSignature.Describe("504b050600000000"));
            Assert.Contains("spanned", FileSignature.Describe("504b070800000000"));
        }

        [Fact]
        public void Case_does_not_decide_the_answer()
        {
            Assert.True(FileSignature.LooksLikeRevit("D0CF11E0A1B11AE1"));
            Assert.Contains("OLE compound file", FileSignature.Describe("D0CF11E0A1B11AE1"));
        }
    }
}
