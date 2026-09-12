// -----------------------------------------------------------------------------
// Horizun Core tests — original Horizun code.
//
// fits_titleblock_cell: a long sheet number that overflows its titleblock cell
// was accepted as correct by the previous campaign because nothing measured
// it. The control is an ESTIMATE from the caller's cell geometry, declared as
// such, and a text that does not fit is a finding - never a value to trim or
// rename on the sheet's behalf.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class PlanimetryTitleblockCellFitTests
    {
        private static PlanimetryRuleOptions Options()
        {
            return new PlanimetryRuleOptions
            {
                Units = "mm", ScaleFromFeet = 304.8, ToleranceFeet = PlanimetryGeometry.TouchToleranceFeet,
                IncludeAdvisory = true, IncludePassedChecks = true
            };
        }

        private static SheetFact Sheet(long id, string number)
        {
            var s = new SheetFact
            {
                Id = id, UniqueId = "sheet-" + id, SheetNumber = number, Name = "Plan " + number,
                IsPlaceholder = false, SheetOutline = PlanBox.FromCorners(0, 0, 841 / 304.8, 594 / 304.8),
                ExtentSource = "titleblock", TitleblockTypeId = 900, TitleblockTypeName = "A1 metric",
                TitleblockFamilyName = "Titleblock", TitleblockExtent = PlanBox.FromCorners(0, 0, 841 / 304.8, 594 / 304.8)
            };
            s.TitleblockIds.Add(id * 1000);
            return s;
        }

        private static PlanimetrySnapshot Snapshot(params SheetFact[] sheets)
        {
            var snap = new PlanimetrySnapshot { DocumentTitle = "HZ_TEST", RevitYear = 2026 };
            foreach (SheetFact s in sheets) snap.Sheets.Add(s);
            return snap;
        }

        private static PlanimetryRequirementSet Set(string rules)
        {
            return PlanimetryRequirementSet.Load(JObject.Parse(
                "{\"requirement_set\":{\"id\":\"cells\",\"version\":\"1\"},\"rules\":" + rules + "}"));
        }

        private const string NumberRule =
            "[{\"id\":\"number-fits\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
            "\"assertion\":{\"operator\":\"fits_titleblock_cell\",\"value\":{\"field\":\"sheet_number\"," +
            "\"cell_width\":40,\"text_height\":5,\"char_width_factor\":0.6}}}]";

        [Fact]
        public void A_short_number_fits_and_a_long_one_is_a_finding_with_the_arithmetic()
        {
            // 40 mm cell, 5 mm text, 0.6 advance -> 13 characters fit, 14 do not.
            PlanimetryRequirementSet set = Set(NumberRule);
            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(Sheet(10, "A-201"), Sheet(11, "HZD-cc3b6e97-1")), set, Options());
            PlanimetryFinding f = r.Findings.Single(x => x.Status == "failed");
            Assert.Equal(11, f.ElementIds.Single());
            Assert.Equal("HZD-cc3b6e97-1", (string)f.Observed["value"]);
            Assert.Equal(14, (int)f.Observed["characters"]);
            Assert.Equal(42.0, (double)f.Observed["estimated_text_width"], 6);
            Assert.Equal(40.0, (double)f.Observed["cell_width"], 6);
            Assert.Contains("estimate", (string)f.Observed["estimate"]);
            Assert.Contains("never trimmed or renamed", (string)f.Observed["action"]);
            Assert.Equal("failed", r.Checks.Single().Status);
        }

        [Fact]
        public void The_boundary_is_inclusive()
        {
            PlanimetryRequirementSet set = Set(NumberRule);
            // 13 chars x 5 x 0.6 = 39 <= 40 fits; exactly 40 also fits.
            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(
                Snapshot(Sheet(10, "1234567890123")), set, Options());
            Assert.Empty(r.Findings.Where(x => x.Status == "failed"));
        }

        [Fact]
        public void The_name_field_is_measured_the_same_way()
        {
            PlanimetryRequirementSet set = Set(
                "[{\"id\":\"name-fits\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"fits_titleblock_cell\",\"value\":{\"field\":\"name\"," +
                "\"cell_width\":20,\"text_height\":3}}}]");
            SheetFact s = Sheet(10, "A-1"); s.Name = "A very long sheet name indeed";
            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(Snapshot(s), set, Options());
            PlanimetryFinding f = r.Findings.Single(x => x.Status == "failed");
            Assert.Equal("name", (string)f.Observed["field"]);
            Assert.Equal(0.6, (double)f.Observed["char_width_factor"], 6);   // the default advance
        }

        [Fact]
        public void An_unreadable_number_is_unknown_not_a_pass()
        {
            PlanimetryRequirementSet set = Set(NumberRule);
            SheetFact s = Sheet(10, null); s.Note("sheet_number", "SheetNumber threw");
            PlanimetryAuditResult r = PlanimetryRules.EvaluateRequirementSet(Snapshot(s), set, Options());
            Assert.Single(r.Findings.Where(x => x.Status == "unknown"));
            Assert.Empty(r.Findings.Where(x => x.Status == "failed"));
        }

        [Fact]
        public void The_value_is_validated_by_name_and_never_guessed()
        {
            Assert.Throws<PlanimetryRequirementSetException>(() => Set(
                "[{\"id\":\"x\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"fits_titleblock_cell\",\"value\":{\"field\":\"sheet_number\",\"cell_width\":40}}}]"));
            Assert.Throws<PlanimetryRequirementSetException>(() => Set(
                "[{\"id\":\"x\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"fits_titleblock_cell\",\"value\":{\"field\":\"title\",\"cell_width\":40,\"text_height\":5}}}]"));
            Assert.Throws<PlanimetryRequirementSetException>(() => Set(
                "[{\"id\":\"x\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"fits_titleblock_cell\",\"value\":{\"field\":\"sheet_number\",\"cell_width\":40,\"text_height\":5,\"font\":\"Arial\"}}}]"));
            Assert.Throws<PlanimetryRequirementSetException>(() => Set(
                "[{\"id\":\"x\",\"entity\":\"sheet\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"fits_titleblock_cell\",\"value\":{\"field\":\"sheet_number\",\"cell_width\":40,\"text_height\":5,\"char_width_factor\":3}}}]"));
            // The operator is about the sheet as a whole; a view cannot carry it.
            Assert.Throws<PlanimetryRequirementSetException>(() => Set(
                "[{\"id\":\"x\",\"entity\":\"view\",\"selector\":{\"applies_to_all\":true}," +
                "\"assertion\":{\"operator\":\"fits_titleblock_cell\",\"value\":{\"field\":\"name\",\"cell_width\":40,\"text_height\":5}}}]"));
        }
    }
}
