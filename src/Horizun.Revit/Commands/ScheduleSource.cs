using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class ScheduleSource
    {
        // A complete body is required: a partial read must never be hashed as complete.
        public static string Read(ViewSchedule schedule, bool coverageRead = false)
        {
            TableSectionData body = schedule.GetTableData().GetSectionData(SectionType.Body);
            if (!AnnotationDependencyRules.ScheduleBodyAllowed(body.NumberOfRows, body.NumberOfColumns, 0, coverageRead))
                throw new InvalidOperationException("Schedule body exceeds the 1000 row / 100 column limit.");
            var rows = new List<string>();
            for (int r = 0; r < body.NumberOfRows; r++)
            {
                var cells = new List<string>();
                for (int c = 0; c < body.NumberOfColumns; c++) cells.Add(schedule.GetCellText(SectionType.Body, r, c));
                rows.Add(string.Join("\t", cells));
            }
            string text = string.Join("\n", rows);
            if ((!coverageRead && string.IsNullOrWhiteSpace(text)) || !AnnotationDependencyRules.ScheduleBodyAllowed(body.NumberOfRows, body.NumberOfColumns, text.Length, coverageRead))
                throw new InvalidOperationException("Schedule body is empty or exceeds 30000 characters.");
            return text;
        }
        public static string Hash(ViewSchedule schedule, string text) => NoteLeaderRules.Signature(schedule.UniqueId, "schedule", text);
    }
}
