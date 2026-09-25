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

        public static void Attach(string tool, ChangeWatch watch, CommandResult result)
        {
            if (watch == null) return;
            foreach (ChangeWatch.DocChanges d in watch.Documents) ChangeLedger.Record(tool, d);
            if (result == null || !result.Success || !Enabled) return;
            if (tool == "horizun_verify_changes") return;
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
