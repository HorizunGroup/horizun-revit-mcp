// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// horizun_plan_cad_update - revision A is in the model, revision B is on screen.
//
// READ-ONLY. It emits actions somebody else executes, exactly like
// horizun_plan_from_cad, and for the same reason: the decision to overwrite what
// is already in a model is not one a planner should be able to take by itself.
//
// The actions it emits are ready calls to commands that already rehearse,
// confirm and re-read their own work: horizun_create_elements for what revision B
// adds, and horizun_transform_elements set_curve for a moved wall whose pairing a
// PERSON has accepted.
//
// That last clause is the whole design. Nothing in a DWG says the wall in
// revision B is the wall from revision A - there is no handle anywhere in the
// Revit CAD API, measured - so a line that moved arrives as a create and an
// orphan. The resemblance between them is OFFERED, with what it was judged on,
// and acted on only when the caller sends it back. What a person moved in the
// model is never in the action list at all.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed class PlanCadUpdateCommand : ICommand
    {
        public string Name => "horizun_plan_cad_update";

        public string Description =>
            "Plan an incremental update from a NEW revision of a drawing already converted once. Read-only.";

        public CommandResult Execute(UIApplication uiApp, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (Exception ex) { return CommandResult.Fail("The arguments are not valid JSON: " + ex.Message); }

            Document doc = uiApp?.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No active Revit document.");

            string wantDoc = request.Value<string>("target_document");
            string title = SafeTitle(doc);
            if (!string.IsNullOrWhiteSpace(wantDoc) && !string.Equals(wantDoc, title, StringComparison.Ordinal))
                return CommandResult.Fail(
                    "target_document is '" + wantDoc + "' and the active document is '" + title + "'. An update " +
                    "planned against one model and applied to another would rewrite the wrong building. Nothing " +
                    "was read.");

            long instanceId = request.Value<long?>("instance_id") ?? -1;
            if (instanceId < 0 || !Rid.CanRepresent(instanceId))
                return CommandResult.Fail(
                    "instance_id is required: which CAD instance is the NEW revision? List them with " +
                    "horizun_query_cad mode='instances'.");

            Element element = doc.GetElement(Rid.Make(instanceId));
            if (!(element is ImportInstance))
                return CommandResult.Fail("Element " + instanceId + " is not an ImportInstance.");

            JObject setJson = request["requirement_set"] as JObject;
            if (setJson == null)
                return CommandResult.Fail(
                    "requirement_set is required, and it must be the SAME set the model was built under - the " +
                    "provenance records its hash, and an update planned under different rules is a second " +
                    "conversion wearing an update's clothes.");

            CadRequirementSet set;
            try { set = CadRequirementSet.Load(setJson); }
            catch (Exception ex) { return CommandResult.Fail(ex.Message); }

            List<JObject> unreadable;
            List<CadInstanceFacts> allFacts = CadFacts.Collect(doc, out unreadable);
            CadInstanceFacts facts = allFacts.FirstOrDefault(f => f.ElementId == instanceId);
            if (facts == null)
                return CommandResult.Fail("CAD instance " + instanceId + " could not be measured.");

            // THIS PLACEMENT, and every other one in the model. Scope is decided
            // per placement now: a file linked twice is two placements with one
            // hash, and the second one's elements are not this run's to touch.
            CadPlacement placement = CadFacts.Placement(facts);
            CadSourceIdentity identity = CadPlacementRules.Identity(placement);
            List<CadPlacement> placementsInModel = allFacts.Select(CadFacts.Placement).Where(p => p != null).ToList();

            double? declaredToMm = CadUnits.MillimetresPer(facts.DeclaredUnits);
            if (!declaredToMm.HasValue || Math.Abs(declaredToMm.Value - set.SourceUnitsToMm) > 1e-9)
                return CommandResult.Fail(
                    "unit_mismatch: the link declares '" + (facts.DeclaredUnits ?? "(nothing)") + "' and the set " +
                    "declares '" + set.SourceUnits + "'. Read at the wrong scale every element would look moved " +
                    "and this plan would propose to move all of them. Nothing was read.");

            string sourceFingerprint = CadFacts.SourceFingerprint(facts);
            string sourceHash = facts.FileSha256 ?? sourceFingerprint ?? "(no-source-identity)";

            CadHarvest harvest = CadGeometryHarvest.Harvest(doc, element, set.ArcSagittaMm,
                Math.Max(1, Math.Min(500000, request.Value<int?>("max_primitives") ?? 200000)));
            if (harvest.GeometryUnreadable)
                return CommandResult.Fail(
                    "geometry_unreadable: Revit returned no geometry for CAD instance " + instanceId +
                    ". An update planned from a drawing that could not be read would propose deleting the whole " +
                    "conversion. Nothing was read.");
            if (harvest.Truncated)
                return CommandResult.Fail(
                    "reading_is_partial: the geometry walk stopped at its bound, so part of revision B was never " +
                    "read - and everything past the bound would be planned as an orphan. Raise max_primitives. " +
                    "Nothing was read.");

            JObject blocksReadForReply = null;
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
                harvest.Segments, set, sourceHash, harvest.Arcs, null, solidHatch);

            // EACH DUCT RUN'S SECTION, read exactly as the first conversion read it. A revision
            // that changes a label - 8x6 grown to 10x6 - leaves the line where it was, and only
            // this reading can see that the run now asks for another size.
            string sectionsFailure;
            JObject sectionsReadForReply = CadSectionsHook.Apply(element, facts, set, harvest, request, interpretation,
                                                                 new JArray(), true, out sectionsFailure);
            if (sectionsFailure != null) return CommandResult.Fail(sectionsFailure);

            // THE SAME READING THE PLAN AND THE AUDIT MAKE, INCLUDING SYMBOLS.
            //
            // An update route that cannot see the drawing's named symbols reports
            // every device built from one as deleted, and then proposes to delete
            // it. Revit's import cannot see a block name, so the file is read - by
            // the same shared reader both other commands use.
            if (set.Rules.Any(r => r.Geometry != null && r.Geometry.Source == CadGeometrySource.Blocks))
            {
                JObject blocksRead = CadBlockSource.Read(element, facts, set, harvest, sourceHash, interpretation,
                                                        request.Value<string>("dwg_path"),
                                                        Math.Max(30, Math.Min(3600,
                                                            request.Value<int?>("dwg_read_timeout_seconds") ?? 900)));
                if (blocksRead?["refused"] != null)
                    return CommandResult.Fail(
                        "symbols_unreadable: this requirement set has blocks rules and the drawing's symbols " +
                        "could not be read (" + blocksRead["refused"] + "). An update computed without them " +
                        "would propose deleting every device built from a symbol. Nothing was planned. " +
                        blocksRead.ToString(Newtonsoft.Json.Formatting.None));
                blocksReadForReply = blocksRead;
            }

            var problems = new List<string>();
            List<CadAuditSubject> subjects = Stamped(doc, problems, WantedParameters(set));
            if (subjects.Count == 0)
                return CommandResult.Fail(
                    "nothing_to_update: no element in '" + title + "' carries Horizun CAD provenance, so there " +
                    "is no revision A to update FROM. This is a first conversion - use horizun_plan_from_cad, " +
                    "which is the command that says so and gets reviewed as such.");

            Dictionary<long, string> accepted;
            string pairingError = Pairings(request, out accepted);
            if (pairingError != null) return CommandResult.Fail(pairingError);
            List<CadDecision> decisions;
            string decisionError = Decisions(request, out decisions);
            if (decisionError != null) return CommandResult.Fail(decisionError);

            // WHICH CONVERSION THIS DRAWING SUPERSEDES.
            //
            // A new revision is a DIFFERENT FILE, so the current file's hash
            // cannot decide which elements belong to this conversion - and if it
            // did, the plan would report the entire existing model as untouched
            // and revision B as new work. The caller says what this supersedes;
            // nothing in a DWG says one file is a re-issue of another.
            var lineage = new List<string>();
            foreach (JToken token in request["supersedes_sha256"] as JArray ?? new JArray())
            {
                string sha = token?.ToString();
                if (!string.IsNullOrWhiteSpace(sha)) lineage.Add(sha.Trim().ToLowerInvariant());
            }

            // ...OR WHICH PLACEMENT. A file placed twice cannot be named by its
            // hash, so a caller may name the placement itself - the ImportInstance
            // UniqueId the earlier plan's reply reported as placement.id.
            var lineagePlacements = new List<string>();
            foreach (JToken token in request["supersedes_placement_ids"] as JArray ?? new JArray())
            {
                string id = token?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) lineagePlacements.Add(id.Trim());
            }

            // ...AND WHICH EARLIER VERSION OF THESE RULES. A rules change is not a new
            // conversion, but nothing in a requirement set says it replaces another:
            // the caller says so, and only for the same set id.
            var rulesLineage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken token in request["supersedes_requirement_set_sha256"] as JArray ?? new JArray())
            {
                string sha = token?.ToString();
                if (!string.IsNullOrWhiteSpace(sha)) rulesLineage.Add(sha.Trim());
            }
            rulesLineage.Remove(set.Sha256 ?? "");
            var otherSetIds = subjects
                .Where(s => rulesLineage.Contains(s.Provenance.RequirementSetSha256 ?? "") &&
                            !string.Equals(s.Provenance.RequirementSetId, set.Id, StringComparison.Ordinal))
                .Select(s => s.Provenance.RequirementSetId ?? "(unrecorded)")
                .Distinct(StringComparer.Ordinal).ToList();
            if (otherSetIds.Count > 0)
                return CommandResult.Fail(
                    "rules_lineage_other_set: supersedes_requirement_set_sha256 names rules recorded as set '" +
                    string.Join("', '", otherSetIds) + "', and this plan is for set '" + set.Id + "'. A version " +
                    "can supersede an earlier version of the SAME set; claiming another set's elements would " +
                    "re-plan somebody else's conversion. Nothing was planned.");

            var minesUnderThisSet = subjects
                .Where(s => set.Sha256 == null || string.IsNullOrEmpty(s.Provenance.RequirementSetSha256) ||
                            string.Equals(s.Provenance.RequirementSetSha256, set.Sha256, StringComparison.Ordinal) ||
                            rulesLineage.Contains(s.Provenance.RequirementSetSha256))
                .ToList();
            int underEarlierRules = minesUnderThisSet.Count(s =>
                !string.IsNullOrEmpty(s.Provenance.RequirementSetSha256) &&
                !string.Equals(s.Provenance.RequirementSetSha256, set.Sha256, StringComparison.Ordinal));
            var shas = minesUnderThisSet
                .Select(s => s.Provenance.SourceFileSha256)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.Ordinal).ToList();

            // WHICH ELEMENTS THIS PLACEMENT MAY CLAIM, decided once, at a desk.
            CadUpdateScope scope = CadPlacementRules.Resolve(minesUnderThisSet, placement, lineage,
                                                             lineagePlacements, placementsInModel);
            foreach (string sha in rulesLineage) scope.RulesLineage.Add(sha);

            if (scope.AmbiguousLineageElements.Count > 0)
                return CommandResult.FailWithDetail(
                    "supersedes_ambiguous: " + scope.AmbiguousLineageElements[0].Says + " " +
                    scope.AmbiguousLineageElements.Count + " element(s) are in this position. Nothing was planned.",
                    new JObject { ["scope"] = scope.ToJson() });

            // AMBIGUOUS v1, REFUSED HERE - before the claimable count, before the
            // transform comparison, before a single action is derived.
            //
            // This had no refusal at all. An ambiguous v1 element is out of
            // scope, so the plan simply went on without it whenever anything else
            // was claimable - and the drawing entity that built it then matched
            // nothing in scope and came back as a `create`. Applying that builds a
            // second wall on top of the one standing. Only the all-ambiguous case
            // refused, and it refused as scope_unidentified, whose advice is to
            // run horizun_plan_from_cad - which against this model builds the
            // whole drawing again. Both outcomes are the harm the ambiguity guard
            // exists to prevent, so the guard now fires on the ambiguity itself.
            if (scope.AmbiguousV1.Count > 0)
                return CommandResult.FailWithDetail(
                    CadPlacementRules.AmbiguousV1Refusal(scope, title),
                    new JObject
                    {
                        ["scope"] = scope.ToJson(),
                        ["source"] = identity.ToJson(),
                        ["ambiguous_v1"] = new JArray(scope.AmbiguousV1.Select(x => x.ToJson()).Take(100))
                    });

            // THE GUARD THE BACKLOG NAMED (8.4c). It used to fire on "other
            // hashes exist" and stay silent when this run could claim nothing
            // for a structural reason - an embedded import, a moved file - so
            // the plan reported zero of everything about a conversion it had not
            // looked at. It now fires on "this run can claim nothing", and says
            // what it looked for and what is there.
            if (scope.ClaimableCount == 0)
            {
                bool nothingStated = lineage.Count == 0 && lineagePlacements.Count == 0;
                bool otherFilesExist = shas.Any(x => !string.Equals(x, facts.FileSha256, StringComparison.Ordinal));
                // The lineage-by-hash wording only makes sense for a source that
                // HAS a hash. An embedded import or a missing file cannot be
                // named that way, and telling its caller to pass supersedes_sha256
                // would send them looking for a number that does not exist.
                bool sourceHasHash = identity.Mode == CadPlacementRules.IdentityFileHash;
                if (nothingStated && otherFilesExist && sourceHasHash)
                    return CommandResult.FailWithDetail(
                        "supersedes_unstated: nothing in '" + title + "' was built from THIS placement, and " +
                        minesUnderThisSet.Count + " element(s) were built under these rules from " + shas.Count +
                        " drawing(s): " + string.Join(", ", shas.Take(8)) +
                        (shas.Count > 8 ? " and more" : "") + ". Planning anyway would report your whole existing " +
                        "conversion as untouched and this drawing as entirely new work - it would build a second " +
                        "copy of the building. Say which one this supersedes in supersedes_sha256 (a file placed " +
                        "once) or supersedes_placement_ids (a placement). Nothing in a DWG says that one file " +
                        "is a re-issue of another, so it is a statement you make, not one this bridge can find.",
                        new JObject { ["scope"] = scope.ToJson(), ["source"] = identity.ToJson() });
                return CommandResult.FailWithDetail(
                    CadPlacementRules.UnidentifiedRefusal(scope, title) +
                    " Source identity of this placement: " + identity.Mode + ".",
                    new JObject { ["scope"] = scope.ToJson(), ["source"] = identity.ToJson() });
            }

            // HAS THIS PLACEMENT MOVED since its elements were built? Compared
            // on the v2 records it claims - a v1 record has no transform to
            // compare and is reported as such rather than as "not moved".
            CadPlacementMove move = null;
            var movedRecords = new List<long>();
            int transformUnknown = 0;
            foreach (CadAuditSubject s in minesUnderThisSet.Where(x => scope.Claimed.Contains(x.ElementId)))
            {
                CadPlacementMove m = CadPlacementRules.CompareTransforms(s.Provenance, placement);
                if (m.DeltaUnknownBecause != null && !m.Moved) { transformUnknown++; continue; }
                if (!m.Moved) continue;
                movedRecords.Add(s.ElementId);
                if (move == null) move = m;
            }
            bool acceptMove = request.Value<bool?>("accept_placement_move") ?? false;
            if (move != null && !acceptMove)
                return CommandResult.FailWithDetail(
                    "placement_moved: CAD instance " + instanceId + " does not sit where it sat when " +
                    movedRecords.Count + " of its element(s) were built - recorded transform " +
                    move.RecordedFingerprint + ", now " + move.CurrentFingerprint +
                    (move.DeltaMm != null
                        ? ", a shift of " + string.Join(", ", move.DeltaMm.Select(v => v.ToString("0.#", CultureInfo.InvariantCulture))) +
                          " mm and " + move.RotationDegrees.ToString("0.##", CultureInfo.InvariantCulture) + " degrees"
                        : ", by an amount that could not be decoded: " + move.DeltaUnknownBecause) +
                    ". Every semantic id is derived from model coordinates, so planned as-is this update would " +
                    "read the whole drawing as deleted and redrawn. Nothing was planned. If the placement was " +
                    "moved ON PURPOSE, send accept_placement_move=true and the plan is re-derived under the new " +
                    "transform: elements still on their built line follow the drawing, elements a person also " +
                    "moved are reported as conflict. If it was moved by accident, move it back.",
                    new JObject
                    {
                        ["placement_moved"] = move.ToJson(),
                        ["elements_built_under_the_old_transform"] = new JArray(movedRecords.Take(200)),
                        ["scope"] = scope.ToJson()
                    });
            if (move != null && acceptMove && (move.From == null || move.To == null))
                return CommandResult.FailWithDetail(
                    "placement_moved_undecodable: the placement moved and " + move.DeltaUnknownBecause +
                    ", so the plan cannot be re-derived under the new transform. Nothing was planned.",
                    new JObject { ["placement_moved"] = move.ToJson() });

            var rejectedPairings = new List<string>();
            foreach (JToken token in request["reject_pairings"] as JArray ?? new JArray())
            {
                string candidate = token?.ToString();
                if (!string.IsNullOrWhiteSpace(candidate)) rejectedPairings.Add(candidate.Trim());
            }

            // WHICH WALL EACH HOSTED CANDIDATE NOW FALLS IN.
            //
            // Resolved here because it needs the open document, and through the
            // same rule the first conversion uses - two different answers to
            // "which wall is this in" would report a rehosting on every run,
            // against a model nobody had touched.
            var hostByCandidate = new Dictionary<string, long>(StringComparer.Ordinal);
            try
            {
                List<Wall> walls = null;
                foreach (CadCandidate hosted in interpretation.Candidates)
                {
                    if (hosted == null || string.IsNullOrEmpty(hosted.SemanticId)) continue;
                    if (hosted.Geometry == null || hosted.Geometry.Count == 0) continue;
                    if (!NeedsWallHost(hosted.ProposedKind) &&
                        !string.Equals(hosted.HostedOn, "wall", StringComparison.OrdinalIgnoreCase)) continue;
                    if (walls == null) walls = CadHostResolver.Walls(doc);
                    CadPoint at = hosted.Geometry[0];
                    XYZ atFeet = CadHostResolver.PointFromMm(at.X, at.Y, at.Z);
                    CadHostMatch match = CadHostResolver.Nearest(walls, atFeet, set.HostSearchMm);
                    if (match.Wall != null) hostByCandidate[hosted.SemanticId] = Rid.Value(match.Wall.Id);
                    else if (set.Rules.FirstOrDefault(r => r.Id == hosted.RuleId)?.HostFaces?.Contains("end") == true)
                    {
                        // THE SAME ANSWER THE CONVERSION GAVE, for a rule that allows wall ends.
                        CadEndMatch end = CadHostResolver.NearestEnd(walls, atFeet, set.HostSearchMm);
                        if (end != null) hostByCandidate[hosted.SemanticId] = Rid.Value(end.Wall.Id);
                    }
                }
            }
            catch { }

            CadUpdate update = CadUpdateRules.Plan(interpretation.Candidates, subjects, set, scope,
                                                   accepted, rejectedPairings, hostByCandidate,
                                                   acceptMove ? move : null);
            // WHERE EACH CHANGE CAME FROM: the drawing, the reading, the rules, or a person.
            // THE SET WAS READ, NOT ONLY THE FILE: a drawing is read with its references, so
            // "the same bytes" means the same host AND the same references.
            string sourceSet = CadDwgCache.SourceSetSha256(facts.ExternalPath, facts.FileSha256);
            JObject origins = CadUpdateRules.AttributeOrigins(update, subjects, set, facts.FileSha256,
                                                              CadInterpretationRules.InterpretationVersion,
                                                              move != null && acceptMove, sourceSet);
            // WHAT A PERSON DECIDED about the changes held for them.
            List<string> decisionErrors = CadDecisions.Apply(update, decisions);
            if (decisionErrors.Count > 0)
                return CommandResult.Fail("resolve_refused: " + string.Join("; ", decisionErrors) +
                                          ". Nothing was planned: a decision that cannot stand is not skipped.");
            JArray migrations = MigrationPlans(doc, update);

            // ---------------------------------------------------------- actions
            // WHAT A PERSON DECIDED about the dependents a split held, bound to THIS document and set.
            string depError;
            List<CadDependentDecision> depDecisions = DependentDecisions(request, out depError);
            if (depError != null)
                return CommandResult.Fail("dependent_decisions refused: " + depError + ". Nothing was planned.");
            var depCtx = new DependentDecisionContext
            {
                Decisions = depDecisions,
                Context = title + "|" + (sourceSet != null ? sourceSet : "file:" + facts.FileSha256)
            };
            string levelError;
            JArray createIndex, withdrawnRows, resolvedNames;
            JArray actions = Actions(doc, update, interpretation, set, request, title, harvest.Segments,
                                     sourceFingerprint, out levelError, out createIndex, out withdrawnRows,
                                     out resolvedNames, depCtx);
            if (levelError != null) return CommandResult.Fail(levelError);
            foreach (CadDependentDecision d in depDecisions.Where(x => !x.Used))
                depCtx.Problems.Add("not_held: element " + d.ElementId + " is not a dependent this plan holds for a " +
                                    "person (it stays, moves by itself, or belongs to no split here)");
            if (depCtx.Problems.Count > 0)
                return CommandResult.Fail("dependent_decisions refused: " + string.Join("; ", depCtx.Problems) +
                                          ". Nothing was planned: a decision that cannot stand is not skipped.");

            // WHAT THE APPLY MUST RE-STAMP without touching geometry: a v1 record
            // this run claimed becomes v2 (it now knows its placement), and under
            // an accepted move every element left in place is stamped with the
            // transform it now sits under - or the next plan reports the move
            // again, forever.
            JArray restamp = Restamp(update, scope, move != null && acceptMove, subjects, facts.FileSha256, sourceSet,
                                     set.Sha256);
            JArray candidateIndex = CandidateIndex(update, actions);
            foreach (JToken row in createIndex) candidateIndex.Add(row);
            foreach (JToken row in restamp) candidateIndex.Add(row);

            var placementJson = new JObject
            {
                ["id"] = placement.PlacementId,
                ["instance_id"] = instanceId,
                ["transform"] = placement.TransformFingerprint,
                ["origin_mm"] = placement.EncodedOrigin,
                ["basis"] = placement.EncodedBasis,
                ["external_path"] = placement.ExternalPath,
                ["identity"] = identity.Mode
            };

            var result = new JObject
            {
                ["document"] = title,
                ["instance_id"] = instanceId,
                ["read_only"] = true,
                ["source"] = new JObject
                {
                    ["fingerprint"] = sourceFingerprint,
                    ["file_sha256"] = facts.FileSha256,
                    ["source_set_sha256"] = sourceSet,
                    ["external_path"] = facts.ExternalPath,
                    ["identity"] = identity.ToJson()
                },
                ["placement"] = placementJson,
                ["scope"] = scope.ToJson(),
                ["scope_means"] = "which elements THIS PLACEMENT may claim. claimed: v2 records naming this " +
                                  "placement or one you named as superseded. migrated_from_v1: records written " +
                                  "before placement identity, claimable because exactly one placement of their " +
                                  "file could have built them; the apply rewrites them as v2. ambiguous_v1: two " +
                                  "placements could have built them - never claimed, never orphaned. " +
                                  "other_placement: this same file under another placement, untouched.",
                ["migrated_from_v1"] = new JObject
                {
                    ["count"] = scope.MigratedFromV1.Count,
                    ["element_ids"] = new JArray(scope.MigratedFromV1.OrderBy(x => x).Take(200)),
                    ["restamped_on_apply"] = restamp.Count(r => r.Value<string>("reason") == CadPlacementRules.RestampMigrated)
                },
                ["ambiguous_v1"] = new JArray(scope.AmbiguousV1.Select(x => x.ToJson()).Take(100)),
                ["placement_moved"] = move == null
                    ? (JToken)JValue.CreateNull()
                    : new JObject
                    {
                        ["accepted"] = acceptMove,
                        ["move"] = move.ToJson(),
                        ["elements_built_under_the_old_transform"] = new JArray(movedRecords.Take(200)),
                        ["means"] = "the placement does not sit where it sat when these elements were built, and " +
                                    "you accepted that. The plan is re-derived under the new transform: an element " +
                                    "still on its built line follows the drawing (set_curve), one already where the " +
                                    "drawing now puts it is left and re-stamped, one a person ALSO moved is a conflict."
                    },
                ["placement_transform_unknown"] = transformUnknown,
                ["requirement_set"] = new JObject
                {
                    ["id"] = set.Id, ["version"] = set.Version, ["sha256"] = set.Sha256
                },
                ["revision_b"] = new JObject
                {
                    ["candidates"] = update.CandidatesRead,
                    ["needing_review"] = interpretation.NeedingReview.Count()
                },
                ["revision_a"] = new JObject
                {
                    ["elements_with_provenance"] = subjects.Count,
                    ["as_built_recorded"] = subjects.Count(s => !string.IsNullOrWhiteSpace(s.Provenance?.BuiltGeometry)),
                    ["as_built_missing"] = subjects.Count(s => string.IsNullOrWhiteSpace(s.Provenance?.BuiltGeometry)),
                    ["as_built_means"] = "an element whose provenance does not record the geometry it was BUILT " +
                                         "with cannot be told apart from one somebody moved. Those come back as " +
                                         "review rather than as an update, and they are elements this bridge " +
                                         "created before it recorded that."
                },
                ["change_origins"] = origins,
                ["decisions"] = new JObject
                {
                    ["applied"] = decisions.Count,
                    ["migration_plans"] = migrations,
                    ["means"] = "retype and rotate_in_face became typed actions that keep the element; delete " +
                                "became a verified delete of that one element; keep " +
                                "re-stamps the element as it stands; replace is a migration plan only. A decision " +
                                "whose typed action could not be derived stays held, with the reason in its evidence."
                },
                ["counts_by_kind"] = update.CountsByKind(),
                ["counts_by_classification"] = update.CountsByClassification(),
                ["classification_vocabulary"] = new JArray(CadChange.All),
                ["classification_means"] =
                    "WHAT CHANGED, as distinct from what to do about it. Several different changes need the " +
                    "same treatment - a retyped wall, a relayered wall and one somebody moved by hand all end " +
                    "in 'a person decides' - and a reader with only the kind cannot tell them apart. The " +
                    "vocabulary is closed and every name is reported with its count, including the zeros: a " +
                    "key that simply disappeared would read as 'not measured' rather than 'none found'.",
                // EVERY DIVISION IN ONE PLACE. See Core/CadRevisionShapes.cs: what survives, what is
                // created, what a decision would remove, which fittings and joins are in the way, and
                // the line before against the lines after. Assembled from the same actions, never
                // deciding anything they do not.
                ["divisions"] = CadRevisionShapes.Describe(doc, update, subjects),
                ["divisions_mean"] = "one row per split or merge, held or accepted. An id in 'keeps' is an id " +
                                     "this operation does not have to give up; 'id_substitutions' is where an " +
                                     "operation that must replace an element says so, and it is empty here.",
                ["actions"] = actions,
                ["rows_in_actions"] = actions.OfType<JObject>().Sum(a =>
                    ((a["arguments"] as JObject)?["elements"] as JArray)?.Count ??
                    ((a["arguments"] as JObject)?["operations"] as JArray)?.Count ?? 0),
                ["withdrawn"] = withdrawnRows,
                ["resolved"] = resolvedNames,
                ["automatic"] = update.Actions.Count(a => a.Automatic && a.Kind != "leave" && a.Kind != "paired_away"),
                ["needs_a_person"] = update.Actions.Count(a => !a.Automatic),
                // WHAT A DECISION IN THIS PLAN CAN RESOLVE, apart from what needs information:
                // a held element whose change admits a typed decision, or a pairing offered.
                ["awaiting_a_decision"] = update.Actions.Count(a => !a.Automatic &&
                    ((a.ElementId.HasValue && CadDecisions.AllowedFor.Any(kv => kv.Value.Contains(a.Classification))) ||
                     a.PairedWith != null || a.Evidence["may_be_element"] != null)) +
                    // and every dependent held with a key: a decision resolves it
                    depCtx.HeldOrphans.Count,
                ["held_for_information"] = update.Actions.Count(a => !a.Automatic &&
                    !((a.ElementId.HasValue && CadDecisions.AllowedFor.Any(kv => kv.Value.Contains(a.Classification))) ||
                      a.PairedWith != null || a.Evidence["may_be_element"] != null)),
                ["plan"] = new JArray(update.Actions.Select(a => a.ToJson())),
                ["kinds_mean"] = new JObject
                {
                    ["create"] = "in revision B, nothing in the model remembers being built from it",
                    ["set_curve"] = "the DRAWING moved and nobody has touched the element since: update it, and " +
                                    "it keeps its id, its parameters and everything hosted on it",
                    ["move"] = "a point element a caller accepted as moved: it is moved along its own wall to where " +
                               "revision B draws it, and keeps its id, its parameters and its host",
                    ["paired_away"] = "the element a move or set_curve in this plan stands for",
                    ["review"] = "a person moved it, or both moved, or nothing recorded where it started. NOT " +
                                 "in the actions: applying any of these could destroy work nobody asked to lose",
                    ["leave"] = "unchanged in both",
                    ["orphan"] = "built from this drawing under these rules, and revision B no longer says it. " +
                                 "Never deleted automatically: an entity that MOVED far enough reads as a new " +
                                 "one, so a deletion and a relocation look identical from here"
                },
                ["apply_binding"] = new JObject
                {
                    ["actions_fingerprint"] = CadConversionPlanRules.ActionsFingerprint(actions),
                    ["source_fingerprint"] = sourceFingerprint,
                    ["requirement_set_sha256"] = set.Sha256,
                    ["interpretation_version"] = CadInterpretationRules.InterpretationVersion,
                    ["target_document"] = title,
                    ["revit_version"] = SafeVersion(uiApp),
                    ["means"] = "the actions above are ready calls to commands that rehearse, confirm and re-read " +
                                "their own work. Send them through horizun_execute_plan. This binding is what " +
                                "proves they are the ones this plan emitted."
                },
                ["provenance"] = new JObject
                {
                    ["requirement_set_id"] = set.Id,
                    ["requirement_set_version"] = set.Version,
                    ["requirement_set_sha256"] = set.Sha256,
                    ["source_fingerprint"] = sourceFingerprint,
                    ["source_file_sha256"] = facts.FileSha256,
                    ["source_set_sha256"] = sourceSet,
                    ["source_path"] = placement.ExternalPath,
                    ["placement"] = placementJson,
                    ["placement_move_accepted"] = move != null && acceptMove,
                    ["interpretation_version"] = CadInterpretationRules.InterpretationVersion,
                    ["plan_fingerprint"] = "cadupd:" + CadConversionPlanRules.ActionsFingerprint(actions).Substring(8),
                    ["means"] = "copy this into horizun_apply_cad_update. Without it the elements this update " +
                                "creates remember nothing, and the NEXT update builds them again. The placement " +
                                "block is what lets the next update tell this placement from another of the " +
                                "same file, and measure whether it has moved."
                },
                ["candidate_index"] = candidateIndex,
                ["restamp"] = new JObject
                {
                    ["count"] = restamp.Count,
                    ["means"] = "elements the apply re-stamps WITHOUT touching their geometry: a v1 record this " +
                                "run claimed becomes v2, and under an accepted move an element left in place is " +
                                "stamped with the transform it now sits under. They ride in candidate_index under " +
                                "the key cad-update-restamp."
                },
                ["lineage"] = new JObject
                {
                    ["this_file_sha256"] = facts.FileSha256,
                    ["this_source_set_sha256"] = sourceSet,
                    ["this_placement_id"] = placement.PlacementId,
                    ["supersedes"] = new JArray(lineage),
                    ["supersedes_placement_ids"] = new JArray(lineagePlacements),
                    ["supersedes_requirement_set_sha256"] = new JArray(rulesLineage.OrderBy(x => x, StringComparer.Ordinal)),
                    ["claimed_under_earlier_rules"] = underEarlierRules,
                    ["source_hashes_in_the_model"] = new JArray(shas),
                    ["means"] = "the elements this update is about are the ones built from THIS placement, or " +
                                "from a placement or file you say it supersedes, under these rules. Another " +
                                "placement of the same file is not this run's, even with the same hash. " +
                                "Everything else in the model is left alone and is not counted here."
                },
                ["pairings_offered"] = new JArray(update.Actions
                    .Where(a => a.PairedWith != null)
                    .Select(a => new JObject
                    {
                        ["element_id"] = a.ElementId,
                        ["candidate_id"] = RecommendedKeep(doc, update, a, set) ?? a.PairedWith,
                        ["longest_piece"] = a.Evidence["may_have_been_split_into"] != null ? a.PairedWith : null,
                        ["confidence"] = a.PairConfidence,
                        ["paired_on"] = a.Evidence.Value<string>("paired_on"),
                        ["split_into"] = a.Evidence["may_have_been_split_into"]
                    })),
                ["pairings_rejected"] = new JArray(update.Rejected),
                ["splits"] = new JArray(update.Of("set_curve")
                    .Where(a => a.Evidence["split_companions"] != null)
                    .Select(a => new JObject
                    {
                        ["element_id"] = a.ElementId,
                        ["keeps_its_id_as"] = a.CandidateId,
                        ["new_pieces"] = a.Evidence["split_companions"],
                        ["automatic"] = a.Automatic,
                        ["held"] = a.Evidence["split_held"],
                        ["means"] = "old element -> the pieces the drawing now shows: the element is re-shaped to " +
                                    "one piece and keeps its id; each other piece is a new element, stamped with its " +
                                    "own candidate id by horizun_apply_cad_update"
                    })),
                ["pairings_mean"] = "a wall that MOVED between revisions leaves a create and an orphan, and they " +
                                    "are the same wall. Nothing in a DWG says so - there is no handle anywhere in " +
                                    "the Revit CAD API, measured - so a resemblance is offered here and never " +
                                    "acted on. Send the ones you accept back in accept_pairings and the element " +
                                    "is re-shaped in place instead of being duplicated.",
                ["provenance_problems"] = new JArray(problems.Take(50)),
                ["not_done_here"] = new JArray(
                    "orphans are never deleted: an entity that moved far enough reads as a new one, so a " +
                    "deletion and a relocation look identical from here",
                    "send these actions to horizun_apply_cad_update, NOT to horizun_execute_plan. Both would " +
                    "build the same elements; only one of them stamps what it built, and an unstamped element " +
                    "is one the next update builds a second time.")
            };
            if (blocksReadForReply != null) result["blocks"] = blocksReadForReply;
            if (sectionsReadForReply != null) result["sections"] = sectionsReadForReply;
            if (_fittingsPlan != null) { result["fittings_plan"] = _fittingsPlan; _fittingsPlan = null; }
            // DEPENDENTS NO WALL RE-HOMES BY ITSELF, each with its alternatives and the key a decision quotes.
            if (depCtx.HeldOrphans.Count > 0) result["orphans_held"] = depCtx.HeldOrphans;
            // WHAT WAS READ FROM THE FILE, and whether the reading was reused: the cache states hit or
            // miss per read, with the reference that changed when a changed set is the reason.
            var reads = new JObject();
            if (solidRead != null)
                reads["solid_hatch"] = new JObject { ["seconds"] = solidRead["seconds"], ["cache"] = solidRead["cache"] };
            if (blocksReadForReply != null)
                reads["blocks"] = new JObject { ["seconds"] = blocksReadForReply["seconds"], ["cache"] = blocksReadForReply["cache"] };
            if (reads.Count > 0) result["dwg_reads"] = reads;
            return CommandResult.Ok(result);
        }

        /// <summary>
        /// The executable half: creates through horizun_create_elements, moves
        /// through horizun_transform_elements set_curve. Everything a person must
        /// decide is deliberately absent.
        /// </summary>
        private static JArray Actions(Document doc, CadUpdate update, CadInterpretation interpretation,
                                      CadRequirementSet set, JObject request, string target,
                                      IList<CadSegment> drawn, string sourceFingerprint,
                                      out string levelError, out JArray createIndex, out JArray withdrawn,
                                      out JArray resolved, DependentDecisionContext depCtx = null)
        {
            levelError = null;
            createIndex = new JArray();
            withdrawn = new JArray();
            resolved = new JArray();
            var actions = new JArray();

            // THE CREATES, BUILT AS THE PLAN ROUTE BUILDS THEM. The candidates are
            // the drawing's own, looked up by id; the conversion rules turn them
            // into rows of their real kind, and the plan route's resolution gives
            // them levels, types, hosts and faces - or withdraws them, by name.
            // AN ACCEPTED SPLIT IS DECIDED BEFORE ITS PIECES ARE PLANNED: if the element
            // cannot be re-shaped, none of its pieces may be built beside it.
            var reshapedTo = new Dictionary<long, CadPoint[]>();
            foreach (CadUpdateAction a in update.Of("set_curve").Where(x => x.Automatic && x.ElementId.HasValue &&
                                                                          x.Evidence["split_companions"] != null).ToList())
            {
                var self = doc.GetElement(Rid.Make(a.ElementId.Value)) as Wall;
                CadCandidate kept = interpretation.Candidates.FirstOrDefault(c => c.Id == a.CandidateId);
                // the width a type is chosen by, not the point tolerance
                CadRule keptRule = kept == null ? null : set.Rules.FirstOrDefault(r => r.Id == kept.RuleId);
                double widthTolerance = keptRule?.WallTypeToleranceMm ?? set.ThicknessToleranceMm;
                if (self != null && kept?.ThicknessMm != null &&
                    Math.Abs(kept.ThicknessMm.Value - self.Width * 304.8) > widthTolerance)
                {
                    // ANOTHER THICKNESS IS A RETYPE, when the set lists a type of that width.
                    string noType;
                    JObject retype = RetypeOperation(doc, a, interpretation, set, out noType);
                    if (retype == null)
                    {
                        HoldSplit(update, a, "kept_piece_is_another_thickness",
                                  "the piece that would keep the element is drawn " +
                                  kept.ThicknessMm.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm thick and the " +
                                  "element is " + (self.Width * 304.8).ToString("0.#", CultureInfo.InvariantCulture) +
                                  " mm, and " + noType);
                        continue;
                    }
                    a.Evidence["split_retype"] = retype;
                }
                // WHAT THE ELEMENT HOSTS, piece by piece.
                string dependentsHeld = ClassifyDependents(doc, update, a, set, depCtx);
                if (dependentsHeld != null)
                {
                    HoldSplit(update, a, "dependents_need_a_person", dependentsHeld);
                    continue;
                }
                JArray occupants = Occupants(doc, a, set);
                if (occupants.Count > 0)
                {
                    HoldSplit(update, a, "kept_piece_occupied",
                              "the kept piece would stand in the space of " +
                              string.Join(", ", occupants.Select(o => "element " + (long)o["element_id"])));
                    continue;
                }
                reshapedTo[a.ElementId.Value] = new[] { a.Geometry[0], a.Geometry[a.Geometry.Count - 1] };
            }

            List<CadUpdateAction> creates = update.Of("create").Where(a => a.Automatic && !a.Blocked).ToList();
            if (creates.Count > 0)
            {
                var byId = new Dictionary<string, CadCandidate>(StringComparer.Ordinal);
                foreach (CadCandidate c in interpretation.Candidates)
                    if (c?.Id != null && !byId.ContainsKey(c.Id)) byId[c.Id] = c;
                var subset = new CadInterpretation();
                foreach (CadUpdateAction a in creates)
                {
                    CadCandidate c;
                    if (a.CandidateId != null && byId.TryGetValue(a.CandidateId, out c)) subset.Candidates.Add(c);
                }

                CadConversionPlan plan = CadConversionPlanRules.Plan(subset, set, sourceFingerprint, false);
                List<JObject> requests = CadConversionPlanRules.AsCreateRequests(plan, target, 100);
                string refusal = PlanFromCadCommand.ResolveRows(doc, requests, request, resolved, set, drawn, withdrawn,
                                                                reshapedTo);
                if (refusal != null)
                {
                    levelError = refusal;
                    return actions;
                }

                var actionByRow = new Dictionary<int, CadPlannedAction>();
                foreach (CadPlannedAction pa in plan.Actions)
                    if (pa.SourceRow > 0) actionByRow[pa.SourceRow] = pa;

                // A SPLIT IS BUILT WHOLE OR NOT AT ALL. A piece resolution withdrew (no type
                // of its width, say) leaves the element's other stretch unbuilt if the rest
                // went ahead: the element and every sibling piece wait with it.
                var withdrawnIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JObject w in withdrawn.OfType<JObject>())
                {
                    int? row = w.Value<int?>("source_row");
                    CadPlannedAction pa;
                    if (row.HasValue && actionByRow.TryGetValue(row.Value, out pa) && pa.CandidateId != null)
                        withdrawnIds.Add(pa.CandidateId);
                }
                var droppedSiblings = new HashSet<string>(StringComparer.Ordinal);
                foreach (CadUpdateAction a in update.Of("set_curve").Where(x => x.Automatic &&
                                                                              x.Evidence["split_companions"] != null).ToList())
                {
                    var ids = ((JArray)a.Evidence["split_companions"]).Select(x => (string)x).ToList();
                    string missing = ids.FirstOrDefault(withdrawnIds.Contains);
                    if (missing == null) continue;
                    HoldSplit(update, a, "a_piece_cannot_be_built",
                              "piece '" + missing + "' was withdrawn by resolution, so re-shaping the element would " +
                              "leave that stretch of the wall unbuilt");
                    foreach (string id in ids) if (!withdrawnIds.Contains(id)) droppedSiblings.Add(id);
                }
                if (droppedSiblings.Count > 0)
                    foreach (JObject r in requests)
                        foreach (JObject row in ((JArray)r["elements"]).OfType<JObject>().ToList())
                        {
                            int? sourceRow = row.Value<int?>("source_row");
                            CadPlannedAction pa;
                            if (sourceRow == null || !actionByRow.TryGetValue(sourceRow.Value, out pa) ||
                                !droppedSiblings.Contains(pa.CandidateId ?? "")) continue;
                            withdrawn.Add(new JObject
                            {
                                ["source_row"] = sourceRow,
                                ["kind"] = row.Value<string>("kind"),
                                ["reason"] = "split_held_with_its_element",
                                ["means"] = "a sibling piece of the same split cannot be built, so the element keeps " +
                                            "its full length and this piece is not built inside it"
                            });
                            ((JArray)r["elements"]).Remove(row);
                        }
                requests = requests.Where(r => ((JArray)r["elements"]).Count > 0).ToList();

                // WHAT RESOLUTION WITHDREW IS NOT AUTOMATIC, and the plan says why.
                var emitted = new HashSet<string>(StringComparer.Ordinal);
                foreach (JObject r in requests)
                {
                    string key = "cad-update-create-" + (int)r["stage"] + "-" + (int)r["batch_of_stage"];
                    var elements = (JArray)r["elements"];
                    for (int k = 0; k < elements.Count; k++)
                    {
                        int? sourceRow = (elements[k] as JObject)?.Value<int?>("source_row");
                        CadPlannedAction pa;
                        if (sourceRow == null || !actionByRow.TryGetValue(sourceRow.Value, out pa)) continue;
                        emitted.Add(pa.CandidateId);
                        createIndex.Add(new JObject
                        {
                            ["key"] = key,
                            ["element_index"] = k,
                            ["candidate_id"] = pa.CandidateId,
                            ["semantic_id"] = pa.SemanticId,
                            ["geometry_id"] = pa.GeometryId,
                            ["rule_id"] = pa.RuleId,
                            ["layer"] = pa.Layer,
                            ["confidence"] = Math.Round(pa.Confidence, 4),
                            ["source_entities"] = new JArray(pa.SourceEntities)
                        });
                    }
                    actions.Add(new JObject
                    {
                        ["key"] = key,
                        ["tool"] = "horizun_create_elements",
                        ["arguments"] = r
                    });
                }
                foreach (CadUpdateAction a in creates)
                {
                    if (a.CandidateId != null && emitted.Contains(a.CandidateId)) continue;
                    a.Automatic = false;
                    JObject why = withdrawn.OfType<JObject>().FirstOrDefault(w =>
                    {
                        int? row = w.Value<int?>("source_row");
                        CadPlannedAction pa;
                        return row.HasValue && actionByRow.TryGetValue(row.Value, out pa) && pa.CandidateId == a.CandidateId;
                    });
                    a.Evidence["withdrawn"] = why == null ? (JToken)"the conversion produced no row for it" : why;
                    a.Evidence["new_or_never_built"] = "this plan cannot tell whether the symbol is new in this " +
                        "revision or was already withdrawn, for the same reason, when the unit was converted: nothing " +
                        "in the model records a withdrawal";
                    a.Says += " NOT in the actions: " + (why == null
                        ? "the conversion rules produced no row for this candidate."
                        : "resolution withdrew it (" + why.Value<string>("reason") + ").");
                }
            }

            // ONE OPERATION PER ELEMENT: a location line belongs to one element,
            // and the command refuses a shared one for exactly that reason.
            int firstMove = actions.Count;
            bool splitApplied = false;
            int n = 0;
            foreach (CadUpdateAction a in update.Of("set_curve").Where(x => x.Automatic))
            {
                if (!a.ElementId.HasValue || a.Geometry.Count < 2) continue;
                bool split = a.Evidence["split_companions"] != null;

                // A RESHAPED WALL IS NOT MOVED INTO A WALL THAT STANDS. MEASURED: a
                // pairing offered after the reading changed would lengthen one piece
                // over its neighbour, and removing the neighbour is never automatic -
                // so the two would share the space. The reshape waits for a person.
                // (A split was asked this before its pieces were planned.)
                if (!split)
                {
                    JArray occupants = Occupants(doc, a, set);
                    if (occupants.Count > 0)
                    {
                        a.Automatic = false;
                        a.Evidence["occupied_by"] = occupants;
                        a.Says += " HELD: the new line would put this wall in the space of " +
                                  string.Join(", ", occupants.Select(o => "element " + (long)o["element_id"])) +
                                  ", which still stands; removing it is not this update's decision.";
                        continue;
                    }
                    // WHAT IS HOSTED STAYS ON ITS WALL. A shortened wall would leave a door
                    // or a device standing past its end: a re-hosting nobody decided.
                    string beyond = HostedBeyond(doc, a, set);
                    if (beyond != null)
                    {
                        a.Automatic = false;
                        a.Evidence["hosted_outside_the_new_line"] = beyond;
                        a.Says += " HELD: " + beyond + ".";
                        continue;
                    }
                }
                splitApplied |= split;
                // WHAT A PERSON DECIDED TO DELETE GOES FIRST, by a verified delete of that one element.
                // MEASURED (campaign 5, doors on W3): deleted after the re-shape, a door the new line no
                // longer carried made Revit roll the re-shape back ("not cutting anything"), and with it
                // the whole update. What is going anyway is removed before the wall changes under it.
                if (split)
                    foreach (JObject gone in (a.Evidence["split_dependents"] as JArray ?? new JArray()).OfType<JObject>()
                                 .Where(dep => dep.Value<bool?>("delete") == true))
                        actions.Add(new JObject
                        {
                            ["key"] = "cad-update-dependent-delete-" + (long)gone["element_id"],
                            ["tool"] = "horizun_delete_verified",
                            ["arguments"] = new JObject
                            {
                                ["target_document"] = target, ["mode"] = "ids", ["ids"] = new JArray((long)gone["element_id"])
                            }
                        });
                actions.Add(new JObject
                {
                    ["key"] = "cad-update-move-" + (n++),
                    // what the apply must NOT put back after the re-shape: the dependents it re-creates
                    ["hosted_to_substitute"] = new JArray((a.Evidence["split_dependents"] as JArray ?? new JArray())
                        .OfType<JObject>().Where(d => (string)d["class"] == CadSplitRules.MovesTo)
                        .Select(d => d["element_id"])),
                    ["tool"] = "horizun_transform_elements",
                    ["arguments"] = new JObject
                    {
                        ["target_document"] = target,
                        ["units"] = "mm",
                        ["operations"] = new JArray(new JObject
                        {
                            ["operation"] = "set_curve",
                            ["element_ids"] = new JArray(a.ElementId.Value),
                            ["start"] = Pt(a.Geometry[0]),
                            ["end"] = Pt(a.Geometry[a.Geometry.Count - 1])
                        })
                    }
                });
            }
            // A SPLIT SHORTENS ITS ELEMENT BEFORE ITS PIECES ARE BUILT beside it, so the
            // model never holds a wall inside another, not even between two stages.
            if (splitApplied && firstMove > 0)
            {
                List<JToken> all = actions.ToList();
                actions.Clear();
                foreach (JToken t in all.Skip(firstMove)) actions.Add(t);
                foreach (JToken t in all.Take(firstMove)) actions.Add(t);
            }

            // WHAT A PERSON DECIDED: a type change, or a turn in the element's own face.
            int d = 0;
            var sectionWrites = new JArray();
            JArray fittingsPlan = null;
            var fittingsBefore = new JObject();
            foreach (CadUpdateAction a in update.Actions.Where(x => x.Automatic &&
                                                                    (x.Kind == CadDecisions.Retype || x.Kind == CadDecisions.RotateInFace)).ToList())
            {
                string why;
                // ALL SECTION RESIZES IN ONE TRANSACTION: two legs of one elbow resized one at a time
                // leave Revit a fitting that fits neither half-way state (MEASURED: it kept the elbow and
                // inserted a transition on each leg).
                if (a.Kind == CadDecisions.Retype && a.Evidence.Value<bool?>("section") == true)
                {
                    // A RECTANGULAR SECTION is the instance's own width and height: written by the
                    // one verified parameter writer, never by a type change that would move others.
                    JArray writes = SectionWrites(doc, a, out why);
                    if (writes == null)
                    {
                        a.Automatic = false;
                        a.Evidence["decision_not_carried_out"] = why;
                        a.Says += " HELD: the decision could not become a typed action (" + why + ").";
                        continue;
                    }
                    foreach (JToken w in writes) sectionWrites.Add(w);
                    fittingsBefore[a.ElementId.Value.ToString(CultureInfo.InvariantCulture)] =
                        a.Evidence["fittings_on_its_ends"] ?? new JArray();
                    a.Evidence["resolve_key"] = "cad-update-resolve-sections";
                    continue;
                }
                JObject op = a.Kind == CadDecisions.Retype
                    ? RetypeOperation(doc, a, interpretation, set, out why)
                    : TurnOperation(doc, a, out why);
                if (op == null)
                {
                    a.Automatic = false;
                    a.Evidence["decision_not_carried_out"] = why;
                    a.Says += " HELD: the decision could not become a typed action (" + why + ").";
                    continue;
                }
                actions.Add(new JObject
                {
                    ["key"] = "cad-update-resolve-" + (d++),
                    ["tool"] = "horizun_transform_elements",
                    ["arguments"] = new JObject
                    {
                        ["target_document"] = target,
                        ["units"] = "mm",
                        ["operations"] = new JArray(op)
                    }
                });
                a.Evidence["resolve_key"] = "cad-update-resolve-" + (d - 1);
            }
            if (sectionWrites.Count > 0)
            {
                // WHAT HAPPENS TO THE FITTINGS, SAID BEFORE ANYTHING IS WRITTEN. MEASURED (campaign 6): with
                // only the runs resized, Revit keeps an elbow at its old size and inserts a transition on each
                // resized leg. fitting_policy "keep" (the default) accepts that and says so here;
                // "rebuild_where_viable" replaces a fitting whose every run takes the same new section.
                string policy = request.Value<string>("fitting_policy") ?? "keep";
                JArray refits;
                fittingsPlan = FittingsPlan(doc, sectionWrites, fittingsBefore, policy, out refits);
                _fittingsPlan = fittingsPlan;
                if (refits.Count > 0)
                {
                    var rebuiltRuns = new HashSet<long>(refits.OfType<JObject>().SelectMany(r => (r["runs"] as JArray).Select(t => (long)t)));
                    var rest = new JArray(sectionWrites.OfType<JObject>().Where(w => !rebuiltRuns.Contains(w.Value<long>("target_id"))));
                    sectionWrites = rest;
                    foreach (CadUpdateAction ra in update.Actions.Where(x => x.ElementId.HasValue && rebuiltRuns.Contains(x.ElementId.Value) &&
                                                                              (string)x.Evidence["resolve_key"] == "cad-update-resolve-sections"))
                        ra.Evidence["resolve_key"] = "cad-update-resolve-refit";
                    actions.Add(new JObject
                    {
                        ["key"] = "cad-update-resolve-refit",
                        ["tool"] = "horizun_cad_connect",
                        ["arguments"] = new JObject { ["target_document"] = target, ["refit"] = refits }
                    });
                }
                if (sectionWrites.Count > 0)
                    actions.Add(new JObject
                    {
                        ["key"] = "cad-update-resolve-sections",
                        ["tool"] = "horizun_write_params_verified",
                        ["arguments"] = new JObject { ["target_document"] = target, ["writes"] = sectionWrites },
                        // read back by the apply: what the fittings on these ducts' ends became
                        ["fittings_before"] = fittingsBefore
                    });
            }

            // WHAT A PERSON DECIDED TO DELETE: one verified delete per element.
            int removals = 0;
            foreach (CadUpdateAction a in update.Of(CadDecisions.Delete).Where(o => o.Automatic && o.ElementId.HasValue))
            {
                actions.Add(new JObject
                {
                    ["key"] = "cad-update-delete-" + (removals++),
                    ["tool"] = "horizun_delete_verified",
                    ["arguments"] = new JObject
                    {
                        ["target_document"] = target,
                        ["mode"] = "ids",
                        ["ids"] = new JArray(a.ElementId.Value)
                    }
                });
            }

            // THE REST OF AN ACCEPTED SPLIT: the kept element's new type, then each dependent
            // re-created on its new piece, then the one it replaces deleted.
            int sub = 0;
            foreach (CadUpdateAction a in update.Of("set_curve").Where(x => x.Automatic &&
                                                                          x.Evidence["split_companions"] != null))
            {
                if (a.Evidence["split_retype"] is JObject retype)
                    actions.Add(new JObject
                    {
                        ["key"] = "cad-update-split-retype-" + a.ElementId.Value,
                        ["tool"] = "horizun_transform_elements",
                        ["arguments"] = new JObject
                        {
                            ["target_document"] = target, ["units"] = "mm", ["operations"] = new JArray(retype)
                        }
                    });
                var splitWall = doc.GetElement(Rid.Make(a.ElementId.Value)) as Wall;
                XYZ lineO = null, lineU = null;
                double lineLen;
                if (splitWall != null) CadSplitDependents.LineOf(splitWall, out lineO, out lineU, out lineLen);
                foreach (JObject move in (a.Evidence["split_dependents"] as JArray ?? new JArray()).OfType<JObject>()
                             .Where(d => (string)d["class"] == CadSplitRules.MovesTo))
                {
                    var fi = doc.GetElement(Rid.Make((long)move["element_id"])) as FamilyInstance;
                    if (fi == null) continue;
                    // A DECISION MAY SEND IT TO THE KEPT PIECE (the element itself) and slide it onto its piece.
                    JToken host = move.Value<bool?>("target_is_kept") == true
                        ? (JToken)a.ElementId.Value
                        : new JObject
                        {
                            [CadSplitDependents.CreatedFor] = (string)move["target_candidate_id"],
                            ["rehearse_with"] = a.ElementId.Value
                        };
                    XYZ at = (fi.Location as LocationPoint)?.Point;
                    double? along = move.Value<double?>("move_along_mm");
                    XYZ slid = along.HasValue && at != null && lineU != null ? at + lineU.Multiply(along.Value / 304.8) : null;
                    EmitSubstitution(doc, fi, host, set, target, "cad-update-substitute-" + sub++, actions, createIndex,
                                     null, slid);
                }
                // (what a person decided to delete was emitted before the re-shape, above)
            }
            Rehome(doc, update, set, target, actions, createIndex, depCtx);

            // A POINT MOVES BY A VECTOR, along its own wall.
            int m = 0;
            foreach (CadUpdateAction a in update.Of("move").Where(x => x.Automatic))
            {
                if (!a.ElementId.HasValue || !a.Vector.HasValue) continue;
                actions.Add(new JObject
                {
                    ["key"] = "cad-update-shift-" + (m++),
                    ["tool"] = "horizun_transform_elements",
                    ["arguments"] = new JObject
                    {
                        ["target_document"] = target,
                        ["units"] = "mm",
                        ["operations"] = new JArray(new JObject
                        {
                            ["operation"] = "move",
                            ["element_ids"] = new JArray(a.ElementId.Value),
                            ["vector"] = Pt(a.Vector.Value)
                        })
                    }
                });
            }
            return actions;
        }

        /// <summary>The decisions a person sent, read strictly: a malformed one is a refusal.</summary>
        private static string Decisions(JObject request, out List<CadDecision> decisions)
        {
            decisions = new List<CadDecision>();
            JArray raw = request["resolve"] as JArray;
            if (raw == null) return request["resolve"] == null ? null : "resolve must be an array";
            if (raw.Count > 500) return "resolve carries " + raw.Count + " entries; 500 is the bound.";
            foreach (JToken t in raw)
            {
                var o = t as JObject;
                long? id = o?.Value<long?>("element_id");
                string decision = o?.Value<string>("decision");
                if (id == null || string.IsNullOrWhiteSpace(decision))
                    return "resolve entries must each be { element_id, decision }, both present.";
                decisions.Add(new CadDecision { ElementId = id.Value, Decision = decision.Trim() });
            }
            return null;
        }

        /// <summary>
        /// The width and height a rectangular duct's section now asks for, as verified parameter
        /// writes - plus the fittings on its ends, named so the reply can say what follows it.
        /// </summary>
        /// <summary>The fittings plan the last Actions() built, for the reply of the same call.</summary>
        [ThreadStatic] private static JArray _fittingsPlan;

        /// <summary>
        /// Per fitting on a resized duct's end: its runs with their section now and proposed, what is
        /// protected, whether a rebuild is viable and why, and what the chosen policy does - including
        /// what Revit is MEASURED to do when the fitting is kept (campaign 6). Nothing is written here.
        /// </summary>
        private static JArray FittingsPlan(Document doc, JArray writes, JObject fittingsBefore, string policy, out JArray refits)
        {
            refits = new JArray();
            var plan = new JArray();
            var proposed = new Dictionary<long, Tuple<double, double>>();
            foreach (var g in writes.OfType<JObject>().GroupBy(w => w.Value<long>("target_id")))
            {
                double? w = g.FirstOrDefault(x => x.Value<string>("parameter") == "RBS_CURVE_WIDTH_PARAM")?.Value<double>("value");
                double? h = g.FirstOrDefault(x => x.Value<string>("parameter") == "RBS_CURVE_HEIGHT_PARAM")?.Value<double>("value");
                if (w.HasValue && h.HasValue) proposed[g.Key] = Tuple.Create(Math.Round(w.Value * 304.8, 1), Math.Round(h.Value * 304.8, 1));
            }
            var fittingIds = fittingsBefore.Properties().SelectMany(p => (p.Value as JArray ?? new JArray()).Select(t => (long)t)).Distinct().ToList();
            foreach (long fid in fittingIds)
            {
                var f = doc.GetElement(Rid.Make(fid)) as FamilyInstance;
                if (f?.MEPModel?.ConnectorManager == null) continue;
                string part = "";
                try { part = ((PartType)(f.Symbol.Family.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger() ?? -1)).ToString(); } catch { }
                var runs = new JArray();
                var protectedIds = new JArray();
                var sizes = new HashSet<string>();
                bool allResized = true, allRuns = true;
                var seen = new HashSet<long>();
                foreach (Connector c in f.MEPModel.ConnectorManager.Connectors)
                    foreach (Connector o in c.AllRefs)
                    {
                        if (o.Owner == null || o.Owner.Id == f.Id) continue;
                        long oid = Rid.Value(o.Owner.Id);
                        if (!seen.Add(oid)) continue;
                        var run = o.Owner as Autodesk.Revit.DB.Mechanical.Duct;
                        if (run == null) { allRuns = false; protectedIds.Add(oid); continue; }
                        Tuple<double, double> to;
                        bool resized = proposed.TryGetValue(oid, out to);
                        if (!resized) { allResized = false; protectedIds.Add(oid); }
                        else sizes.Add(to.Item1.ToString(CultureInfo.InvariantCulture) + "x" + to.Item2.ToString(CultureInfo.InvariantCulture));
                        double wNow = (run.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble() ?? 0) * 304.8;
                        double hNow = (run.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble() ?? 0) * 304.8;
                        runs.Add(new JObject
                        {
                            ["element_id"] = oid,
                            ["section_now_mm"] = Math.Round(wNow, 1).ToString(CultureInfo.InvariantCulture) + "x" + Math.Round(hNow, 1).ToString(CultureInfo.InvariantCulture),
                            ["section_proposed_mm"] = resized ? to.Item1.ToString(CultureInfo.InvariantCulture) + "x" + to.Item2.ToString(CultureInfo.InvariantCulture) : null,
                            ["resized"] = resized
                        });
                    }
                string notViable = !(part == "Elbow" || part == "Tee" || part == "Cross") ? "a " + part + " is not rebuilt by this route"
                    : !allRuns ? "it is joined to another fitting (a chain), which is protected"
                    : !allResized ? "a run of it is not being resized, and is protected"
                    : sizes.Count != 1 ? "its runs are asked for different sections (" + string.Join(", ", sizes) + ")"
                    : null;
                bool rebuild = policy == "rebuild_where_viable" && notViable == null;
                plan.Add(new JObject
                {
                    ["fitting_id"] = fid, ["part"] = part, ["runs"] = runs, ["protected"] = protectedIds,
                    ["viable_to_rebuild"] = notViable == null, ["not_viable_because"] = notViable,
                    ["viable_means"] = "by topology and sections only: whether Revit can PLACE the new fitting (a leg long enough " +
                                       "for its radius, room for it) is known only when it is placed - MEASURED on a real plan, a " +
                                       "70 mm leg could not take a 10 in elbow. The refit is then rolled back whole and says so.",
                    ["policy"] = policy, ["outcome"] = rebuild ? "rebuild" : "keep",
                    ["identity"] = rebuild ? "the fitting is replaced by a new one (new element id); the runs keep theirs" : "unchanged",
                    ["connections_kept"] = "every run stays joined at this junction; every run's other end keeps its connections",
                    ["transitions"] = rebuild
                        ? "none appear: the new " + part.ToLowerInvariant() + " takes the new section at every end"
                        : "expected to appear: Revit keeps this " + part.ToLowerInvariant() + " at its size and inserts a transition " +
                          "on each resized run (measured, campaign 6; re-read after the apply in fittings_after_resize)",
                    ["reason"] = rebuild ? "every run of it takes the same new section and nothing else is joined to it"
                        : policy == "keep" ? "fitting_policy is keep (the default); send rebuild_where_viable to replace viable fittings"
                        : "not viable: " + notViable
                });
                if (rebuild)
                {
                    string[] wh = sizes.First().Split('x');
                    refits.Add(new JObject
                    {
                        ["fitting_id"] = fid,
                        ["runs"] = new JArray(runs.Select(r => r["element_id"])),
                        ["width_mm"] = double.Parse(wh[0], CultureInfo.InvariantCulture),
                        ["height_mm"] = double.Parse(wh[1], CultureInfo.InvariantCulture)
                    });
                }
            }
            return plan;
        }

        private static JArray SectionWrites(Document doc, CadUpdateAction a, out string why)
        {
            why = null;
            var duct = a.ElementId.HasValue ? doc.GetElement(Rid.Make(a.ElementId.Value)) as Autodesk.Revit.DB.Mechanical.Duct : null;
            if (duct == null) { why = "the element is not a duct"; return null; }
            if (a.Evidence["not_resizable"] != null) { why = "the held duct is round: a round duct is not resized into a rectangular one"; return null; }
            double? w = a.Evidence.Value<double?>("drawing_asks_width_mm"), h = a.Evidence.Value<double?>("drawing_asks_height_mm");
            if (!w.HasValue || !h.HasValue || w <= 0 || h <= 0) { why = "the section evidence is incomplete"; return null; }
            var fittings = new JArray();
            try
            {
                foreach (Connector con in duct.ConnectorManager.Connectors)
                    foreach (Connector other in con.AllRefs)
                        if (other.Owner is FamilyInstance fi && fi.Id != duct.Id &&
                            (other.ConnectorType & ConnectorType.Physical) != 0)
                            fittings.Add(Rid.Value(fi.Id));
            }
            catch { }
            a.Evidence["fittings_on_its_ends"] = fittings;
            return new JArray
            {
                new JObject { ["target_id"] = a.ElementId.Value, ["parameter"] = "RBS_CURVE_WIDTH_PARAM", ["value"] = w.Value / 304.8 },
                new JObject { ["target_id"] = a.ElementId.Value, ["parameter"] = "RBS_CURVE_HEIGHT_PARAM", ["value"] = h.Value / 304.8 }
            };
        }

        /// <summary>
        /// change_type to the type the drawing now asks for: by thickness from the
        /// rule's wall_types for a wall, or the rule's family type otherwise.
        /// </summary>
        private static JObject RetypeOperation(Document doc, CadUpdateAction a, CadInterpretation interpretation,
                                               CadRequirementSet set, out string why)
        {
            why = null;
            Element e = a.ElementId.HasValue ? doc.GetElement(Rid.Make(a.ElementId.Value)) : null;
            if (e == null) { why = "the element is gone"; return null; }
            CadCandidate c = interpretation.Candidates.FirstOrDefault(x => x.Id == a.CandidateId);
            CadRule rule = c == null ? null : set.Rules.FirstOrDefault(r => r.Id == c.RuleId);
            if (c == null || rule == null) { why = "the candidate or its rule is not in this reading"; return null; }
            ElementId typeId = null;
            if (e is Wall && rule.WallTypes != null && rule.WallTypes.Count > 0 && c.ThicknessMm.HasValue)
            {
                double best = double.MaxValue;
                foreach (WallType wt in new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>())
                {
                    if (!rule.WallTypes.Any(n => TypeNames.Matches(wt, n, StringComparison.OrdinalIgnoreCase))) continue;
                    double off = Math.Abs(wt.Width * 304.8 - c.ThicknessMm.Value);
                    if (off <= (rule.WallTypeToleranceMm ?? set.ThicknessToleranceMm) && off < best)
                    {
                        best = off;
                        typeId = wt.Id;
                    }
                }
                if (typeId == null) { why = "no listed wall type is " + c.ThicknessMm.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm"; return null; }
            }
            else if (!string.IsNullOrWhiteSpace(c.FamilyType))
            {
                string wanted = c.FamilyType.Replace(" : ", ": ");
                foreach (ElementType t in new FilteredElementCollector(doc).WhereElementIsElementType().Cast<ElementType>())
                {
                    if (!TypeNames.Matches(t, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!e.IsValidType(t.Id)) continue;
                    typeId = t.Id;
                    break;
                }
                if (typeId == null) { why = "no type '" + wanted + "' valid for this element"; return null; }
            }
            else { why = "the rule names no type"; return null; }
            if (typeId == e.GetTypeId()) { why = "the element already has that type"; return null; }
            a.Evidence["retype_to"] = Rid.Value(typeId);
            return new JObject
            {
                ["operation"] = "change_type",
                ["element_ids"] = new JArray(a.ElementId.Value),
                ["type_id"] = Rid.Value(typeId)
            };
        }

        /// <summary>A turn about the element's own face normal, from its hand to the hand the drawing implies.</summary>
        private static JObject TurnOperation(Document doc, CadUpdateAction a, out string why)
        {
            why = null;
            var fi = a.ElementId.HasValue ? doc.GetElement(Rid.Make(a.ElementId.Value)) as FamilyInstance : null;
            var point = (fi?.Location as LocationPoint)?.Point;
            JArray expected = a.Evidence["expected_hand"] as JArray;
            if (fi == null || point == null || expected == null || expected.Count < 2)
            {
                why = "the element, its point or the hand the drawing implies could not be read";
                return null;
            }
            if (fi.HostFace == null) { why = "the element is not hosted on a face"; return null; }
            Transform t = fi.GetTotalTransform();
            XYZ normal = t.BasisZ;
            var want = new XYZ(expected[0].Value<double>(), expected[1].Value<double>(), 0);
            XYZ have = t.BasisX;
            if (Math.Abs(normal.DotProduct(XYZ.BasisZ)) > 1e-6 || want.GetLength() < 1e-9 ||
                Math.Abs(want.Normalize().DotProduct(normal)) > 1e-6)
            {
                why = "the hand the drawing implies does not lie in the element's face";
                return null;
            }
            want = want.Normalize();
            double angle = Math.Atan2(have.CrossProduct(want).DotProduct(normal), have.DotProduct(want)) * 180.0 / Math.PI;
            a.Evidence["turn_degrees"] = Math.Round(angle, 3);
            var p = new JArray(point.X * 304.8, point.Y * 304.8, point.Z * 304.8);
            var q = new JArray((point.X + normal.X) * 304.8, (point.Y + normal.Y) * 304.8, (point.Z + normal.Z) * 304.8);
            return new JObject
            {
                ["operation"] = "rotate",
                ["element_ids"] = new JArray(a.ElementId.Value),
                ["axis_start"] = p,
                ["axis_end"] = q,
                ["angle_degrees"] = Math.Round(angle, 6)
            };
        }

        /// <summary>
        /// WHAT PLACING AN ELEMENT AGAIN WOULD COST, for every element a person marked
        /// replace: its id goes, so anything hosted on it and every value set on it
        /// must be carried or lost. Listed, never done here.
        /// </summary>
        private static JArray MigrationPlans(Document doc, CadUpdate update)
        {
            var plans = new JArray();
            foreach (CadUpdateAction a in update.Of("replace"))
            {
                Element e = a.ElementId.HasValue ? doc.GetElement(Rid.Make(a.ElementId.Value)) : null;
                var hosted = new JArray();
                var values = new JArray();
                if (e != null)
                {
                    try
                    {
                        foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                                                                                         .Cast<FamilyInstance>())
                        {
                            Element h = null;
                            try { h = fi.Host; } catch { }
                            if (h != null && h.Id == e.Id) hosted.Add(Rid.Value(fi.Id));
                            if (hosted.Count >= 200) break;
                        }
                    }
                    catch { }
                    foreach (Parameter prm in e.Parameters)
                    {
                        try
                        {
                            if (prm.IsReadOnly || !prm.HasValue) continue;
                            string shown = prm.AsValueString() ?? prm.AsString();
                            if (string.IsNullOrEmpty(shown)) continue;
                            values.Add(new JObject { ["name"] = prm.Definition?.Name, ["value"] = shown });
                            if (values.Count >= 50) break;
                        }
                        catch { }
                    }
                }
                plans.Add(new JObject
                {
                    ["element_id"] = a.ElementId,
                    ["classification"] = a.Classification,
                    ["candidate_id"] = a.CandidateId,
                    ["exists"] = e != null,
                    ["hosted_elements"] = hosted,
                    ["values_to_carry"] = values,
                    ["consequences"] = new JArray(
                        "the element gets a NEW id: schedules, tags, views and references that name the old one lose it",
                        hosted.Count > 0 ? "the " + hosted.Count + " element(s) hosted on it must be placed again or deleted with it"
                                         : "nothing is hosted on it",
                        "the values listed are not carried by a create and must be written again",
                        "the provenance record moves to the new element; the map old -> new is the apply's to record"),
                    ["how"] = "create the candidate through this plan's create path, write the values, then delete " +
                              "the old element with horizun_delete_verified - in that order, and only after the create verified."
                });
            }
            return plans;
        }

        /// <summary>
        /// The pairings a caller has decided are the same wall. Read strictly:
        /// this argument re-shapes existing elements, so a malformed entry is a
        /// refusal rather than a skipped row.
        /// </summary>
        private static string Pairings(JObject request, out Dictionary<long, string> accepted)
        {
            accepted = new Dictionary<long, string>();
            JArray raw = request["accept_pairings"] as JArray;
            if (raw == null) return null;
            if (raw.Count > 500)
                return "accept_pairings carries " + raw.Count + " entries; 500 is the bound. Split the update.";

            foreach (JToken token in raw)
            {
                var entry = token as JObject;
                long id = entry?.Value<long?>("element_id") ?? -1;
                string candidate = entry?.Value<string>("candidate_id");
                if (entry == null || id < 0 || !Rid.CanRepresent(id) || string.IsNullOrWhiteSpace(candidate))
                    return "accept_pairings entries must each be { element_id, candidate_id }, both present. " +
                           "This argument RE-SHAPES an existing element, so a malformed entry is refused rather " +
                           "than skipped: a skipped pairing would silently build a duplicate instead. Nothing " +
                           "was planned.";
                if (accepted.ContainsKey(id))
                    return "accept_pairings names element " + id + " twice. An element can be one wall moved, " +
                           "not two. Nothing was planned.";
                if (accepted.Values.Contains(candidate, StringComparer.Ordinal))
                    return "accept_pairings names candidate '" + candidate + "' twice. Two elements cannot both " +
                           "be the same drawing entity moved. Nothing was planned.";
                accepted[id] = candidate;
            }
            return null;
        }

        /// <summary>
        /// WHICH candidate produced WHICH element, keyed by the action it belongs
        /// to - the same idea as horizun_plan_from_cad's index, and the reason
        /// horizun_apply_cad_update can stamp what it built. A set_curve row also
        /// carries the element id, because that element already exists and is
        /// being re-shaped rather than created.
        /// </summary>
        private static JArray CandidateIndex(CadUpdate update, JArray actions)
        {
            var index = new JArray();
            foreach (JObject action in actions.OfType<JObject>())
            {
                string key = action.Value<string>("key") ?? "";
                // Creates are indexed where they are built, row by row, from the
                // conversion that built them.
                if (key.StartsWith("cad-update-create", StringComparison.Ordinal)) continue;
                if (key.StartsWith("cad-update-shift-", StringComparison.Ordinal))
                {
                    int s;
                    if (!int.TryParse(key.Substring("cad-update-shift-".Length), out s)) continue;
                    CadUpdateAction shift = update.Of("move").Where(x => x.Automatic).Skip(s).FirstOrDefault();
                    if (shift != null) index.Add(Row(key, shift, null));
                    continue;
                }
                if (key.StartsWith("cad-update-resolve-", StringComparison.Ordinal))
                {
                    // EVERY decided action behind this key: one batched action (every section resize, a refit)
                    // carries several elements, and each must be re-stamped to this revision.
                    foreach (CadUpdateAction decided in update.Actions.Where(x =>
                                 string.Equals((string)x.Evidence["resolve_key"], key, StringComparison.Ordinal)))
                        index.Add(Row(key, decided, null));
                    continue;
                }
                if (!key.StartsWith("cad-update-move-", StringComparison.Ordinal)) continue;
                int n;
                if (!int.TryParse(key.Substring("cad-update-move-".Length), out n)) continue;
                CadUpdateAction move = update.Of("set_curve").Where(x => x.Automatic).Skip(n).FirstOrDefault();
                if (move != null) index.Add(Row(key, move, null));
            }
            return index;
        }

        /// <summary>
        /// Elements the apply re-stamps without an action: claimed v1 records
        /// (they become v2, now naming their placement) and, under an accepted
        /// move, everything left in place (now naming the transform it sits
        /// under). Orphans are not here: a record on an element the drawing no
        /// longer says is left exactly as it was.
        /// </summary>
        private static JArray Restamp(CadUpdate update, CadUpdateScope scope, bool moveAccepted,
                                      IList<CadAuditSubject> subjects, string thisFileSha256,
                                      string thisSetSha256 = null, string thisRulesSha256 = null)
        {
            var bySubject = new Dictionary<long, CadAuditSubject>();
            foreach (CadAuditSubject s in subjects ?? new List<CadAuditSubject>())
                if (s != null && !bySubject.ContainsKey(s.ElementId)) bySubject[s.ElementId] = s;
            var rows = new JArray();
            var seen = new HashSet<long>();
            foreach (CadUpdateAction a in update.Actions)
            {
                if (!a.ElementId.HasValue) continue;
                if (a.Kind != "leave" && a.Kind != "review") continue;
                long id = a.ElementId.Value;
                string reason = null;
                if (a.Kind == "leave" && (string)a.Evidence["decision"] == CadDecisions.Keep)
                    reason = CadPlacementRules.RestampAccepted;
                else if (scope.MigratedFromV1.Contains(id)) reason = CadPlacementRules.RestampMigrated;
                else if (moveAccepted && a.Kind == "leave") reason = CadPlacementRules.RestampPlacementMoved;
                else if (a.Kind == "leave" && a.CandidateId != null && a.Classification != CadChange.Relayered &&
                         !string.IsNullOrEmpty(thisFileSha256))
                {
                    // THE SAME ENTITY, NOW CITED FROM THIS REVISION - only for what the
                    // update left as it is. MEASURED: a device the revised drawing had
                    // turned, held for review, was carried to revision B by the first
                    // apply; the next plan saw the same bytes and the same reading and
                    // blamed the turn on a person. A held change keeps citing the
                    // revision it was built from until someone decides it.
                    CadAuditSubject s;
                    // a new revision is new HOST bytes, or the same host read with changed references
                    if (bySubject.TryGetValue(id, out s) && s.Provenance != null &&
                        (!string.Equals(s.Provenance.SourceFileSha256, thisFileSha256, StringComparison.Ordinal) ||
                         (thisSetSha256 != null &&
                          !string.Equals(s.Provenance.SourceSetSha256, thisSetSha256, StringComparison.Ordinal))))
                        reason = CadPlacementRules.RestampCarried;
                }
                // LEFT AS IT STANDS UNDER THE NEWER RULES the caller declared. A held
                // change keeps naming the rules it was built under.
                CadAuditSubject was;
                if (reason == null && a.Kind == "leave" && thisRulesSha256 != null &&
                    bySubject.TryGetValue(id, out was) && was.Provenance != null &&
                    !string.IsNullOrEmpty(was.Provenance.RequirementSetSha256) &&
                    !string.Equals(was.Provenance.RequirementSetSha256, thisRulesSha256, StringComparison.Ordinal))
                    reason = CadPlacementRules.RestampRulesSuperseded;
                if (reason == null || !seen.Add(id)) continue;
                rows.Add(new JObject
                {
                    ["key"] = "cad-update-restamp",
                    ["element_id"] = id,
                    ["reason"] = reason,
                    ["candidate_id"] = a.CandidateId,
                    ["semantic_id"] = a.SemanticId,
                    ["geometry_id"] = a.GeometryId
                });
            }
            return rows;
        }

        /// <summary>Walls on the element's level whose solid the action's new line would share.</summary>
        private static JArray Occupants(Document doc, CadUpdateAction a, CadRequirementSet set)
        {
            var occupants = new JArray();
            var self = doc.GetElement(Rid.Make(a.ElementId.Value)) as Wall;
            if (self == null || a.Geometry.Count < 2) return occupants;
            foreach (Wall w in CadHostResolver.Walls(doc))
            {
                if (Rid.Value(w.Id) == a.ElementId.Value || w.LevelId != self.LevelId) continue;
                Line line = (w.Location as LocationCurve)?.Curve as Line;
                if (line == null) continue;
                XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
                double across, along;
                if (!CadWallReadings.SolidsIntersect(a.Geometry[0], a.Geometry[a.Geometry.Count - 1],
                        self.Width * 304.8,
                        new CadPoint(p0.X * 304.8, p0.Y * 304.8), new CadPoint(p1.X * 304.8, p1.Y * 304.8),
                        w.Width * 304.8, set.AngleToleranceDegrees, set.PointToleranceMm, set.WallOverlapMm,
                        out across, out along))
                    continue;
                occupants.Add(new JObject
                {
                    ["element_id"] = Rid.Value(w.Id),
                    ["across_overlap_mm"] = Math.Round(across, 1),
                    ["along_overlap_mm"] = Math.Round(along, 1)
                });
            }
            return occupants;
        }

        /// <summary>
        /// Instances hosted on the element that would stand outside its new line, named;
        /// null when there are none.
        /// </summary>
        private static string HostedBeyond(Document doc, CadUpdateAction a, CadRequirementSet set)
        {
            var wall = doc.GetElement(Rid.Make(a.ElementId.Value)) as Wall;
            if (wall == null || a.Geometry.Count < 2) return null;
            CadPoint s = a.Geometry[0], e = a.Geometry[a.Geometry.Count - 1];
            double len = s.PlanDistanceTo(e);
            if (len <= 0) return null;
            double ux = (e.X - s.X) / len, uy = (e.Y - s.Y) / len;
            double tol = Math.Max(set.PointToleranceMm, 1.0);
            var outside = new List<string>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                                                                          .Cast<FamilyInstance>())
            {
                ElementId hostId;
                try { hostId = fi.Host?.Id; } catch { continue; }
                if (hostId == null || hostId != wall.Id) continue;
                XYZ at = (fi.Location as LocationPoint)?.Point;
                if (at == null)
                {
                    BoundingBoxXYZ box = fi.get_BoundingBox(null);
                    if (box == null) continue;
                    at = (box.Min + box.Max) / 2;
                }
                double along = (at.X * 304.8 - s.X) * ux + (at.Y * 304.8 - s.Y) * uy;
                if (along >= -tol && along <= len + tol) continue;
                outside.Add(Rid.Value(fi.Id).ToString(CultureInfo.InvariantCulture));
            }
            if (outside.Count == 0) return null;
            return outside.Count + " element(s) hosted on it (" + string.Join(", ", outside.Take(8)) +
                   (outside.Count > 8 ? ", ..." : "") + ") would stand outside its new line; moving them to another " +
                   "wall is a decision this update does not take";
        }

        /// <summary>
        /// Every instance the split element hosts, classified against the pieces. Returns why the
        /// split must wait (a dependent in the removed stretch, across a boundary, or of a class this
        /// build cannot re-create), or null; the classification is kept on the action either way.
        /// </summary>
        /// <summary>The decisions on split dependents, and what they must be bound to.</summary>
        internal sealed class DependentDecisionContext
        {
            public List<CadDependentDecision> Decisions = new List<CadDependentDecision>();
            public string Context;
            public List<string> Problems = new List<string>();
            /// <summary>Dependents no wall re-homes by itself, held with their alternatives and key.</summary>
            public JArray HeldOrphans = new JArray();
        }

        /// <summary>dependent_decisions, read strictly: each entry names an element, an outcome and the key it answers.</summary>
        private static List<CadDependentDecision> DependentDecisions(JObject request, out string error)
        {
            error = null;
            var list = new List<CadDependentDecision>();
            JToken raw = request["dependent_decisions"];
            if (raw == null) return list;
            var arr = raw as JArray;
            if (arr == null) { error = "dependent_decisions must be an array"; return list; }
            if (arr.Count > 500) { error = "dependent_decisions carries " + arr.Count + " entries; 500 is the bound"; return list; }
            var seen = new HashSet<long>();
            foreach (JToken t in arr)
            {
                var o = t as JObject;
                long? id = o?.Value<long?>("element_id");
                string decision = o?.Value<string>("decision");
                string key = o?.Value<string>("decision_key");
                if (o == null || !id.HasValue || string.IsNullOrWhiteSpace(decision) || string.IsNullOrWhiteSpace(key))
                { error = "each entry needs element_id, decision (stay | move_to | delete) and the decision_key its proposal gave"; return list; }
                if (decision == "move_to" && string.IsNullOrWhiteSpace(o.Value<string>("piece")))
                { error = "move_to for element " + id + " names no piece"; return list; }
                if (!seen.Add(id.Value)) { error = "element " + id + " is decided twice"; return list; }
                list.Add(new CadDependentDecision { ElementId = id.Value, Decision = decision, Piece = o.Value<string>("piece"), Key = key });
            }
            return list;
        }

        private static string ClassifyDependents(Document doc, CadUpdate update, CadUpdateAction a, CadRequirementSet set,
                                                 DependentDecisionContext depCtx = null)
        {
            var wall = doc.GetElement(Rid.Make(a.ElementId.Value)) as Wall;
            XYZ o, u;
            double len;
            if (wall == null || !CadSplitDependents.LineOf(wall, out o, out u, out len)) return null;
            var pieces = new List<CadSplitPiece>();
            Func<List<CadPoint>, double[]> span = g => new[]
            {
                Math.Min(CadSplitDependents.AlongMm(o, u, g[0].X, g[0].Y),
                         CadSplitDependents.AlongMm(o, u, g[g.Count - 1].X, g[g.Count - 1].Y)),
                Math.Max(CadSplitDependents.AlongMm(o, u, g[0].X, g[0].Y),
                         CadSplitDependents.AlongMm(o, u, g[g.Count - 1].X, g[g.Count - 1].Y))
            };
            double[] k = span(a.Geometry);
            pieces.Add(new CadSplitPiece { CandidateId = a.CandidateId, Lo = k[0], Hi = k[1], KeepsTheElement = true });
            foreach (string id in ((JArray)a.Evidence["split_companions"]).Select(x => (string)x))
            {
                CadUpdateAction c = update.Of("create").FirstOrDefault(x => x.CandidateId == id);
                if (c == null || c.Geometry.Count < 2) continue;
                double[] s = span(c.Geometry);
                pieces.Add(new CadSplitPiece { CandidateId = id, Lo = s[0], Hi = s[1] });
            }
            var byId = new Dictionary<long, FamilyInstance>();
            List<CadSplitDependent> deps = CadSplitDependents.Read(doc, wall, byId);
            CadSplitRules.Classify(pieces, deps, Math.Max(set.PointToleranceMm, 1.0));
            // EVERY HELD DEPENDENT GETS THE KEY A DECISION MUST QUOTE; decisions that quote it are applied.
            List<string> problems = CadSplitRules.ApplyDecisions(pieces, deps, depCtx?.Decisions, depCtx?.Context ?? "",
                                                                 a.ElementId.Value, Math.Max(set.PointToleranceMm, 1.0));
            if (depCtx != null) depCtx.Problems.AddRange(problems);
            a.Evidence["split_dependents"] = new JArray(deps.Select(d => d.ToJson()));
            var held = deps.Where(d => d.Class != CadSplitRules.Stays && d.Class != CadSplitRules.MovesTo &&
                                       !d.Delete).ToList();
            if (held.Count == 0) return null;
            return held.Count + " dependent(s) cannot be placed on one piece without a person (" +
                   string.Join("; ", held.Take(6).Select(d => "element " + d.ElementId + " " + d.Class +
                       (d.Alternatives.Count > 0 ? ": " + string.Join(" | ", d.Alternatives) : "") +
                       " [decision_key " + d.DecisionKey + "]")) + ")";
        }

        /// <summary>For a split offered: the piece that should keep the element (most dependents, same width, longest).</summary>
        private static string RecommendedKeep(Document doc, CadUpdate update, CadUpdateAction orphan, CadRequirementSet set)
        {
            var ids = orphan.Evidence["may_have_been_split_into"] as JArray;
            if (ids == null || !orphan.ElementId.HasValue) return null;
            var wall = doc.GetElement(Rid.Make(orphan.ElementId.Value)) as Wall;
            XYZ o, u;
            double len;
            if (wall == null || !CadSplitDependents.LineOf(wall, out o, out u, out len)) return null;
            var pieces = new List<CadSplitPiece>();
            foreach (string id in ids.Select(x => (string)x))
            {
                CadUpdateAction c = update.Actions.FirstOrDefault(x => x.CandidateId == id && x.Geometry.Count >= 2);
                if (c == null) continue;
                double s0 = CadSplitDependents.AlongMm(o, u, c.Geometry[0].X, c.Geometry[0].Y);
                double s1 = CadSplitDependents.AlongMm(o, u, c.Geometry[c.Geometry.Count - 1].X, c.Geometry[c.Geometry.Count - 1].Y);
                double? t = c.Evidence.Value<double?>("thickness_mm");
                pieces.Add(new CadSplitPiece { CandidateId = id, Lo = Math.Min(s0, s1), Hi = Math.Max(s0, s1), ThicknessMm = t });
            }
            if (pieces.Count == 0) return null;
            var deps = CadSplitDependents.Read(doc, wall, new Dictionary<long, FamilyInstance>());
            CadSplitPiece best = CadSplitRules.RecommendKeep(pieces, deps, wall.Width * 304.8, set.ThicknessToleranceMm,
                                                             Math.Max(set.PointToleranceMm, 1.0));
            orphan.Evidence["recommended_keep"] = best?.CandidateId;
            return best?.CandidateId;
        }

        /// <summary>Beside a wall: within the host search of its line, laterally and along it.</summary>
        private static bool Beside(Wall w, XYZ at, CadRequirementSet set)
        {
            XYZ o, u;
            double len;
            if (!CadSplitDependents.LineOf(w, out o, out u, out len)) return false;
            double along = CadSplitDependents.AlongMm(o, u, at.X * 304.8, at.Y * 304.8);
            double across = Math.Abs(((at.X - o.X) * -u.Y + (at.Y - o.Y) * u.X) * 304.8);
            double half = 0;
            try { half = w.Width * 304.8 / 2.0; } catch { }
            // ALONG, a gap's worth past either end: a device in a stretch the drawing removed is beyond BOTH
            // pieces' ends by up to half the gap (MEASURED, campaign 5: 302 mm past the new piece's end).
            double reach = Math.Max(set.HostSearchMm, CadHostPlausibility.JambGapMm);
            return across <= half + set.HostSearchMm && along >= -reach && along <= len + reach;
        }

        /// <summary>
        /// A dependent no wall of the update re-homes by itself: its alternatives are the walls BESIDE it
        /// (parallel, within the host search), each a piece along one line; a person's decision - delete, or
        /// move_to 'wall:ID' (slid onto that wall as little as needed) - is applied only when it quotes the
        /// key given here (CadSplitRules.ApplyDecisions). Returns the held row, or null when decided.
        /// </summary>
        private static JObject DecideOrphan(Document doc, FamilyInstance fi, string why, CadRequirementSet set,
                                            DependentDecisionContext depCtx, string target, JArray actions,
                                            JArray createIndex, ref int n)
        {
            XYZ at = (fi.Location as LocationPoint)?.Point;
            double tol = Math.Max(set.PointToleranceMm, 1.0);
            var beside = at == null ? new List<Wall>() : CadHostResolver.Walls(doc).Where(w => Beside(w, at, set)).ToList();
            XYZ o = null, u = null;
            var pieces = new List<CadSplitPiece>();
            Wall first = beside.OrderBy(w =>
            {
                XYZ wo, wu; double wl;
                return CadSplitDependents.LineOf(w, out wo, out wu, out wl)
                    ? Math.Abs(((at.X - wo.X) * -wu.Y + (at.Y - wo.Y) * wu.X)) : double.MaxValue;
            }).FirstOrDefault();
            double firstLen;
            if (first != null && CadSplitDependents.LineOf(first, out o, out u, out firstLen))
                foreach (Wall w in beside)
                {
                    XYZ wo, wu; double wl;
                    if (!CadSplitDependents.LineOf(w, out wo, out wu, out wl)) continue;
                    if (Math.Abs(wu.X * u.Y - wu.Y * u.X) > Math.Sin(set.AngleToleranceDegrees * Math.PI / 180.0)) continue;
                    double a0 = CadSplitDependents.AlongMm(o, u, wo.X * 304.8, wo.Y * 304.8);
                    double a1 = CadSplitDependents.AlongMm(o, u, (wo.X + wu.X * wl / 304.8) * 304.8, (wo.Y + wu.Y * wl / 304.8) * 304.8);
                    pieces.Add(new CadSplitPiece { CandidateId = "wall:" + Rid.Value(w.Id), Lo = Math.Min(a0, a1), Hi = Math.Max(a0, a1) });
                }
            var dep = new CadSplitDependent { ElementId = Rid.Value(fi.Id), Category = fi.Category?.Name, Class = "orphaned" };
            FamilyPlacementType kind = FamilyPlacementType.Invalid;
            try { kind = fi.Symbol.Family.FamilyPlacementType; } catch { }
            dep.Recreatable = kind == FamilyPlacementType.WorkPlaneBased || kind == FamilyPlacementType.OneLevelBasedHosted;
            if (o != null && at != null)
            {
                double c = CadSplitDependents.AlongMm(o, u, at.X * 304.8, at.Y * 304.8), half = 0;
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                if (bb != null)
                    half = Math.Abs((bb.Max.X - bb.Min.X) * u.X * 304.8) / 2.0 + Math.Abs((bb.Max.Y - bb.Min.Y) * u.Y * 304.8) / 2.0;
                dep.Lo = c - half; dep.Hi = c + half;
            }
            dep.Alternatives.Add("delete it");
            foreach (CadSplitPiece p in pieces) dep.Alternatives.Add("move_to '" + p.CandidateId + "'");
            List<string> problems = CadSplitRules.ApplyDecisions(pieces, new List<CadSplitDependent> { dep },
                                                                 depCtx?.Decisions, depCtx?.Context ?? "", 0, tol);
            if (depCtx != null) depCtx.Problems.AddRange(problems);
            if (dep.Delete)
            {
                actions.Add(new JObject
                {
                    ["key"] = "cad-update-orphan-delete-" + dep.ElementId,
                    ["tool"] = "horizun_delete_verified",
                    ["arguments"] = new JObject { ["target_document"] = target, ["mode"] = "ids", ["ids"] = new JArray(dep.ElementId) }
                });
                return null;
            }
            if (dep.Class == CadSplitRules.MovesTo && dep.TargetCandidateId != null && at != null)
            {
                long wallId = long.Parse(dep.TargetCandidateId.Substring("wall:".Length), CultureInfo.InvariantCulture);
                var to = doc.GetElement(Rid.Make(wallId)) as Wall;
                XYZ slid = dep.MoveAlongMm.HasValue ? at + u.Multiply(dep.MoveAlongMm.Value / 304.8) : at;
                EmitSubstitution(doc, fi, wallId, set, target, "cad-update-rehome-" + n++, actions, createIndex,
                                 to?.LevelId, slid);
                return null;
            }
            return new JObject
            {
                ["element_id"] = dep.ElementId, ["host"] = null, ["why"] = why,
                ["alternatives"] = new JArray(dep.Alternatives), ["decision_key"] = dep.DecisionKey
            };
        }

        /// <summary>Re-create an instance on another host, then delete it: two actions and one index entry.</summary>
        private static void EmitSubstitution(Document doc, FamilyInstance fi, JToken host, CadRequirementSet set,
                                             string target, string key, JArray actions, JArray createIndex,
                                             ElementId levelIfUnhosted = null, XYZ pointOverride = null)
        {
            JObject carried = CadSplitDependents.CarriedParameters(fi);
            actions.Add(new JObject
            {
                ["key"] = key,
                ["tool"] = "horizun_create_elements",
                ["arguments"] = new JObject
                {
                    ["target_document"] = target, ["units"] = "mm",
                    ["elements"] = new JArray(CadSplitDependents.SubstitutionRow(doc, fi, host, set, carried, levelIfUnhosted, pointOverride))
                }
            });
            createIndex.Add(CadSplitDependents.IndexEntry(key, fi, carried));
            actions.Add(new JObject
            {
                ["key"] = key + "-delete",
                ["tool"] = "horizun_delete_verified",
                ["arguments"] = new JObject
                {
                    ["target_document"] = target, ["mode"] = "ids", ["ids"] = new JArray(Rid.Value(fi.Id))
                }
            });
        }

        /// <summary>
        /// DEPENDENTS LEFT BEHIND by an earlier split that stopped half way: an instance standing past
        /// its wall's ends, on the line of exactly one other wall of this update that carries it, is
        /// re-created there - or, when its substitute already stands there, only the old one is deleted.
        /// Anything else is listed and left alone.
        /// </summary>
        private static void Rehome(Document doc, CadUpdate update, CadRequirementSet set, string target,
                                   JArray actions, JArray createIndex, DependentDecisionContext depCtx = null)
        {
            // EVERY WALL OF THE UPDATE, for WHOSE dependents are left without a host: a wall held for review
            // (MEASURED: W1 held as resized after an apply stopped) still had them.
            var anyWalls = update.Actions.Where(x => x.ElementId.HasValue)
                                 .Select(x => doc.GetElement(Rid.Make(x.ElementId.Value)) as Wall)
                                 .Where(w => w != null).GroupBy(w => w.Id).Select(g => g.First()).ToList();
            if (anyWalls.Count == 0) return;
            double tol = Math.Max(set.PointToleranceMm, 1.0);
            var held = new JArray();
            int n = 0;
            foreach (Wall w in anyWalls)
            {
                if (update.Actions.Any(x => x.ElementId == Rid.Value(w.Id) && x.Kind == "set_curve")) continue;
                XYZ o, u;
                double len;
                if (!CadSplitDependents.LineOf(w, out o, out u, out len)) continue;
                var byId = new Dictionary<long, FamilyInstance>();
                foreach (CadSplitDependent d in CadSplitDependents.Read(doc, w, byId))
                {
                    // CARRIED AWAY FROM WHERE IT WAS BUILT. MEASURED (campaign 4): re-shaping a wall moves
                    // its face-hosted devices with it; one the split meant to re-create elsewhere, and an
                    // apply that stopped before doing so, leaves it on the wrong stretch. Its as-built point,
                    // carried by exactly one other wall of this update, is where it is re-created.
                    FamilyInstance hosted = byId[d.ElementId];
                    string recordProblem;
                    CadProvenance rec = CadProvenanceStore.Read(hosted, out recordProblem);
                    List<CadPoint> built = CadUpdateRules.AsBuiltOf(rec);
                    XYZ nowAt = (hosted.Location as LocationPoint)?.Point;
                    if (built != null && built.Count == 1 && nowAt != null)
                    {
                        var was = new XYZ(built[0].X / 304.8, built[0].Y / 304.8, nowAt.Z);
                        if (was.DistanceTo(nowAt) * 304.8 > tol && !CadHostResolver.CarriesPoint(w, was))
                        {
                            var home = anyWalls.Where(x => x.Id != w.Id && Carries(x, was, hosted, tol) &&
                                                           CadHostResolver.CarriesPoint(x, was)).ToList();
                            if (home.Count == 1)
                            {
                                EmitSubstitution(doc, hosted, Rid.Value(home[0].Id), set, target, "cad-update-rehome-" + n++,
                                                 actions, createIndex, null, was);
                                continue;
                            }
                        }
                    }
                    if (d.Lo >= -tol && d.Hi <= len + tol) continue;
                    FamilyInstance fi = byId[d.ElementId];
                    XYZ at = ((LocationPoint)fi.Location).Point;
                    // STILL CARRIED BY ITS OWN WALL (a face runs past the line at a joined corner): not left behind.
                    if (CadHostResolver.CarriesPoint(w, at)) continue;
                    var carriers = anyWalls.Where(x => x.Id != w.Id && Carries(x, at, fi, tol)).ToList();
                    if (carriers.Count != 1 || !d.Recreatable)
                    {
                        string why = !d.Recreatable ? "a class this build does not re-create"
                                   : carriers.Count == 0 ? "no wall of this update carries it"
                                   : "more than one wall carries it";
                        JObject row = DecideOrphan(doc, fi, why, set, depCtx, target, actions, createIndex, ref n);
                        if (row != null) held.Add(row);
                        continue;
                    }
                    Wall to = carriers[0];
                    FamilyInstance twin = CadSplitDependents.HostedOn(doc, to).FirstOrDefault(x =>
                        x.GetTypeId() == fi.GetTypeId() && x.Location is LocationPoint lp && lp.Point.DistanceTo(at) * 304.8 <= tol);
                    if (twin != null)
                    {
                        actions.Add(new JObject
                        {
                            ["key"] = "cad-update-rehome-delete-" + n++,
                            ["tool"] = "horizun_delete_verified",
                            ["arguments"] = new JObject
                            {
                                ["target_document"] = target, ["mode"] = "ids", ["ids"] = new JArray(d.ElementId)
                            }
                        });
                        continue;
                    }
                    EmitSubstitution(doc, fi, Rid.Value(to.Id), set, target, "cad-update-rehome-" + n++, actions, createIndex);
                }
            }
            // LEFT WITHOUT A HOST. MEASURED (campaign 4): shortening a wall past a face-hosted
            // device keeps the device and drops its host, silently. One whose point a single wall
            // of this update carries on a face is re-created there.
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                                                                          .Cast<FamilyInstance>())
            {
                Element hostNow;
                try { hostNow = fi.Host; } catch { continue; }
                if (hostNow != null) continue;
                FamilyPlacementType kind;
                try { kind = fi.Symbol.Family.FamilyPlacementType; } catch { continue; }
                if (kind != FamilyPlacementType.WorkPlaneBased && kind != FamilyPlacementType.OneLevelBasedHosted) continue;
                XYZ at = (fi.Location as LocationPoint)?.Point;
                if (at == null) continue;
                var carriers = anyWalls.Where(x => Carries(x, at, fi, tol) && CadHostResolver.CarriesPoint(x, at)).ToList();
                if (carriers.Count != 1)
                {
                    // NO WALL CARRIES IT: MEASURED (campaign 5) - an apply stopped after shortening a wall left
                    // a device in the removed stretch without a host, and this pass skipped it silently.
                    // One beside a wall of this update is held, with its alternatives and a key to decide it.
                    if (carriers.Count == 0 && !anyWalls.Any(x => Beside(x, at, set)))
                        continue;                      // not a dependent of these walls
                    JObject row = DecideOrphan(doc, fi, carriers.Count == 0 ? "unhosted, and no wall carries it"
                                                                           : "unhosted, and more than one wall carries it",
                                               set, depCtx, target, actions, createIndex, ref n);
                    if (row != null) held.Add(row);
                    continue;
                }
                FamilyInstance standing = CadSplitDependents.HostedOn(doc, carriers[0]).FirstOrDefault(x =>
                    x.GetTypeId() == fi.GetTypeId() && x.Location is LocationPoint lp && lp.Point.DistanceTo(at) * 304.8 <= tol);
                if (standing != null)
                {
                    // ITS SUBSTITUTE ALREADY STANDS (an apply stopped before deleting it): only the delete remains.
                    actions.Add(new JObject
                    {
                        ["key"] = "cad-update-rehome-delete-" + n++,
                        ["tool"] = "horizun_delete_verified",
                        ["arguments"] = new JObject
                        {
                            ["target_document"] = target, ["mode"] = "ids", ["ids"] = new JArray(Rid.Value(fi.Id))
                        }
                    });
                    continue;
                }
                EmitSubstitution(doc, fi, Rid.Value(carriers[0].Id), set, target, "cad-update-rehome-" + n++, actions,
                                 createIndex, carriers[0].LevelId);
            }
            if (held.Count > 0)
            {
                update.Rejected.Add("dependents left past their wall's end and not re-homed: " +
                                    held.ToString(Newtonsoft.Json.Formatting.None));
                if (depCtx != null) foreach (JToken h in held) depCtx.HeldOrphans.Add(h);
            }
        }

        private static bool Carries(Wall w, XYZ at, FamilyInstance fi, double tolMm)
        {
            XYZ o, u;
            double len;
            if (!CadSplitDependents.LineOf(w, out o, out u, out len)) return false;
            double along = CadSplitDependents.AlongMm(o, u, at.X * 304.8, at.Y * 304.8);
            double across = Math.Abs((at.X - o.X) * -u.Y + (at.Y - o.Y) * u.X) * 304.8;
            return along >= -tolMm && along <= len + tolMm && across <= w.Width * 304.8 / 2.0 + tolMm;
        }

        /// <summary>An accepted split that cannot be carried out waits whole: the element and every piece.</summary>
        private static void HoldSplit(CadUpdate update, CadUpdateAction reshape, string reason, string detail)
        {
            reshape.Automatic = false;
            var held = new JObject { ["reason"] = reason, ["detail"] = detail };
            reshape.Evidence["split_held"] = held;
            reshape.Says += " HELD: the split cannot be carried out - " + detail + ".";
            foreach (string id in ((JArray)reshape.Evidence["split_companions"]).Select(x => (string)x))
            {
                CadUpdateAction piece = update.Of("create").FirstOrDefault(c => c.CandidateId == id);
                if (piece == null) continue;
                piece.Automatic = false;
                piece.Evidence["split_held"] = held;
                piece.Says += " HELD with element " + reshape.ElementId + ": " + detail + ".";
            }
        }

        private static JObject Row(string key, CadUpdateAction a, int? elementIndex)
        {
            var o = new JObject
            {
                ["key"] = key,
                ["candidate_id"] = a.CandidateId,
                ["semantic_id"] = a.SemanticId,
                // WITHOUT THIS every element an update created was stamped with
                // GeometryId null - the one field the relayered rung matches on.
                ["geometry_id"] = a.GeometryId,
                ["rule_id"] = a.Evidence.Value<string>("rule_id"),
                ["layer"] = a.Evidence.Value<string>("layer"),
                ["confidence"] = a.Evidence.Value<double?>("confidence") ?? 0
            };
            if (elementIndex.HasValue) o["element_index"] = elementIndex.Value;
            if (a.ElementId.HasValue) o["element_id"] = a.ElementId.Value;
            return o;
        }

        /// <summary>
        /// What Revit will not place without a host wall. The same list the plan
        /// command works from, and for the same reason: a door placed
        /// free-standing is a different building that verifies happily.
        /// </summary>
        private static bool NeedsWallHost(string produces)
        {
            return produces == "door" || produces == "window";
        }

        /// <summary>
        /// Every parameter name some rule in this set writes. The same question
        /// the audit asks, for the same reason: only what a rule named is read,
        /// because sweeping every parameter of every element would cost more than
        /// the whole comparison and answer nothing anybody asked.
        /// </summary>
        private static List<string> WantedParameters(CadRequirementSet set)
        {
            var names = new List<string>();
            if (set?.Rules == null) return names;
            foreach (CadRule rule in set.Rules)
                foreach (CadParameterWrite write in rule?.Parameters ?? new List<CadParameterWrite>())
                    if (!string.IsNullOrWhiteSpace(write?.Parameter) && !names.Contains(write.Parameter))
                        names.Add(write.Parameter);
            return names;
        }

        private static List<CadAuditSubject> Stamped(Document doc, List<string> problems,
                                                     List<string> wantedParameters)
        {
            var subjects = new List<CadAuditSubject>();
            try
            {
                // EVERY VERSION OF THE RECORD. A collector on the current GUID
                // alone would lose every v1 conversion the day the writer moved
                // to v2, and report it as a first conversion.
                foreach (Element e in CadProvenanceStore.Holders(doc))
                {
                    string problem;
                    CadProvenance p = CadProvenanceStore.Read(e, out problem);
                    if (problem != null) { problems.Add("element " + Rid.Value(e.Id) + ": " + problem); continue; }
                    if (p == null) continue;
                    // THE SAME READING THE AUDIT DOES. This used to be its own
                    // copy, and the copies diverged where it mattered: this one
                    // never read the element's TYPE, so a classification that
                    // compares the drawing's requested type against the element's
                    // own could not fire through the command that needs it. It
                    // fired in tests, because tests build subjects by hand.
                    // THE PARAMETERS SOME RULE NAMED, and nothing else. Without
                    // them the update cannot see a value a person edited, which is
                    // the one kind of change a drawing can never report.
                    CadAuditSubject s = CadSubjectReader.Measure(e, wantedParameters);
                    s.Provenance = p;
                    subjects.Add(s);
                }
            }
            catch { }
            return subjects.OrderBy(s => s.ElementId).ToList();
        }

        private static JArray Pt(CadPoint p) => new JArray(
            Math.Round(p.X, 4, MidpointRounding.AwayFromZero),
            Math.Round(p.Y, 4, MidpointRounding.AwayFromZero),
            Math.Round(p.Z, 4, MidpointRounding.AwayFromZero));


        private static string SafeName(Element e) { try { return e.Name; } catch { return null; } }
        private static string SafeTitle(Document d) { try { return d.Title; } catch { return null; } }
        private static string SafeVersion(UIApplication a)
        { try { return a?.Application?.VersionNumber + "." + a?.Application?.VersionBuild; } catch { return null; } }
    }
}
