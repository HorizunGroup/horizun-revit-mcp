// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// A SIZE WRITTEN PAST A BRANCH'S OPEN END.
//
// MEASURED (campaign 7, M106): three branch stubs carry their "4X4" beyond the
// stub's free end - beside the damper or grille that ends the branch - 436 mm
// and 637 mm away, where no run passes beside the text. Read as "beside a run's
// interior" they name nothing, and the three branches stay unsized. The rule may
// DECLARE free_end_max_distance_mm; these cases pin what that reads and where it
// must refuse: off unless declared, the outward side only, never a second size
// for a run with its own label, and two ends about as near is ambiguity.
// -----------------------------------------------------------------------------
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CadFreeEndLabelTests
    {
        private static CadSectionRule Rule(double? freeEnd) => new CadSectionRule
        {
            LabelLayers = { "M-TEXT" }, LabelUnits = "inch", LabelOrder = "width_x_height",
            MaxDistanceMm = 600, LeaderToleranceMm = 300, FreeEndMaxDistanceMm = freeEnd
        };

        private static CadRunSection Run(string id, double x1, double y1, double x2, double y2) =>
            new CadRunSection { RunId = id, SemanticId = "s-" + id, Start = new CadPoint(x1, y1), End = new CadPoint(x2, y2) };

        private static CadLabel Label(string id, string text, double x, double y) =>
            new CadLabel { Id = id, Text = text, Layer = "M-TEXT", At = new CadPoint(x, y) };

        // a main with its own label, and a 1.1 m stub tapping it at (0,0) whose far end is free
        private static List<CadRunSection> Branch() => new List<CadRunSection>
        {
            Run("main", 0, -2000, 0, 2000), Run("stub", 0, 0, 1100, 0)
        };

        private static CadSectionReading Read(List<CadRunSection> runs, double? freeEnd, params CadLabel[] labels) =>
            CadDuctSections.Assign(runs, labels, new List<CadLeaderLine>(), Rule(freeEnd), 25.4);

        private static JObject Row(CadSectionReading r, string label) =>
            r.SizeLabels.Single(x => x.Value<string>("label") == label);

        [Fact]
        public void Off_unless_declared_the_size_past_an_open_end_names_nothing()
        {
            CadSectionReading r = Read(Branch(), null, Label("L1", "8X8", 300, 1500), Label("L2", "4X4", 1150, 436));
            Assert.Equal("no_run_within_reach", Row(r, "L2").Value<string>("outcome"));
            Assert.Equal("missing", r.Runs.Single(x => x.RunId == "stub").State);
        }

        [Fact]
        public void Declared_the_stub_takes_the_size_written_past_its_free_end_and_says_so()
        {
            CadSectionReading r = Read(Branch(), 700, Label("L1", "8X8", 300, 1500), Label("L2", "4X4", 1150, 436));
            JObject row = Row(r, "L2");
            Assert.Equal("associated", row.Value<string>("outcome"));
            Assert.Equal("stub", row.Value<string>("run"));
            Assert.True(row.Value<bool>("beyond_free_end"));
            Assert.Contains("free end", row.Value<string>("by"));
            Assert.Contains("declared", row.Value<string>("by"));
            CadRunSection stub = r.Runs.Single(x => x.RunId == "stub");
            Assert.Equal("documented", stub.State);
            Assert.Equal(101.6, stub.WidthMm.Value, 3);
            Assert.Equal(101.6, stub.HeightMm.Value, 3);
            Assert.Contains("beyond its free end", stub.Reason);
            Assert.Equal("documented", r.Runs.Single(x => x.RunId == "main").State);   // the main keeps its own
        }

        [Fact]
        public void Farther_than_declared_or_on_the_inward_side_it_still_names_nothing()
        {
            CadSectionReading far = Read(Branch(), 700, Label("L2", "4X4", 1700, 500));     // 800 mm from the end
            Assert.Equal("no_run_within_reach", Row(far, "L2").Value<string>("outcome"));
            Assert.Contains("free_end_max_distance_mm", Row(far, "L2").Value<string>("means"));
            CadSectionReading behind = Read(Branch(), 700, Label("L3", "4X4", -700, 0));    // behind both free ends
            Assert.Equal("no_run_within_reach", Row(behind, "L3").Value<string>("outcome"));
            Assert.All(behind.Runs, x => Assert.Equal("missing", x.State));
        }

        [Fact]
        public void A_run_with_its_own_label_takes_no_second_size_from_past_its_end()
        {
            CadSectionReading r = Read(Branch(), 700, Label("L1", "6X6", 550, 300), Label("L2", "4X4", 1150, 436));
            Assert.Equal("free_end_of_a_labelled_run", Row(r, "L2").Value<string>("outcome"));
            CadRunSection stub = r.Runs.Single(x => x.RunId == "stub");
            Assert.Equal("documented", stub.State);                 // 6X6, not contradictory
            Assert.Equal(152.4, stub.WidthMm.Value, 3);
        }

        [Fact]
        public void Two_open_ends_about_as_near_is_ambiguity_not_a_choice()
        {
            var runs = new List<CadRunSection>
            {
                Run("main", 0, -2000, 0, 2000), Run("a", 0, 0, 1000, 0), Run("b", 0, 1000, 1000, 1000)
            };
            CadSectionReading r = Read(runs, 700, Label("L", "4X4", 1400, 500));
            Assert.Equal("ambiguous", Row(r, "L").Value<string>("outcome"));
            Assert.Equal("ambiguous", r.Runs.Single(x => x.RunId == "a").State);
            Assert.Equal("ambiguous", r.Runs.Single(x => x.RunId == "b").State);
        }

        [Fact]
        public void The_declaration_is_bounded_and_echoed()
        {
            JObject s = JObject.Parse("{ \"from\": \"labels\", \"label_layers\": [\"M-TEXT\"], \"label_units\": \"inch\", " +
                                      "\"label_order\": \"width_x_height\", \"max_distance_mm\": 600, \"free_end_max_distance_mm\": 700 }");
            CadSectionRule rule = CadDuctSections.Parse("d", s, "duct", null);
            Assert.Equal(700, rule.FreeEndMaxDistanceMm.Value);
            Assert.Equal(700, rule.ToJson().Value<double>("free_end_max_distance_mm"));
            foreach (string v in new[] { "0", "-5", "5001" })
            {
                JObject bad = (JObject)s.DeepClone();
                bad["free_end_max_distance_mm"] = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                Assert.Throws<CadRequirementSetException>(() => CadDuctSections.Parse("d", bad, "duct", null));
            }
            s.Remove("free_end_max_distance_mm");
            Assert.Null(CadDuctSections.Parse("d", s, "duct", null).FreeEndMaxDistanceMm);
        }
    }
}
