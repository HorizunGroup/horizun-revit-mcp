// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// THE GAP (course rehearsal, 2026-10-03). horizun_budget_compare refuses a
// truncated takeoff, and a real structural model gave 905 rows: the only routes
// were all of them through the agent's context, or a Python export. rows_file
// writes the complete reply to the bridge's own folder; these tests prove that
// file is what budget_compare accepts - even when the inline reply was capped.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public sealed class TakeoffRowsFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hz-takeoff-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        private static JObject Row(int id, string code) => new JObject
        {
            ["element_id"] = id.ToString(),
            ["document"] = "Mirador.rvt",
            ["link_instance_id"] = null,
            ["classification_code"] = code,
            ["quantities"] = new JObject
            {
                ["volume"] = new JObject { ["value"] = 1.5, ["state"] = "measured", ["unit"] = "m3", ["reason"] = null }
            }
        };

        private static (JObject reply, JArray all) CappedReply(int total, int top)
        {
            var all = new JArray();
            for (int i = 1; i <= total; i++) all.Add(Row(i, i % 2 == 0 ? "C-01" : "C-02"));
            var shown = new JArray();
            for (int i = 0; i < top; i++) shown.Add(all[i].DeepClone());
            var reply = new JObject
            {
                ["mode"] = "takeoff",
                ["classification_parameter"] = "HRZ_COD_PRES",
                ["rows"] = shown,
                ["rows_matching"] = total,
                ["shown"] = top,
                ["top"] = top,
                ["truncated"] = true,
                ["truncated_note"] = "shortened"
            };
            return (reply, all);
        }

        [Fact]
        public void The_inline_reply_is_refused_and_the_file_holds_every_row_budget_compare_accepts()
        {
            var (reply, all) = CappedReply(total: 905, top: 200);
            string problem;
            Assert.Null(BudgetComparisonRules.ReadModelRows(reply, "classification_code", out problem));

            JObject block = TakeoffRowsFile.Write(_dir, TakeoffRowsFile.Document(reply, all),
                                                  new DateTime(2026, 10, 3, 15, 4, 5, DateTimeKind.Utc), Guid.NewGuid());
            Assert.True((bool)block["written"], (string)block["error"]);
            Assert.Equal(905, (int)block["rows"]);

            string path = (string)block["path"];
            var fromDisk = JToken.Parse(File.ReadAllText(path, Encoding.UTF8));
            var rows = BudgetComparisonRules.ReadModelRows(fromDisk, "classification_code", out problem);
            Assert.Null(problem);
            Assert.Equal(905, rows.Count);
            Assert.False((bool)fromDisk["truncated"]);
            Assert.Equal(905, (int)fromDisk["shown"]);
            Assert.Equal(905, (int)fromDisk["rows_matching"]);
            Assert.Null(fromDisk["rows_file"]);

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(bytes.LongLength, (long)block["bytes"]);
            using (var sha = SHA256.Create())
                Assert.Equal(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(),
                             (string)block["sha256"]);
            Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
        }

        [Fact]
        public void The_document_does_not_mutate_the_inline_reply()
        {
            var (reply, all) = CappedReply(total: 10, top: 3);
            TakeoffRowsFile.Document(reply, all);
            Assert.True((bool)reply["truncated"]);
            Assert.Equal(3, ((JArray)reply["rows"]).Count);
        }

        [Fact]
        public void File_names_sort_by_time_and_do_not_collide_within_a_second()
        {
            var t = new DateTime(2026, 10, 3, 15, 4, 5, DateTimeKind.Utc);
            string a = TakeoffRowsFile.FileName(t, Guid.NewGuid());
            string b = TakeoffRowsFile.FileName(t, Guid.NewGuid());
            Assert.StartsWith("takeoff-20261003T150405Z-", a);
            Assert.EndsWith(".json", a);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void A_failed_write_is_reported_not_thrown()
        {
            var (reply, all) = CappedReply(total: 4, top: 4);
            // A FILE where the directory should be: CreateDirectory fails.
            Directory.CreateDirectory(_dir);
            string blocker = Path.Combine(_dir, "not-a-dir");
            File.WriteAllText(blocker, "x");

            JObject block = TakeoffRowsFile.Write(blocker, TakeoffRowsFile.Document(reply, all), DateTime.UtcNow, Guid.NewGuid());
            Assert.False((bool)block["written"]);
            Assert.False(string.IsNullOrEmpty((string)block["error"]));
            Assert.Contains("NOT written", (string)block["means"]);
        }
    }
}
