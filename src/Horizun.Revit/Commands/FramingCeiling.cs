// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_framing operation=ceiling: the Revit half.
// Original Horizun code.
//
// READING. The boundary is the ceiling's own sketch (Ceiling.SketchId, present in
// the 2023 and 2026 RevitAPI.xml; its dependents also hold the sketches of the
// openings it hosts), plus the outline of every opening hosted by it and every
// shaft crossing its height as a hole, each loop chained end to end from the
// tessellated curves, the largest loop first. A sketch with a second region
// OUTSIDE the largest one is refused by name: the plan's direction and extent are
// the outer loop's, and a second island would silently get no mains. The top face
// is the ceiling's box top; a box taller than the type's compound width (+1 mm)
// is a sloped ceiling and is refused, never framed flat. Under a view_id scope
// those refusals become 'skipped' rows instead (the wall operation's rule).
//
// HEIGHTS (plan mm, absolute z). Cross and perimeter members bear on the top face
// (axis = top + depth/2); mains sit drop_mm above it (axis = top + drop + depth/2);
// a hanger runs from the mains' top face up to the first floor, structural framing
// or roof straight above it, host or loaded link, within max_length_mm. depth_mm is
// optional and its absence is a warning, not a guess of the section.
//
// HANGER RAYS reuse horizun_mep_routing's approach (MepRoutingHangers.cs): a
// temporary isometric view whose visibility this code sets (no template, no
// filters, no section box, structure and links shown, the source's phase),
// ReferenceIntersector with links, inside a transaction that is ALWAYS rolled
// back. A station with nothing above is reported no_support_above and NOT placed.
// A marked horizun_framing member is never a support: on a second apply the
// mains already there would otherwise shorten every rod and change the plan.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    /// <summary>One ceiling read for framing: boundary loops (plan mm, largest first), top face and level.</summary>
    internal sealed class FramedCeiling
    {
        public Level Level;
        public double TopMm, MainZMm, CrossZMm, PerimeterZMm, HangerFromMm;
        public List<List<double[]>> LoopsMm = new List<List<double[]>>();
        public readonly JArray NoSupportAbove = new JArray();
        /// <summary>Openings (hosted or shafts) whose outlines were added as holes.</summary>
        public readonly List<long> HoleIds = new List<long>();
        /// <summary>"host:id" or "linked:link/id" -> hangers it carries.</summary>
        public readonly Dictionary<string, int> Supports = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public sealed partial class FramingCommand
    {
        private const double MmPerFt = 304.8;
        private static readonly BuiltInCategory[] SupportCategories =
            { BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFraming, BuiltInCategory.OST_Roofs };

        internal static FramedCeiling ReadCeiling(Document doc, Ceiling ceiling, out string refusal)
        {
            refusal = null;
            long id = Rid.Value(ceiling.Id);
            if (!(doc.GetElement(ceiling.LevelId) is Level level)) { refusal = "ceiling " + id + " has no level."; return null; }
            BoundingBoxXYZ bb = ceiling.get_BoundingBox(null);
            if (bb == null) { refusal = "ceiling " + id + " has no geometry to read its top face from."; return null; }
            // A type with no compound structure (a Basic Ceiling) has width 0: its box must then be
            // flat to within 1 mm, so a sloped one is refused rather than framed at its high point.
            double thickFt = (doc.GetElement(ceiling.GetTypeId()) as HostObjAttributes)?.GetCompoundStructure()?.GetWidth() ?? 0;
            if ((bb.Max.Z - bb.Min.Z) - thickFt > 1.0 / MmPerFt)
            {
                refusal = "ceiling " + id + " is sloped (its box is " + Math.Round((bb.Max.Z - bb.Min.Z) * MmPerFt, 1).ToString(CultureInfo.InvariantCulture) + " mm tall for a " +
                          Math.Round(thickFt * MmPerFt, 1).ToString(CultureInfo.InvariantCulture) + " mm type); only flat ceilings are framed.";
                return null;
            }
            // The ceiling's OWN sketch: its dependents also hold the sketches of openings it hosts.
            Sketch sketch = ceiling.SketchId == ElementId.InvalidElementId ? null : doc.GetElement(ceiling.SketchId) as Sketch;
            if (sketch?.Profile == null || sketch.Profile.Size == 0) { refusal = "ceiling " + id + " exposes no sketch boundary."; return null; }
            var fc = new FramedCeiling { Level = level, TopMm = bb.Max.Z * MmPerFt };
            foreach (CurveArray arr in sketch.Profile)
            {
                List<double[]> loop = Chain(arr, out string why);
                if (loop == null) { refusal = "ceiling " + id + ": " + why + "."; return null; }
                if (loop.Count >= 3) fc.LoopsMm.Add(loop);
            }
            if (fc.LoopsMm.Count == 0) { refusal = "ceiling " + id + ": its sketch has no closed loop of three or more points."; return null; }
            fc.LoopsMm = fc.LoopsMm.OrderByDescending(l => Math.Abs(CeilingFramingRules.Area(l))).ToList();
            var outer = new List<List<double[]>> { fc.LoopsMm[0] };
            int islands = fc.LoopsMm.Skip(1).Count(l => !CeilingFramingRules.Inside(outer, l[0][0], l[0][1]));
            if (islands > 0)
            {
                refusal = "ceiling " + id + " sketches " + (islands + 1) + " separate regions; split it into one ceiling per region before framing it.";
                return null;
            }
            // Openings cut into it (hosted by-face openings, shafts crossing its height) are holes:
            // added as loops, the even-odd boundary test treats them as such in plan and verify.
            foreach (Opening op in new FilteredElementCollector(doc).OfClass(typeof(Opening)).Cast<Opening>())
            {
                bool hosted = op.Host != null && op.Host.Id == ceiling.Id;
                if (!hosted)
                {
                    if (op.Host != null || op.Category == null || Rid.Value(op.Category.Id) != (long)BuiltInCategory.OST_ShaftOpening) continue;
                    BoundingBoxXYZ ob = op.get_BoundingBox(null);
                    if (ob == null || ob.Max.Z < bb.Min.Z || ob.Min.Z > bb.Max.Z || ob.Max.X < bb.Min.X || ob.Min.X > bb.Max.X || ob.Max.Y < bb.Min.Y || ob.Min.Y > bb.Max.Y) continue;
                }
                List<double[]> hole = null;
                if (op.IsRectBoundary && op.BoundaryRect != null && op.BoundaryRect.Count > 1)
                {
                    XYZ a = op.BoundaryRect[0], b = op.BoundaryRect[1];
                    hole = new List<double[]> { new[] { a.X * MmPerFt, a.Y * MmPerFt }, new[] { b.X * MmPerFt, a.Y * MmPerFt }, new[] { b.X * MmPerFt, b.Y * MmPerFt }, new[] { a.X * MmPerFt, b.Y * MmPerFt } };
                }
                else if (op.BoundaryCurves != null && op.BoundaryCurves.Size > 0) hole = Chain(op.BoundaryCurves, out string _);
                if (hole != null && hole.Count >= 3) { fc.LoopsMm.Add(hole); fc.HoleIds.Add(Rid.Value(op.Id)); }
            }
            return fc;
        }

        /// <summary>One sketch loop as plan points in mm, its curves chained end to end whatever their order and sense.</summary>
        private static List<double[]> Chain(CurveArray arr, out string why)
        {
            why = null;
            var curves = new List<List<XYZ>>();
            foreach (Curve c in arr) curves.Add(c.Tessellate().ToList());
            if (curves.Count == 0) { why = "an empty sketch loop"; return null; }
            double tol = 1.0 / MmPerFt;
            var pts = new List<XYZ>(curves[0]);
            curves.RemoveAt(0);
            while (curves.Count > 0)
            {
                XYZ end = pts[pts.Count - 1];
                int k = curves.FindIndex(c => c[0].DistanceTo(end) < tol || c[c.Count - 1].DistanceTo(end) < tol);
                if (k < 0) { why = "a sketch loop does not close (gap after " + Fmt(end) + " mm)"; return null; }
                List<XYZ> next = curves[k];
                curves.RemoveAt(k);
                if (next[0].DistanceTo(end) >= tol) next.Reverse();
                pts.AddRange(next.Skip(1));
            }
            if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) < tol) pts.RemoveAt(pts.Count - 1);
            return pts.Select(p => new[] { p.X * MmPerFt, p.Y * MmPerFt }).ToList();
        }

        private static List<FramingSourcePlan> PlanCeilings(Document doc, JObject request, CeilingFramingSpec spec, string specHash, List<string> skipped)
        {
            bool viewScope = request["view_id"] != null && SourceIds(request) == null;
            var symbols = new Dictionary<string, FamilySymbol>(StringComparer.Ordinal);
            foreach (long id in spec.TypeIds())
            {
                FamilySymbol s = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) as FamilySymbol : null;
                if (s == null) throw new ArgumentException("type id " + id + " in spec.ceiling is not a family type of this document.");
                symbols[id.ToString(CultureInfo.InvariantCulture)] = s;
            }
            var kinds = new Dictionary<string, FramingPlacementKind>(StringComparer.Ordinal);
            var plans = new List<FramingSourcePlan>();
            foreach (Ceiling ceiling in Sources<Ceiling>(doc, request, "ceiling"))
            {
                long sid = Rid.Value(ceiling.Id);
                FramedCeiling fc = ReadCeiling(doc, ceiling, out string refusal);
                if (fc == null)
                {
                    // A view shows whatever the model has: there a sloped, multi-region or sketchless
                    // ceiling is listed in 'skipped' with its reason, as a curtain wall is for
                    // operation=wall. Named in element_ids it still refuses the call.
                    if (!viewScope) throw new ArgumentException(refusal);
                    skipped.Add(refusal);
                    continue;
                }
                CeilingFramingPlan plan = CeilingFramingRules.Plan(spec.ToInput(fc.LoopsMm), MaxMembersPerSource);
                if (!string.IsNullOrEmpty(plan.Refusal)) throw new ArgumentException("ceiling " + sid + ": " + plan.Refusal);
                var p = new FramingSourcePlan { Source = ceiling, Operation = "ceiling", Ceiling = fc, SpecHash = specHash, Symbols = symbols, Kinds = kinds, Members = plan.Members, PlaneSpan = XYZ.BasisZ };
                p.Warnings.AddRange(plan.Warnings);
                if (spec.MainDepthMm == null) p.Warnings.Add("ceiling " + sid + ": no main.depth_mm; the mains' axis sits drop_mm above the top face and hangers start there");
                if (spec.CrossTypeId != null && spec.CrossDepthMm == null) p.Warnings.Add("ceiling " + sid + ": no cross.depth_mm; the cross members' axis sits on the top face");
                if (spec.PerimeterTypeId != null && spec.PerimeterDepthMm == null) p.Warnings.Add("ceiling " + sid + ": no perimeter.depth_mm; the perimeter axis sits on the top face");
                // The cross (furring) members bear on the top face and the mains start drop_mm above
                // it: a drop smaller than the furring's depth plans mains running THROUGH them.
                if (spec.CrossTypeId != null && spec.CrossDepthMm != null && spec.DropMm < spec.CrossDepthMm.Value - 1e-6)
                    p.Warnings.Add("ceiling " + sid + ": drop_mm " + spec.DropMm.ToString(CultureInfo.InvariantCulture) + " is less than cross.depth_mm " +
                                   spec.CrossDepthMm.Value.ToString(CultureInfo.InvariantCulture) + "; the mains run through the cross members");
                double mainDepth = spec.MainDepthMm ?? 0;
                fc.CrossZMm = fc.TopMm + (spec.CrossDepthMm ?? 0) / 2;
                fc.PerimeterZMm = fc.TopMm + (spec.PerimeterDepthMm ?? 0) / 2;
                fc.MainZMm = fc.TopMm + spec.DropMm + mainDepth / 2;
                fc.HangerFromMm = fc.TopMm + spec.DropMm + mainDepth;
                foreach (FramingMember m in p.Members)
                {
                    double z = m.Role == FramingRoles.Main ? fc.MainZMm : m.Role == FramingRoles.Cross ? fc.CrossZMm
                             : m.Role == FramingRoles.Perimeter ? fc.PerimeterZMm : fc.HangerFromMm;
                    m.Z0 = m.Z1 = Math.Round(z, 1);
                }
                plans.Add(p);
            }
            if (plans.Count == 0)
                throw new ArgumentException("view " + request.Value<long?>("view_id") + " shows no ceiling this operation can frame (" + skipped.Count +
                                            " skipped: " + string.Join("; ", skipped.Take(5)) + ").");
            CastHangers(doc, plans, spec.HangerMaxLengthMm);

            int total = 0;
            foreach (FramingSourcePlan p in plans)
            {
                total += p.Members.Count;
                if (total > MaxMembersTotal) throw new ArgumentException("the plan exceeds " + MaxMembersTotal + " members across its ceilings; frame fewer ceilings per call.");
                foreach (FramingMember m in p.Members)
                {
                    string key = m.Role + "|" + m.TypeKey;
                    if (kinds.ContainsKey(key)) continue;
                    if (m.TypeKey == null || !symbols.TryGetValue(m.TypeKey, out FamilySymbol sym)) throw new ArgumentException(m.Role + " has no type in the spec.");
                    string why = ClassifyType(sym, m.Role == FramingRoles.Hanger, m.Role, out FramingPlacementKind kind);
                    if (why != null) throw new ArgumentException(why);
                    kinds[key] = kind;
                }
                p.Axis = m => Line.CreateBound(new XYZ(m.X0 / MmPerFt, m.Y0 / MmPerFt, m.Z0 / MmPerFt), new XYZ(m.X1 / MmPerFt, m.Y1 / MmPerFt, m.Z1 / MmPerFt));
                // A line-based member's work plane: horizontal for mains, cross and perimeter
                // (spanned by the member and its horizontal normal), vertical for a hanger.
                p.Span = m => m.Role == FramingRoles.Hanger ? XYZ.BasisX : XYZ.BasisZ.CrossProduct(new XYZ(m.X1 - m.X0, m.Y1 - m.Y0, 0).Normalize());
                p.Signature = FramingPlanSignature.Of(p.Members);
                ClaimExisting(doc, p);
            }
            return plans;
        }

        /// <summary>Each hanger's rod to the structure above; stations with none within max length leave the plan, named.</summary>
        private static void CastHangers(Document doc, List<FramingSourcePlan> plans, double maxLengthMm)
        {
            if (!plans.Any(p => p.Members.Any(m => m.Role == FramingRoles.Hanger))) return;
            using (var tx = new Transaction(doc, "Horizun: framing ray view (rolled back)"))
            {
                if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("the temporary ray view could not be created: no transaction could start.");
                try
                {
                    var views = new Dictionary<long, View3D>();
                    var filter = new ElementMulticategoryFilter(SupportCategories.Concat(new[] { BuiltInCategory.OST_RvtLinks }).ToList());
                    foreach (FramingSourcePlan p in plans)
                    {
                        ElementId phase = SourcePhase(doc, p.Source);
                        long pk = phase == null ? -1 : Rid.Value(phase);
                        if (!views.TryGetValue(pk, out View3D view)) views[pk] = view = CeilingRayView(doc, phase);
                        var ray = new ReferenceIntersector(filter, FindReferenceTarget.Face, view) { FindReferencesInRevitLinks = true };
                        var kept = new List<FramingMember>();
                        foreach (FramingMember m in p.Members)
                        {
                            if (m.Role != FramingRoles.Hanger) { kept.Add(m); continue; }
                            double rodFt = SupportAbove(doc, ray, new XYZ(m.X0 / MmPerFt, m.Y0 / MmPerFt, m.Z0 / MmPerFt), maxLengthMm / MmPerFt, out string support);
                            if (support == null || rodFt * MmPerFt < 1.0)
                            {
                                p.Ceiling.NoSupportAbove.Add(new JObject { ["main"] = m.Source, ["point_mm"] = new JArray(Math.Round(m.X0, 1), Math.Round(m.Y0, 1), Math.Round(m.Z0, 1)) });
                                continue;
                            }
                            m.Z1 = Math.Round(m.Z0 + rodFt * MmPerFt, 1);
                            p.Ceiling.Supports[support] = (p.Ceiling.Supports.TryGetValue(support, out int n) ? n : 0) + 1;
                            kept.Add(m);
                        }
                        p.Members = kept;
                        if (p.Ceiling.NoSupportAbove.Count > 0)
                            p.Warnings.Add("ceiling " + Rid.Value(p.Source.Id) + ": " + p.Ceiling.NoSupportAbove.Count + " hanger station(s) have no floor, framing or roof above within " + maxLengthMm + " mm and are not placed (no_support_above)");
                    }
                }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                }
            }
        }

        /// <summary>Distance (ft) to the nearest floor/framing/roof straight above, host or link, not a framing member; 0 and support=null when none.</summary>
        private static double SupportAbove(Document doc, ReferenceIntersector ray, XYZ origin, double maxFt, out string support)
        {
            support = null;
            IList<ReferenceWithContext> hits = ray.Find(origin, XYZ.BasisZ);
            if (hits == null) return 0;
            foreach (ReferenceWithContext h in hits.OrderBy(x => x.Proximity))
            {
                if (h.Proximity <= 1e-6) continue;
                if (h.Proximity > maxFt) break;
                Reference r = h.GetReference();
                if (r == null) continue;
                Element e = doc.GetElement(r.ElementId);
                if (e is RevitLinkInstance link)
                {
                    if (r.LinkedElementId == ElementId.InvalidElementId) continue;
                    Element linked = link.GetLinkDocument()?.GetElement(r.LinkedElementId);
                    if (!IsSupport(linked)) continue;
                    support = "linked:" + Rid.Value(link.Id) + "/" + Rid.Value(linked.Id);
                }
                else
                {
                    if (!IsSupport(e) || FramingMarker.Read(e) != null) continue;
                    support = "host:" + Rid.Value(e.Id);
                }
                return h.Proximity;
            }
            return 0;
        }

        private static bool IsSupport(Element e)
        {
            long cat = e?.Category == null ? -1 : Rid.Value(e.Category.Id);
            return SupportCategories.Any(c => (long)(int)c == cat);
        }

        private static ElementId SourcePhase(Document doc, Element e)
        {
            ElementId id = null;
            try { id = e.CreatedPhaseId; } catch { }
            if (id != null && id != ElementId.InvalidElementId) return id;
            return doc.Phases.Size > 0 ? doc.Phases.get_Item(doc.Phases.Size - 1).Id : null;
        }

        /// <summary>A temporary isometric view with KNOWN visibility, inside the caller's rolled-back transaction.</summary>
        private static View3D CeilingRayView(Document doc, ElementId phaseId)
        {
            ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft == null) throw new InvalidOperationException("the model has no 3D view type to build the temporary ray view from.");
            View3D view = View3D.CreateIsometric(doc, vft.Id);
            if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;
            foreach (ElementId f in view.GetFilters().ToList()) view.RemoveFilter(f);
            if (view.IsSectionBoxActive) view.IsSectionBoxActive = false;
            try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }
            foreach (BuiltInCategory bic in SupportCategories.Concat(new[] { BuiltInCategory.OST_RvtLinks }))
            {
                Category c = null;
                try { c = Category.GetCategory(doc, bic); } catch { }
                if (c != null && view.CanCategoryBeHidden(c.Id) && view.GetCategoryHidden(c.Id)) view.SetCategoryHidden(c.Id, false);
            }
            if (doc.IsWorkshared)
                foreach (Workset w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                    if (w.IsOpen && view.GetWorksetVisibility(w.Id) != WorksetVisibility.Visible) view.SetWorksetVisibility(w.Id, WorksetVisibility.Visible);
            if (phaseId != null)
            {
                Parameter vp = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (vp != null && !vp.IsReadOnly) vp.Set(phaseId);
            }
            doc.Regenerate();
            return view;
        }

        private static PostconditionCheck VerifyCeilings(Document doc, List<FramingSourcePlan> plans, JObject evidence)
        {
            var check = new PostconditionCheck("member_count", "member_types", "member_endpoints", "counts_by_role", "inside_boundary", "beam_settings", "hanger_reaches_support");
            var hangerTops = new List<HangerTop>();
            int planned = 0, found = 0, wrongType = 0, unreadable = 0, beamOff = 0;
            double maxDev = 0, maxOutside = 0;
            var heightReads = new JArray();
            var plannedRoles = new JObject();
            var foundRoles = new JObject();
            var perSource = new JArray();
            foreach (FramingSourcePlan p in plans)
            {
                long sid = Rid.Value(p.Source.Id);
                var byIndex = FramingMarker.Find(doc, new HashSet<long> { sid })
                    .Where(x => x.Value.Role != FramingMarker.WorkPlaneRole && x.Value.SpecHash == p.SpecHash && x.Value.PlanSignature == p.Signature)
                    .GroupBy(x => x.Value.Index).ToDictionary(g => g.Key, g => g.ToList());
                int srcFound = 0;
                double srcDev = 0, srcOut = 0;
                for (int i = 0; i < p.Members.Count; i++)
                {
                    FramingMember m = p.Members[i];
                    planned++;
                    plannedRoles[m.Role] = (plannedRoles.Value<int?>(m.Role) ?? 0) + 1;
                    if (!byIndex.TryGetValue(i, out var list) || list.Count != 1 || list[0].Value.Role != m.Role) continue;
                    Element e = list[0].Key;
                    found++; srcFound++;
                    foundRoles[m.Role] = (foundRoles.Value<int?>(m.Role) ?? 0) + 1;
                    if (Rid.Value(e.GetTypeId()).ToString(CultureInfo.InvariantCulture) != m.TypeKey) wrongType++;
                    if (p.Kinds[m.Role + "|" + m.TypeKey] == FramingPlacementKind.Beam) beamOff += BeamSettingsOff(e);
                    XYZ[] ends = MemberEnds(doc, e, out string _);
                    if (ends == null) { unreadable++; continue; }
                    if (m.Role == FramingRoles.Hanger)
                        hangerTops.Add(new HangerTop { Id = Rid.Value(e.Id), Top = ends[0].Z >= ends[1].Z ? ends[0] : ends[1], LengthMm = ends[0].DistanceTo(ends[1]) * MmPerFt, Phase = SourcePhase(doc, p.Source) });
                    Line axis = p.Axis(m);
                    XYZ a = axis.GetEndPoint(0), b = axis.GetEndPoint(1);
                    double dev = Math.Min(Math.Max(ends[0].DistanceTo(a), ends[1].DistanceTo(b)), Math.Max(ends[0].DistanceTo(b), ends[1].DistanceTo(a))) * MmPerFt;
                    srcDev = Math.Max(srcDev, dev);
                    if (dev > EndpointToleranceMm && p.Kinds[m.Role + "|" + m.TypeKey] == FramingPlacementKind.LineBased && heightReads.Count < 3)
                        heightReads.Add(HeightRead(e, a.Z * MmPerFt));
                    foreach (XYZ end in ends)
                        srcOut = Math.Max(srcOut, CeilingFramingRules.DistanceOutside(p.Ceiling.LoopsMm, end.X * MmPerFt, end.Y * MmPerFt));
                }
                maxDev = Math.Max(maxDev, srcDev);
                maxOutside = Math.Max(maxOutside, srcOut);
                var supports = new JObject();
                foreach (KeyValuePair<string, int> kv in p.Ceiling.Supports.OrderBy(k => k.Key, StringComparer.Ordinal)) supports[kv.Key] = kv.Value;
                perSource.Add(new JObject
                {
                    ["source_id"] = sid, ["already_applied"] = p.AlreadyApplied, ["planned"] = p.Members.Count, ["found"] = srcFound,
                    ["max_endpoint_deviation_mm"] = Math.Round(srcDev, 3), ["max_outside_boundary_mm"] = Math.Round(srcOut, 3),
                    ["hanger_supports"] = supports, ["no_support_above"] = p.Ceiling.NoSupportAbove,
                    ["member_ids"] = new JArray(p.MemberIds.OrderBy(kv => kv.Key).Select(kv => kv.Value)),
                    ["work_plane_ids"] = new JArray(p.WorkPlaneIds)
                });
            }
            check.Compare("member_count", planned, found);
            check.Compare("member_types", 0, wrongType);
            if (unreadable > 0) check.Unreadable("member_endpoints", 0, unreadable + " member(s) report neither a location curve nor column constraints");
            else check.Measure("member_endpoints", 0, maxDev, EndpointToleranceMm, "mm", "max over members of the farther end's distance to the planned axis end");
            check.Record("counts_by_role", plannedRoles, foundRoles, JToken.DeepEquals(plannedRoles, foundRoles));
            check.Compare("beam_settings", 0, beamOff);
            check.Measure("inside_boundary", 0, maxOutside, EndpointToleranceMm, "mm", "max plan distance of a member end outside the ceiling's sketch boundary (holes count)");
            List<long> short_ = RecheckHangers(doc, hangerTops, out double maxGap);
            check.Compare("hanger_reaches_support", 0, short_.Count);
            evidence["hanger_recheck"] = new JObject
            {
                ["checked"] = hangerTops.Count, ["not_at_support"] = new JArray(short_),
                ["max_gap_mm"] = hangerTops.Count == 0 || double.IsInfinity(maxGap) ? JValue.CreateNull() : (JToken)Math.Round(maxGap, 3)
            };
            evidence["sources"] = perSource;
            if (heightReads.Count > 0) evidence["line_based_height"] = heightReads;
            return check;
        }

        private sealed class HangerTop
        {
            public long Id;
            public XYZ Top;
            public double LengthMm;
            public ElementId Phase;
        }

        /// <summary>
        /// After the commit, one ray up from just under each placed hanger's top end: the first
        /// support must sit at that end (within 1 mm). The planning ray chose the rod length; this
        /// re-reads that the rod AS BUILT reaches it. Same rolled-back temporary view as planning.
        /// Returns the hangers that do not; maxGapMm is the largest gap measured.
        /// </summary>
        private static List<long> RecheckHangers(Document doc, List<HangerTop> tops, out double maxGapMm)
        {
            maxGapMm = 0;
            var misses = new List<long>();
            if (tops.Count == 0) return misses;
            using (var tx = new Transaction(doc, "Horizun: framing hanger re-read (rolled back)"))
            {
                if (tx.Start() != TransactionStatus.Started) { maxGapMm = double.PositiveInfinity; return tops.Select(h => h.Id).ToList(); }
                try
                {
                    var views = new Dictionary<long, View3D>();
                    var filter = new ElementMulticategoryFilter(SupportCategories.Concat(new[] { BuiltInCategory.OST_RvtLinks }).ToList());
                    foreach (HangerTop h in tops)
                    {
                        long pk = h.Phase == null ? -1 : Rid.Value(h.Phase);
                        if (!views.TryGetValue(pk, out View3D view)) views[pk] = view = CeilingRayView(doc, h.Phase);
                        var ray = new ReferenceIntersector(filter, FindReferenceTarget.Face, view) { FindReferencesInRevitLinks = true };
                        // From below the top end, so the support face is a hit at a known distance
                        // rather than the ray's own origin (planning skips a zero-proximity hit).
                        double back = Math.Min(50.0, h.LengthMm / 2) / MmPerFt;
                        double d = SupportAbove(doc, ray, h.Top - new XYZ(0, 0, back), back + 100 / MmPerFt, out string support);
                        double gap = support == null ? double.PositiveInfinity : Math.Abs(d - back) * MmPerFt;
                        maxGapMm = Math.Max(maxGapMm, gap);
                        if (gap > EndpointToleranceMm) misses.Add(h.Id);
                    }
                }
                finally
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                }
            }
            return misses;
        }

        private static JObject CeilingSummary(List<FramingSourcePlan> plans)
        {
            var rows = new JArray();
            int listed = 0;
            foreach (FramingSourcePlan p in plans)
            {
                FramedCeiling fc = p.Ceiling;
                var members = new JArray();
                for (int i = 0; i < p.Members.Count && listed < SummaryMemberCap; i++, listed++)
                {
                    FramingMember m = p.Members[i];
                    members.Add(new JObject
                    {
                        ["i"] = i, ["role"] = m.Role, ["type_id"] = long.Parse(m.TypeKey, CultureInfo.InvariantCulture),
                        ["from"] = new JArray(Math.Round(m.X0, 1), Math.Round(m.Y0, 1), Math.Round(m.Z0, 1)),
                        ["to"] = new JArray(Math.Round(m.X1, 1), Math.Round(m.Y1, 1), Math.Round(m.Z1, 1))
                    });
                }
                var counts = new JObject();
                foreach (KeyValuePair<string, int> kv in FramingPlanSignature.CountByRole(p.Members).OrderBy(k => k.Key, StringComparer.Ordinal)) counts[kv.Key] = kv.Value;
                rows.Add(new JObject
                {
                    ["source_id"] = Rid.Value(p.Source.Id), ["status"] = p.AlreadyApplied ? "already_applied" : "planned",
                    ["top_face_mm"] = Math.Round(fc.TopMm, 1), ["loops"] = fc.LoopsMm.Count, ["opening_holes"] = new JArray(fc.HoleIds),
                    ["z_mm"] = new JObject { ["main_axis"] = Math.Round(fc.MainZMm, 1), ["cross_axis"] = Math.Round(fc.CrossZMm, 1), ["perimeter_axis"] = Math.Round(fc.PerimeterZMm, 1), ["hanger_from"] = Math.Round(fc.HangerFromMm, 1) },
                    ["count_by_role"] = counts, ["member_count"] = p.Members.Count, ["no_support_above"] = fc.NoSupportAbove,
                    ["plan_signature"] = p.Signature, ["spec_hash"] = p.SpecHash,
                    ["warnings"] = new JArray(p.Warnings.ToArray()),
                    ["members_frame"] = "model x, y, z in mm",
                    ["members"] = members
                });
            }
            return new JObject
            {
                ["sources"] = rows, ["member_count"] = plans.Sum(p => p.Members.Count),
                ["members_listed"] = listed, ["truncated"] = listed < plans.Sum(p => p.Members.Count)
            };
        }
    }
}
