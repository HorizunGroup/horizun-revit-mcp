// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// WHAT A FRESH INSTALL SEES. tools/list is built once at startup and filtered by
// Settings.IsToolAllowed, so a permission default that is right in Settings and
// wrong here would still leave the client without the tool - and a client that
// never saw a tool never calls it, whatever the add-in would have allowed.
//
// The product decision under test: with no settings.json, only the safe_write
// surface is advertised. Arbitrary code, document sessions and external writes
// require explicit owner elevation.
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
        public void A_fresh_install_does_not_advertise_execute_python()
        {
            WithDataRoot(null, () =>
            {
                Assert.False(Advertised("horizun_execute_python"));
                Assert.NotNull(Tools.DisabledReason("horizun_execute_python"));
            });
        }

        [Fact]
        public void A_fresh_install_advertises_only_safe_write_surface()
        {
            WithDataRoot(null, () =>
            {
                Assert.False(Advertised("horizun_export"));
                Assert.False(Advertised("horizun_save_document"));
                Assert.False(Advertised("horizun_create_family"));
                Assert.True(Advertised("horizun_execute_plan"));
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
