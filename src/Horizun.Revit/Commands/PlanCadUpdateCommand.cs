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
            CadInstanceFacts facts = CadFacts.Collect(doc, out unreadable)
                .FirstOrDefault(f => f.ElementId == instanceId);
            if (facts == null)
                return CommandResult.Fail("CAD instance " + instanceId + " could not be measured.");

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

            CadInterpretation interpretation = CadInterpretationRules.Interpret(
                harvest.Segments, set, sourceHash, harvest.Arcs);

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

            var minesUnderThisSet = subjects
                .Where(s => set.Sha256 == null || string.IsNullOrEmpty(s.Provenance.RequirementSetSha256) ||
                            string.Equals(s.Provenance.RequirementSetSha256, set.Sha256, StringComparison.Ordinal))
                .ToList();
            var shas = minesUnderThisSet
                .Select(s => s.Provenance.SourceFileSha256)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.Ordinal).ToList();
            bool anyFromThisFile = facts.FileSha256 != null && shas.Contains(facts.FileSha256, StringComparer.Ordinal);

            if (!anyFromThisFile && lineage.Count == 0 && shas.Count > 0)
                return CommandResult.Fail(
                    "supersedes_unstated: nothing in '" + title + "' was built from THIS file, and " +
                    minesUnderThisSet.Count + " element(s) were built under these rules from " + shas.Count +
                    " other drawing(s): " + string.Join(", ", shas.Take(8)) +
                    (shas.Count > 8 ? " and more" : "") + ". Planning anyway would report your whole existing " +
                    "conversion as untouched and this drawing as entirely new work - it would build a second " +
                    "copy of the building. Say which one this supersedes in supersedes_sha256. Nothing in a DWG " +
                    "says that one file is a re-issue of another, so it is a statement you make, not one this " +
                    "bridge can find.");

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
                    if (!NeedsWallHost(hosted.ProposedKind)) continue;
                    if (walls == null) walls = CadHostResolver.Walls(doc);
                    CadPoint at = hosted.Geometry[0];
                    CadHostMatch match = CadHostResolver.Nearest(
                        walls, CadHostResolver.PointFromMm(at.X, at.Y, at.Z), set.PointToleranceMm);
                    if (match.Wall != null) hostByCandidate[hosted.SemanticId] = Rid.Value(match.Wall.Id);
                }
            }
            catch { }

            CadUpdate update = CadUpdateRules.Plan(interpretation.Candidates, subjects, set,
                                                   facts.FileSha256, accepted, lineage, rejectedPairings,
                                                   hostByCandidate);

            // ---------------------------------------------------------- actions
            string levelError;
            JArray actions = Actions(doc, update, set, request, title, out levelError);
            if (levelError != null) return CommandResult.Fail(levelError);

            var result = new JObject
            {
                ["document"] = title,
                ["instance_id"] = instanceId,
                ["read_only"] = true,
                ["source"] = new JObject
                {
                    ["fingerprint"] = sourceFingerprint,
                    ["file_sha256"] = facts.FileSha256,
                    ["external_path"] = facts.ExternalPath
                },
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
                ["counts_by_kind"] = update.CountsByKind(),
                ["counts_by_classification"] = update.CountsByClassification(),
                ["classification_vocabulary"] = new JArray(CadChange.All),
                ["classification_means"] =
                    "WHAT CHANGED, as distinct from what to do about it. Several different changes need the " +
                    "same treatment - a retyped wall, a relayered wall and one somebody moved by hand all end " +
                    "in 'a person decides' - and a reader with only the kind cannot tell them apart. The " +
                    "vocabulary is closed and every name is reported with its count, including the zeros: a " +
                    "key that simply disappeared would read as 'not measured' rather than 'none found'.",
                ["actions"] = actions,
                ["automatic"] = update.Actions.Count(a => a.Automatic && a.Kind != "leave"),
                ["needs_a_person"] = update.Actions.Count(a => !a.Automatic),
                ["plan"] = new JArray(update.Actions.Select(a => a.ToJson())),
                ["kinds_mean"] = new JObject
                {
                    ["create"] = "in revision B, nothing in the model remembers being built from it",
                    ["set_curve"] = "the DRAWING moved and nobody has touched the element since: update it, and " +
                                    "it keeps its id, its parameters and everything hosted on it",
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
                    ["plan_fingerprint"] = "cadupd:" + CadConversionPlanRules.ActionsFingerprint(actions).Substring(8),
                    ["means"] = "copy this into horizun_apply_cad_update. Without it the elements this update " +
                                "creates remember nothing, and the NEXT update builds them again."
                },
                ["candidate_index"] = CandidateIndex(update, actions),
                ["lineage"] = new JObject
                {
                    ["this_file_sha256"] = facts.FileSha256,
                    ["supersedes"] = new JArray(lineage),
                    ["source_hashes_in_the_model"] = new JArray(shas),
                    ["means"] = "the elements this update is about are the ones built from this file or from a " +
                                "drawing it supersedes, under these rules. Everything else in the model is left " +
                                "alone and is not counted here."
                },
                ["pairings_offered"] = new JArray(update.Actions
                    .Where(a => a.PairedWith != null)
                    .Select(a => new JObject
                    {
                        ["element_id"] = a.ElementId,
                        ["candidate_id"] = a.PairedWith,
                        ["confidence"] = a.PairConfidence,
                        ["paired_on"] = a.Evidence.Value<string>("paired_on")
                    })),
                ["pairings_rejected"] = new JArray(update.Rejected),
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
            return CommandResult.Ok(result);
        }

        /// <summary>
        /// The executable half: creates through horizun_create_elements, moves
        /// through horizun_transform_elements set_curve. Everything a person must
        /// decide is deliberately absent.
        /// </summary>
        private static JArray Actions(Document doc, CadUpdate update, CadRequirementSet set,
                                      JObject request, string target, out string levelError)
        {
            levelError = null;
            var actions = new JArray();

            List<CadUpdateAction> creates = update.Of("create").Where(a => a.Automatic).ToList();
            if (creates.Count > 0)
            {
                string levelName = request.Value<string>("level_name");
                long? levelId = request.Value<long?>("level_id");
                List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                Level level = null;
                if (levelName != null)
                    level = levels.FirstOrDefault(l => string.Equals(SafeName(l), levelName, StringComparison.Ordinal))
                         ?? levels.FirstOrDefault(l => string.Equals(SafeName(l), levelName, StringComparison.OrdinalIgnoreCase));
                else if (levelId.HasValue)
                    level = doc.GetElement(Rid.Make(levelId.Value)) as Level;

                if (level == null)
                {
                    levelError = "level_unresolved: revision B adds " + creates.Count + " element(s), and a 2D " +
                                 "drawing does not carry a storey. Pass level_name (or level_id) - the same one " +
                                 "the first conversion used, or the update will build the new walls on a " +
                                 "different floor from the old ones. The levels in '" + target + "' are: " +
                                 string.Join(", ", levels.Take(24).Select(l => "'" + SafeName(l) + "'")) + ".";
                    return actions;
                }

                var elements = new JArray();
                foreach (CadUpdateAction a in creates)
                {
                    if (a.Geometry.Count < 2) continue;
                    elements.Add(new JObject
                    {
                        ["kind"] = "wall",
                        ["start"] = Pt(a.Geometry[0]),
                        ["end"] = Pt(a.Geometry[a.Geometry.Count - 1]),
                        ["height"] = set.Rules.Select(r => r.HeightMm).FirstOrDefault(h => h.HasValue) ?? 3000.0,
                        ["level_id"] = Rid.Value(level.Id)
                    });
                }
                if (elements.Count > 0)
                    actions.Add(new JObject
                    {
                        ["key"] = "cad-update-create",
                        ["tool"] = "horizun_create_elements",
                        ["arguments"] = new JObject
                        {
                            ["target_document"] = target,
                            ["units"] = "mm",
                            ["elements"] = elements
                        }
                    });
            }

            // ONE OPERATION PER ELEMENT: a location line belongs to one element,
            // and the command refuses a shared one for exactly that reason.
            int n = 0;
            foreach (CadUpdateAction a in update.Of("set_curve").Where(x => x.Automatic))
            {
                if (!a.ElementId.HasValue || a.Geometry.Count < 2) continue;
                actions.Add(new JObject
                {
                    ["key"] = "cad-update-move-" + (n++),
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
            return actions;
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
                if (key == "cad-update-create")
                {
                    int i = 0;
                    foreach (CadUpdateAction a in update.Of("create").Where(x => x.Automatic))
                    {
                        if (a.Geometry.Count < 2) continue;
                        index.Add(Row(key, a, i++));
                    }
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

        private static JObject Row(string key, CadUpdateAction a, int? elementIndex)
        {
            var o = new JObject
            {
                ["key"] = key,
                ["candidate_id"] = a.CandidateId,
                ["semantic_id"] = a.SemanticId,
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
                if (Schema.Lookup(CadProvenanceStore.SchemaGuid) == null) return subjects;
                foreach (Element e in new FilteredElementCollector(doc)
                             .WhereElementIsNotElementType()
                             .WherePasses(new ExtensibleStorageFilter(CadProvenanceStore.SchemaGuid)))
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
