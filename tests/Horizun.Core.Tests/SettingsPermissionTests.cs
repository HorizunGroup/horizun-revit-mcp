using System;
using System.IO;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
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
        /// A fresh install permits typed in-document writes, but neither external/session
        /// effects nor arbitrary code. Consent must be explicit.
        /// </summary>
        [Fact]
        public void AbsentFileDefaultsToSafeWriteWithPythonDisabled()
        {
            WithoutSettingsFile(() =>
            {
                Assert.Equal("safe_write", Settings.PermissionProfile);
                Assert.False(Settings.ExecutePythonEnabled);
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _));
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_export"), out _));
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_save_document"), out _));
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_execute_plan"), out _));
            });
        }

        /// <summary>A file that exists but omits the keys is the same absence, key by key.</summary>
        [Fact]
        public void AbsentKeysDefaultToSafeEvenWhenOtherKeysExist()
        {
            WithSettings(@"{""denied_tools"":[""horizun_export""]}", () =>
            {
                Assert.Equal("safe_write", Settings.PermissionProfile);
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _));
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
        /// Both explicit switches are required. A profile alone is not consent.
        /// </summary>
        [Fact]
        public void UnsafeProfileStillRequiresExplicitPythonOptIn()
        {
            WithSettings(@"{""permission_profile"":""unsafe_code""}", () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
            WithSettings(@"{""permission_profile"":""unsafe_code"",""enable_execute_python"":true}", () =>
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
            WithSettings(@"{""permission_profile"":""unsafe_code"",""enable_execute_python"":false}", () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
        }

        [Fact]
        public void LegacyTemporaryPythonUnlockStillExpiresFailClosedAfterUpgrade()
        {
            WithSettings(@"{""permission_profile"":""safe_write"",""enable_execute_python"":false,""execute_python_ui_grant_until_utc"":""2999-01-01T00:00:00Z""}", () =>
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
            WithSettings(@"{""permission_profile"":""safe_write"",""enable_execute_python"":false,""execute_python_ui_grant_until_utc"":""2000-01-01T00:00:00Z""}", () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
            WithSettings(@"{""permission_profile"":""safe_write"",""enable_execute_python"":false,""execute_python_ui_grant_until_utc"":""not-a-date""}", () =>
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _)));
        }

        [Fact]
        public void RevitPersistentGrantStaysOnUntilOwnerRevokesIt()
        {
            WithSettings(@"{""permission_profile"":""safe_write"",""enable_execute_python"":false,""denied_tools"":[]}", () =>
            {
                Assert.True(Settings.TryGrantExecutePythonPersistently(out string grantError), grantError);
                Assert.Equal("safe_write", Settings.PermissionProfile);
                Assert.True(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out string reason), reason);
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_export"), out _));

                JObject persisted = JObject.Parse(File.ReadAllText(HorizunPaths.SettingsPath()));
                Assert.Equal("safe_write", (string)persisted["permission_profile"]);
                Assert.True((bool)persisted["execute_python_ui_granted"]);
                Assert.False((bool)persisted["enable_execute_python"]);
                Assert.NotNull(persisted["execute_python_ui_granted_at_utc"]);
                Assert.Null(persisted["execute_python_ui_grant_until_utc"]);
                Assert.NotNull(persisted["denied_tools"]);

                Assert.True(Settings.TryRevokeExecutePython(out string revokeError), revokeError);
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _));
                Assert.Equal("safe_write", Settings.PermissionProfile);
                JObject revoked = JObject.Parse(File.ReadAllText(HorizunPaths.SettingsPath()));
                Assert.False((bool)revoked["enable_execute_python"]);
                Assert.Null(revoked["execute_python_ui_granted"]);
                Assert.Null(revoked["execute_python_ui_granted_at_utc"]);
            });
        }

        [Fact]
        public void RevitGrantRefusesToOverwriteMalformedSettings()
        {
            WithSettings("{ not json", () =>
            {
                Assert.False(Settings.TryGrantExecutePythonPersistently(out string error));
                Assert.Contains("malformed", error);
                Assert.Equal("{ not json", File.ReadAllText(HorizunPaths.SettingsPath()));
            });
        }

        [Fact]
        public void RevitPermissionUpdatesKeepOnlyThreeRecoverableBackups()
        {
            WithSettings(@"{""permission_profile"":""safe_write""}", () =>
            {
                for (int i = 0; i < 6; i++)
                {
                    Assert.True(Settings.TryGrantExecutePythonPersistently(out string grantError), grantError);
                    Assert.True(Settings.TryRevokeExecutePython(out string revokeError), revokeError);
                }
                string directory = Path.GetDirectoryName(HorizunPaths.SettingsPath());
                Assert.True(Directory.GetFiles(directory, "settings.json.horizun-ui-bak-*").Length <= 3);
            });
        }

        [Fact]
        public void RevitPermissionUpdatesWaitForTheCrossProcessMutex()
        {
            WithSettings(@"{""permission_profile"":""safe_write""}", () =>
            {
                using (var mutex = new System.Threading.Mutex(false, "Local\\Horizun.Revit.Settings.V1"))
                {
                    Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(2)));
                    var update = System.Threading.Tasks.Task.Run(() =>
                    {
                        bool ok = Settings.TryGrantExecutePythonPersistently(out string error);
                        return Tuple.Create(ok, error);
                    });
                    Assert.False(update.Wait(250));
                    mutex.ReleaseMutex();
                    Assert.True(update.Wait(TimeSpan.FromSeconds(3)));
                    Assert.True(update.Result.Item1, update.Result.Item2);
                }
            });
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
        /// The fail-closed posture: a file that EXISTS but does
        /// not parse may be a corrupted explicit restriction, so it falls CLOSED - the
        /// stricter than the absent-file safe_write default. Corruption never converts "I turned
        /// this off" into "everything is enabled".
        /// </summary>
        [Fact]
        public void MalformedFileFallsClosedNotOpen()
        {
            WithSettings("{ this is not json", () =>
            {
                Assert.Equal("read_only", Settings.PermissionProfile);
                Assert.False(Settings.ExecutePythonEnabled);
                Assert.Equal("invalid-settings-file", Settings.RetentionValue("job_retention_days"));
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_execute_python"), out _));
                Assert.False(Settings.IsToolAllowed(Contract.Find("horizun_create_elements"), out _));
            });
        }

        [Fact]
        public void RetentionReaderDistinguishesMissingPolicyFromCorruptSettings()
        {
            WithoutSettingsFile(() =>
                Assert.Null(Settings.RetentionValue("job_retention_days")));
            WithSettings("{}", () =>
                Assert.Null(Settings.RetentionValue("job_retention_days")));
            WithSettings("{ this is not json", () =>
                Assert.Equal("invalid-settings-file", Settings.RetentionValue("job_retention_days")));
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
