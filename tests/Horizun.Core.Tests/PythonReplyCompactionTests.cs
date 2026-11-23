using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    /// <summary>
    /// execute_python response_mode=compact on a model with >1000 persistent warnings
    /// (field report: ~127,000 characters for a few lines of __output__).
    /// </summary>
    public sealed class PythonReplyCompactionTests
    {
        [Fact]
        public void Output_only_returns_script_value_and_preserves_oversize_diagnostics()
        {
            var reply = new JObject { ["output"] = new JArray(1, 2), ["host_verified"] = false };
            Assert.True(JToken.DeepEquals(new JArray(1, 2), PythonReplyCompaction.OutputOnly(reply)["output"]));
            var oversized = new JObject { ["output"] = null, ["output_truncated"] = true,
                ["output_original_chars"] = 50000, ["output_full_path"] = "output.json" };
            JToken result = PythonReplyCompaction.OutputOnly(oversized);
            Assert.True((bool)result["output_truncated"]);
            Assert.Equal("output.json", (string)result["output_full_path"]);
        }

        [Fact]
        public void Output_only_reports_when_serialization_lost_structure()
        {
            var reply = new JObject { ["output"] = "fallback text", ["output_kind"] = "text_only",
                ["output_note"] = "structure was lost" };
            JObject result = PythonReplyCompaction.OutputOnly(reply);
            Assert.Equal("text_only", (string)result["output_kind"]);
            Assert.Equal("structure was lost", (string)result["output_note"]);
        }
        private static string Sig(int i) => "f2e5a4b1-0000-4000-8000-00000000" + (i % 10000).ToString("D4") + ":" + (100000 + i) + "," + (200000 + i) + ":";

        /// <summary>The shape PythonHostObservations.Delta returns, with n warnings before and after.</summary>
        private static JObject FullDelta(int before, int after, int added, int resolved)
        {
            var a = Enumerable.Range(0, before).Select(Sig).ToList();
            var b = Enumerable.Range(resolved, before - resolved).Select(Sig).Concat(Enumerable.Range(50000, added).Select(Sig)).ToList();
            return new JObject
            {
                ["before"] = new JObject { ["observed"] = true, ["count"] = a.Count, ["truncated"] = false, ["signatures"] = new JArray(a) },
                ["after"] = new JObject { ["observed"] = true, ["count"] = b.Count, ["truncated"] = false, ["signatures"] = new JArray(b) },
                ["persistent_warning_comparison_complete"] = true,
                ["new"] = new JArray(b.Except(a)),
                ["resolved"] = new JArray(a.Except(b)),
                ["suppressed_warning_coverage"] = "unknown",
                ["note"] = "GetWarnings compares persistent warnings on the captured document. A script may suppress transient failures; an empty delta does not prove no warnings were raised."
            };
        }

        private static JObject Reply(JObject delta) => new JObject
        {
            ["mode"] = "sync", ["executed"] = true,
            ["source"] = new JObject { ["from"] = "code", ["chars"] = 40, ["note"] = new string('n', 300) },
            ["output"] = new JObject { ["status"] = "verified", ["n"] = 3 },
            ["evidence_contract"] = new string('c', 900),
            ["host_verification_note"] = new string('h', 400),
            ["transaction_policy"] = new string('t', 600),
            ["revit_raised_note"] = new string('r', 300),
            ["observation_note"] = new string('o', 300),
            ["warning_delta"] = delta
        };

        [Fact]
        public void Compact_reply_on_a_model_with_1200_warnings_stays_small()
        {
            JObject full = Reply(FullDelta(1200, 1200, 3, 2));
            int fullChars = full.ToString(Formatting.None).Length;
            Assert.True(fullChars > 100000, "the fixture must reproduce the field size, got " + fullChars);

            JObject compact = PythonReplyCompaction.Compact((JObject)full.DeepClone());
            int compactChars = compact.ToString(Formatting.None).Length;
            Assert.True(compactChars < 2500, "compact reply is " + compactChars + " chars");

            JObject d = (JObject)compact["warning_delta"];
            Assert.Equal("compact", (string)d["shape"]);
            Assert.Equal(1200, (int)d["count_before"]);
            Assert.Equal(1201, (int)d["count_after"]);
            Assert.Equal(3, (int)d["new_count"]);
            Assert.Equal(2, (int)d["resolved_count"]);
            Assert.Equal(3, ((JArray)d["new"]).Count);
            Assert.Equal(2, ((JArray)d["resolved"]).Count);
            Assert.Null(d["before"]);
            Assert.Null(d["after"]);
            foreach (string f in PythonReplyCompaction.FixedTextFields) Assert.Null(compact[f]);
            Assert.Null(compact["source"]["note"]);
            Assert.Equal(PythonReplyCompaction.ContractRef, (string)compact["contract_ref"]);
            // What the script produced is untouched.
            Assert.Equal(3, (int)compact["output"]["n"]);
        }

        [Fact]
        public void Compact_bounds_the_new_and_resolved_lists_and_keeps_their_totals()
        {
            JObject d = PythonReplyCompaction.CompactWarningDelta(FullDelta(1500, 1500, 400, 300));
            Assert.Equal(400, (int)d["new_count"]);
            Assert.Equal(300, (int)d["resolved_count"]);
            Assert.Equal(PythonReplyCompaction.MaxListedWarnings, ((JArray)d["new"]).Count);
            Assert.Equal(PythonReplyCompaction.MaxListedWarnings, ((JArray)d["resolved"]).Count);
            Assert.True((bool)d["new_truncated"]);
            Assert.True((bool)d["resolved_truncated"]);
            Assert.True(d.ToString(Formatting.None).Length < 4000);
        }

        [Fact]
        public void An_incomplete_comparison_stays_unknown_not_empty()
        {
            JObject full = FullDelta(10, 10, 0, 0);
            full["persistent_warning_comparison_complete"] = false;
            full["new"] = null; full["resolved"] = null;
            ((JObject)full["after"])["observed"] = false;
            ((JObject)full["after"])["error"] = "document closed";
            JObject d = PythonReplyCompaction.CompactWarningDelta(full);
            Assert.Equal(JTokenType.Null, d["new_count"].Type);
            Assert.Equal(JTokenType.Null, d["new"]?.Type ?? JTokenType.Null);
            Assert.False((bool)d["observed"]);
            Assert.Equal("document closed", (string)d["observation_error"]);
        }

        [Fact]
        public void Compact_is_idempotent_and_full_mode_is_not_touched_by_it()
        {
            JObject once = PythonReplyCompaction.Compact(Reply(FullDelta(50, 50, 1, 1)));
            string first = once.ToString(Formatting.None);
            JObject twice = PythonReplyCompaction.Compact(once);
            Assert.Equal(first, twice.ToString(Formatting.None));
            // Full mode never calls Compact: the full delta keeps both lists.
            JObject full = Reply(FullDelta(50, 50, 1, 1));
            Assert.Equal(50, ((JArray)full["warning_delta"]["before"]["signatures"]).Count);
        }
    }
}
