// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// THE PER-TOOL BYTE LEDGER. tools/list reached 524,199 of its 524,288-byte budget
// (MEASURED 2026-09-26 at c56a742) and the only guard was one total: a test that
// failed saying "too big" without saying WHICH tool grew, by how much, or since
// when. This ledger pins every advertised entry - its size and a hash of what a
// model reads (description + inputSchema) - per tool, per permission profile and
// per tool pack, in a committed golden. A change to the advertised surface now
// fails naming the tool and the signed delta, and the refresh is a deliberate,
// reviewed diff committed with the change that caused it.
//
// POSTURE-FREE BY CONSTRUCTION. The per-tool rows come from Tools.Publish over
// Tools.All - the worst case, every contract row - so they do not depend on the
// machine's settings.json, on an environment toolset selection or on a live add-in.
// The per-profile totals DO depend on posture, which is the point of measuring
// them, so each one is measured under a settings root of its own.
//
// The 512 KiB total ceiling test in McpPrimitiveTests stays; this adds the
// posture-free form of it (Worst_case_list_fits_the_ceiling).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public class ToolsListLedgerTests
    {
        private const int CeilingBytes = 512 * 1024;
        private const string UpdateVariable = "HORIZUN_UPDATE_TOOLS_LEDGER";
        private const string MeasuredWith =
            "Tools.Publish(t, advertiseTaskSupport:true) over Tools.All, Formatting.None, UTF-8";
        private const string RefreshHint =
            "Deliberate? set " + UpdateVariable + "=1 and run: dotnet test tests/Horizun.Server.Tests -c Release " +
            "--filter \"FullyQualifiedName~ToolsListLedger\", review git diff of tools-list-ledger.json and commit " +
            "it with the change that caused it.";

        private static readonly string[] PermissionProfiles = { "read_only", "documentation", "safe_write", "full_write", "unsafe_code" };

        [Fact]
        public void Advertised_surface_matches_the_committed_ledger()
        {
            JObject current = CurrentLedger();
            RewriteIfAsked(current);
            JObject golden = Golden();

            var problems = new List<string>();
            CompareTools(golden, current, problems);
            CompareTotal(golden, current, problems);
            if (problems.Count > 0)
            {
                CompareProfiles(golden, current, problems); // the reviewer sees every consequence in one message
                Assert.Fail(Report(current, problems));
            }
        }

        [Fact]
        public void Profile_totals_match_the_ledger()
        {
            JObject current = CurrentLedger();
            RewriteIfAsked(current);
            JObject golden = Golden();

            var problems = new List<string>();
            CompareProfiles(golden, current, problems);
            if (problems.Count > 0) Assert.Fail(Report(current, problems));
        }

        [Fact]
        public void Ledger_covers_every_contract_tool_and_every_pack()
        {
            JObject golden = Golden();
            string[] ledgerTools = ((JArray)golden["tools"]).Select(r => (string)r["name"]).ToArray();
            string[] contractTools = Horizun.Contracts.Contract.All.Select(c => c.Name).ToArray();
            Assert.Equal(contractTools, ledgerTools);                         // same set AND contract order
            Assert.Equal(Tools.All.Select(t => t.Name).ToArray(), ledgerTools);
            Assert.Equal(ledgerTools.Length, (int)golden["all"]["tools"]);

            var expected = new SortedSet<string>(ExpectedProfileKeys(), StringComparer.Ordinal);
            var recorded = new SortedSet<string>(((JObject)golden["profiles"]).Properties().Select(p => p.Name), StringComparer.Ordinal);
            Assert.Equal(expected, recorded);
        }

        [Fact]
        public void Worst_case_list_fits_the_ceiling()
        {
            // The McpPrimitiveTests ceiling measures Tools.List(true) at whatever posture
            // the machine running the test has; a read_only developer machine would pass
            // it with the list over budget. This measures every contract row.
            var all = new JArray(Tools.All.Select(t => (JToken)Tools.Publish(t, true)));
            int bytes = Bytes(all);
            Assert.True(bytes <= CeilingBytes - 8000,
                "the worst-case tools/list is " + N(bytes) + " B, over the " + N(CeilingBytes) + " B ceiling by " +
                N(bytes - CeilingBytes) + " B; delivery requires at least 8000 B headroom");
        }

        // ---- measurement -------------------------------------------------------------

        private static IEnumerable<string> ExpectedProfileKeys()
        {
            foreach (string p in PermissionProfiles) yield return "permission:" + p;
            yield return "permission:unsafe_code+python";
            foreach (string pack in ToolPacks.KnownPacks) yield return "pack:" + pack;
            foreach (string profile in Horizun.Contracts.ToolGroupCatalog.Profiles) yield return "group_profile:" + profile;
        }

        internal static JObject CurrentLedger()
        {
            var rows = new JArray();
            var all = new JArray();
            foreach (ToolDef t in Tools.All)
            {
                JObject entry = Tools.Publish(t, true);
                all.Add(entry);
                string description = (string)entry["description"] ?? "";
                string schema = entry["inputSchema"] == null || entry["inputSchema"].Type == JTokenType.Null
                    ? "" : entry["inputSchema"].ToString(Formatting.None);
                rows.Add(new JObject
                {
                    ["name"] = t.Name,
                    ["sha256"] = Sha256(description + "\n" + schema),
                    ["entry_bytes"] = Bytes(entry),
                    ["description_bytes"] = Encoding.UTF8.GetByteCount(description),
                    ["input_schema_bytes"] = Encoding.UTF8.GetByteCount(schema)
                });
            }
            int total = Bytes(all);

            var profiles = new JObject();
            foreach (string p in PermissionProfiles)
                WithDataRoot("{\"permission_profile\":\"" + p + "\"}",
                    () => profiles["permission:" + p] = Totals(Tools.List(true)));
            WithDataRoot("{\"permission_profile\":\"unsafe_code\",\"enable_execute_python\":true}", () =>
            {
                profiles["permission:unsafe_code+python"] = Totals(Tools.List(true));
                foreach (string pack in ToolPacks.KnownPacks.OrderBy(k => k, StringComparer.Ordinal))
                    profiles["pack:" + pack] = Totals(Tools.ListForPacks(new[] { pack }, true));
                // 4.0: what each tool profile lists (core + its groups + the three meta tools).
                foreach (string profile in Horizun.Contracts.ToolGroupCatalog.Profiles)
                    profiles["group_profile:" + profile] = Totals(ToolVisibility.ListForProfile(profile, true));
            });

            return new JObject
            {
                ["schema"] = 1,
                ["measured_with"] = MeasuredWith,
                ["ceiling_bytes"] = CeilingBytes,
                ["all"] = new JObject { ["tools"] = rows.Count, ["bytes"] = total, ["headroom_bytes"] = CeilingBytes - total },
                ["tools"] = rows,
                ["profiles"] = profiles
            };
        }

        private static JObject Totals(JArray list) =>
            new JObject { ["tools"] = list.Count, ["bytes"] = Bytes(list) };

        private static int Bytes(JToken t) => Encoding.UTF8.GetByteCount(t.ToString(Formatting.None));

        private static string Sha256(string text)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }

        // A toolset selection in the environment would silently shrink every profile
        // total, so it is lifted for the measurement and restored afterwards.
        private static void WithDataRoot(string settingsJson, Action action)
        {
            string saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            string savedPacks = Environment.GetEnvironmentVariable(ToolPacks.EnvironmentOverride);
            string savedToolsets = Environment.GetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable);
            string temp = Path.Combine(Path.GetTempPath(), "hz-ledger-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, temp);
                Environment.SetEnvironmentVariable(ToolPacks.EnvironmentOverride, null);
                Environment.SetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable, null);
                File.WriteAllText(HorizunPaths.SettingsPath(), settingsJson);
                action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, saved);
                Environment.SetEnvironmentVariable(ToolPacks.EnvironmentOverride, savedPacks);
                Environment.SetEnvironmentVariable(ToolPacks.ToolsetsEnvironmentVariable, savedToolsets);
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        // ---- golden ------------------------------------------------------------------

        private static string GoldenPath([CallerFilePath] string here = null) =>
            Path.Combine(Path.GetDirectoryName(here), "tools-list-ledger.json");

        private static JObject Golden()
        {
            string path = GoldenPath();
            Assert.True(File.Exists(path), "missing " + path + ". " + RefreshHint);
            return JObject.Parse(File.ReadAllText(path)); // parsed, so CRLF/LF checkouts compare equal
        }

        // With the variable set the golden is rewritten and the test FAILS once, so an
        // accidentally exported variable can never turn a CI run green; CI refuses to
        // write at all.
        private static void RewriteIfAsked(JObject current)
        {
            if (Environment.GetEnvironmentVariable(UpdateVariable) != "1") return;
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                Assert.Fail(UpdateVariable + " is set under CI=true; the ledger golden is refreshed on a developer " +
                            "machine and committed, never written by CI.");
            File.WriteAllText(GoldenPath(), Serialize(current));
            Assert.Fail("golden rewritten (" + GoldenPath() + "); rerun without " + UpdateVariable +
                        " and review git diff of tools-list-ledger.json");
        }

        // One tool and one profile per line, so a refresh reads as a reviewable diff.
        internal static string Serialize(JObject ledger)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"schema\": ").Append(ledger["schema"].ToString(Formatting.None)).Append(",\n");
            sb.Append("  \"measured_with\": ").Append(ledger["measured_with"].ToString(Formatting.None)).Append(",\n");
            sb.Append("  \"ceiling_bytes\": ").Append(ledger["ceiling_bytes"].ToString(Formatting.None)).Append(",\n");
            sb.Append("  \"all\": ").Append(ledger["all"].ToString(Formatting.None)).Append(",\n");
            sb.Append("  \"tools\": [\n");
            var rows = (JArray)ledger["tools"];
            for (int i = 0; i < rows.Count; i++)
                sb.Append("    ").Append(rows[i].ToString(Formatting.None)).Append(i + 1 < rows.Count ? ",\n" : "\n");
            sb.Append("  ],\n");
            sb.Append("  \"profiles\": {\n");
            var profiles = ((JObject)ledger["profiles"]).Properties().ToList();
            for (int i = 0; i < profiles.Count; i++)
                sb.Append("    ").Append(JsonConvert.ToString(profiles[i].Name)).Append(": ")
                  .Append(profiles[i].Value.ToString(Formatting.None)).Append(i + 1 < profiles.Count ? ",\n" : "\n");
            sb.Append("  }\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        // ---- the failure message is the review artifact -----------------------------

        private static void CompareTools(JObject golden, JObject current, List<string> problems)
        {
            var before = ((JArray)golden["tools"]).Cast<JObject>().ToDictionary(r => (string)r["name"], StringComparer.Ordinal);
            var after = ((JArray)current["tools"]).Cast<JObject>().ToDictionary(r => (string)r["name"], StringComparer.Ordinal);

            foreach (JObject now in ((JArray)current["tools"]).Cast<JObject>())
            {
                string name = (string)now["name"];
                int bytesNow = (int)now["entry_bytes"];
                if (!before.TryGetValue(name, out JObject was))
                {
                    problems.Add(name + " added (+" + N(bytesNow) + " B), sha " + Short(now));
                    continue;
                }
                int bytesWas = (int)was["entry_bytes"];
                bool shaMoved = (string)was["sha256"] != (string)now["sha256"];
                int delta = bytesNow - bytesWas;
                if (delta != 0)
                    problems.Add(name + (delta > 0 ? " grew " : " shrank ") + Signed(delta) + " B (" + N(bytesWas) + " -> " +
                                 N(bytesNow) + "), " + (shaMoved
                                     ? "sha " + Short(was) + " -> " + Short(now)
                                     : "description and inputSchema unchanged (title, annotations, outputSchema, execution or _meta moved)"));
                else if (shaMoved)
                    problems.Add(name + " changed with the same size (" + N(bytesNow) + " B), sha " + Short(was) + " -> " + Short(now));
                else if (!JToken.DeepEquals(was, now))
                    problems.Add(name + " changed its recorded description/inputSchema split: " +
                                 was.ToString(Formatting.None) + " -> " + now.ToString(Formatting.None));
            }
            foreach (JObject was in ((JArray)golden["tools"]).Cast<JObject>())
                if (!after.ContainsKey((string)was["name"]))
                    problems.Add((string)was["name"] + " removed (-" + N((int)was["entry_bytes"]) + " B)");

            string[] orderWas = ((JArray)golden["tools"]).Select(r => (string)r["name"]).Where(after.ContainsKey).ToArray();
            string[] orderNow = ((JArray)current["tools"]).Select(r => (string)r["name"]).Where(before.ContainsKey).ToArray();
            if (!orderWas.SequenceEqual(orderNow, StringComparer.Ordinal))
                problems.Add("the published ORDER of tools changed (tools/list follows contract order; prompt caches pay for it)");
        }

        private static void CompareTotal(JObject golden, JObject current, List<string> problems)
        {
            if (!JToken.DeepEquals(golden["all"], current["all"]) || (int?)golden["ceiling_bytes"] != CeilingBytes ||
                (string)golden["measured_with"] != MeasuredWith || (int?)golden["schema"] != 1)
            {
                int was = (int?)golden["all"]?["bytes"] ?? 0, now = (int)current["all"]["bytes"];
                problems.Add("all " + (int?)golden["all"]?["tools"] + " -> " + (int)current["all"]["tools"] + " tools, " +
                             N(was) + " -> " + N(now) + " B (" + Signed(now - was) + " B)");
            }
        }

        private static void CompareProfiles(JObject golden, JObject current, List<string> problems)
        {
            var was = (JObject)golden["profiles"] ?? new JObject();
            var now = (JObject)current["profiles"];
            foreach (string key in was.Properties().Select(p => p.Name).Union(now.Properties().Select(p => p.Name)))
            {
                JToken a = was[key], b = now[key];
                if (JToken.DeepEquals(a, b)) continue;
                if (a == null) { problems.Add("profile " + key + " added: " + b.ToString(Formatting.None)); continue; }
                if (b == null) { problems.Add("profile " + key + " removed (was " + a.ToString(Formatting.None) + ")"); continue; }
                int bw = (int)a["bytes"], bn = (int)b["bytes"];
                problems.Add("profile " + key + ": " + (int)a["tools"] + " -> " + (int)b["tools"] + " tools, " +
                             N(bw) + " -> " + N(bn) + " B (" + Signed(bn - bw) + " B)");
            }
        }

        private static string Report(JObject current, List<string> problems)
        {
            int total = (int)current["all"]["bytes"];
            return "The advertised tools/list surface no longer matches tests/Horizun.Server.Tests/tools-list-ledger.json:\n  " +
                   string.Join("\n  ", problems) + "\n" +
                   "Now: " + (int)current["all"]["tools"] + " tools, " + N(total) + " B worst case, headroom " +
                   N(CeilingBytes - total) + " B against " + N(CeilingBytes) + ".\n" + RefreshHint;
        }

        private static string Short(JObject row) => ((string)row["sha256"]).Substring(0, 4) + "..";
        private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        private static string Signed(int n) => (n >= 0 ? "+" : "-") + N(Math.Abs(n));
    }
}
