// -----------------------------------------------------------------------------
// Horizun MCP — original Horizun code.
//
// horizun_plan_from_cad — a drawing, a requirement set, and a plan nobody has
// built yet.
//
// READ-ONLY, deliberately and completely. It opens no transaction and creates
// nothing; it measures the CAD, reads it through the caller's requirement set,
// and hands back an ordered plan plus everything needed to decide whether to
// run it. That separation is the point: the argument about what a drawing MEANS
// happens before, and separately from, the writing.
//
// WHAT COMES BACK, AND WHY EACH PART IS THERE:
//
//   actions_by_stage   the order a building goes up in. A door cannot be hosted
//                      by a wall that does not exist yet.
//   deferred_detail    every candidate nobody will build, with its reason, its
//                      rival readings and the facts the drawing did not carry.
//                      This is the half a reviewer actually reads.
//   coverage           how much of the drawn geometry the reading accounts for.
//                      A requirement set that quietly matches a tenth of a
//                      drawing produces a confident-looking plan of nearly
//                      nothing, and this is the number that shows it.
//   plan_fingerprint   what the plan is BOUND to: the drawing's bytes, the
//                      link's transform, the requirement set's hash, the
//                      resolved actions, and whether review was bypassed. The
//                      apply refuses if any of it moved.
//   execute_plan_request  the plan as a ready call, so applying it reuses the
//                      atomic, confirmed, verified write path this bridge
//                      already has rather than a second one written for CAD.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class PlanFromCadCommand : ICommand
    {
        public string Name => "horizun_plan_from_cad";

        public string Description =>
            "Read a linked or imported DWG through a versioned requirement set and return an ORDERED BIM plan " +
            "without building anything: staged typed actions, every deferred candidate with its reason and its " +
            "rival readings, the fraction of the drawing the reading accounts for, and a plan fingerprint bound " +
            "to the drawing's bytes, the link's transform and the requirement set's hash. Read-only.";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");

            // A PLAN NAMED FOR ONE DOCUMENT IS NOT COMPUTED AGAINST ANOTHER.
            //
            // MEASURED: a plan asked for HZ_CLEAN2 while HZ_CLEAN3 was in front was
            // computed against HZ_CLEAN3 - its host ids, its "already built" -
            // and then carried HZ_CLEAN2 into the request it emitted. The audit
            // and the update planner already refused this; the plan did not.
            CommandResult wrongDocument = DocumentGate.ReadGuard(doc, request, Name);
            if (wrongDocument != null) return wrongDocument;

            // ---- the requirement set: whole, or refused whole ---------------------
            JObject setJson = request["requirement_set"] as JObject;
            if (setJson == null)
                return CommandResult.Fail(
                    "requirement_set is required. This bridge compiles no organisation's layer names, families or " +
                    "standards into itself, so the mapping from drawing to model arrives as a versioned artefact " +
                    "the caller supplies.");
            CadRequirementSet set;
            try { set = CadRequirementSet.Load(setJson); }
            catch (CadRequirementSetException ex) { return CommandResult.Fail("requirement_set refused: " + ex.Message); }

            // ---- THE CATALOGUE ONLY: every rule against this model, nothing read, nothing written ----
            if (request.Value<bool?>("catalog_check_only") == true)
            {
                var levelNames = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(Level))
                    .Cast<Level>().Select(SafeName), StringComparer.Ordinal);
                JObject check = CadCatalogCheck.Check(set, name => TypeFactsOf(doc, name), levelNames,
                                                      produces => TypesOfKind(doc, produces));
                check["document"] = SafeTitle(doc);
                return CommandResult.Ok(check);
            }

            // ---- the drawing ------------------------------------------------------
            long instanceId = request.Value<long?>("instance_id") ?? -1;
            if (instanceId < 0)
                return CommandResult.Fail(
                    "instance_id is required: which CAD instance to read. List them with horizun_query_cad " +
                    "mode='instances'; there is no default drawing and choosing one would be a guess.");
            if (!Rid.CanRepresent(instanceId)) return CommandResult.Fail(Rid.RangeError(instanceId));

            Element element = doc.GetElement(Rid.Make(instanceId));
            if (element == null)
                return CommandResult.Fail("No element with id " + instanceId + " in '" + SafeTitle(doc) + "'.");
            if (!(element is ImportInstance))
                return CommandResult.Fail("Element " + instanceId + " is a " + element.GetType().Name +
                                          ", not an ImportInstance.");

            List<JObject> unreadable;
            List<CadInstanceFacts> all = CadFacts.Collect(doc, out unreadable);
            CadInstanceFacts facts = all.FirstOrDefault(f => f.ElementId == instanceId);
            if (facts == null)
                return CommandResult.Fail("CAD instance " + instanceId + " could not be measured; it is listed as " +
                                          "unreadable by horizun_query_cad, and planning from a drawing nothing " +
                                          "could read would be planning from nothing.");

            // ---- the unit check that stops a 200 becoming 200 metres ---------------
            string declared = facts.DeclaredUnits;
            double? declaredToMm = CadUnits.MillimetresPer(declared);
            bool unitsAgree = declaredToMm.HasValue &&
                              Math.Abs(declaredToMm.Value - set.SourceUnitsToMm) < 1e-9;
            bool acceptMismatch = request.Value<bool?>("accept_unit_mismatch") ?? false;
            if (!unitsAgree)
            {
                // THE FLAG CANNOT FIX A RESOLVABLE DISAGREEMENT, so it is not
                // offered for one.
                //
                // The first version's message said to pass accept_unit_mismatch
                // "to say the LINK is wrong and the set is right". But Revit hands
                // geometry over ALREADY SCALED by the link's unit, and nothing
                // downstream multiplies by the set's unit - so the flag corrected
                // nothing and simply proceeded over geometry a thousand times too
                // large, reporting unit_mismatch_accepted as though that were a
                // disclosure. A 200 mm wall became 200 m.
                //
                // The one case the flag CAN honour is a link that declares no
                // resolvable unit at all: there is nothing to disagree with, and
                // the geometry is trusted as handed over.
                if (declaredToMm.HasValue)
                    return CommandResult.Fail(
                        "unit_mismatch: the CAD link declares '" + declared + "' (" +
                        declaredToMm.Value.ToString("0.###", CultureInfo.InvariantCulture) +
                        " mm per unit) and the requirement set declares '" + set.SourceUnits + "' (" +
                        set.SourceUnitsToMm.ToString("0.###", CultureInfo.InvariantCulture) +
                        " mm per unit). Revit hands this geometry over ALREADY scaled by the link's unit, so " +
                        "nothing here can rescale it - and building anyway would put the model out by a factor " +
                        "of " + (declaredToMm.Value / set.SourceUnitsToMm).ToString("0.###", CultureInfo.InvariantCulture) +
                        ". Correct the requirement set to say '" + declared + "', or re-link the DWG with the " +
                        "unit it was drawn in. accept_unit_mismatch does NOT apply here: it cannot rescale " +
                        "anything, and it is only for a link that declares no unit at all.");

                if (!acceptMismatch)
                    return CommandResult.Fail(
                        "unit_undeclared: the CAD link declares '" + (declared ?? "(nothing)") + "', which is not " +
                        "a unit this bridge can resolve, and the requirement set declares '" + set.SourceUnits +
                        "'. Revit still hands the geometry over scaled by whatever the link is set to. Re-link " +
                        "the DWG with an explicit unit, or pass accept_unit_mismatch=true to accept the geometry " +
                        "exactly as Revit hands it over - which is a claim that the coordinates are already right.");
            }

            // Revit hands geometry over already transformed and in feet; the
            // harvester converts to mm. So the requirement set's source unit is a
            // DECLARATION to check against, not a scale to apply twice.
            int maxPrimitives = Math.Max(1, Math.Min(500000, request.Value<int?>("max_primitives") ?? 200000));
            CadHarvest harvest = CadGeometryHarvest.Harvest(doc, element, set.ArcSagittaMm, maxPrimitives);

            // A PARTIAL READING MUST NOT PRODUCE A CONFIDENT PLAN.
            //
            // Truncation lowers the numerator and the denominator together, so a
            // walk that stopped two thirds of the way through a site plan still
            // reports coverage near 1.0 and raises no warning. The only trace was
            // a nested truncated:true beside a coverage block saying 97%.
            if (harvest.Truncated)
                return CommandResult.Fail(
                    "reading_is_partial: the geometry walk stopped at its bound of " + maxPrimitives +
                    " primitives, so this drawing was only partly read. A plan built from it would look " +
                    "complete - the coverage fraction cannot see the part that was never walked - and the " +
                    "elements past the bound would simply never be built. Raise max_primitives, or convert " +
                    "the drawing in layer-filtered passes. NOTHING was planned.");
            if (harvest.GeometryUnreadable)
                return CommandResult.Fail(
                    "geometry_unreadable: Revit returned no geometry container for CAD instance " + instanceId +
                    ". This is NOT an empty drawing, and planning from it would report 'no rule matched' and " +
                    "blame your requirement set. The commonest cause is a CAD placed in a SINGLE VIEW (this " +
                    "instance is " + (facts.OwnerViewName != null ? "owned by view '" + facts.OwnerViewName + "'" :
                    "not view-specific") + "), or a link that is not loaded (status: " +
                    (facts.LinkedFileStatus ?? "unknown") + ").");

            string sourceHash = facts.FileSha256 ?? CadFacts.SourceFingerprint(facts) ?? "(no-source-identity)";
            // WHAT THE MODEL ALREADY CALLS THINGS.
            //
            // A grid name must be unique in a document and Revit refuses a
            // duplicate AT CREATION - which takes the whole batch down after
            // building part of it. Gathered here, where the document is open, so
            // the collision is a refusal in the plan rather than a rollback
            // halfway through the apply.
            // THE WALL HATCH, WHEN A WALL RULE ASKS FOR IT - read before the
            // interpretation, because it decides which pairs of faces are walls.
            CadSolidHatch solidHatch = null;
            JObject solidRead = null;
            if (set.Rules.Any(r => r.Geometry != null && r.Geometry.SolidHatchLayers.Count > 0))
            {
                solidRead = new JObject();
                solidHatch = CadBlockSource.ReadSolid(element, facts, set, harvest, request.Value<string>("dwg_path"),
                                                      Math.Max(30, Math.Min(3600,
                                                          request.Value<int?>("dwg_read_timeout_seconds") ?? 900)),
                                                      solidRead);
                if (solidHatch == null)
                    return CommandResult.Fail(
                        "solid_evidence_unread: " + ((string)solidRead["refused"] ?? "unknown") + ". " +
                        CadBlockSource.Explain(solidRead) +
                        " A wall rule of this set declares solid_hatch_layers, and its walls are not read " +
                        "without them. Nothing was examined.");
            }
            CadInterpretation interpretation = CadInterpretationRules.Interpret(
                harvest.Segments, set, sourceHash, harvest.Arcs, ExistingNames(doc, set), solidHatch);

            // A NAMING THAT COULD NOT BE SETTLED STOPS THE PLAN.
            //
            // Not because naming is more important than geometry, but because
            // every one of these means the plan would build something called
            // what nobody chose - and a grid is what every dimension in the model
            // is measured from.
            if (interpretation.NamingProblems.Count > 0)
                return CommandResult.Fail(
                    "naming_unresolved: " + string.Join(" ", interpretation.NamingProblems) +
                    " NOTHING was planned. A DWG carries no text this bridge can read - measured: no string is " +
                    "reachable from imported geometry at any depth - so every name comes from the requirement " +
                    "set, and a name it cannot settle is not one to guess at.");

            // ---- the symbols, read from the FILE ---------------------------------
            //
            // Only when the set asks for them. A set with no blocks rule never
            // starts a second reader, and one that does gets told what it cost.
            // Rows a host could not be found for. Declared here because the reply
            // is assembled before the resolution runs.
            var withdrawn = new JArray();

            // ---- each duct run's SECTION, from the drawing's labels -------------
            //
            // Only for rules that declare one. A run whose size the labels do not settle -
            // missing, ambiguous, contradictory - is NOT planned at a size nobody chose: it is
            // withdrawn with its reason, and every other run is planned.
            string sectionsFailure;
            JObject sectionsReport = CadSectionsHook.Apply(element, facts, set, harvest, request, interpretation,
                                                           withdrawn, false, out sectionsFailure);
            if (sectionsFailure != null) return CommandResult.Fail(sectionsFailure);

            JObject blocksReport = null;
            if (set.Rules.Any(r => r.Geometry != null && r.Geometry.Source == CadGeometrySource.Blocks))
                blocksReport = CadBlockSource.Read(element, facts, set, harvest, sourceHash, interpretation,
                                          request.Value<string>("dwg_path"),
                                          Math.Max(30, Math.Min(3600,
                                              request.Value<int?>("dwg_read_timeout_seconds") ?? 900)));

            bool includeIneligible = request.Value<bool?>("include_candidates_needing_review") ?? false;
            string sourceFingerprint = CadFacts.SourceFingerprint(facts);
            // ---- what this drawing has already built here ----------------------
            //
            // Read BEFORE planning, with the audit's own matcher, so that a second
            // apply of the same plan builds nothing rather than a second copy of
            // everything. MEASURED before this existed: 8 devices, then 8 more,
            // exactly coincident.
            var alreadyBuilt = new JArray();
            string alreadyBuiltProblem = null;
            try
            {
                var provenanceProblems = new List<string>();
                List<CadAuditSubject> standing = AuditCadModelCommand.Subjects(
                    doc, set, interpretation, provenanceProblems, false);
                if (standing.Count > 0)
                {
                    CadAudit standingAudit = CadAuditRules.Compare(interpretation.Candidates, standing, set,
                                                                   sourceFingerprint, facts.FileSha256);
                    var built = new HashSet<string>(StringComparer.Ordinal);
                    foreach (CadMatch m in standingAudit.Matches)
                    {
                        if (m.CandidateId == null) continue;
                        built.Add(m.CandidateId);
                        alreadyBuilt.Add(new JObject
                        {
                            ["candidate_id"] = m.CandidateId,
                            ["element_id"] = m.ElementId,
                            ["matched_on"] = m.MatchedOn,
                            ["state"] = m.State,
                            ["differences"] = new JArray(m.Differences)
                        });
                    }
                    if (built.Count > 0)
                        foreach (CadCandidate c in interpretation.Candidates)
                            if (built.Contains(c.Id)) c.EligibleForAutomaticApply = false;
                }
            }
            catch (Exception ex)
            {
                alreadyBuiltProblem = ex.Message;
            }
            JObject alreadyBuiltReport = null;
            if (alreadyBuilt.Count > 0)
                alreadyBuiltReport = new JObject
                {
                    ["count"] = alreadyBuilt.Count,
                    ["rows"] = alreadyBuilt,
                    ["agree"] = alreadyBuilt.Count(x => (string)x["state"] == "agrees"),
                    ["differ"] = alreadyBuilt.Count(x => (string)x["state"] == "differs"),
                    ["means"] = "these candidates are already in this model, matched by the same ladder the " +
                                "audit uses. They are NOT planned again: applying this plan a second time " +
                                "would otherwise build a second copy of each, exactly coincident. Each row says " +
                                "whether the element AGREES with this drawing or DIFFERS, and in what: a row that " +
                                "differs is built and not correct, and bringing it back is " +
                                "horizun_plan_cad_update's work, not this plan's."
                };


            CadConversionPlan plan = CadConversionPlanRules.Plan(interpretation, set, sourceFingerprint, includeIneligible);

            // ---- the fall, when the caller says where the drawing drains to -----
            //
            // A slope declared per layer says HOW STEEP and not WHICH WAY. The
            // direction is a fact about the network, so without an outfall every
            // run here is built flat - connected, and draining nowhere.
            JObject fallBlock = null;
            JArray outfall = request["outfall"] as JArray;
            if (outfall != null)
            {
                if (outfall.Count < 2)
                    return CommandResult.Fail(
                        "outfall_malformed: outfall must be [x, y] in millimetres, in the drawing's own " +
                        "coordinates - the point the network drains to. NOTHING was planned.");

                // THE SAME GEOMETRY AND THE SAME TOLERANCE AS THE INTERPRETATION.
                // A semantic id is layer plus endpoints at a tolerance; two readings
                // agree on an id only when their tolerances agree, and this plan is
                // matched to this network by exactly that id.
                var options = new CadNetworkOptions
                {
                    ConnectToleranceMm = set.PointToleranceMm,
                    IdentityToleranceMm = set.PointToleranceMm,
                    GapReviewDistanceMm = set.GapToleranceMm
                };
                CadNetwork network = CadNetworkRules.Build(
                    harvest.Segments, options, CadNetworkRules.DeclarationsFrom(set), harvest.Arcs);

                double outfallTolerance = request.Value<double?>("outfall_tolerance_mm") ?? 50.0;
                var at = new CadPoint(outfall[0].Value<double>(), outfall[1].Value<double>(),
                                      outfall.Count > 2 ? outfall[2].Value<double>() : 0);
                string node = CadFallRules.NodeNear(network, at, outfallTolerance);
                if (node == null)
                    return CommandResult.FailWithDetail(
                        "outfall_matches_no_node: no junction of this drawing's network is within " +
                        outfallTolerance.ToString("0.#", CultureInfo.InvariantCulture) + " mm of that point.",
                        new JObject
                        {
                            ["refused"] = "outfall_matches_no_node",
                            ["outfall"] = outfall,
                            ["tolerance_mm"] = outfallTolerance,
                            ["nodes"] = network.Junctions.Count,
                            ["means"] = "an outfall placed on the wrong node inverts an entire drainage " +
                                        "layout while looking plausible, so the nearest node is NOT taken " +
                                        "when it is out of tolerance. NOTHING was planned."
                        });

                CadFall fall = CadFallRules.Compute(network, node,
                    request.Value<double?>("outfall_invert_mm") ?? 0.0, run => run.SlopePercent);

                CadConversionPlanRules.CadFallApplication applied =
                    CadConversionPlanRules.ApplyFall(plan, network, fall, set.PointToleranceMm);

                // ASKED FOR FALL AND GOT NONE IS A REFUSAL, not a footnote.
                //
                // The plan would come back byte-identical to one nobody asked about
                // fall at all, and the only difference would live in a block the
                // caller has to read. This one is loud instead.
                if (!applied.Ok || applied.ActionsGivenFall == 0)
                    return CommandResult.FailWithDetail(
                        "fall_not_applied: an outfall was named and no planned run took a height from it. " +
                        (applied.Refusal ?? "Every run that could have taken one was skipped; the detail " +
                         "says why, run by run - commonly a layer with no declared slope, or a run with no " +
                         "declared bore, which is what an invert has to be turned into a centreline with.") +
                        " If nothing in this drawing falls - a pressure main, a duct, a conduit - then omit " +
                        "the outfall and every run is planned at the elevation its rule declares, which for " +
                        "those is correct. NOTHING was planned.",
                        new JObject
                        {
                            ["refused"] = "fall_not_applied",
                            ["fall"] = fall.ToJson(),
                            ["fall_application"] = applied.ToJson()
                        });

                // The plan changed after it was fingerprinted, so it is fingerprinted
                // again - the fingerprint is what an apply checks the plan by.
                plan.PlanFingerprint = CadConversionPlanRules.Fingerprint(
                    plan, set, sourceFingerprint, includeIneligible);

                fallBlock = new JObject
                {
                    ["outfall_node"] = node,
                    ["outfall_tolerance_mm"] = outfallTolerance,
                    ["fall"] = fall.ToJson(),
                    ["fall_application"] = applied.ToJson(),
                    ["means"] = "the planned runs carry the heights this drawing's declared slopes imply, " +
                                "measured along the network from the outfall. The upstream end of each run " +
                                "was decided by the NETWORK and not by which point the drawing listed first."
                };
            }

            JObject report = CadConversionPlanRules.ToJson(plan, set);
            report["fall"] = fallBlock ?? (JToken)JValue.CreateNull();
            if (fallBlock == null)
                report["fall_means"] =
                    "no outfall was named, so every run is planned at the flat elevation its rule declares. " +
                    "A slope declared per layer says HOW STEEP and not WHICH WAY: the direction is a fact " +
                    "about the network, and naming the point it drains to is the only thing that settles it. " +
                    "A drainage layout built flat is connected and drains nowhere, and passes every other " +
                    "check this bridge has.";
            report["mode"] = "plan";
            report["document"] = SafeTitle(doc);
            report["instance_id"] = instanceId;
            report["instance_name"] = facts.Name;
            report["source"] = new JObject
            {
                ["fingerprint"] = sourceFingerprint,
                ["file_sha256"] = facts.FileSha256,
                ["source_set_sha256"] = CadDwgCache.SourceSetSha256(facts.ExternalPath, facts.FileSha256),
                ["external_path"] = facts.ExternalPath,
                ["linked_file_status"] = facts.LinkedFileStatus,
                ["declared_units"] = declared,
                ["transform_fingerprint"] = facts.TransformFingerprint,
                ["units_agree_with_requirement_set"] = unitsAgree,
                ["unit_mismatch_accepted"] = !unitsAgree,
                // WHICH HALF CAME FROM WHERE. source_set_sha256 above already moves when a reference does;
                // this says why that matters here - the geometry is the link's, the labels are the file's,
                // and a plan can be made of one issue's runs and the next issue's sizes without a word.
                ["geometry_source"] = CadReadingHelper.GeometrySource(facts.ExternalPath)
            };
            report["harvest_coverage"] = harvest.CoverageJson(set.ArcSagittaMm);

            // MAY THIS PLAN BE APPLIED? Two halves, two places: the geometry is the link as Revit loaded it,
            // the sizes are the file read now. Publishing both identities made a mismatch observable; it did
            // not make building from one safe. See Core/CadSourceCoherence.cs - only a state this bridge can
            // DEMONSTRATE grants applicable, and everything else keeps the diagnosis and withholds it.
            JObject coherence = CadSourceCoherence.Evaluate(doc, element, facts, harvest, false);
            report["coherence"] = coherence;
            bool applicable = coherence.Value<bool?>("applicable") ?? false;
            report["applicable"] = applicable;
            report["applicable_means"] = applicable
                ? "the link and the files are the same issue of the drawing, so horizun_apply_cad_plan will " +
                  "accept this plan - it re-checks the same thing before writing."
                : "this plan is NOT ready to apply: " + coherence.Value<string>("means") + " The actions are " +
                  "still here to read and to reason about; horizun_apply_cad_plan will refuse them while the " +
                  "state is " + coherence.Value<string>("state") + ". " + coherence.Value<string>("remedy");

            // THE ZONE, NAMED BESIDE EVERY NUMBER IT CHANGED.
            //
            // Coverage, the layer map and "no rule matched" all describe the zone
            // when there is one, and a reader who does not know that reads them
            // as being about the floor. The crossing count is the one worth
            // arguing with: those are runs that leave the apartment.
            if (interpretation.ExtentDescription != null)
                report["extent"] = new JObject
                {
                    ["declared_mm"] = interpretation.ExtentDescription,
                    ["segments_outside"] = interpretation.SegmentsOutsideExtent,
                    ["segments_crossing"] = interpretation.SegmentsCrossingExtent,
                    ["crossing_policy"] = set.ExtentMm?.Crossing,
                    ["segments_crossing_kept_whole"] = interpretation.SegmentsCrossingKept,
                    ["wall_margin_mm"] = set.ExtentMm?.WallMarginMm,
                    ["segments_read_in_margin"] = interpretation.SegmentsInMargin,
                    ["walls_outside_zone"] = interpretation.CandidatesOutsideExtent,
                    ["means"] =
                        "This set declares source.extent_mm, so everything below describes that zone and not " +
                        "the whole drawing. The drawing was still read whole. segments_crossing reach past the " +
                        "zone's edge: under crossing_policy 'exclude' they were NOT planned - half a run built to " +
                        "an invisible boundary is worse than none - and under 'whole' they were read entire, never " +
                        "clipped."
                };
            if (interpretation.IdentityCollisions.Count > 0)
                report["identity_collisions"] = interpretation.IdentityCollisions;

            // WHAT EACH NAMING PASS DECIDED, and on what. A reviewer checking
            // "why is this grid called 3" reads named_on rather than re-deriving
            // the order.
            if (interpretation.Naming.Count > 0)
            {
                var naming = new JObject();
                foreach (var kv in interpretation.Naming.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    CadRule rule = set.Rules.FirstOrDefault(r => r.Id == kv.Key);
                    naming[kv.Key] = kv.Value.ToJson(rule?.Naming);
                }
                report["naming"] = naming;
                report["naming_means"] =
                    "A DWG carries no text this bridge can read, so every name here came from the requirement " +
                    "set. named_on records what each assignment was earned on, so it can be checked without " +
                    "being re-derived.";
            }
            if (blocksReport != null) report["blocks"] = blocksReport;
            if (sectionsReport != null) report["sections"] = sectionsReport;
            if (alreadyBuiltReport != null) report["already_built"] = alreadyBuiltReport;
            if (interpretation.ClosedLoops.Count > 0)
            {
                report["closed_loops"] = new JArray(interpretation.ClosedLoops);
                report["closed_loops_means"] =
                    "every closed loop these rules met, and what was read into it. A loop whose corners are joined " +
                    "THROUGH its inside is a figure - a route does not cross itself - and its edges are not runs. A " +
                    "loop with nothing crossing it is not decided by its shape: its edges are proposed and HELD for " +
                    "review, because a ring main closes and so does a boundary drawn on the same layer.";
            }
            if (alreadyBuiltProblem != null) report["already_built_unreadable"] = alreadyBuiltProblem;
            if (interpretation.DoubleLineReasoning.Count > 0)
                report["double_line_reasoning"] = interpretation.DoubleLineReasoning;
            report["layer_map"] = new JObject(interpretation.LayerMap
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new JProperty(kv.Key, new JArray(kv.Value))));
            report["unclaimed"] = new JArray(interpretation.Unclaimed.Select(u => new JObject
            {
                ["layer"] = u.Layer,
                ["reason"] = u.Reason,
                ["entity_count"] = u.EntityCount,
                ["rules_that_looked"] = new JArray(u.RuleIds),
                ["means"] = u.Means
            }));
            report["review_bypassed"] = includeIneligible;
            report["candidates_needing_review"] = interpretation.NeedingReview.Count();
            report["visibility_coverage"] = DocumentVisibility.Measure(doc).ToJson();

            // The plan as a ready execute_plan call: applying it reuses the atomic,
            // confirmed, post-commit-verified write path that already exists.
            string target = request.Value<string>("target_document") ?? SafeTitle(doc);
            List<JObject> creates = CadConversionPlanRules.AsCreateRequests(plan, target,
                Math.Max(1, Math.Min(200, request.Value<int?>("max_per_batch") ?? 100)));

            // RESOLVE THE NAMES NOW, AGAINST THIS DOCUMENT, OR REFUSE.
            //
            // MEASURED on the live chain, 2026-08-26: the plan emitted level_name
            // and type_name, horizun_create_elements takes level_id and type_id,
            // and so every action this plan had ever produced was unbuildable. The
            // rehearsal did not catch it either - it validated a request nobody
            // could execute and handed back confirmation tokens for it. A plan
            // whose actions cannot run is not a plan; it is a document.
            //
            // A drawing carries no level, so the level comes from the rule, or
            // from this call, or from nowhere - and "nowhere" is a refusal, not a
            // guess. Choosing a storey for somebody's building is exactly the kind
            // of decision this bridge does not make on its own.
            var resolved = new JArray();
            var wallTypesChosen = new JArray();
            string unresolved = ResolveWallTypes(doc, creates, resolved, withdrawn, wallTypesChosen);
            if (unresolved != null) return CommandResult.Fail(unresolved);
            if (wallTypesChosen.Count > 0) report["wall_types_chosen"] = wallTypesChosen;
            if (solidRead != null)
            {
                solidRead["pairs_without_hatched_material"] = interpretation.SolidVetoes;
                report["solid_evidence"] = solidRead;
            }
            unresolved = ResolveNames(doc, creates, request, resolved);
            if (unresolved != null) return CommandResult.Fail(unresolved);

            // THE TWO LEVELS A SHAFT RUNS BETWEEN, and the view a room separator
            // belongs to. Both are resolved here for the same reason the level and
            // the type are: a drawing carries neither, and an id is something only
            // the open document can supply.
            unresolved = ResolveShaftsAndViews(doc, creates, resolved);
            if (unresolved != null) return CommandResult.Fail(unresolved);

            // The HOST, after the level and the type: a door needs a wall, and
            // the drawing has no ids to name one with.
            unresolved = ResolveHosts(doc, creates, resolved, set, harvest.Segments, withdrawn);
            if (unresolved != null) return CommandResult.Fail(unresolved);
            WithdrawHostless(doc, creates, withdrawn);
            WithdrawOccupied(doc, creates, set, withdrawn);

            // A BATCH WITH NOTHING LEFT IN IT IS NOT A BATCH. Withdrawing the only
            // row of a stage must not leave an empty create for the apply to send.
            creates = creates.Where(c => ((JArray)c["elements"]).Count > 0).ToList();

            // ---- what became of every instance, counted where it is known -----
            //
            // This used to sit beside the blocks report, which is assembled before
            // the hosts are resolved - so it counted the plan's intentions rather
            // than its rows, and reported 21 modelled while the request it emitted
            // held 8. Thirteen rows had been withdrawn in between. The accounting
            // is the one thing that must not lose anything, so it runs here, where
            // the rows are final.
            int emittedRows = creates.Sum(c => ((JArray)c["elements"]).Count);
            if (withdrawn.Count > 0)
                report["withdrawn"] = new JObject
                {
                    ["rows"] = withdrawn,
                    ["means"] = "these candidates were read, matched a rule and were NOT planned: each one says " +
                                "why and what it measured. They are not failures of the run and they are not " +
                                "silently absent - an audit will report them as candidates nobody built."
                };

            // THE CATALOGUE, AS A WHOLE, before anything is written.
            {
                var wallRows = withdrawn.OfType<JObject>().Where(w => (string)w["kind"] == "wall").ToList();
                var chosenRows = wallTypesChosen.OfType<JObject>().ToList();
                var earlier = (request["withdrawn_walls"] as JArray ?? new JArray()).OfType<JObject>()
                    .Where(w => (string)w["kind"] == "wall").ToList();
                var symbolsOut = withdrawn.OfType<JObject>().Where(w => (string)w["kind"] != "wall").ToList();
                var alternatives = new List<Tuple<string, double>>();
                var notFound = new JArray();
                foreach (string name in (request["alternative_wall_types"] as JArray ?? new JArray()).Select(x => (string)x))
                {
                    var wt = FindType(doc, name) as WallType;
                    double width = double.NaN;
                    try { if (wt != null) width = wt.Width * 304.8; } catch { }
                    if (double.IsNaN(width)) notFound.Add(name);
                    else alternatives.Add(Tuple.Create(name, width));
                }
                if (chosenRows.Count + wallRows.Count > 0 || earlier.Count > 0)
                {
                    double tol = chosenRows.Concat(wallRows).Select(r => r.Value<double?>("tolerance_mm"))
                                           .FirstOrDefault(v => v.HasValue) ?? 3.2;
                    JObject preflight = CadCatalogPreflight.Summarize(chosenRows, wallRows, alternatives, tol);
                    if (notFound.Count > 0) preflight["alternatives_not_in_this_document"] = notFound;
                    var affected = CadCatalogPreflight.AffectedSymbols(wallRows.Concat(earlier), symbolsOut,
                                                                       set.PointToleranceMm);
                    if (affected.Count > 0)
                    {
                        preflight["affected_symbols"] = affected;
                        preflight["affected_symbols_lost_with_a_withdrawn_wall"] =
                            affected.Count(a => (string)a["verdict"] == "lost_with_that_wall");
                    }
                    report["catalog_preflight"] = preflight;
                }
            }

            if (blocksReport != null)
            {
                int considered = blocksReport.Value<int?>("instances_considered") ?? 0;
                // From the total, never from the listed names: the list is bounded.
                int unclaimed = blocksReport.Value<int?>("unclaimed_instances") ?? 0;
                int tied = 0;
                foreach (JObject t in (blocksReport["ties"] as JArray ?? new JArray()).OfType<JObject>())
                    tied += t.Value<int?>("count") ?? 0;
                int mirrorPending = plan.Deferred.Count(d =>
                    d.Reasons.Any(r => r != null && r.Contains("MIRRORED")));
                // ALREADY BUILT IS NOT HELD FOR REVIEW. Those candidates are made
                // ineligible so they are not built twice, and used to be counted as
                // waiting for a person - 39 of them, on a model where all 39 agreed.
                var builtIds = new HashSet<string>(alreadyBuilt.OfType<JObject>()
                    .Select(x => (string)x["candidate_id"]).Where(x => x != null), StringComparer.Ordinal);
                int alreadyThere = plan.Deferred.Count(d => d.CandidateId != null && builtIds.Contains(d.CandidateId) &&
                    !d.Reasons.Any(r => r != null && r.Contains("MIRRORED")));
                int reviewed = plan.Deferred.Count - mirrorPending - alreadyThere;
                // Only the rows a SYMBOL produced; a withdrawn wall is not an instance.
                List<JObject> withdrawnSymbols = withdrawn.OfType<JObject>()
                    .Where(w => (string)w["kind"] != "wall").ToList();

                blocksReport["classification"] = new JObject
                {
                    ["considered"] = considered,
                    ["modelled"] = emittedRows,
                    ["outside_the_rules"] = unclaimed,
                    ["ambiguous_two_rules_of_equal_precedence"] = tied,
                    ["mirror_unresolved"] = mirrorPending,
                    ["already_built"] = alreadyThere,
                    ["held_for_review"] = reviewed,
                    ["no_host_within_allowance"] = withdrawnSymbols.Count(w =>
                        (string)w["reason"] == "no_host_within_allowance" ||
                        (string)w["reason"] == "no_wall_face_carries_this_point"),
                    ["side_of_the_wall_not_stated"] = withdrawnSymbols.Count(w =>
                        (string)w["reason"] == "side_of_the_wall_not_stated"),
                    ["nearer_drawn_wall_is_not_in_the_model"] = withdrawnSymbols.Count(w =>
                        (string)w["reason"] == "nearer_drawn_wall_is_not_in_the_model"),
                    ["family_needs_a_host_the_rule_does_not_declare"] = withdrawnSymbols.Count(w =>
                        (string)w["reason"] == "family_needs_a_host_the_rule_does_not_declare"),
                    ["means"] = "every block instance this reading considered, by what became of it. " +
                                "outside_the_rules is not a failure: it is a symbol this correspondence set is " +
                                "not about, and the set is deliberately bounded. modelled is the number of rows " +
                                "the emitted request actually carries, counted after every exclusion.",
                    ["unaccounted"] = considered - emittedRows - unclaimed - tied - mirrorPending - alreadyThere
                                      - reviewed - withdrawnSymbols.Count,
                    ["unaccounted_means"] = "instances this classification cannot place. It should be zero; a " +
                                            "number here is a defect in the accounting, not in the drawing."
                };
            }

            var emittedActions = new JArray(creates.Select(c => new JObject
            {
                ["key"] = "cad-stage-" + (int)c["stage"] + "-batch-" + (int)c["batch_of_stage"],
                ["tool"] = "horizun_create_elements",
                ["arguments"] = c
            }));
            // WHICH candidate produced WHICH element, by the element's index in
            // its own request. The apply keys provenance off the index the row
            // reports, never off the position of the row in the reply: a create
            // that skips a row would otherwise stamp every following element
            // with somebody else's origin, which is worse than stamping none.
            // BY NAME, NOT BY POSITION.
            //
            // This used to pair element k of each batch with plan action k, which
            // is correct exactly as long as no row is ever left out - and rows are
            // now left out, because a symbol with no wall under it waits while its
            // neighbours are built. Positional pairing would have given the second
            // element the third's source entity, and the audit would then report a
            // model agreeing with a drawing it does not agree with.
            //
            // Each emitted row carries `source_row`, assigned when the plan was
            // emitted; the index is built by reading that number back off the rows
            // that remain. A row with no name is reported rather than guessed at.
            var actionByRow = new Dictionary<int, CadPlannedAction>();
            foreach (CadPlannedAction a in plan.Actions)
                if (a.SourceRow > 0) actionByRow[a.SourceRow] = a;

            var candidateIndex = new JArray();
            var unnamed = new JArray();
            foreach (JObject c in creates)
            {
                var rows = new JArray();
                var elements = (JArray)c["elements"];
                for (int k = 0; k < elements.Count; k++)
                {
                    var row = elements[k] as JObject;
                    int? sourceRow = row?.Value<int?>("source_row");
                    CadPlannedAction a;
                    if (sourceRow == null || !actionByRow.TryGetValue(sourceRow.Value, out a))
                    {
                        unnamed.Add(new JObject
                        {
                            ["element_index"] = k,
                            ["source_row"] = sourceRow,
                            ["means"] = "this row carries no source_row this plan issued, so nothing can say " +
                                        "which CAD entity it came from. It will be created and left ANONYMOUS " +
                                        "rather than stamped with somebody else's origin."
                        });
                        continue;
                    }
                    rows.Add(new JObject
                    {
                        ["element_index"] = k,
                        ["source_row"] = sourceRow.Value,
                        ["candidate_id"] = a.CandidateId,
                        ["geometry_id"] = a.GeometryId,
                        ["semantic_id"] = a.SemanticId,
                        ["rule_id"] = a.RuleId,
                        ["layer"] = a.Layer,
                        ["confidence"] = Math.Round(a.Confidence, 4),
                        ["source_entities"] = new JArray(a.SourceEntities)
                    });
                }
                candidateIndex.Add(new JObject
                {
                    ["key"] = "cad-stage-" + (int)c["stage"] + "-batch-" + (int)c["batch_of_stage"],
                    ["candidates"] = rows
                });
            }
            if (unnamed.Count > 0) report["rows_without_a_name"] = unnamed;
            report["candidate_index"] = candidateIndex;

            report["execute_plan_request"] = new JObject
            {
                ["target_document"] = target,
                ["dry_run"] = true,
                ["actions"] = emittedActions,
                ["note"] = "Send this to horizun_execute_plan for the atomic, confirmed write - or to " +
                           "horizun_apply_cad_plan, which does the same through the same commands AND re-checks " +
                           "the drawing has not moved since this plan was made, then records provenance on every " +
                           "element it creates."
            };
            report["resolved_names"] = resolved;
            report["apply_binding"] = new JObject
            {
                ["resolved_names"] = resolved,
                ["plan_fingerprint"] = plan.PlanFingerprint,
                ["actions_fingerprint"] = CadConversionPlanRules.ActionsFingerprint(emittedActions),
                ["source_fingerprint"] = sourceFingerprint,
                ["requirement_set_sha256"] = set.Sha256,
                ["interpretation_version"] = CadInterpretationRules.InterpretationVersion,
                // THE ISSUE OF THE DRAWING THIS PLAN WAS MADE OF, and whether that could be demonstrated.
                // The apply re-measures both: a set that moved between the plan and the apply is drift like
                // any other, and a plan made while the coherence could not be shown is not applied at all.
                ["source_set_sha256"] = CadDwgCache.SourceSetSha256(facts.ExternalPath, facts.FileSha256),
                ["coherence_state"] = coherence.Value<string>("state"),
                ["target_document"] = target,
                ["revit_version"] = SafeVersion(app),
                ["means"] = "horizun_apply_cad_plan re-measures every one of these before writing and refuses " +
                            "stale_plan naming which moved. actions_fingerprint covers the EXACT actions emitted " +
                            "above: a caller that edits a coordinate, a type, an element or the stage order is " +
                            "refused, because a binding that does not cover what is about to be built is not a " +
                            "binding at all. resolved_names covers the levels and types those ids stood for when " +
                            "this plan was made: an id that has since been deleted, or now names something else, " +
                            "is drift too - the same number pointing at a different thing is the one change a " +
                            "fingerprint over the actions cannot see."
            };
            return CommandResult.Ok(report);
        }

        /// <summary>
        /// Turn the rule's NAMES into the ids horizun_create_elements takes, or
        /// return the refusal text. Every resolution is recorded in
        /// <paramref name="resolved"/> so the apply can check the id still stands
        /// for the same thing it stood for here.
        /// </summary>
        /// <summary>
        /// The resolution this command applies to its rows - names, levels and
        /// types; shafts and views; hosts and faces - offered to the update planner
        /// so a revision builds exactly what a first conversion would. Rows it
        /// withdraws land in <paramref name="withdrawn"/>; a refusal comes back as
        /// the error string.
        /// </summary>
        internal static string ResolveRows(Document doc, List<JObject> creates, JObject request, JArray resolved,
                                           CadRequirementSet set, IList<CadSegment> drawn, JArray withdrawn,
                                           IDictionary<long, CadPoint[]> reshapedTo = null)
        {
            string e = ResolveWallTypes(doc, creates, resolved, withdrawn, null);
            if (e != null) return e;
            e = ResolveNames(doc, creates, request, resolved);
            if (e != null) return e;
            e = ResolveShaftsAndViews(doc, creates, resolved);
            if (e != null) return e;
            e = ResolveHosts(doc, creates, resolved, set, drawn, withdrawn);
            if (e != null) return e;
            WithdrawHostless(doc, creates, withdrawn);
            WithdrawOccupied(doc, creates, set, withdrawn, reshapedTo);
            return null;
        }

        /// <summary>
        /// THE WALL TYPE, BY THE THICKNESS THE DRAWING GIVES.
        ///
        /// MEASURED on two apartments: a set naming one wall type built 52 walls at
        /// 152.4 mm whose drawn thicknesses ran from 125 to 322 mm, and the audit let
        /// 18 of them pass because it compared widths with the revision tolerance.
        /// A rule that lists wall_types gets, per wall, the listed type whose width -
        /// read from THIS model, not declared - is nearest the drawn thickness within
        /// the rule's tolerance. None fits: the wall is withdrawn with the nearest
        /// type named (or built as family_type when the rule says so). Two fit
        /// equally: withdrawn, because choosing between them is not a measurement.
        /// </summary>
        private static string ResolveWallTypes(Document doc, List<JObject> creates, JArray resolved,
                                               JArray withdrawn, JArray chosen)
        {
            var byName = new Dictionary<string, WallType>(StringComparer.Ordinal);
            var seen = new HashSet<long>();
            foreach (JObject c in creates)
                foreach (JObject row in ((JArray)c["elements"]).OfType<JObject>().ToList())
                {
                    var choices = row["wall_type_choices"] as JArray;
                    if (choices == null) continue;
                    double tolerance = row.Value<double?>("wall_type_tolerance_mm") ?? 3.2;
                    string otherwise = row.Value<string>("wall_type_otherwise") ?? "withdraw";
                    double thickness = row.Value<double?>("interpreted_thickness_mm") ?? double.NaN;
                    foreach (string k in new[] { "wall_type_choices", "wall_type_tolerance_mm",
                                                 "wall_type_otherwise", "interpreted_thickness_mm" })
                        row.Remove(k);

                    var options = new List<Tuple<string, WallType, double>>();
                    foreach (string name in choices.Select(x => (string)x))
                    {
                        WallType w;
                        if (!byName.TryGetValue(name, out w))
                        {
                            w = FindType(doc, name) as WallType;
                            if (w == null)
                                return "wall_type_not_found: the set lists '" + name + "' among the wall types to " +
                                       "choose by thickness, and " + Quote(SafeTitle(doc)) + " has no wall type of that " +
                                       "name. NOTHING was planned.";
                            byName[name] = w;
                        }
                        double width;
                        try { width = w.Width * 304.8; } catch { continue; }
                        options.Add(Tuple.Create(name, w, width));
                    }
                    var ranked = options.OrderBy(o => Math.Abs(o.Item3 - thickness)).ThenBy(o => o.Item1, StringComparer.Ordinal).ToList();
                    var fitting = ranked.Where(o => Math.Abs(o.Item3 - thickness) <= tolerance + 1e-6).ToList();
                    Tuple<string, WallType, double> nearest = ranked.FirstOrDefault();

                    JObject Where() => new JObject
                    {
                        ["source_row"] = row["source_row"],
                        ["kind"] = "wall",
                        ["from_mm"] = row["start"],
                        ["to_mm"] = row["end"],
                        ["thickness_mm"] = Math.Round(thickness, 1),
                        ["tolerance_mm"] = tolerance,
                        ["nearest_type"] = nearest == null ? null : nearest.Item1,
                        ["nearest_width_mm"] = nearest == null ? (JToken)JValue.CreateNull() : Math.Round(nearest.Item3, 1),
                        ["candidates"] = new JArray(ranked.Take(4).Select(o => new JObject
                        {
                            ["type"] = o.Item1, ["width_mm"] = Math.Round(o.Item3, 1),
                            ["off_by_mm"] = Math.Round(o.Item3 - thickness, 1)
                        }))
                    };

                    if (fitting.Count == 0)
                    {
                        if (otherwise == "family_type")
                        {
                            JObject note = Where();
                            note["chosen"] = row.Value<string>("type_name");
                            note["because"] = "no listed type is within the tolerance; the rule says to build the " +
                                              "wall as its family_type, and the audit will report the width";
                            if (chosen != null) chosen.Add(note);
                            continue;
                        }
                        JObject w = Where();
                        w["reason"] = "no_wall_type_for_this_thickness";
                        w["means"] = "the drawing gives this wall a thickness no listed wall type has, within the " +
                                     "tolerance. It is NOT planned: building it at another width misstates every " +
                                     "quantity and every face a device is placed on. Add a type of this width, or " +
                                     "list one.";
                        withdrawn.Add(w);
                        ((JArray)c["elements"]).Remove(row);
                        continue;
                    }
                    if (fitting.Count > 1 &&
                        Math.Abs(Math.Abs(fitting[0].Item3 - thickness) - Math.Abs(fitting[1].Item3 - thickness)) < 0.05 &&
                        Rid.Value(fitting[0].Item2.Id) != Rid.Value(fitting[1].Item2.Id))
                    {
                        JObject w = Where();
                        w["reason"] = "wall_types_fit_equally";
                        w["means"] = "two listed wall types are equally near this thickness, so which one it is " +
                                     "is not a measurement. It is NOT planned.";
                        withdrawn.Add(w);
                        ((JArray)c["elements"]).Remove(row);
                        continue;
                    }

                    Tuple<string, WallType, double> pick = fitting[0];
                    row["type_name"] = pick.Item1;
                    if (seen.Add(Rid.Value(pick.Item2.Id)))
                        resolved.Add(Resolved("wall_type", pick.Item2, pick.Item1, TypeLabel(pick.Item2)));
                    if (chosen != null)
                    {
                        JObject note = Where();
                        note["chosen"] = pick.Item1;
                        note["chosen_width_mm"] = Math.Round(pick.Item3, 1);
                        note["off_by_mm"] = Math.Round(pick.Item3 - thickness, 1);
                        chosen.Add(note);
                    }
                }
            return null;
        }

        /// <summary>
        /// A FAMILY THAT NEEDS A HOST IS NOT PLANNED WITHOUT ONE.
        ///
        /// MEASURED on a second apartment with the first one's rules: the panel
        /// rule declared no host - the first zone had no panel, so it was never
        /// exercised - and the panelboard family can only be placed on one. The
        /// rehearsal refused it, and because a stage is atomic, the one row took
        /// the other 41 with it. The row is withdrawn here instead, naming the
        /// rule: which host a panel stands on is the requirement set's statement
        /// to make, not this bridge's guess.
        /// </summary>
        private static void WithdrawHostless(Document doc, List<JObject> creates, JArray withdrawn)
        {
            foreach (JObject c in creates)
                foreach (JObject row in ((JArray)c["elements"]).OfType<JObject>().ToList())
                {
                    string kind = row.Value<string>("kind");
                    if (kind != "family_instance" || row["host_id"] != null) continue;
                    long? typeId = row.Value<long?>("type_id");
                    var symbol = typeId.HasValue ? doc.GetElement(Rid.Make(typeId.Value)) as FamilySymbol : null;
                    if (symbol == null) continue;
                    FamilyPlacementType placement;
                    try { placement = symbol.Family.FamilyPlacementType; } catch { continue; }
                    if (placement == FamilyPlacementType.OneLevelBased || placement == FamilyPlacementType.TwoLevelsBased)
                        continue;
                    XYZ point = PlanPoint(row["point"]);
                    withdrawn.Add(new JObject
                    {
                        ["source_row"] = row["source_row"],
                        ["kind"] = kind,
                        ["at_mm"] = point == null ? null
                            : new JArray(Math.Round(point.X * 304.8, 1), Math.Round(point.Y * 304.8, 1)),
                        ["reason"] = "family_needs_a_host_the_rule_does_not_declare",
                        ["family"] = SafeName(symbol.Family),
                        ["type"] = SafeName(symbol),
                        ["placement_type"] = placement.ToString(),
                        ["means"] = "this family can only be placed on a host, and the rule that produced the row " +
                                    "declares none. It is NOT planned; the rows beside it are. Declaring hosted_on " +
                                    "on that rule is a statement about the building a person makes."
                    });
                    ((JArray)c["elements"]).Remove(row);
                }
        }

        /// <summary>
        /// A WALL IS NOT PLANNED WHERE A WALL ALREADY STANDS.
        ///
        /// MEASURED on a model built from this drawing: after the wall reading
        /// improved - two pairs of collinear pieces were read as the two walls they
        /// are - planning the same drawing again recognised nine built walls by
        /// identity and emitted two new ones exactly on top of four that stand.
        /// Identity cannot see that: the reading changed, not the drawing. So a
        /// planned wall whose solid intersects a wall already on its level is
        /// WITHDRAWN, naming the occupant. Nothing is replaced here; comparing a
        /// built wall with a changed reading is horizun_plan_cad_update's work.
        /// </summary>
        private static void WithdrawOccupied(Document doc, List<JObject> creates, CadRequirementSet set, JArray withdrawn,
                                             IDictionary<long, CadPoint[]> reshapedTo = null)
        {
            List<Wall> standing = null;
            double? defaultWidthMm = null;
            foreach (JObject c in creates)
                foreach (JObject row in ((JArray)c["elements"]).OfType<JObject>().ToList())
                {
                    if (row.Value<string>("kind") != "wall") continue;
                    var s = row["start"] as JArray;
                    var t = row["end"] as JArray;
                    if (s == null || t == null || s.Count < 2 || t.Count < 2) continue;
                    if (standing == null) standing = CadHostResolver.Walls(doc);
                    if (standing.Count == 0) return;

                    double widthMm;
                    long? typeId = row.Value<long?>("type_id");
                    var wallType = typeId.HasValue ? doc.GetElement(Rid.Make(typeId.Value)) as WallType : null;
                    if (wallType != null) widthMm = wallType.Width * 304.8;
                    else
                    {
                        if (defaultWidthMm == null)
                        {
                            var d = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.WallType)) as WallType;
                            defaultWidthMm = d != null ? d.Width * 304.8 : 0;
                        }
                        widthMm = defaultWidthMm.Value;
                    }
                    long? levelId = row.Value<long?>("level_id");
                    var a0 = new CadPoint((double)s[0], (double)s[1]);
                    var a1 = new CadPoint((double)t[0], (double)t[1]);

                    var occupants = new JArray();
                    foreach (Wall w in standing)
                    {
                        if (levelId.HasValue && Rid.Value(w.LevelId) != levelId.Value) continue;
                        Line line = (w.Location as LocationCurve)?.Curve as Line;
                        if (line == null) continue;
                        XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
                        var w0 = new CadPoint(p0.X * 304.8, p0.Y * 304.8);
                        var w1 = new CadPoint(p1.X * 304.8, p1.Y * 304.8);
                        // A WALL THE SAME UPDATE RE-SHAPES holds the space of its NEW line.
                        CadPoint[] next = null;
                        if (reshapedTo != null && reshapedTo.TryGetValue(Rid.Value(w.Id), out next))
                        { w0 = next[0]; w1 = next[1]; }
                        double across, along;
                        if (!CadWallReadings.SolidsIntersect(a0, a1, widthMm, w0, w1,
                                w.Width * 304.8, set.AngleToleranceDegrees, set.PointToleranceMm, set.WallOverlapMm,
                                out across, out along))
                            continue;
                        string problem;
                        CadProvenance p = CadProvenanceStore.Read(w, out problem);
                        occupants.Add(new JObject
                        {
                            ["element_id"] = Rid.Value(w.Id),
                            ["across_overlap_mm"] = Math.Round(across, 1),
                            ["along_overlap_mm"] = Math.Round(along, 1),
                            ["built_from_cad"] = p != null,
                            ["its_candidate_id"] = p?.CandidateId
                        });
                    }
                    if (occupants.Count == 0) continue;

                    withdrawn.Add(new JObject
                    {
                        ["source_row"] = row["source_row"],
                        ["kind"] = "wall",
                        ["from_mm"] = new JArray(Math.Round(a0.X, 1), Math.Round(a0.Y, 1)),
                        ["to_mm"] = new JArray(Math.Round(a1.X, 1), Math.Round(a1.Y, 1)),
                        ["reason"] = "space_already_occupied_by_a_built_wall",
                        ["occupied_by"] = occupants,
                        ["means"] = "a wall already stands where this one would go, on the same level. Building " +
                                    "it would put two walls in one space. It is NOT planned. If the wall there was " +
                                    "built from this drawing under an earlier reading, horizun_plan_cad_update " +
                                    "compares the two; nothing is replaced or deleted here."
                    });
                    ((JArray)c["elements"]).Remove(row);
                }
        }

        private static string ResolveNames(Document doc, List<JObject> creates, JObject request, JArray resolved)
        {
            string defaultLevelName = request.Value<string>("level_name");
            long? defaultLevelId = request.Value<long?>("level_id");

            List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (JObject c in creates)
            {
                foreach (JObject row in ((JArray)c["elements"]).OfType<JObject>())
                {
                    string kind = row.Value<string>("kind");
                    string want = row.Value<string>("level_name") ?? defaultLevelName;
                    row.Remove("level_name");

                    // A HOLE IS NOT HOSTED ON A LEVEL, it is hosted in a slab -
                    // but the level is how a drawing says WHICH slab, because a
                    // building has the same plan on several storeys. The name is
                    // carried to ResolveHosts and consumed there; keeping it in
                    // level_name would hand create_elements a key its slab_opening
                    // does not read.
                    // A HOLE IN A WALL HAS A STOREY TOO, and for two reasons: it
                    // decides WHICH wall when several stand one above another, and
                    // it is what the sill and head heights are measured FROM.
                    if ((kind == "slab_opening" || kind == "wall_opening") && want != null)
                        row["host_level_name"] = want;

                    if (NeedsLevel(kind))
                    {
                        Level level;
                        if (want == null && defaultLevelId.HasValue)
                        {
                            level = doc.GetElement(Rid.Make(defaultLevelId.Value)) as Level;
                            if (level == null)
                                return "level_not_found: level_id " + defaultLevelId.Value + " is not a Level in " +
                                       Quote(SafeTitle(doc)) + ". NOTHING was planned. The levels there are: " +
                                       Names(levels) + ".";
                        }
                        else if (want != null)
                        {
                            level = levels.FirstOrDefault(l => string.Equals(SafeName(l), want, StringComparison.Ordinal))
                                 ?? levels.FirstOrDefault(l => string.Equals(SafeName(l), want, StringComparison.OrdinalIgnoreCase));
                            if (level == null)
                                return "level_not_found: no level in " + Quote(SafeTitle(doc)) + " is named " +
                                       Quote(want) + ". NOTHING was planned, because a storey chosen wrongly is not " +
                                       "a mistake a plan view shows. The levels there are: " + Names(levels) + ".";
                        }
                        else
                        {
                            return "level_unresolved: this plan produces " + kind + "s, which Revit hosts on a " +
                                   "level, and a 2D drawing does not carry one. NOTHING was planned. Declare " +
                                   Quote("level") + " on the rule that produced them, or pass level_name (or " +
                                   "level_id) to this call - either way the choice is recorded in the plan and " +
                                   "bound into the apply. The levels in " + Quote(SafeTitle(doc)) + " are: " +
                                   Names(levels) + ".";
                        }

                        row["level_id"] = Rid.Value(level.Id);
                        // A run's declared height is relative to THIS storey; only here is
                        // the storey known. Resolved to absolute Z and the key removed.
                        double levelElevationMm = 0;
                        try { levelElevationMm = CadUnits.FeetToMm(level.Elevation); } catch { }
                        CadConversionPlanRules.ResolveOffsetFromLevel(row, levelElevationMm);
                        if (seen.Add("level:" + Rid.Value(level.Id)))
                            resolved.Add(Resolved("level", level,
                                want ?? (defaultLevelId.HasValue ? "level_id " + defaultLevelId.Value : null)));
                    }

                    // The TYPE is optional: create_elements falls back to the
                    // document's own default and re-reads what it built. A name
                    // that matches nothing is still a refusal, because quietly
                    // building a generic wall where the set asked for a fire-rated
                    // one is the failure this whole path exists to prevent.
                    string typeName = row.Value<string>("type_name");
                    row.Remove("type_name");

                    // THE SYSTEM TYPE, by name. Revit refuses to make a pipe
                    // without one, and choosing somebody's system for them is
                    // exactly the decision this bridge does not make.
                    string systemName = row.Value<string>("system_type_name");
                    row.Remove("system_type_name");
                    if (!string.IsNullOrWhiteSpace(systemName))
                    {
                        List<MEPSystemType> systems = new FilteredElementCollector(doc)
                            .OfClass(typeof(MEPSystemType)).Cast<MEPSystemType>().ToList();
                        MEPSystemType system =
                            systems.FirstOrDefault(t => string.Equals(SafeName(t), systemName, StringComparison.Ordinal))
                            ?? systems.FirstOrDefault(t => string.Equals(SafeName(t), systemName, StringComparison.OrdinalIgnoreCase));
                        if (system == null)
                            return "system_type_not_found: no MEP system type in " + Quote(SafeTitle(doc)) +
                                   " is named " + Quote(systemName) + ", which the rule producing these " + kind +
                                   "s asked for. NOTHING was planned. The systems there are: " +
                                   (systems.Count == 0
                                        ? "(none - this document has no MEP system types at all)"
                                        : string.Join(", ", systems.Select(t => Quote(SafeName(t)))
                                                                   .OrderBy(x => x, StringComparer.Ordinal))) +
                                   ". A run put on the wrong system connects to the wrong things and reads " +
                                   "correct in every view.";
                        row["system_type_id"] = Rid.Value(system.Id);
                        if (seen.Add("system:" + Rid.Value(system.Id)))
                            resolved.Add(Resolved("system_type", system, systemName));
                    }

                    if (string.IsNullOrWhiteSpace(typeName)) continue;
                    ElementType et = FindType(doc, typeName);
                    if (et == null)
                        return "type_not_found: no element type in " + Quote(SafeTitle(doc)) + " is named " +
                               Quote(typeName) + ", which the rule producing these " + kind + "s asked for; " +
                               CadCatalogCheck.LoadedOfFamily(typeName, SameFamily(doc, typeName), kind,
                                                              TypesOfKind(doc, kind)) + " " +
                               "NOTHING was planned. Load the family or correct the requirement set - a plan that " +
                               "substitutes a default type builds a different building and verifies it happily.";
                    row["type_id"] = Rid.Value(et.Id);
                    if (seen.Add("type:" + Rid.Value(et.Id)))
                        resolved.Add(Resolved("type", et, typeName, TypeLabel(et)));
                }
            }
            return null;
        }

        /// <summary>
        /// THE WALL A DOOR BELONGS TO.
        ///
        /// The drawing carries no ids, so a hosted row arrives saying only that it
        /// needs a wall. Here - where the document is open - that becomes a
        /// specific wall: the one whose centreline passes closest to the point,
        /// within half its own length of it, and no further than the tolerance the
        /// requirement set declares for a point plus the wall's own thickness.
        ///
        /// WHEN NO WALL IS THERE, THIS REFUSES. It is almost always the same
        /// cause and worth saying out loud: the walls have not been built yet. A
        /// plan is computed before anything is applied, so a single run that
        /// converts wall layers AND door layers cannot host the doors - the walls
        /// do not exist at the moment the plan is made. Convert the walls, look at
        /// them, then plan the doors against the model that now has walls in it.
        /// The alternative - placing the door unhosted and calling it created - is
        /// the failure this whole path exists to prevent.
        /// </summary>
        private static string ResolveHosts(Document doc, List<JObject> creates, JArray resolved,
                                           CadRequirementSet set, IList<CadSegment> drawn, JArray withdrawn)
        {
            double pointToleranceMm = set.HostSearchMm;
            List<Wall> walls = null;
            List<Element> slabs = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (JObject c in creates)
                foreach (JObject row in ((JArray)c["elements"]).OfType<JObject>().ToList())
                {
                    string hostedOn = row.Value<string>("hosted_on");
                    if (hostedOn == null) continue;
                    row.Remove("hosted_on");

                    if (hostedOn == "slab")
                    {
                        string slabError = ResolveSlabHost(doc, row, resolved, seen, ref slabs);
                        if (slabError != null) return slabError;
                        continue;
                    }
                    if (hostedOn != "wall") continue;
                    var hostLayers = (row["host_layers"] as JArray)?.Select(x => (string)x).ToList();
                    row.Remove("host_layers");
                    bool endAllowed = (row["host_faces"] as JArray)?.Any(x => (string)x == "end") == true;
                    row.Remove("host_faces");

                    // A DOOR CARRIES A POINT; A HOLE CARRIES A RING. Both need the
                    // same answer - which wall - so both are resolved here, from
                    // whichever of the two the row actually has.
                    XYZ point = PlanPoint(row["point"]) ?? PlanPoint(row["host_point"]);
                    row.Remove("host_point");
                    if (point == null)
                        return "host_unresolvable: a hosted element was planned without a point. NOTHING was " +
                               "planned.";

                    if (walls == null) walls = CadHostResolver.Walls(doc);

                    // THE SHARED RULE. The incremental update asks the same
                    // question - "which wall does this door belong in" - and if
                    // the two answered differently, every update would report a
                    // rehosting on a model nobody had touched.
                    CadHostMatch match = CadHostResolver.Nearest(walls, point, pointToleranceMm);
                    Wall best = match.Wall;
                    double allowance = match.AllowanceMm;
                    double bestMm = match.DistanceMm ?? double.MaxValue;

                    if (match.NoWallsAtAll)
                        return "host_not_found: this plan places doors or windows, which Revit hosts IN a wall, " +
                               "and " + Quote(SafeTitle(doc)) + " contains no wall at all. NOTHING was planned. " +
                               "Convert the wall layers first and plan the openings against the model that " +
                               "results - a plan is computed before it is applied, so one run cannot build a " +
                               "wall and then host a door in it.";

                    // THE END OF A WALL, when the rule allows it and no side carried the point.
                    if (best == null && endAllowed && row.Value<string>("kind") == "family_instance")
                    {
                        CadEndMatch end = CadHostResolver.NearestEnd(walls, point, pointToleranceMm);
                        if (end != null)
                        {
                            string endWithdrawn = null;
                            JObject endEvidence = new JObject
                            {
                                ["wall"] = Rid.Value(end.Wall.Id), ["wall_end"] = end.End,
                                ["distance_mm"] = Math.Round(end.DistanceMm, 1)
                            };
                            if (hostLayers != null && hostLayers.Count > 0 && drawn != null)
                            {
                                var lines = drawn.Where(s => s != null && hostLayers.Any(g =>
                                    CadGlob.IsMatch(s.Layer ?? "", g, set.CaseSensitiveLayers)));
                                CadHostPlausibilityResult pe = CadHostPlausibility.CheckEnd(
                                    new CadPoint(point.X * 304.8, point.Y * 304.8),
                                    new CadPoint(end.EndPoint.X * 304.8, end.EndPoint.Y * 304.8),
                                    new CadVector(end.Outward.X, end.Outward.Y),
                                    end.Wall.Width * 304.8 / 2.0, lines, set.AngleToleranceDegrees, set.PointToleranceMm);
                                if (pe.NearerWallDrawn)
                                {
                                    endWithdrawn = "nearer_drawn_end_is_not_in_the_model";
                                    endEvidence["drawn_end_mm"] = Math.Round(pe.OtherWallMm.Value, 1);
                                    endEvidence["model_end_mm"] = Math.Round(pe.HostFaceMm.Value, 1);
                                    endEvidence["means"] = "the drawing closes this wall NEARER the symbol than the model " +
                                        "does (a pier or a return the model does not have). Hosting it on the model's " +
                                        "end would bury it in that pier. NOT planned; decide what the drawn end is.";
                                }
                                else if (pe.Jamb)
                                {
                                    endWithdrawn = "end_is_a_jamb";
                                    endEvidence["means"] = "the same wall resumes beyond the symbol: this end is a jamb, " +
                                        "and a device there belongs to the wall around the opening. NOT planned.";
                                }
                            }
                            if (endWithdrawn != null)
                            {
                                endEvidence["source_row"] = row["source_row"];
                                endEvidence["kind"] = row.Value<string>("kind");
                                endEvidence["at_mm"] = new JArray(Math.Round(point.X * 304.8, 1), Math.Round(point.Y * 304.8, 1));
                                endEvidence["reason"] = endWithdrawn;
                                withdrawn.Add(endEvidence);
                                ((JArray)c["elements"]).Remove(row);
                                continue;
                            }
                            row["host_id"] = Rid.Value(end.Wall.Id);
                            row["host_face"] = "end";
                            row.Remove("side_dead_band_mm");
                            if (seen.Add("host:" + Rid.Value(end.Wall.Id)))
                                resolved.Add(Resolved("host_wall", end.Wall,
                                    "the free END of the wall nearest the drawn symbol, " +
                                    end.DistanceMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm away (host_faces allows it)"));
                            string zEnd = RaiseToStorey(doc, end.Wall, row);
                            if (zEnd != null) return zEnd;
                            continue;
                        }
                    }

                    if (best == null)
                    {
                        // A SYMBOL WAITS; A HOLE STOPS THE RUN.
                        //
                        // An opening or a door IS its host - one cut in the wrong
                        // element, or in none, is a defect in somebody's model. A
                        // device is not: it is one of many marks on a plan, and the
                        // other twenty should not wait for it. So this row is
                        // WITHDRAWN, by name, with the distance that withdrew it.
                        string kind = row.Value<string>("kind");
                        bool isDevice = kind == "family_instance" || kind == "structural_column";
                        if (!isDevice)
                            return "host_too_far: the nearest wall to a hosted element at (" +
                                   Mm(point.X) + ", " + Mm(point.Y) + ") mm is " +
                                   bestMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm away, and " +
                                   allowance.ToString("0.#", CultureInfo.InvariantCulture) + " mm is the most this " +
                                   "set allows. NOTHING was planned. Either the wall layers have not been " +
                                   "converted yet - convert them first, then plan the openings - or the drawing " +
                                   "puts this symbol somewhere no wall runs, which is a finding about the drawing.";

                        withdrawn.Add(new JObject
                        {
                            ["source_row"] = row["source_row"],
                            ["kind"] = kind,
                            ["at_mm"] = new JArray(Math.Round(point.X * 304.8, 1), Math.Round(point.Y * 304.8, 1)),
                            ["reason"] = match.NoFaceCarriesThePoint
                                ? "no_wall_face_carries_this_point"
                                : "no_host_within_allowance",
                            ["walls_passed_over"] = match.WallsPassedOver,
                            ["nearest_wall_mm"] = Math.Round(bestMm, 1),
                            ["allowance_mm"] = Math.Round(allowance, 1),
                            ["means"] = "the drawing puts this symbol where no converted wall runs. It is NOT " +
                                        "planned and NOT built; the rows beside it are, and this one keeps its " +
                                        "own name so nothing else inherits its origin."
                        });
                        ((JArray)c["elements"]).Remove(row);
                        continue;
                    }

                    // IS THIS THE WALL THE DRAWING SHOWS IT AGAINST? Only when the rule
                    // said where its hosts are drawn; otherwise nothing is claimed.
                    if (hostLayers != null && hostLayers.Count > 0 && drawn != null)
                    {
                        var hostCurve = (best.Location as LocationCurve)?.Curve;
                        if (hostCurve != null)
                        {
                            var lines = drawn.Where(s => s != null && hostLayers.Any(g =>
                                CadGlob.IsMatch(s.Layer ?? "", g, set.CaseSensitiveLayers)));
                            XYZ h0 = hostCurve.GetEndPoint(0), h1 = hostCurve.GetEndPoint(1);
                            CadHostPlausibilityResult plausible = CadHostPlausibility.Check(
                                new CadPoint(point.X * 304.8, point.Y * 304.8),
                                new CadPoint(h0.X * 304.8, h0.Y * 304.8), new CadPoint(h1.X * 304.8, h1.Y * 304.8),
                                best.Width * 304.8 / 2.0, lines, set.AngleToleranceDegrees, set.PointToleranceMm,
                                set.HostSearchMm);
                            if (plausible.NearerWallDrawn)
                            {
                                string kind = row.Value<string>("kind");
                                CadSegment o = plausible.OtherWallLine;
                                withdrawn.Add(new JObject
                                {
                                    ["source_row"] = row["source_row"],
                                    ["kind"] = kind,
                                    ["at_mm"] = new JArray(Math.Round(point.X * 304.8, 1), Math.Round(point.Y * 304.8, 1)),
                                    ["reason"] = "nearer_drawn_wall_is_not_in_the_model",
                                    ["nearest_wall_mm"] = Math.Round(bestMm, 1),
                                    ["host_that_was_nearest"] = Rid.Value(best.Id),
                                    ["host_face_line_mm"] = Math.Round(plausible.HostFaceMm.Value, 1),
                                    ["other_wall_line_mm"] = Math.Round(plausible.OtherWallMm.Value, 1),
                                    ["other_wall_line"] = new JObject
                                    {
                                        ["layer"] = o.Layer,
                                        ["from_mm"] = new JArray(Math.Round(o.A.X, 1), Math.Round(o.A.Y, 1)),
                                        ["to_mm"] = new JArray(Math.Round(o.B.X, 1), Math.Round(o.B.Y, 1))
                                    },
                                    ["means"] = "the nearest converted wall is within the host search, but the drawing " +
                                                "shows this symbol against a DIFFERENT wall line, clearly nearer, that no " +
                                                "wall in the model stands on. Hosting it on the converted wall would " +
                                                "build it on the wrong wall. It is NOT planned; convert that wall first."
                                });
                                ((JArray)c["elements"]).Remove(row);
                                continue;
                            }
                        }
                    }

                    // WHICH FACE, decided now by the rule the placement applies
                    // (CadDeviceSide), so a symbol nobody can place on a side is
                    // withdrawn by name instead of refusing its whole stage.
                    string deviceKind = row.Value<string>("kind");
                    double? deadBand = row.Value<double?>("side_dead_band_mm");
                    var sideCurve = (best.Location as LocationCurve)?.Curve as Line;
                    if (deadBand.HasValue && deviceKind == "family_instance" && sideCurve != null)
                    {
                        XYZ s0 = sideCurve.GetEndPoint(0), s1 = sideCurve.GetEndPoint(1);
                        double lx = (s1.X - s0.X) * 304.8, ly = (s1.Y - s0.Y) * 304.8;
                        double ll = Math.Sqrt(lx * lx + ly * ly);
                        if (ll > 1e-6)
                        {
                            var normal = new CadVector(-ly / ll, lx / ll);
                            double offset = (point.X - s0.X) * 304.8 * normal.X + (point.Y - s0.Y) * 304.8 * normal.Y;
                            double? facingDegrees = row.Value<double?>("facing_degrees");
                            CadVector? facing = facingDegrees.HasValue
                                ? new CadVector(Math.Cos(facingDegrees.Value * Math.PI / 180.0),
                                                Math.Sin(facingDegrees.Value * Math.PI / 180.0))
                                : (CadVector?)null;
                            string sideFrom;
                            int side = CadDeviceSide.Expected(offset, best.Width * 304.8 / 2.0, facing, normal,
                                                              deadBand.Value, out sideFrom);
                            if (side == 0)
                            {
                                withdrawn.Add(new JObject
                                {
                                    ["source_row"] = row["source_row"],
                                    ["kind"] = deviceKind,
                                    ["at_mm"] = new JArray(Math.Round(point.X * 304.8, 1), Math.Round(point.Y * 304.8, 1)),
                                    ["reason"] = "side_of_the_wall_not_stated",
                                    ["host_that_was_nearest"] = Rid.Value(best.Id),
                                    ["offset_from_centreline_mm"] = Math.Round(offset, 1),
                                    ["half_width_mm"] = Math.Round(best.Width * 304.8 / 2.0, 1),
                                    ["dead_band_mm"] = deadBand.Value,
                                    ["facing_declared"] = facingDegrees.HasValue,
                                    ["means"] = "the symbol is drawn over the wall's own thickness, too close to its " +
                                                "centreline for that to say which face, and no facing declared for its " +
                                                "block points out of one. The rotation is not read as a side (measured: it " +
                                                "is not one). It is NOT planned; declare geometry.block_facing for the " +
                                                "block, or place it by hand."
                                });
                                ((JArray)c["elements"]).Remove(row);
                                continue;
                            }
                        }
                    }

                    row["host_id"] = Rid.Value(best.Id);
                    if (seen.Add("host:" + Rid.Value(best.Id)))
                        resolved.Add(Resolved("host_wall", best,
                            "the wall nearest the drawn symbol, " +
                            bestMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm away"));

                    // AND THE HEIGHTS ARE MEASURED FROM THAT WALL'S STOREY.
                    //
                    // sill_height_mm and head_height_mm are what a person means by
                    // them - heights above the floor - and they travelled to Revit
                    // as ABSOLUTE Z. On the ground storey the two agree and nothing
                    // shows; on any storey above it the hole was cut metres below
                    // where it was asked for, or outside the wall entirely, and the
                    // row still came back created and host_verified. The offset is
                    // applied HERE because this is the only place that knows both
                    // the numbers and the wall they belong to.
                    string zError = RaiseToStorey(doc, best, row);
                    if (zError != null) return zError;
                }
            return null;
        }

        /// <summary>
        /// THE FLOOR A HOLE IS CUT IN.
        ///
        /// A door finds its wall by being NEAR one. A hole does not: it belongs to
        /// the slab it is inside, and "the nearest floor" to a ring drawn over a
        /// courtyard is the floor around the courtyard - which would cut the hole
        /// in the wrong element and verify it happily.
        ///
        /// Everything here refuses rather than chooses. Nothing covers the ring:
        /// the floors have not been converted yet, or the drawing puts a hole where
        /// there is no slab, and both are findings. Several cover it: that is a
        /// building with storeys, and the rule's level is the only thing that can
        /// say which storey this drawing is.
        /// </summary>
        private static string ResolveSlabHost(Document doc, JObject row, JArray resolved,
                                              HashSet<string> seen, ref List<Element> slabs)
        {
            string wantLevel = row.Value<string>("host_level_name");
            row.Remove("host_level_name");

            XYZ centre = PlanPoint(row["center"]);
            if (centre == null)
                return "host_unresolvable: an opening was planned without a centre. NOTHING was planned.";

            if (slabs == null) slabs = CadHostResolver.Slabs(doc);
            CadSlabMatch hit = CadHostResolver.Containing(slabs, centre, wantLevel);

            if (hit.NoSlabsAtAll)
                return "host_not_found: this plan cuts openings, which Revit hosts IN a floor, roof or ceiling, " +
                       "and " + Quote(SafeTitle(doc)) + " contains none at all. NOTHING was planned. Convert the " +
                       "slab layers first and plan the openings against the model that results - a plan is " +
                       "computed before it is applied, so one run cannot build a floor and then cut it.";

            if (hit.Covering.Count == 0)
                return "host_not_found: no floor, roof or ceiling in " + Quote(SafeTitle(doc)) + " covers the " +
                       "opening drawn at (" + Mm(centre.X) + ", " + Mm(centre.Y) + ") mm. NOTHING was planned. " +
                       "The nearest slab is not an answer here: a hole belongs to the slab it is INSIDE, and " +
                       "cutting the one next to it would be a hole the drawing does not show. Either the slab " +
                       "layers have not been converted yet, or the drawing puts this opening where the building " +
                       "has no floor - which is a finding about the drawing.";

            // COVERED, AND NOT BY THE STOREY THAT WAS NAMED. Cutting the slab that
            // happens to be there would put the hole on a floor nobody asked for,
            // and a hole in the wrong floor is invisible in the plan it was drawn on.
            if (hit.CoveredButNotOnThatLevel)
                return "host_wrong_storey: " + hit.Covering.Count + " slab(s) cover the opening drawn at (" +
                       Mm(centre.X) + ", " + Mm(centre.Y) + ") mm and NONE of them is on " +
                       Quote(hit.DeclaredLevel) + ", which this rule names - they are on " +
                       string.Join(", ", hit.Covering.Take(6).Select(e => Quote(LevelOf(doc, e))).Distinct()) +
                       ". NOTHING was planned. Either that storey's slab has not been converted yet, or the " +
                       "rule names the wrong one; cutting the floor that happens to be under the ring would " +
                       "put the hole on a storey nobody asked for, where the plan it was drawn on would not " +
                       "show it.";

            if (hit.Slab == null)
                return "host_ambiguous: " + hit.Covering.Count + " slabs cover the opening drawn at (" +
                       Mm(centre.X) + ", " + Mm(centre.Y) + ") mm - " +
                       string.Join(", ", hit.Covering.Take(6).Select(e => Rid.Value(e.Id).ToString(CultureInfo.InvariantCulture) +
                                                                          " on " + Quote(LevelOf(doc, e)))) +
                       (hit.Covering.Count > 6 ? " and more" : "") + ". NOTHING was planned. A plan drawing looks " +
                       "the same on every storey it repeats on, so which floor this hole cuts is not something " +
                       "the drawing answers. " +
                       (string.IsNullOrWhiteSpace(hit.DeclaredLevel)
                           ? "Declare " + Quote("level") + " on the rule that produces these openings, or pass " +
                             "level_name to this call."
                           : "The rule already names " + Quote(hit.DeclaredLevel) + " and these are ALL on it - a " +
                             "floor and its ceiling, or a structural slab and the architectural one over it. A " +
                             "storey cannot separate them, so the drawing has to: put the openings that cut the " +
                             "floor on their own layer, with a rule of their own.");

            row["host_id"] = Rid.Value(hit.Slab.Id);
            if (seen.Add("slab:" + Rid.Value(hit.Slab.Id)))
                resolved.Add(Resolved("host_slab", hit.Slab,
                    "the floor, roof or ceiling the drawn opening falls inside" +
                    (hit.NarrowedByLevel ? ", chosen from " + hit.Covering.Count + " on that point by the rule's level" : "")));
            return null;
        }

        /// <summary>
        /// A usable plan view OF THIS STOREY, or null. Templates are excluded:
        /// a template is not a view anything can be drawn in.
        /// </summary>
        private static ViewPlan PlanOf(Document doc, Level level)
        {
            try
            {
                return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id)
                    .OrderBy(v => Rid.Value(v.Id))
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static string LevelOf(Document doc, Element e)
        {
            try { return SafeName(doc.GetElement(e.LevelId)); }
            catch { return null; }
        }

        /// <summary>
        /// Move a wall opening's two corners from heights-above-the-floor to the
        /// absolute Z Revit takes, or return the refusal when the storey cannot be
        /// read.
        ///
        /// A wall sits at its level's elevation plus its own base offset, and both
        /// are part of "how high is the floor here". A wall whose storey cannot be
        /// read is a refusal and not a zero: assuming the ground storey would put
        /// the hole metres from where it was asked for, on a row that verifies.
        /// </summary>
        private static string RaiseToStorey(Document doc, Wall host, JObject row)
        {
            if (row["corner_1"] == null || row["corner_2"] == null) return null;   // not a wall opening

            Level storey = null;
            try { storey = doc.GetElement(host.LevelId) as Level; } catch { }
            if (storey == null)
                return "host_storey_unreadable: wall " + Rid.Value(host.Id) + " does not report the storey it " +
                       "stands on, and the sill and head heights on this rule are measured FROM that storey. " +
                       "NOTHING was planned. Treating them as absolute would cut the hole wherever that wall " +
                       "happens to be, on a row that verifies.";

            double baseMm = CadUnits.FeetToMm(storey.Elevation);
            try
            {
                Parameter offset = host.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                if (offset != null && offset.StorageType == StorageType.Double)
                    baseMm += CadUnits.FeetToMm(offset.AsDouble());
            }
            catch { }

            foreach (string key in new[] { "corner_1", "corner_2" })
            {
                var corner = row[key] as JArray;
                if (corner == null || corner.Count < 3) continue;
                corner[2] = Math.Round((double)corner[2] + baseMm, 4);
            }
            row["heights_measured_from"] = new JObject
            {
                ["level"] = SafeName(storey),
                ["level_elevation_mm"] = Math.Round(CadUnits.FeetToMm(storey.Elevation), 4),
                ["wall_base_offset_included"] = true,
                ["means"] = "sill_height_mm and head_height_mm are heights above the floor, which is what a " +
                            "person means by them. Revit takes an absolute Z, so the storey this wall stands " +
                            "on was added here."
            };
            return null;
        }

        private static XYZ PlanPoint(JToken token)
        {
            var a = token as JArray;
            if (a == null || a.Count < 3) return null;
            // The plan speaks millimetres; Revit's own geometry is decimal feet.
            return new XYZ((double)a[0] / 304.8, (double)a[1] / 304.8, (double)a[2] / 304.8);
        }

        private static string Mm(double feet)
        {
            return (feet * 304.8).ToString("0.#", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A SHAFT'S TWO LEVELS, and a SEPARATOR'S VIEW.
        ///
        /// A shaft cuts every floor between two storeys, so it needs both named -
        /// and a shaft that stopped at the wrong storey would look entirely
        /// correct in plan, which is why nothing here is defaulted. A room
        /// separator needs a view because Revit takes room boundary lines through
        /// one, and it must be a plan OF THE STOREY the separator sits on.
        ///
        /// This used the ACTIVE view when the plan named none. MEASURED, and the
        /// reason this comment is long: handing NewRoomBoundaryLines a view whose
        /// storey is not the sketch plane's TOOK REVIT DOWN - not an exception,
        /// not a refusal, the process went away mid-transaction and the bridge
        /// reported a closed pipe. Whatever happens to be on screen when a plan is
        /// applied is not an input anybody chose, and here it is not merely wrong.
        /// </summary>
        private static string ResolveShaftsAndViews(Document doc, List<JObject> creates, JArray resolved)
        {
            List<Level> levels = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (JObject c in creates)
                foreach (JObject row in ((JArray)c["elements"]).OfType<JObject>())
                {
                    string kind = row.Value<string>("kind");

                    if (kind == "shaft")
                    {
                        if (levels == null)
                            levels = new FilteredElementCollector(doc).OfClass(typeof(Level))
                                .Cast<Level>().ToList();

                        foreach (string field in new[] { "base_level_name", "top_level_name" })
                        {
                            string want = row.Value<string>(field);
                            row.Remove(field);
                            if (string.IsNullOrWhiteSpace(want))
                                return "shaft_level_unstated: a shaft runs BETWEEN two levels and the rule " +
                                       "named " + (field == "base_level_name" ? "no base" : "no top") +
                                       " level. NOTHING was planned. A drawing shows one ring and says nothing " +
                                       "about height, so both are the requirement set's statement.";

                            Level level = levels.FirstOrDefault(l =>
                                              string.Equals(SafeName(l), want, StringComparison.Ordinal))
                                       ?? levels.FirstOrDefault(l =>
                                              string.Equals(SafeName(l), want, StringComparison.OrdinalIgnoreCase));
                            if (level == null)
                                return "level_not_found: no level in " + Quote(SafeTitle(doc)) + " is named " +
                                       Quote(want) + ", which a shaft rule asked for. NOTHING was planned. The " +
                                       "levels there are: " + Names(levels) + ".";

                            row[field == "base_level_name" ? "base_level_id" : "top_level_id"] =
                                Rid.Value(level.Id);
                            if (seen.Add("level:" + Rid.Value(level.Id)))
                                resolved.Add(Resolved("level", level, want));
                        }

                        long baseId = row.Value<long?>("base_level_id") ?? -1;
                        long topId = row.Value<long?>("top_level_id") ?? -1;
                        Level bottom = levels.FirstOrDefault(l => Rid.Value(l.Id) == baseId);
                        Level top = levels.FirstOrDefault(l => Rid.Value(l.Id) == topId);
                        if (bottom != null && top != null && top.Elevation <= bottom.Elevation)
                            return "shaft_inverted: top level " + Quote(SafeName(top)) + " sits at or below " +
                                   "base level " + Quote(SafeName(bottom)) + ". A shaft runs upward, and one " +
                                   "that does not cuts nothing. NOTHING was planned.";
                    }

                    if (kind == "room_separator" && row["view_id"] == null)
                    {
                        long? levelId = row.Value<long?>("level_id");
                        Level on = levelId.HasValue ? doc.GetElement(Rid.Make(levelId.Value)) as Level : null;
                        if (on == null)
                            return "separator_view_unresolved: a room separator is drawn on a storey and this " +
                                   "row has none, so no plan of it can be found. NOTHING was planned.";

                        ViewPlan drawnIn = PlanOf(doc, on);
                        if (drawnIn == null)
                            return "separator_view_not_found: Revit takes room boundary lines THROUGH a view, " +
                                   "and it has to be a plan of the storey the separator sits on - " +
                                   Quote(SafeName(on)) + " has none in " + Quote(SafeTitle(doc)) + ". NOTHING " +
                                   "was planned. Create a floor plan of that storey and run this again. The view " +
                                   "that happens to be on screen is NOT a substitute: a view whose storey is not " +
                                   "the separator's takes Revit down rather than refusing, which is measured and " +
                                   "is why nothing here falls back to it.";

                        row["view_id"] = Rid.Value(drawnIn.Id);
                        if (seen.Add("view:" + Rid.Value(drawnIn.Id)))
                            resolved.Add(Resolved("separator_view", drawnIn,
                                                  "a plan of " + Quote(SafeName(on)) + ", the storey this " +
                                                  "separator is drawn on"));
                    }
                }
            return null;
        }

        /// <summary>
        /// A RESOLVED ENTRY, and the one place that decides what it says.
        ///
        /// horizun_apply_cad_plan re-reads each id and compares Element.Name to
        /// the "name" recorded here; anything else is drift and NOTHING is
        /// written. So "name" must be exactly what the apply will read - not a
        /// prettier version of it.
        ///
        /// MEASURED: the type entry recorded "HZ_DOOR: HZ_DOOR", the family and
        /// the type joined for a human, while the apply read "HZ_DOOR". Same id,
        /// same element, nothing moved, and every plan that resolved a family
        /// type refused itself as stale. Levels never showed it because their
        /// entry happened to record the plain name. The label a person wants is
        /// still here - beside the name, not instead of it.
        /// </summary>
        private static JObject Resolved(string what, Element element, string askedFor, string label = null)
        {
            var o = new JObject
            {
                ["what"] = what,
                ["id"] = Rid.Value(element.Id),
                ["name"] = SafeName(element),
                ["asked_for"] = askedFor
            };
            if (label != null && label != SafeName(element)) o["label"] = label;
            return o;
        }

        /// <summary>
        /// What the document already calls things of the categories this set
        /// produces. Only gathered for the categories that carry a unique name -
        /// asking for every element's name in a large model would cost more than
        /// the plan and answer a question nobody asked.
        /// </summary>
        private static List<string> ExistingNames(Document doc, CadRequirementSet set)
        {
            var names = new List<string>();
            if (doc == null || set == null) return names;

            bool wantsGrids = set.Rules.Any(r => r?.Naming != null && r.Produces == "grid");
            bool wantsLevels = set.Rules.Any(r => r?.Naming != null && r.Produces == "level");
            if (!wantsGrids && !wantsLevels) return names;

            try
            {
                if (wantsGrids)
                    foreach (Grid g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
                        { try { names.Add(g.Name); } catch { } }
                if (wantsLevels)
                    foreach (Level l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                        { try { names.Add(l.Name); } catch { } }
            }
            catch { }
            return names;
        }

        /// <summary>Which kinds Revit hosts on a level. Grids and levels host nothing.</summary>
        private static bool NeedsLevel(string kind)
        {
            switch (kind)
            {
                case "wall": case "floor": case "ceiling": case "roof": case "room":
                case "family_instance": case "structural_column": case "structural_framing":
                case "duct": case "pipe": case "conduit": case "cable_tray":
                // A room separator is drawn ON a level even though Revit takes it
                // through a view: the sketch plane sits at the level's elevation,
                // and a separator on the wrong storey bounds a room nobody meant.
                case "room_separator":
                    return true;
                // A SHAFT NEEDS TWO, not one, and they are resolved on their own
                // path. Asking for a single level_id here would give it a third.
                case "shaft":
                    return false;
                default: return false;
            }
        }

        /// <summary>What the model says about a type name, for the catalogue check.</summary>
        private static CadTypeFacts TypeFactsOf(Document doc, string name)
        {
            ElementType et = FindType(doc, name);
            if (et == null) return new CadTypeFacts { Found = false, SameFamily = SameFamily(doc, name) };
            var facts = new CadTypeFacts { Found = true };
            try
            {
                if (et.Category != null)
                    facts.Category = ((BuiltInCategory)(int)Rid.Value(et.Category.Id)).ToString();
            }
            catch { }
            if (et is FamilySymbol fs)
            {
                try { facts.PlacementType = fs.Family.FamilyPlacementType.ToString(); } catch { }
            }
            if (et is WallType wt)
            {
                facts.IsWallType = true;
                try { facts.WidthMm = wt.Width * 304.8; } catch { }
            }
            return facts;
        }

        private static ElementType FindType(Document doc, string name)
        {
            List<ElementType> types = new FilteredElementCollector(doc).WhereElementIsElementType()
                .Cast<ElementType>().ToList();
            // the localized label first; then the language-independent one of a system wall family (TypeNames)
            return types.FirstOrDefault(t => string.Equals(TypeLabel(t), name, StringComparison.Ordinal))
                ?? types.FirstOrDefault(t => string.Equals(TypeNames.Canonical(t), name, StringComparison.Ordinal))
                ?? types.FirstOrDefault(t => string.Equals(SafeName(t), name, StringComparison.Ordinal))
                ?? types.FirstOrDefault(t => string.Equals(TypeLabel(t), name, StringComparison.OrdinalIgnoreCase))
                ?? types.FirstOrDefault(t => string.Equals(SafeName(t), name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The loaded types of the class a rule's kind is built from, as labels, sorted; at most 25.</summary>
        private static List<string> TypesOfKind(Document doc, string produces)
        {
            Type cls;
            switch (produces)
            {
                case "duct": cls = typeof(Autodesk.Revit.DB.Mechanical.DuctType); break;
                case "pipe": cls = typeof(Autodesk.Revit.DB.Plumbing.PipeType); break;
                case "conduit": cls = typeof(Autodesk.Revit.DB.Electrical.ConduitType); break;
                case "cable_tray": cls = typeof(Autodesk.Revit.DB.Electrical.CableTrayType); break;
                case "wall": cls = typeof(WallType); break;
                default: return new List<string>();
            }
            try
            {
                return new FilteredElementCollector(doc).OfClass(cls).Cast<ElementType>().Select(TypeLabel)
                    .Where(x => x != null).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
                    .Take(25).ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>The loaded types of the family a "Family: Type" name names, as labels, sorted; at most 25.</summary>
        private static List<string> SameFamily(Document doc, string name)
        {
            string family = CadCatalogCheck.FamilyOf(name);
            if (family == null) return new List<string>();
            return new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>()
                .Where(t => { try { return string.Equals(t.FamilyName, family, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
                .Select(TypeLabel).Where(x => x != null).Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).Take(25).ToList();
        }

        private static string TypeLabel(ElementType t)
        {
            try { return string.IsNullOrEmpty(t.FamilyName) ? SafeName(t) : t.FamilyName + ": " + SafeName(t); }
            catch { return SafeName(t); }
        }

        private static string SafeName(Element e) { try { return e.Name; } catch { return null; } }

        private static string Quote(string s) { return "'" + (s ?? "(none)") + "'"; }

        private static string Names(List<Level> levels)
        {
            if (levels.Count == 0) return "(this document has none)";
            List<string> named = levels.OrderBy(l => { try { return l.Elevation; } catch { return 0.0; } })
                                       .Take(24)
                                       .Select(l => Quote(SafeName(l)) + " (id " + Rid.Value(l.Id) + ")").ToList();
            return string.Join(", ", named) + (levels.Count > named.Count
                ? " and " + (levels.Count - named.Count) + " more" : "");
        }

        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafeVersion(UIApplication a)
        { try { return a?.Application?.VersionNumber + "." + a?.Application?.VersionBuild; } catch { return null; } }
    }
}
