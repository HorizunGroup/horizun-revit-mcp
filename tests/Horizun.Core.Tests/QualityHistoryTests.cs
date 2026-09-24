// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The quality history: flattening a scan and an audit into metrics without
// inventing any, the append-only JSONL that counts its malformed lines, the
// trend rows handed to Power BI, and the explanation that narrates only facts.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class QualityHistoryTests
    {
        private static JObject Scan() => JObject.Parse(@"{
            ""complete"": false,
            ""sections_failed"": [""health""],
            ""sections"": {
                ""document"": { ""status"": ""ok"", ""element_count"": 1200, ""file_size_mb"": 55.5, ""title"": ""m"" },
                ""cleanliness"": { ""status"": ""ok"", ""imported_cad"": { ""total"": 3, ""items"": [] },
                                   ""unused"": { ""templates"": { ""total"": 7 } } },
                ""health"": { ""status"": ""failed"", ""reason"": ""boom"" },
                ""links"": { ""status"": ""not_requested"" }
            }
        }");

        [Fact]
        public void A_scan_is_flattened_to_its_own_totals_and_a_failed_section_contributes_nothing()
        {
            JObject m = QualityHistory.SummarizeScan(Scan(), out bool complete, out JArray failed);
            Assert.False(complete);
            Assert.Equal("health", (string)failed.Single());
            Assert.Equal(3.0, (double)m["cleanliness.imported_cad"]);
            Assert.Equal(7.0, (double)m["cleanliness.unused.templates"]);
            Assert.Equal(1200.0, (double)m["document.element_count"]);
            Assert.Equal(55.5, (double)m["document.file_size_mb"]);
            Assert.DoesNotContain(m.Properties(), p => p.Name.StartsWith("health"));
            Assert.DoesNotContain(m.Properties(), p => p.Name.StartsWith("links"));
        }

        [Fact]
        public void An_audit_is_flattened_to_finding_counts_issues_and_the_health_score()
        {
            JObject audit = JObject.Parse(@"{
                ""findings"": [ { ""check"": ""warnings"", ""count"": 42, ""is_issue"": true },
                                { ""check"": ""in_place_families"", ""count"": 0, ""is_issue"": false } ],
                ""checks_failed"": [ { ""check"": ""links"" } ],
                ""health"": { ""score"": 81.5 }
            }");
            JObject m = QualityHistory.SummarizeAudit(audit, out bool complete, out JArray failed);
            Assert.False(complete);
            Assert.Equal("links", (string)failed.Single());
            Assert.Equal(42.0, (double)m["finding.warnings"]);
            Assert.Equal(1.0, (double)m["issue.warnings"]);
            Assert.Equal(0.0, (double)m["issue.in_place_families"]);
            Assert.Equal(81.5, (double)m["health.score"]);
        }

        [Theory]
        [InlineData("Tower A.rvt", "Tower_A")]
        [InlineData("..\\..\\evil", "evil")]
        [InlineData("", "default")]
        [InlineData(null, "default")]
        public void The_project_key_is_a_safe_file_name(string raw, string expected)
            => Assert.Equal(expected, QualityHistory.ProjectKey(raw));

        [Fact]
        public void Append_then_read_returns_the_record_exactly_and_counts_malformed_lines()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hz-quality-" + Guid.NewGuid().ToString("N"));
            string path = QualityHistory.PathFor(dir, "p1");
            try
            {
                JObject r1 = QualityHistory.Record(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "p1",
                    new JObject { ["title"] = "m" }, "model_scan", true, new JArray(), new JObject { ["a"] = 1.0 });
                QualityHistory.Append(path, r1);
                File.AppendAllText(path, "{ not json\n");
                JObject r2 = QualityHistory.Record(new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc), "p1",
                    new JObject { ["title"] = "m" }, "model_scan", false, new JArray("x"), new JObject { ["a"] = 3.0, ["b"] = 2.0 });
                QualityHistory.Append(path, r2);

                QualityReadResult read = QualityHistory.Read(path);
                Assert.True(read.FileExists);
                Assert.Equal(2, read.Records.Count);
                Assert.Equal(1, read.MalformedLines);
                Assert.True(JToken.DeepEquals(r2, read.Records[1]));
                Assert.Equal("2026-09-01T00:00:00Z", (string)read.Records[0]["recorded_utc"]);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void A_missing_history_is_not_an_empty_one()
        {
            QualityReadResult r = QualityHistory.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl"));
            Assert.False(r.FileExists);
            Assert.Empty(r.Records);
        }

        [Fact]
        public void Trend_rows_are_wide_ordered_by_date_filterable_and_a_missing_metric_is_null()
        {
            var records = new[]
            {
                QualityHistory.Record(new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc), "p", new JObject { ["title"] = "m" },
                    "model_scan", true, null, new JObject { ["cleanliness.x"] = 3.0, ["doc.y"] = 1.0 }),
                QualityHistory.Record(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "p", new JObject { ["title"] = "m" },
                    "model_scan", false, null, new JObject { ["cleanliness.x"] = 5.0 })
            };
            var rows = QualityHistory.Rows(records, new[] { "cleanliness.*" }, out var columns);
            Assert.Equal(new[] { "cleanliness.x" }, columns.ToArray());
            Assert.Equal("2026-09-01T00:00:00Z", (string)rows[0]["recorded_utc"]);
            Assert.Equal(5.0, (double)rows[0]["cleanliness.x"]);
            Assert.False((bool)rows[0]["complete"]);

            var all = QualityHistory.Rows(records, null, out var allColumns);
            Assert.Equal(JTokenType.Null, all[0]["doc.y"].Type);
            string csv = QualityHistory.Csv(all, allColumns);
            Assert.StartsWith("recorded_utc,project,document,version_guid,source,complete,cleanliness.x,doc.y", csv);
            Assert.Equal(3, csv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length);
        }

        [Fact]
        public void The_narrative_states_only_present_facts_and_names_absences()
        {
            JObject facts = JObject.Parse(@"{
                ""title"": ""m"", ""model_elements"": 10,
                ""by_discipline"": { ""architecture"": 8, ""structure"": 2 },
                ""by_category"": { ""Walls"": 6, ""Floors"": 2, ""Structural Columns"": 2 },
                ""by_level"": { ""L1"": 10 },
                ""links"": { ""total"": 0, ""loaded"": 0 },
                ""worksets"": { ""workshared"": true, ""user"": 3, ""open"": 2 },
                ""phases"": [ ""Existing"", ""New"" ],
                ""last_quality"": { ""status"": ""none_recorded"" }
            }");
            JObject n = ModelExplainRules.Narrate(facts);
            string en = (string)n["en"], es = (string)n["es"];
            Assert.Contains("10 model elements", en);
            Assert.Contains("architecture (8)", en);
            Assert.Contains("NOT counted", en);
            Assert.Contains("No Revit links", en);
            Assert.Contains("No quality run is recorded", en);
            Assert.Contains("NO se contaron", es);
        }

        [Fact]
        public void Last_quality_picks_the_latest_record()
        {
            var h = new QualityReadResult { FileExists = true };
            h.Records.Add(QualityHistory.Record(new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc), "p", null, "audit_model", true, null, null));
            h.Records.Add(QualityHistory.Record(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "p", null, "model_scan", true, null, null));
            JObject last = ModelExplainRules.LastQuality(h);
            Assert.Equal("recorded", (string)last["status"]);
            Assert.Equal("audit_model", (string)last["source"]);
            Assert.Equal(2, (int)last["records_in_history"]);
        }
    }
}
