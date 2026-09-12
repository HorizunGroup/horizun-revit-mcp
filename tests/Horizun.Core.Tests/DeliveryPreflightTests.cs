// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// The delivery preflight. Every property here is about finding a predictable
// error BEFORE the first write and naming it by stage and field - a profile
// whose third stage is invalid must be refused whole, with every finding, not
// executed for two stages and abandoned.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class DeliveryPreflightTests
    {
        private static JObject Profile()
        {
            return JObject.Parse(@"{
              ""id"": ""arch"", ""version"": ""1"", ""units"": ""mm"",
              ""views"": [{
                ""view_id"": 101,
                ""dimension_sets"": [{
                  ""role"": ""thickness"", ""operation"": ""intent_dimension"",
                  ""reference_targets"": [{""element_id"": 501, ""selector"": ""exterior_face""},
                                          {""element_id"": 501, ""selector"": ""interior_face""}],
                  ""offset"": 10, ""side"": ""positive"", ""dimension_type_id"": 301
                }],
                ""tags"": {""element_ids"": [501], ""tag_type_id"": 401, ""clearance"": 3, ""max_displacement"": 20}
              }],
              ""packing"": {
                ""sheets"": [{""sheet_id"": 201, ""usable_rect"": [10, 10, 700, 500]},
                             {""sheet_id"": 202, ""usable_rect"": [10, 10, 700, 500]}],
                ""items"": [{""key"": ""plan"", ""view_id"": 101}], ""margin"": 5, ""gap"": 10
              },
              ""publication"": {""format"": ""pdf"", ""view_ids"": [201, 202],
                                ""output_path"": ""C:/ApprovedOutput/delivery.pdf"",
                                ""pdf_combine"": false, ""overwrite"": false},
              ""requirement_set"": {
                ""requirement_set"": {""id"": ""sheet-standard"", ""version"": ""1""},
                ""rules"": [{""id"": ""number"", ""entity"": ""sheet"", ""selector"": {""applies_to_all"": true},
                             ""assertion"": {""field"": ""sheet_number"", ""operator"": ""matches"", ""value"": ""^A""}}]
              }
            }");
        }

        private static JObject[] Errors(JObject result) => ((JArray)result["errors"]).Cast<JObject>().ToArray();

        [Fact]
        public void AWellFormedProfilePassesAndNamesWhatOnlyARehearsalCanDecide()
        {
            JObject r = DeliveryPreflight.Static(Profile(), 2026);
            Assert.True(r.Value<bool>("ok"), r.ToString());
            Assert.Equal(0, r.Value<int>("error_count"));
            var undetermined = ((JArray)r["undetermined"]).Cast<JObject>().Select(u => u.Value<string>("code")).ToArray();
            Assert.Contains("decided_by_rehearsal", undetermined);
            Assert.Contains("decided_by_export", undetermined);
            Assert.Contains("views.dimension_sets", r["checked"].Values<string>());
            Assert.Contains("publication", r["checked"].Values<string>());
        }

        [Fact]
        public void EveryErrorIsCollectedAtOnceByStageAndField()
        {
            JObject p = Profile();
            p["views"][0]["dimension_sets"][0]["selector"] = "axis";          // unknown selector
            p["views"][0]["tags"]["tag_mode"] = "by_family";                   // unknown enum
            p["packing"]["items"][0]["key"] = "";                              // empty key
            p["publication"]["output_path"] = "delivery.pdf";                  // relative
            JObject r = DeliveryPreflight.Static(p, 2026);
            Assert.False(r.Value<bool>("ok"));
            JObject[] errors = Errors(r);
            Assert.Equal(4, errors.Length);
            Assert.Contains(errors, e => e.Value<string>("stage") == "dimensions_101" && e.Value<string>("field") == "dimension_sets[0].selector");
            Assert.Contains(errors, e => e.Value<string>("stage") == "tags_101" && e.Value<string>("field") == "tags.tag_mode");
            Assert.Contains(errors, e => e.Value<string>("stage") == "pack" && e.Value<string>("field") == "packing.items[0].key");
            Assert.Contains(errors, e => e.Value<string>("stage") == "publish" && e.Value<string>("field") == "publication.output_path");
        }

        [Fact]
        public void UnknownFieldsAreRefusedByNameInEveryStage()
        {
            JObject p = Profile();
            p["views"][0]["dimension_sets"][0]["colour"] = "red";
            p["views"][0]["tags"]["font"] = "Arial";
            p["packing"]["sheets"][0]["paper"] = "A1";
            p["packing"]["items"][0]["rotation"] = 90;
            p["publication"]["printer"] = "PDF";
            JObject r = DeliveryPreflight.Static(p, 2026);
            JObject[] unknown = Errors(r).Where(e => e.Value<string>("code") == "unknown_field").ToArray();
            Assert.Equal(5, unknown.Length);
            Assert.Contains(unknown, e => e.Value<string>("field") == "dimension_sets[0].colour");
            Assert.Contains(unknown, e => e.Value<string>("field") == "tags.font");
            Assert.Contains(unknown, e => e.Value<string>("field") == "packing.sheets[0].paper");
            Assert.Contains(unknown, e => e.Value<string>("field") == "packing.items[0].rotation");
            Assert.Contains(unknown, e => e.Value<string>("field") == "publication.printer");
        }

        [Fact]
        public void IntentDimensionTakesExactlyOneOfIdsOrTargets()
        {
            JObject p = Profile();
            p["views"][0]["dimension_sets"][0]["element_ids"] = new JArray(501, 502);
            Assert.Contains(Errors(DeliveryPreflight.Static(p, 2026)),
                e => e.Value<string>("field") == "dimension_sets[0]" && e.Value<string>("message").Contains("exactly one"));

            JObject q = Profile();
            ((JObject)q["views"][0]["dimension_sets"][0]).Remove("reference_targets");
            Assert.Contains(Errors(DeliveryPreflight.Static(q, 2026)), e => e.Value<string>("field") == "dimension_sets[0]");
        }

        [Fact]
        public void NearestFaceSelectorRequiresAProbePoint()
        {
            JObject p = Profile();
            p["views"][0]["dimension_sets"][0]["reference_targets"][0]["selector"] = "nearest_face";
            JObject[] errors = Errors(DeliveryPreflight.Static(p, 2026));
            Assert.Contains(errors, e => e.Value<string>("field") == "dimension_sets[0].reference_targets[0].probe_point" && e.Value<string>("code") == "missing");
        }

        [Fact]
        public void LinkedDatumsAreRefusedBecauseRevitRejectsThem()
        {
            JObject p = Profile();
            p["views"][0]["dimension_sets"][0]["link_instance_id"] = 77;
            Assert.Contains(Errors(DeliveryPreflight.Static(p, 2026)), e => e.Value<string>("field") == "dimension_sets[0].link_instance_id" && e.Value<string>("code") == "refused");
        }

        [Fact]
        public void DuplicateRolesAndDuplicateIdsAreFindings()
        {
            JObject p = Profile();
            var sets = (JArray)p["views"][0]["dimension_sets"];
            sets.Add(sets[0].DeepClone());
            p["views"][0]["tags"]["element_ids"] = new JArray(501, 501);
            JObject[] errors = Errors(DeliveryPreflight.Static(p, 2026));
            Assert.Contains(errors, e => e.Value<string>("field") == "dimension_sets[1].role" && e.Value<string>("code") == "duplicate");
            Assert.Contains(errors, e => e.Value<string>("field") == "tags.element_ids" && e.Value<string>("code") == "duplicate");
        }

        [Fact]
        public void PackingRectanglesAndDistancesAreValidatedStatically()
        {
            JObject p = Profile();
            p["packing"]["sheets"][1]["usable_rect"] = new JArray(700, 500, 10, 10);   // inverted
            p["packing"]["sheets"][0]["reserved_zones"] = new JArray(new JArray(0, 0, 10));  // three numbers
            p["packing"]["gap"] = -1;
            JObject[] errors = Errors(DeliveryPreflight.Static(p, 2026));
            Assert.Contains(errors, e => e.Value<string>("field") == "packing.sheets[1].usable_rect");
            Assert.Contains(errors, e => e.Value<string>("field") == "packing.sheets[0].reserved_zones[0]");
            Assert.Contains(errors, e => e.Value<string>("field") == "packing.gap");
        }

        [Fact]
        public void PrintPolicyIsValidatedAgainstTheHostYearBeforeAnyStageRuns()
        {
            JObject p = Profile();
            p["publication"]["pdf_print"] = new JObject { ["paper_format"] = "ISO_A1", ["export_in_background"] = false };
            Assert.True(DeliveryPreflight.Static(p, 2026).Value<bool>("ok"));
            JObject old = DeliveryPreflight.Static(p, 2024);
            Assert.False(old.Value<bool>("ok"));
            Assert.Contains(Errors(old), e => e.Value<string>("field") == "publication.pdf_print" && e.Value<string>("message").Contains("2025"));
            // A background export returns before the file exists (measured): true refuses on every year.
            p["publication"]["pdf_print"] = new JObject { ["paper_format"] = "ISO_A1", ["export_in_background"] = true };
            Assert.Contains(Errors(DeliveryPreflight.Static(p, 2026)), e => e.Value<string>("field") == "publication.pdf_print" && e.Value<string>("message").Contains("before the file exists"));

            p["publication"]["pdf_print"] = new JObject { ["paper_size"] = "A1" };
            Assert.Contains(Errors(DeliveryPreflight.Static(p, 2026)), e => e.Value<string>("field") == "publication.pdf_print" && e.Value<string>("message").Contains("paper_size"));
        }

        [Fact]
        public void OutputPathMustBeAbsoluteAndAPdf()
        {
            JObject p = Profile();
            p["publication"]["output_path"] = "C:/ApprovedOutput/delivery.dwg";
            Assert.Contains(Errors(DeliveryPreflight.Static(p, 2026)), e => e.Value<string>("field") == "publication.output_path" && e.Value<string>("message").Contains(".pdf"));
        }

        [Fact]
        public void ARequirementSetErrorIsAnAuditStageFinding()
        {
            JObject p = Profile();
            p["requirement_set"]["rules"][0]["assertion"]["operator"] = "looks_nice";
            JObject[] errors = Errors(DeliveryPreflight.Static(p, 2026));
            Assert.Single(errors);
            Assert.Equal("audit", errors[0].Value<string>("stage"));
            Assert.Contains("looks_nice", errors[0].Value<string>("message"));
        }

        [Fact]
        public void MergeKeepsEveryFindingAndRecomputesOk()
        {
            JObject stat = DeliveryPreflight.Static(Profile(), 2026);
            Assert.True(stat.Value<bool>("ok"));
            JObject merged = DeliveryPreflight.Merge(stat,
                new[] { DeliveryPreflight.Finding("publish", "publication.view_ids", "not_found", "sheet 202 does not exist") },
                new[] { DeliveryPreflight.Finding("pack", "packing.items[plan].view_id", "unreadable", "threw") },
                new[] { "host.sheets" });
            Assert.False(merged.Value<bool>("ok"));
            Assert.Equal(1, merged.Value<int>("error_count"));
            Assert.Contains("host.sheets", merged["checked"].Values<string>());
            Assert.Contains(((JArray)merged["undetermined"]).Cast<JObject>(), u => u.Value<string>("code") == "unreadable");
            Assert.Contains(((JArray)merged["undetermined"]).Cast<JObject>(), u => u.Value<string>("code") == "decided_by_rehearsal");
        }

        [Fact]
        public void AMissingProfileIsASingleFindingNotAnException()
        {
            JObject r = DeliveryPreflight.Static(null, 2026);
            Assert.False(r.Value<bool>("ok"));
            Assert.Single(Errors(r));
        }
    }
}
