// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// WHAT A FRESH INSTALL SEES. tools/list is built once at startup and filtered by
// Settings.IsToolAllowed, so a permission default that is right in Settings and
// wrong here would still leave the client without the tool - and a client that
// never saw a tool never calls it, whatever the add-in would have allowed.
//
// The product decision under test: with no settings.json at all, the full
// surface INCLUDING horizun_execute_python is advertised, because Python is the
// execution fallback. And the reverse, equally load-bearing: an explicit off
// still removes it from the list.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class ToolListDefaultsTests
    {
        private static bool Advertised(string tool) =>
            Tools.List().Any(t => (string)t["name"] == tool);

        [Fact]
        public void A_fresh_install_advertises_execute_python()
        {
            WithDataRoot(null, () =>
            {
                Assert.True(Advertised("horizun_execute_python"),
                    "with no settings.json, execute_python must appear in tools/list - it is the fallback a " +
                    "client is expected to reach for, and an unadvertised tool is an unreachable one.");
                Assert.Null(Tools.DisabledReason("horizun_execute_python"));
            });
        }

        [Fact]
        public void A_fresh_install_advertises_the_full_write_and_session_surface_too()
        {
            WithDataRoot(null, () =>
            {
                Assert.True(Advertised("horizun_export"));
                Assert.True(Advertised("horizun_save_document"));
                Assert.True(Advertised("horizun_create_family"));
            });
        }

        [Fact]
        public void An_explicit_off_removes_it_from_the_list_again()
        {
            WithDataRoot(@"{""enable_execute_python"":false}", () =>
            {
                Assert.False(Advertised("horizun_execute_python"));
                Assert.NotNull(Tools.DisabledReason("horizun_execute_python"));
                // Everything else the profile still allows stays advertised.
                Assert.True(Advertised("horizun_query_model"));
            });

            WithDataRoot(@"{""permission_profile"":""safe_write""}", () =>
            {
                Assert.False(Advertised("horizun_execute_python"));
                Assert.False(Advertised("horizun_export"));
            });
        }

        [Fact]
        public void A_malformed_settings_file_falls_closed_rather_than_open()
        {
            WithDataRoot("{ not json at all", () =>
            {
                Assert.False(Advertised("horizun_execute_python"));
                Assert.False(Advertised("horizun_create_elements"));
                Assert.True(Advertised("horizun_query_model"));
            });
        }

        /// <summary>
        /// Point HORIZUN_DATA_ROOT at a temp folder, optionally with a settings.json in
        /// it, and put every touched variable back afterwards. Settings re-reads the
        /// file on every call, so no cache has to be invalidated.
        /// </summary>
        private static void WithDataRoot(string settingsJson, Action action)
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            string temp = Path.Combine(Path.GetTempPath(), "hz-toollist-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, temp);
                if (settingsJson != null) File.WriteAllText(HorizunPaths.SettingsPath(), settingsJson);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved);
                try { Directory.Delete(temp, true); } catch { }
            }
        }
    }
}
