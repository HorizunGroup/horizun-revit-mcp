using Horizun.Server;
using Xunit;

namespace Horizun.Server.Tests
{
    public class StaleServerTests
    {
        [Fact]
        public void Older_process_gets_restart_instruction_instead_of_redeploy()
        {
            var result = StaleServer.Diagnose("4.1.0-rc2+aaaa", "4.2.0+bbbb");
            Assert.True((bool)result["stale_server_process"]);
            Assert.Equal("4.1.0-rc2", (string)result["server_version"]);
            Assert.Equal("4.2.0", (string)result["installed_version"]);
            Assert.Contains("Restart", (string)result["remediation"]);
        }

        [Theory]
        [InlineData("4.2.0+aaa", "4.2.0+bbb")]
        [InlineData("4.2.0", "4.1.0")]
        [InlineData("unknown", "4.2.0")]
        [InlineData("4.2.0", null)]
        public void Unproven_newer_install_is_not_called_stale(string running, string installed)
        {
            Assert.Null(StaleServer.Diagnose(running, installed));
        }

        [Fact]
        public void Later_numbered_release_candidate_is_newer()
        {
            Assert.NotNull(StaleServer.Diagnose("4.2.0-rc2+aaa", "4.2.0-rc3+bbb"));
            Assert.Null(StaleServer.Diagnose("4.2.0-rc3+aaa", "4.2.0-rc2+bbb"));
        }
    }
}
