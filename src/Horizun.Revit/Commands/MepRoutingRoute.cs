// -----------------------------------------------------------------------------
// Horizun Revit MCP - MEP routing: obstacle-avoiding path search and creation.
// Original Horizun code. Partial class: plugs the 'route' operation into the
// shared dry_run -> confirmation_token -> apply pipeline defined in
// MepRoutingCommand.cs (WritePlan, Rehearse, the transaction/rollback wrapper).
//
// WHAT 'route' PROVES, re-read from the model after the commit:
//   - the path RouteSearch (Core, Revit-free, unit-tested in RouteSearchTests.cs)
//     returned, within 1 mm: a FREE end (the route's own start/end) sits on its
//     planned point; an end at a BEND is trimmed back by the elbow (NewElbowFitting
//     does that - measured in CadConnectCommand/CadUpdateRules), so there the
//     NOMINAL junction (the intersection of the elbow's two connector axes) sits on
//     the planned vertex, the physical end stayed on the planned leg's axis, pulled
//     back toward the other end, and its connector is connected to THAT elbow.
//   - every elbow placed between two consecutive segments has both its
//     connectors CONNECTED (NewElbowFitting is asked, not trusted).
//   - a spatial check (SpatialCoherence, the same engine horizun_verify_changes
//     uses) against every element the route created finds NO error against a
//     physical host or a loaded link, and is COMPLETE: a partial check (budget or
//     subject cap hit, links skipped) is 'could not look', not 'clean', and
//     refuses the same way. Any error throws inside Apply(), which
//     the shared wrapper in MepRoutingCommand.cs rolls back and reports by
//     name - this file adds no rollback code of its own.
//
// OBSTACLES. Physical host elements (SpatialCoherence.IsPhysical - excludes
// element types, view-specific and non-Model categories) and elements of every
// LOADED link, both read from a box around start/end inflated by a margin wide
// enough for RouteSearch's own default search box (MarginSteps grid steps),
// so nothing the search could reach through is missed. Each obstacle box is
// the element's own bounding box (world space for links, via GetTotalTransform,
// all EIGHT corners - two opposite corners collapse the box of a rotated link)
// inflated by clearance_mm + half the run's OUTSIDE size - the search must keep
// clearance_mm of AIR around the new pipe's wall, not around its nominal circle.
//
// THE TOKEN BINDS THE ROUTE. Apply re-plans (Build searches again against the model
// as it is then); the resolved plan carries the rounded polyline, kind, type,
// system, level and size as its ContextFingerprint, so a route that changed since
// the dry run is refused as stale instead of committing a path nobody previewed.
// An unloaded link is not observable (measured elsewhere in this codebase,
// see workset-cerrado-en-vinculo-no-observable) and is skipped, not refused.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;
using System.Globalization;

namespace Horizun.Revit.Commands
{
    public sealed partial class MepRoutingCommand
    {
        private const double MmToFeet = 1.0 / 304.8;
        private const double MinGridMm = 10.0;      // a finer lattice runs A* on Revit's UI thread for too long
        private const int MaxNodesCap = 200000;     // twice per call (dry run, then apply) on the UI thread

        private sealed class RoutePlan : WritePlan
        {
            private string _kind;
            private ElementId _typeId, _systemTypeId, _levelId;
            private double? _diameter, _width, _height;
            private RouteSearch.Result _result;
            private readonly List<SegRecord> _segments = new List<SegRecord>();
            private readonly List<ElementId> _elbowIds = new List<ElementId>();
            private readonly List<ElementId> _createdIds = new List<ElementId>();
            private SpatialCoherence.Outcome _spatial; // the gate's own outcome, from the Apply that committed
            private double _outerSize; private string _sizeBasis;

            private sealed class SegRecord { public ElementId Id; public XYZ Start; public XYZ End; public BuiltInParameter[] SizeParams; public double[] SizeWanted; public ElementId StartElbow, EndElbow; }

            private int Legs => _result.Polyline.Count - 1;

            // PLANNED, from the polyline - not what the last Apply created: the rehearsal resets
            // that, and the dry run must declare the N segments + N-1 elbows it will create
            // (reviewed defect: it declared 0 requested changes and an empty change_preview).
            public override int Count => Legs + Math.Max(0, Legs - 1);

            public static RoutePlan Build(Document doc, JObject request, Units u, out string error)
            {
                error = null;
                string kind = (request.Value<string>("kind") ?? "").Trim().ToLowerInvariant();
                if (kind != "pipe" && kind != "duct" && kind != "conduit" && kind != "cable_tray")
                { error = "kind must be pipe, duct, conduit or cable_tray."; return null; }

                long typeIdRaw = request.Value<long?>("type_id") ?? -1;
                if (typeIdRaw < 0 || !Rid.CanRepresent(typeIdRaw)) { error = "type_id is required for route."; return null; }
                ElementId typeId = Rid.Make(typeIdRaw);
                Element typeEl = doc.GetElement(typeId);
                bool typeMatches = kind == "pipe" ? typeEl is PipeType : kind == "duct" ? typeEl is DuctType
                    : kind == "conduit" ? typeEl is ConduitType : typeEl is CableTrayType;
                if (!typeMatches)
                { error = "type_id " + typeIdRaw + " is " + (typeEl == null ? "not an element" : "a " + typeEl.GetType().Name) + ", not a " + kind + " type."; return null; }

                ElementId systemTypeId = null;
                if (kind == "pipe" || kind == "duct")
                {
                    long sysRaw = request.Value<long?>("system_type_id") ?? -1;
                    if (sysRaw < 0 || !Rid.CanRepresent(sysRaw)) { error = "system_type_id is required for kind pipe or duct."; return null; }
                    systemTypeId = Rid.Make(sysRaw);
                    if (doc.GetElement(systemTypeId) == null) { error = "system_type_id " + sysRaw + " does not resolve."; return null; }
                }

                long levelRaw = request.Value<long?>("level_id") ?? -1;
                if (levelRaw < 0 || !Rid.CanRepresent(levelRaw)) { error = "level_id is required for route."; return null; }
                ElementId levelId = Rid.Make(levelRaw);
                if (!(doc.GetElement(levelId) is Level)) { error = "level_id " + levelRaw + " is not a Level."; return null; }

                // Sizes and points are in the request's units (default mm), like resize's sizes;
                // clearance_mm, grid_mm and preferred_elevation are always mm, as named.
                double? diaIn = request.Value<double?>("diameter");
                double? wIn = request.Value<double?>("width"), hIn = request.Value<double?>("height");
                // Which size a run takes is fixed by its kind (and a duct type's shape), so a
                // mismatch is refused here, before any write, instead of failing mid-Apply.
                bool wantsDiameter = kind == "pipe" || kind == "conduit" ||
                    (kind == "duct" && ((DuctType)typeEl).Shape == ConnectorProfileType.Round);
                if (wantsDiameter && (diaIn == null || wIn != null || hIn != null))
                { error = "a " + kind + (kind == "duct" ? " of a round type" : "") + " takes diameter only."; return null; }
                if (!wantsDiameter && (diaIn != null || wIn == null || hIn == null))
                { error = "a " + (kind == "duct" ? "rectangular or oval duct" : kind) + " takes width and height only."; return null; }
                double? diameterFt = diaIn.HasValue ? diaIn.Value * u.ToFeet : (double?)null;
                double? widthFt = wIn.HasValue ? wIn.Value * u.ToFeet : (double?)null;
                double? heightFt = hIn.HasValue ? hIn.Value * u.ToFeet : (double?)null;
                if ((diameterFt.HasValue && diameterFt.Value <= 0) || (widthFt.HasValue && widthFt.Value <= 0) || (heightFt.HasValue && heightFt.Value <= 0))
                { error = "diameter, width and height must be positive."; return null; }

                XYZ start = ParsePoint(request["start"], "start", u, ref error);
                if (error != null) return null;
                XYZ end = ParsePoint(request["end"], "end", u, ref error);
                if (error != null) return null;

                double clearanceMm = request.Value<double?>("clearance_mm") ?? 50.0;
                double gridMm = request.Value<double?>("grid_mm") ?? 100.0;
                if (!(clearanceMm >= 0) || double.IsInfinity(clearanceMm) || !(gridMm >= MinGridMm) || double.IsInfinity(gridMm))
                { error = "clearance_mm must be >= 0 and grid_mm must be >= " + MinGridMm + " (a finer lattice runs the search on Revit's UI thread for too long)."; return null; }
                double clearanceFt = clearanceMm * MmToFeet, gridFt = gridMm * MmToFeet;
                int maxNodes = request.Value<int?>("max_nodes") ?? RouteSearch.DefaultMaxNodes;
                if (maxNodes <= 0 || maxNodes > MaxNodesCap) { error = "max_nodes must be between 1 and " + MaxNodesCap + " (the search runs on Revit's UI thread, twice: dry run and apply)."; return null; }

                // The run's OUTSIDE size: clearance is air around the wall, not around the nominal
                // circle (DN100 steel is 114.3 mm outside; reviewed defect: nominal lost 7-9 mm of it).
                string sizeBasis = "width/height as given";
                double runSize = diameterFt.HasValue ? OuterDiameterOf(doc, typeEl, diameterFt.Value, out sizeBasis) : Math.Max(widthFt.Value, heightFt.Value);
                double inflate = clearanceFt + runSize / 2.0;

                var reqStart = new RouteSearch.Point3(start.X, start.Y, start.Z);
                var reqEnd = new RouteSearch.Point3(end.X, end.Y, end.Z);
                int marginSteps = 6; // RouteSearch grows its box to x3 and x9 of this before refusing
                double margin = (marginSteps * 9 + 1) * gridFt + inflate; // covers the largest box the search can grow to
                List<RouteSearch.Box3> obstacles = CollectObstacles(doc, start, end, margin, inflate);

                var searchReq = new RouteSearch.Request
                {
                    Start = reqStart, End = reqEnd, Obstacles = obstacles, GridSize = gridFt, MaxNodes = maxNodes, MarginSteps = marginSteps,
                    // A FLOOR, not the fitting's real take-off (that depends on the elbow family and
                    // routing preferences, unknown before one is placed): one outside size per elbow.
                    // Revit's own answer is measured by Verify (the trim must stay inside the leg).
                    MinEndLeg = runSize, MinInteriorLeg = 2 * runSize
                };
                JToken pref = request["preferred_elevation"];
                double? pMinMm = pref?["min_mm"]?.Value<double>(), pMaxMm = pref?["max_mm"]?.Value<double>();
                if (pMinMm.HasValue) searchReq.PreferredMinZ = pMinMm.Value * MmToFeet;
                if (pMaxMm.HasValue) searchReq.PreferredMaxZ = pMaxMm.Value * MmToFeet;

                RouteSearch.Result result = RouteSearch.Find(searchReq);
                if (!result.Found && result.ShortLeg.HasValue)
                {
                    error = "no_route: leg " + (result.ShortLeg.Value + 1) + " of " + (result.Polyline.Count - 1) + " of the best path is " +
                        Math.Round(result.ShortLegLength * 304.8, 1).ToString(CultureInfo.InvariantCulture) + " mm, shorter than the " + Math.Round(result.ShortLegMinimum * 304.8, 1).ToString(CultureInfo.InvariantCulture) +
                        " mm its elbow(s) need (one outside size per elbow) - put the end on the start's axis, or at least that far off it.";
                    return null;
                }
                if (!result.Found)
                {
                    string reason = result.Reason ?? "no path was found within max_nodes";
                    error = (reason.StartsWith("no_route", StringComparison.Ordinal) ? reason : "no_route: " + reason) +
                        (result.BlockingRegion.HasValue ? " (blocking region: " + (result.BlockingRegion.Value.Name ?? "unnamed") + ")" : "");
                    return null;
                }

                return new RoutePlan
                {
                    _kind = kind, _typeId = typeId, _systemTypeId = systemTypeId, _levelId = levelId,
                    _diameter = diameterFt, _width = widthFt, _height = heightFt, _result = result,
                    _outerSize = runSize, _sizeBasis = sizeBasis
                };
            }

            /// <summary>
            /// The OUTSIDE diameter for the nominal size asked, from the pipe segment(s) the
            /// type's routing preferences name; a size no segment lists, a conduit or a round
            /// duct falls back to the nominal, and size_basis says which was used.
            /// </summary>
            private static double OuterDiameterOf(Document doc, Element typeEl, double nominal, out string basis)
            {
                basis = "nominal";
                RoutingPreferenceManager rpm = typeEl is PipeType pt ? Rpm(pt) : null;
                if (rpm == null) return nominal;
                basis = "nominal (no pipe segment of the type lists this size)";
                double best = nominal;
                try
                {
                    int n = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments);
                    for (int i = 0; i < n; i++)
                    {
                        var seg = doc.GetElement(rpm.GetRule(RoutingPreferenceRuleGroupType.Segments, i).MEPPartId) as PipeSegment;
                        if (seg == null) continue;
                        foreach (MEPSize s in seg.GetSizes())
                            if (Math.Abs(s.NominalDiameter - nominal) <= MepRoutingRules.SizeToleranceFeet && s.OuterDiameter > best)
                            { best = s.OuterDiameter; basis = "outer diameter of pipe segment " + Rid.Value(seg.Id); }
                    }
                }
                catch { }
                return best;
            }

            private static XYZ ParsePoint(JToken token, string name, Units u, ref string error)
            {
                var arr = token as JArray;
                if (arr == null || arr.Count != 3) { error = name + " must be [x, y, z] in the request's units."; return null; }
                try { return new XYZ(arr[0].Value<double>() * u.ToFeet, arr[1].Value<double>() * u.ToFeet, arr[2].Value<double>() * u.ToFeet); }
                catch (Exception ex) { error = name + " must be three finite numbers: " + ex.Message; return null; }
            }

            /// <summary>Physical hosts and loaded-link elements near the search box, as inflated world-space boxes.</summary>
            private static List<RouteSearch.Box3> CollectObstacles(Document doc, XYZ start, XYZ end, double margin, double inflate)
            {
                var boxes = new List<RouteSearch.Box3>();
                var min = new XYZ(Math.Min(start.X, end.X) - margin, Math.Min(start.Y, end.Y) - margin, Math.Min(start.Z, end.Z) - margin);
                var max = new XYZ(Math.Max(start.X, end.X) + margin, Math.Max(start.Y, end.Y) + margin, Math.Max(start.Z, end.Z) + margin);
                Outline outline;
                try { outline = new Outline(min, max); } catch { return boxes; }
                var bboxFilter = new BoundingBoxIntersectsFilter(outline);

                foreach (Element e in new FilteredElementCollector(doc).WherePasses(bboxFilter).WhereElementIsNotElementType())
                {
                    if (!SpatialCoherence.IsPhysical(e)) continue;
                    // WHAT THE ROUTE CONTINUES OR FEEDS is not in its way: an element with a FREE connector at the start or
                    // the end (measured 2026-10-16: routing from a branch's free end refused as 'the start point is inside'
                    // that very branch, and a route to a cabinet's connector would end inside the cabinet).
                    if (FreeConnectorAt(e, start) || FreeConnectorAt(e, end)) continue;
                    AddBox(boxes, e, null, inflate, "host:" + Rid.Value(e.Id) + ":" + (e.Category?.Name ?? e.GetType().Name));
                }

                foreach (Element linkEl in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                {
                    var inst = linkEl as RevitLinkInstance;
                    Document linkedDoc = null;
                    try { linkedDoc = inst?.GetLinkDocument(); } catch { }
                    if (linkedDoc == null) continue; // unloaded: not observable, skipped rather than refused
                    Transform t;
                    try { t = inst.GetTotalTransform(); } catch { continue; }
                    XYZ localMin, localMax;
                    try { CornersBox(t.Inverse, min, max, out localMin, out localMax); } catch { continue; }
                    BoundingBoxIntersectsFilter linkFilter;
                    try { linkFilter = new BoundingBoxIntersectsFilter(new Outline(localMin, localMax)); } catch { continue; }
                    foreach (Element e in new FilteredElementCollector(linkedDoc).WherePasses(linkFilter).WhereElementIsNotElementType())
                    {
                        if (!SpatialCoherence.IsPhysical(e)) continue;
                        AddBox(boxes, e, t, inflate, "link:" + (inst.Name ?? "?") + ":" + Rid.Value(e.Id));
                    }
                }
                return boxes;
            }

            private static bool FreeConnectorAt(Element e, XYZ p)
            {
                ConnectorManager m = MepFacts.ManagerOf(e);
                if (m == null) return false;
                try
                {
                    foreach (Connector c in m.Connectors)
                        if (c.ConnectorType == ConnectorType.End && !c.IsConnected && c.Origin.DistanceTo(p) <= 1.0 / 304.8) return true;
                }
                catch { }
                return false;
            }

            private static void AddBox(List<RouteSearch.Box3> boxes, Element e, Transform worldTransform, double inflate, string name)
            {
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch { }
                if (bb == null) return;
                // The box's own transform first, then the link's, over all eight corners.
                Transform own = null;
                try { own = bb.Transform != null && !bb.Transform.IsIdentity ? bb.Transform : null; } catch { }
                Transform total = own == null ? worldTransform : worldTransform == null ? own : worldTransform.Multiply(own);
                CornersBox(total, bb.Min, bb.Max, out XYZ mn, out XYZ mx);
                boxes.Add(new RouteSearch.Box3(mn.X - inflate, mn.Y - inflate, mn.Z - inflate, mx.X + inflate, mx.Y + inflate, mx.Z + inflate, name));
            }

            /// <summary>
            /// The axis-aligned box, in t's target space, around all EIGHT corners of min/max.
            /// Reviewed defect: mapping two opposite corners undersizes the box of anything in
            /// a link rotated off 90 degrees (at 45 degrees a square collapses to zero width).
            /// </summary>
            private static void CornersBox(Transform t, XYZ min, XYZ max, out XYZ lo, out XYZ hi)
            {
                double lx = double.MaxValue, ly = double.MaxValue, lz = double.MaxValue;
                double hx = double.MinValue, hy = double.MinValue, hz = double.MinValue;
                for (int i = 0; i < 8; i++)
                {
                    var c = new XYZ((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
                    XYZ w = t == null ? c : t.OfPoint(c);
                    lx = Math.Min(lx, w.X); ly = Math.Min(ly, w.Y); lz = Math.Min(lz, w.Z);
                    hx = Math.Max(hx, w.X); hy = Math.Max(hy, w.Y); hz = Math.Max(hz, w.Z);
                }
                lo = new XYZ(lx, ly, lz); hi = new XYZ(hx, hy, hz);
            }

            public override void Apply(Document doc)
            {
                _segments.Clear(); _elbowIds.Clear(); _createdIds.Clear();
                List<RouteSearch.Point3> poly = _result.Polyline;
                var segments = new List<Element>();
                for (int i = 0; i < poly.Count - 1; i++)
                {
                    XYZ a = ToXyz(poly[i]), b = ToXyz(poly[i + 1]);
                    // RouteSearch never emits a leg under its minimum. Refused, not skipped: skipping
                    // shifted every later bend onto the wrong polyline vertex (reviewed defect).
                    if (a.DistanceTo(b) < MmToFeet)
                        throw new InvalidOperationException("leg " + (i + 1) + " of the planned route is under 1 mm; refused rather than skipped");
                    Element seg;
                    switch (_kind)
                    {
                        case "pipe": seg = Pipe.Create(doc, _systemTypeId, _typeId, _levelId, a, b); break;
                        case "duct": seg = Duct.Create(doc, _systemTypeId, _typeId, _levelId, a, b); break;
                        case "conduit": seg = Conduit.Create(doc, _typeId, a, b, _levelId); break;
                        case "cable_tray": seg = CableTray.Create(doc, _typeId, a, b, _levelId); break;
                        default: throw new InvalidOperationException("unsupported kind '" + _kind + "'");
                    }
                    var rec = new SegRecord { Id = seg.Id, Start = a, End = b };
                    ApplySize(seg, ref rec);
                    segments.Add(seg);
                    _segments.Add(rec);
                    _createdIds.Add(seg.Id);
                }
                for (int i = 0; i < segments.Count - 1; i++)
                {
                    XYZ bendPoint = ToXyz(poly[i + 1]); // segments[i] is poly[i]..poly[i+1]: no leg is ever skipped
                    Connector cA = OpenConnectorNear(segments[i], bendPoint);
                    Connector cB = OpenConnectorNear(segments[i + 1], bendPoint);
                    if (cA == null || cB == null)
                        throw new InvalidOperationException("segment at bend " + (i + 1) + " has no open connector within 1 mm of the bend to fit an elbow (nothing was rolled back yet - the caller's transaction wrapper does that)");
                    FamilyInstance elbow = doc.Create.NewElbowFitting(cA, cB);
                    _segments[i].EndElbow = elbow.Id; _segments[i + 1].StartElbow = elbow.Id;
                    _elbowIds.Add(elbow.Id);
                    _createdIds.Add(elbow.Id);
                }
                doc.Regenerate();

                SpatialCoherence.Outcome spatial = SpatialCoherence.Check(doc, SpatialCoherence.Subjects(doc, _createdIds));
                _spatial = spatial;
                // 'Could not look' is not 'clean': a check that ran out of budget or subjects skipped
                // part of the route or every link (reviewed defect: it committed with host_verified=true).
                if (spatial.Partial)
                    throw new InvalidOperationException("the spatial check after routing was incomplete (" + (spatial.PartialWhy ?? "partial") + "); a route is not committed on a check that could not look everywhere. Rolled back.");
                if (spatial.Errors > 0)
                {
                    SpatialCoherence.Finding f = spatial.Findings.FirstOrDefault(x => x.Verdict.Severity == "error");
                    throw new InvalidOperationException("the spatial check found " + spatial.Errors + " error(s) against physical elements after routing" +
                        (f != null ? ": " + f.Verdict.Kind + " (" + f.Verdict.Reason + ") between element " + Rid.Value(f.A.Id) + " and " + Rid.Value(f.B.Id) +
                            (f.LinkB != null ? " (in link '" + f.LinkB + "')" : "") : "") + ". Rolled back.");
                }
            }

            /// <summary>
            /// Sets the run's size through the same parameter map resize uses (Classify): pipe
            /// and conduit diameters, a duct's diameter or width/height by its type's shape, and
            /// a cable tray's RBS_CABLETRAY_* width/height (not the RBS_CURVE_* pair, which a
            /// cable tray does not carry). A size Revit refuses throws, rolling the route back;
            /// the value it holds is re-read by Verify() as its own postcondition.
            /// </summary>
            private void ApplySize(Element seg, ref SegRecord rec)
            {
                string why;
                Run r = seg is MEPCurve mc ? Classify(mc, out why) : null;
                if (r == null) throw new InvalidOperationException("cannot size new segment " + Rid.Value(seg.Id) + " (no size parameters readable)");
                double[] want = r.Params.Length == 1
                    ? new[] { _diameter ?? throw new InvalidOperationException("a " + r.Kind + " takes a diameter") }
                    : new[] { _width ?? throw new InvalidOperationException("a " + r.Kind + " takes width and height"), _height.Value };
                for (int i = 0; i < r.Params.Length; i++)
                {
                    Parameter p = seg.get_Parameter(r.Params[i]);
                    if (p == null || p.IsReadOnly || !p.Set(want[i]))
                        throw new InvalidOperationException("Revit refused " + r.Params[i] + " = " + Math.Round(want[i] * 304.8, 1).ToString(CultureInfo.InvariantCulture) + " mm on new " + r.Kind + " " + Rid.Value(seg.Id));
                }
                rec.SizeParams = r.Params; rec.SizeWanted = want;
            }

            /// <summary>The element's own FREE connector nearest to <paramref name="target"/> - the bend point an elbow goes at.</summary>
            private static Connector OpenConnectorNear(Element seg, XYZ target)
            {
                var mep = seg as MEPCurve;
                ConnectorManager mgr = mep?.ConnectorManager;
                if (mgr == null) return null;
                Connector best = null; double bestDist = double.MaxValue;
                foreach (Connector c in mgr.Connectors)
                {
                    if (c.IsConnected) continue;
                    double d = c.Origin.DistanceTo(target);
                    if (d <= MmToFeet && d < bestDist) { bestDist = d; best = c; }
                }
                return best;
            }

            private static XYZ ToXyz(RouteSearch.Point3 p) => new XYZ(p.X, p.Y, p.Z);

            public override PostconditionCheck Verify(Document doc)
            {
                var required = new List<string>();
                foreach (SegRecord s in _segments) { required.Add("segment:" + Rid.Value(s.Id) + ":start"); required.Add("segment:" + Rid.Value(s.Id) + ":end"); required.Add("segment:" + Rid.Value(s.Id) + ":size"); }
                foreach (ElementId id in _elbowIds) required.Add("elbow:" + Rid.Value(id) + ":connected");
                var check = new PostconditionCheck(required.ToArray());

                foreach (SegRecord s in _segments)
                {
                    Element live = null;
                    try { live = doc.GetElement(s.Id); } catch { }
                    Curve curve = (live?.Location as LocationCurve)?.Curve;
                    XYZ actualStart = null, actualEnd = null;
                    try { if (curve != null) { actualStart = curve.GetEndPoint(0); actualEnd = curve.GetEndPoint(1); } } catch { }
                    bool startOk = EndOk(doc, live as MEPCurve, actualStart, s.Start, s.End, s.StartElbow, out JToken startFound);
                    bool endOk = EndOk(doc, live as MEPCurve, actualEnd, s.End, s.Start, s.EndElbow, out JToken endFound);
                    // Both trims together must still leave the run pointing the planned way.
                    if (actualStart != null && actualEnd != null && (actualEnd - actualStart).DotProduct(s.End - s.Start) <= 0) endOk = false;
                    check.Record("segment:" + Rid.Value(s.Id) + ":start", PointJson(s.Start), startFound, startOk);
                    check.Record("segment:" + Rid.Value(s.Id) + ":end", PointJson(s.End), endFound, endOk);
                    // The size as the model holds it now - a size Revit snapped to its catalog
                    // (not the one asked for) is a failed postcondition, named, not a pass.
                    var wantMm = new JArray(); var haveMm = new JArray(); bool sizeOk = s.SizeParams != null;
                    for (int i = 0; s.SizeParams != null && i < s.SizeParams.Length; i++)
                    {
                        double? have = null;
                        try { Parameter p = live?.get_Parameter(s.SizeParams[i]); if (p != null && p.HasValue) have = p.AsDouble(); } catch { }
                        wantMm.Add(Math.Round(s.SizeWanted[i] * 304.8, 1));
                        haveMm.Add(have.HasValue ? (JToken)Math.Round(have.Value * 304.8, 1) : JValue.CreateNull());
                        if (!have.HasValue || Math.Abs(have.Value - s.SizeWanted[i]) > MmToFeet) sizeOk = false;
                    }
                    check.Record("segment:" + Rid.Value(s.Id) + ":size", wantMm, haveMm, sizeOk);
                }
                foreach (ElementId id in _elbowIds)
                {
                    bool connected = false;
                    try
                    {
                        var fi = doc.GetElement(id) as FamilyInstance;
                        ConnectorManager mgr = fi?.MEPModel?.ConnectorManager;
                        if (mgr != null) connected = mgr.Connectors.Cast<Connector>().Count(c => c.IsConnected) >= 2;
                    }
                    catch { }
                    check.Record("elbow:" + Rid.Value(id) + ":connected", true, connected, connected);
                }
                return check;
            }

            /// <summary>
            /// A FREE end (no elbow) must sit on its planned point within 1 mm. An end at a BEND
            /// cannot: NewElbowFitting trims both runs back to the elbow's connectors. There the
            /// NOMINAL junction - the intersection of the elbow's two connector axes - must sit
            /// on the planned vertex within 1 mm, the physical end must stay on the planned leg's
            /// axis, pulled back toward the other end (never past it), and its connector must be
            /// connected to that same elbow. Reviewed defect of the first version: it compared the
            /// trimmed ends with the vertices, so every route with a bend failed its rehearsal.
            /// </summary>
            private static bool EndOk(Document doc, MEPCurve curve, XYZ physical, XYZ planned, XYZ other, ElementId elbowId, out JToken found)
            {
                found = physical == null ? (JToken)JValue.CreateNull() : PointJson(physical);
                if (physical == null) return false;
                if (elbowId == null) return physical.DistanceTo(planned) <= MmToFeet;
                var o = new JObject { ["physical_mm"] = PointJson(physical), ["elbow_id"] = Rid.Value(elbowId) };
                found = o;
                try
                {
                    XYZ outward = (planned - other).Normalize();
                    double trim = (planned - physical).DotProduct(outward);
                    double offAxis = (physical - other).CrossProduct(outward).GetLength();
                    o["trim_mm"] = Math.Round(trim * 304.8, 1);
                    bool onLeg = offAxis <= MmToFeet && trim >= -MmToFeet && trim < planned.DistanceTo(other);
                    List<Connector> ports = MepFacts.Ordered(MepFacts.ManagerOf(doc.GetElement(elbowId)))
                        .Where(c => c.ConnectorType == ConnectorType.End).ToList();
                    if (ports.Count != 2) { o["why"] = "the elbow exposes " + ports.Count + " end connectors, not 2"; return false; }
                    bool attached = curve != null && MepFacts.Ordered(curve.ConnectorManager).Any(c => c.Origin.DistanceTo(physical) <= MmToFeet &&
                        ports.Any(p => p.Origin.DistanceTo(physical) <= MmToFeet && p.IsConnectedTo(c)));
                    o["attached_to_elbow"] = attached;
                    double[] j = MepRules.AxisIntersection(AxisFact(ports[0]), AxisFact(ports[1]), MmToFeet);
                    XYZ junction = j == null ? null : new XYZ(j[0], j[1], j[2]);
                    o["junction_mm"] = junction == null ? (JToken)JValue.CreateNull() : PointJson(junction);
                    return onLeg && attached && junction != null && junction.DistanceTo(planned) <= MmToFeet;
                }
                catch (Exception ex) { o["why"] = ex.Message; return false; }
            }

            private static ConnectorFact AxisFact(Connector c)
            {
                XYZ origin = c.Origin, d = c.CoordinateSystem.BasisZ;
                return new ConnectorFact { X = origin.X, Y = origin.Y, Z = origin.Z, DirX = d.X, DirY = d.Y, DirZ = d.Z };
            }

            private static JToken PointJson(XYZ p) => new JArray(Math.Round(p.X * 304.8, 1), Math.Round(p.Y * 304.8, 1), Math.Round(p.Z * 304.8, 1));

            public override JObject Describe(Units u)
            {
                var poly = new JArray(_result.Polyline.Select(p => (JToken)PointJson(ToXyz(p))));
                return new JObject
                {
                    ["kind"] = _kind, ["type_id"] = Rid.Value(_typeId),
                    ["system_type_id"] = _systemTypeId == null ? (JToken)JValue.CreateNull() : Rid.Value(_systemTypeId),
                    ["level_id"] = Rid.Value(_levelId),
                    ["length_mm"] = Math.Round(_result.Length * 304.8, 1), ["bends"] = _result.Bends,
                    ["nodes_expanded"] = _result.NodesExpanded, ["search_margin_steps"] = _result.MarginStepsUsed, ["polyline_mm"] = poly,
                    ["planned_segments"] = Legs, ["planned_elbows"] = Math.Max(0, Legs - 1),
                    ["outer_size_mm"] = Math.Round(_outerSize * 304.8, 1), ["size_basis"] = _sizeBasis
                };
            }

            public override JToken Report(Document doc, Units u)
            {
                return new JObject
                {
                    ["length_mm"] = Math.Round(_result.Length * 304.8, 1), ["bends"] = _result.Bends,
                    ["polyline_mm"] = new JArray(_result.Polyline.Select(p => (JToken)PointJson(ToXyz(p)))),
                    ["segment_ids"] = new JArray(_segments.Select(s => (JToken)Rid.Value(s.Id))),
                    ["elbow_ids"] = new JArray(_elbowIds.Select(id => (JToken)Rid.Value(id))),
                    // Evidence of the gate that let this route commit: errors is 0 by construction
                    // (any error threw and rolled back); partial says what the check could not see.
                    ["spatial_check"] = _spatial == null ? (JToken)JValue.CreateNull() : new JObject
                    {
                        ["subjects"] = _spatial.Subjects, ["checked"] = _spatial.Checked, ["errors"] = _spatial.Errors,
                        ["warnings"] = _spatial.Findings.Count(f => f.Verdict.Severity == "warning"),
                        ["links_examined"] = _spatial.LinksExamined, ["partial"] = _spatial.Partial,
                        ["partial_why"] = _spatial.PartialWhy
                    }
                };
            }

            public override void ResetAfterRehearsal() { _segments.Clear(); _elbowIds.Clear(); _createdIds.Clear(); _spatial = null; }

            public override ResolvedPlan Resolved(GateResult gate, UIApplication app, string command)
            {
                // The token binds THIS route: apply searches again, and a different polyline (the
                // model changed in between) must be refused as stale, not committed unpreviewed.
                var rp = NewResolved(gate, app, command);
                string Mm(double v) => Math.Round(v * 304.8, 1).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                string Pt(RouteSearch.Point3 p) => Mm(p.X) + "," + Mm(p.Y) + "," + Mm(p.Z);
                List<RouteSearch.Point3> poly = _result.Polyline;
                rp.ContextFingerprint = "route=" + _kind + ";type=" + Rid.Value(_typeId) +
                    ";system=" + (_systemTypeId == null ? "-" : Rid.Value(_systemTypeId).ToString()) + ";level=" + Rid.Value(_levelId) +
                    ";size=" + (_diameter.HasValue ? "d" + Mm(_diameter.Value) : "w" + Mm(_width.Value) + "h" + Mm(_height.Value)) +
                    ";polyline=" + string.Join("|", poly.Select(Pt));
                for (int i = 0; i < Legs; i++)
                    rp.Elements.Add(new PlannedElement
                    {
                        UniqueId = "route:segment:" + i, Category = _kind, Action = PlannedAction.Create,
                        ProposedValues = new Dictionary<string, string> { ["from_mm"] = Pt(poly[i]), ["to_mm"] = Pt(poly[i + 1]) }
                    });
                for (int i = 1; i < Legs; i++)
                    rp.Elements.Add(new PlannedElement
                    {
                        UniqueId = "route:elbow:" + i, Category = _kind + "_elbow", Action = PlannedAction.Create,
                        ProposedValues = new Dictionary<string, string> { ["junction_mm"] = Pt(poly[i]) }
                    });
                return rp;
            }
        }
    }
}
