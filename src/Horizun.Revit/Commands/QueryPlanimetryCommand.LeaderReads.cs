using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class QueryPlanimetryCommand
    {
        private static List<TextNote> NotesOf(Document doc, JObject request, out List<long> unmatched)
        {
            unmatched = new List<long>();
            var notes = new List<TextNote>();
            if (request["note_ids"] is JArray ids)
                foreach (JToken t in PlanimetryReadBudget.CheckEach(ids))
                {
                    long id = t.Value<long>();
                    var n = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as TextNote : null;
                    if (n == null) unmatched.Add(id); else notes.Add(n);
                }
            if (request["view_ids"] is JArray views)
                foreach (JToken t in PlanimetryReadBudget.CheckEach(views))
                {
                    View v = NeedAs<View>(doc, t.Value<long>(), "view");
                    notes.AddRange(PlanimetryReadBudget.CheckEach(new FilteredElementCollector(doc, v.Id).OfClass(typeof(TextNote)).Cast<TextNote>()));
                }
            return notes.GroupBy(n => Rid.Value(n.Id)).Select(g => g.First()).OrderBy(n => Rid.Value(n.Id)).ToList();
        }

        private static JObject Coverage3d(View3D v3, int tagsWithLeaders) => new JObject
        {
            ["ray"] = v3 == null ? "no_3d_view" : "complete",
            ["ray_view"] = v3?.Name,
            ["nested_links"] = "a hit through a link requires resolving each instance on its path",
            ["tags_with_leaders_not_covered"] = tagsWithLeaders
        };

        // ---- leader_targets ------------------------------------------------------------------------

        private static CommandResult LeaderTargets(Document doc, JObject request, int maxRows, JArray rows)
        {
            List<long> unmatched;
            List<TextNote> notes = NotesOf(doc, request, out unmatched);
            View3D v3 = LeaderTargetReader.FindView3D(doc);
            var planGraphics = new Dictionary<ElementId, LeaderTargetReader.PlanLinkGraphics>();
            int leaders = 0, hits = 0, withoutLeader = 0;
            bool truncated = false, rayIncomplete = false, missing3d = false;
            foreach (TextNote n in PlanimetryReadBudget.CheckEach(notes))
            {
                IList<Leader> ls = n.GetLeaders();
                if (ls.Count == 0) { withoutLeader++; continue; }
                if (rows.Count >= maxRows) { truncated = true; continue; }
                View v = doc.GetElement(n.OwnerViewId) as View;
                var graphics = GraphicsFor(v, planGraphics);
                for (int i = 0; i < ls.Count; i++)
                {
                    PlanimetryReadBudget.Checkpoint();
                    leaders++;
                    LeaderHit hit = LeaderTargetReader.CastForLeader(doc, v, LeaderTargetReader.RayViewFor(v, v3), ls[i].End, graphics);
                    if (hit.Source != null) hits++;
                    if (hit.Coverage == "partial" || hit.Coverage == "not_cast") rayIncomplete = true;
                    missing3d |= hit.Coverage == "no_3d_view";
                    rows.Add(new JObject
                    {
                        ["entity_kind"] = "leader_target", ["note_id"] = Rid.Value(n.Id), ["view_id"] = Rid.Value(n.OwnerViewId),
                        ["leader_index"] = i, ["end_mm"] = Mm3(ls[i].End), ["target"] = hit.ToJson()
                    });
                }
            }
            JObject coverage = Coverage3d(v3, 0);
            coverage["ray"] = rayIncomplete ? "partial" : missing3d ? "no_3d_view" : "complete";
            return CommandResult.Ok(new JObject
            {
                ["mode"] = "leader_targets", ["notes_scanned"] = notes.Count, ["notes_without_leader"] = withoutLeader,
                ["leaders"] = leaders, ["leaders_with_target"] = hits, ["rows"] = rows, ["truncated"] = truncated,
                ["unmatched_ids"] = new JArray(unmatched), ["coverage"] = coverage
            });
        }

        // ---- view_ray: what the view's own looking direction meets from each point ---------------------

        private static LeaderTargetReader.PlanLinkGraphics GraphicsFor(View view,
            Dictionary<ElementId, LeaderTargetReader.PlanLinkGraphics> cache)
        {
            if (!(view is ViewPlan plan)) return null;
            if (!cache.TryGetValue(plan.Id, out var graphics))
            {
                graphics = new LeaderTargetReader.PlanLinkGraphics(plan);
                cache.Add(plan.Id, graphics);
            }
            return graphics;
        }

        private static CommandResult ViewRay(Document doc, JObject request, JArray rows)
        {
            View view = NeedAs<View>(doc, request.Value<long>("view_id"), "view");
            View3D v3 = LeaderTargetReader.FindView3D(doc);
            int hits = 0, i = 0;
            foreach (JToken t in PlanimetryReadBudget.CheckEach((JArray)request["points_mm"]))
            {
                var p = new XYZ(t[0].Value<double>() * FtPerMm, t[1].Value<double>() * FtPerMm, t[2].Value<double>() * FtPerMm);
                LeaderHit hit = LeaderTargetReader.Cast(doc, view, v3, p);
                if (hit.Source != null) hits++;
                rows.Add(new JObject { ["entity_kind"] = "view_ray", ["index"] = i++, ["point_mm"] = Mm3(p), ["target"] = hit.ToJson() });
            }
            return CommandResult.Ok(new JObject { ["mode"] = "view_ray", ["view_id"] = Rid.Value(view.Id), ["points"] = rows.Count, ["with_target"] = hits, ["rows"] = rows, ["coverage"] = Coverage3d(v3, 0) });
        }

        // ---- annotation_staleness ---------------------------------------------------------------------

        private static CommandResult Staleness(Document doc, JObject request, int maxRows, JArray rows)
        {
            List<long> unmatched;
            List<TextNote> notes = NotesOf(doc, request, out unmatched);
            var changed = new HashSet<string>(((request["changed_unique_ids"] as JArray) ?? new JArray()).Select(t => (string)t), StringComparer.Ordinal);
            var deleted = new HashSet<string>(((request["deleted_unique_ids"] as JArray) ?? new JArray()).Select(t => (string)t), StringComparer.Ordinal);
            View3D v3 = LeaderTargetReader.FindView3D(doc);
            var planGraphics = new Dictionary<ElementId, LeaderTargetReader.PlanLinkGraphics>();
            int stale = 0, current = 0, unknown = 0, untracked = 0, withOrigin = 0, withHit = 0;
            bool rayIncomplete = false, missing3d = false;
            foreach (TextNote n in PlanimetryReadBudget.CheckEach(notes))
            {
                IList<Leader> leaders = n.GetLeaders();
                var hits = new List<LeaderHit>(leaders.Count);
                if (leaders.Count > 0)
                {
                    View view = doc.GetElement(n.OwnerViewId) as View;
                    View3D rayView = LeaderTargetReader.RayViewFor(view, v3);
                    var graphics = GraphicsFor(view, planGraphics);
                    foreach (Leader leader in PlanimetryReadBudget.CheckEach(leaders))
                        hits.Add(LeaderTargetReader.CastForLeader(doc, view, rayView, leader.End, graphics));
                    rayIncomplete |= hits.Any(hit => !CompleteRay(hit));
                    missing3d |= hits.Any(hit => hit.Coverage == "no_3d_view");
                    if (hits.Any(hit => hit.Source != null)) withHit++;
                }

                var association = ScheduleAssociationStorage.Read(n);
                NoteOrigin.Record rec = association == null ? NoteOrigin.Read(n) : null;
                string verdict;
                var reasons = new List<string>();
                if (association != null)
                {
                    bool? exists = null; string hash = null;
                    try
                    {
                        Element sourceElement = doc.GetElement(association.SourceUniqueId);
                        exists = sourceElement != null;
                        var schedule = sourceElement as ViewSchedule;
                        if (schedule != null && schedule.UniqueId == association.ScheduleUniqueId)
                            hash = ScheduleSource.Hash(schedule, ScheduleSource.Read(schedule));
                    }
                    catch { exists = null; }
                    verdict = AnnotationDependencyRules.ScheduleVerdict(association.SourceHash, exists, hash);
                }
                else if (rec?.Source == "schedule")
                {
                    verdict = "unknown";
                    reasons.Add("schedule association missing or unreadable");
                }
                else
                {
                    bool? originExists = null; string currentSignature = null;
                    if (rec != null)
                    {
                        withOrigin++;
                        ReadNoteOrigin(doc, rec, hits, out originExists, out currentSignature);
                    }
                    reasons = NoteLeaderRules.StaleReasons(rec?.SourceUniqueId, originExists, rec?.Signature, currentSignature,
                        null, false, changed, deleted, out verdict);
                    if (verdict == "untracked" && hits.Count > 0)
                    {
                        verdict = "current";
                        reasons.Clear();
                    }
                }

                var leaderRows = new JArray();
                for (int i = 0; i < hits.Count; i++)
                {
                    LeaderHit hit = hits[i];
                    bool seen = CompleteRay(hit);
                    string leaderVerdict;
                    List<string> leaderReasons = NoteLeaderRules.StaleReasons(null, null, null, null,
                        seen ? hit.UniqueId : null, seen, changed, deleted, out leaderVerdict, hasLeader: true);
                    foreach (string reason in leaderReasons)
                    {
                        if (hit.UniqueId == rec?.SourceUniqueId &&
                            ((reason == "target_deleted" && reasons.Contains("origin_deleted")) ||
                             (reason == "target_changed" && reasons.Contains("origin_changed"))))
                            continue;
                        if (!reasons.Contains(reason)) reasons.Add(reason);
                    }
                    if (leaderVerdict == "stale" && verdict != "source_deleted") verdict = "stale";
                    else if (verdict == "current" && leaderVerdict != "current") verdict = leaderVerdict;
                    else if (verdict == "unknown_no_target" && leaderVerdict == "unknown_not_measured")
                        verdict = leaderVerdict;

                    if (rows.Count < maxRows)
                        leaderRows.Add(new JObject { ["leader_index"] = i, ["target"] = hit.ToJson(),
                            ["verdict"] = leaderVerdict, ["reasons"] = new JArray(leaderReasons) });
                }

                if (verdict == "stale" || verdict == "source_deleted") stale++;
                else if (verdict == "current") current++;
                else if (verdict == "untracked") untracked++;
                else unknown++;
                if (verdict == "current" || rows.Count >= maxRows) continue;
                var row = new JObject
                {
                    ["entity_kind"] = "note_staleness", ["note_id"] = Rid.Value(n.Id),
                    ["view_id"] = Rid.Value(n.OwnerViewId), ["verdict"] = verdict
                };
                if (association != null)
                {
                    row["schedule_unique_id"] = association.ScheduleUniqueId;
                    row["origin_unique_id"] = association.SourceUniqueId;
                }
                else if (rec?.Source != "schedule")
                {
                    row["origin_unique_id"] = rec?.SourceUniqueId;
                    row["leader_target"] = hits.FirstOrDefault()?.ToJson();
                }
                if (association == null || hits.Count > 0) row["reasons"] = new JArray(reasons);
                if (hits.Count > 0) row["leader_targets"] = leaderRows;
                rows.Add(row);
            }
            var tableReads = new List<bool>();
            var tableRows = new JArray();
            if (request["sheet_ids"] is JArray sheetIds)
            {
                foreach (long sheetId in PlanimetryReadBudget.CheckEach(sheetIds.Select(t => t.Value<long>()).Distinct()))
                {
                    ViewSheet sheet = NeedAs<ViewSheet>(doc, sheetId, "sheet");
                    foreach (ScheduleSheetInstance instance in PlanimetryReadBudget.CheckEach(new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()))
                    {
                        try
                        {
                            var schedule = doc.GetElement(instance.ScheduleId) as ViewSchedule;
                            if (schedule == null) throw new InvalidOperationException("schedule unavailable");
                            string body = ScheduleSource.Read(schedule, coverageRead: true);
                            tableReads.Add(true);
                            tableRows.Add(new JObject { ["sheet_id"] = sheetId, ["schedule_id"] = Rid.Value(schedule.Id), ["source_hash"] = ScheduleSource.Hash(schedule, body), ["read"] = true });
                        }
                        catch (Exception ex)
                        {
                            tableReads.Add(false);
                            tableRows.Add(new JObject { ["sheet_id"] = sheetId, ["schedule_id"] = Rid.Value(instance.ScheduleId), ["read"] = false, ["reason"] = ex.Message });
                        }
                    }
                }
            }
            bool tablesCovered = AnnotationDependencyRules.TablesCovered(request["sheet_ids"] is JArray, tableReads);
            bool clean = AnnotationDependencyRules.IsClean(stale, unknown, !rayIncomplete, tablesCovered);
            var cov = Coverage3d(v3, 0);
            cov["ray"] = missing3d && v3 == null ? "no_3d_view" : rayIncomplete ? "partial" : "complete";
            cov["notes_with_origin"] = withOrigin; cov["notes_with_leader_target"] = withHit;
            cov["finishes_tables"] = new JObject { ["complete"] = tablesCovered, ["schedules"] = tableRows,
                ["scope"] = "all schedules placed on the supplied sheets; native schedule bodies are read, associated notes are compared separately" };
            return CommandResult.Ok(new JObject
            {
                ["mode"] = "annotation_staleness", ["notes_scanned"] = notes.Count, ["stale"] = stale, ["current"] = current, ["unknown"] = unknown, ["untracked"] = untracked,
                ["clean"] = clean,
                ["clean_note"] = clean ? "Notes and scoped schedules were read without findings." : (tablesCovered ? "" : "Schedule coverage incomplete. ") + (stale > 0 ? stale + " stale. " : "") + (unknown > 0 ? unknown + " could not be judged. " : "") + (missing3d ? "No 3D view for one or more leader rays." : rayIncomplete ? "Leader ray coverage incomplete." : ""),
                ["changed_ids_given"] = changed.Count, ["deleted_ids_given"] = deleted.Count,
                ["rows"] = rows, ["unmatched_ids"] = new JArray(unmatched), ["coverage"] = cov
            });
        }

        private static bool CompleteRay(LeaderHit hit) => hit.Cast && hit.Coverage == "complete";

        private static void ReadNoteOrigin(Document doc, NoteOrigin.Record origin, List<LeaderHit> hits,
            out bool? exists, out string signature)
        {
            exists = null;
            signature = null;
            Document sourceDoc = doc;
            if (origin.LinkInstanceUniqueId != null)
                sourceDoc = (doc.GetElement(origin.LinkInstanceUniqueId) as RevitLinkInstance)?.GetLinkDocument();
            if (sourceDoc == null) return;

            Element source = sourceDoc.GetElement(origin.SourceUniqueId);
            exists = source != null;
            if (source == null) return;

            if (origin.Source == "material")
            {
                if (!hits.Any(CompleteRay)) return;
                string structural = null;
                Parameter parameter = source.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                if (parameter != null && parameter.AsElementId() != ElementId.InvalidElementId)
                    structural = sourceDoc.GetElement(parameter.AsElementId())?.Name;
                foreach (LeaderHit hit in hits.Where(CompleteRay))
                {
                    string material = NoteLeaderRules.MaterialFromMatchingHit(origin.SourceUniqueId, origin.LinkInstanceUniqueId,
                        hit.UniqueId, hit.LinkInstanceUniqueId, hit.LayerMaterial, structural);
                    if (material == null) continue;
                    signature = NoteLeaderRules.Signature(origin.SourceUniqueId, origin.Source, material);
                    // Another leader can reach a different layer of the same element.
                    if (signature == origin.Signature) break;
                }
                return;
            }

            var type = sourceDoc.GetElement(source.GetTypeId()) as ElementType;
            string text = type?.Name;
            if (origin.Source != "type_name")
            {
                var layers = type is HostObjAttributes host ? LeaderTargetReader.Layers(sourceDoc, host) : null;
                text = layers?.Count > 0 ? NoteLeaderRules.LayersText(layers, type.Name) : null;
            }
            if (text != null) signature = NoteLeaderRules.Signature(origin.SourceUniqueId, origin.Source, text);
        }
    }
}
