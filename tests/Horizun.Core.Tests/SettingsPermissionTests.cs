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
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_create_family"), out _));
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_power_bi_push"), out _));
            });
        }

        [Fact]
        public void FullWriteAllowsVerifiedExternalOperations()
        {
            WithSettings(@"{""permission_profile"":""full_write""}", () =>
            {
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_export"), out _));
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_create_family"), out _));
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_power_bi_push"), out _));
            });
        }

        /// <summary>
        /// The default-on posture: no settings file at all reads as unsafe_code plus
        /// enable_execute_python=true, so a fresh install advertises the full surface -
        /// Tools.List() filters on exactly this predicate, so what is proved here is
        /// what tools/list shows.
        /// </summary>
        [Fact]
        public void AbsentFileDefaultsToUnsafeCodeWithPythonEnabled()
        {
            WithoutSettingsFile(() =>
            {
                Assert.Equal("unsafe_code", Settings.PermissionProfile);
                Assert.True(Settings.ExecutePythonEnabled);
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _));
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_export"), out _));
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_save_document"), out _));
            });
        }

        /// <summary>A file that exists but omits the keys is the same absence, key by key.</summary>
        [Fact]
        public void AbsentKeysDefaultToEnabledEvenWhenOtherKeysExist()
        {
            WithSettings(@"{""denied_tools"":[""horizun_export""]}", () =>
            {
                Assert.Equal("unsafe_code", Settings.PermissionProfile);
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _));
                // The explicit deny still wins over the permissive default.
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_export"), out _));
            });
        }

        /// <summary>
        /// An explicit restrictive choice is respected exactly as before the defaults
        /// flipped: the defaults fill absence, they never override a decision.
        /// </summary>
        [Fact]
        public void ExplicitRestrictionsAreRespectedOverTheDefaults()
        {
            WithSettings(@"{""enable_execute_python"":false}", () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
            WithSettings(@"{""permission_profile"":""safe_write""}", () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
            WithSettings(@"{""permission_profile"":""read_only""}", () =>
            {
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _));
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_create_elements"), out _));
            });
            WithSettings(@"{""permission_profile"":""full_write""}", () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
        }

        /// <summary>
        /// unsafe_code alone used to be insufficient; with enable_execute_python
        /// defaulting to true, the profile alone now admits Python - and an explicit
        /// false beside it still wins.
        /// </summary>
        [Fact]
        public void UnsafeProfileAloneNowAdmitsPythonAndExplicitFalseStillWins()
        {
            WithSettings(@"{""permission_profile"":""unsafe_code""}", () =>
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
            WithSettings(@"{""permission_profile"":""unsafe_code"",""enable_execute_python"":true}", () =>
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
            WithSettings(@"{""permission_profile"":""unsafe_code"",""enable_execute_python"":false}", () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
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

        /// <summary>
        /// The asymmetry the default-on posture must keep: a file that EXISTS but does
        /// not parse may be a corrupted explicit restriction, so it falls CLOSED - the
        /// opposite of the absent-file default. Corruption never converts "I turned
        /// this off" into "everything is enabled".
        /// </summary>
        [Fact]
        public void MalformedFileFallsClosedNotOpen()
        {
            WithSettings("{ this is not json", () =>
            {
                Assert.Equal("read_only", Settings.PermissionProfile);
                Assert.False(Settings.ExecutePythonEnabled);
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _));
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_create_elements"), out _));
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

        private static void WithoutSettingsFile(Action action)
        {
            using (new EnvGuard())
            {
                string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hz-settings-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try
                {
                    Directory.CreateDirectory(temp); // the root exists; settings.json does not
                    action();
                }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }
    }
}
