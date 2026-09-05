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
        public const string CodeBarOutsideSolid = "bar_outside_host_solid";
        public const string CodeBarPartlyOutsideSolid = "bar_partly_outside_host_solid";
        public const string CodeZoneRuleRefused = "stirrup_zone_rule_refused";
        public const string CodeShortOfDeclaredCover = "bar_short_of_the_declared_cover";
        public const string CodeContainmentNotEvaluable = "containment_not_evaluable";
        public const string CodeMatRuleRefused = "mat_rule_refused";
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

        /// <summary>Every code this resolver can emit. Published so the contract cannot drift from it.</summary>
        public static readonly string[] AllCodes =
        {
            CodeHostNotFound, CodeHostIneligible, CodeHostNotMeasured, CodeHostUnreadable,
            CodeBarTypeNotFound, CodeBarTypeAmbiguous, CodeShapeNotFound, CodeShapeAmbiguous,
            CodeHookNotFound, CodeHookAmbiguous, CodeCoverTypeNotFound, CodeCoverTypeAmbiguous,
            CodeCoverDistanceUnreadable, CodeCurveNotPlanar, CodeCurveDegenerate, CodeLayoutRefused,
            CodeShapeNotDeclared, CodeShapeStyleDiffers, CodeShapeNotAllowed, CodeShapeAllowanceUnreadable,
            CodeShapeStyleUnreadable, CodeDiameterUnreadable, CodeNormalInThePlaneOfTheBar,
            CodeAlreadyBuilt,
            RebarPlanRules.CodeSetOutsideHost, RebarPlanRules.CodeBarOutsideHost,
            RebarPlanRules.CodeHostNotMeasured, RebarPlanRules.CodeBarNotMeasured,
            RebarPlanRules.CodeNormalDegenerate
        };

        // ------------------------------------------------------------- hosts

        /// <summary>
        /// Every element a selector picks. A selector matching MANY hosts is not
        /// ambiguity - a rule applies to every beam of a type on purpose. Ambiguity
        /// is a NAME resolving to two definitions, and that is refused elsewhere.
        /// </summary>
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

            if (narrowTo != null && narrowTo.Count > 0)
                found = found.Where(e => narrowTo.Contains(Rid.Value(e.Id))).ToList();

            // Deterministic. "Whatever Revit returned first" is not an order.
            return found.OrderBy(e => Rid.Value(e.Id)).ToList();
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
            foreach (StructuralStirrupZoneRule zr in set.StirrupZoneRules)
            {
                List<StructuralRebarRule> zoneRules;
                string zoneWhy = ExpandZoneRule(doc, set, zr, narrowTo, out zoneRules);
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

                ElementId startHook, endHook;
                string hookError = Hook(doc, set, rule.Start, out startHook);
                if (hookError != null) { rows.Add(Refuse(rule, CodeHookNotFound, hookError)); continue; }
                hookError = Hook(doc, set, rule.End, out endHook);
                if (hookError != null) { rows.Add(Refuse(rule, CodeHookNotFound, hookError)); continue; }

                // Geometry, resolved once: it is declared in model coordinates and
                // does not depend on which host it lands in.
                double worstOff;
                if (!RebarPlanRules.IsPlanar(rule.CurvesMm, set.Tolerances.LengthMm, out worstOff))
                {
                    rows.Add(Refuse(rule, CodeCurveNotPlanar,
                        "the declared centreline is not planar: its points lie up to " + Mm(worstOff) +
                        " off their own best-fit plane, and the tolerance is " + Mm(set.Tolerances.LengthMm) +
                        ". A shape-driven bar must be planar. No point is named as the culprit because the " +
                        "geometry does not support naming one - displace a single corner of a rectangle and " +
                        "all four end up equally far from the fitted plane."));
                    continue;
                }

                List<Curve> curves;
                string gerr = BuildCurves(rule, out curves);
                if (gerr != null) { rows.Add(Refuse(rule, CodeCurveDegenerate, gerr)); continue; }

                XYZ normal = new XYZ(rule.NormalMm[0], rule.NormalMm[1], rule.NormalMm[2]).Normalize();

                // A SET CANNOT MARCH ALONG THE BAR IT IS MADE OF. If the declared
                // normal lies in the plane of the declared curves, the copies land
                // on top of one another and Revit builds a set that looks like one
                // bar. Nothing downstream would notice: the count is right, the
                // positions are inside the host, and the steel is all in one place.
                if (rule.CurvesMm.Count >= 2)
                {
                    double spread = 0;
                    double baseAt = RebarPlanRules.Project(rule.CurvesMm[0], rule.NormalMm);
                    foreach (double[] q in rule.CurvesMm)
                        spread = Math.Max(spread, Math.Abs(RebarPlanRules.Project(q, rule.NormalMm) - baseAt));
                    double extent = 0;
                    for (int a = 0; a < rule.CurvesMm.Count; a++)
                        for (int b2 = a + 1; b2 < rule.CurvesMm.Count; b2++)
                            extent = Math.Max(extent, RebarPlanRules.Distance(rule.CurvesMm[a], rule.CurvesMm[b2]));
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
                List<Element> hosts = Hosts(doc, rule.Host, narrowTo, out unresolved);
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
                        PointsMm = rule.CurvesMm,
                        Normal = normal,
                        Layout = layout,
                        ExpectedBarLengthMm = RebarPlanRules.CentrelineLengthMm(rule.CurvesMm, rule.Closed)
                    };

                    bool valid;
                    string eligErr = Eligible(host, out valid);
                    if (eligErr != null)
                    {
                        row.Code = CodeHostUnreadable;
                        row.Why = eligErr;
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

                    List<double[]> corners = HostCorners(host);
                    // THE SIDE THE SET MARCHES TO. MEASURED on Revit 2026: with
                    // bars_on_normal_side false the position offsets come back
                    // NEGATIVE. The plan's positions are always positive, so the fit
                    // check was measuring the wrong half of the host - passing sets
                    // that run off the near end and failing ones that fit.
                    List<double> signed = rule.BarsOnNormalSide
                        ? layout.PositionsMm
                        : layout.PositionsMm.Select(v => -v).ToList();
                    row.SignedPositionsMm = signed;
                    row.Fit = RebarPlanRules.Fit(rule.CurvesMm, corners, rule.NormalMm,
                                                 signed, set.Tolerances.LengthMm);

                    // THE SOLID, not the box. The projection above answers "is this
                    // set too long for its host" against Revit's AXIS-ALIGNED
                    // bounding box, which for a beam at an angle is bigger than the
                    // beam in every direction - so a bar half a metre out in the air
                    // passes it. This measures every declared bar against the host's
                    // own boundary, before a transaction is opened.
                    string meshWhy;
                    row.Mesh = MeshFor(host, out meshWhy);
                    row.MeshWhy = meshWhy;
                    double radiusMm = 0;
                    double? modelForRadius = SafeModelDiameter(row.BarType);
                    if (modelForRadius.HasValue) radiusMm = modelForRadius.Value * FtToMm / 2.0;
                    // `rule.Closed` MATTERS HERE. A requirement set is refused for
                    // repeating a closed shape's first point, so a declared stirrup
                    // arrives as an open polyline and used to be measured with one
                    // whole side never sampled.
                    row.Containment = RebarContainment.Check(
                        row.Mesh, rule.CurvesMm, rule.Closed, signed, rule.NormalMm, radiusMm,
                        CoverForContainment(doc, set, host), set.Tolerances.LengthMm,
                        RebarContainment.DefaultSampleStepMm);

                    if (!row.Fit.Fits)
                    {
                        row.Code = row.Fit.Code;
                        row.Why = row.Fit.Why;
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
                        row.Why = "measured against the host's own boundary rather than its bounding box: " +
                                  RebarContainment.Explain(row.Containment) + " " + row.Containment.Why;
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
                           " has no location curve to measure. State span_mm instead.";
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

            MatResult plan = MatRules.Expand(rule, mesh, id => DiameterOf(doc, set, id),
                                             HostCoverMm(host), out expanded);
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
                if (lc?.Curve == null) return null;
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
                    if (c == null || !c.DistanceMm.HasValue || c.Host == null || !c.Host.Any) continue;
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

        private static string BuildCurves(StructuralRebarRule rule, out List<Curve> curves)
        {
            curves = new List<Curve>();
            List<double[]> p = rule.CurvesMm;
            try
            {
                for (int i = 1; i < p.Count; i++)
                    curves.Add(Line.CreateBound(Ft(p[i - 1]), Ft(p[i])));
                if (rule.Closed) curves.Add(Line.CreateBound(Ft(p[p.Count - 1]), Ft(p[0])));
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
            if (!r.Ok)
            {
                o["code"] = r.Code;
                o["why"] = r.Why;
                return o;
            }
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
            o["curve_mm"] = PointArray(r.Rule.CurvesMm);
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
            if (r.Mesh == null && r.MeshWhy != null) o["containment"]["boundary_why"] = r.MeshWhy;
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
