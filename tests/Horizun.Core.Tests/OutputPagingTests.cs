// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// A large __output__ is paged, not poured into the client's context (field 2026-11:
// about 130,000 characters for one call). The page size is max_output_chars when the caller
// sent it, else 20,000. Page 1 comes back as text, the whole is saved, the run's status is
// classified from the WHOLE, and the pages join back to exactly the JSON the script produced.
// A failed save never costs the output of a run that already happened.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Contracts;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class OutputPagingTests
    {
        private const int Hard = Contract.MaxScriptTextChars;     // 262,144: what still fits inline

        private static Dictionary<string, object> Big(int rows)
        {
            var list = new List<object>();
            for (int i = 0; i < rows; i++) list.Add(new Dictionary<string, object> { ["id"] = i, ["name"] = "element-" + i });
            return new Dictionary<string, object>
            {
                ["status"] = "verified",
                ["verification"] = new Dictionary<string, object> { ["checked"] = true, ["evidence"] = new List<object> { "ids re-read" } },
                ["rows"] = list
            };
        }

        [Fact]
        public void An_output_over_one_page_comes_back_as_page_one_and_is_saved_whole()
        {
            string saved = null;
            var r = ScriptOutput.Render(Big(4000), Hard, json => { saved = json; return @"C:\jobs\outputs\j1.20000.json"; }, OutputPages.PageChars);

            Assert.Equal("paged", r.Kind);
            Assert.Equal(JTokenType.String, r.Value.Type);                       // text, never a half structure
            Assert.Equal(OutputPages.PageChars, ((string)r.Value).Length);
            Assert.True(saved.Length > 100000);                                  // the field case was ~130,000
            Assert.Equal(saved.Length, r.OriginalChars);
            Assert.Equal(OutputPages.PageCount(saved.Length), r.Pages);
            Assert.Equal(@"C:\jobs\outputs\j1.20000.json", r.FullOutputPath);
            Assert.Contains("page 1 of", r.Note);
            Assert.NotNull(r.FullValue);
        }

        [Fact]
        public void The_pages_join_back_to_the_exact_json()
        {
            string saved = null;
            ScriptOutput.Render(Big(3000), Hard, json => { saved = json; return "p"; }, OutputPages.PageChars);
            int pages = OutputPages.PageCount(saved.Length);
            string joined = string.Concat(Enumerable.Range(1, pages).Select(p => OutputPages.Slice(saved, p)));
            Assert.Equal(saved, joined);
            Assert.Equal(string.Empty, OutputPages.Slice(saved, pages + 1));
            Assert.NotNull(JObject.Parse(joined));
        }

        [Fact]
        public void A_surrogate_pair_is_never_split_between_pages()
        {
            string text = new string('a', OutputPages.PageChars - 1) + "\U0001F600" + new string('b', 50);
            string first = OutputPages.Slice(text, 1);
            string second = OutputPages.Slice(text, 2);
            Assert.False(char.IsHighSurrogate(first[^1]));
            Assert.False(char.IsLowSurrogate(second[0]));
            Assert.Equal(text, first + second);
        }

        [Fact]
        public void Paging_does_not_cost_the_evidence_status()
        {
            var r = ScriptOutput.Render(Big(4000), Hard, json => "p", OutputPages.PageChars);
            // Classified from the page text it would be completed_unverified; from the whole it is what the script declared.
            Assert.Equal("completed_unverified", ScriptEvidence.Classify(r.Value).Status);
            Assert.Equal("self_reported_verified", ScriptEvidence.Classify(r.FullValue).Status);
        }

        [Fact]
        public void An_output_inside_one_page_comes_back_whole_and_unpaged()
        {
            var r = ScriptOutput.Render(Big(10), Hard, json => "never", OutputPages.PageChars);
            Assert.Equal("structure", r.Kind);
            Assert.Null(r.FullValue);
        }

        [Fact]
        public void An_explicit_page_size_is_the_threshold_and_the_page_it_neither_turns_paging_off_nor_withholds()
        {
            // The case of a client that sends 50,000 for a 130,000 output: pages of 50,000, not too_large.
            string saved = null;
            var r = ScriptOutput.Render(Big(4000), Hard, json => { saved = json; return "p"; }, 50000);
            Assert.Equal("paged", r.Kind);
            Assert.Equal(50000, ((string)r.Value).Length);
            Assert.Equal(50000, r.PageChars);
            Assert.Equal(OutputPages.PageCount(saved.Length, 50000), r.Pages);
            // Sending the largest value pages only an output bigger than it.
            Assert.Equal("structure", ScriptOutput.Render(Big(4000), Hard, json => "never", Hard).Kind);
        }

        [Fact]
        public void When_the_output_cannot_be_saved_but_still_fits_it_is_returned_whole()
        {
            var r = ScriptOutput.Render(Big(4000), Hard, json => throw new System.IO.IOException("disk full"), OutputPages.PageChars);
            Assert.Equal("structure", r.Kind);
            Assert.Equal(JTokenType.Object, r.Value.Type);
            Assert.Equal(4000, ((JArray)r.Value["rows"]).Count);
        }

        [Fact]
        public void When_it_cannot_be_saved_and_does_not_fit_it_is_withheld_without_inviting_a_rerun()
        {
            var r = ScriptOutput.Render(Big(9000), Hard, json => throw new System.IO.IOException("disk full"), OutputPages.PageChars);
            Assert.Equal("too_large", r.Kind);
            Assert.Equal(JTokenType.Null, r.Value.Type);
            Assert.Contains("disk full", r.Note);
            Assert.Contains("ALREADY RAN", r.Note);
            Assert.Contains("Do NOT run it again", r.Note);
            Assert.Contains("typed reads", r.Note);
            // ...and the status of the run is still classified from the complete output.
            Assert.Equal("self_reported_verified", ScriptEvidence.Classify(r.FullValue).Status);
        }

        [Fact]
        public void Above_four_megabytes_it_is_still_saved_whole_but_not_by_pages_and_not_lost()
        {
            int pagedSaves = 0, fullSaves = 0;
            string fullJson = null;
            var huge = new Dictionary<string, object> { ["status"] = "verified", ["data"] = new string('x', 5 * 1024 * 1024) };
            var r = ScriptOutput.Render(huge, Hard, json => { pagedSaves++; return "paged.json"; }, OutputPages.PageChars,
                                        json => { fullSaves++; fullJson = json; return @"C:\jobs\outputs\j9.full.json"; });
            Assert.Equal("too_large", r.Kind);
            Assert.Equal(0, pagedSaves);                                  // not as pages...
            Assert.Equal(1, fullSaves);                                   // ...but SAVED, whole
            Assert.Equal(@"C:\jobs\outputs\j9.full.json", r.FullOutputPath);
            Assert.Equal(r.OriginalChars, fullJson.Length);
            Assert.Contains("open the file", r.Note);
            Assert.Contains("output_page reads", r.Note);
            Assert.Contains("Do NOT run it again", r.Note);
            Assert.NotNull(r.FullValue);
        }

        [Fact]
        public void The_over_four_megabyte_file_is_named_apart_so_output_page_never_finds_it()
        {
            Assert.Equal("j9.full.json", OutputPages.FullFileName("j9"));
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hz-fullname-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                System.IO.Directory.CreateDirectory(OutputPages.Folder(root));
                System.IO.File.WriteAllText(System.IO.Path.Combine(OutputPages.Folder(root), OutputPages.FullFileName("j9")), "x");
                Assert.Null(OutputPages.Find(root, "j9", out int size));
                Assert.Equal("j9", OutputPages.JobIdOfFile(OutputPages.FullFileName("j9")));
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void A_failed_save_that_returns_the_output_whole_says_why_and_that_the_page_size_was_ignored()
        {
            var r = ScriptOutput.Render(Big(4000), Hard, json => throw new System.IO.IOException("disk full"), 50000);
            Assert.Equal("structure", r.Kind);
            Assert.Equal("disk full", r.PersistenceError);
            Assert.Contains("disk full", r.Note);
            Assert.Contains("50000", r.Note);
            Assert.Contains("ignored", r.Note);
        }

        [Fact]
        public void A_job_id_can_only_be_a_bare_file_name_without_dots()
        {
            Assert.True(OutputPages.ValidJobId("20261118-120102-345-ab12cd"));
            Assert.False(OutputPages.ValidJobId(@"..\x"));
            Assert.False(OutputPages.ValidJobId("a/b"));
            Assert.False(OutputPages.ValidJobId("C:evil"));
            Assert.False(OutputPages.ValidJobId("a.b"));
            Assert.False(OutputPages.ValidJobId(""));
        }

        [WindowsFact]
        public void Files_are_named_by_job_and_page_size_and_belong_to_their_job()
        {
            Assert.Equal("job-1.50000.json", OutputPages.FileName("job-1", 50000));
            Assert.Equal("job-1", OutputPages.JobIdOfFile(@"C:\x\outputs\job-1.50000.json"));
            Assert.Equal("job-1", OutputPages.JobIdOfFile("job-1.50000.ab12cd34.tmp"));
        }

        [Fact]
        public void The_missing_output_says_a_different_thing_for_expired_running_and_finished_jobs()
        {
            string expired = OutputPages.MissingReason("j", false, false);
            string running = OutputPages.MissingReason("j", true, false);
            string finished = OutputPages.MissingReason("j", true, true);
            Assert.Contains("expired", expired);
            Assert.DoesNotContain("horizun_job_status", expired);              // there is no record to point at
            Assert.Contains("still running", running);
            Assert.Contains("fitted in one page", finished);
            Assert.NotEqual(expired, running);
            Assert.NotEqual(running, finished);
        }

        [Fact]
        public void The_output_only_shape_keeps_the_paging_block_and_the_status()
        {
            var reply = new JObject
            {
                ["output"] = "{\"a\":", ["output_kind"] = "paged", ["output_note"] = "n", ["evidence_status"] = "self_reported_verified",
                ["output_paging"] = new JObject { ["pages"] = 7 }
            };
            JObject only = PythonReplyCompaction.OutputOnly(reply);
            Assert.Equal("paged", (string)only["output_kind"]);
            Assert.Equal(7, (int)only["output_paging"]["pages"]);
            Assert.Equal("self_reported_verified", (string)only["evidence_status"]);
        }

        [Fact]
        public void Compact_mode_bounds_the_host_observation_rows_and_keeps_the_totals()
        {
            var rows = new JArray(Enumerable.Range(1, 400).Select(i => (JToken)new JObject { ["reported_id"] = i }));
            var reply = new JObject { ["host_observations"] = new JObject { ["reported"] = 400, ["checked"] = 400, ["rows"] = rows } };
            PythonReplyCompaction.Compact(reply);
            var obs = (JObject)reply["host_observations"];
            Assert.Equal(PythonReplyCompaction.MaxListedObservationRows, ((JArray)obs["rows"]).Count);
            Assert.Equal(400, (int)obs["rows_total"]);
            Assert.Equal(350, (int)obs["rows_omitted_in_compact"]);
            Assert.Equal(400, (int)obs["checked"]);
            PythonReplyCompaction.Compact(reply);                              // idempotent
            Assert.Equal(PythonReplyCompaction.MaxListedObservationRows, ((JArray)((JObject)reply["host_observations"])["rows"]).Count);
        }
    }
}
