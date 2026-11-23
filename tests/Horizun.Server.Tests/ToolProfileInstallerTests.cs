// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// 4.0: what the installer writes for the tool profile, and what it does NOT write.
//   * NEW install: the chosen profile lands in settings.json as tool_profile, every
//     other key untouched, and the server then lists core + that profile;
//   * UPGRADE with "keep": nothing is written, so a 3.x machine keeps every tool;
//   * a settings.json that is not JSON is never rewritten;
//   * the helper's list and the installer page's list are the catalog's list.
// -----------------------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Horizun.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class ToolProfileInstallerTests
    {
        // GetDateTimeString takes its separators as Char: an empty string compiles but raises "Type Mismatch" at run
        // time, and an upgrade over an existing install died in CurStepChanged with the payload copied but not committed.
        [Fact]
        public void Installer_never_passes_an_empty_string_as_a_date_separator()
        {
            string iss = File.ReadAllText(Repo("installer", "horizun-mcp.iss"));
            var calls = Regex.Matches(iss, @"GetDateTimeString\(([^)]*)\)").Cast<Match>().ToList();
            Assert.NotEmpty(calls);
            foreach (Match call in calls)
                Assert.DoesNotContain("''", call.Groups[1].Value);
        }

        [Fact]
        public void Preflight_has_diagnostic_exit_codes_and_skips_only_unrelated_unparseable_manifests()
        {
            string iss = File.ReadAllText(Repo("installer", "horizun-mcp.iss"));
            Assert.Contains("EnumerationCompleted := (EnumerationError = 0) or (EnumerationError = 18)", iss);
            Assert.Contains("RawBytesContainHorizunGuid(RawBytes)", iss);
            Assert.Contains("Utf16LE := Utf16LE + Pattern[I] + #0", iss);
            Assert.Contains("Utf16BE := Utf16BE + #0 + Pattern[I]", iss);
            Assert.Contains("for Style := 0 to 3", iss);
            Assert.Contains("for LetterCase := 0 to 1", iss);
            Assert.Contains("Manifest preflight ignored unreadable foreign manifest:", iss);
            Assert.Contains("Win32ExitProcess(ExitCode)", iss);
            Assert.Contains("WritePrecheck(Cause, ExitCode, Evidence)", iss);
            Assert.Contains("\"exit_code\":' + IntToStr(ExitCode)", iss);
            Assert.Contains("SaveStringToFile(Path, Utf8Encode(Json), False)", iss);
            Assert.Contains("'revit_running'", iss);
            Assert.Contains("'duplicate_addin_id'", iss);
            Assert.Contains("'unreadable_manifest_or_folder'", iss);
            Assert.Contains("'enumeration_error'", iss);
            Assert.Contains("Evidence, 10)", iss);
            Assert.Contains("Duplicates, 11)", iss);
            Assert.Contains("Unreadables, 12)", iss);
            Assert.Contains("EnumerationErrors, 13)", iss);
            Assert.Contains("'Nothing has been changed.'", iss);
            Assert.Contains("No se ha cambiado nada.", iss);
        }

        [Fact]
        public void The_helper_and_the_installer_page_name_exactly_the_catalog_profiles()
        {
            string helper = File.ReadAllText(Repo("scripts", "set-tool-profile.ps1"));
            Match m = Regex.Match(helper, @"\$Profiles = @\(([^)]*)\)", RegexOptions.Singleline);
            Assert.True(m.Success, "set-tool-profile.ps1 no longer declares $Profiles in one list");
            string[] listed = Regex.Matches(m.Groups[1].Value, "'([a-z_]+)'").Cast<Match>().Select(x => x.Groups[1].Value).ToArray();
            Assert.Equal(ToolGroupCatalog.Profiles.OrderBy(p => p), listed.OrderBy(p => p));

            string iss = File.ReadAllText(Repo("installer", "horizun-mcp.iss"));
            string[] page = Regex.Matches(iss, @"\d+: Result := '([a-z_]+)';").Cast<Match>().Select(x => x.Groups[1].Value).ToArray();
            Assert.Equal(ToolGroupCatalog.Profiles.OrderBy(p => p), page.OrderBy(p => p));
            Assert.Contains("ApplyToolProfile", iss);
            Assert.Contains("{param:PROFILE|}", iss);
        }

        [Fact]
        public void A_new_install_writes_the_profile_and_keeps_every_other_key()
        {
            WithSettings("{\"permission_profile\":\"full_write\",\"denied_tools\":[\"horizun_undo\"]}", path =>
            {
                Assert.Equal(0, RunHelper(path, "-ToolProfile", "documentador", "-Mode", "new"));
                JObject o = JObject.Parse(File.ReadAllText(path));
                Assert.Equal("documentador", (string)o["tool_profile"]);
                Assert.Equal("full_write", (string)o["permission_profile"]);
                Assert.Equal("horizun_undo", (string)o["denied_tools"][0]);
                Assert.Equal("installer (new)", (string)o["tool_profile_selected_by"]);
            });
        }

        [Fact]
        public void An_upgrade_that_keeps_writes_nothing_and_the_server_keeps_the_full_list()
        {
            const string before = "{\"permission_profile\":\"safe_write\"}";
            WithSettings(before, path =>
            {
                Assert.Equal(0, RunHelper(path, "-ToolProfile", "keep", "-Mode", "upgrade"));
                Assert.Equal(before, File.ReadAllText(path));
            });
            SchemaFacadeTests.WithDataRoot(before, null, () =>
            {
                ToolVisibility.ResetSession();
                Assert.True(ToolVisibility.AllVisible());
                Assert.Equal("default", ToolVisibility.ResolveInitial().Source);
            });
        }

        [Fact]
        public void Garbage_is_refused_and_an_unreadable_settings_file_is_never_rewritten()
        {
            WithSettings("{\"permission_profile\":\"safe_write\"}", path =>
            {
                Assert.Equal(2, RunHelper(path, "-ToolProfile", "zzz", "-Mode", "new"));
                Assert.Equal(2, RunHelper(path, "-ToolProfile", "keep", "-Mode", "new"));
            });
            WithSettings("{ not json", path =>
            {
                Assert.Equal(3, RunHelper(path, "-ToolProfile", "auditor", "-Mode", "upgrade"));
                Assert.Equal("{ not json", File.ReadAllText(path));
            });
            // Settings.cs falls closed (read_only) on these; writing {} + tool_profile over
            // them would move the machine to safe_write. Each one is refused, untouched.
            foreach (string closed in new[] { "", "   ", "true", "1", "[]", "\"x\"" })
                WithSettings(closed, path =>
                {
                    Assert.Equal(3, RunHelper(path, "-ToolProfile", "auditor", "-Mode", "new"));
                    Assert.Equal(closed, File.ReadAllText(path));
                });
        }

        // ---- helpers -------------------------------------------------------------------------

        private static string Repo(string folder, string file, [CallerFilePath] string here = null)
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here), "..", ".."));
            return Path.Combine(root, folder, file);
        }

        private static void WithSettings(string content, Action<string> action)
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, content);
            try { action(path); }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static int RunHelper(string settingsPath, params string[] args)
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Repo("scripts", "set-tool-profile.ps1") })
                psi.ArgumentList.Add(a);
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-SettingsPath");
            psi.ArgumentList.Add(settingsPath);
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } throw new Xunit.Sdk.XunitException("helper hung"); }
                return p.ExitCode;
            }
        }
    }
}
