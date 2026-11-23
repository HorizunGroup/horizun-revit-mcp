using System;
using System.IO;
using Horizun.Server;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class TargetMemoryDocumentTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hz-targetdoc-" + Guid.NewGuid().ToString("N"));
        private readonly string _oldKey = Environment.GetEnvironmentVariable(TargetMemory.SessionVariable);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(TargetMemory.SessionVariable, _oldKey);
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void A_document_target_survives_the_process_with_its_document()
        {
            Environment.SetEnvironmentVariable(TargetMemory.SessionVariable, "n1-doc");
            TargetMemory.Remember(TargetSelection.ByDocument(7, "Torre A"), _root);
            TargetSelection back = TargetMemory.Read("n1-doc", _root, out string why);
            Assert.Null(why);
            Assert.Equal("Torre A", back.Document);
            Assert.Equal(7, back.Pid);
        }
    }
}
