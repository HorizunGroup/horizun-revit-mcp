// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// INT-7: the findings of the independent review of INT-6 (int/4.2 @ 0b41ea8c) that a Revit-free test can hold, written against the
// API that already existed at 0b41ea8c so each one can be run, and fail for a real reason, BEFORE the fix. The tests that need API
// the fix adds live in Int7Tests.cs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    // ---- m-4 (guard removed): the owner's grants are plain booleans, read as they are written -----------------------------

    public class OwnerGrantsArePlainBooleansTests
    {
        private static void WithRoot(Action<string> action)
        {
            using (new EnvGuard())
            {
                string temp = Path.Combine(Path.GetTempPath(), "hz-int7-grants-" + Guid.NewGuid().ToString("N"));
                EnvGuard.Set(HorizunPaths.RootOverrideVariable, temp);
                try { Directory.CreateDirectory(temp); action(temp); }
                finally { try { Directory.Delete(temp, true); } catch { } }
            }
        }

        private static JObject Raw() => JObject.Parse(File.ReadAllText(HorizunPaths.SettingsPath()));

        [Fact]
        public void A_grant_written_in_the_file_reads_as_written_whoever_wrote_it()
        {
            WithRoot(_ =>
            {
                Assert.True(Settings.TrySetSyncOnlyGrant(true, out string error), error);
                JObject o = Raw();
                o["sync_with_central_owner_granted"] = true;            // a hand edit, a script, another tool: the file is the owner's file
                File.WriteAllText(HorizunPaths.SettingsPath(), o.ToString());
                Assert.True(Settings.SyncWithCentralOwnerEnabled);
                Assert.True(Settings.SyncOnlyGrantEnabled);
                Assert.True(Settings.ReadSyncSettings().OwnerSyncGrant);
            });
        }

        [Fact]
        public void The_owners_writer_leaves_no_seal_key_and_no_seals()
        {
            WithRoot(_ =>
            {
                Assert.True(Settings.TrySetSyncOnlyGrant(true, out _));
                Assert.True(Settings.TrySetSyncWithCentralOwnerGrant(true, out _));
                Assert.False(File.Exists(Path.Combine(HorizunPaths.DataRoot(), "owner-seal.key")));
                Assert.Null(Raw()["owner_seals"]);
            });
        }

        [Fact]
        public void A_settings_file_with_grants_and_a_leftover_seal_key_that_is_broken_still_reads_the_grants_on()
        {
            // A machine that ran a build with the seal keeps its grants after the update: nothing reads the seal any more.
            WithRoot(_ =>
            {
                File.WriteAllText(HorizunPaths.SettingsPath(),
                    "{\"sync_with_central_owner_granted\":true,\"allow_sync_only_grant\":true,\"owner_seals\":{\"allow_sync_only_grant\":\"x\"}}");
                File.WriteAllText(Path.Combine(HorizunPaths.DataRoot(), "owner-seal.key"), "not base64 at all");
                Assert.True(Settings.SyncWithCentralOwnerEnabled);
                Assert.True(Settings.SyncOnlyGrantEnabled);
                var s = Settings.ReadSyncSettings();
                Assert.True(s.OwnerSyncGrant);
                Assert.True(s.SyncOnlyFlag);
            });
        }

        [Fact]
        public void An_update_of_another_setting_keeps_both_grants_and_never_writes_a_seal()
        {
            WithRoot(_ =>
            {
                File.WriteAllText(HorizunPaths.SettingsPath(), "{\"sync_with_central_owner_granted\":true,\"allow_sync_only_grant\":true}");
                Assert.True(Settings.TrySetMcpPaused(false, out _));
                Assert.True(Settings.TrySetForceReadOnlyOnWorkshared(false, out _));
                JObject o = Raw();
                Assert.True((bool)o["sync_with_central_owner_granted"]);
                Assert.True((bool)o["allow_sync_only_grant"]);
                Assert.Null(o["owner_seals"]);
                Assert.False(File.Exists(Path.Combine(HorizunPaths.DataRoot(), "owner-seal.key")));
            });
        }

        [Fact]
        public void A_grant_the_owner_turned_off_reads_off_even_with_a_leftover_seal_in_the_file()
        {
            WithRoot(_ =>
            {
                File.WriteAllText(HorizunPaths.SettingsPath(), "{\"allow_sync_only_grant\":false,\"owner_seals\":{\"allow_sync_only_grant\":\"x\"}}");
                Assert.False(Settings.SyncOnlyGrantEnabled);
            });
        }
    }

    // ---- m-e: a cancel after the end of an async script says not_running, as before --------------------------------------

    [Collection("AsyncQueue")]
    public class AsyncScriptCancelAfterEndTests
    {
        private static void Drain() { while (AsyncQueue.Take() != null) { } }

        [Fact]
        public void An_async_execute_python_that_ended_answers_not_running_to_a_late_cancel()
        {
            Drain();
            AsyncQueue.TryAdd(new AsyncWork { JobId = "int7-py-a", Command = "horizun_execute_python", ParamsJson = "{}", QueuedUtc = DateTime.UtcNow }, out _);
            Assert.Equal("int7-py-a", AsyncQueue.Take().JobId);
            var control = new PythonRunControl(null);
            PythonRunRegistry.Register("int7-py-a", control);
            Assert.Equal("interrupt_requested", PythonRunRegistry.Cancel("int7-py-a", "while it runs"));
            PythonRunRegistry.ChildEnded("int7-py-a");                       // the script ended; its job record is still open for a moment
            Assert.Equal("not_running", PythonRunRegistry.Cancel("int7-py-a", "too late"));
            Assert.False(PythonRunRegistry.IsCancelRequested("int7-py-a"));
        }

        [Fact]
        public void A_plan_keeps_a_cancel_asked_between_its_scripts()
        {
            Drain();
            AsyncQueue.TryAdd(new AsyncWork { JobId = "int7-plan-a", Command = "horizun_execute_plan", ParamsJson = "{}", QueuedUtc = DateTime.UtcNow }, out _);
            AsyncQueue.Take();
            PythonRunRegistry.Register("int7-plan-a", new PythonRunControl(null));
            PythonRunRegistry.ChildEnded("int7-plan-a");
            Assert.Equal("interrupt_requested", PythonRunRegistry.Cancel("int7-plan-a", "between"));
            Assert.True(PythonRunRegistry.IsCancelRequested("int7-plan-a"));
            PythonRunRegistry.Unregister("int7-plan-a");
        }
    }

    // ---- m-f: a number glued to its prefix is still a reference; the boundary is the match's, not the group's -------------

    public class SheetReferencePrefixTests
    {
        private static Dictionary<string, string> Map(params string[] pairs)
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pairs.Length; i += 2) m[pairs[i]] = pairs[i + 1];
            return m;
        }

        /// <summary>
        /// An independent reading of the rule, made with look-arounds instead of index arithmetic: the whole match, not the group,
        /// must start and end at a word edge. A different technique than the code under test on purpose.
        /// </summary>
        internal static string Reference(string plain, Regex rx, IDictionary<string, string> map)
        {
            var wrapped = new Regex(@"(?<![\p{L}\p{N}])(?:" + rx + @")(?![\p{L}\p{N}])", rx.Options);
            return wrapped.Replace(plain, m =>
            {
                Group g = m.Groups[1];
                if (!g.Success || !map.TryGetValue(g.Value, out string to) || to == null) return m.Value;
                int at = g.Index - m.Index;
                return m.Value.Substring(0, at) + to + m.Value.Substring(at + g.Length);
            });
        }

        [Theory]
        [InlineData(@"S(\d+)", "Ver S12 y S7", "12", "13", "Ver S13 y S7")]
        [InlineData(@"P(\d{3})", "Plano P101 listo", "101", "102", "Plano P102 listo")]
        [InlineData(@"P(\d{3})", "Plano P1010 listo", "101", "102", "Plano P1010 listo")]
        [InlineData(@"S(\d+)", "AS12", "12", "13", "AS12")]
        public void A_pattern_with_a_prefix_glued_to_the_number_works_and_the_word_edges_are_the_matchs(string pattern, string plain, string from, string to, string expected)
        {
            var rx = new Regex(pattern);
            var map = Map(from, to);
            Assert.Equal(expected, Reference(plain, rx, map));
            List<NoteLeaderRules.SheetRefEdit> edits = NoteLeaderRules.PlanSheetReferenceEdits(plain, rx, map);
            Assert.Equal(expected, NoteLeaderRules.ApplySheetReferenceEdits(plain, edits));
            Assert.Equal(expected, NoteLeaderRules.ExpectedAfterSheetReferences(plain, rx, map));
            Assert.Equal(expected == plain, edits.Count == 0);
        }

        // ---- M-1 again, against the REAL old code --------------------------------------------------------------------------

        // e7af5680 (PlanimetryOpsCommand.NotesMore.cs + .Notes.cs), copied LITERALLY and only wrapped in a function:
        //     foreach (Match m in rx.Matches(plain)) { string num = m.Groups[1].Value; ... if (map != null && map.TryGetValue(num, out mapped))
        //         { string fresh = m.Value.Replace(num, mapped); if (!pairs.Any(p => p.Item1 == m.Value)) pairs.Add(Tuple.Create(m.Value, fresh)); } }
        //     string expected = plain; foreach (var pr in pairs) expected = expected.Replace(pr.Item1, pr.Item2);
        // It is kept here to show the defect the position-based rule closed; it is not used by the product.
        internal static string E7af5680Rewrite(string plain, Regex rx, IDictionary<string, string> map)
        {
            var pairs = new List<Tuple<string, string>>();
            foreach (Match m in rx.Matches(plain))
            {
                string num = m.Groups[1].Value;
                string mapped = null;
                if (map != null && map.TryGetValue(num, out mapped))
                {
                    string fresh = m.Value.Replace(num, mapped);
                    if (!pairs.Any(p => p.Item1 == m.Value)) pairs.Add(Tuple.Create(m.Value, fresh));
                }
            }
            string expected = plain;
            foreach (var pr in pairs) expected = expected.Replace(pr.Item1, pr.Item2);
            return expected;
        }

        private static readonly Regex Default = new Regex(
            @"(?i)\b(?:PLANCHA|L[ÁA]MINA|HOJA|SHEET)\s+([A-Z]{1,4}[-.]?\d{1,4}(?:[.-]\d{1,3})?)", RegexOptions.None);

        [Fact]
        public void The_old_chain_of_replaces_read_A3_twice_where_the_reference_and_the_position_rule_read_A2_and_A3()
        {
            const string plain = "Ver Hoja A1 y Hoja A2";
            var map = Map("A1", "A2", "A2", "A3");
            Assert.Equal("Ver Hoja A3 y Hoja A3", E7af5680Rewrite(plain, Default, map));
            Assert.Equal("Ver Hoja A2 y Hoja A3", Reference(plain, Default, map));
            Assert.Equal("Ver Hoja A2 y Hoja A3", NoteLeaderRules.ApplySheetReferenceEdits(plain, NoteLeaderRules.PlanSheetReferenceEdits(plain, Default, map)));
            Assert.Equal(Reference(plain, Default, map), NoteLeaderRules.ExpectedAfterSheetReferences(plain, Default, map));
        }

        [Theory]
        [InlineData("Hoja A1, Hoja A10 y Hoja A1.", "A1", "B1")]
        [InlineData("Hoja A1 y Hoja A10", "A10", "A11")]
        [InlineData("Hoja A1 / Hoja A2 / Hoja A3", "A1", "A100")]
        public void The_expected_text_of_the_rule_is_the_independent_references(string plain, string from, string to)
        {
            var map = Map(from, to);
            Assert.Equal(Reference(plain, Default, map), NoteLeaderRules.ExpectedAfterSheetReferences(plain, Default, map));
            Assert.Equal(Reference(plain, Default, map),
                NoteLeaderRules.ApplySheetReferenceEdits(plain, NoteLeaderRules.PlanSheetReferenceEdits(plain, Default, map)));
        }
    }
}
