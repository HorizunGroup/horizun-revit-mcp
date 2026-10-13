// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// Backlog #46: a schedule date with a positive offset was moved to UTC BEFORE it
// was truncated to its day, so a task that starts 2026-10-10T00:30+02:00 started
// on 2026-10-09 in the 4D. The day is the one the schedule wrote.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ScheduleImportDateTests
    {
        [Theory]
        [InlineData("2026-10-10T00:30:00+02:00", "2026-10-10")]
        [InlineData("2026-10-10T23:30:00-05:00", "2026-10-10")]
        [InlineData("2026-10-10T08:00:00Z", "2026-10-10")]
        [InlineData("2026-10-10T08:00:00.000+05:30", "2026-10-10")]
        [InlineData("2026-10-10", "2026-10-10")]
        [InlineData("2026-10-10 08:00", "2026-10-10")]
        public void The_day_is_the_one_the_schedule_wrote(string text, string day)
        {
            DateTime? value;
            Assert.True(ScheduleImport.TryDate(text, out value));
            Assert.Equal(day, ScheduleImport.Day(value));
        }
    }
}
