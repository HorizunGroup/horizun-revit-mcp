using System;
using System.IO;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class SettingsPermissionTests
    {
        [Fact]
        public void SafeWriteAllowsVerifiedModelEditButNotSessionOrExport()
        {
            WithSettings(@"{""permission_profile"":""safe_write""}", () =>
            {
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_execute_plan"), out _));
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_save_document"), out _));
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_export"), out _));
            });
        }

        [Fact]
        public void UnsafePythonRequiresBothIndependentSwitches()
        {
            WithSettings(@"{""permission_profile"":""unsafe_code""}", () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
            WithSettings(@"{""permission_profile"":""unsafe_code"",""enable_execute_python"":true}", () =>
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
        }

        [Fact]
        public void MalformedProfileFallsBackToReadOnly()
        {
            WithSettings(@"{""permission_profile"":""superadmin""}", () =>
            {
                Assert.Equal("read_only", Settings.PermissionProfile);
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_create_elements"), out _));
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_query_model"), out _));
            });
        }

        private static void WithSettings(string json, Action action)
        {
            using (new EnvGuard())
            {
                string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hz-settings-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Directory.CreateDirectory(temp);
                    File.WriteAllText(HorizunPaths.SettingsPath(), json);
                    action();
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }
    }
}
