// -----------------------------------------------------------------------------
// Horizun Revit MCP - turning a requirement set into resolved, measured rows.
// Original Horizun code.
//
// ONE resolver, used by the read-only plan and by the write. If the plan resolved
// hosts one way and the apply another, the rehearsal a person read would not be
// the thing that ran - which is the only failure mode a rehearsal exists to
// prevent.
//
// Every refusal here happens BEFORE a transaction is opened, and every one names
// a code from a closed set. The rule that shapes all of them: where two readings
// are possible, this returns `review` and writes nothing. A type name that
// matches two types in the model is not a tie to be broken by whichever Revit
// returned first - that is a coin toss recorded as a decision.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class ResolvedCoverRow
    {
        public StructuralCoverRule Rule;
        public Element Host;
        public RebarCoverType CoverType;
        public double? CurrentDistanceMm;
        /// <summary>False when Revit would not answer at all - which is not the same as a host with mixed faces.</summary>
        public bool CurrentReadable = true;
        public double? WantedDistanceMm;
        public string Code;
        public string Why;
        public bool Ok { get { return Code == null; } }
        public bool AlreadyRight;
        /// <summary>
        /// A per-face rule (L7): the exposed faces whose outward normal is the declared one, each
        /// with the cover type it carries now. Empty for the common cover.
        /// </summary>
        public List<ReinforcementResolver.FaceCover> Faces = new List<ReinforcementResolver.FaceCover>();
    }

    public sealed class ResolvedRebarRow
    {
        public StructuralRebarRule Rule;
        public Element Host;
        public RebarBarType BarType;
        public RebarShape Shape;
        public ElementId StartHookId = ElementId.InvalidElementId;
        public ElementId EndHookId = ElementId.InvalidElementId;
        public List<Curve> Curves = new List<Curve>();
        public List<double[]> PointsMm = new List<double[]>();
        public XYZ Normal;
        public RebarLayoutPlan Layout;
        public RebarFitVerdict Fit;
        /// <summary>The host's own boundary, triangulated. Null when Revit would not give it.</summary>
        public HostMesh Mesh;
        /// <summary>Why there is no mesh, when there is none. Never silence.</summary>
        public string MeshWhy;
        /// <summary>Where the DECLARED bars fall against that boundary - the plan's answer.</summary>
        public SetContainment Containment;
        public double ExpectedBarLengthMm;
        /// <summary>The plan's positions with the side applied - negative when the set marches the other way.</summary>
        public List<double> SignedPositionsMm = new List<double>();
        /// <summary>Ids the rule NAMED that resolved to no element. Reported, never dropped.</summary>
        public List<long> UnresolvedHostIds = new List<long>();
        /// <summary>What the bars were measured against: the host, or the union with into (L1).</summary>
        public ReinforcementResolver.ContainmentContext Context;
        /// <summary>The declared anchorages, measured on the declared centreline (L1).</summary>
        public List<AnchorageResult> Anchorage = new List<AnchorageResult>();
        /// <summary>
        /// A placed hoop or spiral (L7): what the shape's definition was read as, and the
        /// diameter or radius constraint it carries - the parameter, how it measures - when it
        /// carries one. Read before anything is built; never inferred from the shape's name.
        /// </summary>
        public string PlacedDefinition;
        public string PlacedSizeConstraint;
        public string PlacedSizeReference;
        public ElementId PlacedSizeParamId = ElementId.InvalidElementId;
        /// <summary>The coupler type joining this piece to the next (L7), resolved by name. Null otherwise.</summary>
        public ElementType CouplerType;
        /// <summary>The sets this one clashes with (overlapping_bar), when that is why it was refused. Null otherwise.</summary>
        public JArray Overlaps;
        /// <summary>
        /// The displacement a declared crossing_order made of this set (etapa 1c), or null. When set, PointsMm,
        /// Curves and Rule carry the DISPLACED centreline - what is built and verified - and DeclaredPointsMm the
        /// one the set declared.
        /// </summary>
        public CrossingMove Crossing;
        public List<double[]> DeclaredPointsMm;
        public string Code;
        public string Why;
        public bool Ok { get { return Code == null; } }
    }

    public static class ReinforcementResolver
    {
        public const double FtToMm = 304.8;
        public const double MmToFt = 1.0 / 304.8;

        // Closed refusal set.
        public const string CodeHostNotFound = "host_not_found";
        public const string CodeHostIneligible = "host_ineligible";
        public const string CodeHostNotMeasured = "host_extent_not_measured";
        public const string CodeBarTypeNotFound = "bar_type_not_found";
        public const string CodeBarTypeAmbiguous = "bar_type_ambiguous";
        public const string CodeShapeNotFound = "shape_not_found";
        public const string CodeShapeAmbiguous = "shape_ambiguous";
        public const string CodeHookNotFound = "hook_type_not_found";
        public const string CodeHookAmbiguous = "hook_type_ambiguous";
        public const string CodeCoverTypeNotFound = "cover_type_not_found";
        public const string CodeCoverTypeAmbiguous = "cover_type_ambiguous";
        public const string CodeCurveNotPlanar = "curve_not_planar";
        public const string CodeCurveOffNormalPlane = "curve_off_its_normal_plane";
        /// <summary>How far a declared point may be moved onto the plane its normal declares: rounding noise only.</summary>
        public const double NormalPlaneSquaringMm = 0.01;
        public const string CodeBarOutsideSolid = "bar_outside_host_solid";
        public const string CodeBarPartlyOutsideSolid = "bar_partly_outside_host_solid";
        public const string CodeZoneRuleRefused = "stirrup_zone_rule_refused";
        public const string CodeShortOfDeclaredCover = "bar_short_of_the_declared_cover";
        public const string CodeContainmentNotEvaluable = "containment_not_evaluable";
        public const string CodeMatRuleRefused = "mat_rule_refused";
        public const string CodeSectionRuleRefused = "section_rule_refused";
        public const string CodeWallBoundaryRuleRefused = "wall_boundary_rule_refused";
        public const string CodeCurveDegenerate = "curve_degenerate";
        public const string CodeLayoutRefused = "layout_refused";
        public const string CodeShapeNotDeclared = "shape_not_declared_and_new_shapes_not_allowed";
        public const string CodeShapeStyleDiffers = "shape_style_differs_from_declared_style";
        public const string CodeShapeNotAllowed = "shape_not_allowed_for_this_bar_type";
        public const string CodeHostUnreadable = "host_eligibility_unreadable";
        public const string CodeShapeAllowanceUnreadable = "shape_allowance_unreadable";
        public const string CodeShapeStyleUnreadable = "shape_style_unreadable";
        public const string CodeDiameterUnreadable = "bar_diameter_unreadable";
        public const string CodeCoverDistanceUnreadable = "cover_distance_unreadable";
        public const string CodeNormalInThePlaneOfTheBar = "normal_lies_in_the_plane_of_the_bar";
        public const string CodeAlreadyBuilt = "this_rule_already_built_a_set_in_this_host";
        public const string CodeArcChainNotBuildable = "arc_chain_not_buildable_from_curves";
        public const string CodeHostNotStructural = "host_not_structural";
        public const string CodeStairNotHost = "stair_not_a_rebar_host";

        private static bool IsStair(Element e)
        {
            try
            {
                long c = e?.Category == null ? 0 : Rid.Value(e.Category.Id);
                return c == (long)BuiltInCategory.OST_Stairs || c == (long)BuiltInCategory.OST_StairsRuns ||
                       c == (long)BuiltInCategory.OST_StairsLandings;
            }
            catch { return false; }
        }
        public const string CodeCouplerTypeNotFound = "coupler_type_not_found";
        public const string CodeCouplerTypeAmbiguous = "coupler_type_ambiguous";

        /// <summary>Every code this resolver can emit. Published so the contract cannot drift from it.</summary>
        public static readonly string[] AllCodes =
        {
            CodeHostNotFound, CodeHostIneligible, CodeHostNotMeasured, CodeHostUnreadable,
            CodeBarTypeNotFound, CodeBarTypeAmbiguous, CodeShapeNotFound, CodeShapeAmbiguous,
            CodeHookNotFound, CodeHookAmbiguous, CodeCoverTypeNotFound, CodeCoverTypeAmbiguous,
            CodeCoverDistanceUnreadable, CodeCurveNotPlanar, CodeCurveOffNormalPlane, CodeCurveDegenerate, CodeLayoutRefused,
            CodeShapeNotDeclared, CodeShapeStyleDiffers, CodeShapeNotAllowed, CodeShapeAllowanceUnreadable,
            CodeShapeStyleUnreadable, CodeDiameterUnreadable, CodeNormalInThePlaneOfTheBar,
            CodeAlreadyBuilt, CodeArcChainNotBuildable, CodeWallBoundaryRuleRefused, CodeHostNotStructural, CodeStairNotHost, "host_in_model_group",
            PlacedShapes.CodeNotACircle, PlacedShapes.CodeNotASpiral, PlacedShapes.CodeDefinitionUnreadable,
            CodeCouplerTypeNotFound, CodeCouplerTypeAmbiguous, CoverFaceRules.CodeFaceNotFound,
            "into_element_not_found", "into_names_the_host", "into_needs_a_single_host", "into_union_not_measurable",
            "anchorage_short", "anchorage_not_measured",
            RebarPlanRules.CodeSetOutsideHost, RebarPlanRules.CodeBarOutsideHost,
            RebarPlanRules.CodeHostNotMeasured, RebarPlanRules.CodeBarNotMeasured,
            RebarPlanRules.CodeNormalDegenerate,
            RebarOverlap.CodeIntersect, RebarOverlap.CodeTooClose, RebarOverlap.CodeNotEvaluable, RebarOverlap.CodeDuplicate,
            RebarCrossing.CodeAmbiguous, RebarCrossing.CodeConflict, RebarCrossing.CodeNotApplicable, RebarCrossing.CodeBreaksCover
        };

        /// <summary>
        /// BARS OF DIFFERENT RULES, against each other (overlapping_bar), before anything is written: every
        /// buildable row, as DescribeRebarRow publishes it, held to the set's declared clearances - or, with
        /// none declared, to not passing through one another. Revit moves a bar that crosses another until
        /// the two touch and says nothing (measured, 2024 and 2026), so a clash is refused HERE, on both sets,
        /// by the plan and by the apply alike. The audit runs the same check on the bars the model carries.
        /// </summary>
        public static OverlapReport RefuseOverlaps(List<ResolvedRebarRow> rows, StructuralRequirementSet set)
        {
            OverlapReport report = RebarOverlap.Check(MembersOf(rows), set.Clearances, set.Tolerances.LengthMm);
            foreach (OverlapMember u in report.Unreadable)
            {
                rows[u.Tag].Code = RebarOverlap.CodeNotEvaluable;
                rows[u.Tag].Why = "whether this set's bars clear the other rules' bars could not be measured: " + u.UnreadableWhy +
                                  " Unknown is not a pass.";
            }
            foreach (OverlapRefusal r in RebarOverlap.Refusals(report))
            {
                rows[r.Tag].Code = r.Code;
                rows[r.Tag].Why = r.Why + (rows[r.Tag].Crossing == null ? "" :
                    " This set had been displaced by the declared crossing_order (" + rows[r.Tag].Crossing.Describe() + "), and the " +
                    "displaced bars still clash: the displacement is not enough, or another set crosses it. Declare another offset or order.");
                rows[r.Tag].Overlaps = r.Overlaps;
            }
            return report;
        }

        /// <summary>Every buildable row as the overlap check reads it, tagged with its index, with its host's centre (crossing_order).</summary>
        public static List<OverlapMember> MembersOf(List<ResolvedRebarRow> rows)
        {
            var members = new List<OverlapMember>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].Ok) continue;
                OverlapMember m = RebarOverlap.FromPlanRow(DescribeRebarRow(rows[i], i));
                m.Tag = i;
                List<double[]> corners = rows[i].Host == null ? null : HostCorners(rows[i].Host);
                if (corners != null && corners.Count > 0)
                    m.HostCentreMm = new[] { corners.Average(c => c[0]), corners.Average(c => c[1]), corners.Average(c => c[2]) };
                members.Add(m);
            }
            return members;
        }

        /// <summary>
        /// CROSSING ORDER (etapa 1c). Resolve every rule; with a declared crossing_order, measure the declared
        /// centrelines against each other (the overlap check), decide which sets give way and how (RebarCrossing),
        /// and resolve AGAIN with those sets displaced - so containment, cover, the fit and the anchorage are measured
        /// on the displaced bars, and the caller's RefuseOverlaps measures the RESULT. The plan, the apply and the
        /// audit all call this, so they decide the same moves. Without a crossing_order it is ResolveRebar.
        /// </summary>
        public static List<ResolvedRebarRow> ResolveRebarCrossed(Document doc, StructuralRequirementSet set, IList<long> narrowTo,
                                                                 bool refuseAlreadyBuilt, out CrossingDecision decision)
        {
            List<ResolvedRebarRow> rows = ResolveRebar(doc, set, narrowTo, refuseAlreadyBuilt);
            decision = null;
            if (set.CrossingOrder == null) return rows;
            OverlapReport declared = RebarOverlap.Check(MembersOf(rows), set.Clearances, set.Tolerances.LengthMm);
            decision = RebarCrossing.Decide(declared, set.CrossingOrder, set.Tolerances.LengthMm);
            if (decision.Moves.Count == 0 && decision.Refused.Count == 0) return rows;
            return ResolveRebar(doc, set, narrowTo, refuseAlreadyBuilt, decision);
        }

        /// <summary>The reply's crossing_order block: what was decided, and what became of each displaced row.</summary>
        public static JObject DescribeCrossing(CrossingDecision d, List<ResolvedRebarRow> rows)
        {
            if (d == null)
                return new JObject
                {
                    ["declared"] = false,
                    ["means"] = "the set declares no crossing_order: bars of different rules that pass through each other are refused " +
                                "(bars_intersect), both sets, and nothing is displaced."
                };
            JObject o = d.ToJson();
            foreach (JObject m in ((JArray)o["displacements"]).OfType<JObject>())
            {
                string key = CrossingDecision.Key((string)m["rule_id"], (long)m["host_id"]);
                int at = rows.FindIndex(r => r.Crossing != null && r.Host != null &&
                                             CrossingDecision.Key(r.Rule.Id, Rid.Value(r.Host.Id)) == key);
                m["index"] = at;
                m["will_build"] = at >= 0 && rows[at].Ok;
                if (at >= 0 && !rows[at].Ok) m["code"] = rows[at].Code;
            }
            return o;
        }

        // ------------------------------------------------------------- hosts

        /// <summary>
        /// Every element a selector picks. A selector matching MANY hosts is not
        /// ambiguity - a rule applies to every beam of a type on purpose. Ambiguity
        /// is a NAME resolving to two definitions, and that is refused elsewhere.
        /// </summary>
        /// <summary>A zone or mat rule whose hosts all lie outside host_ids: skipped, not refused.</summary>
        internal const string OutsideHostIds = "\u0000outside_host_ids";

        /// <summary>
        /// What host_ids does to a rule (backlog P0 #7): it only intersects. Counted in the
        /// whole document only when the narrowed set came back empty, so the common case
        /// pays nothing extra.
        /// </summary>
        internal static HostNarrowing.Outcome Scope(Document doc, StructuralHostSelector sel, IList<long> narrowTo, int matchedInHostIds)
        {
            bool narrowed = narrowTo != null && narrowTo.Count > 0;
            int inDocument = matchedInHostIds > 0 || !narrowed ? matchedInHostIds : Hosts(doc, sel, null).Count;
            return HostNarrowing.Decide(inDocument, matchedInHostIds, narrowed);
        }

        public static List<Element> Hosts(Document doc, StructuralHostSelector sel, IList<long> narrowTo)
        {
            List<long> ignored;
            return Hosts(doc, sel, narrowTo, out ignored);
        }

        /// <summary>
        /// Every element a selector picks, and - separately - every element id it
        /// NAMED that resolved to nothing.
        ///
        /// Those ids used to be dropped in silence: a rule naming three beams, one
        /// of them since deleted, built two sets, reported success, and never
        /// mentioned the third. Only a selector resolving to NOTHING was reported.
        /// </summary>
        public static List<Element> Hosts(Document doc, StructuralHostSelector sel, IList<long> narrowTo,
                                          out List<long> unresolvedIds)
        {
            unresolvedIds = new List<long>();
            var found = new List<Element>();
            if (sel.ElementIds.Count > 0)
            {
                // A HOST NAMED TWICE IS ONE HOST. A repeated id used to produce two
                // identical rows, and the apply built the steel twice into the same
                // beam.
                var seen = new HashSet<long>();
                foreach (long id in sel.ElementIds)
                {
                    if (!seen.Add(id)) continue;
                    if (!Rid.CanRepresent(id)) { unresolvedIds.Add(id); continue; }
                    Element e = doc.GetElement(Rid.Make(id));
                    if (e != null) found.Add(e); else unresolvedIds.Add(id);
                }
            }
            else
            {
                var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                if (!string.IsNullOrWhiteSpace(sel.Category))
                {
                    BuiltInCategory bic;
                    if (Enum.TryParse(sel.Category, out bic))
                        collector = collector.OfCategory(bic);
                }
                found = collector.ToList();
                if (!string.IsNullOrWhiteSpace(sel.Category))
                {
                    BuiltInCategory bic;
                    if (!Enum.TryParse(sel.Category, out bic)) return new List<Element>();
                }
            }

            if (!string.IsNullOrWhiteSpace(sel.TypeName))
                found = found.Where(e =>
                {
                    ElementType t = doc.GetElement(e.GetTypeId()) as ElementType;
                    return t != null && string.Equals(t.Name, sel.TypeName, StringComparison.Ordinal);
                }).ToList();

            if (!string.IsNullOrWhiteSpace(sel.Mark))
                found = found.Where(e => string.Equals(MarkOf(e), sel.Mark, StringComparison.Ordinal)).ToList();
            if (!string.IsNullOrWhiteSpace(sel.Level))
                found = found.Where(e => string.Equals(LevelNameOf(doc, e), sel.Level, StringComparison.Ordinal)).ToList();

            if (narrowTo != null && narrowTo.Count > 0)
                found = found.Where(e => narrowTo.Contains(Rid.Value(e.Id))).ToList();

            // Deterministic. "Whatever Revit returned first" is not an order.
            return found.OrderBy(e => Rid.Value(e.Id)).ToList();
        }

        /// <summary>The element's Mark, exactly as stored, or null.</summary>
        internal static string MarkOf(Element e)
        {
            try { return e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(); } catch { return null; }
        }

        /// <summary>
        /// The level an element is on: its own LevelId, else the reference level a beam
        /// carries, else a column's base level. Null when none can be read.
        /// </summary>
        internal static string LevelNameOf(Document doc, Element e)
        {
            try
            {
                ElementId id = e.LevelId;
                if (id == null || id == ElementId.InvalidElementId)
                {
                    Parameter p = e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)
                                  ?? e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
                    id = p?.AsElementId();
                }
                return id == null || id == ElementId.InvalidElementId ? null : (doc.GetElement(id) as Level)?.Name;
            }
            catch { return null; }
        }

        // ------------------------------------------------------- name lookups

        /// <summary>
        /// The one element type with this name, or a refusal. TWO types with one
        /// name is a review, never a choice: picking the first is a coin toss
        /// recorded as a decision, and the bar that comes out has a diameter
        /// nobody chose.
        /// </summary>
        public static T ByName<T>(Document doc, string name, out int matches) where T : Element
        {
            matches = 0;
            if (string.IsNullOrWhiteSpace(name)) return null;
            var all = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
                .Where(t => string.Equals(SafeName(t), name, StringComparison.Ordinal))
                .OrderBy(t => Rid.Value(t.Id)).ToList();
            matches = all.Count;
            return all.Count == 1 ? all[0] : null;
        }

        private static string SafeName(Element e)
        {
            try { return e.Name; } catch { return null; }
        }

        // ------------------------------------------------------------- cover

        public static List<ResolvedCoverRow> ResolveCover(Document doc, StructuralRequirementSet set,
                                                          IList<long> narrowTo)
        {
            var rows = new List<ResolvedCoverRow>();
            foreach (StructuralCoverRule rule in set.CoverRules)
            {
                List<Element> hosts = Hosts(doc, rule.Host, narrowTo);
                if (Scope(doc, rule.Host, narrowTo, hosts.Count) == HostNarrowing.Outcome.SkipOutsideHostIds) continue;
                if (hosts.Count == 0)
                {
                    rows.Add(new ResolvedCoverRow
                    {
                        Rule = rule,
                        Code = CodeHostNotFound,
                        Why = "no element in this document matches the host selector of cover rule '" + rule.Id + "'."
                    });
                    continue;
                }
                foreach (Element host in hosts)
                {
                    var row = new ResolvedCoverRow { Rule = rule, Host = host };
                    bool eligible;
                    string eligibilityError = Eligible(host, out eligible);
                    if (eligibilityError != null)
                    {
                        row.Code = CodeHostUnreadable;
                        row.Why = eligibilityError;
                        rows.Add(row);
                        continue;
                    }
                    if (!eligible)
                    {
                        row.Code = CodeHostIneligible;
                        row.Why = "Revit does not accept element " + Rid.Value(host.Id) +
                                  " as a reinforcement host, so it can carry no cover.";
                        rows.Add(row);
                        continue;
                    }

                    RebarCoverType wanted = null;
                    if (!string.IsNullOrWhiteSpace(rule.CoverTypeName))
                    {
                        int matches;
                        wanted = ByName<RebarCoverType>(doc, rule.CoverTypeName, out matches);
                        if (wanted == null)
                        {
                            row.Code = matches > 1 ? CodeCoverTypeAmbiguous : CodeCoverTypeNotFound;
                            row.Why = matches > 1
                                ? matches + " cover types in this document are named '" + rule.CoverTypeName +
                                  "'. Which one is meant cannot be read from the set, and picking one would be a " +
                                  "cover nobody chose."
                                : "no cover type in this document is named '" + rule.CoverTypeName +
                                  "'. This bridge does not create one: a cover distance is a design decision.";
                            rows.Add(row);
                            continue;
                        }
                    }
                    else
                    {
                        // A DISTANCE with no name: find the cover type that already
                        // carries it. Creating one would be inventing a name for
                        // somebody's standard, and two cover types at the same
                        // distance is a real condition in real models.
                        var byDistance = new FilteredElementCollector(doc).OfClass(typeof(RebarCoverType))
                            .Cast<RebarCoverType>()
                            .Where(t =>
                            {
                                double? d = SafeCover(t);
                                return d.HasValue &&
                                       Math.Abs(d.Value * FtToMm - rule.DistanceMm.Value) <= set.Tolerances.CoverMm;
                            })
                            .OrderBy(t => Rid.Value(t.Id)).ToList();
                        if (byDistance.Count != 1)
                        {
                            row.Code = byDistance.Count > 1 ? CodeCoverTypeAmbiguous : CodeCoverTypeNotFound;
                            row.Why = byDistance.Count > 1
                                ? byDistance.Count + " cover types measure " + Mm(rule.DistanceMm.Value) +
                                  " within tolerance, so naming one by its distance does not identify it. " +
                                  "Declare cover_type_name."
                                : "no cover type in this document measures " + Mm(rule.DistanceMm.Value) +
                                  " within " + Mm(set.Tolerances.CoverMm) + ". This bridge does not create cover " +
                                  "types, because the name and the distance are both somebody's standard.";
                            rows.Add(row);
                            continue;
                        }
                        wanted = byDistance[0];
                    }

                    row.CoverType = wanted;
                    double? wantedFt = SafeCover(wanted);
                    if (!wantedFt.HasValue)
                    {
                        row.Code = CodeCoverDistanceUnreadable;
                        row.Why = "cover type '" + SafeName(wanted) + "' would not report its distance, so whether " +
                                  "it is the cover this rule declares is UNKNOWN. Unknown is not agreement.";
                        rows.Add(row);
                        continue;
                    }
                    row.WantedDistanceMm = wantedFt.Value * FtToMm;
                    if (rule.DistanceMm.HasValue &&
                        Math.Abs(row.WantedDistanceMm.Value - rule.DistanceMm.Value) > set.Tolerances.CoverMm)
                    {
                        row.Code = CodeCoverTypeNotFound;
                        row.Why = "cover type '" + SafeName(wanted) + "' measures " + Mm(row.WantedDistanceMm.Value) +
                                  " and the rule declares " + Mm(rule.DistanceMm.Value) +
                                  ". The name and the number disagree; this bridge does not change a cover type's " +
                                  "distance, because that would change every host already using it.";
                        rows.Add(row);
                        continue;
                    }

                    // ONE FACE, NAMED BY ITS OUTWARD NORMAL (L7): every exposed face pointing that way,
                    // each with the cover it carries now. None is a refusal that lists what was found.
                    if (rule.Face == StructuralCoverRule.FaceNormal)
                    {
                        List<double[]> seen;
                        string fwhy;
                        List<FaceCover> faces = CoverFaces(host, rule.FaceNormalMm, set.Tolerances.AngleDegrees, out seen, out fwhy);
                        if (faces == null)
                        {
                            row.Code = CodeHostIneligible;
                            row.Why = fwhy;
                        }
                        else if (faces.Count == 0)
                        {
                            row.Code = CoverFaceRules.CodeFaceNotFound;
                            row.Why = "element " + Rid.Value(host.Id) + " has no exposed planar face whose outward normal is within " +
                                      set.Tolerances.AngleDegrees.ToString("0.###", CultureInfo.InvariantCulture) + " degrees of [" +
                                      string.Join(", ", rule.FaceNormalMm.Select(v => Math.Round(v, 4).ToString(CultureInfo.InvariantCulture))) +
                                      "]. Its exposed planar faces point along: " +
                                      (seen.Count == 0 ? "none" : string.Join("; ", seen.Select(n => "[" + string.Join(", ",
                                          n.Select(v => Math.Round(v, 4).ToString(CultureInfo.InvariantCulture))) + "]"))) + ".";
                        }
                        else
                        {
                            row.Faces = faces;
                            row.CurrentReadable = faces.All(f => f.CurrentReadable);
                            row.AlreadyRight = faces.All(f => f.Current != null && f.Current == wanted.Id);
                            var distinct = faces.Select(f => f.Current == null ? -1 : Rid.Value(f.Current)).Distinct().ToList();
                            if (row.CurrentReadable && distinct.Count == 1 && distinct[0] >= 0)
                            {
                                double? d = SafeCover(doc.GetElement(faces[0].Current) as RebarCoverType);
                                row.CurrentDistanceMm = d.HasValue ? (double?)(d.Value * FtToMm) : null;
                            }
                        }
                        rows.Add(row);
                        continue;
                    }

                    RebarHostData data = null;
                    try { data = RebarHostData.GetRebarHostData(host); } catch { }
                    if (data == null)
                    {
                        row.Code = CodeHostIneligible;
                        row.Why = "the element is a valid host and Revit would not return its host data.";
                        rows.Add(row);
                        continue;
                    }
                    using (data)
                    {
                        RebarCoverType current = null;
                        bool commonReadable = true;
                        try { current = data.GetCommonCoverType(); } catch { commonReadable = false; }
                        double? cur = current == null ? null : SafeCover(current);
                        row.CurrentDistanceMm = cur.HasValue ? (double?)(cur.Value * FtToMm) : null;
                        row.CurrentReadable = commonReadable;
                        row.AlreadyRight = current != null && current.Id == wanted.Id;
                    }
                    rows.Add(row);
                }
            }
            return rows;
        }

        /// <summary>
        /// The cover distance, or NULL when it could not be read.
        ///
        /// It used to return NaN, and every comparison against NaN is false - so the
        /// "the name and the number disagree" guard silently passed, the distance
        /// search silently excluded the type, and Math.Round(NaN) reached the reply
        /// as a JSON NaN literal, which is not JSON.
        /// </summary>
        private static double? SafeCover(RebarCoverType t)
        {
            if (t == null) return null;
            try
            {
                double v = t.CoverDistance;
                return RebarLayoutRules.IsFinite(v) ? (double?)v : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------- rebar

        public static List<ResolvedRebarRow> ResolveRebar(Document doc, StructuralRequirementSet set,
                                                          IList<long> narrowTo)
        {
            return ResolveRebar(doc, set, narrowTo, refuseAlreadyBuilt: false);
        }

        /// <summary>
        /// Resolve every rule against the model.
        ///
        /// `refuseAlreadyBuilt` IS THE WRITE PATH'S QUESTION, AND ONLY ITS QUESTION.
        /// A rule whose bars already stand must stop an APPLY - otherwise a second
        /// deliberate run puts a second coincident cage in the same beam, and the
        /// idempotency ledger cannot see it because a fresh rehearsal is not a retry.
        /// But the AUDIT re-resolves exactly those rules on purpose: existing bars
        /// are what it came to check. Measured live: with the guard inside the
        /// resolver, auditing a set that had been built reported every one of its own
        /// bars as `rule_built_nothing` - the audit accusing the model of missing the
        /// reinforcement it was looking straight at.
        /// </summary>
        public static List<ResolvedRebarRow> ResolveRebar(Document doc, StructuralRequirementSet set,
                                                          IList<long> narrowTo, bool refuseAlreadyBuilt)
        {
            return ResolveRebar(doc, set, narrowTo, refuseAlreadyBuilt, null);
        }

        /// <summary>The same, with a crossing_order's decision applied: see ResolveRebarCrossed.</summary>
        public static List<ResolvedRebarRow> ResolveRebar(Document doc, StructuralRequirementSet set,
                                                          IList<long> narrowTo, bool refuseAlreadyBuilt, CrossingDecision crossing)
        {
            var rows = new List<ResolvedRebarRow>();
            Dictionary<long, StructuralProvenance> already = refuseAlreadyBuilt
                ? StructuralProvenanceStore.Index(doc)
                : new Dictionary<long, StructuralProvenance>();

            // STIRRUP ZONES ARE NOT A FOURTH KIND OF THING. They expand here into
            // ordinary reinforcement rules, one per zone, before anything else runs -
            // so containment, the point-by-point audit, provenance and idempotency
            // all apply to a zone without knowing that zones exist. The expansion is
            // deterministic, so the plan, the apply and the audit all produce the
            // same rule ids and the audit can find what the apply wrote.
            var effective = new List<StructuralRebarRule>(set.RebarRules);

            // SECTION RULES EXPAND FIRST, per host, into ordinary rules and - for their
            // stirrups - into stirrup zone rules, which then expand below like any other.
            var zoneRulesToExpand = new List<StructuralStirrupZoneRule>(set.StirrupZoneRules);
            foreach (StructuralSectionRule sr in set.SectionRules)
            {
                List<StructuralRebarRule> secBars;
                List<StructuralStirrupZoneRule> secZones;
                rows.AddRange(ExpandSectionRule(doc, set, sr, narrowTo, out secBars, out secZones));
                effective.AddRange(secBars);
                zoneRulesToExpand.AddRange(secZones);
            }

            // WALL BOUNDARY ELEMENTS ARE SECTION RULES over a wall's end region (acero 3.0, L5),
            // expanded here for the same reason and in the same place: their stirrups join the
            // zone rules below like any other.
            foreach (StructuralWallBoundaryRule wr in set.WallBoundaryRules)
            {
                List<StructuralRebarRule> wallBars;
                List<StructuralStirrupZoneRule> wallZones;
                rows.AddRange(ExpandWallBoundaryRule(doc, set, wr, narrowTo, out wallBars, out wallZones));
                effective.AddRange(wallBars);
                zoneRulesToExpand.AddRange(wallZones);
            }

            foreach (StructuralStirrupZoneRule zr in zoneRulesToExpand)
            {
                List<StructuralRebarRule> zoneRules;
                string zoneWhy = ExpandZoneRule(doc, set, zr, narrowTo, out zoneRules);
                if (zoneWhy == OutsideHostIds) continue;
                if (zoneWhy != null)
                {
                    rows.Add(RefuseZone(zr, zoneWhy));
                    continue;
                }
                effective.AddRange(zoneRules);
            }

            // MATS EXPAND THE SAME WAY, and for the same reason: a component is an
            // ordinary reinforcement rule once its centreline has been worked out
            // from the host's own boundary.
            foreach (StructuralMatRule mr in set.MatRules)
            {
                List<StructuralRebarRule> matRules;
                string matWhy = ExpandMatRule(doc, set, mr, narrowTo, out matRules);
                if (matWhy == OutsideHostIds) continue;
                if (matWhy != null)
                {
                    rows.Add(RefuseMat(mr, matWhy));
                    continue;
                }
                effective.AddRange(matRules);
            }

            foreach (StructuralRebarRule rule in effective)
            {
                // Types are resolved ONCE per rule, not once per host: a name that is
                // ambiguous is ambiguous for every host, and repeating the refusal
                // per beam buries it.
                int matches;
                StructuralBarTypeRef btRef = set.BarTypes[rule.BarTypeId];
                RebarBarType barType = ByName<RebarBarType>(doc, btRef.TypeName, out matches);
                if (barType == null)
                {
                    rows.Add(Refuse(rule, matches > 1 ? CodeBarTypeAmbiguous : CodeBarTypeNotFound,
                        matches > 1
                            ? matches + " bar types are named '" + btRef.TypeName + "'. Which one is meant cannot " +
                              "be read from the set, and they carry different diameters."
                            : "no RebarBarType in this document is named '" + btRef.TypeName + "'. This bridge " +
                              "does not create bar types: a bar type carries a diameter, a bend radius and a " +
                              "grade, and inventing those is designing."));
                    continue;
                }

                RebarShape shape = null;
                if (!string.IsNullOrWhiteSpace(rule.ShapeName))
                {
                    shape = ByName<RebarShape>(doc, rule.ShapeName, out matches);
                    if (shape == null)
                    {
                        rows.Add(Refuse(rule, matches > 1 ? CodeShapeAmbiguous : CodeShapeNotFound,
                            matches > 1
                                ? matches + " rebar shapes are named '" + rule.ShapeName + "'."
                                : "no RebarShape in this document is named '" + rule.ShapeName +
                                  "'. Load it, or set allow_new_shape and let Revit create one - which adds a " +
                                  "shape family to the project, so it is declared rather than assumed."));
                        continue;
                    }
                }

                // THE STYLE THE SHAPE CARRIES, against the style the rule declares.
                // CreateFromCurvesAndShape takes the style FROM THE SHAPE and
                // ignores everything else, so a rule saying stirrup_tie beside a
                // Standard shape used to build a Standard bar in silence - and
                // nothing downstream compared the two, because the audit has no
                // style to compare against once the shape has decided it.
                if (shape != null)
                {
                    string shapeStyle = null;
                    string styleError = null;
                    try
                    {
                        shapeStyle = shape.RebarStyle == RebarStyle.StirrupTie
                            ? StructuralStyle.StirrupTie : StructuralStyle.Standard;
                    }
                    catch (Exception ex) { styleError = ex.Message; }
                    // AN UNREADABLE STYLE IS NOT AN AGREEING ONE. The guard used to
                    // short-circuit on null and let the row through, reinstating the
                    // very defect the comment above describes - and nothing
                    // downstream can catch it, because once the shape has decided the
                    // style there is nothing left to compare.
                    if (shapeStyle == null)
                    {
                        rows.Add(Refuse(rule, CodeShapeStyleUnreadable,
                            "shape '" + SafeName(shape) + "' would not report its style (" + styleError +
                            "), so whether it matches the declared style '" + rule.Style + "' is UNKNOWN. Revit " +
                            "takes the style FROM THE SHAPE, so building anyway would settle it silently."));
                        continue;
                    }
                    // A PLACED HOOP OR SPIRAL (L7) takes the style its shape carries, READ here and
                    // published: the section rule's stirrups never declared one for it.
                    if (rule.Placement != null) rule.Style = shapeStyle;
                    if (!string.Equals(shapeStyle, rule.Style, StringComparison.Ordinal))
                    {
                        rows.Add(Refuse(rule, CodeShapeStyleDiffers,
                            "rule '" + rule.Id + "' declares style '" + rule.Style + "' and shape '" +
                            SafeName(shape) + "' is a '" + shapeStyle + "' shape. Revit takes the style FROM THE " +
                            "SHAPE, so building this would quietly produce a " + shapeStyle + " bar. Change one " +
                            "of them; this bridge will not decide which was meant."));
                        continue;
                    }
                    // FALSE UNTIL REVIT SAYS OTHERWISE, which is the same default
                    // this file already uses for IsValidHost. It was true, so an
                    // exception read as Revit agreeing that the shape is allowed.
                    bool allowed = false;
                    string allowError = null;
                    try { allowed = shape.GetAllowed(barType); }
                    catch (Exception ex) { allowError = ex.Message; }
                    if (allowError != null)
                    {
                        rows.Add(Refuse(rule, CodeShapeAllowanceUnreadable,
                            "Revit would not say whether shape '" + SafeName(shape) + "' is allowed for bar type '" +
                            SafeName(barType) + "' (" + allowError + "). Unknown is not permission."));
                        continue;
                    }
                    if (!allowed)
                    {
                        rows.Add(Refuse(rule, CodeShapeNotAllowed,
                            "shape '" + SafeName(shape) + "' is not allowed for bar type '" + SafeName(barType) +
                            "'. Revit itself says so - a shape carries bend radii that a given diameter cannot " +
                            "achieve - and creating the bar anyway is how a cage comes out unbuildable."));
                        continue;
                    }
                }

                // A PLACED HOOP OR SPIRAL (L7): the shape's DEFINITION is read and judged against the
                // kind the rule declares, never its name. A template's M_T6 is not known to be a
                // lapped circle until its RebarShapeDefinitionByArc says so.
                string placedKind = null, sizeConstraint = null, sizeReference = null;
                ElementId sizeParam = ElementId.InvalidElementId;
                if (rule.Placement != null)
                {
                    if (shape == null)
                    {
                        rows.Add(Refuse(rule, CodeShapeNotFound, "rule '" + rule.Id + "' is placed from a shape and names none."));
                        continue;
                    }
                    ReadPlacedDefinition(shape, out placedKind, out sizeConstraint, out sizeReference, out sizeParam);
                    string[] refusal = PlacedShapes.DefinitionRefusal(rule.Placement.Kind, placedKind, SafeName(shape));
                    if (refusal != null) { rows.Add(Refuse(rule, refusal[0], refusal[1])); continue; }
                }

                ElementId startHook, endHook;
                string hookError = Hook(doc, set, rule.Start, out startHook);
                if (hookError != null) { rows.Add(Refuse(rule, CodeHookNotFound, hookError)); continue; }
                hookError = Hook(doc, set, rule.End, out endHook);
                if (hookError != null) { rows.Add(Refuse(rule, CodeHookNotFound, hookError)); continue; }

                // THE COUPLER TYPE a coupled splice names (L7), by name, in the coupler category. Two
                // with one name is a review, as for every other type here.
                ElementType couplerType = null;
                if (rule.CouplerNextTypeName != null)
                {
                    List<ElementType> found = CouplerTypes(doc, rule.CouplerNextTypeName);
                    if (found.Count != 1)
                    {
                        rows.Add(Refuse(rule, found.Count > 1 ? CodeCouplerTypeAmbiguous : CodeCouplerTypeNotFound,
                            found.Count > 1
                                ? found.Count + " coupler types are named '" + rule.CouplerNextTypeName + "'. Which one is meant cannot " +
                                  "be read from the set."
                                : "no coupler type (category Rebar Couplers) in this document is named '" + rule.CouplerNextTypeName +
                                  "'. This bridge does not load coupler families; load it, then name it."));
                        continue;
                    }
                    couplerType = found[0];
                }

                // Geometry, resolved once: it is declared in model coordinates and
                // does not depend on which host it lands in.
                // A CLOSED CHAIN WITH AN ARC (L2, narrowed in etapa 1b). MEASURED on Revit 2024: a closed
                // circle of arcs fails inside Rebar.CreateFromCurves with "An internal error has occurred",
                // whatever the style - refused here, by name, before a transaction rolls back over it. An
                // OPEN chain goes to Revit: the anonymized regression case's hoops (three arcs round and on over the lap, 135
                // degree hooks) were built that way on Revit 2024, and what Revit builds is re-read.
                string arcChain = rule.Placement == null && rule.ArcThrough != null
                    ? RebarArcs.ChainRefusal(rule.Closed, rule.Closed ? rule.CurvesMm.Count : rule.CurvesMm.Count - 1, rule.ArcThrough.Count)
                    : null;
                if (arcChain != null)
                {
                    rows.Add(Refuse(rule, CodeArcChainNotBuildable, "rule '" + rule.Id + "' is " + arcChain));
                    continue;
                }
                // THE PATH EVERY MEASUREMENT USES (L2): the declared vertices with every arc sampled.
                // Revit is asked for the true arcs (BuildCurves); nothing measures a polygon instead.
                List<double[]> geom = RebarArcs.Path(rule.CurvesMm, rule.Closed, rule.ArcThrough, RebarArcs.DefaultChordMm);
                // A SPIRAL is a helix: not planar, and not a set marching across its own plane. Both
                // checks below are about curves Revit is asked to draw, and a placed shape draws none.
                bool spiral = rule.Placement != null && rule.Placement.IsSpiral;
                double worstOff;
                if (!spiral && !RebarPlanRules.IsPlanar(geom, set.Tolerances.LengthMm, out worstOff))
                {
                    rows.Add(Refuse(rule, CodeCurveNotPlanar,
                        "the declared centreline is not planar: its points lie up to " + Mm(worstOff) +
                        " off their own best-fit plane, and the tolerance is " + Mm(set.Tolerances.LengthMm) +
                        ". A shape-driven bar must be planar. No point is named as the culprit because the " +
                        "geometry does not support naming one - displace a single corner of a rectangle and " +
                        "all four end up equally far from the fitted plane. A FREE-FORM (non-planar) bar is not " +
                        "implemented: Rebar.CreateFreeForm builds a bar with no shape and no layout, which nothing " +
                        "here can lay out, re-read or audit."));
                    continue;
                }

                // ON THE PLANE ITS NORMAL DECLARES (measured, Revit 2024, anonymized regression case): an oblique bar whose points
                // were rounded to 0.001 mm made CreateFromCurves fail with "An internal error has occurred". Rounding
                // noise - up to NormalPlaneSquaringMm - is squared away exactly; anything larger is refused by name.
                // A normal that lies in the bar's plane is a different mistake, refused further down with its own words.
                if (!spiral && rule.Placement == null && geom.Count >= 2 && rule.NormalMm != null)
                {
                    double nSpread = 0, nExtent = 0, nBase = RebarPlanRules.Project(geom[0], rule.NormalMm);
                    foreach (double[] q in geom) nSpread = Math.Max(nSpread, Math.Abs(RebarPlanRules.Project(q, rule.NormalMm) - nBase));
                    for (int a1 = 0; a1 < geom.Count; a1++)
                        for (int b1 = a1 + 1; b1 < geom.Count; b1++) nExtent = Math.Max(nExtent, RebarPlanRules.Distance(geom[a1], geom[b1]));
                    if (!(nExtent > 0 && nSpread > 0.02 * nExtent))
                    {
                        var declared = new List<double[]>(rule.CurvesMm);
                        var arcKeys = rule.ArcThrough.Keys.OrderBy(k => k).ToList();
                        foreach (int k in arcKeys) declared.Add(rule.ArcThrough[k]);
                        List<double[]> squared; double off;
                        if (!RebarPlanRules.SquareToNormalPlane(declared, rule.NormalMm, NormalPlaneSquaringMm, out squared, out off))
                        {
                            rows.Add(Refuse(rule, CodeCurveOffNormalPlane,
                                "the declared points lie up to " + Mm(off) + " off the plane perpendicular to the declared normal. " +
                                "Revit builds a bar only from curves in that plane (measured: points 0.0005 mm off made it fail " +
                                "with an internal error), and this bridge squares away no more than " + Mm(NormalPlaneSquaringMm) +
                                " of rounding. Declare the points in the plane, or the normal of the plane they are in."));
                            continue;
                        }
                        if (off > 0)
                        {
                            for (int i1 = 0; i1 < rule.CurvesMm.Count; i1++) rule.CurvesMm[i1] = squared[i1];
                            for (int i1 = 0; i1 < arcKeys.Count; i1++) rule.ArcThrough[arcKeys[i1]] = squared[rule.CurvesMm.Count + i1];
                            geom = RebarArcs.Path(rule.CurvesMm, rule.Closed, rule.ArcThrough, RebarArcs.DefaultChordMm);
                        }
                    }
                }

                List<Curve> curves = new List<Curve>();
                if (rule.Placement == null)
                {
                    string gerr = BuildCurves(rule, out curves);
                    if (gerr != null) { rows.Add(Refuse(rule, CodeCurveDegenerate, gerr)); continue; }
                }

                XYZ normal = new XYZ(rule.NormalMm[0], rule.NormalMm[1], rule.NormalMm[2]).Normalize();

                // A SET CANNOT MARCH ALONG THE BAR IT IS MADE OF. If the declared
                // normal lies in the plane of the declared curves, the copies land
                // on top of one another and Revit builds a set that looks like one
                // bar. Nothing downstream would notice: the count is right, the
                // positions are inside the host, and the steel is all in one place.
                if (geom.Count >= 2 && !spiral)
                {
                    double spread = 0;
                    double baseAt = RebarPlanRules.Project(geom[0], rule.NormalMm);
                    foreach (double[] q in geom)
                        spread = Math.Max(spread, Math.Abs(RebarPlanRules.Project(q, rule.NormalMm) - baseAt));
                    double extent = 0;
                    for (int a = 0; a < geom.Count; a++)
                        for (int b2 = a + 1; b2 < geom.Count; b2++)
                            extent = Math.Max(extent, RebarPlanRules.Distance(geom[a], geom[b2]));
                    // The bar must be essentially FLAT along the normal: a planar bar
                    // distributed perpendicular to its own plane spreads by nothing.
                    if (extent > 0 && spread > 0.02 * extent)
                        rows.Add(Refuse(rule, CodeNormalInThePlaneOfTheBar,
                            "the declared normal is not perpendicular to the plane of the declared curves: the " +
                            "bar spreads " + Mm(spread) + " along it, against an overall extent of " + Mm(extent) +
                            ". A set marches PERPENDICULAR to the bar it copies; a normal lying in the bar's own " +
                            "plane stacks the copies on top of one another."));
                    if (extent > 0 && spread > 0.02 * extent) continue;
                }

                // The layout is re-resolved with the diameter READ FROM THE MODEL,
                // not the one the set declared. A minimum clear spacing computed
                // from a diameter that disagrees with the bar type would produce a
                // count the model never reproduces.
                double? modelDia = SafeModelDiameter(barType);
                double? nominalDia = SafeNominalDiameter(barType);
                if (rule.Layout.Layout == RebarLayout.MinimumClearSpacing && !modelDia.HasValue)
                {
                    rows.Add(Refuse(rule, CodeDiameterUnreadable,
                        "bar type '" + SafeName(barType) + "' would not report a model diameter, and a clear " +
                        "distance is measured between bar SURFACES - so the number of bars cannot be computed."));
                    continue;
                }
                var layoutRequest = new RebarLayoutRequest
                {
                    Layout = rule.Layout.Layout,
                    Number = rule.Layout.Number,
                    SpacingMm = rule.Layout.SpacingMm,
                    ArrayLengthMm = rule.Layout.ArrayLengthMm,
                    IncludeFirstBar = rule.Layout.IncludeFirstBar,
                    IncludeLastBar = rule.Layout.IncludeLastBar,
                    // THE MODEL DIAMETER, measured. See SafeModelDiameter.
                    BarDiameterMm = modelDia.HasValue ? (double?)(modelDia.Value * FtToMm) : null
                };
                RebarLayoutPlan layout = RebarLayoutRules.Resolve(layoutRequest);
                if (!layout.Ok)
                {
                    rows.Add(Refuse(rule, CodeLayoutRefused,
                        layout.Error + " (" + layout.Code + ") - computed with the MODEL diameter read from bar " +
                        "type '" + SafeName(barType) + "', " +
                        (layoutRequest.BarDiameterMm.HasValue ? Mm(layoutRequest.BarDiameterMm.Value) : "unreadable") +
                        ". Revit's own clear-spacing count uses the drawn diameter, not the designation one."));
                    continue;
                }

                List<long> unresolved;
                // A MAT'S BARS WERE SCOPED BY THE MAT: its own host decided whether it is in this run. A bar a
                // followed face hosts in an into element is not dropped for not being in the narrowed ids.
                IList<long> narrowHere = rule.ScopedByMat ? null : narrowTo;
                List<Element> hosts = Hosts(doc, rule.Host, narrowHere, out unresolved);
                if (unresolved.Count == 0 && Scope(doc, rule.Host, narrowHere, hosts.Count) == HostNarrowing.Outcome.SkipOutsideHostIds) continue;
                if (hosts.Count == 0)
                {
                    ResolvedRebarRow none = Refuse(rule, CodeHostNotFound,
                        "no element in this document matches the host selector of rule '" + rule.Id + "'." +
                        (unresolved.Count > 0
                            ? " It named " + unresolved.Count + " element id(s) that resolve to nothing: " +
                              string.Join(", ", unresolved.Select(x => x.ToString(CultureInfo.InvariantCulture)))
                            : ""));
                    none.UnresolvedHostIds = unresolved;
                    rows.Add(none);
                    continue;
                }

                // INTO NAMES ELEMENTS, so it means one host. A rule that selects every column of a
                // type and says "into footing 50" would run every column's bars into one footing.
                if (rule.Into.Count > 0 && hosts.Count > 1)
                {
                    rows.Add(Refuse(rule, CodeIntoNeedsOneHost,
                        "rule '" + rule.Id + "' runs into elements " + string.Join(", ", rule.Into) + " and its host selector " +
                        "matches " + hosts.Count + " elements. into names the elements one host's bars run into; write " +
                        "one rule per host."));
                    continue;
                }

                foreach (Element host in hosts)
                {
                    var row = new ResolvedRebarRow
                    {
                        Rule = rule,
                        Host = host,
                        UnresolvedHostIds = unresolved,
                        BarType = barType,
                        Shape = shape,
                        StartHookId = startHook,
                        EndHookId = endHook,
                        Curves = curves,
                        PointsMm = geom,
                        Normal = normal,
                        Layout = layout,
                        ExpectedBarLengthMm = RebarPlanRules.CentrelineLengthMm(geom, rule.Closed),
                        PlacedDefinition = placedKind,
                        PlacedSizeConstraint = sizeConstraint,
                        PlacedSizeReference = sizeReference,
                        PlacedSizeParamId = sizeParam,
                        CouplerType = couplerType
                    };

                    // A DECLARED CROSSING ORDER (etapa 1c): this set gives way to another, so it is displaced HERE, before
                    // anything is measured - every check below (the fit, containment and cover, the anchorage) is made on
                    // the displaced bars, and what is built and verified is the displaced centreline. A displacement that
                    // cannot be made, or an order that does not read one way, is a refusal by name.
                    List<double[]> g = geom;
                    if (crossing != null)
                    {
                        string ckey = CrossingDecision.Key(rule.Id, Rid.Value(host.Id));
                        CrossingRefusal cref;
                        if (crossing.Refused.TryGetValue(ckey, out cref))
                        {
                            row.Code = cref.Code;
                            row.Why = cref.Why;
                            rows.Add(row);
                            continue;
                        }
                        CrossingMove cmove;
                        if (crossing.Moves.TryGetValue(ckey, out cmove))
                        {
                            row.Crossing = cmove;
                            row.DeclaredPointsMm = geom;
                            List<double[]> mc = null;
                            Dictionary<int, double[]> ma = null;
                            string cwhy = rule.Placement != null
                                ? "a hoop or spiral placed from a shape is placed by the shape's own parameters, not by a centreline, and is not displaced."
                                : RebarCrossing.Displace(rule.CurvesMm, rule.ArcThrough, rule.Closed, cmove, out mc, out ma);
                            StructuralRebarRule moved = null;
                            List<Curve> mcurves = null;
                            if (cwhy == null)
                            {
                                moved = rule.CloneMoved(mc, ma);
                                moved.HookCornerSeatMm = RebarCrossing.SeatAfterMove(moved.HookCornerSeatMm, cmove.OffsetMm);
                                cwhy = BuildCurves(moved, out mcurves);
                            }
                            if (cwhy != null)
                            {
                                row.Code = RebarCrossing.CodeNotApplicable;
                                row.Why = "the declared crossing_order moves this set (" + cmove.Describe() + "), and it cannot be moved that way: " + cwhy;
                                rows.Add(row);
                                continue;
                            }
                            g = RebarArcs.Path(mc, rule.Closed, ma, RebarArcs.DefaultChordMm);
                            row.Rule = moved;
                            row.Curves = mcurves;
                            row.PointsMm = g;
                            row.ExpectedBarLengthMm = RebarPlanRules.CentrelineLengthMm(g, rule.Closed);
                        }
                    }

                    bool valid;
                    string eligErr = Eligible(host, out valid);
                    if (eligErr != null)
                    {
                        row.Code = CodeHostUnreadable;
                        row.Why = eligErr;
                        rows.Add(row);
                        continue;
                    }
                    // A FLOOR OR WALL THAT IS NOT STRUCTURAL (acero 3.0, L8, measured by the external
                    // pipeline): Revit will not host steel in it, and the generic refusal did not say
                    // why. The flag that decides it is named, with the way out.
                    // A MEMBER OF A GROUP PLACED MORE THAN ONCE (L8): Revit removes the steel at the
                    // commit without an error. Refused here, before anything is written.
                    string inGroup = GroupRefusal(host);
                    if (inGroup != null)
                    {
                        row.Code = GroupHostRule.Code;
                        row.Why = inGroup;
                        rows.Add(row);
                        continue;
                    }
                    string notStructural = valid ? null : NotStructuralWhy(host);
                    if (notStructural != null)
                    {
                        row.Code = CodeHostNotStructural;
                        row.Why = notStructural;
                        rows.Add(row);
                        continue;
                    }
                    // A STAIR (acero 3.0, L6): Revit does not take a stair element as a reinforcement
                    // host. Named, with the way a stair is reinforced instead.
                    if (!valid && IsStair(host))
                    {
                        row.Code = CodeStairNotHost;
                        row.Why = "element " + Rid.Value(host.Id) + " is a stair, and Revit does not accept a stair element as a " +
                                  "reinforcement host. Model the waist slab as a sloped structural floor and each landing as a " +
                                  "structural floor, and reinforce those with mat_rules.";
                        rows.Add(row);
                        continue;
                    }
                    if (!valid)
                    {
                        row.Code = CodeHostIneligible;
                        row.Why = "Revit does not accept element " + Rid.Value(host.Id) +
                                  " as a reinforcement host. A curtain wall, an in-place mass and a non-structural " +
                                  "family are all ineligible, and none of them is a failure - but nothing may be " +
                                  "planned onto them.";
                        rows.Add(row);
                        continue;
                    }

                    if (shape == null && !rule.AllowNewShape)
                    {
                        row.Code = CodeShapeNotDeclared;
                        row.Why = "rule '" + rule.Id + "' names no shape and does not set allow_new_shape. Revit " +
                                  "would either match an existing shape or CREATE one, and creating a shape " +
                                  "family puts it in the project browser, in schedules and in everybody else's " +
                                  "model. That is declared, never assumed.";
                        rows.Add(row);
                        continue;
                    }

                    // ALREADY BUILT BY THIS RULE, IN THIS HOST. Only asked on the
                    // write path; see the overload's remarks.
                    long hostId = Rid.Value(host.Id);
                    KeyValuePair<long, StructuralProvenance> prior = already.FirstOrDefault(kv =>
                        kv.Value != null &&
                        string.Equals(kv.Value.RuleId, rule.Id, StringComparison.Ordinal) &&
                        kv.Value.HostElementId == hostId);
                    if (prior.Value != null)
                    {
                        row.Code = CodeAlreadyBuilt;
                        row.Why = "element " + prior.Key + " in this host already records rule '" + rule.Id +
                                  "' from requirement set '" + (prior.Value.RequirementSetId ?? "?") +
                                  "'. Building again would put a SECOND coincident set of bars in the same " +
                                  "member - doubled steel, doubled quantities, and a duplicate mark - and " +
                                  "nothing downstream looks for coincident bars. Delete the existing set, or " +
                                  "give this rule a different id if a second layer is intended.";
                        rows.Add(row);
                        continue;
                    }

                    // WHAT THE BARS ARE MEASURED AGAINST: the host, or its union with the elements
                    // the rule runs into (L1). Refused here when those cannot be resolved.
                    ContainmentContext ctx = ContextFor(doc, set, host, rule);
                    row.Context = ctx;
                    if (ctx.Code != null)
                    {
                        row.Code = ctx.Code;
                        row.Why = ctx.Why;
                        rows.Add(row);
                        continue;
                    }
                    List<double[]> corners = ctx.Corners;
                    // THE SIDE THE SET MARCHES TO. MEASURED on Revit 2026: with
                    // bars_on_normal_side false the position offsets come back
                    // NEGATIVE. The plan's positions are always positive, so the fit
                    // check was measuring the wrong half of the host - passing sets
                    // that run off the near end and failing ones that fit.
                    List<double> signed = rule.BarsOnNormalSide
                        ? layout.PositionsMm
                        : layout.PositionsMm.Select(v => -v).ToList();
                    row.SignedPositionsMm = signed;
                    row.Fit = RebarPlanRules.Fit(g, corners, rule.NormalMm,
                                                 signed, set.Tolerances.LengthMm);

                    // THE SOLID, not the box. The projection above answers "is this
                    // set too long for its host" against Revit's AXIS-ALIGNED
                    // bounding box, which for a beam at an angle is bigger than the
                    // beam in every direction - so a bar half a metre out in the air
                    // passes it. This measures every declared bar against the host's
                    // own boundary, before a transaction is opened.
                    row.Mesh = ctx.Mesh;
                    row.MeshWhy = ctx.MeshWhy;
                    double radiusMm = 0;
                    double? modelForRadius = SafeModelDiameter(row.BarType);
                    if (modelForRadius.HasValue) radiusMm = modelForRadius.Value * FtToMm / 2.0;
                    // `rule.Closed` MATTERS HERE. A requirement set is refused for
                    // repeating a closed shape's first point, so a declared stirrup
                    // arrives as an open polyline and used to be measured with one
                    // whole side never sampled.
                    // A DISPLACED SET IS HELD TO A COVER EVEN WHERE THE SET DECLARES NONE (etapa 1c): the bridge moved it,
                    // nobody declared where it now stands, so it must keep the cover its host carries. MEASURED on Revit
                    // 2025: a mat layer sent down 12.7 mm out of a 20 mm slab cover was planned as inside, because a set
                    // without cover_rules holds its bars to no cover at all.
                    double? coverHere = ctx.CoverMm;
                    if (row.Crossing != null && !coverHere.HasValue) coverHere = HostCoverMm(host);
                    row.Containment = RebarContainment.Check(
                        row.Mesh, g, rule.Closed, signed, rule.NormalMm, radiusMm,
                        coverHere, set.Tolerances.LengthMm,
                        RebarContainment.DefaultSampleStepMm, ctx.MeshWhy);

                    // THE ANCHORED LENGTH, on the declared centreline (L1). A hook declared at an
                    // end can carry what the straight length lacks, and Revit only says how long
                    // once the bar is drawn - that answer waits for the apply.
                    row.Anchorage = AnchorageCheck.Evaluate(ctx.Mesh, ctx.PartIds, rule.Anchorage, g,
                        signed, rule.NormalMm, set.Tolerances.LengthMm, false, AnyHookDeclared(set, rule));

                    if (!row.Fit.Fits)
                    {
                        row.Code = row.Fit.Code;
                        row.Why = row.Fit.Why;
                        if (row.Crossing != null)
                        {
                            string cc, cw;
                            RebarCrossing.RefuseMovedRow(row.Crossing, row.Code, row.Why, out cc, out cw);
                            row.Code = cc;
                            row.Why = cw;
                        }
                    }
                    else if (row.Containment.Word != SolidContainment.Inside)
                    {
                        // EVERY answer that is not `inside` is refused HERE, in the
                        // rehearsal, where nothing has been written. Two of them used
                        // to pass the plan and then fail the apply's own verification
                        // AFTER the commit: a declaration short of its declared cover,
                        // and a host whose boundary Revit would not give up. Nothing
                        // changes between the two moments, so the refusal belongs in
                        // the one where no steel exists yet.
                        if (row.Containment.Word == SolidContainment.CompletelyOutside)
                            row.Code = CodeBarOutsideSolid;
                        else if (row.Containment.Word == SolidContainment.PartiallyOutside)
                            row.Code = CodeBarPartlyOutsideSolid;
                        else if (row.Containment.Word == SolidContainment.InsideCoverViolated)
                            row.Code = CodeShortOfDeclaredCover;
                        else
                            row.Code = CodeContainmentNotEvaluable;
                        row.Why = "measured against " + (ctx.IsUnion
                                      ? "the union of elements " + string.Join(", ", ctx.PartIds) + " (host first)"
                                      : "the host's own boundary") + " rather than a bounding box: " +
                                  RebarContainment.Describe(row.Containment);
                        // A DISPLACED SET THAT NO LONGER FITS: refused by name as the crossing order's doing, never squeezed.
                        if (row.Crossing != null && row.Code != CodeContainmentNotEvaluable)
                        {
                            string cc, cw;
                            RebarCrossing.RefuseMovedRow(row.Crossing, row.Code, row.Why, out cc, out cw);
                            row.Code = cc;
                            row.Why = cw;
                        }
                    }
                    else
                    {
                        AnchorageResult bad = row.Anchorage.FirstOrDefault(a => a.Verdict == AnchorageCheck.Short) ??
                                              row.Anchorage.FirstOrDefault(a => a.Verdict == AnchorageCheck.NotMeasured);
                        if (bad != null)
                        {
                            row.Code = bad.Verdict == AnchorageCheck.Short ? CodeAnchorageShort : CodeAnchorageNotMeasured;
                            row.Why = bad.Why;
                        }
                    }
                    rows.Add(row);
                }
            }
            return rows;
        }

        private static string Hook(Document doc, StructuralRequirementSet set, StructuralTermination t,
                                   out ElementId id)
        {
            id = ElementId.InvalidElementId;
            if (t == null || string.IsNullOrWhiteSpace(t.HookTypeId)) return null;
            StructuralHookTypeRef href = set.HookTypes[t.HookTypeId];
            if (href.None) return null;
            int matches;
            RebarHookType hook = ByName<RebarHookType>(doc, href.TypeName, out matches);
            if (hook == null)
                return matches > 1
                    ? matches + " hook types are named '" + href.TypeName + "'. Which one is meant cannot be read " +
                      "from the set, and they bend to different angles."
                    : "no RebarHookType in this document is named '" + href.TypeName + "'.";
            id = hook.Id;
            return null;
        }

        /// <summary>The designation diameter, or null when it could not be read. Zero is not a diameter.</summary>
        private static double? SafeNominalDiameter(RebarBarType t)
        {
            try
            {
                double v = t.BarNominalDiameter;
                return RebarLayoutRules.IsFinite(v) && v > 0 ? (double?)v : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The diameter Revit DRAWS, which is the one a clear distance is measured
        /// against - and, MEASURED on Revit 2026, the one Revit's own
        /// SetLayoutAsMinimumClearSpacing counts with.
        ///
        /// A bar type with nominal 10 mm and model 20 mm, clear 100 mm over a 900 mm
        /// array: nominal arithmetic predicts 9 positions, model arithmetic predicts
        /// 8, and Revit built 8. Feeding the nominal diameter into the layout made
        /// the plan predict a count the model would never reproduce, and the apply
        /// then reported a correctly built set as a failure.
        /// </summary>
        private static double? SafeModelDiameter(RebarBarType t)
        {
            try
            {
                double v = t.BarModelDiameter;
                return RebarLayoutRules.IsFinite(v) && v > 0 ? (double?)v : null;
            }
            catch { return null; }
        }

        /// <summary>Is this element a reinforcement host? A message when Revit would not say.</summary>
        /// <summary>The L8 group refusal for a host, or null when it is in no group or in one placed once.</summary>
        public static string GroupRefusal(Element host)
        {
            try
            {
                if (host == null || host.GroupId == null || host.GroupId == ElementId.InvalidElementId) return null;
                var g = host.Document.GetElement(host.GroupId) as Group;
                if (g == null) return null;
                int n = g.GroupType?.Groups?.Size ?? 0;
                return GroupHostRule.Refusal(Rid.Value(g.Id), g.Name, n);
            }
            catch { return null; }
        }

        /// <summary>Why a floor or wall cannot host steel because it is not structural, or null.</summary>
        public static string NotStructuralWhy(Element host)
        {
            try
            {
                Parameter p = host is Floor ? host.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)
                            : host is Wall ? host.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)
                            : null;
                if (p == null || p.AsInteger() != 0) return null;
                return "element " + Rid.Value(host.Id) + " is a " + (host is Floor ? "floor" : "wall") + " whose '" + p.Definition.Name +
                       "' is off, and Revit hosts reinforcement only in structural floors and walls. Turn it on " +
                       "(horizun_write_params_verified) if the element carries load; nothing here changes it for you.";
            }
            catch { return null; }
        }

        private static string Eligible(Element host, out bool eligible)
        {
            eligible = false;
            try { eligible = RebarHostData.IsValidHost(host); return null; }
            catch (Exception ex)
            {
                // "Revit does not accept this element as a host" is a CLAIM, and it
                // was being made on the strength of a call that never answered.
                return "Revit would not say whether element " + Rid.Value(host.Id) +
                       " can host reinforcement (" + ex.Message + "). Unknown is not a refusal by Revit, and it " +
                       "is not permission either.";
            }
        }

        /// <summary>
        /// One stirrup zone rule becomes the reinforcement rules it means. Returns
        /// null on success, or the reason nothing could be expanded.
        ///
        /// The span may be measured from the host, which is why this lives on the
        /// Revit side: the requirement set can be parsed without a model open, and
        /// the length of a beam cannot.
        /// </summary>
        public static string ExpandZoneRule(Document doc, StructuralRequirementSet set,
            StructuralStirrupZoneRule rule, IList<long> narrowTo, out List<StructuralRebarRule> expanded)
        {
            expanded = new List<StructuralRebarRule>();

            List<Element> hosts;
            try { hosts = Hosts(doc, rule.Host, narrowTo); }
            catch (Exception ex) { return "the hosts of this zone rule could not be resolved: " + ex.Message; }
            if (Scope(doc, rule.Host, narrowTo, hosts.Count) == HostNarrowing.Outcome.SkipOutsideHostIds) return OutsideHostIds;

            if (hosts.Count == 0)
                return "no element in this document matches the host selector.";
            if (hosts.Count > 1)
            {
                // The profile is declared in MODEL coordinates, so it is already in
                // one beam. Expanding it against several would put the same stirrup
                // outline in all of them, at the same place in space.
                var ids = new List<string>();
                foreach (Element e in hosts) ids.Add(Rid.Value(e.Id).ToString());
                return "this rule resolves to " + hosts.Count + " hosts (" + string.Join(", ", ids) +
                       "). A stirrup zone rule declares its profile in model coordinates, so it belongs to one " +
                       "member: expanding it against several would put the same outline in all of them, in the " +
                       "same place in space. Give each member its own rule.";
            }

            Element host = hosts[0];
            double spanMm;
            if (rule.SpanMm.HasValue) spanMm = rule.SpanMm.Value;
            else
            {
                double? measured = HostLengthMm(host);
                if (!measured.HasValue)
                    return "span: host_length was declared and element " + Rid.Value(host.Id) +
                           " has neither a location curve nor a measurable height. State span_mm instead.";
                spanMm = measured.Value;
            }

            int matches;
            StructuralBarTypeRef btRef = set.BarTypes[rule.BarTypeId];
            RebarBarType barType = ByName<RebarBarType>(doc, btRef.TypeName, out matches);
            double dia = 0;
            double? mod = SafeModelDiameter(barType);
            if (mod.HasValue) dia = mod.Value * FtToMm;

            // THE HOST'S COVER, when the rule asked for it. Read here because only
            // this side can see the model; the arithmetic that uses it is in Core.
            // A host with no readable common cover is a refusal by name inside the
            // expansion, never a zero: Revit clamps the array to the host's cover
            // whatever this predicts, and predicting with zero is predicting wrong.
            double? hostCover = null;
            if (rule.Cover != null && rule.Cover.Source == StructuralStirrupZoneCover.SourceHost)
                hostCover = HostCoverMm(host);

            StirrupZoneResult plan = StirrupZoneRules.Expand(rule, spanMm, dia, hostCover, out expanded);
            if (!plan.Ok)
            {
                expanded = new List<StructuralRebarRule>();
                return plan.Code + ": " + plan.Why;
            }
            return null;
        }

        /// <summary>
        /// One section rule, for every host it selects: the host's frame measured off its
        /// solid, then SectionRules.Expand. A host that cannot be read as a straight
        /// rectangular member is refused by name; the other hosts still expand.
        /// </summary>
        public static List<ResolvedRebarRow> ExpandSectionRule(Document doc, StructuralRequirementSet set,
            StructuralSectionRule rule, IList<long> narrowTo, out List<StructuralRebarRule> bars,
            out List<StructuralStirrupZoneRule> zones)
        {
            bars = new List<StructuralRebarRule>();
            zones = new List<StructuralStirrupZoneRule>();
            var refused = new List<ResolvedRebarRow>();
            List<Element> hosts;
            try { hosts = Hosts(doc, rule.Host, narrowTo); }
            catch (Exception ex) { refused.Add(RefuseSection(rule, null, "the hosts could not be resolved: " + ex.Message)); return refused; }
            // host_ids only intersects (backlog P0 #7): a section rule with no host inside it
            // has nothing to do here and is skipped, not refused.
            if (Scope(doc, rule.Host, narrowTo, hosts.Count) == HostNarrowing.Outcome.SkipOutsideHostIds) return refused;
            if (hosts.Count == 0)
            {
                refused.Add(RefuseSection(rule, null, "no element in this document matches the host selector."));
                return refused;
            }
            // INTO NAMES ELEMENTS, so a layer declared into them means one host (L1): every column
            // of a type laid "into footing 50" would put every column's dowels in one footing.
            StructuralSectionLayer intoLayer = rule.Layers.FirstOrDefault(l => l.Into.Count > 0);
            if (intoLayer != null && hosts.Count > 1)
            {
                refused.Add(RefuseSection(rule, null, CodeIntoNeedsOneHost + ": layer '" + (intoLayer.Name ?? intoLayer.Face) +
                    "' runs into elements " + string.Join(", ", intoLayer.Into) + " and the host selector matches " +
                    hosts.Count + " elements. Write one section rule per host."));
                return refused;
            }
            int landed = 0, notHere = 0;
            foreach (Element host in hosts)
            {
                long hostId = Rid.Value(host.Id);
                string why;
                string belowHost = SectionRules.CoverBelowHost(rule, HostCoverMm(host));
                if (belowHost != null) { refused.Add(RefuseSection(rule, hostId, SectionRules.CodeCoverBelowHost + ": " + belowHost)); continue; }
                SectionHostFrame frame = SectionFrameOf(host, out why, rule.Axis == "vertical");
                if (frame == null) { refused.Add(RefuseSection(rule, hostId, why)); continue; }
                PlaceNamedAxes(doc, rule, frame);
                List<StructuralRebarRule> hostBars;
                List<StructuralStirrupZoneRule> hostZones;
                SectionResult res = SectionRules.ExpandAll(rule, frame, id => DiameterOf(doc, set, id),
                                                           id => StirrupBendDiameterOf(doc, set, id),
                                                           rule.Cover.FromHost ? HostCoverMm(host) : null,
                                                           out hostBars, out hostZones);
                if (!res.Ok) { refused.Add(RefuseSection(rule, hostId, res.Code + ": " + res.Why)); continue; }
                if (hostBars.Count + hostZones.Count > 0) landed++; else if (res.NotHere.Count > 0) notHere++;
                bars.AddRange(hostBars);
                zones.AddRange(hostZones);
            }
            // A rule measured from named axes may leave a host of its mark with nothing - another span's
            // beam - but a rule that lands on NO host at all asked for steel nobody receives.
            if (landed == 0 && notHere > 0 && refused.Count == 0)
                refused.Add(RefuseSection(rule, null, SectionRules.CodeNothingInHost + ": rule '" + rule.Id + "' is measured from axes, and none of the " +
                    notHere + " element(s) of its mark lies where its steel is."));
            return refused;
        }

        /// <summary>
        /// The stations of the grids a section rule names (an extent from a named axis, stage 1c),
        /// on this host's frame: each name must be ONE straight grid of this document. A name that is
        /// none, several or not straight is recorded as the refusal it is; SectionRules refuses the
        /// rule for this host by that code. Deterministic: names are matched exactly as written.
        /// </summary>
        private static void PlaceNamedAxes(Document doc, StructuralSectionRule rule, SectionHostFrame frame)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (StructuralSectionLayer l in rule.Layers)
                if (l.Extend == StructuralSectionLayer.ExtendAxes) { names.Add(l.Axis); names.Add(l.Toward); }
            if (rule.StirrupsAxis != null) { names.Add(rule.StirrupsAxis); names.Add(rule.StirrupsToward); }
            if (names.Count == 0) return;
            List<Grid> grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().ToList();
            foreach (string n in names)
            {
                List<Grid> named = grids.Where(g => string.Equals(g.Name, n, StringComparison.Ordinal)).ToList();
                if (named.Count == 0) { frame.AxisProblems[n] = SectionRules.CodeAxisNotInModel + ": no grid in this document is named '" + n + "'."; continue; }
                if (named.Count > 1) { frame.AxisProblems[n] = SectionRules.CodeAxisNotUnique + ": " + named.Count + " grids are named '" + n + "'."; continue; }
                Line line = named[0].Curve as Line;
                if (line == null) { frame.AxisProblems[n] = SectionRules.CodeAxisNotStraight + ": grid '" + n + "' is not a straight line."; continue; }
                XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                double? t = SectionRules.AxisStation(frame.OriginMm, frame.U, new[] { a.X * 304.8, a.Y * 304.8 }, new[] { b.X * 304.8, b.Y * 304.8 }, n, out string problem);
                if (t.HasValue) frame.AxisStations[n] = t.Value;
                else frame.AxisProblems[n] = problem;
            }
        }

        /// <summary>
        /// One wall boundary rule (acero 3.0, L5): its ONE wall, the wall's frame measured off its
        /// location line and its solid (along the line, up world Z), then the end region's frame
        /// and SectionRules.ExpandAll through WallBoundaryRules. Refused by name, never guessed.
        /// </summary>
        public static List<ResolvedRebarRow> ExpandWallBoundaryRule(Document doc, StructuralRequirementSet set,
            StructuralWallBoundaryRule rule, IList<long> narrowTo, out List<StructuralRebarRule> bars,
            out List<StructuralStirrupZoneRule> zones)
        {
            bars = new List<StructuralRebarRule>();
            zones = new List<StructuralStirrupZoneRule>();
            var refused = new List<ResolvedRebarRow>();
            List<Element> hosts;
            try { hosts = Hosts(doc, rule.Host, narrowTo); }
            catch (Exception ex) { refused.Add(RefuseWallBoundary(rule, null, "the wall could not be resolved: " + ex.Message)); return refused; }
            if (Scope(doc, rule.Host, narrowTo, hosts.Count) == HostNarrowing.Outcome.SkipOutsideHostIds) return refused;
            if (hosts.Count != 1)
            {
                refused.Add(RefuseWallBoundary(rule, null, hosts.Count == 0
                    ? "no element in this document matches the host selector."
                    : "the host selector matches " + hosts.Count + " elements; a boundary element is measured from ONE wall."));
                return refused;
            }
            Element host = hosts[0];
            long hostId = Rid.Value(host.Id);
            string category = host.Category == null ? null : ((BuiltInCategory)Rid.Value(host.Category.Id)).ToString();
            string notWall = WallBoundaryRules.HostRefusal(category);
            if (notWall == null && !(host is Wall))
                notWall = WallBoundaryRules.CodeNotAWall + ": element " + hostId + " is in the walls category but is not a system " +
                          "wall (an in-place or family wall), so it has no location line to measure its ends from.";
            if (notWall != null) { refused.Add(RefuseWallBoundary(rule, hostId, notWall)); return refused; }
            if (!(host.Location is LocationCurve))
            {
                refused.Add(RefuseWallBoundary(rule, hostId, "the wall has no location line, so it has no ends to measure a boundary element from."));
                return refused;
            }
            string why;
            SectionHostFrame wall = SectionFrameOf(host, out why);
            if (wall == null) { refused.Add(RefuseWallBoundary(rule, hostId, why)); return refused; }
            SectionResult res = WallBoundaryRules.ExpandAll(rule, wall, id => DiameterOf(doc, set, id),
                                                            id => StirrupBendDiameterOf(doc, set, id),
                                                            rule.Section.Cover.FromHost ? HostCoverMm(host) : null,
                                                            out bars, out zones);
            if (!res.Ok) refused.Add(RefuseWallBoundary(rule, hostId, res.Code + ": " + res.Why));
            return refused;
        }

        private static ResolvedRebarRow RefuseWallBoundary(StructuralWallBoundaryRule rule, long? hostId, string why)
        {
            return new ResolvedRebarRow
            {
                Rule = new StructuralRebarRule
                {
                    Id = hostId.HasValue ? rule.Id + "#h" + hostId.Value.ToString(CultureInfo.InvariantCulture) : rule.Id,
                    Host = hostId.HasValue ? new StructuralHostSelector { ElementIds = new List<long> { hostId.Value } } : rule.Host,
                    Required = rule.Required
                },
                Code = CodeWallBoundaryRuleRefused,
                Why = why
            };
        }

        /// <summary>
        /// A host's section frame: a member with a straight location line runs along it
        /// (up is world Z made square to it); a member placed at a point - a column - runs
        /// up world Z with its facing as the section's up. Measured off the solid.
        /// </summary>
        public static SectionHostFrame SectionFrameOf(Element host, out string why) => SectionFrameOf(host, out why, false);

        public static SectionHostFrame SectionFrameOf(Element host, out string why, bool declaredVertical)
        {
            why = null;
            double[] origin, along, up;
            try
            {
                var fi = host as FamilyInstance;
                if (host.Location is LocationCurve lc)
                {
                    var line = lc.Curve as Line;
                    if (line == null) { why = "its location is a curve, not a straight line; a section rule reinforces straight members."; return null; }
                    XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1), d = (p1 - p0).Normalize();
                    origin = new[] { p0.X * FtToMm, p0.Y * FtToMm, p0.Z * FtToMm };
                    along = new[] { d.X, d.Y, d.Z };
                    if (Math.Abs(d.Z) > 0.99)
                    {
                        if (fi == null) { why = "it is vertical and has no facing to read its section's up from."; return null; }
                        XYZ f = fi.FacingOrientation;
                        up = new[] { f.X, f.Y, f.Z };
                    }
                    else up = new[] { 0.0, 0.0, 1.0 };
                }
                else if (host.Location is LocationPoint lp)
                {
                    if (fi == null) { why = "it is placed at a point and is not a family instance, so it has no facing."; return null; }
                    string category = host.Category == null ? null : ((BuiltInCategory)Rid.Value(host.Category.Id)).ToString();
                    string notColumn = SectionRules.PointHostRefusal(category, declaredVertical);
                    if (notColumn != null) { why = notColumn; return null; }
                    XYZ p = lp.Point, f = fi.FacingOrientation;
                    origin = new[] { p.X * FtToMm, p.Y * FtToMm, p.Z * FtToMm };
                    along = new[] { 0.0, 0.0, 1.0 };
                    up = new[] { f.X, f.Y, 0.0 };
                    if (Math.Abs(f.X) + Math.Abs(f.Y) < 1e-9) { why = "its facing is vertical, so its section has no up."; return null; }
                }
                else { why = "it has neither a straight location line nor a location point."; return null; }
            }
            catch (Exception ex) { why = "its location could not be read: " + ex.Message; return null; }

            string meshWhy;
            HostMesh mesh = MeshFor(host, out meshWhy);
            if (mesh == null) { why = "its solid could not be read: " + meshWhy; return null; }
            return SectionFrames.Measure(Rid.Value(host.Id), origin, along, up, mesh);
        }

        /// <summary>The stirrup/tie bend diameter of a declared bar type id, in mm, or zero.</summary>
        public static double StirrupBendDiameterOf(Document doc, StructuralRequirementSet set, string barTypeId)
        {
            if (string.IsNullOrWhiteSpace(barTypeId) || !set.BarTypes.ContainsKey(barTypeId)) return 0;
            int matches;
            RebarBarType bt = ByName<RebarBarType>(doc, set.BarTypes[barTypeId].TypeName, out matches);
            try { return bt == null ? 0 : bt.StirrupTieBendDiameter * FtToMm; } catch { return 0; }
        }

        private static ResolvedRebarRow RefuseSection(StructuralSectionRule rule, long? hostId, string why)
        {
            return new ResolvedRebarRow
            {
                Rule = new StructuralRebarRule
                {
                    Id = hostId.HasValue ? rule.Id + "#h" + hostId.Value.ToString(CultureInfo.InvariantCulture) : rule.Id,
                    Host = hostId.HasValue ? new StructuralHostSelector { ElementIds = new List<long> { hostId.Value } } : rule.Host,
                    Required = rule.Required
                },
                Code = CodeSectionRuleRefused,
                Why = why
            };
        }

        /// <summary>
        /// One mat rule becomes the reinforcement rules it means, with the
        /// centrelines derived from the host's boundary. Returns null on success.
        /// </summary>
        public static string ExpandMatRule(Document doc, StructuralRequirementSet set,
            StructuralMatRule rule, IList<long> narrowTo, out List<StructuralRebarRule> expanded)
        {
            expanded = new List<StructuralRebarRule>();

            List<Element> hosts;
            try { hosts = Hosts(doc, rule.Host, narrowTo); }
            catch (Exception ex) { return "the hosts of this mat rule could not be resolved: " + ex.Message; }
            if (Scope(doc, rule.Host, narrowTo, hosts.Count) == HostNarrowing.Outcome.SkipOutsideHostIds) return OutsideHostIds;

            if (hosts.Count == 0) return "no element in this document matches the host selector.";
            if (hosts.Count > 1)
            {
                var ids = new List<string>();
                foreach (Element e in hosts) ids.Add(Rid.Value(e.Id).ToString());
                return "this rule resolves to " + hosts.Count + " hosts (" + string.Join(", ", ids) +
                       "). A mat is derived from ONE host's boundary - its face, its extents and its edges - " +
                       "so one rule builds one mat. Give each slab or wall its own rule.";
            }

            Element host = hosts[0];
            string meshWhy;
            HostMesh mesh = MeshFor(host, out meshWhy);
            if (mesh == null)
                return "the boundary of element " + Rid.Value(host.Id) + " is what this mat would be measured " +
                       "from, and it is not available: " + meshWhy;

            // THE ELEMENTS THE FACE RUNS THROUGH (acero 3.0): the host first, then into in order, each with
            // its own cover - a followed bar is hosted by the element most of it lies on.
            double? common = HostCoverMm(host);
            var parts = new List<MatHostPart>
            {
                new MatHostPart { ElementId = Rid.Value(host.Id), Mesh = mesh, CommonCoverMm = common,
                                  FaceCoverMm = common.HasValue ? null : FaceCoverMm(host, rule.FaceNormalMm) }
            };
            foreach (long id in rule.Into)
            {
                if (id == Rid.Value(host.Id))
                    return CodeIntoIsHost + ": mat rule '" + rule.Id + "' names its own host, element " + id + ", in into. into lists " +
                           "the OTHER elements the face continues into.";
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null)
                    return CodeIntoNotFound + ": mat rule '" + rule.Id + "' runs into element " + id + ", and this document has no " +
                           "element with that id.";
                string ewhy;
                HostMesh em = MeshFor(e, out ewhy);
                if (em == null)
                    return CodeIntoNotMeasurable + ": " + RebarContainment.UnionElementUnreadableWhy(id, ewhy);
                double? ec = HostCoverMm(e);
                parts.Add(new MatHostPart { ElementId = id, Mesh = em, CommonCoverMm = ec,
                                            FaceCoverMm = ec.HasValue ? null : FaceCoverMm(e, rule.FaceNormalMm) });
            }
            MatResult plan = MatRules.Expand(rule, parts, id => DiameterOf(doc, set, id), out expanded);
            if (!plan.Ok)
            {
                expanded = new List<StructuralRebarRule>();
                return plan.Code + ": " + plan.Why;
            }
            return null;
        }

        /// <summary>
        /// The host's own common cover in millimetres, or null when it has none or
        /// its faces disagree. Revit clamps a hosted bar to this whatever the
        /// declaration says, which is why a mat has to know it before it writes.
        /// </summary>
        /// <summary>
        /// The cover of the exposed face whose outward normal is the declared one (acero 3.0, L3), for a
        /// host with no common cover; the largest if several faces point that way. Null when none does
        /// or Revit will not say - and then no face check is made, as before.
        /// </summary>
        public static double? FaceCoverMm(Element host, double[] normal)
        {
            double[] n = RebarContainment.Unit(normal);
            if (host == null || n == null) return null;
            try
            {
                RebarHostData data = RebarHostData.GetRebarHostData(host);
                if (data == null) return null;
                using (data)
                {
                    IList<Reference> faces = data.GetExposedFaces();
                    if (faces == null) return null;
                    Transform tr = host is FamilyInstance fi ? fi.GetTotalTransform() : Transform.Identity;
                    double? best = null;
                    foreach (Reference r in faces)
                    {
                        var pf = host.GetGeometryObjectFromReference(r) as PlanarFace;
                        if (pf == null) continue;
                        XYZ fn = tr.OfVector(pf.FaceNormal).Normalize();
                        if (fn.X * n[0] + fn.Y * n[1] + fn.Z * n[2] < 0.99) continue;
                        RebarCoverType ct = data.GetCoverType(r);
                        if (ct == null) continue;
                        double mm = ct.CoverDistance * FtToMm;
                        if (!best.HasValue || mm > best.Value) best = mm;
                    }
                    return best;
                }
            }
            catch { return null; }
        }

        public static double? HostCoverMm(Element host)
        {
            if (host == null) return null;
            try
            {
                RebarHostData data = RebarHostData.GetRebarHostData(host);
                if (data == null) return null;
                using (data)
                {
                    RebarCoverType ct = data.GetCommonCoverType();
                    if (ct == null) return null;
                    double ft = ct.CoverDistance;
                    if (!RebarLayoutRules.IsFinite(ft) || ft <= 0) return null;
                    return ft * FtToMm;
                }
            }
            catch { return null; }
        }

        /// <summary>The MODEL diameter of a resolved bar type, in millimetres, or zero.</summary>
        public static double SafeDiameterMm(RebarBarType t)
        {
            double? mod = SafeModelDiameter(t);
            return mod.HasValue ? mod.Value * FtToMm : 0;
        }

        /// <summary>The MODEL diameter of a declared bar type id, in millimetres, or zero.</summary>
        public static double DiameterOf(Document doc, StructuralRequirementSet set, string barTypeId)
        {
            if (string.IsNullOrWhiteSpace(barTypeId) || !set.BarTypes.ContainsKey(barTypeId)) return 0;
            int matches;
            RebarBarType bt = ByName<RebarBarType>(doc, set.BarTypes[barTypeId].TypeName, out matches);
            double? mod = SafeModelDiameter(bt);
            return mod.HasValue ? mod.Value * FtToMm : 0;
        }

        private static ResolvedRebarRow RefuseMat(StructuralMatRule mr, string why)
        {
            return new ResolvedRebarRow
            {
                Rule = new StructuralRebarRule { Id = mr.Id, Host = mr.Host, Required = mr.Required },
                Code = CodeMatRuleRefused,
                Why = why
            };
        }

        /// <summary>The length of the host's location curve in millimetres, or null.</summary>
        public static double? HostLengthMm(Element host)
        {
            try
            {
                var lc = host?.Location as LocationCurve;
                if (lc?.Curve == null)
                {
                    // A MEMBER PLACED AT A POINT - a column - runs up its height: measured off
                    // its own solid, base to top with every offset applied (refuerzo desde
                    // cartilla, F1.2). A host that cannot be measured stays null and is refused.
                    if (!(host?.Location is LocationPoint)) return null;
                    string why;
                    SectionHostFrame f = SectionFrameOf(host, out why);
                    if (f == null || !RebarLayoutRules.IsFinite(f.LengthMm) || f.LengthMm <= 0) return null;
                    return f.LengthMm;
                }
                double ft = lc.Curve.Length;
                if (!RebarLayoutRules.IsFinite(ft) || ft <= 0) return null;
                return ft * FtToMm;
            }
            catch { return null; }
        }

        private static ResolvedRebarRow RefuseZone(StructuralStirrupZoneRule zr, string why)
        {
            return new ResolvedRebarRow
            {
                Rule = new StructuralRebarRule
                {
                    Id = zr.Id,
                    Host = zr.Host,
                    BarTypeId = zr.BarTypeId,
                    Style = zr.Style,
                    Required = zr.Required
                },
                Code = CodeZoneRuleRefused,
                Why = why
            };
        }

        /// <summary>
        /// The host's boundary, cached for the length of one command. A model_scan
        /// over a hundred hosts would otherwise tessellate the same beam once per
        /// rule, and tessellation is the expensive part of this whole check.
        /// </summary>
        [ThreadStatic] private static Dictionary<long, HostMesh> _meshCache;
        [ThreadStatic] private static Dictionary<long, string> _meshWhy;

        public static HostMesh MeshFor(Element host, out string why)
        {
            why = null;
            if (host == null) { why = "there was no host to measure."; return null; }
            long id = Rid.Value(host.Id);
            if (_meshCache == null) { _meshCache = new Dictionary<long, HostMesh>(); _meshWhy = new Dictionary<long, string>(); }
            HostMesh cached;
            if (_meshCache.TryGetValue(id, out cached)) { why = _meshWhy[id]; return cached; }
            HostMesh m = HostSolidMesh.Usable(host, out why);
            _meshCache[id] = m;
            _meshWhy[id] = why;
            return m;
        }

        // ------------------------------------------------- crossing elements (L1)

        public const string CodeIntoNotFound = "into_element_not_found";
        public const string CodeIntoIsHost = "into_names_the_host";
        public const string CodeIntoNeedsOneHost = "into_needs_a_single_host";
        public const string CodeIntoNotMeasurable = "into_union_not_measurable";
        public const string CodeAnchorageShort = "anchorage_short";
        public const string CodeAnchorageNotMeasured = "anchorage_not_measured";

        /// <summary>The codes the L1 checks add to the resolver's closed set.</summary>
        public static readonly string[] IntoCodes =
        {
            CodeIntoNotFound, CodeIntoIsHost, CodeIntoNeedsOneHost, CodeIntoNotMeasurable,
            CodeAnchorageShort, CodeAnchorageNotMeasured
        };

        /// <summary>
        /// What one rule's bars are measured against in one host: the host alone, or - for a
        /// rule declared into other elements - the union of the host and those (acero 3.0, L1).
        /// ONE answer for the plan, the apply and the audit, so they cannot measure a dowel
        /// against three different solids.
        /// </summary>
        public sealed class ContainmentContext
        {
            public HostMesh Mesh;
            public string MeshWhy;
            /// <summary>Bounding-box corners of every element, for the projection check.</summary>
            public List<double[]> Corners = new List<double[]>();
            public double? CoverMm;
            /// <summary>The element of each part of the union: the host first, then into in order.</summary>
            public List<long> PartIds = new List<long>();
            public bool IsUnion;
            /// <summary>Set when the into elements could not be resolved; the row is refused with it.</summary>
            public string Code;
            public string Why;
        }

        public static ContainmentContext ContextFor(Document doc, StructuralRequirementSet set, Element host,
                                                    StructuralRebarRule rule)
        {
            var c = new ContainmentContext();
            c.PartIds.Add(Rid.Value(host.Id));
            if (rule == null || rule.Into == null || rule.Into.Count == 0)
            {
                string why;
                c.Mesh = MeshFor(host, out why);
                c.MeshWhy = why;
                c.Corners = HostCorners(host);
                c.CoverMm = CoverForContainment(doc, set, host);
                return c;
            }

            c.IsUnion = true;
            var elements = new List<Element> { host };
            foreach (long id in rule.Into)
            {
                if (id == Rid.Value(host.Id))
                {
                    c.Code = CodeIntoIsHost;
                    c.Why = "rule '" + rule.Id + "' names its own host, element " + id + ", in into. into lists the OTHER " +
                            "elements the bars run into; the host is always part of what they are measured against.";
                    return c;
                }
                Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                if (e == null)
                {
                    c.Code = CodeIntoNotFound;
                    c.Why = "rule '" + rule.Id + "' runs into element " + id + ", and this document has no element with " +
                            "that id. A bar declared into an element that is not there has nothing to be anchored in.";
                    return c;
                }
                elements.Add(e);
                c.PartIds.Add(id);
            }

            var meshes = new List<HostMesh>();
            var covers = new List<double?>();
            foreach (Element e in elements)
            {
                string why;
                HostMesh m = MeshFor(e, out why);
                if (m == null)
                {
                    c.MeshWhy = RebarContainment.UnionElementUnreadableWhy(Rid.Value(e.Id), why);
                    return c;
                }
                meshes.Add(m);
                c.Corners.AddRange(HostCorners(e));
                covers.Add(CoverForContainment(doc, set, e));
            }
            // THE SMALLEST DECLARED COVER of the elements the bar runs through: a dowel sits at
            // the column's cover inside the column, and holding it there to the footing's larger
            // cover would refuse every correctly placed dowel. The host's cover may be declared by
            // the bar's own section rule rather than by cover_rules; it counts the same.
            c.CoverMm = RebarContainment.UnionCoverMm(covers, rule.SectionCoverMm);
            string uwhy;
            c.Mesh = HostMesh.Union(meshes, out uwhy);
            if (c.Mesh == null)
            {
                c.Code = CodeIntoNotMeasurable;
                c.Why = "the bars of rule '" + rule.Id + "' run through elements " + string.Join(", ", c.PartIds) +
                        " (host first), and their union could not be measured: " + uwhy + " The bars are refused " +
                        "rather than measured against the host alone, which would call every dowel outside.";
                c.MeshWhy = c.Why;
            }
            return c;
        }

        /// <summary>Whether a rule declares a hook at either end - the anchorage may need it.</summary>
        public static bool AnyHookDeclared(StructuralRequirementSet set, StructuralRebarRule rule)
        {
            foreach (StructuralTermination t in new[] { rule.Start, rule.End })
            {
                if (t == null || string.IsNullOrWhiteSpace(t.HookTypeId)) continue;
                StructuralHookTypeRef h;
                if (set.HookTypes.TryGetValue(t.HookTypeId, out h) && !h.None) return true;
            }
            return false;
        }

        /// <summary>Forget the cached boundaries. Called once per command, so a write is never measured against a stale solid.</summary>
        public static void ForgetMeshes()
        {
            _meshCache = null;
            _meshWhy = null;
            _coverByHost = null;
            _coverSetOwner = null;
        }

        /// <summary>
        /// The cover a containment check should hold this rule to, or null when the
        /// set declares none. NOT invented: a requirement set that says nothing about
        /// cover gets a containment answer that says nothing about cover.
        /// </summary>
        public static double? CoverForContainment(Document doc, StructuralRequirementSet set, Element host)
        {
            if (doc == null || set == null || host == null) return null;

            // ONE SWEEP PER COVER RULE PER COMMAND, not one per rule per row. Each
            // call resolved every cover rule's selector from scratch, and a category
            // selector materialises the whole category. Fifty cover rules over two
            // hundred rows was ten thousand full-category sweeps to answer "does any
            // cover rule name this one host". The map is cleared with the meshes, so
            // it cannot outlive the command that built it.
            if (_coverByHost == null || !ReferenceEquals(_coverSetOwner, set))
            {
                _coverByHost = new Dictionary<long, double?>();
                _coverSetOwner = set;
                foreach (StructuralCoverRule c in set.CoverRules)
                {
                    // A PER-FACE cover (L7) is not the host's one number: containment holds a bar to a single
                    // cover, and a face rule says nothing about the faces it does not name.
                    if (c == null || !c.DistanceMm.HasValue || c.Host == null || !c.Host.Any ||
                        c.Face == StructuralCoverRule.FaceNormal) continue;
                    List<Element> selected;
                    try { selected = Hosts(doc, c.Host, null); }
                    catch { continue; }
                    foreach (Element e in selected)
                    {
                        if (e == null) continue;
                        long id = Rid.Value(e.Id);
                        double? already;
                        if (_coverByHost.TryGetValue(id, out already))
                        {
                            // TWO cover rules naming the same host is a contradiction
                            // the requirement set has to resolve, not something to
                            // average or to pick the first of. The containment check
                            // then holds this bar to no cover at all, not to a guess.
                            if (!already.HasValue || Math.Abs(already.Value - c.DistanceMm.Value) > 1e-9)
                                _coverByHost[id] = null;
                        }
                        else _coverByHost[id] = c.DistanceMm;
                    }
                }
            }

            double? found;
            return _coverByHost.TryGetValue(Rid.Value(host.Id), out found) ? found : null;
        }

        [ThreadStatic] private static Dictionary<long, double?> _coverByHost;
        [ThreadStatic] private static StructuralRequirementSet _coverSetOwner;

        /// <summary>A bend diameter in millimetres, or null when Revit will not say.</summary>
        public static JToken BendMm(RebarBarType type, bool stirrup)
        {
            if (type == null) return JValue.CreateNull();
            try
            {
                double ft = stirrup ? type.StirrupTieBendDiameter : type.StandardBendDiameter;
                if (double.IsNaN(ft) || double.IsInfinity(ft) || ft <= 0) return JValue.CreateNull();
                return Math.Round(ft * FtToMm, 3);
            }
            catch { return JValue.CreateNull(); }
        }

        /// <summary>Points as [[x, y, z], ...], which is what the audit compares.</summary>
        public static JArray PointArray(IList<double[]> points)
        {
            var a = new JArray();
            if (points == null) return a;
            foreach (double[] p in points)
            {
                if (p == null || p.Length < 3) continue;
                a.Add(new JArray(Math.Round(p[0], 3), Math.Round(p[1], 3), Math.Round(p[2], 3)));
            }
            return a;
        }

        /// <summary>The host's bounding box corners, in millimetres, or an empty list.</summary>
        public static List<double[]> HostCorners(Element host)
        {
            BoundingBoxXYZ box = null;
            try { box = host.get_BoundingBox(null); } catch { }
            if (box == null) return new List<double[]>();
            return RebarPlanRules.BoxCorners(
                new[] { box.Min.X * FtToMm, box.Min.Y * FtToMm, box.Min.Z * FtToMm },
                new[] { box.Max.X * FtToMm, box.Max.Y * FtToMm, box.Max.Z * FtToMm });
        }

        /// <summary>
        /// What a rebar shape's definition says it is (L7): one of the PlacedShapes.Definition*
        /// words, and the diameter or radius constraint it carries when it carries one. A
        /// definition that cannot be read is "unreadable", never a guess.
        /// </summary>
        public static void ReadPlacedDefinition(RebarShape shape, out string kind, out string constraint,
                                                out string reference, out ElementId param)
        {
            kind = PlacedShapes.DefinitionUnreadable; constraint = null; reference = null; param = ElementId.InvalidElementId;
            try
            {
                RebarShapeDefinition def = shape.GetRebarShapeDefinition();
                var arc = def as RebarShapeDefinitionByArc;
                if (arc == null)
                {
                    kind = def is RebarShapeDefinitionBySegments ? PlacedShapes.DefinitionSegments : "other";
                    return;
                }
                kind = arc.Type == RebarShapeDefinitionByArcType.LappedCircle ? PlacedShapes.DefinitionLappedCircle
                     : arc.Type == RebarShapeDefinitionByArcType.Spiral ? PlacedShapes.DefinitionSpiral
                     : PlacedShapes.DefinitionArc;
                foreach (RebarShapeConstraint c in arc.GetConstraints())
                {
                    if (c is RebarShapeConstraintDiameter d) { constraint = "diameter"; reference = ArcReference(d.ArcReferenceType); }
                    else if (c is RebarShapeConstraintRadius r) { constraint = "radius"; reference = ArcReference(r.ArcReferenceType); }
                    else continue;
                    param = c.GetParamId();
                    break;
                }
            }
            catch { kind = PlacedShapes.DefinitionUnreadable; constraint = null; reference = null; param = ElementId.InvalidElementId; }
        }

        private static string ArcReference(RebarShapeArcReferenceType t) =>
            t == RebarShapeArcReferenceType.Centerline ? "centerline"
            : t == RebarShapeArcReferenceType.External ? "external"
            : t == RebarShapeArcReferenceType.Internal ? "internal" : t.ToString();

        /// <summary>A rebar shape's own (shared) parameter on a bar, in millimetres, or null when it cannot be read.</summary>
        public static double? ReadShapeParameterMm(Document doc, Rebar bar, ElementId paramId)
        {
            try
            {
                var sp = doc.GetElement(paramId) as SharedParameterElement;
                Parameter p = sp == null ? null : bar.get_Parameter(sp.GuidValue);
                if (p == null || p.StorageType != StorageType.Double) return null;
                double v = p.AsDouble();
                return RebarLayoutRules.IsFinite(v) ? (double?)(v * FtToMm) : null;
            }
            catch { return null; }
        }

        /// <summary>A placed spiral's pitch, height and finishing turns read off the bar (L7), against its placement.</summary>
        public static JObject SpiralParametersRead(Rebar bar, PlacedShapeGeometry g, double toleranceMm)
        {
            return PlacedShapes.CompareSpiralParameters(g,
                LengthMm(bar, BuiltInParameter.REBAR_SHAPE_SPIRAL_PITCH),
                LengthMm(bar, BuiltInParameter.REBAR_SHAPE_SPIRAL_HEIGHT),
                Turns(bar, BuiltInParameter.REBAR_SHAPE_SPIRAL_BASE_FINISHING_TURNS),
                Turns(bar, BuiltInParameter.REBAR_SHAPE_SPIRAL_TOP_FINISHING_TURNS),
                toleranceMm);
        }

        private static double? LengthMm(Rebar bar, BuiltInParameter which)
        {
            try
            {
                Parameter p = bar.get_Parameter(which);
                if (p == null || p.StorageType != StorageType.Double) return null;
                double v = p.AsDouble();
                return RebarLayoutRules.IsFinite(v) ? (double?)(v * FtToMm) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// A whole number of turns, or null when unread. A stored value that is not whole is
        /// reported as -1, which no declaration carries, so it reads as a difference rather than
        /// being rounded into agreement.
        /// </summary>
        private static int? Turns(Rebar bar, BuiltInParameter which)
        {
            try
            {
                Parameter p = bar.get_Parameter(which);
                if (p == null) return null;
                if (p.StorageType == StorageType.Integer) return p.AsInteger();
                if (p.StorageType != StorageType.Double) return null;
                double v = p.AsDouble();
                return Math.Abs(v - Math.Round(v)) < 1e-6 ? (int)Math.Round(v) : -1;
            }
            catch { return null; }
        }

        /// <summary>Every coupler type (category Rebar Couplers) with exactly this name.</summary>
        public static List<ElementType> CouplerTypes(Document doc, string name)
        {
            try
            {
                return new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Coupler).WhereElementIsElementType()
                    .OfType<ElementType>().Where(t => string.Equals(SafeName(t), name, StringComparison.Ordinal))
                    .OrderBy(t => Rid.Value(t.Id)).ToList();
            }
            catch { return new List<ElementType>(); }
        }

        /// <summary>One exposed face a per-face cover rule means (L7), with the cover it carries now.</summary>
        public sealed class FaceCover
        {
            public Reference Ref;
            public double[] Normal;
            public ElementId Current;
            public bool CurrentReadable = true;
        }

        /// <summary>
        /// The exposed PLANAR faces of a host whose outward normal is the declared one (L7), with
        /// the cover type each carries. `seen` lists every exposed planar face's normal, for the
        /// refusal. Null when Revit would not give up the host data or its faces.
        /// </summary>
        public static List<FaceCover> CoverFaces(Element host, double[] normal, double angleTolDeg,
                                                 out List<double[]> seen, out string why)
        {
            seen = new List<double[]>();
            why = null;
            try
            {
                RebarHostData data = RebarHostData.GetRebarHostData(host);
                if (data == null) { why = "Revit would not return the host data of element " + Rid.Value(host.Id) + "."; return null; }
                using (data)
                {
                    IList<Reference> faces = data.GetExposedFaces();
                    if (faces == null) { why = "Revit would not list the exposed faces of element " + Rid.Value(host.Id) + "."; return null; }
                    Transform tr = host is FamilyInstance fi ? fi.GetTotalTransform() : Transform.Identity;
                    var found = new List<FaceCover>();
                    foreach (Reference r in faces)
                    {
                        var pf = host.GetGeometryObjectFromReference(r) as PlanarFace;
                        if (pf == null) continue;
                        XYZ fn = tr.OfVector(pf.FaceNormal).Normalize();
                        var n = new[] { fn.X, fn.Y, fn.Z };
                        seen.Add(n);
                        if (!CoverFaceRules.Matches(n, normal, angleTolDeg)) continue;
                        var fc = new FaceCover { Ref = r, Normal = n };
                        try { fc.Current = data.GetCoverType(r)?.Id; } catch { fc.CurrentReadable = false; }
                        found.Add(fc);
                    }
                    return found;
                }
            }
            catch (Exception ex)
            {
                why = "the exposed faces of element " + Rid.Value(host.Id) + " could not be read: " + ex.Message;
                return null;
            }
        }

        private static string BuildCurves(StructuralRebarRule rule, out List<Curve> curves)
        {
            curves = new List<Curve>();
            List<double[]> p = rule.CurvesMm;
            try
            {
                // ARCS (L2) are asked for as true arcs: Revit then draws a circle, not a polygon.
                int segs = rule.Closed ? p.Count : p.Count - 1;
                for (int i = 0; i < segs; i++)
                {
                    double[] a = p[i], b = p[(i + 1) % p.Count];
                    double[] t;
                    if (rule.ArcThrough != null && rule.ArcThrough.TryGetValue(i, out t))
                        curves.Add(Arc.Create(Ft(a), Ft(b), Ft(t)));
                    else
                        curves.Add(Line.CreateBound(Ft(a), Ft(b)));
                }
            }
            catch (Exception ex)
            {
                // Revit refuses a segment shorter than its own short-curve tolerance,
                // which is about 0.8 mm and is NOT the same as the zero-length check
                // the requirement set already made.
                return "Revit refused a segment of the declared centreline: " + ex.Message +
                       " Revit has a short-curve tolerance of roughly 0.8 mm, which is stricter than the " +
                       "zero-length check the requirement set makes.";
            }
            return null;
        }

        private static XYZ Ft(double[] mm)
        {
            return new XYZ(mm[0] * MmToFt, mm[1] * MmToFt, mm[2] * MmToFt);
        }

        private static ResolvedRebarRow Refuse(StructuralRebarRule rule, string code, string why)
        {
            return new ResolvedRebarRow { Rule = rule, Code = code, Why = why };
        }

        private static string Mm(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture) + " mm";
        }

        // ----------------------------------------------------------- reporting

        public static JObject DescribeRebarRow(ResolvedRebarRow r, int index)
        {
            var o = new JObject
            {
                ["index"] = index,
                ["rule_id"] = r.Rule == null ? null : r.Rule.Id,
                ["host_id"] = r.Host == null ? -1 : Rid.Value(r.Host.Id),
                ["host_category"] = r.Host == null || r.Host.Category == null ? null : r.Host.Category.Name,
                ["will_build"] = r.Ok,
                ["required"] = r.Rule != null && r.Rule.Required
            };
            if (r.Crossing != null)
            {
                JObject c = r.Crossing.ToJson();
                if (r.DeclaredPointsMm != null) c["declared_curve_mm"] = PointArray(r.DeclaredPointsMm);
                c["means"] = "this set gives way under the declared crossing_order: curve_mm is the DISPLACED centreline - what is " +
                             "measured, built and verified - and declared_curve_mm the one the set declared.";
                o["crossing"] = c;
            }
            if (!r.Ok)
            {
                o["code"] = r.Code;
                o["why"] = r.Why;
                if (r.Overlaps != null) o["overlaps"] = r.Overlaps;
                return o;
            }
            // WHAT THIS SET BELONGS TO (overlapping_bar): the declared rule, its layout, its cage and its
            // part in it, and a declared lap or coupler to the next piece - what decides which contacts are
            // the design and which are a clash.
            o["belongs_to"] = RebarOverlap.Identity(r.Rule);
            double? nom = SafeNominalDiameter(r.BarType), mod = SafeModelDiameter(r.BarType);
            o["bar_type"] = new JObject
            {
                ["id"] = Rid.Value(r.BarType.Id),
                ["name"] = SafeName(r.BarType),
                ["nominal_diameter_mm"] = nom.HasValue ? (JToken)Math.Round(nom.Value * FtToMm, 3) : JValue.CreateNull(),
                ["model_diameter_mm"] = mod.HasValue ? (JToken)Math.Round(mod.Value * FtToMm, 3) : JValue.CreateNull(),
                ["clear_spacing_counted_with"] = "model_diameter_mm",
                // The bend Revit will put in a corner the declaration draws sharp.
                // Published because the point-by-point comparison has to allow for
                // it, and an allowance nobody can see is indistinguishable from a
                // loose tolerance.
                ["standard_bend_diameter_mm"] = BendMm(r.BarType, false),
                ["stirrup_tie_bend_diameter_mm"] = BendMm(r.BarType, true)
            };
            o["shape"] = r.Shape == null
                ? (JToken)new JObject { ["declared"] = false, ["revit_will_match_or_create"] = true }
                : new JObject { ["declared"] = true, ["id"] = Rid.Value(r.Shape.Id), ["name"] = SafeName(r.Shape) };
            o["style"] = r.Rule.Style;
            o["closed"] = r.Rule.Closed;
            // THE DECLARED CENTRELINE, as points, so the audit can compare it with
            // the one Revit drew rather than only with its total length.
            // THE MEASURED PATH (arcs sampled, L2): what the audit compares the drawn bar with.
            o["curve_mm"] = PointArray(r.PointsMm);
            if (r.Rule.ArcThrough != null && r.Rule.ArcThrough.Count > 0) o["arcs"] = r.Rule.ArcThrough.Count;
            // A PLACED HOOP OR SPIRAL (L7): where it goes, what its shape's definition was read as,
            // and the size parameter the shape carries. The audit compares the placement, not the polyline.
            if (r.Rule.Placement != null)
            {
                JObject pj = r.Rule.Placement.ToJson();
                pj["definition_read_as"] = r.PlacedDefinition;
                pj["size_parameter"] = r.PlacedSizeConstraint == null
                    ? (JToken)JValue.CreateNull()
                    : new JObject
                    {
                        ["constraint"] = r.PlacedSizeConstraint,
                        ["measured_to"] = r.PlacedSizeReference,
                        ["parameter_id"] = Rid.Value(r.PlacedSizeParamId),
                        ["expected_mm"] = PlacedShapes.ExpectedSizeMm(r.PlacedSizeConstraint, r.PlacedSizeReference,
                            r.Rule.Placement.CentrelineRadiusMm, SafeDiameterMm(r.BarType)) is double es ? (JToken)Math.Round(es, 3) : JValue.CreateNull()
                    };
                pj["built_by"] = "Rebar.CreateFromRebarShape at the box's corner, then RebarShapeDrivenAccessor." +
                                 (r.Rule.Placement.IsSpiral ? "ScaleToBoxFor3D over the height, then pitch and finishing turns set"
                                                            : "ScaleToBox to the hoop's outside diameter (centreline diameter + one bar)") +
                                 "; both hooks cleared. curve_mm is the path containment measures, not curves Revit is asked to draw.";
                o["placement"] = pj;
            }
            if (r.Rule.CouplerNextRuleId != null)
                o["coupler_next"] = new JObject
                {
                    ["type_name"] = r.Rule.CouplerNextTypeName,
                    ["type_id"] = r.CouplerType == null ? -1 : Rid.Value(r.CouplerType.Id),
                    ["next_rule_id"] = r.Rule.CouplerNextRuleId,
                    ["at"] = "the END of this piece and the START of the next, which meet at the declared station"
                };
            o["segments"] = r.Curves.Count;
            o["expected_bar_length_mm"] = Math.Round(r.ExpectedBarLengthMm, 3);
            o["expected_bar_length_means"] =
                "the declared centreline only. Revit ADDS hook length and reports the total, so this number is " +
                "smaller than what the model will say whenever a hook is declared - and an expectation that " +
                "guessed at the hook could never be matched.";
            o["normal"] = new JObject
            {
                ["x"] = Math.Round(r.Normal.X, 6),
                ["y"] = Math.Round(r.Normal.Y, 6),
                ["z"] = Math.Round(r.Normal.Z, 6)
            };
            // HOW FAR THE MODEL IS ALLOWED TO FALL SHORT of the declared array
            // length, published so the audit can compare like with like. Revit
            // lays a set out over somewhere between the declared length and one
            // MODEL bar diameter less, and no rule was found that says which -
            // RebarArrayGeometry carries the eleven measurements. Comparing the
            // declaration against the model for EQUALITY reported a finding on
            // every correctly built array.
            double planModelDiameterMm = r.BarType != null ? SafeDiameterMm(r.BarType) : 0;
            o["layout"] = new JObject
            {
                ["rule"] = r.Layout.Layout,
                ["number_of_bar_positions"] = r.Layout.NumberOfBarPositions,
                ["quantity"] = r.Layout.Quantity,
                ["array_length_mm"] = Math.Round(r.Layout.ArrayLengthMm, 3),
                ["array_length_means"] =
                    "what the rule DECLARED. The model may report up to one model bar diameter less - see " +
                    "array_length_shortfall_allowed_mm.",
                ["array_length_shortfall_allowed_mm"] = planModelDiameterMm > 0
                    ? (JToken)Math.Round(planModelDiameterMm, 3) : JValue.CreateNull(),
                ["array_length_shortfall_means"] = RebarArrayGeometry.WhyMeasuredNotPredicted,
                ["resulting_spacing_mm"] = r.Layout.ResultingSpacingMm.HasValue
                    ? (JToken)Math.Round(r.Layout.ResultingSpacingMm.Value, 3) : JValue.CreateNull(),
                ["include_first_bar"] = r.Layout.IncludeFirstBar,
                ["include_last_bar"] = r.Layout.IncludeLastBar,
                ["bars_on_normal_side"] = r.Rule.BarsOnNormalSide,
                ["positions_mm"] = new JArray(r.Layout.PositionsMm.Select(x => Math.Round(x, 3)).Cast<object>().ToArray()),
                ["signed_positions_mm"] = new JArray(r.SignedPositionsMm.Select(x => Math.Round(x, 3)).Cast<object>().ToArray()),
                ["signed_positions_mean"] =
                    "the same positions with the side applied. MEASURED: with bars_on_normal_side false Revit " +
                    "lays the set out at NEGATIVE offsets, so these are what the model is compared against."
            };
            o["expected_total_steel_length_mm"] = Math.Round(r.ExpectedBarLengthMm * r.Layout.Quantity, 3);
            if (r.Rule.HookCornerSeatMm.HasValue) o["hook_corner_seat_mm"] = Math.Round(r.Rule.HookCornerSeatMm.Value, 3);
            // THE ENDS A COUPLER JOINS (L7): there Revit may leave the bar short, inside the coupler's body.
            if (r.Rule.CoupledAtStart || r.Rule.CouplerNextRuleId != null)
            {
                var ce = new JArray();
                if (r.Rule.CoupledAtStart) ce.Add(0);
                if (r.Rule.CouplerNextRuleId != null && !(r.Rule.CoupledAtStart && r.Rule.CouplerNextEnd == 0)) ce.Add(r.Rule.CouplerNextEnd);
                o["coupled_ends"] = ce;
            }
            // THE ANSWER THAT MATTERS, before the projection that used to be it.
            // Same code the apply and the audit run, on the centreline this plan is
            // about to ask Revit for.
            o["containment"] = r.Containment == null
                ? new JObject
                {
                    ["containment"] = SolidContainment.NotEvaluable,
                    ["evaluated"] = false,
                    ["why"] = r.MeshWhy ?? "containment was not measured."
                }
                : r.Containment.ToJson();
            if (r.Mesh == null && r.MeshWhy != null && !((string)o["containment"]["why"] ?? "").Contains(r.MeshWhy))
                o["containment"]["boundary_why"] = r.MeshWhy;
            if (r.Context != null && r.Context.IsUnion)
            {
                o["containment"]["measured_against"] = new JArray(r.Context.PartIds.Cast<object>().ToArray());
                o["containment"]["measured_against_means"] =
                    "the union of the host (first) and the elements the rule runs into. A face two of them share " +
                    "is concrete on both sides and is not a boundary; the cover held is the smallest any of them declares.";
            }
            if (r.Anchorage.Count > 0)
                o["anchorage"] = new JArray(r.Anchorage.Select(a => (object)a.ToJson()).ToArray());
            o["containment"]["relationship_to_fit"] =
                "fit is a projection onto the distribution axis and answers only whether the set is too long " +
                "for its host. containment is measured against the host's own boundary and is the answer to " +
                "whether the steel is in the concrete. A set can pass fit and be entirely outside the member.";

            o["fit"] = r.Fit == null ? null : new JObject
            {
                ["fits"] = r.Fit.Fits,
                ["code"] = r.Fit.Code,
                ["how_measured"] = r.Fit.Why,
                ["host_span_mm"] = new JArray(Math.Round(r.Fit.HostSpan.Min, 3), Math.Round(r.Fit.HostSpan.Max, 3)),
                ["set_span_mm"] = new JArray(Math.Round(r.Fit.SetSpan.Min, 3), Math.Round(r.Fit.SetSpan.Max, 3))
            };
            o["terminations"] = new JObject
            {
                ["start"] = new JObject
                {
                    ["hook_type_id"] = Rid.Value(r.StartHookId),
                    ["has_hook"] = r.StartHookId != ElementId.InvalidElementId,
                    ["orientation"] = r.Rule.Start.Orientation
                },
                ["end"] = new JObject
                {
                    ["hook_type_id"] = Rid.Value(r.EndHookId),
                    ["has_hook"] = r.EndHookId != ElementId.InvalidElementId,
                    ["orientation"] = r.Rule.End.Orientation
                }
            };
            if (!string.IsNullOrWhiteSpace(r.Rule.Mark)) o["mark"] = r.Rule.Mark;
            // A COVER-AWARE ZONE says what it predicted and from what, so the
            // arithmetic is on the page before anything is written and the apply's
            // comparison against it can be read as the proof it is.
            if (r.Rule.CoverPrediction != null)
            {
                StirrupCoverPrediction cp = r.Rule.CoverPrediction;
                o["cover_prediction"] = new JObject
                {
                    ["status"] = StirrupCoverPrediction.Marker,
                    ["source"] = cp.Source,
                    ["cover_mm"] = Math.Round(cp.CoverMm, 3),
                    ["bar_radius_mm"] = Math.Round(cp.BarRadiusMm, 3),
                    ["clamp_each_end_mm"] = Math.Round(cp.ClampEachEndMm, 3),
                    ["host_span_mm"] = Math.Round(cp.HostSpanMm, 3),
                    ["usable_span_mm"] = Math.Round(cp.UsableSpanMm, 3),
                    ["zone"] = cp.ZoneName,
                    ["first_station_mm"] = Math.Round(cp.ZoneStartMm, 3),
                    ["last_station_mm"] = Math.Round(cp.ZoneEndMm, 3),
                    ["means"] =
                        "the zone was laid out on the host span less cover + bar radius at each end, which is " +
                        "where Revit clamps a hosted array (ADR-003 item 7). The stations are a PREDICTION from " +
                        "that measured rule; the apply compares the first bar Revit drew and the span it reports " +
                        "against them, and only that comparison proves it. The profile is not moved by the cover."
                };
            }
            // A MAT OVER OPENINGS says which holes it saw, which it ignored, and
            // what the declared policy did to this component's bars.
            if (r.Rule.OpeningContext != null) o["openings"] = r.Rule.OpeningContext.ToJson();
            // A MAT THAT FOLLOWS ITS FACE says what it was laid on: the facets, the first bar's polyline
            // in its own (along, up) frame, the stations this set carries, and its host and into elements.
            if (r.Rule.FollowFace != null) o["follow_face"] = r.Rule.FollowFace.ToJson();
            if (r.UnresolvedHostIds.Count > 0)
            {
                o["host_ids_that_resolved_to_nothing"] =
                    new JArray(r.UnresolvedHostIds.Cast<object>().ToArray());
                o["host_ids_that_resolved_to_nothing_mean"] =
                    "this rule NAMED these element ids and this document has no element with them. They are " +
                    "reported rather than dropped: a rule naming three beams, one of them since deleted, used " +
                    "to build two sets and report success.";
            }
            return o;
        }

        public static JObject DescribeCoverRow(ResolvedCoverRow r, int index)
        {
            var o = new JObject
            {
                ["index"] = index,
                ["rule_id"] = r.Rule == null ? null : r.Rule.Id,
                ["host_id"] = r.Host == null ? -1 : Rid.Value(r.Host.Id),
                ["will_set"] = r.Ok && !r.AlreadyRight,
                ["already_right"] = r.AlreadyRight,
                ["required"] = r.Rule != null && r.Rule.Required
            };
            if (!r.Ok)
            {
                o["code"] = r.Code;
                o["why"] = r.Why;
                return o;
            }
            o["cover_type"] = new JObject
            {
                ["id"] = Rid.Value(r.CoverType.Id),
                ["name"] = SafeName(r.CoverType),
                ["distance_mm"] = r.WantedDistanceMm.HasValue
                    ? (JToken)Math.Round(r.WantedDistanceMm.Value, 3) : JValue.CreateNull()
            };
            if (r.Rule.Face == StructuralCoverRule.FaceNormal)
            {
                o["face"] = new JObject
                {
                    ["normal"] = new JArray(r.Rule.FaceNormalMm.Select(v => (object)Math.Round(v, 6)).ToArray()),
                    ["faces_matched"] = r.Faces.Count,
                    ["already_right"] = r.Faces.Count(f => f.Current != null && f.Current == r.CoverType.Id),
                    ["means"] = "every exposed planar face of the host whose OUTWARD normal is the declared one, within " +
                                "the angle tolerance. Each is set and re-read; the common cover is not touched."
                };
                o["current_face_cover_mm"] = r.CurrentDistanceMm.HasValue
                    ? (JToken)Math.Round(r.CurrentDistanceMm.Value, 3) : JValue.CreateNull();
                o["current_means"] = "null when the matched faces carry different cover types now, or one could not be read.";
                return o;
            }
            o["current_common_cover_mm"] = r.CurrentDistanceMm.HasValue
                ? (JToken)Math.Round(r.CurrentDistanceMm.Value, 3) : JValue.CreateNull();
            o["current_readable"] = r.CurrentReadable;
            o["current_means"] =
                r.CurrentReadable
                    ? "null here is a FACT about the host: a host whose faces carry different cover types has no " +
                      "common cover, and setting one would overwrite every face."
                    : "Revit would not answer what the common cover is, so null here means UNREAD rather than " +
                      "mixed. The two used to be the same null.";
            return o;
        }
    }
}
