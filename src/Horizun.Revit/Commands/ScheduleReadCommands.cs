// -----------------------------------------------------------------------------
// Horizun Revit MCP — bounded, read-only inspection of native schedules.
// -----------------------------------------------------------------------------
using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ListSchedulesCommand : ICommand
    {
        public string Name => "horizun_list_schedules";
        public string Description => "List native schedules with their real fields, linked-file setting, itemization and body dimensions. Read-only.";

        /// <summary>
        /// The API marks these with IsTitleblockRevisionSchedule; guarded because the
        /// property can throw on odd view states, and a listing must never fail while
        /// MEASURING what it lists. Unreadable counts as not-revision: the row stays
        /// visible rather than silently vanishing behind the filter.
        /// </summary>
        private static bool IsRevisionSchedule(ViewSchedule s)
        {
            try { return s.IsTitleblockRevisionSchedule; } catch { return false; }
        }

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            int maxRows = Math.Max(1, Math.Min(1000, request.Value<int?>("max_rows") ?? 200));

            // Revision schedules are one per titleblock family and they bury the real
            // list: measured in the field, 28 of 49 rows in one project were
            // <Revision Schedule>. Excludable, and LABELLED either way - the default
            // stays "include" so no existing caller's count changes under them.
            bool includeRevision = request["include_revision_schedules"] == null ||
                                   request.Value<bool>("include_revision_schedules");

            var schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            int revisionCount = schedules.Count(IsRevisionSchedule);
            if (!includeRevision) schedules = schedules.Where(s => !IsRevisionSchedule(s)).ToList();
            var rows = new JArray();
            foreach (ViewSchedule schedule in schedules.Take(maxRows))
            {
                TableSectionData body = schedule.GetTableData().GetSectionData(SectionType.Body);
                rows.Add(new JObject
                {
                    ["schedule_id"] = Rid.Value(schedule.Id),
                    ["name"] = schedule.Name,
                    ["is_revision_schedule"] = IsRevisionSchedule(schedule),
                    ["include_links"] = schedule.Definition.IncludeLinkedFiles,
                    ["itemized"] = schedule.Definition.IsItemized,
                    ["fields"] = new JArray(schedule.Definition.GetFieldOrder().Select(id =>
                        (JToken)schedule.Definition.GetField(id).GetName())),
                    ["body_rows"] = body.NumberOfRows,
                    ["body_columns"] = body.NumberOfColumns
                });
            }
            return CommandResult.Ok(new JObject
            {
                ["total"] = schedules.Count,
                ["returned"] = rows.Count,
                ["truncated"] = rows.Count < schedules.Count,
                // Reported even when excluded - ESPECIALLY when excluded. A count that
                // silently shrank because of a filter is how "how many schedules does
                // this model have" gets answered wrong.
                ["revision_schedules_in_document"] = revisionCount,
                ["revision_schedules_included"] = includeRevision,
                ["host_visibility_coverage"] = DocumentVisibility.Measure(doc).ToJson(),
                ["linked_models_coverage"] = FederatedVisibility.Measure(doc, true),
                ["rows"] = rows
            });
        }
    }

    public sealed class GetScheduleDataCommand : ICommand
    {
        public string Name => "horizun_get_schedule_data";
        public string Description =>
            "Read the displayed cells of one native schedule, including rows produced from RVT links. Returns bounded " +
            "header and body matrices plus exact dimensions; truncation is explicit. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            long id = request.Value<long?>("schedule_id") ?? -1;
            if (!Rid.CanRepresent(id)) return CommandResult.Fail(Rid.RangeError(id));
            ViewSchedule schedule = doc.GetElement(Rid.Make(id)) as ViewSchedule;
            if (schedule == null) return CommandResult.Fail("schedule_id does not identify a native ViewSchedule in the active document.");

            int maxRows = Math.Max(1, Math.Min(1000, request.Value<int?>("max_rows") ?? 200));
            int maxColumns = Math.Max(1, Math.Min(100, request.Value<int?>("max_columns") ?? 50));
            TableData table = schedule.GetTableData();
            TableSectionData header = table.GetSectionData(SectionType.Header);
            TableSectionData body = table.GetSectionData(SectionType.Body);

            return CommandResult.Ok(new JObject
            {
                ["schedule_id"] = Rid.Value(schedule.Id),
                ["name"] = schedule.Name,
                ["include_links"] = schedule.Definition.IncludeLinkedFiles,
                ["header_rows"] = header.NumberOfRows,
                ["header_columns"] = header.NumberOfColumns,
                ["body_rows"] = body.NumberOfRows,
                ["body_columns"] = body.NumberOfColumns,
                ["truncated"] = header.NumberOfRows > maxRows || header.NumberOfColumns > maxColumns ||
                    body.NumberOfRows > maxRows || body.NumberOfColumns > maxColumns,
                ["federated_coverage"] = FederatedVisibility.Measure(doc, schedule.Definition.IncludeLinkedFiles),
                ["header"] = ReadSection(schedule, SectionType.Header, header, maxRows, maxColumns),
                ["body"] = ReadSection(schedule, SectionType.Body, body, maxRows, maxColumns)
            });
        }

        private static JArray ReadSection(ViewSchedule schedule, SectionType type, TableSectionData section, int maxRows, int maxColumns)
        {
            var result = new JArray();
            int rows = Math.Min(section.NumberOfRows, maxRows);
            int columns = Math.Min(section.NumberOfColumns, maxColumns);
            for (int r = 0; r < rows; r++)
            {
                var row = new JArray();
                for (int c = 0; c < columns; c++)
                {
                    try { row.Add(schedule.GetCellText(type, r, c)); }
                    catch (Exception ex) { row.Add(new JObject { ["read_error"] = ex.Message }); }
                }
                result.Add(row);
            }
            return result;
        }
    }
}
