// -----------------------------------------------------------------------------
// Horizun Revit MCP — bounded, read-only inspection of native schedules.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
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
        /// <summary>
        /// One field: what it is called, and whether its value comes from a formula.
        ///
        /// A caller that cannot see which columns are calculated cannot tell an editable
        /// column from one whose values Revit computes - and will try to write to it.
        /// </summary>
        private static JObject FieldSummary(ScheduleField field)
        {
            var summary = new JObject();
            try { summary["name"] = field.GetName(); } catch { summary["name"] = "(unreadable)"; }
            try { summary["field_type"] = field.FieldType.ToString(); } catch { }
            try
            {
                summary["is_calculated"] = field.IsCalculatedField;
                if (field.IsCalculatedField)
                    summary["means"] =
                        "Revit computes this column from a formula. It can be added, removed, hidden, " +
                        "formatted and re-ordered from here; the FORMULA itself can only be written by hand " +
                        "in Revit, because the API exposes no way to set one.";
            }
            catch { summary["is_calculated"] = JValue.CreateNull(); }
            try { summary["hidden"] = field.IsHidden; } catch { }
            return summary;
        }

        private static bool IsRevisionSchedule(ViewSchedule s)
        {
            try { return s.IsTitleblockRevisionSchedule; } catch { return false; }
        }

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDocument != null) return wrongDocument;
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
                    // NAME AND KIND. A list of names cannot tell a calculated column from an
                    // ordinary one, and the difference decides what a caller may do with it:
                    // a calculated field can be added, hidden, formatted and removed, and its
                    // formula can only be written by hand in Revit.
                    ["fields"] = new JArray(schedule.Definition.GetFieldOrder().Select(id =>
                        (JToken)FieldSummary(schedule.Definition.GetField(id)))),
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
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : Horizun.Revit.Core.JsonText.ParseObject(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");
            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDocument != null) return wrongDocument;
            JToken idToken = request["schedule_id"];
            long? sentId = idToken == null || idToken.Type == JTokenType.Null ? (long?)null : request.Value<long?>("schedule_id");
            // The order (not sent, negative, no element, other element, TEMPLATE, readable) is decided in
            // ScheduleIdMessages.Classify and tested there; this only answers what the document holds.
            if (sentId.HasValue && sentId.Value >= 0 && !Rid.CanRepresent(sentId.Value))
                return CommandResult.Fail(Rid.RangeError(sentId.Value));
            Element found = null;
            ScheduleIdHit hit = ScheduleIdMessages.Classify(sentId, id =>
            {
                found = doc.GetElement(Rid.Make(id));
                var asSchedule = found as ViewSchedule;
                bool isTemplate = false;
                if (asSchedule != null)
                {
                    try { isTemplate = asSchedule.IsTemplate; } catch (Exception) { isTemplate = false; }
                }
                return new ScheduleIdFacts(found != null, asSchedule != null, isTemplate);
            });
            if (hit != ScheduleIdHit.Readable) return CommandResult.Fail(ScheduleIdRefusal(doc, hit, sentId, found));
            var schedule = (ViewSchedule)found;

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

        /// <summary>
        /// Why schedule_id names no readable schedule, and which ones exist (ScheduleIdMessages says it;
        /// this only gathers the facts from the document).
        /// </summary>
        internal static string ScheduleIdRefusal(Document doc, ScheduleIdHit hit, long? id, Element found)
        {
            string type = null, name = null;
            try { type = found?.GetType().Name; } catch (Exception) { }
            try { name = found?.Name; } catch (Exception) { }
            List<KeyValuePair<long, string>> schedules;
            try
            {
                schedules = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .Where(v => !v.IsTemplate).Select(v => new KeyValuePair<long, string>(Rid.Value(v.Id), v.Name)).ToList();
            }
            catch (Exception) { schedules = null; }
            return ScheduleIdMessages.Refusal(hit, id, type, name, schedules);
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
