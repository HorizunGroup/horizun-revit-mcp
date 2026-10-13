// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Audit 2026-10-08: a schedule exported as CSV from a Spanish Excel is
// semicolon-separated with decimal commas, and its percent_complete "37,5" was
// rejected as "not a number" - the separator was detected, the decimal mark that
// goes with it was not.
// -----------------------------------------------------------------------------
using System.Globalization;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleImportCultureTests
    {
        [Theory]
        [InlineData("en-US")]
        [InlineData("es-CO")]
        public void A_semicolon_csv_reads_its_decimal_comma(string culture)
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                ScheduleImportResult r = ScheduleImport.ParseCsv(
                    "id;name;start;finish;percent_complete\n" +
                    "A1;Excavación;2026-01-05;2026-01-20;37,5\n" +
                    "A2;Cimentación;2026-01-21;2026-02-10;100\n");
                Assert.Equal(2, r.Activities.Count);
                Assert.Equal(37.5, r.Activities[0].PercentComplete);
                Assert.Equal(100, r.Activities[1].PercentComplete);
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        [Fact]
        public void A_comma_csv_still_reads_a_decimal_point_and_never_a_comma()
        {
            ScheduleImportResult r = ScheduleImport.ParseCsv(
                "id,name,start,finish,percent_complete\n" +
                "A1,Excavation,2026-01-05,2026-01-20,37.5\n");
            Assert.Equal(37.5, r.Activities[0].PercentComplete);
        }

        [Fact]
        public void A_semicolon_csv_with_both_marks_is_rejected_not_guessed()
        {
            ScheduleImportResult r = ScheduleImport.ParseCsv(
                "id;name;start;finish;percent_complete\n" +
                "A1;X;2026-01-05;2026-01-20;1.037,5\n");
            Assert.Empty(r.Activities);
        }
    }
}
