// -----------------------------------------------------------------------------
// Horizun Server tests - original Horizun code.
//
// horizun_job_status output_page=N: the pages of a large Python __output__, read from disk
// without Revit. The file is found by job id ONLY (never by a path a record names), and the
// page size is the one it was cut with (max_output_chars, else 20,000).
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    public class JobOutputPageTests : IDisposable
    {
        private readonly string _root;
        private readonly string _saved;

        public JobOutputPageTests()
        {
            _saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            _root = Path.Combine(Path.GetTempPath(), "hz-outpages-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _saved);
            try { Directory.Delete(_root, true); } catch { }
        }

        private string Save(string jobId, string text, int pageChars = OutputPages.PageChars)
        {
            string path = OutputPages.FilePath(HorizunPaths.JobsDir(), jobId, pageChars);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            return path;
        }

        private void Record(string jobId, bool finished)
        {
            Directory.CreateDirectory(HorizunPaths.JobsDir());
            string body = "{\"event\":\"start\",\"tool\":\"horizun_execute_python\"}\n" + (finished ? "{\"event\":\"finish\",\"status\":\"ok\"}\n" : "");
            File.WriteAllText(Path.Combine(HorizunPaths.JobsDir(), jobId + ".jsonl"), body);
        }

        [Theory]
        [InlineData(OutputPages.PageChars)]
        [InlineData(50000)]
        [InlineData(1024)]
        public void Every_page_is_served_in_order_with_the_size_it_was_cut_with_and_joins_back(int pageChars)
        {
            string text = "[" + string.Join(",", Enumerable.Range(0, 9000).Select(i => "\"element-" + i + "\"")) + "]";
            Save("job-big", text, pageChars);
            int pages = OutputPages.PageCount(text.Length, pageChars);
            Assert.True(pages >= 2);

            var joined = new System.Text.StringBuilder();
            for (int p = 1; p <= pages; p++)
            {
                JObject r = JobStatus.Handle(new JObject { ["job_id"] = "job-big", ["output_page"] = p });
                Assert.Equal(pages, (int)r["pages"]);
                Assert.Equal(pageChars, (int)r["page_chars"]);
                Assert.Equal(p, (int)r["output_page"]);
                Assert.Equal(text.Length, (int)r["total_chars"]);
                Assert.Equal(p == pages, (bool)r["last_page"]);
                Assert.Null(r["complete"]);                                    // one name for "last page", not two
                Assert.True(((string)r["text"]).Length <= pageChars + 1);
                if (p < pages) Assert.Equal("horizun_job_status job_id=job-big output_page=" + (p + 1), (string)r["next_page_with"]);
                else Assert.Equal(JTokenType.Null, r["next_page_with"].Type);
                joined.Append((string)r["text"]);
            }
            Assert.Equal(text, joined.ToString());
            Assert.Equal(9000, JArray.Parse(joined.ToString()).Count);
        }

        [Fact]
        public void A_page_past_the_end_and_a_path_like_id_are_refused()
        {
            Save("job-small", new string('x', 30000));
            Assert.Throws<ArgumentException>(() => JobStatus.Handle(new JObject { ["job_id"] = "job-small", ["output_page"] = 3 }));
            Assert.Throws<ArgumentException>(() => JobStatus.Handle(new JObject { ["job_id"] = @"..\..\secret", ["output_page"] = 1 }));
            Assert.Throws<ArgumentException>(() => JobStatus.Handle(new JObject { ["output_page"] = 1 }));
            Assert.Throws<ArgumentException>(() => JobStatus.Handle(new JObject { ["job_id"] = "job-small", ["output_page"] = 0 }));
        }

        [Theory]
        [InlineData("cancel")]
        [InlineData("limit")]
        [InlineData("checkpoints")]
        public void output_page_cannot_be_combined_with_cancel_limit_or_checkpoints(string other)
        {
            Save("job-c", new string('x', 30000));
            JToken value = other == "cancel" ? (JToken)true : 3;
            Assert.Throws<ArgumentException>(() => JobStatus.Handle(new JObject { ["job_id"] = "job-c", ["output_page"] = 1, [other] = value }));
        }

        [Fact]
        public void Null_and_default_values_of_those_arguments_count_as_absent()
        {
            Save("job-d", new string('x', 30000));
            var args = new JObject
            {
                ["job_id"] = "job-d", ["output_page"] = 1,
                ["cancel"] = false, ["limit"] = JValue.CreateNull(), ["checkpoints"] = JValue.CreateNull()
            };
            Assert.Equal(1, (int)JobStatus.Handle(args)["output_page"]);
            var defaults = new JObject { ["job_id"] = "job-d", ["output_page"] = 2, ["limit"] = 5, ["checkpoints"] = 10 };
            Assert.Equal(2, (int)JobStatus.Handle(defaults)["output_page"]);
        }

        [Fact]
        public void A_missing_output_explains_which_of_expired_running_or_finished_it_is()
        {
            var expired = Assert.Throws<FileNotFoundException>(() => JobStatus.Handle(new JObject { ["job_id"] = "job-none", ["output_page"] = 1 }));
            Assert.Contains("no record", expired.Message);

            Record("job-run", finished: false);
            var running = Assert.Throws<FileNotFoundException>(() => JobStatus.Handle(new JObject { ["job_id"] = "job-run", ["output_page"] = 1 }));
            Assert.Contains("still running", running.Message);

            Record("job-fin", finished: true);
            var finished = Assert.Throws<FileNotFoundException>(() => JobStatus.Handle(new JObject { ["job_id"] = "job-fin", ["output_page"] = 1 }));
            Assert.Contains("fitted in one page", finished.Message);
        }

        [Fact]
        public void A_saved_file_over_the_kept_size_is_refused_before_it_is_read()
        {
            string path = Save("job-huge", "x");
            using (var fs = new FileStream(path, FileMode.Open)) fs.SetLength(OutputPages.MaxPagedBytes + 1);
            var ex = Assert.Throws<InvalidDataException>(() => JobStatus.Handle(new JObject { ["job_id"] = "job-huge", ["output_page"] = 1 }));
            Assert.Contains("Nothing was read", ex.Message);
        }

        [Fact]
        public void The_server_and_the_add_in_name_the_probe_field_the_same()
        {
            Assert.Equal(ProbeFlag.Field, PipeClient.ProbeField);
        }
    }
}
