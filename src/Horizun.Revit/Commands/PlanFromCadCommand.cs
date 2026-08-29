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
            CadInterpretation interpretation = CadInterpretationRules.Interpret(
                harvest.Segments, set, sourceHash, harvest.Arcs, ExistingNames(doc, set));

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

            bool includeIneligible = request.Value<bool?>("include_candidates_needing_review") ?? false;
            string sourceFingerprint = CadFacts.SourceFingerprint(facts);
            CadConversionPlan plan = CadConversionPlanRules.Plan(interpretation, set, sourceFingerprint, includeIneligible);

            JObject report = CadConversionPlanRules.ToJson(plan, set);
            report["mode"] = "plan";
            report["document"] = SafeTitle(doc);
            report["instance_id"] = instanceId;
            report["instance_name"] = facts.Name;
            report["source"] = new JObject
            {
                ["fingerprint"] = sourceFingerprint,
                ["file_sha256"] = facts.FileSha256,
                ["external_path"] = facts.ExternalPath,
                ["linked_file_status"] = facts.LinkedFileStatus,
                ["declared_units"] = declared,
                ["transform_fingerprint"] = facts.TransformFingerprint,
                ["units_agree_with_requirement_set"] = unitsAgree,
                ["unit_mismatch_accepted"] = !unitsAgree
            };
            report["harvest_coverage"] = harvest.CoverageJson(set.ArcSagittaMm);

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
            report["layer_map"] = new JObject(interpretation.LayerMap
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new JProperty(kv.Key, new JArray(kv.Value))));
            report["unclaimed"] = new JArray(interpretation.Unclaimed.Select(u => new JObject
            {
                ["layer"] = u.Layer,
                ["reason"] = u.Reason,
                ["entity_count"] = u.EntityCount,
                ["rules_that_looked"] = new JArray(u.RuleIds)
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
            string unresolved = ResolveNames(doc, creates, request, resolved);
            if (unresolved != null) return CommandResult.Fail(unresolved);

            // THE TWO LEVELS A SHAFT RUNS BETWEEN, and the view a room separator
            // belongs to. Both are resolved here for the same reason the level and
            // the type are: a drawing carries neither, and an id is something only
            // the open document can supply.
            unresolved = ResolveShaftsAndViews(doc, creates, resolved);
            if (unresolved != null) return CommandResult.Fail(unresolved);

            // The HOST, after the level and the type: a door needs a wall, and
            // the drawing has no ids to name one with.
            unresolved = ResolveHosts(doc, creates, resolved, set.PointToleranceMm);
            if (unresolved != null) return CommandResult.Fail(unresolved);

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
            var candidateIndex = new JArray();
            int cursor = 0;
            foreach (JObject c in creates)
            {
                int count = ((JArray)c["elements"]).Count;
                var rows = new JArray();
                for (int k = 0; k < count && cursor + k < plan.Actions.Count; k++)
                {
                    CadPlannedAction a = plan.Actions[cursor + k];
                    rows.Add(new JObject
                    {
                        ["element_index"] = k,
                        ["candidate_id"] = a.CandidateId,
                        ["geometry_id"] = a.GeometryId,
                        ["semantic_id"] = a.SemanticId,
                        ["rule_id"] = a.RuleId,
                        ["layer"] = a.Layer,
                        ["confidence"] = Math.Round(a.Confidence, 4)
                    });
                }
                cursor += count;
                candidateIndex.Add(new JObject
                {
                    ["key"] = "cad-stage-" + (int)c["stage"] + "-batch-" + (int)c["batch_of_stage"],
                    ["candidates"] = rows
                });
            }
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
                               Quote(typeName) + ", which the rule producing these " + kind + "s asked for. " +
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
                                           double pointToleranceMm)
        {
            List<Wall> walls = null;
            List<Element> slabs = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (JObject c in creates)
                foreach (JObject row in ((JArray)c["elements"]).OfType<JObject>())
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

                    if (best == null)
                        return "host_too_far: the nearest wall to a hosted element at (" +
                               Mm(point.X) + ", " + Mm(point.Y) + ") mm is " +
                               bestMm.ToString("0.#", CultureInfo.InvariantCulture) + " mm away, and " +
                               allowance.ToString("0.#", CultureInfo.InvariantCulture) + " mm is the most this " +
                               "set allows. NOTHING was planned. Either the wall layers have not been converted " +
                               "yet - convert them first, then plan the openings - or the drawing puts this " +
                               "symbol somewhere no wall runs, which is a finding about the drawing.";

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

        private static ElementType FindType(Document doc, string name)
        {
            List<ElementType> types = new FilteredElementCollector(doc).WhereElementIsElementType()
                .Cast<ElementType>().ToList();
            return types.FirstOrDefault(t => string.Equals(TypeLabel(t), name, StringComparison.Ordinal))
                ?? types.FirstOrDefault(t => string.Equals(SafeName(t), name, StringComparison.Ordinal))
                ?? types.FirstOrDefault(t => string.Equals(TypeLabel(t), name, StringComparison.OrdinalIgnoreCase))
                ?? types.FirstOrDefault(t => string.Equals(SafeName(t), name, StringComparison.OrdinalIgnoreCase));
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
