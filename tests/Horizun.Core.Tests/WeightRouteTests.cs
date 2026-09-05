// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE WHOLE ROUTE, not the calculator.
//
// WeightAttributionTests proves the arithmetic. This proves the journey:
// the shapes the scan emitters actually build -> WeightAttributionFromScan ->
// WeightAttributionRules -> the reply. Every fixture below is the real shape of
// a real section, so a test here fails when an emitter and the rule stop
// agreeing about a key - which is the failure a pure-calculator test cannot see.
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
    public class WeightRouteTests
    {
        private static readonly string[] AllSections =
        {
            "document", "categories", "cleanliness", "naming", "documentation",
            "project_info", "health", "links", "worksets", "design_options", "lines", "types", "weight"
        };

        /// <summary>A bucket in the shape ScanPagingContext actually builds.</summary>
        private static JObject Bucket(int total, bool exact = true, params string[] ids) => new JObject
        {
            ["total"] = total,
            ["returned"] = ids.Length,
            ["truncated"] = ids.Length < total,
            ["total_is_exact"] = exact,
            ["limit"] = 50,
            ["items"] = new JArray(ids.Select(i => (JToken)new JObject { ["id"] = i })),
        };

        /// <summary>A scan reply with every section present and ok.</summary>
        private static JObject Sections(Action<JObject> tweak = null)
        {
            var s = new JObject
            {
                ["cleanliness"] = new JObject
                {
                    ["status"] = "ok",
                    ["families_in_place"] = Bucket(12, true, "101", "102"),
                    ["group_types_total"] = 40,
                    ["group_types_orphan"] = Bucket(3, true, "201"),
                    ["group_instances_nested"] = 7,
                    ["cad_imported"] = Bucket(2, true, "301"),
                    ["cad_linked"] = Bucket(5, true, "401"),
                    ["raster_images"] = 9,
                    ["point_clouds"] = 1,
                    ["line_patterns_import"] = 30,
                    ["fill_patterns_import"] = 11,
                    ["view_templates_total"] = 18,
                    ["view_templates_unused"] = Bucket(4, true, "501"),
                    ["filters_unused"] = Bucket(6, true, "601"),
                    ["mep_curves_without_system"] = 22,
                },
                ["types"] = new JObject { ["status"] = "ok", ["family_symbols_no_instances"] = 340 },
                ["lines"] = new JObject { ["status"] = "ok", ["model_lines"] = 9000 },
                ["links"] = new JObject { ["status"] = "ok", ["rvt_link_instances"] = 8 },
                ["documentation"] = new JObject { ["status"] = "ok", ["views_not_on_sheet"] = Bucket(120, true, "701") },
                ["health"] = new JObject { ["status"] = "ok", ["warnings_total"] = 412 },
                ["worksets"] = new JObject { ["status"] = "ok", ["user_worksets"] = 6 },
                ["design_options"] = new JObject { ["status"] = "ok", ["design_options"] = Bucket(2, true, "801") },
                ["categories"] = new JObject { ["status"] = "ok", ["by_category"] = Bucket(35, true, "Walls") },
            };
            tweak?.Invoke(s);
            return s;
        }

        private static WeightProfile Profile(string json) =>
            WeightAttributionRules.ReadProfile(JToken.Parse(json), WeightAttributionFromScan.Kinds);

        private static JObject Run(JObject sections, string profileJson = null,
                                   IReadOnlyCollection<string> requested = null)
        {
            List<Contributor> built = WeightAttributionFromScan.Build(sections, requested ?? AllSections);
            WeightProfile wp = profileJson == null
                ? WeightAttributionRules.ReadProfile(null, WeightAttributionFromScan.Kinds)
                : Profile(profileJson);
            return WeightAttributionFromScan.ToJson(WeightAttributionRules.Attribute(built, wp), built);
        }

        private static JObject Find(JObject reply, string id)
        {
            JToken t = reply["candidates"].Concat(reply["not_assessable"])
                .FirstOrDefault(c => (string)c["id"] == id);
            Assert.True(t != null, "no candidate '" + id + "'");
            return (JObject)t;
        }

        // ------------------------------------------------- the route carries facts

        [Fact]
        public void Every_contributor_the_scan_can_measure_arrives_with_its_number()
        {
            JObject r = Run(Sections(), @"{ ""version"": ""v1"", ""weights"": { ""model_lines"": 1 } }");

            Assert.Equal(12, Find(r, "in_place_families").Value<int>("observed_value"));
            Assert.Equal(340, Find(r, "types_without_instances").Value<int>("observed_value"));
            Assert.Equal(7, Find(r, "nested_groups").Value<int>("observed_value"));
            Assert.Equal(2, Find(r, "imported_cad").Value<int>("observed_value"));
            Assert.Equal(9, Find(r, "raster_images").Value<int>("observed_value"));
            Assert.Equal(1, Find(r, "point_clouds").Value<int>("observed_value"));
            Assert.Equal(9000, Find(r, "model_lines").Value<int>("observed_value"));
            Assert.Equal(8, Find(r, "revit_link_instances").Value<int>("observed_value"));
            Assert.Equal(120, Find(r, "views_not_on_sheet").Value<int>("observed_value"));
            Assert.Equal(412, Find(r, "warnings").Value<int>("observed_value"));
            Assert.Equal(22, Find(r, "mep_without_system").Value<int>("observed_value"));
            Assert.Equal(2, Find(r, "design_options").Value<int>("observed_value"));
        }

        [Fact]
        public void Every_candidate_carries_the_whole_record_a_reader_needs()
        {
            JObject r = Run(Sections(), @"{ ""version"": ""org-7"", ""weights"": { ""warnings"": 2 } }");
            JObject w = Find(r, "warnings");

            foreach (string field in new[]
            {
                "id", "name", "category", "evidence_class", "observed_value", "unit",
                "examined_count", "total_count", "total_is_exact", "coverage", "confidence",
                "evidence", "explanation", "limitations", "recommendation",
                "ranking_contribution", "why_it_ranks",
            })
                Assert.True(w[field] != null, "missing field: " + field);

            Assert.Equal("org-7", r.Value<string>("profile_version"));
            Assert.Equal(824, w.Value<int>("ranking_contribution"));   // 412 x 2
        }

        [Fact]
        public void The_extractor_producing_a_fact_the_rule_does_not_read_is_visible()
        {
            // THE WIRING FAILURE. If an emitter renames its key, the rule stops
            // receiving the fact - and this test says so instead of the reply quietly
            // showing not_assessable for something the model does have.
            JObject s = Sections(x => ((JObject)x["cleanliness"])["raster_images_RENAMED"] =
                ((JObject)x["cleanliness"])["raster_images"]);
            ((JObject)s["cleanliness"]).Remove("raster_images");

            JObject r = Run(s, @"{ ""version"": ""v1"", ""weights"": {} }");
            JObject img = Find(r, "raster_images");
            Assert.Equal(ContributorStatus.NotAssessable, img.Value<string>("status"));
            Assert.Contains("does not measure it yet", img.Value<string>("limitations"));
        }

        [Fact]
        public void A_collector_that_failed_leaves_null_and_is_never_read_as_zero()
        {
            // The emitter writes null plus an _error when its collector throws. Zero
            // would sort the contributor last and read as "there are none of those".
            JObject s = Sections(x =>
            {
                ((JObject)x["cleanliness"])["point_clouds"] = JValue.CreateNull();
                ((JObject)x["cleanliness"])["point_clouds_error"] = "the collector threw";
            });

            JObject r = Run(s, @"{ ""version"": ""v1"", ""weights"": { ""point_clouds"": 100 } }");
            JObject pc = Find(r, "point_clouds");

            Assert.Equal(ContributorStatus.NotAssessable, pc.Value<string>("status"));
            Assert.Equal("none", pc.Value<string>("coverage"));
            Assert.Contains("a failure is not a zero", pc.Value<string>("limitations"));
            Assert.Contains("the collector threw", pc.Value<string>("limitations"));

            // and it is NOT in the ranked list, whatever its weight
            Assert.DoesNotContain(r["candidates"], c => (string)c["id"] == "point_clouds");
        }

        [Fact]
        public void A_section_that_threw_makes_everything_it_would_have_counted_not_assessable()
        {
            JObject s = Sections(x => x["cleanliness"] =
                new JObject { ["status"] = "failed", ["reason"] = "the collector blew up" });

            JObject r = Run(s, @"{ ""version"": ""v1"", ""weights"": {} }");
            foreach (string kind in new[] { "in_place_families", "imported_cad", "raster_images", "nested_groups" })
            {
                JObject c = Find(r, kind);
                Assert.Equal(ContributorStatus.NotAssessable, c.Value<string>("status"));
                Assert.Contains("the collector blew up", c.Value<string>("limitations"));
            }
        }

        [Fact]
        public void A_section_nobody_asked_for_is_not_requested_not_zero_and_not_ok()
        {
            JObject r = Run(Sections(), @"{ ""version"": ""v1"", ""weights"": {} }",
                            requested: new[] { "cleanliness" });
            JObject lines = Find(r, "model_lines");
            Assert.Equal(ContributorStatus.NotRequested, lines.Value<string>("status"));
            Assert.Contains("not the same as there being none", lines.Value<string>("limitations"));
        }

        [Fact]
        public void A_truncated_bucket_still_reports_the_exact_population()
        {
            // `returned` is what the caller got; `total` is what exists. A page that
            // showed 2 of 12 must not make the contributor look like 2.
            JObject r = Run(Sections(), @"{ ""version"": ""v1"", ""weights"": { ""in_place_families"": 1 } }");
            JObject f = Find(r, "in_place_families");
            Assert.Equal(12, f.Value<int>("observed_value"));
            Assert.True(f.Value<bool>("total_is_exact"));
            Assert.Equal("complete", f.Value<string>("coverage"));
        }

        [Fact]
        public void A_bucket_whose_total_is_a_lower_bound_says_so_and_stays_ranked()
        {
            JObject s = Sections(x =>
            {
                var b = Bucket(40, false, "1");
                b["unreadable"] = 7;
                b["total_note"] = "a lower bound: 7 could not be read.";
                ((JObject)x["cleanliness"])["families_in_place"] = b;
            });

            JObject r = Run(s, @"{ ""version"": ""v1"", ""weights"": { ""in_place_families"": 2 } }");
            JObject f = Find(r, "in_place_families");

            Assert.Equal(ContributorStatus.LowerBound, f.Value<string>("status"));
            Assert.False(f.Value<bool>("total_is_exact"));
            Assert.Equal("partial", f.Value<string>("coverage"));
            Assert.Contains("LOWER BOUND", f.Value<string>("why_it_ranks"));
            Assert.Contains("lower bound", string.Join(" ", r["limitations"].Select(x => (string)x)));
        }

        [Fact]
        public void Lower_coverage_never_improves_the_ranking()
        {
            // A second scan that read LESS must not look better. The unreadable half
            // leaves the contributor a lower bound and its score no higher.
            JObject full = Run(Sections(), @"{ ""version"": ""v1"", ""weights"": { ""in_place_families"": 3 } }");
            JObject less = Run(Sections(x => ((JObject)x["cleanliness"])["families_in_place"] =
                                   Bucket(4, false, "1")),
                               @"{ ""version"": ""v1"", ""weights"": { ""in_place_families"": 3 } }");

            int a = Find(full, "in_place_families").Value<int>("ranking_contribution");
            int b = Find(less, "in_place_families").Value<int>("ranking_contribution");
            Assert.True(b <= a, "a smaller sample scored higher");
            Assert.Equal("partial", Find(less, "in_place_families").Value<string>("coverage"));
        }

        // ------------------------------------------------------- the epistemic line

        [Fact]
        public void A_rule_of_thumb_is_carried_as_an_indicator_never_as_a_measurement()
        {
            JObject r = Run(Sections(), @"{ ""version"": ""v1"", ""weights"": {} }");
            Assert.Equal(EvidenceClass.Indicator, Find(r, "in_place_families").Value<string>("evidence_class"));
            Assert.Equal(EvidenceClass.Indicator, Find(r, "warnings").Value<string>("evidence_class"));
            Assert.Equal(EvidenceClass.Indicator, Find(r, "nested_groups").Value<string>("evidence_class"));
            // a plain count is measured
            Assert.Equal(EvidenceClass.Measured, Find(r, "model_lines").Value<string>("evidence_class"));
            Assert.Equal(EvidenceClass.Measured, Find(r, "raster_images").Value<string>("evidence_class"));
        }

        [Fact]
        public void Every_candidate_declares_its_class_so_the_contract_cannot_drop_it()
        {
            JObject r = Run(Sections(), @"{ ""version"": ""v1"", ""weights"": {} }");
            foreach (JToken c in r["candidates"].Concat(r["not_assessable"]))
            {
                string cls = (string)c["evidence_class"];
                Assert.False(string.IsNullOrWhiteSpace(cls), (string)c["id"] + " has no evidence_class");
                Assert.Contains(cls, EvidenceClass.All);
            }
        }

        [Fact]
        public void Without_a_profile_there_is_no_ranking_and_no_version_is_invented()
        {
            JObject r = Run(Sections());
            Assert.False(r.Value<bool>("ranked"));
            Assert.Null(r["profile_version"].Type == JTokenType.Null ? null : r.Value<string>("profile_version"));
            Assert.Contains("no weight profile", r.Value<string>("why_not_ranked"));
            Assert.All(r["candidates"], c => Assert.Equal(0, c.Value<int>("ranking_contribution")));
        }

        [Fact]
        public void The_profile_version_travels_with_the_ranking()
        {
            JObject r = Run(Sections(), @"{ ""version"": ""acme-2026-03"", ""weights"": { ""warnings"": 1 } }");
            Assert.True(r.Value<bool>("ranked"));
            Assert.Equal("acme-2026-03", r.Value<string>("profile_version"));
            Assert.Null(r["why_not_ranked"].Type == JTokenType.Null ? null : r.Value<string>("why_not_ranked"));
        }

        [Fact]
        public void No_field_anywhere_reports_bytes_or_a_share_of_the_file()
        {
            JObject r = Run(Sections(), @"{ ""version"": ""v1"", ""weights"": { ""warnings"": 1 } }");
            string s = r.ToString().ToLowerInvariant();
            Assert.Contains("does not publish", r.Value<string>("bytes_are_not_known"));
            foreach (string forbidden in new[] { "\"bytes\"", "\"mb\"", "\"size_mb\"", "\"percent_of_file\"" })
                Assert.DoesNotContain(forbidden, s);
        }

        // --------------------------------------------------------------- the wiring

        private static string ScanSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir.FullName,
                "src", "Horizun.Revit", "Commands", "ModelScanCommand.cs"));
        }

        [Fact]
        public void The_scan_runs_the_weight_section_over_its_own_output_and_last()
        {
            string src = ScanSource();
            Assert.Contains("WeightAttributionFromScan.Build(result, sections)", src);
            Assert.Contains("WeightAttributionRules.ReadProfile(", src);
            Assert.Contains("request[\"weight_profile\"]", src);

            // LAST: it reads what the others produced, so it cannot run before them.
            int weight = src.IndexOf("sections, \"weight\"", StringComparison.Ordinal);
            foreach (string other in new[] { "\"cleanliness\"", "\"types\"", "\"links\"", "\"health\"" })
            {
                int i = src.IndexOf("sections, " + other, StringComparison.Ordinal);
                Assert.True(i > 0 && i < weight, "weight runs before " + other);
            }
        }

        [Fact]
        public void The_weight_section_collects_nothing_of_its_own()
        {
            // Its whole point is that it reads the sections' numbers. A collector here
            // would be a second count of the same population, free to disagree.
            string src = ScanSource();
            int start = src.IndexOf("sections, \"weight\"", StringComparison.Ordinal);
            int end = src.IndexOf("});", start, StringComparison.Ordinal);
            Assert.True(end > start);
            string body = src.Substring(start, end - start);
            Assert.DoesNotContain("FilteredElementCollector", body);
        }

        [Fact]
        public void Weight_is_a_section_the_request_validator_knows()
        {
            Assert.True(ScanRequestRules.Check(
                JObject.Parse(@"{ ""target_document_title"": ""M"", ""sections"": [""weight""],
                                  ""weight_profile"": { ""version"": ""v1"", ""weights"": {} } }"),
                AllSections).Ok);
        }

        [Fact]
        public void The_new_populations_are_extracted_without_taking_the_section_down()
        {
            // Each new count is guarded on its own: one category that throws leaves a
            // null and an error beside it, and the rest of the section still reports.
            string src = ScanSource();
            foreach (string key in new[] { "raster_images", "point_clouds",
                                           "group_instances_nested", "mep_curves_without_system" })
                Assert.Contains("\"" + key + "\"", src);

            Assert.Contains("into[key] = null;", src);
            Assert.Contains("into[key + \"_error\"] = ex.Message;", src);
        }
    }
}
