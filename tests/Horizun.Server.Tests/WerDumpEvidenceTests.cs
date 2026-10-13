using System;
using System.IO;
using Horizun.Server;
using Xunit;

public sealed class WerDumpEvidenceTests
{
    [Fact]
    public void IgnoresDumpFromEarlierUseOfTheSamePid()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hz-wer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "Revit.exe.1234.dmp");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            File.SetLastWriteTime(path, new DateTime(2026, 1, 1));
            Assert.False(WerDumpEvidence.Find(dir, 1234, new DateTime(2026, 2, 1), new DateTime(2026, 2, 1)).Found);
            File.SetLastWriteTime(path, new DateTime(2026, 2, 1, 0, 1, 0));
            var result = WerDumpEvidence.Find(dir, 1234, new DateTime(2026, 2, 1), new DateTime(2026, 2, 1, 0, 1, 10));
            Assert.True(result.Found);
            Assert.True(result.RecentWrite);
            Assert.Equal(3, result.Bytes);
        }
        finally { Directory.Delete(dir, true); }
    }
}
