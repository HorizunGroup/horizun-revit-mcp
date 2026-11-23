// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// Field case (course model, 2026-11): types renamed through the API, then a sheet
// exported to PDF - the schedules on it printed the OLD names. Revit had not rebuilt
// the schedule data; RefreshData plus a ShowHeaders flip there and back, in a
// transaction, brought it up to date.
//
// So a PDF export and a sheet capture refresh the schedules placed on the sheets
// first, inside a TransactionGroup that is rolled back once the file is written: the
// output carries current data and the model is left as it was. A schedule that cannot
// be refreshed (owned by another user, a read-only document, Revit refusing) is named
// in the report; the export still runs and says which schedules may be stale.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class ScheduleRefresh
    {
        /// <summary>The schedules placed on the sheets among <paramref name="views"/>, and any view that is itself a schedule.</summary>
        public static List<ViewSchedule> On(Document doc, IEnumerable<View> views)
        {
            var found = new Dictionary<long, ViewSchedule>();
            var sheetIds = new HashSet<long>();
            foreach (View v in views)
            {
                if (v is ViewSheet) sheetIds.Add(Rid.Value(v.Id));
                else if (v is ViewSchedule s && !s.IsTemplate) found[Rid.Value(s.Id)] = s;
            }
            if (sheetIds.Count > 0)
                foreach (ScheduleSheetInstance inst in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                {
                    if (!sheetIds.Contains(Rid.Value(inst.OwnerViewId))) continue;
                    if (inst.IsTitleblockRevisionSchedule) continue;
                    if (doc.GetElement(inst.ScheduleId) is ViewSchedule s) found[Rid.Value(s.Id)] = s;
                }
            return found.Values.ToList();
        }

        /// <summary>
        /// Runs <paramref name="export"/> with the schedules on <paramref name="views"/> refreshed, inside a
        /// TransactionGroup that is rolled back afterwards. Returns the report (null when there is nothing to refresh).
        /// </summary>
        public static JObject Around(Document doc, IEnumerable<View> views, Action export)
        {
            List<ViewSchedule> schedules;
            try { schedules = On(doc, views); }
            catch (Exception ex) { export(); return new JObject { ["refreshed"] = 0, ["note"] = "the schedules on the sheets could not be listed: " + ex.Message }; }
            if (schedules.Count == 0) { export(); return null; }
            string unavailable = doc.IsReadOnly ? "the document is read-only"
                : doc.IsModifiable ? "a transaction is already open on the document" : null;
            if (unavailable != null)
            {
                export();
                return Report(0, schedules.Select(s => Skip(s, unavailable)).ToList(), false);
            }
            var skipped = new List<JObject>();
            int refreshed = 0;
            bool rolledBack = false;
            using (var group = new TransactionGroup(doc, "Horizun: refresh schedules for export (rolled back)"))
            {
                group.Start();
                try
                {
                    foreach (ViewSchedule s in schedules)
                    {
                        string why = Refresh(doc, s);
                        if (why == null) refreshed++; else skipped.Add(Skip(s, why));
                    }
                    export();
                }
                finally
                {
                    try { rolledBack = group.RollBack() == TransactionStatus.RolledBack; } catch { rolledBack = false; }
                }
            }
            return Report(refreshed, skipped, rolledBack);
        }

        /// <summary>null when refreshed; else why not.</summary>
        private static string Refresh(Document doc, ViewSchedule s)
        {
            try
            {
                if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, s.Id) == CheckoutStatus.OwnedByOtherUser)
                    return "owned by another user";
            }
            catch { /* status unreadable: try the refresh, a refusal is reported below */ }
            using (var tx = new Transaction(doc, "Horizun: refresh schedule"))
            {
                try
                {
                    FailureHandlingOptions f = tx.GetFailureHandlingOptions();
                    f.SetClearAfterRollback(true);
                    f.SetForcedModalHandling(false);
                    tx.SetFailureHandlingOptions(f);
                    tx.Start();
                    s.RefreshData();
                    ScheduleDefinition d = s.Definition;
                    bool headers = d.ShowHeaders;
                    d.ShowHeaders = !headers;
                    doc.Regenerate();
                    d.ShowHeaders = headers;
                    TransactionStatus st = tx.Commit();
                    return st == TransactionStatus.Committed ? null : "Revit returned " + st;
                }
                catch (Exception ex)
                {
                    try { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); } catch { }
                    return ex.Message;
                }
            }
        }

        private static JObject Skip(ViewSchedule s, string why) => new JObject
        {
            ["schedule_id"] = Rid.Value(s.Id), ["name"] = s.Name, ["reason"] = why
        };

        private static JObject Report(int refreshed, List<JObject> skipped, bool rolledBack) => new JObject
        {
            ["refreshed"] = refreshed,
            ["not_refreshed"] = new JArray(skipped),
            ["model_restored"] = rolledBack,
            ["note"] = skipped.Count == 0
                ? "The schedules on the exported sheets were rebuilt before export, inside a group that was rolled back."
                : skipped.Count + " schedule(s) could not be rebuilt before export and may show data older than the model."
        };
    }
}
