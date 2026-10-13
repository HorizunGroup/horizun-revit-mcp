// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_resolve_clash: SLEEVES AND STRUCTURAL OPENINGS, for a finding a move
// cannot resolve (ResolveClashCommand.cs covers the move path). Pure geometry
// lives in Core/SleeveRules.cs; this file is the Revit half.
//
//   propose_opening  READ-ONLY. One side of the finding must be an MEP run
//                     (pipe/duct/conduit/cable tray), the other a host wall,
//                     floor, roof, ceiling, or structural framing/column. The
//                     crossing is where the run's centreline passes through the
//                     host's SOLID (Solid.IntersectWithCurve; the bounding box is
//                     only a prefilter). The size is the run's OUTER section
//                     (outside diameter + insulation) projected through the host's
//                     measured thickness along the run (a skewed crossing is
//                     wider), plus clearance_mm. A STRUCTURAL wall/floor is cut
//                     only with allow_structural=true (PenetrationRules' opt-in).
//                     Framing/columns get sleeve-only: the API could cut them
//                     (NewOpening(member, profile, eRefFace)); this operation
//                     deliberately does not - refused by name (member_cut_not_offered).
//   apply_opening    dry_run (default) -> token -> ONE TransactionGroup. The
//                     token binds each proposal's crossing and size; apply
//                     re-derives them and refuses a drift. The run must still
//                     meet the host on solids before anything is written
//                     (nothing_to_cut otherwise). Either cuts the host
//                     (NewOpening(wall, pt1, pt2) / NewOpening(host, CurveArray,
//                     false) - a vertical cut) or, with sleeve_type_id, places that
//                     caller-supplied family at the crossing (org-neutral: no
//                     family ships with this bridge). Kept ONLY when, re-read
//                     after the commit: the element exists, a clearance envelope
//                     (the run's outer section + clearance/2, along the run) meets
//                     no host or sleeve material, the cut host no longer meets the
//                     run (a sleeve's host_cut is MEASURED, never assumed), a
//                     level-based sleeve sits on the run's level at the crossing,
//                     and no new clash appeared around it; otherwise the whole
//                     group rolls back. The ledger finding gets an
//                     "opening_requested" history entry and STAYS OPEN.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ResolveClashCommand
    {
        private static readonly string[] RunCategories =
        {
            "OST_PipeCurves", "OST_DuctCurves", "OST_Conduit", "OST_CableTray", "OST_FlexPipeCurves", "OST_FlexDuctCurves"
        };
        private static bool IsRunCategory(string bic) => bic != null && Array.IndexOf(RunCategories, bic) >= 0;

        private sealed class OpeningPlan
        {
            public string FindingId; public Element Mep; public Element Host; public string HostKind; public string Route;
            public double[] CrossingMm, EntryMm, ExitMm, RunDirection;        // RunDirection: unit
            public bool Round; public double SectionHalfMm; public double[] SectionAxis1, SectionAxis2; public bool AxesKnown; public string OuterBasis;
            public double[] AxisA, AxisB; public double ThicknessMm;          // opening axes in the host's frame; thickness along its normal
            public double OpeningWidthMm, OpeningHeightMm; public string Shape;
            public bool HostStructural;
            // Filled while applying.
            public long CreatedId = -1; public string Kind; public bool HostCut; public string Placement; public long LevelId = -1; public bool PointPlaced;
        }

        // ---- propose_opening -------------------------------------------------------

        private CommandResult ProposeOpening(UIApplication app, JObject request, double clearance)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            CommandResult readRefusal = DocumentGate.ReadGuard(doc, request, Name);
            if (readRefusal != null) return readRefusal;
            List<string> ids = (request["finding_ids"] as JArray ?? new JArray()).Select(t => (string)t).ToList();
            if (ids.Count == 0 || ids.Count > 50) return CommandResult.Fail("finding_ids must list 1..50 findings (horizun_coordination list).");
            bool allowStructural = request.Value<bool?>("allow_structural") == true;
            string ledgerPath = CoordinationLedger.PathFor(doc.Title, UndoCapture.SafePath(doc));
            Dictionary<string, CoordinationFinding> ledger = CoordinationLedger.Load(ledgerPath, out string _);

            var rows = new JArray();
            var actions = new JArray();
            foreach (string id in ids)
            {
                var row = new JObject { ["finding_id"] = id, ["status"] = "report_only" };
                rows.Add(row);
                if (!ledger.TryGetValue(id, out CoordinationFinding f))
                { row["status"] = "unknown_finding"; row["reason"] = "not in this document's ledger"; continue; }
                if (f.Status == CoordinationRules.StatusResolvedByModel || f.Status == CoordinationRules.StatusClosedByDecision)
                { row["status"] = "not_open"; row["reason"] = "finding is " + f.Status; continue; }
                JObject action = PlanOpening(doc, f, clearance, allowStructural, row);
                if (action == null) continue;
                row["status"] = "proposed";
                actions.Add(action);
            }
            var result = new JObject
            {
                ["read_only"] = true, ["proposals"] = rows, ["proposed"] = actions.Count, ["clearance_mm"] = clearance,
                ["note"] = "Candidates only: nothing was written. Wall: rectangular cut; floor/roof/ceiling: vertical boundary cut (round for a vertical round run); " +
                           "a structural wall/floor needs allow_structural=true; framing/columns: a cut is not offered (member_cut_not_offered) and apply_opening needs sleeve_type_id."
            };
            if (actions.Count > 0)
            {
                var next = new JObject
                {
                    ["operation"] = "apply_opening", ["target_document"] = doc.Title, ["clearance_mm"] = clearance,
                    ["proposals"] = actions, ["dry_run"] = true
                };
                if (allowStructural) next["allow_structural"] = true;
                result["next_arguments"] = next;
            }
            return CommandResult.Ok(result);
        }

        /// <summary>One finding's opening/sleeve proposal, filled onto `row`; null when report-only.</summary>
        private static JObject PlanOpening(Document doc, CoordinationFinding f, double clearance, bool allowStructural, JObject row)
        {
            if (!ClashResolveRules.ParseSide(f.SideA, out bool hostA, out string uidA) ||
                !ClashResolveRules.ParseSide(f.SideB, out bool hostB, out string uidB))
            { row["code"] = ClashResolveRules.CodeNoGeometry; row["reason"] = "the finding's sides cannot be parsed"; return null; }
            if (!hostA || !hostB)
            { row["code"] = ClashResolveRules.CodeLinked; row["reason"] = "both sides must be host elements to plan an opening - a linked side is resolved in its own model"; return null; }
            Element a = doc.GetElement(uidA), b = doc.GetElement(uidB);
            if (a == null || b == null)
            { row["code"] = ClashResolveRules.CodeNoGeometry; row["reason"] = "an element of the pair no longer exists in the host"; return null; }
            string error = BuildPlan(doc, f.Id, a, b, clearance, allowStructural, out OpeningPlan p, out string code);
            if (p != null) { row["host_kind"] = p.HostKind; row["route"] = p.Route; row["host_structural"] = p.HostStructural; }
            if (error != null) { row["code"] = code; row["reason"] = error; return null; }
            row["mep_element_id"] = Rid.Value(p.Mep.Id);
            row["host_element_id"] = Rid.Value(p.Host.Id);
            row["crossing_point_mm"] = new JArray(p.CrossingMm);
            row["run_direction"] = new JArray(p.RunDirection.Select(v => Math.Round(v, 6)));
            row["outer_section"] = p.OuterBasis;
            row["host_thickness_mm"] = Math.Round(p.ThicknessMm, 1);
            row["opening_width_mm"] = p.OpeningWidthMm; row["opening_height_mm"] = p.OpeningHeightMm; row["shape"] = p.Shape;
            if (p.Route == SleeveRules.RouteSleeveOnly)
            {
                row["cut_refused"] = new JObject
                {
                    ["code"] = SleeveRules.CodeCutRefused,
                    ["reason"] = "this operation does not cut a " + p.HostKind + " (the API could, through NewOpening(member, profile, eRefFace)); " +
                                 "a structural member's penetration is an engineer's sized decision, so only a sleeve family is proposed"
                };
                row["prediction"] = "apply_opening needs sleeve_type_id and allow_structural=true to place that family at the crossing; the member is modified only by the family's own void.";
            }
            else
            {
                row["prediction"] = SleeveRules.Describe(p.HostKind, p.OpeningWidthMm, p.OpeningHeightMm, p.Shape) +
                                    (p.Route == SleeveRules.RouteWallOpening ? " (a wall opening is always rectangular in the API)" : " (vertical cut)") +
                                    "; apply re-detects the run against the cut host on solids and checks a clearance envelope of the run's outer section + " +
                                    (clearance / 2).ToString("0.#", CultureInfo.InvariantCulture) + " mm meets no host material.";
            }
            var action = new JObject
            {
                ["finding_id"] = f.Id, ["mep_element_id"] = Rid.Value(p.Mep.Id), ["host_element_id"] = Rid.Value(p.Host.Id),
                ["host_kind"] = p.HostKind, ["route"] = p.Route,
                // Bound by the confirmation token: apply refuses a live re-derivation that drifted from this.
                ["crossing_point_mm"] = new JArray(p.CrossingMm), ["opening_width_mm"] = p.OpeningWidthMm,
                ["opening_height_mm"] = p.OpeningHeightMm, ["shape"] = p.Shape
            };
            return action;
        }

        /// <summary>Shared by propose and apply: always derived fresh from the live model.</summary>
        private static string BuildPlan(Document doc, string findingId, Element a, Element b, double clearance, bool allowStructural,
                                        out OpeningPlan plan, out string code)
        {
            plan = null; code = ClashResolveRules.CodeNoGeometry;
            string bicA = CategoryBic(a), bicB = CategoryBic(b);
            bool runA = IsRunCategory(bicA), runB = IsRunCategory(bicB);
            if (runA == runB)
            {
                code = runA ? ClashResolveRules.CodeBothMovable : ClashResolveRules.CodeNotMovable;
                return runA ? "both sides are MEP runs; an opening is planned for a run-against-host pair, not run-against-run"
                            : "neither side is an MEP run (pipe/duct/conduit/cable tray)";
            }
            Element mep = runA ? a : b, host = runA ? b : a;
            string hostBic = runA ? bicB : bicA;
            string hostKind = SleeveRules.HostKindOf(hostBic);
            string route = SleeveRules.RouteFor(hostKind);
            plan = new OpeningPlan { FindingId = findingId, Mep = mep, Host = host, HostKind = hostKind, Route = route };
            if (route == SleeveRules.RouteRefused)
            { code = SleeveRules.CodeHostUnsupported; return "the host category " + (hostBic ?? "unknown") + " has no documented opening or sleeve route"; }
            plan.HostStructural = SleeveHostIsStructural(host);
            if (route != SleeveRules.RouteSleeveOnly &&
                !PenetrationRules.HostPermitted(true, plan.HostStructural, allowStructural, out string permitCode, out string _))
            {
                code = permitCode;
                return "the host is STRUCTURAL: cutting it is an engineering decision - pass allow_structural=true to record that a person approved it";
            }
            ResolveRun run = Run(mep);
            if (run == null) return "the run has no straight centreline";
            double[] u = SleeveRules.Unit(new[] { run.End[0] - run.Start[0], run.End[1] - run.Start[1], run.End[2] - run.Start[2] });
            if (u == null) return "the run has zero length";
            plan.RunDirection = u;
            string dirCode = SleeveRules.DirectionRefusal(route, u);
            if (dirCode != null)
            {
                code = dirCode;
                return dirCode == SleeveRules.CodeTooSteepForWall ? "the run is too steep for a wall opening (a wall is cut for a near-horizontal run)"
                                                                  : "the run is too flat for a floor/roof/ceiling opening (it runs inside the slab rather than through it)";
            }
            if (!OuterSection(doc, mep, plan)) { code = SleeveRules.CodeNoProfile; return "the run has no readable profile"; }
            double[] n;
            if (route == SleeveRules.RouteWallOpening)
            {
                double[] along = null;
                if (host is Wall wall && (wall.Location as LocationCurve)?.Curve is Line wl)
                {
                    XYZ d = wl.GetEndPoint(1) - wl.GetEndPoint(0);
                    along = SleeveRules.Unit(new[] { d.X, d.Y, 0.0 });
                }
                if (along == null)
                { code = SleeveRules.CodeCurvedWall; return "only a straight wall has one opening plane; a curved wall is refused rather than cut on a guessed tangent"; }
                plan.AxisA = along; plan.AxisB = new double[] { 0, 0, 1 }; n = new[] { -along[1], along[0], 0.0 };
            }
            else if (route == SleeveRules.RouteFloorOpening)
            {
                // Vertical cut: the opening is a plan shape, so its axes are plan X/Y and the host's
                // "thickness" is the vertical extent of the run inside it (a sloped roof included).
                plan.AxisA = new double[] { 1, 0, 0 }; plan.AxisB = new double[] { 0, 1, 0 }; n = new double[] { 0, 0, 1 };
            }
            else { plan.AxisA = plan.SectionAxis1; plan.AxisB = plan.SectionAxis2; n = u; }
            ResolveBox hostBox = Box(host.get_BoundingBox(null));
            if (hostBox == null) { code = SleeveRules.CodeNoHostBox; return "the host has no bounding box in this document"; }
            if (!SleeveRules.LineBoxIntersect(run.Start, run.End, hostBox, out double[] _, out double[] _, out string crossCode))
            { code = crossCode; return "the run's centreline does not reach the host's bounding box"; }
            if (!SolidCrossing(doc, host, run, out double[] entry, out double[] exit, out bool readable))
            {
                code = readable ? SleeveRules.CodeNoCrossing : SleeveRules.CodeHostSolidUnreadable;
                return readable ? "the run's centreline does not pass through the host's solid (it may already pass through an opening)"
                                : "the host's solid could not be read, so the crossing cannot be measured";
            }
            plan.EntryMm = entry; plan.ExitMm = exit; plan.CrossingMm = SleeveRules.Midpoint(entry, exit);
            plan.ThicknessMm = Math.Abs(SleeveRules.Dot(new[] { exit[0] - entry[0], exit[1] - entry[1], exit[2] - entry[2] }, n));
            double[] e1 = plan.AxesKnown ? plan.SectionAxis1 : null, e2 = plan.AxesKnown ? plan.SectionAxis2 : null;
            double ha = SleeveRules.FootprintHalfExtent(u, n, plan.AxisA, e1, e2, plan.Round, plan.SectionHalfMm, plan.ThicknessMm);
            double hb = SleeveRules.FootprintHalfExtent(u, n, plan.AxisB, e1, e2, plan.Round, plan.SectionHalfMm, plan.ThicknessMm);
            if (double.IsNaN(ha) || double.IsNaN(hb))
            { code = SleeveRules.CodeParallel; return "the run is nearly parallel to the host's face; its footprint through the host is unbounded"; }
            // Rounded UP to 0.1 mm: a size is never reported smaller than the one that was derived.
            plan.OpeningWidthMm = Math.Ceiling((2 * ha + clearance) * 10) / 10;
            plan.OpeningHeightMm = Math.Ceiling((2 * hb + clearance) * 10) / 10;
            bool circle = plan.Round && route == SleeveRules.RouteFloorOpening && Math.Abs(u[2]) > 0.999;
            if (circle) plan.OpeningWidthMm = plan.OpeningHeightMm = Math.Max(plan.OpeningWidthMm, plan.OpeningHeightMm);
            plan.Shape = circle || (plan.Round && route == SleeveRules.RouteSleeveOnly) ? SleeveRules.ShapeRound : SleeveRules.ShapeRect;
            return null;
        }

        /// <summary>
        /// The run's OUTER section: a pipe's/conduit's outside diameter when larger than the
        /// connector's nominal size, plus twice the thickest insulation; a rectangular/oval
        /// section is squared to its larger side on the connector's own axes (or, unread, sized
        /// for every rotation).
        /// </summary>
        private static bool OuterSection(Document doc, Element mep, OpeningPlan plan)
        {
            if (!MepFacts.TryProfile(mep, out string shape, out double w, out double h)) return false;
            bool round = shape == "round";
            string basis = "connector size";
            if (round)
            {
                double od = ParamFeet(mep, BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
                if (od <= 0) od = ParamFeet(mep, BuiltInParameter.RBS_CONDUIT_OUTER_DIAM_PARAM);
                if (od > w) { w = h = od; basis = "outside diameter"; }
            }
            double insulation = 0;
            try
            {
                foreach (ElementId id in InsulationLiningBase.GetInsulationIds(doc, mep.Id))
                    if (doc.GetElement(id) is InsulationLiningBase il && il.Thickness > insulation) insulation = il.Thickness;
            }
            catch { /* no insulation readable: the bare outer section stands, and the envelope check measures the real cut */ }
            if (insulation > 0)
            {
                w += 2 * insulation; h += 2 * insulation;
                basis += " + insulation " + (insulation * MmPerFoot).ToString("0.#", CultureInfo.InvariantCulture) + " mm";
            }
            plan.Round = round;
            plan.SectionHalfMm = (round ? w : Math.Max(w, h)) * MmPerFoot / 2;
            double[] e1 = null, e2 = null;
            if (!round) ConnectorAxes(mep, plan.RunDirection, out e1, out e2);
            plan.AxesKnown = e1 != null;
            if (e1 == null) SleeveRules.DefaultSectionAxes(plan.RunDirection, out e1, out e2);
            plan.SectionAxis1 = e1; plan.SectionAxis2 = e2;
            if (!round) basis += plan.AxesKnown ? ", squared to the larger side on the connector's axes" : ", section rotation unread: sized for every rotation";
            plan.OuterBasis = (plan.SectionHalfMm * 2).ToString("0.#", CultureInfo.InvariantCulture) + " mm (" + basis + ")";
            return true;
        }

        private static double ParamFeet(Element e, BuiltInParameter bip)
        {
            try { Parameter p = e.get_Parameter(bip); return p != null && p.HasValue && p.StorageType == StorageType.Double ? p.AsDouble() : 0; }
            catch { return 0; }
        }

        /// <summary>The section axes of an end connector, when they are perpendicular to the run (MEASURE LIVE).</summary>
        private static void ConnectorAxes(Element mep, double[] u, out double[] e1, out double[] e2)
        {
            e1 = null; e2 = null;
            try
            {
                ConnectorSet set = (mep as MEPCurve)?.ConnectorManager?.Connectors;
                if (set == null) return;
                foreach (Connector c in set)
                {
                    if (c.ConnectorType != ConnectorType.End) continue;
                    Transform cs = c.CoordinateSystem;
                    double[] x = { cs.BasisX.X, cs.BasisX.Y, cs.BasisX.Z }, y = { cs.BasisY.X, cs.BasisY.Y, cs.BasisY.Z };
                    if (Math.Abs(SleeveRules.Dot(x, u)) > 0.01 || Math.Abs(SleeveRules.Dot(y, u)) > 0.01) continue;
                    e1 = x; e2 = y; return;
                }
            }
            catch { e1 = null; e2 = null; }
        }

        private static bool SleeveHostIsStructural(Element host)
        {
            try
            {
                if (host is Wall wall) return wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;
                if (host is Floor floor) return floor.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;
                string bic = CategoryBic(host);
                return SleeveRules.IsStructuralCategory(bic);
            }
            catch { return true; }   // unreadable is treated as structural: the opt-in is the safe side
        }

        /// <summary>
        /// Where the run's centreline is INSIDE the host's solids: the first entry and last exit
        /// along the run (mm). False with readable=true when the line never enters the solid.
        /// </summary>
        private static bool SolidCrossing(Document doc, Element host, ResolveRun run, out double[] entryMm, out double[] exitMm, out bool readable)
        {
            entryMm = null; exitMm = null; readable = true;
            var s = new XYZ(run.Start[0] / MmPerFoot, run.Start[1] / MmPerFoot, run.Start[2] / MmPerFoot);
            var e = new XYZ(run.End[0] / MmPerFoot, run.End[1] / MmPerFoot, run.End[2] / MmPerFoot);
            if ((e - s).GetLength() < 1e-9) return false;
            List<Solid> solids = Solids(doc, Rid.Value(host.Id), new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine }, new Dictionary<long, List<Solid>>());
            if (solids == null || solids.Count == 0) { readable = false; return false; }
            Line line = Line.CreateBound(s, e);
            XYZ dir = (e - s).Normalize();
            double tMin = double.MaxValue, tMax = double.MinValue;
            var inside = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
            foreach (Solid solid in solids)
            {
                SolidCurveIntersection hit;
                try { hit = solid.IntersectWithCurve(line, inside); }
                catch { readable = false; continue; }
                if (hit == null) continue;
                for (int i = 0; i < hit.SegmentCount; i++)
                {
                    Curve c = hit.GetCurveSegment(i);
                    foreach (XYZ q in new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                    {
                        double t = (q - s).DotProduct(dir);
                        tMin = Math.Min(tMin, t); tMax = Math.Max(tMax, t);
                    }
                }
            }
            if (!(tMax > tMin)) return false;
            entryMm = Mm(s + dir * tMin); exitMm = Mm(s + dir * tMax);
            return true;
        }

        // ---- apply_opening ----------------------------------------------------------

        private CommandResult ApplyOpening(UIApplication app, JObject request, double clearance)
        {
            GateResult gate = DocumentGate.ForMutation(app, request, Name);
            if (!gate.Ok) return gate.Refusal;
            Document doc = gate.Document;
            JArray input = request["proposals"] as JArray;
            if (input == null || input.Count == 0 || input.Count > 50) return CommandResult.Fail("proposals must list 1..50 entries from operation=propose_opening.");
            long? sleeveTypeId = request.Value<long?>("sleeve_type_id");
            string approvalParam = request.Value<string>("approval_parameter");
            string approvalValue = request.Value<string>("approval_value") ?? "";
            bool allowStructural = request.Value<bool?>("allow_structural") == true;
            string ledgerPath = CoordinationLedger.PathFor(doc.Title, UndoCapture.SafePath(doc));
            Dictionary<string, CoordinationFinding> ledger = CoordinationLedger.Load(ledgerPath, out string ledgerTitle);

            var plans = new List<OpeningPlan>(); var errors = new JArray(); var claimedMep = new HashSet<long>(); var claimedHost = new HashSet<string>();
            for (int i = 0; i < input.Count; i++)
            {
                string error = ParseOpening(doc, input[i] as JObject, ledger, clearance, allowStructural, claimedMep, claimedHost, out OpeningPlan p, out string code);
                if (error != null) errors.Add(new JObject { ["index"] = i, ["code"] = code, ["error"] = error }); else plans.Add(p);
            }
            FamilySymbol sleeveSymbol = null;
            if (sleeveTypeId != null)
            {
                sleeveSymbol = Rid.CanRepresent(sleeveTypeId.Value) ? doc.GetElement(Rid.Make(sleeveTypeId.Value)) as FamilySymbol : null;
                if (sleeveSymbol == null) errors.Add(new JObject { ["code"] = "sleeve_type_not_found", ["error"] = "sleeve_type_id does not resolve to a family type (symbol) in this document." });
            }
            foreach (OpeningPlan p in plans)
            {
                if (p.Route == SleeveRules.RouteSleeveOnly && sleeveSymbol == null)
                    errors.Add(new JObject
                    {
                        ["finding_id"] = p.FindingId, ["code"] = SleeveRules.CodeCutRefused,
                        ["error"] = "this operation does not cut a " + p.HostKind + "; pass sleeve_type_id (a sleeve family) instead"
                    });
                // A sleeve family's void may cut its host, so a structural host needs the same opt-in as a cut.
                else if (sleeveSymbol != null && p.HostStructural && !allowStructural)
                    errors.Add(new JObject
                    {
                        ["finding_id"] = p.FindingId, ["code"] = PenetrationRules.CodeStructuralHostRequiresOptIn,
                        ["error"] = "the " + p.HostKind + " is STRUCTURAL and a sleeve family may cut it with its void: pass allow_structural=true to record that a person approved it"
                    });
            }

            bool dryRun = request["dry_run"] == null || request.Value<bool>("dry_run");
            string planHash = DocumentGate.PlanHash(request, "proposals", "clearance_mm", "sleeve_type_id", "approval_parameter", "approval_value", "allow_structural");
            if (dryRun)
            {
                var result = new JObject
                {
                    ["dry_run"] = true, ["transaction_status"] = "not_started", ["valid"] = plans.Count, ["invalid"] = errors.Count,
                    ["errors"] = errors,
                    ["plan"] = new JArray(plans.Select(p => (JToken)new JObject
                    {
                        ["finding_id"] = p.FindingId, ["mep_element_id"] = Rid.Value(p.Mep.Id), ["host_element_id"] = Rid.Value(p.Host.Id),
                        ["host_kind"] = p.HostKind, ["route"] = p.Route, ["host_structural"] = p.HostStructural, ["crossing_point_mm"] = new JArray(p.CrossingMm),
                        ["outer_section"] = p.OuterBasis, ["opening_width_mm"] = p.OpeningWidthMm, ["opening_height_mm"] = p.OpeningHeightMm, ["shape"] = p.Shape,
                        ["placement"] = sleeveSymbol != null ? "sleeve_family" : (p.Route == SleeveRules.RouteSleeveOnly ? "refused_no_cut_route" : "native_opening")
                    })),
                    ["note"] = "Nothing was created. Apply needs the run to still meet the host on solids, then keeps the result only after a clearance-envelope " +
                               "and solid re-detection check around the crossing; the ledger gets an opening_requested entry and the finding stays open."
                };
                ApplicationOutcome.StampRehearsal(result, input.Count, errors.Count, 0, 0);
                DocumentGate.StampConfirmation(result, gate, Name, planHash, errors.Count == 0,
                    errors.Count == 0 ? "the token binds the proposals (with their crossing and size), clearance and sleeve/approval/structural arguments" : "no token while any proposal is invalid");
                return CommandResult.Ok(result);
            }
            if (errors.Count > 0) return CommandResult.Fail("Invalid proposals; nothing ran: " + errors.ToString(Formatting.None));
            CommandResult refusal = DocumentGate.RequireConfirmation(app, gate, request, Name, planHash);
            if (refusal != null) return refusal;

            // The neighbourhood of every crossing, measured before AND after over the same set:
            // the entry-exit segment grown by the opening's larger side plus clearance.
            var region = new Dictionary<long, Element>();
            foreach (OpeningPlan p in plans)
            {
                double grow = Math.Max(p.OpeningWidthMm, p.OpeningHeightMm) + clearance;
                var seg = new ResolveBox(Math.Min(p.EntryMm[0], p.ExitMm[0]), Math.Min(p.EntryMm[1], p.ExitMm[1]), Math.Min(p.EntryMm[2], p.ExitMm[2]),
                                         Math.Max(p.EntryMm[0], p.ExitMm[0]), Math.Max(p.EntryMm[1], p.ExitMm[1]), Math.Max(p.EntryMm[2], p.ExitMm[2]));
                foreach (Element e in Neighbours(doc, seg, grow)) region[Rid.Value(e.Id)] = e;
                region[Rid.Value(p.Host.Id)] = p.Host; region[Rid.Value(p.Mep.Id)] = p.Mep;
            }
            var runIds = new HashSet<long>(plans.Select(p => Rid.Value(p.Mep.Id)));
            List<string> before = Detect(doc, runIds, region.Keys, out bool completeBefore);
            // Nothing to cut unless the run still meets the host NOW: a second pass over the same
            // finding (the finding stays open by design) must not stack a second opening.
            foreach (OpeningPlan p in plans)
                if (!before.Contains(ClashResolveRules.PairKey(Rid.Value(p.Mep.Id), Rid.Value(p.Host.Id))))
                    return CommandResult.FailWithDetail(completeBefore
                        ? "Nothing ran: finding " + p.FindingId + " - the run no longer meets the host on solids (an opening or sleeve may already be there)."
                        : "Nothing ran: finding " + p.FindingId + " - the run-host contact could not be measured before writing.",
                        new JObject { ["code"] = completeBefore ? SleeveRules.CodeNothingToCut : "contact_unmeasured", ["finding_id"] = p.FindingId, ["state"] = "not_started" });

            string txName = "Horizun: resolve clash (opening/sleeve)";
            PostconditionCheck postconditions = null; string verdict = null; var fresh = new List<string>();
            var linksSkipped = new List<string>();
            using (var group = new TransactionGroup(doc, txName))
            {
                RevitErrorRecorder said = null;
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("transaction group did not start");
                    using (var tx = new Transaction(doc, txName))
                    {
                        said = RevitErrorRecorder.On(tx);
                        tx.Start();
                        foreach (OpeningPlan p in plans)
                        {
                            string why = null;
                            if (!CreateOpeningOrSleeve(doc, p, sleeveSymbol, out string failReason)) why = "Nothing created for finding " + p.FindingId + ": " + failReason;
                            else if (!string.IsNullOrEmpty(approvalParam))
                            {
                                Parameter par = doc.GetElement(Rid.Make(p.CreatedId))?.LookupParameter(approvalParam);
                                string markWhy = par == null ? "the created " + p.Kind + " has no parameter named '" + approvalParam + "'"
                                               : par.IsReadOnly ? "'" + approvalParam + "' is read-only on the created " + p.Kind
                                               : par.StorageType != StorageType.String ? "'" + approvalParam + "' is not a text parameter"
                                               : (par.Set(approvalValue) ? null : "Revit refused to set '" + approvalParam + "'");
                                if (markWhy != null) why = "the approval mark could not be written - " + markWhy + ".";
                            }
                            if (why != null)
                            {
                                Guard.RollBack(tx);
                                Guard.RollbackResult rb = Guard.RollBack(group);
                                return CommandResult.FailWithDetail(why + " " + PlanFailure.SingleTransactionOutcome(true, rb.StatusName, "nothing was created"),
                                    new JObject { ["state"] = rb.Confirmed ? "rolled_back" : "uncertain", ["rollback_status"] = rb.StatusName });
                            }
                        }
                        Guard.Commit(tx, txName);
                    }

                    // Re-read INSIDE the group: every check below can still roll everything back.
                    var keys = new List<string>();
                    foreach (OpeningPlan p in plans)
                    {
                        keys.Add("created:" + p.FindingId); keys.Add("clearance:" + p.FindingId);
                        if (!string.IsNullOrEmpty(approvalParam)) keys.Add("approval:" + p.FindingId);
                        keys.Add((p.Kind == "opening" ? "host_cleared:" : "host_cut:") + p.FindingId);
                        if (p.LevelId >= 0) keys.Add("level:" + p.FindingId);
                        if (p.PointPlaced) keys.Add("at_crossing:" + p.FindingId);
                    }
                    keys.Add("no_new_clash");
                    postconditions = new PostconditionCheck(keys.ToArray());
                    bool ok = true;
                    var solidOptions = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
                    // A void-only sleeve has no solid to clash with, so only solid-bearing sleeves are
                    // movers; an UNREADABLE sleeve (null) is not void-only - it leaves the measurement incomplete.
                    var sleeveSolids = new Dictionary<long, List<Solid>>(); bool sleevesReadable = true;
                    foreach (OpeningPlan p in plans.Where(x => x.Kind == "sleeve"))
                    {
                        List<Solid> ss = Solids(doc, p.CreatedId, solidOptions, new Dictionary<long, List<Solid>>());
                        if (ss == null) sleevesReadable = false;
                        sleeveSolids[p.CreatedId] = ss;
                    }
                    var sleeveIds = new HashSet<long>(sleeveSolids.Where(kv => kv.Value != null && kv.Value.Count > 0).Select(kv => kv.Key));
                    var movers = new HashSet<long>(runIds.Concat(sleeveIds));
                    var regionAfter = region.Keys.Concat(sleeveIds).Distinct().ToList();
                    List<string> after = Detect(doc, movers, regionAfter, out bool completeAfter);
                    completeAfter &= sleevesReadable;
                    if (sleeveIds.Count > 0)
                    {
                        after = after.Concat(DetectLinks(doc, sleeveIds, out bool completeLinks, out linksSkipped)).ToList();
                        completeAfter &= completeLinks;
                    }
                    foreach (OpeningPlan p in plans)
                    {
                        Element el = doc.GetElement(Rid.Make(p.CreatedId));
                        bool exists = el != null;
                        postconditions.Record("created:" + p.FindingId, p.Kind, exists ? p.Kind : "missing", exists);
                        ok &= exists;
                        long hostId = Rid.Value(p.Host.Id);
                        bool runMeetsHost = after.Contains(ClashResolveRules.PairKey(Rid.Value(p.Mep.Id), hostId));
                        if (p.Kind == "opening")
                        {
                            if (completeAfter) postconditions.Record("host_cleared:" + p.FindingId, "absent", runMeetsHost ? "present" : "absent", !runMeetsHost);
                            else postconditions.Unreadable("host_cleared:" + p.FindingId, "absent", "solid re-detection incomplete");
                            p.HostCut = completeAfter && !runMeetsHost;
                            ok &= p.HostCut;
                        }
                        else
                        {
                            // MEASURED, never assumed: a hosted sleeve whose void cuts on hosting counts as
                            // cut here although AddInstanceVoidCut never ran for it.
                            p.HostCut = completeAfter && !runMeetsHost;
                            if (completeAfter) postconditions.Record("host_cut:" + p.FindingId, "measured",
                                p.HostCut ? "cut: the run no longer meets the host" : "not cut: the run still meets the host", true);
                            else { postconditions.Unreadable("host_cut:" + p.FindingId, "measured", "solid re-detection incomplete"); ok = false; }
                        }
                        ok &= RecordClearance(doc, p, clearance, el, sleeveIds, solidOptions, postconditions);
                        if (!string.IsNullOrEmpty(approvalParam))
                        {
                            string found = el?.LookupParameter(approvalParam)?.AsString();
                            bool marked = found == approvalValue;
                            postconditions.Record("approval:" + p.FindingId, approvalValue, found, marked);
                            ok &= marked;
                        }
                        if (p.LevelId >= 0)
                        {
                            long foundLevel = el != null ? Rid.Value(el.LevelId) : -1;
                            postconditions.Record("level:" + p.FindingId, p.LevelId, foundLevel, foundLevel == p.LevelId);
                            ok &= foundLevel == p.LevelId;
                        }
                        if (p.PointPlaced)
                        {
                            XYZ at = (el?.Location as LocationPoint)?.Point;
                            if (at == null) { postconditions.Unreadable("at_crossing:" + p.FindingId, "within 1 mm of the crossing", "the sleeve has no location point"); ok = false; }
                            else
                            {
                                double[] atMm = Mm(at);
                                double dist = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (atMm[i] - p.CrossingMm[i]) * (atMm[i] - p.CrossingMm[i])));
                                bool near = dist <= 1.0;
                                postconditions.Record("at_crossing:" + p.FindingId, "within 1 mm of the crossing",
                                    dist.ToString("0.0", CultureInfo.InvariantCulture) + " mm away", near);
                                ok &= near;
                            }
                        }
                    }
                    fresh = ClashResolveRules.NewPairs(before, after);
                    foreach (OpeningPlan p in plans.Where(x => x.Kind == "sleeve"))
                        fresh = SleeveRules.UnintendedNewPairs(new string[0], fresh, p.CreatedId, Rid.Value(p.Host.Id));
                    if (completeAfter && completeBefore) postconditions.Record("no_new_clash", new JArray(), new JArray(fresh), fresh.Count == 0);
                    else postconditions.Unreadable("no_new_clash", new JArray(), "solid detection before or after was incomplete");
                    ok &= completeAfter && completeBefore && fresh.Count == 0;
                    if (!ok)
                    {
                        Guard.RollbackResult rb = Guard.RollBack(group);
                        return CommandResult.FailWithDetail("Rolled back, nothing kept: a postcondition failed after the opening/sleeve was built.", new JObject
                        {
                            ["state"] = rb.Confirmed ? "rolled_back" : "uncertain", ["rollback_status"] = rb.StatusName,
                            ["new_clashes"] = new JArray(fresh), ["postconditions"] = postconditions.ToJson(),
                            ["links_skipped"] = new JArray(linksSkipped)
                        });
                    }
                    verdict = "every opening/sleeve exists, a clearance envelope of the run's outer section + " +
                              (clearance / 2).ToString("0.#", CultureInfo.InvariantCulture) + " mm meets no host or sleeve material, " +
                              (plans.Any(p => p.Kind == "opening") ? "every cut host no longer meets its run on solids, " : "") + "and no new clash appeared";
                    Guard.Assimilate(group, txName);
                }
                catch (Exception ex)
                {
                    string rb = PlanFailure.NotAttempted; bool attempted = false;
                    if (group.GetStatus() == TransactionStatus.Started) { attempted = true; rb = Guard.RollBack(group).StatusName; }
                    return CommandResult.Fail("Opening/sleeve creation failed: " + ex.Message + (said == null ? "" : said.Said()) + " " +
                        PlanFailure.SingleTransactionOutcome(attempted, rb, "nothing was created"));
                }
            }

            foreach (OpeningPlan p in plans)
                if (doc.GetElement(Rid.Make(p.CreatedId)) == null)
                    return CommandResult.FailWithDetail("The group was kept but element " + p.CreatedId + " does not re-read; inspect the model.",
                        new JObject { ["state"] = "uncertain" });

            JObject undo = UndoCapture.Record(doc, Name, new List<UndoEntry>
            {
                UndoCapture.Entry(doc, "created", plans.Select(p => p.CreatedId), new JObject(), new JObject())
            });

            // The ledger learns that an opening was REQUESTED - never that the clash is resolved.
            string ledgerNote = null; var requested = new JArray();
            try
            {
                string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                foreach (OpeningPlan p in plans)
                    if (ledger.TryGetValue(p.FindingId, out CoordinationFinding f))
                    {
                        CoordinationRules.AppendEvent(f, "opening_requested",
                            "horizun_resolve_clash apply_opening created " + p.Kind + " " + p.CreatedId + " (" + p.Placement + ") at the crossing with host " +
                            Rid.Value(p.Host.Id) + (p.HostCut ? "; measured: the host is cut" : "; measured: the run still meets the host") +
                            "; the finding stays open until a detection run measures it", now);
                        requested.Add(p.FindingId);
                    }
                CoordinationLedger.Save(ledgerPath, ledgerTitle ?? doc.Title, ledger);
            }
            catch (Exception ex) { ledgerNote = "the model change is kept and verified, but the ledger could not be updated: " + ex.Message; }

            var applied = new JObject
            {
                ["dry_run"] = false, ["transaction_status"] = ApplicationOutcome.Committed, ["transaction_name"] = txName,
                ["verdict"] = verdict, ["postconditions"] = postconditions.ToJson(),
                ["created"] = new JArray(plans.Select(p => (JToken)new JObject
                {
                    ["finding_id"] = p.FindingId, ["created_element_id"] = p.CreatedId, ["kind"] = p.Kind, ["placement"] = p.Placement,
                    ["host_kind"] = p.HostKind, ["host_cut"] = p.HostCut
                })),
                ["findings_opening_requested"] = requested, ["findings_resolved_by_model"] = new JArray(),
                ["ledger_note"] = ledgerNote, ["undo"] = undo, ["neighbourhood_elements"] = region.Count,
                ["links_skipped"] = new JArray(linksSkipped),
                ["next_step"] = "run horizun_clash with record_findings=true: only that measurement can resolve the finding"
            };
            ApplicationOutcome.StampApplied(applied, ApplicationOutcome.Committed, plans.Count, plans.Count, plans.Count, 0, 0, 0);
            return CommandResult.Ok(applied);
        }

        /// <summary>
        /// The clearance, measured on solids: an envelope of the run's outer section grown by
        /// clearance/2 (less EnvelopeToleranceMm), from before the entry to past the exit, must
        /// meet no material of the cut host (an opening, or a sleeve measured to have cut it) and
        /// none of the sleeve's own solid. Nothing measurable is recorded unreadable, which fails.
        /// </summary>
        private static bool RecordClearance(Document doc, OpeningPlan p, double clearance, Element el, HashSet<long> sleeveIds, Options options, PostconditionCheck check)
        {
            string key = "clearance:" + p.FindingId;
            string expected = "no host/sleeve material within the run's outer section + " + (clearance / 2).ToString("0.#", CultureInfo.InvariantCulture) +
                              " mm (" + SleeveRules.EnvelopeToleranceMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm tolerance)";
            var against = new List<long>();
            if (p.HostCut) against.Add(Rid.Value(p.Host.Id));
            if (p.Kind == "sleeve" && sleeveIds.Contains(p.CreatedId)) against.Add(p.CreatedId);
            if (el == null || against.Count == 0)
            {
                check.Unreadable(key, expected, el == null ? "the created element does not re-read"
                    : "the host was not cut and the sleeve has no solid: there is nothing to measure the clearance on");
                return false;
            }
            Solid envelope;
            try { envelope = ClearanceEnvelope(p, clearance); }
            catch (Exception ex) { check.Unreadable(key, expected, "the clearance envelope could not be built: " + ex.Message); return false; }
            if (envelope == null) { check.Unreadable(key, expected, "the clearance envelope has no size"); return false; }
            double overlap = 0; bool readable = true;
            foreach (long id in against)
            {
                List<Solid> solids = Solids(doc, id, options, new Dictionary<long, List<Solid>>());
                if (solids == null) { readable = false; continue; }
                foreach (Solid s in solids)
                {
                    try
                    {
                        Solid i = BooleanOperationsUtils.ExecuteBooleanOperation(envelope, s, BooleanOperationsType.Intersect);
                        if (i != null) overlap += i.Volume;
                    }
                    catch { readable = false; }
                }
            }
            if (!readable) { check.Unreadable(key, expected, "a solid intersection with the envelope failed"); return false; }
            bool clear = overlap <= TinyVolume;
            double mm3 = overlap * MmPerFoot * MmPerFoot * MmPerFoot;
            check.Record(key, expected, mm3.ToString("0", CultureInfo.InvariantCulture) + " mm3 of " +
                string.Join("+", against.Select(x => x == p.CreatedId ? "sleeve" : "host")) + " inside the envelope", clear);
            return clear;
        }

        private static Solid ClearanceEnvelope(OpeningPlan p, double clearance)
        {
            XYZ u = new XYZ(p.RunDirection[0], p.RunDirection[1], p.RunDirection[2]);
            XYZ entry = new XYZ(p.EntryMm[0] / MmPerFoot, p.EntryMm[1] / MmPerFoot, p.EntryMm[2] / MmPerFoot);
            XYZ exit = new XYZ(p.ExitMm[0] / MmPerFoot, p.ExitMm[1] / MmPerFoot, p.ExitMm[2] / MmPerFoot);
            double pad = 100 / MmPerFoot;
            XYZ origin = entry - u * pad;
            double length = (exit - entry).GetLength() + 2 * pad;
            bool round = p.Round || !p.AxesKnown;
            double sectionHalf = p.Round || p.AxesKnown ? p.SectionHalfMm : p.SectionHalfMm * Math.Sqrt(2);
            double half = (sectionHalf + clearance / 2 - SleeveRules.EnvelopeToleranceMm) / MmPerFoot;
            if (half <= 0) return null;
            XYZ e1 = new XYZ(p.SectionAxis1[0], p.SectionAxis1[1], p.SectionAxis1[2]).Normalize();
            XYZ e2 = new XYZ(p.SectionAxis2[0], p.SectionAxis2[1], p.SectionAxis2[2]).Normalize();
            var loop = new CurveLoop();
            if (round)
            {
                loop.Append(Arc.Create(origin, half, 0, Math.PI, e1, e2));
                loop.Append(Arc.Create(origin, half, Math.PI, 2 * Math.PI, e1, e2));
            }
            else
            {
                XYZ c0 = origin - e1 * half - e2 * half, c1 = origin + e1 * half - e2 * half, c2 = origin + e1 * half + e2 * half, c3 = origin - e1 * half + e2 * half;
                loop.Append(Line.CreateBound(c0, c1)); loop.Append(Line.CreateBound(c1, c2));
                loop.Append(Line.CreateBound(c2, c3)); loop.Append(Line.CreateBound(c3, c0));
            }
            return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, u, length);
        }

        /// <summary>Re-derives everything fresh from the live model and refuses a drift from the geometry the token bound.</summary>
        private static string ParseOpening(Document doc, JObject o, Dictionary<string, CoordinationFinding> ledger, double clearance, bool allowStructural,
                                           HashSet<long> claimedMep, HashSet<string> claimedHost, out OpeningPlan plan, out string code)
        {
            plan = null; code = "invalid_proposal";
            if (o == null) return "entry is not an object";
            string fid = o.Value<string>("finding_id");
            if (string.IsNullOrEmpty(fid) || !ledger.TryGetValue(fid, out CoordinationFinding f)) return "finding_id is not in this document's ledger";
            if (f.Status == CoordinationRules.StatusResolvedByModel || f.Status == CoordinationRules.StatusClosedByDecision) return "finding " + fid + " is " + f.Status;
            long mepRaw = o.Value<long?>("mep_element_id") ?? -1;
            long hostRaw = o.Value<long?>("host_element_id") ?? -1;
            if (!Rid.CanRepresent(mepRaw) || !Rid.CanRepresent(hostRaw)) return "mep_element_id/host_element_id are invalid";
            Element mep = doc.GetElement(Rid.Make(mepRaw)); Element host = doc.GetElement(Rid.Make(hostRaw));
            if (mep == null || host == null) return "the run or the host no longer exists in this document";
            // One opening per run-host crossing: the same pair twice would cut twice.
            if (!claimedHost.Add(mepRaw.ToString(CultureInfo.InvariantCulture) + "|" + hostRaw.ToString(CultureInfo.InvariantCulture)))
                return "the pair " + mepRaw + "/" + hostRaw + " appears twice; one opening per crossing";
            claimedMep.Add(mepRaw);
            ClashResolveRules.ParseSide(f.SideA, out bool hostA, out string uidA);
            ClashResolveRules.ParseSide(f.SideB, out bool hostB, out string uidB);
            bool matches = (hostA && uidA == mep.UniqueId && hostB && uidB == host.UniqueId) ||
                           (hostB && uidB == mep.UniqueId && hostA && uidA == host.UniqueId);
            if (!matches) return "the pair does not match finding " + fid;
            string error = BuildPlan(doc, fid, mep, host, clearance, allowStructural, out OpeningPlan p, out code);
            if (error != null) return error;
            if (Rid.Value(p.Mep.Id) != mepRaw) return "mep_element_id names the host side of finding " + fid;
            double[] boundCrossing = null;
            try { boundCrossing = (o["crossing_point_mm"] as JArray)?.Select(t => (double)t).ToArray(); } catch { boundCrossing = null; }
            if (!SleeveRules.SameGeometry(boundCrossing, o.Value<double?>("opening_width_mm") ?? double.NaN, o.Value<double?>("opening_height_mm") ?? double.NaN,
                                          o.Value<string>("shape"), p.CrossingMm, p.OpeningWidthMm, p.OpeningHeightMm, p.Shape))
            {
                code = SleeveRules.CodeGeometryChanged;
                return "the crossing or opening size re-derived from the live model differs from the proposal (or the proposal carries none): run propose_opening again";
            }
            plan = p;
            return null;
        }

        /// <summary>Either the caller-supplied sleeve family (any host), or the native cut (wall / floor-roof-ceiling only).</summary>
        private static bool CreateOpeningOrSleeve(Document doc, OpeningPlan p, FamilySymbol sleeveSymbol, out string failReason)
        {
            failReason = null;
            var crossingFt = new XYZ(p.CrossingMm[0] / MmPerFoot, p.CrossingMm[1] / MmPerFoot, p.CrossingMm[2] / MmPerFoot);
            if (sleeveSymbol != null)
            {
                FamilyInstance sleeve = PlaceSleeve(doc, p, sleeveSymbol, crossingFt, out failReason);
                if (sleeve == null) return false;
                p.CreatedId = Rid.Value(sleeve.Id); p.Kind = "sleeve";
                // A family with UNATTACHED voids cuts only when asked; an attached void cuts on
                // hosting by itself. Either way host_cut is measured after the commit, not here.
                try
                {
                    if (InstanceVoidCutUtils.CanBeCutWithVoid(p.Host) && InstanceVoidCutUtils.IsVoidInstanceCuttingElement(sleeve))
                        InstanceVoidCutUtils.AddInstanceVoidCut(doc, p.Host, sleeve);
                }
                catch { /* not cut here; the measurement decides */ }
                return true;
            }
            var axisA = new XYZ(p.AxisA[0], p.AxisA[1], p.AxisA[2]);
            var axisB = new XYZ(p.AxisB[0], p.AxisB[1], p.AxisB[2]);
            double halfW = (p.OpeningWidthMm / 2) / MmPerFoot, halfH = (p.OpeningHeightMm / 2) / MmPerFoot;
            if (p.Route == SleeveRules.RouteWallOpening && p.Host is Wall wall)
            {
                XYZ pt1 = crossingFt - axisA * halfW - axisB * halfH;
                XYZ pt2 = crossingFt + axisA * halfW + axisB * halfH;
                try
                {
                    Opening opening = doc.Create.NewOpening(wall, pt1, pt2);
                    if (opening == null) { failReason = "NewOpening returned null"; return false; }
                    p.CreatedId = Rid.Value(opening.Id); p.Kind = "opening"; p.Placement = "wall_rectangular_opening";
                    return true;
                }
                catch (Exception ex) { failReason = "wall opening failed: " + ex.Message; return false; }
            }
            if (p.Route == SleeveRules.RouteFloorOpening && p.Host is HostObject hostObj)
            {
                var loop = new CurveArray();
                if (p.Shape == SleeveRules.ShapeRound)
                {
                    loop.Append(Arc.Create(crossingFt, halfW, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                    loop.Append(Arc.Create(crossingFt, halfW, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                }
                else
                {
                    XYZ c0 = crossingFt - axisA * halfW - axisB * halfH, c1 = crossingFt + axisA * halfW - axisB * halfH;
                    XYZ c2 = crossingFt + axisA * halfW + axisB * halfH, c3 = crossingFt - axisA * halfW + axisB * halfH;
                    loop.Append(Line.CreateBound(c0, c1)); loop.Append(Line.CreateBound(c1, c2));
                    loop.Append(Line.CreateBound(c2, c3)); loop.Append(Line.CreateBound(c3, c0));
                }
                try
                {
                    // bPerpendicularFace=false: a VERTICAL cut. The route only takes steep runs
                    // (DirectionRefusal), and a cut normal to a sloped roof would drift t*tan(slope).
                    Opening opening = doc.Create.NewOpening(hostObj, loop, false);
                    if (opening == null) { failReason = "NewOpening returned null"; return false; }
                    p.CreatedId = Rid.Value(opening.Id); p.Kind = "opening";
                    p.Placement = p.Shape == SleeveRules.ShapeRound ? "vertical_boundary_opening_circular" : "vertical_boundary_opening_rectangular";
                    return true;
                }
                catch (Exception ex) { failReason = "floor/roof/ceiling opening failed: " + ex.Message; return false; }
            }
            failReason = SleeveRules.CodeCutRefused + ": host route " + p.Route + " has no cut without sleeve_type_id";
            return false;
        }

        /// <summary>
        /// Places the caller's sleeve family by its own placement type: face-based on the host
        /// face the run enters (through the SYMBOL geometry's reference for an instance host),
        /// level-based on the run's reference level at the crossing, line-based along the
        /// entry-exit segment, hosted on the host, anything else at the crossing point. A point
        /// placement is rotated about Z to a horizontal run. A family whose placement cannot be
        /// satisfied fails with the reason - never a guessed placement.
        /// MEASURE LIVE: the orientation of a point-placed family depends on how its author
        /// modelled the axis; the clearance envelope is what catches a wrong one.
        /// </summary>
        private static FamilyInstance PlaceSleeve(Document doc, OpeningPlan p, FamilySymbol symbol, XYZ crossingFt, out string failReason)
        {
            failReason = null;
            try { if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); } } catch { /* activation failure surfaces in the placement below */ }
            var entryFt = new XYZ(p.EntryMm[0] / MmPerFoot, p.EntryMm[1] / MmPerFoot, p.EntryMm[2] / MmPerFoot);
            var exitFt = new XYZ(p.ExitMm[0] / MmPerFoot, p.ExitMm[1] / MmPerFoot, p.ExitMm[2] / MmPerFoot);
            XYZ runDir = new XYZ(p.RunDirection[0], p.RunDirection[1], p.RunDirection[2]);
            FamilyPlacementType placement = FamilyPlacementType.Invalid;
            try { placement = symbol.Family.FamilyPlacementType; } catch { }
            FamilyInstance instance = null;
            bool rotate = false;
            try
            {
                if (placement == FamilyPlacementType.OneLevelBasedHosted)
                {
                    instance = doc.Create.NewFamilyInstance(crossingFt, symbol, p.Host, StructuralType.NonStructural);
                    p.Placement = "hosted_on_host_at_crossing";
                }
                else if (placement == FamilyPlacementType.WorkPlaneBased)
                {
                    Reference face = EntryFaceReference(p.Host, entryFt, out XYZ normal);
                    if (face == null) { failReason = "no planar face of the host contains the run's entry point, so a face-based sleeve has nowhere to sit"; return null; }
                    XYZ refDir = Math.Abs(normal.Z) > 0.9 ? XYZ.BasisX : XYZ.BasisZ.CrossProduct(normal).Normalize();
                    instance = doc.Create.NewFamilyInstance(face, entryFt, refDir, symbol);
                    p.Placement = "face_based_on_entry_face";
                }
                else if (placement == FamilyPlacementType.CurveBased)
                {
                    Level level = RunLevel(doc, p);
                    if (level == null) { failReason = "a line-based sleeve needs a level and neither the run nor the host has one"; return null; }
                    instance = doc.Create.NewFamilyInstance(Line.CreateBound(entryFt, exitFt), symbol, level, StructuralType.NonStructural);
                    p.Placement = "line_based_entry_to_exit";
                }
                else if (placement == FamilyPlacementType.OneLevelBased)
                {
                    // The Creation.Document overload WITH a level: the no-level overload is documented as
                    // not for level-based families. The level and position are re-read after the commit.
                    Level level = RunLevel(doc, p);
                    if (level == null) { failReason = "a level-based sleeve needs the run's reference level (or the host's) and neither has one"; return null; }
                    instance = doc.Create.NewFamilyInstance(crossingFt, symbol, level, StructuralType.NonStructural);
                    p.Placement = "level_based_at_crossing"; p.LevelId = Rid.Value(level.Id); p.PointPlaced = true; rotate = true;
                }
                else
                {
                    instance = doc.Create.NewFamilyInstance(crossingFt, symbol, StructuralType.NonStructural);
                    p.Placement = "point_at_crossing"; p.PointPlaced = true; rotate = true;
                }
                if (instance != null && rotate && Math.Abs(runDir.Z) < 0.5)
                {
                    double angle = Math.Atan2(runDir.Y, runDir.X);
                    if (Math.Abs(angle) > 1e-6)
                        ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(crossingFt, crossingFt + XYZ.BasisZ), angle);
                    p.Placement += "_rotated_to_run";
                }
            }
            catch (Exception ex)
            {
                failReason = "the sleeve family (placement " + placement + ") could not be placed at the crossing: " + ex.Message;
                return null;
            }
            if (instance == null) failReason = "NewFamilyInstance returned null";
            return instance;
        }

        private static Level RunLevel(Document doc, OpeningPlan p) =>
            (p.Mep as MEPCurve)?.ReferenceLevel ?? doc.GetElement(p.Mep.LevelId) as Level ?? doc.GetElement(p.Host.LevelId) as Level;

        /// <summary>
        /// The reference of the host's planar face that contains `pointFt` (within 1 mm), with its
        /// model-space normal. An instance host (every unjoined steel member) is walked through
        /// GetSymbolGeometry with its transform: the references of GetInstanceGeometry copies are
        /// documented as unusable for creating elements.
        /// </summary>
        private static Reference EntryFaceReference(Element host, XYZ pointFt, out XYZ normal)
        {
            normal = XYZ.BasisZ;
            GeometryElement g;
            try { g = host.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine }); }
            catch { return null; }
            if (g == null) return null;
            XYZ n = XYZ.BasisZ;
            Reference found = FindFace(g, Transform.Identity, pointFt, ref n);
            normal = n;
            return found;
        }

        private static Reference FindFace(GeometryElement g, Transform toModel, XYZ pointFt, ref XYZ normal)
        {
            foreach (GeometryObject o in g)
            {
                if (o is GeometryInstance gi)
                {
                    GeometryElement symbolGeometry = gi.GetSymbolGeometry();
                    if (symbolGeometry == null) continue;
                    Reference r = FindFace(symbolGeometry, toModel.Multiply(gi.Transform), pointFt, ref normal);
                    if (r != null) return r;
                }
                else if (o is Solid s)
                {
                    XYZ local = toModel.Inverse.OfPoint(pointFt);
                    foreach (Face f in s.Faces)
                    {
                        if (!(f is PlanarFace pf) || pf.Reference == null) continue;
                        IntersectionResult hit = pf.Project(local);
                        if (hit != null && hit.Distance < 1.0 / MmPerFoot) { normal = toModel.OfVector(pf.FaceNormal).Normalize(); return pf.Reference; }
                    }
                }
            }
            return null;
        }
    }
}
