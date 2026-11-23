using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class RevitAvailabilityRulesTests
    {
        [Fact]
        public void No_Revit_process_is_closed_with_the_open_Revit_step()
        {
            string step = RevitAvailabilityRules.NextStep(new RunningRevit[0], "2026");
            Assert.Equal(RevitAvailabilityRules.Closed, RevitAvailabilityRules.State(new RunningRevit[0], "2026"));
            Assert.Contains("CLOSED", step);
            Assert.Contains("open Revit 2026", step);
            Assert.Contains("horizun_health", step);
        }

        [Fact]
        public void A_running_Revit_without_a_bridge_names_the_dialog_and_the_wait()
        {
            var running = new[] { new RunningRevit(47080, "2025") };
            Assert.Equal(RevitAvailabilityRules.RunningWithoutBridge, RevitAvailabilityRules.State(running, null));
            string step = RevitAvailabilityRules.NextStep(running, null);
            Assert.Contains("IS running", step);
            Assert.Contains("pid 47080", step);
            Assert.Contains("Always Load", step);
        }

        [Fact]
        public void Only_the_requested_year_counts_and_an_unknown_year_counts()
        {
            var running = new[] { new RunningRevit(1, "2024"), new RunningRevit(2, null) };
            Assert.Single(RevitAvailabilityRules.Relevant(running, "2026"));
            Assert.Equal(RevitAvailabilityRules.Closed, RevitAvailabilityRules.State(new[] { new RunningRevit(1, "2024") }, "2026"));
            Assert.Equal(2, RevitAvailabilityRules.Relevant(running, null).Count);
        }

        [Theory]
        [InlineData(@"C:\Program Files\Autodesk\Revit 2025\Revit.exe", "2025")]
        [InlineData(@"D:\Apps\Autodesk\revit 2027\Revit.exe", "2027")]
        [InlineData(null, null)]
        [InlineData(@"C:\Tools\Revit.exe", null)]
        public void The_year_comes_from_the_install_path(string path, string year)
            => Assert.Equal(year, RevitAvailabilityRules.YearFromPath(path));
    }
}
