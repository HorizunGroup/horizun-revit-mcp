// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// The Revit half of the spatial coherence check (rules in SpatialCoherenceRules.cs).
// Given the elements a command added or changed, find every model element whose SOLID
// intersects theirs, measure how much they share, work out how the two relate (host,
// joined, connected, same assembly) and let the rules decide.
//
// It never writes and never opens a transaction. It is bounded: a subject cap and a
// time budget, and when either stops it the answer says "partial" - an unfinished
// check must not read as a clean one.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;

namespace Horizun.Revit.Core
{
    internal static class SpatialCoherence
    {
        public const int DefaultMaxSubjects = 1500;
        public const int DefaultBudgetMs = 20000;
        private const double M3PerFt3 = 0.028316846592;

        public sealed class Finding
        {
            public SpatialCoherenceRules.Verdict Verdict;
            public Element A, B;
            public double? SharedVolumeFt3;
            /// <summary>B lives in this loaded link (its name), or null for the host.</summary>
            public string LinkB;
        }

        public sealed class Outcome
        {
            public readonly List<Finding> Findings = new List<Finding>();
            public int Subjects, Checked, WithoutSolid, Candidates, Expected, LinksExamined, LinkCandidates;
            public readonly List<string> LinksSkipped = new List<string>();
            public bool Partial;
            public string PartialWhy;
            public long Ms;
            public int Errors => Findings.Count(f => f.Verdict.Severity == "error");
            public int Warnings => Findings.Count(f => f.Verdict.Severity == "warning");
        }

        /// <summary>Model elements among <paramref name="ids"/> that a spatial check can say something about.</summary>
        public static List<Element> Subjects(Document doc, IEnumerable<ElementId> ids)
        {
            var list = new List<Element>();
            var seen = new HashSet<long>();
            foreach (ElementId id in ids)
            {
                if (id == null || id == ElementId.InvalidElementId || !seen.Add(Rid.Value(id))) continue;
                Element e = null;
                try { e = doc.GetElement(id); } catch { }
                if (IsPhysical(e)) list.Add(e);
            }
            return list;
        }

        public static bool IsPhysical(Element e)
        {
            try
            {
                if (e == null || !e.IsValidObject || e is ElementType || e.ViewSpecific) return false;
                Category c = e.Category;
                if (c == null || c.CategoryType != CategoryType.Model) return false;
                if (!SpatialCoherenceRules.Considered(CategoryKey(e))) return false;
                return e.get_BoundingBox(null) != null;
            }
            catch { return false; }
        }

        public static Outcome Check(Document doc, IList<Element> subjects, int maxSubjects = DefaultMaxSubjects, int budgetMs = DefaultBudgetMs, bool includeLinks = true)
        {
            var o = new Outcome { Subjects = subjects.Count };
            var clock = Stopwatch.StartNew();
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            var solidCache = new Dictionary<long, List<Solid>>();
            foreach (Element a in subjects)
            {
                if (o.Checked >= maxSubjects) { o.Partial = true; o.PartialWhy = "only the first " + maxSubjects + " of " + subjects.Count + " elements were checked"; break; }
                if (clock.ElapsedMilliseconds > budgetMs) { o.Partial = true; o.PartialWhy = "the " + budgetMs / 1000 + " s budget ran out after " + o.Checked + " of " + subjects.Count + " elements"; break; }
                o.Checked++;
                List<Solid> sa = Solids(a, solidCache);
                if (sa.Count == 0) { o.WithoutSolid++; continue; }
                foreach (Element b in Candidates(doc, a))
                {
                    long ia = Rid.Value(a.Id), ib = Rid.Value(b.Id);
                    if (ia == ib || !pairs.Add(Math.Min(ia, ib) + "|" + Math.Max(ia, ib))) continue;
                    if (!IsPhysical(b)) continue;
                    o.Candidates++;
                    List<Solid> sb = Solids(b, solidCache);
                    double? shared = Shared(sa, sb);
                    var pair = new SpatialCoherenceRules.Pair
                    {
                        CategoryA = CategoryKey(a), CategoryB = CategoryKey(b),
                        SameType = SafeType(a) != null && SafeType(a) == SafeType(b),
                        HostRelation = Hosts(a, b) || Hosts(b, a),
                        Joined = Joined(doc, a, b),
                        Connected = Connected(a, b),
                        SameAssembly = SameAssembly(a, b),
                        SharedVolume = shared,
                        VolumeA = Volume(sa), VolumeB = Volume(sb)
                    };
                    SpatialCoherenceRules.Verdict v = SpatialCoherenceRules.Classify(pair);
                    if (v.Kind == SpatialCoherenceRules.Kind.Expected) { o.Expected++; continue; }
                    if (v.Kind == SpatialCoherenceRules.Kind.None) continue;
                    o.Findings.Add(new Finding { Verdict = v, A = a, B = b, SharedVolumeFt3 = shared });
                }
            }
            if (!o.Partial) DoorClearance(doc, subjects, o, pairs, solidCache, clock, budgetMs);
            if (!o.Partial && includeLinks) AgainstLinks(doc, subjects, o, solidCache, clock, budgetMs);
            o.Ms = clock.ElapsedMilliseconds;
            return o;
        }

        // ---- loaded links ---------------------------------------------------------------
        // A column in the linked STRUCTURE model standing in a door of THIS architecture
        // model is the same defect as two host elements - it is just split across files,
        // which is how real projects are built. Each changed solid is carried into the
        // link's own coordinates (inverse of the instance's total transform), the link is
        // queried there, and the shared volume is measured in the link. Relations the host
        // could read (host, join, connector) do not cross files, so a link pair is judged
        // by category only - which is what an expert looking at the federated view does.
        private static void AgainstLinks(Document doc, IList<Element> subjects, Outcome o,
                                         Dictionary<long, List<Solid>> cache, Stopwatch clock, int budgetMs)
        {
            List<RevitLinkInstance> links;
            try { links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList(); }
            catch { return; }
            foreach (RevitLinkInstance link in links)
            {
                Document linked = null;
                try { linked = link.GetLinkDocument(); } catch { }
                string linkName = SafeName(link);
                if (linked == null) { o.LinksSkipped.Add(linkName + " (not loaded)"); continue; }
                Transform toLink;
                try { toLink = link.GetTotalTransform().Inverse; } catch { o.LinksSkipped.Add(linkName + " (no transform)"); continue; }
                o.LinksExamined++;
                var linkCache = new Dictionary<long, List<Solid>>();
                foreach (Element a in subjects)
                {
                    if (clock.ElapsedMilliseconds > budgetMs) { o.Partial = true; o.PartialWhy = "the time budget ran out while examining link '" + linkName + "'"; return; }
                    List<Solid> sa = Solids(a, cache);
                    if (sa.Count == 0) continue;
                    var moved = new List<Solid>();
                    foreach (Solid x in sa)
                        try { moved.Add(SolidUtils.CreateTransformed(x, toLink)); } catch { }
                    if (moved.Count == 0) continue;
                    var hits = new Dictionary<long, Element>();
                    foreach (Solid m in moved)
                    {
                        try
                        {
                            BoundingBoxXYZ bb = m.GetBoundingBox();
                            Transform t = bb.Transform;
                            XYZ p0 = t.OfPoint(bb.Min), p1 = t.OfPoint(bb.Max);
                            var outline = new Outline(new XYZ(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Min(p0.Z, p1.Z)),
                                                      new XYZ(Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), Math.Max(p0.Z, p1.Z)));
                            foreach (Element b in new FilteredElementCollector(linked).WhereElementIsNotElementType()
                                         .WherePasses(new BoundingBoxIntersectsFilter(outline))
                                         .WherePasses(new ElementIntersectsSolidFilter(m)))
                                hits[Rid.Value(b.Id)] = b;
                        }
                        catch { }
                    }
                    foreach (Element b in hits.Values)
                    {
                        if (!IsPhysical(b)) continue;
                        o.LinkCandidates++;
                        List<Solid> sb = Solids(b, linkCache);
                        double? shared = Shared(moved, sb);
                        var pair = new SpatialCoherenceRules.Pair
                        {
                            CategoryA = CategoryKey(a), CategoryB = CategoryKey(b),
                            SameType = false, SharedVolume = shared,
                            VolumeA = Volume(sa), VolumeB = Volume(sb)
                        };
                        SpatialCoherenceRules.Verdict v = SpatialCoherenceRules.Classify(pair);
                        if (v.Kind == SpatialCoherenceRules.Kind.Expected) { o.Expected++; continue; }
                        if (v.Kind == SpatialCoherenceRules.Kind.None) continue;
                        v.Reason += " (in link '" + linkName + "')";
                        o.Findings.Add(new Finding { Verdict = v, A = a, B = b, SharedVolumeFt3 = shared, LinkB = linkName });
                    }
                }
            }
        }

        private static string SafeName(Element e)
        {
            try { return e.Name; } catch { return "link " + Rid.Value(e.Id); }
        }

        private static JObject DescribeIn(Element e, string link)
        {
            JObject o = Describe(e);
            if (link != null) { o["link"] = link; o["source"] = "link"; }
            return o;
        }

        // ---- door clear zones -----------------------------------------------------------
        // A column one hand's width in front of a door shares no solid with it, so the pair
        // check above cannot see it - and it is exactly what an expert spots first. Each
        // door gets a clear zone on both faces of its host (door width deep, at least
        // 0.6 m; 2 m high; slightly narrower than the leaf so a wall at the jamb corner is
        // not an obstacle) and whatever physical element fills part of it is judged by
        // SpatialCoherenceRules.Clearance. Reported only when the door or the obstacle is
        // among the elements being checked.
        private static void DoorClearance(Document doc, IList<Element> subjects, Outcome o, HashSet<string> pairs,
                                          Dictionary<long, List<Solid>> cache, Stopwatch clock, int budgetMs)
        {
            var subjectIds = new HashSet<long>(subjects.Select(e => Rid.Value(e.Id)));
            var doors = new Dictionary<long, FamilyInstance>();
            foreach (Element e in subjects)
                if (e is FamilyInstance fi && CategoryKey(e) == "OST_Doors") doors[Rid.Value(e.Id)] = fi;
            foreach (Element e in subjects)
            {
                if (CategoryKey(e) == "OST_Doors") continue;
                BoundingBoxXYZ b = null;
                try { b = e.get_BoundingBox(null); } catch { }
                if (b == null) continue;
                const double reach = 2.0 / 0.3048;
                var around = new Outline(b.Min - new XYZ(reach, reach, 0.5), b.Max + new XYZ(reach, reach, 0.5));
                try
                {
                    foreach (Element d in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors)
                                 .WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(around)))
                        if (d is FamilyInstance fd) doors[Rid.Value(d.Id)] = fd;
                }
                catch { }
            }
            foreach (FamilyInstance door in doors.Values)
            {
                if (clock.ElapsedMilliseconds > budgetMs) { o.Partial = true; o.PartialWhy = "the time budget ran out during the door clear-zone pass"; return; }
                List<Solid> zones = ClearZones(door);
                long doorId = Rid.Value(door.Id);
                long hostId = door.Host == null ? -1 : Rid.Value(door.Host.Id);
                foreach (Solid zone in zones)
                {
                    IList<Element> hits;
                    try
                    {
                        hits = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                            .WherePasses(new ElementIntersectsSolidFilter(zone)).ToElements();
                    }
                    catch { continue; }
                    foreach (Element b in hits)
                    {
                        long ib = Rid.Value(b.Id);
                        if (ib == doorId || !IsPhysical(b)) continue;
                        if (!subjectIds.Contains(doorId) && !subjectIds.Contains(ib)) continue;
                        if (b is FamilyInstance bf && bf.SuperComponent != null && Rid.Value(bf.SuperComponent.Id) == doorId) continue;
                        SpatialCoherenceRules.Verdict v = SpatialCoherenceRules.Clearance(CategoryKey(b), ib == hostId || Hosts(b, door));
                        if (v.Kind == SpatialCoherenceRules.Kind.None) continue;
                        double? shared = Shared(new List<Solid> { zone }, Solids(b, cache));
                        if (shared.HasValue && shared.Value < SpatialCoherenceRules.TouchVolumeFt3) continue;
                        if (!pairs.Add("clear:" + doorId + "|" + ib)) continue;
                        o.Findings.Add(new Finding { Verdict = v, A = door, B = b, SharedVolumeFt3 = shared });
                    }
                }
            }
        }

        private static List<Solid> ClearZones(FamilyInstance door)
        {
            var zones = new List<Solid>();
            try
            {
                if (!(door.Location is LocationPoint lp)) return zones;
                XYZ p = lp.Point;
                XYZ hand = door.HandOrientation, facing = door.FacingOrientation;
                if (hand == null || facing == null || hand.IsZeroLength() || facing.IsZeroLength()) return zones;
                hand = new XYZ(hand.X, hand.Y, 0).Normalize(); facing = new XYZ(facing.X, facing.Y, 0).Normalize();
                double width = DoorWidth(door);
                if (width <= 0) return zones;
                double half = width * 0.45;   // narrower than the leaf: a wall at the jamb is not an obstacle
                double depth = Math.Max(SpatialCoherenceRules.ClearanceMinFt, width);
                double wall = door.Host is Wall w ? w.Width : 0;
                double z0 = p.Z + 0.05;
                foreach (int side in new[] { 1, -1 })
                {
                    XYZ f = facing * side;
                    XYZ start = new XYZ(p.X, p.Y, z0) + f * (wall / 2 + 0.05);
                    XYZ a = start - hand * half, b = start + hand * half, c = b + f * depth, d = a + f * depth;
                    var loop = CurveLoop.Create(new List<Curve> { Line.CreateBound(a, b), Line.CreateBound(b, c), Line.CreateBound(c, d), Line.CreateBound(d, a) });
                    zones.Add(GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, SpatialCoherenceRules.ClearanceHeightFt - 0.05));
                }
            }
            catch { }
            return zones;
        }

        private static double DoorWidth(FamilyInstance door)
        {
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.FURNITURE_WIDTH })
            {
                try
                {
                    Parameter q = door.get_Parameter(bip) ?? door.Symbol?.get_Parameter(bip);
                    if (q != null && q.StorageType == StorageType.Double && q.AsDouble() > 0.3) return q.AsDouble();
                }
                catch { }
            }
            try
            {
                BoundingBoxXYZ b = door.get_BoundingBox(null);
                XYZ h = door.HandOrientation;
                if (b != null && h != null) return Math.Abs((b.Max - b.Min).DotProduct(new XYZ(Math.Abs(h.X), Math.Abs(h.Y), 0)));
            }
            catch { }
            return 0;
        }

        public static JObject ToJson(Outcome o, int maxFindings = 50)
        {
            string status = o.Errors > 0 ? "conflicts" : o.Warnings > 0 ? "warnings" : o.Partial ? "partial" : o.Subjects == 0 ? "nothing_to_check"
                          : o.Checked > 0 && o.WithoutSolid == o.Checked ? "not_measured" : "clean";
            var list = new JArray();
            foreach (Finding f in o.Findings.OrderBy(f => f.Verdict.Severity == "error" ? 0 : 1).Take(maxFindings))
                list.Add(new JObject
                {
                    ["kind"] = f.Verdict.Kind.ToString().ToLowerInvariant(),
                    ["severity"] = f.Verdict.Severity,
                    ["reason"] = f.Verdict.Reason,
                    ["suggestion"] = f.Verdict.Suggestion,
                    ["a"] = Describe(f.A), ["b"] = DescribeIn(f.B, f.LinkB),
                    ["shared_volume_m3"] = f.SharedVolumeFt3.HasValue ? (JToken)Math.Round(f.SharedVolumeFt3.Value * M3PerFt3, 6) : JValue.CreateNull()
                });
            return new JObject
            {
                ["status"] = status,
                ["errors"] = o.Errors, ["warnings"] = o.Warnings,
                ["subjects"] = o.Subjects, ["checked"] = o.Checked, ["without_solid"] = o.WithoutSolid,
                ["neighbours_examined"] = o.Candidates, ["expected_intersections"] = o.Expected,
                ["partial"] = o.Partial, ["partial_why"] = o.PartialWhy,
                ["findings"] = list, ["findings_truncated"] = o.Findings.Count > maxFindings,
                ["ms"] = o.Ms,
                ["links_examined"] = o.LinksExamined, ["link_neighbours_examined"] = o.LinkCandidates,
                ["links_skipped"] = new JArray(o.LinksSkipped),
                ["method"] = "solid intersection (ElementIntersectsElementFilter / ElementIntersectsSolidFilter + BooleanOperationsUtils) of each changed model element against every model element in the host document AND in every loaded Revit link (the changed solid is carried into the link's coordinates); hosts, joins, MEP connections and same-assembly members are expected, not findings. An unloaded link is listed in links_skipped, never counted as clear."
            };
        }

        /// <summary>One line a person reads first: what is wrong, or that nothing is.</summary>
        public static string Headline(Outcome o)
        {
            if (o.Subjects == 0) return null;
            if (o.Errors == 0 && o.Warnings == 0)
                return o.Partial ? "Spatial check PARTIAL (" + o.PartialWhy + "); no conflict among what was checked." : null;
            var first = o.Findings.OrderBy(f => f.Verdict.Severity == "error" ? 0 : 1).First();
            return "Spatial check: " + o.Errors + " error(s), " + o.Warnings + " warning(s) among the elements this call changed - e.g. " +
                   first.Verdict.Reason + " (" + Rid.Value(first.A.Id) + " / " + Rid.Value(first.B.Id) + "). The write is committed; review spatial_check.findings and fix or undo.";
        }

        // ---- facts --------------------------------------------------------------------

        public static string CategoryKey(Element e)
        {
            try
            {
                Category c = e?.Category;
                if (c == null) return null;
                long v = Rid.Value(c.Id);
                if (v >= 0) return null;   // user subcategory ids are positive; model categories are built-in
                return ((BuiltInCategory)v).ToString();
            }
            catch { return null; }
        }

        private static IEnumerable<Element> Candidates(Document doc, Element a)
        {
            BoundingBoxXYZ box = a.get_BoundingBox(null);
            if (box == null) return Enumerable.Empty<Element>();
            const double pad = 0.003;   // ~1 mm: touching faces are found, then judged by shared volume
            var outline = new Outline(box.Min - new XYZ(pad, pad, pad), box.Max + new XYZ(pad, pad, pad));
            try
            {
                return new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .WherePasses(new ElementIntersectsElementFilter(a))
                    .ToElements();
            }
            catch
            {
                // Some elements cannot drive the intersects filter; fall back to boxes and let
                // the boolean below decide.
                try
                {
                    return new FilteredElementCollector(doc).WhereElementIsNotElementType()
                        .WherePasses(new BoundingBoxIntersectsFilter(outline)).ToElements();
                }
                catch { return Enumerable.Empty<Element>(); }
            }
        }

        private static List<Solid> Solids(Element e, Dictionary<long, List<Solid>> cache)
        {
            long id = Rid.Value(e.Id);
            if (cache.TryGetValue(id, out List<Solid> hit)) return hit;
            var list = new List<Solid>();
            try
            {
                GeometryElement g = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false });
                Collect(g, list, 0);
            }
            catch { }
            cache[id] = list;
            return list;
        }

        private static void Collect(GeometryElement g, List<Solid> list, int depth)
        {
            if (g == null || depth > 4) return;
            foreach (GeometryObject o in g)
            {
                if (o is Solid s && s.Volume > 1e-9) list.Add(s);
                else if (o is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), list, depth + 1);
            }
        }

        private static double? Shared(List<Solid> a, List<Solid> b)
        {
            if (a.Count == 0 || b.Count == 0) return null;
            double total = 0; bool any = false, failed = false;
            foreach (Solid x in a)
                foreach (Solid y in b)
                {
                    try
                    {
                        Solid i = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect);
                        if (i != null) total += Math.Abs(i.Volume);
                        any = true;
                    }
                    catch { failed = true; }
                }
            // A pair Revit could not intersect may be exactly the overlapping one: below the
            // touch threshold with any failure, the size is UNKNOWN, never "no overlap".
            if (!any || (failed && total < SpatialCoherenceRules.TouchVolumeFt3)) return null;
            return total;
        }

        private static double? Volume(List<Solid> s)
        {
            if (s.Count == 0) return null;
            double v = 0; foreach (Solid x in s) v += Math.Abs(x.Volume);
            return v;
        }

        private static long? SafeType(Element e)
        {
            try { ElementId t = e.GetTypeId(); return t == null || t == ElementId.InvalidElementId ? (long?)null : Rid.Value(t); }
            catch { return null; }
        }

        private static bool Hosts(Element host, Element guest)
        {
            try
            {
                long h = Rid.Value(host.Id);
                if (guest is FamilyInstance fi && fi.Host != null && Rid.Value(fi.Host.Id) == h) return true;
                if (guest is Opening op && op.Host != null && Rid.Value(op.Host.Id) == h) return true;
                if (guest is Rebar r && Rid.Value(r.GetHostId()) == h) return true;
                if (guest is Wall w && w.StackedWallOwnerId != ElementId.InvalidElementId && Rid.Value(w.StackedWallOwnerId) == h) return true;
                // A wall used as a curtain panel lists the curtain wall as its host through the grid.
                if (host is Wall cw && cw.CurtainGrid != null && cw.CurtainGrid.GetPanelIds().Any(id => Rid.Value(id) == Rid.Value(guest.Id))) return true;
                if (host is Wall cw2 && cw2.CurtainGrid != null && cw2.CurtainGrid.GetMullionIds().Any(id => Rid.Value(id) == Rid.Value(guest.Id))) return true;
            }
            catch { }
            return false;
        }

        private static bool Joined(Document doc, Element a, Element b)
        {
            try { if (JoinGeometryUtils.AreElementsJoined(doc, a, b)) return true; } catch { }
            return WallsMeet(a, b) || WallsMeet(b, a);
        }

        private static bool WallsMeet(Element a, Element b)
        {
            try
            {
                if (!(a is Wall) || !(a.Location is LocationCurve lc)) return false;
                long ib = Rid.Value(b.Id);
                for (int end = 0; end < 2; end++)
                    foreach (Element j in lc.get_ElementsAtJoin(end))
                        if (Rid.Value(j.Id) == ib) return true;
            }
            catch { }
            return false;
        }

        private static bool Connected(Element a, Element b)
        {
            try
            {
                ConnectorSet cs = Connectors(a);
                if (cs == null) return false;
                long ib = Rid.Value(b.Id);
                foreach (Connector c in cs)
                {
                    if (!c.IsConnected) continue;
                    foreach (Connector r in c.AllRefs)
                        if (r?.Owner != null && Rid.Value(r.Owner.Id) == ib) return true;
                }
            }
            catch { }
            return false;
        }

        private static ConnectorSet Connectors(Element e)
        {
            if (e is MEPCurve m) return m.ConnectorManager?.Connectors;
            if (e is FamilyInstance fi) return fi.MEPModel?.ConnectorManager?.Connectors;
            return null;
        }

        private static bool SameAssembly(Element a, Element b)
        {
            try
            {
                if (a is FamilyInstance fa)
                {
                    if (fa.SuperComponent != null && (Rid.Value(fa.SuperComponent.Id) == Rid.Value(b.Id) ||
                        (b is FamilyInstance fb0 && fb0.SuperComponent != null && Rid.Value(fb0.SuperComponent.Id) == Rid.Value(fa.SuperComponent.Id))))
                        return true;
                }
                if (b is FamilyInstance fb && fb.SuperComponent != null && Rid.Value(fb.SuperComponent.Id) == Rid.Value(a.Id)) return true;
                // Curtain panels and mullions of the same curtain wall/system.
                if (a is FamilyInstance pa && b is FamilyInstance pb && pa.Host != null && pb.Host != null &&
                    Rid.Value(pa.Host.Id) == Rid.Value(pb.Host.Id) &&
                    (IsCurtainMember(a) || IsCurtainMember(b))) return true;
                if (a is Mullion && b is Mullion) return true;
                if (a.AssemblyInstanceId != ElementId.InvalidElementId && a.AssemblyInstanceId == b.AssemblyInstanceId) return false;
            }
            catch { }
            return false;
        }

        private static bool IsCurtainMember(Element e)
        {
            string k = CategoryKey(e);
            return k == "OST_CurtainWallPanels" || k == "OST_CurtainWallMullions";
        }

        public static JObject Describe(Element e)
        {
            var o = new JObject { ["id"] = Rid.Value(e.Id), ["category"] = SafeCategory(e) };
            try { o["name"] = e.Name; } catch { }
            try
            {
                ElementId t = e.GetTypeId();
                if (t != null && t != ElementId.InvalidElementId && e.Document.GetElement(t) is ElementType et)
                    o["type"] = (et.FamilyName ?? "") + ": " + et.Name;
            }
            catch { }
            try
            {
                ElementId lv = e.LevelId;
                if (lv != null && lv != ElementId.InvalidElementId) o["level"] = e.Document.GetElement(lv)?.Name;
            }
            catch { }
            return o;
        }

        private static string SafeCategory(Element e)
        {
            try { return e.Category?.Name; } catch { return null; }
        }
    }
}
