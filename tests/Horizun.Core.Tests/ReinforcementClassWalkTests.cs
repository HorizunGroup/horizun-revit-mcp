using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ReinforcementClassWalkTests
    {
        private static List<ReinforcementClassWalk> AllWalked()
        {
            return ReinforcementClasses.All.Select((c, i) => new ReinforcementClassWalk { Class = c, Walked = true, Count = i }).ToList();
        }

        [Fact]
        public void Whole_truth_only_when_all_six_classes_were_walked_and_each_reports_its_count()
        {
            JArray reasons;
            JObject r = ReinforcementClasses.Report(AllWalked(), out reasons);
            Assert.Empty(reasons);
            Assert.Equal(6, r.Properties().Count());
            Assert.Equal(1, r["RebarInSystem"].Value<int>("count"));
            Assert.True(StructuralCoverage.IsWholeTruth(ReinforcementClasses.Coverage(reasons)));
        }

        [Fact]
        public void A_walk_of_Rebar_alone_is_partial_and_names_every_class_it_skipped()
        {
            // The field defect: only Rebar was walked and the reply still said whole truth.
            JArray reasons;
            ReinforcementClasses.Report(new[] { new ReinforcementClassWalk { Class = "Rebar", Walked = true, Count = 3 } }, out reasons);
            Assert.Equal(5, reasons.Count);
            Assert.Contains(reasons.OfType<JObject>(), x => ((string)x["why"]).Contains("RebarInSystem"));
            Assert.False(StructuralCoverage.IsWholeTruth(ReinforcementClasses.Coverage(reasons)));
        }

        [Fact]
        public void A_class_that_would_not_read_is_named_and_makes_the_coverage_partial()
        {
            List<ReinforcementClassWalk> w = AllWalked();
            w.First(x => x.Class == "FabricArea").Walked = false;
            w.First(x => x.Class == "FabricArea").Why = "collector threw";
            JArray reasons;
            JObject r = ReinforcementClasses.Report(w, out reasons);
            Assert.Single(reasons);
            Assert.Contains("FabricArea", (string)reasons[0]["why"]);
            Assert.False(r["FabricArea"].Value<bool>("walked"));
            Assert.Equal(StructuralCoverage.Partial, ReinforcementClasses.Coverage(reasons));
        }
    }
}
