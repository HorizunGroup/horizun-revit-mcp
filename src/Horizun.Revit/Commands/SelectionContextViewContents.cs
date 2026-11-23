// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_selection_context operation=view_contents (read-only): what does this view draw?
//
// Field case: a coordinate box said "this bathroom has no door and no fixtures"; the door
// was 187 mm outside the box and the fixtures were inside a group. The view itself knows
// what it draws, so this reads the view, per category, and adds what a box misses:
//   - the members of every visible group: Element.GroupId marks them (group_id). What the
//     view collector did NOT return but a walk over Group.GetMemberIds finds (cropped out,
//     outside the view range, filtered, another phase or workset) is NOT proven drawn: it is
//     kept apart (group_walk_total / group_walk_rows, drawn "unverified"), out of every total,
//     and it makes coverage_complete false;
//   - the elements of each linked model shown in the view, one link at a time
//     (FilteredElementCollector(doc, viewId, linkInstanceId), Revit 2024+; on 2023 the
//     answer is "unknown" with that reason). NEVER on a view that is not open in the UI:
//     measured 2026-10-28, the link-in-view collector on a view never drawn in the session
//     terminated Revit 2025, so such a view is "unknown" and the reply says how to open it.
//     Links nested inside a link are not read and are named in "unknown";
//   - a warning when a requested category, the Revit Links category or a link is hidden
//     in the view (a hidden thing is not counted, and the reply says it was hidden);
//   - optionally the room's REAL outline (or a polygon) with a tolerance, instead of a box:
//     an element whose box overlaps the outline is inside, one within the tolerance is near
//     with its distance, and one without a readable box is unknown - never dropped as outside.
// Cost: the collectors take the requested categories as a filter and, with a region, its box
// widened by the tolerance (the elements the view owns are kept: annotations have no model box).
// Read-only: nothing is written, no transaction is opened.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class SelectionContextCommand
    {
        /// <summary>Category name and OST_ name per category id, for one document (the id of a non built-in category differs between documents).</summary>
        private sealed class CategoryNames
        {
            private readonly Dictionary<long, string[]> _byId = new Dictionary<long, string[]>();
            public string[] Of(Category c)
            {
                long id = Rid.Value(c.Id);
                if (!_byId.TryGetValue(id, out string[] names)) _byId[id] = names = new[] { c.Name, BuiltInName(c) };
                return names;
            }
        }

        private CommandResult ViewContents(UIDocument uidoc, Document doc, JObject request)
        {
            if (!ViewContentsRules.TryParse(request, out ViewContentsRequest req, out string error))
                return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, error);

            View view = null;
            if (req.ViewId.HasValue)
            {
                if (Rid.CanRepresent(req.ViewId.Value)) view = doc.GetElement(Rid.Make(req.ViewId.Value)) as View;
                if (view == null) return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, "view_id " + req.ViewId.Value + " is not a view of '" + doc.Title + "'.");
            }
            else
            {
                view = uidoc.ActiveView;
                if (view == null) return CommandResult.FailCoded(ErrorCodes.SinVista, "There is no active view; send view_id.");
            }
            if (view.IsTemplate || view is ViewSheet || view is ViewSchedule)
                return CommandResult.FailCoded(ErrorCodes.EntradaInvalida,
                    "view_contents reads a graphical view of the model (plan, section, elevation, 3D, drafting); '" + view.Name + "' is a " +
                    (view.IsTemplate ? "view template" : view is ViewSheet ? "sheet" : "schedule") + ". Send the view_id of the view that draws it.");

            var warnings = new JArray();
            var unknown = new JArray();

            // ---- the region: the room's real outline, or the polygon the caller sent
            RegionSpec region = req.Region;
            JObject regionInfo = null;
            if (req.RoomId.HasValue)
            {
                SpatialElement room = Rid.CanRepresent(req.RoomId.Value) ? doc.GetElement(Rid.Make(req.RoomId.Value)) as SpatialElement : null;
                if (room == null)
                    return CommandResult.FailCoded(ErrorCodes.EntradaInvalida, "room_id " + req.RoomId.Value + " is not a room, space or area of '" + doc.Title + "' (a room of a linked model is not supported: send region).");
                region = RegionOfRoom(room, out string roomProblem, out bool roomIsReadFailure);
                if (region == null) return CommandResult.FailCoded(roomIsReadFailure ? ErrorCodes.ApiRevitExcepcion : ErrorCodes.EntradaInvalida, roomProblem);
            }
            double toleranceFeet = req.ToleranceMm / 304.8;
            if (region != null)
            {
                regionInfo = new JObject
                {
                    ["source"] = region.Source,
                    ["tolerance_mm"] = req.ToleranceMm,
                    ["loops"] = region.Loops.Count,
                    ["points"] = region.Loops.Sum(l => l.Count),
                    ["coordinates"] = "internal coordinates of the host model (not shared coordinates), in " + req.Unit,
                    ["z_min"] = region.ZMinFeet.HasValue ? (JToken)Math.Round(region.ZMinFeet.Value * req.LengthFromFeet, 1) : JValue.CreateNull(),
                    ["z_max"] = region.ZMaxFeet.HasValue ? (JToken)Math.Round(region.ZMaxFeet.Value * req.LengthFromFeet, 1) : JValue.CreateNull(),
                    ["prefilter"] = "the collectors keep elements whose model box touches the region widened by the tolerance, and the elements the view owns; elements with no model box are read in a second pass and counted as region_unknown"
                };
                if (req.RoomId.HasValue) regionInfo["room_id"] = req.RoomId.Value;
                if (!region.ZMinFeet.HasValue && !region.ZMaxFeet.HasValue)
                    regionInfo["height_range"] = "none: the outline is tested in plan only";
            }

            // ---- the categories the model knows: hidden warnings, unknown names, the filter's ids
            var hostNames = new CategoryNames();
            var known = new List<KeyValuePair<string, string>>();
            var hiddenCategories = new List<Category>();
            var requestedIds = new List<ElementId>();
            try
            {
                foreach (Category c in doc.Settings.Categories)
                {
                    string bic = hostNames.Of(c)[1];
                    known.Add(new KeyValuePair<string, string>(bic ?? c.Name, c.Name));
                    if (req.Categories.Count > 0 && ViewContentsRules.CategoryMatches(req.Categories, bic, c.Name)) requestedIds.Add(c.Id);
                    if (CategoryHiddenIn(view, c)) hiddenCategories.Add(c);
                }
            }
            catch (Exception ex) { unknown.Add(new JObject { ["what"] = "category list", ["reason"] = ex.Message }); }
            foreach (string missing in ViewContentsRules.UnmatchedRequests(req.Categories, known))
                warnings.Add("'" + missing + "' is not a category of this model (use OST_Doors or Doors); nothing can be counted under that name.");
            var hiddenListed = new JArray();
            foreach (Category c in hiddenCategories.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (req.Categories.Count > 0)
                {
                    if (ViewContentsRules.CategoryMatches(req.Categories, hostNames.Of(c)[1], c.Name))
                        warnings.Add("Category '" + c.Name + "' is hidden in this view: what the view does not draw is not counted, so a 0 here is not 'none in the model'.");
                    continue;
                }
                // no filter: the hidden MODEL categories that do have elements in the model (an empty hidden category is not news)
                if (hiddenListed.Count >= ViewContentsRules.MaxHiddenListed || c.CategoryType != CategoryType.Model) continue;
                int inModel;
                try { inModel = new FilteredElementCollector(doc).OfCategoryId(c.Id).WhereElementIsNotElementType().GetElementCount(); } catch { continue; }
                if (inModel > 0) hiddenListed.Add(new JObject { ["category"] = c.Name, ["elements_in_model"] = inModel });
            }
            if (hiddenListed.Count > 0)
                warnings.Add(hiddenListed.Count + " model categor" + (hiddenListed.Count == 1 ? "y is" : "ies are") + " hidden in this view and have elements in the model (see hidden_categories): they are not counted here.");

            // ---- the host: what the view collector returns, then the members of the groups
            var items = new List<ContentItem>();
            var seenHost = new HashSet<long>();
            var groups = new List<Group>();
            bool nothingToRead = req.Categories.Count > 0 && requestedIds.Count == 0;
            try
            {
                if (!nothingToRead)
                {
                    FilteredElementCollector host = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
                    if (req.Categories.Count > 0) host = host.WherePasses(new ElementMulticategoryFilter(requestedIds));
                    if (region != null) host = host.WherePasses(new LogicalOrFilter(RegionBoxFilter(region, toleranceFeet, null), new ElementOwnerViewFilter(view.Id)));
                    foreach (Element e in host)
                    {
                        if (!seenHost.Add(Rid.Value(e.Id))) continue;
                        AddItem(items, e, doc, hostNames, "host", null, null, false, view, req, region, toleranceFeet);
                    }
                    if (region != null)
                    {
                        // the box filter drops, without a word, every model element that has no box: read them apart (same view and
                        // category filter, no box filter) so they are counted as region_unknown instead of vanishing
                        FilteredElementCollector boxless = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
                        if (req.Categories.Count > 0) boxless = boxless.WherePasses(new ElementMulticategoryFilter(requestedIds));
                        foreach (Element e in boxless)
                        {
                            if (seenHost.Contains(Rid.Value(e.Id))) continue;
                            if (e.get_BoundingBox(null) != null) continue;
                            seenHost.Add(Rid.Value(e.Id));
                            AddItem(items, e, doc, hostNames, "host", null, null, false, view, req, region, toleranceFeet);
                        }
                    }
                }
                // the groups themselves are read whatever the filter: their members are walked below
                foreach (Element g in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Group)).WhereElementIsNotElementType())
                    if (g is Group group) groups.Add(group);
            }
            catch (Exception ex)
            {
                return CommandResult.FailCoded(ErrorCodes.ApiRevitExcepcion, "The view '" + view.Name + "' could not be read: " + ex.GetType().Name + ": " + ex.Message);
            }

            int walkSkippedHidden = 0, groupsWalked = 0, tooDeep = 0;
            var queue = new Queue<KeyValuePair<Group, int>>(groups.Select(g => new KeyValuePair<Group, int>(g, 0)));
            var walked = new HashSet<long>();
            while (queue.Count > 0)
            {
                KeyValuePair<Group, int> next = queue.Dequeue();
                Group group = next.Key;
                if (!walked.Add(Rid.Value(group.Id))) continue;
                if (next.Value > ViewContentsRules.MaxGroupDepth)
                {
                    tooDeep++;
                    unknown.Add(new JObject { ["what"] = "members of group " + Rid.Value(group.Id), ["reason"] = "nested more than " + ViewContentsRules.MaxGroupDepth + " levels deep: not walked" });
                    continue;
                }
                groupsWalked++;
                List<long> memberIds;
                try { memberIds = group.GetMemberIds().Select(Rid.Value).ToList(); }
                catch (Exception ex) { unknown.Add(new JObject { ["what"] = "members of group " + Rid.Value(group.Id), ["reason"] = ex.Message }); continue; }
                foreach (long id in ViewContentsRules.MembersNotYetSeen(seenHost, memberIds))
                {
                    Element m = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (m == null) continue;
                    if (m is Group inner) queue.Enqueue(new KeyValuePair<Group, int>(inner, next.Value + 1));
                    bool hidden = false;
                    try { hidden = m.IsHidden(view) || (m.Category != null && CategoryHiddenIn(view, m.Category)); } catch { }
                    if (hidden) { walkSkippedHidden++; continue; }
                    seenHost.Add(id);
                    AddItem(items, m, doc, hostNames, "host", null, null, true, view, req, region, toleranceFeet);
                }
            }
            int walkAdded = items.Count(i => i.ViaGroupWalk);
            var groupInfo = new JObject
            {
                ["groups_in_view"] = groups.Count,
                ["groups_walked"] = groupsWalked,
                ["members_found_only_by_walk"] = walkAdded,
                ["members_skipped_hidden"] = walkSkippedHidden,
                ["groups_too_deep"] = tooDeep,
                ["walk_members_are"] = "NOT in any total: the view collector did not return them (crop, view range, filter, phase or workset), so whether they are drawn is unverified",
                ["groups_inside_links_walked"] = false
            };

            // ---- the links
            var linkRows = new JArray();
            if (req.IncludeLinks)
            {
                HashSet<long> open = OpenViewIdsOf(doc);
                bool viewOpen = open != null && open.Contains(Rid.Value(view.Id));
                bool linksCategoryHidden = false;
                try { linksCategoryHidden = CategoryHiddenIn(view, Category.GetCategory(doc, BuiltInCategory.OST_RvtLinks)); } catch { }
                if (linksCategoryHidden) warnings.Add("The Revit Links category is hidden in this view: no linked element is drawn by it.");
                foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().OrderBy(l => Rid.Value(l.Id)))
                {
                    Document linked = null;
                    try { linked = link.GetLinkDocument(); } catch { }
                    bool hiddenHere = false;
                    try { hiddenHere = link.IsHidden(view); } catch { }
                    string visibility = null;
#if !REVIT2023
                    bool supportsLinkCollector = true;
                    try { visibility = view.GetLinkOverrides(link.Id)?.LinkVisibilityType.ToString(); } catch { }
#else
                    bool supportsLinkCollector = false;
#endif
                    LinkViewState st = ViewContentsRules.LinkState(Rid.Value(link.Id), link.Name, linked != null, hiddenHere, linksCategoryHidden, supportsLinkCollector, visibility);
                    if (st.Status == "read" && !viewOpen)
                    {
                        st.Status = "unknown";
                        st.Reason = "view_not_open_in_ui: counting a link in a view never drawn in this session terminates Revit 2025, so it is not attempted. " +
                                    "Open the view (horizun_navigate open_view) and repeat.";
                    }
                    if (st.Status == "read" && visibility == "Custom")
                        warnings.Add("Link '" + link.Name + "' uses Custom visibility: its per-category table is not readable through the API; the counts are what the view collector returns.");
                    int nestedLinks = 0;
                    if (st.Status == "read")
                    {
                        Transform toHost = null;
                        try { toHost = link.GetTotalTransform(); } catch { }
                        try
                        {
                            st.Elements = ReadLink(doc, view, link, linked, toHost, items, req, region, toleranceFeet);
                            try { nestedLinks = new FilteredElementCollector(linked).OfClass(typeof(RevitLinkInstance)).GetElementCount(); } catch { }
                        }
                        catch (Exception ex)
                        {
                            st.Status = "unreadable";
                            st.Reason = ex.GetType().Name + ": " + ex.Message;
                        }
                    }
                    if (st.Status == "hidden_in_view") warnings.Add("Link '" + link.Name + "': " + st.Reason + ".");
                    if (st.Status == "unknown" || st.Status == "unloaded" || st.Status == "unreadable")
                        unknown.Add(new JObject { ["what"] = "link '" + link.Name + "' (" + Rid.Value(link.Id) + ")", ["status"] = st.Status, ["reason"] = st.Reason });
                    if (nestedLinks > 0)
                        unknown.Add(new JObject
                        {
                            ["what"] = "links inside link '" + link.Name + "' (" + Rid.Value(link.Id) + ")",
                            ["status"] = "not_read",
                            ["reason"] = "the linked model has " + nestedLinks + " link instance(s) of its own; their elements are not counted"
                        });
                    var lr = new JObject
                    {
                        ["link_instance_id"] = st.LinkInstanceId,
                        ["name"] = st.Name,
                        ["status"] = st.Status,
                        ["visibility"] = st.Visibility
                    };
                    if (st.Status == "read") lr["elements"] = st.Elements;
                    if (nestedLinks > 0) lr["nested_links"] = nestedLinks;
                    if (st.Reason != null) lr["reason"] = st.Reason;
                    linkRows.Add(lr);
                }
            }
            else
            {
                unknown.Add(new JObject { ["what"] = "linked models", ["status"] = "not_read", ["reason"] = "include_links=false: the elements of linked models were not read and are not counted" });
            }

            // ---- aggregate and answer
            double feetToUnit = req.LengthFromFeet;
            ViewContentsRules.Aggregation agg = ViewContentsRules.Aggregate(items, req.MaxRows, region != null, feetToUnit, it => DescribeContent(it));
            if (agg.RowsCapped)
                warnings.Add("The reply holds at most " + ViewContentsRules.MaxTotalRows + " rows over all categories; some categories returned fewer rows than max_rows. The totals are exact.");
            if (agg.GroupWalkTotal > 0)
                warnings.Add(agg.GroupWalkTotal + " group member(s) were found only by walking the groups: the view collector did not return them, so they are kept out of every total (group_walk_total, drawn: unverified) and coverage_complete is false.");
            if (region != null)
            {
                regionInfo["near_total"] = agg.Categories.Sum(c => (int?)c["near_total"] ?? 0);
                regionInfo["excluded_total"] = agg.ExcludedByRegion;
                regionInfo["unknown_total"] = agg.RegionUnknown;
                if (agg.RegionUnknown > 0)
                    warnings.Add(agg.RegionUnknown + " element(s) have no readable bounding box, so they could not be placed inside or outside the region; they are counted as region_unknown per category (with a few ids), never as outside.");
                if (agg.ExcludedByRegion > 0 && agg.ElementsTotal == 0 && agg.GroupWalkTotal == 0 && agg.RegionUnknown == 0)
                    warnings.Add("The region excluded every one of the " + agg.ExcludedByRegion + " element(s) read: check that its points are in the host's internal coordinates and in '" + req.Unit + "', and the tolerance.");
            }
            var result = new JObject
            {
                ["target_document"] = doc.Title,
                ["operation"] = SelectionContextOps.ViewContents,
                ["view"] = new JObject { ["id"] = Rid.Value(view.Id), ["name"] = view.Name, ["type"] = view.ViewType.ToString(), ["active"] = uidoc.ActiveView != null && uidoc.ActiveView.Id == view.Id },
                ["unit"] = req.Unit,
                ["categories_requested"] = new JArray(req.Categories),
                ["elements_total"] = agg.ElementsTotal,
                ["group_walk_total"] = agg.GroupWalkTotal,
                ["categories"] = agg.Categories,
                ["groups"] = groupInfo,
                ["links_read"] = req.IncludeLinks,
                ["links"] = linkRows,
                ["warnings"] = warnings,
                ["unknown"] = unknown,
                ["coverage_complete"] = unknown.Count == 0 && agg.GroupWalkTotal == 0,
                ["note"] = "What the view draws: the elements Revit returns for this view (category visibility, filters, view range, crop), plus linked elements; group members the collector did not return are listed apart. It is not a pixel check."
            };
            if (hiddenListed.Count > 0) result["hidden_categories"] = hiddenListed;
            if (regionInfo != null) result["region"] = regionInfo;
            return CommandResult.Ok(result);
        }

        private static ElementFilter RegionBoxFilter(RegionSpec region, double toleranceFeet, Transform hostToLinkOrNull)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (List<double[]> loop in region.Loops)
                foreach (double[] p in loop)
                {
                    minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]);
                    minY = Math.Min(minY, p[1]); maxY = Math.Max(maxY, p[1]);
                }
            double minZ = region.ZMinFeet.HasValue ? region.ZMinFeet.Value - toleranceFeet : -1e5;
            double maxZ = region.ZMaxFeet.HasValue ? region.ZMaxFeet.Value + toleranceFeet : 1e5;
            XYZ lo = new XYZ(minX - toleranceFeet, minY - toleranceFeet, minZ), hi = new XYZ(maxX + toleranceFeet, maxY + toleranceFeet, maxZ);
            if (hostToLinkOrNull != null)
            {
                double lx = double.MaxValue, ly = double.MaxValue, lz = double.MaxValue, hx = double.MinValue, hy = double.MinValue, hz = double.MinValue;
                foreach (double x in new[] { lo.X, hi.X })
                    foreach (double y in new[] { lo.Y, hi.Y })
                        foreach (double z in new[] { lo.Z, hi.Z })
                        {
                            XYZ q = hostToLinkOrNull.OfPoint(new XYZ(x, y, z));
                            lx = Math.Min(lx, q.X); ly = Math.Min(ly, q.Y); lz = Math.Min(lz, q.Z);
                            hx = Math.Max(hx, q.X); hy = Math.Max(hy, q.Y); hz = Math.Max(hz, q.Z);
                        }
                lo = new XYZ(lx, ly, lz); hi = new XYZ(hx, hy, hz);
            }
            return new BoundingBoxIntersectsFilter(new Outline(lo, hi));
        }

        private static int ReadLink(Document host, View view, RevitLinkInstance link, Document linked, Transform toHost, List<ContentItem> items,
                                    ViewContentsRequest req, RegionSpec region, double toleranceFeet)
        {
            int count = 0;
            if (region != null && toHost == null)
                throw new InvalidOperationException("the link's transform could not be read, so its elements cannot be placed in the host's coordinates");
#if !REVIT2023
            var names = new CategoryNames();
            var ids = new List<ElementId>();
            if (req.Categories.Count > 0)
            {
                foreach (Category c in linked.Settings.Categories)
                    if (ViewContentsRules.CategoryMatches(req.Categories, names.Of(c)[1], c.Name)) ids.Add(c.Id);
                if (ids.Count == 0) return 0;      // the linked model has no category of that name: nothing to read
            }
            FilteredElementCollector collector = new FilteredElementCollector(host, view.Id, link.Id).WhereElementIsNotElementType();
            if (ids.Count > 0) collector = collector.WherePasses(new ElementMulticategoryFilter(ids));
            var seen = new HashSet<long>();
            if (region != null)
            {
                // never the host's coordinates in the link's collector: without the inverse transform the box filter is not applied
                Transform inverse = null;
                try { inverse = toHost.Inverse; } catch { inverse = null; }
                if (inverse != null) collector = collector.WherePasses(RegionBoxFilter(region, toleranceFeet, inverse));
            }
            foreach (Element e in collector)
            {
                seen.Add(Rid.Value(e.Id));
                int before = items.Count;
                AddItem(items, e, linked, names, "link", Rid.Value(link.Id), toHost, false, view, req, region, toleranceFeet);
                count += items.Count - before;
            }
            if (region != null)
            {
                // elements the box filter dropped because they have no model box: counted as region_unknown
                FilteredElementCollector boxless = new FilteredElementCollector(host, view.Id, link.Id).WhereElementIsNotElementType();
                if (ids.Count > 0) boxless = boxless.WherePasses(new ElementMulticategoryFilter(ids));
                foreach (Element e in boxless)
                {
                    if (seen.Contains(Rid.Value(e.Id))) continue;
                    if (e.get_BoundingBox(null) != null) continue;
                    int before = items.Count;
                    AddItem(items, e, linked, names, "link", Rid.Value(link.Id), toHost, false, view, req, region, toleranceFeet);
                    count += items.Count - before;
                }
            }
#endif
            return count;
        }

        private static void AddItem(List<ContentItem> items, Element e, Document source, CategoryNames names, string kind, long? linkId, Transform toHost, bool viaWalk,
                                    View view, ViewContentsRequest req, RegionSpec region, double toleranceFeet)
        {
            Category c = e.Category;
            if (c == null) return;
            string[] n = names.Of(c);
            if (!ViewContentsRules.CategoryMatches(req.Categories, n[1], n[0])) return;
            long? groupId = null;
            try { if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId) groupId = Rid.Value(e.GroupId); } catch { }
            var item = new ContentItem
            {
                CategoryName = n[0],
                CategoryKey = n[1] ?? n[0],
                Id = Rid.Value(e.Id),
                SourceKind = kind,
                LinkInstanceId = linkId,
                GroupId = groupId,
                ViaGroupWalk = viaWalk,
                Handle = source          // only the document: the sample is re-read by id, so no Element is kept for thousands of rows
            };
            if (region != null)
            {
                double[] min = null, max = null;
                try { BoxInHost(e, toHost, view, out min, out max); } catch { min = max = null; }
                item.Region = ViewContentsRules.Classify(region, min, max, toleranceFeet, out double? distance);
                item.DistanceFeet = distance;
            }
            items.Add(item);
        }

        /// <summary>The element's box in the HOST's coordinates (a linked element goes through the link's transform). Null when it has none.</summary>
        private static void BoxInHost(Element e, Transform toHost, View view, out double[] min, out double[] max)
        {
            min = max = null;
            BoundingBoxXYZ b = e.get_BoundingBox(null);
            if (b == null && e.ViewSpecific) b = e.get_BoundingBox(view);
            if (b == null || b.Min == null || b.Max == null) return;
            Transform t = toHost;
            if (b.Transform != null && !b.Transform.IsIdentity) t = t == null ? b.Transform : t.Multiply(b.Transform);
            double lox = double.MaxValue, loy = double.MaxValue, loz = double.MaxValue, hix = double.MinValue, hiy = double.MinValue, hiz = double.MinValue;
            foreach (double x in new[] { b.Min.X, b.Max.X })
                foreach (double y in new[] { b.Min.Y, b.Max.Y })
                    foreach (double z in new[] { b.Min.Z, b.Max.Z })
                    {
                        XYZ p = t == null ? new XYZ(x, y, z) : t.OfPoint(new XYZ(x, y, z));
                        lox = Math.Min(lox, p.X); loy = Math.Min(loy, p.Y); loz = Math.Min(loz, p.Z);
                        hix = Math.Max(hix, p.X); hiy = Math.Max(hiy, p.Y); hiz = Math.Max(hiz, p.Z);
                    }
            min = new[] { lox, loy, loz };
            max = new[] { hix, hiy, hiz };
        }

        private static RegionSpec RegionOfRoom(SpatialElement room, out string problem, out bool readFailure)
        {
            problem = null; readFailure = false;
            IList<IList<BoundarySegment>> loops;
            try { loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish }); }
            catch (Exception ex) { problem = "The outline of '" + room.Name + "' could not be read: " + ex.Message; readFailure = true; return null; }
            if (loops == null || loops.Count == 0)
            {
                problem = "'" + room.Name + "' has no boundary (not placed, not enclosed or redundant): there is no outline to test. Send region instead.";
                return null;
            }
            var region = new RegionSpec { Source = "room" };
            int total = 0;
            foreach (IList<BoundarySegment> loop in loops)
            {
                var pts = new List<double[]>();
                foreach (BoundarySegment seg in loop)
                {
                    Curve curve = seg.GetCurve();
                    IList<XYZ> poly = curve is Line ? new List<XYZ> { curve.GetEndPoint(0) } : curve.Tessellate();
                    foreach (XYZ p in poly) pts.Add(new[] { p.X, p.Y });
                    total += poly.Count;
                    if (total > ViewContentsRules.MaxRoomPoints)
                    {
                        problem = "The outline of '" + room.Name + "' has more than " + ViewContentsRules.MaxRoomPoints + " points once its curves are tessellated: too many to test element by element. Send region with a simpler polygon.";
                        return null;
                    }
                }
                if (pts.Count >= 3) region.Loops.Add(pts);
            }
            if (region.Loops.Count == 0) { problem = "The outline of '" + room.Name + "' has fewer than 3 points."; return null; }
            // an Area is a plan region with no height: only rooms and spaces get the height band of their box
            if (!(room is Autodesk.Revit.DB.Area))
            {
                try
                {
                    BoundingBoxXYZ rb = room.get_BoundingBox(null);
                    if (rb != null) { region.ZMinFeet = rb.Min.Z; region.ZMaxFeet = rb.Max.Z; }
                }
                catch { }
            }
            return region;
        }

        private static JObject DescribeContent(ContentItem it)
        {
            Document d = (Document)it.Handle;
            Element e = Rid.CanRepresent(it.Id) ? d.GetElement(Rid.Make(it.Id)) : null;
            if (e == null) return new JObject { ["element_id"] = it.Id, ["read_error"] = "the element is no longer in the model" };
            string typeName = null;
            try
            {
                Element type = d.GetElement(e.GetTypeId());
                if (type != null)
                {
                    string family = (type as ElementType)?.FamilyName;
                    typeName = string.IsNullOrEmpty(family) ? type.Name : family + ": " + type.Name;
                }
            }
            catch { }
            string name = null;
            try { name = e.Name; } catch { }
            return new JObject
            {
                ["element_id"] = it.Id,
                ["unique_id"] = e.UniqueId,
                ["name"] = name,
                ["type"] = typeName,
                ["level"] = LevelNameOf(d, e)
            };
        }

        private static string LevelNameOf(Document d, Element e)
        {
            try
            {
                if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId) return d.GetElement(e.LevelId)?.Name;
                Parameter p = e.get_Parameter(BuiltInParameter.LEVEL_PARAM) ?? e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                if (p == null) return null;
                if (p.StorageType == StorageType.ElementId) return d.GetElement(p.AsElementId())?.Name;
                return p.AsValueString();
            }
            catch { return null; }
        }

        /// <summary>The OST_ name of a built-in category (the enum, not an int: BuiltInCategory is Int64-backed from 2024), else null.</summary>
        private static string BuiltInName(Category c)
        {
            try
            {
                long v = Rid.Value(c.Id);
                var bic = (BuiltInCategory)v;
                return v < 0 && v >= int.MinValue && Enum.IsDefined(typeof(BuiltInCategory), bic) ? bic.ToString() : null;
            }
            catch { return null; }
        }

        private static bool CategoryHiddenIn(View view, Category c)
        {
            try { return c != null && c.get_AllowsVisibilityControl(view) && view.GetCategoryHidden(c.Id); }
            catch { return false; }
        }

        private static HashSet<long> OpenViewIdsOf(Document doc)
        {
            try { return new HashSet<long>(new UIDocument(doc).GetOpenUIViews().Select(u => Rid.Value(u.ViewId))); }
            catch { return null; }
        }
    }
}
