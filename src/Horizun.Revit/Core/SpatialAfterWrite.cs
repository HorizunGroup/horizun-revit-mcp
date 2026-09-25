// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// Every command that leaves the model changed gets the spatial coherence check on
// what it changed, without having to opt in - the same way Interference reports what
// Revit raised. The dispatcher calls Attach after the command returns; a command that
// changed nothing (every read, every rehearsal, every rolled-back write) costs one
// empty-set test.
//
// It never rolls anything back: the command already committed and verified what it
// was asked to do. It adds `spatial_check` (and `attention` when something is wrong)
// to the result, so the caller learns in the same reply that a column now stands in
// a doorway, and can fix it or call horizun_undo.
//
// HORIZUN_SPATIAL_CHECK=off turns it off for a process (a bulk import where the caller
// runs horizun_verify_changes once at the end instead).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class SpatialAfterWrite
    {
        public const int MaxSubjects = 800;
        public const int BudgetMs = 8000;

        /// <summary>
        /// Tools whose writes are data, not geometry. Revit's DocumentChanged cannot tell a
        /// moved element from a renamed one, and a parameter write to ten thousand
        /// elements would otherwise pay seconds of solid booleans and blame this call for
        /// conflicts it never touched.
        /// </summary>
        public static readonly HashSet<string> DataOnlyTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "horizun_write_params_verified", "horizun_set_keynote", "horizun_bind_shared_param",
            "horizun_manage_parameters", "horizun_manage_materials", "horizun_manage_styles", "horizun_manage_units",
            "horizun_manage_revisions", "horizun_manage_worksets", "horizun_manage_phases", "horizun_relinquish_all",
            "horizun_save_document", "horizun_manage_system_types", "horizun_regroup_by_param",
            "horizun_ungroup_and_mark", "horizun_manage_schedules", "horizun_create_schedule", "horizun_manage_views",
            "horizun_pack_sheets", "horizun_manage_links", "horizun_manage_cad_links", "horizun_family_apply"
        };

        public static bool Enabled
        {
            get
            {
                string v = null;
                try { v = Environment.GetEnvironmentVariable("HORIZUN_SPATIAL_CHECK"); } catch { }
                return !string.Equals(v?.Trim(), "off", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(v?.Trim(), "0", StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// model_changes on every successful call that changed a document: what the call
        /// really left added, modified and deleted, as Revit reported it - for the caller,
        /// and for the operations pane, which otherwise could not tell a write from a read.
        /// </summary>
        private static void StampChanges(ChangeWatch watch, CommandResult result)
        {
            try
            {
                int added = 0, modified = 0, deleted = 0;
                var docs = new JArray();
                foreach (ChangeWatch.DocChanges d in watch.Documents)
                {
                    if (d.Added.Count + d.Modified.Count + d.Deleted.Count == 0) continue;
                    added += d.Added.Count; modified += d.Modified.Count; deleted += d.Deleted.Count;
                    string title = null; try { title = d.Document.Title; } catch { }
                    docs.Add(title);
                }
                if (added + modified + deleted == 0) return;
                JObject data = result.Data as JObject ?? (result.Data == null ? new JObject() : JObject.FromObject(result.Data));
                if (data["model_changes"] == null)
                    data["model_changes"] = new JObject { ["added"] = added, ["modified"] = modified, ["deleted"] = deleted, ["documents"] = docs,
                        ["source"] = "Revit DocumentChanged during this call, after its own rollbacks" };
                result.ReplaceData(data);
            }
            catch { }
        }

        public static void Attach(string tool, ChangeWatch watch, CommandResult result)
        {
            if (watch == null) return;
            foreach (ChangeWatch.DocChanges d in watch.Documents) ChangeLedger.Record(tool, d);
            if (result == null || !result.Success) return;
            StampChanges(watch, result);
            if (!Enabled) return;
            if (tool == "horizun_verify_changes" || DataOnlyTools.Contains(tool)) return;
            try
            {
                ChangeWatch.DocChanges changed = watch.Documents
                    .Where(d => d.Added.Count + d.Modified.Count > 0)
                    .OrderByDescending(d => d.Added.Count + d.Modified.Count).FirstOrDefault();
                if (changed == null) return;
                Document doc = changed.Document;
                if (doc == null || !doc.IsValidObject || doc.IsFamilyDocument) return;
                var ids = changed.Added.Concat(changed.Modified).Where(Rid.CanRepresent).Select(Rid.Make);
                List<Element> subjects = SpatialCoherence.Subjects(doc, ids);
                if (subjects.Count == 0) return;
                SpatialCoherence.Outcome o = SpatialCoherence.Check(doc, subjects, MaxSubjects, BudgetMs);
                JObject data = result.Data as JObject ?? (result.Data == null ? new JObject() : JObject.FromObject(result.Data));
                JObject check = SpatialCoherence.ToJson(o, 25);
                check["scope"] = "elements this call added or modified (" + changed.Added.Count + " added, " + changed.Modified.Count + " modified)";
                check["see_it"] = "horizun_verify_changes captures an image of these elements with the findings highlighted";
                data["spatial_check"] = check;
                string headline = SpatialCoherence.Headline(o);
                if (headline != null)
                {
                    // First key of the reply: the text a client shows is this object printed
                    // in order, and a finding at the bottom of a long payload is not read.
                    string prior = data.Value<string>("attention");
                    data.Remove("attention");
                    var first = new JObject { ["attention"] = prior == null ? headline : headline + " " + prior };
                    foreach (JProperty p in data.Properties()) first.Add(p.Name, p.Value);
                    data = first;
                }
                result.ReplaceData(data);
            }
            catch (Exception ex)
            {
                try
                {
                    JObject data = result.Data as JObject ?? (result.Data == null ? new JObject() : JObject.FromObject(result.Data));
                    data["spatial_check"] = new JObject { ["status"] = "not_measured", ["error"] = ex.Message };
                    result.ReplaceData(data);
                }
                catch { }
            }
        }
    }
}
