// -----------------------------------------------------------------------------
// Horizun Revit MCP - deterministic, read-only annotation planning: auto_tags.
//
// Explicit elements (element_ids, as before) or a SWEEP of the view when element_ids is
// omitted: every model element Revit lists as visible in the view, members of groups
// included, and - with include_links - the elements of each loaded link visible in it
// (FilteredElementCollector(doc, view, link), Revit 2024+). Rooms, areas and spaces get their
// own tag at their location; every other element an IndependentTag at a collision-free point
// of the same bounded spiral as before. Nothing already tagged in the view is planned twice
// (host ids, link pairs and room/area/space tags all count).
//
// The reply accounts for EVERY element seen, per category: planned, already tagged, or not
// taggable with a coded reason (TagRules / AutoTagReport). coverage_complete is false whenever
// something visible was left out. It never writes: horizun_annotate rehearses each tag.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class PlanAnnotationsCommand
    {
#if REVIT2023
        // MEASURED: FilteredElementCollector(Document, viewId, linkId) does not exist in Revit 2023.
        private static readonly bool LinkViewCollector = false;
#else
        private static readonly bool LinkViewCollector = true;
#endif
        private const int MaxPlannedTags = 500;

        private sealed class TagCandidate
        {
            public Element Element;
            public RevitLinkInstance Link;
            public string Category;
            public bool InGroup;
            public string Key => Link == null ? Rid.Value(Element.Id).ToString() : Rid.Value(Link.Id) + ":" + Rid.Value(Element.Id);
        }

        private static string BicName(Category c)
        {
            if (c == null) return null;
            try { BuiltInCategory b = c.BuiltInCategory; return b == BuiltInCategory.INVALID ? null : b.ToString(); }
            catch { return null; }
        }

        private static IList<Element> VisibleInLink(Document doc, View view, RevitLinkInstance link)
        {
#if REVIT2023
            return new List<Element>();
#else
            return new FilteredElementCollector(doc, view.Id, link.Id).WhereElementIsNotElementType().ToElements();
#endif
        }

        private static bool IsTaggableModelElement(Element e)
        {
            if (e == null || e is ElementType || e is View || e.Category == null) return false;
            try { return e.Category.CategoryType == CategoryType.Model; } catch { return false; }
        }

        private static CommandResult PlanTags(Document doc, JObject request, string units, double toFeet)
        {
            string error;
            View view = ResolveView(doc, request, out error);
            if (view == null) return CommandResult.Fail(error);
            bool sweep = request["element_ids"] == null;
            List<Element> targets = null;
            if (!sweep)
            {
                targets = ResolveTargets(doc, request["element_ids"] as JArray, MaxPlannedTags, out error);
                if (targets == null) return CommandResult.Fail(error);
            }
            HashSet<string> categories = null;
            if (request["categories"] != null)
            {
                JArray list = request["categories"] as JArray;
                if (!sweep) return CommandResult.Fail("categories filters a sweep of the view; with element_ids the elements are already named. Send one or the other.");
                if (list == null || list.Count == 0 || list.Count > 100 || list.Any(t => t.Type != JTokenType.String || !((string)t).StartsWith("OST_", StringComparison.Ordinal)))
                    return CommandResult.Fail("categories must be 1..100 BuiltInCategory names such as OST_Doors.");
                categories = new HashSet<string>(list.Select(t => (string)t), StringComparer.Ordinal);
            }
            bool includeLinks = request.Value<bool?>("include_links") ?? false;
            if (includeLinks && !sweep)
                return CommandResult.Fail("include_links adds the link elements visible in the view to a sweep; omit element_ids to sweep.");

            double clearance = (request.Value<double?>("clearance") ?? 10.0) * toFeet;
            if (clearance < 0) return CommandResult.Fail("clearance must be zero or greater.");
            bool skipExisting = request.Value<bool?>("skip_existing") ?? true;
            bool addLeader = request.Value<bool?>("add_leader") ?? true;
            string mode = TagRules.NormalizeMode(request.Value<string>("tag_mode"));
            if (mode != TagRules.ByCategory && mode != TagRules.MultiCategory && mode != TagRules.Material)
                return CommandResult.Fail("auto_tags tag_mode is by_category, multi_category or material; keynotes are placed one by one with horizun_annotate.");
            long? tagType = request.Value<long?>("tag_type_id");
            ElementType tagTypeElement = null;
            if (tagType.HasValue)
            {
                tagTypeElement = Rid.CanRepresent(tagType.Value) ? doc.GetElement(Rid.Make(tagType.Value)) as ElementType : null;
                if (tagTypeElement == null) return CommandResult.Fail("tag_type_id must identify an ElementType in the active document.");
            }
            string tagTypeCategory = tagTypeElement == null ? null : BicName(tagTypeElement.Category);

            HashSet<long> accepted;
            try { accepted = AnnotationVisibility.ReadAccepted(request["accept_unmeasurable"]); }
            catch (ArgumentException ex) { return CommandResult.Fail(ex.Message); }

            List<Box2> occupied;
            HashSet<long> existing;
            HashSet<string> existingLinked;
            JObject coverage;
            try
            {
                AnnotationSurvey survey = AnnotationLayout.Survey(doc, view, null, accepted);
                coverage = survey.Coverage;
                // A blocked survey is a structured refusal with ids, not a sentence.
                if (!survey.Complete)
                    return CommandResult.FailWithDetail(AnnotationVisibility.RefusalMessage(coverage), coverage);
                occupied = survey.Obstacles.Select(b => new Box2(b.MinX, b.MinY, b.MaxX, b.MaxY)).ToList();
                existing = ExistingTargets(doc, view.Id, out existingLinked);
            }
            catch (AnnotationCoverageException ex) { return CommandResult.FailWithDetail(ex.Message, ex.Coverage); }
            catch (Exception ex) { return CommandResult.Fail("Annotation coverage is unknown: " + ex.Message); }

            var report = new AutoTagReport();
            var candidates = new List<TagCandidate>();
            if (!sweep)
                candidates.AddRange(targets.OrderBy(e => Rid.Value(e.Id)).Select(e => new TagCandidate
                    { Element = e, Category = BicName(e.Category), InGroup = InGroup(e) }));
            else
            {
                var links = new List<RevitLinkInstance>();
                foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElements()
                    .OrderBy(x => Rid.Value(x.Id)))
                {
                    if (e is RevitLinkInstance link) { links.Add(link); continue; }
                    if (!IsTaggableModelElement(e)) continue;
                    string cat = BicName(e.Category);
                    if (categories != null && (cat == null || !categories.Contains(cat))) continue;
                    candidates.Add(new TagCandidate { Element = e, Category = cat, InGroup = InGroup(e) });
                }
                if (includeLinks)
                    foreach (RevitLinkInstance link in links)
                    {
                        string linkKey = "link:" + Rid.Value(link.Id);
                        Document linkDoc = null;
                        try { linkDoc = link.GetLinkDocument(); } catch { linkDoc = null; }
                        if (linkDoc == null) { report.Add("OST_RvtLinks", AutoTagReport.LinkNotLoaded, linkKey, false, true); continue; }
                        if (!LinkViewCollector) { report.Add("OST_RvtLinks", AutoTagReport.LinksNeedRevit2024, linkKey, false, true); continue; }
                        IList<Element> visible;
                        try { visible = VisibleInLink(doc, view, link); }
                        catch (Exception ex) { return CommandResult.Fail("The elements of link " + Rid.Value(link.Id) + " visible in the view could not be listed: " + ex.Message); }
                        foreach (Element e in visible.OrderBy(x => Rid.Value(x.Id)))
                        {
                            if (!IsTaggableModelElement(e)) continue;
                            string cat = BicName(e.Category);
                            if (categories != null && (cat == null || !categories.Contains(cat))) continue;
                            candidates.Add(new TagCandidate { Element = e, Link = link, Category = cat, InGroup = InGroup(e) });
                        }
                    }
            }

            var actions = new JArray();
            var skipped = new JArray();
            var unreadable = new JArray();
            var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            XYZ right = view.RightDirection.Normalize(), up = view.UpDirection.Normalize(), origin = view.Origin;
            int planned = 0;
            foreach (TagCandidate c in candidates)
            {
                Element target = c.Element;
                long id = Rid.Value(target.Id);
                bool fromLink = c.Link != null;
                if (planned >= MaxPlannedTags) { report.Add(c.Category, AutoTagReport.OverLimit, c.Key, c.InGroup, fromLink); continue; }
                bool tagged = fromLink ? existingLinked.Contains(c.Key) : existing.Contains(id);
                if (skipExisting && tagged)
                {
                    report.Add(c.Category, AutoTagReport.AlreadyTagged, c.Key, c.InGroup, fromLink);
                    if (skipped.Count < 200) skipped.Add(new JObject { ["element_id"] = fromLink ? (JToken)c.Key : id, ["reason"] = "already_tagged_in_view" });
                    continue;
                }

                if (target is Room || target is Area || target is Space)
                {
                    string why = PlanSpatialAction(doc, view, c, units, toFeet, tagTypeElement, sweep, typeCounts, actions);
                    if (why != null) report.Add(c.Category, why, c.Key, c.InGroup, fromLink);
                    else { report.Add(c.Category, AutoTagReport.Planned, c.Key, c.InGroup, fromLink); planned++; }
                    continue;
                }

                // A material tag over a link is refused by horizun_annotate (Revit throws InternalException),
                // so it is reported here instead of becoming a row that can only fail.
                if (fromLink && TagRules.IsMaterialMode(mode))
                { report.Add(c.Category, AutoTagReport.LinkedMaterial, c.Key, c.InGroup, fromLink); continue; }

                // Can the category be tagged at all in this mode, with what this document has loaded?
                string tagCategory = mode == TagRules.ByCategory ? TagRules.TagCategoryFor(c.Category) : TagRules.RequiredTypeCategory(mode);
                bool namedExplicitly = !sweep || (categories != null && c.Category != null && categories.Contains(c.Category));
                if (tagCategory == null && !namedExplicitly)
                { report.Add(c.Category, AutoTagReport.NoTagCategory, c.Key, c.InGroup, fromLink); continue; }
                bool typeForThis = tagTypeElement != null && (!sweep || tagTypeCategory == tagCategory);
                if (tagCategory != null && !typeForThis && LoadedTypeCount(doc, tagCategory, typeCounts) == 0)
                { report.Add(c.Category, AutoTagReport.NoTagTypeLoaded, c.Key, c.InGroup, fromLink); continue; }

                XYZ center = TagSeed(view, target, c.Link);
                if (center == null)
                {
                    report.Add(c.Category, AutoTagReport.NoBoundingBox, c.Key, c.InGroup, fromLink);
                    if (unreadable.Count < 200) unreadable.Add(new JObject { ["element_id"] = fromLink ? (JToken)c.Key : id, ["reason"] = "no bounding box in the requested view" });
                    continue;
                }
                XYZ chosen = null;
                // Deterministic square spiral.  Candidate 0 is above the target; later
                // candidates walk right/up/left/down in view-plane coordinates. A bounded
                // 1.2 m at the default clearance (120 rings), finite and deterministic; the
                // returned point remains subject to the real annotate rehearsal.
                for (int ring = 1; ring <= 120 && chosen == null; ring++)
                {
                    foreach (int[] cell in Ring(ring))
                    {
                        XYZ candidate = center.Add(right.Multiply(cell[0] * clearance))
                                              .Add(up.Multiply(cell[1] * clearance));
                        double x = candidate.Subtract(origin).DotProduct(right);
                        double y = candidate.Subtract(origin).DotProduct(up);
                        if (occupied.All(b => !b.Contains(x, y, clearance * 0.5)))
                        {
                            chosen = candidate;
                            occupied.Add(new Box2(x, y, x, y));
                            break;
                        }
                    }
                }
                if (chosen == null)
                {
                    report.Add(c.Category, AutoTagReport.NoFreePoint, c.Key, c.InGroup, fromLink);
                    if (unreadable.Count < 200) unreadable.Add(new JObject { ["element_id"] = fromLink ? (JToken)c.Key : id, ["reason"] = "no collision-free tag point found in 120 bounded search rings" });
                    continue;
                }
                var action = new JObject { ["operation"] = "tag", ["view_id"] = Rid.Value(view.Id) };
                if (fromLink) { action["link_instance_id"] = Rid.Value(c.Link.Id); action["linked_element_id"] = id; }
                else action["element_id"] = id;
                action["point"] = Point(chosen, 1.0 / toFeet);
                action["add_leader"] = addLeader;
                action["tag_mode"] = mode;
                action["orientation"] = request.Value<string>("orientation") ?? "horizontal";
                action["avoid_collisions"] = true;
                action["layout_clearance"] = clearance / toFeet;
                action["layout_max_displacement"] = request.Value<double?>("max_displacement") ?? 1200;
                action["require_tag_text"] = true;
                if (accepted.Count > 0)
                    action["layout_accept_unmeasurable"] = new JArray(accepted.OrderBy(x => x));
                if (typeForThis) action["tag_type_id"] = tagType.Value;
                actions.Add(action);
                report.Add(c.Category, AutoTagReport.Planned, c.Key, c.InGroup, fromLink);
                planned++;
            }

            bool complete = unreadable.Count == 0 && report.Complete;
            var next = AnnotateRequest(doc, units, actions);
            // A sweep spans many categories and families: one row Revit refuses must not cost the
            // others, so its request rehearses row by row (best_effort reports every refused row).
            if (sweep) next["on_failure"] = "best_effort";
            return CommandResult.Ok(new JObject
            {
                ["operation"] = "auto_tags", ["view_id"] = Rid.Value(view.Id),
                ["source"] = sweep ? (includeLinks ? "view_sweep_with_links" : "view_sweep") : "element_ids",
                ["requested"] = sweep ? candidates.Count : targets.Count, ["planned"] = planned,
                ["already_tagged"] = report.AlreadyTaggedCount, ["not_taggable"] = report.NotTaggableCount,
                ["by_category"] = report.ToJson(),
                ["skipped"] = skipped,
                ["unreadable"] = unreadable, ["coverage_complete"] = complete,
                ["annotation_coverage"] = coverage,
                ["clearance_scope"] = coverage.Value<string>("clearance_scope"),
                ["safe_to_execute"] = actions.Count > 0 && (complete || sweep),
                ["next_tool"] = "horizun_annotate", ["next_arguments"] = next,
                ["note"] = "This planner made no model changes. Run the returned horizun_annotate dry run; only its rehearsal proves the selected tag family/type can tag each target." +
                           (sweep ? " by_category accounts for every visible element: planned, already tagged, or not taggable with the reason." : "")
            });
        }

        private static bool InGroup(Element e)
        {
            try { return e.GroupId != null && e.GroupId != ElementId.InvalidElementId; } catch { return false; }
        }

        private static int LoadedTypeCount(Document doc, string tagCategory, Dictionary<string, int> cache)
        {
            int n;
            if (cache.TryGetValue(tagCategory, out n)) return n;
            BuiltInCategory bic;
            n = Enum.TryParse(tagCategory, out bic) && Enum.IsDefined(typeof(BuiltInCategory), bic)
                ? new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType().GetElementCount()
                : 0;
            cache[tagCategory] = n;
            return n;
        }

        /// <summary>The centre of the element's box in the view, in HOST coordinates (a linked box is carried by the link transform).</summary>
        private static XYZ TagSeed(View view, Element target, RevitLinkInstance link)
        {
            BoundingBoxXYZ bb;
            try { bb = link == null ? target.get_BoundingBox(view) : target.get_BoundingBox(null); } catch { bb = null; }
            if (bb == null) return null;
            XYZ center = bb.Min.Add(bb.Max).Multiply(0.5);
            if (link != null)
            {
                try { center = link.GetTotalTransform().OfPoint(center); } catch { return null; }
            }
            return center;
        }

        /// <summary>A room, area or space: its own tag at its location point, no leader, no layout move. Null when planned, else the reason code.</summary>
        private static string PlanSpatialAction(Document doc, View view, TagCandidate c, string units, double toFeet,
                                                ElementType tagType, bool sweep, Dictionary<string, int> typeCounts, JArray actions)
        {
            Element target = c.Element;
            if (c.Link != null && !(target is Room)) return AutoTagReport.LinkedAreaOrSpace;
            XYZ at = (target.Location as LocationPoint)?.Point;
            if (at == null) return AutoTagReport.UnplacedSpatial;
            if (c.Link != null)
            {
                try { at = c.Link.GetTotalTransform().OfPoint(at); } catch { return AutoTagReport.NoBoundingBox; }
            }
            string tagCategory = TagRules.TagCategoryFor(c.Category);
            var action = new JObject { ["operation"] = "tag", ["view_id"] = Rid.Value(view.Id) };
            if (c.Link != null) { action["link_instance_id"] = Rid.Value(c.Link.Id); action["linked_element_id"] = Rid.Value(target.Id); }
            else action["element_id"] = Rid.Value(target.Id);
            action["point"] = Point(at, 1.0 / toFeet);
            action["add_leader"] = false;
            if (tagType != null && BicName(tagType.Category) == tagCategory) action["tag_type_id"] = Rid.Value(tagType.Id);
            else
            {
                // A room, area or space tag with no type named takes what horizun_annotate would take:
                // the single loaded type of its tag category, else the document's default for that
                // category when it is one of those loaded types (TagRules.DefaultTagTypeId).
                BuiltInCategory bic = target is Room ? BuiltInCategory.OST_RoomTags
                    : target is Area ? BuiltInCategory.OST_AreaTags : BuiltInCategory.OST_MEPSpaceTags;
                if (LoadedTypeCount(doc, bic.ToString(), typeCounts) == 0) return AutoTagReport.NoTagTypeLoaded;
                List<long> loaded = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType()
                    .ToElementIds().Select(Rid.Value).ToList();
                long? def = null;
                try
                {
                    ElementId d = doc.GetDefaultFamilyTypeId(new ElementId(bic));
                    if (d != null && d != ElementId.InvalidElementId) def = Rid.Value(d);
                }
                catch { def = null; }
                long? chosen = TagRules.DefaultTagTypeId(loaded, def);
                if (chosen == null) return AutoTagReport.AmbiguousTagType;
                action["tag_type_id"] = chosen.Value;
            }
            actions.Add(action);
            return null;
        }

        /// <summary>
        /// What is already tagged in the view: host ids (category, multi-category, material and keynote
        /// tags, room/area/space tags) and link pairs "instance:element" (tags over a link, room tags of linked rooms).
        /// </summary>
        private static HashSet<long> ExistingTargets(Document doc, ElementId viewId, out HashSet<string> linked)
        {
            var result = new HashSet<long>();
            linked = new HashSet<string>(StringComparer.Ordinal);
            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                try
                {
                    if (tag.OwnerViewId != viewId) continue;
                    foreach (ElementId id in tag.GetTaggedLocalElementIds()) result.Add(Rid.Value(id));
                    foreach (LinkElementId l in tag.GetTaggedElementIds())
                        if (l != null && l.LinkInstanceId != null && l.LinkInstanceId != ElementId.InvalidElementId)
                            linked.Add(Rid.Value(l.LinkInstanceId) + ":" + Rid.Value(l.LinkedElementId));
                }
                catch (Exception ex) { throw new InvalidOperationException("Could not inspect existing tag " + Rid.Value(tag.Id) + ": " + ex.Message); }
            }
            foreach (SpatialElementTag tag in new FilteredElementCollector(doc).WherePasses(new LogicalOrFilter(new List<ElementFilter>
                {
                    new ElementCategoryFilter(BuiltInCategory.OST_RoomTags), new ElementCategoryFilter(BuiltInCategory.OST_AreaTags),
                    new ElementCategoryFilter(BuiltInCategory.OST_MEPSpaceTags)
                })).WhereElementIsNotElementType().OfType<SpatialElementTag>())
            {
                try
                {
                    if (tag.OwnerViewId != viewId) continue;
                    if (tag is RoomTag room)
                    {
                        LinkElementId bound = room.TaggedRoomId;
                        if (bound != null && bound.LinkInstanceId != null && bound.LinkInstanceId != ElementId.InvalidElementId)
                            linked.Add(Rid.Value(bound.LinkInstanceId) + ":" + Rid.Value(bound.LinkedElementId));
                        else if (room.TaggedLocalRoomId != null && room.TaggedLocalRoomId != ElementId.InvalidElementId)
                            result.Add(Rid.Value(room.TaggedLocalRoomId));
                    }
                    else if (tag is AreaTag area && area.Area != null) result.Add(Rid.Value(area.Area.Id));
                    else if (tag is SpaceTag space && space.Space != null) result.Add(Rid.Value(space.Space.Id));
                }
                catch (Exception ex) { throw new InvalidOperationException("Could not inspect existing tag " + Rid.Value(tag.Id) + ": " + ex.Message); }
            }
            return result;
        }
    }
}
