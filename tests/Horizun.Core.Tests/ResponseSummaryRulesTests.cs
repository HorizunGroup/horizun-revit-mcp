// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// response_mode=summary for horizun_create_elements and horizun_clash (course dry
// run 2026-09-30, defect #17: 75 walls answered 262-276 kB and 165 clashes 58 kB,
// both over the client's limit). The shaping is presentation only: nothing that is
// not a clean verified row is ever collapsed, the counts still describe the whole
// set, and every omission is named.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Horizun.Core.Tests
{
    public class ResponseSummaryRulesTests
    {
        private readonly ITestOutputHelper _out;
        public ResponseSummaryRulesTests(ITestOutputHelper output) => _out = output;

        // ---- a create_elements apply reply shaped like CreateElementsGeometry.ReadCreated ----

        private static JObject Row(int index, long id, bool verified = true, bool sourceMatches = true)
        {
            var properties = new JArray();
            foreach (string p in new[] { "kind", "type_id", "level_id", "level_elevation", "start_x", "start_y", "end_x", "end_y", "height", "base_offset" })
                properties.Add(new JObject
                {
                    ["property"] = p, ["requested"] = 1234.5678, ["found_in_committed_model"] = 1234.5678,
                    ["matches"] = verified, ["tolerance"] = 0.001, ["unit"] = "feet",
                    ["how_read"] = "re-read from the committed element after the transaction group assimilated"
                });
            return new JObject
            {
                ["index"] = index, ["kind"] = "wall", ["element_id"] = id, ["unique_id"] = "uid-" + id,
                ["present_after_commit"] = true, ["verified"] = verified,
                ["postconditions"] = new JObject
                {
                    ["all_verified"] = verified, ["checked"] = 10, ["all_measured"] = true,
                    ["required"] = new JArray(), ["missing"] = new JArray(), ["unexpected"] = new JArray(),
                    ["verified_means"] = "true only when EXACTLY the required properties were checked - each once, none missing, none substituted - and every one of them was RE-READ from the committed model and matched.",
                    ["properties"] = properties
                },
                ["source_comparison"] = sourceMatches ? null : new JObject { ["matches"] = false }
            };
        }

        private static JObject Applied(int count, JObject spatial = null)
        {
            var rows = new JArray();
            for (int i = 0; i < count; i++) rows.Add(Row(i, 500000 + i));
            var data = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = "Committed", ["requested"] = count,
                ["created_verified"] = count, ["rows"] = rows,
                ["verification"] = new JObject { ["intended"] = count, ["actual"] = count, ["verified"] = true }
            };
            if (spatial != null) data["spatial_check"] = spatial;
            return data;
        }

        private static int Bytes(JToken t) => System.Text.Encoding.UTF8.GetByteCount(t.ToString(Formatting.None));

        [Fact]
        public void Seventy_five_clean_walls_collapse_to_counts_and_ids_and_shrink_the_reply()
        {
            JObject data = Applied(75);
            int before = Bytes(data);
            ResponseSummaryRules.CreateElements(data);
            int after = Bytes(data);
            _out.WriteLine("create_elements 75 walls: " + before + " -> " + after + " bytes");

            Assert.Empty((JArray)data["rows"]);
            var s = (JObject)data["rows_summary"]["rows"];
            Assert.Equal(75, (int)s["total"]);
            Assert.Equal(75, (int)s["by_status"]["verified_clean"]);
            Assert.Equal(75, (int)s["by_kind"]["wall"]);
            Assert.Equal(75, ((JArray)s["collapsed_element_ids"]).Count);
            Assert.Equal(500000L, (long)s["collapsed_element_ids"][0]);
            // The verdict is untouched.
            Assert.Equal(75, (int)data["created_verified"]);
            Assert.True((bool)data["verification"]["verified"]);
            // The omission is named the way model_scan names its own.
            Assert.Equal("summary", (string)data["response_mode"]);
            Assert.False((bool)data["response_detail_complete"]);
            var om = (JObject)data["response_omissions"][0];
            Assert.Equal("/rows", (string)om["json_pointer"]);
            Assert.Equal(75, (int)om["omitted"]);
            Assert.Equal("horizun_query_model", (string)data["expand"]["tool"]);
            Assert.True(after * 10 < before, "the summary must be an order of magnitude smaller: " + before + " -> " + after);
        }

        [Fact]
        public void Rows_with_findings_and_rows_the_spatial_check_names_stay_whole()
        {
            JObject data = Applied(5, new JObject
            {
                ["status"] = "warnings",
                ["findings"] = new JArray(new JObject
                {
                    ["severity"] = "warning", ["reason"] = "two wall overlap without being joined",
                    ["a"] = new JObject { ["id"] = 500002 }, ["b"] = new JObject { ["id"] = 999 }
                })
            });
            ((JArray)data["rows"])[4] = Row(4, 500004, sourceMatches: false);

            ResponseSummaryRules.CreateElements(data);

            long[] whole = ((JArray)data["rows"]).Select(r => (long)r["element_id"]).ToArray();
            Assert.Equal(new[] { 500002L, 500004L }, whole);
            Assert.NotNull(data["rows"][0]["postconditions"]["properties"]);   // in FULL, not trimmed
            var s = (JObject)data["rows_summary"]["rows"];
            Assert.Equal(3, (int)s["by_status"]["verified_clean"]);
            Assert.Equal(2, (int)s["by_status"]["verified_with_findings"]);
            Assert.Equal(new[] { 500000L, 500001L, 500003L }, s["collapsed_element_ids"].Select(t => (long)t).ToArray());
            // The spatial check itself is not shaped.
            Assert.Single((JArray)data["spatial_check"]["findings"]);
        }

        [Fact]
        public void A_row_that_did_not_verify_is_never_collapsed()
        {
            Assert.False(ResponseSummaryRules.IsCleanRow(Row(0, 1, verified: false), null));
            var noId = Row(0, 1); noId.Remove("element_id");
            Assert.False(ResponseSummaryRules.IsCleanRow(noId, null));
            var errored = Row(0, 1); errored["error"] = "unreadable";
            Assert.False(ResponseSummaryRules.IsCleanRow(errored, null));
            Assert.True(ResponseSummaryRules.IsCleanRow(Row(0, 1), null));
        }

        [Fact]
        public void A_rehearsal_s_provisional_verification_is_shaped_the_same_way()
        {
            var provisional = new JArray(Row(0, 1), Row(1, 2, verified: false));
            var data = new JObject { ["dry_run"] = true, ["api_rehearsal"] = new JObject { ["provisional_verification"] = provisional } };
            ResponseSummaryRules.CreateElements(data);
            Assert.Single((JArray)data["api_rehearsal"]["provisional_verification"]);
            Assert.Equal(1, (int)data["rows_summary"]["api_rehearsal.provisional_verification"]["by_status"]["not_verified"]);
        }

        [Fact]
        public void Nothing_collapsed_means_the_detail_is_complete()
        {
            var data = new JObject { ["rows"] = new JArray(Row(0, 1, verified: false)) };
            ResponseSummaryRules.CreateElements(data);
            Assert.True((bool)data["response_detail_complete"]);
            Assert.Empty((JArray)data["response_omissions"]);
        }

        // ---- mode parsing --------------------------------------------------------------

        [Fact]
        public void Full_is_the_default_and_anything_else_is_refused()
        {
            Assert.Equal("full", ResponseSummaryRules.ParseMode(new JObject(), out string e0)); Assert.Null(e0);
            Assert.Equal("summary", ResponseSummaryRules.ParseMode(new JObject { ["response_mode"] = "summary" }, out _));
            Assert.Null(ResponseSummaryRules.ParseMode(new JObject { ["response_mode"] = "compact" }, out string e1));
            Assert.Contains("full or summary", e1);
        }

        [Fact]
        public void The_dispatcher_hook_leaves_full_requests_failures_and_other_tools_alone()
        {
            JObject data = Applied(3);
            var ok = CommandResult.Ok(data);
            ResponseSummaryRules.ApplyToResult("horizun_create_elements", "{\"units\":\"mm\"}", ok);
            Assert.Equal(3, ((JArray)((JObject)ok.Data)["rows"]).Count);
            ResponseSummaryRules.ApplyToResult("horizun_transform_elements", "{\"response_mode\":\"summary\"}", ok);
            Assert.Equal(3, ((JArray)((JObject)ok.Data)["rows"]).Count);
            ResponseSummaryRules.ApplyToResult("horizun_create_elements", "{\"response_mode\":\"summary\"}", ok);
            Assert.Empty((JArray)((JObject)ok.Data)["rows"]);
        }

        // ---- clash --------------------------------------------------------------------

        private static JObject Clash(string catA, string srcA, string catB, string srcB, double v, bool cross) => new JObject
        {
            ["a"] = new JObject { ["element_id"] = "481385", ["source_model"] = srcA, ["name"] = "Mitered Elbows / Taps", ["category"] = catA },
            ["b"] = new JObject { ["element_id"] = "124592", ["source_model"] = srcB, ["name"] = "400 x 800mm", ["category"] = catB },
            ["intersection_volume_m3"] = v, ["intersection_volume_is_complete"] = true, ["cross_model"] = cross
        };

        private static JObject ClashReply(bool recorded)
        {
            // The course run's mix: 127 + 15 + 16 + 3 + 4 = 165.
            var rows = new JArray();
            void Add(int n, string a, string sa, string b, string sb, double v0)
            { for (int i = 0; i < n; i++) rows.Add(Clash(a, sa, b, sb, v0 + i * 0.0001, sa != sb)); }
            Add(127, "Ducts", "Sample - MEP.rvt", "Structural Framing", "host", 0.01);
            Add(15, "Ducts", "Sample - MEP.rvt", "Structural Columns", "host", 0.02);
            Add(16, "Pipes", "Sample - MEP.rvt", "Structural Framing", "host", 0.001);
            Add(3, "Pipes", "Sample - MEP.rvt", "Structural Columns", "host", 0.002);
            Add(4, "Ducts", "Sample - MEP.rvt", "Structural Columns", "Sample - MEP.rvt", 0.05);
            ((JObject)rows[60])["intersection_volume_m3"] = 0.52;   // the largest, mid-list
            var data = new JObject
            {
                ["clash_count"] = rows.Count, ["result"] = "partial", ["truncated"] = false,
                ["coverage"] = new JObject { ["complete"] = false }, ["clashes"] = rows,
                ["headline"] = "165 clashes; PARTIAL"
            };
            if (recorded) data["findings"] = new JObject { ["ledger_path"] = "C:/x.json", ["new"] = 165 };
            return data;
        }

        

        

        
    }
}
