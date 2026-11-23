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
//
// DataOnlyTools used to be skipped outright: DocumentChanged cannot tell a moved
// element from a renamed one. That missed the write most likely to introduce the
// field defect this check exists for - a parameter edit that changes an offset, a
// height or a type. DataOnlyFallback (below, backed by BBoxCache.cs and the pure
// DataOnlyGeometryRules.cs) gives those tools a cheaper, bounded look instead of
// none: a known mover is always checked; a never-seen element is checked only
// under a 200-element cap, reported either way.
//
// horizun_annotate also gets an automatic pass here (AnnotationCheck), but a 2D
// one: TagOverlapCheck.cs looks at whether the tags/text notes it just placed
// overlap each other or another tag of the same host, in the view(s) they were
// placed in - "cheap" is enforced by capping how many owning views get looked at.
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

        /// <summary>Tools that create tags/text notes: worth an automatic, cheap look at
        /// whether what they just placed overlaps in its own view.</summary>
        private static readonly HashSet<string> AnnotationTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "horizun_annotate"
        };

        /// <summary>"If cheap": at most this many distinct owning views get the automatic pass.</summary>
        private const int MaxAutoAnnotationViews = 3;

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
            "horizun_save_document", "horizun_regroup_by_param",
            "horizun_ungroup_and_mark", "horizun_manage_schedules", "horizun_create_schedule", "horizun_manage_views",
            "horizun_pack_sheets"
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
                var sample = new JArray();
                foreach (ChangeWatch.DocChanges d in watch.Documents)
                {
                    if (d.Added.Count + d.Modified.Count + d.Deleted.Count == 0) continue;
                    added += d.Added.Count; modified += d.Modified.Count; deleted += d.Deleted.Count;
                    string title = null; try { title = d.Document.Title; } catch { }
                    docs.Add(title);
                    foreach (long id in d.Added.Take(SampleSize - sample.Count)) sample.Add(Describe(d.Document, id, "added"));
                    foreach (long id in d.Modified.Take(SampleSize - sample.Count)) sample.Add(Describe(d.Document, id, "modified"));
                }
                var touches = new JArray();
                foreach (ChangeWatch.DocChanges d in watch.Documents)
                    foreach (long id in d.InternalTouches.Take(SampleSize - touches.Count)) touches.Add(Describe(d.Document, id, "refreshed"));
                if (added + modified + deleted == 0 && touches.Count == 0) return;
                JObject data = result.Data as JObject ?? (result.Data == null ? new JObject() : JObject.FromObject(result.Data));
                // Revit's browser panes: said, never counted (RevitInternalTouchRules). A rehearsal that only touched them gets no model_changes.
                if (touches.Count > 0 && data["revit_internal_touches"] == null)
                    data["revit_internal_touches"] = new JObject { ["count"] = watch.Documents.Sum(d => d.InternalTouches.Count), ["sample"] = touches,
                        ["means"] = RevitInternalTouchRules.Means };
                if (added + modified + deleted > 0 && data["model_changes"] == null)
                    data["model_changes"] = new JObject { ["added"] = added, ["modified"] = modified, ["deleted"] = deleted, ["documents"] = docs,
                        ["sample"] = sample, ["sample_limit"] = SampleSize,
                        ["source"] = "Revit DocumentChanged during this call, reconciled against the model after its own rollbacks" };
                result.ReplaceData(data);
            }
            catch { }
        }

        private const int SampleSize = 12;

        /// <summary>What a changed id IS, so a count is not the only thing a reader gets.</summary>
        private static JObject Describe(Document doc, long id, string change)
        {
            var row = new JObject { ["id"] = id, ["change"] = change };
            try
            {
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e != null)
                {
                    row["category"] = e.Category?.Name;
                    row["class"] = e.GetType().Name;
                    row["name"] = e.Name;
                }
            }
            catch { }
            return row;
        }

        public static void Attach(string tool, ChangeWatch watch, CommandResult result)
        {
            if (watch == null) return;
            if (result == null || !result.Success) return;
            // A command that PROVED the document unchanged (Document.IsModified false before
            // and after - horizun_export, Core/ExportIsolationRules.cs) has already stated
            // model_changes. The event residue it measured is not a write: it is neither
            // recorded as this document's last write nor spatially checked.
            if (ProvenUnchanged(result)) return;
            watch.Settle();
            foreach (ChangeWatch.DocChanges d in watch.Documents) ChangeLedger.Record(tool, d);
            StampChanges(watch, result);
            // A reply that declares geometry_untouched (manage_worksets enable: every element
            // changes workset, none moves) is recorded above but not spatially checked: the
            // findings would be the model's old defects, blamed on this call.
            if (GeometryUntouched(result)) return;
            if (!Enabled) return;
            if (tool == "horizun_verify_changes") return;
            if (DataOnlyTools.Contains(tool)) { DataOnlyFallback(tool, watch, result); return; }
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
                RememberBoxes(doc, subjects);
                JObject data = GetData(result);
                string headline = null;
                if (subjects.Count > 0)
                {
                    // Equipment maintenance/access clearance rules for the AUTOMATIC pass have
                    // no caller argument to read from: they come from the project's own
                    // project-context.json, or a machine-wide default file, never compiled in
                    // (ClearanceRulesSource.cs documents the precedence). Neither present is the
                    // ordinary case and costs one failed File.Exists check.
                    List<ClearanceZoneRules.Rule> clearanceRules = ClearanceRulesSource.Load(doc, out string clearanceOrigin, out List<string> clearanceErrors);
                    SpatialCoherence.Outcome o = SpatialCoherence.Check(doc, subjects, MaxSubjects, BudgetMs, clearanceRules: clearanceRules);
                    o.ClearanceRuleErrors.AddRange(clearanceErrors);
                    JObject check = SpatialCoherence.ToJson(o, 25);
                    check["scope"] = "elements this call added or modified (" + changed.Added.Count + " added, " + changed.Modified.Count + " modified)";
                    if (clearanceOrigin != null) check["clearance_rules_source"] = clearanceOrigin;
                    check["see_it"] = "horizun_verify_changes captures an image of these elements with the findings highlighted";
                    data["spatial_check"] = check;
                    headline = SpatialCoherence.Headline(o, NoCommitRules.LeftNothing(data));
                }
                string annotationHeadline = AnnotationCheck(tool, doc, changed, data);
                headline = Join(headline, annotationHeadline);
                if (headline != null) data = PrependAttention(data, headline);
                result.ReplaceData(data);
            }
            catch (Exception ex)
            {
                try
                {
                    JObject data = GetData(result);
                    data["spatial_check"] = new JObject { ["status"] = "not_measured", ["error"] = ex.Message };
                    result.ReplaceData(data);
                }
                catch { }
            }
        }

        /// <summary>
        /// Data-only tools (parameters, keynotes, worksets...) used to be skipped outright.
        /// A parameter write can still move geometry, so this looks anyway - bounded, and
        /// honest about the bound. See DataOnlyGeometryRules.cs for the decision and the
        /// file header above for why it exists.
        /// </summary>
        private static void DataOnlyFallback(string tool, ChangeWatch watch, CommandResult result)
        {
            try
            {
                ChangeWatch.DocChanges changed = watch.Documents.FirstOrDefault(d => d.Modified.Count > 0);
                if (changed == null) return;
                Document doc = changed.Document;
                if (doc == null || !doc.IsValidObject || doc.IsFamilyDocument) return;
                var candidates = new List<DataOnlyGeometryRules.Candidate>();
                foreach (long id in changed.Modified)
                {
                    if (!Rid.CanRepresent(id)) continue;
                    Element e = null;
                    try { e = doc.GetElement(Rid.Make(id)); } catch { }
                    bool physical = SpatialCoherence.IsPhysical(e);
                    double[] current = physical ? BBoxCache.Snapshot(e) : null;
                    double[] previous = BBoxCache.Get(doc, id);
                    candidates.Add(new DataOnlyGeometryRules.Candidate { Id = id, Physical = physical, Previous = previous, Current = current });
                    if (current != null) BBoxCache.Put(doc, id, current);
                }
                DataOnlyGeometryRules.Outcome decision = DataOnlyGeometryRules.Decide(candidates);
                if (decision.Subjects.Count == 0)
                {
                    if (decision.ScopeNote == null) return;
                    JObject none = GetData(result);
                    none["spatial_check"] = new JObject { ["status"] = "not_measured", ["scope"] = decision.ScopeNote };
                    result.ReplaceData(none);
                    return;
                }
                List<Element> subjects = SpatialCoherence.Subjects(doc, decision.Subjects.Where(Rid.CanRepresent).Select(Rid.Make));
                if (subjects.Count == 0) return;
                SpatialCoherence.Outcome o = SpatialCoherence.Check(doc, subjects, MaxSubjects, BudgetMs);
                JObject data = GetData(result);
                JObject check = SpatialCoherence.ToJson(o, 25);
                string scopeText = "data-only write: " + decision.Moved + " element(s) whose bounding box moved";
                if (decision.UnknownIncluded > 0) scopeText += ", " + decision.UnknownIncluded + " never seen before this session and checked under the fallback cap";
                check["scope"] = scopeText;
                if (decision.ScopeNote != null) check["scope_note"] = decision.ScopeNote;
                check["see_it"] = "horizun_verify_changes captures an image of these elements with the findings highlighted";
                data["spatial_check"] = check;
                string headline = SpatialCoherence.Headline(o);
                if (headline != null) data = PrependAttention(data, headline);
                result.ReplaceData(data);
            }
            catch (Exception ex)
            {
                try
                {
                    JObject data = GetData(result);
                    data["spatial_check"] = new JObject { ["status"] = "not_measured", ["error"] = ex.Message };
                    result.ReplaceData(data);
                }
                catch { }
            }
        }

        /// <summary>
        /// Automatic, cheap pass for a tool that just created tags/text notes: do any of them
        /// overlap each other, or another tag of the same host, in the view they were placed
        /// in? "Cheap" is a cap on how many owning views get looked at - a batch that tagged
        /// many views at once falls back to calling horizun_verify_changes(include_annotation=true)
        /// with explicit view_ids. Returns a headline sentence when it found an overlap, else null.
        /// </summary>
        private static string AnnotationCheck(string tool, Document doc, ChangeWatch.DocChanges changed, JObject data)
        {
            if (!AnnotationTools.Contains(tool)) return null;
            try
            {
                var viewIds = new HashSet<long>();
                foreach (long id in changed.Added.Concat(changed.Modified))
                {
                    if (!Rid.CanRepresent(id)) continue;
                    Element e = null;
                    try { e = doc.GetElement(Rid.Make(id)); } catch { }
                    if (e is IndependentTag || e is TextNote)
                        try { viewIds.Add(Rid.Value(e.OwnerViewId)); } catch { }
                }
                if (viewIds.Count == 0 || viewIds.Count > MaxAutoAnnotationViews) return null;
                List<View> views = viewIds
                    .Select(v => { try { return doc.GetElement(Rid.Make(v)) as View; } catch { return null; } })
                    .Where(v => v != null).ToList();
                if (views.Count == 0) return null;
                JObject check = TagOverlapCheck.Run(doc, views);
                data["annotation_check"] = check;
                int findings = check["findings"] is JArray arr ? arr.Count : 0;
                if (findings == 0) return null;
                return "Annotation check: " + findings + " tag/text-note overlap(s) in the view(s) this call placed annotation in - review annotation_check.findings.";
            }
            catch (Exception ex)
            {
                data["annotation_check"] = new JObject { ["status"] = "not_measured", ["error"] = ex.Message };
                return null;
            }
        }

        /// <summary>Keeps BBoxCache warm for every physical element a NORMAL (non data-only)
        /// write touched, so a LATER data-only write on the same elements has a precise
        /// "did it move" comparison instead of falling into the unknown-fallback tier.</summary>
        private static void RememberBoxes(Document doc, IEnumerable<Element> subjects)
        {
            foreach (Element e in subjects)
            {
                double[] box = BBoxCache.Snapshot(e);
                if (box != null) BBoxCache.Put(doc, Rid.Value(e.Id), box);
            }
        }

        private static bool ProvenUnchanged(CommandResult result)
        {
            try { return ((result.Data as JObject)?["model_changes"] as JObject)?.Value<bool?>("proven_unchanged") == true; }
            catch { return false; }
        }

        private static bool GeometryUntouched(CommandResult result)
        {
            try { return (result.Data as JObject)?.Value<bool?>("geometry_untouched") == true; }
            catch { return false; }
        }

        private static JObject GetData(CommandResult result) =>
            result.Data as JObject ?? (result.Data == null ? new JObject() : JObject.FromObject(result.Data));

        private static string Join(string a, string b) => a == null ? b : b == null ? a : a + " " + b;

        /// <summary>First key of the reply: the text a client shows is this object printed in
        /// order, and a finding at the bottom of a long payload is not read.</summary>
        private static JObject PrependAttention(JObject data, string headline)
        {
            string prior = data.Value<string>("attention");
            data.Remove("attention");
            var first = new JObject { ["attention"] = prior == null ? headline : headline + " " + prior };
            foreach (JProperty p in data.Properties()) first.Add(p.Name, p.Value);
            return first;
        }
    }
}
