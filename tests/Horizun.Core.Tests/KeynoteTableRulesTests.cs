// Horizun MCP — original Horizun code.
// The keynote file diagnosis that gates horizun_manage_classification_table set_path.
using System.Text;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class KeynoteTableRulesTests
    {
        private static byte[] U16(string s) => new UnicodeEncoding(false, true).GetPreamble().Concat(Encoding.Unicode.GetBytes(s));

        [Fact]
        public void SoundUtf16FileIsOk()
        {
            var d = KeynoteTableRules.Diagnose(U16("22\tElementos\r\n22.03\tConcreto\t22\r\n22.03.30-001\tMuro\t22.03\r\n"));
            Assert.True(d.Ok);
            Assert.Equal("utf-16le", d.Encoding);
            Assert.Equal(3, d.ByKey.Count);
        }

        [Fact]
        public void IdenticalDuplicateWarnsConflictingDuplicateFails()
        {
            var ok = KeynoteTableRules.Diagnose(U16("22\tA\r\n22.1\tB\t22\r\n22.1\tB\t22\r\n"));
            Assert.True(ok.Ok);
            Assert.Equal(1, ok.DuplicateIdentical);
            var bad = KeynoteTableRules.Diagnose(U16("22\tA\r\n22.1\tB\t22\r\n22.1\tOtra\t22\r\n"));
            Assert.False(bad.Ok);
            Assert.Equal(1, bad.DuplicateConflicting);
        }

        [Fact]
        public void MissingParentAndCycleFailParentAfterChildWarns()
        {
            Assert.Equal(1, KeynoteTableRules.Diagnose(U16("22.1\tB\t99\r\n")).MissingParents);
            var cyc = KeynoteTableRules.Diagnose(U16("A\ta\tB\r\nB\tb\tA\r\n"));
            Assert.False(cyc.Ok);
            Assert.True(cyc.Cycles > 0);
            var late = KeynoteTableRules.Diagnose(U16("22.1\tB\t22\r\n22\tA\r\n"));
            Assert.True(late.Ok);
            Assert.Equal(1, late.ParentAfterChild);
        }

        [Fact]
        public void Utf16WithoutBomIsUnreadable()
        {
            var d = KeynoteTableRules.Diagnose(Encoding.Unicode.GetBytes("22\tA\r\n"));
            Assert.False(d.Ok);
            Assert.False(d.Readable);
        }
    }

    internal static class ByteConcat
    {
        public static byte[] Concat(this byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            a.CopyTo(r, 0); b.CopyTo(r, a.Length);
            return r;
        }
    }
}
