// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// THE REVIT-FREE HALF OF horizun_selection_context operation=view_contents (read-only).
//
// Field case: looking for the contents of a bathroom with a coordinate box gave false
// negatives ("no door, no fixtures") because the door sat 187 mm outside the box and the
// fixtures lived inside a group. The question "what does this view draw?" is answered by
// the view itself, not by a box, and it must include group members and the elements of
// every linked model shown in that view.
//
// This file holds what can be proved without Revit:
//  - the request (view, categories, rows per category, room or region, tolerance);
//  - the aggregation per category: totals, host / link / group counts, a capped sample;
//  - the category match (OST_ name or display name, never a substring);
//  - the room / region test on the real outline (polygon with holes), not a box: an
//    element whose box overlaps the outline is "inside", one within the tolerance is
//    "near" WITH its distance, and one without a readable box is "unknown", never dropped
//    as if it were outside;
//  - the state of each link in the view: read, hidden, unloaded, or unknown with a reason.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    public sealed class RegionSpec
    {
        /// <summary>Closed outlines in FEET (model coordinates of the host). The first is the outer one; the others are holes (even-odd).</summary>
        public List<List<double[]>> Loops = new List<List<double[]>>();
        public double? ZMinFeet, ZMaxFeet;
        /// <summary>Where the outline came from: room, region.</summary>
        public string Source;
    }

    public sealed class ViewContentsRequest
    {
        public long? ViewId;
        public List<string> Categories = new List<string>();
        public int MaxRows = ViewContentsRules.DefaultMaxRows;
        public long? RoomId;
        public RegionSpec Region;
        public double ToleranceMm = ViewContentsRules.DefaultToleranceMm;
        public bool IncludeLinks = true;
        public string Unit = "mm";
        public double LengthFromFeet = 304.8;
    }

    public enum RegionRelation { NotTested, Inside, Near, Outside, Unknown }

    /// <summary>One element the view draws, as the aggregation needs it. <see cref="Handle"/> is whatever the caller needs to describe the row later.</summary>
    public sealed class ContentItem
    {
        public string CategoryName;
        public string CategoryKey;
        public long Id;
        /// <summary>host | link</summary>
        public string SourceKind = "host";
        public long? LinkInstanceId;
        public long? GroupId;
        /// <summary>True when only the walk over a group's members found it (the view collector did not return it).</summary>
        public bool ViaGroupWalk;
        public RegionRelation Region = RegionRelation.NotTested;
        public double? DistanceFeet;
        public object Handle;
    }

    public sealed class LinkViewState
    {
        public long LinkInstanceId;
        public string Name;
        /// <summary>read | hidden_in_view | unloaded | unknown | unreadable</summary>
        public string Status;
        public string Reason;
        public string Visibility;
        public int Elements;
    }

    public static class ViewContentsRules
    {
        public const int DefaultMaxRows = 20;
        public const int MaxRowsCap = 200;
        public const int MaxCategories = 40;
        public const int MaxRegionPoints = 500;
        public const double DefaultToleranceMm = 200;
        public const double MaxToleranceMm = 5000;
        /// <summary>Rows returned over ALL categories; past it the sample stops and the reply says so (totals stay exact).</summary>
        public const int MaxTotalRows = 2000;
        /// <summary>Points of a room's tessellated outline; past it the room is refused (send region) instead of read slowly.</summary>
        public const int MaxRoomPoints = 2000;
        public const int MaxGroupDepth = 6;
        public const int MaxUnknownSampleIds = 5;
        public const int MaxHiddenListed = 40;

        /// <summary>Arguments that belong to the other operations of horizun_selection_context. view_contents refuses them (they would be ignored).</summary>
        public static readonly string[] ArgumentsOfOtherOperations =
            { "ids", "selector", "selection", "checks", "snapshot_name", "against", "include", "radius_mm", "parameters", "max_faces" };
        /// <summary>What validate_ids refuses besides the view_contents arguments: it takes target_document, operation and checks only.</summary>
        public static readonly string[] NotForValidateIds =
            { "ids", "selector", "selection", "snapshot_name", "against", "include", "radius_mm", "parameters", "max_faces", "tolerance_mm", "unit" };
        /// <summary>Arguments that belong to view_contents only. Every other operation refuses them.</summary>
        public static readonly string[] ViewContentsOnlyArguments =
            { "view_id", "categories", "max_rows", "room_id", "region", "include_links" };

        /// <summary>
        /// The first argument that this operation would silently ignore, or null. An argument sent as JSON null counts as absent.
        /// operation is the normalised name (context when none). checks belongs to validate_ids only, even among the old operations.
        /// </summary>
        public static string ForeignArgument(string operation, JObject request)
        {
            bool viewContents = operation == SelectionContextOps.ViewContents;
            foreach (string name in viewContents ? ArgumentsOfOtherOperations : ViewContentsOnlyArguments)
            {
                JToken t = request[name];
                if (t != null && t.Type != JTokenType.Null) return name;
            }
            if (operation == SelectionContextOps.ValidateIds)
            {
                foreach (string name in NotForValidateIds)
                {
                    JToken t = request[name];
                    if (t != null && t.Type != JTokenType.Null) return name;
                }
            }
            if (!viewContents && operation != SelectionContextOps.ValidateIds)
            {
                JToken t = request["checks"];
                if (t != null && t.Type != JTokenType.Null) return "checks";
            }
            return null;
        }

        public static string ForeignArgumentMessage(string operation, string field) =>
            operation == SelectionContextOps.ViewContents
                ? "'" + field + "' is not an argument of view_contents: it would be ignored, so the call is refused. view_contents takes view_id, categories, max_rows, room_id, region, include_links, tolerance_mm and unit."
                : operation == SelectionContextOps.ValidateIds && Array.IndexOf(ViewContentsOnlyArguments, field) < 0
                    ? "'" + field + "' is not an argument of validate_ids: it would be ignored, so the call is refused. validate_ids takes target_document and checks."
                : field == "checks"
                    ? "'checks' belongs to operation=validate_ids only: it would be ignored by '" + operation + "', so the call is refused."
                    : "'" + field + "' belongs to operation=view_contents only: it would be ignored by '" + operation + "', so the call is refused.";

        private static bool TryLong(JToken t, string name, out long value, out string error)
        {
            value = 0; error = null;
            if (t.Type != JTokenType.Integer) { error = name + " must be an integer."; return false; }
            try { value = (long)t; return true; }
            catch (OverflowException) { error = name + " is out of range for a 64-bit integer."; return false; }
        }
        // ------------------------------------------------------------------ the request

        public static bool TryParse(JObject request, out ViewContentsRequest parsed, out string error)
        {
            parsed = null; error = null;
            var r = new ViewContentsRequest();

            JToken view = request["view_id"];
            if (view != null && view.Type != JTokenType.Null)
            {
                if (!TryLong(view, "view_id", out long viewId, out error)) return false;
                r.ViewId = viewId;
            }

            JToken cats = request["categories"];
            if (cats != null && cats.Type != JTokenType.Null)
            {
                var arr = cats as JArray;
                if (arr == null) { error = "categories must be an array of category names (OST_Doors or Doors)."; return false; }
                foreach (JToken t in arr)
                {
                    string name = t.Type == JTokenType.String ? ((string)t).Trim() : null;
                    if (string.IsNullOrEmpty(name)) { error = "categories holds a value that is not a non-empty name: " + t; return false; }
                    if (!r.Categories.Contains(name, StringComparer.OrdinalIgnoreCase)) r.Categories.Add(name);
                }
                if (r.Categories.Count > MaxCategories) { error = "categories names " + r.Categories.Count + "; the limit is " + MaxCategories + "."; return false; }
            }

            JToken rows = request["max_rows"];
            if (rows != null && rows.Type != JTokenType.Null)
            {
                if (!TryLong(rows, "max_rows", out long maxRows, out error)) return false;
                if (maxRows < 0 || maxRows > MaxRowsCap) { error = "max_rows must be between 0 and " + MaxRowsCap + " (rows per category; the totals do not depend on it)."; return false; }
                r.MaxRows = (int)maxRows;
            }

            JToken room = request["room_id"];
            if (room != null && room.Type != JTokenType.Null)
            {
                if (!TryLong(room, "room_id", out long roomId, out error)) return false;
                r.RoomId = roomId;
            }

            r.Unit = ((string)request["unit"] ?? "mm").Trim().ToLowerInvariant();
            if (!GeometryMeasureRules.TryUnit(r.Unit, out r.LengthFromFeet)) { error = "unit must be mm, m or feet."; return false; }

            JToken region = request["region"];
            if (region != null && region.Type != JTokenType.Null)
            {
                if (r.RoomId.HasValue) { error = "send room_id or region, not both."; return false; }
                if (!TryParseRegion(region, r.LengthFromFeet, out r.Region, out error)) return false;
            }

            JToken tol = request["tolerance_mm"];
            if (tol != null && tol.Type != JTokenType.Null)
            {
                if (tol.Type != JTokenType.Integer && tol.Type != JTokenType.Float) { error = "tolerance_mm must be a number."; return false; }
                r.ToleranceMm = (double)tol;
                if (double.IsNaN(r.ToleranceMm) || r.ToleranceMm < 0 || r.ToleranceMm > MaxToleranceMm) { error = "tolerance_mm must be between 0 and " + MaxToleranceMm + "."; return false; }
            }

            JToken links = request["include_links"];
            if (links != null && links.Type != JTokenType.Null)
            {
                if (links.Type != JTokenType.Boolean) { error = "include_links must be true or false."; return false; }
                r.IncludeLinks = (bool)links;
            }
            parsed = r;
            return true;
        }

        /// <summary>region = { points: [[x, y], ...] (3 or more, in <c>unit</c>), z_min, z_max (optional) }. A polygon, never a box.</summary>
        public static bool TryParseRegion(JToken token, double lengthFromFeet, out RegionSpec region, out string error)
        {
            region = null; error = null;
            var obj = token as JObject;
            if (obj == null) { error = "region must be an object {points: [[x, y], ...], z_min, z_max}."; return false; }
            foreach (JProperty p in obj.Properties())
                if (p.Name != "points" && p.Name != "z_min" && p.Name != "z_max") { error = "region has no field '" + p.Name + "'; it takes points, z_min and z_max."; return false; }
            var pts = obj["points"] as JArray;
            if (pts == null || pts.Count < 3) { error = "region.points needs at least 3 [x, y] points."; return false; }
            if (pts.Count > MaxRegionPoints) { error = "region.points takes at most " + MaxRegionPoints + " points; got " + pts.Count + "."; return false; }
            var loop = new List<double[]>();
            foreach (JToken pt in pts)
            {
                var xy = pt as JArray;
                if (xy == null || xy.Count != 2 || !IsNumber(xy[0]) || !IsNumber(xy[1]))
                { error = "every region point must be [x, y] with two numbers: " + pt; return false; }
                double x = (double)xy[0] / lengthFromFeet, y = (double)xy[1] / lengthFromFeet;
                if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)) { error = "region points must be finite numbers."; return false; }
                loop.Add(new[] { x, y });
            }
            region = new RegionSpec { Source = "region" };
            region.Loops.Add(loop);
            foreach (string key in new[] { "z_min", "z_max" })
            {
                JToken z = obj[key];
                if (z == null || z.Type == JTokenType.Null) continue;
                if (!IsNumber(z)) { error = "region." + key + " must be a number."; return false; }
                double v = (double)z / lengthFromFeet;
                if (key == "z_min") region.ZMinFeet = v; else region.ZMaxFeet = v;
            }
            if (region.ZMinFeet.HasValue && region.ZMaxFeet.HasValue && region.ZMinFeet > region.ZMaxFeet) { error = "region.z_min is above region.z_max."; region = null; return false; }
            return true;
        }

        private static bool IsNumber(JToken t) => t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float);

        // ------------------------------------------------------------------ the category match

        /// <summary>Does the category match one of the requested names? Exact on the OST_ name or the display name, case-insensitive. No requested names = every category.</summary>
        public static bool CategoryMatches(IList<string> requested, string builtInName, string displayName)
        {
            if (requested == null || requested.Count == 0) return true;
            foreach (string r in requested)
            {
                if (!string.IsNullOrEmpty(builtInName) && string.Equals(r, builtInName, StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(displayName) && string.Equals(r, displayName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The requested names that matched no category the view holds or the model has: said, never silently an empty answer.</summary>
        public static List<string> UnmatchedRequests(IList<string> requested, IEnumerable<KeyValuePair<string, string>> knownBuiltInAndDisplay)
        {
            var known = knownBuiltInAndDisplay.ToList();
            return (requested ?? new List<string>())
                .Where(r => !known.Any(k => string.Equals(r, k.Key, StringComparison.OrdinalIgnoreCase) || string.Equals(r, k.Value, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // ------------------------------------------------------------------ groups and links

        /// <summary>The group members not yet seen (same host id), each once. The set is only read (Contains), never copied: one call per group stays linear.</summary>
        public static List<long> MembersNotYetSeen(ISet<long> alreadySeen, IEnumerable<long> members)
        {
            var fresh = new List<long>();
            var once = new HashSet<long>();
            foreach (long m in members ?? Enumerable.Empty<long>())
                if (!(alreadySeen != null && alreadySeen.Contains(m)) && once.Add(m)) fresh.Add(m);
            return fresh;
        }

        /// <summary>
        /// What can be said of one link instance in this view. supportsLinkCollector: FilteredElementCollector(doc, viewId, linkInstanceId)
        /// exists from Revit 2024; before it the answer is "unknown" with that reason, never a count of 0.
        /// </summary>
        public static LinkViewState LinkState(long linkInstanceId, string name, bool documentLoaded, bool hiddenInView, bool categoryHiddenInView, bool supportsLinkCollector, string visibility)
        {
            var s = new LinkViewState { LinkInstanceId = linkInstanceId, Name = name, Visibility = visibility };
            if (!documentLoaded) { s.Status = "unloaded"; s.Reason = "the link is unloaded or its document is unavailable: its elements cannot be read, and none is counted"; return s; }
            if (categoryHiddenInView) { s.Status = "hidden_in_view"; s.Reason = "the Revit Links category is hidden in this view"; return s; }
            if (hiddenInView) { s.Status = "hidden_in_view"; s.Reason = "this link instance is hidden in this view"; return s; }
            if (!supportsLinkCollector) { s.Status = "unknown"; s.Reason = "what a link draws in a view is read with FilteredElementCollector(doc, viewId, linkInstanceId), which needs Revit 2024 or later"; return s; }
            s.Status = "read";
            return s;
        }

        // ------------------------------------------------------------------ the region test (real outline, not a box)

        public static bool PointInLoops(IList<List<double[]>> loops, double x, double y)
        {
            bool inside = false;      // even-odd: a hole inside the outer loop flips it back
            foreach (List<double[]> loop in loops)
            {
                for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                {
                    double xi = loop[i][0], yi = loop[i][1], xj = loop[j][0], yj = loop[j][1];
                    if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>Shortest horizontal distance from the box (feet) to the outline region: 0 when they overlap.</summary>
        public static double BoxDistanceToLoops(IList<List<double[]>> loops, double minX, double minY, double maxX, double maxY)
        {
            double[][] corners = { new[] { minX, minY }, new[] { maxX, minY }, new[] { maxX, maxY }, new[] { minX, maxY } };
            // overlap: a corner or the centre inside, a vertex inside the box, or an edge crossing an edge
            foreach (double[] c in corners) if (PointInLoops(loops, c[0], c[1])) return 0;
            if (PointInLoops(loops, (minX + maxX) / 2, (minY + maxY) / 2)) return 0;
            double best = double.MaxValue;
            foreach (List<double[]> loop in loops)
            {
                for (int i = 0; i < loop.Count; i++)
                {
                    double[] a = loop[i], b = loop[(i + 1) % loop.Count];
                    if (a[0] >= minX && a[0] <= maxX && a[1] >= minY && a[1] <= maxY) return 0;
                    for (int k = 0; k < 4; k++)
                    {
                        double[] c0 = corners[k], c1 = corners[(k + 1) % 4];
                        if (SegmentsIntersect(a, b, c0, c1)) return 0;
                        best = Math.Min(best, PointToSegment(c0[0], c0[1], a, b));
                        best = Math.Min(best, PointToSegment(a[0], a[1], c0, c1));
                        best = Math.Min(best, PointToSegment(b[0], b[1], c0, c1));
                    }
                }
            }
            return best == double.MaxValue ? double.NaN : best;
        }

        /// <summary>
        /// Relation of an element's box (feet) to the region: Inside = the box overlaps the outline and the height range;
        /// Near = within toleranceFeet (distance reported); Outside; Unknown = no box. NaN or missing numbers are Unknown, never Outside.
        /// </summary>
        public static RegionRelation Classify(RegionSpec region, double[] boxMin, double[] boxMax, double toleranceFeet, out double? distanceFeet)
        {
            distanceFeet = null;
            if (region == null || region.Loops.Count == 0) return RegionRelation.NotTested;
            if (boxMin == null || boxMax == null || boxMin.Length < 3 || boxMax.Length < 3 || boxMin.Concat(boxMax).Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                return RegionRelation.Unknown;
            double horizontal = BoxDistanceToLoops(region.Loops, boxMin[0], boxMin[1], boxMax[0], boxMax[1]);
            if (double.IsNaN(horizontal)) return RegionRelation.Unknown;
            double vertical = 0;
            if (region.ZMinFeet.HasValue && boxMax[2] < region.ZMinFeet.Value) vertical = region.ZMinFeet.Value - boxMax[2];
            else if (region.ZMaxFeet.HasValue && boxMin[2] > region.ZMaxFeet.Value) vertical = boxMin[2] - region.ZMaxFeet.Value;
            double d = Math.Sqrt(horizontal * horizontal + vertical * vertical);
            if (d <= 1e-9) { distanceFeet = 0; return RegionRelation.Inside; }
            distanceFeet = d;
            return d <= toleranceFeet ? RegionRelation.Near : RegionRelation.Outside;
        }

        private static double PointToSegment(double px, double py, double[] a, double[] b)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1];
            double len2 = dx * dx + dy * dy;
            double t = len2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((px - a[0]) * dx + (py - a[1]) * dy) / len2));
            double qx = a[0] + t * dx, qy = a[1] + t * dy;
            return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
        }

        private static bool SegmentsIntersect(double[] p1, double[] p2, double[] p3, double[] p4)
        {
            double d1 = Cross(p3, p4, p1), d2 = Cross(p3, p4, p2), d3 = Cross(p1, p2, p3), d4 = Cross(p1, p2, p4);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            return (d1 == 0 && OnSegment(p3, p4, p1)) || (d2 == 0 && OnSegment(p3, p4, p2)) ||
                   (d3 == 0 && OnSegment(p1, p2, p3)) || (d4 == 0 && OnSegment(p1, p2, p4));
        }

        private static double Cross(double[] a, double[] b, double[] c) => (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);

        private static bool OnSegment(double[] a, double[] b, double[] p) =>
            Math.Min(a[0], b[0]) <= p[0] && p[0] <= Math.Max(a[0], b[0]) && Math.Min(a[1], b[1]) <= p[1] && p[1] <= Math.Max(a[1], b[1]);

        // ------------------------------------------------------------------ the aggregation per category

        public sealed class Aggregation
        {
            public JArray Categories = new JArray();
            /// <summary>Elements the view collector returned (host, link). Group-walk elements are NOT in it.</summary>
            public int ElementsTotal;
            /// <summary>Elements only the walk over group members found: not proven drawn, kept out of every total.</summary>
            public int GroupWalkTotal;
            public int ExcludedByRegion;
            public int RegionUnknown;
            /// <summary>The global row cap was reached: some categories returned fewer rows than their maximum.</summary>
            public bool RowsCapped;
        }

        /// <summary>
        /// Groups the items per category. With a region, Outside items are counted apart (ExcludedByRegion) and Unknown ones
        /// are counted in their category as region_unknown (with a few ids), never as inside and never as outside. Items that only the
        /// group walk found go to group_walk_total / group_walk_rows (drawn: "unverified") and out of total, host_total and ElementsTotal.
        /// The sample is capped by maxRows per category and maxTotalRows overall; the totals are exact. describe fills each retained row.
        /// </summary>
        public static Aggregation Aggregate(IEnumerable<ContentItem> items, int maxRows, bool regionTested, double feetToUnit, Func<ContentItem, JObject> describe,
                                            int maxTotalRows = MaxTotalRows)
        {
            var result = new Aggregation();
            var main = new SortedDictionary<string, List<ContentItem>>(StringComparer.Ordinal);
            var walk = new Dictionary<string, List<ContentItem>>(StringComparer.Ordinal);
            var display = new Dictionary<string, string>(StringComparer.Ordinal);
            var unknownIds = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            var unknownCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ContentItem it in items)
            {
                string key = it.CategoryKey ?? it.CategoryName ?? "(no category)";
                if (!display.ContainsKey(key)) display[key] = it.CategoryName ?? key;
                if (regionTested && it.Region == RegionRelation.Outside) { result.ExcludedByRegion++; continue; }
                if (!main.ContainsKey(key)) main[key] = new List<ContentItem>();
                if (regionTested && it.Region == RegionRelation.Unknown)
                {
                    result.RegionUnknown++;
                    unknownCount[key] = (unknownCount.TryGetValue(key, out int u) ? u : 0) + 1;
                    if (!unknownIds.TryGetValue(key, out List<long> ids)) unknownIds[key] = ids = new List<long>();
                    if (ids.Count < MaxUnknownSampleIds) ids.Add(it.Id);
                    continue;
                }
                if (it.ViaGroupWalk)
                {
                    if (!walk.TryGetValue(key, out List<ContentItem> w)) walk[key] = w = new List<ContentItem>();
                    w.Add(it);
                    result.GroupWalkTotal++;
                    continue;
                }
                main[key].Add(it);
            }

            int budget = Math.Max(0, maxTotalRows);
            foreach (KeyValuePair<string, List<ContentItem>> pair in main)
            {
                List<ContentItem> list = pair.Value;
                JArray rows = SampleRows(list, maxRows, regionTested, feetToUnit, describe, ref budget, false, ref result.RowsCapped);
                var cat = new JObject
                {
                    ["category"] = display[pair.Key],
                    ["total"] = list.Count,
                    ["host_total"] = list.Count(i => i.SourceKind == "host"),
                    ["linked_total"] = list.Count(i => i.SourceKind == "link"),
                    ["group_member_total"] = list.Count(i => i.GroupId.HasValue),
                    ["rows_total"] = list.Count,
                    ["rows_returned"] = rows.Count,
                    ["truncated"] = rows.Count < list.Count,
                    ["rows"] = rows
                };
                if (regionTested)
                {
                    cat["near_total"] = list.Count(i => i.Region == RegionRelation.Near);
                    if (unknownCount.TryGetValue(pair.Key, out int unk))
                    {
                        cat["region_unknown"] = unk;
                        cat["region_unknown_ids"] = new JArray(unknownIds[pair.Key]);
                    }
                }
                if (walk.TryGetValue(pair.Key, out List<ContentItem> walked))
                {
                    cat["group_walk_total"] = walked.Count;
                    cat["group_walk_rows"] = SampleRows(walked, maxRows, regionTested, feetToUnit, describe, ref budget, true, ref result.RowsCapped);
                }
                result.ElementsTotal += list.Count;
                result.Categories.Add(cat);
            }
            return result;
        }

        private static JArray SampleRows(List<ContentItem> list, int maxRows, bool regionTested, double feetToUnit, Func<ContentItem, JObject> describe,
                                         ref int budget, bool walked, ref bool capped)
        {
            var rows = new JArray();
            foreach (ContentItem it in list
                .OrderBy(i => RelationRank(i.Region))
                .ThenBy(i => i.SourceKind == "host" ? 0 : 1)
                .ThenBy(i => i.LinkInstanceId ?? 0)
                .ThenBy(i => i.Id).Take(Math.Max(0, maxRows)))
            {
                if (budget <= 0) { capped = true; break; }
                budget--;
                JObject row;
                try { row = describe(it) ?? new JObject(); }
                catch (Exception ex) { row = new JObject { ["element_id"] = it.Id, ["read_error"] = ex.Message }; }
                row["source_kind"] = it.SourceKind;
                if (it.LinkInstanceId.HasValue) row["link_instance_id"] = it.LinkInstanceId.Value;
                if (it.GroupId.HasValue) row["group_id"] = it.GroupId.Value;
                if (walked) { row["via"] = "group_walk"; row["drawn"] = "unverified"; }
                if (regionTested)
                {
                    row["region"] = it.Region == RegionRelation.Inside ? "inside" : "near";
                    if (it.Region == RegionRelation.Near && it.DistanceFeet.HasValue)
                        row["distance_to_region"] = Math.Round(it.DistanceFeet.Value * feetToUnit, 1);
                }
                rows.Add(row);
            }
            return rows;
        }

        private static int RelationRank(RegionRelation r) => r == RegionRelation.Inside || r == RegionRelation.NotTested ? 0 : 1;
    }
}
