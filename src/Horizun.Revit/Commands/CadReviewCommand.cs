// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_cad_review — does the model actually agree with the drawing's network?
//
// A conversion reports what it built. An audit reports what it did not convert.
// Neither answers the question somebody asks a week later, which is whether the
// thing in the model is a NETWORK: pipes that touch and are not joined pass both
// of those reports and fail the first time anyone opens the system browser.
//
// So this walks the drawing's junctions and asks the MODEL about each one:
//
//   MADE            elements meet here and their connectors are joined.
//   NOT MADE        elements meet here and their connectors are open. This is
//                   the finding - the conversion built the runs and nobody
//                   connected them.
//   NOTHING BUILT   the drawing has a junction here and the model has no MEP
//                   element near it. Either the layer was not converted, or it
//                   was deferred for review, and the audit says which.
//   NOT PROPOSED    the drawing has a junction here that the network reading
//                   itself refused - a crossing, a riser, a degree no fitting
//                   covers. An open connector here is CORRECT.
//
// THE LAST ROW IS WHY THIS TOOL IS NOT A COUNT. "72 of 412 connectors are open"
// is a number that sounds like a defect and is usually half correct: some of
// those opens are terminals at fixtures, some are crossings the reading refused
// on purpose, and some are the real finding. Separating them is the work.
//
// MATCHING IS GEOMETRIC, and it says so. Revit's geometry walk returns an
// imported drawing in MODEL coordinates, so a junction point and a connector
// origin are directly comparable - but a connector 30 mm from where the drawing
// put the junction may be the right one or the wrong one, and this reports the
// distance rather than hiding it inside a boolean.
//
// Read-only. No transaction is opened.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class CadReviewCommand : ICommand
    {
        public string Name => "horizun_cad_review";

        public string Description =>
            "Ask the MODEL whether it agrees with the drawing's network. For every junction the drawing " +
            "shows, this reports whether the elements meeting there are JOINED, whether they meet and are " +
            "OPEN - which is the finding a conversion report never surfaces, because the conversion built " +
            "the runs and nobody connected them - whether nothing was built there at all, or whether the " +
            "network reading itself refused the junction, in which case an open connector is CORRECT. That " +
            "last category is why this is not a count: '72 of 412 connectors are open' sounds like a defect " +
            "and is usually half terminals at fixtures and crossings refused on purpose. It also reports " +
            "open connectors in the model that the drawing does NOT explain, and elements built from this " +
            "drawing whose provenance the model still carries. Matching is geometric and every match " +
            "publishes its distance rather than hiding it in a boolean. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            CadReading r = CadReadingHelper.Resolve(app, paramsJson);
            if (!r.Ok) return r.Refusal;
            Document doc = r.Document;

            double tolerance = r.Request.Value<double?>("match_tolerance_mm") ?? 25.0;
            if (tolerance <= 0)
                return CommandResult.Fail(
                    "match_tolerance_mm must be positive: it is how far a connector may sit from where the " +
                    "drawing put the junction and still be the one meant.");

            var options = new CadNetworkOptions
            {
                ConnectToleranceMm = r.Request.Value<double?>("connect_tolerance_mm") ?? 1.0,
                GapReviewDistanceMm = r.Request.Value<double?>("gap_review_distance_mm") ?? 50.0,
                CollinearToleranceDegrees = r.Request.Value<double?>("collinear_tolerance_degrees") ?? 2.0,
                ThroughToleranceDegrees = r.Request.Value<double?>("through_tolerance_degrees") ?? 15.0,
                CrossingCheckLimit = Math.Max(0, r.Request.Value<int?>("crossing_check_limit") ?? 4000)
            };

            CadRequirementSet set = null;
            JObject setJson = r.Request["requirement_set"] as JObject;
            if (setJson != null)
            {
                try { set = CadRequirementSet.Load(setJson); }
                catch (Exception ex)
                {
                    return CommandResult.Fail(
                        "the requirement set was refused WHOLE: " + ex.Message +
                        " Reviewing against a partially-read set would compare the model with rules nobody wrote.");
                }
            }
            var ties = new List<JObject>();
            Func<string, CadNetworkRules.CadRunDeclaration> fromSet =
                CadNetworkRules.DeclarationsFrom(set, ties);

            List<string> layersUsed;
            List<CadSegment> segments = CadReadingHelper.Selected(r, out layersUsed);
            // WITH THE ARCS, so a curved run is ONE run here too. A review whose
            // network differs from the one the conversion was planned against is
            // comparing the model with a different drawing.
            CadNetwork network = CadNetworkRules.Build(segments, options,
                layer => fromSet == null ? null : fromSet(layer),
                CadReadingHelper.ArcsOn(r, layersUsed));

            // ---- what the model holds -------------------------------------------
            var connectable = Connectable(doc);
            var provenanceProblems = new List<string>();
            Dictionary<string, List<Element>> byCandidate =
                CadProvenanceStore.IndexByCandidate(doc, out provenanceProblems);

            var fromThisDrawing = new HashSet<long>();
            string sourceFingerprint = r.Facts == null ? null : CadFacts.SourceFingerprint(r.Facts);
            foreach (Element e in connectable)
            {
                string problem;
                CadProvenance p = CadProvenanceStore.Read(e, out problem);
                if (p == null) continue;
                if (sourceFingerprint != null && p.SourceFingerprint != null &&
                    !string.Equals(p.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal)) continue;
                fromThisDrawing.Add(Rid.Value(e.Id));
            }

            // Every connector in the model, with its owner and its state. Built once:
            // walking the model per junction would make the review cost more than the
            // conversion it is reviewing.
            var connectors = new List<ConnectorFactHere>();
            foreach (Element e in connectable)
                foreach (Connector c in MepConnect.ConnectorsOf(e))
                {
                    XYZ origin;
                    try { origin = c.Origin; } catch { continue; }
                    if (origin == null) continue;
                    bool joined;
                    try { joined = c.IsConnected; } catch { joined = false; }
                    connectors.Add(new ConnectorFactHere
                    {
                        OwnerId = Rid.Value(e.Id),
                        FromThisDrawing = fromThisDrawing.Contains(Rid.Value(e.Id)),
                        At = new CadPoint(CadUnits.FeetToMm(origin.X), CadUnits.FeetToMm(origin.Y),
                                          CadUnits.FeetToMm(origin.Z)),
                        Joined = joined,
                        HalfDepthMm = HalfDepthOf(c)
                    });
                }

            // A GRID OVER THE CONNECTORS, so a junction probes its own cell and
            // the neighbours instead of the whole model. Every junction scanning
            // every connector is five thousand against fifty thousand on a real
            // model - two hundred and fifty million distance computations for a
            // read-only review.
            var grid = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            double cell = Math.Max(tolerance, 1e-6);
            for (int i = 0; i < connectors.Count; i++)
            {
                string key = Cell(connectors[i].At, cell);
                List<int> bucket;
                if (!grid.TryGetValue(key, out bucket)) grid[key] = bucket = new List<int>();
                bucket.Add(i);
            }

            // THE FALL THE DRAWING IMPLIES, when an outfall is named. A network
            // laid perfectly level is connected, flows nowhere, and passes every
            // other check here - so the heights are the half that catches it.
            CadFall fall = null;
            var outfall = r.Request["outfall"] as JArray;
            if (outfall != null && outfall.Count >= 2)
            {
                double outfallTolerance = r.Request.Value<double?>("outfall_tolerance_mm") ?? 50.0;
                var outfallAt = new CadPoint(outfall[0].Value<double>(), outfall[1].Value<double>(),
                                             outfall.Count > 2 ? outfall[2].Value<double>() : 0);
                string outfallNode = CadFallRules.NodeNear(network, outfallAt, outfallTolerance);
                if (outfallNode == null)
                    return CommandResult.Fail(
                        "outfall_matches_no_node: no junction of this network is within " +
                        outfallTolerance.ToString("0.#", CultureInfo.InvariantCulture) +
                        " mm of that point. Nothing was compared: an outfall on the wrong node inverts an " +
                        "entire drainage layout while looking plausible.");

                fall = CadFallRules.Compute(network, outfallNode,
                                            r.Request.Value<double?>("outfall_invert_mm") ?? 0.0,
                                            run => run.SlopePercent);
            }
            var invertOf = new Dictionary<string, double>(StringComparer.Ordinal);
            if (fall != null)
                foreach (CadInvert invert in fall.Inverts)
                    invertOf[invert.NodeKey] = fall.OutfallInvertMm + invert.RiseMm;

            double heightTolerance = r.Request.Value<double?>("height_tolerance_mm") ?? 25.0;
            int heightWrong = 0, heightRight = 0, heightUnknown = 0;

            // ---- junction by junction --------------------------------------------
            var rows = new JArray();
            int made = 0, notMade = 0, nothingBuilt = 0, notProposed = 0;
            var explained = new HashSet<int>();

            foreach (CadJunction j in network.Junctions)
            {
                var near = Near(grid, connectors, j.Point, cell, tolerance);

                // A TERMINAL IS NOT A JUNCTION TO REVIEW - one run ending is a
                // fixture, a piece of equipment, or a drawing that stops, and an
                // open connector there is what the drawing SHOWS.
                //
                // But its connectors are still EXPLAINED by the drawing, and the
                // first version skipped the terminal before marking them. So every
                // fixture connection in the building came back in "open connectors
                // the drawing does not explain" - which is the list a reviewer
                // reads to find modelling nobody accounted for, and a list that
                // long is one nobody reads.
                foreach (int i in near) explained.Add(i);
                if (j.Kind == CadJunctionKind.Terminal) continue;

                string state;
                if (near.Count == 0) { state = "nothing_built"; nothingBuilt++; }
                else if (!j.Automatic) { state = "not_proposed"; notProposed++; }
                else if (near.All(i => connectors[i].Joined)) { state = "made"; made++; }
                else { state = "not_made"; notMade++; }

                rows.Add(new JObject
                {
                    ["node"] = j.NodeKey,
                    ["kind"] = j.Kind,
                    ["degree"] = j.Degree,
                    ["automatic"] = j.Automatic,
                    ["state"] = state,
                    ["at_mm"] = new JArray(Round(j.Point.X), Round(j.Point.Y), Round(j.Point.Z)),
                    ["connectors_found"] = near.Count,
                    ["connectors_joined"] = near.Count(i => connectors[i].Joined),
                    ["nearest_in_plan_mm"] = near.Count == 0
                        ? (JToken)JValue.CreateNull()
                        : Round(near.Min(i => connectors[i].At.PlanDistanceTo(j.Point))),
                    // THE HEIGHT SPREAD, reported rather than folded into the match.
                    // Two elements meeting in plan and 2400 mm apart vertically are
                    // not meeting - and that is a finding about the MODEL, not a
                    // reason to fail to find them.
                    ["height_spread_mm"] = near.Count == 0
                        ? (JToken)JValue.CreateNull()
                        : Round(near.Max(i => connectors[i].At.Z) - near.Min(i => connectors[i].At.Z)),
                    ["elements_z_mm"] = new JArray(near
                        .Select(i => (JToken)Round(connectors[i].At.Z)).Distinct()),
                    ["elements"] = new JArray(near.Select(i => (JToken)connectors[i].OwnerId).Distinct()),
                    ["says"] = j.Says,
                    ["means"] = Means(state, j)
                });

                // ---- and the height, when the drawing implies one ----------------
                if (fall == null) continue;
                var row = (JObject)rows[rows.Count - 1];
                double expected;
                if (!invertOf.TryGetValue(j.NodeKey, out expected))
                {
                    heightUnknown++;
                    row["invert_means"] =
                        "the fall walk never reached this node, so the drawing implies no height here. " +
                        "The fall's own blocked and unreachable lists say why.";
                    continue;
                }
                row["computed_invert_mm"] = Round(expected);
                if (near.Count == 0)
                {
                    heightUnknown++;
                    row["invert_means"] = "nothing is built here to compare the height against.";
                    continue;
                }

                double built = near.Average(i => connectors[i].At.Z);
                row["built_z_mm"] = Round(built);

                // THE TWO NUMBERS MUST BE THE SAME KIND OF HEIGHT.
                //
                // The fall walk gives an INVERT - the inside bottom, which is what a
                // drainage drawing puts on its nodes. A connector origin sits on the
                // CENTRELINE. Comparing them reports a correctly built 150 mm drain
                // as 75 mm high and passes one built 75 mm too low, which is the
                // error that floods a building.
                bool depthKnown = near.Count > 0 && near.All(i => connectors[i].HalfDepthMm.HasValue);
                if (!depthKnown)
                {
                    heightUnknown++;
                    row["built_invert_mm"] = JValue.CreateNull();
                    row["invert_agrees"] = JValue.CreateNull();
                    row["invert_means"] =
                        "NO VERDICT: the drawing implies an INVERT here and the model offers only a " +
                        "centreline. At least one connector at this junction reports no profile - an " +
                        "electrical connector does not - so there is no way to reach its invert from it. " +
                        "The two numbers above are different kinds of height and differ by half a " +
                        "diameter on any pipe: comparing them would report a correct 150 mm drain as " +
                        "75 mm high and pass one laid 75 mm too low.";
                    continue;
                }

                double builtInvert = near.Average(i => connectors[i].At.Z - connectors[i].HalfDepthMm.Value);
                double off = builtInvert - expected;
                row["built_invert_mm"] = Round(builtInvert);
                row["invert_difference_mm"] = Round(off);
                bool ok = Math.Abs(off) <= heightTolerance;
                if (ok) heightRight++; else heightWrong++;
                row["invert_agrees"] = ok;
                row["invert_means"] = (ok
                    ? "the model sits within the height tolerance of what the drawing's slope implies here."
                    : "THE MODEL IS " + Round(Math.Abs(off)).ToString("0.#", CultureInfo.InvariantCulture) +
                      " mm " + (off > 0 ? "ABOVE" : "BELOW") + " what the drawing's slope implies. A network " +
                      "laid level is connected, flows nowhere, and passes every other check here - which is " +
                      "why this comparison exists. It is a COMPARISON, not a verdict: the tolerance is the " +
                      "caller's and the difference is reported either way.") +
                    " Both heights are INVERTS: built_invert_mm is the connector origin taken down to the " +
                    "bottom of its own profile, so it is the same kind of height as the computed one.";
            }

            // ---- open connectors the drawing does not explain --------------------
            //
            // The other direction, and the one a junction-by-junction walk misses:
            // an element in the model with an open end nowhere near anything the
            // drawing shows. Usually somebody modelled by hand; occasionally the
            // conversion put a run where the drawing does not have one.
            var unexplained = new JArray();
            for (int i = 0; i < connectors.Count && unexplained.Count < 500; i++)
            {
                if (connectors[i].Joined) continue;
                if (explained.Contains(i)) continue;
                if (!connectors[i].FromThisDrawing) continue;
                unexplained.Add(new JObject
                {
                    ["element"] = connectors[i].OwnerId,
                    ["at_mm"] = new JArray(Round(connectors[i].At.X), Round(connectors[i].At.Y),
                                           Round(connectors[i].At.Z))
                });
            }

            var reply = new JObject
            {
                ["junctions"] = rows,
                ["summary"] = new JObject
                {
                    ["junctions_reviewed"] = rows.Count,
                    ["made"] = made,
                    ["not_made"] = notMade,
                    ["nothing_built"] = nothingBuilt,
                    ["not_proposed"] = notProposed,
                    ["terminals_skipped"] = network.Junctions.Count(x => x.Kind == CadJunctionKind.Terminal),
                    ["means"] =
                        "not_made is the finding: the elements are there, they meet, and their connectors are " +
                        "open. not_proposed is NOT a finding - the network reading refused that junction " +
                        "(a crossing, a riser, a degree no fitting covers) and an open connector there is " +
                        "correct. nothing_built means the drawing shows a junction and the model has no MEP " +
                        "element near it, which horizun_audit_cad_model explains. Terminals are skipped: one " +
                        "run ending is a fixture or a drawing that stops."
                },
                ["open_connectors_the_drawing_does_not_explain"] = unexplained,
                ["open_connectors_mean"] =
                    "these belong to elements this drawing built and sit nowhere near any junction it shows - " +
                    "terminals included, so a fixture connection at the end of a run is NOT listed here. " +
                    "Usually somebody modelled by hand afterwards; occasionally the conversion put a run " +
                    "where the drawing does not have one. Only elements carrying this drawing's provenance " +
                    "are listed, so a hand-built model beside the converted one is not reported as a defect.",
                ["model_reach"] = new JObject
                {
                    ["connectable_elements"] = connectable.Count,
                    ["connectors_read"] = connectors.Count,
                    ["elements_from_this_drawing"] = fromThisDrawing.Count,
                    ["candidates_with_provenance"] = byCandidate.Count,
                    ["provenance_problems"] = new JArray(provenanceProblems.Take(50).Select(x => (JToken)x)),
                    ["means"] = fromThisDrawing.Count == 0
                        ? "NO element in this model remembers being built from this drawing. Either the " +
                          "conversion has not been applied here, or it was applied through a route that " +
                          "records nothing - horizun_execute_plan creates elements that remember nothing, " +
                          "which is why horizun_apply_cad_plan exists. The junction review below still " +
                          "works, because it matches by GEOMETRY, but nothing can be attributed."
                        : "elements are matched to this drawing by the provenance the apply stamped, and to " +
                          "its junctions by geometry. The two are separate: a junction can be made by " +
                          "elements the model does not attribute to this drawing."
                },
                ["fall"] = fall == null ? (JToken)JValue.CreateNull() : fall.ToJson(),
                ["invert_check"] = fall == null
                    ? (JToken)new JObject
                    {
                        ["ran"] = false,
                        ["means"] = "no outfall was named, so no height was compared. A network laid " +
                                    "perfectly level is connected and flows nowhere, and every other check " +
                                    "here passes it - name the outfall to catch that."
                    }
                    : new JObject
                    {
                        ["ran"] = true,
                        ["height_tolerance_mm"] = heightTolerance,
                        ["agrees"] = heightRight,
                        ["disagrees"] = heightWrong,
                        ["not_comparable"] = heightUnknown,
                        ["means"] = "per junction, the INVERT the drawing's declared slopes imply against " +
                                    "the INVERT the model actually has. Both are inside-bottom heights: the " +
                                    "model's is its connector origin taken down to the bottom of that " +
                                    "connector's own profile, because a connector sits on the centreline " +
                                    "and comparing a centreline against an invert is wrong by half a " +
                                    "diameter - 75 mm on a 150 mm drain, in the direction that PASSES a " +
                                    "pipe laid too low. not_comparable counts three things: the junctions " +
                                    "the fall walk never reached, the ones with nothing built at them, and " +
                                    "the ones whose connectors report no profile, so no invert can be " +
                                    "reached from them. Each row says which of the three it is.",
                        ["terminals"] = "TERMINALS ARE NOT COMPARED HERE, the same way they are not reviewed " +
                                        "for connectivity: one run ending is a fixture, and this walk has no " +
                                        "row for it. Their inverts ARE in the fall block above, by node, so " +
                                        "a floor drain's height can be read off it - it is just not checked " +
                                        "against the model here."
                    },
                ["network"] = network.SummaryJson(),
                ["layers_read"] = new JArray(layersUsed.Select(x => (JToken)x)),
                ["match_tolerance_mm"] = tolerance,
                ["matching_means"] =
                    "junctions are matched to connectors IN PLAN, because the drawing is a plan: its " +
                    "junctions carry the drawing's own Z and the elements built from it sit at the height " +
                    "the rules gave them. Each row reports the plan distance AND the height spread of the " +
                    "connectors it found, so two elements meeting on the page and 2400 mm apart in the " +
                    "building show up as what they are rather than as nothing built."
            };
            if (ties.Count > 0) reply["rule_precedence_ties"] = new JArray(ties);

            foreach (var p in CadReadingHelper.ReadingBlock(r, CadReadingHelper.Sagitta(r.Request)))
                reply[p.Key] = p.Value;
            return CommandResult.Ok(reply);
        }

        private static string Means(string state, CadJunction j)
        {
            switch (state)
            {
                case "made":
                    return "the elements meeting here are joined. This is what a converted network looks like.";
                case "not_made":
                    return "THE FINDING. Elements meet here and their connectors are open, so nothing flows " +
                           "through this point, no system spans it, and a schedule counts the runs " +
                           "separately. The conversion built them; nobody connected them. " +
                           "horizun_cad_connect makes this junction.";
                case "nothing_built":
                    return "the drawing shows a junction here and no MEP element in the model is near it. " +
                           "Either the layer was not converted or the candidates were deferred for review; " +
                           "horizun_audit_cad_model says which.";
                default:
                    return "the network reading refused this junction - " + (j.Says ?? "no reason recorded") +
                           " - so an open connector here is CORRECT and is not a defect.";
            }
        }

        /// <summary>Every element in the document that can carry a connector.</summary>
        private static List<Element> Connectable(Document doc)
        {
            var found = new List<Element>();
            var categories = new[]
            {
                BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_DuctFitting,
                BuiltInCategory.OST_ConduitFitting, BuiltInCategory.OST_CableTrayFitting,
                BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctAccessory,
                BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_MechanicalEquipment,
                BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_Sprinklers
            };
            foreach (BuiltInCategory category in categories)
            {
                try
                {
                    found.AddRange(new FilteredElementCollector(doc)
                        .OfCategory(category)
                        .WhereElementIsNotElementType()
                        .ToElements());
                }
                catch { }
            }
            return found;
        }

        private static double Round(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

        /// <summary>
        /// The grid cell of a point, in PLAN.
        ///
        /// Z is deliberately not part of it. A plan drawing's junctions are at the
        /// drawing's own Z - zero, on every plan - and the elements built from it
        /// sit at the height the rules gave them. Bucketing in three dimensions put
        /// a run at +2400 in a cell 2400 mm from its own junction.
        /// </summary>
        private static string Cell(CadPoint p, double cell) =>
            string.Format(CultureInfo.InvariantCulture, "{0}|{1}",
                          (long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));

        /// <summary>
        /// The connectors within tolerance of a point, found through the grid.
        ///
        /// The cell is only a way of finding candidates; the DISTANCE decides, so a
        /// connector a hair across a cell boundary is still found.
        /// </summary>
        private static List<int> Near(Dictionary<string, List<int>> grid, List<ConnectorFactHere> connectors,
                                      CadPoint at, double cell, double toleranceMm)
        {
            var found = new List<int>();
            long cx = (long)Math.Floor(at.X / cell);
            long cy = (long)Math.Floor(at.Y / cell);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                {
                    List<int> bucket;
                    string key = string.Format(CultureInfo.InvariantCulture, "{0}|{1}", cx + dx, cy + dy);
                    if (!grid.TryGetValue(key, out bucket)) continue;
                    foreach (int i in bucket)
                        // IN PLAN. The drawing is a plan; the model is not. Comparing
                        // them in three dimensions asks whether the conversion also
                        // put everything at Z zero, which nobody wanted.
                        if (connectors[i].At.PlanDistanceTo(at) <= toleranceMm) found.Add(i);
                }
            found.Sort();
            return found;
        }

        private sealed class ConnectorFactHere
        {
            public long OwnerId;
            public bool FromThisDrawing;
            public CadPoint At;
            public bool Joined;

            /// <summary>
            /// How far BELOW this connector's origin the inside bottom of the thing
            /// it ends is: the radius of a round connector, half the height of a
            /// rectangular or oval one.
            ///
            /// Null when the connector reports no profile - an electrical connector
            /// does not - and null is not zero. Zero would say "the origin IS the
            /// invert", which is the mistake this field exists to stop.
            /// </summary>
            public double? HalfDepthMm;
        }

        /// <summary>
        /// The distance from a connector's origin down to its own bottom, in mm, or
        /// null when the connector does not report a profile.
        ///
        /// Round connectors report a Radius; rectangular and oval ones report Width
        /// and Height, and it is the HEIGHT that reaches the bottom - a 400x200 duct
        /// laid flat has 100 mm below its centreline, not 200.
        /// </summary>
        private static double? HalfDepthOf(Connector c)
        {
            ConnectorProfileType shape;
            try { shape = c.Shape; } catch { return null; }
            try
            {
                if (shape == ConnectorProfileType.Round)
                {
                    double r = c.Radius;
                    return r > 0 ? (double?)CadUnits.FeetToMm(r) : null;
                }
                if (shape == ConnectorProfileType.Rectangular || shape == ConnectorProfileType.Oval)
                {
                    double h = c.Height;
                    return h > 0 ? (double?)(CadUnits.FeetToMm(h) / 2.0) : null;
                }
            }
            catch { return null; }
            return null;
        }
    }
}
