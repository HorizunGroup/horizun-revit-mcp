// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_view_visibility operation=copy_link_overrides.
//
// Field request (cloud workshared model, Revit 2024): turn off ONLY the Grids and
// Levels of a linked model in a set of views and keep the rest of the link visible.
// That lives in Visibility/Graphics > Revit Links > Custom > per-category settings,
// and the Revit API (2023-2027) does not expose it: RevitLinkGraphicsSettings carries
// LinkVisibilityType and LinkedViewId (2024) and a handful of by-host/custom switches
// (2025+), never the per-category table. What the API CAN do is move the whole RVT
// Links tab from one view to another, the way "Apply template properties" does:
//
//   1. CreateViewTemplate from the SOURCE view (the person set it up once, by hand);
//   2. SetNonControlledTemplateParameterIds so the temporary template controls ONLY
//      VIS_GRAPHICS_RVT_LINKS - nothing else of the source reaches the targets;
//   3. ApplyViewTemplateParameters on each target (the template is NOT assigned);
//   4. delete the temporary template - all inside one transaction.
//
// Re-read after commit, and honest about what cannot be read:
//   - the temporary template no longer exists; each target's ViewTemplateId unchanged;
//   - 2024+: per link instance and type, the RevitLinkGraphicsSettings the API reads
//     (visibility type, linked view; on 2025+ also object styles, nested links, view
//     filters, view range, colour fill, detail level, discipline, phase, phase filter)
//     must equal the source's - a postcondition;
//   - 2024+: as EVIDENCE, the link elements visible in each view per category
//     (FilteredElementCollector(host, view, link)), source vs target: a category the
//     source hides and a target still shows is named. Counts legitimately differ
//     between views (level, crop), so this informs and does not fail the edit;
//     ONLY for views open in Revit's UI: on a view never drawn in the session the
//     collector threw InternalException in 2024 and KILLED Revit 2025 outright (no
//     exception, journal ends; measured 2026-10-28), so an unopened view is unknown;
//   - the per-category table itself is NOT readable through the API in any year, and
//     the reply says so (not_readable_via_api); on 2023 the link settings are not
//     readable at all and only the template mechanics are verified.
//
// Targets must be OUR views (the tool's rule); the source is only read. Each target
// must be the same view type as the source, and a target whose own view template
// controls the RVT Links tab is refused (the template would override the copy).
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ViewVisibilityCommand
    {
        /// <summary>Targets whose visible link elements are counted as evidence; beyond this, not counted.</summary>
        private const int MaxCountedTargets = 50;

        private const string NotReadable =
            "The per-category visibility INSIDE a link (Visibility/Graphics > Revit Links > Custom > Model/Annotation " +
            "categories, e.g. the link's Grids or Levels off) is not exposed by the Revit API in 2023-2027 and cannot be " +
            "re-read. It was copied by Revit itself as part of the RVT Links parameter of the source view. Check it with " +
            "category_evidence (2024+) or horizun_capture_view.";

        private static ArchModelEdit CopyLinkOverrides(Document doc, JObject r, View source, HashSet<string> session)
        {
            var linksParam = new ElementId(BuiltInParameter.VIS_GRAPHICS_RVT_LINKS);
            if (!source.GetTemplateParameterIds().Any(id => id.Equals(linksParam)))
                throw new ArgumentException("view_id " + Rid.Value(source.Id) + " ('" + source.Name + "') has no RVT Links " +
                    "visibility settings to copy (its view type does not carry VIS_GRAPHICS_RVT_LINKS). Nothing was written.");

            List<RevitLinkInstance> links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>().OrderBy(l => Rid.Value(l.Id)).ToList();
            if (links.Count == 0)
                throw new ArgumentException("This model has no Revit link instances; there are no link settings to copy. Nothing was written.");

            var targets = new List<View>();
            var alsoAffected = new SortedDictionary<long, string>();
            foreach (JToken t in (JArray)r["view_ids"])
            {
                long raw = t.Value<long>();
                View v = Rid.CanRepresent(raw) ? doc.GetElement(Rid.Make(raw)) as View : null;
                if (v == null) throw new ArgumentException("view_ids: " + raw + " is not a view of this document. Nothing was written.");
                if (targets.Any(x => x.Id.Equals(v.Id))) continue;
                if (v.IsTemplate) throw new ArgumentException("view_ids: " + raw + " is a view template; copy_link_overrides edits views. Nothing was written.");
                RequireOwn(doc, v, session);
                // A primary view and its dependents share Visibility/Graphics: applying the
                // RVT Links tab to one changes them all, so every one of them must be ours.
                foreach (View shared in SharedVisibilityViews(doc, v))
                    if (!alsoAffected.ContainsKey(Rid.Value(shared.Id))) { RequireOwn(doc, shared, session); alsoAffected[Rid.Value(shared.Id)] = shared.Name; }
                if (v.ViewType != source.ViewType)
                    throw new ArgumentException("view '" + v.Name + "' (" + raw + ") is a " + v.ViewType + " and the source is a " +
                        source.ViewType + "; the RVT Links settings are copied between views of the same type. Nothing was written.");
                if (!v.GetTemplateParameterIds().Any(id => id.Equals(linksParam)))
                    throw new ArgumentException("view '" + v.Name + "' (" + raw + ") does not carry RVT Links settings. Nothing was written.");
                if (v.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(v.ViewTemplateId) is View assigned &&
                    !assigned.GetNonControlledTemplateParameterIds().Any(id => id.Equals(linksParam)))
                    throw new ArgumentException("view '" + v.Name + "' (" + raw + ") uses view template '" + assigned.Name + "' (" +
                        Rid.Value(assigned.Id) + "), which controls the RVT Links tab: it would override the copy. Change the " +
                        "template, or stop it controlling RVT Links, first. Nothing was written.");
                targets.Add(v);
            }

            List<ElementId> linkKeys = LinkKeys(doc, links);
            HashSet<string> evidenceCats = EvidenceCategories(r);
            JObject sourceBefore = LinkState(doc, source, links, linkKeys, evidenceCats, count: true);
            var before = new JObject();
            foreach (View v in targets)
                before[Rid.Value(v.Id).ToString()] = LinkState(doc, v, links, linkKeys, evidenceCats, count: targets.IndexOf(v) < MaxCountedTargets);
            var templateBefore = targets.ToDictionary(v => Rid.Value(v.Id), v => Rid.Value(v.ViewTemplateId));

            var edit = new ArchModelEdit();
            edit.Summary["operation"] = "copy_link_overrides";
            edit.Summary["source"] = new JObject { ["view_id"] = Rid.Value(source.Id), ["name"] = source.Name, ["view_type"] = source.ViewType.ToString() };
            edit.Summary["targets"] = new JArray(targets.Select(v => new JObject { ["view_id"] = Rid.Value(v.Id), ["name"] = v.Name }));
            edit.Summary["links"] = new JArray(links.Select(l => new JObject { ["link_instance_id"] = Rid.Value(l.Id), ["name"] = l.Name }));
            edit.Summary["mechanism"] = "temporary view template from the source controlling only VIS_GRAPHICS_RVT_LINKS, applied " +
                                        "to each target with ApplyViewTemplateParameters (not assigned), then deleted - one transaction";
            edit.Summary["source_link_settings"] = sourceBefore;
            edit.Summary["targets_before"] = before;
            foreach (View v in targets) alsoAffected.Remove(Rid.Value(v.Id));
            edit.Summary["views_sharing_visibility"] = new JArray(alsoAffected.Select(kv => new JObject { ["view_id"] = kv.Key, ["name"] = kv.Value }));
            edit.Summary["not_readable_via_api"] = NotReadable;
            foreach (View v in targets)
            {
                // The link settings rehearsed are part of the plan: a change to the source's or a
                // target's RVT Links tab between rehearsal and apply makes the token stale.
                PlannedElement planned = ModelEditRunner.Planned(v, PlannedAction.Modify, r);
                planned.BeforeValues["rvt_links"] = (before[Rid.Value(v.Id).ToString()]?["settings"] ?? JValue.CreateNull()).ToString(Newtonsoft.Json.Formatting.None);
                planned.BeforeValues["source_rvt_links"] = (sourceBefore["settings"] ?? JValue.CreateNull()).ToString(Newtonsoft.Json.Formatting.None);
                edit.Planned.Add(planned);
            }

            ElementId tempId = ElementId.InvalidElementId;
            string tempName = null;
            edit.Apply = d =>
            {
                View src = (View)d.GetElement(source.Id);
                View temp = src.CreateViewTemplate();
                tempId = temp.Id;
                tempName = temp.Name;
                // Everything the template could control EXCEPT the RVT Links tab is left
                // uncontrolled, so ApplyViewTemplateParameters carries that tab and nothing else.
                temp.SetNonControlledTemplateParameterIds(temp.GetTemplateParameterIds().Where(id => !id.Equals(linksParam)).ToList());
                foreach (View v in targets) ((View)d.GetElement(v.Id)).ApplyViewTemplateParameters(temp);
                d.Delete(tempId);
            };
            edit.Verify = d =>
            {
                var req = new List<string> { "temporary_template_deleted" };
                req.AddRange(targets.Select(v => "view_template_unchanged:" + Rid.Value(v.Id)));
                bool readable = LinkSettingsReadable;
                if (readable)
                    foreach (View v in targets) foreach (ElementId k in linkKeys) req.Add("link_settings:" + Rid.Value(v.Id) + ":" + Rid.Value(k));
                else
                    req.AddRange(targets.Select(v => "link_settings:" + Rid.Value(v.Id)));
                var check = new PostconditionCheck(req.ToArray());
                bool tempGone = tempId != ElementId.InvalidElementId && d.GetElement(tempId) == null;
                check.Compare("temporary_template_deleted", true, tempGone);
                View srcNow = (View)d.GetElement(source.Id);
                JObject sourceAfter = LinkState(d, srcNow, links, linkKeys, evidenceCats, count: true);
                var after = new JObject();
                var categoryEvidence = new JObject();
                int index = 0;
                foreach (View v in targets)
                {
                    View now = (View)d.GetElement(v.Id);
                    long id = Rid.Value(v.Id);
                    check.Compare("view_template_unchanged:" + id, templateBefore[id], Rid.Value(now.ViewTemplateId));
                    JObject state = LinkState(d, now, links, linkKeys, evidenceCats, count: index++ < MaxCountedTargets);
                    after[id.ToString()] = state;
                    if (readable)
                        foreach (ElementId k in linkKeys)
                        {
                            string key = Rid.Value(k).ToString();
                            JObject want = sourceAfter["settings"]?[key] as JObject, got = state["settings"]?[key] as JObject;
                            if (want?.Value<bool?>("readable") == false || got?.Value<bool?>("readable") == false)
                            {
                                // Two identical read errors are not two identical settings.
                                check.Unreadable("link_settings:" + id + ":" + key, want,
                                    "link settings could not be read: " + ((string)got?["error"] ?? (string)want?["error"]));
                                continue;
                            }
                            List<string> differ = ViewVisibilityRules.LinkSettingsDiffer(want, got);
                            check.Record("link_settings:" + id + ":" + key, want, got, want != null && got != null && differ.Count == 0);
                        }
                    else
                        check.Unreadable("link_settings:" + id, null,
                            "Revit 2023 exposes no link graphic settings (View.GetLinkOverrides is 2024+); only the template mechanics were verified.");
                    var perLink = new JObject();
                    foreach (RevitLinkInstance l in links)
                    {
                        string lk = Rid.Value(l.Id).ToString();
                        if (state["visible_by_category"]?[lk] is JObject t && sourceAfter["visible_by_category"]?[lk] is JObject s)
                            perLink[lk] = t["measured"] != null || s["measured"] != null
                                ? (JToken)new JObject { ["measured"] = false, ["source"] = s.DeepClone(), ["target"] = t.DeepClone() }
                                : ViewVisibilityRules.CategoryPresenceDiffers(s, t);
                    }
                    if (perLink.Count > 0) categoryEvidence[id.ToString()] = perLink;
                }
                edit.Evidence["temporary_template"] = new JObject { ["id"] = tempId == ElementId.InvalidElementId ? null : (JToken)Rid.Value(tempId), ["name"] = tempName, ["deleted"] = tempGone };
                edit.Evidence["source_link_settings"] = sourceAfter;
                edit.Evidence["targets_after"] = after;
                edit.Evidence["category_evidence"] = new JObject
                {
                    ["available"] = LinkSettingsReadable,
                    ["differs_from_source"] = categoryEvidence,
                    ["means"] = LinkSettingsReadable
                        ? "Per target and link: categories whose link elements are visible in one view and not the other " +
                          "(FilteredElementCollector over the link in each host view). Empty = same presence as the source. " +
                          "Counts differ legitimately between views (level, crop, view range); this is evidence, not a postcondition. " +
                          "MEASURED (Revit 2024): the counts reflect each view's LAST DRAWING - a target not redrawn since the " +
                          "commit still shows its previous state; open it (horizun_navigate open_view) and read again."
                        : "Revit 2023 cannot list link elements visible in a host view; no category evidence."
                };
                edit.Evidence["link_settings_readable"] = readable;
                edit.Evidence["not_readable_via_api"] = NotReadable;
                return check;
            };
            return edit;
        }

        // static readonly, not const: a const false would make the 2023 build warn about
        // unreachable code, and every year must build with zero warnings.
#if REVIT2023
        private static readonly bool LinkSettingsReadable = false;
#else
        private static readonly bool LinkSettingsReadable = true;
#endif

        /// <summary>The other views that share Visibility/Graphics with this one: its primary and the primary's dependents.</summary>
        private static IEnumerable<View> SharedVisibilityViews(Document doc, View view)
        {
            ElementId primaryId = view.GetPrimaryViewId();
            View primary = primaryId != ElementId.InvalidElementId ? doc.GetElement(primaryId) as View : view;
            if (primary == null) yield break;
            if (!primary.Id.Equals(view.Id)) yield return primary;
            foreach (ElementId d in primary.GetDependentViewIds())
                if (!d.Equals(view.Id) && doc.GetElement(d) is View dependent) yield return dependent;
        }

        /// <summary>Instances and their types: the RVT Links tab has a row for each.</summary>
        private static List<ElementId> LinkKeys(Document doc, List<RevitLinkInstance> links)
        {
            var keys = new List<ElementId>();
            foreach (RevitLinkInstance l in links)
            {
                ElementId type = l.GetTypeId();
                if (type != ElementId.InvalidElementId && !keys.Any(k => k.Equals(type))) keys.Add(type);
                keys.Add(l.Id);
            }
            return keys;
        }

        private static HashSet<string> EvidenceCategories(JObject r)
        {
            var cats = ((r["categories"] as JArray) ?? new JArray()).Select(t => t.ToString().Trim()).Where(t => t.Length > 0).ToList();
            return cats.Count == 0 ? null : new HashSet<string>(cats, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The readable link settings of one view (2024+) and, when asked, the visible link elements per category.</summary>
        private static JObject LinkState(Document doc, View view, List<RevitLinkInstance> links, List<ElementId> keys,
                                         HashSet<string> evidenceCats, bool count)
        {
            var state = new JObject { ["view_id"] = Rid.Value(view.Id) };
#if REVIT2023
            state["settings"] = null;
            state["visible_by_category"] = null;
            state["note"] = "Revit 2023 exposes no link graphic settings (View.GetLinkOverrides is 2024+).";
#else
            var settings = new JObject();
            foreach (ElementId k in keys) settings[Rid.Value(k).ToString()] = ReadLinkSettings(view, k);
            state["settings"] = settings;
            if (count)
            {
                var visible = new JObject();
                foreach (RevitLinkInstance l in links) visible[Rid.Value(l.Id).ToString()] = VisibleByCategory(doc, view, l, evidenceCats);
                state["visible_by_category"] = visible;
            }
            else state["visible_by_category"] = null;
#endif
            return state;
        }

#if !REVIT2023
        private static JObject ReadLinkSettings(View view, ElementId linkId)
        {
            var o = new JObject();
            try
            {
                RevitLinkGraphicsSettings g = view.GetLinkOverrides(linkId);
                // MEASURED (Revit 2024): null when the view has no override for this link,
                // i.e. the RVT Links row is at its default (by host view). That is a state,
                // and it is compared like one.
                if (g == null) { o["override"] = false; return o; }
                o["override"] = true;
                o["visibility_type"] = g.LinkVisibilityType.ToString();
                o["linked_view_id"] = Rid.Value(g.LinkedViewId);
#if REVIT2025 || REVIT2026 || REVIT2027
                o["object_styles"] = g.ObjectStyles.ToString();
                o["nested_links"] = g.NestedLinks.ToString();
                o["view_filters"] = g.ViewFilterType.ToString();
                o["view_range"] = g.ViewRange.ToString();
                o["color_fill"] = g.ColorFill.ToString();
                o["detail_level"] = g.GetViewDetailLevelType() + ":" + g.GetViewDetailLevel();
                o["discipline"] = g.GetDisciplineType() + ":" + g.GetDiscipline();
                o["phase"] = g.GetPhaseType() + ":" + Rid.Value(g.GetPhaseId());
                o["phase_filter"] = g.GetPhaseFilterType() + ":" + Rid.Value(g.GetPhaseFilterId());
#endif
            }
            catch (Exception ex) { o["readable"] = false; o["error"] = ex.Message; }
            return o;
        }

        /// <summary>Ids of the views open in Revit's UI for this document; null when that cannot be read.</summary>
        private static HashSet<long> OpenViewIds(Document doc)
        {
            try { return new HashSet<long>(new Autodesk.Revit.UI.UIDocument(doc).GetOpenUIViews().Select(u => Rid.Value(u.ViewId))); }
            catch { return null; }
        }

        private static JToken VisibleByCategory(Document doc, View view, RevitLinkInstance link, HashSet<string> only)
        {
            // NEVER on a view that is not open in the UI. MEASURED 2026-10-28 (Revit 2025.4): the
            // link-in-view collector on a view never drawn in this session terminated Revit -
            // no exception to catch, the journal just ends. 2024 threw InternalException for the
            // same view. An open view has been drawn, so it is the one safe case.
            HashSet<long> open = OpenViewIds(doc);
            if (open == null || !open.Contains(Rid.Value(view.Id)))
                return new JObject { ["measured"] = false, ["reason"] = "view_not_open_in_ui",
                                     ["how"] = "open the view (horizun_navigate open_view) and repeat to get category evidence; " +
                                               "counting a view that was never drawn terminates Revit 2025" };
            try
            {
                var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
                foreach (Element e in new FilteredElementCollector(doc, view.Id, link.Id).WhereElementIsNotElementType())
                {
                    Category c = e.Category;
                    if (c == null) continue;
                    // THE ENUM, NOT AN INT: BuiltInCategory is Int64-backed from 2024 and
                    // Enum.IsDefined with a boxed int throws there (CodeCheckReaders.cs).
                    long v = Rid.Value(c.Id);
                    var bic = (BuiltInCategory)v;
                    string key = v < 0 && v >= int.MinValue && Enum.IsDefined(typeof(BuiltInCategory), bic) ? bic.ToString() : c.Name;
                    if (only != null && !only.Contains(key)) continue;
                    counts[key] = counts.TryGetValue(key, out long n) ? n + 1 : 1;
                }
                var o = new JObject();
                if (only != null) foreach (string k in only) if (!counts.ContainsKey(k)) o[k] = 0;
                foreach (var kv in counts) o[kv.Key] = kv.Value;
                return o;
            }
            catch (Autodesk.Revit.Exceptions.InternalException)
            {
                // MEASURED (Revit 2024): the link-in-view collector throws until the view has
                // been drawn once in this session; after opening it, it counts. Unknown, not 0.
                return new JObject { ["measured"] = false, ["reason"] = "view_not_drawn_in_this_session",
                                     ["how"] = "open the view once (horizun_navigate open_view) and repeat to get category evidence" };
            }
            catch (Exception ex) { return new JObject { ["measured"] = false, ["error"] = ex.Message }; }
        }
#endif
    }
}
